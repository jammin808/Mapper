using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PlayMap.Core.Gpu;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests;

/// <summary>The backends a test job can have, in order, the reference first. Each maker opens its leg, or answers null where the job
/// lacks the backend; GL is on every job (llvmpipe here, Mesa WGL on the Windows job). A later backend adds its maker here, and its
/// list's open-pass answer to <see cref="InPass"/>.</summary>
public static class BackendLegs
{
    public static readonly ImmutableArray<Func<BackendLeg?>> Makers = [GlLeg.Open];

    /// <summary>Every leg the job has, opened now.</summary>
    internal static ImmutableArray<BackendLeg> Open()
    {
        var legs = ImmutableArray.CreateBuilder<BackendLeg>();
        foreach (var make in Makers)
        {
            if (make() is { } leg)
                legs.Add(leg);
        }
        if (legs.Count == 0)
            throw new InvalidOperationException("this job has no GPU backend");
        return legs.ToImmutable();
    }

    /// <summary>Whether a frame's list is inside a pass, as the backend's own list says.</summary>
    public static bool InPass(ICommandList list) => list switch
    {
        GlCommandList gl => gl.InPass,
        _ => throw new ArgumentException($"no leg of this job records into a {list.GetType().Name}", nameof(list)),
    };
}

/// <summary>One backend of the job: a device made, used and disposed on a thread of its own, the errors its own checks raised, the
/// resources it holds alive, and a wait for the GPU. Every body handed to <see cref="Run{T}"/> runs on that thread, one at a time,
/// in order.</summary>
public abstract class BackendLeg : IDisposable
{
    private readonly GpuThread _thread;
    private IGpuDevice? _device;

    protected BackendLeg(string name)
    {
        Name = name;
        _thread = new GpuThread("gpu tests: " + name);
    }

    /// <summary>The leg's name in failures and logs: its backend and dialect ("gl core330", "gl es300").</summary>
    public string Name { get; }

    public abstract GpuBackendKind Kind { get; }

    /// <summary>The leg's device, on the leg's thread only.</summary>
    public IGpuDevice Device => _device ?? throw new InvalidOperationException($"the {Name} leg has no device");

    /// <summary>What the backend's own checks saw since the last take (GL: its error flags and debug output).</summary>
    public abstract IReadOnlyList<string> TakeErrors();

    /// <summary>The textures, targets and buffers the device holds alive now.</summary>
    public abstract GpuCensus Census();

    /// <summary>Waits until the GPU has done everything asked of it so far.</summary>
    public abstract void Finish();

    /// <summary>Runs a body on the leg's thread and returns its answer; an exception rethrows here with its stack. A body run from
    /// the leg's own thread runs at once.</summary>
    public T Run<T>(Func<BackendLeg, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _thread.Run(() => body(this));
    }

    public void Run(Action<BackendLeg> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _thread.Run(() =>
        {
            body(this);
            return true;
        });
    }

    /// <summary>Makes the device on the leg's thread; a device the job cannot make throws here.</summary>
    protected void Start(Func<IGpuDevice> open) => _thread.Run(() => _device = open());

    /// <summary>Lets the device go, on the leg's thread.</summary>
    protected abstract void Close();

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _thread.Run(() =>
        {
            Close();
            return true;
        });
        _thread.Dispose();
    }
}

/// <summary>The resources a device holds alive.</summary>
public readonly record struct GpuCensus(int Textures, int Targets, int Buffers);

/// <summary>A thread that runs the bodies handed to it one at a time, in order, and hands each answer back: a GPU context is current
/// on one thread, and xUnit runs test bodies on whichever pool thread is free. A body handed from the thread itself runs at once.</summary>
public sealed class GpuThread : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public GpuThread(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var action in _work.GetConsumingEnumerable())
            action();
    }

    /// <summary>Runs a body on the thread and returns its result; an exception rethrows here with its stack.</summary>
    public T Run<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Thread.CurrentThread == _thread)
            return body();
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim();
        _work.Add(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        failure?.Throw();
        return result;
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join();
        _work.Dispose();
    }
}
