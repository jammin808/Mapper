using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using PlayMap.Core.Dmx;
using PlayMap.Core.Time;
using PlayMap.Net.Control;
using PlayMap.Tests;

namespace PlayMap.Engine.Tests.Control;

/// <summary>The node's one Art-Net socket (Art-Net 4: UDP 6454; ArtPoll answered by a unicast ArtPollReply within a random delay of
/// up to 1 s; ArtTimeCode), each test on a loopback address of the slot's own.</summary>
public sealed class ArtNetInTests
{
    private static void Send(Socket from, string to, ReadOnlySpan<byte> packet) =>
        from.SendTo(packet, SocketFlags.None, new IPEndPoint(IPAddress.Parse(to), ArtNet.Port));

    /// <summary>Sends <paramref name="perWindow"/> packets for each slot of <paramref name="bytes"/>, writes the bytes this thread
    /// allocated and the methods the runtime compiled on it over each window, and answers the sends refused. Compiled fully
    /// optimised before it first runs, and never inlined: the windows are never where the runtime compiles the loop that counts
    /// them (a tier-0 loop is recompiled on stack, on this thread, and that compile can grow the runtime's cast cache or box a
    /// static here). Each window begins as every allocation window of the suites does (Allocations.Start).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static int SendCounting(ArtNetEndpoint endpoint, ReadOnlySpan<byte> packet, SocketAddress to, long[] bytes, long[] compiled, int perWindow)
    {
        var refused = 0;
        for (var k = 0; k < bytes.Length; k++)
        {
            var jitted = JitInfo.GetCompiledMethodCount(currentThread: true);
            var before = Allocations.Start();
            for (var i = 0; i < perWindow; i++)
            {
                if (!endpoint.Send(packet, to))
                    refused++;
            }
            bytes[k] = Allocations.Since(before);
            compiled[k] = JitInfo.GetCompiledMethodCount(currentThread: true) - jitted;
        }
        return refused;
    }

    /// <summary>Binds the sockets' native send methods now. The runtime binds a native method's target when new code first calls it
    /// through its stub (a 56 B string, once in the process, on the calling thread): a send path recompiled at tier 1 during the
    /// windows would bind it there.</summary>
    private static void BindSocketSends()
    {
        var bound = 0;
        foreach (var type in typeof(Socket).Assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0 || !method.Name.Contains("send", StringComparison.OrdinalIgnoreCase))
                    continue;
                Marshal.Prelink(method);
                bound++;
            }
        }
        Assert.True(bound > 0, "System.Net.Sockets has no native send method to bind");
    }

    /// <summary>A warm send allocates nothing. The windows hold the sends alone: the counting loop is compiled before it runs and the
    /// native send methods are bound before the first window, so the runtime's own first-time work (a compile, a binding) is not
    /// counted against them; what any window allocated, the sends refused and an exception thrown on this thread are the words.</summary>
    [Fact]
    public async Task TenThousandSendsAllocateNothing()
    {
        await using var endpoint = new ArtNetEndpoint(ControlRig.Address(41));
        using var receiver = ControlRig.Udp(ControlRig.Address(42), ArtNet.Port);
        var to = endpoint.AddressOf(new IPEndPoint(IPAddress.Parse(ControlRig.Address(42)), ArtNet.Port));
        var packet = new byte[530];
        var n = ArtNet.WriteDmx(packet, 1, 0, new byte[512]);
        BindSocketSends();
        SendCounting(endpoint, packet.AsSpan(0, n), to, new long[1], new long[1], 200);
        var perThousand = new long[10];
        var compiled = new long[10];
        var refused = 0;
        Exception? thrown = null;
        var thread = Environment.CurrentManagedThreadId;
        EventHandler<FirstChanceExceptionEventArgs> seen = (_, e) =>
        {
            if (Environment.CurrentManagedThreadId == thread)
                thrown ??= e.Exception;
        };
        AppDomain.CurrentDomain.FirstChanceException += seen;
        try
        {
            refused = SendCounting(endpoint, packet.AsSpan(0, n), to, perThousand, compiled, 1000);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= seen;
        }
        Assert.True(perThousand.All(b => b == 0), $"bytes per thousand sends: {string.Join(' ', perThousand)}; methods compiled on this "
            + $"thread meanwhile: {string.Join(' ', compiled)}; {refused} refused; "
            + (thrown is null ? "nothing thrown on this thread" : $"{thrown.GetType().Name}: {thrown.Message}"));
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
    public void ASocketOnEveryInterfaceAnswersAPollerWithItsAddressOnThePollersNetwork()
    {
        // Each IPv4 network of this machine: a poller there (another host of that network) is told this machine's address on it.
        var networks = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.PrefixLength is > 0 and < 31).ToList();
        foreach (var u in networks)
        {
            var host = BinaryPrimitives.ReadUInt32BigEndian(u.Address.GetAddressBytes());
            var mask = uint.MaxValue << (32 - u.PrefixLength);
            var peer = (host & mask) | ((host & ~mask) == 1 ? 2u : 1u);
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, peer);
            Assert.Equal(u.Address, Engine.Control.ControlService.LocalAddressToward(new IPAddress(bytes)));
        }
        Assert.True(IPAddress.IsLoopback(Engine.Control.ControlService.LocalAddressToward(IPAddress.Parse(ControlRig.Address(44)))));
    }

    [Fact]
    public async Task ASourceOutsideTheAllowListCannotHoldTheAllowedConsolesFrames()
    {
        // On a clock that never moves, a frame held for an ArtSync (Art-Net 4: once a sync is seen, frames wait for the next) would
        // wait for ever: only the allowed console's packets reach the input, so its frames fire.
        var clock = new ManualClock(1_000_000_000);
        var node = ControlRig.Address(47);
        var allowed = ControlRig.Address(48);
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, artNetAddress: node, clock: clock,
            settings: s => s with { ArtNetEnabled = true, ArtNetInterface = node, DmxAllow = [allowed] });
        await rig.Ok("BIND ADD ARTNET 0 1 TO SET sf_lobby opacity *");
        using var allowedDesk = ControlRig.Udp(allowed, ArtNet.Port);
        using var stranger = ControlRig.Udp(ControlRig.Address(49), ArtNet.Port);
        var sync = new byte[14];
        ArtNet.WriteSync(sync);
        Send(stranger, node, sync);
        await ControlRig.Until(() => rig.ArtNet!.Packets >= 1, "the stranger's ArtSync");
        // The allowed console refreshes its universe (Art-Net 4: at least every 4 s; here every 50 ms) until the binding takes it.
        var dmx = new byte[530];
        var n = ArtNet.WriteDmx(dmx, 0, 0, [51]);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Math.Abs(rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity - 0.2) > 1e-9)
        {
            Assert.True(DateTime.UtcNow < deadline, "Lobby never took the allowed console's 20 %");
            Send(allowedDesk, node, dmx.AsSpan(0, n));
            await Task.Delay(50);
        }
        var monitor = JsonDocument.Parse((await rig.Ok("CONTROL MONITOR")).Payload!).RootElement;
        Assert.All(monitor.EnumerateArray(), row => Assert.Equal(allowed, row.GetProperty("source").GetString()));
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
        // The code runs at 25 fps against the real clock (frame n at 40n ms, sent as its frame begins) for as long as it takes the
        // source to read rolling: code that stops reads dropout 2.5 frames later, so the state is read while the code still runs.
        var tc = new byte[ArtNet.TimeCodeLength];
        string? state = null;
        JsonElement source = default;
        var run = Stopwatch.StartNew();
        var last = -1L;
        while (state != "rolling" && run.Elapsed < TimeSpan.FromSeconds(10))
        {
            var n = run.ElapsedMilliseconds / 40;
            if (n != last)
            {
                last = n;
                ArtNet.WriteTimeCode(tc, new ArtTimeCode((int)(n % 25), 30 + (int)(n / 25), 2, 1, 1, 0));
                Send(desk, node, tc);
                source = rig.State().GetProperty("timecode").GetProperty("sources")[0];
                state = source.GetProperty("state").GetString();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, 40 - (run.ElapsedMilliseconds % 40))));
        }
        Assert.True(state == "rolling", source.ToString());
        Assert.StartsWith("01:02:", source.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("25", source.GetProperty("detected").GetString());
        // Art-Net control input is off: the DMX frame was heard by the socket and fired nothing.
        Assert.Equal(1, rig.Engine.Store.Project.FindSurface("sf_lobby")!.Opacity);
        Assert.False(rig.State().GetProperty("control").GetProperty("dmx").GetProperty("artnet").GetProperty("enabled").GetBoolean());
    }
}
