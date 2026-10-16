using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;

namespace PlayMap.Render.Tests.Decode;

/// <summary>A desktop 3.3 core context on EGL with no window system (Mesa's surfaceless platform), on a thread of its own: dmabuf
/// imports need an EGL display, which the GLX fixture does not have.</summary>
public sealed class EglFixture : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RenderContext? _context;

    public EglFixture()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "egl tests" };
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    private void Loop()
    {
        try
        {
            _context = RenderContext.CreateSurfaceless(new GlDeviceOptions(CheckErrors: true, DebugOutput: false, Dialect: GlDialect.Core330));
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

[CollectionDefinition("egl")]
public sealed class EglCollection : ICollectionFixture<EglFixture>;
