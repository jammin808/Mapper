using System.Globalization;
using System.Net;
using System.Net.Sockets;
using PlayMap.Core.Bus;
using PlayMap.Core.Dmx;
using PlayMap.Core.Preflight;

namespace PlayMap.Engine.Tests.Control;

/// <summary>sACN control input on this slot's loopback addresses and multicast groups of its own (universes 63000 + 8 × slot + n):
/// two consoles on a universe's group merge by priority into a bound fader; the higher one terminating hands the fader back; a
/// surface named by a console's CID hears only that console; unicast to the node's interface is heard too; the verbs, STATE and
/// preflight say what is on.</summary>
public sealed class SacnInTests
{
    private static readonly Guid Main = Guid.Parse("0e1f2a3b-4c5d-4e6f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid Backup = Guid.Parse("1e1f2a3b-4c5d-4e6f-8a9b-0c1d2e3f4a5b");

    /// <summary>A universe of this slot's own (its multicast group is no other slot's).</summary>
    private static int Universe(int n) => 63000 + 8 * int.Parse(ControlRig.Loopback.Split('.')[1], CultureInfo.InvariantCulture) + n;

    private static Socket Console(string address)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Parse(address), 0));
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Parse(address).GetAddressBytes());
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        return s;
    }

    private static void Send(Socket from, IPAddress to, Guid cid, int universe, byte sequence, int priority, byte level1, byte level2 = 0, bool terminated = false)
    {
        var slots = new byte[512];
        slots[0] = level1;
        slots[1] = level2;
        var packet = new byte[638];
        var n = Sacn.WriteData(packet, new SacnData(cid, cid == Main ? "Main desk" : "Backup desk", priority, 0, sequence, false, terminated, false, universe, 0, 512), slots);
        from.SendTo(packet.AsSpan(0, n), SocketFlags.None, new IPEndPoint(to, Sacn.Port));
    }

    /// <summary>Sends a console's frame every 20 ms (a new sequence each) until the condition holds, or fails after 10 s.</summary>
    private static async Task StreamUntil(Func<bool> condition, string what, Action<byte> frame)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        byte seq = 0;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("timed out waiting for " + what);
            frame(++seq);
            await Task.Delay(20);
        }
    }

    private static double Opacity(ControlRig rig, string id) => rig.Engine.Store.Project.FindSurface(id)!.Opacity;

    [Fact]
    public async Task TwoConsolesOnAGroupMergeByPriorityAndTheHigherOneLeavingHandsItBack()
    {
        var u = Universe(0);
        var iface = ControlRig.Address(61);
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true, settings: s => s with { SacnEnabled = true, SacnInterface = iface });
        await rig.Ok($"BIND ADD SACN {u} 1 TO SET sf_lobby opacity *");
        await ControlRig.Until(() => rig.State().GetProperty("control").GetProperty("dmx").GetProperty("sacn").GetProperty("universes").EnumerateArray()
            .Any(x => x.GetInt32() == u), "the group joined");
        var group = Sacn.GroupOf(u);
        using var main = Console(ControlRig.Address(62));
        using var backup = Console(ControlRig.Address(63));
        byte mainSeq = 0;
        await StreamUntil(() => Math.Abs(Opacity(rig, "sf_lobby") - 128 / 255.0) < 1e-6, "the main desk's level", seq =>
        {
            mainSeq = seq;
            Send(main, group, Main, u, seq, 100, 128);
        });
        // The backup desk at priority 150 takes the fader, lower as its level is.
        byte backupSeq = 0;
        await StreamUntil(() => Math.Abs(Opacity(rig, "sf_lobby") - 64 / 255.0) < 1e-6, "the backup desk's level", seq =>
        {
            Send(main, group, Main, u, ++mainSeq, 100, 128);
            backupSeq = seq;
            Send(backup, group, Backup, u, seq, 150, 64);
        });
        var sacn = rig.State().GetProperty("control").GetProperty("dmx").GetProperty("sacn");
        Assert.True(sacn.GetProperty("open").GetBoolean());
        Assert.Equal(iface, sacn.GetProperty("interface").GetString());
        // The backup desk stops (its stream terminated): the main desk has the fader again.
        Send(backup, group, Backup, u, ++backupSeq, 150, 64, terminated: true);
        await StreamUntil(() => Math.Abs(Opacity(rig, "sf_lobby") - 128 / 255.0) < 1e-6, "the main desk again", _ => Send(main, group, Main, u, ++mainSeq, 100, 128));
        await ControlRig.Until(() => rig.State().GetProperty("control").GetProperty("dmx").GetProperty("sacn").GetProperty("sources").GetArrayLength() == 1, "one source");
        var source = rig.State().GetProperty("control").GetProperty("dmx").GetProperty("sacn").GetProperty("sources")[0];
        Assert.Equal((u, "Main desk", ControlRig.Address(62), 100), (source.GetProperty("universe").GetInt32(), source.GetProperty("name").GetString(),
            source.GetProperty("address").GetString(), source.GetProperty("priority").GetInt32()));
        Assert.Equal(Main.ToString("D"), source.GetProperty("cid").GetString());
    }

    [Fact]
    public async Task UnicastIsHeardASurfaceByCidHearsItsConsoleOnlyAndTheVerbsSayWhatIsOn()
    {
        var u = Universe(1);
        var iface = ControlRig.Address(64);
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show(), service: true);
        var preflight = new Engine.Control.ControlPreflight();
        var context = new PreflightContext(rig.Engine, PreflightDepth.Quick, false);
        await rig.Ok($"CONTROL SURFACE ADD Backup SACN {Backup}");
        await rig.Ok($"BIND ADD SACN {u} 1 TO SET sf_lobby opacity *");
        await rig.Ok($"BIND ADD SACN Backup {u} 2 TO SET sf_stage opacity *");
        var rows = (await preflight.RunAsync(context, CancellationToken.None)).ToDictionary(r => r.Id);
        Assert.Equal((Light.Red, "CONTROL DMX SACN ON"), (rows["control:sacn"].Light, rows["control:sacn"].Fix));

        Assert.Equal($"sACN control input on at {iface} — STATE says what it hears", (await rig.Ok($"CONTROL DMX SACN ON {iface}")).Effect);
        Assert.Equal("sACN control input is already on", await rig.Refused("CONTROL DMX SACN ON"));
        await ControlRig.Until(() => rig.Service!.SacnListening, "the receiver open");
        rows = (await preflight.RunAsync(context, CancellationToken.None)).ToDictionary(r => r.Id);
        Assert.Equal(Light.Green, rows["control:sacn"].Light);

        // Unicast to the node's interface: the main desk moves the unsurfaced binding, not the Backup surface's.
        using var main = Console(ControlRig.Address(65));
        using var backup = Console(ControlRig.Address(66));
        var node = IPAddress.Parse(iface);
        await StreamUntil(() => Math.Abs(Opacity(rig, "sf_lobby") - 0.2) < 1e-6, "the main desk's level", seq => Send(main, node, Main, u, seq, 100, 51, 200));
        Assert.Equal(1, Opacity(rig, "sf_stage"));
        // The backup desk at a higher priority: both bindings follow it (the Backup surface's channel is its own).
        await StreamUntil(() => Math.Abs(Opacity(rig, "sf_stage") - 0.4) < 1e-6, "the backup desk's channel 2", seq => Send(backup, node, Backup, u, seq, 120, 51, 102));

        Assert.Equal("sACN control input off — DMX no longer fires bindings", (await rig.Ok("CONTROL DMX SACN OFF")).Effect);
        Assert.False(rig.Service!.SacnListening);
        Assert.False(rig.State().GetProperty("control").GetProperty("dmx").GetProperty("sacn").GetProperty("enabled").GetBoolean());
        var personality = await rig.Ok($$"""BIND ADD {"personality":"playmap","source":"sacn","universe":{{u}},"address":101}""");
        Assert.Contains($"SACN {u} 101", string.Join(' ', rig.Engine.Store.Project.Control.Bindings.Select(b => b.Input)), StringComparison.Ordinal);
        Assert.Contains("\"sheet\"", personality.Payload, StringComparison.Ordinal);
    }
}
