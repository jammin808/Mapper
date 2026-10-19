using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using PlayMap.Core.Gpu;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests;

/// <summary>One hidden GL context for every GPU test, owned by a dedicated thread: a GL context is current on
/// one thread, and xUnit runs test bodies on whichever pool thread is free, so each test hands its body to
/// this thread and waits for it.</summary>
public sealed class GlFixture : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RenderContext? _context;

    public GlFixture()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "gl tests" };
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    public RenderContext Context => _context ?? throw new InvalidOperationException("No context.");

    /// <summary>The dialect this run's context speaks: desktop 3.30 core, or ES 3.00 when PLAYMAP_GL_DIALECT says es300.</summary>
    public static GlDialect Dialect => GlDialects.Parse(Environment.GetEnvironmentVariable("PLAYMAP_GL_DIALECT")) ?? GlDialect.Core330;

    public GlDevice Device => Context.Device;

    private void Loop()
    {
        try
        {
            // PLAYMAP_GL_DIALECT=es300 runs the whole suite as its OpenGL ES 3.00 leg (the same window system, an ES context).
            _context = RenderContext.CreateHidden(new GlDeviceOptions(CheckErrors: true, DebugOutput: true, Dialect: Dialect));
            _ready.SetResult();
        }
        catch (Exception ex)
        {
            _ready.SetException(ex);
            return;
        }
        foreach (var action in _work.GetConsumingEnumerable())
            action();
        _context.Dispose();
    }

    /// <summary>Runs a body on the GL thread and returns its result; an exception rethrows here with its stack.</summary>
    public T Run<T>(Func<RenderContext, T> body)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim();
        _work.Add(() =>
        {
            try
            {
                result = body(Context);
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

    public void Run(Action<RenderContext> body) => Run<bool>(c =>
    {
        body(c);
        return true;
    });

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join();
        _work.Dispose();
    }
}

[CollectionDefinition("gl")]
public sealed class GlCollection : ICollectionFixture<GlFixture>;

/// <summary>Reading pictures back in tests.</summary>
public static class Pixels
{
    /// <summary>A target read back as BGRA bytes, top row first.</summary>
    public static byte[] Read(GlDevice device, RenderTarget target)
    {
        var bytes = new byte[target.Width * target.Height * 4];
        device.ReadPixels(target, bytes);
        return bytes;
    }

    /// <summary>(r, g, b) at a pixel of a BGRA picture.</summary>
    public static (int R, int G, int B) At(byte[] bgra, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    public static void Near((int R, int G, int B) expected, (int R, int G, int B) actual, int tolerance, string what = "")
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"{what} expected {expected} ±{tolerance}, got {actual}");
    }
}
