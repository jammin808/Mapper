using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Modulation;
using PlayMap.Render.Generators;

namespace PlayMap.Engine.Content;

/// <summary>The sting a node fired last: its preset, the leader instant it starts (and that instant on the wall clock) and its
/// length. Kept for STATE; the render thread reads the same sting from <see cref="ContentBoard.Stings"/>.</summary>
internal sealed record StingFired(StingPreset Preset, long AtLeaderNs, long AtUnixMs, int LengthMs)
{
    public long EndLeaderNs => AtLeaderNs + LengthMs * 1_000_000L;

    /// <summary>0 before it starts, 1 once it is over.</summary>
    public double Progress(long leaderNs) => Math.Clamp((leaderNs - AtLeaderNs) / (LengthMs * 1e6), 0, 1);
}

/// <summary>One generator as STATE, INDEX, preflight and the context tell it: the record, where the renderer has it ("ok",
/// "warming", "faulted", "idle") with its progress and words, its time class (live when it listens to this node's sound), its own
/// time, its measured GPU time, what it draws a count of, whether a frame drew it lately (on the air), its warnings, and each
/// modulated number's value at its last draw.</summary>
internal sealed record GeneratorView(Generator Generator, string State, double Progress, TimeClass Time, double TimeSeconds, double GpuMs,
    double GpuMsP95, int Count, bool OnAir, string Words, IReadOnlyList<string> Warnings, IReadOnlyList<(string Name, double Value)> Live)
{
    public string Label => Generator.Name.Length > 0 ? Generator.Name : Generator.Kind;

    public Light Light(double budgetMs) => State switch
    {
        "faulted" => Core.Bus.Light.Red,
        "idle" => Core.Bus.Light.Grey,
        _ when OnAir && GpuMsP95 > 2 * budgetMs => Core.Bus.Light.Red,
        _ when OnAir && GpuMsP95 > budgetMs => Core.Bus.Light.Amber,
        "warming" => Core.Bus.Light.Amber,
        _ => Core.Bus.Light.Green,
    };
}

/// <summary>The engine's side of content: the sting channel the render thread reads, the sting last fired, and the renderer's
/// observations of each generator when the render host has a renderer (its <see cref="ContentFrame"/>). Views are built from the
/// published project and those observations. Any thread.</summary>
internal sealed class ContentBoard
{
    /// <summary>A generator the renderer has not drawn for this many frames is not on the air now.</summary>
    private const long RecentFrames = 30;

    private volatile ContentFrame? _frame;
    private volatile StingFired? _sting;

    /// <summary>The sting channel every generator answers (written on the lane, read by the render thread each frame).</summary>
    public EffectImpulseChannel Stings { get; } = new();

    /// <summary>The render host's content frame (null: no renderer on this node, or its context went).</summary>
    public ContentFrame? Frame => _frame;

    public void Attach(ContentFrame? frame) => _frame = frame;

    /// <summary>The sting last fired (null: none, or cleared).</summary>
    public StingFired? Sting => _sting;

    public void Fire(StingFired sting)
    {
        ArgumentNullException.ThrowIfNull(sting);
        _sting = sting;
        Stings.Fire(sting.Preset.Preset, sting.AtLeaderNs / 1e9, sting.LengthMs / 1000.0);
    }

    public void Clear()
    {
        _sting = null;
        Stings.Clear();
    }

    /// <summary>Every generator of the show as the renderer last observed it.</summary>
    public List<GeneratorView> Views(Project p, long leaderNs)
    {
        ArgumentNullException.ThrowIfNull(p);
        var list = new List<GeneratorView>(p.Generators.Length);
        foreach (var g in p.Generators)
            list.Add(View(g, leaderNs));
        return list;
    }

    public GeneratorView View(Generator g, long leaderNs)
    {
        ArgumentNullException.ThrowIfNull(g);
        var traits = GeneratorTraits.Of(g.Kind);
        var listens = Listens(g, traits);
        var stats = _frame?.Stats;
        var warnings = new List<string>();
        if (GeneratorCatalog.Find(g.Kind) is null)
            return new GeneratorView(g, "faulted", 0, traits.Time, 0, 0, 0, 0, false, $"this node has no generator kind {g.Kind}", warnings, []);
        if (stats is null || !stats.TryGet(g.Id, out var live))
        {
            var own = ContentTime.Seconds(leaderNs, g.OriginNs);
            if (ContentTime.PrecisionLost(g.Kind, own))
                warnings.Add(ContentTime.PrecisionWords);
            // Not drawn: no own time observed (the warning still reads the leader's clock).
            return new GeneratorView(g, "idle", 0, traits.With(listens), 0, 0, 0, 0, false,
                stats is null ? "no renderer on this node" : "not drawn: nothing shows it", warnings, []);
        }
        var onAir = stats.Frame - live.LastFrame <= RecentFrames;
        var state = !onAir ? "idle" : live.State switch
        {
            GeneratorState.Faulted => "faulted",
            GeneratorState.Warming => "warming",
            GeneratorState.Ok => "ok",
            _ => "idle",
        };
        var seconds = live.TimeNs / 1e9;
        if (ContentTime.PrecisionLost(g.Kind, seconds))
            warnings.Add(ContentTime.PrecisionWords);
        var (names, values) = live.Live();
        var bound = new List<(string, double)>(names.Length);
        for (var i = 0; i < names.Length; i++)
            bound.Add((names[i], values[i]));
        var words = state == "idle" ? "not drawn lately: nothing shows it" : live.Words;
        return new GeneratorView(g, state, state == "idle" ? 0 : live.Progress, traits.With(listens), seconds, live.GpuMs, live.GpuMsP95, live.Count,
            onAir && state != "faulted", words, warnings, bound);
    }

    /// <summary>True when a generator reads this node's own sound: its kind listens and its sound is up, or a binding reads a live source.</summary>
    public static bool Listens(Generator g, GeneratorTraits traits)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(traits);
        if (traits.ReadsAudio && GeneratorCatalog.Find(g.Kind) is { } kind && kind.Param("sound") is not null
            && ParamValues.Number(g.Params, kind, "sound") > 0)
            return true;
        foreach (var b in g.Bindings)
            if (b.Enabled && ModSource.TryParse(b.Source, out var s, out _) && s.IsAudio)
                return true;
        return false;
    }

    /// <summary>The content light: red when a faulted generator or one on the air at twice the budget, amber over the budget or
    /// warming, grey when nothing is drawn, else green; the on-air p95 total against the budget.</summary>
    public static (Light Light, double CostMs) Cost(IReadOnlyList<GeneratorView> views, double budgetMs)
    {
        ArgumentNullException.ThrowIfNull(views);
        var cost = views.Where(v => v.OnAir).Sum(v => v.GpuMsP95);
        var light = views.Count == 0 || views.All(v => v.State == "idle") ? Light.Grey : Light.Green;
        foreach (var v in views)
        {
            var l = v.Light(budgetMs);
            if (l == Light.Red || (l == Light.Amber && light != Light.Red))
                light = l;
        }
        if (cost > 2 * budgetMs)
            light = Light.Red;
        else if (cost > budgetMs && light != Light.Red)
            light = Light.Amber;
        return (light, cost);
    }

    public static void WriteGenerator(Utf8JsonWriter w, GeneratorView v, double budgetMs)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(v);
        var g = v.Generator;
        w.WriteStartObject();
        w.WriteString("id", g.Id);
        w.WriteString("kind", g.Kind);
        w.WriteString("name", g.Name);
        w.WriteString("state", v.State);
        w.WriteNumber("progress", Math.Round(v.Progress, 3));
        w.WriteString("time", GeneratorTraits.Word(v.Time));
        var traits = GeneratorTraits.Of(g.Kind);
        if (traits.Time == TimeClass.Bounded && traits.MaxLifeSeconds > 0)
            w.WriteNumber("horizonS", HorizonSeconds(g));
        w.WriteNumber("originNs", g.OriginNs);
        w.WriteNumber("timeS", Math.Round(v.TimeSeconds, 3));
        w.WriteNumber("gpuMs", Math.Round(v.GpuMs, 3));
        w.WriteNumber("gpuMsP95", Math.Round(v.GpuMsP95, 3));
        w.WriteNumber("count", v.Count);
        w.WriteNull("morph");
        w.WriteStartObject("live");
        foreach (var (name, value) in v.Live)
            w.WriteNumber(name, Math.Round(value, 4));
        w.WriteEndObject();
        w.WriteStartObject("inputs");
        foreach (var (name, source) in g.Inputs)
            w.WriteString(name, source.ToString());
        w.WriteEndObject();
        w.WriteBoolean("onAir", v.OnAir);
        w.WriteString("light", v.Light(budgetMs).ToString().ToLowerInvariant());
        w.WriteString("words", v.Words);
        w.WriteStartArray("warnings");
        foreach (var warning in v.Warnings)
            w.WriteStringValue(warning);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>The seconds a bounded generator settles within: its longest life.</summary>
    public static double HorizonSeconds(Generator g)
    {
        ArgumentNullException.ThrowIfNull(g);
        return g.Kind.Equals(ParticleFlowSettings.KindId, StringComparison.OrdinalIgnoreCase)
            ? ParticleFlowSettings.Resolve(g.Params).LifeMaxSeconds
            : GeneratorTraits.Of(g.Kind).MaxLifeSeconds;
    }

    public static string Ms(double ms) => ms.ToString("0.0", CultureInfo.InvariantCulture);
}
