using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Control.Osc;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Control;

/// <summary>show × control: an armed node refuses edits that arrive over OSC with the gate's own words. A /playmap line from a paired
/// sender is answered ERR with the words (and /playmap/error), a bound fader's line runs as the binding (transport "binding", a
/// remote client to Show Mode) and is refused the same way; both are journalled, Show Mode counts them, and the project does not
/// move. Disarmed, the same messages apply.</summary>
public sealed class OscArmedTests
{
    private const string Token = "K7QM-3XWD-P9RA";

    private static readonly string NotWhileArmed =
        "Not while armed: SET moves the mapping on the air, and this client may only run the show while it is armed. The desk can SHOW DISARM.";

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

    [Fact]
    public async Task AnArmedNodeRefusesOscLinesAndBoundFadersWithTheGatesWordsAndDisarmedTheyApply()
    {
        // A node with a token that trusts its own machine: the loopback sender is paired by that trust.
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true,
            node: n => n with { Token = Token, TrustLoopback = true });
        await rig.Ok("BIND ADD OSC /fader TO SET sf_lobby opacity *");
        var binding = Assert.Single(rig.Engine.Store.Project.Control.Bindings);
        // A binding's line runs as the binding: Show Mode takes it for a remote client.
        Assert.Equal(ShowRole.Remote, rig.Engine.ShowRoleOf(new SessionInfo("bind:" + binding.Id, "binding", "", false, "binding:" + binding.Id, true)));
        await rig.Ok("SHOW ARM");
        Assert.True(rig.State().GetProperty("show").GetProperty("armed").GetBoolean());
        var before = rig.Engine.Store.Current.Revision;
        var lobby = rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity;
        var stage = rig.Engine.Store.Project.FindSurface("sf_stage")!.Opacity;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Bind(new IPEndPoint(IPAddress.Parse(ControlRig.Loopback), 0));
        var node = rig.Service!.OscLocal!;
        var me = (IPEndPoint)client.LocalEndPoint!;

        // A line under /playmap: refused with the gate's words, to the sender and in the journal.
        await client.SendToAsync(OscCodec.Write(OscMessage.Of("/playmap/set/sf_stage/opacity", OscArg.F(0.5f))), SocketFlags.None, node);
        var reply = await Receive(client, "/playmap/reply");
        Assert.Equal(("ERR", NotWhileArmed), (reply.Args[0].Text, reply.Args[1].Text));
        Assert.Equal(NotWhileArmed, (await Receive(client, "/playmap/error")).Args[0].Text);
        var row = Assert.Single(rig.Engine.Journal.Recent(50), r => r.Line == "SET sf_stage opacity 0.5");
        Assert.Equal(("refused", NotWhileArmed, "OSC " + me, false), (row.Kind, row.Text, row.Who, row.Ok));

        // A bound fader: its binding's line is refused by the same gate; the gesture is journalled once with the words.
        await client.SendToAsync(OscCodec.Write(OscMessage.Of("/fader", OscArg.F(0.25f))), SocketFlags.None, node);
        await ControlRig.Until(() => rig.Engine.Journal.Recent(50).Any(r => r.Who == $"binding:{binding.Id} (OSC /fader)"), "the fader's gesture journalled");
        var gesture = Assert.Single(rig.Engine.Journal.Recent(50), r => r.Who == $"binding:{binding.Id} (OSC /fader)");
        Assert.Equal(("refused", "OSC /fader → Lobby opacity, 1 time: " + NotWhileArmed, "SET sf_lobby opacity 0.25", false),
            (gesture.Kind, gesture.Text, gesture.Line, gesture.Ok));

        // Show Mode counted both, and nothing moved.
        var refused = rig.State().GetProperty("show").GetProperty("refused");
        Assert.Equal(2, refused.GetProperty("last60s").GetInt32());
        Assert.Equal($"binding:{binding.Id} (OSC /fader) — SET sf_lobby opacity 0.25 — {NotWhileArmed}", refused.GetProperty("last").GetString());
        Assert.Equal(before, rig.Engine.Store.Current.Revision);
        Assert.Equal(lobby, rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity);
        Assert.Equal(stage, rig.Engine.Store.Project.FindSurface("sf_stage")!.Opacity);

        // Disarmed, the same messages apply.
        await rig.Ok("SHOW DISARM");
        await client.SendToAsync(OscCodec.Write(OscMessage.Of("/playmap/set/sf_stage/opacity", OscArg.F(0.5f))), SocketFlags.None, node);
        Assert.Equal("OK", (await Receive(client, "/playmap/reply")).Args[0].Text);
        Assert.Equal(0.5, rig.Engine.Store.Project.FindSurface("sf_stage")!.Opacity, 9);
        await client.SendToAsync(OscCodec.Write(OscMessage.Of("/fader", OscArg.F(0.4f))), SocketFlags.None, node);
        await ControlRig.Until(() => Math.Abs(rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity - 0.4) < 1e-6, "Lobby at 0.4");
    }
}
