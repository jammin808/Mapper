using System.Text.Json;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Tests.Show;

/// <summary>Blind edit on a manual show clock: a draft session's edits stay off the air (PROJECT GET, STATE and the snapshot's
/// project keep the live root) and BLIND DIFF names them field by field; BLIND COMMIT lands the draft as one undo step at its
/// boundary — a commit lead ahead, at the playlist's next switch, or on the next GO's own instant — never before; a live change to
/// an entity the draft also changed refuses the commit naming it until FORCE; DISCARD drops it; while a commit waits, a live edit
/// to what it lands is refused and any other live edit is carried into it.</summary>
public sealed class BlindTests
{
    private static string Background(ShowRig rig) =>
        JsonDocument.Parse(rig.Engine.ProjectJson().Json).RootElement.GetProperty("canvases")[0].GetProperty("background").GetString()!;

    private static JsonElement Blind(ShowRig rig) => rig.Show().GetProperty("blind");

    [Fact]
    public async Task ADraftSessionsEditsStayOffTheAirAndTheDiffNamesThemInWords()
    {
        await using var rig = new ShowRig();
        var revision = rig.Engine.Store.Current.Revision;
        Assert.Equal(JsonValueKind.Null, Blind(rig).ValueKind);
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        await rig.Ok("SET op_1 name \"Left\"");

        // The air, PROJECT GET and the live store did not move; the draft holds both edits.
        Assert.Equal("#000000", Background(rig));
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Equal("#000000", rig.Engine.Snapshot.Project.Project.Canvases[0].Background);
        Assert.Equal("#FF0000", rig.Engine.Snapshot.Draft!.Project.Canvases[0].Background);
        Assert.Null(rig.Engine.Snapshot.Pending);
        var blind = Blind(rig);
        Assert.True(blind.GetProperty("on").GetBoolean());
        Assert.Equal("Desk", blind.GetProperty("owner").GetString());
        Assert.Equal(2, blind.GetProperty("changes").GetInt32());
        Assert.Equal(JsonValueKind.Null, blind.GetProperty("commit").ValueKind);

        var diff = JsonDocument.Parse(await rig.Ok("BLIND DIFF")).RootElement;
        Assert.Equal(revision, diff.GetProperty("base").GetInt64());
        var changes = diff.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(["cv_main", "op_1"], changes.Select(c => c.GetProperty("id").GetString()));
        var canvas = changes[0];
        Assert.Equal("canvas", canvas.GetProperty("kind").GetString());
        Assert.Equal("changed", canvas.GetProperty("op").GetString());
        var field = Assert.Single(canvas.GetProperty("fields").EnumerateArray());
        Assert.Equal("background", field.GetProperty("field").GetString());
        Assert.Equal("\"#000000\"", field.GetProperty("from").GetString());
        Assert.Equal("\"#FF0000\"", field.GetProperty("to").GetString());
        Assert.Equal("Main canvas (canvas): background \"#000000\" → \"#FF0000\"", Assert.Single(canvas.GetProperty("words").EnumerateArray()).GetString());
        Assert.Equal("Left (output): name \"Output 1\" → \"Left\"", changes[1].GetProperty("words")[0].GetString());
        Assert.Empty(diff.GetProperty("conflicts").EnumerateArray());

        // Another session edits live (rehearsal): the draft is the owner's only.
        await rig.Ok("SET gn_testcard name \"Card\"", ShowRig.Remote);
        Assert.Equal(revision + 1, rig.Engine.Store.Current.Revision);
        Assert.Equal("Card", rig.Engine.Store.Project.Generators[0].Name);
        Assert.Equal("Test card", rig.Engine.Snapshot.Draft!.Project.Generators[0].Name);

        // The owner's UNDO is the draft's own; BLIND OFF refuses while it holds changes.
        await rig.Ok("UNDO");
        Assert.Equal("Output 1", rig.Engine.Snapshot.Draft!.Project.Outputs[0].Name);
        Assert.Equal("Card", rig.Engine.Store.Project.Generators[0].Name);
        Assert.Equal("1 change staged — BLIND COMMIT or BLIND DISCARD", await rig.Refused("BLIND OFF"));
    }

    [Fact]
    public async Task ACommitLandsACommitLeadAheadAsOneUndoStep()
    {
        await using var rig = new ShowRig();
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        await rig.Ok("SET op_1 name \"Left\"");
        var before = rig.Engine.Store.Current.Revision;
        var t = rig.Now;
        var reply = await rig.Run("BLIND COMMIT");
        Assert.True(reply.Ok && !reply.Applied, reply.Payload);
        var commit = JsonDocument.Parse(reply.Payload!).RootElement;
        Assert.Equal("now", commit.GetProperty("boundary").GetString());
        Assert.Equal(t + 34_000_000, commit.GetProperty("effectiveLeaderNs").GetInt64());
        Assert.Equal(t + 34_000_000, rig.Engine.Snapshot.Pending!.EffectiveLeaderNs);
        Assert.Equal("#FF0000", rig.Engine.Snapshot.Pending!.Root.Canvases[0].Background);
        // The draft takes no more edits while it lands.
        Assert.StartsWith("the blind draft lands at ", await rig.Refused("SET cv_main background \"#00FF00\""), StringComparison.Ordinal);

        rig.Advance(33);
        await rig.Ticked();
        Assert.Equal("#000000", Background(rig));
        rig.Advance(1);
        await rig.Ticked();
        Assert.Equal("#FF0000", Background(rig));
        Assert.Equal("Left", rig.Engine.Store.Project.Outputs[0].Name);
        Assert.Equal(before + 1, rig.Engine.Store.Current.Revision);
        Assert.Equal("blind commit: 2 changes", rig.Engine.Store.UndoLabel);
        Assert.Equal(JsonValueKind.Null, Blind(rig).ValueKind);
        Assert.Null(rig.Engine.Snapshot.Draft);
        Assert.Null(rig.Engine.Snapshot.Pending);

        // One undo step takes the whole draft back.
        await rig.Ok("UNDO");
        Assert.Equal("#000000", Background(rig));
        Assert.Equal("Output 1", rig.Engine.Store.Project.Outputs[0].Name);
    }

    [Fact]
    public async Task CommitNextLandsAtThePlaylistsSwitchInstantNotBefore()
    {
        await using var rig = new ShowRig();
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        // Nothing ends on an idle playlist: NEXT says what to use instead.
        Assert.Equal("nothing ends on Show — use ON GO or AT", await rig.Refused("BLIND COMMIT NEXT pl_show"));
        var start = rig.Now;
        await rig.Ok("PLAYLIST GO pl_show");
        rig.Advance(6_000);
        var switchAt = rig.Engine.Playback.Lane("pl_show")!.SwitchAtLeaderNs;
        Assert.Equal(start + 10_000_000_000, switchAt);
        var commit = JsonDocument.Parse(await rig.Ok("BLIND COMMIT NEXT pl_show")).RootElement;
        Assert.Equal("next", commit.GetProperty("boundary").GetString());
        Assert.Equal("pl_show", commit.GetProperty("playlist").GetString());
        Assert.Equal(switchAt, commit.GetProperty("effectiveLeaderNs").GetInt64());
        Assert.Equal("next", Blind(rig).GetProperty("commit").GetProperty("boundary").GetString());

        rig.Clock.NowNs = switchAt - 1_000_000;
        await rig.Ticked();
        Assert.Equal("#000000", Background(rig));
        rig.Clock.NowNs = switchAt;
        await rig.Ticked();
        Assert.Equal("#FF0000", Background(rig));
        Assert.Equal("it_b", rig.Engine.Playback.Lane("pl_show")!.ItemId);
        Assert.Contains(rig.Engine.Journal.Recent(20), r => r.Text == "blind commit: 1 change landed (next) by Desk");
    }

    [Fact]
    public async Task CommitOnGoLandsOnTheGosOwnInstant()
    {
        await using var rig = new ShowRig();
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        var waiting = JsonDocument.Parse(await rig.Ok("BLIND COMMIT ON GO pl_show")).RootElement;
        Assert.Equal("on go", waiting.GetProperty("boundary").GetString());
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("effectiveLeaderNs").ValueKind);
        Assert.Equal(long.MaxValue, rig.Engine.Snapshot.Pending!.EffectiveLeaderNs);
        rig.Advance(5_000);
        await rig.Ticked();
        Assert.Equal("#000000", Background(rig));

        var go = rig.Now + 200_000_000;
        await rig.Ok("PLAYLIST GO pl_show AT +200");
        Assert.Equal(go, rig.Engine.Snapshot.Pending!.EffectiveLeaderNs);
        Assert.Equal(go, Blind(rig).GetProperty("commit").GetProperty("effectiveLeaderNs").GetInt64());
        // The item and the draft take the air on the same leader instant.
        Assert.Equal("it_a", rig.Engine.Playback.Lane("pl_show")!.At(go).ItemId);
        rig.Clock.NowNs = go - 1;
        await rig.Ticked();
        Assert.Equal("#000000", Background(rig));
        rig.Clock.NowNs = go;
        await rig.Ticked();
        Assert.Equal("#FF0000", Background(rig));
    }

    [Fact]
    public async Task ALiveChangeToADraftedEntityRefusesTheCommitNamingItUntilForced()
    {
        await using var rig = new ShowRig();
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        await rig.Ok("SET cv_main name \"Stage\"", ShowRig.Remote);
        var diff = JsonDocument.Parse(await rig.Ok("BLIND DIFF")).RootElement;
        Assert.Equal("cv_main", Assert.Single(diff.GetProperty("conflicts").EnumerateArray()).GetString());
        Assert.Equal("changed on the air since BLIND ON: Main canvas (canvas, cv_main) — BLIND DIFF shows the draft; FORCE lands the draft's version",
            await rig.Refused("BLIND COMMIT"));
        Assert.Null(rig.Engine.Snapshot.Pending);

        var forced = JsonDocument.Parse(await rig.Ok("BLIND COMMIT NOW FORCE")).RootElement;
        Assert.True(forced.GetProperty("forced").GetBoolean());
        rig.Advance(34);
        await rig.Ticked();
        Assert.Equal("#FF0000", Background(rig));
        // FORCE lands the draft's whole version of the entity: the live rename is overwritten, as the refusal said.
        Assert.Equal("Main canvas", rig.Engine.Store.Project.Canvases[0].Name);
    }

    [Fact]
    public async Task WhileACommitWaitsLiveEditsToWhatItLandsAreRefusedAndOthersAreCarriedIn()
    {
        await using var rig = new ShowRig();
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        await rig.Ok("BLIND COMMIT AT +500");
        var refused = await rig.Refused("SET cv_main name \"Stage\"", ShowRig.Remote);
        Assert.StartsWith("a blind commit lands Main canvas (canvas) at ", refused, StringComparison.Ordinal);
        Assert.EndsWith(" — wait for it, or BLIND DISCARD", refused, StringComparison.Ordinal);

        await rig.Ok("SET gn_testcard name \"Card\"", ShowRig.Remote);
        Assert.Equal("Card", rig.Engine.Snapshot.Pending!.Root.Generators[0].Name);
        Assert.Equal("#FF0000", rig.Engine.Snapshot.Pending!.Root.Canvases[0].Background);
        rig.Advance(500);
        await rig.Ticked();
        Assert.Equal("#FF0000", Background(rig));
        Assert.Equal("Card", rig.Engine.Store.Project.Generators[0].Name);
    }

    [Fact]
    public async Task DiscardDropsTheDraftAndAWaitingCommit()
    {
        await using var rig = new ShowRig();
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        await rig.Ok("BLIND COMMIT ON GO");
        var discarded = JsonDocument.Parse(await rig.Ok("BLIND DISCARD")).RootElement;
        Assert.Equal(1, discarded.GetProperty("discarded").GetInt32());
        Assert.True(discarded.GetProperty("commitCancelled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Blind(rig).ValueKind);
        Assert.Null(rig.Engine.Snapshot.Draft);
        Assert.Null(rig.Engine.Snapshot.Pending);
        await rig.Ok("PLAYLIST GO pl_show");
        rig.Advance(100);
        await rig.Ticked();
        Assert.Equal("#000000", Background(rig));
        // Edits are live again.
        await rig.Ok("SET cv_main background \"#0000FF\"");
        Assert.Equal("#0000FF", Background(rig));
        Assert.Equal("not blind — nothing to discard", await rig.Refused("BLIND DISCARD"));
    }

    [Fact]
    public async Task ArmedTheDeskStagesMappingBlindAndTheCommitLandsIt()
    {
        await using var rig = new ShowRig();
        await rig.Ok("SHOW ARM");
        Assert.StartsWith("Not while armed: SET", await rig.Refused("SET op_1 rotation 90"), StringComparison.Ordinal);
        await rig.Ok("BLIND ON");
        await rig.Ok("SET op_1 rotation 90");
        Assert.Equal(0, rig.Engine.Store.Project.Outputs[0].Rotation);
        // A remote still may not draft or edit; the owner's transport and air stay live.
        Assert.StartsWith("Not while armed: BLIND ON", await rig.Refused("BLIND ON", ShowRig.Remote), StringComparison.Ordinal);
        await rig.Ok("PLAYLIST GO pl_show");
        Assert.Equal("it_a", rig.Engine.Playback.Lane("pl_show")!.ItemId);
        // The show's own locks hold on the draft as they hold live.
        await rig.Ok("LOCK op_1 \"focus\"");
        Assert.StartsWith("Output 1 (output) is locked by Desk", await rig.Refused("SET op_1 rotation 180"), StringComparison.Ordinal);
        await rig.Ok("UNLOCK op_1");

        await rig.Ok("BLIND COMMIT");
        rig.Advance(40);
        await rig.Ticked();
        Assert.Equal(90, rig.Engine.Store.Project.Outputs[0].Rotation);
        Assert.True(rig.Show().GetProperty("armed").GetBoolean());
    }

    [Fact]
    public async Task OneDraftPerNodeAndTheDeskTakesItsOwnBack()
    {
        await using var rig = new ShowRig();
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        Assert.Equal("already blind — 1 change staged since " + Core.Show.LockGuard.Clock(rig.Engine.Services.Wall.UnixMs), await rig.Refused("BLIND ON"));
        Assert.Equal("Desk holds the blind draft (1 change staged) — one draft per node: it commits or discards it first",
            await rig.Refused("BLIND ON", ShowRig.Remote));
        Assert.Equal("the blind draft is Desk's — it commits or discards it (or the desk does)", await rig.Refused("BLIND DISCARD", ShowRig.Remote));

        // The desk reconnected: a new session with the same name takes the draft back and edits it.
        var again = Core.Wire.SessionInfo.Desk("d2");
        await rig.Ok("BLIND ON", again);
        await rig.Ok("SET op_1 name \"Left\"", again);
        Assert.Equal(2, Blind(rig).GetProperty("changes").GetInt32());
        // The old session's edits are live again.
        await rig.Ok("SET gn_testcard name \"Card\"");
        Assert.Equal("Card", rig.Engine.Store.Project.Generators[0].Name);
    }

    [Fact]
    public async Task AnOffLaneEditOfTheOwnerGoesToTheDraft()
    {
        await using var rig = new ShowRig();
        await rig.Ok("BLIND ON");
        var r = await rig.Engine.EditAsync(ShowRig.Desk, "rename", p => p with { Name = "Draft name" });
        Assert.True(r.Changed, r.Effect);
        Assert.Equal("Show test", rig.Engine.Store.Project.Name);
        Assert.Equal("Draft name", rig.Engine.Snapshot.Draft!.Project.Name);
        var diff = JsonDocument.Parse(await rig.Ok("BLIND DIFF")).RootElement;
        Assert.Equal("name", diff.GetProperty("changes")[0].GetProperty("id").GetString());
        // Another session's off-lane edit is live.
        Assert.True((await rig.Engine.EditAsync(ShowRig.Remote, "rename", p => p with { Name = "Live name" })).Changed);
        Assert.Equal("Live name", rig.Engine.Store.Project.Name);
    }

    [Fact]
    public async Task CommitLeadIsAShowRuleAndTooCloseIsRefused()
    {
        await using var rig = new ShowRig();
        await rig.Ok("SHOW SET COMMIT-LEAD 100");
        Assert.Equal(100, rig.Show().GetProperty("rules").GetProperty("commitLeadMs").GetInt32());
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        Assert.Equal("too close to the boundary — a commit lands at least 100 ms ahead (SHOW SET COMMIT-LEAD)", await rig.Refused("BLIND COMMIT AT +50"));
        var t = rig.Now;
        var commit = JsonDocument.Parse(await rig.Ok("BLIND COMMIT")).RootElement;
        Assert.Equal(t + 100_000_000, commit.GetProperty("effectiveLeaderNs").GetInt64());
    }

    private static Dictionary<string, (string Light, string State)> IndexRows(ShowRig rig)
    {
        var section = Assert.Single(rig.Engine.Routes.IndexSections, x => x.Section == "show");
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
            section.WriteRows(rig.Engine.Store.Project, rig.Engine.Snapshot, w);
        return JsonDocument.Parse(buffer.ToArray()).RootElement.EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetString()!, r => (r.GetProperty("light").GetString()!, r.GetProperty("state").GetString()!));
    }

    [Fact]
    public async Task TheIndexsShowSectionHoldsTheDraftsCommitNextAndDiscardKeys()
    {
        await using var rig = new ShowRig();
        Assert.Equal(("grey", "no draft"), IndexRows(rig)["BLIND COMMIT NEXT"]);
        Assert.Equal(("grey", "no draft"), IndexRows(rig)["BLIND DISCARD"]);
        await rig.Ok("BLIND ON");
        await rig.Ok("SET cv_main background \"#FF0000\"");
        Assert.Equal(("amber", "1 staged"), IndexRows(rig)["BLIND COMMIT NEXT"]);
        await rig.Ok("PLAYLIST GO pl_show");
        // The key's line lands the draft at the focused playlist's next switch.
        await rig.Ok("BLIND COMMIT NEXT");
        Assert.Equal(("amber", "landing next"), IndexRows(rig)["BLIND DISCARD"]);
        Assert.Equal("pl_show", Blind(rig).GetProperty("commit").GetProperty("playlist").GetString());
    }
}
