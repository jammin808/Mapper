using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.App.Link;
using PlayMap.App.State;
using PlayMap.Core.Content;
using PlayMap.Core.Model;

namespace PlayMap.App.ViewModels.Generated;

/// <summary>The entries every object's menu gains from the round-2 packages, generated from what the node says it can do:
/// Add effect ▸ (FX KINDS) on outputs, surfaces, layers and canvases; Modulate ▸ (a generator's numbers × MOD SOURCES);
/// Bind ▸ (BIND LEARN for each of the object's own actions); Optimise ▸ on media; Use input ▸ (STATE.videoio.inputs) on
/// surfaces. Each is a catalogue verb line; an entry whose verb the node does not run hides. The same instances sit on the
/// object's inspector toolbar, so the menu and the toolbar never disagree. The lists are asked quietly once the node's
/// verbs are known, and the entries follow them.</summary>
public sealed class ObjectMenusModule : DeskModule
{
    /// <summary>The sources Modulate ▸ offers first (the beat clock and an LFO: pure, the same on every node); the node's
    /// own list follows when it answers MOD SOURCES.</summary>
    private static readonly ImmutableArray<(string Source, string Words)> PureSources =
        [("beat", "the beat's phase"), ("bar", "the bar's phase"), ("beat:pulse", "a pulse on each beat"), ("lfo:sine/4", "a sine every 4 s")];

    private static readonly ImmutableArray<(string Target, string Words)> OptimiseTargets =
        [("hap", "HAP"), ("hap-alpha", "HAP Alpha"), ("hap-q", "HAP Q"), ("hap-q-alpha", "HAP Q Alpha"), ("rechunk", "Rechunk for the GPU")];

    private readonly DeskSession _session;
    private readonly Dictionary<string, List<DeskAction>> _entries = new(StringComparer.Ordinal);
    private ImmutableArray<(string Id, string Name)> _fxKinds = [];
    private ImmutableArray<(string Source, string Words)> _sources = PureSources;
    private ImmutableArray<(string Id, string Name)> _inputs = [];
    private Project? _project;
    private bool _asked;
    private bool _learning;

    public ObjectMenusModule(DeskSession session)
    {
        _session = session;
        session.VerbsUpdated += () =>
        {
            _asked = false;
            _ = AskAsync();
        };
    }

    /// <summary>The effect kinds the node answered FX KINDS with.</summary>
    public ImmutableArray<(string Id, string Name)> FxKinds => _fxKinds;

    public override void OnProject(Project project)
    {
        _project = project;
        // An object's entries are made from the project (a generator's numbers): made again on the next ask.
        Rebuild();
    }

    public override void OnState(StateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!_asked)
            _ = AskAsync();
        _learning = state.Root.ValueKind == JsonValueKind.Object && state.Root.TryGetProperty("control", out var control)
            && control.ValueKind == JsonValueKind.Object && control.TryGetProperty("learning", out var learning) && learning.ValueKind == JsonValueKind.Object;
        var inputs = state.Section("videoio", ReadInputs);
        if (!inputs.SequenceEqual(_inputs))
        {
            _inputs = inputs;
            Rebuild();
        }
    }

    private static ImmutableArray<(string Id, string Name)> ReadInputs(JsonElement? videoio)
    {
        if (videoio is not { ValueKind: JsonValueKind.Object } v || !v.TryGetProperty("inputs", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        return [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(e => (e.GetProperty("id").GetString()!, e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : e.GetProperty("id").GetString()!))];
    }

    /// <summary>Asks the node for its effect kinds and its modulation sources (quietly: a node without them answers ERR,
    /// and the entries stay empty, so they hide).</summary>
    private async Task AskAsync()
    {
        if (!_session.IsLive || _asked)
            return;
        _asked = true;
        ImmutableArray<(string Id, string Name)>? kinds = null;
        ImmutableArray<(string Source, string Words)>? sources = null;
        if (_session.Knows("FX KINDS"))
        {
            var reply = await _session.SendQuietAsync("FX KINDS");
            if (reply.Ok && TryArray(reply.Text, out var k))
                kinds = [.. k.Where(e => e.TryGetProperty("id", out _)).Select(e => (e.GetProperty("id").GetString() ?? "", e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))
                    .Where(x => x.Item1.Length > 0)];
        }
        if (_session.Knows("MOD SOURCES"))
        {
            var reply = await _session.SendQuietAsync("MOD SOURCES");
            if (reply.Ok && TryArray(reply.Text, out var s))
            {
                var named = s.Where(e => e.TryGetProperty("source", out _)).Select(e => (e.GetProperty("source").GetString() ?? "", e.TryGetProperty("words", out var w) ? w.GetString() ?? "" : ""))
                    .Where(x => x.Item1.Length > 0 && !x.Item1.StartsWith("audio:fft", StringComparison.Ordinal) && !x.Item1.StartsWith("audio:band", StringComparison.Ordinal));
                sources = [.. PureSources.Concat(named).DistinctBy(x => x.Item1)];
            }
        }
        // The replies may land on a worker: the lists change on the UI thread only, and the window re-reads its entries.
        _session.OnUi(() =>
        {
            if (kinds is { } kk)
                _fxKinds = kk;
            if (sources is { } ss)
                _sources = ss;
            Rebuild();
        });
    }

    private static bool TryArray(string text, out List<JsonElement> items)
    {
        items = [];
        if (!text.TrimStart().StartsWith('['))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            items = [.. doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone())];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Raised on the UI thread when the generated entries were made again (the window re-reads its toolbars).</summary>
    public event Action? EntriesChanged;

    /// <summary>The lists moved: every object's entries are made again on their next ask (menus rebuild as they open).</summary>
    private void Rebuild()
    {
        _entries.Clear();
        EntriesChanged?.Invoke();
    }

    public override IEnumerable<DeskAction> MenuExtras(IMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var key = target.Id + "|" + target.Kind;
        if (!_entries.TryGetValue(key, out var list))
        {
            list = Build(target);
            _entries[key] = list;
        }
        return list;
    }

    private List<DeskAction> Build(IMenuTarget target)
    {
        var id = target.Id;
        var list = new List<DeskAction>();
        if (target.Kind is "output" or "surface" or "layer" or "canvas" && _fxKinds.Length > 0)
        {
            var fx = Menu("gen.fx", "Add effect", "fx");
            foreach (var (fxKind, name) in _fxKinds)
                fx.Children.Add(DeskAction.Send(_session, "gen.fx." + fxKind, name.Length > 0 ? name : fxKind, () => $"FX ADD {id} {fxKind}"));
            list.Add(fx);
        }
        if (target.Kind == "generator" && _project?.FindGenerator(id) is { } gen && GeneratorCatalog.Find(gen.Kind) is { } kind
            && kind.Params.Any(p => p.Type is ParamType.Number or ParamType.Integer))
        {
            var mod = Menu("gen.mod", "Modulate", "modulation");
            foreach (var p in kind.Params.Where(p => p.Type is ParamType.Number or ParamType.Integer))
            {
                var param = Menu("gen.mod." + p.Name, Words(p.Name), "modulation");
                foreach (var (source, words) in _sources)
                    param.Children.Add(DeskAction.Send(_session, "gen.mod." + p.Name + "." + source, source + (words.Length > 0 ? " — " + words : ""),
                        () => $"MOD BIND {id} {p.Name} {source}"));
                mod.Children.Add(param);
            }
            mod.Children.Add(DeskAction.Send(_session, "gen.mod.hold", "Hold its numbers", () => $"MOD BYPASS TOGGLE {id}").Look(separator: true));
            mod.Children.Add(DeskAction.Send(_session, "gen.mod.none", "Unbind every number", () => $"MOD UNBIND {id} ALL"));
            list.Add(mod);
        }
        if (target.Kind == "media")
        {
            var opt = Menu("gen.optimise", "Optimise", "optimise");
            foreach (var (to, words) in OptimiseTargets)
                opt.Children.Add(DeskAction.Send(_session, "gen.optimise." + to, words, () => $"MEDIA OPTIMISE {id} TO {to}"));
            opt.Children.Add(DeskAction.Send(_session, "gen.optimise.estimate", "Estimate", () => $"MEDIA ESTIMATE {id}").Look(separator: true));
            opt.Children.Add(DeskAction.Send(_session, "gen.optimise.versions", "Versions", () => $"MEDIA VERSIONS {id}"));
            list.Add(opt);
        }
        if (target.Kind == "surface" && _inputs.Length > 0)
        {
            var use = Menu("gen.input", "Use input", "live");
            foreach (var (input, name) in _inputs)
                use.Children.Add(DeskAction.Send(_session, "gen.input." + input, name, () => $"SURFACE SOURCE {id} live:{input}"));
            list.Add(use);
        }
        var own = target.MenuActions.Where(a => !a.HasChildren && a.Verb.Length > 0 && !a.Verb.StartsWith("BIND ", StringComparison.Ordinal)).ToList();
        if (own.Count > 0)
        {
            var bind = Menu("gen.bind", "Bind", "bind");
            foreach (var a in own)
                bind.Children.Add(DeskAction.Send(_session, "gen.bind." + a.Id, a.Label, () => a.Verb.Length > 0 ? "BIND LEARN " + a.Verb : null));
            bind.Children.Add(DeskAction.Send(_session, "gen.bind.off", "Stop learning", () => "BIND LEARN OFF").Look(separator: true));
            list.Add(bind);
        }
        return list;
    }

    /// <summary>Esc while a learn waits (STATE.control.learning): BIND LEARN OFF, before the overlays and PANIC.</summary>
    public override bool TryEscape()
    {
        if (!_learning)
            return false;
        _session.Fire("BIND LEARN OFF");
        return true;
    }

    public override int EscapeOrder => Infrastructure.EscapeLadder.Learn;

    private static DeskAction Menu(string id, string label, string icon) => DeskAction.Local(id, label, () => { }).Look(icon, menuOnly: true);

    private static string Words(string name) => ParamEditor.Words(name);
}
