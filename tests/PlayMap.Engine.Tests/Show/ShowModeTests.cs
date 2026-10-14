using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Core.Model;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Show;

/// <summary>Show Mode through the engine: ARM restricts every caller and survives a restart through the recovery record; edits
/// are refused from the desk, the wire, Companion, MCP and the assistant while transport, BLACKOUT and PANIC still run; DISARM
/// follows pairing and the DISARM-FROM rule; an assistant's armed line waits on this node's list for a person; STATE, the
/// context, the headline and the events say so.</summary>
public sealed class ShowModeTests
{
    [Fact]
    public async Task ArmedSurvivesARestartThroughTheRecoveryRecord()
    {
        RecoveryRecord record;
        await using (var before = new ShowRig())
        {
            var armed = JsonDocument.Parse(await before.Ok("SHOW ARM doors at eight")).RootElement;
            Assert.True(armed.GetProperty("armed").GetBoolean());
            Assert.Equal("Desk", armed.GetProperty("by").GetString());
            // Words the verb does not take are refused, not ignored.
            Assert.True(JsonDocument.Parse(await before.Ok("SHOW STATUS")).RootElement.GetProperty("armed").GetBoolean());
            Assert.Equal("SHOW [STATUS]", await before.Refused("SHOW ARMED"));
            Assert.Equal("LOCKS takes no words", await before.Refused("LOCKS mapping"));
            Assert.True(before.Show().GetProperty("armed").GetBoolean());
            record = await before.Engine.Lane.RunAsync(() => RecoveryRecord.Capture(before.Engine));
        }
        Assert.Contains("\"armed\":true", record.Modules["show"], StringComparison.Ordinal);

        await using var after = new ShowRig(Project.CreateDefault("Empty"));
        Assert.False(after.Show().GetProperty("armed").GetBoolean());
        await after.Engine.RestoreAsync(record);
        var show = after.Show();
        Assert.True(show.GetProperty("armed").GetBoolean());
        Assert.Equal("Desk", show.GetProperty("by").GetString());
        Assert.Equal("doors at eight", show.GetProperty("words").GetString());
        Assert.Equal(300, show.GetProperty("minGoMs").GetInt32());
        Assert.True(after.Engine.ShowGate.Armed);
        Assert.False((await after.Run("SET op_1 width 1280")).Ok);
    }

    [Fact]
    public async Task ArmedEditsAreRefusedFromEveryCallerWhileTheShowRuns()
    {
        await using var rig = new ShowRig();
        await rig.Ok("SHOW ARM");
        var revision = rig.Engine.Store.Current.Revision;
        foreach (var who in new[] { ShowRig.Desk, ShowRig.Remote, ShowRig.Companion, ShowRig.Mcp, ShowRig.Assistant })
        {
            foreach (var edit in new[] { "SET op_1 width 1280", "WARP NODE op_1 1 1 0.01 0", "PLAYLIST REMOVE pl_show it_c" })
            {
                var words = await rig.Refused(edit, who);
                // A person's session is refused with the rule and the way out; an assistant's line waits for a person instead.
                if (who.Transport is "mcp" or "assistant")
                    Assert.Contains(" waits for a person while the show is armed", words, StringComparison.Ordinal);
                else
                    Assert.StartsWith("Not while armed: ", words, StringComparison.Ordinal);
            }
        }
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Equal(1920, rig.Engine.Store.Project.Outputs[0].Width);

        // The show runs: transport, blackout and PANIC from the desk and a remote.
        await rig.Ok("PLAYLIST GO pl_show");
        rig.Advance(1000);
        await rig.Ok("PLAYLIST NEXT pl_show", ShowRig.Remote);
        await rig.Ok("BLACKOUT ON", ShowRig.Remote);
        await rig.Ok("BLACKOUT OFF");
        await rig.Ok("PANIC FADE 0", ShowRig.Companion);
        Assert.Equal("it_b", rig.Engine.Playback.Lane("pl_show")!.ItemId);

        var refused = rig.Show().GetProperty("refused");
        Assert.Equal(15, refused.GetProperty("last60s").GetInt32());
        Assert.Contains("PLAYLIST REMOVE", refused.GetProperty("last").GetString(), StringComparison.Ordinal);
        Assert.Contains(rig.Engine.Journal.Recent(50), r => r.Kind == "refused" && r.Line == "SET op_1 width 1280" && r.Who == "Companion: FOH deck");
    }

    [Fact]
    public async Task DisarmFollowsPairingAndTheDisarmFromRule()
    {
        await using var rig = new ShowRig();
        Assert.Equal("not armed — the show is in rehearsal", await rig.Refused("SHOW DISARM"));
        await rig.Ok("SHOW SET DISARM-FROM DESK");
        await rig.Ok("SHOW ARM", ShowRig.Remote);
        Assert.Equal("Remote desk", rig.Show().GetProperty("by").GetString());
        Assert.StartsWith("already armed by Remote desk", await rig.Refused("SHOW ARM"), StringComparison.Ordinal);
        Assert.Equal("no fleet fan-out on this node — SHOW ARM arms this node only", await rig.Refused("SHOW ARM ALL"));

        Assert.StartsWith("not paired", await rig.Refused("SHOW DISARM", ShowRig.Unpaired), StringComparison.Ordinal);
        var remote = await rig.Refused("SHOW DISARM", ShowRig.Remote);
        Assert.StartsWith("Not while armed: SHOW DISARM is the desk's", remote, StringComparison.Ordinal);
        Assert.True(rig.Show().GetProperty("armed").GetBoolean());

        // A show rule is a document edit (one undo step) the desk may still change armed.
        await rig.Ok("SHOW SET MINGO 500");
        Assert.Equal(500, rig.Show().GetProperty("rules").GetProperty("minGoMs").GetInt32());
        Assert.Equal(500, rig.Show().GetProperty("minGoMs").GetInt32());
        Assert.Equal("already so: double-GO protection 500 ms armed", await rig.Refused("SHOW SET MINGO 500"));
        Assert.Equal("COMMIT-LEAD times blind commits, and this node has no blind editing yet", await rig.Refused("SHOW SET COMMIT-LEAD 40"));

        var off = JsonDocument.Parse(await rig.Ok("SHOW DISARM")).RootElement;
        Assert.False(off.GetProperty("armed").GetBoolean());
        Assert.False(rig.Show().GetProperty("armed").GetBoolean());
        await rig.Ok("UNDO");
        Assert.Equal(300, rig.Show().GetProperty("rules").GetProperty("minGoMs").GetInt32());
        Assert.Equal(0, rig.Show().GetProperty("minGoMs").GetInt32());
        await rig.Ok("SET op_1 width 1280", ShowRig.Remote);
    }

    [Fact]
    public async Task StateTheContextTheHeadlineAndTheEventsSayArmed()
    {
        await using var rig = new ShowRig();
        var events = new List<ShowEvent>();
        rig.Engine.EventRaised += e =>
        {
            lock (events)
                events.Add(e);
        };
        var revision = rig.Engine.State().Revision;
        var rehearsal = rig.Context();
        Assert.Equal("show", rehearsal.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal("grey", rehearsal.GetProperty("items")[0].GetProperty("light").GetString());

        await rig.Ok("SHOW ARM");
        Assert.NotEqual(revision, rig.Engine.State().Revision);
        var ctx = rig.Context();
        var item = ctx.GetProperty("items")[0];
        Assert.Equal(("show", "green"), (item.GetProperty("kind").GetString(), item.GetProperty("light").GetString()));
        Assert.StartsWith("ARMED by Desk since ", item.GetProperty("state").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("ARMED · ", ctx.GetProperty("headline").GetString(), StringComparison.Ordinal);
        Assert.Contains(ctx.GetProperty("links").EnumerateArray(), l =>
            l.GetProperty("from").GetString() == "show" && l.GetProperty("to").GetString() == "output:op_1" && l.GetProperty("kind").GetString() == "controls");
        Assert.Contains(rig.Show().GetProperty("locks").EnumerateArray(), l => l.GetProperty("target").GetString() == "mapping" && l.GetProperty("implicit").GetBoolean());

        await rig.Refused("SET op_1 width 1280", ShowRig.Remote);
        await rig.Ok("SHOW DISARM");
        ShowEvent[] seen;
        lock (events)
            seen = [.. events];
        Assert.Equal(["show.armed", "refused", "show.disarmed"], seen.Select(e => e.Kind).ToArray());
        Assert.Equal("{\"by\":\"Desk\"}", seen[0].FieldsJson);
    }

    [Fact]
    public async Task ARefusalLeavesTheLastMinuteAndStateMovesWithIt()
    {
        await using var rig = new ShowRig();
        await rig.Ok("SHOW ARM");
        await rig.Refused("SET op_1 width 1280", ShowRig.Remote);
        Assert.Equal(1, rig.Show().GetProperty("refused").GetProperty("last60s").GetInt32());
        await rig.Ticked();
        var revision = rig.Engine.State().Revision;
        rig.Advance(60_001);
        // The count leaves the window on the lane's tick, and STATE's revision moves so a subscriber hears it.
        Assert.True(await ShowRig.Eventually(() => rig.Engine.State().Revision != revision));
        var refused = rig.Show().GetProperty("refused");
        Assert.Equal(0, refused.GetProperty("last60s").GetInt32());
        Assert.StartsWith("Remote desk — SET op_1 width 1280 — Not while armed: ", refused.GetProperty("last").GetString(), StringComparison.Ordinal);
    }

    /// <summary>A node linked to Patterns and Stagewatch, both live.</summary>
    private sealed class Linked : INetworkView
    {
        public IReadOnlyList<PeerView> Peers() => [];

        public LinkView? Patterns() => new("live", "10.0.0.2:4000", true, "");

        public LinkView? Stagewatch() => new("live", "10.0.0.3:4100", true, "");

        public string? StagewatchTimerJson() => null;

        public SyncView Sync() => new("solo", null, 0, 0, 0);
    }

    [Fact]
    public async Task TheIndexHoldsTheShowsKeysAndPatternsControlsTheShowNotTheAir()
    {
        await using var rig = new ShowRig(services: s => s with { Network = new Linked() });
        await rig.Ok("PANIC SET op_1 SAFE SAFE color:#000000");
        var section = Assert.Single(rig.Engine.Routes.IndexSections, x => x.Section == "show");
        Assert.Equal("$(id)", Assert.Single(section.Hint.Keys).DownLine);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
            section.WriteRows(rig.Engine.Store.Project, rig.Engine.Snapshot, w);
        var rows = JsonDocument.Parse(buffer.ToArray()).RootElement.EnumerateArray().ToList();
        var ids = rows.Select(r => r.GetProperty("id").GetString()!).ToList();
        foreach (var id in new[] { "SHOW ARM", "SHOW DISARM", "PANIC", "PANIC BLACK", "PANIC SAFE", "PANIC HARD", "PANIC CLEAR", "MASTER 0", "MASTER 100", "MASTER" })
            Assert.Contains(id, ids);
        foreach (var id in ids)
        {
            Assert.True(WireGrammar.TryParse(id, out var line, out _));
            Assert.NotNull(VerbCatalog.Match(line!));
        }
        Assert.Equal("encoder", rows.Single(r => r.GetProperty("id").GetString() == "MASTER").GetProperty("kind").GetString());

        var links = rig.Context().GetProperty("links").EnumerateArray()
            .Select(l => (l.GetProperty("from").GetString(), l.GetProperty("to").GetString(), l.GetProperty("kind").GetString())).ToList();
        Assert.Contains(("link:patterns", "show", "controls"), links);
        Assert.Contains(("link:stagewatch", "show", "feeds"), links);
        Assert.DoesNotContain(links, l => l.Item2 == "air");
    }

    [Fact]
    public async Task AnAssistantsArmedLineWaitsOnThisNodesListForAPerson()
    {
        var rig = new OpsRig(project: ShowRig.OneScreen());
        try
        {
            await rig.Ok("SHOW ARM");
            var words = (await rig.Run("SET op_1 name \"Stage\"", OpsRig.Mcp)).Payload!;
            Assert.StartsWith("SET waits for a person while the show is armed", words, StringComparison.Ordinal);
            Assert.Contains("queued for a person as proposal ap", words, StringComparison.Ordinal);
            var proposal = Assert.Single(rig.Assistant.Queue.Pending);
            Assert.StartsWith("SET op_1 name", proposal.Line, StringComparison.Ordinal);
            Assert.Equal("Output 1", rig.Engine.Store.Project.Outputs[0].Name);

            // Approved while armed, the line gets what a remote gets: a layout edit is still refused, and says so.
            var approved = await rig.Assistant.Queue.ApproveAsync(proposal.Id, "Desk", CancellationToken.None);
            Assert.Equal(ProposalState.Failed, approved.State);
            Assert.Contains("Not while armed: SET", approved.ResultWords, StringComparison.Ordinal);
            Assert.Equal("Output 1", rig.Engine.Store.Project.Outputs[0].Name);

            // The gate the parts read through the services is the engine's own.
            Assert.True(rig.Engine.Services.ShowGate.Armed);
            Assert.Equal(ShowVerdict.Propose, rig.Engine.Services.ShowGate.Decide(VerbCatalog.Find("PANIC")!,
                new WireLine(null, ["PANIC"], "PANIC"), OpsRig.Mcp).Verdict);
            Assert.Contains("PANIC", rig.Engine.ShowGate.Allowed(OpsRig.Desk)!);
            Assert.DoesNotContain("WARP NODE", rig.Engine.ShowGate.Allowed(OpsRig.Desk)!);
            await rig.Ok("SHOW DISARM");
            Assert.Null(rig.Engine.ShowGate.Allowed(OpsRig.Desk));
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
