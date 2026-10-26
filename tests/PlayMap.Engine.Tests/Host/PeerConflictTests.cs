using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Net.Bus;

namespace PlayMap.Engine.Tests.Host;

/// <summary>Show Bus 1.3 §2's conflict mark on a running node, the hellos real datagrams on the node's own hello port: two running nodes
/// saying one id, and another process saying this node's own id, are amber rows in STATE's peers naming both machines, and no row of the
/// context's peers (§6) — this node's own clash is an amber item of the context instead; a Stagewatch in conflict is not followed, and is
/// once its mark clears; and the context lists at most <c>context.peers</c> peers.</summary>
public sealed class PeerConflictTests
{
    [Fact]
    public async Task TwoMachinesSayingOneIdAreAmberInStateAndNoRowOfTheContext()
    {
        await using var rig = await HostRig.StartAsync();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        foreach (var (inst, host) in new[] { ("a41f09c2", "FOH-PC"), ("b7c3d9e1", "FOH-PC2"), ("a41f09c2", "FOH-PC") })
            await SendAsync(rig, udp, Hello("playmap", "cccc0003", inst, host, 9941));
        const string Words = "two machines say they are cccc0003: FOH-PC2 at 127.0.0.1 and FOH-PC at 127.0.0.1";

        Assert.True(await Eventually.TrueAsync(() => StatePeer(rig, "cccc0003") is { } p && p.GetProperty("light").GetString() == "amber"),
            "STATE's peer never turned amber");
        var row = StatePeer(rig, "cccc0003")!.Value;
        Assert.Equal(Words, row.GetProperty("headline").GetString());
        Assert.True(row.GetProperty("live").GetBoolean());
        Assert.Empty(rig.Network.Hello!.Peers.Live("playmap"));

        using var context = JsonDocument.Parse(rig.Engine.Context());
        Assert.DoesNotContain(context.RootElement.GetProperty("peers").EnumerateArray(), p => p.GetProperty("id").GetString() == "cccc0003");
        Assert.DoesNotContain(Words, rig.Engine.Context(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherProcessSayingThisNodesIdIsAmberInStateAndAnItemOfTheContext()
    {
        await using var rig = await HostRig.StartAsync();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await SendAsync(rig, udp, Hello("playmap", rig.Id, "b7c3d9e1", "RACK-2", 9941));
        var words = $"two machines say they are {rig.Id}: this node and RACK-2 at 127.0.0.1";

        Assert.True(await Eventually.TrueAsync(() => StatePeer(rig, rig.Id) is not null), "the hello saying this node's id was never heard");
        var row = StatePeer(rig, rig.Id)!.Value;
        Assert.Equal("amber", row.GetProperty("light").GetString());
        Assert.Equal(words, row.GetProperty("headline").GetString());

        using var context = JsonDocument.Parse(rig.Engine.Context());
        Assert.DoesNotContain(context.RootElement.GetProperty("peers").EnumerateArray(), p => p.GetProperty("id").GetString() == rig.Id);
        var item = Assert.Single(context.RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "bus:hello");
        Assert.Equal("amber", item.GetProperty("light").GetString());
        Assert.Equal(words, item.GetProperty("state").GetString());
    }

    /// <summary>§2: no new link is dialled to an id in conflict while the mark stands. "Follow the first Stagewatch heard" finds none
    /// while the only Stagewatch heard is marked, and the node follows it once 60 s pass without an alternation (the mark's clearing is
    /// a change of the peer, and a change of a Stagewatch peer is followed when none is).</summary>
    [Fact]
    public async Task AStagewatchInConflictIsNotFollowedUntilItsMarkClears()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var rig = await HostRig.StartAsync(mono: clock);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ClosedPort();
        foreach (var (inst, host) in new[] { ("a41f09c2", "FOH-PC"), ("b7c3d9e1", "FOH-PC2"), ("a41f09c2", "FOH-PC") })
            await SendAsync(rig, udp, Hello(ShowBusConstants.KindStagewatch, "5a7e0001", inst, host, port));
        Assert.NotNull(rig.Network.Hello!.Peers.Find("5a7e0001")!.Conflict);

        await rig.OkAsync("LINK STAGEWATCH \"\"");
        Assert.True(await Eventually.TrueAsync(() => rig.Journal().Contains("following the first Stagewatch heard — none is heard yet", StringComparison.Ordinal)),
            "the link's change did not end as none heard: " + rig.Journal());
        Assert.Null(rig.Network.Stagewatch());

        // Only the first machine from here, a hello every 2 s: the mark stands to 60 s after the last alternation, then clears.
        for (var i = 0; i < 29; i++)
        {
            clock.AdvanceMs(2000);
            await SendAsync(rig, udp, Hello(ShowBusConstants.KindStagewatch, "5a7e0001", "a41f09c2", "FOH-PC", port));
        }
        Assert.NotNull(rig.Network.Hello!.Peers.Find("5a7e0001")!.Conflict);
        Assert.Null(rig.Network.Stagewatch());

        clock.AdvanceMs(2001);
        await SendAsync(rig, udp, Hello(ShowBusConstants.KindStagewatch, "5a7e0001", "a41f09c2", "FOH-PC", port));
        Assert.Null(rig.Network.Hello!.Peers.Find("5a7e0001")!.Conflict);
        Assert.True(await Eventually.TrueAsync(() => rig.Network.Stagewatch() is { } link && link.Host.StartsWith("127.0.0.1", StringComparison.Ordinal)), "the Stagewatch was not followed once its mark cleared");
    }

    /// <summary>§6: a context lists at most <c>context.peers</c> peer rows; STATE's peers list every peer the table holds.</summary>
    [Fact]
    public async Task TheContextListsAtMostItsPeerRows()
    {
        await using var rig = await HostRig.StartAsync();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var count = ShowBusConstants.ContextPeers + 8;
        for (var i = 0; i < count; i++)
            await SendAsync(rig, udp, Hello("playmap", "node" + i.ToString("x4", CultureInfo.InvariantCulture), "a41f09c2", "RACK", 9941));

        Assert.Equal(count, rig.State().GetProperty("peers").GetArrayLength());
        using var context = JsonDocument.Parse(rig.Engine.Context());
        Assert.Equal(ShowBusConstants.ContextPeers, context.RootElement.GetProperty("peers").GetArrayLength());
    }

    /// <summary>Sends one hello and waits until the node's table has taken it, so the next is heard after it.</summary>
    private static async Task SendAsync(HostRig rig, UdpClient udp, byte[] hello)
    {
        var heard = rig.Network.Hello!.Heard;
        await udp.SendAsync(hello, new IPEndPoint(IPAddress.Loopback, rig.Network.HelloPort));
        Assert.True(await Eventually.TrueAsync(() => rig.Network.Hello!.Heard > heard), "the node did not take the hello");
    }

    /// <summary>A loopback port nothing listens on: a link dialled to it is up and finds no one.</summary>
    private static int ClosedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static JsonElement? StatePeer(HostRig rig, string id)
    {
        foreach (var p in rig.State().GetProperty("peers").EnumerateArray())
            if (p.GetProperty("id").GetString() == id)
                return p.Clone();
        return null;
    }

    private static byte[] Hello(string kind, string id, string inst, string host, int port) =>
        new ShowBusHello(kind, 1, ShowBusConstants.Proto, id, "FOH", host,
            new Dictionary<string, int> { [kind == ShowBusConstants.KindStagewatch ? "http" : "wire"] = port }, ["state"], 1, inst, null).ToJson();
}
