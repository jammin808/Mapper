using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Time;
using PlayMap.Engine.Fleet;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Host;
using PlayMap.Net.Bus;
using PlayMap.Net.Fleet;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>Two nodes on loopback, each with the fleet's part on its own hand-moved clock: B appears in A's FLEET when B's first HEALTH
/// arrives (the subscription is pushed, over TLS pinned by B's hello; A's clock moves only while A's monitor waits to dial again); B
/// stopped is stale at 3 s and gone, red with words, at 6 s of A's clock, and stays on the board. The web port's /health and /fleet are
/// open reads that change nothing.</summary>
public sealed class FleetMonitorTests
{
    private static readonly string[] Reads = ["/health", "/fleet"];

    /// <summary>How often the monitor ages liveness (<see cref="FleetMonitorOptions.SweepEvery"/>, which the fleet's part leaves as it is).</summary>
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMilliseconds(250);

    internal static EngineParts Parts(ManualTime time) => new()
    {
        Extensions = [setup => new FleetExtension(setup, new FleetOptions { Time = time, Reader = new UnknownMachineReader("not read in tests") })],
    };

    private static JsonElement Fleet(HostRig rig)
    {
        using var doc = JsonDocument.Parse(rig.Engine.FleetBoardJson());
        return doc.RootElement.Clone();
    }

    private static JsonElement? Machine(HostRig rig, string id) =>
        Fleet(rig).GetProperty("machines").EnumerateArray().Where(m => m.GetProperty("id").GetString() == id).Select(m => (JsonElement?)m).FirstOrDefault();

    [Fact]
    public async Task APeerAppearsWhenItsHealthArrivesAndGoesStaleThenGoneOnTheMonitorsClock()
    {
        var timeA = new ManualTime();
        var timeB = new ManualTime();
        await using var a = await HostRig.StartAsync("FOH-A", network: true, parts: Parts(timeA));
        var b = await HostRig.StartAsync("FOH-B", network: true, parts: Parts(timeB));
        try
        {
            Assert.Null(Machine(a, b.Id));
            HostRig.Introduce(a, b);

            // Event-driven: B's hello, the subscription, B's first record pushed at once — no clock moved but by the steps the wait gives
            // A's monitor while it waits on A's clock to dial again (none when its first subscription to B was heard; a first one that
            // ended unheard, refused or cut, is dialled again only once its backoff has run out on that clock). Each step is the
            // monitor's sweep (250 ms), so B's record is heard at an instant a sweep ages from, as on the unmoved clock, and the
            // advances below cross 3 s and 6 s of its silence at a sweep.
            var (live, redial) = await FleetWait.UntilAsync(() => Machine(a, b.Id) is { } m && m.GetProperty("heard").GetString() == "live",
                a.Engine.Services.FleetPeers as FleetMonitor, [timeA], SweepEvery);
            Assert.True(live, $"A does not hear B live: {FleetWait.Knows(a, b)}\n{a.Journal()}");
            Assert.Equal(redial.Ticks, timeA.GetTimestamp());
            var peer = a.Engine.Services.FleetPeers!.Peers().Single(p => p.Id == b.Id);
            Assert.Equal("pinned", peer.Pin);   // B's hello offers TLS: the certificate is checked and pinned on its first record
            Assert.Equal("FOH-B", peer.Health!.Facts.Name);

            // STATE carries it, and the context's peers carry B's own light and headline.
            var state = a.State();
            var nodes = state.GetProperty("fleet").GetProperty("nodes").EnumerateArray().ToList();
            var node = Assert.Single(nodes);   // the machines heard: this node is STATE itself
            Assert.Equal(b.Id, node.GetProperty("id").GetString());
            Assert.Equal("live", node.GetProperty("heard").GetString());
            // Two machines, each with its one output (not measured on a node without a renderer: grey).
            Assert.Equal(4, state.GetProperty("eye").GetProperty("counts").GetProperty("things").GetInt32());
            // The context carries the headline of the record the monitor holds as it is built (B publishes a newer one whenever its
            // own lights move, so the one read above may be gone by then).
            string Held() => a.Engine.Services.FleetPeers!.Peers().Single(p => p.Id == b.Id).Health!.Headline;
            Assert.True(await Eventually.TrueAsync(() =>
            {
                var before = Held();
                var carried = ContextBuilder.Build(a.Engine).Peers.Single(p => p.Id == b.Id).Headline;
                return carried == before && carried == Held();
            }), $"the context's headline is not the monitor's: {Held()}");

            await b.StopAsync();
            timeA.Advance(TimeSpan.FromMilliseconds(2900));
            Assert.Equal("live", Machine(a, b.Id)!.Value.GetProperty("heard").GetString());
            timeA.Advance(TimeSpan.FromMilliseconds(200));
            Assert.Equal("stale", Machine(a, b.Id)!.Value.GetProperty("heard").GetString());
            Assert.Equal("amber", Machine(a, b.Id)!.Value.GetProperty("light").GetString());
            timeA.Advance(TimeSpan.FromSeconds(3));
            var gone = Machine(a, b.Id)!.Value;
            Assert.Equal("gone", gone.GetProperty("heard").GetString());
            Assert.Equal("red", gone.GetProperty("light").GetString());
            Assert.StartsWith("gone quiet", gone.GetProperty("headline").GetString(), StringComparison.Ordinal);

            // Never removed silently: still on the board and in STATE, red, a minute later.
            timeA.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal("red", Machine(a, b.Id)!.Value.GetProperty("light").GetString());
            Assert.Contains(a.State().GetProperty("fleet").GetProperty("nodes").EnumerateArray(),
                n => n.GetProperty("id").GetString() == b.Id && n.GetProperty("light").GetString() == "red");
            Assert.Contains("machine:" + b.Id, a.State().GetProperty("eye").GetProperty("problems").EnumerateArray().Select(p => p.GetString()));
        }
        finally
        {
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task HealthAndFleetAreOpenReadsThatChangeNothing()
    {
        var time = new ManualTime();
        await using var rig = await HostRig.StartAsync("FOH-A", parts: Parts(time), settings: s => s with { TrustLoopback = false });
        using var http = rig.Http();
        foreach (var path in Reads)
        {
            using var get = await http.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("no-store", get.Headers.CacheControl?.ToString());
            using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            if (path == "/health")
                Assert.Equal(rig.Id, doc.RootElement.GetProperty("node").GetString());
            else
                Assert.Equal(rig.Id, doc.RootElement.GetProperty("machines")[0].GetProperty("id").GetString());
            Assert.DoesNotContain(HostRig.Token, doc.RootElement.GetRawText(), StringComparison.Ordinal);

            using var content = new StringContent("BLACKOUT ON", Encoding.UTF8, "text/plain");
            using var post = await http.PostAsync(new Uri(path, UriKind.Relative), content);
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
            Assert.Contains("read-only", await post.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        // The node's own routes still ask the token of an address it does not trust.
        using var state = await http.GetAsync(new Uri("/api/state", UriKind.Relative));
        Assert.NotEqual(HttpStatusCode.OK, state.StatusCode);
        Assert.False(rig.State().GetProperty("air").GetProperty("blackout").GetBoolean());
    }

    [Fact]
    public async Task TheHealthSocketFeedsAtMostItsNumberAndRefusesOneMore()
    {
        var time = new ManualTime();
        var parts = new EngineParts
        {
            Extensions = [setup => new FleetExtension(setup, new FleetOptions { Time = time, Reader = new UnknownMachineReader("not read in tests"), HealthFeeds = 1 })],
        };
        await using var rig = await HostRig.StartAsync("FOH-A", parts: parts);
        var uri = new Uri(string.Create(CultureInfo.InvariantCulture, $"ws://127.0.0.1:{rig.Network.HttpPort}/health"));
        using var first = new ClientWebSocket();
        await first.ConnectAsync(uri, CancellationToken.None);
        var buffer = new byte[64 * 1024];
        var received = await first.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(rig.Id, HealthJson.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count))!.Facts.Node);

        // A page from another site cannot hold a feed (a monitor sends no Origin; a browser always does).
        using var foreign = new ClientWebSocket();
        foreign.Options.CollectHttpResponseDetails = true;
        foreign.Options.SetRequestHeader("Origin", "http://stranger.example");
        await Assert.ThrowsAsync<WebSocketException>(() => foreign.ConnectAsync(uri, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.HttpStatusCode);

        // One more socket is refused (503); a GET still reads the record.
        using var second = new ClientWebSocket();
        second.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => second.ConnectAsync(uri, CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.HttpStatusCode);
        using var http = rig.Http();
        using (var get = await http.GetAsync(new Uri("/health", UriKind.Relative)))
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        // A socket that closes frees its place.
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        Assert.True(await Eventually.TrueAsync(async () =>
        {
            using var again = new ClientWebSocket();
            try
            {
                await again.ConnectAsync(uri, CancellationToken.None);
                return true;
            }
            catch (WebSocketException)
            {
                return false;
            }
        }));
    }

    /// <summary>A transport whose subscriptions are held open, no record ever sent, until the test ends one.</summary>
    private sealed class HeldFeeds : IHealthFeeds
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _held = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, int> Dialled { get; } = new(StringComparer.Ordinal);

        public async Task<FeedEnd> RunAsync(FleetDial dial, Action<HealthRecord> onRecord, Action<string> onPin, CancellationToken ct)
        {
            Dialled.AddOrUpdate(dial.NodeId, 1, (_, n) => n + 1);
            await Held(dial.NodeId).Task.WaitAsync(ct);
            _held.TryRemove(dial.NodeId, out _);
            return new FeedEnd(dial.Name + " ended", false, false);
        }

        public void End(string id) => Held(id).TrySetResult();

        private TaskCompletionSource Held(string id) =>
            _held.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private static ShowBusHello Hello(string id, string host = "show-pc") =>
        new(ShowBusConstants.KindPlayMap, 1, 1, id, "N-" + id, host, new Dictionary<string, int> { ["http"] = 9940 }, ShowBusHello.PlayMapCaps, 0,
            null, null);

    private static (PeerTable Table, ManualClock Mono, HeldFeeds Feeds, FleetMonitor Monitor) Rig(int max)
    {
        var mono = new ManualClock();
        var table = new PeerTable(mono);
        var feeds = new HeldFeeds();
        var monitor = new FleetMonitor(new FleetMonitorOptions { SelfId = "5e1f0000", Table = () => table, Feeds = feeds, Time = new ManualTime(), Max = max });
        monitor.Start();
        return (table, mono, feeds, monitor);
    }

    [Fact]
    public async Task NoMoreThanItsNumberOfPeersAreSubscribedEvenWhenOneComesBack()
    {
        var (table, mono, feeds, monitor) = Rig(max: 2);
        await using (monitor)
        {
            table.Hear(Hello("a0000001"), "10.0.0.1");
            table.Hear(Hello("b0000002"), "10.0.0.2");
            table.Hear(Hello("c0000003"), "10.0.0.3");
            Assert.Equal(2, monitor.Subscriptions);
            Assert.True(await Eventually.TrueAsync(() => feeds.Dialled.Count == 2));
            Assert.False(feeds.Dialled.ContainsKey("c0000003"));

            // A is forgotten by the hello and its subscription ends; C takes the place.
            mono.AdvanceMs(ShowBusConstants.ForgetMs - 1000);
            table.Hear(Hello("b0000002"), "10.0.0.2");
            table.Hear(Hello("c0000003"), "10.0.0.3");
            mono.AdvanceMs(2000);
            table.Sweep();
            feeds.End("a0000001");
            Assert.True(await Eventually.TrueAsync(() => monitor.Subscriptions == 1));
            table.Hear(Hello("c0000003", "show-pc-2"), "10.0.0.3");
            Assert.Equal(2, monitor.Subscriptions);

            // A comes back while two are subscribed: it waits like any other machine.
            table.Hear(Hello("a0000001"), "10.0.0.1");
            Assert.Equal(2, monitor.Subscriptions);
            Assert.True(await Eventually.TrueAsync(() => feeds.Dialled.ContainsKey("c0000003")));
            Assert.Equal(1, feeds.Dialled["a0000001"]);
        }
    }

    [Fact]
    public async Task MachinesNeverHeardAreLetGoSoTheMonitorKeepsABoundedNumber()
    {
        var (table, mono, feeds, monitor) = Rig(max: 2);
        await using (monitor)
        {
            for (var i = 0; i < 80; i++)
            {
                var id = i.ToString("x8", CultureInfo.InvariantCulture);
                table.Hear(Hello(id), "10.0.1.1");
                Assert.Equal(1, monitor.Subscriptions);
                mono.AdvanceMs(ShowBusConstants.ForgetMs + 1);
                table.Sweep();
                feeds.End(id);
                Assert.True(await Eventually.TrueAsync(() => monitor.Subscriptions == 0), id);
            }
            Assert.Equal(80, feeds.Dialled.Count);
            Assert.Equal(monitor.Kept, monitor.Count);
            Assert.Empty(monitor.Peers());   // none was heard: none is on the board
        }
    }
}
