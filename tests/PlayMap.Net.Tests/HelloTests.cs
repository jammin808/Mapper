using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Net.Bus;

namespace PlayMap.Net.Tests;

/// <summary>The Show Bus hello through real UDP on loopback: two nodes hear each other and never themselves, a
/// peer ages live → stale → gone → forgotten on the monotonic clock, PlayMap's hello says what the contract asks
/// (and no secret), Patterns' beacon port is never a target, a loopback-only node stays silent, and a stray
/// datagram is dropped. Show Bus 1.3 §2's hearing, held against this tree's copy of the contract (docs/SHOWBUS.md): the hello
/// is the revision's, within its numbers and of its rows for <c>playmap</c>, and docs/SHOWBUS-NODE.md's example is what it builds;
/// the table keeps 128 ids, every address of one with the dialled one sticky, and marks two running nodes saying one id.</summary>
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

        // Every hello b sent has been taken into a's table before the clock moves, so none lands after it as a fresh one.
        Assert.True(await Eventually.TrueAsync(() => a.Heard >= b.Sent), $"a heard {a.Heard} of the {b.Sent} hellos b sent");
        var changes = new List<PeerChange>();
        a.Peers.Changed += changes.Add;
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
    public async Task AHelloIsCountedHeardOnlyOnceThePeerTableHasTakenItIn()
    {
        // Heard says what the table holds: while the table's listener is still being told of the new peer, the hello is not counted.
        var clock = new ManualClock(1_000_000_000);
        using var told = new ManualResetEventSlim();
        await using var a = Make(Node("aaaa0001", "Stage left"), clock);
        await using var b = Make(Node("bbbb0002", "Stage right"), clock);
        var telling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Peers.Changed += change =>
        {
            if (change.Kind != PeerChangeKind.Added)
                return;
            telling.TrySetResult();
            told.Wait(TimeSpan.FromSeconds(20));
        };
        a.Start();
        b.Start();
        b.Targets = [new IPEndPoint(IPAddress.Loopback, a.ListenPort)];
        try
        {
            await telling.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal((1, 0L), (a.Peers.Count, a.Heard));
        }
        finally
        {
            told.Set();
        }
        Assert.True(await Eventually.TrueAsync(() => a.Heard >= 1), $"a counted {a.Heard} hellos heard");
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

    private const string Fingerprint = "sha256:03e9b152ca42518c28128f36def699be8aea5eec11bb9d5afc18a314f8c3eb04";

    /// <summary>PlayMap's hello as Show Bus 1.3 says one: <c>proto</c> the block's, kind <c>playmap</c> a kind row, every cap a cap row
    /// that names <c>playmap</c> and every door a port row, within <c>hello.caps</c> and <c>hello.ports</c>; within <c>hello.bytes</c> as
    /// sent whatever its names, an ordinary name whole; the instance word the sender's, of <c>hello.inst.chars</c> lowercase hex.</summary>
    [Fact]
    public async Task PlayMapsHelloSaysTheRowsShowBus13GivesItsKind()
    {
        var rows = ShowbusRows();
        int Row(string name) => int.Parse(rows.Single(r => r[0] == name)[1], NumberStyles.None, CultureInfo.InvariantCulture);
        var hello = HelloService.PlayMapHello(Node("a1b2c3d4", "Stage left"), 1760101234567, Fingerprint, "RACK-1");

        Assert.Equal(Row("proto"), hello.Proto);
        Assert.Contains(rows, r => r is ["kind", "playmap", ..]);
        var caps = rows.Where(r => r[0] == "cap" && r.Skip(2).Contains("playmap")).Select(r => r[1]).ToHashSet(StringComparer.Ordinal);
        Assert.All(hello.Caps, c => Assert.True(caps.Contains(c), $"'{c}' is no cap row for playmap"));
        Assert.Equal(hello.Caps.Count, hello.Caps.Distinct(StringComparer.Ordinal).Count());
        Assert.InRange(hello.Caps.Count, 1, Row("hello.caps"));
        var ports = rows.Where(r => r[0] == "port").Select(r => r[1]).ToHashSet(StringComparer.Ordinal);
        Assert.All(hello.Ports.Keys, p => Assert.True(ports.Contains(p), $"'{p}' is no port row"));
        Assert.InRange(hello.Ports.Count, 1, Row("hello.ports"));

        string[] worst = [new string('\u0378', 80), new string('\u2028', 80), string.Concat(Enumerable.Repeat("😀", 40)), new string('"', 80)];
        foreach (var name in worst)
        {
            var sent = (HelloService.PlayMapHello(Node(new string('a', 40), name), long.MaxValue, Fingerprint, name,
                new BuildInfo("0.2.0", int.MaxValue, "abc1234")) with { Instance = "0badf00d" }).Fitted();
            Assert.InRange(sent.ToJson().Length, 1, Row("hello.bytes"));
            Assert.InRange(sent.Name.Length, 0, Row("hello.name.chars"));
        }
        var whole = HelloService.PlayMapHello(Node("a1b2c3d4", new string('n', 64)), 1, Fingerprint, new string('h', 64)) with { Instance = "0badf00d" };
        Assert.Same(whole, whole.Fitted());
        Assert.Equal((new string('n', 64), new string('h', 64)), (ShowBusHello.Parse(whole.ToJson())!.Name, ShowBusHello.Parse(whole.ToJson())!.Host));

        await using var service = new HelloService(Node("a1b2c3d4", "Stage left"), () => hello);
        Assert.True(ShowBusHello.InstShape(service.Instance) && service.Instance.Length == Row("hello.inst.chars"), service.Instance);
    }

    /// <summary>docs/SHOWBUS-NODE.md §1's hello is what <see cref="HelloService.PlayMapHello"/> builds for the inputs it names.</summary>
    [Fact]
    public void TheNodePagesHelloIsWhatTheBuilderSays()
    {
        var page = File.ReadAllText(Path.Combine(Root(), "docs", "SHOWBUS-NODE.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var at = page.IndexOf("```json\n", StringComparison.Ordinal);
        Assert.True(at >= 0, "SHOWBUS-NODE.md shows no hello");
        var json = page[(at + 8)..page.IndexOf("\n```", at + 8, StringComparison.Ordinal)];
        var built = HelloService.PlayMapHello(Node("a1b2c3d4", "Stage left"), 1760101234567, Fingerprint, "RENDER-01",
            new BuildInfo("0.2.0", 17, "abc1234")) with { Instance = "0badc0de" };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(built.Fitted().ToJson())),
            "SHOWBUS-NODE.md §1 shows " + json + " — the builder says " + built);
    }

    /// <summary>§2: one id, one peer. A hello heard from a second address is no change of the peer; the address dialled stays while
    /// it is heard, and moves (<see cref="PeerChangeKind.Moved"/>, never Changed) only once it goes stale.</summary>
    [Fact]
    public void AnAddressFlipIsNoChangeAndTheDialledAddressStaysWhileItIsHeard()
    {
        var clock = new ManualClock(1_000_000_000);
        var table = new PeerTable(clock);
        var changes = new List<PeerChange>();
        table.Changed += changes.Add;
        var h = Peer("cccc0003", "a41f09c2");
        foreach (var address in new[] { "10.0.0.5", "127.0.0.1", "10.0.0.5", "127.0.0.1" })
        {
            Assert.True(table.Hear(h, address));
            clock.AdvanceMs(1000);
        }
        Assert.Equal([PeerChangeKind.Added], changes.Select(c => c.Kind));
        var peer = table.Find("cccc0003")!;
        Assert.Equal("10.0.0.5", peer.Address);
        Assert.Equal(["127.0.0.1", "10.0.0.5"], peer.Addresses);
        Assert.Equal("cccc0003", Assert.Single(table.Live("playmap")).Id);

        // Only the copy here is heard: the address dialled goes stale after 6 s, and the next hello moves the dial.
        for (var i = 0; i < 3; i++)
        {
            clock.AdvanceMs(2000);
            table.Hear(h, "127.0.0.1");
        }
        Assert.Equal([PeerChangeKind.Added, PeerChangeKind.Moved], changes.Select(c => c.Kind));
        Assert.Equal("127.0.0.1", changes[^1].Peer.Address);
        Assert.Equal(["127.0.0.1"], table.Find("cccc0003")!.Addresses);
    }

    /// <summary>At most four addresses per id: a new one past that takes the place of the one heard longest ago, never of the one
    /// dialled; and the peer's hello is the one last heard at the address dialled, so its certificate is that address's.</summary>
    [Fact]
    public void AFifthAddressTakesThePlaceOfTheOldestNeverTheDialledOne()
    {
        var clock = new ManualClock(1_000_000_000);
        var table = new PeerTable(clock);
        table.Hear(Peer("cccc0003", "a41f09c2", tls: "sha256:" + new string('1', 64)), "10.0.0.1");
        foreach (var address in new[] { "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5" })
        {
            clock.AdvanceMs(500);
            table.Hear(Peer("cccc0003", "a41f09c2", tls: "sha256:" + new string('2', 64)), address);
        }
        var peer = table.Find("cccc0003")!;
        Assert.Equal(ShowBusConstants.PeerAddresses, peer.Addresses.Count);
        Assert.Equal(["10.0.0.5", "10.0.0.4", "10.0.0.3", "10.0.0.1"], peer.Addresses);
        Assert.Equal("10.0.0.1", peer.Address);
        Assert.Equal("sha256:" + new string('1', 64), peer.Hello.TlsFingerprint);
    }

    /// <summary>§2: one id from two running nodes — an inst heard within 6 s comes back after another — is marked in conflict, amber in
    /// words naming both machines; no new link may be dialled to it (it leaves <see cref="PeerTable.Live"/>) while the mark stands, the
    /// alternation going on raises nothing more, and 60 s without one clears it.</summary>
    [Fact]
    public void TwoRunningNodesSayingOneIdAreMarkedInConflictUntilTheMarkClears()
    {
        var clock = new ManualClock(1_000_000_000);
        var table = new PeerTable(clock);
        var changes = new List<PeerChange>();
        table.Changed += changes.Add;
        table.Hear(Peer("cccc0003", "a41f09c2", "FOH-PC"), "10.0.0.5");
        clock.AdvanceMs(1000);
        table.Hear(Peer("cccc0003", "b7c3d9e1", "FOH-PC2"), "10.0.0.7");
        Assert.Null(table.Find("cccc0003")!.Conflict);
        clock.AdvanceMs(1000);
        table.Hear(Peer("cccc0003", "a41f09c2", "FOH-PC"), "10.0.0.5");

        var marked = Assert.Single(changes, c => c.Kind == PeerChangeKind.Changed);
        Assert.Equal("two machines say they are cccc0003: FOH-PC2 at 10.0.0.7 and FOH-PC at 10.0.0.5", marked.Peer.Conflict!.Words);
        Assert.Equal("10.0.0.5", marked.Peer.Address);
        Assert.Empty(table.Live("playmap"));
        for (var i = 0; i < 4; i++)
        {
            clock.AdvanceMs(1000);
            table.Hear(Peer("cccc0003", i % 2 == 0 ? "b7c3d9e1" : "a41f09c2", i % 2 == 0 ? "FOH-PC2" : "FOH-PC"), i % 2 == 0 ? "10.0.0.7" : "10.0.0.5");
        }
        Assert.Single(changes, c => c.Kind == PeerChangeKind.Changed);

        // Only the first machine from here: 60 s after the last alternation the mark clears and a link may be dialled again.
        for (var i = 0; i < 29; i++)
        {
            clock.AdvanceMs(2000);
            table.Hear(Peer("cccc0003", "a41f09c2", "FOH-PC"), "10.0.0.5");
            table.Sweep();
        }
        Assert.NotNull(table.Find("cccc0003")!.Conflict);
        clock.AdvanceMs(2001);
        table.Sweep();
        Assert.Null(changes[^1].Peer.Conflict);
        Assert.Equal(PeerChangeKind.Changed, changes[^1].Kind);
        Assert.Equal("cccc0003", Assert.Single(table.Live("playmap")).Id);
    }

    /// <summary>A restart (A, then only B), and a run of restarts (A, B, C), is never a conflict; three copies taking turns (A, B, C,
    /// A) are one; and from nodes that send no inst, two hosts within 6 s are, one host from two addresses is not.</summary>
    [Fact]
    public void ARestartIsNoConflictAndTwoHostsWithNoInstAreOne()
    {
        var clock = new ManualClock(1_000_000_000);
        var table = new PeerTable(clock);
        foreach (var inst in new[] { "a41f09c2", "b7c3d9e1", "b7c3d9e1", "c0ffee11", "c0ffee11" })
        {
            table.Hear(Peer("cccc0003", inst), "10.0.0.5");
            clock.AdvanceMs(1000);
        }
        Assert.Null(table.Find("cccc0003")!.Conflict);
        table.Hear(Peer("cccc0003", "a41f09c2"), "10.0.0.5");
        Assert.NotNull(table.Find("cccc0003")!.Conflict);

        table.Hear(Peer("dddd0004", null, "FOH-PC"), "10.0.0.5");
        table.Hear(Peer("dddd0004", null, "FOH-PC"), "127.0.0.1");
        Assert.Null(table.Find("dddd0004")!.Conflict);
        clock.AdvanceMs(1000);
        table.Hear(Peer("dddd0004", null, "FOH-PC2"), "10.0.0.7");
        Assert.Equal("two machines say they are dddd0004: FOH-PC at 127.0.0.1 and FOH-PC2 at 10.0.0.7", table.Find("dddd0004")!.Conflict!.Words);
    }

    /// <summary>Another process saying this node's own id (settings copied to a second machine) is marked at once, naming this node
    /// and the other machine; it is never a peer a link may dial.</summary>
    [Fact]
    public void AnotherProcessSayingThisNodesIdIsMarkedAtOnce()
    {
        var clock = new ManualClock(1_000_000_000);
        var table = new PeerTable(clock, "a1b2c3d4");
        var changes = new List<PeerChange>();
        table.Changed += changes.Add;
        table.Hear(Peer("a1b2c3d4", "b7c3d9e1", "RACK-2"), "10.0.0.7");
        var added = Assert.Single(changes);
        Assert.Equal(PeerChangeKind.Added, added.Kind);
        Assert.Equal("two machines say they are a1b2c3d4: this node and RACK-2 at 10.0.0.7", added.Peer.Conflict!.Words);
        Assert.Empty(table.Live("playmap"));
    }

    /// <summary>§2: one inst from two addresses is one node, so a mark is the two machines, never their addresses. Two machines taking
    /// turns, one heard at two addresses, are one mark: it keeps the words it first said and raises one Changed however they take turns,
    /// and a third copy among them changes nothing a reader is told while the two it names are heard. Another process saying this node's
    /// id from two addresses is one Added.</summary>
    [Fact]
    public void AMarkIsTheTwoMachinesNeverTheirAddresses()
    {
        var clock = new ManualClock(1_000_000_000);
        var table = new PeerTable(clock, "a1b2c3d4");
        var changes = new List<PeerChange>();
        table.Changed += changes.Add;
        for (var round = 0; round < 5; round++)
        {
            foreach (var (inst, host, address) in new[] { ("a41f09c2", "FOH-PC", "127.0.0.1"), ("a41f09c2", "FOH-PC", "10.0.0.5"), ("b7c3d9e1", "FOH-PC2", "10.0.0.7") })
            {
                table.Hear(Peer("cccc0003", inst, host), address);
                clock.AdvanceMs(500);
            }
        }
        Assert.Equal([PeerChangeKind.Added, PeerChangeKind.Changed], changes.Select(c => c.Kind));
        const string Words = "two machines say they are cccc0003: FOH-PC2 at 10.0.0.7 and FOH-PC at 127.0.0.1";
        Assert.Equal(Words, changes[1].Peer.Conflict!.Words);
        Assert.Equal(Words, table.Find("cccc0003")!.Conflict!.Words);

        changes.Clear();
        foreach (var (inst, host, address) in new[]
        {
            ("c0ffee11", "FOH-PC3", "10.0.0.9"), ("a41f09c2", "FOH-PC", "127.0.0.1"), ("b7c3d9e1", "FOH-PC2", "10.0.0.7"),
            ("c0ffee11", "FOH-PC3", "10.0.0.9"), ("a41f09c2", "FOH-PC", "10.0.0.5"),
        })
        {
            table.Hear(Peer("cccc0003", inst, host), address);
            clock.AdvanceMs(500);
        }
        Assert.Empty(changes);
        Assert.Equal(Words, table.Find("cccc0003")!.Conflict!.Words);

        foreach (var address in new[] { "127.0.0.1", "10.0.0.7", "127.0.0.1", "10.0.0.7" })
        {
            table.Hear(Peer("a1b2c3d4", "b7c3d9e1", "RACK-2"), address);
            clock.AdvanceMs(500);
        }
        var own = Assert.Single(changes);
        Assert.Equal(PeerChangeKind.Added, own.Kind);
        Assert.Equal("two machines say they are a1b2c3d4: this node and RACK-2 at 127.0.0.1", table.Find("a1b2c3d4")!.Conflict!.Words);
    }

    /// <summary>§2: a reader keeps at most 128 ids; a hello from a new id past that is dropped (and the sender counts it dropped), until
    /// one is forgotten.</summary>
    [Fact]
    public void TheTableKeeps128IdsAndDropsANewOnePastThat()
    {
        var clock = new ManualClock(1_000_000_000);
        var table = new PeerTable(clock);
        var added = 0;
        table.Changed += c => added += c.Kind == PeerChangeKind.Added ? 1 : 0;
        for (var i = 0; i < ShowBusConstants.PeersMax; i++)
            Assert.True(table.Hear(Peer("node" + i.ToString("x4", CultureInfo.InvariantCulture), null), "10.0.1." + (i % 250).ToString(CultureInfo.InvariantCulture)));
        Assert.False(table.Hear(Peer("late0001", null), "10.0.2.1"));
        Assert.Equal((ShowBusConstants.PeersMax, ShowBusConstants.PeersMax), (table.Count, added));
        Assert.Null(table.Find("late0001"));

        clock.AdvanceMs(ShowBusConstants.ForgetMs);
        Assert.True(table.Hear(Peer("late0001", null), "10.0.2.1"));
        Assert.Equal(1, table.Count);
        Assert.Equal(ShowBusConstants.PeersMax + 1, added);
    }

    /// <summary>A datagram the JSON reader takes but no hello reader can read out — a <c>v</c> that is no whole number, a string or a
    /// name escaping a lone surrogate, raw bytes that are not UTF-8 — is no hello: <see cref="ShowBusHello.Parse"/> says null and never
    /// throws, and the listener counts each one dropped and hears the next hello (before, the first of them ended the listen loop).</summary>
    [Fact]
    public async Task ADatagramTheReaderCannotReadIsDroppedAndTheListenerHearsOn()
    {
        var bad = new List<byte[]>
        {
            Encoding.UTF8.GetBytes("{\"t\":\"playmap\",\"v\":1.5,\"id\":\"cccc0003\"}"),
            Encoding.UTF8.GetBytes("{\"t\":\"playmap\",\"v\":1e40,\"id\":\"cccc0003\"}"),
            Encoding.UTF8.GetBytes("{\"t\":\"playmap\",\"v\":1,\"id\":\"cccc0003\",\"name\":\"\\uD800\"}"),
            Encoding.UTF8.GetBytes("{\"t\":\"\\uD800\",\"v\":1,\"id\":\"cccc0003\"}"),
            Encoding.UTF8.GetBytes("{\"t\":\"playmap\",\"v\":1,\"id\":\"cccc0003\",\"inst\":\"\\uDC00\"}"),
            Encoding.UTF8.GetBytes("{\"t\":\"playmap\",\"v\":1,\"id\":\"cccc0003\",\"host\":\"a\\uD800b\"}"),
            Encoding.UTF8.GetBytes("{\"t\":\"playmap\",\"v\":1,\"id\":\"cccc0003\",\"caps\":[\"\\uD800\"]}"),
            Encoding.UTF8.GetBytes("{\"t\":\"playmap\",\"v\":1,\"id\":\"cccc0003\",\"ports\":{\"\\uD800\":9941}}"),
            Encoding.UTF8.GetBytes("{\"t\":\"playmap\",\"v\":1,\"id\":\"cccc0003\",\"build\":\"\\uD800\"}"),
            Raw("{\"t\":\"playmap\",\"v\":1,\"id\":\"cccc0003\",\"name\":\"a", [0xFF], "\"}"),
            Raw("{\"t\":\"playmap\",\"v\":1,\"id\":\"cccc0003\",\"name\":\"", [0xED, 0xA0, 0x80], "\"}"),
        };
        await using var hello = Make(Node("a1b2c3d4", "Stage left"), new ManualClock(1_000_000_000));
        hello.Start();
        using var udp = new UdpClient();
        var to = new IPEndPoint(IPAddress.Loopback, hello.ListenPort);
        for (var i = 0; i < bad.Count; i++)
        {
            await udp.SendAsync(bad[i], to);
            var dropped = i + 1;
            Assert.True(await Eventually.TrueAsync(() => hello.Dropped == dropped), $"datagram {i} was not counted dropped");
        }
        await udp.SendAsync(Peer("cccc0003", "a41f09c2").ToJson(), to);
        Assert.True(await Eventually.TrueAsync(() => hello.Peers.Find("cccc0003") is not null), "the listener stopped hearing");
        Assert.Equal(bad.Count, hello.Dropped);
        Assert.Equal(1, hello.Heard);
        foreach (var datagram in bad)
            Assert.Null(ShowBusHello.Parse(datagram));
    }

    /// <summary>A datagram of UTF-8 text around bytes that are not UTF-8.</summary>
    private static byte[] Raw(string before, byte[] bytes, string after) => [.. Encoding.UTF8.GetBytes(before), .. bytes, .. Encoding.UTF8.GetBytes(after)];

    private static ShowBusHello Peer(string id, string? inst, string host = "FOH-PC", string? tls = null) =>
        new("playmap", 1, ShowBusConstants.Proto, id, "FOH", host, new Dictionary<string, int> { ["wire"] = 9941 }, ["state"], 1, inst, tls);

    /// <summary>The ```showbus block of this tree's copy of the contract, each row its words.</summary>
    private static List<string[]> ShowbusRows()
    {
        var lines = File.ReadAllText(Path.Combine(Root(), "docs", "SHOWBUS.md")).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var open = Array.IndexOf(lines, "```showbus");
        Assert.True(open >= 0, "docs/SHOWBUS.md has no ```showbus block");
        return lines.Skip(open + 1).TakeWhile(l => l != "```").Select(l => l.Split(' ')).ToList();
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no PlayMap.sln above " + AppContext.BaseDirectory);
    }
}
