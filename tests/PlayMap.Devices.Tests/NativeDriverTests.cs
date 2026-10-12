using System.Security.Cryptography;
using System.Text;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Drivers.Barco;
using PlayMap.Devices.Drivers.Christie;
using PlayMap.Devices.Drivers.Epson;
using PlayMap.Devices.Drivers.Nec;
using PlayMap.Devices.Drivers.Panasonic;
using PlayMap.Devices.Drivers.Sony;
using PlayMap.Devices.Model;
using PlayMap.Devices.Tests.Simulators;

namespace PlayMap.Devices.Tests;

/// <summary>Christie's serial protocol over TCP against a simulated projector: silent sets raised to facts by their
/// queries, FYI frames skipped, error frames in words, raw lens positions, and the family-dependent geometry gate.</summary>
public class ChristieDriverTests
{
    private static ChristieDriver Driver(ChristieSimulator sim, bool allowUnverified = false) =>
        new(TestSupport.Link(ProjectorProtocol.ChristieAscii, sim.Port, allowUnverified: allowUnverified), TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);

    [Fact]
    public async Task AShutterSetIsSentSilentlyThenObservedByItsQuery()
    {
        await using var sim = new ChristieSimulator();
        await using var d = Driver(sim);
        var result = await d.SetShutterAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.Equal(1, sim.Get("SHU"));
        Assert.Equal(["(SHU 1)", "(SHU?)"], sim.Received);
    }

    [Fact]
    public async Task APowerQueryIsReadPastTheUnsolicitedFyiFrame()
    {
        await using var sim = new ChristieSimulator();
        sim.Set("PWR", 1);
        await using var d = Driver(sim);
        var reading = await d.ReadStatusAsync(CancellationToken.None);
        Assert.Equal(CommandStage.Observed, reading.Result.Stage);
        Assert.Equal(PowerState.On, reading.State!.Power);
        Assert.False(reading.State.ShutterClosed);
        Assert.Equal("001", reading.State.Input);
        Assert.Equal(new[] { 1234 }, reading.State.LightHours);
    }

    [Fact]
    public async Task ABadValueIsRejectedWithItsErrorWords()
    {
        await using var sim = new ChristieSimulator();
        await using var d = Driver(sim);
        var result = await d.RawAsync("(SHU 5)", CancellationToken.None);
        Assert.Equal(CommandStage.Rejected, result.Stage);
        Assert.Contains("invalid parameter", result.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLensIsReadInRawMotorUnits()
    {
        await using var sim = new ChristieSimulator();
        await using var d = Driver(sim);
        var reading = await d.ReadLensAsync(CancellationToken.None);
        Assert.Equal(new LensPositions(-120, 300, 15, -7), reading.State!.Lens);
    }

    [Fact]
    public async Task AnAbsoluteLensMoveIsSentButNotCalledAFact()
    {
        await using var sim = new ChristieSimulator();
        await using var d = Driver(sim);
        var result = await d.MoveLensAsync(LensMove.To(LensAxis.ShiftHorizontal, -250), CancellationToken.None);
        Assert.Equal(CommandStage.Sent, result.Stage);
        Assert.Contains("(LHO -0250)", sim.Received);
        Assert.Equal(-250, sim.Get("LHO"));
    }

    [Fact]
    public async Task GeometryResetIsRefusedUntilTheLinkAllowsUnverifiedCommands()
    {
        await using var sim = new ChristieSimulator();
        await using var gated = Driver(sim);
        var refused = await gated.ResetGeometryAsync(CancellationToken.None);
        Assert.Equal(CommandStage.Refused, refused.Stage);
        Assert.Contains("unverified", refused.Words, StringComparison.Ordinal);
        Assert.Equal(0, sim.Connections);
    }

    [Fact]
    public async Task GeometryResetFallsBackToWarpSelectWhenTheFamilyLacksWrp()
    {
        await using var sim = new ChristieSimulator();
        await using var d = Driver(sim, allowUnverified: true);
        var result = await d.ResetGeometryAsync(CancellationToken.None);
        Assert.Equal(CommandStage.Sent, result.Stage);
        Assert.Contains("(WPS 0)", result.Words, StringComparison.Ordinal);
        Assert.Equal(0, sim.Get("WPS"));
    }

    [Fact]
    public async Task TestPatternsOffIsObserved()
    {
        await using var sim = new ChristieSimulator();
        sim.Set("ITP", 3);
        await using var d = Driver(sim);
        Assert.Equal(CommandStage.Observed, (await d.ShowTestPatternAsync("off", CancellationToken.None)).Stage);
        Assert.Equal(0, sim.Get("ITP"));
    }
}

/// <summary>Panasonic NTCONTROL against a simulated projector: the protected-mode MD5 token on every command, a wrong
/// password in words, sets raised to facts by queries, identity, temperatures, absolute lens and geometry off.</summary>
public class PanasonicDriverTests
{
    private static PanasonicDriver Driver(PanasonicSimulator sim, string user = "", string password = "") =>
        new(TestSupport.Link(ProjectorProtocol.PanasonicNtControl, sim.Port, password.Length > 0 ? "pana" : ""),
            password.Length > 0 ? TestSupport.Store("pana", user, password) : TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);

    [Fact]
    public void TheTokenIsTheUppercaseMd5OfUserPasswordAndSeedJoinedByColons()
    {
        var expected = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes("admin1:panasonic:a1b2c3d4")));
        Assert.Equal(expected, NtControlProtocol.Token("admin1", "panasonic", "a1b2c3d4"));
        Assert.Equal(32, expected.Length);
    }

    [Fact]
    public void GreetingsAndErrorsAreRead()
    {
        Assert.Equal(new NtControlGreeting(true, true, "a1b2c3d4"), NtControlProtocol.ParseGreeting("NTCONTROL 1 a1b2c3d4\r"));
        Assert.Equal(new NtControlGreeting(true, false, ""), NtControlProtocol.ParseGreeting("NTCONTROL 0"));
        Assert.False(NtControlProtocol.ParseGreeting("PJLINK 0").Valid);
        Assert.Equal("ERR2", NtControlProtocol.ParseReply("ERR2").Error);
        Assert.Equal("ERRA", NtControlProtocol.ParseReply("00ERRA").Error);
        Assert.Equal("001", NtControlProtocol.ParseReply("00001").Body);
    }

    [Fact]
    public async Task AProtectedSessionPowersOnAndReadsItBack()
    {
        await using var sim = new PanasonicSimulator(password: "panasonic");
        await using var d = Driver(sim, password: "panasonic");
        var result = await d.SetPowerAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.Equal(["PON", "QPW"], sim.Received);
    }

    [Fact]
    public async Task AWrongPasswordReadsAsAPasswordMismatch()
    {
        await using var sim = new PanasonicSimulator(password: "panasonic");
        await using var d = Driver(sim, password: "not-it");
        var result = await d.SetShutterAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Unreached, result.Stage);
        Assert.Contains("password mismatch", result.Words, StringComparison.Ordinal);
        Assert.Empty(sim.Received);
    }

    [Fact]
    public async Task ClosingTheShutterIsObserved()
    {
        await using var sim = new PanasonicSimulator(user: "operator", password: "pw");
        await using var d = Driver(sim, "operator", "pw");
        var result = await d.SetShutterAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.Equal("1", sim.Shutter);
    }

    [Fact]
    public async Task AStatusReadGivesIdentityHoursTemperaturesAndTheSignal()
    {
        await using var sim = new PanasonicSimulator { Power = "001", Shutter = "1" };
        await using var d = Driver(sim);
        var s = (await d.ReadStatusAsync(CancellationToken.None)).State!;
        Assert.Equal(PowerState.On, s.Power);
        Assert.True(s.ShutterClosed);
        Assert.Equal("HD1", s.Input);
        Assert.Equal("PT-RZ120", s.Model);
        Assert.Equal("SW0101234", s.Serial);
        Assert.Equal("1.00.01", s.Firmware);
        Assert.Equal(new[] { 420 }, s.LightHours);
        Assert.Equal(new DeviceTemperature("intake", 31), s.Temperatures[0]);
        Assert.Equal(3, s.Temperatures.Length);
        Assert.Equal("1080/60p", s.Signal.Text);
    }

    [Fact]
    public async Task AnInputTheProjectorLacksIsRejectedAsOutOfRange()
    {
        await using var sim = new PanasonicSimulator();
        await using var d = Driver(sim);
        var result = await d.SelectInputAsync("RG2", CancellationToken.None);
        Assert.Equal(CommandStage.Rejected, result.Stage);
        Assert.Contains("parameter out of range", result.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLensMovesToAnAbsolutePositionThatReadsBack()
    {
        await using var sim = new PanasonicSimulator();
        await using var d = Driver(sim);
        var result = await d.MoveLensAsync(LensMove.To(LensAxis.ShiftVertical, -1200), CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.Contains("VXX:LNSI8=-01200", sim.Received);
        var lens = (await d.ReadLensAsync(CancellationToken.None)).State!.Lens;
        Assert.Equal(new LensPositions(-120, -1200, null, 5), lens);
    }

    [Fact]
    public async Task GeometryOffIsObserved()
    {
        await using var sim = new PanasonicSimulator();
        await using var d = Driver(sim);
        Assert.Equal(CommandStage.Observed, (await d.ResetGeometryAsync(CancellationToken.None)).Stage);
        Assert.Equal("+00000", sim.Geometry);
    }
}

/// <summary>Barco's Pulse JSON-RPC against a simulated projector: unframed objects, notifications skipped, sets
/// raised to facts by property reads, JSON-RPC errors in the projector's words.</summary>
public class BarcoPulseDriverTests
{
    private static BarcoPulseDriver Driver(BarcoPulseSimulator sim) =>
        new(TestSupport.Link(ProjectorProtocol.BarcoPulse, sim.Port), TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);

    [Fact]
    public void ARequestIsJsonRpc2WithItsParams()
    {
        var json = PulseProtocol.Request(7, "property.set", [new("property", "optics.shutter.target"), new("value", "Closed")]);
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"method\":\"property.set\",\"params\":{\"property\":\"optics.shutter.target\",\"value\":\"Closed\"},\"id\":7}", json);
        Assert.Null(PulseProtocol.ParseReply("{\"jsonrpc\":\"2.0\",\"method\":\"property.changed\",\"params\":{}}"));
        Assert.Equal("on", PulseProtocol.ParseReply("{\"jsonrpc\":\"2.0\",\"result\":\"on\",\"id\":3}")!.ResultText);
    }

    [Fact]
    public async Task PowerOnIsObservedThroughTheSystemState()
    {
        await using var sim = new BarcoPulseSimulator();
        await using var d = Driver(sim);
        var result = await d.SetPowerAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.Equal("on", sim.State);
    }

    [Fact]
    public async Task TheShutterIsSetAndObservedThroughItsPosition()
    {
        await using var sim = new BarcoPulseSimulator { State = "on" };
        await using var d = Driver(sim);
        var result = await d.SetShutterAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.Equal("Closed", sim.Shutter);
    }

    [Fact]
    public async Task AStatusReadGivesPowerShutterSourceAndIdentity()
    {
        await using var sim = new BarcoPulseSimulator { State = "on", Shutter = "Closed" };
        await using var d = Driver(sim);
        var s = (await d.ReadStatusAsync(CancellationToken.None)).State!;
        Assert.Equal(PowerState.On, s.Power);
        Assert.True(s.ShutterClosed);
        Assert.Equal("L1 HDMI", s.Input);
        Assert.Equal("F80-4K12", s.Model);
        Assert.Equal("2591234567", s.Serial);
    }

    [Fact]
    public async Task AnUnknownMethodIsRejectedWithTheProjectorsMessage()
    {
        await using var sim = new BarcoPulseSimulator();
        await using var d = Driver(sim);
        var result = await d.RawAsync("system.selfdestruct", CancellationToken.None);
        Assert.Equal(CommandStage.Rejected, result.Stage);
        Assert.Contains("Method not found", result.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LensStepsAreAcceptedAndAbsolutePositionsRefused()
    {
        await using var sim = new BarcoPulseSimulator();
        await using var d = Driver(sim);
        Assert.Equal(CommandStage.Accepted, (await d.MoveLensAsync(LensMove.By(LensAxis.Zoom, -3), CancellationToken.None)).Stage);
        Assert.Contains(sim.Received, j => j.Contains("optics.zoom.stepreverse", StringComparison.Ordinal) && j.Contains("\"steps\":3", StringComparison.Ordinal));
        Assert.Equal(CommandStage.Refused, (await d.MoveLensAsync(LensMove.To(LensAxis.Zoom, 100), CancellationToken.None)).Stage);
    }
}

/// <summary>Sony ADCP against a simulated projector: the whole driver is unverified and refused until the link
/// allows it; then the SHA-256 challenge, power observed through power_status and the picture blank.</summary>
public class SonyAdcpDriverTests
{
    private static SonyAdcpDriver Driver(SonyAdcpSimulator sim, string password = "", bool allow = true) =>
        new(TestSupport.Link(ProjectorProtocol.SonyAdcp, sim.Port, password.Length > 0 ? "sony" : "", allowUnverified: allow),
            password.Length > 0 ? TestSupport.Store("sony", "", password) : TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);

    [Fact]
    public async Task EveryCommandIsRefusedUntilTheLinkAllowsUnverifiedCommands()
    {
        await using var sim = new SonyAdcpSimulator();
        await using var d = Driver(sim, allow: false);
        var result = await d.SetPowerAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Refused, result.Stage);
        Assert.Contains("unverified", result.Words, StringComparison.Ordinal);
        Assert.Equal(CommandStage.Refused, (await d.ReadStatusAsync(CancellationToken.None)).Result.Stage);
        Assert.Equal(CommandStage.Refused, (await d.RawAsync("power_status ?", CancellationToken.None)).Stage);
        Assert.Equal(0, sim.Connections);
    }

    [Fact]
    public async Task TheChallengeIsAnsweredWithSha256AndPowerIsObserved()
    {
        await using var sim = new SonyAdcpSimulator(password: "Projector1");
        await using var d = Driver(sim, "Projector1");
        var result = await d.SetPowerAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.Equal(["power \"on\"", "power_status ?"], sim.Received);
    }

    [Fact]
    public async Task AWrongPasswordIsUnreachedInWords()
    {
        await using var sim = new SonyAdcpSimulator(password: "Projector1");
        await using var d = Driver(sim, "nope");
        var result = await d.ReadStatusAsync(CancellationToken.None);
        Assert.Equal(CommandStage.Unreached, result.Result.Stage);
        Assert.Contains("authentication failed", result.Result.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBlankIsObservedWhenOnAndRejectedInStandby()
    {
        await using var sim = new SonyAdcpSimulator();
        await using var d = Driver(sim);
        var standby = await d.SetShutterAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Rejected, standby.Stage);
        Assert.Contains("not available now", standby.Words, StringComparison.Ordinal);
        sim.Power = "on";
        Assert.Equal(CommandStage.Observed, (await d.SetShutterAsync(true, CancellationToken.None)).Stage);
        Assert.Equal("on", sim.Blank);
    }
}

/// <summary>Epson ESC/VP.net against a simulated projector: the CONNECT header with the password header, the
/// status codes in words, ESC/VP21 sets accepted by ":" and observed by their query.</summary>
public class EpsonDriverTests
{
    private static EpsonDriver Driver(EpsonSimulator sim, string password = "") =>
        new(TestSupport.Link(ProjectorProtocol.EpsonEscVpNet, sim.Port, password.Length > 0 ? "ep" : ""),
            password.Length > 0 ? TestSupport.Store("ep", "", password) : TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);

    [Fact]
    public void TheConnectRequestIsTheSixteenByteHeaderAndAPasswordHeader()
    {
        var bytes = EscVpProtocol.ConnectRequest("pw");
        Assert.Equal(16 + 18, bytes.Length);
        Assert.Equal("ESC/VP.net", Encoding.ASCII.GetString(bytes, 0, 10));
        Assert.Equal(new byte[] { 0x10, 0x03, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01 }, bytes[10..18]);
        Assert.Equal((byte)'p', bytes[18]);
        Assert.Equal(16, EscVpProtocol.ConnectRequest(null).Length);
    }

    [Fact]
    public async Task APasswordedConnectPowersOnAndReadsItBack()
    {
        await using var sim = new EpsonSimulator(password: "epson");
        await using var d = Driver(sim, "epson");
        var result = await d.SetPowerAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.Equal(["PWR ON", "PWR?"], sim.Received);
    }

    [Fact]
    public async Task AWrongPasswordIsUnreachedInWords()
    {
        await using var sim = new EpsonSimulator(password: "epson");
        await using var d = Driver(sim, "x9-not-it");
        var result = await d.SetPowerAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Unreached, result.Stage);
        Assert.Contains("password wrong", result.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AvMuteIsObservedWhenOnAndRejectedInStandby()
    {
        await using var sim = new EpsonSimulator();
        await using var d = Driver(sim);
        Assert.Equal(CommandStage.Rejected, (await d.SetShutterAsync(true, CancellationToken.None)).Stage);
        sim.Power = "01";
        Assert.Equal(CommandStage.Observed, (await d.SetShutterAsync(true, CancellationToken.None)).Stage);
        Assert.Equal("ON", sim.Mute);
    }

    [Fact]
    public async Task AStatusReadGivesPowerHoursSerialAndNoFaults()
    {
        await using var sim = new EpsonSimulator { Power = "01" };
        await using var d = Driver(sim);
        var s = (await d.ReadStatusAsync(CancellationToken.None)).State!;
        Assert.Equal(PowerState.On, s.Power);
        Assert.Equal(new[] { 812 }, s.LightHours);
        Assert.Equal("X4AB1234567", s.Serial);
        Assert.True(s.FaultsRead);
        Assert.Empty(s.Faults);
    }
}

/// <summary>NEC binary control against a simulated projector: the checksum, the unverified gate, acknowledgement
/// then the running status, and a refusal's error bytes in words.</summary>
public class NecDriverTests
{
    private static NecDriver Driver(NecSimulator sim, bool allow = true) =>
        new(TestSupport.Link(ProjectorProtocol.NecBinary, sim.Port, allowUnverified: allow), TestSupport.NoSecrets(), DeviceLog.None, DriverTimings.Fast);

    [Fact]
    public void AFrameEndsWithTheLowByteOfTheSumOfItsBytes()
    {
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x02 }, NecProtocol.PowerOn);
        Assert.Equal(new byte[] { 0x02, 0x01, 0x00, 0x00, 0x00, 0x03 }, NecProtocol.PowerOff);
        Assert.Equal(new byte[] { 0x00, 0x85, 0x00, 0x00, 0x01, 0x01, 0x87 }, NecProtocol.RunningStatus);
        Assert.Equal(0x12, NecProtocol.PictureMuteOn[^1]);
    }

    [Fact]
    public async Task EveryCommandIsRefusedUntilTheLinkAllowsUnverifiedCommands()
    {
        await using var sim = new NecSimulator();
        await using var d = Driver(sim, allow: false);
        Assert.Equal(CommandStage.Refused, (await d.SetPowerAsync(true, CancellationToken.None)).Stage);
        Assert.Equal(0, sim.Connections);
    }

    [Fact]
    public async Task PowerIsAcknowledgedThenObservedThroughTheRunningStatus()
    {
        await using var sim = new NecSimulator();
        await using var d = Driver(sim);
        var result = await d.SetPowerAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Observed, result.Stage);
        Assert.True(sim.On);
        Assert.Equal(0, sim.BadChecksums);
        var status = await d.ReadStatusAsync(CancellationToken.None);
        Assert.Equal(PowerState.On, status.State!.Power);
    }

    [Fact]
    public async Task PictureMuteIsAcknowledgedWhenOnAndRefusedInWordsWhenOff()
    {
        await using var sim = new NecSimulator();
        await using var d = Driver(sim);
        var off = await d.SetShutterAsync(true, CancellationToken.None);
        Assert.Equal(CommandStage.Rejected, off.Stage);
        Assert.Contains("the projector is off", off.Words, StringComparison.Ordinal);
        sim.On = true;
        Assert.Equal(CommandStage.Accepted, (await d.SetShutterAsync(true, CancellationToken.None)).Stage);
        Assert.True(sim.PictureMuted);
    }
}
