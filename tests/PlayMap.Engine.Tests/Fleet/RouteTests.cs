using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PlayMap.Core.Time;
using PlayMap.Devices.Tests.Simulators;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Sync;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>What a takeover switches so the audience sees the backup, against the Devices module's PJLink projector simulator: FLEET
/// ROUTE on the primary keeps the route in the show (the backup has it by the mirror); TEST reads the projector's input (INPT ?) and
/// switches nothing; FAILOVER on the backup sends INPT 32 through the backup's own DEVICE INPUT and reads it back (observed); a route
/// whose projector does not answer makes FAILOVER go on and say so.</summary>
public sealed class RouteTests
{
    private const long Second = SyncRig.Second;

    private static int ClosedPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Words(Core.Wire.CommandReply reply)
    {
        Assert.True(reply.Ok, reply.Payload);
        return JsonDocument.Parse(reply.Payload!).RootElement.EnumerateArray().Single().GetProperty("words").GetString()!;
    }

    [Fact]
    public async Task RouteTestReadsTheInputAndFailoverSwitchesItObserved()
    {
        await using var projector = new PjLinkSimulator();
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, clock, time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        await SyncRig.OpenShowAsync(primary);
        var dv = await primary.OkAsync($"DEVICE ADD P1 pjlink 127.0.0.1:{projector.Port.ToString(CultureInfo.InvariantCulture)}");

        Assert.Contains("no routes", (await primary.RunAsync("FLEET ROUTE TEST")).Payload, StringComparison.Ordinal);
        Assert.Contains("both 31", (await primary.RunAsync($"FLEET ROUTE op_1 DEVICE {dv} 31 31")).Payload, StringComparison.Ordinal);
        var route = await primary.RunAsync($"FLEET ROUTE op_1 DEVICE {dv} 31 32");
        Assert.True(route.Ok, route.Payload);
        Assert.Contains("is that already", (await primary.RunAsync($"FLEET ROUTE op_1 DEVICE {dv} 31 32")).Payload, StringComparison.Ordinal);
        var kept = Assert.Single(primary.Engine.Store.Project.Fleet.Routes);
        Assert.Equal(("op_1", dv, "31", "32", primary.Id), (kept.OutputId, kept.DeviceId, kept.PrimaryInput, kept.BackupInput, kept.PrimaryNode));

        // TEST reads the projector's input now and switches nothing.
        var test = await primary.RunAsync("FLEET ROUTE TEST");
        Assert.True(test.Ok, test.Payload);
        var row = JsonDocument.Parse(test.Payload!).RootElement.EnumerateArray().Single();
        Assert.Equal("P1 on 31 (primary) · reachable", row.GetProperty("words").GetString());
        Assert.Equal("31", projector.Input);
        Assert.Equal("P1 on 31 (primary) · reachable", BackupRig.Fleet(primary).GetProperty("routeTest").GetProperty("words").GetString());

        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        await BackupRig.BackUpAsync(primary, backup);
        Assert.Equal(kept, Assert.Single(backup.Engine.Store.Project.Fleet.Routes));
        // A route is the show's plan: edited where the show is, not on its backup.
        Assert.Contains("FOH-B mirrors FOH-A", (await backup.RunAsync("FLEET ROUTE op_1 NONE")).Payload, StringComparison.Ordinal);

        var takeover = await backup.RunAsync("FAILOVER");
        Assert.True(takeover.Ok, takeover.Payload);
        Assert.Contains("P1 32 observed", takeover.Effect, StringComparison.Ordinal);
        Assert.Equal("32", projector.Input);
        Assert.Equal("P1 on 32 (backup) · reachable", Words(await backup.RunAsync("FLEET ROUTE TEST")));
    }

    [Fact]
    public async Task AnUnreachableRouteMakesFailoverGoOnAndSaySo()
    {
        var clock = new ManualClock(1_000 * Second);
        var time = new ManualTime();
        await using var primary = await BackupRig.StartAsync("FOH-A", BackupRig.PrimaryId, clock, time);
        await using var backup = await BackupRig.StartAsync("FOH-B", BackupRig.BackupId, clock, time);
        await SyncRig.OpenShowAsync(primary);
        var dv = await primary.OkAsync($"DEVICE ADD P1 pjlink 127.0.0.1:{ClosedPort().ToString(CultureInfo.InvariantCulture)}");
        await primary.OkAsync($"FLEET ROUTE op_1 DEVICE {dv} 31 32");
        Assert.StartsWith("P1 · unreachable", Words(await primary.RunAsync("FLEET ROUTE TEST")), StringComparison.Ordinal);
        await primary.OkAsync("SYNC LEAD show");
        await BackupRig.MeetAsync(primary, backup);
        await BackupRig.BackUpAsync(primary, backup);

        var takeover = await backup.RunAsync("FAILOVER");
        Assert.True(takeover.Ok, takeover.Payload);
        Assert.Contains("P1 32 unreached", takeover.Effect, StringComparison.Ordinal);
        Assert.Contains("switch it by hand", takeover.Effect, StringComparison.Ordinal);
        Assert.Equal("primary", BackupRig.Role(backup));
    }
}
