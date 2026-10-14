using PlayMap.Render.Effects;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;

namespace PlayMap.Render.Tests.Effects;

/// <summary>The compile lane's second context is made the way output windows are: on the primary's own API (EGL when the primary
/// is a GLFW window on EGL) and under the window-hint gate, so it shares the primary's objects and compiles off the render thread
/// rather than being asked across APIs, which the window system refuses.</summary>
public sealed class FxLaneContextTests
{
    private const string Vertex = "in vec2 aPos;\nvoid main() { gl_Position = vec4(aPos, 0.0, 1.0); }\n";
    private const string Fragment = "out vec4 oColor;\nvoid main() { oColor = vec4(1.0, 0.5, 0.25, 1.0); }\n";

    [Fact]
    public void AnEsContextMadeThroughTheWindowSystemCompilesOnASharedContext()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var c = RenderContext.CreateHidden(new GlDeviceOptions(CheckErrors: true, DebugOutput: false, Dialect: GlDialect.Es300));
                Assert.Equal(GlDialect.Es300, c.Dialect);
                Assert.True(c.Api == "egl" || c.Words.StartsWith("ES through GLX", StringComparison.Ordinal), $"{c.Api}: {c.Words}");
                Assert.Equal(c.Api == "egl", c.EglWindows);
                using var lane = new FxCompileLane(c);
                Assert.Equal("shared-context", lane.Mode);
                Assert.Equal("", lane.Words);
                var report = lane.CheckAsync("solid", Vertex, Fragment).WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
                Assert.True(report.Ok, report.Error);
                Assert.Equal("shared-context", report.Lane);
                Assert.Empty(c.Device.TakeErrors());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { Name = "es lane test" };
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("the ES context's compile lane failed", failure);
    }
}
