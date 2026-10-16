using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Audio;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Modulation;
using PlayMap.Core.Time;
using PlayMap.Engine.Audio;

namespace PlayMap.Engine;

/// <summary>wp2-audio's hooks into the engine (EngineCore.Modules.cs): the AUDIO, TEMPO and MOD verbs, the lane's tick (the listening
/// input kept to what the show asks, the beat clock kept to the show's tempo, STATE's facts refreshed), STATE.audio and
/// STATE.modulation, the context's audio and tempo items, and the check of bindings an edit writes.</summary>
public sealed partial class EngineCore
{
    private AudioRuntime? _audio;
    private readonly AnalysisFrame _audioScratch = new();
    private readonly Lock _planGate = new();
    private ModulationPlan _lanePlan = ModulationPlan.Empty;
    private volatile TempoFacts? _tempoFacts;
    private volatile bool _tempoInUse;
    private volatile bool _modBypassAll;
    private volatile ImmutableHashSet<string> _modBypassed = ImmutableHashSet<string>.Empty;
    private Task? _listening;

    /// <summary>The node's sound and beat (the host's, or the engine's own with the test inputs only).</summary>
    internal AudioRuntime AudioRt => _audio!;

    /// <summary>MOD BYPASS ON: every modulated number at its base.</summary>
    internal bool ModBypassAll => _modBypassAll;

    /// <summary>Entities held at their base (MOD BYPASS ON &lt;entity&gt;).</summary>
    internal ImmutableHashSet<string> ModBypassed => _modBypassed;

    partial void RouteAudio(VerbRoutes routes)
    {
        _audio = Services.AudioRuntime
            ?? new AudioRuntime(Services.Mono, Services.Clock, paced: false, Services.Settings.Audio)
                .AddBackend(PlayMap.Media.Audio.FileAudioInputs.Prefix, new PlayMap.Media.Audio.FileAudioInputs());
        _audio.FxRanges ??= new EffectRanges(this);
        _audio.Tempo = TempoTimeline.Start(Store.Project.Tempo.Bpm, Store.Project.Tempo.BeatsPerBar, Store.Project.Tempo.BarsPerPhrase);
        RouteTempo(routes);
        RouteMod(routes);
        RouteListen(routes);
        routes.Index(new TempoIndex(this));
    }

    partial void TickAudio(long leaderNs)
    {
        var rt = AudioRt;
        if (!rt.Paced)
            rt.Synthetic.Pump();
        var changed = KeepTempo(leaderNs);
        if (DetectTempo(leaderNs))
            changed = true;
        if (rt.Observe(Services.Mono.NowNs, leaderNs, _audioScratch))
            changed = true;
        KeepListening();
        if (PublishTempo(leaderNs))
            changed = true;
        if (changed)
            FactsChanged();
    }

    /// <summary>The audio part of the lane's tick, run now (on the lane): a test on a hand-moved clock runs it at its own instants.</summary>
    internal void RunAudioTick() => TickAudio(LeaderNowNs);

    /// <summary>STATE's audio facts and beat taken now, whatever moved (on the lane): what a test reads at a fixed instant.</summary>
    internal void RefreshAudioFacts()
    {
        AudioRt.Observe(Services.Mono.NowNs, LeaderNowNs, _audioScratch, force: true);
        _tempoFacts = null;
        PublishTempo(LeaderNowNs);
        FactsChanged();
    }

    /// <summary>The beat clock follows the show's tempo and meter when they change without a TEMPO verb (an undo, a loaded show, a
    /// generic SET): the change is scheduled one lead ahead, like any other.</summary>
    private bool KeepTempo(long leaderNs)
    {
        var rt = AudioRt;
        var t = Store.Project.Tempo;
        var clock = rt.Tempo.Kept(leaderNs);
        // Detected, the tempo is the sound's (the show's tempo is the manual one).
        if (t.Source != "detect" && Math.Abs(clock.Bpm - t.Bpm) > 1e-9)
            clock = clock.WithTempo(leaderNs + TempoTimeline.LeadNs, t.Bpm, "set");
        if (clock.BeatsPerBar != t.BeatsPerBar || clock.BarsPerPhrase != t.BarsPerPhrase)
            clock = clock.WithMeter(t.BeatsPerBar, t.BarsPerPhrase);
        var moved = !ReferenceEquals(clock, rt.Tempo) && !clock.Equals(rt.Tempo);
        rt.Tempo = clock;
        return moved;
    }

    /// <summary>What the node listens to: the show's input, or this machine's own (AUDIO LISTEN … HERE).</summary>
    internal ListenSpec ListenSpecNow()
    {
        var a = Store.Project.Audio;
        var node = AudioRt.Node;
        return new ListenSpec(node.Input.Length > 0 ? node.Input : a.Input, node.Channels.Length > 0 ? node.Channels : a.Channels,
            a.GainDb, a.AutoGain, a.GateDb, node.InputLatencyMs);
    }

    /// <summary>Opens, closes or retries the input when what the show asks differs from what is open (one open at a time, off the
    /// lane: closing a device joins its reader, and resolving a name lists every backend's devices, so none of it runs on the lane's
    /// thread).</summary>
    private void KeepListening()
    {
        var spec = ListenSpecNow();
        if (_listening is { IsCompleted: false } || !AudioRt.Due(spec, Services.Mono.NowNs))
            return;
        _listening = Task.Run(() => ListenNowAsync(spec), CancellationToken.None);
    }

    private async Task ListenNowAsync(ListenSpec spec)
    {
        var before = AudioRt.State;
        ListenState after;
        try
        {
            after = await AudioRt.ListenAsync(spec, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // The node is closing: nothing to listen to.
        }
        if (after != before)
        {
            Journal.Add("audio", after.State == "listening" ? $"listening to {after.Name}" : $"audio input {after.State}: {after.Words}",
                ok: after.State is "listening" or "off");
            FactsChanged();
        }
    }

    /// <summary>The bindings of the published project, resolved once per project (the lane's and STATE's view).</summary>
    internal ModulationPlan LanePlan()
    {
        var p = Store.Project;
        lock (_planGate)
        {
            if (!ReferenceEquals(_lanePlan.Project, p))
                _lanePlan = ModulationPlan.For(p, AudioRt.FxRanges);
            return _lanePlan;
        }
    }

    partial void StateAudio(Utf8JsonWriter w)
    {
        var rt = AudioRt;
        var p = Store.Project;
        var facts = rt.Facts;
        var state = rt.State;
        var tempo = _tempoFacts;
        w.WriteStartObject("audio");
        w.WriteNumber("level", Round(facts.Level));
        w.WriteNumber("bpm", Math.Round(rt.Tempo.Bpm, 3));
        WriteNullable(w, "beat", tempo?.BeatInBar);
        WriteNullable(w, "bar", tempo?.Bar);
        WriteNullable(w, "phrase", tempo?.Phrase);
        if (tempo is null)
            w.WriteNull("phase");
        else
            w.WriteNumber("phase", Math.Round(tempo.Phase, 3));
        w.WriteNumber("bass", Round(facts.Bass));
        w.WriteNumber("mid", Round(facts.Mid));
        w.WriteNumber("high", Round(facts.High));
        w.WriteNumber("peakDb", Math.Round(facts.PeakDb, 1));
        w.WriteBoolean("silent", facts.Silent);
        w.WriteStartObject("input");
        w.WriteString("key", state.Key);
        w.WriteString("name", state.Name);
        w.WriteString("channels", state.Channels.Length > 0 ? state.Channels : ListenSpecNow().Channels);
        w.WriteNumber("rateHz", state.RateHz);
        w.WriteString("state", state.State);
        w.WriteString("words", state.Words);
        w.WriteString("backend", state.Backend);
        w.WriteString("asked", ListenSpecNow().Input);
        w.WriteBoolean("here", rt.Node.Input.Length > 0);
        w.WriteNumber("silentForS", Math.Round(facts.SilentForS, 1));
        w.WriteEndObject();
        w.WriteStartObject("tempo");
        w.WriteString("source", p.Tempo.Source);
        w.WriteString("lock", TempoLockNow);
        w.WriteNumber("confidence", p.Tempo.Source == "manual" ? 1 : TempoConfidenceNow);
        w.WriteNumber("bpm", Math.Round(rt.Tempo.Bpm, 3));
        w.WriteNumber("beatsPerBar", rt.Tempo.BeatsPerBar);
        w.WriteNumber("barsPerPhrase", rt.Tempo.BarsPerPhrase);
        w.WriteBoolean("inUse", _tempoInUse);
        if (!_tempoInUse)
        {
            w.WriteNull("epochUnixMs");
            w.WriteNull("anchor");
        }
        else
        {
            w.WriteNumber("epochUnixMs", Services.Wall.UnixMs - LeaderNowNs / Durations.NsPerMs);
            var a = rt.Tempo.At(LeaderNowNs);
            w.WriteStartObject("anchor");
            w.WriteNumber("leaderNs", a.LeaderNs);
            w.WriteNumber("beat", Math.Round(a.Beat, 9));
            w.WriteNumber("bpm", Math.Round(a.Bpm, 6));
            w.WriteNumber("seq", a.Seq);
            w.WriteString("source", a.Source);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteStartObject("latencyMs");
        var input = rt.Node.InputLatencyMs;
        const double analysis = AudioAnalyser.Window / 2.0 * 1000 / AudioAnalyser.SampleRate;
        var acoustic = p.Audio.ListenerDistanceM > 0 ? p.Audio.ListenerDistanceM / 343 * 1000 : 0;
        w.WriteNumber("input", input >= 0 ? Math.Round(input, 1) : -1);
        w.WriteNumber("analysis", Math.Round(analysis, 1));
        w.WriteNumber("total", input >= 0 ? Math.Round(input + analysis, 1) : -1);
        w.WriteNumber("acoustic", Math.Round(acoustic, 1));
        w.WriteEndObject();
        w.WriteEndObject();

        var plan = LanePlan();
        var live = 0;
        var pure = 0;
        foreach (var item in plan.Items.Where(i => i.Source is not null))
        {
            if (item.Source!.Determinism == SourceDeterminism.Live)
                live++;
            else
                pure++;
        }
        w.WriteStartObject("modulation");
        w.WriteNumber("bindings", plan.Items.Length);
        w.WriteBoolean("bypassed", _modBypassAll);
        w.WriteNumber("live", live);
        w.WriteNumber("shared", 0);
        w.WriteNumber("pure", pure);
        w.WriteStartArray("unbound");
        foreach (var words in Unbound(plan))
            w.WriteStringValue(words);
        w.WriteEndArray();
        w.WriteStartArray("held");
        foreach (var id in _modBypassed.Order(StringComparer.Ordinal))
            w.WriteStringValue(id);
        w.WriteEndArray();
        w.WriteNumber("flashLimited", -1);
        w.WriteEndObject();
    }

    /// <summary>Each binding that holds its base value on the air now, in words ("audio:bass (no audio on this node)").</summary>
    internal IReadOnlyList<string> Unbound(ModulationPlan plan)
    {
        var listening = AudioRt.Feed.Listening;
        var list = new List<string>();
        foreach (var item in plan.Items)
        {
            if (item.Problem.Length > 0)
                list.Add($"{item.Entity.Name} {item.Binding.Param}: {item.Problem}");
            else if (DrawnAtBase(plan.Project, item.Entity, item.Param))
                list.Add($"{item.Entity.Name} {item.Binding.Param}: {GeneratorDrawsBase}");
            else if (item.Source is { IsAudio: true } && !listening)
                list.Add($"{item.Binding.Source} (no audio on this node)");
        }
        return [.. list.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Why a generator's bound number is drawn at its base: its renderer evaluates its own bindings, and this kind's renderer
    /// reads its numbers once per change, not every frame.</summary>
    internal const string GeneratorDrawsBase = "drawn at its base: a generator reads its bindings on its own render path, and its kind reads this number once per change";

    private static double Round(float v) => Math.Round(v, 3);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static void WriteNullable(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v)
            w.WriteNumber(name, v);
        else
            w.WriteNull(name);
    }

    partial void ContextAudio(List<ContextItem> items, List<ContextLink> links)
    {
        var rt = AudioRt;
        var state = rt.State;
        var facts = rt.Facts;
        var plan = LanePlan();
        var dependsOnSound = plan.Items.Any(i => i.Source is { IsAudio: true });
        var (light, words) = state.State switch
        {
            "listening" when facts.Silent && facts.SilentForS > 10 => (Light.Amber, Inv($"{state.Name} · silent {facts.SilentForS:0} s")),
            "listening" => (Light.Green, Inv($"{state.Name} · level {facts.Level:0.00}")),
            "opening" => (Light.Amber, $"opening {state.Input}"),
            "retrying" => (dependsOnSound ? Light.Red : Light.Amber, $"{state.Input}: {state.Words}"),
            _ => (dependsOnSound ? Light.Red : Light.Grey, dependsOnSound ? "no input while bindings listen" : "not listening"),
        };
        items.Add(new ContextItem("audio", "audio", "Audio", light, words));
        var tempo = rt.Tempo;
        var tempoWords = Inv($"{tempo.Bpm:0.#} BPM · {tempo.BeatsPerBar} beats a bar · {Store.Project.Tempo.Source}");
        items.Add(new ContextItem("tempo", "tempo", "Tempo", TempoLight, tempoWords + (TempoLockNow is "manual" or "locked" ? "" : " · " + TempoLockNow)));
    }

    /// <summary>The beat clock's light, as the context and INDEX show it: amber searching or holding, red lost, green in use, grey
    /// before anything uses it.</summary>
    private Light TempoLight => TempoLockNow switch
    {
        "searching" or "holding" => Light.Amber,
        "lost" => Light.Red,
        _ => _tempoInUse ? Light.Green : Light.Grey,
    };

    /// <summary><see cref="TempoLight"/> as INDEX writes a light.</summary>
    internal string TempoLightNow => TempoLight.ToString().ToLowerInvariant();

    partial void ValidateAudio(Project p, object entity, ref string? why)
    {
        if (why is not null)
            return;
        var bindings = entity switch
        {
            Generator g => g.Bindings,
            Layer l => l.Bindings,
            Surface s => s.Bindings,
            FxSlot f => f.Bindings,
            _ => [],
        };
        foreach (var b in bindings)
        {
            if (!ModSource.TryParse(ModTargets.SourceKey(b), out _, out var sourceWhy))
            {
                why = $"binding {b.Param}: {sourceWhy}";
                return;
            }
            if (BindingParamProblem(entity, b.Param) is { } problem)
            {
                why = $"binding {b.Param}: {problem}";
                return;
            }
            if (b.Mode == BindingMode.Rate)
            {
                why = $"binding {b.Param}: RATE is not evaluated on this node (ADD, SCALE or REPLACE)";
                return;
            }
        }
        if (bindings.Select(b => b.Param.ToLowerInvariant()).Distinct().Count() != bindings.Length)
            why = "one binding per parameter";
    }

    /// <summary>Why a parameter of an entity (as an edit writes it) takes no binding, or null.</summary>
    private string? BindingParamProblem(object entity, string param)
    {
        switch (entity)
        {
            case Generator g:
                var kind = PlayMap.Core.Content.GeneratorCatalog.Find(g.Kind);
                var spec = kind?.Param(param);
                return spec is { Type: PlayMap.Core.Content.ParamType.Number } ? null : $"{param} is not a number of {g.Kind}";
            case Layer:
                return ModTargets.LayerParams.Contains(param.ToLowerInvariant()) ? null : $"a layer modulates {string.Join(", ", ModTargets.LayerParams)}";
            case Surface:
                return param.Equals("opacity", StringComparison.OrdinalIgnoreCase) ? null : "a surface modulates its opacity";
            case FxSlot f:
                return param.Equals("mix", StringComparison.OrdinalIgnoreCase)
                    || (AudioRt.FxRanges?.TryRange(f.Kind, param, out _, out _, out _) ?? false) ? null : $"{param} is not a bindable number of {f.Kind}";
            default:
                return null;
        }
    }

    /// <summary>The beat as STATE shows it: refreshed by the tick when the beat count moves, while the clock is in use.</summary>
    private sealed record TempoFacts(long Beat, long BeatInBar, long Bar, long Phrase, double Phase);

    private bool PublishTempo(long leaderNs)
    {
        if (!_tempoInUse && LanePlan().Items.Any(i => i.Source is { IsAudio: false }))
            _tempoInUse = true;
        if (!_tempoInUse)
            return false;
        var clock = AudioRt.Tempo;
        var counts = clock.CountsAt(leaderNs);
        if (_tempoFacts is { } f && f.Beat == counts.Beat)
            return false;
        _tempoFacts = new TempoFacts(counts.Beat, counts.BeatInBar, counts.Bar, counts.Phrase, clock.PhasesAt(leaderNs).Beat);
        return true;
    }

    /// <summary>The clock is in use from the first TEMPO verb (or binding that reads it): STATE shows the beat from then on.</summary>
    private void TempoInUse()
    {
        _tempoInUse = true;
        PublishTempo(LeaderNowNs);
    }

    /// <summary>A Unix ms on this node's show clock as a leader instant (past instants allowed, within a day).</summary>
    private long? LeaderAtUnix(long unixMs)
    {
        var delta = unixMs - Services.Wall.UnixMs;
        if (delta is < -86_400_000 or > 86_400_000)
            return null;
        return LeaderNowNs + delta * Durations.NsPerMs;
    }
}
