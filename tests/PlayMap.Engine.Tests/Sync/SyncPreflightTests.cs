using System.Text.Json;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>Preflight's sync rows from facts, by injecting the fault: a follower whose leader stops is red on "Sync leader" with the
/// line that fixes it (a catalogue line); once the leader is back the fix line brings it back, and the row is green again. A
/// leader's mixed rate is red with the line that sets its own odd output's rate; a solo node is grey.</summary>
public sealed class SyncPreflightTests
{
    private const long Second = SyncRig.Second;

    private static async Task<JsonElement> PreflightAsync(HostRig rig)
    {
        var id = JsonDocument.Parse(await rig.OkAsync("PREFLIGHT")).RootElement.GetProperty("id").GetString()!;
        JsonElement report = default;
        Assert.True(await Eventually.TrueAsync(async () =>
        {
            var r = await rig.RunAsync("PREFLIGHT REPORT " + id);
            if (!r.Ok)
                return false;
            report = JsonDocument.Parse(r.Payload!).RootElement.Clone();
            return true;
        }));
        return report;
    }

    private static JsonElement Row(JsonElement report, string id) =>
        report.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);

    [Fact]
    public async Task AFollowerWhoseLeaderStopsIsRedWithTheLineThatFixesItAndGreenOnceItRuns()
    {
        var clock = new ManualClock(1_000 * Second);
        await using var solo = await SyncRig.StartAsync("Spare", "1a2b3c4d", clock);
        Assert.Equal("grey", Row(await PreflightAsync(solo), "sync:none").GetProperty("light").GetString());

        var first = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, 5 * Second));
        await using var follower = await SyncRig.StartAsync("Stage", SyncRig.FollowerId, clock);
        await using (first)
        {
            await SyncRig.GroupAsync(first, follower);
            var ok = await PreflightAsync(follower);
            Assert.Equal("green", Row(ok, "sync:leader").GetProperty("light").GetString());
            Assert.Equal("green", Row(ok, "sync:clock").GetProperty("light").GetString());
            Assert.Equal("green", Row(ok, "sync:feed").GetProperty("light").GetString());

            // The fault: the leader stops, the follower free-wheels.
            await first.StopAsync();
            clock.AdvanceMs(4_000);
            Assert.True(await Eventually.TrueAsync(() => follower.Journal().Contains("went quiet", StringComparison.Ordinal)));
            var bad = Row(await PreflightAsync(follower), "sync:leader");
            Assert.Equal("red", bad.GetProperty("light").GetString());
            var fix = bad.GetProperty("fix").GetString()!;
            Assert.Equal("SYNC FOLLOW show " + SyncRig.LeaderId, fix);
            Assert.True(WireGrammar.TryParse(fix, out var line, out _));
            Assert.Equal("SYNC FOLLOW", VerbCatalog.Match(line!)!.Value.Spec.Name);

            // The leader is back (it leads again from its settings, on a new port): the fix line follows it afresh.
            await using var back = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, new OffsetClock(clock, 5 * Second), dir: first.Dir);
            HostRig.Introduce(back, follower);
            Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(back).GetProperty("role").GetString() == "leader"), back.Journal());
            Assert.True(await Eventually.TrueAsync(() => follower.Network.Hello!.Peers.Find(back.Id)?.Hello.Port("sync") == back.Network.SyncPort));
            await follower.OkAsync(fix);
            await SyncRig.CaughtUpAsync(back, follower);
            Assert.True(await Eventually.TrueAsync(() => !follower.Engine.Services.Clock.IsHolding));
            Assert.Equal("green", Row(await PreflightAsync(follower), "sync:leader").GetProperty("light").GetString());
        }
    }

    [Fact]
    public async Task ALeadersOwnOddRateIsRedWithTheLineThatSetsIt()
    {
        var clock = new ManualClock(1_000 * Second);
        var leaderV = new FakeVblanks(FakeVblanks.At("op_1", 60, clock.NowNs), FakeVblanks.At("op_2", 50, clock.NowNs));
        await using var leader = await SyncRig.StartAsync("FOH", SyncRig.LeaderId, clock, SyncRig.Fast(leaderV));
        var show = SyncRig.Show();
        await SyncRig.OpenShowAsync(leader, show with { Outputs = [show.Outputs[0], show.Outputs[0] with { Id = "op_2", Name = "Right", RefreshHz = 50 }] });
        await leader.OkAsync("SYNC LEAD show");
        Assert.True(await Eventually.TrueAsync(() => SyncRig.Sync(leader).GetProperty("verdict").GetString() == "mixed rate"));
        var row = Row(await PreflightAsync(leader), "sync:rates");
        Assert.Equal("red", row.GetProperty("light").GetString());
        Assert.Equal("SET op_2 refreshHz 60", row.GetProperty("fix").GetString());
        await leader.OkAsync(row.GetProperty("fix").GetString()!);
        Assert.Equal(60, leader.Engine.Store.Project.FindOutput("op_2")!.RefreshHz);
    }
}
