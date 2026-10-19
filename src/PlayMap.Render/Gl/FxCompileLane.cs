using System.Collections.Concurrent;
using System.Collections.Immutable;
using PlayMap.Core.Gpu;
using PlayMap.Render.Effects;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;

namespace PlayMap.Render.Gl;

/// <summary>A picture an effect imports, decoded on the lane: RGBA bytes, top row first.</summary>
internal sealed record FxDecodedImage(string Name, int Width, int Height, byte[] Rgba);

/// <summary>A program the lane made (or the reason it could not), with its imported pictures decoded.</summary>
internal sealed record FxCompiled(string Key, uint ProgramId, string? Error, ImmutableArray<FxDecodedImage> Images);

/// <summary>A program made for the device (a user program asked of the backend, or a library program a warm-up links), linked and
/// finished on the lane's context but not yet the device's: its GL name and its reflected layout (none for a library program, whose
/// layout is the table's), or the stage that refused ("source", "vertex", "fragment", "link", "layout", "lane") and its words.</summary>
internal sealed record FxMade(uint ProgramId, ProgramLayout? Layout, string Stage, string Words)
{
    public static FxMade Refused(string stage, string words) => new(0, null, stage, words);
}

/// <summary>A program the lane is making for the device: the lane writes its outcome once, from its thread; any thread reads it.</summary>
internal sealed class FxMaking
{
    private FxMade? _made;

    /// <summary>The outcome, null until the lane has run the job.</summary>
    public FxMade? Made => Volatile.Read(ref _made);

    internal void Finish(FxMade made) => Interlocked.CompareExchange(ref _made, made, null);
}

/// <summary>A program ready on the render thread: the program, or why there is none, and its imported pictures uploaded.</summary>
internal sealed class FxProgramEntry : IDisposable
{
    public required string Key { get; init; }
    public ShaderProgram? Program { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, GpuTexture> Images { get; } = new(StringComparer.Ordinal);
    public long LastUsed;

    public void Dispose()
    {
        Program?.Dispose();
        foreach (var t in Images.Values)
            t.Dispose();
        Images.Clear();
    }
}

/// <summary>Where effects' programs are compiled: on a second GL context that shares the render context's objects, on a thread of
/// its own, so a compile never runs on the render thread and never delays a frame (a slot shows its input until its program is
/// handed over). The lane links, finishes its context, and the render thread takes the program at its next frame. When a shared
/// context cannot be made, the lane runs on the render thread instead, one job a frame, and says so in <see cref="Mode"/>.
///
/// Threading: made, collected and disposed on the render thread with the render context current; <see cref="Request"/> and
/// <see cref="CheckAsync"/> from any thread.</summary>
public sealed class FxCompileLane : IDisposable
{
    /// <summary>A program no slot has used for this many frames is let go.</summary>
    public const int UnusedFrames = 600;

    private sealed record Job(string Key, string Name, string Vertex, string Fragment, ImmutableSortedDictionary<string, string> Images,
        TaskCompletionSource<FxCompileReport>? Report, FxMaking? Making = null, bool Reflect = true);

    private readonly RenderContext _context;
    private readonly BlockingCollection<Job> _jobs = new();
    private readonly ConcurrentQueue<FxCompiled> _done = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FxProgramEntry> _programs = new(StringComparer.Ordinal);
    private readonly List<string> _stale = [];
    private readonly GlDialect _dialect;
    private readonly GL _gl;
    private readonly IWindow? _window;
    private readonly Thread? _thread;
    private bool _disposed;

    /// <param name="context">The render context (current on the calling thread).</param>
    /// <param name="sharedContext">False runs the lane on the render thread (the fallback, or a test of it).</param>
    public FxCompileLane(RenderContext context, bool sharedContext = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _dialect = context.Device.Dialect;
        _gl = context.Gl;
        Mode = "render-thread";
        // A context with no window to share from (a surfaceless one) compiles on the render thread.
        if (!sharedContext || context.Window is not { } primary)
            return;
        try
        {
            var options = WindowOptions.Default with
            {
                IsVisible = false,
                Size = new Vector2D<int>(16, 16),
                Title = "PlayMap effects",
                API = primary.API,
                SharedContext = primary.GLContext,
                ShouldSwapAutomatically = false,
                VSync = false,
                PreferredDepthBufferBits = 0,
                PreferredStencilBufferBits = 0,
            };
            // Made as output windows are, under the hint gate and on the primary's API: a shared context asked across APIs (a
            // GLX window sharing with an EGL one) is refused by the window system.
            var window = RenderContext.MakeWindow(options, context.EglWindows);
            context.MakeCurrent();
            _window = window;
            _thread = new Thread(Loop) { IsBackground = true, Name = "fx compile" };
            Mode = "shared-context";
            _thread.Start();
        }
        // The boundary of the second context: whatever the platform throws making it (a windowing library's own exception
        // included), the effects stay — they compile on the render thread, one a frame — and the renderer starts.
        catch (Exception ex)
        {
            context.MakeCurrent();
            Words = "no second GL context (" + ex.Message + "): effects compile on the render thread, one a frame";
        }
    }

    /// <summary>"shared-context" (a thread and a context of its own) or "render-thread" (the fallback).</summary>
    public string Mode { get; }

    /// <summary>Why the lane runs where it does, when that is not on its own context ("" otherwise).</summary>
    public string Words { get; } = "";

    /// <summary>Runs before each compile on the lane with the program's name, and may block it there: a test holds a compile open
    /// with it and counts the frames that go on meanwhile.</summary>
    public Action<string>? BeforeCompile { get; set; }

    /// <summary>The ISF dialect this lane's context compiles.</summary>
    public PlayMap.Core.Effects.IsfDialect Dialect => FxChainRunner.DialectOf(_dialect);

    /// <summary>Jobs asked for and not yet handed over.</summary>
    public int Pending => _pending.Count;

    /// <summary>Programs held for slots.</summary>
    public int Programs => _programs.Count;

    /// <summary>Asks for a program once (a key already asked for or made is not asked again). Any thread.</summary>
    internal void Request(string key, string name, string vertex, string fragment, ImmutableSortedDictionary<string, string> images)
    {
        if (_disposed || _programs.ContainsKey(key) || !_pending.TryAdd(key, 0))
            return;
        _jobs.Add(new Job(key, name, vertex, fragment, images, null));
    }

    /// <summary>Compiles sources for a report only: the outcome arrives when the lane has run it. Any thread.</summary>
    public Task<FxCompileReport> CheckAsync(string name, string vertex, string fragment)
    {
        var done = new TaskCompletionSource<FxCompileReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_disposed && _jobs.TryAddLive(new Job("", name, vertex, fragment, ImmutableSortedDictionary<string, string>.Empty, done)))
            return done.Task;
        done.TrySetResult(new FxCompileReport(false, "the renderer is stopping", Mode));
        return done.Task;
    }

    /// <summary>Compiles, links and (unless <paramref name="reflect"/> is false: a library program, whose layout is the table's)
    /// reflects a program for the device: the outcome is written when the lane has run it, the program
    /// finished on the lane's context and not yet the device's (the render thread adopts it, <see cref="GlDevice.AdoptProgram"/>, or
    /// deletes it). Any thread.</summary>
    internal FxMaking Make(string name, string vertex, string fragment, bool reflect = true)
    {
        var making = new FxMaking();
        if (!_disposed && _jobs.TryAddLive(new Job("", name, vertex, fragment, ImmutableSortedDictionary<string, string>.Empty, null, making, reflect)))
            return making;
        making.Finish(FxMade.Refused("lane", "the renderer is stopping"));
        return making;
    }

    /// <summary>The program for a key once handed over (null while it compiles). Render thread.</summary>
    internal FxProgramEntry? Get(string key, long frame)
    {
        if (!_programs.TryGetValue(key, out var e))
            return null;
        e.LastUsed = frame;
        return e;
    }

    /// <summary>True while a key is asked for and not yet handed over.</summary>
    internal bool IsPending(string key) => _pending.ContainsKey(key);

    /// <summary>Takes the programs the lane has finished (and, on the fallback, compiles one job here); lets go of programs no
    /// slot has used for a while. Render thread, once a frame.</summary>
    public void Collect(long frame)
    {
        if (_disposed)
            return;
        if (_thread is null && _jobs.TryTake(out var job))
        {
            if (job.Making is { } making)
                making.Finish(Make(job));
            else
                Finish(Compile(job), job);
        }
        while (_done.TryDequeue(out var compiled))
            Adopt(compiled);
        if (frame % 60 != 0)
            return;
        foreach (var (key, e) in _programs)
            if (frame - e.LastUsed > UnusedFrames)
                _stale.Add(key);
        foreach (var key in _stale)
        {
            _programs[key].Dispose();
            _programs.Remove(key);
        }
        _stale.Clear();
    }

    private void Adopt(FxCompiled compiled)
    {
        var device = _context.Device;
        var entry = new FxProgramEntry
        {
            Key = compiled.Key,
            Program = compiled.ProgramId != 0 ? device.AdoptProgram(compiled.ProgramId, "fx " + compiled.Key[..Math.Min(8, compiled.Key.Length)]) : null,
            Error = compiled.Error,
        };
        foreach (var img in compiled.Images)
        {
            var t = device.CreateTexture(img.Width, img.Height, TextureFormat.Rgba8);
            device.Upload(t, img.Rgba, UploadFormat.Rgba8);
            entry.Images[img.Name] = t;
        }
        if (_programs.Remove(compiled.Key, out var old))
            old.Dispose();
        _programs[compiled.Key] = entry;
        _pending.TryRemove(compiled.Key, out _);
    }

    private void Loop()
    {
        var window = _window!;
        window.MakeCurrent();
        try
        {
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                if (job.Making is { } making)
                {
                    var program = Make(job);
                    _gl.Finish();
                    making.Finish(program);
                    continue;
                }
                var compiled = Compile(job);
                _gl.Finish();
                Finish(compiled, job);
            }
        }
        finally
        {
            window.GLContext?.Clear();
        }
    }

    /// <summary>Hands a finished job to whoever waits for it: a report deletes its program, a slot's program waits for the render
    /// thread to take it.</summary>
    private void Finish(FxCompiled compiled, Job job)
    {
        if (job.Report is not { } report)
        {
            _done.Enqueue(compiled);
            return;
        }
        if (compiled.ProgramId != 0)
            _gl.DeleteProgram(compiled.ProgramId);
        report.TrySetResult(new FxCompileReport(compiled.Error is null, compiled.Error, Mode));
    }

    /// <summary>One job, wherever the lane runs. Its boundary: whatever a job throws (a driver's or a decoder's exception)
    /// faults that job with its words and the lane goes on, so nothing a file holds stops the compiles after it, the frame or the
    /// process.</summary>
    private FxCompiled Compile(Job job)
    {
        try
        {
            BeforeCompile?.Invoke(job.Name);
            return Build(job);
        }
        catch (Exception ex)
        {
            return new FxCompiled(job.Key, 0, $"the compile lane could not build {job.Name}: {ex.Message}", []);
        }
    }

    /// <summary>A program for the device, wherever the lane runs: compiled and linked with the stage that refused, then its layout
    /// read back. Its boundary is <see cref="Compile"/>'s: whatever the driver throws refuses this program with its words.</summary>
    private FxMade Make(Job job)
    {
        try
        {
            BeforeCompile?.Invoke(job.Name);
            uint id;
            string? error;
            string stage;
            try
            {
                id = GlDevice.CompileDetached(_gl, _dialect, job.Name, job.Vertex, job.Fragment, out error, out stage);
            }
            catch (ArgumentException ex)
            {
                return FxMade.Refused("source", ex.Message);
            }
            if (id == 0)
                return FxMade.Refused(stage, error ?? "the program was not made");
            if (!job.Reflect)
                return new FxMade(id, null, "", "");
            var layout = GlDevice.ReflectLayout(_gl, id, job.Name, job.Vertex, job.Fragment, out error);
            if (layout is null)
            {
                _gl.DeleteProgram(id);
                return FxMade.Refused("layout", error ?? "the program's layout does not read");
            }
            return new FxMade(id, layout, "", "");
        }
        catch (Exception ex)
        {
            return FxMade.Refused("lane", $"the compile lane could not build {job.Name}: {ex.Message}");
        }
    }

    private FxCompiled Build(Job job)
    {
        uint id;
        string? error;
        try
        {
            id = GlDevice.CompileDetached(_gl, _dialect, job.Name, job.Vertex, job.Fragment, out error);
        }
        catch (ArgumentException ex)
        {
            (id, error) = (0, ex.Message);
        }
        var images = ImmutableArray.CreateBuilder<FxDecodedImage>();
        foreach (var (name, data) in job.Images)
        {
            if (Decode(name, data, out var why) is { } img)
                images.Add(img);
            else
                error ??= why;
        }
        if (error is not null && id != 0)
        {
            _gl.DeleteProgram(id);
            id = 0;
        }
        return new FxCompiled(job.Key, id, error, images.ToImmutable());
    }

    /// <summary>The most pixels an imported picture may have (4096 × 4096): its size is read from its header before it is
    /// decoded, so a small file that would unpack to gigabytes is refused, not decoded.</summary>
    internal const long MaxImagePixels = 4096L * 4096;

    private static FxDecodedImage? Decode(string name, string base64, out string why)
    {
        why = $"the imported image {name} does not decode (PNG or JPEG, base64)";
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
        using (var data = SKData.CreateCopy(bytes))
        using (var codec = SKCodec.Create(data))
        {
            if (codec is null)
                return null;
            if ((long)codec.Info.Width * codec.Info.Height > MaxImagePixels)
            {
                why = $"the imported image {name} is {codec.Info.Width} × {codec.Info.Height}; an effect's picture is at most 4096 × 4096";
                return null;
            }
        }
        using var decoded = SKBitmap.Decode(bytes);
        if (decoded is null || decoded.Width < 1 || decoded.Height < 1)
            return null;
        using var rgba = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (!decoded.CopyTo(rgba, SKColorType.Rgba8888))
            return null;
        return new FxDecodedImage(name, rgba.Width, rgba.Height, rgba.Bytes);
    }

    /// <summary>Stops the lane (a held compile must be let go first), lets its context go and deletes the programs. Render
    /// thread.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _jobs.CompleteAdding();
        _thread?.Join();
        while (_jobs.TryTake(out var left))
        {
            left.Report?.TrySetResult(new FxCompileReport(false, "the renderer stopped", Mode));
            left.Making?.Finish(FxMade.Refused("lane", "the renderer stopped"));
        }
        while (_done.TryDequeue(out var orphan))
            if (orphan.ProgramId != 0)
                _gl.DeleteProgram(orphan.ProgramId);
        if (_window is not null)
        {
            _window.Dispose();
            _context.MakeCurrent();
        }
        foreach (var e in _programs.Values)
            e.Dispose();
        _programs.Clear();
        _pending.Clear();
        _jobs.Dispose();
    }
}

/// <summary>Adding to a queue that the render thread may close (or let go) between a caller's check and its add.</summary>
internal static class FxJobQueue
{
    /// <summary>Adds the item; false when the queue no longer takes items (it was completed or disposed meanwhile).</summary>
    public static bool TryAddLive<T>(this BlockingCollection<T> queue, T item)
    {
        try
        {
            return queue.TryAdd(item);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }
}
