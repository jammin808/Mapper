using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Audio;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Core.Modulation;
using PlayMap.Engine.Audio;

namespace PlayMap.Engine;

/// <summary>The AUDIO verbs: the inputs this node can hear, what the show listens to (the show's choice, or this machine's own with
/// HERE), the gain, and the sound as analysed now. AUDIO LISTEN answers what the node then hears, observed: listening, or retrying
/// with the device's words.</summary>
public sealed partial class EngineCore
{
    private const double MinGainDb = -24;
    private const double MaxGainDb = 24;

    private void RouteListen(VerbRoutes routes)
    {
        routes.OffLane("AUDIO INPUTS", c => Task.Run(AudioInputsReply, c.Ct));
        routes.OffLane("AUDIO LISTEN", AudioListenAsync);
        routes.Lane("AUDIO GAIN", c => AudioGain(c.Args));
        routes.Lane("AUDIO STATUS", _ => CommandReply.Success(AudioStatusJson()));
    }

    private CommandReply AudioInputsReply()
    {
        var rt = AudioRt;
        var inputs = rt.Hub.Inputs();
        var state = rt.State;
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("inputs");
            foreach (var i in inputs)
            {
                w.WriteStartObject();
                w.WriteString("key", i.Key);
                w.WriteString("name", i.Name);
                w.WriteString("kind", i.Kind.ToString().ToLowerInvariant());
                w.WriteNumber("channels", i.Channels);
                w.WriteStartArray("rates");
                foreach (var r in i.Rates)
                    w.WriteNumberValue(r);
                w.WriteEndArray();
                w.WriteString("backend", i.Backend);
                w.WriteBoolean("default", i.IsDefault);
                w.WriteBoolean("listening", state.State == "listening" && state.Key == i.Key);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("backends");
            foreach (var (backend, words) in rt.BackendWords())
            {
                w.WriteStartObject();
                w.WriteString("backend", backend);
                w.WriteString("words", words);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }), Inv($"{inputs.Count} inputs"));
    }

    /// <summary>What AUDIO LISTEN asks: the input, its channels and gain, and whether for this machine only.</summary>
    private sealed record ListenAsk(string Input, string? Channels, double? GainDb, bool Here);

    private static bool TryListenAsk(List<string> args, out ListenAsk ask, out string why)
    {
        ask = new ListenAsk("", null, null, false);
        why = "";
        if (args.Count == 0)
        {
            why = "AUDIO LISTEN <input>|DEFAULT|SYSTEM|OFF [CHANNELS 1|1+2|3-4|M|S] [GAIN <dB>] [HERE], or AUDIO LISTEN TEST CLICK|SWEEP|NOISE|DRUMS";
            return false;
        }
        var i = 0;
        string input;
        var first = args[0].ToUpperInvariant();
        if (first == "TEST")
        {
            if (args.Count < 2 || !TestSignal.Kinds.Contains(args[1].ToLowerInvariant()))
            {
                why = "AUDIO LISTEN TEST CLICK|SWEEP|NOISE|DRUMS";
                return false;
            }
            input = "test:" + args[1].ToLowerInvariant();
            i = 2;
        }
        else
        {
            input = first switch
            {
                "DEFAULT" => "default",
                "SYSTEM" => "system",
                "OFF" => "",
                _ => args[0],
            };
            i = 1;
        }
        string? channels = null;
        double? gain = null;
        var here = false;
        for (; i < args.Count; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "CHANNELS" when i + 1 < args.Count:
                    if (!ChannelPick.TryParse(args[i + 1], out var pick, out why) || pick.Kind == ChannelPickKind.All)
                    {
                        why = why.Length > 0 ? why : "CHANNELS is 1, 1+2, 3-4, M or S";
                        return false;
                    }
                    channels = pick.ToString();
                    i++;
                    break;
                case "GAIN" when i + 1 < args.Count:
                    if (!double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g) || !double.IsFinite(g)
                        || g is < MinGainDb or > MaxGainDb)
                    {
                        why = "GAIN is -24 to 24 dB";
                        return false;
                    }
                    gain = g;
                    i++;
                    break;
                case "HERE":
                    here = true;
                    break;
                default:
                    why = $"'{args[i]}' is not an option of AUDIO LISTEN (CHANNELS, GAIN, HERE) — a name with spaces is quoted";
                    return false;
            }
        }
        if (input.Length == 0 && (channels is not null || gain is not null))
        {
            why = "AUDIO LISTEN OFF takes no CHANNELS or GAIN";
            return false;
        }
        ask = new ListenAsk(input, channels, gain, here);
        return true;
    }

    private async Task<CommandReply> AudioListenAsync(VerbCall c)
    {
        if (!TryListenAsk(c.Args, out var ask, out var why))
            return CommandReply.Fail(why);
        var rt = AudioRt;
        var before = ListenSpecNow();
        var label = ask.Input.Length == 0 ? "listen to nothing" : $"listen to {ask.Input}";
        string applied;
        if (ask.Here)
        {
            var node = rt.Node;
            var next = node with { Input = ask.Input, Channels = ask.Channels ?? node.Channels };
            var unchanged = next == node && (ask.GainDb is null || Math.Abs(ask.GainDb.Value - Store.Project.Audio.GainDb) < 1e-9);
            if (unchanged && rt.State.State is "listening" or "off")
                return CommandReply.Fail(ask.Input.Length == 0 ? "this machine already follows the show's input" : $"this machine already listens to {ask.Input}");
            if (ask.GainDb is { } g && Math.Abs(g - Store.Project.Audio.GainDb) > 1e-9)
            {
                var gr = await EditAsync(c.Session, Inv($"audio gain {g:0.#} dB"), p => p with { Audio = p.Audio with { GainDb = g } }).ConfigureAwait(false);
                if (!gr.Changed)
                    return FromEdit(gr);
            }
            rt.Node = next;
            rt.SaveNode?.Invoke(next);
            applied = ask.Input.Length == 0 ? "this machine follows the show's input" : $"this machine listens to {ask.Input}";
        }
        else
        {
            var a = Store.Project.Audio;
            var next = a with { Input = ask.Input, Channels = ask.Channels ?? a.Channels, GainDb = ask.GainDb ?? a.GainDb };
            if (next == a && rt.State.State is "listening" or "off")
                return CommandReply.Fail(ask.Input.Length == 0 ? "the show already listens to nothing" : $"the show already listens to {ask.Input}");
            if (next != a)
            {
                var r = await EditAsync(c.Session, label, p => p with { Audio = next }).ConfigureAwait(false);
                if (!r.Changed)
                    return FromEdit(r);
            }
            applied = ask.Input.Length == 0 ? "the show listens to nothing" : $"the show listens to {ask.Input}";
            if (rt.Node.Input.Length > 0)
                applied += $" (this machine keeps its own input, {rt.Node.Input} — AUDIO LISTEN OFF HERE follows the show)";
        }
        var spec = ListenSpecNow();
        var state = spec == before && rt.State.State == "listening" ? rt.State : await rt.ListenAsync(ListenSpecNow, c.Ct).ConfigureAwait(false);
        FactsChanged();
        var heard = state.State switch
        {
            "listening" => Inv($"; listening to {state.Name} ({state.Channels}) at {state.RateHz} Hz"),
            "off" => "",
            _ => $"; this node cannot hear it yet: {state.Words} (tried again every {AudioRuntime.RetryEvery.TotalSeconds:0} s)",
        };
        return CommandReply.Success(JsonOut(w => WriteListenState(w, state)), applied + heard);
    }

    private static void WriteListenState(Utf8JsonWriter w, ListenState s)
    {
        w.WriteStartObject();
        w.WriteString("input", s.Input);
        w.WriteString("key", s.Key);
        w.WriteString("name", s.Name);
        w.WriteString("channels", s.Channels);
        w.WriteNumber("rateHz", s.RateHz);
        w.WriteString("state", s.State);
        w.WriteString("words", s.Words);
        w.WriteString("backend", s.Backend);
        w.WriteEndObject();
    }

    private CommandReply AudioGain(List<string> args)
    {
        if (args.Count != 1)
            return CommandReply.Fail("AUDIO GAIN <-24…24>|AUTO|FIXED");
        var a = Store.Project.Audio;
        var word = args[0].ToUpperInvariant();
        if (word is "AUTO" or "FIXED")
        {
            var auto = word == "AUTO";
            if (a.AutoGain == auto)
                return CommandReply.Fail(auto ? "auto-gain is already on" : "the gain is already fixed");
            var r = Edits.Apply(auto ? "auto-gain on" : "auto-gain off", p => p with { Audio = p.Audio with { AutoGain = auto } });
            if (r.Changed && ReferenceEquals(Edits, Store) && AudioRt.Analyser is { } an)
                an.AutoGain = auto;
            return r.Changed ? CommandReply.Success(effect: auto ? "the normalised sources (…/n) use the whole range on their own"
                : "the normalised sources (…/n) follow the gain only") : FromEdit(r);
        }
        if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var db) || !double.IsFinite(db) || db is < MinGainDb or > MaxGainDb)
            return CommandReply.Fail("AUDIO GAIN is -24 to 24 dB, AUTO or FIXED");
        db = Math.Round(db, 2);
        if (Math.Abs(db - a.GainDb) < 1e-9)
            return CommandReply.Fail(Inv($"the gain is already {db:0.##} dB"));
        var gr = Edits.Apply(Inv($"audio gain {db:0.##} dB"), p => p with { Audio = p.Audio with { GainDb = db } }, coalesceKey: "audio gain");
        if (gr.Changed && ReferenceEquals(Edits, Store) && AudioRt.Analyser is { } analyser)
            analyser.GainDb = db;
        return gr.Changed ? CommandReply.Success(effect: Inv($"the gain is {db:0.##} dB")) : FromEdit(gr);
    }

    /// <summary>The sound as analysed now: the input's state, the newest frame's numbers, bands, spectrum and onset.</summary>
    internal string AudioStatusJson()
    {
        var rt = AudioRt;
        var now = LeaderNowNs;
        var frame = new AnalysisFrame();
        var has = rt.Feed.Listening && rt.Feed.Ring.TryReadLatest(frame);
        return JsonOut(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("input");
            WriteListenState(w, rt.State);
            w.WriteBoolean("heard", has);
            if (has)
            {
                w.WriteNumber("frameAgeMs", Math.Round((now - frame.LeaderNs) / 1e6, 1));
                w.WriteNumber("level", Round(frame.Level));
                w.WriteNumber("bass", Round(frame.Bass));
                w.WriteNumber("mid", Round(frame.Mid));
                w.WriteNumber("high", Round(frame.High));
                w.WriteNumber("levelN", Round(frame.LevelN));
                w.WriteNumber("bassN", Round(frame.BassN));
                w.WriteNumber("midN", Round(frame.MidN));
                w.WriteNumber("highN", Round(frame.HighN));
                w.WriteNumber("peakDb", Math.Round(frame.PeakDb, 1));
                w.WriteNumber("flux", Round(frame.Flux));
                w.WriteNumber("centroid", Round(frame.Centroid));
                w.WriteBoolean("silent", frame.Silent);
                if (frame.LastOnsetLeaderNs == long.MinValue)
                    w.WriteNull("onsetAgoMs");
                else
                    w.WriteNumber("onsetAgoMs", Math.Round((now - frame.LastOnsetLeaderNs) / 1e6, 1));
                w.WriteStartArray("bands");
                foreach (var b in frame.Bands)
                    w.WriteNumberValue(Round(b));
                w.WriteEndArray();
                w.WriteStartArray("spectrum");
                foreach (var s in frame.Spectrum)
                    w.WriteNumberValue(Round(s));
                w.WriteEndArray();
            }
            w.WriteNumber("gainDb", Store.Project.Audio.GainDb);
            w.WriteBoolean("autoGain", Store.Project.Audio.AutoGain);
            w.WriteNumber("gateDb", Store.Project.Audio.GateDb);
            w.WriteEndObject();
        });
    }
}
