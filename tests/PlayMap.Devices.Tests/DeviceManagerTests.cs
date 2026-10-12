using System.Collections.Concurrent;
using System.Net;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Drivers.PjLink;
using PlayMap.Devices.Manager;
using PlayMap.Devices.Model;
using PlayMap.Devices.Tests.Simulators;

namespace PlayMap.Devices.Tests;

/// <summary>The manager on a manual clock: liveness from silence (live, stale, gone, back), one command in flight
/// per projector, gated drivers left grey with the reason, staggered first polls, notifications folded in, and
/// discovery that identifies what answered and suggests its driver.</summary>
public class DeviceManagerTests
{
    private static DeviceManager Manager(IProjectorDriverFactory factory, ManualClock clock) =>
        new(factory, clock, DeviceLog.None);

    [Fact]
    public async Task ADeadProjectorIsMarkedStaleThenGoneAndRecovers()
    {
        await using var sim = new PjLinkSimulator { Power = "1" };
        var clock = new ManualClock();
        await using var m = Manager(new ProjectorDrivers(TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast), clock);
        var seen = new ConcurrentQueue<DeviceLiveness>();
        m.StateChanged += (_, e) =>
        {
            if (e.Previous.Liveness != e.Current.Liveness)
                seen.Enqueue(e.Current.Liveness);
        };
        await m.SetLinksAsync([TestSupport.Link(ProjectorProtocol.PjLink, sim.Port, pollMs: 1000)]);

        clock.AdvanceMs(1000);
        await m.PollDueAsync(CancellationToken.None);
        var live = m.State("dv00000001")!;
        Assert.Equal(DeviceLiveness.Live, live.Liveness);
        Assert.Equal(Light.Green, live.Light);
        Assert.Equal(PowerState.On, live.Power);

        sim.Dark = true;
        clock.AdvanceMs(7000);
        await m.PollDueAsync(CancellationToken.None);
        var stale = m.State("dv00000001")!;
        Assert.Equal(DeviceLiveness.Stale, stale.Liveness);
        Assert.Equal(Light.Amber, stale.Light);
        Assert.False(stale.Online);
        Assert.NotEmpty(stale.LastProblem);

        clock.AdvanceMs(6000);
        await m.PollDueAsync(CancellationToken.None);
        Assert.Equal(DeviceLiveness.Gone, m.State("dv00000001")!.Liveness);
        Assert.Equal(Light.Red, m.State("dv00000001")!.Light);

        sim.Dark = false;
        clock.AdvanceMs(70_000);
        await m.PollDueAsync(CancellationToken.None);
        var back = m.State("dv00000001")!;
        Assert.Equal(DeviceLiveness.Live, back.Liveness);
        Assert.True(back.Online);
        Assert.Equal("", back.LastProblem);
        Assert.Equal([DeviceLiveness.Live, DeviceLiveness.Stale, DeviceLiveness.Gone, DeviceLiveness.Live], seen);
    }

    [Fact]
    public async Task ASilentProjectorIsPolledLessOftenWhileItStaysSilent()
    {
        var factory = new FakeFactory(_ => new FakeDriver { Fail = true });
        var clock = new ManualClock();
        await using var m = Manager(factory, clock);
        await m.SetLinksAsync([TestSupport.Link(ProjectorProtocol.PjLink, 1, pollMs: 1000)]);
        for (var t = 0; t < 60; t++)
        {
            clock.AdvanceMs(1000);
            await m.PollDueAsync(CancellationToken.None);
        }
        var polls = factory.Drivers.Single().StatusReads;
        Assert.InRange(polls, 4, 10);
    }

    [Fact]
    public async Task NeverMoreThanOneCommandIsInFlightOnAProjector()
    {
        var factory = new FakeFactory(_ => new FakeDriver { Delay = TimeSpan.FromMilliseconds(30) });
        var clock = new ManualClock();
        await using var m = Manager(factory, clock);
        await m.SetLinksAsync([TestSupport.Link(ProjectorProtocol.PjLink, 1, pollMs: 1000)]);
        clock.AdvanceMs(1000);
        var commands = Enumerable.Range(0, 6).Select(i => m.RunAsync("dv00000001", (d, ct) => d.SetShutterAsync(i % 2 == 0, ct), CancellationToken.None)).ToList();
        var poll = m.PollDueAsync(CancellationToken.None);
        await Task.WhenAll(commands);
        await poll;
        var driver = factory.Drivers.Single();
        Assert.Equal(1, driver.MaxConcurrent);
        Assert.Equal(6, driver.ShutterCalls);
        Assert.All(commands, c => Assert.Equal(CommandStage.Observed, c.Result.Stage));
    }

    [Fact]
    public async Task AnUnknownLinkIsRefusedByName()
    {
        await using var m = Manager(new FakeFactory(_ => new FakeDriver()), new ManualClock());
        var r = await m.RunAsync("dvnope", (d, ct) => d.SetPowerAsync(true, ct), CancellationToken.None);
        Assert.Equal(CommandStage.Refused, r.Stage);
        Assert.Contains("dvnope", r.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGatedDriverStaysGreyWithTheReason()
    {
        await using var sim = new SonyAdcpSimulator();
        var clock = new ManualClock();
        await using var m = Manager(new ProjectorDrivers(TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast), clock);
        await m.SetLinksAsync([TestSupport.Link(ProjectorProtocol.SonyAdcp, sim.Port, pollMs: 1000)]);
        clock.AdvanceMs(60_000);
        await m.PollDueAsync(CancellationToken.None);
        var s = m.State("dv00000001")!;
        Assert.Equal(DeviceLiveness.Unknown, s.Liveness);
        Assert.Equal(Light.Grey, s.Light);
        Assert.Contains("unverified", s.LastProblem, StringComparison.Ordinal);
        Assert.Equal(0, sim.Connections);
    }

    [Fact]
    public async Task FirstPollsAreStaggeredAcrossTheStartOfTheInterval()
    {
        var factory = new FakeFactory(_ => new FakeDriver());
        var clock = new ManualClock();
        await using var m = Manager(factory, clock);
        var links = Enumerable.Range(1, 8).Select(i => TestSupport.Link(ProjectorProtocol.PjLink, 1, pollMs: 4000) with { Id = "dv0000000" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList();
        await m.SetLinksAsync(links);
        await m.PollDueAsync(CancellationToken.None);
        var atStart = factory.Drivers.Count(d => d.StatusReads > 0);
        clock.AdvanceMs(1000);
        await m.PollDueAsync(CancellationToken.None);
        Assert.True(atStart < 8, "every projector polled in the same tick");
        Assert.All(factory.Drivers, d => Assert.Equal(1, d.StatusReads));
        Assert.Equal(8, m.States.Length);
    }

    [Fact]
    public async Task APowerNotificationIsFoldedIntoTheProjectorsState()
    {
        using var projector = new PjLinkUdpSimulator("00:0a:1b:2c:3d:4e");
        await using var udp = new PjLinkUdp(0, IPAddress.Loopback);
        var clock = new ManualClock(1);
        await using var m = Manager(new FakeFactory(_ => new FakeDriver()), clock);
        await m.SetLinksAsync([TestSupport.Link(ProjectorProtocol.PjLink, 1)]);
        var changed = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.StateChanged += (_, e) => changed.TrySetResult(e.Current);
        m.AttachNotifications(udp);
        await projector.SendAsync("%2POWR=2\r", new IPEndPoint(IPAddress.Loopback, udp.Port));
        var s = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PowerState.CoolingDown, s.Power);
        Assert.Equal(DeviceLiveness.Live, s.Liveness);
    }

    [Fact]
    public async Task DiscoveryFindsAProjectorIdentifiesItAndSuggestsItsNativeDriver()
    {
        using var projectorUdp = new PjLinkUdpSimulator("00:0a:1b:2c:3d:4e");
        await using var projectorTcp = new PjLinkSimulator { Power = "1" };
        await using var udp = new PjLinkUdp(0, IPAddress.Loopback);
        var discovery = new ProjectorDiscovery(TestSupport.NoSecrets(), DeviceLog.None);
        var found = await discovery.DiscoverAsync(udp, new DiscoveryOptions
        {
            SearchTarget = projectorUdp.EndPoint,
            Window = TimeSpan.FromMilliseconds(400),
            PjLinkPort = projectorTcp.Port,
            ProbeNativePort = false,
            Timings = DriverTimings.Fast,
        }, CancellationToken.None);
        var p = Assert.Single(found);
        Assert.Equal("127.0.0.1", p.Host);
        Assert.Equal("00:0a:1b:2c:3d:4e", p.Mac);
        Assert.Equal(2, p.PjLinkClass);
        Assert.Equal("PANASONIC", p.Manufacturer);
        Assert.Equal("PT-RZ120", p.Model);
        Assert.Equal("sim", p.Info);
        Assert.Equal("SH1234567", p.Serial);
        Assert.Equal(ProjectorProtocol.PanasonicNtControl, p.Suggested);
        Assert.Equal("", p.Problem);
    }

    [Fact]
    public void UnverifiedNativeDriversAreNotSuggestedOverPjLink()
    {
        Assert.Equal(ProjectorProtocol.ChristieAscii, DriverSuggestion.For("CHRISTIE", "D20WU-HS").Protocol);
        Assert.Equal(ProjectorProtocol.EpsonEscVpNet, DriverSuggestion.For("EPSON", "EB-PU2010").Protocol);
        Assert.Equal(ProjectorProtocol.BarcoPulse, DriverSuggestion.For("BARCO", "F80-4K12").Protocol);
        Assert.Equal(ProjectorProtocol.PjLink, DriverSuggestion.For("SONY", "VPL-FHZ91").Protocol);
        Assert.Equal(ProjectorProtocol.PjLink, DriverSuggestion.For("SHARP NEC", "PA1004UL").Protocol);
        Assert.Equal(ProjectorProtocol.SonyAdcp, DriverSuggestion.Native("SONY"));
        Assert.Equal(ProjectorProtocol.PjLink, DriverSuggestion.For("ACME", "X1").Protocol);
    }
}

/// <summary>Hands the manager fake drivers and keeps them for asserts.</summary>
internal sealed class FakeFactory(Func<ProjectorLink, FakeDriver> make) : IProjectorDriverFactory
{
    public ConcurrentBag<FakeDriver> Drivers { get; } = [];

    public IProjectorDriver Create(ProjectorLink link)
    {
        var d = make(link);
        d.Bind(link);
        Drivers.Add(d);
        return d;
    }
}

/// <summary>A driver that counts what it is asked and how many calls overlap; it can fail every status read.</summary>
internal sealed class FakeDriver : IProjectorDriver
{
    private int _inFlight;
    private int _maxConcurrent;
    private int _statusReads;
    private int _shutterCalls;
    private ProjectorLink _link = TestSupport.Link(ProjectorProtocol.PjLink, 1);

    public bool Fail { get; init; }

    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    public int StatusReads => Volatile.Read(ref _statusReads);

    public int ShutterCalls => Volatile.Read(ref _shutterCalls);

    public void Bind(ProjectorLink link) => _link = link;

    public ProjectorLink Link => _link;

    public ProjectorProtocol Protocol => ProjectorProtocol.PjLink;

    public DriverProvenance Provenance => DriverProvenance.VendorDocument;

    public ProjectorCapabilities Capabilities => ProjectorCapabilities.Power | ProjectorCapabilities.Shutter | ProjectorCapabilities.Status;

    public ProjectorCapabilities Unverified => ProjectorCapabilities.None;

    public IReadOnlyList<string> TestPatternNames => [];

    public bool CanIdentify => false;

    public string Caveat(ProjectorCapabilities capability) => "";

    private async Task<T> Track<T>(Func<T> result)
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while ((seen = Volatile.Read(ref _maxConcurrent)) < now && Interlocked.CompareExchange(ref _maxConcurrent, now, seen) != seen)
        {
            // Retried until the maximum holds the larger value.
        }
        try
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay);
            return result();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public Task<CommandResult> ConnectAsync(CancellationToken ct) => Task.FromResult(CommandResult.Accepted("connect", "ok"));

    public Task<CommandResult> SetPowerAsync(bool on, CancellationToken ct) => Track(() => CommandResult.Observed("power", "ok"));

    public Task<CommandResult> SelectInputAsync(string input, CancellationToken ct) => Task.FromResult(CommandResult.Refused("input", "no"));

    public Task<CommandResult> SetShutterAsync(bool closed, CancellationToken ct) =>
        Track(() =>
        {
            Interlocked.Increment(ref _shutterCalls);
            return CommandResult.Observed("shutter", closed ? "closed" : "open");
        });

    public Task<CommandResult> SetFreezeAsync(bool frozen, CancellationToken ct) => Task.FromResult(CommandResult.Refused("freeze", "no"));

    public Task<CommandResult> MoveLensAsync(LensMove move, CancellationToken ct) => Task.FromResult(CommandResult.Refused("lens", "no"));

    public Task<CommandResult> RecallLensMemoryAsync(int slot, CancellationToken ct) => Task.FromResult(CommandResult.Refused("lens memory", "no"));

    public Task<CommandResult> ShowTestPatternAsync(string pattern, CancellationToken ct) => Task.FromResult(CommandResult.Refused("pattern", "no"));

    public Task<CommandResult> ResetGeometryAsync(CancellationToken ct) => Task.FromResult(CommandResult.Refused("geometry", "no"));

    public Task<CommandResult> UploadGeometryAsync(WarpGrid grid, CancellationToken ct) => Task.FromResult(CommandResult.Refused("geometry upload", "no"));

    public Task<CommandResult> SetEdgeBlendAsync(EdgeBlendSettings blend, CancellationToken ct) => Task.FromResult(CommandResult.Refused("blend", "no"));

    public Task<CommandResult> SetBlackLevelAsync(BlackLevelSettings level, CancellationToken ct) => Task.FromResult(CommandResult.Refused("black", "no"));

    public Task<CommandResult> SetColorGainsAsync(ColorGains gains, CancellationToken ct) => Task.FromResult(CommandResult.Refused("gains", "no"));

    public Task<DriverReading> ReadStatusAsync(CancellationToken ct) =>
        Track(() =>
        {
            Interlocked.Increment(ref _statusReads);
            return Fail
                ? new DriverReading(CommandResult.Unreached("status", "no connection"), null)
                : new DriverReading(CommandResult.Observed("status", "on"), new DeviceState { LinkId = _link.Id, Power = PowerState.On });
        });

    public Task<DriverReading> ReadLensAsync(CancellationToken ct) => Task.FromResult(new DriverReading(CommandResult.Refused("lens", "no"), null));

    public Task<CommandResult> RawAsync(string line, CancellationToken ct) => Task.FromResult(CommandResult.Refused("raw", "no"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
