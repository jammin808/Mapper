using System.Globalization;
using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Dmx;
using PlayMap.Core.Led;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Time;
using PlayMap.Net.Control;
using PlayMap.Net.Led;

namespace PlayMap.Net.Tests.Led;

/// <summary>The sender on loopback with a hand-moved clock: fake Art-Net, sACN and DDP receivers on the slot's own addresses at the
/// spec ports. Frames are decimated on the leader-time grid, leave at the light's instant less the node's latency, every universe
/// then the latch; idle universes are kept alive; stopping sends one black frame (sACN adds three terminations); a WLED device gets
/// 8-bit DDP with push on its last packet; a warm send allocates nothing.</summary>
public sealed class LedSenderTests
{
    private static string Loopback => Environment.GetEnvironmentVariable("PLAYMAP_TEST_LOOPBACK") is { Length: > 0 } a ? a : "127.0.0.1";

    /// <summary>Another address of the slot's /24 (each test binds the spec ports on addresses of its own).</summary>
    private static string Address(int last)
    {
        var parts = Loopback.Split('.');
        return $"{parts[0]}.{parts[1]}.{parts[2]}.{last.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>A UDP receiver at an address and port that hands back what arrived, waiting at most a while for the first packet.</summary>
    private sealed class Receiver : IDisposable
    {
        private readonly Socket _socket;

        public Receiver(string address, int port)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Bind(new IPEndPoint(IPAddress.Parse(address), port));
        }

        /// <summary>Every packet that has arrived but ArtPolls (the sender polls its nodes while it runs), waiting up to 2 s for each of
        /// the first <paramref name="atLeast"/>.</summary>
        public List<(byte[] Bytes, IPEndPoint From)> Take(int atLeast = 1)
        {
            var list = new List<(byte[], IPEndPoint)>();
            var buf = new byte[2048];
            while (list.Count < atLeast ? _socket.Poll(2_000_000, SelectMode.SelectRead) : _socket.Available > 0)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var n = _socket.ReceiveFrom(buf, ref from);
                if (ArtNet.OpOf(buf.AsSpan(0, n)) == ArtOp.Poll)
                {
                    Polls++;
                    continue;
                }
                list.Add((buf[..n], (IPEndPoint)from));
            }
            return list;
        }

        /// <summary>ArtPolls heard so far.</summary>
        public int Polls { get; private set; }

        public void Dispose() => _socket.Dispose();
    }

    private static LedFixture Strip(string id, string type, int pixels, LedPatch patch) =>
        new(id, id, type, LedSpace.Canvas, "cv00000001", Transform3.Identity, new LedLayout(LedShape.Line, pixels, 1, new Vec3d(pixels - 1, 0, 0), LedStart.TopLeft, false, false, []),
            patch, LedSampling.Point, 0, LedColor.Neutral, true, false, false, "#FF8800", []);

    private static LedPlan Plan(LedNode node, params LedFixture[] fixtures)
    {
        var p = Project.CreateDefault("LED");
        p = p with
        {
            Canvases = [new Canvas("cv00000001", "Wall", 1024, 64, "#000000", [])],
            Leds = new LedRig { Nodes = [node], Fixtures = [.. fixtures] },
        };
        return LedPlanBuilder.Build(p, 200, 7);
    }

    private static LedNode Node(LedProtocol protocol, string host, double latencyMs = 0, string vendor = "", double rate = 0) =>
        new("ln00000001", "Node", protocol, host, 0, true, rate, latencyMs, LedSync.Latch, 510, "#FFFFFF") { Vendor = vendor, SacnMulticast = false };

    private static void Submit(LedSender sender, LedPlan plan, long leaderNs, long lightMonoNs, byte fill)
    {
        var f = sender.Sink.Rent(plan.Rows.Length)!;
        Array.Fill(f.Slots, fill, 0, plan.Rows.Length * LedPlan.SlotsPerRow);
        f.LeaderNs = leaderNs;
        f.LightMonoNs = lightMonoNs;
        f.PlanSerial = plan.Serial;
        sender.Sink.Submit(f);
    }

    [Fact]
    public async Task ArtDmxForEveryUniverseThenArtSyncOnTheRateGrid()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var art = new ArtNetEndpoint(Address(40));
        using var node = new Receiver(Address(41), ArtNet.Port);
        await using var sender = new LedSender(new LedSenderOptions { ArtNet = art, Bind = IPAddress.Parse(Address(40)), Clock = clock });
        var plan = Plan(Node(LedProtocol.ArtNet, Address(41)), Strip("lf00000001", "builtin:rgb", 200, new LedPatch("ln00000001", 5, 1)));
        sender.Apply(LedPrograms.Build(plan, Guid.NewGuid(), "PlayMap test"));
        sender.SetMaster(true, false);
        var leader0 = 1_760_000_000_000_000_000L;
        var sends = 0;
        var expected = new List<long>();
        var lastStep = long.MinValue;
        for (var k = 0; k < 60; k++)
        {
            // Frame k is drawn at mono k/60 s for light 10 ms later.
            var leader = leader0 + k * 1_000_000_000L / 60;
            clock.NowNs = 1_000_000_000 + k * 1_000_000_000L / 60;
            Submit(sender, plan, leader, clock.NowNs + 10_000_000, (byte)k);
            if (LedPrograms.StepOf(leader, 44_000) != lastStep)
            {
                lastStep = LedPrograms.StepOf(leader, 44_000);
                expected.Add(k);
            }
            sender.Pump(clock.NowNs);
            clock.NowNs += 10_000_000;
            sender.Pump(clock.NowNs);
            var frames = sender.View().Nodes[0].Frames;
            sends = (int)frames;
        }
        // 60 frames a second decimated to 44 Hz on the leader grid.
        Assert.InRange(sends, 44, 45);
        var packets = node.Take(sends * 3);
        Assert.Equal(sends * 3, packets.Count);
        // Every packet came from the one Art-Net socket (its spec port), and each frame is ArtDmx 5, ArtDmx 6, then ArtSync.
        Assert.All(packets, p => Assert.Equal(ArtNet.Port, p.From.Port));
        for (var i = 0; i < sends; i++)
        {
            Assert.True(ArtNet.TryReadDmx(packets[i * 3].Bytes, out var a, out var da));
            Assert.True(ArtNet.TryReadDmx(packets[i * 3 + 1].Bytes, out var b, out _));
            Assert.True(ArtNet.IsSync(packets[i * 3 + 2].Bytes));
            Assert.Equal((5, 6), (a.PortAddress, b.PortAddress));
            Assert.Equal((510, 90), (a.Length, b.Length));
            Assert.Equal((byte)(i + 1), a.Sequence);
            // The frame sent is the one whose leader step moved (its fill is its frame number).
            Assert.Equal((byte)expected[i], da[0]);
        }
        Assert.Equal(expected.Count, sends);
        Assert.Equal("sending", sender.View().Nodes[0].State);
        Assert.Equal(0, sender.View().Nodes[0].LateMs);
        Assert.Equal(1, node.Polls);
    }

    [Fact]
    public async Task AFrameLeavesAtItsLightLessTheNodesLatencyAndLatenessIsMeasured()
    {
        var clock = new ManualClock(10_000_000_000);
        await using var art = new ArtNetEndpoint(Address(42));
        using var node = new Receiver(Address(43), ArtNet.Port);
        await using var sender = new LedSender(new LedSenderOptions { ArtNet = art, Bind = IPAddress.Parse(Address(42)), Clock = clock });
        var plan = Plan(Node(LedProtocol.ArtNet, Address(43), latencyMs: 20), Strip("lf00000001", "builtin:rgb", 10, new LedPatch("ln00000001", 0, 1)));
        sender.Apply(LedPrograms.Build(plan, Guid.NewGuid(), "PlayMap test"));
        sender.SetMaster(true, false);
        var light = 10_100_000_000L;
        Submit(sender, plan, 1_000_000_000, light, 9);
        var next = sender.Pump(clock.NowNs);
        Assert.Equal(light - 20_000_000, next);
        clock.NowNs = next - 1;
        sender.Pump(clock.NowNs);
        Assert.Equal(0, sender.View().Nodes[0].Packets);
        clock.NowNs = next;
        sender.Pump(clock.NowNs);
        Assert.Equal(2, sender.View().Nodes[0].Packets);
        Assert.Equal(0, sender.View().Nodes[0].LateMs);
        Assert.Equal(1_000_000_000, sender.View().Nodes[0].LastLeaderNs);
        // A frame collected after its instant leaves at once; its lateness is counted (p95 over the last two seconds).
        Submit(sender, plan, 2_000_000_000, clock.NowNs + 10_000_000, 3);
        clock.NowNs += 15_000_000;
        sender.Pump(clock.NowNs);
        Assert.Equal(25, sender.View().Nodes[0].LateMs, 6);
        Assert.Equal("late", sender.View().Nodes[0].State);
        Assert.Equal(4, node.Take(4).Count);
    }

    [Fact]
    public async Task AnIdleUniverseIsKeptAliveEveryNineHundredMilliseconds()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var art = new ArtNetEndpoint(Address(44));
        using var node = new Receiver(Address(45), ArtNet.Port);
        await using var sender = new LedSender(new LedSenderOptions { ArtNet = art, Bind = IPAddress.Parse(Address(44)), Clock = clock });
        var plan = Plan(Node(LedProtocol.ArtNet, Address(45)), Strip("lf00000001", "builtin:rgb", 10, new LedPatch("ln00000001", 3, 1)));
        sender.Apply(LedPrograms.Build(plan, Guid.NewGuid(), "PlayMap test"));
        sender.SetMaster(true, false);
        Submit(sender, plan, 1, clock.NowNs, 77);
        sender.Pump(clock.NowNs);
        var t0 = clock.NowNs;
        Assert.Equal(2, node.Take(2).Count);
        // A frozen show: no frames. Nothing until 900 ms, then the last payload and the latch again, every 900 ms.
        clock.NowNs = t0 + 899_999_999;
        var next = sender.Pump(clock.NowNs);
        Assert.Equal(t0 + 900_000_000, next);
        Assert.Equal(2, sender.View().Nodes[0].Packets);
        for (var i = 1; i <= 3; i++)
        {
            clock.NowNs = t0 + i * 900_000_000L;
            sender.Pump(clock.NowNs);
            var again = node.Take(2);
            Assert.Equal(2, again.Count);
            Assert.True(ArtNet.TryReadDmx(again[0].Bytes, out var h, out var data));
            Assert.Equal((3, (byte)(i + 1)), (h.PortAddress, h.Sequence));
            Assert.Equal(77, data[0]);
            Assert.True(ArtNet.IsSync(again[1].Bytes));
        }
    }

    [Fact]
    public async Task StoppingSendsOneBlackFrameAndSacnTerminatesThreeTimes()
    {
        var clock = new ManualClock(1_000_000_000);
        using var receiver = new Receiver(Address(46), Sacn.Port);
        await using var sender = new LedSender(new LedSenderOptions { Bind = IPAddress.Parse(Address(47)), Clock = clock });
        var mode = new LedFixtureType("lt00000001", "Mode RGB", [new LedChannelSlot(LedChannel.Constant, 8, 9)],
            [new LedChannelSlot(LedChannel.R, 8, 0), new LedChannelSlot(LedChannel.G, 8, 0), new LedChannelSlot(LedChannel.B, 8, 0)], [], 0, "", "");
        var p = Project.CreateDefault("LED");
        var node = Node(LedProtocol.Sacn, Address(46));
        p = p with
        {
            Canvases = [new Canvas("cv00000001", "Wall", 1024, 64, "#000000", [])],
            Leds = new LedRig { Nodes = [node], Types = [mode], Fixtures = [Strip("lf00000001", mode.Id, 200, new LedPatch(node.Id, 1, 1))] },
        };
        var plan = LedPlanBuilder.Build(p, 200, 3);
        var cid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        sender.Apply(LedPrograms.Build(plan, cid, "PlayMap test"));
        sender.SetMaster(true, false);
        Submit(sender, plan, 1, clock.NowNs, 200);
        sender.Pump(clock.NowNs);
        Assert.Equal(2, receiver.Take(2).Count);
        sender.SetMaster(false, blackFirst: true);
        clock.NowNs += 1_000_000;
        sender.Pump(clock.NowNs);
        var stop = receiver.Take(8);
        Assert.Equal(8, stop.Count);
        // The black frame (constants kept), then three terminated packets per universe; then silence.
        for (var i = 0; i < 8; i++)
        {
            Assert.True(Sacn.TryReadData(stop[i].Bytes, out var h, out var slots));
            Assert.Equal(cid, h.Cid);
            Assert.Equal(i >= 2, h.Terminated);
            Assert.Equal(i % 2 == 0 ? 1 : 2, h.Universe);
            if (h.Universe == 1)
                Assert.Equal((9, 0, 0), (slots[0], slots[1], slots[300]));
        }
        Submit(sender, plan, 2, clock.NowNs, 200);
        clock.NowNs += 5_000_000_000;
        sender.Pump(clock.NowNs);
        Assert.Equal("off", sender.View().Nodes[0].State);
        Assert.False(sender.View().Output);
    }

    [Fact]
    public async Task AWledDeviceGetsEightBitDdpWithPushOnItsLastPacket()
    {
        var clock = new ManualClock(1_000_000_000);
        using var wled = new Receiver(Address(48), Ddp.Port);
        await using var sender = new LedSender(new LedSenderOptions { Bind = IPAddress.Parse(Address(49)), Clock = clock });
        var plan = Plan(Node(LedProtocol.Ddp, Address(48), vendor: "wled"), Strip("lf00000001", "builtin:rgb", 600, new LedPatch("ln00000001", 0, 1)));
        Assert.Equal(Ddp.Rgb8, plan.Nodes[0].DdpType);
        sender.Apply(LedPrograms.Build(plan, Guid.NewGuid(), "PlayMap test"));
        sender.SetMaster(true, false);
        Submit(sender, plan, 1, clock.NowNs, 5);
        sender.Pump(clock.NowNs);
        var packets = wled.Take(2);
        Assert.Equal(2, packets.Count);
        Assert.True(Ddp.TryRead(packets[0].Bytes, out var a, out _, out _));
        Assert.True(Ddp.TryRead(packets[1].Bytes, out var b, out var tail, out _));
        Assert.Equal(new DdpHeader(false, 1, Ddp.Rgb8, 1, 0, 1440), a);
        Assert.Equal(new DdpHeader(true, 2, Ddp.Rgb8, 1, 1440, 360), b);
        Assert.Equal(5, tail[359]);
    }

    [Fact]
    public async Task AFrameOfAnotherPlanIsDroppedAndAWarmSendAllocatesNothing()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var art = new ArtNetEndpoint(Address(50));
        using var node = new Receiver(Address(51), ArtNet.Port);
        await using var sender = new LedSender(new LedSenderOptions { ArtNet = art, Bind = IPAddress.Parse(Address(50)), Clock = clock });
        var plan = Plan(Node(LedProtocol.ArtNet, Address(51), rate: 1000), Strip("lf00000001", "builtin:rgb", 400, new LedPatch("ln00000001", 0, 1)));
        sender.Apply(LedPrograms.Build(plan, Guid.NewGuid(), "PlayMap test"));
        sender.SetMaster(true, false);
        var f = sender.Sink.Rent(plan.Rows.Length)!;
        f.PlanSerial = plan.Serial + 1;
        sender.Sink.Submit(f);
        sender.Pump(clock.NowNs);
        Assert.Equal(1, sender.Stale);
        Assert.Equal(0, sender.View().Nodes[0].Packets);
        void Cycle(int k)
        {
            clock.NowNs += 1_000_000;
            Submit(sender, plan, k * 1_000_000L, clock.NowNs, (byte)k);
            sender.Pump(clock.NowNs);
        }
        for (var k = 1; k <= 200; k++)
            Cycle(k);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var k = 201; k <= 2200; k++)
            Cycle(k);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(2200, sender.View().Nodes[0].Frames);
        Assert.NotEmpty(node.Take());
    }

    /// <summary>An Art-Net sender and node pair on two addresses of the slot, with a program from a rig of strips.</summary>
    private static (ManualClock Clock, ArtNetEndpoint Art, LedSender Sender) ArtNetPair(int at)
    {
        var clock = new ManualClock(1_000_000_000);
        var art = new ArtNetEndpoint(Address(at));
        var sender = new LedSender(new LedSenderOptions { ArtNet = art, Bind = IPAddress.Parse(Address(at)), Clock = clock });
        return (clock, art, sender);
    }

    [Fact]
    public async Task AHeldStopIsKeptWhenTheProgramThatDropsTheNodeFollowsIt()
    {
        var (clock, art, sender) = ArtNetPair(52);
        await using var artOwned = art;
        await using var senderOwned = sender;
        using var node = new Receiver(Address(53), ArtNet.Port);
        var plan = Plan(Node(LedProtocol.ArtNet, Address(53)), Strip("lf00000001", "builtin:rgb", 10, new LedPatch("ln00000001", 0, 1)));
        sender.Apply(LedPrograms.Build(plan, Guid.NewGuid(), "t"));
        sender.SetMaster(true, false);
        Submit(sender, plan, 1, clock.NowNs, 90);
        sender.Pump(clock.NowNs);
        Assert.Equal(2, node.Take(2).Count);
        var packets = sender.View().Nodes[0].Packets;
        // LED OUTPUT OFF <node> HOLD: the stop, then the program without the node, in one pump.
        sender.StopNode("ln00000001", blackFirst: false);
        sender.Apply(LedPrograms.Build(Plan(Node(LedProtocol.ArtNet, Address(53)) with { Enabled = false }), Guid.NewGuid(), "t"));
        clock.NowNs += 5_000_000_000;
        sender.Pump(clock.NowNs);
        Assert.Equal(2, packets);
        Assert.Empty(sender.View().Nodes);
        Assert.Empty(node.Take(0));
    }

    [Fact]
    public async Task AUniverseThePatchLeavesIsBlackedAndTheRestCarryOn()
    {
        var (clock, art, sender) = ArtNetPair(55);
        await using var artOwned = art;
        await using var senderOwned = sender;
        using var node = new Receiver(Address(56), ArtNet.Port);
        var n = Node(LedProtocol.ArtNet, Address(56));
        var plan = Plan(n, Strip("lf00000001", "builtin:rgb", 10, new LedPatch(n.Id, 5, 1)), Strip("lf00000002", "builtin:rgb", 10, new LedPatch(n.Id, 6, 1)));
        sender.Apply(LedPrograms.Build(plan, Guid.NewGuid(), "t"));
        sender.SetMaster(true, false);
        Submit(sender, plan, 1, clock.NowNs, 120);
        sender.Pump(clock.NowNs);
        Assert.Equal(3, node.Take(3).Count);
        // The second strip moves from universe 6 to 9: universe 6 goes black (and is latched) at once.
        var moved = Plan(n, Strip("lf00000001", "builtin:rgb", 10, new LedPatch(n.Id, 5, 1)), Strip("lf00000002", "builtin:rgb", 10, new LedPatch(n.Id, 9, 1)));
        sender.Apply(LedPrograms.Build(moved, Guid.NewGuid(), "t"));
        sender.Pump(clock.NowNs + 1);
        var black = node.Take(2);
        Assert.Equal(2, black.Count);
        Assert.True(ArtNet.TryReadDmx(black[0].Bytes, out var h, out var data));
        Assert.Equal(6, h.PortAddress);
        Assert.All(data.ToArray(), b => Assert.Equal(0, b));
        Assert.True(ArtNet.IsSync(black[1].Bytes));
        // A node moved to another address: the old one is blacked whole.
        var elsewhere = Plan(n with { Host = Address(57) }, Strip("lf00000001", "builtin:rgb", 10, new LedPatch(n.Id, 5, 1)));
        sender.Apply(LedPrograms.Build(elsewhere, Guid.NewGuid(), "t"));
        sender.Pump(clock.NowNs + 2);
        var whole = node.Take(3);
        Assert.Equal([5, 9], whole.Take(2).Select(p => ArtNet.TryReadDmx(p.Bytes, out var x, out _) ? x.PortAddress : -1));
    }

    [Fact]
    public async Task TheGuardRefusingBlacksTheRigAndTurningBackOnResumes()
    {
        var clock = new ManualClock(1_000_000_000);
        using var receiver = new Receiver(Address(58), Sacn.Port);
        await using var sender = new LedSender(new LedSenderOptions { Bind = IPAddress.Parse(Address(59)), Clock = clock });
        var n = Node(LedProtocol.Sacn, Address(58)) with { SacnSyncUniverse = 7000 };
        var p = Project.CreateDefault("LED") with { Canvases = [new Canvas("cv00000001", "Wall", 1024, 64, "#000000", [])] };
        p = p with { Leds = new LedRig { Nodes = [n], Fixtures = [Strip("lf00000001", "builtin:rgb", 10, new LedPatch(n.Id, 1, 1))] } };
        var plan = LedPlanBuilder.Build(p, 200, 1);
        sender.Apply(LedPrograms.Build(plan, Guid.NewGuid(), "t"));
        sender.SetMaster(true, false);
        Submit(sender, plan, 1, clock.NowNs, 50);
        sender.Pump(clock.NowNs);
        var first = receiver.Take(2);
        // The data packet names the sync universe; the sync packet follows it.
        Assert.True(Sacn.TryReadData(first[0].Bytes, out var d, out _));
        Assert.Equal(7000, d.SyncAddress);
        Assert.True(Sacn.TryReadSync(first[1].Bytes, out var sync));
        Assert.Equal(7000, sync.SyncAddress);
        var refused = LedPlanBuilder.Build(p, 0, 2);
        sender.Apply(LedPrograms.Build(refused, Guid.NewGuid(), "t"));
        sender.Pump(clock.NowNs + 1);
        var stop = receiver.Take(5);
        Assert.Equal(5, stop.Count);
        Assert.True(Sacn.TryReadData(stop[0].Bytes, out var b0, out var slots));
        Assert.Equal((false, 0), (b0.Terminated, (int)slots[0]));
        Assert.All(stop.Skip(2), x => Assert.True(Sacn.TryReadData(x.Bytes, out var t, out _) && t.Terminated));
        Assert.Equal("refused", sender.View().Nodes[0].State);
        // Master off and on again: frames flow from the next leader step.
        sender.SetMaster(false, false);
        sender.Apply(LedPrograms.Build(LedPlanBuilder.Build(p, 200, 3), Guid.NewGuid(), "t"));
        sender.SetMaster(true, false);
        clock.NowNs += 10_000_000;
        sender.Pump(clock.NowNs);
        var again = sender.Sink.Rent(1)!;
        (again.LeaderNs, again.LightMonoNs, again.PlanSerial) = (1_000_000_000, clock.NowNs, 3);
        sender.Sink.Submit(again);
        sender.Pump(clock.NowNs);
        Assert.True(Sacn.TryReadData(receiver.Take(1)[0].Bytes, out var resumed, out _));
        Assert.False(resumed.Terminated);
    }

    [Fact]
    public async Task ADisposedSenderLeavesTheRigDarkAndItsSinkRefusesQuietly()
    {
        var (clock, art, sender) = ArtNetPair(60);
        await using var artOwned = art;
        using var node = new Receiver(Address(61), ArtNet.Port);
        var plan = Plan(Node(LedProtocol.ArtNet, Address(61)), Strip("lf00000001", "builtin:rgb", 10, new LedPatch("ln00000001", 4, 1)));
        sender.Apply(LedPrograms.Build(plan, Guid.NewGuid(), "t"));
        sender.SetMaster(true, false);
        Submit(sender, plan, 1, clock.NowNs, 99);
        sender.Pump(clock.NowNs);
        Assert.Equal(2, node.Take(2).Count);
        // A frame the render thread holds while the node stops.
        var held = sender.Sink.Rent(plan.Rows.Length)!;
        await sender.DisposeAsync();
        // The node stopping sends one black frame, latched.
        var stop = node.Take(2);
        Assert.Equal(2, stop.Count);
        Assert.True(ArtNet.TryReadDmx(stop[0].Bytes, out var h, out var data));
        Assert.Equal(4, h.PortAddress);
        Assert.All(data.ToArray(), b => Assert.Equal(0, b));
        Assert.True(ArtNet.IsSync(stop[1].Bytes));
        // Afterwards the sink neither throws nor hands out frames.
        sender.Sink.Submit(held);
        Assert.Null(sender.Sink.Rent(plan.Rows.Length));
    }
}
