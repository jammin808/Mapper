using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Control.Osc;

namespace PlayMap.Engine.Tests.Control;

/// <summary>The node's OSC socket on this slot's loopback address: lines under /playmap reach the router and are answered on the
/// sender's own port; a refusal comes back with the node's words; a bound address fires its binding; a malformed datagram is
/// counted and the port stays open.</summary>
public sealed class OscServerTests
{
    private static async Task<OscMessage> Receive(Socket socket, string address)
    {
        var buffer = new byte[65536];
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var got = await socket.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(OscCodec.TryRead(buffer.AsSpan(0, got), out var packet, out var why), why);
            if (packet is OscMessage m && m.Address == address)
                return m;
        }
        throw new TimeoutException("no " + address);
    }

    private static Socket Client()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Parse(ControlRig.Loopback), 0));
        return s;
    }

    [Fact]
    public async Task PlaylistGoOverOscPlaysAndARefusalComesBackWithTheWords()
    {
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true);
        var node = rig.Service!.OscLocal!;
        Assert.True(rig.Service.OscOpen);
        using var client = Client();
        await client.SendToAsync(OscCodec.Write(OscMessage.Of("/playmap/playlist/go", OscArg.S("show"))), SocketFlags.None, node);
        var ok = await Receive(client, "/playmap/reply");
        Assert.Equal("OK", ok.Args[0].Text);
        Assert.Equal("Show: playing Red", ok.Args[1].Text);
        // Observed, not assumed: the playlist's lane plays its first item.
        Assert.Equal(("it_a", Core.Playback.TransportState.Playing), (rig.Engine.Playback.Lane("pl_show")!.ItemId, rig.Engine.Playback.Lane("pl_show")!.Clip.State));

        await client.SendToAsync(OscCodec.Write(OscMessage.Of("/playmap/line", OscArg.S("PLAYLIST GO nowhere"), OscArg.S("r7"))), SocketFlags.None, node);
        var err = await Receive(client, "/playmap/reply");
        Assert.Equal(("ERR", "no playlist nowhere", "r7"), (err.Args[0].Text, err.Args[1].Text, err.Args[2].Text));
        var words = await Receive(client, "/playmap/error");
        Assert.Equal("no playlist nowhere", words.Args[0].Text);

        await client.SendToAsync(OscCodec.Write(OscMessage.Of("/playmap/frob")), SocketFlags.None, node);
        Assert.Equal("no verb FROB — /playmap/line ,s \"<line>\" runs any line", (await Receive(client, "/playmap/reply")).Args[1].Text);
        Assert.True(rig.State().GetProperty("control").GetProperty("osc").GetProperty("refused").GetInt32() >= 2);
    }

    [Fact]
    public async Task ABoundAddressFiresItsBindingAndAMalformedDatagramLeavesThePortOpen()
    {
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true);
        await rig.Ok("BIND ADD OSC /fader/1 TO SET sf_lobby opacity *");
        var node = rig.Service!.OscLocal!;
        using var client = Client();
        await client.SendToAsync(new byte[] { 1, 2, 3 }, SocketFlags.None, node);
        Assert.StartsWith("malformed: ", (await Receive(client, "/playmap/error")).Args[0].Text, StringComparison.Ordinal);
        await client.SendToAsync(OscCodec.Write(OscMessage.Of("/fader/1", OscArg.F(0.25f))), SocketFlags.None, node);
        await ControlRig.Until(() => Math.Abs(rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity - 0.25) < 1e-9, "Lobby at 0.25");
        var osc = rig.State().GetProperty("control").GetProperty("osc");
        Assert.Equal(1, osc.GetProperty("malformed").GetInt32());
        Assert.True(osc.GetProperty("open").GetBoolean());
        // A bundle's messages run in order.
        var bundle = new OscBundle(OscBundle.Immediately, [OscMessage.Of("/playmap/set/sf_stage/opacity", OscArg.F(0.5f)), OscMessage.Of("/playmap/blackout", OscArg.I(1))]);
        await client.SendToAsync(OscCodec.Write(bundle), SocketFlags.None, node);
        await ControlRig.Until(() => rig.Engine.Air.Blackout, "blackout");
        Assert.Equal(0.5, rig.Engine.Store.Project.FindSurface("sf_stage")!.Opacity, 9);
        // A read of a field answers on the same address.
        await client.SendToAsync(OscCodec.Write(OscMessage.Of("/playmap/set/sf_stage/opacity", OscArg.S("?"))), SocketFlags.None, node);
        Assert.Equal(0.5, (await Receive(client, "/playmap/set/sf_stage/opacity")).Args[0].Float, 9);
    }

    [Fact]
    public async Task AnUnpairedSenderIsRefusedChangesButMayRead()
    {
        // A node with a token that does not trust loopback: a sender it does not know is not paired.
        var services = EngineServices.ForTests(Core.Time.MonoClock.Instance);
        services = services with { Settings = services.Settings with { NodeId = Guid.NewGuid().ToString("N")[..8], Token = "K7QM-3XWD-P9RA", TrustLoopback = false } };
        var service = new Engine.Control.ControlService(new Engine.Control.ControlServiceOptions
        {
            Midi = new PlayMap.Devices.Midi.FakeMidiBackend(), OscBind = IPAddress.Parse(ControlRig.Loopback), OscPort = 0,
        });
        await using var engine = new EngineCore(services with { Control = service }, BindingEngineTests.Show());
        await using (service)
        {
            await service.AttachAsync(engine);
            using var client = Client();
            await client.SendToAsync(OscCodec.Write(OscMessage.Of("/playmap/blackout", OscArg.I(1))), SocketFlags.None, service.OscLocal!);
            var refused = await Receive(client, "/playmap/reply");
            Assert.Equal("ERR", refused.Args[0].Text);
            Assert.StartsWith("not paired", refused.Args[1].Text, StringComparison.Ordinal);
            Assert.False(engine.Air.Blackout);
            await client.SendToAsync(OscCodec.Write(OscMessage.Of("/playmap/playlist/list")), SocketFlags.None, service.OscLocal!);
            Assert.Equal("OK", (await Receive(client, "/playmap/reply")).Args[0].Text);
        }
    }
}
