using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Time;
using PlayMap.Core.Tc;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tc;

namespace PlayMap.Engine;

/// <summary>wp2-control's timecode chase in the engine: the TIMECODE verbs, the chase that runs in the lane's tick (a chased
/// playlist's transport is a function of its source's fitted line at every instant), STATE.timecode, each playlist's chase, the
/// context's timecode and chase items and the disengaged chases kept for recovery. Only the chase moves a chased playlist; manual
/// transport on it is refused with words until it is disengaged.</summary>
public sealed partial class EngineCore
{
    private const long ReanchorEveryNs = 250_000_000;

    private readonly Dictionary<string, ChaseRun> _chases = new(StringComparer.Ordinal);
    private volatile ImmutableDictionary<string, ChaseView> _chaseViews = ImmutableDictionary<string, ChaseView>.Empty.WithComparers(StringComparer.Ordinal);
    private Project? _timecodeProject;

    /// <summary>The show's timecode sources at run time (fed by the control service's inputs, read by the chase).</summary>
    public TimecodeBoard TimecodeSources { get; } = new();

    /// <summary>A playlist's chase as the last tick left it, or null.</summary>
    public ChaseView? ChaseOf(string playlistId) => _chaseViews.GetValueOrDefault(playlistId);

    /// <summary>A chase as STATE shows it.</summary>
    public sealed record ChaseView(string PlaylistId, ImmutableArray<string> Sources, string Active, bool Engaged, ChaseState State, string Text,
        string? ItemId, double Rate, long FreewheelLeftMs, string Words, string? LastJump);

    private sealed class ChaseRun
    {
        public bool Engaged { get; set; } = true;
        public ChaseMemory Memory { get; set; } = ChaseMemory.Start;
        public ChasePlan? Plan { get; set; }
        public string Active { get; set; } = "";
        public long LastAnchorNs { get; set; } = long.MinValue;
        public string? LastJump { get; set; }
        public (Playlist List, TcFormat Format, ImmutableArray<TcRegion> Regions, string Problem)? Regions { get; set; }
    }

    private void RouteTimecode(VerbRoutes routes)
    {
        routes.Lane("TIMECODE STATUS", c => TimecodeStatus(c.Args));
        routes.Lane("TIMECODE DEVICES", _ => TimecodeDevices());
        routes.Lane("TIMECODE SOURCE", c => TimecodeSource(c.Args));
        routes.Lane("TIMECODE CHASE", c => TimecodeChaseVerb(c.Args));
        routes.Lane("TIMECODE ENGAGE", c => TimecodeEngage(c.Args));
        routes.Lane("TIMECODE SET", c => TimecodeSet(c.Args));
        routes.Lane("TIMECODE IN", c => TimecodeIn(c.Args));
        routes.Lane("TIMECODE INTERNAL", c => TimecodeInternal(c.Args));
        routes.Index(new ChaseIndex(this));
    }

    // ---- the chase (lane) ----

    /// <summary>Runs every chase once at a leader instant (the lane's tick, or a test).</summary>
    private void TickTimecode(long leaderNs)
    {
        var p = Store.Project;
        if (!ReferenceEquals(p, _timecodeProject))
        {
            _timecodeProject = p;
            TimecodeSources.Reconcile(p);
            foreach (var gone in _chases.Keys.Where(k => p.FindPlaylist(k)?.Chase is null).ToList())
            {
                _chases.Remove(gone);
                if (p.FindPlaylist(gone) is { } released)
                    Playback.Guard(released, null, leaderNs);
            }
        }
        if (p.Playlists.All(l => l.Chase is null) && _chases.Count == 0)
            return;
        var mono = Services.Mono.NowNs;
        var views = ImmutableDictionary.CreateBuilder<string, ChaseView>(StringComparer.Ordinal);
        foreach (var list in p.Playlists)
        {
            if (list.Chase is not { } chase)
                continue;
            if (!_chases.TryGetValue(list.Id, out var run))
                _chases[list.Id] = run = new ChaseRun();
            views[list.Id] = Step(list, chase, run, leaderNs, mono);
        }
        _chaseViews = views.ToImmutable();
    }

    /// <summary>One chase at one instant: the active source (the first rolling one in order, else the first), its line in leader
    /// time, the plan, and the lane made to match it when it moves by a quarter frame or more (at most four times a second within a
    /// region; at once when the region or the state changes).</summary>
    private ChaseView Step(Playlist list, TimecodeChase chase, ChaseRun run, long leaderNs, long mono)
    {
        var sources = chase.Sources;
        var readings = sources.Select(s => (Id: s, Reading: TimecodeSources.ReadingOf(s, mono))).ToList();
        var active = readings.FirstOrDefault(r => r.Reading.State is "rolling" or "parked");
        if (active.Id is null)
            active = readings.FirstOrDefault(r => r.Id == run.Active) is { Id: not null } kept ? kept : readings.FirstOrDefault();
        var reading = active.Reading ?? TcReading.None;
        if (active.Id is not null && run.Active.Length > 0 && active.Id != run.Active)
            Journal.Add("timecode", $"{list.Name} switched to {SourceName(active.Id)}", "timecode");
        run.Active = active.Id ?? "";
        var sourceName = SourceName(run.Active);
        if (!run.Engaged)
        {
            Playback.Guard(list, null, leaderNs);
            run.Memory = ChaseMemory.Start;
            return new ChaseView(list.Id, sources, run.Active, false, ChaseState.Disengaged, reading.Text, Playback.Lane(list.Id)?.ItemId, 0, 0,
                $"disengaged — {list.Name} plays by hand", run.LastJump);
        }
        var format = reading.Format ?? FormatOf(run.Active);
        var regions = RegionsOf(list, format, run);
        var line = reading.Line is { } l ? l with { AtNs = Services.Clock.ToLeader(l.AtNs) } : (TcLine?)null;
        var before = run.Memory.State;
        var (plan, memory) = ChaseLogic.Decide(ChaseSettings.Of(chase), regions.Regions, reading.State, line, run.Memory, leaderNs);
        run.Memory = memory;
        run.Plan = plan;
        if (before != memory.State)
            Journal.Add("timecode", $"{list.Name}: {StateWord(memory.State)} — {plan.Words}", "timecode");
        Playback.Guard(list, $"{list.Name} chases {sourceName} ({StateWord(plan.State)}) — TIMECODE ENGAGE {list.Name} OFF to drive it by hand", leaderNs);
        Apply(list, regions.Regions, plan, format, run, leaderNs);
        var freewheelLeft = plan.State == ChaseState.Freewheel && memory.FreewheelSinceNs != long.MinValue
            ? Math.Max(0, (chase.FreewheelNs - (leaderNs - memory.FreewheelSinceNs)) / 1_000_000) : -1;
        var words = plan.State == ChaseState.Locked
            ? $"locked to {sourceName}{(chase.DelayNs != 0 ? $", picture {Fmt(chase.DelayNs / 1e6)} ms later" : "")}"
            : regions.Problem.Length > 0 && plan.State == ChaseState.Outside ? regions.Problem : plan.Words;
        var item = plan.Region >= 0 && plan.Region < regions.Regions.Length ? regions.Regions[plan.Region].ItemId : Playback.Lane(list.Id)?.ItemId;
        return new ChaseView(list.Id, sources, run.Active, true, plan.State, line is null ? "" : format.Format(format.FrameOf(plan.TcNs)), item,
            plan.Rate, freewheelLeft, words, run.LastJump);
    }

    private (TcFormat Format, ImmutableArray<TcRegion> Regions, string Problem) RegionsOf(Playlist list, TcFormat format, ChaseRun run)
    {
        if (run.Regions is { } r && ReferenceEquals(r.List, list) && r.Format == format)
            return (r.Format, r.Regions, r.Problem);
        var (regions, problem) = TimecodeRegions.Build(list, format, item => item.Source.Kind == SourceKind.Media ? MediaDurationNs(item.Source.Id) : 0);
        run.Regions = (list, format, regions, problem);
        return (format, regions, problem);
    }

    private void Apply(Playlist list, ImmutableArray<TcRegion> regions, ChasePlan plan, TcFormat format, ChaseRun run, long leaderNs)
    {
        var lane = Playback.Lane(list.Id);
        var quarter = Math.Max(1, format.FrameNs / 4);
        ClipTransport NextClip(out int index)
        {
            index = -1;
            if (plan.NextRegion < 0 || plan.NextAtLeaderNs == long.MaxValue)
                return ClipTransport.Empty;
            var nr = regions[plan.NextRegion];
            index = nr.Index;
            var lineRate = plan.Action == ChaseAction.Anchor && plan.Region >= 0 ? plan.Rate / regions[plan.Region].Rate : run.Memory.LastLine?.Rate ?? 1;
            return new ClipTransport(nr.ItemId, TransportState.Playing, new TimelineAnchor(nr.InNs, plan.NextAtLeaderNs, lineRate * nr.Rate, true),
                nr.InNs, nr.OutNs, nr.Loop, 0);
        }
        switch (plan.Action)
        {
            case ChaseAction.Anchor:
            {
                var r = regions[plan.Region];
                var anchor = new TimelineAnchor(plan.MediaNs, plan.AtLeaderNs, plan.Running ? plan.Rate : r.Rate, plan.Running);
                var clip = new ClipTransport(r.ItemId, plan.Running ? TransportState.Playing : TransportState.Paused, anchor, r.InNs, r.OutNs, r.Loop, 0);
                var next = NextClip(out var nextIndex);
                if (lane is not null && lane.Index == r.Index && lane.ItemId == r.ItemId && lane.Clip.State == clip.State)
                {
                    var off = Math.Abs(lane.Clip.Anchor.MediaAt(leaderNs) - anchor.MediaAt(leaderNs));
                    var rateOff = Math.Abs(lane.Clip.Anchor.Rate - anchor.Rate) / Math.Max(1e-9, Math.Abs(anchor.Rate));
                    var nextSame = lane.NextIndex == nextIndex && Math.Abs(lane.SwitchAtLeaderNs - plan.NextAtLeaderNs) < quarter;
                    if (off < quarter && rateOff < 50e-6 && nextSame)
                        return;
                    if (off < 4 * quarter && nextSame && run.LastAnchorNs != long.MinValue && leaderNs - run.LastAnchorNs < ReanchorEveryNs)
                        return;
                    if (off >= 4 * quarter)
                        run.LastJump = $"{format.Format(format.FrameOf(r.StartNs + (long)((lane.Clip.Anchor.MediaAt(leaderNs) - r.InNs) / r.Rate)))} → {format.Format(format.FrameOf(plan.TcNs))}";
                }
                if (Playback.Chase(list, r.Index, clip, nextIndex, next, plan.NextAtLeaderNs))
                    run.LastAnchorNs = leaderNs;
                return;
            }
            case ChaseAction.Hold:
            {
                if (plan.Region < 0)
                {
                    // Reverse: hold where the lane is.
                    if (lane is { IsIdle: false, Clip.State: TransportState.Playing } playing)
                        Playback.Chase(list, playing.Index, playing.Clip with { State = TransportState.Paused, Anchor = playing.Clip.Anchor.Pause(leaderNs) }, -1,
                            ClipTransport.Empty, long.MaxValue);
                    return;
                }
                var r = regions[plan.Region];
                var held = new ClipTransport(r.ItemId, TransportState.Paused, new TimelineAnchor(plan.MediaNs, leaderNs, r.Rate, false), r.InNs, r.OutNs, r.Loop, 0);
                if (lane is { IsIdle: false } h && h.Index == r.Index && h.Clip.State == TransportState.Paused
                    && Math.Abs(h.Clip.Anchor.MediaAt(leaderNs) - plan.MediaNs) < quarter && h.NextIndex == -1 && plan.NextRegion < 0)
                    return;
                var next = NextClip(out var nextIndex);
                Playback.Chase(list, r.Index, held, nextIndex, next, plan.NextAtLeaderNs);
                return;
            }
            case ChaseAction.Gap:
            {
                var next = NextClip(out var nextIndex);
                Playback.ChaseGap(list, nextIndex, next, plan.NextAtLeaderNs);
                return;
            }
            case ChaseAction.Idle:
                Playback.ChaseGap(list, -1, ClipTransport.Empty, long.MaxValue);
                return;
        }
    }

    /// <summary>Runs the chases now (a test's tick, on the lane).</summary>
    internal Task StepChasesAsync() => _lane.RunAsync(() =>
    {
        TickTimecode(LeaderNowNs);
        Playback.Tick(Store.Project, LeaderNowNs);
        return 0;
    });

    private static string StateWord(ChaseState s) => s.ToString().ToLowerInvariant();

    private string SourceName(string id) => Store.Project.FindTimecodeSource(id)?.Name ?? id;

    private TcFormat FormatOf(string sourceId) =>
        Store.Project.FindTimecodeSource(sourceId) is { } s && TcFormat.TryParse(s.Format, out var f) ? f : TcFormat.Fps25;

    // ---- verbs ----

    private TimecodeSource? FindSource(string key)
    {
        var p = Edits.Project;
        return p.FindTimecodeSource(key) ?? p.Timecode.Sources.FirstOrDefault(s => s.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private CommandReply TimecodeStatus(List<string> args)
    {
        if (args.Count == 0)
            return CommandReply.Success(JsonOut(WriteTimecodeState));
        if (FindSource(args[0]) is { } s)
            return CommandReply.Success(JsonOut(w => WriteSource(w, s, Services.Mono.NowNs)));
        if (ResolvePlaylist(args[0]) is { } list)
        {
            if (list.Chase is null)
                return CommandReply.Fail($"{list.Name} chases no timecode — TIMECODE CHASE {list.Name} <source>");
            return CommandReply.Success(JsonOut(w => WriteChase(w, list)));
        }
        return CommandReply.Fail($"no timecode source or playlist {args[0]}");
    }

    private CommandReply TimecodeDevices() => CommandReply.Success(JsonOut(w =>
    {
        w.WriteStartObject();
        w.WriteStartArray("audioIn");
        if (Services.AudioInputs is { } audio)
        {
            foreach (var a in audio.Inputs())
            {
                w.WriteStartObject();
                w.WriteString("key", a.Key);
                w.WriteString("name", a.Name);
                w.WriteNumber("channels", a.Channels);
                w.WriteString("backend", a.Backend);
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
        w.WriteStartArray("midiIn");
        if (Services.Control is { } control)
            foreach (var name in control.MidiInputNames())
                w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteStartObject("artnet");
        w.WriteString("interface", ControlInput.Settings.ArtNetInterface.Length == 0 ? "all" : ControlInput.Settings.ArtNetInterface);
        w.WriteBoolean("open", Services.Control?.ArtNetOpen ?? false);
        w.WriteEndObject();
        if (Services.AudioInputs is null)
            w.WriteString("words", "no audio inputs on this node (the audio hub is not running): LTC cannot arrive here");
        w.WriteEndObject();
    }));

    private CommandReply TimecodeSource(List<string> args)
    {
        var usage = "TIMECODE SOURCE ADD <name> LTC [<device>[:<channel>]]|MTC <port>|ARTNET [STREAM <n>]|INTERNAL [FORMAT <fmt>] | TIMECODE SOURCE REMOVE <source> | TIMECODE SOURCE RESET <source>";
        if (args.Count < 2)
            return CommandReply.Fail(usage);
        var sub = args[0].ToUpperInvariant();
        if (sub is "REMOVE" or "RESET")
        {
            if (FindSource(args[1]) is not { } s)
                return CommandReply.Fail($"no timecode source {args[1]}");
            TimecodeSources.Reconcile(Store.Project);
            if (sub == "RESET")
                return TimecodeSources.Reset(s.Id) ? CommandReply.Success(effect: $"{s.Name}: its error counts start again") : CommandReply.Fail($"{s.Name} has no counts yet");
            var only = Edits.Project.Playlists.FirstOrDefault(l => l.Chase is { } c && c.Sources.Length == 1 && c.Sources[0] == s.Id
                && (!_chases.TryGetValue(l.Id, out var run) || run.Engaged));
            if (only is not null)
                return CommandReply.Fail($"{only.Name} chases only {s.Name} — TIMECODE CHASE {only.Name} OFF first");
            return FromEdit(Edits.Apply($"remove timecode source {s.Name}", p => p with
            {
                Timecode = p.Timecode with { Sources = p.Timecode.Sources.RemoveId(s.Id) },
                Playlists = p.Playlists.Select(l => l.Chase is { } c && c.Sources.Contains(s.Id)
                    ? l with { Chase = c with { Sources = c.Sources.Remove(s.Id) } } : l).ToImmutableArray(),
            }));
        }
        if (sub != "ADD" || args.Count < 3)
            return CommandReply.Fail(usage);
        var name = args[1];
        var rest = args.Skip(3).ToList();
        var format = "auto";
        var fi = rest.FindIndex(w => w.Equals("FORMAT", StringComparison.OrdinalIgnoreCase));
        if (fi >= 0)
        {
            if (fi + 1 >= rest.Count || !TcFormat.TryParse(rest[fi + 1], out var f))
                return CommandReply.Fail("FORMAT 23.976|24|25|29.97|29.97df|30|50|59.94|59.94df|60");
            format = f.Text;
            rest.RemoveRange(fi, 2);
        }
        TimecodeSource source;
        switch (args[2].ToUpperInvariant())
        {
            case "LTC":
            {
                if (rest.Count > 1)
                    return CommandReply.Fail("TIMECODE SOURCE ADD <name> LTC [<device>[:<channel>]]");
                var device = rest.Count == 1 ? rest[0] : "";
                var channel = 1;
                var colon = device.LastIndexOf(':');
                if (colon > 0 && int.TryParse(device.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ch))
                {
                    channel = ch;
                    device = device[..colon];
                }
                if (channel is < 1 or > 64)
                    return CommandReply.Fail("an LTC channel is 1 to 64");
                source = new TimecodeSource("", name, TimecodeKind.Ltc) { Device = device, Channel = channel, Format = format };
                break;
            }
            case "MTC":
                if (rest.Count != 1)
                    return CommandReply.Fail("TIMECODE SOURCE ADD <name> MTC <port>");
                source = new TimecodeSource("", name, TimecodeKind.Mtc) { Device = rest[0], Format = format };
                break;
            case "ARTNET":
            {
                var stream = 0;
                if (rest.Count == 2 && rest[0].Equals("STREAM", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out stream) || stream > 255)
                        return CommandReply.Fail("an Art-Net timecode stream is 0 to 255");
                }
                else if (rest.Count != 0)
                {
                    return CommandReply.Fail("TIMECODE SOURCE ADD <name> ARTNET [STREAM <n>]");
                }
                source = new TimecodeSource("", name, TimecodeKind.ArtNet) { Channel = stream, Format = format };
                break;
            }
            case "INTERNAL":
                if (rest.Count != 0)
                    return CommandReply.Fail("TIMECODE SOURCE ADD <name> INTERNAL [FORMAT <fmt>]");
                source = new TimecodeSource("", name, TimecodeKind.Internal) { Format = format == "auto" ? "25" : format };
                break;
            default:
                return CommandReply.Fail(usage);
        }
        var id = "";
        var r = Edits.Apply($"add timecode source {name}", p =>
        {
            id = p.NewId(Ids.TimecodeSource);
            var made = source with { Id = id };
            Validate(p, made);
            return p with { Timecode = p.Timecode with { Sources = p.Timecode.Sources.Add(made) } };
        });
        if (!r.Changed)
            return CommandReply.Fail(r.Effect);
        var opening = source.Kind switch
        {
            TimecodeKind.Ltc => $"opening {(source.Device.Length == 0 ? "the default audio input" : source.Device)} channel {source.Channel}",
            TimecodeKind.Mtc => $"listening on MIDI {source.Device}",
            TimecodeKind.ArtNet => $"listening for Art-Net timecode stream {source.Channel}",
            _ => "stopped at 00:00:00:00 — TIMECODE INTERNAL " + name + " PLAY rolls it",
        };
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteEndObject();
        }), $"added {name} ({id}); {opening} — STATE says when code arrives") with { Applied = source.Kind == TimecodeKind.Internal };
    }

    private CommandReply TimecodeChaseVerb(List<string> args)
    {
        if (args.Count != 2)
            return CommandReply.Fail("TIMECODE CHASE <playlist> <source>[,<backup>…]|OFF");
        if (ResolvePlaylist(args[0]) is not { } list)
            return CommandReply.Fail($"no playlist {args[0]}");
        if (IsOff(args[1]))
        {
            if (list.Chase is null)
                return CommandReply.Fail($"{list.Name} chases no timecode");
            return FromEdit(Edits.Apply($"{list.Name} stops chasing timecode", p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { Chase = null }) }));
        }
        var ids = new List<string>();
        foreach (var key in args[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (FindSource(key) is not { } s)
                return CommandReply.Fail($"no timecode source {key} — TIMECODE SOURCE ADD names one");
            if (ids.Contains(s.Id))
                return CommandReply.Fail($"{s.Name} is named twice");
            ids.Add(s.Id);
        }
        if (ids.Count == 0)
            return CommandReply.Fail("TIMECODE CHASE <playlist> <source>[,<backup>…]");
        if (list.SyncGroup is not null && Services.Network.Sync().Role == "follower")
            return CommandReply.Fail($"{list.Name} follows its sync group's leader — the chase runs on the leader (this node follows its anchors)");
        if (list.Chase is { } existing && existing.Sources.SequenceEqual(ids))
            return CommandReply.Fail($"{list.Name} already chases {string.Join(", ", ids.Select(SourceName))}");
        var chase = list.Chase is { } c ? c with { Sources = [.. ids] } : new TimecodeChase([.. ids]);
        var r = Edits.Apply($"{list.Name} chases {string.Join(", ", ids.Select(SourceName))}", p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { Chase = chase }) });
        if (!r.Changed)
            return CommandReply.Fail(r.Effect);
        if (_chases.TryGetValue(list.Id, out var run))
            run.Engaged = true;
        return CommandReply.Success(effect: $"{list.Name} chases {SourceName(ids[0])} (locked), engaged — waiting for code") with { Applied = false };
    }

    private CommandReply TimecodeEngage(List<string> args)
    {
        if (args.Count != 2 || args[1].ToUpperInvariant() is not ("ON" or "OFF" or "TOGGLE"))
            return CommandReply.Fail("TIMECODE ENGAGE <playlist>|ALL ON|OFF|TOGGLE");
        var p = Edits.Project;
        var lists = args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? p.Playlists.Where(l => l.Chase is not null).ToList()
            : ResolvePlaylist(args[0]) is { } one ? [one] : [];
        if (lists.Count == 0)
            return CommandReply.Fail(args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase) ? "no playlist chases timecode" : $"no playlist {args[0]}");
        var words = new List<string>();
        foreach (var list in lists)
        {
            if (list.Chase is null)
                return CommandReply.Fail($"{list.Name} chases no timecode — TIMECODE CHASE {list.Name} <source>");
            if (!_chases.TryGetValue(list.Id, out var run))
                _chases[list.Id] = run = new ChaseRun();
            var on = args[1].ToUpperInvariant() switch { "ON" => true, "OFF" => false, _ => !run.Engaged };
            if (on == run.Engaged)
            {
                if (lists.Count == 1)
                    return CommandReply.Fail(on ? $"{list.Name} is already engaged" : $"{list.Name} is already disengaged");
                continue;
            }
            run.Engaged = on;
            run.Memory = ChaseMemory.Start;
            if (on)
            {
                words.Add($"{list.Name} engaged: takes {SourceName(list.Chase.Sources.FirstOrDefault() ?? "")} at its next frame");
            }
            else
            {
                Playback.Guard(list, null, LeaderNowNs);
                var at = _chaseViews.TryGetValue(list.Id, out var v) ? v.Text : "";
                words.Add($"{list.Name} disengaged: plays on by hand{(at.Length > 0 ? " from " + at : "")}");
            }
        }
        if (words.Count == 0)
            return CommandReply.Fail("every chase is already so");
        TickTimecode(LeaderNowNs);
        return CommandReply.Success(effect: string.Join("; ", words));
    }

    private CommandReply TimecodeSet(List<string> args)
    {
        var usage = "TIMECODE SET <playlist> DELAY <±ms>|FREEWHEEL <ms>|ONLOSS HOLD|STOP|CONTINUE|JUMP <ms>|AUTO|GAP BLACK|HOLD";
        if (args.Count != 3)
            return CommandReply.Fail(usage);
        if (ResolvePlaylist(args[0]) is not { } list)
            return CommandReply.Fail($"no playlist {args[0]}");
        if (list.Chase is not { } chase)
            return CommandReply.Fail($"{list.Name} chases no timecode — TIMECODE CHASE {list.Name} <source>");
        var value = args[2];
        bool Ms(double lo, double hi, out long ns)
        {
            ns = 0;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms < lo || ms > hi)
                return false;
            ns = (long)Math.Round(ms * 1e6);
            return true;
        }
        TimecodeChase next;
        string effect;
        switch (args[1].ToUpperInvariant())
        {
            case "DELAY":
                if (!Ms(-2000, 2000, out var delay))
                    return CommandReply.Fail("DELAY is −2000 to 2000 ms");
                next = chase with { DelayNs = delay };
                effect = delay == 0 ? $"{list.Name}: picture on its timecode" : $"{list.Name}: picture {Fmt(Math.Abs(delay) / 1e6)} ms {(delay > 0 ? "later" : "earlier")} than its timecode";
                break;
            case "FREEWHEEL":
                if (!Ms(0, 10000, out var free))
                    return CommandReply.Fail("FREEWHEEL is 0 to 10000 ms");
                next = chase with { FreewheelNs = free };
                effect = $"{list.Name}: rides a dropout for {Fmt(free / 1e6)} ms";
                break;
            case "ONLOSS":
                if (!Enum.TryParse<ChaseLoss>(value, true, out var loss) || int.TryParse(value, out _))
                    return CommandReply.Fail("ONLOSS HOLD|STOP|CONTINUE");
                next = chase with { OnLoss = loss };
                effect = $"{list.Name}: when the code stops it {loss.ToString().ToLowerInvariant()}s";
                break;
            case "JUMP":
                if (value.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
                {
                    next = chase with { JumpNs = 0 };
                    effect = $"{list.Name}: jumps when the code moves by more than 2 frames or 5σ";
                }
                else if (Ms(1, 10000, out var jump))
                {
                    next = chase with { JumpNs = jump };
                    effect = $"{list.Name}: jumps when the code moves by more than {Fmt(jump / 1e6)} ms";
                }
                else
                {
                    return CommandReply.Fail("JUMP <1-10000 ms>|AUTO");
                }
                break;
            case "GAP":
                if (!Enum.TryParse<ChaseGap>(value, true, out var gap) || int.TryParse(value, out _))
                    return CommandReply.Fail("GAP BLACK|HOLD");
                next = chase with { Gap = gap };
                effect = $"{list.Name}: between items it {(gap == ChaseGap.Black ? "shows black" : "holds the last frame")}";
                break;
            default:
                return CommandReply.Fail(usage);
        }
        if (next == chase)
            return CommandReply.Fail($"{list.Name} is already so");
        var r = Edits.Apply($"{list.Name} chase {args[1].ToLowerInvariant()}", p => p with { Playlists = p.Playlists.Update(list.Id, l => l with { Chase = next }) });
        return r.Changed ? CommandReply.Success(effect: effect) : CommandReply.Fail(r.Effect);
    }

    private CommandReply TimecodeIn(List<string> args)
    {
        if (args.Count != 3)
            return CommandReply.Fail("TIMECODE IN <playlist> <item> <HH:MM:SS:FF>|NOW|OFF");
        if (ResolvePlaylist(args[0]) is not { } list)
            return CommandReply.Fail($"no playlist {args[0]}");
        var index = ItemIndex(list, args[1]);
        if (index < 0)
            return CommandReply.Fail($"{list.Name} has no item {args[1]}");
        var item = list.Items[index];
        var label = item.Label.Length > 0 ? item.Label : $"item {index + 1}";
        var format = list.Chase is { Sources.Length: > 0 } c ? TimecodeSources.ReadingOf(c.Sources[0], Services.Mono.NowNs).Format ?? FormatOf(c.Sources[0])
            : args[2].Contains(';', StringComparison.Ordinal) ? TcFormat.Fps2997Df : TcFormat.Fps30;
        string? tcIn;
        if (IsOff(args[2]))
        {
            if (item.TcIn is null)
                return CommandReply.Fail($"{label} has no TC in");
            tcIn = null;
        }
        else if (args[2].Equals("NOW", StringComparison.OrdinalIgnoreCase))
        {
            if (list.Chase is null || !_chaseViews.TryGetValue(list.Id, out var view) || view.Text.Length == 0)
                return CommandReply.Fail($"{list.Name} has no code now to take — NOW takes the chase's current timecode");
            tcIn = view.Text;
        }
        else
        {
            if (!format.TryParseLabel(args[2], out var frame))
            {
                if (format.DropFrame && format.TryParseLabel(args[2][..^2] + "02", out _))
                    return CommandReply.Fail($"{args[2]} does not exist at {format.Text} — {args[2][..^2]}{(format.LabelFps / 15).ToString("00", CultureInfo.InvariantCulture)} is the first frame of that minute");
                return CommandReply.Fail($"{args[2]} is no timecode at {format.Text} (HH:MM:SS:FF)");
            }
            tcIn = format.Format(frame);
        }
        if (string.Equals(item.TcIn, tcIn, StringComparison.Ordinal))
            return CommandReply.Fail($"{label} already starts at {tcIn}");
        var r = Edits.Apply($"{label} TC in {(tcIn ?? "off")}", p => p with
        {
            Playlists = p.Playlists.Update(list.Id, l => l with { Items = l.Items.Update(item.Id, i => i with { TcIn = tcIn }) }),
        });
        return r.Changed ? CommandReply.Success(effect: tcIn is null ? $"{label} follows the item before it (in {list.Name})" : $"{label} starts at {tcIn} (in {list.Name})")
            : CommandReply.Fail(r.Effect);
    }

    private CommandReply TimecodeInternal(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("TIMECODE INTERNAL <source> PLAY|STOP | LOCATE <tc> | RATE <0.5-2>");
        if (FindSource(args[0]) is not { } s)
            return CommandReply.Fail($"no timecode source {args[0]}");
        var verb = args[1].ToUpperInvariant();
        var format = TcFormat.TryParse(s.Format, out var f) ? f : TcFormat.Fps25;
        long tc = 0;
        double rate = 1;
        switch (verb)
        {
            case "PLAY" or "STOP" when args.Count == 2:
                break;
            case "LOCATE" when args.Count == 3:
                if (!format.TryParseLabel(args[2], out var frame))
                    return CommandReply.Fail($"{args[2]} is no timecode at {format.Text}");
                tc = format.Ns(frame);
                break;
            case "RATE" when args.Count == 3:
                if (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rate) || rate < 0.5 || rate > 2)
                    return CommandReply.Fail("RATE is 0.5 to 2");
                break;
            default:
                return CommandReply.Fail("TIMECODE INTERNAL <source> PLAY|STOP | LOCATE <tc> | RATE <0.5-2>");
        }
        TimecodeSources.Reconcile(Store.Project);
        var (ok, words) = TimecodeSources.Internal(s.Id, verb, tc, rate, Services.Mono.NowNs);
        if (ok)
            TickTimecode(LeaderNowNs);
        return ok ? CommandReply.Success(effect: words) : CommandReply.Fail(words);
    }

    // ---- STATE.timecode ----

    private void WriteTimecodeState(Utf8JsonWriter w)
    {
        var p = Store.Project;
        var mono = Services.Mono.NowNs;
        w.WriteStartObject();
        w.WriteStartArray("sources");
        foreach (var s in p.Timecode.Sources)
            WriteSource(w, s, mono);
        w.WriteEndArray();
        w.WriteStartArray("chases");
        foreach (var list in p.Playlists.Where(l => l.Chase is not null))
            WriteChase(w, list);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private void WriteSource(Utf8JsonWriter w, TimecodeSource s, long mono)
    {
        var r = TimecodeSources.ReadingOf(s.Id, mono);
        var (open, level, inputWords) = TimecodeSources.InputOf(s.Id);
        w.WriteStartObject();
        w.WriteString("id", s.Id);
        w.WriteString("name", s.Name);
        w.WriteString("kind", s.Kind switch { TimecodeKind.Ltc => "ltc", TimecodeKind.Mtc => "mtc", TimecodeKind.ArtNet => "artnet", _ => "internal" });
        w.WriteString("device", s.Kind == TimecodeKind.Ltc ? $"{(s.Device.Length == 0 ? "default" : s.Device)}:{s.Channel.ToString(CultureInfo.InvariantCulture)}"
            : s.Kind == TimecodeKind.ArtNet ? "stream " + s.Channel.ToString(CultureInfo.InvariantCulture) : s.Device);
        w.WriteString("state", r.State);
        w.WriteString("text", r.Text);
        w.WriteBoolean("df", r.Format?.DropFrame ?? false);
        w.WriteString("format", s.Format);
        w.WriteString("detected", r.Format?.Text ?? "");
        w.WriteNumber("fpsMeasured", Math.Round(r.MeasuredFps, 4));
        w.WriteNumber("speedPpm", r.Line is { Running: true } l ? Math.Round((l.Rate - 1) * 1e6) : 0);
        w.WriteNumber("sigmaUs", r.Line is { } line ? Math.Round(line.SigmaNs / 1000, 1) : 0);
        if (s.Kind == TimecodeKind.Ltc)
        {
            w.WriteNumber("levelDb", Math.Round(level, 1));
            w.WriteString("level", level >= -14 ? "green" : level >= -26 ? "amber" : "red");
        }
        w.WriteBoolean("open", s.Kind == TimecodeKind.Internal || open);
        w.WriteStartObject("errors");
        var c = r.Counters;
        w.WriteNumber("biphase", c.Biphase);
        w.WriteNumber("missing", c.Missing);
        w.WriteNumber("duplicate", c.Duplicate);
        w.WriteNumber("discontinuity", c.Discontinuity);
        w.WriteNumber("invalid", c.Invalid);
        w.WriteNumber("badBcd", c.BadBcd);
        w.WriteNumber("parity", c.Parity);
        w.WriteEndObject();
        w.WriteString("userBits", r.UserBits.ToString("X8", CultureInfo.InvariantCulture));
        w.WriteNumber("ageMs", r.State == "none" ? -1 : Math.Max(0, (mono - r.LastMonoNs) / 1_000_000));
        w.WriteNumber("latencyMs", Math.Round(s.LatencyNs / 1e6, 1));
        w.WriteString("words", inputWords.Length > 0 && r.State is "none" or "unavailable" ? inputWords : r.Words);
        w.WriteEndObject();
    }

    private void WriteChase(Utf8JsonWriter w, Playlist list)
    {
        var chase = list.Chase!;
        var v = _chaseViews.GetValueOrDefault(list.Id);
        w.WriteStartObject();
        w.WriteString("playlist", list.Id);
        w.WriteStartArray("sources");
        foreach (var s in chase.Sources)
            w.WriteStringValue(s);
        w.WriteEndArray();
        w.WriteString("active", v?.Active ?? chase.Sources.FirstOrDefault() ?? "");
        w.WriteString("mode", "locked");
        w.WriteBoolean("engaged", v?.Engaged ?? !_chases.TryGetValue(list.Id, out var r) || r.Engaged);
        w.WriteString("state", StateWord(v?.State ?? ChaseState.Waiting));
        w.WriteString("text", v?.Text ?? "");
        if (v?.ItemId is { } item)
            w.WriteString("item", item);
        else
            w.WriteNull("item");
        w.WriteNumber("rate", Math.Round(v?.Rate ?? 0, 6));
        w.WriteNumber("delayMs", Math.Round(chase.DelayNs / 1e6, 1));
        w.WriteNumber("freewheelMs", chase.FreewheelNs / 1_000_000);
        if (v is { FreewheelLeftMs: >= 0 })
            w.WriteNumber("freewheelLeftMs", v.FreewheelLeftMs);
        else
            w.WriteNull("freewheelLeftMs");
        w.WriteString("onLoss", chase.OnLoss.ToString().ToLowerInvariant());
        w.WriteString("gap", chase.Gap.ToString().ToLowerInvariant());
        if (v?.LastJump is { } jump)
            w.WriteString("lastJump", jump);
        else
            w.WriteNull("lastJump");
        w.WriteString("words", v?.Words ?? "engaged — waiting for code");
        w.WriteEndObject();
    }

    private void WritePlaylistChase(Utf8JsonWriter w, Playlist list)
    {
        if (list.Chase is null)
        {
            w.WriteNull("chase");
            return;
        }
        var v = _chaseViews.GetValueOrDefault(list.Id);
        w.WriteStartObject("chase");
        w.WriteString("state", StateWord(v?.State ?? ChaseState.Waiting));
        w.WriteString("source", v?.Active ?? list.Chase.Sources.FirstOrDefault() ?? "");
        w.WriteString("text", v?.Text ?? "");
        w.WriteBoolean("engaged", v?.Engaged ?? true);
        w.WriteEndObject();
    }

    // ---- context ----

    private void ContextTimecode(List<ContextItem> items, List<ContextLink> links)
    {
        var p = Store.Project;
        var mono = Services.Mono.NowNs;
        foreach (var s in p.Timecode.Sources)
        {
            var r = TimecodeSources.ReadingOf(s.Id, mono);
            var chased = p.Playlists.Any(l => l.Chase?.Sources.Contains(s.Id) == true);
            var (open, level, _) = TimecodeSources.InputOf(s.Id);
            var light = r.State switch
            {
                "rolling" or "parked" => s.Kind == TimecodeKind.Ltc && open && level < -26 ? Light.Amber : Light.Green,
                "acquiring" or "dropout" or "reverse" => Light.Amber,
                "unavailable" or "silent" or "lost" when chased => Light.Red,
                _ => Light.Grey,
            };
            items.Add(new ContextItem("timecode:" + s.Id, "timecode", s.Name, chased ? light : r.State is "rolling" ? Light.Green : Light.Grey,
                r.Text.Length > 0 ? $"{r.Text} · {r.Words}" : r.Words));
        }
        foreach (var list in p.Playlists.Where(l => l.Chase is not null))
        {
            var v = _chaseViews.GetValueOrDefault(list.Id);
            var state = v?.State ?? ChaseState.Waiting;
            var light = state switch
            {
                ChaseState.Locked or ChaseState.Parked => Light.Green,
                ChaseState.Freewheel or ChaseState.Free or ChaseState.Outside or ChaseState.Acquiring or ChaseState.Reverse => Light.Amber,
                ChaseState.Held or ChaseState.Stopped => Light.Red,
                ChaseState.Disengaged when TimecodeSources.ReadingOf(v?.Active ?? "", mono).State == "rolling" => Light.Amber,
                _ => Light.Grey,
            };
            items.Add(new ContextItem("chase:" + list.Id, "chase", list.Name, light, v is null ? "engaged — waiting for code" : v.Text.Length > 0 ? $"{v.Text} · {v.Words}" : v.Words));
            links.Add(new ContextLink("chase:" + list.Id, "playlist:" + list.Id, ContextLinkKinds.Drives, light));
            foreach (var s in list.Chase!.Sources)
                links.Add(new ContextLink("chase:" + list.Id, "timecode:" + s, ContextLinkKinds.Follows, light));
        }
    }

    // ---- checks and recovery ----

    private static string? TimecodeSourceProblem(Project p, TimecodeSource t)
    {
        if (string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 64)
            return "a timecode source's name is 1–64 characters";
        if (p.Timecode.Sources.Any(x => x.Id != t.Id && x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))
            return $"there is already a timecode source called {t.Name}";
        if (t.Format != "auto" && !TcFormat.TryParse(t.Format, out _))
            return $"{t.Format} is no timecode format — 23.976, 24, 25, 29.97, 29.97df, 30, 50, 59.94, 59.94df, 60 or auto";
        if (t.Kind == TimecodeKind.ArtNet && t.Channel is < 0 or > 255)
            return "an Art-Net timecode stream is 0 to 255";
        if (t.Kind == TimecodeKind.Ltc && t.Channel is < 1 or > 64)
            return "an LTC channel is 1 to 64";
        if (t.Kind == TimecodeKind.Mtc && t.Device.Length == 0)
            return "an MTC source names its MIDI port";
        if (t.FitWindowMs is < 200 or > 10000)
            return "a fit window is 200 to 10000 ms";
        return null;
    }

    private void RecoverySaveTimecode(IDictionary<string, string> into)
    {
        var off = _chases.Where(kv => !kv.Value.Engaged).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        if (off.Count > 0)
            into["control.disengaged"] = string.Join(',', off);
    }

    private void RecoveryLoadTimecode(IReadOnlyDictionary<string, string> from)
    {
        if (!from.TryGetValue("control.disengaged", out var off))
            return;
        foreach (var id in off.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_chases.TryGetValue(id, out var run))
                _chases[id] = run = new ChaseRun();
            run.Engaged = false;
        }
    }

    /// <summary>INDEX's "timecode" section: one row per chased playlist, its key engages or disengages the chase.</summary>
    private sealed class ChaseIndex(EngineCore engine) : IIndexContributor
    {
        public string Section => "timecode";

        public IndexPresetHint Hint { get; } = new("Timecode", "chase",
            [new IndexKey("Engage", "TIMECODE ENGAGE $(id) TOGGLE", "chase.engaged", "instant")]);

        public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
        {
            ArgumentNullException.ThrowIfNull(project);
            ArgumentNullException.ThrowIfNull(w);
            var views = engine._chaseViews;
            w.WriteStartArray();
            foreach (var list in project.Playlists.Where(l => l.Chase is not null))
            {
                var v = views.GetValueOrDefault(list.Id);
                w.WriteStartObject();
                w.WriteString("id", list.Id);
                w.WriteString("kind", "chase");
                w.WriteString("label", list.Name);
                w.WriteString("parent", list.Id);
                w.WriteString("light", v?.State switch
                {
                    ChaseState.Locked or ChaseState.Parked => "green",
                    ChaseState.Held or ChaseState.Stopped => "red",
                    null or ChaseState.Waiting or ChaseState.Disengaged => "grey",
                    _ => "amber",
                });
                w.WriteString("state", StateWord(v?.State ?? ChaseState.Waiting));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
    }
}
