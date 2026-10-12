using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Net.Bus;

namespace PlayMap.Net.Tests;

/// <summary>The Show Bus hello through real UDP on loopback: two nodes hear each other and never themselves, a
/// peer ages live → stale → gone → forgotten on the monotonic clock, PlayMap's hello says what the contract asks
/// (and no secret), Patterns' beacon port is never a target, a loopback-only node stays silent, and a stray
/// datagram is dropped.</summary>
public class HelloTests
{
    private static NodeSettings Node(string id, string name, string bind = "") =>
        TestSettings.Make() with { NodeId = id, Name = name, Bind = bind, HttpPort = 9940, WirePort = 9941, SyncPort = 9942 };

    private static HelloService Make(NodeSettings s, IMonoClock clock) =>
        new(s, () => HelloService.PlayMapHello(s, 1760101234567, "sha256:" + new string('a', 64)), clock,
            new HelloOptions { ListenPort = 0, Targets = [], Every = TimeSpan.FromMilliseconds(100), SweepEvery = TimeSpan.FromHours(1) });

    [Fact]
    public async Task TwoNodesHearEachOtherAndAgeOutOnTheClock()
    {
        var clock = new ManualClock(1_000_000_000);
        await using var a = Make(Node("aaaa0001", "Stage left"), clock);
        var b = Make(Node("bbbb0002", "Stage right"), clock);
        try
        {
            a.Start();
            b.Start();
            a.Targets = [new IPEndPoint(IPAddress.Loopback, b.ListenPort), new IPEndPoint(IPAddress.Loopback, a.ListenPort)];
            b.Targets = [new IPEndPoint(IPAddress.Loopback, a.ListenPort)];
            Assert.True(await Eventually.TrueAsync(() => a.Peers.Count == 1 && b.Peers.Count == 1));

            var seen = a.Peers.Find("bbbb0002")!;
            Assert.Equal("Stage right", seen.Name);
            Assert.Equal("playmap", seen.Kind);
            Assert.Equal("127.0.0.1", seen.Address);
            Assert.Equal(9941, seen.Hello.Port("wire"));
            Assert.Equal(9940, seen.Hello.Port("https"));
            Assert.True(seen.Has("video.transport"));
            Assert.Equal(PeerLiveness.Live, seen.Liveness);
            Assert.Null(a.Peers.Find("aaaa0001"));   // its own hellos came back and were known by the instance
            Assert.Equal("aaaa0001", Assert.Single(b.Peers.Snapshot()).Id);
        }
        finally
        {
            await b.DisposeAsync();
        }

        var changes = new List<PeerChange>();
        a.Peers.Changed += changes.Add;
        await Task.Delay(150);   // a datagram still in flight lands before the clock moves
        clock.AdvanceMs(7000);
        a.Peers.Sweep();
        Assert.Equal(PeerLiveness.Stale, Assert.Single(changes).Peer.Liveness);
        clock.AdvanceMs(6000);
        a.Peers.Sweep();
        Assert.Equal(PeerLiveness.Gone, changes[^1].Peer.Liveness);
        Assert.Equal(PeerLiveness.Gone, a.Peers.Find("bbbb0002")!.Liveness);
        clock.AdvanceMs(48000);
        a.Peers.Sweep();
        Assert.Equal(PeerChangeKind.Forgotten, changes[^1].Kind);
        Assert.Equal(0, a.Peers.Count);
    }

    [Fact]
    public async Task APeerHeardAgainIsLiveAndANewNameIsAChange()
    {
        var clock = new ManualClock();
        var table = new PeerTable(clock);
        var changes = new List<PeerChange>();
        table.Changed += changes.Add;
        var s = Node("cccc0003", "FOH");
        table.Hear(HelloService.PlayMapHello(s, 1, null), "10.0.0.5");
        table.Hear(HelloService.PlayMapHello(s, 2, null), "10.0.0.5");
        Assert.Equal([PeerChangeKind.Added], changes.Select(c => c.Kind));
        clock.AdvanceMs(8000);
        table.Sweep();
        table.Hear(HelloService.PlayMapHello(s, 3, null), "10.0.0.5");
        Assert.Equal(PeerLiveness.Live, changes[^1].Peer.Liveness);
        table.Hear(HelloService.PlayMapHello(s with { Name = "FOH 2" }, 4, null), "10.0.0.5");
        Assert.Equal(PeerChangeKind.Changed, changes[^1].Kind);
        Assert.Equal("FOH 2", table.Find("cccc0003")!.Name);
        await Task.CompletedTask;
    }

    [Fact]
    public void PlayMapsHelloSaysWhatTheContractAsksAndNoSecret()
    {
        var s = Node("a1b2c3d4", "Stage left");
        var bytes = (HelloService.PlayMapHello(s, 1760101234567, "sha256:" + new string('b', 64), "RACK-1") with { Instance = "0badf00d" }).ToJson();
        Assert.InRange(bytes.Length, 1, ShowBusConstants.HelloMaxBytes);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(Pairing.Normalise(TestSettings.Token), Pairing.Normalise(text), StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(bytes);
        var r = doc.RootElement;
        Assert.Equal("playmap", r.GetProperty("t").GetString());
        Assert.Equal(1, r.GetProperty("v").GetInt32());
        Assert.Equal(9940, r.GetProperty("web").GetInt32());
        Assert.Equal(9941, r.GetProperty("wire").GetInt32());
        Assert.Equal(9940, r.GetProperty("ports").GetProperty("https").GetInt32());
        Assert.Equal(9942, r.GetProperty("ports").GetProperty("sync").GetInt32());
        Assert.Equal("0badf00d", r.GetProperty("inst").GetString());
        Assert.StartsWith("sha256:", r.GetProperty("tls").GetString(), StringComparison.Ordinal);
        Assert.Contains("video.seek", r.GetProperty("caps").EnumerateArray().Select(c => c.GetString()));
        var back = ShowBusHello.Parse(bytes)!;
        Assert.Equal("RACK-1", back.Host);
    }

    [Fact]
    public void PatternsBeaconPortIsNeverATarget()
    {
        var s = Node("a1b2c3d4", "Stage left");
        Assert.Throws<ArgumentException>(() => new HelloService(s, () => HelloService.PlayMapHello(s, 1, null), null,
            new HelloOptions { Targets = [new IPEndPoint(IPAddress.Broadcast, 9700)] }));
    }

    [Fact]
    public async Task ANodeBoundToLoopbackOnlySaysNoHelloAndAStrayDatagramIsDropped()
    {
        var s = Node("a1b2c3d4", "Stage left", "127.0.0.1");
        await using var hello = new HelloService(s, () => HelloService.PlayMapHello(s, 1, null), new ManualClock(),
            new HelloOptions { ListenPort = 0, Every = TimeSpan.FromMilliseconds(50) });
        hello.Start();
        Assert.False(hello.Announcing);
        using var udp = new UdpClient();
        await udp.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"x\"}"), new IPEndPoint(IPAddress.Loopback, hello.ListenPort));
        await udp.SendAsync(new byte[] { 1, 2, 3 }, new IPEndPoint(IPAddress.Loopback, hello.ListenPort));
        Assert.True(await Eventually.TrueAsync(() => hello.Dropped == 2));
        Assert.Equal(0, hello.Sent);
        Assert.Equal(0, hello.Peers.Count);
    }
}
