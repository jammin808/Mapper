using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.App.ViewModels.Health;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;
using PlayMap.Engine;

namespace PlayMap.App.Tests;

/// <summary>Health ▸ Preflight: the board reads the report STATE.show.preflight names (PREFLIGHT REPORT &lt;id&gt;) while it is shown,
/// lays a section per check with a status row per row, and every row's Fix sends that row's own fix line — a real engine's report
/// of the desk's own show; Run sends PREFLIGHT QUICK. The Ready light is the worst of the show's preflight, the fleet and the group's
/// vblank picture, each in the node's words.</summary>
public class PreflightBoardTests(ITestOutputHelper output)
{
    private static readonly SessionInfo DeskOnNode = new("demo", "desk", "127.0.0.1", true, Link.FakeEngineLink.ClientName, true);

    /// <summary>A real engine's preflight of the project, finished (PREFLIGHT WAIT answers the report).</summary>
    private static async Task<PreflightReport> RealReport(PlayMap.Core.Model.Project project)
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), project);
        Assert.True(WireGrammar.TryParse("PREFLIGHT WAIT", out var line, out _));
        var reply = await engine.HandleAsync(line, DeskOnNode, Ct);
        Assert.True(reply.Ok, reply.Payload);
        return PreflightReports.Read(reply.Payload!) ?? throw new InvalidOperationException("not a report: " + reply.Payload);
    }

    [AvaloniaFact]
    public async Task EveryFixSendsItsRowsFixLineFromARealEnginesReport()
    {
        await using var desk = new DeskHarness();
        var report = await RealReport(desk.Link.CurrentProject);
        var fixes = report.Rows.Where(r => r.Fix is not null).ToList();
        Assert.True(fixes.Count >= 2, "the engine's report of the demo show has rows with fix lines");
        desk.Link.PutPreflightReport(report);
        var window = MainWindowTests.Open(desk);
        var board = desk.Main.Health.Preflight;
        // Hidden, the board reads nothing.
        Assert.DoesNotContain(desk.Link.SentLines, l => l.StartsWith("PREFLIGHT REPORT", StringComparison.Ordinal));
        desk.Main.Drawers.Show("health", "preflight");
        DeskHarness.Pump();
        await board.Fetching;
        DeskHarness.Pump();
        Assert.Equal("PREFLIGHT REPORT " + report.Id, Assert.Single(desk.Link.SentLines, l => l.StartsWith("PREFLIGHT REPORT", StringComparison.Ordinal)));
        Assert.Equal("show-ready " + PreflightReports.ReadyWords(report.Ready) + (report.Ready.FirstOpen.Length > 0 ? " · " + report.Ready.FirstOpen : ""), board.Header);

        // A section per check in the report's order, a row per row, each with its light, item, value and note.
        var ordered = report.Rows.GroupBy(r => r.Section).SelectMany(g => g).ToList();
        var rows = board.Sections.SelectMany(s => s.Rows).ToList();
        Assert.Equal(ordered.Count, rows.Count);
        Assert.Equal(report.Rows.Select(r => r.Section).Distinct().ToArray(), board.Sections.Select(s => s.Title).ToArray());
        for (var i = 0; i < rows.Count; i++)
        {
            var r = ordered[i];
            Assert.Equal((PlayMap.Core.Bus.ShowContext.LightWord(r.Light), r.Item, r.Value, r.Note), (rows[i].Light, rows[i].Title, rows[i].Value, rows[i].Words));
            Assert.Equal(r.Fix is not null, rows[i].HasFix);
        }

        // Each Fix on screen, pressed with the mouse, sends exactly its row's line (a catalogue line); the show-arming fix last, as it
        // greys the others.
        var view = window.GetVisualDescendants().OfType<Views.Health.PreflightBoardView>().Single();
        var pressed = 0;
        foreach (var (row, status) in ordered.Zip(rows).Where(x => x.First.Fix is not null).OrderBy(x => x.First.Fix == "SHOW ARM" ? 1 : 0))
        {
            output.WriteLine($"{row.Section} · {row.Item}: {row.Fix}");
            Assert.Equal(row.Fix, status.Fix!.Verb);
            Assert.Null(DeskVerbRuleTests.Fault(row.Fix!));
            var button = view.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, status.Fix));
            Assert.True(button.IsEffectivelyEnabled, row.Fix + ": " + status.Fix.Refusal);
            desk.Link.ClearSent();
            DeskHarness.Click(window, button);
            await status.Fix.Command.Last;
            DeskHarness.Pump();
            Assert.Equal(row.Fix, Assert.Single(desk.Verbs()));
            pressed++;
        }
        Assert.Equal(fixes.Count, pressed);
        Screens.Save(window, "health-preflight");
        window.Close();
    }

    [AvaloniaFact]
    public async Task RunSendsPreflightQuickAndTheBoardReadsEachNewReportOnceWhileShown()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        var board = desk.Main.Health.Preflight;
        Assert.Equal("No preflight has run on this node yet.", board.Header);
        Assert.Empty(board.Sections);

        // Run while hidden: the node runs it, STATE names the report, the board does not read it yet.
        await board.Run.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal("PREFLIGHT QUICK", Assert.Single(desk.Verbs()));
        var first = desk.Session.State.Root.GetProperty("show").GetProperty("preflight").GetProperty("id").GetString()!;
        Assert.DoesNotContain(desk.Link.SentLines, l => l.StartsWith("PREFLIGHT REPORT", StringComparison.Ordinal));

        // Shown: read once; a push that names the same report reads nothing more.
        desk.Main.Drawers.Show("health", "preflight");
        DeskHarness.Pump();
        await board.Fetching;
        DeskHarness.Pump();
        Assert.Equal(["PREFLIGHT QUICK", "PREFLIGHT REPORT " + first], desk.Verbs());
        Assert.NotEmpty(board.Sections);
        await desk.Session.SendAsync("BLACKOUT ON", Ct);
        DeskHarness.Pump();
        Assert.Single(desk.Link.SentLines, l => l == "PREFLIGHT REPORT " + first);

        // A new report while shown is read at once; the demo's rows carry the engine's fix lines (the media it lacks, the show's rules).
        desk.Link.ClearSent();
        await board.Run.Command.ExecuteAsync();
        DeskHarness.Pump();
        await board.Fetching;
        DeskHarness.Pump();
        var second = desk.Session.State.Root.GetProperty("show").GetProperty("preflight").GetProperty("id").GetString()!;
        Assert.NotEqual(first, second);
        Assert.Equal(["PREFLIGHT QUICK", "PREFLIGHT REPORT " + second], desk.Verbs());
        Assert.Equal(second, board.Report!.Id);
        Assert.All(board.Fixes, f => Assert.Null(DeskVerbRuleTests.Fault(f.Verb)));
        Assert.Contains(board.Fixes, f => f.Verb == "SHOW ARM");
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheReadyLightIsTheWorstOfTheShowsPreflightTheFleetAndTheGroupsVblanks()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        var window = MainWindowTests.Open(desk);
        var strip = desk.Main.ShowModule.Strip;
        var light = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ReadyButton").GetVisualDescendants().OfType<Ellipse>().Single();
        void Is(string expected)
        {
            DeskHarness.Pump();
            Assert.Equal(expected, strip.ReadyLight);
            Assert.Equal(LightBrushConverter.Brushes.For(expected), light.Fill);
        }

        // show.json's preflight is red: the light is red, in its words.
        Is("red");
        Assert.Equal(["show"], strip.Readiness.Parts.Select(p => p.Name).ToArray());
        Assert.Contains("show: red — outputs: Output 1 — this machine has no display for it", strip.ReadyTip, StringComparison.Ordinal);

        // A green preflight with fleet.json's red fleet (a machine gone): the fleet makes it red, with the fleet's headline.
        desk.Link.SetSection("show", """{"preflight":{"light":"green","firstOpen":"","ready":"5/5"}}""");
        Is("green");
        StateFixtures.Load(desk.Link, "fleet");
        Is("red");
        var fleet = Assert.Single(strip.Readiness.Parts, p => p.Name == "fleet");
        Assert.Equal(("red", "3 machines · 3 outputs · 1 red · 2 amber — FOH-C: gone quiet 7 s"), (fleet.Light, fleet.Words));

        // The fleet green again; sync.json's group (free-running, nothing slips soon): green.
        desk.Link.SetSection("fleet", """{"light":"green"}""");
        StateFixtures.Load(desk.Link, "sync");
        Is("green");
        var syncWords = desk.Session.State.Root.GetProperty("sync").GetProperty("words").GetString();
        Assert.Equal(("green", syncWords), (strip.Readiness.Parts.Single(p => p.Name == "sync").Light, strip.Readiness.Parts.Single(p => p.Name == "sync").Words));

        // An output of the group that fell out of the lock is red (the sync board's red).
        desk.Link.SetSection("sync", """{"outputs":[{"node":"0badc0de","output":"op_1","state":"fell-out"}],"words":"2 nodes, 2 outputs: op_1 fell out of the lock"}""");
        Is("red");
        Assert.Contains("sync: red — 2 nodes, 2 outputs: op_1 fell out of the lock", strip.ReadyTip, StringComparison.Ordinal);
        // Mixed rates are amber.
        desk.Link.SetSection("sync", """{"outputs":[{"node":"0badc0de","output":"op_1","state":"free-measured"}],"verdict":"mixed rate"}""");
        Is("amber");
        // A preflight running is not a light yet; the others still are.
        desk.Link.SetSection("show", """{"preflight":{"running":true,"light":"red"}}""");
        Is("amber");
        Assert.Equal("Checking…", strip.ReadyText);
        window.Close();
    }

    [Fact]
    public void ABoardWithNoPartsIsGrey()
    {
        Assert.Equal("grey", Readiness.Of(PlayMap.App.State.StateSnapshot.Empty).Light);
        Assert.Equal(3, Readiness.Rank("red"));
        Assert.Equal(0, Readiness.Rank("purple"));
    }
}
