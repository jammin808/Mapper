using System.Net;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Drivers.PjLink;
using PlayMap.Devices.Model;
using PlayMap.Devices.Tests.Simulators;

namespace PlayMap.Devices.Tests;

/// <summary>The PJLink driver end to end against a simulated projector on loopback: authentication, attempts
/// raised to facts by read-backs, errors in words, class 1 limits, and class 2 search and notifications on UDP.</summary>
public class PjLinkDriverTests
{
    private static PjLinkDriver Driver(PjLinkSimulator sim, string password = "", IDeviceLog? log = null) =>
        new(TestSupport.Link(ProjectorProtocol.PjLink, sim.Port, password.Length > 0 ? "pj" : ""),
            password.Length > 0 ? TestSupport.Store("pj", "", password) : TestSupport.NoSecrets(), log ?? DeviceLog.None, DriverTimings.Fast);

    [Fact]
    public async Task AProtectedProjectorGetsTheDigestInFrontOfTheFirstCommandOnly()
    {
        await using var sim = new PjLinkSimulator(password: "JBMIAProjectorLink") { Power = "1" };
        await using var d = Driver(sim, "JBMIAProjectorLink");
        var reading = await d.ReadStatusAsync(CancellationToken.None);
        Assert.Equal(CommandStage.Observed, reading.Result.Stage);
        Assert.Single(sim.Digests);
        Assert.Equal("5d8409bc1c3fa39749434aa3a5c38682", sim.Digests[0]);
        Assert.Contains("%1POWR ?", sim.Received);
        Assert.Contains("%2SNUM ?", sim.Received);
    }

    [Fact]
    public async Task AWrongPasswordReadsAsAuthenticationFailed()
    {
        await using var sim = new PjLinkSimulator(password: "right-one");
        await using var d = Driver(sim, "wrong-one");
        var reading = await d.ReadStatusAsync(CancellationToken.None);
        Assert.Equal(CommandStage.Unreached, reading.Result.Stage);
        Assert.Contains("authentication failed", reading.Result.Words, StringComparison.Ordinal);
        Assert.Null(reading.State);
    }

    [Fact]
    public async Task AProjectorThatAsksForAPasswordWeDoNotHaveIsUnreachedWithTheReason()
    {
        await using var sim = new PjLinkSimulator(password: "set-on-projector");
        await using var d = Driver(sim);
        var result = await d.SetPowerAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Unreached, result.Stage);
        Assert.Contains("credential", result.Words, StringComparison.Ordinal);
        Assert.Empty(sim.Received);
    }

    [Fact]
    public async Task PowerOnIsAcceptedThenObservedWhileTheProjectorWarmsUp()
    {
        await using var sim = new PjLinkSimulator { WarmUpSteps = 3 };
        await using var d = Driver(sim);
        var result = await d.SetPowerAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.Contains("warming up", result.Words, StringComparison.Ordinal);
        Assert.Equal(["%1POWR 1", "%1POWR ?"], sim.Received);
    }

    [Fact]
    public async Task APowerCommandAnsweredErr3IsRejectedWithTheReasonAndNotReadBack()
    {
        await using var sim = new PjLinkSimulator { PowerBusy = true };
        await using var d = Driver(sim);
        var result = await d.SetPowerAsync(false, CancellationToken.None);
        Assert.Equal(CommandStage.Rejected, result.Stage);
        Assert.Contains("unavailable now", result.Words, StringComparison.Ordinal);
        Assert.Single(sim.Received);
    }

    [Fact]
    public async Task ClosingTheShutterIsObservedThroughAvMute()
    {
        await using var sim = new PjLinkSimulator { Power = "1" };
        await using var d = Driver(sim);
        var result = await d.SetShutterAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.Equal("31", sim.Avmt);
        var open = await d.SetShutterAsync(false, CancellationToken.None);
        Assert.Equal(CommandStage.Observed, open.Stage);
        Assert.Equal("30", sim.Avmt);
    }

    [Fact]
    public async Task FreezeOnAClass1ProjectorIsRejectedAsAnUndefinedCommand()
    {
        await using var sim = new PjLinkSimulator(pjClass: 1) { Power = "1" };
        await using var d = Driver(sim);
        var result = await d.SetFreezeAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Rejected, result.Stage);
        Assert.Contains("undefined command", result.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreezeOnAClass2ProjectorIsObserved()
    {
        await using var sim = new PjLinkSimulator { Power = "1" };
        await using var d = Driver(sim);
        Assert.Equal(CommandStage.Observed, (await d.SetFreezeAsync(true, CancellationToken.None)).Stage);
        Assert.Equal("1", sim.Freeze);
    }

    [Fact]
    public async Task AStatusReadFillsEveryClass1AndClass2Field()
    {
        await using var sim = new PjLinkSimulator { Power = "1", Avmt = "31", Erst = "002000", Lamp = "1500 1 20 0", Input = "32" };
        await using var d = Driver(sim);
        var reading = await d.ReadStatusAsync(CancellationToken.None);
        var s = reading.State!;
        Assert.Equal(PowerState.On, s.Power);
        Assert.True(s.ShutterClosed);
        Assert.Equal("32", s.Input);
        Assert.True(s.FaultsRead);
        Assert.Equal(new[] { new DeviceFault("temperature: error", FaultSeverity.Error) }, s.Faults);
        Assert.Equal(new[] { 1500, 20 }, s.LightHours);
        Assert.Equal("PANASONIC", s.Manufacturer);
        Assert.Equal("PT-RZ120", s.Model);
        Assert.Equal("sim", s.Info);
        Assert.Equal("Stage Left", s.ProjectorName);
        Assert.Equal("SH1234567", s.Serial);
        Assert.Equal("1.02", s.Firmware);
        Assert.Equal((1920, 1080), (s.Signal.Width, s.Signal.Height));
        Assert.False(s.Frozen);
        Assert.Equal(2, d.ProjectorClass);
    }

    [Fact]
    public async Task AClass1StatusReadDoesNotAskClass2Questions()
    {
        await using var sim = new PjLinkSimulator(pjClass: 1) { Power = "1" };
        await using var d = Driver(sim);
        var reading = await d.ReadStatusAsync(CancellationToken.None);
        Assert.Equal(CommandStage.Observed, reading.Result.Stage);
        Assert.DoesNotContain(sim.Received, l => l.StartsWith("%2", StringComparison.Ordinal));
        Assert.Null(reading.State!.Serial);
    }

    [Fact]
    public async Task AnInputThatIsNotAPjLinkCodeIsRefusedWithoutAConnection()
    {
        await using var sim = new PjLinkSimulator();
        await using var d = Driver(sim);
        var result = await d.SelectInputAsync("HDMI", CancellationToken.None);
        Assert.Equal(CommandStage.Refused, result.Stage);
        Assert.Equal(0, sim.Connections);
    }

    [Fact]
    public async Task AnInputTheProjectorLacksIsRejectedAsOutOfParameter()
    {
        await using var sim = new PjLinkSimulator();
        await using var d = Driver(sim);
        var result = await d.SelectInputAsync("52", CancellationToken.None);
        Assert.Equal(CommandStage.Rejected, result.Stage);
        Assert.Contains("out of parameter", result.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInputIsObservedByReadingItBack()
    {
        await using var sim = new PjLinkSimulator();
        await using var d = Driver(sim);
        var result = await d.SelectInputAsync("11", CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.Equal("11", sim.Input);
    }

    [Fact]
    public async Task ADeadProjectorIsUnreachedAfterOneRetry()
    {
        await using var sim = new PjLinkSimulator { Dark = true };
        await using var d = Driver(sim);
        var result = await d.SetPowerAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Unreached, result.Stage);
        Assert.Equal(2, sim.Connections);
    }

    [Fact]
    public async Task ARawLineComesBackWithTheProjectorsAnswer()
    {
        await using var sim = new PjLinkSimulator { Power = "1" };
        await using var d = Driver(sim);
        var result = await d.RawAsync("%1LAMP ?", CancellationToken.None);
        Assert.Equal(CommandStage.Accepted, result.Stage);
        Assert.Equal("%1LAMP=1234 1", result.Reply);
    }

    [Fact]
    public async Task ThingsPjLinkHasNoWordForAreRefusedWithTheReason()
    {
        await using var sim = new PjLinkSimulator();
        await using var d = Driver(sim);
        var r = await d.ShowTestPatternAsync("off", CancellationToken.None);
        Assert.Equal(CommandStage.Refused, r.Stage);
        Assert.Contains("PJLink has no published command for test patterns", r.Words, StringComparison.Ordinal);
        Assert.Equal(CommandStage.Refused, (await d.ResetGeometryAsync(CancellationToken.None)).Stage);
        Assert.Equal(0, sim.Connections);
    }

    [Fact]
    public async Task ASearchFindsAClass2ProjectorOnLoopbackUdp()
    {
        using var projector = new PjLinkUdpSimulator("00:0a:1b:2c:3d:4e");
        await using var udp = new PjLinkUdp(0, IPAddress.Loopback);
        var hits = await udp.SearchAsync(projector.EndPoint, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        var hit = Assert.Single(hits);
        Assert.Equal("00:0a:1b:2c:3d:4e", hit.Mac);
        Assert.Equal(IPAddress.Loopback, hit.Address);
    }

    [Fact]
    public async Task ALinkUpNotificationIsHeardWithItsMacAddress()
    {
        using var projector = new PjLinkUdpSimulator("00:0a:1b:2c:3d:4e");
        await using var udp = new PjLinkUdp(0, IPAddress.Loopback);
        var heard = new TaskCompletionSource<PjLinkNoticeEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        udp.Noticed += (_, e) => heard.TrySetResult(e);
        udp.Start();
        await projector.SendAsync("%2LKUP=00:0a:1b:2c:3d:4e\r", new IPEndPoint(IPAddress.Loopback, udp.Port));
        var e = await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PjLinkNoticeKind.LinkUp, e.Notice.Kind);
        Assert.Equal("00:0a:1b:2c:3d:4e", e.Notice.Value);
        Assert.Equal(projector.EndPoint, e.From);
    }
}
