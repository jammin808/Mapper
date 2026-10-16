using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.Infrastructure;
using PlayMap.App.State;
using PlayMap.App.ViewModels;
using PlayMap.App.ViewModels.Show;

namespace PlayMap.App.Tests;

/// <summary>The desk spine's seams: modules compose through the partial hooks and bring their views through the module
/// view locator; a STATE section is read once per snapshot; banners, badges and inspector sections follow STATE; the gate
/// greys a control with its words; the four drawers hold tabs; the Esc ladder keeps its order and its 600 ms guard; the
/// preferences persist; the top bar fits at every width the desk supports.</summary>
public class DeskSpineTests(ITestOutputHelper output)
{
    private sealed class ProbeView : Border
    {
        public ProbeView() => Tag = "probe";
    }

    private sealed record ProbeSection(string Target);

    /// <summary>A module a test adds: a badge and a section for one object, a gate on one verb.</summary>
    private sealed class ProbeModule(string target) : DeskModule
    {
        public bool Badged { get; set; } = true;

        public override IEnumerable<ObjectBadge> Badges(IMenuTarget t) =>
            Badged && t.Id == target ? [new ObjectBadge("probe", "lock", "probe", "amber", "a probe badge")] : [];

        public override IEnumerable<object> InspectorSections(IMenuTarget t) => t.Id == target ? [new ProbeSection(target)] : [];

        public override Link.GateRefusal? Gate(PlayMap.Core.Wire.WireLine line, PlayMap.Core.Wire.VerbSpec spec) =>
            spec.Name == "WARP RESET" ? new Link.GateRefusal("Not while probed: WARP RESET is held by the probe.", "probed") : null;
    }

    [AvaloniaFact]
    public async Task ModulesComposeThroughTheHooksAndTheirViewsComeFromTheLocator()
    {
        await using var desk = new DeskHarness();
        Assert.Contains(desk.Main.Modules, m => m is ShowStripModule);
        Assert.IsType<ShowStripViewModel>(desk.Main.ShowSlot);
        var window = MainWindowTests.Open(desk);
        var slot = MainWindowTests.Find<ContentControl>(window, "ShowSlot");
        Assert.Contains(slot.GetVisualDescendants(), v => v is Views.Show.ShowStripView);

        ModuleViews.Register<ProbeSection>(() => new ProbeView());
        var locator = new ModuleViewLocator();
        Assert.True(locator.Match(new ProbeSection("x")));
        Assert.IsType<ProbeView>(locator.Build(new ProbeSection("x")));
        Assert.False(locator.Match("not registered"));
        window.Close();
    }

    [Fact]
    public void ASectionIsReadOncePerSnapshot()
    {
        var s = StateSnapshot.Parse("{\"seq\":1,\"show\":{\"armed\":true,\"by\":\"Desk\"}}");
        var reads = 0;
        ShowSection Read(System.Text.Json.JsonElement? e)
        {
            reads++;
            return ShowSection.Read(e);
        }
        Assert.True(s.Section("show", Read).Armed);
        Assert.True(s.Section("show", Read).Armed);
        Assert.Equal(1, reads);
        Assert.False(StateSnapshot.Parse("{\"seq\":2}").Section("show", ShowSection.Read).Present);
    }

    [AvaloniaFact]
    public async Task BadgesAndInspectorSectionsRenderAndFollowTheModules()
    {
        await using var desk = new DeskHarness();
        var probe = new ProbeModule("op_1");
        desk.Main.Modules.Add(probe);
        ModuleViews.Register<ProbeSection>(() => new ProbeView { Name = "ProbeView" });
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Map;
        desk.Main.Map.Select("output", "op_1");
        await desk.Link.SendAsync("STATE", Ct);
        desk.Link.SetSection("probe", "{}");
        DeskHarness.Pump();

        var strip = window.GetVisualDescendants().OfType<BadgeStrip>().First(b => b.DataContext is OutlinerNode { Id: "op_1" });
        Assert.Equal("probe", Assert.Single(strip.Shown).Id);
        Assert.Contains(window.GetVisualDescendants(), v => v is ProbeView);
        Assert.Equal("op_1", Assert.IsType<ProbeSection>(Assert.Single(desk.Main.Map.Inspector.Sections)).Target);

        // The badge goes when its cause goes: the next push rebuilds it.
        probe.Badged = false;
        desk.Link.SetSection("probe", "{\"moved\":true}");
        DeskHarness.Pump();
        Assert.Empty(strip.Shown);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheGateGreysAControlWithItsWordsBeforeThePress()
    {
        await using var desk = new DeskHarness();
        desk.Main.Modules.Add(new ProbeModule("op_1"));
        // A module that joins the gate moves its facts.
        desk.Session.GateMoved();
        desk.Main.Map.Select("output", "op_1");
        desk.Link.SetSection("probe", "{}");
        DeskHarness.Pump();
        var node = desk.Main.Map.Outliner.SelectMany(g => g.Nodes).First(n => n.Id == "op_1");
        var reset = node.Actions.First(a => a.Verb == "WARP RESET op_1");
        Assert.False(reset.IsEnabled);
        Assert.Equal("Not while probed: WARP RESET is held by the probe.", reset.Refusal);
        Assert.Contains("Not while probed", reset.Tip, StringComparison.Ordinal);
        Assert.EndsWith(" · probed", reset.MenuLabel, StringComparison.Ordinal);
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, node.Actions);
        var item = menu.Items.OfType<MenuItem>().First(i => i.Tag == reset);
        Assert.False(item.IsEnabled);
        // The identify line is not gated: it stays live.
        Assert.True(node.Actions.First(a => a.Verb == "OUTPUT IDENTIFY op_1").IsEnabled);
    }

    [AvaloniaFact]
    public async Task TheFourDrawersHoldTabsAndOnlyOneIsOpen()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var drawers = desk.Main.Drawers;
        Assert.Equal(["outputs", "inputs", "health", "network"], drawers.Drawers.Select(d => d.Key).ToArray());
        Assert.Equal("plan", drawers.Outputs.Tabs[0].Key);
        Assert.Equal("links", drawers.Network.Tabs[0].Key);

        desk.Main.IsOutputsOpen = true;
        DeskHarness.Pump();
        Assert.Contains("DISPLAYS", desk.Link.SentLines);
        Assert.Contains(window.GetVisualDescendants(), v => v is Views.OutputsDrawer);
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "HealthPill"));
        Assert.True(desk.Main.IsHealthOpen);
        Assert.False(desk.Main.IsOutputsOpen);
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "WideButton"));
        Assert.True(drawers.IsWide);
        Assert.True(double.IsNaN(drawers.Width));
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "CloseDrawer"));
        Assert.False(drawers.IsOpen);
        window.Close();
    }

    [Fact]
    public void TheEscLadderTakesTheFirstRungThatUsesTheKeyAndGuardsPanic()
    {
        var now = 1_000L;
        var ladder = new EscapeLadder(() => now);
        var used = new List<string>();
        var drawerOpen = true;
        var learning = false;
        ladder.Add(new EscapeRung(EscapeLadder.Panic, "panic", () =>
        {
            used.Add("panic");
            return true;
        }));
        ladder.Add(new EscapeRung(EscapeLadder.Overlays, "overlays", () =>
        {
            if (!drawerOpen)
                return false;
            drawerOpen = false;
            return true;
        }));
        ladder.Add(new EscapeRung(EscapeLadder.Learn, "learn", () =>
        {
            if (!learning)
                return false;
            learning = false;
            return true;
        }));
        Assert.Equal(["learn", "overlays", "panic"], ladder.Rungs.Select(r => r.Name).ToArray());

        learning = true;
        Assert.Equal("learn", ladder.Escape());
        Assert.Equal("overlays", ladder.Escape());
        // Two quick Escs that closed things never reach PANIC.
        now += EscapeLadder.GuardMs - 1;
        Assert.Null(ladder.Escape());
        Assert.Empty(used);
        now += 1;
        Assert.Equal("panic", ladder.Escape());
        Assert.Equal(["panic"], used);
    }

    [AvaloniaFact]
    public void AHoldButtonActsOnlyWhenHeldItsTimeAndADropGoesToTheHandlerOfItsFormat()
    {
        var runs = 0;
        var hold = new HoldButton { HoldMs = 600, Command = new RelayCommand(() => runs++) };
        var window = new Window { Content = hold, Width = 200, Height = 100 };
        window.Show();
        DeskHarness.Pump();
        var at = hold.TranslatePoint(new Point(hold.Bounds.Width / 2, hold.Bounds.Height / 2), window)!.Value;
        // A short press sends nothing; a press held to its time runs the command once.
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        Assert.False(hold.Complete(599));
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        Assert.False(hold.Complete(600));
        Assert.Equal(0, runs);
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        DeskHarness.Pump();
        Assert.True(hold.IsHeld);
        Assert.True(hold.Complete(600));
        Assert.False(hold.Complete(700));
        Assert.Equal(1, runs);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        window.Close();

        var dropped = new List<(object Data, object? Target)>();
        DeskDrop.Register(new DropHandler("playmap.test-drop", (data, target) =>
        {
            dropped.Add((data, target));
            return target is not null;
        }));
        Assert.True(DeskDrop.Offer("playmap.test-drop", "in_arena", "sf_1"));
        Assert.False(DeskDrop.Offer("playmap.test-drop", "in_arena", null));
        Assert.False(DeskDrop.Offer("playmap.other", "in_arena", "sf_1"));
        Assert.Equal([("in_arena", (object?)"sf_1"), ("in_arena", null)], dropped);
        Assert.True(DeskDrop.Unregister("playmap.test-drop"));
        Assert.False(DeskDrop.Offer("playmap.test-drop", "in_arena", "sf_1"));

        var ring = new ScoreRing { Value = 75, Maximum = 100 };
        Assert.Equal(0.75, ring.Fraction);
        ring.Value = 250;
        Assert.Equal(1, ring.Fraction);
    }

    [Fact]
    public void ThePreferencesPersistInDeskJson()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-desk-");
        try
        {
            var path = Path.Combine(dir.FullName, "desk.json");
            var prefs = DeskPrefs.Load(path);
            Assert.True(prefs.IsSimple);
            Assert.False(prefs.EscIsPanic);
            prefs.IsPro = true;
            prefs.EscIsPanic = true;
            var again = DeskPrefs.Load(path);
            Assert.True(again.IsPro);
            Assert.True(again.EscIsPanic);
            File.WriteAllText(path, "not json");
            Assert.True(DeskPrefs.Load(path).IsSimple);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData(1100)]
    [InlineData(1280)]
    [InlineData(1366)]
    [InlineData(1600)]
    [InlineData(1920)]
    public async Task TheTopBarFitsAtEveryWidth(int width)
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        var window = MainWindowTests.Open(desk, width, 900);
        DeskHarness.Pump();
        var bar = MainWindowTests.Find<TopBarPanel>(window, "TopBarPanel");
        if (width < TopBarPanel.TwoRowBelow)
            Assert.True(bar.IsTwoRow);
        var right = window.Bounds.Width;
        foreach (var child in bar.Children.Where(c => c.IsVisible))
            output.WriteLine($"{width}: {child.Name} {child.DesiredSize.Width:0} wanted, {child.Bounds.Width:0} given, at {child.Bounds.X:0},{child.Bounds.Y:0} (two rows: {bar.IsTwoRow})");
        foreach (var child in bar.Children.Where(c => c.IsVisible))
        {
            var r = child.TranslatePoint(new Point(child.Bounds.Width, 0), window)?.X ?? double.NaN;
            Assert.True(r <= right + 0.5, $"{child.Name} ends at {r:0} px, beyond the {right:0} px window");
            Assert.True(child.Bounds.Width + child.Margin.Left + child.Margin.Right + 0.5 >= child.DesiredSize.Width || child.Name == "MasterTransport",
                $"{child.Name} was squeezed to {child.Bounds.Width:0} of {child.DesiredSize.Width:0} px");
        }
        // One row only when everything fits on it; the panel wraps otherwise.
        if (!bar.IsTwoRow)
            Assert.True(bar.Children.Where(c => c.IsVisible).Sum(c => c.DesiredSize.Width) <= right);
        // The transport is never squeezed below its buttons: every one is whole on screen.
        var transport = MainWindowTests.Find<Border>(window, "MasterTransport");
        foreach (var b in MainWindowTests.Find<ItemsControl>(window, "MasterButtons").GetVisualDescendants().OfType<Button>())
        {
            var left = b.TranslatePoint(new Point(0, 0), transport)!.Value.X;
            Assert.True(left >= 0 && left + b.Bounds.Width <= transport.Bounds.Width + 0.5, $"a transport button is cut at {width} px");
        }
        // The show cluster never shrinks, and it is all on screen.
        var cluster = MainWindowTests.Find<StackPanel>(window, "ShowCluster");
        Assert.True(cluster.Bounds.Width + 0.5 >= cluster.DesiredSize.Width);
        Assert.True(MainWindowTests.Find<Button>(window, "PanicButton").IsEffectivelyVisible);
        Screens.Save(window, "top-bar-" + width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        window.Close();
    }

    [AvaloniaFact]
    public async Task BannersComeAndGoWithStateAndFoldBeyondTwo()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        Assert.Empty(desk.Main.Banners);
        var extra = new BannerModule();
        desk.Main.Modules.Add(extra);
        extra.Count = 3;
        desk.Link.SetSection("probe", "{\"n\":3}");
        DeskHarness.Pump();
        Assert.Equal(3, desk.Main.Banners.Count);
        Assert.Equal(2, desk.Main.ShownBanners.Count);
        Assert.Equal("+1 more", desk.Main.MoreBannersText);
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "MoreBanners"));
        Assert.Equal(3, desk.Main.ShownBanners.Count);
        extra.Count = 0;
        desk.Link.SetSection("probe", "{\"n\":0}");
        DeskHarness.Pump();
        Assert.Empty(desk.Main.Banners);
        Assert.Empty(desk.Main.ShownBanners);
        window.Close();
    }

    private sealed class BannerModule : DeskModule
    {
        public int Count { get; set; }

        public override IEnumerable<Banner> Banners(StateSnapshot state) =>
            Enumerable.Range(0, Count).Select(i => new Banner("b" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "amber", "banner", ImmutableArray<DeskAction>.Empty, i));
    }
}
