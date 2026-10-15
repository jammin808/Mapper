using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Bus;
using PlayMap.Core.Dmx;
using PlayMap.Core.Led;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Engine.Tests.Control;
using PlayMap.Net.Led;

namespace PlayMap.Engine.Tests.Led;

/// <summary>Control and LED on the node's one Art-Net socket (PLAN §6.1, control × led): a console's ArtDmx into it fires a binding,
/// and the LED rig's ArtDmx and ArtSync leave from it (UDP 6454) — no second socket on the spec port.</summary>
public sealed class ArtNetSharedSocketTests
{
    [Fact]
    public async Task DmxInAndLedOutShareOneSocket()
    {
        var nodeAddress = ControlRig.Address(91);
        var pixLite = ControlRig.Address(92);
        LedSender? sender = null;
        var show = BindingEngineTests.Show() with
        {
            Canvases = [new Canvas("cv00000001", "Wall", 64, 32, "#000000", [])],
            Leds = new LedRig
            {
                Nodes = [new LedNode("ln00000001", "PixLite", LedProtocol.ArtNet, pixLite, 0, true, 0, 0, LedSync.Latch, 510, "#FFFFFF")],
                Fixtures = [new LedFixture("lf00000001", "Strip", "builtin:rgb", LedSpace.Canvas, "cv00000001", Transform3.Identity,
                    new LedLayout(LedShape.Line, 10, 1, new Vec3d(9, 0, 0), LedStart.TopLeft, false, false, []), new LedPatch("ln00000001", 2, 1),
                    LedSampling.Point, 0, LedColor.Neutral, true, false, false, "#FF8800", [])],
            },
        };
        using var node = ControlRig.Udp(pixLite, ArtNet.Port);
        using var desk = ControlRig.Udp(ControlRig.Address(93), ArtNet.Port);
        await using var rig = await ControlRig.StartAsync(show, service: true, artNetAddress: nodeAddress,
            settings: s => s with { ArtNetEnabled = true, ArtNetInterface = nodeAddress },
            node: n => n with { Led = LedSettings.Default with { Output = true } },
            with: s =>
            {
                sender = new LedSender(new LedSenderOptions { ArtNet = s.ArtNet, Bind = IPAddress.Parse(ControlRig.Loopback) });
                return s with { LedOutput = sender };
            });
        try
        {
            await rig.Ok("BIND ADD ARTNET 0 1 TO SET sf_lobby opacity *");

            // LED out: a frame of the rig's plan, sent through the shared socket.
            var plan = rig.Engine.Led.Current;
            var frame = sender!.Sink.Rent(plan.Rows.Length)!;
            Array.Fill(frame.Slots, (byte)200);
            (frame.LeaderNs, frame.LightMonoNs, frame.PlanSerial) = (1, 0, plan.Serial);
            sender.Sink.Submit(frame);
            sender.Pump(long.MaxValue / 2);
            var buffer = new byte[600];
            var got = new List<(ArtOp Op, IPEndPoint From)>();
            node.ReceiveTimeout = 5000;
            while (got.Count(g => g.Op != ArtOp.Poll) < 2)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var n = node.ReceiveFrom(buffer, ref from);
                got.Add((ArtNet.OpOf(buffer.AsSpan(0, n))!.Value, (IPEndPoint)from));
                if (got[^1].Op == ArtOp.Dmx)
                {
                    Assert.True(ArtNet.TryReadDmx(buffer.AsSpan(0, n), out var h, out var data));
                    Assert.Equal((2, 30, (byte)200), (h.PortAddress, h.Length, data[0]));
                }
            }
            Assert.Equal([ArtOp.Dmx, ArtOp.Sync], got.Where(g => g.Op != ArtOp.Poll).Select(g => g.Op));
            Assert.All(got, g => Assert.Equal(new IPEndPoint(IPAddress.Parse(nodeAddress), ArtNet.Port), g.From));

            // DMX in on the same socket: a console's 20 % reaches the binding.
            var dmx = new byte[530];
            var len = ArtNet.WriteDmx(dmx, 0, 0, [51]);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Math.Abs(rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity - 0.2) > 1e-9)
            {
                Assert.True(DateTime.UtcNow < deadline, "Lobby never took the console's 20 %");
                desk.SendTo(dmx.AsSpan(0, len), SocketFlags.None, new IPEndPoint(IPAddress.Parse(nodeAddress), ArtNet.Port));
                await Task.Delay(50);
            }
            Assert.Equal(ArtNet.Port, rig.ArtNet!.LocalPort);
            Assert.Null(rig.ArtNet.Problem);
            Assert.True(rig.ArtNet.Sent >= 2);
        }
        finally
        {
            await sender!.DisposeAsync();
        }
    }
}
