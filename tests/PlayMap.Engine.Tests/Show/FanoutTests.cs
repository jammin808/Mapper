using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Show;

/// <summary>SHOW ARM ALL, SHOW DISARM ALL and PREFLIGHT ALL through the group's fan-out: each member runs the line through its own
/// gate as a fleet peer and its receipt comes back in the reply (applied with its leader instant, or refused with its words); a
/// group preflight's report holds every member's rows under its own name, one section per node, and a member that does not answer
/// is a red row. The group here is engines in one process behind a stand-in for the fleet's fan-out (wp2-sync's carries it over the
/// network: ArmAllTests at that merge).</summary>
public sealed class FanoutTests
{
    /// <summary>Forwards a line to member engines as a fleet peer would send it; an applied receipt's words are the member's reply.
    /// A member set silent answers nothing, and its receipt says so.</summary>
    private sealed class Group(params (string Id, string Name, EngineCore Engine)[] members) : IFleetFanout
    {
        public HashSet<string> Silent { get; } = [];

        public List<string> Sent { get; } = [];

        public IReadOnlyList<string> Members => [.. members.Select(m => m.Id)];

        public async Task<IReadOnlyList<FanoutReceipt>> SendAsync(string line, long atLeaderNs, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add(line);
            Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
            var receipts = new List<FanoutReceipt>();
            foreach (var (id, name, engine) in members)
            {
                if (Silent.Contains(id))
                {
                    receipts.Add(new FanoutReceipt(id, name, "timeout", 0, $"{name} did not answer in 2 s"));
                    continue;
                }
                var reply = await engine.HandleAsync(parsed!, new SessionInfo("fleet-lead", "fleet", "127.0.0.2", false, "Lead", true), ct);
                receipts.Add(reply.Ok
                    ? new FanoutReceipt(id, name, "applied", engine.LeaderNowNs, reply.Payload ?? reply.Effect)
                    : new FanoutReceipt(id, name, "refused", 0, reply.Payload ?? "refused"));
            }
            return receipts;
        }
    }

    private static ShowRig Member(string name) => new(services: s => s with { Settings = s.Settings with { Name = name } });

    [Fact]
    public async Task ArmAllArmsEveryMemberAndAnswersWithEachReceipt()
    {
        await using var b = Member("FOH-B");
        await using var c = Member("FOH-C");
        var group = new Group(("nb", "FOH-B", b.Engine), ("nc", "FOH-C", c.Engine));
        await using var lead = new ShowRig(services: s => s with { Settings = s.Settings with { Name = "FOH-A" }, Fanout = group });
        await c.Ok("SHOW ARM early");

        var reply = await lead.Run("SHOW ARM ALL doors");
        Assert.True(reply.Ok, reply.Payload);
        var armed = JsonDocument.Parse(reply.Payload!).RootElement;
        Assert.True(armed.GetProperty("armed").GetBoolean());
        Assert.Equal(2, armed.GetProperty("members").GetInt32());
        var nodes = armed.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(["FOH-A", "FOH-B", "FOH-C"], nodes.Select(n => n.GetProperty("name").GetString()));
        Assert.Equal(["applied", "applied", "refused"], nodes.Select(n => n.GetProperty("stage").GetString()));
        Assert.StartsWith("already armed by Desk", nodes[2].GetProperty("words").GetString(), StringComparison.Ordinal);
        Assert.Equal(b.Engine.LeaderNowNs, nodes[1].GetProperty("appliedLeaderNs").GetInt64());
        Assert.Equal(["SHOW ARM \"doors\""], group.Sent);
        Assert.True(lead.Engine.ShowNow.Armed);
        Assert.True(b.Engine.ShowNow.Armed);
        Assert.Equal(("Lead", "doors"), (b.Engine.ShowNow.By, b.Engine.ShowNow.Words));
        Assert.Contains(lead.Engine.Journal.Recent(20), r => r.Kind == "refused" && r.Text.StartsWith("SHOW ARM \"doors\" on FOH-C: refused — already armed", StringComparison.Ordinal));

        // This node already armed: ALL still brings the group to it.
        await b.Ok("SHOW DISARM");
        var again = JsonDocument.Parse(await lead.Ok("SHOW ARM ALL")).RootElement.GetProperty("nodes");
        Assert.Equal("already", again[0].GetProperty("stage").GetString());
        Assert.True(b.Engine.ShowNow.Armed);

        var off = JsonDocument.Parse(await lead.Ok("SHOW DISARM ALL")).RootElement;
        Assert.False(off.GetProperty("armed").GetBoolean());
        Assert.All(off.GetProperty("nodes").EnumerateArray(), n => Assert.Equal("applied", n.GetProperty("stage").GetString()));
        Assert.False(b.Engine.ShowNow.Armed || c.Engine.ShowNow.Armed || lead.Engine.ShowNow.Armed);
    }

    [Fact]
    public async Task PreflightAllListsOneSectionPerNodeAndASilentMemberIsRed()
    {
        await using var b = Member("FOH-B");
        await using var c = Member("FOH-C");
        var group = new Group(("nb", "FOH-B", b.Engine), ("nc", "FOH-C", c.Engine));
        await using var lead = new ShowRig(services: s => s with { Settings = s.Settings with { Name = "FOH-A" }, Fanout = group });
        await b.Ok("SHOW SET MINGO 0");
        group.Silent.Add("nc");

        var done = new TaskCompletionSource<PreflightReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        lead.Engine.PreflightDone += r => done.TrySetResult(r);
        var accepted = await lead.Run("PREFLIGHT ALL");
        Assert.True(accepted.Ok && !accepted.Applied, accepted.Payload);
        var id = JsonDocument.Parse(accepted.Payload!).RootElement.GetProperty("id").GetString();
        var report = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(id, report.Id);
        Assert.Equal(["PREFLIGHT QUICK WAIT"], group.Sent);

        // Every row names its node: the leader's own, the member's own rows from its report, and the silent member's red row.
        Assert.Equal(["FOH-A", "FOH-B", "FOH-C"], report.Rows.Select(r => r.Node).Distinct());
        Assert.Contains(report.Rows, r => r.Node == "FOH-B" && r.Id == "safety:mingo" && r.Light == Core.Bus.Light.Amber);
        Assert.Contains(report.Rows, r => r.Node == "FOH-B" && r.Id == "fleet:nb" && r.Light == Core.Bus.Light.Green);
        var silent = report.Rows.Single(r => r.Id == "fleet:nc");
        Assert.Equal((Core.Bus.Light.Red, "timeout", "FOH-C did not answer in 2 s"), (silent.Light, silent.Value, silent.Note));
        Assert.False(report.Complete);
        Assert.Contains("on 3 nodes", lead.Engine.Journal.Recent(20).Single(r => r.Kind == "preflight").Text, StringComparison.Ordinal);

        var json = JsonDocument.Parse(await lead.Ok($"PREFLIGHT REPORT {id}")).RootElement;
        var nodes = json.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(["FOH-A", "FOH-B", "FOH-C"], nodes.Select(n => n.GetProperty("node").GetString()));
        Assert.Equal("red", nodes[2].GetProperty("light").GetString());
        Assert.Equal(1, nodes[2].GetProperty("red").GetInt32());
        // The member ran its own preflight and keeps its report.
        Assert.Equal(JsonValueKind.Object, b.Show().GetProperty("preflight").ValueKind);
    }

    [Fact]
    public async Task WaitAnswersWithTheFinishedReport()
    {
        await using var rig = new ShowRig();
        var report = PreflightReports.Read(await rig.Ok("PREFLIGHT QUICK WAIT"))!;
        Assert.True(report.Complete);
        Assert.Contains(report.Rows, r => r.Section == "safety");
        Assert.Equal(report.Id, rig.Show().GetProperty("preflight").GetProperty("id").GetString());
        Assert.StartsWith("PREFLIGHT [QUICK] [ALL|WAIT]", await rig.Refused("PREFLIGHT ALL WAIT"), StringComparison.Ordinal);
    }
}
