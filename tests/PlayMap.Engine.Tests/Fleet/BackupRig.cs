using System.Text.Json;
using PlayMap.Core.Model;
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

    public static string ShowJson(HostRig rig) => ProjectJson.Serialize(rig.Engine.Store.Project);

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
