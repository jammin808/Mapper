using System.Net;
using System.Text;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Engine.Fleet;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>Two nodes on loopback, each with the fleet's part on its own hand-moved clock: B appears in A's FLEET when B's first HEALTH
/// arrives (the subscription is pushed, over TLS pinned by B's hello); B stopped is stale at 3 s and gone, red with words, at 6 s of
/// A's clock, and stays on the board. The web port's /health and /fleet are open reads that change nothing.</summary>
public sealed class FleetMonitorTests
{
    private static readonly string[] Reads = ["/health", "/fleet"];

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

            // Event-driven: B's hello, the subscription, B's first record pushed at once — no clock moved.
            Assert.True(await Eventually.TrueAsync(() => Machine(a, b.Id) is { } m && m.GetProperty("heard").GetString() == "live"), a.Journal());
            Assert.Equal(0, timeA.GetTimestamp());
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
            var context = ContextBuilder.Build(a.Engine);
            var asPeer = context.Peers.Single(p => p.Id == b.Id);
            Assert.Equal(peer.Health.Headline, asPeer.Headline);

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
}
