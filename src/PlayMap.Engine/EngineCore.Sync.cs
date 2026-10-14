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

/// <summary>wp2-sync's hooks into the engine (EngineCore.Modules.cs): the group's clock, anchors and vblank verdict.</summary>
public sealed partial class EngineCore
{
    partial void RouteSync(VerbRoutes routes)
    {
        // wp2-sync fills this.
    }

    partial void TickSync(long leaderNs)
    {
        // wp2-sync fills this.
    }

    partial void SyncSync(Utf8JsonWriter w)
    {
        // wp2-sync fills this.
    }

    partial void ContextSync(List<ContextItem> items, List<ContextLink> links)
    {
        // wp2-sync fills this.
    }

    partial void RecoverySaveSync(IDictionary<string, string> into)
    {
        // wp2-sync fills this.
    }

    partial void RecoveryLoadSync(IReadOnlyDictionary<string, string> from)
    {
        // wp2-sync fills this.
    }
}
