using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;
using PlayMap.Render.Gl;

namespace PlayMap.Render.Tests.Spine;

/// <summary>The air path end to end on llvmpipe: with no levels the output's codes are the picture's own (a gain of one changes no
/// byte); a gain of 0.5 halves one output and leaves the other; a hold keeps one output's last picture while the other moves on; an
/// override shows its source on its output only; and the packages' passes run at their three points in their order, none of them
/// in a blackout but the frame's end.</summary>
[Collection("gl")]
public sealed class AirPlumbingTests(GlFixture gl)
{
    private const int W = 32;
    private const int H = 18;

    private sealed class Levels(float gain = 1, string? holdOutput = null, string? overrideOutput = null, SourceRef? safe = null) : IAirLevels
    {
        public float Gain(string outputId, long leaderNs) => outputId == "op1" ? gain : 1f;

        public SourceRef? Override(string outputId, long leaderNs) => outputId == overrideOutput ? safe : null;

        public bool Hold(string outputId, long leaderNs) => outputId == holdOutput;
    }

    private sealed class CountingPass(string name, int order, List<string> said) : IFramePass
    {
        public string Name => name;

        public int Order => order;

        public GpuTexture Picture(in PassFrame frame, Output output, GpuTexture picture)
        {
            said.Add($"{name} picture {output.Id}");
            return picture;
        }

        public void Final(in PassFrame frame, Output output, RenderTarget final) => said.Add($"{name} final {output.Id}");

        public void FrameEnd(in PassFrame frame) => said.Add($"{name} end {(frame.Blackout ? "black" : "show")}");

        public void Dispose()
        {
            // Holds no GPU object.
        }
    }

    private static Project Two(string first, string second) =>
        Scene.Project([], [Scene.SourceOutput("op1", SourceRef.Solid(first), W, H), Scene.SourceOutput("op2", SourceRef.Solid(second), W, H)]);

    /// <summary>Renders frames and answers each output's middle pixel after the last one.</summary>
    private ((int R, int G, int B) One, (int R, int G, int B) Two, byte[] Raw) Render(params FrameInputs[] frames) => gl.Run(c =>
    {
        using var engine = new RenderEngine(c.Backend, new RenderEngineOptions(Dither: false, GpuTiming: false));
        foreach (var f in frames)
            engine.Render(f);
        var one = new byte[W * H * 4];
        var two = new byte[W * H * 4];
        Assert.True(engine.ReadOutput("op1", one));
        Assert.True(engine.ReadOutput("op2", two));
        Assert.Empty(c.Device.TakeErrors());
        return (Pixels.At(one, W, W / 2, H / 2), Pixels.At(two, W, W / 2, H / 2), one);
    });

    [Fact]
    public void WithNoLevelsTheCodesAreThePicturesOwnAndAGainOfOneChangesNoByte()
    {
        var project = Two("#C86432", "#3C78B4");
        var bare = Render(Scene.Inputs(project));
        var one = Render(Scene.Inputs(project) with { Levels = new Levels(gain: 1) });

        Assert.Equal((200, 100, 50), bare.One);
        Assert.Equal((60, 120, 180), bare.Two);
        Assert.Equal(bare.Raw, one.Raw);
    }

    [Fact]
    public void AGainOfAHalfHalvesOneOutputAndLeavesTheOther()
    {
        var got = Render(Scene.Inputs(Two("#C86432", "#3C78B4")) with { Levels = new Levels(gain: 0.5f) });

        Pixels.Near((100, 50, 25), got.One, 1, "op1 at half");
        Assert.Equal((60, 120, 180), got.Two);
        var black = Render(Scene.Inputs(Two("#FFFFFF", "#FFFFFF")) with { Levels = new Levels(gain: 0) });
        Assert.Equal((0, 0, 0), black.One);
    }

    [Fact]
    public void AHoldKeepsOneOutputsLastPictureWhileTheOtherMovesOn()
    {
        var got = Render(
            Scene.Inputs(Two("#FF0000", "#FF0000"), 1),
            Scene.Inputs(Two("#0000FF", "#0000FF"), 1.1) with { Levels = new Levels(holdOutput: "op1") });

        Assert.Equal((255, 0, 0), got.One);
        Assert.Equal((0, 0, 255), got.Two);
    }

    [Fact]
    public void AnOverrideShowsItsSourceOnItsOutputOnly()
    {
        var got = Render(Scene.Inputs(Two("#C86432", "#3C78B4")) with { Levels = new Levels(overrideOutput: "op2", safe: SourceRef.Solid("#00FF00")) });

        Assert.Equal((200, 100, 50), got.One);
        Assert.Equal((0, 255, 0), got.Two);
    }

    [Fact]
    public void PassesRunAtTheirPointsInTheirOrderAndOnlyTheFramesEndInABlackout()
    {
        var said = new List<string>();
        var project = Two("#C86432", "#3C78B4");
        using var late = new CountingPass("late", 20, said);
        using var early = new CountingPass("early", 10, said);
        Render(Scene.Inputs(project) with { Passes = [late, early] });

        Assert.Equal(
            ["early picture op1", "late picture op1", "early final op1", "late final op1",
             "early picture op2", "late picture op2", "early final op2", "late final op2",
             "early end show", "late end show"],
            said);

        said.Clear();
        Render(Scene.Inputs(project) with { Passes = [late, early], Blackout = true });
        Assert.Equal(["early end black", "late end black"], said);
    }
}
