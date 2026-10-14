using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Dmx;
using PlayMap.Core.Time;
using PlayMap.Net.Control;

namespace PlayMap.Engine.Tests.Control;

/// <summary>The node's one Art-Net socket (Art-Net 4: UDP 6454; ArtPoll answered by a unicast ArtPollReply within a random delay of
/// up to 1 s; ArtTimeCode), each test on a loopback address of the slot's own.</summary>
public sealed class ArtNetInTests
{
    private static void Send(Socket from, string to, ReadOnlySpan<byte> packet) =>
        from.SendTo(packet, SocketFlags.None, new IPEndPoint(IPAddress.Parse(to), ArtNet.Port));

    [Fact]
    public async Task TenThousandSendsAllocateNothing()
    {
        await using var endpoint = new ArtNetEndpoint(ControlRig.Address(41));
        using var receiver = ControlRig.Udp(ControlRig.Address(42), ArtNet.Port);
        var to = endpoint.AddressOf(new IPEndPoint(IPAddress.Parse(ControlRig.Address(42)), ArtNet.Port));
        var packet = new byte[530];
        var n = ArtNet.WriteDmx(packet, 1, 0, new byte[512]);
        for (var i = 0; i < 200; i++)
            endpoint.Send(packet.AsSpan(0, n), to);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            endpoint.Send(packet.AsSpan(0, n), to);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(endpoint.IsOpen);
        Assert.Null(endpoint.Problem);
        Assert.True(endpoint.Sent >= 10_000);
    }

    [Fact]
    public async Task APollIsAnsweredUnicastWithinOneSecondOfTheNodesClock()
    {
        var clock = new ManualClock(1_000_000_000);
        var node = ControlRig.Address(43);
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, artNetAddress: node, clock: clock,
            settings: s => s with { ArtNetEnabled = true, ArtNetInterface = node });
        await rig.Ok("BIND ADD ARTNET 259 1 TO SET sf_lobby opacity *");
        using var desk = ControlRig.Udp(ControlRig.Address(44), ArtNet.Port);
        desk.ReceiveTimeout = 300;
        var poll = new byte[14];
        ArtNet.WritePoll(poll);
        Send(desk, node, poll);
        await ControlRig.Until(() => rig.State().GetProperty("control").GetProperty("dmx").GetProperty("artnet").GetProperty("polls").GetInt32() == 1, "the poll");
        // The node's clock has not moved: no reply yet, however long the test waits.
        var buffer = new byte[600];
        Assert.Throws<SocketException>(() => desk.Receive(buffer));
        clock.AdvanceMs(1000);
        desk.ReceiveTimeout = 5000;
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        var got = desk.ReceiveFrom(buffer, ref from);
        Assert.Equal(ArtNet.PollReplyLength, got);
        Assert.Equal(ArtOp.PollReply, ArtNet.OpOf(buffer));
        Assert.Equal("PlayMap", ArtNet.ShortNameOf(buffer));
        Assert.Equal(node, ((IPEndPoint)from).Address.ToString());
        // The bound universe (259 = net 1, sub-net 0, universe 3) is the reply's first port.
        Assert.Equal((1, 0, 3), (buffer[18], buffer[19], buffer[190]));
    }

    [Fact]
    public async Task ATimecodeConsumerOpensTheSocketArtTimeCodeRollsAndDmxFiresNoBinding()
    {
        var node = ControlRig.Address(45);
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, artNetAddress: node, settings: s => s with { ArtNetInterface = node });
        await rig.Ok("BIND ADD ARTNET 0 1 TO SET sf_lobby opacity *");
        Assert.False(rig.ArtNet!.IsOpen);
        await rig.Ok("TIMECODE SOURCE ADD Desk ARTNET");
        await ControlRig.Until(() => rig.ArtNet.IsOpen, "the socket opened for the timecode source");
        using var desk = ControlRig.Udp(ControlRig.Address(46), ArtNet.Port);
        var dmx = new byte[530];
        Send(desk, node, dmx.AsSpan(0, ArtNet.WriteDmx(dmx, 0, 0, [77, 0])));
        var tc = new byte[ArtNet.TimeCodeLength];
        for (var f = 0; f < 12; f++)
        {
            ArtNet.WriteTimeCode(tc, new ArtTimeCode(f, 30, 2, 1, 1, 0));
            Send(desk, node, tc);
            await Task.Delay(40);
        }
        await ControlRig.Until(() => rig.State().GetProperty("timecode").GetProperty("sources")[0].GetProperty("state").GetString() == "rolling", "the code rolling");
        var source = rig.State().GetProperty("timecode").GetProperty("sources")[0];
        Assert.StartsWith("01:02:30:", source.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("25", source.GetProperty("detected").GetString());
        // Art-Net control input is off: the DMX frame was heard by the socket and fired nothing.
        Assert.Equal(1, rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity);
        Assert.False(rig.State().GetProperty("control").GetProperty("dmx").GetProperty("artnet").GetProperty("enabled").GetBoolean());
    }
}
