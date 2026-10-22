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

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>Nodes for the hot-backup tests: each a real node on loopback with the fleet (monitor on its own hand-moved clock, the
/// backup's watch fast), the sync part (fast, synthetic vblanks none) and the operations parts (the credential store, projector
/// links); every node asks the pairing token even from this machine, so a backup proves it like a machine across the room.</summary>
internal static class BackupRig
{
    public const string PrimaryId = SyncRig.LeaderId;
    public const string BackupId = SyncRig.FollowerId;
    public const string ThirdId = "c0ffee00";

    public static EngineParts Parts(ManualTime time)
    {
        var fleet = new FleetOptions
        {
            Time = time,
            Reader = new UnknownMachineReader("not read in tests"),
            Backup = new BackupOptions { WatchEvery = TimeSpan.FromMilliseconds(20) },
        };
        var parts = new EngineParts
        {
            Extensions = [setup => new FleetExtension(setup, fleet), SyncExtension.With(SyncRig.Fast())],
        };
        return OpsParts.With(parts, OpsHostTests.Quick());
    }

    public static Task<HostRig> StartAsync(string name, string id, IMonoClock mono, ManualTime time, DirectoryInfo? dir = null) =>
        HostRig.StartAsync(name, network: true, mono: mono, parts: Parts(time), settings: s => s with { NodeId = id, TrustLoopback = false }, dir: dir);

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

    /// <summary>Introduces the nodes to each other and waits until each hears the others' hellos and health.</summary>
    public static async Task MeetAsync(params HostRig[] rigs)
    {
        foreach (var a in rigs)
        {
            a.Network.Hello!.Targets = [.. rigs.Where(b => b != a).Select(b => new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, b.Network.HelloPort))];
        }
        foreach (var a in rigs)
        {
            foreach (var b in rigs.Where(b => b != a))
            {
                Assert.True(await Eventually.TrueAsync(() => a.Network.Hello!.Peers.Find(b.Id)?.Hello.Port("http") > 0), $"{a.Id} does not hear {b.Id}");
                Assert.True(await Eventually.TrueAsync(() => a.Engine.Services.FleetPeers!.Peers().Any(p => p.Id == b.Id && p.Health is not null)),
                    $"{a.Id} has no health of {b.Id}");
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
/// as the row is written, and whether a push of STATE was raised, before the row, while <c>moved</c> read true (the fact the row
/// reports is the node's by then).</summary>
internal sealed class RowWatch
{
    private readonly TaskCompletionSource<(JournalRow Row, JsonElement State, bool Pushed)> _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pushed;

    public RowWatch(HostRig rig, string kind, string words, Func<bool> moved)
    {
        rig.Engine.StateChanged += _ =>
        {
            if (moved())
                Interlocked.Exchange(ref _pushed, 1);
        };
        rig.Engine.Journal.Written += r =>
        {
            if (r.Kind == kind && r.Text.Contains(words, StringComparison.Ordinal))
                _seen.TrySetResult((r, rig.State(), Volatile.Read(ref _pushed) == 1));
        };
    }

    public Task<(JournalRow Row, JsonElement State, bool Pushed)> SeenAsync(TimeSpan within) => _seen.Task.WaitAsync(within);
}
