using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests.Decode;

/// <summary>A desktop 3.3 core context on EGL with no window system (Mesa's surfaceless platform), on a thread of its own: dmabuf
/// imports need an EGL display, which the GL leg's context does not have. Dmabufs are Linux's: elsewhere no context is made (every test
/// of the collection is Linux-only and skipped there), and a body handed to it fails with the words.</summary>
public sealed class EglFixture : IDisposable
{
    private readonly GpuThread _thread = new("egl tests");
    private readonly RenderContext? _context;

    public EglFixture()
    {
        if (OperatingSystem.IsLinux())
            _context = _thread.Run(() => RenderContext.CreateSurfaceless(new GlDeviceOptions(CheckErrors: true, DebugOutput: false, Dialect: GlDialect.Core330)));
    }

    public T Run<T>(Func<RenderContext, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _thread.Run(() => body(_context ?? throw new PlatformNotSupportedException("dmabuf imports need EGL's surfaceless display, on Linux only")));
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
            _context?.Dispose();
            return true;
        });
        _thread.Dispose();
    }
}

[CollectionDefinition("egl")]
public sealed class EglCollection : ICollectionFixture<EglFixture>;
