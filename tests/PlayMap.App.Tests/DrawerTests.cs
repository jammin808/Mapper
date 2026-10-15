using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.Core.Media;
using PlayMap.Core.Model;

namespace PlayMap.App.Tests;

/// <summary>The drawers: dragging an output on the plan sets its plan rectangle; the blend-grid helper lays
/// canvas outputs side by side with soft edges where they overlap; the Network drawer's links are the LINK
/// verbs and the pairing token is read where it lives, never from a line; the preview surface shows the
/// engine's picture from its ring.</summary>
public class DrawerTests
{
    [AvaloniaFact]
    public async Task DraggingAnOutputOnThePlanSetsItsPlanRectangle()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.IsOutputsOpen = true;
        DeskHarness.Pump();
        Assert.Contains("DISPLAYS", desk.Link.SentLines);
        Assert.Equal(3, desk.Main.Outputs.Displays.Count);
        var plan = MainWindowTests.Find<PlanCanvas>(window, "Plan");
        var tile = desk.Main.Outputs.Tiles.Single(t => t.Id == "op_3");
        var start = plan.TranslatePoint(new Point(plan.Bounds.Width * 0.5, plan.Bounds.Height * 0.85), window)!.Value;
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        Assert.Same(tile, desk.Main.Outputs.Selected);
        Assert.IsType<OutputInspector>(desk.Main.Outputs.Inspector.Current);
        window.MouseMove(start + new Point(-40, -10), RawInputModifiers.LeftMouseButton);
        window.MouseMove(start + new Point(-80, -20), RawInputModifiers.LeftMouseButton);
        window.MouseUp(start + new Point(-80, -20), MouseButton.Left, RawInputModifiers.None);
        await desk.FlushThrottle();
        var set = desk.Verbs().Where(l => l.StartsWith("SET op_3 plan ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(set);
        var moved = desk.Link.CurrentProject.FindOutput("op_3")!.Plan;
        Assert.NotEqual(960, moved.X);
        Assert.Equal(0, moved.X % 10);
        Screens.Save(window, "outputs-drawer");
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheBlendGridHelperLaysCanvasOutputsSideBySideWithSoftEdges()
    {
        await using var desk = new DeskHarness();
        await desk.Session.SendAsync("SET op_2 mode \"canvas\"", Ct);
        await desk.Session.SendAsync("SET op_1 mode \"canvas\"", Ct);
        DeskHarness.Pump();
        var drawer = desk.Main.Outputs;
        drawer.GridColumns = 2;
        drawer.GridRows = 1;
        drawer.GridOverlap = 240;
        var grid = drawer.PlanGrid();
        Assert.Equal(2, grid.Count);
        Assert.Equal(new EdgeBlend(0, 0, 240, 0), grid[0].Edges);
        Assert.Equal(new EdgeBlend(240, 0, 0, 0), grid[1].Edges);
        Assert.Equal(240 * 1920 / (2 * 1920.0 - 240), grid[0].Source.Right - grid[1].Source.X, 6);
        Assert.Equal(1920, grid[1].Source.Right, 6);
        await drawer.ApplyGrid.Command.ExecuteAsync();
        DeskHarness.Pump();
        var op1 = desk.Link.CurrentProject.FindOutput("op_1")!;
        var op2 = desk.Link.CurrentProject.FindOutput("op_2")!;
        Assert.Equal(new EdgeBlend(0, 0, 240, 0), op1.Blend.Edges);
        Assert.Equal(new EdgeBlend(240, 0, 0, 0), op2.Blend.Edges);
        Assert.False(op2.Blend.Auto3D);
        Assert.Equal(1920 - 240, op2.Plan.X);
        Assert.Equal(PlayMap.App.Link.ToastKind.Done, desk.Session.LastToast!.Kind);
    }

    [AvaloniaFact]
    public async Task TheNetworkDrawerLinksWithTheLinkVerbsAndShowsTheTokenFromWhereItLives()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.IsNetworkOpen = true;
        var net = desk.Main.Network;
        net.PatternsHost = "10.0.0.5:9697";
        net.PatternsToken = "K7QM-3XWD-P9RA";
        await net.LinkPatterns.Command.ExecuteAsync();
        net.StagewatchHost = "10.0.0.6";
        await net.LinkStagewatch.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Contains("LINK PATTERNS 10.0.0.5:9697 K7QM-3XWD-P9RA", desk.Verbs());
        Assert.Contains("LINK STAGEWATCH 10.0.0.6", desk.Verbs());
        Assert.StartsWith("live", net.PatternsState, StringComparison.Ordinal);
        Assert.True(net.HasTimer);
        Assert.Equal(3, net.Peers.Count);

        // The demo node's token is read from the node, not from a line; regenerating it is the verb.
        net.ShowToken.Execute(null);
        var before = net.Token;
        Assert.Equal(before, net.TokenText);
        Assert.DoesNotContain(desk.Link.SentLines, l => l.Contains(before, StringComparison.Ordinal));
        await net.Regenerate.Command.ExecuteAsync();
        Assert.Equal("PAIRING NEW", desk.Verbs()[^1]);
        Assert.NotEqual(before, net.Token);
        Screens.Save(window, "network-drawer");
        window.Close();
    }

    [AvaloniaFact]
    public void ThePreviewSurfaceShowsTheEnginesPictureFromItsRing()
    {
        var path = Path.Combine(PreviewRing.DefaultFolder(), $"playmap-test{Environment.ProcessId}-view.preview");
        var pixels = new byte[64 * 36 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 3] = 255;
        }
        using var source = new RingPreviewSource(path);
        using (var writer = new PreviewRingWriter(path, 64, 36))
        {
            Assert.True(writer.Write(pixels, 64, 36, 64 * 4, 1));
            var frame = source.Poll();
            Assert.NotNull(frame);
            Assert.Equal(new PixelSize(64, 36), frame.PixelSize);
            Assert.Null(source.Poll());
            Assert.True(writer.Write(pixels, 32, 18, 64 * 4, 2));
            Assert.Equal(new PixelSize(32, 18), source.Poll()!.PixelSize);
        }
        // The engine let go of the ring: the desk lets go too, and opens the ring the engine makes next at the path.
        Assert.Null(source.Poll());
        using (var again = new PreviewRingWriter(path, 48, 27))
        {
            Assert.True(again.Write(pixels, 48, 27, 64 * 4, 3));
            var waited = Stopwatch.StartNew();
            var next = source.Poll();
            while (next is null && waited.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(50);
                next = source.Poll();
            }
            Assert.Equal(new PixelSize(48, 27), next?.PixelSize);
        }
        using var gone = new RingPreviewSource(path);
        Assert.Null(gone.Poll());
    }

    [AvaloniaFact]
    public async Task TheConnectChoiceOpensWhenNothingIsConnectedAndTheDemoConnects()
    {
        var session = new PlayMap.App.Link.DeskSession(a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        var vm = new MainViewModel(session);
        await using (vm)
        {
            vm.Start(new DeskOptions(null, null, null));
            var window = new PlayMap.App.Views.MainWindow { DataContext = vm, Width = 1600, Height = 960 };
            window.Show();
            DeskHarness.Pump();
            Assert.True(vm.Connect.IsOpen);
            Assert.False(vm.IsConnected);
            Assert.True(MainWindowTests.Find<Button>(window, "ConnectButton").IsEffectivelyVisible);
            Screens.Save(window, "connect");
            vm.Connect.Choice = PlayMap.App.Link.ConnectChoice.Demo;
            await vm.Connect.ConnectCommand.ExecuteAsync();
            DeskHarness.Pump();
            Assert.False(vm.Connect.IsOpen);
            Assert.True(vm.IsConnected);
            Assert.Equal("Demo node", session.LinkLabel);
            Assert.Equal("Demo show", vm.ProjectName);
            window.Close();
        }
    }
}
