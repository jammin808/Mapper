using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Engine.Fleet;
using PlayMap.Engine.Host;
using PlayMap.Engine.Sync;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;
using PlayMap.Net.Fleet;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>Nodes for the hot-backup tests: each a real node on loopback with the fleet (monitor on its own hand-moved clock, the
/// backup's watch fast), the sync part (fast, synthetic vblanks none) and the operations parts (the credential store, projector
/// links); every node asks the pairing token even from this machine, so a backup proves it like a machine across the room.</summary>
internal static class BackupRig
{
    public const string PrimaryId = SyncRig.LeaderId;
    public const string BackupId = SyncRig.FollowerId;
    public const string ThirdId = "c0ffee00";

    /// <param name="feeds">The fleet monitor's transport, when a test stands between the monitor and its peers' health.</param>
    public static EngineParts Parts(ManualTime time, Func<FleetPinFile, IHealthFeeds>? feeds = null)
    {
        var fleet = new FleetOptions
        {
            Time = time,
            Reader = new UnknownMachineReader("not read in tests"),
            Backup = new BackupOptions { WatchEvery = TimeSpan.FromMilliseconds(20) },
            Feeds = feeds,
        };
        var parts = new EngineParts
        {
            Extensions = [setup => new FleetExtension(setup, fleet), SyncExtension.With(SyncRig.Fast())],
        };
        return OpsParts.With(parts, OpsHostTests.Quick());
    }

    public static Task<HostRig> StartAsync(string name, string id, IMonoClock mono, ManualTime time, DirectoryInfo? dir = null,
        Func<FleetPinFile, IHealthFeeds>? feeds = null) =>
        HostRig.StartAsync(name, network: true, mono: mono, parts: Parts(time, feeds), settings: s => s with { NodeId = id, TrustLoopback = false },
            dir: dir);

    public static JsonElement Fleet(HostRig rig) => rig.State().GetProperty("fleet");

    public static string Role(HostRig rig) => Fleet(rig).GetProperty("role").GetString()!;

    public static string? MirrorState(HostRig rig) =>
        Fleet(rig).GetProperty("mirror") is { ValueKind: JsonValueKind.Object } m ? m.GetProperty("state").GetString() : null;

    /// <summary>What a backup saw of its primary, for a failing assertion: its mirror (state and words), the mirror link (its status,
    /// its connections, how long the primary has said nothing on it, on the node's clock) and the fleet monitor's record of the primary
    /// (how long since it was heard, on the fleet's clock).</summary>
    public static string Saw(HostRig backup)
    {
        var node = backup.Engine.Services.Backup!;
        var link = node.Link is { } l
            ? string.Create(CultureInfo.InvariantCulture, $"the mirror link is {l.Status} on connection {l.Connections}, the primary silent on it for {l.Silence.TotalSeconds:0.###} s on the node's clock")
            : "no mirror link";
        var heard = backup.Engine.Services.FleetPeers?.Peers().FirstOrDefault(p => p.Id == node.Settings.Of) is { } p
            ? string.Create(CultureInfo.InvariantCulture, $"the fleet monitor last heard {p.Name} {p.SilentS:0.###} s ago on the fleet's clock ({FleetPeer.HeardWord(p.Heard)})")
            : "the fleet monitor has no record of the primary";
        return $"{Fleet(backup).GetProperty("mirror")}; {link}; {heard}";
    }

    public static string ShowJson(HostRig rig) => ProjectJson.Serialize(rig.Engine.Store.Project);

    /// <summary>Preflight's backup section on a node now, run at its quick depth.</summary>
    public static async Task<IReadOnlyList<PreflightRow>> PreflightAsync(HostRig rig)
    {
        var check = rig.Engine.Services.PreflightChecks.Single(c => c.Section == "backup");
        return await check.RunAsync(new PreflightContext(rig.Engine, PreflightDepth.Quick, false), CancellationToken.None);
    }

    /// <summary>INDEX's fleet section on a node now: its rows.</summary>
    public static async Task<List<JsonElement>> IndexAsync(HostRig rig)
    {
        var json = await rig.OkAsync("INDEX");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("sections").EnumerateArray()
            .Single(s => s.GetProperty("section").GetString() == "fleet").GetProperty("rows").EnumerateArray().Select(r => r.Clone()).ToList();
    }

    /// <summary>Introduces the nodes to each other and waits until each hears the others' hellos, at the web port each serves (so the
    /// machine a fleet monitor dials under that id is the rig's own), and their health. A monitor whose subscription ended without a
    /// record dials again only once its backoff has run out on the fleet's clock, which these tests move by hand: while a node lacks a
    /// peer's health and its monitor waits to dial any machine again, each look moves every node's fleet clock a step on
    /// (<see cref="FleetWait.UntilAsync"/>). That step also moves any dial a node has under way (its connect timeout runs on that clock)
    /// and ages every record a node holds; no look moves it while the node's monitor waits to dial none, or once the node holds the
    /// peer's health.</summary>
    public static async Task MeetAsync(params HostRig[] rigs)
    {
        foreach (var a in rigs)
        {
            a.Network.Hello!.Targets = [.. rigs.Where(b => b != a).Select(b => new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, b.Network.HelloPort))];
        }
        var clocks = rigs.Select(r => r.Engine.Services.FleetTime).OfType<ManualTime>().Distinct().ToList();
        foreach (var a in rigs)
        {
            foreach (var b in rigs.Where(b => b != a))
            {
                Assert.True(await Eventually.TrueAsync(() => a.Network.Hello!.Peers.Find(b.Id)?.Hello.Port("http") == b.Network.HttpPort),
                    $"{a.Id} does not hear {b.Id} at its web port: {FleetWait.Knows(a, b)}");
                var (held, _) = await FleetWait.UntilAsync(() => a.Engine.Services.FleetPeers!.Peers().Any(p => p.Id == b.Id && p.Health is not null),
                    a.Engine.Services.FleetPeers as FleetMonitor, clocks);
                Assert.True(held, $"{a.Id} has no health of {b.Id}: {FleetWait.Knows(a, b)}");
            }
        }
    }

    /// <summary>The backup proves the primary's token, joins its mirror, and is identical.</summary>
    public static async Task BackUpAsync(HostRig of, HostRig by)
    {
        await by.OkAsync($"CREDENTIAL SET node.{of.Id} {HostRig.Token}");
        await by.OkAsync("FLEET BACKUP " + of.Id);
        Assert.True(await Eventually.TrueAsync(() => MirrorState(by) == "identical", 15000), Fleet(by).ToString() + "\n" + by.Journal());
    }
}

/// <summary>The node's first row of a kind whose text holds the words, written once this watch is made: the row, the node's STATE read
/// as the row is written, and whether the thread that wrote the row raised a push of STATE before it while <c>moved</c> read true (the
/// fact the row reports was the node's by then). The fact moves and its row is written on one thread with no wait between, so only that
/// thread's pushes count: a push another thread raises in that span, for a fact of its own, neither stands in for the product's push
/// nor lets a missing one pass some runs.</summary>
internal sealed class RowWatch
{
    private readonly TaskCompletionSource<(JournalRow Row, JsonElement State, bool Pushed)> _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, bool> _pushedOn = new();

    public RowWatch(HostRig rig, string kind, string words, Func<bool> moved)
    {
        rig.Engine.StateChanged += _ =>
        {
            if (moved())
                _pushedOn.TryAdd(Environment.CurrentManagedThreadId, true);
        };
        rig.Engine.Journal.Written += r =>
        {
            if (r.Kind == kind && r.Text.Contains(words, StringComparison.Ordinal))
                _seen.TrySetResult((r, rig.State(), _pushedOn.ContainsKey(Environment.CurrentManagedThreadId)));
        };
    }

    public Task<(JournalRow Row, JsonElement State, bool Pushed)> SeenAsync(TimeSpan within) => _seen.Task.WaitAsync(within);
}
