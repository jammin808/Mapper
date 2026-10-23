using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.State;
using PlayMap.App.ViewModels;
using PlayMap.Core.Media;
using PlayMap.Core.Model;

namespace PlayMap.App.Tests;

/// <summary>The drawers: dragging an output on the plan sets its plan rectangle; the blend-grid helper lays
/// canvas outputs side by side with soft edges where they overlap; the Network drawer's links are the LINK
/// verbs and the pairing token is read where it lives, never from a line, and each media transfer says where it stands and its
/// last failed try's words, while PLAY counts only the pulls on their way as copies; the preview surface shows the engine's picture
/// from its ring.</summary>
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
    public async Task TheNetworkDrawerSaysWhereEachTransferStandsAndItsLastTrysWords()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.IsNetworkOpen = true;
        var net = desk.Main.Network;
        // The fake engine's own pull, its row in the engine's keys.
        var missing = desk.Link.CurrentProject.Media.First(m => desk.Link.Availability(m).State == "missing");
        await desk.Session.SendAsync("MEDIA FETCH " + missing.Id, Ct);
        DeskHarness.Pump();
        Assert.Equal($"{missing.Name} from Stage right · 0 % · receiving", Assert.Single(net.Transfers).Text);

        // A pull whose last try met a silent peer: its stage, and the try's words beneath it.
        const string Note = "chunk 1 of 63 from 10.0.0.12:9940: sent nothing for 10 s, 65536 of 8388608 bytes in";
        desk.Link.SetSection("media", """
            {"transfers":[{"media":"md1","name":"Intro","progress":0.42,"from":"Stage left","at":"10.0.0.12:9940","state":"pulling",
              "stage":"receiving","since":1760000000000,"note":"chunk 1 of 63 from 10.0.0.12:9940: sent nothing for 10 s, 65536 of 8388608 bytes in",
              "hash":"5e3f","bytesDone":220200960,"size":524288000}]}
            """);
        DeskHarness.Pump();
        Assert.Equal("Intro from Stage left · 42 % · receiving", Assert.Single(net.Transfers).Text);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "last try: " + Note && t.IsEffectivelyVisible);

        // A pull that failed says its words in place of its progress.
        desk.Link.SetSection("media", """
            {"transfers":[{"media":"md1","name":"Intro","progress":0,"from":"","at":"","state":"failed: Stage left: chunk 3 of 63 from 10.0.0.12:9940 could not be had: answered 503",
              "stage":"","since":1760000005000,"note":"","hash":"5e3f","bytesDone":0,"size":524288000}]}
            """);
        DeskHarness.Pump();
        Assert.Equal("Intro · failed: Stage left: chunk 3 of 63 from 10.0.0.12:9940 could not be had: answered 503", Assert.Single(net.Transfers).Text);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("last try: ", StringComparison.Ordinal) == true && t.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PlayCountsAPullOnItsWayAsACopyAndOneThatFailedAsNone()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var play = desk.Main.Play;
        var missing = desk.Link.CurrentProject.Media.First(m => desk.Link.Availability(m).State == "missing");
        var item = play.Media.Single(m => m.Id == missing.Id);
        string Row(string state, double progress) => $$"""
            {"transfers":[{"media":"{{missing.Id}}","name":"{{missing.Name}}","progress":{{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
              "from":"Stage left","at":"10.0.0.12:9940","state":"{{state}}","stage":"","since":1760000005000,"note":"","hash":"5e3f","bytesDone":0,"size":524288000}]}
            """;

        // On its way: its item is fetching and PLAY counts a copy.
        desk.Link.SetSection("media", Row("pulling", 0.5));
        DeskHarness.Pump();
        Assert.Equal(("fetching", 0.5), (item.Availability, item.Progress));
        Assert.Contains(" · 1 copying", play.MediaSummary, StringComparison.Ordinal);

        // Failed: no copy runs; the item stands where MEDIA LIST says, and PLAY says one could not be copied.
        desk.Link.SetSection("media", Row("failed: Stage left: chunk 3 of 63 from 10.0.0.12:9940 could not be had: answered 503", 0));
        DeskHarness.Pump();
        Assert.Equal("missing", item.Availability);
        Assert.DoesNotContain("copying", play.MediaSummary, StringComparison.Ordinal);
        Assert.EndsWith(" · 1 could not be copied", play.MediaSummary, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void ThePreviewSurfaceShowsTheEnginesPictureFromItsRing()
    {
        var node = $"test{Environment.ProcessId}";
        var path = PreviewRing.PathFor(node, "view", 1);
        if (File.Exists(path))
            File.Delete(path);
        using (var writer = new PreviewRingWriter(path, 64, 36))
        {
            var pixels = new byte[64 * 36 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
                pixels[i + 3] = 255;
            }
            Assert.True(writer.Write(pixels, 64, 36, 64 * 4, 1));
            using var source = new RingPreviewSource(node, "view", () => new PreviewRingName(path, null));
            var frame = source.Poll();
            Assert.NotNull(frame);
            Assert.Equal(new PixelSize(64, 36), frame.PixelSize);
            Assert.Null(source.Poll());
            Assert.True(writer.Write(pixels, 32, 18, 64 * 4, 2));
            Assert.Equal(new PixelSize(32, 18), source.Poll()!.PixelSize);
        }
        using var gone = new RingPreviewSource(node, "view", () => new PreviewRingName(path, null));
        Assert.Null(gone.Poll());
    }

    [AvaloniaFact]
    public void ARingTheEngineTookAwayIsLetGoAndANewRingAtTheNamedPathIsRead()
    {
        var node = $"test{Environment.ProcessId}";
        var path = PreviewRing.PathFor(node, "replaced", 1);
        if (File.Exists(path))
            File.Delete(path);
        var pixels = new byte[64 * 36 * 4];
        using var source = new RingPreviewSource(node, "replaced", () => new PreviewRingName(path, null), TimeSpan.Zero);
        using (var first = new PreviewRingWriter(path, 64, 36))
        {
            Assert.True(first.Write(pixels, 64, 36, 64 * 4, 1));
            Assert.Equal(new PixelSize(64, 36), source.Poll()!.PixelSize);
        }
        // The engine took the view's ring away: the desk lets its mapping go, so nothing holds the file.
        Assert.Null(source.Poll());
        Assert.False(File.Exists(path));
        // A new ring at the path the node still names: the desk reads it.
        using (var second = new PreviewRingWriter(path, 32, 18))
        {
            Assert.True(second.Write(pixels, 32, 18, 32 * 4, 1));
            Assert.Equal(new PixelSize(32, 18), source.Poll()!.PixelSize);
            Assert.Null(source.Poll());
        }
        if (!OperatingSystem.IsWindows())
        {
            // Where a file can be made again at a path a reader still maps, a ring replaced between two polls is noticed too.
            using var third = new PreviewRingWriter(path, 48, 27);
            Assert.True(third.Write(pixels, 48, 27, 48 * 4, 1));
            Assert.Equal(new PixelSize(48, 27), source.Poll()!.PixelSize);
        }
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
