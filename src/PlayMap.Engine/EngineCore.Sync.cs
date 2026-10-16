using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;
using PlayMap.Engine.Playback;

namespace PlayMap.Engine;

/// <summary>Sync groups and recovery: a leader publishes each synced playlist's transport as an anchor; a
/// follower adopts it exactly; a restarted node puts its transports back where the show is now.</summary>
public sealed partial class EngineCore
{
    /// <summary>Raised on the lane when a playlist in a sync group changes its transport here: the playlist
    /// id and the anchor JSON the followers take with PLAYLIST ANCHOR.</summary>
    public event Action<string, string>? AnchorPublished;

    /// <summary>The anchor of a playlist's current item (the leader's transport, in leader nanoseconds).</summary>
    public string? AnchorJson(string playlistId)
    {
        var snap = Snapshot;
        if (!snap.Playback.Playlists.TryGetValue(playlistId, out var lane) || lane.IsIdle)
            return null;
        var c = lane.Clip;
        return JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("index", lane.Index);
            w.WriteString("itemId", lane.ItemId);
            w.WriteString("state", c.State.ToString());
            w.WriteNumber("mediaNs", c.Anchor.MediaNs);
            w.WriteNumber("leaderNs", c.Anchor.LeaderNs);
            w.WriteNumber("rate", c.Anchor.Rate);
            w.WriteBoolean("running", c.Anchor.Running);
            w.WriteNumber("inNs", c.InNs);
            w.WriteNumber("outNs", c.OutNs);
            w.WriteBoolean("loop", c.Loop);
            w.WriteNumber("seq", c.Seq);
            w.WriteEndObject();
        });
    }

    private CommandReply PlaylistAnchor(List<string> args, WireLine line)
    {
        if (args.Count < 2)
            return CommandReply.Fail("PLAYLIST ANCHOR <playlist> <json>");
        var list = ResolvePlaylist(args[0]);
        if (list is null)
            return CommandReply.Fail($"no playlist {args[0]}");
        if (list.SyncGroup is null)
            return CommandReply.Fail($"{list.Name} is not in a sync group — PLAYLIST SYNC {list.Id} <group> first");
        try
        {
            using var doc = JsonDocument.Parse(line.RestAfter(3));
            var r = doc.RootElement;
            var itemId = r.GetProperty("itemId").GetString();
            var index = list.Items.IndexOfId(itemId);
            if (index < 0)
                index = r.GetProperty("index").GetInt32();
            if (!Enum.TryParse<TransportState>(r.GetProperty("state").GetString(), out var state))
                return CommandReply.Fail("an anchor's state is Cued, Playing, Paused or Ended");
            var anchor = new TimelineAnchor(r.GetProperty("mediaNs").GetInt64(), r.GetProperty("leaderNs").GetInt64(),
                r.GetProperty("rate").GetDouble(), r.GetProperty("running").GetBoolean());
            var clip = new ClipTransport(itemId ?? "", state, anchor, r.GetProperty("inNs").GetInt64(), r.GetProperty("outNs").GetInt64(),
                r.GetProperty("loop").GetBoolean(), r.GetProperty("seq").GetInt64());
            var moved = IsAdoptedGo(Playback.Lane(list.Id), index, clip, out var switches) ? BlindOnGo(list.Id, anchor.LeaderNs, switches) : null;
            var (ok, words) = Playback.Adopt(list, index, clip, LeaderNowNs);
            if (!ok)
                BlindGoRefused(moved);
            return ok ? CommandReply.Success(effect: words) : CommandReply.Fail(words);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return CommandReply.Fail("the anchor JSON is not one this node reads: " + ex.Message.Split('.')[0]);
        }
    }

    /// <summary>Whether the leader's anchor is a GO here: it sets the playlist playing, from its instant, on an item other than the one
    /// this node has on the air then (<paramref name="switches"/>: the GO switches the item, as a PLAYLIST GO, NEXT or PREV does), or
    /// on the same item that was not playing here then (a PLAY that resumes it). The leader's own GO lands a blind commit waiting for
    /// a GO, or for the playlist's next switch, on that instant; its anchor does the same on a member, whose desk gives no GO there.
    /// A scheduled switch the member's own playlist makes too is no GO: the item it brings is already the one on the air here.</summary>
    private static bool IsAdoptedGo(PlaylistPlayback? lane, int index, ClipTransport clip, out bool switches)
    {
        switches = false;
        if (clip.State != TransportState.Playing || !clip.Anchor.Running)
            return false;
        if (lane is null || lane.IsIdle)
        {
            switches = true;
            return true;
        }
        var at = clip.Anchor.LeaderNs;
        switches = PlaybackLanes.OnAirIndex(lane, at) != index;
        return switches || lane.At(at).Clip.State != TransportState.Playing;
    }

    /// <summary>Publishes anchors for synced playlists whose transport moved (called on the lane after every
    /// playback change).</summary>
    private void PublishAnchors(Playback.PlaybackSnapshot before, Playback.PlaybackSnapshot after)
    {
        if (AnchorPublished is null)
            return;
        foreach (var list in Store.Project.Playlists)
        {
            if (list.SyncGroup is null || !after.Playlists.TryGetValue(list.Id, out var lane) || lane.IsIdle)
                continue;
            if (before.Playlists.TryGetValue(list.Id, out var old) && old.Index == lane.Index && old.Clip == lane.Clip)
                continue;
            if (AnchorJson(list.Id) is { } json)
                AnchorPublished(list.Id, json);
        }
    }

    /// <summary>Puts a recovery record back: the project, the air, and every playlist at where the show is
    /// now (a playing item has moved on by the time the node was away, at its rate).</summary>
    public Task<string> RestoreAsync(RecoveryRecord record) => _lane.RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(record);
        Project? project = null;
        if (record.UnsavedProjectJson is not null)
            project = Core.Model.ProjectJson.Deserialize(record.UnsavedProjectJson);
        else if (record.ProjectPath is not null && File.Exists(record.ProjectPath))
            project = Core.Model.ProjectJson.Deserialize(File.ReadAllText(record.ProjectPath));
        if (project is null)
            return "nothing to recover";
        Store.Replace(project, "recovered after a restart");
        _projectPath = record.ProjectPath;
        _dirty = record.UnsavedProjectJson is not null;
        SetAir(a => a with { Blackout = record.Blackout, PatternKind = record.Pattern });
        var now = LeaderNowNs;
        var away = Math.Max(0, Services.Wall.UnixMs - record.WrittenUnixMs) * 1_000_000L;
        var restored = 0;
        foreach (var lane in record.Lanes)
        {
            var list = project.FindPlaylist(lane.PlaylistId);
            var index = list?.Items.IndexOfId(lane.ItemId) ?? -1;
            if (list is null || index < 0)
                continue;
            var position = lane.State == TransportState.Playing ? lane.PositionNs + (long)(away * lane.Rate) : lane.PositionNs;
            if (lane.State == TransportState.Playing)
                Playback.Go(list, index, now);
            else
                Playback.Cue(list, index, now);
            Playback.Transport(list, c => c.Seek(position, now), now);
            restored++;
        }
        if (record.FocusedPlaylist is not null)
            Playback.Focus(record.FocusedPlaylist);
        LoadModuleRecovery(record.Modules ?? ImmutableSortedDictionary<string, string>.Empty);
        Journal.Add("recovery", $"recovered {project.Name} with {restored} playlist{(restored == 1 ? "" : "s")} at where the show is now");
        return $"recovered {project.Name}";
    });
}

/// <summary>wp2-sync's hooks into the engine (EngineCore.Modules.cs): the group's clock, anchors and vblank verdict.
/// <list type="bullet">
/// <item>Every engine rebases on a step of its leader clock: the clock hands its step to this lane, which takes it and moves every
/// playlist's anchor by the same Δ in the same action, so no playlist jumps (a group playlist that took its leader's anchor is
/// already on the leader's time line and stays as it is).</item>
/// <item>With the sync part (<see cref="EngineServices.GroupSync"/>): SYNC LEAD and SYNC FOLLOW keep the role for the next start,
/// SUBSCRIBE ANCHORS serves the feed, SYNC RECEIPT takes members' receipts, a leader's PANIC, PANIC CLEAR, BLACKOUT and MASTER
/// reach every member, and STATE's <c>sync</c> gains the group, the feed, the clock's slew and steps and the board's verdict.</item>
/// </list></summary>
public sealed partial class EngineCore
{
    private long _syncSteps;
    private long _lockOffered = long.MinValue;

    /// <summary>The transport of the session a member runs its sync leader's lines as (a peer of the show).</summary>
    internal const string LeaderTransport = "sync";

    partial void RouteSync(VerbRoutes routes)
    {
        // Every engine takes its clock's steps on this lane, with the rebase, whether or not the sync part is in.
        Services.Clock.StepOn = TakeStepOnLane;
        Services.Clock.Stepped += RebaseOnStep;
        // Every node lists the sync section, a node without the sync part (or in no group) with no rows.
        routes.Index(new Sync.SyncIndex(this));
        if (Services.GroupSync is not { } sync)
            return;
        routes.OffLane("SYNC LEAD", c => sync.LeadAsync(c.Args, c.Ct));
        routes.OffLane("SYNC FOLLOW", c => sync.FollowAsync(c.Args, c.Ct));
        routes.Lane("SYNC RECEIPT", c => sync.Receipt(c.Args, c.Session));
        routes.Lane("LINK PATTERNS", c => LinkPatternsKept(sync, c));
        routes.Topic("ANCHORS", sync.OpenAnchors);
        AnchorPublished += (playlistId, json) =>
        {
            if (Store.Project.FindPlaylist(playlistId)?.SyncGroup is { } group)
                sync.Feed.Publish(group, playlistId, json);
        };
        Journal.Written += FanOutAir;
    }

    partial void TickSync(long leaderNs) => Services.GroupSync?.Tick(this, leaderNs);

    partial void SyncSync(Utf8JsonWriter w)
    {
        if (Services.GroupSync is not { } sync)
            return;
        var clock = Services.Clock;
        var role = sync.Role;
        w.WriteString("group", role == "solo" && sync.Settings.Role == "solo" ? "" : sync.Settings.Group);
        w.WriteString("feed", sync.FeedWord);
        w.WriteBoolean("slewing", clock.IsSlewing);
        w.WriteNumber("steps", Interlocked.Read(ref _syncSteps));
        w.WriteNumber("freewheelS", Math.Round(clock.HoldingNs / 1e9, 1));
        var picture = sync.Picture(IsAnimating);
        var rows = role == "leader" ? picture.Outputs : [.. sync.OwnRows()];
        w.WriteStartArray("outputs");
        foreach (var o in rows)
        {
            w.WriteStartObject();
            w.WriteString("node", o.Node);
            w.WriteString("output", o.Output);
            w.WriteString("state", o.State);
            w.WriteString("path", o.Path);
            w.WriteNumber("refreshHz", o.RefreshHz);
            w.WriteNumber("ppm", o.Ppm);
            w.WriteNumber("phaseUs", o.PhaseUs);
            w.WriteNumber("uncertaintyUs", o.UncertaintyUs);
            if (o.NextSlipS is { } slip)
                w.WriteNumber("nextSlipS", slip);
            else
                w.WriteNull("nextSlipS");
            w.WriteNumber("missed60s", o.Missed60s);
            w.WriteNumber("frameCount", o.FrameCount);
            w.WriteBoolean("heard", o.Heard);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("pairs");
        foreach (var p in picture.Pairs)
        {
            w.WriteStartObject();
            w.WriteString("a", p.A);
            w.WriteString("aNode", p.ANode);
            w.WriteString("b", p.B);
            w.WriteString("bNode", p.BNode);
            w.WriteNumber("phaseUs", p.PhaseUs);
            w.WriteNumber("uncertaintyUs", p.UncertaintyUs);
            if (p.NextSlipS is { } slip)
                w.WriteNumber("nextSlipS", slip);
            else
                w.WriteNull("nextSlipS");
            w.WriteBoolean("mixedRate", p.MixedRate);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        var (verdict, _, words) = SyncVerdict(sync, picture);
        w.WriteString("verdict", verdict);
        w.WriteString("words", words);
    }

    partial void ContextSync(List<ContextItem> items, List<ContextLink> links)
    {
        if (Services.GroupSync is not { } sync || sync.Settings.Group.Length == 0 || sync.Settings.Role == "solo")
            return;
        var group = sync.Settings.Group;
        var id = "sync:" + group;
        var picture = sync.Picture(IsAnimating);
        var (_, light, words) = SyncVerdict(sync, picture);
        items.Add(new ContextItem(id, "sync", "Sync " + group, light, words));
        var p = Store.Project;
        // The machine follows the group's clock on a follower; on the leader the group follows the machine.
        if (items.Exists(i => i.Id == "machine"))
            links.Add(sync.Role == "follower" ? new ContextLink("machine", id, ContextLinkKinds.Follows, light) : new ContextLink(id, "machine", ContextLinkKinds.Follows, light));
        if (sync.Role == "follower")
        {
            foreach (var list in p.Playlists.Where(l => string.Equals(l.SyncGroup, group, StringComparison.Ordinal)))
                links.Add(new ContextLink("playlist:" + list.Id, id, ContextLinkKinds.Follows, light));
        }
        var free = picture.Outputs.Where(o => o.Local && o.State is "free-measured" or "free-unknown" or "fell-out").Select(o => o.Output).ToHashSet(StringComparer.Ordinal);
        foreach (var o in p.Outputs.Where(o => o.Enabled && (sync.Role == "follower" || free.Contains(o.Id))))
            links.Add(new ContextLink("output:" + o.Id, id, ContextLinkKinds.Hears, light));
    }

    partial void RecoverySaveSync(IDictionary<string, string> into)
    {
        // The role is the node's, kept in its settings (NodeSettings.Sync), not in the recovery record.
    }

    partial void RecoveryLoadSync(IReadOnlyDictionary<string, string> from)
    {
        // The role is re-entered from the node's settings by the sync part at start.
    }

    /// <summary>The group's verdict as this node tells it: the board's on a leader; a follower's own link and clock (the verdict is
    /// the leader's); solo says how to join.</summary>
    private (string Verdict, Light Light, string Words) SyncVerdict(Sync.SyncNode sync, Net.Sync.SyncPicture picture)
    {
        switch (sync.Role)
        {
            case "leader":
                var members = sync.Members.Count;
                return (picture.Verdict, picture.Light, $"leading {sync.Settings.Group} with {members} member{(members == 1 ? "" : "s")}; {picture.Words}");
            case "follower":
                var light = Services.Clock.IsHolding || sync.FeedWord == "lost" ? Light.Red : sync.FeedWord == "live" ? Light.Green : Light.Amber;
                return ("unknown", light, sync.Words + (sync.FeedWord == "live" ? "" : " — the anchor feed is " + sync.FeedWord) + "; the verdict is the leader's (SYNC STATUS there)");
            default:
                return ("unknown", Light.Grey, sync.Settings.Role == "follow" ? sync.Words
                    : "not in a sync group — SYNC LEAD <group> here, or SYNC FOLLOW <group> <node>");
        }
    }

    /// <summary>LINK PATTERNS as the first round answers it; the FOLLOW-BLACKOUT switch is also kept for the next start.</summary>
    private CommandReply LinkPatternsKept(Sync.SyncNode sync, VerbCall c)
    {
        var reply = RouteRoundOne(c.Spec, c.Args, c.Line, c.Session, c.Ct);
        if (!reply.Ok || c.Args.Count < 2 || !c.Args[0].Equals("FOLLOW-BLACKOUT", StringComparison.OrdinalIgnoreCase))
            return reply;
        sync.PersistFollowBlackout(_air.FollowPatternsBlackout);
        return reply;
    }

    /// <summary>Every synced playlist's anchor as it is now, to a feed (a node that begins to lead: a member that joins later is put
    /// on the playing item). Any thread: published state only.</summary>
    internal void PublishAnchors(Net.Sync.AnchorFeed feed)
    {
        foreach (var list in Store.Project.Playlists)
        {
            if (list.SyncGroup is { } group && AnchorJson(list.Id) is { } json)
                feed.Publish(group, list.Id, json);
        }
    }

    /// <summary>The follow-blackout switch as the node's settings keep it, put on the air at start.</summary>
    internal Task RestoreFollowBlackoutAsync(bool on) => _lane.RunAsync(() =>
    {
        if (_air.FollowPatternsBlackout != on)
            SetAir(a => a with { FollowPatternsBlackout = on });
    });

    // ---- The group's frame lock ----------------------------------------------------------------------------------------------

    /// <summary>The frame lock as this node's outputs hold it (a leader's record for its members), or null with no render host.</summary>
    internal Sync.LockRecord? LockRecordNow() =>
        LockBackend is { } backend ? new Sync.LockRecord(backend.Intent.Apply, backend.Intent.SwapGroup, backend.Intent.Barrier, backend.LocalAnchor()) : null;

    /// <summary><see cref="AdoptLeaderLock"/> as one action on the lane (the armed and blackout facts it reads cannot move under it).</summary>
    /// <paramref name="current"/> is asked on the lane: a record a newer one replaced meanwhile is not taken.
    internal Task<(bool Taken, string Words)> AdoptLeaderLockAsync(Sync.LockRecord record, Func<bool> current) =>
        _lane.RunAsync(() => current() ? AdoptLeaderLock(record) : (false, "a newer record of the leader's lock came meanwhile"));

    /// <summary>A member takes its leader's lock record: the counter anchor always (so its counts map to the same show time), and the
    /// leader's wish — joining when the leader applied the lock, leaving when it released it — unless this node's frame lock is off,
    /// or the show is armed and not blacked out, which waits (<c>Taken</c> false) for a boundary as SYNC APPLY and SYNC LEAVE would.
    /// A node that has begun to stop takes nothing of it (the leader's record is a line from outside the node). On the lane.</summary>
    internal (bool Taken, string Words) AdoptLeaderLock(Sync.LockRecord record)
    {
        if (Stopping)
            return (true, StoppingWords);
        if (LockBackend is not { } backend)
            return (true, "no render host: the leader's frame lock is not taken here");
        // Each of the leader's anchors once (the counter takes it at its own count, so an offered one may not be active yet).
        if (record.Counter is { } counter && Interlocked.Exchange(ref _lockOffered, counter.Seq) != counter.Seq)
            backend.OfferAnchor(counter);
        if (FrameLockOff)
            return (true, FrameLockOffWords);
        if (record.Apply && !backend.Intent.Apply)
        {
            if (Services.ShowGate.Armed && !Air.EffectiveBlackout)
                return (false, "the leader applied the frame lock; this node is armed, so it joins at the next blackout or once disarmed");
            backend.Apply(Math.Max(1, record.SwapGroup), record.Barrier);
            Journal.Add("sync", "the group's leader applied the frame lock: this node's outputs join it");
            FactsChanged();
            return (true, "joining the leader's frame lock");
        }
        if (!record.Apply && backend.Intent.Apply)
        {
            // Leaving changes how every output presents: as a join, it waits for a boundary on an armed show.
            if (Services.ShowGate.Armed && !Air.EffectiveBlackout)
                return (false, "the leader's frame lock is released; this node is armed, so it leaves at the next blackout or once disarmed");
            backend.LeaveAll();
            Journal.Add("sync", "the group's leader released the frame lock: this node's outputs present free");
            FactsChanged();
            return (true, "left the frame lock with the leader");
        }
        return (true, "");
    }

    // ---- The clock's steps ------------------------------------------------------------------------------------------------

    /// <summary>Where the clock takes a step: this lane, so the step and the rebase are one action here. A lane that has stopped
    /// (the engine is going) takes it where it is.</summary>
    private void TakeStepOnLane(Action step) =>
        _ = _lane.RunAsync(step).ContinueWith(t =>
        {
            if (t.Exception?.InnerException is ObjectDisposedException)
                step();
            else
                Journal.Add("fault", "the show clock's step: " + Journal.Describe(t.Exception!.InnerException ?? t.Exception), ok: false);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>The leader clock stepped by Δ (on the lane): every playing or cued playlist's anchor moves by +Δ, so its media
    /// position is where it was; a group playlist that took its leader's anchor is on the leader's time line already and stays
    /// (when the clock now reads that leader's line). Every other instant the air holds moves with them in the same action: the
    /// identify deadline, the master's and the panic's fades and safe looks, the fades back from a panic, the panic's transport steps
    /// and lines still waiting for their instants, and a blind commit waiting for its instant — so nothing on the air jumps or fires
    /// early or late. STATE counts the step once all of this and its journal line are done.</summary>
    private void RebaseOnStep(long delta)
    {
        var p = Store.Project;
        var now = LeaderNowNs;
        var adopted = Services.Clock.IsFollowing ? Services.GroupSync?.Adopted ?? [] : [];
        RebaseBlind(delta, p, adopted);
        var kept = 0;
        foreach (var (id, lane) in Playback.Snapshot.Playlists)
        {
            if (lane.IsIdle || p.FindPlaylist(id) is not { } list)
                continue;
            if (list.SyncGroup is not null && adopted.Contains(id))
                continue;
            var a = lane.Clip.Anchor;
            if (a.LeaderNs is long.MinValue or long.MaxValue)
                continue;
            var clip = lane.Clip with { Anchor = a with { LeaderNs = a.LeaderNs + delta } };
            try
            {
                if (Playback.Adopt(list, lane.Index, clip, now).Ok)
                    kept++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One playlist that cannot take its moved anchor is that playlist's; the others and the air still move.
                Journal.Add("fault", $"{id} could not keep its place across the clock's step: {Journal.Describe(ex)}", ok: false);
            }
        }
        RebaseAir(delta);
        var who = Services.Clock.IsFollowing && Services.GroupSync is { Joining.Length: > 0 } s ? $"followed {s.Joining}: " : "";
        Journal.Add("sync", $"{who}show clock stepped {StepWords(delta)}; {kept} playlist{(kept == 1 ? "" : "s")} kept {(kept == 1 ? "its" : "their")} place");
        // Counted last: a reader that sees the count sees the step taken in full — every anchor and instant moved, the line written.
        Interlocked.Increment(ref _syncSteps);
        RaiseEvent("sync.stepped", now, w => w.WriteNumber("deltaNs", delta));
        FactsChanged();
    }

    /// <summary>A waiting blind commit's instant by +Δ, published at once (the render draws its root from that instant on): first, so
    /// no snapshot of the step pairs the moved clock with the old instant. One for a group playlist that took its leader's anchor is
    /// on the leader's time line already and stays, as that playlist does; one with no instant yet (waiting for a GO) has none to
    /// move.</summary>
    private void RebaseBlind(long delta, Project p, IReadOnlyCollection<string> adopted)
    {
        if (_blind is not { Commit: { } commit } b || commit.EffectiveLeaderNs is long.MinValue or long.MaxValue)
            return;
        if (commit.PlaylistId is { } id && p.FindPlaylist(id)?.SyncGroup is not null && adopted.Contains(id))
            return;
        b.Commit = commit with { EffectiveLeaderNs = commit.EffectiveLeaderNs + delta };
        TouchShow();
    }

    /// <summary>The air's own leader instants by +Δ (show's panic and master state, kept on this lane, and the stacks' degrade fades),
    /// then the levels the render thread reads rebuilt from them.</summary>
    private void RebaseAir(long delta)
    {
        static long At(long t, long d) => t is long.MinValue or long.MaxValue ? t : t + d;
        static AirFade Fade(AirFade f, long d) => f == AirFade.Full ? f : f with { StartLeaderNs = At(f.StartLeaderNs, d) };
        SetAir(a => a with
        {
            IdentifyUntilLeaderNs = a.IdentifyUntilLeaderNs == 0 ? 0 : a.IdentifyUntilLeaderNs + delta,
            Master = Fade(a.Master, delta),
            Panic = a.Panic is not { } panic ? null : panic with
            {
                SinceLeaderNs = At(panic.SinceLeaderNs, delta),
                Outputs = [.. panic.Outputs.Select(o => o with
                {
                    Level = Fade(o.Level, delta),
                    SafeFromLeaderNs = o.Safe is null ? o.SafeFromLeaderNs : At(o.SafeFromLeaderNs, delta),
                    SafeUntilLeaderNs = At(o.SafeUntilLeaderNs, delta),
                })],
            },
        });
        _returns = [.. _returns.Select(r => r with { Level = Fade(r.Level, delta), SafeUntilLeaderNs = At(r.SafeUntilLeaderNs, delta) })];
        for (var i = 0; i < _panicSteps.Count; i++)
            _panicSteps[i] = _panicSteps[i] with { AtLeaderNs = At(_panicSteps[i].AtLeaderNs, delta) };
        for (var i = 0; i < _panicLinesDue.Count; i++)
            _panicLinesDue[i] = _panicLinesDue[i] with { AtLeaderNs = At(_panicLinesDue[i].AtLeaderNs, delta) };
        Services.StackGains?.Rebase(delta);
        RebuildLevels();
    }

    private static string StepWords(long delta)
    {
        var sign = delta < 0 ? "−" : "+";
        var ns = Math.Abs((double)delta);
        if (ns < 1e9)
            return sign + (ns / 1e6).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " ms";
        if (ns < 60e9)
            return sign + (ns / 1e9).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s";
        var s = (long)Math.Round(ns / 1e9);
        return s < 3600 ? $"{sign}{s / 60} min {s % 60} s" : $"{sign}{s / 3600} h {s % 3600 / 60} min";
    }

    // ---- The group's air ---------------------------------------------------------------------------------------------------

    /// <summary>A leader's own air line (journalled as run) to every member, at the instant it ran here; each member's receipt is
    /// journalled when it comes.</summary>
    private void FanOutAir(JournalRow row)
    {
        if (row.Kind == "verb" && row.Ok && row.Line is not null)
            FanOutAirLine(row.Line, journalReceipts: true);
    }

    /// <summary>A line of a binding's gesture (a fader's or an encoder's move) that ran here: the journal keeps the gesture as one row,
    /// not a verb row per line, yet each of its lines is the group's air (a fader on MASTER) and goes to every member as it runs here.
    /// Its members' receipts are not journalled line by line, as the sweep itself is not.</summary>
    private void FanOutGestureLine(WireLine line) => FanOutAirLine(VerbCatalog.Redacted(line), journalReceipts: false);

    /// <summary>A leader's own line that ran here to every member as its air line, at the instant it ran here (see
    /// <see cref="Sync.SyncNode.FanoutLine"/>); each member's receipt journalled when it comes, when <paramref name="journalReceipts"/>.</summary>
    private void FanOutAirLine(string line, bool journalReceipts)
    {
        // Sent with no member too: the feed keeps it as its kind's latest for a member that joins (or comes back) later.
        if (Services.GroupSync is not { Leads: true } sync)
            return;
        if (Sync.SyncNode.FanoutLine(line, _air.Blackout, Services.Wall.UnixMs, Store.Project.Show.Rules.PanicLeadMs) is not { } text)
            return;
        var at = LeaderNowNs;
        // Pushed here, in the order this session's lines ran (the journal's row is written before the line is answered); only the
        // receipts are waited for beside it.
        Task<IReadOnlyList<FanoutReceipt>> sent;
        try
        {
            sent = sync.SendAsync(text, at, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Journal.Add("fault", $"{text} to the group: {Journal.Describe(ex)}", ok: false);
            return;
        }
        _ = JournalReceiptsAsync(text, sent, journalReceipts);
    }

    private async Task JournalReceiptsAsync(string text, Task<IReadOnlyList<FanoutReceipt>> sent, bool journalReceipts)
    {
        try
        {
            var receipts = await sent.ConfigureAwait(false);
            if (!journalReceipts)
                return;
            foreach (var r in receipts)
                Journal.Add(r.Stage == "refused" ? "refused" : "fleet", $"{text} on {r.Name}: {r.Stage}{(r.Words.Length > 0 ? " — " + r.Words : "")}", ok: r.Stage is not ("refused" or "silent"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Journal.Add("fault", $"{text} to the group: {Journal.Describe(ex)}", ok: false);
        }
    }

    /// <summary>A verb answered on the lane (a package's lane route or a first-round row), so a member can run it pinned to an instant.</summary>
    internal bool IsLaneVerb(VerbSpec spec) => !_routes.TryOffLane(spec.Name, out _);

    /// <summary>A leader's air line on this member, run as if at the leader's instant (its fades start on the same frames); a verb
    /// answered off the lane runs as any line does. Answers the reply and the leader instant the line took effect here: the
    /// leader's instant, or this node's own now when the line ran after it (a switch such as BLACKOUT lands when it runs).</summary>
    internal async Task<(CommandReply Reply, long RanAtLeaderNs)> RunAirLineAsync(WireLine line, VerbSpec spec, long atLeaderNs, SessionInfo session)
    {
        if (IsLaneVerb(spec))
            return await _lane.RunAsync(() => RunAirLineOnLane(line, atLeaderNs, session)).ConfigureAwait(false);
        var ran = Math.Max(atLeaderNs, LeaderNowNs);
        return (await HandleAsync(line, session, CancellationToken.None).ConfigureAwait(false), ran);
    }

    /// <summary>Runs a line on the lane with now pinned to <paramref name="atLeaderNs"/>; journalled as the router journals. Answers the
    /// reply and the instant it took effect (the later of the pinned instant and the lane's own now).</summary>
    internal (CommandReply Reply, long RanAtLeaderNs) RunAirLineOnLane(WireLine line, long atLeaderNs, SessionInfo session)
    {
        var ran = Math.Max(atLeaderNs, LeaderNowNs);
        // A leader's PANIC from no later than the panic this node is in is that press again (handed anew to a member that came back):
        // pressed here a second time it would turn the panic hard.
        if (_air.Panic is { } panic && atLeaderNs <= panic.SinceLeaderNs && VerbCatalog.Match(line) is { Spec.Name: "PANIC" })
            return (CommandReply.Fail($"already in this panic since {Seconds(panic.SinceUnixMs)} — the leader's press at that instant ran here"), ran);
        CommandReply reply;
        try
        {
            using (Services.Clock.Pin(atLeaderNs))
                reply = Route(line, session, CancellationToken.None);
        }
        catch (EditRefusedException refused)
        {
            reply = CommandReply.Fail(refused.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Journal.Add("fault", $"{line.Verb}: {Journal.Describe(ex)}", session.ClientName, VerbCatalog.Redacted(line), false);
            return (CommandReply.Fail($"the node faulted on this line ({ex.GetType().Name}) — logged"), ran);
        }
        if (!reply.Silent && IsJournalled(line))
            Journal.Add(reply.Ok ? "verb" : "refused", reply.Ok ? (reply.Effect.Length > 0 ? reply.Effect : VerbCatalog.Redacted(line)) : reply.Payload ?? "refused",
                session.ClientName, VerbCatalog.Redacted(line), reply.Ok);
        return (reply, ran);
    }

    /// <summary>A leader's anchor taken through this node's own PLAYLIST ANCHOR, for the playlist of that id in the group, or else —
    /// when <paramref name="oneOnLeader"/> (the leader has shown one playlist for the group) and the group has one playlist here — that
    /// one; answers the playlists now at the leader's anchor, and the words when it had none to take it.</summary>
    internal async Task<(IReadOnlyList<string> Adopted, string Unmatched)> AdoptLeaderAnchorAsync(string group, string playlistId, string json,
        bool oneOnLeader, SessionInfo session)
    {
        var lists = Store.Project.Playlists.Where(l => string.Equals(l.SyncGroup, group, StringComparison.Ordinal)).ToList();
        var same = lists.Where(l => l.Id == playlistId).ToList();
        var targets = same.Count > 0 ? same : oneOnLeader && lists.Count == 1 ? lists : [];
        if (targets.Count == 0)
        {
            return ([], lists.Count == 0
                ? $"no playlist here is in the group {group}"
                : $"no playlist here has the leader's id {playlistId}, and {(lists.Count > 1 ? $"{lists.Count} playlists here" : "the leader's several playlists")} could take it — give the group's playlists the same ids on every node");
        }
        var adopted = new List<string>();
        foreach (var list in targets)
        {
            if (!WireGrammar.TryParse($"PLAYLIST ANCHOR {list.Id} {json}", out var line, out _))
                continue;
            var reply = await HandleAsync(line, session, CancellationToken.None).ConfigureAwait(false);
            if (reply.Ok || reply.Payload == "already at that anchor")
                adopted.Add(list.Id);
        }
        return (adopted, "");
    }
}
