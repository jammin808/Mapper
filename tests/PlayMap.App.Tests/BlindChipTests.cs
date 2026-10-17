using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.App.ViewModels.Show;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine;

namespace PlayMap.App.Tests;

/// <summary>The blind chip: while a draft is open (STATE.show.blind) the show strip shows "BLIND n", and a press opens the blind
/// panel — the draft's changes as BLIND DIFF words them (a real engine's diff of the same edit reads the same rows), Commit now
/// (BLIND COMMIT NOW), Commit at the next item (exactly BLIND COMMIT NEXT, which waits for the focused playlist's next item and lands
/// there), Discard (BLIND DISCARD), FORCE added while the diff names conflicts, and the draft view (MAP's 3D view asks for VIEW
/// editor-draft SHOW editor DRAFT), offered only for this desk's own draft. Each line is one the catalogue names and a real engine
/// decides as the demo does.</summary>
public class BlindChipTests
{
    /// <summary>Another client on the node (its edits go to the air, not to the desk's draft).</summary>
    private static readonly SessionInfo Patterns = new("p1", "bus", "127.0.0.1", true, "Patterns", true);

    private static async Task<CommandReply> Run(EngineCore engine, string text, SessionInfo session)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out _), text);
        return await engine.HandleAsync(line, session, Ct);
    }

    /// <summary>The show fixture without its foreign draft, its panic cleared: the desk opens its own draft and renames a surface in it.</summary>
    private static async Task<(DeskHarness Desk, string Surface, string Rename)> OwnDraft()
    {
        var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        desk.Link.SetSection("show", "{\"blind\":null}");
        await desk.Session.SendAsync("PANIC CLEAR", Ct);
        DeskHarness.Pump();
        Assert.False(desk.Main.ShowModule.Strip.Blind.IsPresent);
        await desk.Main.ShowModule.Strip.ArmMenu.Single(a => a.Id == "show.blind").Command.ExecuteAsync();
        var sf = desk.Link.CurrentProject.Stage.Surfaces[0].Id;
        var rename = $"SET {sf} name \"Blind wall\"";
        Assert.True((await desk.Session.SendAsync(rename, Ct)).Ok);
        DeskHarness.Pump();
        desk.Link.ClearSent();
        return (desk, sf, rename);
    }

    private static ActionMenuButton Pressable(Window window, DeskAction action) =>
        window.GetVisualDescendants().OfType<ActionMenuButton>().Single(b => b.Name != "HistoryDot" && ReferenceEquals(b.Action, action) && b.IsEffectivelyVisible);

    [AvaloniaFact]
    public async Task TheChipOpensTheDraftsDiffAndCommitAtTheNextItemSendsBlindCommitNextAndLandsThere()
    {
        var (desk, sf, rename) = await OwnDraft();
        await using var _ = desk;
        var window = MainWindowTests.Open(desk);
        var blind = desk.Main.ShowModule.Strip.Blind;
        var chip = MainWindowTests.Find<Button>(window, "BlindChip");
        Assert.True(chip.IsEffectivelyVisible);
        Assert.Equal("BLIND 1", MainWindowTests.Find<TextBlock>(window, "BlindText").Text);
        Assert.True(blind.OwnsDraft);
        Assert.Equal($"Blind editing by {Link.FakeEngineLink.ClientName} (this desk): 1 change staged", blind.Words);
        // The chip's right-click menu is the panel's lines.
        Assert.Same(blind.Actions, DeskMenu.GetActions(chip));
        foreach (var a in blind.Actions.Where(a => a.Verb.Length > 0))
            Assert.Null(DeskVerbRuleTests.Fault(a.Verb));

        // A press opens the panel and reads the draft's diff once.
        var host = MainWindowTests.Find<ContentControl>(window, "BlindPanelHost");
        Assert.False(host.IsVisible);
        DeskHarness.Click(window, chip);
        await blind.Fetching;
        DeskHarness.Pump();
        Assert.True(blind.IsOpen);
        Assert.True(host.IsEffectivelyVisible);
        Assert.Equal(["BLIND DIFF"], desk.Verbs());
        var change = Assert.Single(blind.Changes);
        Assert.Equal(sf, change.Id);
        Assert.False(change.Conflict);
        Assert.Contains(change.Words, w => w.Contains("Blind wall", StringComparison.Ordinal));
        var rows = MainWindowTests.Find<ItemsControl>(window, "BlindChanges");
        Assert.Equal(1, rows.ItemCount);

        // A real engine given the desk's show and the same edit words the same diff.
        await using (var engine = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), desk.Link.CurrentProject))
        {
            Assert.True((await QuadToolTests.Run(engine, "BLIND ON")).Ok);
            Assert.True((await QuadToolTests.Run(engine, rename)).Ok);
            var diff = await QuadToolTests.Run(engine, "BLIND DIFF");
            Assert.True(diff.Ok, diff.Payload);
            Assert.Equal(BlindChange.Read(diff.Payload!), blind.Changes.ToArray());
            // With nothing playing, the next item does not come: the node says so, as the demo does.
            var early = await QuadToolTests.Run(engine, "BLIND COMMIT NEXT");
            Assert.False(early.Ok);
            desk.Link.ClearSent();
            DeskHarness.Click(window, Pressable(window, blind.CommitNext));
            await blind.CommitNext.Command.Last;
            DeskHarness.Pump();
            Assert.Equal(["BLIND COMMIT NEXT"], desk.Verbs());
            Assert.Equal(Link.DeskSession.Plain(early.Payload!), desk.Session.LastToast?.Text);
        }

        // Playing, Commit at the next item sends exactly BLIND COMMIT NEXT; the commit waits and the chip says where it lands.
        Assert.True((await desk.Session.SendAsync("PLAYLIST GO pl_show", Ct)).Ok);
        DeskHarness.Pump();
        desk.Link.ClearSent();
        DeskHarness.Click(window, Pressable(window, blind.CommitNext));
        await blind.CommitNext.Command.Last;
        DeskHarness.Pump();
        Assert.Equal(["BLIND COMMIT NEXT"], desk.Verbs());
        var waiting = desk.Session.State.Section("show", ShowSection.Read).Blind;
        Assert.Equal("at the next item of Show", waiting?.CommitWhen);
        Assert.EndsWith(" · lands at the next item of Show", blind.Words, StringComparison.Ordinal);
        Assert.False(blind.CommitNow.IsEnabled);
        Assert.False(blind.CommitNext.IsEnabled);
        Assert.NotEqual("Blind wall", desk.Link.CurrentProject.Stage.Surfaces.Find(sf)!.Name);

        // The next item lands it: the air has the draft's name, the chip and its panel go.
        Assert.True((await desk.Session.SendAsync("PLAYLIST NEXT pl_show", Ct)).Ok);
        DeskHarness.Pump();
        Assert.Equal("Blind wall", desk.Link.CurrentProject.Stage.Surfaces.Find(sf)!.Name);
        Assert.Null(desk.Session.State.Section("show", ShowSection.Read).Blind);
        Assert.False(chip.IsVisible);
        Assert.False(blind.IsOpen);
        Assert.False(host.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CommitNowAndDiscardSendTheirLinesAndARealEngineRunsThem()
    {
        var (desk, sf, rename) = await OwnDraft();
        await using var _ = desk;
        var window = MainWindowTests.Open(desk);
        var blind = desk.Main.ShowModule.Strip.Blind;
        var show = desk.Link.CurrentProject;
        var before = show.Stage.Surfaces.Find(sf)!.Name;
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "BlindChip"));
        await blind.Fetching;
        DeskHarness.Pump();

        // Discard drops the draft: the air keeps its name, the chip goes.
        desk.Link.ClearSent();
        DeskHarness.Click(window, Pressable(window, blind.Discard));
        await blind.Discard.Command.Last;
        DeskHarness.Pump();
        Assert.Equal(["BLIND DISCARD"], desk.Verbs());
        Assert.Equal(before, desk.Link.CurrentProject.Stage.Surfaces.Find(sf)!.Name);
        Assert.False(blind.IsPresent);
        Assert.False(MainWindowTests.Find<Button>(window, "BlindChip").IsVisible);

        // Again, and Commit now lands it at once.
        await desk.Main.ShowModule.Strip.ArmMenu.Single(a => a.Id == "show.blind").Command.ExecuteAsync();
        Assert.True((await desk.Session.SendAsync(rename, Ct)).Ok);
        DeskHarness.Pump();
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "BlindChip"));
        await blind.Fetching;
        DeskHarness.Pump();
        Assert.Equal("Commit now", blind.CommitNow.Label);
        desk.Link.ClearSent();
        DeskHarness.Click(window, Pressable(window, blind.CommitNow));
        await blind.CommitNow.Command.Last;
        DeskHarness.Pump();
        Assert.Equal(["BLIND COMMIT NOW"], desk.Verbs());
        Assert.Equal("Blind wall", desk.Link.CurrentProject.Stage.Surfaces.Find(sf)!.Name);
        Assert.False(blind.IsPresent);

        // A real engine given the show as it was runs each line the panel sends.
        await using var engine = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), show);
        foreach (var line in new[] { "BLIND ON", rename, "BLIND DIFF", "BLIND DISCARD", "BLIND ON", rename, "BLIND COMMIT NOW" })
        {
            var r = await QuadToolTests.Run(engine, line);
            Assert.True(r.Ok, line + ": " + r.Payload);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task ADiffNamingConflictsAddsForceToTheCommitsAsARealEngineAsksForIt()
    {
        var (desk, sf, rename) = await OwnDraft();
        await using var _ = desk;
        await using var engine = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), desk.Link.CurrentProject);
        // The desk renames the surface in its draft; another client renames it on the air.
        Assert.True((await QuadToolTests.Run(engine, "BLIND ON")).Ok);
        Assert.True((await QuadToolTests.Run(engine, rename)).Ok);
        var air = await Run(engine, $"SET {sf} name \"Air wall\"", Patterns);
        Assert.True(air.Ok, air.Payload);
        var diff = await QuadToolTests.Run(engine, "BLIND DIFF");
        Assert.True(diff.Ok, diff.Payload);
        using (var d = JsonDocument.Parse(diff.Payload!))
            Assert.Equal([sf], d.RootElement.GetProperty("conflicts").EnumerateArray().Select(c => c.GetString() ?? "").ToArray());
        desk.Link.Answer("BLIND DIFF", diff.Payload!);

        var window = MainWindowTests.Open(desk);
        var blind = desk.Main.ShowModule.Strip.Blind;
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "BlindChip"));
        await blind.Fetching;
        DeskHarness.Pump();
        var change = Assert.Single(blind.Changes);
        Assert.True(change.Conflict);
        Assert.True(blind.HasConflicts);
        Assert.EndsWith(" — changed on the air too", change.Title, StringComparison.Ordinal);
        Assert.Equal(("BLIND COMMIT NOW FORCE", "BLIND COMMIT NEXT FORCE"), (blind.CommitNow.Verb, blind.CommitNext.Verb));
        Assert.Equal(("Commit now anyway (FORCE)", "Commit at the next item anyway (FORCE)"), (blind.CommitNow.Label, blind.CommitNext.Label));

        // Without FORCE the node refuses the commit and names the conflict; with it the draft's version lands.
        var plain = await QuadToolTests.Run(engine, "BLIND COMMIT NOW");
        Assert.False(plain.Ok);
        Assert.StartsWith("changed on the air since BLIND ON: ", plain.Payload, StringComparison.Ordinal);
        Assert.True((await QuadToolTests.Run(engine, "BLIND COMMIT NOW FORCE")).Ok);
        desk.Link.ClearSent();
        DeskHarness.Click(window, Pressable(window, blind.CommitNow));
        await blind.CommitNow.Command.Last;
        DeskHarness.Pump();
        Assert.Equal(["BLIND COMMIT NOW FORCE"], desk.Verbs());
        Assert.Null(DeskVerbRuleTests.Fault("BLIND COMMIT NEXT FORCE"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheDraftViewAsksMapsViewForTheDraftAndOnlyTheDraftsOwnerHasIt()
    {
        var (desk, _, _) = await OwnDraft();
        await using var __ = desk;
        var window = MainWindowTests.Open(desk);
        var blind = desk.Main.ShowModule.Strip.Blind;
        desk.Main.Workspace = Workspace.Map;
        DeskHarness.Pump();
        Assert.Equal("editor", desk.Main.Map.EditorViewId);
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "BlindChip"));
        await blind.Fetching;
        DeskHarness.Pump();
        var toggle = MainWindowTests.Find<ToggleButton>(window, "DraftViewToggle");
        Assert.True(toggle.IsEnabled);
        Assert.True(blind.DraftView.IsEnabled);

        // On: MAP's 3D view lets the air's view go and asks for the draft's.
        desk.Link.ClearSent();
        DeskHarness.Click(window, toggle);
        Assert.True(desk.Main.Map.ShowDraft);
        Assert.True(toggle.IsChecked);
        Assert.Equal("editor-draft", desk.Main.Map.EditorViewId);
        Assert.Equal("editor-draft", MainWindowTests.Find<MapViewport>(window, "Viewport").ViewId);
        Assert.Contains("VIEW editor-draft SHOW editor DRAFT", desk.Link.SentLines);
        Assert.Contains("VIEW editor HIDE", desk.Link.SentLines);
        Assert.Null(DeskVerbRuleTests.Fault("VIEW editor-draft SHOW editor DRAFT"));
        Assert.EndsWith("the blind draft", desk.Main.Map.ViewTitle, StringComparison.Ordinal);

        // Off: the air's view again.
        desk.Link.ClearSent();
        DeskHarness.Click(window, toggle);
        Assert.False(desk.Main.Map.ShowDraft);
        Assert.Contains("VIEW editor-draft HIDE", desk.Link.SentLines);
        Assert.Contains(desk.Link.SentLines, l => l.StartsWith("VIEW editor SHOW editor ", StringComparison.Ordinal));

        // The draft gone, the draft view goes with it.
        DeskHarness.Click(window, toggle);
        Assert.True(desk.Main.Map.ShowDraft);
        await blind.Discard.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.False(desk.Main.Map.ShowDraft);
        Assert.Equal("editor", desk.Main.Map.EditorViewId);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnotherSessionsDraftShowsItsChipButNoDraftViewAndItsWaitingCommitTakesNoSecond()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "show");
        var window = MainWindowTests.Open(desk);
        var blind = desk.Main.ShowModule.Strip.Blind;
        using var doc = JsonDocument.Parse(StateFixtures.Read("show"));
        var held = doc.RootElement.GetProperty("show").GetProperty("blind");
        Assert.True(blind.IsPresent);
        Assert.False(blind.OwnsDraft);
        Assert.Equal("BLIND " + held.GetProperty("changes").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            MainWindowTests.Find<TextBlock>(window, "BlindText").Text);
        Assert.Equal($"Blind editing by {held.GetProperty("owner").GetString()}: 1 change staged · lands {held.GetProperty("commit").GetProperty("when").GetString()}", blind.Words);
        // Its commit already waits: neither commit is offered; the desk may still discard it (the node lets the desk drop a draft).
        Assert.False(blind.CommitNow.IsEnabled);
        Assert.False(blind.CommitNext.IsEnabled);
        Assert.True(blind.Discard.IsEnabled);
        Assert.False(blind.DraftView.IsEnabled);

        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "BlindChip"));
        await blind.Fetching;
        DeskHarness.Pump();
        Assert.False(MainWindowTests.Find<ToggleButton>(window, "DraftViewToggle").IsEnabled);
        // A diff the node could not give shows the node's own words, and no rows.
        var reply = await desk.Session.SendQuietAsync("BLIND DIFF", Ct);
        Assert.False(reply.Ok);
        Assert.Equal(Link.DeskSession.Plain(reply.Text), blind.DiffWords);
        Assert.Empty(blind.Changes);
        window.Close();
    }
}
