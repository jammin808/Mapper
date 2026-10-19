using System.Text;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Effects;
using PlayMap.Render.Engine;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests.Effects;

/// <summary>The compile lane's second context is made the way output windows are: on the primary's own API (EGL when the primary
/// is a GLFW window on EGL) and under the window-hint gate, so it shares the primary's objects and compiles off the render thread
/// rather than being asked across APIs, which the window system refuses. Where there is no second context the lane makes one program
/// a frame on the render thread, however many lists the frame opens.</summary>
public sealed class FxLaneContextTests
{
    private const string Vertex = "in vec2 aPos;\nvoid main() { gl_Position = vec4(aPos, 0.0, 1.0); }\n";
    private const string Fragment = "out vec4 oColor;\nvoid main() { oColor = vec4(1.0, 0.5, 0.25, 1.0); }\n";

    private const string FullscreenVs = """
        out vec2 vUv;
        void main() {
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            vUv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    [LinuxFact]
    [Trait("Category", "Linux")]
    public void OnTheRenderThreadTheLaneMakesOneProgramAFrameHoweverManyListsTheFrameOpens()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var c = RenderContext.CreateSurfaceless(new GlDeviceOptions(CheckErrors: true, DebugOutput: false, Dialect: GlLeg.Dialect));
                var b = c.Backend;
                var project = Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Solid("#336699"), 32, 18)]);
                using var show = new RenderEngine(b, new RenderEngineOptions(GpuTiming: false));
                using var draft = new RenderEngine(b, new RenderEngineOptions(GpuTiming: false));
                for (var f = 0; f < 3; f++)
                    show.Render(Scene.Inputs(project, 1 + f / 60.0));
                var makes = Enumerable.Range(0, 6).Select(i => b.CreateProgramAsync(new ProgramSource($"user {i}", $"k{i}", ProgramCode.Glsl,
                    Encoding.UTF8.GetBytes(FullscreenVs),
                    Encoding.UTF8.GetBytes($"in vec2 vUv;\nout vec4 oColor;\nvoid main() {{ oColor = vec4(vUv, {i}.0 / 8.0, 1.0); }}\n"), default),
                    CancellationToken.None)).ToArray();
                var finished = new List<int>();
                for (var f = 0; f < 12; f++)
                {
                    show.Render(Scene.Inputs(project, 1 + f / 60.0));
                    // Three previews after the frame, each a list of its own numbered as the frame, and a draft view's renderer,
                    // whose frames count from 1.
                    for (var p = 0; p < 3; p++)
                        b.Submit(b.BeginFrame(show.Frame));
                    draft.Render(Scene.Inputs(project, 1));
                    finished.Add(makes.Count(m => m.IsCompleted));
                }
                Assert.Equal("render-thread", b.CompileLane!.Mode);
                Assert.All(finished.Zip(finished.Skip(1)), p => Assert.InRange(p.Second - p.First, 0, 1));
                Assert.All(makes, m => Assert.True(m.IsCompletedSuccessfully && m.Result.Ok, m.IsCompletedSuccessfully ? m.Result.Words : "not made"));
                Assert.Empty(c.Device.TakeErrors());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { Name = "lane frame test" };
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("the render-thread lane's frames failed", failure);
    }

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
                // On Linux ES is made on EGL (or on GLX, saying so); elsewhere the window system's own API makes it and has nothing to say.
                if (OperatingSystem.IsLinux())
                    Assert.True(c.Api == "egl" || c.Words.StartsWith("ES through GLX", StringComparison.Ordinal), $"{c.Api}: {c.Words}");
                else
                    Assert.Equal((OperatingSystem.IsWindows() ? "wgl" : "cocoa", ""), (c.Api, c.Words));
                Assert.Equal(c.Api == "egl", c.EglWindows);
                using var lane = new FxLane(c.Backend);
                Assert.Equal("shared-context", lane.Mode);
                Assert.Equal("", lane.Words);
                var check = lane.CheckAsync("solid", Vertex, Fragment);
                Assert.True(BackendFrames.Until(c.Backend, () => check.IsCompleted), "the check never came back");
                var report = check.GetAwaiter().GetResult();
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
