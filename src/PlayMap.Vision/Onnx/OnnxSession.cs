using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PlayMap.Vision.Onnx;

/// <summary>Where a model runs. GPU providers come from optional runtime packages the app may ship; the base
/// package has only the CPU, which is always last and always there.</summary>
public enum ExecutionProvider
{
    TensorRt,
    Cuda,
    /// <summary>Windows' DirectX 12 path (in sustained engineering upstream: kept as a fallback, not a target).</summary>
    DirectML,
    Cpu,
}

/// <summary>How a session is opened: providers in order of preference (the CPU is appended when missing), the
/// GPU device, and the CPU thread count (0 = the runtime's choice).</summary>
public sealed record OnnxSessionSettings(ImmutableArray<ExecutionProvider> Preference, int DeviceId = 0, int IntraOpThreads = 0)
{
    /// <summary>CUDA, then DirectML, then the CPU.</summary>
    public static readonly OnnxSessionSettings Default = new([ExecutionProvider.Cuda, ExecutionProvider.DirectML, ExecutionProvider.Cpu]);

    public static readonly OnnxSessionSettings CpuOnly = new([ExecutionProvider.Cpu]);

    /// <summary>The preference with the CPU last, each provider once.</summary>
    public ImmutableArray<ExecutionProvider> Order()
    {
        var list = new List<ExecutionProvider>();
        foreach (var p in Preference.IsDefault ? [] : Preference)
            if (p != ExecutionProvider.Cpu && !list.Contains(p))
                list.Add(p);
        list.Add(ExecutionProvider.Cpu);
        return [.. list];
    }
}

/// <summary>One provider tried while opening a session, and what happened.</summary>
public sealed record ProviderAttempt(ExecutionProvider Provider, bool Opened, string Words);

/// <summary>A model input or output: name, element type and dimensions (−1 where symbolic).</summary>
public sealed record TensorInfo(string Name, TensorElementType ElementType, ImmutableArray<long> Dimensions)
{
    /// <summary>The dimension, or <paramref name="fallback"/> when it is symbolic or absent.</summary>
    public long Dim(int axis, long fallback) => axis < Dimensions.Length && Dimensions[axis] > 0 ? Dimensions[axis] : fallback;
}

/// <summary>A model output copied into managed memory (floats whatever the model's type), row-major.</summary>
public sealed record TensorData(float[] Values, ImmutableArray<long> Shape)
{
    public long Dim(int axis) => Shape[axis];
}

/// <summary>An input tensor in a pooled float buffer; converted to the model's element type at run time.
/// Dispose returns the buffer.</summary>
public sealed class TensorBuffer : IDisposable
{
    private float[]? _array;

    public TensorBuffer(string name, params long[] shape)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(shape);
        long n = 1;
        foreach (var d in shape)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(d);
            n *= d;
        }
        if (n > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(shape), "The tensor is larger than a buffer.");
        Name = name;
        Shape = shape;
        Length = (int)n;
        _array = ArrayPool<float>.Shared.Rent(Math.Max(Length, 1));
        Span.Clear();
    }

    public string Name { get; }
    public long[] Shape { get; }
    public int Length { get; }

    public Span<float> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_array is null, this);
            return _array.AsSpan(0, Length);
        }
    }

    internal Memory<float> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_array is null, this);
            return _array.AsMemory(0, Length);
        }
    }

    public static TensorBuffer Scalar(string name, float value, params long[] shape)
    {
        var t = new TensorBuffer(name, shape.Length == 0 ? [1] : shape);
        t.Span.Fill(value);
        return t;
    }

    public void Dispose()
    {
        var a = Interlocked.Exchange(ref _array, null);
        if (a is not null)
            ArrayPool<float>.Shared.Return(a);
    }
}

/// <summary>An ONNX Runtime session opened on the best provider this machine has. Opening tries each preferred
/// provider in turn — not in this build, failed to append, failed to create a session — and falls back to the
/// CPU without throwing; <see cref="Attempts"/> says what happened to each. Runs are serialised (one at a time per
/// session) and honour cancellation by terminating the run.</summary>
public sealed class OnnxSession : IDisposable
{
    private static readonly Dictionary<ExecutionProvider, string> ProviderNames = new()
    {
        [ExecutionProvider.TensorRt] = "TensorrtExecutionProvider",
        [ExecutionProvider.Cuda] = "CUDAExecutionProvider",
        [ExecutionProvider.DirectML] = "DmlExecutionProvider",
        [ExecutionProvider.Cpu] = "CPUExecutionProvider",
    };

    private readonly InferenceSession _session;
    private readonly SessionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TensorInfo> _inputs;
    private int _disposed;

    private OnnxSession(InferenceSession session, SessionOptions options, ExecutionProvider provider, IReadOnlyList<ProviderAttempt> attempts)
    {
        _session = session;
        _options = options;
        Provider = provider;
        Attempts = attempts;
        Inputs = Describe(session.InputMetadata);
        Outputs = Describe(session.OutputMetadata);
        _inputs = Inputs.ToDictionary(i => i.Name, StringComparer.Ordinal);
    }

    public ExecutionProvider Provider { get; }
    public IReadOnlyList<ProviderAttempt> Attempts { get; }
    public ImmutableArray<TensorInfo> Inputs { get; }
    public ImmutableArray<TensorInfo> Outputs { get; }

    public bool HasInput(string name) => _inputs.ContainsKey(name);

    public TensorInfo? Input(string name) => _inputs.GetValueOrDefault(name);

    public bool HasOutput(string name) => Outputs.Any(o => o.Name == name);

    /// <summary>The execution providers the loaded native runtime was built with (the base package: the CPU).</summary>
    public static IReadOnlySet<string> AvailableProviders()
    {
        try
        {
            return new HashSet<string>(OrtEnv.Instance().GetAvailableProviders(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or OnnxRuntimeException)
        {
            // No native runtime at all: nothing is available, and opening will say so.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public static OnnxSession Open(string modelPath, OnnxSessionSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The model file is not there.", modelPath);
        return Open(settings, options => new InferenceSession(modelPath, options));
    }

    public static OnnxSession Open(byte[] model, OnnxSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Open(settings, options => new InferenceSession(model, options));
    }

    private static OnnxSession Open(OnnxSessionSettings settings, Func<SessionOptions, InferenceSession> create)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var available = AvailableProviders();
        var (session, attempts) = OpenWithFallback(settings.Order(), available, provider =>
        {
            var options = MakeOptions(provider, settings);
            try
            {
                var s = create(options);
                return (Session: s, Options: options);
            }
            catch
            {
                options.Dispose();
                throw;
            }
        });
        return new OnnxSession(session.Session, session.Options, attempts[^1].Provider, attempts);
    }

    /// <summary>The fallback walk, apart from the runtime so it can be proven: each provider in order is skipped
    /// when the build lacks it, and tried otherwise; a throw moves on to the next. The CPU is tried even when the
    /// build does not list it. Throws only when the CPU fails too, with every attempt's words.</summary>
    internal static (T Session, List<ProviderAttempt> Attempts) OpenWithFallback<T>(
        IReadOnlyList<ExecutionProvider> order, IReadOnlySet<string> available, Func<ExecutionProvider, T> tryOpen)
    {
        var attempts = new List<ProviderAttempt>();
        Exception? last = null;
        foreach (var provider in order)
        {
            if (provider != ExecutionProvider.Cpu && !available.Contains(ProviderNames[provider]))
            {
                attempts.Add(new ProviderAttempt(provider, false, "not in this runtime build"));
                continue;
            }
            try
            {
                var session = tryOpen(provider);
                attempts.Add(new ProviderAttempt(provider, true, "opened"));
                return (session, attempts);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // A provider whose libraries or device are missing fails here; the next one is tried.
                attempts.Add(new ProviderAttempt(provider, false, ex.Message));
                last = ex;
            }
        }
        var words = string.Join("; ", attempts.Select(a => $"{a.Provider}: {a.Words}"));
        throw new InvalidOperationException("No execution provider could open the model (" + words + ").", last);
    }

    private static SessionOptions MakeOptions(ExecutionProvider provider, OnnxSessionSettings settings)
    {
        var options = new SessionOptions();
        try
        {
            options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            if (settings.IntraOpThreads > 0)
                options.IntraOpNumThreads = settings.IntraOpThreads;
            switch (provider)
            {
                case ExecutionProvider.TensorRt:
                    options.AppendExecutionProvider_Tensorrt(settings.DeviceId);
                    options.AppendExecutionProvider_CUDA(settings.DeviceId);
                    break;
                case ExecutionProvider.Cuda:
                    options.AppendExecutionProvider_CUDA(settings.DeviceId);
                    break;
                case ExecutionProvider.DirectML:
                    // DirectML requires sequential execution without memory patterns.
                    options.EnableMemoryPattern = false;
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.AppendExecutionProvider_DML(settings.DeviceId);
                    break;
                case ExecutionProvider.Cpu:
                    break;
            }
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    private static ImmutableArray<TensorInfo> Describe(IReadOnlyDictionary<string, NodeMetadata> metadata)
    {
        var list = ImmutableArray.CreateBuilder<TensorInfo>(metadata.Count);
        foreach (var (name, m) in metadata)
        {
            var dims = m.IsTensor ? m.Dimensions.Select(d => (long)d).ToImmutableArray() : [];
            list.Add(new TensorInfo(name, m.IsTensor ? m.ElementDataType : TensorElementType.DataTypeMax, dims));
        }
        return list.ToImmutable();
    }

    /// <summary>Runs the model on the inputs (each converted to its declared element type) and copies the named
    /// outputs out as floats. Cancellation terminates the run and throws <see cref="OperationCanceledException"/>.</summary>
    public async Task<IReadOnlyDictionary<string, TensorData>> RunAsync(IReadOnlyList<TensorBuffer> inputs, IReadOnlyList<string> outputNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputNames);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PlayMap.Core.Threading.Compute.RunAsync(() => RunLocked(inputs, outputNames, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, TensorData> RunLocked(IReadOnlyList<TensorBuffer> inputs, IReadOnlyList<string> outputNames, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = new List<OrtValue>(inputs.Count);
        var converted = new List<Action>();
        try
        {
            foreach (var input in inputs)
            {
                var info = Input(input.Name) ?? throw new ArgumentException("The model has no input named " + input.Name + ".", nameof(inputs));
                values.Add(CreateValue(input, info.ElementType, converted));
            }
            using var runOptions = new RunOptions();
            using var registration = cancellationToken.Register(() => runOptions.Terminate = true);
            IDisposableReadOnlyCollection<OrtValue> results;
            try
            {
                results = _session.Run(runOptions, inputs.Select(i => i.Name).ToArray(), values, outputNames.ToArray());
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            using (results)
            {
                var map = new Dictionary<string, TensorData>(StringComparer.Ordinal);
                var i = 0;
                foreach (var r in results)
                    map[outputNames[i++]] = CopyOut(r);
                return map;
            }
        }
        finally
        {
            foreach (var v in values)
                v.Dispose();
            foreach (var release in converted)
                release();
        }
    }

    private static OrtValue CreateValue(TensorBuffer input, TensorElementType type, List<Action> converted)
    {
        var src = input.Span;
        switch (type)
        {
            case TensorElementType.Float:
                return OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input.Memory, input.Shape);
            case TensorElementType.Int64:
                {
                    var a = ArrayPool<long>.Shared.Rent(Math.Max(src.Length, 1));
                    converted.Add(() => ArrayPool<long>.Shared.Return(a));
                    for (var i = 0; i < src.Length; i++)
                        a[i] = (long)Math.Round(src[i]);
                    return OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, a.AsMemory(0, src.Length), input.Shape);
                }
            case TensorElementType.Int32:
                {
                    var a = ArrayPool<int>.Shared.Rent(Math.Max(src.Length, 1));
                    converted.Add(() => ArrayPool<int>.Shared.Return(a));
                    for (var i = 0; i < src.Length; i++)
                        a[i] = (int)Math.Round(src[i]);
                    return OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, a.AsMemory(0, src.Length), input.Shape);
                }
            case TensorElementType.Double:
                {
                    var a = ArrayPool<double>.Shared.Rent(Math.Max(src.Length, 1));
                    converted.Add(() => ArrayPool<double>.Shared.Return(a));
                    for (var i = 0; i < src.Length; i++)
                        a[i] = src[i];
                    return OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, a.AsMemory(0, src.Length), input.Shape);
                }
            case TensorElementType.Bool:
                {
                    var a = ArrayPool<bool>.Shared.Rent(Math.Max(src.Length, 1));
                    converted.Add(() => ArrayPool<bool>.Shared.Return(a));
                    for (var i = 0; i < src.Length; i++)
                        a[i] = src[i] != 0;
                    return OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, a.AsMemory(0, src.Length), input.Shape);
                }
            default:
                throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture, $"Input {input.Name} has element type {type}, which PlayMap does not feed."));
        }
    }

    private static TensorData CopyOut(OrtValue value)
    {
        var info = value.GetTensorTypeAndShape();
        var shape = info.Shape.ToImmutableArray();
        var n = (int)info.ElementCount;
        var dst = new float[n];
        switch (info.ElementDataType)
        {
            case TensorElementType.Float:
                value.GetTensorDataAsSpan<float>().CopyTo(dst);
                break;
            case TensorElementType.Double:
                {
                    var s = value.GetTensorDataAsSpan<double>();
                    for (var i = 0; i < n; i++)
                        dst[i] = (float)s[i];
                    break;
                }
            case TensorElementType.Int64:
                {
                    var s = value.GetTensorDataAsSpan<long>();
                    for (var i = 0; i < n; i++)
                        dst[i] = s[i];
                    break;
                }
            case TensorElementType.Int32:
                {
                    var s = value.GetTensorDataAsSpan<int>();
                    for (var i = 0; i < n; i++)
                        dst[i] = s[i];
                    break;
                }
            case TensorElementType.Bool:
                {
                    var s = value.GetTensorDataAsSpan<bool>();
                    for (var i = 0; i < n; i++)
                        dst[i] = s[i] ? 1 : 0;
                    break;
                }
            default:
                throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture, $"An output has element type {info.ElementDataType}, which PlayMap does not read."));
        }
        return new TensorData(dst, shape);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _session.Dispose();
        _options.Dispose();
        _gate.Dispose();
    }
}
