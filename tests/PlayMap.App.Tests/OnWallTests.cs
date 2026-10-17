using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.Core.Model;
using PlayMap.Engine;

namespace PlayMap.App.Tests;

/// <summary>On-wall editing: O sends OVERLAY ON ON &lt;the projector's output&gt; (the line a real engine takes), STATE's air.overlay
/// checks the toggle, this desk renews its lease every 5 s (OVERLAY KEEP), O again sends OVERLAY OFF ON &lt;output&gt;, and leaving MAP
/// takes this desk's overlay off. Armed, O is disabled with the node's own words — a real engine's refusal of the same line — and a
/// press sends nothing.</summary>
public class OnWallTests
{
    [AvaloniaFact]
    public async Task OSendsOverlayOnOnTheProjectorsOutputItsLeaseIsKeptAndLeavingMapTakesItOff()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        var map = desk.Main.Map;
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        var button = MainWindowTests.Find<Button>(window, "OnWallButton");
        Assert.True(button.IsEffectivelyEnabled);
        Assert.False(map.OnWall.IsChecked);
        desk.Link.ClearSent();

        QuadToolTests.Key(window, Key.O, physical: PhysicalKey.O);
        await map.OnWall.Command.Last;
        DeskHarness.Pump();
        Assert.Equal("OVERLAY ON ON op_1", Assert.Single(desk.Verbs()));
        Assert.True(map.Overlay.On);
        Assert.True(map.OnWall.IsChecked);
        Assert.Equal("OVERLAY OFF ON op_1", map.OnWall.Verb);
        await using (var engine = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), desk.Link.CurrentProject))
        {
            var on = await QuadToolTests.Run(engine, "OVERLAY ON ON op_1");
            Assert.True(on.Ok, on.Payload);
            Assert.True((await QuadToolTests.Run(engine, "OVERLAY KEEP ON op_1")).Ok);
            Assert.True((await QuadToolTests.Run(engine, "OVERLAY OFF ON op_1")).Ok);
        }

        // O again: off; the toggle follows STATE.
        desk.Link.ClearSent();
        QuadToolTests.Key(window, Key.O, physical: PhysicalKey.O);
        await map.OnWall.Command.Last;
        DeskHarness.Pump();
        Assert.Equal("OVERLAY OFF ON op_1", Assert.Single(desk.Verbs()));
        Assert.False(map.OnWall.IsChecked);

        // On again, then the desk leaves MAP: this desk's overlay comes off the wall.
        await map.OnWall.Command.ExecuteAsync();
        DeskHarness.Pump();
        desk.Link.ClearSent();
        desk.Main.Workspace = Workspace.Play;
        DeskHarness.Pump();
        Assert.Equal("OVERLAY OFF ON op_1", Assert.Single(desk.Verbs()));
        Assert.False(map.Overlay.On);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ThisDesksOverlayLeaseIsRenewedEveryFiveSecondsUntilItIsOff()
    {
        // No window: its own 4 Hz timer would tick too; the test is the clock here.
        await using var desk = new DeskHarness();
        desk.Main.Workspace = Workspace.Map;
        var map = desk.Main.Map;
        map.Select("projector", "pj_left");
        await map.OnWall.Command.ExecuteAsync();
        DeskHarness.Pump();
        desk.Link.ClearSent();
        string[] Overlay() => [.. desk.Link.SentLines.Where(l => l.StartsWith("OVERLAY", StringComparison.Ordinal))];
        for (var i = 0; i < MapViewModel.KeepOverlayEveryTicks - 1; i++)
            desk.Main.Tick();
        Assert.Empty(Overlay());
        desk.Main.Tick();
        Assert.Equal(["OVERLAY KEEP ON op_1"], Overlay());
        // Off: no lease to renew.
        await map.OnWall.Command.ExecuteAsync();
        DeskHarness.Pump();
        desk.Link.ClearSent();
        for (var i = 0; i < MapViewModel.KeepOverlayEveryTicks * 2; i++)
            desk.Main.Tick();
        Assert.Empty(Overlay());
    }

    [AvaloniaFact]
    public async Task ArmedTheOToggleIsDisabledWithTheNodesWordsAndAPressSendsNothing()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        var map = desk.Main.Map;
        map.Select("projector", "pj_left");
        DeskHarness.Pump();
        Assert.True(desk.Main.ShowModule.Strip.IsArmed);

        // The words a real engine, armed, refuses the same line with.
        string words;
        await using (var engine = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), desk.Link.CurrentProject))
        {
            Assert.True((await QuadToolTests.Run(engine, "SHOW ARM")).Ok);
            var refused = await QuadToolTests.Run(engine, "OVERLAY ON ON op_1");
            Assert.False(refused.Ok);
            words = refused.Payload!;
        }
        Assert.Equal("OVERLAY ON ON op_1", map.OnWall.Verb);
        Assert.False(map.OnWall.IsEnabled);
        Assert.Equal(words, map.OnWall.Refusal);
        var button = MainWindowTests.Find<Button>(window, "OnWallButton");
        Assert.False(button.IsEffectivelyEnabled);
        Assert.Contains(words, ToolTip.GetTip(button) as string, StringComparison.Ordinal);
        Assert.True(button.GetVisualDescendants().OfType<PathIcon>().Last().IsVisible);

        desk.Link.ClearSent();
        QuadToolTests.Key(window, Key.O, physical: PhysicalKey.O);
        Assert.Empty(desk.Verbs());

        // Disarmed, the toggle is live again.
        await desk.Session.SendAsync("SHOW DISARM", Ct);
        DeskHarness.Pump();
        Assert.True(map.OnWall.IsEnabled);
        Assert.Null(map.OnWall.Refusal);
        window.Close();
    }
}

/// <summary>The first-light guide on a show with no projector: step 1 lists the machine's displays (DISPLAYS) and its button sends
/// LIGHT WALL &lt;display&gt;, after which MAP shows the lit projector with the quad tool armed; four clicks and Enter send one SURFACE
/// QUAD (step 2); step 3's clip sends SURFACE SOURCE &lt;the quad&gt; media:&lt;id&gt;, and the guide closes once the first content is up.
/// On a show that already has projectors and no first light it does not show.</summary>
public class FirstLightTests
{
    [AvaloniaFact]
    public async Task LightAWallThenTheQuadThenAClip()
    {
        var empty = Link.DemoProject.Create();
        empty = empty.WithStage(st => st with { Projectors = [], Surfaces = [] }) with { Outputs = [] };
        await using var desk = new DeskHarness(project: empty);
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        DeskHarness.Pump();
        var guide = desk.Main.Map.Guide;
        Assert.True(guide.IsVisible);
        Assert.Equal(1, guide.Step);
        Assert.Equal(["Light Projector A", "Light Projector B", "Light Desk monitor (this screen)"], guide.Lights.Select(a => a.Label).ToArray());
        Assert.Equal(["LIGHT WALL DISPLAY2", "LIGHT WALL DISPLAY3", "LIGHT WALL DISPLAY1"], guide.Lights.Select(a => a.Verb).ToArray());
        Assert.All(guide.Lights, a => Assert.Null(DeskVerbRuleTests.Fault(a.Verb)));
        Assert.True(MainWindowTests.Find<Border>(window, "Guide").IsEffectivelyVisible);

        // Step 1: the button on screen sends LIGHT WALL; MAP then shows the lit projector, the quad tool armed.
        desk.Link.ClearSent();
        var light = window.GetVisualDescendants().OfType<ActionMenuButton>().Single(b => ReferenceEquals(b.Action, guide.Lights[0]));
        DeskHarness.Click(window, light);
        await guide.Lighting;
        DeskHarness.Pump();
        Assert.Equal("LIGHT WALL DISPLAY2", Assert.Single(desk.Verbs()));
        var pj = Assert.Single(desk.Link.CurrentProject.Stage.Projectors);
        var op = Assert.Single(desk.Link.CurrentProject.Outputs);
        Assert.Equal((OutputMode.Projector, pj.Id, "DISPLAY2"), (op.Mode, op.ProjectorId, op.DisplayKey));
        Assert.Equal(Workspace.Map, desk.Main.Workspace);
        var map = desk.Main.Map;
        Assert.Equal((MapTool.Quad, true, pj.Id), (map.Tool, map.IsProjectorView, map.ViewProjector!.Id));
        Assert.Equal(2, guide.Step);
        Assert.True(map.Overlay.On);

        // Step 2: four clicks on the wall and Enter — one SURFACE QUAD on the lit projector.
        var canvas = MainWindowTests.Find<ProjectorCanvas>(window, "ProjectorView");
        desk.Link.ClearSent();
        foreach (var (x, y) in new[] { (0.1, 0.1), (0.9, 0.1), (0.9, 0.9), (0.1, 0.9) })
            QuadToolTests.ClickWall(window, canvas, x, y);
        QuadToolTests.Key(window, Avalonia.Input.Key.Enter, physical: PhysicalKey.Enter);
        await map.LastQuad;
        DeskHarness.Pump();
        Assert.Equal($"SURFACE QUAD {pj.Id} 192 108 1728 108 1728 972 192 972 PX", Assert.Single(desk.Verbs(), l => l.StartsWith("SURFACE QUAD", StringComparison.Ordinal)));
        Assert.Equal(3, guide.Step);
        var quad = desk.Link.CurrentProject.Stage.Surfaces.Single(s => s.Pixel is not null);
        Assert.Equal(quad.Id, guide.QuadId);

        // Step 3: a clip from the show's media is the quad's source; the first map is made and the guide goes.
        var media = desk.Link.CurrentProject.Media[0];
        var clip = guide.Clip.Children.Single(c => c.Label == "Clip · " + media.Name);
        desk.Link.ClearSent();
        await clip.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal($"SURFACE SOURCE {quad.Id} media:{media.Id}", Assert.Single(desk.Verbs()));
        Assert.Null(DeskVerbRuleTests.Fault(desk.Verbs()[0]));
        Assert.False(guide.IsVisible);
        Assert.False(MainWindowTests.Find<Border>(window, "Guide").IsEffectivelyVisible);
        Assert.Equal("content", desk.Session.State.Root.GetProperty("onboarding").GetProperty("stage").GetString());
        window.Close();
    }

    [AvaloniaFact]
    public async Task AShowWithProjectorsAndNoFirstLightShowsNoGuideAndTheCrossClosesIt()
    {
        await using (var desk = new DeskHarness())
        {
            desk.Main.Workspace = Workspace.Map;
            DeskHarness.Pump();
            Assert.Equal("none", desk.Session.State.Root.GetProperty("onboarding").GetProperty("stage").GetString());
            Assert.False(desk.Main.Map.Guide.IsVisible);
        }
        var empty = Link.DemoProject.Create().WithStage(st => st with { Projectors = [], Surfaces = [] }) with { Outputs = [] };
        await using var bare = new DeskHarness(project: empty);
        Assert.True(bare.Main.Map.Guide.IsVisible);
        bare.Main.Map.Guide.Dismiss.Execute(null);
        Assert.False(bare.Main.Map.Guide.IsVisible);
    }
}
