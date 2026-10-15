using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Wire;

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
            var (ok, words) = Playback.Adopt(list, index, clip, LeaderNowNs);
            return ok ? CommandReply.Success(effect: words) : CommandReply.Fail(words);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return CommandReply.Fail("the anchor JSON is not one this node reads: " + ex.Message.Split('.')[0]);
        }
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

    partial void RouteSync(VerbRoutes routes)
    {
        // Every engine takes its clock's steps on this lane, with the rebase, whether or not the sync part is in.
        Services.Clock.StepOn = TakeStepOnLane;
        Services.Clock.Stepped += RebaseOnStep;
        if (Services.GroupSync is not { } sync)
            return;
        routes.OffLane("SYNC LEAD", c => sync.LeadAsync(c.Args, c.Ct));
        routes.OffLane("SYNC FOLLOW", c => sync.FollowAsync(c.Args, c.Ct));
        routes.Lane("SYNC RECEIPT", c => sync.Receipt(c.Args, c.Session));
        routes.Lane("LINK PATTERNS", c => LinkPatternsKept(sync, c));
        routes.Topic("ANCHORS", sync.OpenAnchors);
        routes.Index(new Sync.SyncIndex(this, sync));
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
        w.WriteStartArray("members");
        foreach (var m in sync.Members)
            w.WriteStringValue(m);
        w.WriteEndArray();
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
        var (verdict, light, words) = SyncVerdict(sync, picture);
        w.WriteString("verdict", verdict);
        w.WriteBoolean("mixedRate", picture.MixedRate);
        w.WriteString("light", ShowContext.LightWord(light));
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
        var kept = sync.PersistFollowBlackout(_air.FollowPatternsBlackout);
        return kept.Length == 0 ? reply : reply with { Effect = reply.Effect + kept };
    }

    /// <summary>The follow-blackout switch as the node's settings keep it, put on the air at start.</summary>
    internal Task RestoreFollowBlackoutAsync(bool on) => _lane.RunAsync(() =>
    {
        if (_air.FollowPatternsBlackout != on)
            SetAir(a => a with { FollowPatternsBlackout = on });
    });

    // ---- The clock's steps ------------------------------------------------------------------------------------------------

    /// <summary>Where the clock takes a step: this lane, so the step and the rebase are one action here. A lane that has stopped
    /// (the engine is going) takes it where it is.</summary>
    private void TakeStepOnLane(Action step) =>
        _ = _lane.RunAsync(step).ContinueWith(t =>
        {
            if (t.Exception?.InnerException is ObjectDisposedException)
                step();
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>The leader clock stepped by Δ (on the lane): every playing or cued playlist's anchor moves by +Δ, so its media
    /// position is where it was; a group playlist that took its leader's anchor is on the leader's time line already and stays.</summary>
    private void RebaseOnStep(long delta)
    {
        Interlocked.Increment(ref _syncSteps);
        var p = Store.Project;
        var now = LeaderNowNs;
        var adopted = Services.GroupSync?.Adopted ?? [];
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
            if (Playback.Adopt(list, lane.Index, clip, now).Ok)
                kept++;
        }
        if (_air.IdentifyUntilLeaderNs != 0)
            SetAir(a => a with { IdentifyUntilLeaderNs = a.IdentifyUntilLeaderNs + delta });
        var who = Services.GroupSync is { Role: "follower" } s ? $"followed {s.Settings.Leader}: " : "";
        Journal.Add("sync", $"{who}show clock stepped {StepWords(delta)}; {kept} playlist{(kept == 1 ? "" : "s")} kept {(kept == 1 ? "its" : "their")} place");
        RaiseEvent("sync.stepped", now, w => w.WriteNumber("deltaNs", delta));
        FactsChanged();
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
        if (row.Kind != "verb" || !row.Ok || row.Line is null || Services.GroupSync is not { Leads: true } sync || sync.Members.Count == 0)
            return;
        if (Sync.SyncNode.FanoutLine(row.Line, _air.Blackout) is not { } text)
            return;
        var at = LeaderNowNs;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var r in await sync.SendAsync(text, at, CancellationToken.None).ConfigureAwait(false))
                    Journal.Add(r.Stage == "refused" ? "refused" : "fleet", $"{text} on {r.Name}: {r.Stage}{(r.Words.Length > 0 ? " — " + r.Words : "")}", ok: r.Stage is not ("refused" or "silent"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Journal.Add("fault", $"{text} to the group: {Journal.Describe(ex)}", ok: false);
            }
        }, CancellationToken.None);
    }

    /// <summary>A verb answered on the lane (a package's lane route or a first-round row), so a member can run it pinned to an instant.</summary>
    internal bool IsLaneVerb(VerbSpec spec) => !_routes.TryOffLane(spec.Name, out _);

    /// <summary>A leader's air line on this member, run as if at the leader's instant (its fades start on the same frames); a verb
    /// answered off the lane runs as any line does.</summary>
    internal Task<CommandReply> RunAirLineAsync(WireLine line, VerbSpec spec, long atLeaderNs, SessionInfo session) =>
        IsLaneVerb(spec)
            ? _lane.RunAsync(() => RunAirLineOnLane(line, atLeaderNs, session))
            : HandleAsync(line, session, CancellationToken.None).AsTask();

    /// <summary>Runs a line on the lane with now pinned to <paramref name="atLeaderNs"/>; journalled as the router journals.</summary>
    internal CommandReply RunAirLineOnLane(WireLine line, long atLeaderNs, SessionInfo session)
    {
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
            return CommandReply.Fail($"the node faulted on this line ({ex.GetType().Name}) — logged");
        }
        if (!reply.Silent && IsJournalled(line))
            Journal.Add(reply.Ok ? "verb" : "refused", reply.Ok ? (reply.Effect.Length > 0 ? reply.Effect : VerbCatalog.Redacted(line)) : reply.Payload ?? "refused",
                session.ClientName, VerbCatalog.Redacted(line), reply.Ok);
        return reply;
    }

    /// <summary>A leader's anchor taken through this node's own PLAYLIST ANCHOR, for the playlist of that id in the group, or else
    /// every playlist of the group here; answers the playlists now at the leader's anchor.</summary>
    internal async Task<IReadOnlyList<string>> AdoptLeaderAnchorAsync(string group, string playlistId, string json, SessionInfo session)
    {
        var lists = Store.Project.Playlists.Where(l => string.Equals(l.SyncGroup, group, StringComparison.Ordinal)).ToList();
        var targets = lists.Where(l => l.Id == playlistId).ToList() is { Count: > 0 } same ? same : lists;
        var adopted = new List<string>();
        foreach (var list in targets)
        {
            if (!WireGrammar.TryParse($"PLAYLIST ANCHOR {list.Id} {json}", out var line, out _))
                continue;
            var reply = await HandleAsync(line, session, CancellationToken.None).ConfigureAwait(false);
            if (reply.Ok || reply.Payload == "already at that anchor")
                adopted.Add(list.Id);
        }
        return adopted;
    }
}
