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
using PlayMap.Engine.Rendering;

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
            DeskHarness.Hold(window, Pressable(window, blind.CommitNext));
            await blind.CommitNext.Command.Last;
            DeskHarness.Pump();
            Assert.Equal(["BLIND COMMIT NEXT"], desk.Verbs());
            Assert.Equal(Link.DeskSession.Plain(early.Payload!), desk.Session.LastToast?.Text);
        }

        // Playing, Commit at the next item sends exactly BLIND COMMIT NEXT; the commit waits and the chip says where it lands.
        Assert.True((await desk.Session.SendAsync("PLAYLIST GO pl_show", Ct)).Ok);
        DeskHarness.Pump();
        desk.Link.ClearSent();
        DeskHarness.Hold(window, Pressable(window, blind.CommitNext));
        await blind.CommitNext.Command.Last;
        DeskHarness.Pump();
        Assert.Equal(["BLIND COMMIT NEXT"], desk.Verbs());
        var waiting = desk.Session.State.Section("show", ShowSection.Read).Blind;
        Assert.Equal("at the next item of Show", waiting?.CommitWhen);
        Assert.EndsWith(" · lands at the next item of Show", blind.Words, StringComparison.Ordinal);
        Assert.False(blind.CommitNow.IsEnabled);
        Assert.False(blind.CommitNext.IsEnabled);
        Assert.NotEqual("Blind wall", desk.Link.CurrentProject.Stage.Surfaces.Find(sf)!.Name);

        // The next item lands it: the air has the draft's name, the chip and its panel go (past the node's min-GO window after the GO).
        desk.Clock.AdvanceMs(1000);
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
        // Held to confirm, as its row is: a click sends nothing.
        Assert.True(blind.CommitNow.IsHold);
        DeskHarness.Click(window, Pressable(window, blind.CommitNow));
        await blind.CommitNow.Command.Last;
        Assert.Empty(desk.Verbs());
        DeskHarness.Hold(window, Pressable(window, blind.CommitNow));
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
        DeskHarness.Hold(window, Pressable(window, blind.CommitNow));
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

    /// <summary>The views a real engine draws on a machine with pictures: the render host's views take every request, and its draft feed
    /// runs every draft view (it starts at once and stops when the view goes).</summary>
    private sealed class Drawing : IViewRenderer, IPreviewFeed
    {
        public string? Show(PlayMap.Engine.Rendering.ViewRequest request) => null;

        public string? RingOf(string viewId) => null;

        public string? Camera(string viewId, PlayMap.Render.Projection.EditorCamera camera) => null;

        public bool Hide(string viewId) => true;

        public IDisposable? Start(PlayMap.Engine.Rendering.ViewRequest request, PlayMap.Engine.Rendering.PreviewSink sink, out string why)
        {
            why = "";
            return new CancellationTokenSource();
        }
    }

    /// <summary>The demo answers a draft view as a real engine that draws pictures does, line by line: a draft view is shown with no
    /// draft open (the node draws the air in it), with this session's draft and with another's; a view id that has drawn the air is
    /// refused one with the node's words; KEEP and HIDE of a view not shown are refused alike.</summary>
    [Fact]
    public async Task TheDemoAnswersADraftViewAsARealEngineThatDrawsDoes()
    {
        await using var demo = new Link.FakeEngineLink(clock: new PlayMap.Core.Time.ManualClock(1_000_000_000));
        await using var engine = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), demo.CurrentProject);
        var drawing = new Drawing();
        engine.Previews.AttachRenderer(drawing);
        engine.Previews.Register("draft", drawing);
        string[] lines =
        [
            "VIEW editor SHOW editor 640x360",
            "VIEW editor SHOW editor DRAFT",
            "VIEW editor-draft SHOW editor DRAFT",
            "VIEW editor-draft KEEP",
            "VIEW editor-draft HIDE",
            "VIEW editor-draft HIDE",
            "VIEW editor-draft KEEP",
            "VIEW op-op_1 SHOW output:op_1 480x270",
            "VIEW op-op_1 SHOW editor DRAFT",
            "BLIND ON",
            "VIEW editor-draft SHOW editor DRAFT",
            "BLIND DISCARD",
            "VIEW editor-draft SHOW editor DRAFT",
        ];
        var real = new List<(bool, string)>();
        var fake = new List<(bool, string)>();
        foreach (var text in lines)
        {
            var r = await Run(engine, text, QuadToolTests.DeskOnNode);
            var f = await demo.SendAsync(text, Ct);
            // A VIEW line's answer is compared whole (the view its reply names, or the words why not); a BLIND line's by whether it ran.
            var view = text.StartsWith("VIEW ", StringComparison.Ordinal);
            real.Add((r.Ok, !view ? "" : r.Ok ? ViewOf(r.Payload) : r.Payload ?? ""));
            fake.Add((f.Ok, !view ? "" : f.Ok ? ViewOf(f.Text) : f.Text));
        }
        Assert.Equal(real, fake);
        Assert.Equal((false, "editor has drawn the air — a draft view takes a view id of its own (VIEW editor-draft SHOW editor DRAFT)"), fake[1]);
        Assert.Equal((true, "editor-draft"), fake[2]);

        // Another session's draft: the draft view is shown all the same (one draft per node; the node draws it).
        Assert.True((await Run(engine, "BLIND ON", Patterns)).Ok);
        demo.SetSection("show", JsonSerializer.Serialize(new { blind = new { on = true, owner = "Patterns", session = "p1", transport = "bus", changes = 0 } }));
        var realOther = await Run(engine, "VIEW other-draft SHOW editor DRAFT", QuadToolTests.DeskOnNode);
        var fakeOther = await demo.SendAsync("VIEW other-draft SHOW editor DRAFT", Ct);
        Assert.Equal((true, "other-draft"), (realOther.Ok, ViewOf(realOther.Payload)));
        Assert.Equal((true, "other-draft"), (fakeOther.Ok, ViewOf(fakeOther.Text)));
    }

    /// <summary>The view a SHOW reply names ("" for a reply that is not a view's).</summary>
    private static string ViewOf(string? reply)
    {
        if (reply is not { Length: > 0 } || !reply.TrimStart().StartsWith('{'))
            return reply ?? "";
        using var doc = JsonDocument.Parse(reply);
        return doc.RootElement.TryGetProperty("view", out var v) ? v.GetString() ?? "" : reply;
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
