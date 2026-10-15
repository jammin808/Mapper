using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PlayMap.Core.Control.Osc;
using PlayMap.Core.Time;

namespace PlayMap.Engine.Tests.Control;

/// <summary>OSC feedback on this slot's loopback address, on the node's manual clock (a pass runs only when the test moves the
/// clock): a fader follows the desk on every client, once (dedupe); the client whose fader moved is not fought for 300 ms (the echo
/// guard); a client sending back what it was sent within 50 ms is an echo, dropped; a 1 kHz stream of changes sends at most 50 a
/// second; a lamp lights at once on a press; a node's feedback client and a binding's own feedback words are fed without speaking
/// first.</summary>
public sealed class OscFeedbackTests
{
    private static Socket Client()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Parse(ControlRig.Loopback), 0));
        return s;
    }

    private static Task Send(Socket from, IPEndPoint to, OscMessage m) => from.SendToAsync(OscCodec.Write(m), SocketFlags.None, to);

    private static void Flatten(IOscPacket p, List<OscMessage> into)
    {
        if (p is OscMessage m)
            into.Add(m);
        else
            foreach (var e in ((OscBundle)p).Elements)
                Flatten(e, into);
    }

    /// <summary>The messages that arrive within a time (each datagram read whole; bundles opened).</summary>
    private static List<OscMessage> Drain(Socket s, int ms)
    {
        var got = new List<OscMessage>();
        var buffer = new byte[65536];
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        do
        {
            while (s.Available > 0)
            {
                var n = s.Receive(buffer);
                Assert.True(OscCodec.TryRead(buffer.AsSpan(0, n), out var packet, out var why), why);
                Flatten(packet!, got);
            }
            if (DateTime.UtcNow >= deadline)
                break;
            s.Poll(10_000, SelectMode.SelectRead);
        }
        while (true);
        return got;
    }

    /// <summary>The next message to an address (others passed over), within 10 s.</summary>
    private static OscMessage Next(Socket s, string address)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var m in Drain(s, 50))
                if (m.Address == address)
                    return m;
        }
        throw new TimeoutException("no " + address);
    }

    /// <summary>The first message to an address carrying a value (earlier values passed over: the node's first pass may run before
    /// the test's first change), within 10 s.</summary>
    private static OscMessage NextWith(Socket s, string address, double value)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var m in Drain(s, 50))
                if (m.Address == address && Math.Abs(m.Args[0].Float - value) < 1e-6)
                    return m;
        }
        throw new TimeoutException($"no {address} {value}");
    }

    private static double Opacity(ControlRig rig, string id) => rig.Engine.Store.Project.FindSurface(id)!.Opacity;

    [Fact]
    public async Task AFaderFollowsTheDeskOnEveryClientOnceAndTheHandOnItIsNotFought()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, clock: clock);
        await rig.Ok("BIND ADD OSC /fader/1 TO SET sf_lobby opacity *");
        var node = rig.Service!.OscLocal!;
        using var a = Client();
        using var b = Client();

        // A moves its fader: the target follows, and A becomes a feedback client.
        await Send(a, node, OscMessage.Of("/fader/1", OscArg.F(0.25f)));
        await ControlRig.Until(() => Math.Abs(Opacity(rig, "sf_lobby") - 0.25) < 1e-9, "Lobby at 0.25");
        // B speaks on an address of its own: it is sent what every bound control shows.
        await Send(b, node, OscMessage.Of("/hello"));
        await ControlRig.Until(() => rig.Service.Feedback.Clients == 2, "two clients");
        clock.AdvanceMs(25);
        Assert.Equal(0.25, Next(b, "/fader/1").Args[0].Float, 6);

        // The desk moves the target: B follows at once; A, whose fader moved 25 ms ago, once it has been still for 300 ms.
        await rig.Ok("SET sf_lobby opacity 0.75");
        clock.AdvanceMs(25);
        Assert.Equal(0.75, Next(b, "/fader/1").Args[0].Float, 6);
        Assert.DoesNotContain(Drain(a, 150), m => m.Address == "/fader/1");
        clock.AdvanceMs(300);
        Assert.Equal(0.75, Next(a, "/fader/1").Args[0].Float, 6);
        // Once: nothing more is sent while nothing changes.
        var sent = rig.Service.Feedback.Sent;
        clock.AdvanceMs(25);
        await rig.Ok("SET sf_stage opacity 0.5");
        clock.AdvanceMs(25);
        Assert.Empty(Drain(a, 150));
        Assert.Empty(Drain(b, 0));
        Assert.Equal(sent, rig.Service.Feedback.Sent);

        // B sends back what it was just sent (inside 50 ms on the node's clock): an echo, dropped before any binding hears it.
        await rig.Ok("SET sf_lobby opacity 0.5");
        clock.AdvanceMs(25);
        var echoed = Next(b, "/fader/1");
        Assert.Equal(0.5, echoed.Args[0].Float, 6);
        var heard = rig.Engine.ControlInput.Heard;
        await Send(b, node, echoed);
        await ControlRig.Until(() => rig.Service.Feedback.Echoes == 1, "the echo dropped");
        Assert.Equal(heard, rig.Engine.ControlInput.Heard);

        // STATE says what was fed (counts move STATE at most four times a second of the node's clock).
        clock.AdvanceMs(300);
        await ControlRig.Until(() => rig.State().GetProperty("control").GetProperty("osc").GetProperty("echoes").GetInt32() == 1, "STATE's echoes");
        var osc = rig.State().GetProperty("control").GetProperty("osc");
        Assert.Equal((2, 0), (osc.GetProperty("clients").GetInt32(), osc.GetProperty("feedback").GetArrayLength()));
        Assert.True(osc.GetProperty("feedbackSent").GetInt32() >= 4);
        var list = JsonDocument.Parse((await rig.Ok("BIND LIST")).Payload!).RootElement;
        Assert.Equal("value → OSC /fader/1", list[0].GetProperty("feedback").GetString());
    }

    [Fact]
    public async Task AThousandChangesASecondSendAtMostFifty()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, clock: clock);
        await rig.Ok("BIND ADD OSC /fader/1 TO SET sf_lobby opacity *");
        var node = rig.Service!.OscLocal!;
        using var a = Client();
        await Send(a, node, OscMessage.Of("/hello"));
        await ControlRig.Until(() => rig.Service.Feedback.Clients == 1, "the client");
        clock.AdvanceMs(25);
        Assert.Equal(1, Next(a, "/fader/1").Args[0].Float, 6);
        var before = rig.Service.Feedback.Sent;
        for (var i = 1; i <= 1000; i++)
        {
            await rig.Ok("SET sf_lobby opacity " + (i * 0.000999).ToString("R", CultureInfo.InvariantCulture));
            clock.AdvanceMs(1);
        }
        clock.AdvanceMs(25);
        var got = new List<OscMessage>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((got.Count == 0 || Math.Abs(got[^1].Args[0].Float - 0.999) > 1e-6) && DateTime.UtcNow < deadline)
            got.AddRange(Drain(a, 50).Where(m => m.Address == "/fader/1"));
        Assert.Equal(0.999, got[^1].Args[0].Float, 6);
        // One second of the node's clock (and the last 25 ms): at most one message per 20 ms.
        Assert.InRange(got.Count, 2, 52);
        Assert.Equal(got.Count, rig.Service.Feedback.Sent - before);
    }

    [Fact]
    public async Task ALampLightsOnAPressAndTheNodesFeedbackClientsHearWithoutSpeaking()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, clock: clock);
        await rig.Ok("BIND ADD OSC /bo TO BLACKOUT TOGGLE");
        var node = rig.Service!.OscLocal!;
        using var listener = Client();
        var at = ((IPEndPoint)listener.LocalEndPoint!).ToString();

        Assert.Equal($"{at} hears the bindings' feedback", (await rig.Ok("CONTROL FEEDBACK ADD " + at)).Effect);
        Assert.Equal($"{at} already hears the node's feedback", await rig.Refused("CONTROL FEEDBACK ADD " + at));
        Assert.Equal("'desk:9000' is no host:port (an IP address and a port 1–65535)", await rig.Refused("CONTROL FEEDBACK ADD desk:9000"));
        Assert.Equal("10.0.0.9:9000 is not a feedback client — CONTROL STATUS lists them (control.osc.feedback)", await rig.Refused("CONTROL FEEDBACK REMOVE 10.0.0.9:9000"));
        Assert.Equal($"{node} is this node's own OSC port — its feedback would come straight back", await rig.Refused("CONTROL FEEDBACK ADD " + node));
        Assert.Equal(new[] { at }, rig.Engine.ControlInput.Settings.OscFeedback.ToArray());
        clock.AdvanceMs(25);
        Assert.Equal(0, Next(listener, "/bo").Args[0].Float);

        // A press toggles the blackout; on its release the lamp shows the fact at once (a lamp waits for no guard).
        using var pad = Client();
        await Send(pad, node, OscMessage.Of("/bo", OscArg.Bool(true)));
        await ControlRig.Until(() => rig.Engine.Air.Blackout, "the blackout");
        await Send(pad, node, OscMessage.Of("/bo", OscArg.Bool(false)));
        await ControlRig.Until(() => rig.Engine.ControlInput.Heard == 2, "the release");
        clock.AdvanceMs(25);
        Assert.Equal(1, Next(pad, "/bo").Args[0].Float);
        Assert.Equal(1, Next(listener, "/bo").Args[0].Float);

        Assert.Equal($"{at} no longer hears the node's feedback", (await rig.Ok("CONTROL FEEDBACK REMOVE " + at)).Effect);
        await rig.Ok("BLACKOUT OFF");
        clock.AdvanceMs(25);
        Assert.Equal(0, Next(pad, "/bo").Args[0].Float);
        Assert.Empty(Drain(listener, 150));
        Assert.Equal(0, rig.State().GetProperty("control").GetProperty("osc").GetProperty("feedback").GetArrayLength());
    }

    [Fact]
    public async Task AnUnpairedSenderMovesNoBindingAndIsNotFed()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var closed = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, clock: clock,
            node: n => n with { Token = "tok", TrustLoopback = false });
        await closed.Ok("BIND ADD OSC /fader/1 TO SET sf_lobby opacity *");
        using var stranger = Client();
        await Send(stranger, closed.Service!.OscLocal!, OscMessage.Of("/fader/1", OscArg.F(0.25f)));
        var error = Next(stranger, "/playmap/error");
        Assert.Equal($"not paired — OSC from {ControlRig.Loopback} moves no binding until this node trusts the address (TrustedAddresses or OscAllow)",
            error.Args[0].Text);
        Assert.Equal((1.0, 0L, 0), (Opacity(closed, "sf_lobby"), closed.Engine.ControlInput.Heard, closed.Service.Feedback.Clients));

        // The same sender, allowed by the node's OSC allow list, moves it.
        await using var allowed = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, clock: clock,
            node: n => n with { Token = "tok", TrustLoopback = false }, settings: c => c with { OscAllow = [ControlRig.Loopback] });
        await allowed.Ok("BIND ADD OSC /fader/1 TO SET sf_lobby opacity *");
        await Send(stranger, allowed.Service!.OscLocal!, OscMessage.Of("/fader/1", OscArg.F(0.25f)));
        await ControlRig.Until(() => Math.Abs(Opacity(allowed, "sf_lobby") - 0.25) < 1e-9, "Lobby at 0.25");
        Assert.Equal(1, allowed.Service.Feedback.Clients);
    }

    [Fact]
    public async Task ABindingsOwnFeedbackWordsSendItThereAndWordsThatDoNotReadAreRefused()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, clock: clock);
        using var display = Client();
        var at = ((IPEndPoint)display.LocalEndPoint!).ToString();
        await rig.Ok("""BIND ADD {"input":"MIDI CC 1 7","target":"SET sf_stage opacity *","feedback":{"to":"OSC """ + at + """ /stage"}}""");
        await rig.Ok("SET sf_stage opacity 0.4");
        clock.AdvanceMs(25);
        Assert.Equal("/stage", NextWith(display, "/stage", 0.4).Address);
        var list = JsonDocument.Parse((await rig.Ok("BIND LIST")).Payload!).RootElement;
        Assert.Equal($"value → OSC {at} /stage", list[0].GetProperty("feedback").GetString());
        // Dedupe holds at a binding's own destination too: the next pass sends what changed there, never /stage again.
        await rig.Ok("""BIND ADD {"input":"MIDI CC 1 8","target":"SET sf_lobby opacity *","feedback":{"to":"OSC """ + at + """ /lobby2"}}""");
        await rig.Ok("SET sf_lobby opacity 0.7");
        clock.AdvanceMs(25);
        var heard = new List<OscMessage>();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!heard.Any(m => m.Address == "/lobby2") && DateTime.UtcNow < until)
            heard.AddRange(Drain(display, 50));
        Assert.Equal(0.7, heard.Last(m => m.Address == "/lobby2").Args[0].Float, 6);
        Assert.DoesNotContain(heard, m => m.Address == "/stage");

        var refused = await rig.Refused("""BIND ADD {"input":"OSC /x","target":"SET sf_stage opacity *","feedback":{"to":"OSC nowhere"}}""");
        Assert.Contains("the binding's feedback: 'nowhere' is no host:port (an IP address and a port 1–65535)", refused, StringComparison.Ordinal);
        await rig.Ok("CONTROL SURFACE ADD Pad OSC");
        var surface = rig.Engine.Store.Project.Control.Surfaces[0].Id;
        Assert.Contains("an OSC surface's feedbackTo is a host:port", await rig.Refused($"SET {surface} feedbackTo pad.local"), StringComparison.Ordinal);
        await rig.Ok($"SET {surface} feedbackTo {at}");
        // A binding on the surface feeds the surface's own address, whoever spoke.
        await rig.Ok("BIND ADD OSC Pad /lobby TO SET sf_lobby opacity *");
        await rig.Ok("SET sf_lobby opacity 0.3");
        clock.AdvanceMs(25);
        Assert.Equal(0.3, Next(display, "/lobby").Args[0].Float, 6);
    }
}
