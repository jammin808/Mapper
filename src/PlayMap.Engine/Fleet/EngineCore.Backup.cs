using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Core.Wire;
using PlayMap.Engine.Fleet;

namespace PlayMap.Engine;

/// <summary>wp2-backup's hooks into the engine (the fleet's continuation): the hot backup's verbs, the mirror feed and its lines,
/// the fleet's door on a backup or a fenced node, the routes a takeover switches, STATE's <c>fleet</c> role, mirror, backups and
/// last takeover, and the context's word when a group's leader is gone.</summary>
public sealed partial class EngineCore
{
    /// <summary>This node's role, primary and epoch as the backup part keeps them (the settings' when the part is not in).</summary>
    internal FleetSettings FleetRole => Services.Backup?.Settings ?? Services.Settings.Fleet;

    /// <summary>What this node may put on the network beside its own outputs now — its LED data, its video sends — for its fleet role,
    /// and the words STATE says when that is nothing (live without the fleet's part).</summary>
    private volatile NetworkAirNow? _networkAir;

    private sealed record NetworkAirNow(NetworkAir Air, string Words);

    private NetworkAirNow NetworkAirFact => _networkAir ??= Services.Backup is { } b ? new(b.Air, b.AirWords) : new(NetworkAir.Live, "");

    internal NetworkAir NetworkAir => NetworkAirFact.Air;

    internal string NetworkAirWords => NetworkAirFact.Words;

    /// <summary>The fleet role moves what the node may send (the backup part, before the role is the node's): the LED sender and the
    /// sends take it at once — a backup's go silent with no black frame, a fenced node's go black once and then silent, the new
    /// primary's start on the show clock's grid.</summary>
    internal void SetNetworkAir(NetworkAir air, string words)
    {
        var moved = NetworkAirFact.Air != air;
        _networkAir = new NetworkAirNow(air, words);
        if (moved)
            Led.SetAir(air);
        VideoIoMade?.Reconcile(Store.Project);
        FactsChanged();
    }

    private void RouteBackup(VerbRoutes routes)
    {
        // Every node lists the fleet section, a node without the backup part (or a solo one) with no rows.
        routes.Index(new BackupIndex(this));
        if (Services.Backup is not { } backup)
            return;
        routes.OffLane("FLEET BACKUP", c => backup.BackupAsync(c.Args, c.Ct));
        routes.OffLane("FLEET SOLO", c => backup.SoloAsync(c.Args, c.Ct));
        routes.OffLane("FAILOVER", c => backup.FailoverAsync(c.Args, c.Ct));
        routes.Lane("FAILBACK", c => backup.Failback(c.Args));
        routes.OffLane("FLEET FENCE", c => backup.FenceAsync(c.Args, c.Ct));
        routes.OffLane("FLEET PROMOTE", c => backup.PromoteAsync(c.Args, c.Ct));
        routes.OffLane("MIRROR", backup.MirrorAsync);
        routes.OffLane("FLEET ROUTE", FleetRouteAsync);
        routes.Topic("MIRROR", backup.OpenMirror);
        StateChanged += _ => backup.PublishProject();
        var last = Playback.Snapshot;
        Playback.Changed += snap =>
        {
            var before = last;
            last = snap;
            foreach (var (id, lane) in snap.Playlists)
            {
                if (lane.IsIdle || (before.Playlists.TryGetValue(id, out var old) && old.Index == lane.Index && old.Clip == lane.Clip))
                    continue;
                if (AnchorJson(id) is { } json)
                    backup.PublishAnchor(id, json);
            }
        };
        Journal.Written += row =>
        {
            if (row.Kind != "verb" || !row.Ok || row.Line is not { } line)
                return;
            var carried = Services.GroupSync is { Leads: true }
                && Sync.SyncNode.FanoutLine(line, _air.Blackout, Services.Wall.UnixMs, Store.Project.Show.Rules.PanicLeadMs) is not null;
            backup.PublishAir(line, LeaderNowNs, carried);
        };
    }

    partial void GateFleet(VerbSpec spec, WireLine line, SessionInfo session, ref CommandReply? refused)
    {
        if (Services.Backup?.Refused(spec, line, session) is { } words)
            refused = CommandReply.Fail(words);
    }

    /// <summary>Every playlist's anchor as it is now, to the mirror feed (a node that becomes the show's primary).</summary>
    internal void PublishMirrorAnchors(BackupNode backup)
    {
        foreach (var list in Store.Project.Playlists)
        {
            if (AnchorJson(list.Id) is { } json)
                backup.PublishAnchor(list.Id, json);
        }
    }

    /// <summary>The primary's anchor for a playlist, taken exactly (any playlist: the mirror's show is the primary's), on the lane.
    /// The same anchor twice changes nothing and says nothing.</summary>
    internal Task<CommandReply> AdoptMirrorAnchorAsync(string playlistId, string json) => _lane.RunAsync(() =>
    {
        if (Store.Project.FindPlaylist(playlistId) is not { } list)
            return CommandReply.Fail($"the primary's playlist {playlistId} is not in this node's show yet");
        try
        {
            using var doc = JsonDocument.Parse(json);
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
            return ok || words == "already at that anchor" ? CommandReply.NoReply(words) : CommandReply.Fail(words);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return CommandReply.Fail("the anchor JSON is not one this node reads: " + ex.Message.Split('.')[0]);
        }
    });

    /// <summary>MEDIA FETCH through the fleet's pulls, with the peer's proof over its pinned HTTPS (never its token, never in clear):
    /// <c>&lt;media&gt; VERSION &lt;version&gt; [FROM &lt;node&gt;]</c> one optimised version by hash, <c>&lt;media&gt;|ALL [FROM &lt;node&gt;]</c> the missing
    /// files. Null on a node without the fleet's part (the first round's pull answers it there).</summary>
    private CommandReply? FleetFetch(List<string> args)
    {
        if (Services.Backup is not { } backup)
            return null;
        const string Syntax = "MEDIA FETCH <media>|ALL [VERSION <version>] [FROM <node>]";
        var words = args.ToList();
        string? from = null;
        var fi = words.FindIndex(a => a.Equals("FROM", StringComparison.OrdinalIgnoreCase));
        if (fi >= 0)
        {
            if (fi + 2 != words.Count)
                return CommandReply.Fail(Syntax);
            from = words[fi + 1];
            words.RemoveRange(fi, 2);
        }
        var p = Store.Project;
        if (words.Count == 3 && words[1].Equals("VERSION", StringComparison.OrdinalIgnoreCase))
        {
            if (p.FindMedia(words[0]) is not { } item)
                return CommandReply.Fail($"no media {words[0]}");
            if (item.Versions.FirstOrDefault(v => v.Id.Equals(words[2], StringComparison.OrdinalIgnoreCase)) is not { } version)
                return CommandReply.Fail($"{item.Name} has no version {words[2]} — MEDIA GET {item.Id} lists its versions");
            // The answer is the pull's start: the pull itself reports to the journal when it lands.
            return backup.FetchVersion(item, version, from);
        }
        if (words.Count != 1)
            return CommandReply.Fail(Syntax);
        var all = words[0].Equals("ALL", StringComparison.OrdinalIgnoreCase);
        var wanted = all ? p.Media.Where(m => m.IsFile).ToList() : p.FindMedia(words[0]) is { } one ? [one] : [];
        if (wanted.Count == 0)
            return CommandReply.Fail(all ? "nothing is missing" : $"no media {words[0]}");
        return backup.FetchMedia(wanted, from);
    }

    // ---- The files the show names beside it ---------------------------------------------------------------------------------

    /// <summary>A file the live show names beside its project folder, by its mirror key, for a backup's pull: its full path when the
    /// show names it, it lies inside the folder and it is here; null otherwise (nothing the show does not name is ever served).</summary>
    internal string? ShowFilePath(string key)
    {
        var file = ShowFiles.Of(Store.Project).FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
        return file is not null && Align.AlignArchive.Resolve(ProjectFolderForFiles, file.Path) is { } path && File.Exists(path) ? path : null;
    }

    /// <summary>The folder the show's files go to on a hot backup: its own project's (or the node's), else
    /// <paramref name="fallback"/>, made and kept for this run.</summary>
    internal string MirrorFilesFolder(string fallback)
    {
        if (ProjectFolderForFiles is { } folder)
            return folder;
        Directory.CreateDirectory(fallback);
        _filesFolder = fallback;
        return fallback;
    }

    /// <summary>Files the show names landed after the show did (a backup's late pull): meshes that failed to load are read again, the
    /// measured maps are read again, and the render host builds its stage tables again for the show in hand.</summary>
    internal void ShowFilesLanded()
    {
        Meshes.ForgetFailed();
        AlignFiles.Reload(Store.Project, ProjectFolderForFiles);
        (Services.Outputs as Rendering.RenderHost)?.Stage?.Rebuild();
        FactsChanged();
    }

    // ---- Routes -------------------------------------------------------------------------------------------------------------

    /// <summary>FLEET ROUTE &lt;output&gt; DEVICE &lt;device&gt; &lt;input&gt; &lt;backup input&gt; | &lt;output&gt; NONE | TEST: what a takeover switches,
    /// kept in the show's plan (this node's own input is the primary's), or each route read without switching anything.</summary>
    private async Task<CommandReply> FleetRouteAsync(VerbCall call)
    {
        var a = call.Args;
        if (a.Count >= 1 && a[0].Equals("TEST", StringComparison.OrdinalIgnoreCase))
            return a.Count == 1 ? await RouteTestAsync().ConfigureAwait(false) : CommandReply.Fail("FLEET ROUTE TEST takes no words after it");
        const string Syntax = "FLEET ROUTE <output> DEVICE <device> <input> <backup input> | FLEET ROUTE <output> NONE | FLEET ROUTE TEST";
        if (a.Count < 2)
            return CommandReply.Fail(Syntax);
        var p = Store.Project;
        if (p.FindOutput(a[0]) is not { } output)
            return CommandReply.Fail($"no output {a[0]} — OUTPUT LIST names them");
        FleetRoute? route;
        if (a.Count == 2 && a[1].Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            route = null;
        }
        else if (a.Count == 5 && a[1].Equals("DEVICE", StringComparison.OrdinalIgnoreCase))
        {
            if (p.FindDevice(a[2]) is not { } device)
                return CommandReply.Fail($"no projector link {a[2]} — DEVICE LIST names them");
            if (a[3].Equals(a[4], StringComparison.OrdinalIgnoreCase))
                return CommandReply.Fail($"the primary's input and the backup's are both {a[3]}: a takeover would switch nothing");
            route = new FleetRoute(output.Id, device.Id, a[3], a[4], Services.Settings.NodeId);
        }
        else
        {
            return CommandReply.Fail(Syntax);
        }
        var had = p.Fleet.Routes.FirstOrDefault(r => r.OutputId == output.Id);
        if (route is null && had is null)
            return CommandReply.Fail($"{output.Name} has no route to remove");
        if (route is not null && had == route)
            return CommandReply.Fail($"{output.Name}'s route is that already");
        var routes = p.Fleet.Routes.RemoveAll(r => r.OutputId == output.Id);
        if (route is not null)
            routes = routes.Add(route);
        var label = route is null
            ? $"{output.Name}: no route (switched by hand)"
            : $"{output.Name}: {p.FindDevice(route.DeviceId)!.Name} on {route.PrimaryInput} shows this node, on {route.BackupInput} its backup";
        var r = await EditAsync(call.Session, label, x => x with { Fleet = x.Fleet with { Routes = routes } }).ConfigureAwait(false);
        if (!r.Changed)
            return FromEdit(r);
        return CommandReply.Success(JsonOut(w => WriteRoute(w, route ?? had!, route is null)), label);
    }

    /// <summary>FLEET ROUTE TEST: each route's projector asked for its input now (reachability and the side it shows), nothing
    /// switched; answered when every read is back (2 s each at most).</summary>
    private async Task<CommandReply> RouteTestAsync()
    {
        var p = Store.Project;
        var routes = p.Fleet.Routes;
        if (routes.IsDefaultOrEmpty)
            return CommandReply.Fail("no routes — FLEET ROUTE <output> DEVICE <device> <input> <backup input> on the primary first");
        if (Services.Devices is not { } devices)
            return CommandReply.Fail("projector control is not on this node: its routes cannot be read");
        var rows = await Task.WhenAll(routes.Select(r => RouteReadAsync(devices, r, p.FindDevice(r.DeviceId)?.Name ?? r.DeviceId, Services.FleetTime))).ConfigureAwait(false);
        var words = string.Join("; ", rows);
        _routeTest = new TakeoverRecord("route-test", "", Services.Wall.UnixMs, words);
        FactsChanged();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartArray();
            for (var i = 0; i < routes.Length; i++)
            {
                w.WriteStartObject();
                w.WriteString("output", routes[i].OutputId);
                w.WriteString("device", routes[i].DeviceId);
                w.WriteString("words", rows[i]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }), "route test: " + words);
    }

    /// <summary>"P1 on 31 (primary) · reachable", or why it was not: 2 s on the fleet's clock <paramref name="time"/> at most.</summary>
    internal static async Task<string> RouteReadAsync(Devices.DeviceService devices, FleetRoute route, string name, TimeProvider time)
    {
        if (route.DeviceId.Length == 0)
            return name + ": no route (switched by hand)";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2), time);
        try
        {
            await devices.SyncLinksAsync().ConfigureAwait(false);
            var reading = await devices.Manager.ReadAsync(route.DeviceId, (d, ct) => d.ReadStatusAsync(ct), deadline.Token).ConfigureAwait(false);
            if (!reading.Reached)
                return $"{name} · unreachable ({reading.Result.Words})";
            var input = reading.State?.Input;
            if (input is null)
                return $"{name} · reachable, its input not read";
            var side = route.Side(input);
            return $"{name} on {input}{(side.Length > 0 ? $" ({side})" : " (neither route input)")} · reachable";
        }
        catch (OperationCanceledException)
        {
            return $"{name} · unreachable (no answer within 2 s)";
        }
    }

    private static void WriteRoute(Utf8JsonWriter w, FleetRoute r, bool removed)
    {
        w.WriteStartObject();
        w.WriteString("output", r.OutputId);
        w.WriteString("device", r.DeviceId);
        w.WriteString("input", r.PrimaryInput);
        w.WriteString("backupInput", r.BackupInput);
        w.WriteString("primaryNode", r.PrimaryNode);
        w.WriteBoolean("removed", removed);
        w.WriteEndObject();
    }

    /// <summary>The last route test (its unix ms and words); written once per test, read by STATE.</summary>
    private volatile TakeoverRecord? _routeTest;

    // ---- STATE ---------------------------------------------------------------------------------------------------------------

    /// <summary>STATE.fleet's role keys: role, epoch, state, of, backups, mirror, succession, alarm, last.</summary>
    private void WriteBackupState(Utf8JsonWriter w)
    {
        var role = FleetRole;
        var backup = Services.Backup;
        var mirror = backup?.Mirror();
        w.WriteString("role", role.Role);
        w.WriteNumber("epoch", role.Epoch);
        w.WriteString("state", role.Role switch
        {
            FleetRoles.Fenced => "fenced",
            FleetRoles.Backup => mirror?.State ?? "joining",
            _ => "serving",
        });
        if (role.Of.Length == 0)
            w.WriteNull("of");
        else
            w.WriteString("of", role.Of);
        w.WriteStartArray("backups");
        var peers = Services.FleetPeers?.Peers() ?? [];
        foreach (var m in backup?.Feed.Members ?? [])
        {
            var facts = peers.FirstOrDefault(p => p.Id == m.Node)?.Health?.Facts;
            var state = facts is { Role: FleetRoles.Backup } f && f.Mirror.Length > 0 ? f.Mirror : "joining";
            w.WriteStartObject();
            w.WriteString("node", m.Node);
            w.WriteString("name", facts?.Name ?? m.Name);
            w.WriteString("state", state);
            w.WriteString("light", ShowContext.LightWord(BackupNode.MirrorLight(state)));
            w.WriteString("words", state == "identical" ? $"{facts?.Name ?? m.Name} mirrors this node: identical" : $"{facts?.Name ?? m.Name} is {state}");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (mirror is null)
        {
            w.WriteNull("mirror");
        }
        else
        {
            w.WriteStartObject("mirror");
            w.WriteString("of", mirror.Of);
            w.WriteString("name", mirror.Name);
            w.WriteString("state", mirror.State);
            w.WriteNumber("rev", mirror.Rev);
            w.WriteNumber("behindRev", mirror.BehindRev);
            w.WriteString("words", mirror.Words);
            w.WriteEndObject();
        }
        w.WriteStartArray("succession");
        foreach (var node in Store.Project.Fleet.Succession)
            w.WriteStringValue(node);
        w.WriteEndArray();
        w.WriteNull("alarm");
        if (backup?.Last is not { } last)
        {
            w.WriteNull("last");
        }
        else
        {
            w.WriteStartObject("last");
            w.WriteString("kind", last.Kind);
            w.WriteString("id", last.Id);
            w.WriteNumber("at", last.AtUnixMs);
            w.WriteString("words", last.Words);
            w.WriteEndObject();
        }
    }

    /// <summary>STATE.fleet.routes: each route of the show, and the last route test's words; then the door's words (null on a node with
    /// a show of its own).</summary>
    private void WriteRoutesState(Utf8JsonWriter w)
    {
        w.WriteStartArray("routes");
        foreach (var r in Store.Project.Fleet.Routes)
        {
            w.WriteStartObject();
            w.WriteString("output", r.OutputId);
            w.WriteString("device", r.DeviceId);
            w.WriteString("input", r.PrimaryInput);
            w.WriteString("backupInput", r.BackupInput);
            w.WriteString("primaryNode", r.PrimaryNode);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (_routeTest is not { AtUnixMs: var at, Words: var words })
        {
            w.WriteNull("routeTest");
        }
        else
        {
            w.WriteStartObject("routeTest");
            w.WriteNumber("at", at);
            w.WriteString("words", words);
            w.WriteEndObject();
        }
        if (Services.Backup?.DoorWords is { } door)
            w.WriteString("door", door);
        else
            w.WriteNull("door");
    }

    /// <summary>The context's word when this follower's group leader is gone and no node has taken the lead: who should act.</summary>
    private void ContextBackup(List<ContextItem> items, List<ContextLink> links)
    {
        if (Services.GroupSync is { Role: "follower" } sync && Services.Clock.IsHolding)
        {
            // A hot backup takes its silent primary's show with FAILOVER; any other follower's group needs a new leader.
            items.Add(new ContextItem("fleet:leader", "fleet", "Leader " + sync.Settings.Group, Light.Red,
                FleetRole.Role == FleetRoles.Backup ? $"primary gone — FAILOVER on this backup to take its show"
                    : "leader gone — FLEET PROMOTE on the node that should lead"));
            if (items.Exists(i => i.Id == "machine"))
                links.Add(new ContextLink("machine", "fleet:leader", ContextLinkKinds.Follows, Light.Red));
        }
        var role = FleetRole;
        if (role.Role is FleetRoles.Backup or FleetRoles.Fenced && Services.Backup?.Mirror() is var mirror)
        {
            var light = role.Role == FleetRoles.Fenced ? Light.Red : BackupNode.MirrorLight(mirror?.State);
            var words = role.Role == FleetRoles.Fenced
                ? string.Create(CultureInfo.InvariantCulture, $"fenced: {role.Of} has the show (epoch {role.Epoch}) — outputs held black")
                : mirror?.Words ?? "";
            items.Add(new ContextItem("mirror:" + role.Of, "mirror", role.Role == FleetRoles.Fenced ? "Fenced" : "Mirror of " + (mirror?.Name ?? role.Of), light, words));
            if (items.Exists(i => i.Id == "machine"))
                links.Add(new ContextLink("machine", "mirror:" + role.Of, ContextLinkKinds.Mirrors, light));
        }
    }
}
