using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Fleet;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>Preflight's fleet rows, with faults injected through a fleet of the test's own: a machine heard is green; one gone quiet
/// and one known before the restart and not heard since are red with the line that lists who is heard; a certificate that changed is
/// red, refused with its words; a node whose monitor is off says so in one grey row.</summary>
public sealed class FleetPreflightTests
{
    internal sealed class TestFleet(params FleetPeer[] peers) : IFleetPeers
    {
        public List<FleetPeer> Now { get; } = [.. peers];

        public IReadOnlyList<FleetPeer> Peers() => [.. Now];

        public event Action? Changed;

        public void Move() => Changed?.Invoke();
    }

    private static async Task<IReadOnlyList<PreflightRow>> Rows(EngineCore engine) =>
        await new FleetPreflight(() => "/home/show/keys/fleet-pins.json")
            .RunAsync(new PreflightContext(engine, PreflightDepth.Quick, false), CancellationToken.None);

    [Fact]
    public async Task EveryExpectedMachineIsHeardAndEveryPinIsValid()
    {
        var three = FleetFixtures.ThreeNodes();
        var fleet = new TestFleet(
            new FleetPeer("9f8e7d6c", "FOH-B", "10.0.0.22", three[1].Health, FleetHeard.Live, "subscribed over TLS, certificate pinned") { Pin = "pinned" },
            new FleetPeer("c0ffee00", "FOH-C", "10.0.0.23", three[2].Health, FleetHeard.Gone, "FOH-C's health feed ended") { Pin = "pinned", SilentS = 7 },
            new FleetPeer("deadbeef", "FOH-D", "10.0.0.24", null, FleetHeard.Gone,
                "CERTIFICATE CHANGED: FOH-D now shows sha256:12ab…  — refused until you accept it in the Network drawer") { PinRefused = true, Pin = "refused" });
        var services = HealthTests.Services(new ManualTime()) with { FleetPeers = fleet };
        await using var engine = new EngineCore(services, Project.CreateDefault("Show"));
        engine.Fleet.LoadKnown("{\"known\":[{\"id\":\"9f8e7d6c\",\"name\":\"FOH-B\"},{\"id\":\"f00dcafe\",\"name\":\"FOH-E\"}]}");

        var rows = await Rows(engine);
        Assert.All(rows, r => Assert.Equal("fleet", r.Section));
        var b = rows.Single(r => r.Id == "machine:9f8e7d6c");
        Assert.Equal(Light.Green, b.Light);
        var c = rows.Single(r => r.Id == "machine:c0ffee00");
        Assert.Equal(Light.Red, c.Light);
        Assert.Equal("gone", c.Value);
        Assert.Equal("NODES", c.Fix);
        Assert.Contains("gone quiet 7 s", c.Note, StringComparison.Ordinal);
        var e = rows.Single(r => r.Id == "machine:f00dcafe");    // known before the restart, not heard since
        Assert.Equal(Light.Red, e.Light);
        Assert.Equal("NODES", e.Fix);
        var d = rows.Single(r => r.Id == "machine:deadbeef");
        Assert.Equal(Light.Red, d.Light);
        var pin = rows.Single(r => r.Id == "pin:deadbeef");
        Assert.Equal(Light.Red, pin.Light);
        Assert.Equal("refused", pin.Value);
        Assert.Contains("CERTIFICATE CHANGED", pin.Note, StringComparison.Ordinal);
        Assert.Contains("/home/show/keys/fleet-pins.json", pin.Note, StringComparison.Ordinal);
        Assert.Equal(Light.Green, rows.Single(r => r.Id == "pin:9f8e7d6c").Light);

        // The fault cleared: every machine heard, every pin valid — no red row.
        fleet.Now.Clear();
        fleet.Now.Add(new FleetPeer("9f8e7d6c", "FOH-B", "10.0.0.22", three[1].Health, FleetHeard.Live, "") { Pin = "pinned" });
        fleet.Now.Add(new FleetPeer("f00dcafe", "FOH-E", "10.0.0.25", three[0].Health! with { Facts = three[0].Health!.Facts with { Node = "f00dcafe" } }, FleetHeard.Live, "") { Pin = "pinned" });
        engine.Fleet.LoadKnown("{\"known\":[]}");
        var healthy = await Rows(engine);
        Assert.DoesNotContain(healthy, r => r.Light == Light.Red);
    }

    [Fact]
    public async Task ANodeWithoutTheMonitorSaysSoInOneGreyRow()
    {
        await using var engine = new EngineCore(HealthTests.Services(new ManualTime()), Project.CreateDefault("Show"));
        var rows = await Rows(engine);
        var row = Assert.Single(rows);
        Assert.Equal(Light.Grey, row.Light);
        Assert.Equal("off", row.Value);
    }

    [Fact]
    public async Task TheKnownMachinesSurviveARestartThroughTheRecoveryRecord()
    {
        var three = FleetFixtures.ThreeNodes();
        var fleet = new TestFleet(new FleetPeer("9f8e7d6c", "FOH-B", "10.0.0.22", three[1].Health, FleetHeard.Live, ""));
        await using var engine = new EngineCore(HealthTests.Services(new ManualTime()) with { FleetPeers = fleet }, Project.CreateDefault("Show"));
        fleet.Move();
        var saved = engine.Fleet.KnownJson();
        Assert.Contains("9f8e7d6c", saved, StringComparison.Ordinal);

        await using var restarted = new EngineCore(HealthTests.Services(new ManualTime()) with { FleetPeers = new TestFleet() }, Project.CreateDefault("Show"));
        restarted.Fleet.LoadKnown(saved);
        Assert.Equal("FOH-B", restarted.Fleet.Known["9f8e7d6c"]);
        var row = (await Rows(restarted)).Single(r => r.Id == "machine:9f8e7d6c");
        Assert.Equal(Light.Red, row.Light);
        Assert.Equal("not heard", row.Value);
    }
}
