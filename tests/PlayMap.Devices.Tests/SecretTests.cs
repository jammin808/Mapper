using PlayMap.Core.Time;
using PlayMap.Devices.Commands;
using PlayMap.Devices.Drivers;
using PlayMap.Devices.Drivers.Panasonic;
using PlayMap.Devices.Drivers.PjLink;
using PlayMap.Devices.Drivers.Sony;
using PlayMap.Devices.Manager;
using PlayMap.Devices.Model;
using PlayMap.Devices.Tests.Simulators;

namespace PlayMap.Devices.Tests;

/// <summary>No password, digest, token or hash appears in any log line, result, reply, state or exception message,
/// whether the password is right, wrong or the projector is gone — for every protocol that authenticates.</summary>
public class SecretTests
{
    private const string Secret = "S3cr3t-Pa55word";

    private static async Task<List<string>> ExerciseAsync(IProjectorDriver d)
    {
        var words = new List<string>();
        void Keep(CommandResult r)
        {
            words.Add(r.ToString());
            words.Add(r.Reply);
        }

        try
        {
            Keep(await d.ConnectAsync(CancellationToken.None));
            Keep(await d.SetPowerAsync(true, CancellationToken.None));
            Keep(await d.SetShutterAsync(true, CancellationToken.None));
            Keep(await d.SelectInputAsync("31", CancellationToken.None));
            Keep(await d.ShowTestPatternAsync("off", CancellationToken.None));
            Keep(await d.RawAsync("power_status ?", CancellationToken.None));
            var status = await d.ReadStatusAsync(CancellationToken.None);
            Keep(status.Result);
            words.Add(status.State?.ToString() ?? "");
            words.Add(status.State?.Headline() ?? "");
        }
        catch (Exception e)
        {
            // Nothing should throw, but if something does its message is checked too.
            words.Add(e.ToString());
        }
        words.Add(d.Link.ToString());
        return words;
    }

    private static void AssertClean(IEnumerable<string> lines, params string[] secrets)
    {
        foreach (var line in lines)
            foreach (var s in secrets)
                Assert.DoesNotContain(s, line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACredentialPrintsAsTheWordCredential()
    {
        var c = new Credential("admin1", Secret);
        Assert.Equal("credential", c.ToString());
        Assert.DoesNotContain(Secret, $"{c} {TestSupport.Store("x", "admin1", Secret)}", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Secret)]
    [InlineData("wrong-" + Secret)]
    public async Task PjLinkWritesNoSecretAnywhere(string tried)
    {
        await using var sim = new PjLinkSimulator(password: Secret) { Power = "1" };
        var log = new CollectingLog();
        await using var d = new PjLinkDriver(TestSupport.Link(ProjectorProtocol.PjLink, sim.Port, "c"), TestSupport.Store("c", "", tried), log, DriverTimings.Fast);
        var words = await ExerciseAsync(d);
        AssertClean(words.Concat(log.Lines), Secret, tried, PjLinkProtocol.Digest(sim.Seed, Secret), PjLinkProtocol.Digest(sim.Seed, tried));
        Assert.NotEmpty(log.Lines);
    }

    [Theory]
    [InlineData(Secret)]
    [InlineData("wrong-" + Secret)]
    public async Task PanasonicWritesNoSecretAnywhere(string tried)
    {
        await using var sim = new PanasonicSimulator(password: Secret);
        var log = new CollectingLog();
        await using var d = new PanasonicDriver(TestSupport.Link(ProjectorProtocol.PanasonicNtControl, sim.Port, "c"), TestSupport.Store("c", "admin1", tried), log, DriverTimings.Fast);
        var words = await ExerciseAsync(d);
        AssertClean(words.Concat(log.Lines), Secret, tried, NtControlProtocol.Token("admin1", tried, "a1b2c3d4"));
    }

    [Theory]
    [InlineData(Secret)]
    [InlineData("wrong-" + Secret)]
    public async Task SonyWritesNoSecretAnywhere(string tried)
    {
        await using var sim = new SonyAdcpSimulator(password: Secret);
        var log = new CollectingLog();
        await using var d = new SonyAdcpDriver(TestSupport.Link(ProjectorProtocol.SonyAdcp, sim.Port, "c", allowUnverified: true), TestSupport.Store("c", "", tried), log, DriverTimings.Fast);
        var words = await ExerciseAsync(d);
        AssertClean(words.Concat(log.Lines), Secret, tried, AdcpProtocol.Hash("7f3a9c21", tried));
    }

    [Theory]
    [InlineData(Secret)]
    [InlineData("wrong-" + Secret)]
    public async Task EpsonWritesNoSecretAnywhere(string tried)
    {
        await using var sim = new EpsonSimulator(password: Secret[..12]);
        var log = new CollectingLog();
        await using var d = new Drivers.Epson.EpsonDriver(TestSupport.Link(ProjectorProtocol.EpsonEscVpNet, sim.Port, "c"), TestSupport.Store("c", "", tried[..12]), log, DriverTimings.Fast);
        var words = await ExerciseAsync(d);
        AssertClean(words.Concat(log.Lines), Secret[..12], tried[..12]);
    }

    [Fact]
    public async Task AGoneProjectorsWordsAndTheManagersLinesCarryNoSecret()
    {
        await using var sim = new PjLinkSimulator(password: Secret) { Dark = true };
        var log = new CollectingLog();
        var store = TestSupport.Store("c", "", Secret);
        var clock = new ManualClock();
        await using var m = new DeviceManager(new ProjectorDrivers(store, log, DriverTimings.Fast), clock, log);
        await m.SetLinksAsync([TestSupport.Link(ProjectorProtocol.PjLink, sim.Port, "c", pollMs: 1000)]);
        for (var i = 0; i < 4; i++)
        {
            clock.AdvanceMs(20_000);
            await m.PollDueAsync(CancellationToken.None);
        }
        var s = m.State("dv00000001")!;
        Assert.Equal(DeviceLiveness.Gone, s.Liveness);
        AssertClean(log.Lines.Append(s.ToString()).Append(s.Headline()).Append(s.LastProblem), Secret);
        Assert.NotEmpty(log.Lines);
    }
}
