using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>One OpenGL ES 3.0 context for the ES leg, owned by a thread of its own: on Linux made through EGL's surfaceless platform
/// (Mesa offers OpenGL ES 3.2 here, which compiles "#version 300 es"), so it needs no window system; Windows has no surfaceless
/// EGL, so there it is the window system's own (WGL) on a hidden window, as a node asked for ES makes it.</summary>
public sealed class EsFixture : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RenderContext? _context;

    public EsFixture()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "es tests" };
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    private void Loop()
    {
        try
        {
            var options = new GlDeviceOptions(CheckErrors: true, DebugOutput: true, Dialect: GlDialect.Es300);
            _context = OperatingSystem.IsLinux() ? RenderContext.CreateSurfaceless(options) : RenderContext.CreateHidden(options);
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

    public T Run<T>(Func<RenderContext, T> body)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim();
        _work.Add(() =>
        {
            try
            {
                result = body(_context!);
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

[CollectionDefinition("es")]
public sealed class EsCollection : ICollectionFixture<EsFixture>;
