using PlayMap.App.ViewModels;
using PlayMap.Core.Fleet;
using PlayMap.Core.Wire;
using Avalonia.Headless.XUnit;

namespace PlayMap.App.Tests;

/// <summary>The desk on a hot backup or a fenced node (tests/fixtures/state/backup.json, written from two real nodes): every line the
/// node's door would refuse a client — an edit, a GO — greys with the node's own words from STATE.fleet.door, which the fixture's
/// test shows are the words a real node refuses with; queries, PANIC, Show Mode and the fleet's verbs (FAILOVER) stay live; a banner
/// says whose show it is; the demo engine refuses the same lines with the same words; the backup's cards read the scenario.</summary>
public class BackupDeskTests
{
    private static string Door(string scenario) =>
        StateFixtures.Scenarios("backup").Single(s => s.Name == scenario).Sections["fleet"]!["door"]!.GetValue<string>();

    [AvaloniaFact]
    public async Task OnAHotBackupEditsGreyWithTheNodesWordsAndQueriesPanicAndFailoverStayLive()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "backup", "backup");
        var window = MainWindowTests.Open(desk);
        DeskHarness.Pump();
        var door = Door("backup");
        Assert.StartsWith("FOH-B mirrors FOH-A — send it to FOH-A", door, StringComparison.Ordinal);

        // A surface's edits and an output's warp grey with the node's words, before the press.
        desk.Main.Workspace = Workspace.Map;
        var sf = desk.Main.Map.Outliner.SelectMany(g => g.Nodes).First(n => n.Kind == "surface");
        desk.Main.Map.Select(sf.Kind, sf.Id);
        DeskHarness.Pump();
        var remove = sf.Actions.Single(a => a.Id == "remove");
        Assert.False(remove.IsEnabled);
        Assert.Equal(door, remove.Refusal);
        var source = sf.Actions.Single(a => a.Id == "content").Children.First(c => c.Verb.StartsWith("SURFACE SOURCE", StringComparison.Ordinal));
        Assert.Equal(door, source.Refusal);
        var op = desk.Main.Map.Outliner.SelectMany(g => g.Nodes).First(n => n.Kind == "output");
        Assert.Equal(door, op.Actions.Single(a => a.Id == "warpReset").Refusal);
        // The transports are the primary's: a GO on the backup is refused too.
        var gate = desk.Session.GateWords;
        Assert.Equal(door, gate("PLAYLIST GO " + desk.Link.CurrentProject.Playlists[0].Id));

        // Each greyed line is one Core's BackupGate refuses the desk on a backup with the node's words, and the node refuses it so.
        foreach (var line in new[] { remove.Verb, source.Verb })
        {
            Assert.True(WireGrammar.TryParse(line, out var parsed, out _));
            Assert.Equal(door, BackupGate.Refused(VerbCatalog.Match(parsed)!.Value.Spec, parsed, "desk", FleetRoles.Backup, door));
            desk.Link.ClearSent();
            var reply = await desk.Session.SendQuietAsync(line, Ct);
            Assert.False(reply.Ok);
            Assert.Equal(door, reply.Text);
        }

        // Queries, PANIC, Show Mode and the fleet's own verbs stay live.
        Assert.Null(gate("STATE"));
        Assert.Null(gate("PREFLIGHT QUICK"));
        Assert.Null(gate("PANIC"));
        Assert.Null(gate("FAILOVER"));
        Assert.Null(gate("FLEET SOLO"));
        Assert.True(desk.Main.ShowModule.Strip.Panic.IsEnabled);
        Assert.True(desk.Main.ShowModule.Strip.Arm.IsEnabled);

        // The attention bar says whose show it is.
        var banner = Assert.Single(desk.Main.Banners, b => b.Id == "fleet");
        Assert.Equal(("amber", door), (banner.Light, banner.Words));

        // The mirror's card: green while identical, the mirror's words, FAILOVER live and sent as the line.
        var mirror = desk.Main.Cards.Shown.Single(c => c.Instance.Descriptor.Path == "fleet.mirror");
        Assert.Equal("green", mirror.Light);
        Assert.Equal("same show (rev 4), clock, transports and air as FOH-A", mirror.Words);
        var failover = mirror.Actions.Single(a => a.Verb == "FAILOVER");
        Assert.True(failover.IsEnabled);
        desk.Link.ClearSent();
        await failover.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal("FAILOVER", desk.Verbs()[^1]);

        // The node took the show: its door is gone, the banner with it, and the surface's edits run again.
        Assert.DoesNotContain(desk.Main.Banners, b => b.Id == "fleet");
        Assert.Null(remove.Refusal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OnAFencedNodeEditsGreyWithTheFencesWordsAndTheBannerIsRed()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "backup", "fenced");
        var window = MainWindowTests.Open(desk);
        DeskHarness.Pump();
        var door = Door("fenced");
        Assert.Equal("fenced: FOH-B has the show (epoch 1) — FLEET BACKUP FOH-B to mirror it", door);
        desk.Main.Workspace = Workspace.Map;
        var sf = desk.Main.Map.Outliner.SelectMany(g => g.Nodes).First(n => n.Kind == "surface");
        desk.Main.Map.Select(sf.Kind, sf.Id);
        DeskHarness.Pump();
        Assert.Equal(door, sf.Actions.Single(a => a.Id == "remove").Refusal);
        Assert.Equal(door, (await desk.Session.SendQuietAsync(sf.Actions.Single(a => a.Id == "remove").Verb, Ct)).Text);
        Assert.Null(desk.Session.GateWords("PANIC"));
        Assert.Null(desk.Session.GateWords("FLEET BACKUP 9f8e7d6c"));
        var banner = Assert.Single(desk.Main.Banners, b => b.Id == "fleet");
        Assert.Equal(("red", door), (banner.Light, banner.Words));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ANodeWithTheShowHasNoDoorAndItsEditsRun()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "backup", "takenOver");
        var window = MainWindowTests.Open(desk);
        DeskHarness.Pump();
        desk.Main.Workspace = Workspace.Map;
        var sf = desk.Main.Map.Outliner.SelectMany(g => g.Nodes).First(n => n.Kind == "surface");
        desk.Main.Map.Select(sf.Kind, sf.Id);
        DeskHarness.Pump();
        Assert.Null(sf.Actions.Single(a => a.Id == "remove").Refusal);
        Assert.DoesNotContain(desk.Main.Banners, b => b.Id == "fleet");
        // The last takeover's card: green, its words, FAILBACK CONFIRM.
        var last = desk.Main.Cards.Shown.Single(c => c.Instance.Descriptor.Path == "fleet.last");
        Assert.Equal("green", last.Light);
        Assert.StartsWith("takeover tk00000000 of FOH-A by FOH-B (pressed) — epoch 1 committed", last.Words, StringComparison.Ordinal);
        Assert.Equal("FAILBACK CONFIRM", Assert.Single(last.Actions).Verb);
        window.Close();
    }
}
