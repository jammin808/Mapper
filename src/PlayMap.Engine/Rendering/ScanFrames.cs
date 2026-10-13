using System.Buffers;
using System.Collections.Concurrent;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>The scan's frames on the outputs (<see cref="IScanDisplay"/> through the render host): one 8-bit
/// grey frame per output, shown raw — the whole raster, code for code, no warp, blend, mask, lens, trim or
/// rotation — while every other output keeps the show.
///
/// <para>Any thread hands a frame in (<see cref="ShowAsync"/>; a newer one replaces one not yet drawn, whose
/// task is cancelled). The render thread takes the newest at the start of a frame
/// (<see cref="BeginFrame"/>), hands it to the renderer once as a raw source, and after the frame completes the
/// task with the monotonic instant it was presented (<see cref="AfterFrame"/>). A frame for an output that is
/// gone, off, of another size, or that arrives during blackout faults with the reason: an attempt is not a
/// fact, and a frame the projector never showed must not be reported as shown. <see cref="Release"/> hands
/// every output back to the show; frames handed in after it are kept.</para></summary>
internal sealed class ScanFrames : IRawOutputs, IDisposable
{
    private sealed class Pending(byte[] buffer, int width, int height, long releaseSeq)
    {
        public byte[] Buffer { get; } = buffer;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public long ReleaseSeq { get; } = releaseSeq;
        public TaskCompletionSource<long> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;

        public void Fault(Exception ex)
        {
            Registration.Dispose();
            Done.TrySetException(ex);
            ArrayPool<byte>.Shared.Return(Buffer);
        }

        public void Cancel()
        {
            Registration.Dispose();
            Done.TrySetCanceled(CancellationToken.None);
            ArrayPool<byte>.Shared.Return(Buffer);
        }
    }

    private sealed class Slot(string outputId)
    {
        public string OutputId { get; } = outputId;

        /// <summary>The raw source the renderer is told to draw for this output (one instance per slot).</summary>
        public SourceRef Source { get; } = new(SourceKind.Live, "scan:" + outputId);

        public Pending? Incoming;
        public Pending? Showing;
        public bool HandOut;
        public bool Handed;
        public long Generation;
    }

    private readonly Func<Project?> _project;
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Slot> _new = new();
    // Render thread.
    private readonly Dictionary<string, Slot> _byOutput = new(StringComparer.Ordinal);
    private readonly Dictionary<SourceRef, Slot> _bySource = [];
    private readonly List<Slot> _list = [];
    private long _releaseSeen;
    private int _showing;
    // Shared.
    private long _releaseSeq;
    private int _incoming;
    private volatile string? _closed = "the render host has not started";

    /// <param name="project">The project as it is now (for checking an output before a frame is queued).</param>
    public ScanFrames(Func<Project?> project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
    }

    /// <summary>Opens the display (the render loop is running) or closes it with the reason new frames get.</summary>
    public void SetOpen(string? closedBecause) => _closed = closedBecause;

    // ---- any thread -----------------------------------------------------------------------------------

    public Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outputId);
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<long>(ct);
        if (_closed is { } why)
            return Task.FromException<long>(new InvalidOperationException(why));
        if (width < 1 || height < 1 || gray8.Length < (long)width * height)
            return Task.FromException<long>(new ArgumentException($"a {width}×{height} grey frame needs {(long)width * height} bytes; {gray8.Length} came", nameof(gray8)));
        if (Check(_project()?.FindOutput(outputId), outputId, width, height) is { } problem)
            return Task.FromException<long>(new InvalidOperationException(problem));
        var buffer = ArrayPool<byte>.Shared.Rent(width * height);
        gray8.Span[..(width * height)].CopyTo(buffer);
        var pending = new Pending(buffer, width, height, Interlocked.Read(ref _releaseSeq));
        if (ct.CanBeCanceled)
            pending.Registration = ct.Register(static (state, token) => ((Pending)state!).Done.TrySetCanceled(token), pending);
        if (!_slots.TryGetValue(outputId, out var slot))
        {
            var made = new Slot(outputId);
            slot = _slots.GetOrAdd(outputId, made);
            if (ReferenceEquals(slot, made))
                _new.Enqueue(made);
        }
        Interlocked.Exchange(ref slot.Incoming, pending)?.Cancel();
        Volatile.Write(ref _incoming, 1);
        return pending.Done.Task;
    }

    public void Release() => Interlocked.Increment(ref _releaseSeq);

    private static string? Check(Output? output, string outputId, int width, int height)
    {
        if (output is null)
            return $"no output {outputId}";
        if (!output.Enabled)
            return $"{output.Name} is off — a scan frame on it would never be seen";
        if (output.Width != width || output.Height != height)
            return $"the frame is {width}×{height} but {output.Name}'s raster is {output.Width}×{output.Height}";
        return null;
    }

    // ---- render thread --------------------------------------------------------------------------------

    /// <summary>Takes the frames handed in since the last frame (and a release asked for).</summary>
    public void BeginFrame(Project project, bool blackout)
    {
        while (_new.TryDequeue(out var added))
        {
            if (_byOutput.TryAdd(added.OutputId, added))
            {
                _bySource[added.Source] = added;
                _list.Add(added);
            }
        }
        var release = Interlocked.Read(ref _releaseSeq);
        if (release != _releaseSeen)
        {
            _releaseSeen = release;
            foreach (var slot in _list)
            {
                if (slot.Showing is { } shown)
                {
                    ArrayPool<byte>.Shared.Return(shown.Buffer);
                    slot.Showing = null;
                }
                if (Volatile.Read(ref slot.Incoming) is { } waiting && waiting.ReleaseSeq < release && Interlocked.CompareExchange(ref slot.Incoming, null, waiting) == waiting)
                    waiting.Cancel();
                slot.HandOut = false;
            }
            _showing = 0;
        }
        if (Interlocked.Exchange(ref _incoming, 0) == 0)
            return;
        foreach (var slot in _list)
        {
            if (Interlocked.Exchange(ref slot.Incoming, null) is not { } next)
                continue;
            if (next.Done.Task.IsCanceled)
            {
                // Its caller gave up on it before it was drawn.
                next.Cancel();
                continue;
            }
            if (Check(project.FindOutput(slot.OutputId), slot.OutputId, next.Width, next.Height) is { } problem)
            {
                next.Fault(new InvalidOperationException(problem));
                continue;
            }
            if (blackout)
            {
                next.Fault(new InvalidOperationException("the node is blacked out — a scan frame would not reach the projector"));
                continue;
            }
            if (slot.Showing is { } old)
                ArrayPool<byte>.Shared.Return(old.Buffer);
            else
                _showing++;
            slot.Showing = next;
            slot.Generation++;
            slot.HandOut = true;
        }
    }

    /// <summary>For the renderer: the raw source an output shows this frame, or null for the show.</summary>
    public SourceRef? For(string outputId)
    {
        if (_showing == 0 || !_byOutput.TryGetValue(outputId, out var slot) || slot.Showing is null)
            return null;
        return slot.Source;
    }

    /// <summary>For the provider: true when the source is a scan frame; the lease is the new frame the first
    /// time it is asked for, null after (the GPU has it).</summary>
    public bool TryAcquire(SourceRef source, out VideoFrameLease? lease)
    {
        lease = null;
        if (_list.Count == 0 || !_bySource.TryGetValue(source, out var slot))
            return false;
        if (slot.HandOut && slot.Showing is { } frame)
        {
            lease = new PinnedLease(PixelLayout.Gray8, frame.Buffer, frame.Width, frame.Height, frame.Width, slot.Generation);
            slot.HandOut = false;
            slot.Handed = true;
        }
        return true;
    }

    /// <summary>After the frame presented: completes every frame drawn this frame with its present instant.</summary>
    public void AfterFrame(Func<string, long> presentedNs)
    {
        ArgumentNullException.ThrowIfNull(presentedNs);
        if (_showing == 0)
            return;
        foreach (var slot in _list)
        {
            if (!slot.Handed)
                continue;
            slot.Handed = false;
            if (slot.Showing is { } shown)
            {
                shown.Registration.Dispose();
                shown.Done.TrySetResult(presentedNs(slot.OutputId));
            }
        }
    }

    /// <summary>True when some output drew a scan frame this frame (the host then measures its present).</summary>
    public bool AnyHanded
    {
        get
        {
            if (_showing == 0)
                return false;
            foreach (var slot in _list)
                if (slot.Handed)
                    return true;
            return false;
        }
    }

    /// <summary>Fails every frame still waiting (the host is going away).</summary>
    public void Dispose()
    {
        _closed = "the render host has stopped";
        foreach (var slot in _slots.Values)
        {
            Interlocked.Exchange(ref slot.Incoming, null)?.Fault(new ObjectDisposedException("the render host has stopped"));
            slot.Showing?.Registration.Dispose();
            slot.Showing?.Done.TrySetException(new ObjectDisposedException("the render host has stopped"));
        }
    }
}
