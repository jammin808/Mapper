using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.App.ViewModels.Map;
using PlayMap.Engine;

namespace PlayMap.App.Tests;

/// <summary>Health ▸ Calibration: a score card per target of STATE.calibration (wall.json, written by a real engine): the grade, score
/// and words, the staged result's Accept (first on the card and in its menu), and every term's Fix — pressed on screen — sends that
/// term's own line. The card's Accept, Revert and Save are live exactly when a real engine in the same state runs them, greyed with
/// its words otherwise. The history strip reads CALIBRATION HISTORY &lt;target&gt; when the tab or an inspector shows the card (a real
/// engine's answer of the desk's show); each dot offers exactly the lines the real engine runs on that result; Recall sends
/// CALIBRATION RECALL &lt;cs&gt;; the projector's inspector shows the same card.</summary>
public class CalibrationDeskTests
{
    [AvaloniaFact]
    public async Task TheScoreCardsFixSendsEachTermsLine()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "wall");
        var window = MainWindowTests.Open(desk);
        Assert.Contains(desk.Main.Drawers.Health.Tabs, t => t.Key == "calibration" && t.Title == "Calibration");
        desk.Main.Drawers.Show("health", "calibration");
        DeskHarness.Pump();
        var tab = desk.Main.Calibration.Tab;
        Assert.True(tab.IsShown);
        foreach (var c in tab.Cards)
            await c.Fetching;
        DeskHarness.Pump();
        using var doc = JsonDocument.Parse(StateFixtures.Read("wall"));
        var targets = doc.RootElement.GetProperty("calibration").GetProperty("targets").EnumerateArray().ToList();
        Assert.Equal(targets.Select(t => t.GetProperty("id").GetString()).ToArray(), tab.Cards.Select(c => c.Id).ToArray());
        Assert.Equal(doc.RootElement.GetProperty("calibration").GetProperty("task").GetProperty("words").GetString(), tab.TaskWords);

        var first = tab.Cards[0];
        Assert.Equal(("A", "0.2 px", "green", "A 0.2 — fits its 14 pins to 0.13 px"), (first.Grade, first.Score, first.Light, first.Words));
        Assert.False(first.HasStaged);
        var second = tab.Cards[1];
        Assert.Equal("amber", second.Light);
        Assert.True(second.HasStaged);
        Assert.Equal("CALIBRATION ACCEPT cs_fixture1", second.AcceptStaged.Verb);
        Assert.Equal(("—", ""), (tab.Cards[2].Grade, tab.Cards[2].Score));
        // The card's own lines are the target's.
        Assert.Equal(["CALIBRATION RESOLVE pj_test1", "CALIBRATION ACCEPT pj_test1", "CALIBRATION FALLBACK pj_test1", "CALIBRATION SAVE pj_test1"],
            first.Actions.Select(a => a.Verb).ToArray());

        // Every term is a row with its light, value and words; each Fix on screen sends exactly its term's line.
        var view = window.GetVisualDescendants().OfType<Views.Map.CalibrationTabView>().Single();
        var pressed = 0;
        foreach (var (card, target) in tab.Cards.Zip(targets))
        {
            var terms = target.GetProperty("terms").EnumerateArray().ToList();
            Assert.Equal(terms.Count, card.Terms.Count);
            for (var i = 0; i < terms.Count; i++)
            {
                var term = terms[i];
                var row = card.Terms[i];
                Assert.Equal((term.GetProperty("code").GetString(), term.GetProperty("light").GetString()), (row.Title, row.Light));
                Assert.StartsWith(term.GetProperty("words").GetString()!, row.Words, StringComparison.Ordinal);
                var fix = term.GetProperty("fix").GetString()!;
                Assert.Equal(fix, row.Fix!.Verb);
                Assert.Null(DeskVerbRuleTests.Fault(fix));
                var button = view.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, row.Fix));
                // The operator scrolls the tab to the row.
                button.BringIntoView();
                DeskHarness.Pump();
                // The demo refuses these lines (its show has no such projector or pin): the operator closes the toast before the next press.
                foreach (var toast in desk.Session.Toasts.ToList())
                    desk.Session.Dismiss(toast);
                DeskHarness.Pump();
                desk.Link.ClearSent();
                DeskHarness.Click(window, button);
                await row.Fix.Command.Last;
                DeskHarness.Pump();
                Assert.Equal(fix, Assert.Single(desk.Verbs()));
                pressed++;
            }
        }
        Assert.Equal(12, pressed);
        Screens.Save(window, "health-calibration");
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheHistoryStripRecallsAKeptResultAsARealEngineKeepsIt()
    {
        await using var desk = new DeskHarness();
        // A real engine of the desk's own show makes three results for the left projector and answers its history.
        string calibration, history;
        await using (var engine = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), desk.Link.CurrentProject))
        {
            foreach (var line in new[] { "PROJECTOR SOLVE pj_left", "CALIBRATION SAVE pj_left AS before the show", "PROJECTOR SOLVE pj_left LOCK FOCAL" })
            {
                var r = await QuadToolTests.Run(engine, line);
                Assert.True(r.Ok, line + ": " + r.Payload);
            }
            var h = await QuadToolTests.Run(engine, "CALIBRATION HISTORY pj_left");
            Assert.True(h.Ok, h.Payload);
            history = h.Payload!;
            using (var state = JsonDocument.Parse(engine.State().Json))
                calibration = state.RootElement.GetProperty("calibration").GetRawText();

            desk.Link.SetSection("calibration", calibration);
            desk.Link.Answer("CALIBRATION HISTORY pj_left", history);
            var window = MainWindowTests.Open(desk);
            desk.Main.Drawers.Show("health", "calibration");
            DeskHarness.Pump();
            var card = desk.Main.Calibration.Tab.Cards.Single(c => c.Id == "pj_left");
            await card.Fetching;
            DeskHarness.Pump();
            Assert.Contains("CALIBRATION HISTORY pj_left", desk.Link.SentLines);

            // A dot per kept result, newest first, as the engine listed them; the current one offers no Recall.
            using var hd = JsonDocument.Parse(history);
            var snapshots = hd.RootElement.GetProperty("snapshots").EnumerateArray().ToList();
            Assert.True(snapshots.Count >= 2, history);
            Assert.Equal(snapshots.Select(s => s.GetProperty("id").GetString()).ToArray(), card.History.Select(d => d.Id).ToArray());
            var current = card.History.Single(d => d.Entry.Current);
            Assert.DoesNotContain(current.Recall, current.Actions);
            var older = card.History.First(d => !d.Entry.Current);
            Assert.Contains(older.Recall, older.Actions);
            var strip = window.GetVisualDescendants().OfType<ActionMenuButton>().Where(b => b.Name == "HistoryDot").ToList();
            Assert.Equal(card.History.Count, strip.Count);
            // The dot's right-click menu is its entries.
            var menu = new ContextMenu();
            DeskMenu.Fill(menu.Items, DeskMenu.GetActions(strip.Single(b => ReferenceEquals(b.Action, older.Dot)))!);
            Assert.Contains(menu.Items.OfType<MenuItem>(), i => ReferenceEquals(i.Tag, older.Recall));

            // Every dot offers exactly the lines the node takes on that result: each of Recall, Set as fallback and Forget is in its
            // menu when, and only when, a real engine of the same show runs it (the saved result, neither current nor staged, is no
            // fallback until it is recalled).
            var kept = engine.Store.Project;
            var offered = 0;
            foreach (var dot in card.History)
            {
                foreach (var a in new[] { dot.Recall, dot.SetFallback, dot.Forget })
                {
                    Assert.Null(DeskVerbRuleTests.Fault(a.Verb));
                    await using var fresh = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), kept);
                    var ran = await QuadToolTests.Run(fresh, a.Verb);
                    Assert.True(ran.Ok == dot.Actions.Contains(a), $"{a.Verb} offered {dot.Actions.Contains(a)}, the node: {ran.Payload}");
                    offered += ran.Ok ? 1 : 0;
                }
            }
            Assert.True(offered >= 4, offered.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var saved = card.History.Single(d => d.Entry.Pinned && !d.Entry.Current);
            Assert.DoesNotContain(saved.SetFallback, saved.Actions);

            // Recall sends exactly CALIBRATION RECALL <cs>, and the real engine runs it.
            desk.Link.ClearSent();
            await older.Recall.Command.ExecuteAsync();
            DeskHarness.Pump();
            var recall = "CALIBRATION RECALL " + older.Id;
            Assert.Equal(recall, Assert.Single(desk.Verbs()));
            Assert.Null(DeskVerbRuleTests.Fault(recall));
            var recalled = await QuadToolTests.Run(engine, recall);
            Assert.True(recalled.Ok, recalled.Payload);

            // The projector's inspector shows the same card.
            desk.Main.Workspace = Workspace.Map;
            desk.Main.Map.Select("projector", "pj_left");
            DeskHarness.Pump();
            Assert.Contains(card, desk.Main.Map.Inspector.Sections);
            window.Close();
        }
    }

    /// <summary>Lays a real engine's STATE.calibration and its answer to CALIBRATION HISTORY &lt;target&gt; over the demo, and waits
    /// for the card to read it.</summary>
    private static async Task<ScoreCardViewModel> Mirror(DeskHarness desk, EngineCore engine, string target)
    {
        var h = await QuadToolTests.Run(engine, "CALIBRATION HISTORY " + target);
        Assert.True(h.Ok, h.Payload);
        using (var state = JsonDocument.Parse(engine.State().Json))
        {
            desk.Link.SetSection("calibration", null);
            desk.Link.Answer("CALIBRATION HISTORY " + target, h.Payload!);
            desk.Link.SetSection("calibration", state.RootElement.GetProperty("calibration").GetRawText());
        }
        DeskHarness.Pump();
        var card = desk.Main.Calibration.Tab.Cards.Single(c => c.Id == target);
        await card.Fetching;
        DeskHarness.Pump();
        return card;
    }

    [AvaloniaFact]
    public async Task TheCardsLinesAreGreyedWithTheNodesWordsExactlyWhenARealEngineRefusesThem()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.Drawers.Show("health", "calibration");
        DeskHarness.Pump();
        await using var engine = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), desk.Link.CurrentProject);

        // Accept as fallback, Revert to fallback and Save snapshot are live when, and only when, a real engine in the same state runs
        // them; a greyed one's tooltip ends with the words the node refuses it with.
        async Task Agree(ScoreCardViewModel card, params string[] lines)
        {
            foreach (var a in card.Actions.Where(a => lines.Contains(a.Id)))
            {
                await using var fresh = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), engine.Store.Project);
                var ran = await QuadToolTests.Run(fresh, a.Verb);
                Assert.True(ran.Ok == a.IsEnabled, $"{a.Verb} live {a.IsEnabled}, the node: {ran.Payload}");
                if (!ran.Ok)
                    Assert.EndsWith(ran.Payload!, a.Tip, StringComparison.Ordinal);
            }
        }
        string[] all = ["calibration.accept", "calibration.fallback", "calibration.save"];

        // No result yet: there is no fallback to revert to; Accept records the projector as it stands.
        var card = await Mirror(desk, engine, "pj_left");
        Assert.False(card.Actions.Single(a => a.Id == "calibration.fallback").IsEnabled);
        await Agree(card, all);

        // A first solve is current and the fallback at once: neither Accept nor Revert has anything to do.
        Assert.True((await QuadToolTests.Run(engine, "PROJECTOR SOLVE pj_left")).Ok);
        card = await Mirror(desk, engine, "pj_left");
        Assert.False(card.Actions.Single(a => a.Id == "calibration.accept").IsEnabled);
        Assert.False(card.Actions.Single(a => a.Id == "calibration.fallback").IsEnabled);
        await Agree(card, all);

        // Saved: the current result is kept (pinned) already, so Save has nothing to keep.
        Assert.True((await QuadToolTests.Run(engine, "CALIBRATION SAVE pj_left AS before the show")).Ok);
        card = await Mirror(desk, engine, "pj_left");
        Assert.False(card.Actions.Single(a => a.Id == "calibration.save").IsEnabled);
        await Agree(card, "calibration.accept", "calibration.save");

        // Solved again with the focal locked: a new current result, the fallback the first one — all three run.
        Assert.True((await QuadToolTests.Run(engine, "PROJECTOR SOLVE pj_left LOCK FOCAL")).Ok);
        card = await Mirror(desk, engine, "pj_left");
        Assert.All(card.Actions.Where(a => all.Contains(a.Id)), a => Assert.True(a.IsEnabled, a.Tip));
        await Agree(card, all);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AStagedResultsAcceptIsInTheCardsMenuAndAnInspectorAloneReadsTheHistory()
    {
        await using var desk = new DeskHarness();
        StateFixtures.Load(desk.Link, "wall");
        var window = MainWindowTests.Open(desk);
        DeskHarness.Pump();
        var cards = desk.Main.Calibration.Tab.Cards;

        // The staged result's Accept is the card's first line, on its toolbar and in its right-click menu; a card with nothing staged
        // has none.
        var staged = cards.Single(c => c.HasStaged);
        Assert.Same(staged.AcceptStaged, staged.Actions[0]);
        Assert.Same(staged.AcceptStaged, staged.Toolbar[0]);
        Assert.Equal("CALIBRATION ACCEPT cs_fixture1", staged.AcceptStaged.Verb);
        var plain = cards.First(c => !c.HasStaged);
        Assert.DoesNotContain(plain.AcceptStaged, plain.Actions);
        desk.Main.Drawers.Show("health", "calibration");
        DeskHarness.Pump();
        var tabCard = window.GetVisualDescendants().OfType<Views.Map.ScoreCardView>().Single(v => ReferenceEquals(v.DataContext, staged));
        var menu = new ContextMenu();
        DeskMenu.Fill(menu.Items, DeskMenu.GetActions(tabCard.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ScoreCard"))!);
        Assert.Same(staged.AcceptStaged, menu.Items.OfType<MenuItem>().First().Tag);
        desk.Main.Drawers.Open = null;
        DeskHarness.Pump();
        desk.Link.ClearSent();

        // With Health closed, MAP's inspector of a projector holds its card (a real engine's solve of the show's left projector): the
        // card reads its history (CALIBRATION HISTORY), and lets go when another object is selected.
        await using var engine = new EngineCore(EngineServices.ForTests(new PlayMap.Core.Time.ManualClock(5_000_000_000)), desk.Link.CurrentProject);
        Assert.True((await QuadToolTests.Run(engine, "PROJECTOR SOLVE pj_left")).Ok);
        var card = await Mirror(desk, engine, "pj_left");
        Assert.False(card.IsShown);
        Assert.DoesNotContain(desk.Link.SentLines, l => l.StartsWith("CALIBRATION HISTORY", StringComparison.Ordinal));
        desk.Main.Workspace = Workspace.Map;
        desk.Main.Map.Select("projector", "pj_left");
        DeskHarness.Pump();
        Assert.Contains(card, desk.Main.Map.Inspector.Sections);
        Assert.True(card.IsShown);
        await card.Fetching;
        DeskHarness.Pump();
        Assert.Contains("CALIBRATION HISTORY pj_left", desk.Link.SentLines);
        Assert.True(Assert.Single(card.History).Entry.Current);
        Assert.Single(window.GetVisualDescendants().OfType<ActionMenuButton>(), b => b.Name == "HistoryDot" && ReferenceEquals(b.Action, card.History[0].Dot));
        desk.Main.Map.Select("projector", "pj_right");
        DeskHarness.Pump();
        Assert.DoesNotContain(card, desk.Main.Map.Inspector.Sections);
        Assert.False(card.IsShown);
        window.Close();
    }
}
