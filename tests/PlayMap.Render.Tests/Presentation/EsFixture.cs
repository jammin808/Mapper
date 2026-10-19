using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>One OpenGL ES 3.0 context for the ES leg, owned by a thread of its own: on Linux made through EGL's surfaceless platform
/// (Mesa offers OpenGL ES 3.2 here, which compiles "#version 300 es"), so it needs no window system; elsewhere, where there is no
/// surfaceless platform, a hidden ES context made through the window system.</summary>
public sealed class EsFixture : IDisposable
{
    private readonly GpuThread _thread = new("es tests");
    private readonly RenderContext _context;

    public EsFixture()
    {
        var options = new GlDeviceOptions(CheckErrors: true, DebugOutput: true, Dialect: GlDialect.Es300);
        _context = _thread.Run(() => OperatingSystem.IsLinux() ? RenderContext.CreateSurfaceless(options) : RenderContext.CreateHidden(options));
    }

    public T Run<T>(Func<RenderContext, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _thread.Run(() => body(_context));
    }

    public void Run(Action<RenderContext> body) => Run<bool>(c =>
    {
        body(c);
        return true;
    });

    public void Dispose()
    {
        _thread.Run(() =>
        {
            _context.Dispose();
            return true;
        });
        _thread.Dispose();
    }
}

[CollectionDefinition("es")]
public sealed class EsCollection : ICollectionFixture<EsFixture>;
