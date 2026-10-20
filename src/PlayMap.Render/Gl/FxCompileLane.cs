using System.Collections.Concurrent;
using PlayMap.Core.Gpu;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace PlayMap.Render.Gl;

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

/// <summary>Where the GL backend makes programs off the render thread (user programs, warm-ups' links): on a second GL context that
/// shares the render context's objects, on a thread of its own, so a compile never runs on the render thread and never delays a
/// frame. The lane links, finishes its context, and the render thread adopts the program at a later frame. When a shared context
/// cannot be made, the lane runs on the render thread instead, one job a frame, and says so in <see cref="Mode"/>.
///
/// Threading: made, collected and disposed on the render thread with the render context current; <see cref="Make"/> from any
/// thread.</summary>
public sealed class FxCompileLane : IDisposable
{
    private sealed record Job(string Name, string Vertex, string Fragment, FxMaking Making, bool Reflect);

    private readonly RenderContext _context;
    private readonly BlockingCollection<Job> _jobs = new();
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

    /// <summary>Runs before each make on the lane with the program's name, and may block it there: a test holds a compile open
    /// with it and counts the frames that go on meanwhile.</summary>
    public Action<string>? BeforeCompile { get; set; }

    /// <summary>Compiles, links and (unless <paramref name="reflect"/> is false: a library program, whose layout is the table's)
    /// reflects a program for the device: the outcome is written when the lane has run it, the program
    /// finished on the lane's context and not yet the device's (the render thread adopts it, <see cref="GlDevice.AdoptProgram"/>, or
    /// deletes it). Any thread.</summary>
    internal FxMaking Make(string name, string vertex, string fragment, bool reflect = true)
    {
        var making = new FxMaking();
        if (!_disposed && _jobs.TryAddLive(new Job(name, vertex, fragment, making, reflect)))
            return making;
        making.Finish(FxMade.Refused("lane", "the renderer is stopping"));
        return making;
    }

    /// <summary>On the fallback, runs one make here. Render thread, once a frame.</summary>
    public void Collect()
    {
        if (_disposed || _thread is not null || !_jobs.TryTake(out var job))
            return;
        job.Making.Finish(Make(job));
    }

    private void Loop()
    {
        var window = _window!;
        window.MakeCurrent();
        try
        {
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                var program = Make(job);
                _gl.Finish();
                job.Making.Finish(program);
            }
        }
        finally
        {
            window.GLContext?.Clear();
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

    /// <summary>Stops the lane (a held compile must be let go first) and lets its context go; a make it never ran is refused.
    /// Render thread.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _jobs.CompleteAdding();
        _thread?.Join();
        while (_jobs.TryTake(out var left))
            left.Making.Finish(FxMade.Refused("lane", "the renderer stopped"));
        if (_window is not null)
        {
            _window.Dispose();
            _context.MakeCurrent();
        }
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
