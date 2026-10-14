using System.Net;
using System.Net.Sockets;
using PlayMap.Net.Discovery;

namespace PlayMap.Net.Tests.Companion;

/// <summary>The node's mDNS responder (RFC 6762, RFC 6763): the codec reads Bonjour's and Avahi's packets (name compression, the
/// unicast-response and cache-flush bits) and writes what it read; a legacy unicast query on this slot's loopback address is answered
/// to the sender with its id, PTR, SRV, TXT and A, TTLs of at most 10 s; a name another host answers for while probing becomes
/// "&lt;name&gt; (2)"; a bind the machine refuses is a fault in words. One class, so its sockets never share the port at once.</summary>
public sealed class MdnsTests
{
    /// <summary>A Bonjour response announcing a PlayMap node, as it arrives: compressed names, cache-flush on SRV, TXT and A.</summary>
    private const string BonjourResponse =
        "000084000000000100000003085f706c61796d6170045f746370056c6f63616c00000c000100001194000d0a5374616765206c656674c00cc02b002180010000007800110000000026d40873746167652d7063c01ac02b001080010000119400350b69643d61316232633364340f6e616d653d5374616765206c6566740770726f746f3d310677733d2f777309776972653d39393431c04a00018001000000780004c0a80114";

    /// <summary>An Avahi browse: a QU question for the service type and one for the service list, the second compressed.</summary>
    private const string AvahiQuery =
        "000000000002000000000000085f706c61796d6170045f746370056c6f63616c00000c8001095f7365727669636573075f646e732d7364045f756470c01a000c0001";

    private static IPAddress Loopback() =>
        IPAddress.Parse(Environment.GetEnvironmentVariable("PLAYMAP_TEST_LOOPBACK") is { Length: > 0 } a ? a : "127.0.0.1");

    [Fact]
    public void TheCodecReadsBonjourAndAvahiAndWritesWhatItRead()
    {
        var response = DnsMessage.Read(Convert.FromHexString(BonjourResponse));
        Assert.True(response.IsResponse);
        Assert.True(response.Authoritative);
        var ptr = Assert.Single(response.Answers);
        Assert.Equal(("_playmap._tcp.local", DnsType.Ptr, 4500u, "Stage left._playmap._tcp.local"), (ptr.Name, ptr.Type, ptr.Ttl, ptr.Target));
        var srv = response.Additionals.Single(r => r.Type == DnsType.Srv);
        Assert.Equal(("Stage left._playmap._tcp.local", (ushort)9940, "stage-pc.local", true), (srv.Name, srv.Port, srv.Target, srv.CacheFlush));
        var txt = response.Additionals.Single(r => r.Type == DnsType.Txt);
        Assert.Equal(["id=a1b2c3d4", "name=Stage left", "proto=1", "ws=/ws", "wire=9941"], txt.Text);
        var a = response.Additionals.Single(r => r.Type == DnsType.A);
        Assert.Equal(("stage-pc.local", IPAddress.Parse("192.168.1.20")), (a.Name, a.Address));

        // Written back without compression (RFC 6762 §18.14 allows it), it reads the same.
        var again = DnsMessage.Read(response.Write());
        Assert.Equal(response.Answers.Concat(response.Additionals).Select(Fingerprint), again.Answers.Concat(again.Additionals).Select(Fingerprint));

        var query = DnsMessage.Read(Convert.FromHexString(AvahiQuery));
        Assert.False(query.IsResponse);
        Assert.Equal([("_playmap._tcp.local", DnsType.Ptr, true), ("_services._dns-sd._udp.local", DnsType.Ptr, false)],
            query.Questions.Select(q => (q.Name, q.Type, q.UnicastResponse)));
        Assert.Equal(query.Questions, DnsMessage.Read(query.Write()).Questions);

        // A pointer loop is refused, never followed for ever.
        var loop = Convert.FromHexString("000000000001000000000000c00c000c0001");
        Assert.Throws<InvalidDataException>(() => DnsMessage.Read(loop));
        Assert.Throws<InvalidDataException>(() => DnsMessage.Read([0, 1, 2]));
    }

    private static string Fingerprint(DnsRecord r) =>
        $"{r.Name}|{r.Type}|{r.Ttl}|{r.CacheFlush}|{r.Target}|{r.Port}|{string.Join(";", r.Text)}|{r.Address}";

    private static ServiceRecord Node(int port = 9940) => new("Stage left", "_playmap._tcp", port,
        new Dictionary<string, string> { ["id"] = "a1b2c3d4", ["name"] = "Stage left", ["proto"] = "1", ["ws"] = "/ws", ["wire"] = "9941" });

    /// <summary>The test's own socket on the slot's address: it stands for the multicast group (probes and announcements come to it)
    /// and for a querier.</summary>
    private sealed class Peer : IDisposable
    {
        public Peer(IPAddress address)
        {
            Socket.Bind(new IPEndPoint(address, 0));
            EndPoint = (IPEndPoint)Socket.LocalEndPoint!;
        }

        public Socket Socket { get; } = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        public IPEndPoint EndPoint { get; }

        public async Task<(DnsMessage Message, IPEndPoint From)> ReceiveAsync(Func<DnsMessage, bool> match, int timeoutMs = 5000)
        {
            using var deadline = new CancellationTokenSource(timeoutMs);
            var buffer = new byte[9000];
            while (true)
            {
                var r = await Socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), deadline.Token);
                var m = DnsMessage.Read(buffer.AsSpan(0, r.ReceivedBytes));
                if (match(m))
                    return (m, (IPEndPoint)r.RemoteEndPoint);
            }
        }

        public void Dispose() => Socket.Dispose();
    }

    private static MdnsOptions Options(IPAddress address, Peer group) => new()
    {
        Bind = address,
        Group = group.EndPoint,
        Host = "stage-pc",
        ProbeInterval = TimeSpan.FromMilliseconds(20),
        AnnounceInterval = TimeSpan.FromMilliseconds(20),
    };

    [Fact]
    public async Task ALegacyUnicastQueryIsAnsweredWithPtrSrvTxtAndA()
    {
        var address = Loopback();
        using var peer = new Peer(address);
        await using var responder = new MdnsResponder(Options(address, peer));
        Assert.True(responder.Start(), responder.Fault);
        Assert.Equal(5353, responder.LocalPort);
        responder.Announce(Node());
        // The second announcement says the name is ours: probing is over.
        await peer.ReceiveAsync(m => m.IsResponse && m.Answers.Any(r => r.Type == DnsType.Ptr));
        await peer.ReceiveAsync(m => m.IsResponse && m.Answers.Any(r => r.Type == DnsType.Ptr));

        var query = DnsMessage.Query(0x1234, new DnsQuestion("_playmap._tcp.local", DnsType.Ptr));
        await peer.Socket.SendToAsync(query.Write(), SocketFlags.None, new IPEndPoint(address, 5353));
        var (answer, from) = await peer.ReceiveAsync(m => m.Id == 0x1234);
        Assert.Equal(5353, from.Port);
        Assert.True(answer.IsResponse);
        Assert.Equal("_playmap._tcp.local", Assert.Single(answer.Questions).Name);
        var records = answer.Answers.Concat(answer.Additionals).ToList();
        Assert.Equal("Stage left._playmap._tcp.local", records.Single(r => r.Type == DnsType.Ptr).Target);
        var srv = records.Single(r => r.Type == DnsType.Srv);
        Assert.Equal(((ushort)9940, "stage-pc.local"), (srv.Port, srv.Target));
        Assert.Contains("ws=/ws", records.Single(r => r.Type == DnsType.Txt).Text);
        Assert.Equal(address, records.Single(r => r.Type == DnsType.A).Address);
        Assert.All(records, r => Assert.InRange(r.Ttl, 1u, 10u));
        Assert.All(records, r => Assert.False(r.CacheFlush));

        // The service list and the instance's own name are answered too.
        await peer.Socket.SendToAsync(DnsMessage.Query(7, new DnsQuestion(MdnsResponder.ServicesName, DnsType.Ptr)).Write(), SocketFlags.None, new IPEndPoint(address, 5353));
        Assert.Equal("_playmap._tcp.local", Assert.Single((await peer.ReceiveAsync(m => m.Id == 7)).Message.Answers).Target);
        await peer.Socket.SendToAsync(DnsMessage.Query(8, new DnsQuestion("stage left._playmap._tcp.local", DnsType.Any)).Write(), SocketFlags.None, new IPEndPoint(address, 5353));
        var own = (await peer.ReceiveAsync(m => m.Id == 8)).Message;
        Assert.Equal([DnsType.Txt, DnsType.Srv], own.Answers.Select(r => r.Type).Order());
    }

    [Fact]
    public async Task ANameAnotherHostAnswersForWhileProbingBecomesName2()
    {
        var address = Loopback();
        using var peer = new Peer(address);
        await using var responder = new MdnsResponder(Options(address, peer) with { ProbeInterval = TimeSpan.FromMilliseconds(200) });
        var renamed = new TaskCompletionSource<(string Old, string New)>(TaskCreationOptions.RunContinuationsAsynchronously);
        responder.Renamed += (o, n) => renamed.TrySetResult((o, n));
        Assert.True(responder.Start(), responder.Fault);
        responder.Announce(Node());

        var (probe, from) = await peer.ReceiveAsync(m => !m.IsResponse && m.Questions.Any(q => q.Name == "Stage left._playmap._tcp.local"));
        Assert.True(probe.Questions[0].UnicastResponse);
        Assert.Contains(probe.Authorities, r => r.Type == DnsType.Srv);
        // Another machine already holds the name.
        var theirs = new DnsMessage(0, true, true, [], [DnsRecord.Srv("Stage left._playmap._tcp.local", "other-pc.local", 9940, 120, flush: true)], [], []);
        await peer.Socket.SendToAsync(theirs.Write(), SocketFlags.None, from);

        var (old, now) = await renamed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("Stage left", "Stage left (2)"), (old, now));
        Assert.Equal("Stage left (2)", Assert.Single(responder.Records).Instance);
        // The new name is probed and then announced.
        var announced = await peer.ReceiveAsync(m => m.IsResponse && m.Answers.Any(r => r.Type == DnsType.Ptr));
        Assert.Equal("Stage left (2)._playmap._tcp.local", announced.Message.Answers.Single(r => r.Type == DnsType.Ptr).Target);
    }

    [Fact]
    public async Task ABindTheMachineRefusesIsAFaultInWords()
    {
        // 192.0.2.1 (TEST-NET-1) is no address of this machine: the bind is refused on every platform.
        await using var responder = new MdnsResponder(new MdnsOptions { Bind = IPAddress.Parse("192.0.2.1") });
        Assert.False(responder.Start());
        Assert.Contains("could not open 192.0.2.1:5353", responder.Fault, StringComparison.Ordinal);
        Assert.Empty(responder.Records);
    }

    [Fact]
    public void NamesFitOneLabel()
    {
        Assert.Equal("Stage-left-v2", MdnsResponder.InstanceLabel("Stage.left.v2"));
        Assert.Equal("FOH-PC", MdnsResponder.HostLabel("FOH PC"));
        Assert.Equal("playmap", MdnsResponder.HostLabel("…"));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(MdnsResponder.InstanceLabel(new string('é', 80))) <= 58);
    }
}
