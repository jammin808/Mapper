using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PlayMap.App.Controls;
using PlayMap.App.Infrastructure;
using PlayMap.App.ViewModels;
using PlayMap.App.ViewModels.Show;
using PlayMap.Core.Model;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;
using PlayMap.Engine;

namespace PlayMap.App.Tests;

/// <summary>The desk's gate takes a line through the node's doors in the node's order and greys it with the node's own words:
/// the router's pairing, the fleet's door, the open blind draft, the show's policy with this desk as its draft's owner, the network
/// area's lock, then LockGuard by what the edit changes. Each case is checked against a real engine given the desk's project.</summary>
public class GateOrderTests
{
    /// <summary>The desk's session as the demo engine's WHOAMI names it, for a real engine.</summary>
    private static readonly SessionInfo DeskOnNode = new("demo", "desk", "127.0.0.1", true, Link.FakeEngineLink.ClientName, true);

    private static EngineCore Engine(Project project) =>
        new(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), project);

    private static async Task<CommandReply> Run(EngineCore engine, string text, SessionInfo? session = null)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out _), text);
        return await engine.HandleAsync(line, session ?? DeskOnNode, Ct);
    }

    [AvaloniaFact]
    public async Task ArmedTheDraftsOwnerEditsTheMappingAndTheLinesGoToItsDraft()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        // The fixture's draft is another session's; this desk's own comes with BLIND ON.
        desk.Link.SetSection("show", "{\"blind\":null}");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        DeskHarness.Pump();
        var strip = desk.Main.ShowModule.Strip;
        Assert.True(strip.IsArmed);
        var sf = desk.Link.CurrentProject.Stage.Surfaces[0];
        var rename = $"SET {sf.Id} name \"Blind wall\"";
        var gate = desk.Session.GateWords;
        Assert.StartsWith("Not while armed: SET moves the mapping on the air", gate(rename), StringComparison.Ordinal);

        // Edit blind: the desk's own draft (STATE.show.blind names this desk's session, as WHOAMI does).
        var blindOn = strip.ArmMenu.Single(a => a.Id == "show.blind");
        await blindOn.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.Equal("BLIND ON", desk.Verbs()[^1]);
        var blind = desk.Session.State.Section("show", ShowSection.Read).Blind;
        Assert.NotNull(blind);
        Assert.Equal((desk.Session.SessionId, desk.Session.Transport), (blind.Session, blind.Transport));

        // The mapping is live again for this desk: the inspector's name field sends its SET, the draft takes it, the air keeps the show.
        Assert.Null(gate(rename));
        desk.Main.Workspace = Workspace.Map;
        desk.Main.Map.Select("surface", sf.Id);
        DeskHarness.Pump();
        var inspector = Assert.IsType<SurfaceInspector>(desk.Main.Map.Inspector.Current);
        Assert.True(inspector.IsEditable);
        desk.Link.ClearSent();
        inspector.Name = "Blind wall";
        await desk.FlushThrottle();
        Assert.Equal(rename, Assert.Single(desk.Verbs()));
        Assert.Equal(sf.Name, desk.Link.CurrentProject.Stage.Surfaces.Find(sf.Id)!.Name);
        Assert.Equal(1, desk.Session.State.Section("show", ShowSection.Read).Blind!.Changes);
        // A warp drag's steps go out too (the throttle asks the same gate).
        var op = desk.Link.CurrentProject.Outputs[0].Id;
        Assert.Null(gate($"WARP RESET {op}"));

        // Another show is not opened under the draft: the node's words, before the press.
        var opened = BlindWords.DraftOpen(Link.FakeEngineLink.ClientName, 1);
        Assert.Equal(opened, gate("PROJECT NEW Other"));
        Assert.Equal(opened, (await desk.Session.SendQuietAsync("PROJECT NEW Other", Ct)).Text);

        // A real engine decides the same: armed, its draft's owner's rename runs into the draft; PROJECT NEW is refused so.
        await using var engine = Engine(desk.Link.CurrentProject);
        Assert.True((await Run(engine, "SHOW ARM")).Ok);
        Assert.False((await Run(engine, rename)).Ok);
        Assert.True((await Run(engine, "BLIND ON")).Ok);
        Assert.True((await Run(engine, rename)).Ok);
        var refused = await Run(engine, "PROJECT NEW Other");
        Assert.False(refused.Ok);
        Assert.Equal(opened, refused.Payload);
        // Its STATE names the owner's session as WHOAMI does, so the desk knows the draft is its own.
        using var state = System.Text.Json.JsonDocument.Parse(engine.State().Json);
        var owner = state.RootElement.GetProperty("show").GetProperty("blind");
        Assert.Equal(("demo", "desk"), (owner.GetProperty("session").GetString(), owner.GetProperty("transport").GetString()));
    }

    [AvaloniaFact]
    public async Task ALockHoldsWhatTheEditChangesAsLockGuardDecidesIt()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        await desk.Session.SendAsync("SHOW DISARM", Ct);
        await desk.Session.SendAsync("UNLOCK ALL", Ct);
        await desk.Session.SendAsync("LOCK AREA mapping", Ct);
        DeskHarness.Pump();
        var gate = desk.Session.GateWords;
        var project = desk.Link.CurrentProject;
        var sf = project.Stage.Surfaces[0].Id;
        await using var engine = Engine(project);
        Assert.True((await Run(engine, "LOCK AREA mapping")).Ok);

        // Under a mapping lock a surface's name, its own Locked flag and its opacity are not the mapping: live, and the node runs them.
        foreach (var line in new[] { $"SET {sf} name \"Lobby\"", $"SET {sf} opacity 0.5", $"SET {sf} locked true", $"SET {sf} locked false" })
        {
            Assert.Null(gate(line));
            Assert.True((await Run(engine, line)).Ok, line);
        }
        // Its geometry is: greyed with LockGuard's words, the words the node refuses with; the inspector's position field says
        // them and sends nothing.
        var moved = VerbText.Set(sf, "transform.position", VerbText.Json(new PlayMap.Core.Numerics.Vec3d(1.5, 0, 0)));
        var deskLock = desk.Main.ShowModule.Strip.Show.ExplicitLocks.Single();
        Assert.Equal(LockGuard.AreaWords(deskLock), gate(moved));
        var engineWords = (await Run(engine, moved)).Payload;
        Assert.StartsWith("mapping is locked by ", engineWords, StringComparison.Ordinal);
        Assert.EndsWith("— UNLOCK AREA mapping first", engineWords, StringComparison.Ordinal);
        desk.Main.Workspace = Workspace.Map;
        desk.Main.Map.Select("surface", sf);
        DeskHarness.Pump();
        var inspector = Assert.IsType<SurfaceInspector>(desk.Main.Map.Inspector.Current);
        Assert.True(inspector.IsEditable);
        desk.Link.ClearSent();
        inspector.PositionText = "1.5, 0, 0";
        await desk.FlushThrottle();
        Assert.Empty(desk.Verbs());
        Assert.Equal(LockGuard.AreaWords(deskLock), desk.Session.LastToast?.Text);

        // Under a content lock a surface's opacity is content: greyed with the node's words.
        await desk.Session.SendAsync("UNLOCK AREA mapping", Ct);
        await desk.Session.SendAsync("LOCK AREA content", Ct);
        DeskHarness.Pump();
        var opacity = $"SET {sf} opacity 0.25";
        var lockWords = LockGuard.AreaWords(desk.Main.ShowModule.Strip.Show.ExplicitLocks.Single());
        Assert.Equal(lockWords, gate(opacity));
        Assert.Equal(lockWords, (await desk.Session.SendQuietAsync(opacity, Ct)).Text);
        Assert.True((await Run(engine, "UNLOCK AREA mapping")).Ok);
        Assert.True((await Run(engine, "LOCK AREA content")).Ok);
        Assert.StartsWith("content is locked by ", (await Run(engine, opacity)).Payload, StringComparison.Ordinal);

        // A locked playlist holds an edit of one of its items, though the line names the item.
        await desk.Session.SendAsync("UNLOCK AREA content", Ct);
        var pl = project.Playlists[0];
        await desk.Session.SendAsync("LOCK " + pl.Id, Ct);
        DeskHarness.Pump();
        var item = $"SET {pl.Items[0].Id} label \"Renamed\"";
        var entityWords = LockGuard.EntityWords(desk.Main.ShowModule.Strip.Show.ExplicitLocks.Single(), pl);
        Assert.Equal(entityWords, gate(item));
        Assert.True((await Run(engine, "UNLOCK AREA content")).Ok);
        Assert.True((await Run(engine, "LOCK " + pl.Id)).Ok);
        var held = await Run(engine, item);
        Assert.False(held.Ok);
        Assert.EndsWith($"— UNLOCK {pl.Id} first", held.Payload, StringComparison.Ordinal);
        // The menu says why: locked.
        var remove = DeskAction.Send(desk.Session, "probe", "Rename", () => item);
        Assert.Equal("Rename · locked", remove.MenuLabel);
    }

    [AvaloniaFact]
    public async Task TheNetworkLockSaysTheSameWordsOnTheDeskAndTheNode()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        await desk.Session.SendAsync("SHOW DISARM", Ct);
        await desk.Session.SendAsync("LOCK AREA network doors open", Ct);
        DeskHarness.Pump();
        var words = desk.Session.GateWords("LINK PATTERNS OFF");
        Assert.NotNull(words);
        Assert.Contains("'doors open'", words, StringComparison.Ordinal);
        Assert.Equal(words, (await desk.Session.SendQuietAsync("LINK PATTERNS OFF", Ct)).Text);

        // The desk's words are LockGuard's for the lock STATE shows.
        Assert.Equal(LockGuard.AreaWords(desk.Main.ShowModule.Strip.Show.ExplicitLocks.Single(l => l.Target == LockAreas.Network)), words);

        // A real engine refuses with LockGuard's words for its own lock (the lock's words among them), as the desk shows them.
        await using var engine = Engine(desk.Link.CurrentProject);
        Assert.True((await Run(engine, "LOCK AREA network doors open")).Ok);
        var refused = await Run(engine, "LINK PATTERNS OFF");
        Assert.False(refused.Ok);
        using var state = System.Text.Json.JsonDocument.Parse(engine.State().Json);
        var held = ShowSection.Read(state.RootElement.GetProperty("show")).ExplicitLocks.Single(l => l.Target == LockAreas.Network);
        Assert.Equal(LockGuard.AreaWords(held), refused.Payload);
        Assert.Contains("('doors open', ", refused.Payload, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AnUnpairedDeskGreysWhatTheNodesDoorRefusesWithItsWords()
    {
        await using var desk = new DeskHarness(link => link.Paired = false);
        StateFixtures.Load(desk.Link, "show");
        DeskHarness.Pump();
        Assert.False(desk.Session.Paired);
        var gate = desk.Session.GateWords;
        var notPaired = PlayMap.Net.Commands.WireWords.NotPaired;
        var sf = desk.Link.CurrentProject.Stage.Surfaces[0].Id;
        // Every line that would change something — PANIC among them — meets the door; reading does not.
        foreach (var line in new[] { "PANIC", "PANIC CLEAR FADE 1000", $"SET {sf} name \"x\"", "SHOW ARM" })
            Assert.Equal(notPaired, gate(line));
        Assert.Null(gate("STATE"));
        Assert.False(desk.Main.ShowModule.Strip.Panic.IsEnabled);
        Assert.Equal("PANIC · not paired", desk.Main.ShowModule.Strip.Panic.MenuLabel);
        // The demo engine and a real one say the same.
        Assert.Equal(notPaired, (await desk.Session.SendQuietAsync("PANIC", Ct)).Text);
        await using var engine = Engine(desk.Link.CurrentProject);
        var refused = await Run(engine, "PANIC", new SessionInfo("u1", "wire", "10.0.0.66", false, "Someone", false));
        Assert.Equal(notPaired, refused.Payload);
    }

    [AvaloniaFact]
    public async Task AMenuSaysWhichDoorRefusedAnEntry()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "backup", "backup");
        DeskHarness.Pump();
        var sf = desk.Link.CurrentProject.Stage.Surfaces[0].Id;
        var rename = DeskAction.Send(desk.Session, "probe", "Rename", () => $"SET {sf} name \"x\"");
        Assert.Equal("Rename · mirror", rename.MenuLabel);
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, [rename]);
        Assert.Equal("Rename · mirror", Assert.IsType<MenuItem>(menu.Items[0]).Header);

        StateFixtures.Load(desk.Link, "backup", Assert.Single(StateFixtures.Scenarios("backup"), s => s.Sections["fleet"]?["role"]?.GetValue<string>() == "fenced").Name);
        DeskHarness.Pump();
        rename.Refresh();
        Assert.Equal("Rename · fenced", rename.MenuLabel);
    }
}
