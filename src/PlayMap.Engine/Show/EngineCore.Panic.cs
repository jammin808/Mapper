using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;
using PlayMap.Engine.Show;
using PlayMap.Render.Engine;

namespace PlayMap.Engine;

/// <summary>PANIC, PANIC CLEAR, PANIC SET and MASTER. Every level is a fade on the show clock (<see cref="AirFade"/>), so a dropped
/// frame heals and every node fades on the same frames; the render thread reads <see cref="AirLevels"/>, built on the lane once
/// per change. A panic's transport outcome (pause, rewind, next) lands at its fade's end, at that instant on the show clock, and
/// holds whatever drives the playlist (a timecode chase lets go first); a second PANIC while a fade runs is the hard stop. PANIC is
/// never refused for a paired person.</summary>
public sealed partial class EngineCore
{
    private const long NsPerMs = 1_000_000;
    private ImmutableArray<AirReturn> _returns = [];
    private readonly List<PanicStep> _panicSteps = [];
    private readonly ConcurrentDictionary<string, string> _panicLines = new(StringComparer.Ordinal);
    private volatile AirLevels? _levels;
    private bool _airSettledNoted = true;
    private long _panicSeq;
    private long _showTickedNs = long.MinValue;

    /// <summary>A transport outcome waiting for its fade's end: the playlist and what happens to it.</summary>
    private sealed record PanicStep(long AtLeaderNs, string PlaylistId, PanicOutcome Outcome);

    /// <summary>A panic's own line waiting for the panic's instant (PANIC … AT).</summary>
    private sealed record PanicLine(long AtLeaderNs, string Text, string Key);

    /// <summary>What let go of a playlist (or will, at its step's instant) so a panic's transport outcome holds: the playlist and
    /// the words.</summary>
    private sealed record PanicRelease(string PlaylistId, string Words, long AtLeaderNs, bool Done);

    /// <summary>Whatever drives a playlist's transport besides a hand (wp2-control's timecode chase) lets go of it before a panic
    /// step moves it, so the step's outcome holds whatever drives the playlist. With <paramref name="commit"/> it lets go at
    /// <paramref name="atLeaderNs"/> (its owner's STATE says so, and nothing takes it back but its own verb); without, it only names
    /// what would let go. <paramref name="words"/> stays null when nothing else drives the playlist. On the lane.</summary>
    partial void ReleaseForPanic(Playlist list, PanicOutcome outcome, long atLeaderNs, bool commit, ref string? words);

    private readonly List<PanicLine> _panicLinesDue = [];

    /// <summary>The air's levels the render thread reads now (null: every output at full, nothing held or overridden).</summary>
    internal IAirLevels? AirLevelsNow => _levels;

    /// <summary>The leader instant the lane's last tick saw: every panic step and line due by then has run (any thread).</summary>
    internal long ShowTickedNs => Volatile.Read(ref _showTickedNs);

    /// <summary>The air's sound gain at a leader instant (master × panic), from the same fades as the picture; 0 while the node's
    /// fleet role keeps what it puts out beside its outputs off (a hot backup's sound would double its primary's, a fenced node's
    /// belongs to a show it no longer holds).</summary>
    public float AirAudioGain(long leaderNs) => NetworkAir == Core.Fleet.NetworkAir.Live ? _levels?.AudioGain(leaderNs) ?? 1f : 0f;

    /// <summary>An output's picture gain at a leader instant (master × panic × degrade).</summary>
    public float AirGain(string outputId, long leaderNs) => _levels?.Gain(outputId, leaderNs) ?? 1f;

    private CommandReply Panic(VerbCall c)
    {
        var p = Store.Project;
        var (at, atError) = TakeAt(c.Args);
        if (atError is not null)
            return CommandReply.Fail(atError);
        if (ReadPanicWords(c.Args, p, true, out var outcome, out var targets, out var fadeMs) is { } error)
            return CommandReply.Fail(error);
        var now = LeaderNowNs;
        var current = _air.Panic;
        if (current is not null && current.Outputs.Any(o => now < PanicMath.SettlesAt(o)))
            return HardPanic(current, targets, c.Session, now);
        var fresh = targets.Where(o => current is null || !current.Outputs.Any(x => x.OutputId == o.Id)).ToList();
        if (fresh.Count == 0)
            return CommandReply.Fail($"already in panic since {Seconds(current!.SinceUnixMs)} ({PanicMath.Json(current.Outcome)}) — PANIC CLEAR brings the air back");

        var rules = p.Show.Rules;
        // The panic lands at its AT instant (now when none is given), PanicLeadMs ahead on the show clock, so a group's members
        // fade on the same frames. A member's panic from its sync leader carries the leader's lead in its AT: it adds none of its own.
        var lead = c.Session.Transport == LeaderTransport ? 0 : rules.PanicLeadMs;
        var start = at!.Value + lead * NsPerMs;
        var notes = new List<string>();
        var entries = new List<OutputPanicAir>();
        foreach (var o in fresh)
        {
            var plan = p.Show.Panic.FirstOrDefault(x => x.OutputId == o.Id);
            var oc = outcome ?? plan?.Outcome ?? rules.DefaultOutcome;
            var ms = fadeMs ?? plan?.FadeMs ?? rules.DefaultFadeMs;
            entries.Add(PanicEntry(o, oc, ms, plan?.SafeLook, ReturnLevel(o.Id, start), start, notes));
        }
        var panic = current is null
            ? new PanicAir(outcome ?? entries[0].Outcome, c.Session.ClientName, Services.Wall.UnixMs, now, false, [.. entries], ++_panicSeq)
            : current with { Outputs = current.Outputs.AddRange(entries), Seq = ++_panicSeq };
        _returns = [.. _returns.Where(r => !entries.Any(e => e.OutputId == r.OutputId))];
        SetAir(a => a with { Panic = panic });
        var refused = new List<string>();
        var released = Releases(p, entries, SchedulePanicSteps(p, entries, now, refused));
        notes.AddRange(released.Select(ReleaseNote));
        notes.AddRange(refused);
        RunPanicLines(p, entries, clear: false, start);
        RebuildLevels();
        RaiseEvent("panic", start, w =>
        {
            w.WriteString("outcome", PanicMath.Json(panic.Outcome));
            w.WriteBoolean("hard", false);
        });
        var word = outcome is { } o1 ? PanicMath.Word(o1) : "as planned";
        return CommandReply.Success(PanicReply(panic, entries, hard: false, notes, released),
            $"PANIC {word} on {Plural(entries.Count, "output")} by {c.Session.ClientName}{(notes.Count > 0 ? " — " + string.Join("; ", notes) : "")}");
    }

    /// <summary>One output's panic from now: its level fades from where it stands to black (SAFE: to black over half the fade, then
    /// the safe look up over the other half); FREEZE holds the picture; SHUTTER and FX-OFF leave the picture and act on devices
    /// and effects; HARD is black at once.</summary>
    private static OutputPanicAir PanicEntry(Output o, PanicOutcome oc, int ms, SourceRef? safe, float from, long now, List<string> notes)
    {
        var ns = Math.Clamp(ms, 0, 10_000) * NsPerMs;
        switch (oc)
        {
            case PanicOutcome.Safe when safe is { IsNone: false } look:
                var half = ns / 2;
                return new OutputPanicAir(o.Id, oc, Fade(from, 0, now, half), look, now + half, long.MaxValue);
            case PanicOutcome.Safe:
                notes.Add($"no safe look planned on {o.Name} — black instead (PANIC SET {o.Id} SAFE SAFE <source>)");
                return new OutputPanicAir(o.Id, PanicOutcome.Black, Fade(from, 0, now, ns), null, 0, long.MaxValue);
            case PanicOutcome.Hard:
                return new OutputPanicAir(o.Id, oc, new AirFade(0, 0, now, 0), null, 0, long.MaxValue);
            case PanicOutcome.Freeze or PanicOutcome.Shutter or PanicOutcome.FxOff:
                return new OutputPanicAir(o.Id, oc, new AirFade(from, from, now, 0), null, 0, long.MaxValue);
            default:
                return new OutputPanicAir(o.Id, oc, Fade(from, 0, now, ns), null, 0, long.MaxValue);
        }
    }

    /// <summary>The second press while a fade runs: every panicked output (and any newly named) black at once, every transport
    /// they show paused now, nothing left waiting for a fade's end.</summary>
    private CommandReply HardPanic(PanicAir current, List<Output> targets, SessionInfo session, long now)
    {
        var outputs = current.Outputs.Select(o => o with { Outcome = PanicOutcome.Hard, Level = new AirFade(0, 0, now, 0), Safe = null })
            .Concat(targets.Where(t => !current.Outputs.Any(o => o.OutputId == t.Id))
                .Select(t => new OutputPanicAir(t.Id, PanicOutcome.Hard, new AirFade(0, 0, now, 0), null, 0, long.MaxValue)))
            .ToImmutableArray();
        var panic = current with { Hard = true, Outputs = outputs, Seq = ++_panicSeq };
        _panicSteps.Clear();
        foreach (var due in _panicLinesDue)
            SendSystemLine(due.Text, due.Key);
        _panicLinesDue.Clear();
        _returns = [.. _returns.Where(r => !outputs.Any(o => o.OutputId == r.OutputId))];
        SetAir(a => a with { Panic = panic });
        var refused = new List<string>();
        var released = Releases(Store.Project, outputs, SchedulePanicSteps(Store.Project, outputs, now, refused));
        var notes = released.Select(ReleaseNote).Concat(refused).ToList();
        RebuildLevels();
        RaiseEvent("panic", now, w =>
        {
            w.WriteString("outcome", "hard");
            w.WriteBoolean("hard", true);
        });
        return CommandReply.Success(PanicReply(panic, outputs, hard: true, notes, released),
            $"PANIC hard: every output black at once by {session.ClientName}{(notes.Count > 0 ? " — " + string.Join("; ", notes) : "")}");
    }

    private CommandReply PanicClear(VerbCall c)
    {
        var p = Store.Project;
        if (ReadPanicWords(c.Args, p, false, out _, out var targets, out var fadeMs) is { } error)
            return CommandReply.Fail(error);
        if (_air.Panic is not { } panic)
            return CommandReply.Fail("not in panic — the air is the show's");
        var cleared = panic.Outputs.Where(o => targets.Any(t => t.Id == o.OutputId)).ToList();
        if (cleared.Count == 0)
            return CommandReply.Fail($"{string.Join(", ", targets.Select(t => t.Name))} {(targets.Count == 1 ? "is" : "are")} not in panic");
        var now = LeaderNowNs;
        var ms = Math.Clamp(fadeMs ?? p.Show.Rules.DefaultFadeMs, 0, 10_000);
        var ns = ms * NsPerMs;
        var returns = new List<AirReturn>();
        foreach (var o in cleared)
        {
            var level = PanicMath.Level(o, now);
            if (o.Safe is not null && now >= o.SafeFromLeaderNs)
                returns.Add(new AirReturn(o.OutputId, Fade(level, 0, now, ns / 2), o.Safe, now + ns / 2));
            else if (level < 1f)
                returns.Add(new AirReturn(o.OutputId, Fade(level, 1, now, ns), null, 0));
        }
        var remaining = panic.Outputs.Where(o => !cleared.Contains(o)).ToImmutableArray();
        var still = remaining.IsEmpty ? null : panic with { Outputs = remaining, Seq = ++_panicSeq };
        _returns = [.. _returns.Where(r => !returns.Any(n => n.OutputId == r.OutputId)), .. returns];
        DropStepsNotShownBy(p, remaining);
        // A line an output still in panic needs stays as it is; a line the cleared outputs were still waiting to send is dropped
        // (it never went, so nothing is sent to take it back).
        var untouched = PanicLineKeys(p, remaining);
        foreach (var due in _panicLinesDue.Where(d => !untouched.Contains(d.Key)).ToList())
        {
            _panicLinesDue.Remove(due);
            _panicLines.TryRemove(due.Key, out _);
            untouched.Add(due.Key);
        }
        SetAir(a => a with { Panic = still });
        RunPanicLines(p, cleared, clear: true, now, untouched);
        RebuildLevels();
        RaiseEvent("panic.clear", now, w =>
        {
            w.WriteStartArray("outputs");
            foreach (var o in cleared)
                w.WriteStringValue(o.OutputId);
            w.WriteEndArray();
        });
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("cleared");
            foreach (var o in cleared)
                w.WriteStringValue(o.OutputId);
            w.WriteEndArray();
            w.WriteNumber("fadeMs", ms);
            w.WriteBoolean("inPanic", still is not null);
            w.WriteEndObject();
        }), $"PANIC CLEAR on {Plural(cleared.Count, "output")} over {ms} ms by {c.Session.ClientName}");
    }

    private CommandReply PanicSet(VerbCall c)
    {
        const string Syntax = "PANIC SET <output>|ALL BLACK|RUN|REWIND|NEXT|FREEZE|SAFE|SHUTTER|FX-OFF|HARD [FADE <ms 0-10000>] [SAFE <source>]";
        var a = c.Args;
        var p = Edits.Project;
        if (a.Count < 2 || !PanicMath.TryParse(a[1], out var outcome))
            return CommandReply.Fail(Syntax);
        List<Output> outputs;
        if (a[0].Equals("ALL", StringComparison.OrdinalIgnoreCase))
            outputs = [.. p.Outputs];
        else if (ResolveOutput(a[0]) is { } one)
            outputs = [one];
        else
            return CommandReply.Fail($"no output {a[0]}");
        int? fade = null;
        SourceRef? safe = null;
        for (var i = 2; i < a.Count; i++)
        {
            if (a[i].Equals("FADE", StringComparison.OrdinalIgnoreCase) && i + 1 < a.Count
                && int.TryParse(a[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms is >= 0 and <= 10_000)
            {
                fade = ms;
                i++;
            }
            else if (a[i].Equals("SAFE", StringComparison.OrdinalIgnoreCase) && i + 1 < a.Count && SourceRef.TryParse(a[i + 1], out var look) && !look.IsNone)
            {
                if (ResolveSourceProblem(look) is { } problem)
                    return CommandReply.Fail(problem);
                safe = look;
                i++;
            }
            else
            {
                return CommandReply.Fail(Syntax);
            }
        }
        var rules = p.Show.Rules;
        var plans = p.Show.Panic;
        foreach (var o in outputs)
        {
            var old = plans.FirstOrDefault(x => x.OutputId == o.Id);
            var look = safe ?? old?.SafeLook;
            if (outcome == PanicOutcome.Safe && look is null)
                return CommandReply.Fail($"a SAFE plan names the look it shows: PANIC SET {o.Id} SAFE SAFE <source>");
            var plan = new OutputPanicPlan(o.Id, outcome, fade ?? old?.FadeMs ?? rules.DefaultFadeMs, look);
            plans = old is null ? plans.Add(plan) : plans.Replace(old, plan);
        }
        if (plans.SequenceEqual(p.Show.Panic))
            return CommandReply.Fail("already planned so");
        var who = outputs.Count == 1 ? outputs[0].Name : "every output";
        var r = Edits.Apply($"panic plan for {who}: {PanicMath.Word(outcome)}", q => q with { Show = q.Show with { Panic = plans } });
        if (!r.Changed)
            return CommandReply.Fail(r.Effect);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartArray();
            foreach (var plan in plans.Where(x => outputs.Any(o => o.Id == x.OutputId)))
                WritePlan(w, plan);
            w.WriteEndArray();
        }), r.Effect);
    }

    private CommandReply Master(VerbCall c)
    {
        const string Syntax = "MASTER <0-100> [FADE <ms 0-60000>] [HERE]";
        var a = c.Args;
        if (a.Count == 0 || !double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var level) || level is < 0 or > 100)
            return CommandReply.Fail(Syntax);
        var ms = 0;
        for (var i = 1; i < a.Count; i++)
        {
            if (a[i].Equals("FADE", StringComparison.OrdinalIgnoreCase) && i + 1 < a.Count
                && int.TryParse(a[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) && f is >= 0 and <= 60_000)
            {
                ms = f;
                i++;
            }
            else if (!a[i].Equals("HERE", StringComparison.OrdinalIgnoreCase))
            {
                return CommandReply.Fail(Syntax);
            }
        }
        var now = LeaderNowNs;
        var to = (float)(level / 100.0);
        var master = _air.Master;
        if (Math.Abs(master.To - to) < 1e-6f && master.Settled(now))
            return CommandReply.Fail($"the master is already at {level.ToString("0.#", CultureInfo.InvariantCulture)}");
        var next = to >= 1f && ms == 0 ? AirFade.Full : Fade(master.At(now), to, now, ms * NsPerMs);
        SetAir(x => x with { Master = next });
        RebuildLevels();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("master", to);
            w.WriteNumber("fadeMs", ms);
            w.WriteEndObject();
        }), $"master to {level.ToString("0.#", CultureInfo.InvariantCulture)} over {ms} ms");
    }

    /// <summary>Reads [outcome] [ON &lt;output&gt;…|ALL] [FADE &lt;ms&gt;] [HERE]: the outputs named (every output when none is), or the
    /// words that refuse the line.</summary>
    private string? ReadPanicWords(List<string> a, Project p, bool takesOutcome, out PanicOutcome? outcome, out List<Output> outputs, out int? fadeMs)
    {
        outcome = null;
        outputs = [];
        fadeMs = null;
        var named = false;
        var syntax = takesOutcome
            ? "PANIC [BLACK|RUN|REWIND|NEXT|FREEZE|SAFE|SHUTTER|FX-OFF|HARD] [ON <output>…|ALL] [FADE <ms 0-10000>] [AT +<ms>|<unix ms>] [HERE]"
            : "PANIC CLEAR [ON <output>…|ALL] [FADE <ms 0-10000>] [HERE]";
        for (var i = 0; i < a.Count; i++)
        {
            var w = a[i];
            if (takesOutcome && i == 0 && PanicMath.TryParse(w, out var oc))
            {
                outcome = oc;
            }
            else if (w.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                named = false;
                outputs.Clear();
            }
            else if (w.Equals("FADE", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= a.Count || !int.TryParse(a[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) || ms is < 0 or > 10_000)
                    return "FADE takes 0 to 10000 ms";
                fadeMs = ms;
                i++;
            }
            else if (w.Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                named = true;
                while (i + 1 < a.Count && a[i + 1].ToUpperInvariant() is not ("FADE" or "HERE" or "ALL"))
                {
                    i++;
                    if (ResolveOutput(a[i]) is not { } o)
                        return $"no output {a[i]}";
                    if (!outputs.Contains(o))
                        outputs.Add(o);
                }
                if (outputs.Count == 0)
                    return syntax;
            }
            else if (!w.Equals("HERE", StringComparison.OrdinalIgnoreCase))
            {
                // HERE keeps a line to this node, which is every line while no group fan-out is set.
                return syntax;
            }
        }
        if (!named)
            outputs = [.. p.Outputs];
        return outputs.Count == 0 ? "there are no outputs on this node" : null;
    }

    /// <summary>The level an output stands at now on its way back from an earlier panic (1 when it is not returning).</summary>
    private float ReturnLevel(string outputId, long now)
    {
        foreach (var r in _returns)
        {
            if (r.OutputId == outputId)
                return r.Override(now) is not null ? 0f : r.At(now);
        }
        return 1f;
    }

    /// <summary>Each playlist an output shows waits for the most conservative transport outcome among the outputs showing it, at
    /// the latest of their fades' ends; HARD pauses now. Answers what let go of a playlist for the steps that ran now, and adds the
    /// words of any of them refused to <paramref name="refused"/>.</summary>
    private List<PanicRelease> SchedulePanicSteps(Project p, IEnumerable<OutputPanicAir> entries, long now, List<string> refused)
    {
        foreach (var e in entries)
        {
            if (PanicMath.Severity(e.Outcome) == 0 || p.FindOutput(e.OutputId) is not { } output)
                continue;
            var at = e.Level.StartLeaderNs + (e.Outcome == PanicOutcome.Hard ? 0 : e.Level.DurationNs);
            foreach (var list in PlaylistsShownBy(p, output))
            {
                var i = _panicSteps.FindIndex(s => s.PlaylistId == list);
                if (i < 0)
                    _panicSteps.Add(new PanicStep(at, list, e.Outcome));
                else
                {
                    var s = _panicSteps[i];
                    _panicSteps[i] = new PanicStep(Math.Max(s.AtLeaderNs, at),
                        list, PanicMath.Severity(e.Outcome) > PanicMath.Severity(s.Outcome) ? e.Outcome : s.Outcome);
                }
            }
        }
        var released = new List<PanicRelease>();
        RunDueSteps(now, released, refused);
        return released;
    }

    /// <summary>What lets go of the playlists a panic holds: what let go for the steps that ran now, then what will let go when each
    /// of its waiting steps comes.</summary>
    private List<PanicRelease> Releases(Project p, IEnumerable<OutputPanicAir> entries, List<PanicRelease> done)
    {
        var held = entries.Where(e => PanicMath.Severity(e.Outcome) > 0).Select(e => p.FindOutput(e.OutputId)).OfType<Output>()
            .SelectMany(o => PlaylistsShownBy(p, o)).ToHashSet(StringComparer.Ordinal);
        foreach (var s in _panicSteps)
        {
            if (!held.Contains(s.PlaylistId) || p.FindPlaylist(s.PlaylistId) is not { } list)
                continue;
            string? words = null;
            ReleaseForPanic(list, s.Outcome, s.AtLeaderNs, commit: false, ref words);
            if (words is not null)
                done.Add(new PanicRelease(list.Id, words, s.AtLeaderNs, false));
        }
        return done;
    }

    private static string ReleaseNote(PanicRelease r) => r.Done ? r.Words : "at the fade's end, " + r.Words;

    private void DropStepsNotShownBy(Project p, ImmutableArray<OutputPanicAir> remaining)
    {
        var shown = remaining.Select(o => p.FindOutput(o.OutputId)).OfType<Output>().SelectMany(o => PlaylistsShownBy(p, o)).ToHashSet(StringComparer.Ordinal);
        _panicSteps.RemoveAll(s => !shown.Contains(s.PlaylistId));
    }

    private void RunDueSteps(long now, List<PanicRelease>? released, List<string>? refused)
    {
        for (var i = 0; i < _panicSteps.Count;)
        {
            var s = _panicSteps[i];
            if (s.AtLeaderNs > now)
            {
                i++;
                continue;
            }
            _panicSteps.RemoveAt(i);
            if (RunStep(s, refused) is { } r)
                released?.Add(r);
        }
    }

    /// <summary>A transport outcome at its instant on the show clock: whatever else drives the playlist (a timecode chase) lets go
    /// first, so the outcome holds; then BLACK and HARD pause, REWIND cues the item that was on at its in-point, NEXT cues the next
    /// item. A playlist with nothing loaded is left as it is. A step the playlist refuses is never dropped: it is journalled with the
    /// refusal's words, which are added to <paramref name="refused"/> (the PANIC's own words, for a step that runs inside it). Answers
    /// what let go, if anything did.</summary>
    private PanicRelease? RunStep(PanicStep s, List<string>? refused)
    {
        var list = Store.Project.FindPlaylist(s.PlaylistId);
        if (list is null)
            return null;
        string? words = null;
        ReleaseForPanic(list, s.Outcome, s.AtLeaderNs, commit: true, ref words);
        var released = words is null ? null : new PanicRelease(list.Id, words, s.AtLeaderNs, true);
        if (released is not null)
            Journal.Add("panic", $"panic {PanicMath.Json(s.Outcome)}: {released.Words}");
        var lane = Playback.Lane(s.PlaylistId);
        if (lane is null || lane.IsIdle)
            return released;
        var (_, clip, itemId) = lane.At(s.AtLeaderNs);
        var index = itemId is null ? -1 : list.Items.IndexOfId(itemId);
        (bool Ok, string Words) done = s.Outcome switch
        {
            PanicOutcome.Rewind when index >= 0 => Playback.Cue(list, index, s.AtLeaderNs),
            PanicOutcome.Next when index >= 0 && PlaylistCursor.StepIndex(list, index, 1) is var next && next >= 0 => Playback.Cue(list, next, s.AtLeaderNs),
            _ when clip.State == TransportState.Playing => Playback.Transport(list, t => t.Pause(s.AtLeaderNs), s.AtLeaderNs),
            _ => (false, ""),
        };
        if (done.Ok)
        {
            Journal.Add("panic", $"panic {PanicMath.Json(s.Outcome)}: {done.Words}");
        }
        else if (done.Words.Length > 0)
        {
            var why = $"{list.Name} refused the panic's {PanicMath.Word(s.Outcome)}: {done.Words}";
            Journal.Add("panic", $"panic {PanicMath.Json(s.Outcome)}: {why}", ok: false);
            refused?.Add(why);
        }
        return released;
    }

    /// <summary>Every playlist an output's picture comes from: its source, a canvas's layers, a projector output's surfaces, and
    /// playlists nested in those.</summary>
    internal static HashSet<string> PlaylistsShownBy(Project p, Output o)
    {
        var lists = new HashSet<string>(StringComparer.Ordinal);
        if (o.Mode == OutputMode.Projector)
        {
            foreach (var s in p.Stage.Surfaces)
                Collect(p, s.Content.Source, lists, 0);
        }
        Collect(p, o.Source, lists, 0);
        return lists;

        static void Collect(Project p, SourceRef s, HashSet<string> into, int depth)
        {
            if (depth > 8)
                return;
            switch (s.Kind)
            {
                case SourceKind.Playlist when into.Add(s.Id) && p.FindPlaylist(s.Id) is { } list:
                    foreach (var item in list.Items)
                        Collect(p, item.Source, into, depth + 1);
                    break;
                case SourceKind.Canvas when p.FindCanvas(s.Id) is { } canvas:
                    foreach (var layer in canvas.Layers)
                        Collect(p, layer.Source, into, depth + 1);
                    break;
            }
        }
    }

    /// <summary>The lines a panic sends as the node itself, through the gate like any other: every projector linked to a SHUTTER
    /// output closes (opens on clear); FX-OFF bypasses every effect (and lets them back on clear). Each answer is kept as that
    /// device's or the effects' stage in STATE.</summary>
    private void RunPanicLines(Project p, IEnumerable<OutputPanicAir> entries, bool clear, long at, IReadOnlySet<string>? untouched = null)
    {
        var sent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.Outcome == PanicOutcome.Shutter && p.FindOutput(e.OutputId) is { } o)
            {
                foreach (var d in LinkedDevices(p, o))
                {
                    if (untouched?.Contains(d.Id) != true && sent.Add(d.Id))
                        SendSystemLineAt(at, $"DEVICE SHUTTER {d.Id} {(clear ? "OPEN" : "CLOSE")}", d.Id);
                }
            }
            else if (e.Outcome == PanicOutcome.FxOff && untouched?.Contains(FxLineKey) != true && sent.Add(FxLineKey))
            {
                SendSystemLineAt(at, clear ? "FX BYPASS ALL OFF" : "FX BYPASS ALL ON", FxLineKey);
            }
        }
    }

    /// <summary>The key of the effects' line in a panic's lines (a device's key is its id).</summary>
    private const string FxLineKey = "fx";

    /// <summary>The keys of the lines these outputs' panic holds: each projector linked to a SHUTTER output, and the effects' line
    /// for FX-OFF.</summary>
    private static HashSet<string> PanicLineKeys(Project p, IEnumerable<OutputPanicAir> entries)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.Outcome == PanicOutcome.Shutter && p.FindOutput(e.OutputId) is { } o)
            {
                foreach (var d in LinkedDevices(p, o))
                    keys.Add(d.Id);
            }
            else if (e.Outcome == PanicOutcome.FxOff)
            {
                keys.Add(FxLineKey);
            }
        }
        return keys;
    }

    /// <summary>The projectors linked to an output: by the output itself or by the projector it drives.</summary>
    private static IEnumerable<DeviceLink> LinkedDevices(Project p, Output o) =>
        p.Stage.Devices.Where(d => d.OutputId == o.Id || (o.ProjectorId is { Length: > 0 } pj && d.ProjectorId == pj));

    /// <summary>A panic's line now, or waiting for the panic's instant on the show clock (a projector's shutter cannot be scheduled
    /// in the projector, so the node sends it when the instant comes).</summary>
    private void SendSystemLineAt(long at, string text, string key)
    {
        if (at <= LeaderNowNs)
        {
            SendSystemLine(text, key);
            return;
        }
        _panicLines[key] = "scheduled";
        _panicLinesDue.Add(new PanicLine(at, text, key));
    }

    private void SendSystemLine(string text, string key)
    {
        if (!WireGrammar.TryParse(text, out var line, out _))
            return;
        _panicLines[key] = "sent";
        _ = Task.Run(async () =>
        {
            string stage;
            try
            {
                var reply = await HandleAsync(line, SystemSession, CancellationToken.None).ConfigureAwait(false);
                stage = reply.Ok ? (reply.Applied ? "applied" : "accepted") : "refused: " + (reply.Payload ?? "");
            }
            catch (ObjectDisposedException)
            {
                stage = "not sent: the node is stopping";
            }
            _panicLines[key] = stage;
            FactsChanged();
        }, CancellationToken.None);
    }

    /// <summary>The lane's panic work each tick: transport outcomes whose instant has come, returns that have settled, and one
    /// STATE move when every fade has settled (levels move without revisions while they fade).</summary>
    private void TickPanic(long now)
    {
        if (_panicSteps.Count > 0)
            RunDueSteps(now, null, null);
        for (var i = 0; i < _panicLinesDue.Count;)
        {
            var due = _panicLinesDue[i];
            if (due.AtLeaderNs > now)
            {
                i++;
                continue;
            }
            _panicLinesDue.RemoveAt(i);
            SendSystemLine(due.Text, due.Key);
        }
        if (!_returns.IsEmpty && _returns.Any(r => r.Settled(now)))
        {
            _returns = [.. _returns.Where(r => !r.Settled(now))];
            RebuildLevels();
            FactsChanged();
        }
        var settled = _air.Master.Settled(now) && (_air.Panic?.Outputs.All(o => now >= PanicMath.SettlesAt(o)) ?? true) && _returns.IsEmpty;
        if (settled && !_airSettledNoted)
            FactsChanged();
        _airSettledNoted = settled;
    }

    /// <summary>Builds the render thread's levels from the air now (null when nothing differs from full).</summary>
    private void RebuildLevels()
    {
        var air = _air;
        var gains = Services.OutputGains;
        _levels = air.Master == AirFade.Full && air.Panic is null && _returns.IsEmpty && gains is null
            ? null
            : new AirLevels(air.Master, air.Panic, _returns, gains);
        _airSettledNoted = false;
    }

    /// <summary>A panic put back after a restart: each output at its outcome at once (SAFE showing its look, FREEZE holding).</summary>
    private void RestorePanic(JsonElement e)
    {
        var p = Store.Project;
        var now = LeaderNowNs;
        var outputs = new List<OutputPanicAir>();
        foreach (var o in e.GetProperty("outputs").EnumerateArray())
        {
            var id = o.GetProperty("id").GetString() ?? "";
            if (p.FindOutput(id) is null || !PanicMath.TryParse(o.GetProperty("outcome").GetString() ?? "", out var oc))
                continue;
            var look = p.Show.Panic.FirstOrDefault(x => x.OutputId == id)?.SafeLook;
            outputs.Add(oc switch
            {
                PanicOutcome.Safe when look is not null => new OutputPanicAir(id, oc, new AirFade(0, 0, now, 0), look, now, long.MaxValue),
                PanicOutcome.Freeze or PanicOutcome.Shutter or PanicOutcome.FxOff => new OutputPanicAir(id, oc, AirFade.Full, null, 0, long.MaxValue),
                _ => new OutputPanicAir(id, oc == PanicOutcome.Safe ? PanicOutcome.Black : oc, new AirFade(0, 0, now, 0), null, 0, long.MaxValue),
            });
        }
        if (outputs.Count == 0)
            return;
        var outcome = PanicMath.TryParse(e.GetProperty("outcome").GetString() ?? "", out var parsed) ? parsed : outputs[0].Outcome;
        var panic = new PanicAir(outcome, e.GetProperty("by").GetString() ?? "", e.GetProperty("since").GetInt64(), now,
            e.TryGetProperty("hard", out var hard) && hard.GetBoolean(), [.. outputs], ++_panicSeq);
        SetAir(a => a with { Panic = panic });
        RebuildLevels();
        Journal.Add("recovery", $"in panic again: {Plural(outputs.Count, "output")} at {PanicMath.Json(outcome)} as when the node went away");
    }

    private string PanicReply(PanicAir panic, IEnumerable<OutputPanicAir> entries, bool hard, List<string> notes, List<PanicRelease> released) => JsonOut(w =>
    {
        w.WriteStartObject();
        w.WriteString("outcome", hard ? "hard" : PanicMath.Json(panic.Outcome));
        w.WriteBoolean("hard", hard);
        w.WriteNumber("since", panic.SinceUnixMs);
        w.WriteStartArray("outputs");
        foreach (var o in entries)
        {
            w.WriteStartObject();
            w.WriteString("id", o.OutputId);
            w.WriteString("outcome", PanicMath.Json(o.Outcome));
            w.WriteNumber("fadeMs", (o.Safe is not null ? o.Level.DurationNs * 2 : o.Level.DurationNs) / NsPerMs);
            w.WriteNumber("until", PanicMath.SettlesAt(o));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        WritePanicLines(w);
        w.WriteStartArray("released");
        foreach (var r in released)
        {
            w.WriteStartObject();
            w.WriteString("playlist", r.PlaylistId);
            w.WriteBoolean("done", r.Done);
            w.WriteNumber("atLeaderNs", r.AtLeaderNs);
            w.WriteString("words", r.Words);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (notes.Count > 0)
            w.WriteString("notes", string.Join("; ", notes));
        w.WriteEndObject();
    });

    private void WritePanicLines(Utf8JsonWriter w)
    {
        w.WriteStartArray("devices");
        foreach (var (key, stage) in _panicLines.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("id", key);
            w.WriteString("stage", stage);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>A panic as STATE shows it (air.panic and show.panic): null, or its outcome, who, since, and each output's level now.</summary>
    internal void WritePanic(Utf8JsonWriter w, PanicAir? panic)
    {
        if (panic is null)
        {
            w.WriteNullValue();
            return;
        }
        var now = LeaderNowNs;
        w.WriteStartObject();
        w.WriteString("outcome", PanicMath.Json(panic.Outcome));
        w.WriteBoolean("hard", panic.Hard);
        w.WriteString("by", panic.By);
        w.WriteNumber("since", panic.SinceUnixMs);
        w.WriteStartArray("outputs");
        foreach (var o in panic.Outputs)
        {
            w.WriteStartObject();
            w.WriteString("id", o.OutputId);
            w.WriteString("outcome", PanicMath.Json(o.Outcome));
            w.WriteNumber("level", Math.Round(PanicMath.Level(o, now), 3));
            w.WriteBoolean("settled", now >= PanicMath.SettlesAt(o));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        WritePanicLines(w);
        w.WriteEndObject();
    }

    /// <summary>STATE.air's keys after today's: the master's fade and the panic.</summary>
    private void WriteAirShow(Utf8JsonWriter w)
    {
        var air = _snapshot.Air;
        var now = LeaderNowNs;
        w.WriteStartObject("master");
        w.WriteNumber("level", Math.Round(air.Master.At(now), 3));
        w.WriteNumber("to", air.Master.To);
        w.WriteNumber("untilLeaderNs", air.Master.Settled(now) ? 0 : air.Master.StartLeaderNs + air.Master.DurationNs);
        w.WriteEndObject();
        w.WritePropertyName("panic");
        WritePanic(w, air.Panic);
    }

    private static void WritePlan(Utf8JsonWriter w, OutputPanicPlan plan)
    {
        w.WriteStartObject();
        w.WriteString("output", plan.OutputId);
        w.WriteString("outcome", PanicMath.Json(plan.Outcome));
        w.WriteNumber("fadeMs", plan.FadeMs);
        if (plan.SafeLook is { } look)
            w.WriteString("safe", look.ToString());
        else
            w.WriteNull("safe");
        w.WriteEndObject();
    }

    /// <summary>A fade from now; with no duration the level is already there at now.</summary>
    private static AirFade Fade(float from, float to, long now, long ns) => ns <= 0 ? new AirFade(to, to, now, 0) : new AirFade(from, to, now, ns);

    private static string Plural(int n, string word) => string.Create(CultureInfo.InvariantCulture, $"{n} {word}{(n == 1 ? "" : "s")}");
}
