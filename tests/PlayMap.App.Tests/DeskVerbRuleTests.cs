using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PlayMap.App.Controls;
using PlayMap.App.ViewModels;
using PlayMap.Core.Wire;

namespace PlayMap.App.Tests;

/// <summary>The desk has no private API: with every package's STATE fixture loaded, every action reachable from the top
/// bar, the drawers, the inspectors and every object's right-click menu (its own actions and the modules' entries) sends a
/// line the catalogue parses with every word its row requires, and no line carries a secret. Every toolbar is a subset of its object's menu, and every
/// entry a module appends to a menu is also on that object's inspector toolbar.</summary>
public class DeskVerbRuleTests(ITestOutputHelper output)
{
    /// <summary>Every DeskAction reachable from a view model graph (public properties, lists, sub-actions), and every
    /// object the graph names that a menu can be about.</summary>
    internal static (List<DeskAction> Actions, List<IMenuTarget> Targets, List<object> Objects) Walk(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var actions = new List<DeskAction>();
        var targets = new List<IMenuTarget>();
        var objects = new List<object>();
        var queue = new Queue<object>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var o = queue.Dequeue();
            if (!seen.Add(o))
                continue;
            if (o is DeskAction a)
            {
                actions.Add(a);
                foreach (var c in a.Children)
                    queue.Enqueue(c);
                continue;
            }
            if (o is IMenuTarget t)
                targets.Add(t);
            objects.Add(o);
            foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || !Interesting(p.PropertyType))
                    continue;
                var value = p.GetValue(o);
                if (value is null or string)
                    continue;
                if (value is IEnumerable list)
                {
                    foreach (var item in list)
                    {
                        if (item is not null && Interesting(item.GetType()))
                            queue.Enqueue(item);
                    }
                }
                else if (Interesting(value.GetType()))
                    queue.Enqueue(value);
            }
        }
        return (actions, targets, objects);
    }

    /// <summary>The desk's own view-model types (and lists of them); the session, the link and the mirrors are not walked.</summary>
    private static bool Interesting(Type type)
    {
        if (type == typeof(Link.DeskSession) || typeof(PlayMap.Core.Wire.IEngineLink).IsAssignableFrom(type) || type == typeof(string))
            return false;
        if (type.Namespace?.StartsWith("PlayMap.App.ViewModels", StringComparison.Ordinal) == true)
            return true;
        if (type == typeof(object))
            return true;
        if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
            return type.GetGenericArguments().Any(Interesting);
        return type.IsArray && Interesting(type.GetElementType()!);
    }

    /// <summary>A line is the catalogue's: it parses, a row matches, it holds every word the row requires, and it carries no secret word.</summary>
    internal static string? Fault(string line)
    {
        if (!WireGrammar.TryParse(line, out var parsed, out var error))
            return "does not parse: " + error?.Reason;
        if (VerbCatalog.Match(parsed) is not { } m)
            return "no catalogue row";
        if (!VerbSyntax.Fits(m.Spec, parsed, m.Words, out var lacks))
            return "does not fit " + m.Spec.Name + ": " + lacks;
        if (m.Spec.SecretFrom is int from)
        {
            // A word in the secret position is a secret unless it is one of the syntax's own keywords (LINK PATTERNS
            // FOLLOW-BLACKOUT ON names no token).
            var keywords = System.Text.RegularExpressions.Regex.Matches(m.Spec.Syntax, "[A-Z][A-Z0-9-]*").Select(x => x.Value).ToHashSet(StringComparer.Ordinal);
            for (var i = from; i < parsed.Count; i++)
            {
                if (!keywords.Contains(parsed.Word(i)))
                    return "carries a secret (" + m.Spec.Name + ")";
            }
        }
        return null;
    }

    /// <summary>Every action the desk offers with STATE as the link holds it: from the top bar, the drawers, the inspectors and every
    /// object's right-click menu (its own actions and the modules' entries), over every workspace, every object of the outliner selected,
    /// every media and playlist item selected and every drawer tab shown, and every module entry for every object the desk names — with
    /// the Oops panel open on the show's undo steps (each step's Undo to here) and a quad's draft held (its chip's Resume and Discard).
    /// On the way, every module entry for an inspected object is on its inspector's toolbar and every control's toolbar is in its menu;
    /// at the end the desk is walked once more with Map anything on, and every line a touch would learn (BIND LEARN &lt;line&gt;) is a
    /// catalogue line.</summary>
    internal static async Task<(List<DeskAction> Actions, List<IMenuTarget> Targets, List<object> Objects)> ReachAsync(DeskHarness desk, Window window)
    {
        var all = new List<DeskAction>();
        var targets = new List<IMenuTarget>();
        var objects = new List<object>();

        void Collect()
        {
            DeskHarness.Pump();
            // Every module entry for an object being inspected is on that inspector's toolbar: the same instances.
            foreach (var host in new[] { desk.Main.Play.Inspector, desk.Main.Map.Inspector, desk.Main.Outputs.Inspector })
            {
                if (host.Current is { } current)
                    Assert.Equal(desk.Main.ExtrasFor(current), host.Extras);
            }
            var (a, t, o) = Walk(desk.Main);
            all.AddRange(a);
            targets.AddRange(t);
            objects.AddRange(o);
            foreach (var control in window.GetVisualDescendants().OfType<Control>())
            {
                if (DeskMenu.GetActions(control) is { } own)
                {
                    var menu = new ContextMenu();
                    var extras = DeskMenu.ExtrasOf(control);
                    DeskMenu.Fill(menu.Items, own, extras);
                    var items = Items(menu.Items).ToList();
                    all.AddRange(items);
                    // The object's toolbar is in its menu.
                    if (control.DataContext?.GetType().GetProperty("Toolbar")?.GetValue(control.DataContext) is IEnumerable<DeskAction> toolbar)
                    {
                        foreach (var b in toolbar.Where(x => x.IsAvailable))
                            Assert.Contains(b, items);
                    }
                }
            }
        }

        // An undo step on the node (the show disarmed for the edit, and armed again as it was), and the Oops panel open on it: each step's
        // Undo to here is reached.
        var armed = desk.Main.ShowModule.Strip.IsArmed;
        if (armed)
            Assert.True((await desk.Session.SendAsync("SHOW DISARM", Ct)).Ok);
        Assert.True((await desk.Session.SendAsync("SET pj_left name \"Left reached\"", Ct)).Ok);
        if (armed)
            Assert.True((await desk.Session.SendAsync("SHOW ARM", Ct)).Ok);
        DeskHarness.Pump();
        await desk.Main.Ux.Oops.Toggle.Command.ExecuteAsync();
        await desk.Main.Ux.Oops.Fetching;
        DeskHarness.Pump();
        Assert.NotEmpty(desk.Main.Ux.Oops.Steps);
        // A quad's draft held on a projector: its chip's entries are reached.
        desk.Main.Workspace = Workspace.Map;
        desk.Main.Map.Select("projector", "pj_left");
        desk.Main.Map.IsProjectorView = true;
        desk.Main.Map.Tool = MapTool.Quad;
        desk.Main.Map.QuadClick(new PlayMap.Core.Numerics.Vec2d(0.3, 0.3));
        desk.Main.Map.Tool = MapTool.Select;
        DeskHarness.Pump();
        Assert.NotEmpty(desk.Main.Map.DraftChips);

        foreach (var ws in new[] { Workspace.Play, Workspace.Map, Workspace.Scan })
        {
            desk.Main.Workspace = ws;
            Collect();
        }
        foreach (var node in desk.Main.Map.Outliner.SelectMany(g => g.Nodes).ToList())
        {
            desk.Main.Workspace = Workspace.Map;
            desk.Main.Map.Select(node.Kind, node.Id);
            Collect();
        }
        desk.Main.Workspace = Workspace.Play;
        foreach (var media in desk.Main.Play.Media.ToList())
        {
            desk.Main.Play.SelectedMedia = media;
            Collect();
        }
        foreach (var pl in desk.Main.Play.Playlists.ToList())
        {
            foreach (var item in pl.Items.ToList())
            {
                pl.SelectedItem = item;
                Collect();
            }
        }
        foreach (var d in desk.Main.Drawers.Drawers)
        {
            foreach (var tab in d.Tabs)
            {
                desk.Main.Drawers.Show(d.Key, tab.Key);
                Collect();
            }
        }

        // Every module entry for every object the desk names.
        foreach (var t in targets.DistinctBy(t => t.Id))
            all.AddRange(desk.Main.ExtrasFor(t).SelectMany(x => x.Children.Prepend(x)));

        // Once more with Map anything on (the show disarmed: armed, a learn is refused and the mode ends): every line a touch would learn
        // is BIND LEARN of a catalogue line, itself a catalogue line.
        if (armed)
        {
            Assert.True((await desk.Session.SendAsync("SHOW DISARM", Ct)).Ok);
            DeskHarness.Pump();
        }
        desk.Session.MapAnything = true;
        desk.Main.Workspace = Workspace.Play;
        Collect();
        var learnt = all.Distinct().Where(a => a.IsBindable).Select(a => DeskAction.LearnLine(a.Verb)).Distinct().ToList();
        Assert.NotEmpty(learnt);
        Assert.All(learnt, line => Assert.True(Fault(line) is null, line + ": " + Fault(line)));
        desk.Session.MapAnything = false;
        if (armed)
            Assert.True((await desk.Session.SendAsync("SHOW ARM", Ct)).Ok);
        // The panel and the draft go, so what follows the walk meets the desk as it was.
        await desk.Main.Ux.Oops.Fetching;
        await desk.Main.Ux.Oops.Toggle.Command.ExecuteAsync();
        desk.Main.Map.DiscardDraft("pj_left");
        DeskHarness.Pump();
        Assert.False(desk.Main.Ux.Oops.IsOpen);
        return (all, targets, objects);
    }

    [AvaloniaFact]
    public async Task EveryReachableActionSendsACatalogueLineAndEveryToolbarIsInItsMenu()
    {
        await using var desk = new DeskHarness();
        StateFixtures.LoadAll(desk.Link);
        var window = MainWindowTests.Open(desk);
        DeskHarness.Pump();
        var (all, targets, objects) = await ReachAsync(desk, window);

        var lines = all.Distinct().Select(a => (a, line: a.Verb)).Where(x => x.line.Length > 0).ToList();
        Assert.NotEmpty(lines);
        var faults = lines.Select(x => (x.a, x.line, why: Fault(x.line))).Where(x => x.why is not null).ToList();
        foreach (var f in faults)
            output.WriteLine($"{f.a.Id} '{f.a.Label}': {f.line} — {f.why}");
        Assert.Empty(faults);
        output.WriteLine($"{lines.Count} action lines checked over {targets.DistinctBy(t => t.Id).Count()} objects");

        // Toolbars inside the view models are subsets of their menus.
        foreach (var o in objects)
        {
            if (o.GetType().GetProperty("Toolbar")?.GetValue(o) is IEnumerable<DeskAction> toolbar
                && o.GetType().GetProperty("Actions")?.GetValue(o) is IEnumerable<DeskAction> menu)
                Assert.Subset(menu.ToHashSet(), toolbar.ToHashSet());
        }
        window.Close();
    }

    private static IEnumerable<DeskAction> Items(ItemCollection items)
    {
        foreach (var i in items.OfType<MenuItem>())
        {
            if (i.Tag is DeskAction a)
                yield return a;
            foreach (var c in Items(i.Items))
                yield return c;
        }
    }

    [Fact]
    public void TheRuleFindsALineThatIsNotTheCataloguesOrCarriesASecret()
    {
        Assert.Null(Fault("SHOW ARM"));
        Assert.NotNull(Fault("FROBNICATE ALL"));
        Assert.NotNull(Fault("LINK PATTERNS 10.0.0.2 s3cr3t"));
        Assert.Null(Fault("LINK PATTERNS OFF"));
        Assert.Null(Fault("LINK PATTERNS FOLLOW-BLACKOUT ON"));
        Assert.NotNull(Fault("CREDENTIAL SET dv_1 admin hunter2"));
    }
}
