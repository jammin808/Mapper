using PlayMap.Core.Gpu;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests;

/// <summary>The GL backend's leg: one hidden context in the run's dialect — desktop 3.30 core, or OpenGL ES 3.00 when
/// PLAYMAP_GL_DIALECT says es300 (the same window system, an ES context) — its errors checked and its debug output on. The GL
/// backend's own tests reach its context through <see cref="RunGl{T}"/>.</summary>
public sealed class GlLeg : BackendLeg
{
    private RenderContext? _context;

    private GlLeg()
        : base("gl " + GlDialects.Word(Dialect))
    {
        Start(() =>
        {
            _context = RenderContext.CreateHidden(new GlDeviceOptions(CheckErrors: true, DebugOutput: true, Dialect: Dialect));
            return _context.Backend;
        });
    }

    /// <summary>The leg's maker: GL is on every job.</summary>
    public static BackendLeg? Open() => new GlLeg();

    /// <summary>The dialect this run's context speaks.</summary>
    public static GlDialect Dialect => GlDialects.Parse(Environment.GetEnvironmentVariable("PLAYMAP_GL_DIALECT")) ?? GlDialect.Core330;

    public override GpuBackendKind Kind => GpuBackendKind.Gl;

    /// <summary>The context, on the leg's thread only.</summary>
    public RenderContext Context => _context ?? throw new InvalidOperationException("No context.");

    public override IReadOnlyList<string> TakeErrors() => Context.Device.TakeErrors();

    public override GpuCensus Census()
    {
        var d = Context.Device;
        return new GpuCensus(d.LiveTextures, d.LiveTargets, d.LiveBuffers);
    }

    public override void Finish() => Context.Device.Finish();

    /// <summary>Runs a body on the leg's thread with its context and returns its answer.</summary>
    public T RunGl<T>(Func<RenderContext, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Run(_ => body(Context));
    }

    public void RunGl(Action<RenderContext> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Run(_ => body(Context));
    }

    protected override void Close() => _context?.Dispose();
}

/// <summary>The GL leg of the fixture, for the GL backend's own tests: they name GL types, hold GL facts, and leave with GL.</summary>
public static class GlLegs
{
    extension(BackendFixture fixture)
    {
        /// <summary>The job's GL leg.</summary>
        public GlLeg Gl => (GlLeg)fixture.On(GpuBackendKind.Gl);

        /// <summary>Runs a body on the GL leg's thread with its context and returns its answer.</summary>
        public T RunGl<T>(Func<RenderContext, T> body) => fixture.Gl.RunGl(body);

        public void RunGl(Action<RenderContext> body) => fixture.Gl.RunGl(body);
    }
}
