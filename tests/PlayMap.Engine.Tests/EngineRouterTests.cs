using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine;

namespace PlayMap.Engine.Tests;

/// <summary>The engine's verbs, run as a peer would send them: what each answers, what it changes, and
/// what STATE and the context then say.</summary>
public sealed class EngineRouterTests
{
    private static readonly SessionInfo Paired = new("t1", "wire", "10.0.0.9", false, "Test", true);
    private static readonly SessionInfo Unpaired = Paired with { Paired = false };

    /// <summary>An engine on a hand-moved clock, with the helpers every test uses.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public readonly ManualClock Clock = new(5_000_000_000);
        public readonly EngineCore Engine;

        public Rig() => Engine = new EngineCore(EngineServices.ForTests(Clock));

        public async Task<CommandReply> Run(string line, SessionInfo? session = null)
        {
            Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
            return await Engine.HandleAsync(parsed, session ?? Paired, CancellationToken.None);
        }

        public async Task<string> Ok(string line)
        {
            var r = await Run(line);
            Assert.True(r.Ok, $"{line} → {r.Payload}");
            return r.Payload ?? "";
        }

        public async Task Tick() => await Engine.Lane.RunAsync(() => Engine.Playback.Tick(Engine.Store.Project, Engine.LeaderNowNs));

        public JsonElement State() => JsonDocument.Parse(Engine.State().Json).RootElement;

        /// <summary>A playlist of two 10 s generator items, focused.</summary>
        public async Task<(string List, string A, string B)> TwoItems()
        {
            var list = await Ok("PLAYLIST NEW Show");
            var g1 = await Ok("GENERATOR NEW testcard Card");
            var g2 = await Ok("GENERATOR NEW julia Fractal");
            var a = await Ok($"PLAYLIST ADD {list} generator:{g1} Opening");
            var b = await Ok($"PLAYLIST ADD {list} generator:{g2} Main");
            await Run($"PLAYLIST FOCUS {list}"); // the first playlist is focused already; either answer is fine here
            return (list, a, b);
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    [Fact]
    public async Task MutatingVerbsAreRefusedUntilPairedAndQueriesStayOpen()
    {
        await using var rig = new Rig();
        var r = await rig.Run("BLACKOUT ON", Unpaired);
        Assert.False(r.Ok);
        Assert.StartsWith("not paired", r.Payload, StringComparison.Ordinal);
        Assert.True((await rig.Run("STATE", Unpaired)).Ok);
        Assert.True((await rig.Run("CONTEXT", Unpaired)).Ok);
        Assert.False(rig.Engine.Air.Blackout);
    }

    [Fact]
    public async Task UnknownWordsAreRefusedAndNeverDefaulted()
    {
        await using var rig = new Rig();
        var r = await rig.Run("LAUNCH ROCKETS");
        Assert.False(r.Ok);
        Assert.Contains("unknown verb", r.Payload, StringComparison.Ordinal);
        Assert.False((await rig.Run("BLACKOUT MAYBE")).Ok);
    }

    [Fact]
    public async Task APatternsFactLineIsTakenInAndNeverAnswered()
    {
        await using var rig = new Rig();
        var r = await rig.Run("BLACKOUT 1");
        Assert.True(r.Silent);
        Assert.True(rig.Engine.Air.PatternsBlackout);
        Assert.True(rig.Engine.Air.EffectiveBlackout);
        Assert.True((await rig.Run("LIVE 0")).Silent);
        Assert.True((await rig.Run("DECK 3 12")).Silent);
        Assert.True(rig.State().GetProperty("air").GetProperty("blackout").GetBoolean());
        Assert.True((await rig.Run("BLACKOUT 0")).Silent);
        Assert.False(rig.Engine.Air.EffectiveBlackout);
    }

    [Fact]
    public async Task ClipVerbsMeanWhatTheyMeanOnAPatternsDesk()
    {
        await using var rig = new Rig();
        var (list, _, _) = await rig.TwoItems();
        Assert.False((await rig.Run("VIDEO PLAY")).Ok, "nothing loaded yet");
        await rig.Ok($"PLAYLIST GO {list}");
        var again = await rig.Run("VT PLAY");
        Assert.False(again.Ok);
        Assert.Equal("already playing", again.Payload);
        rig.Clock.AdvanceMs(3000);
        await rig.Ok("VIDEO PAUSE");
        var video = rig.State().GetProperty("video");
        Assert.Equal(3000, video.GetProperty("positionMs").GetInt64());
        Assert.Equal(10000, video.GetProperty("lengthMs").GetInt64());
        Assert.True(video.GetProperty("paused").GetBoolean());
        Assert.Equal("Show", video.GetProperty("tag").GetString());
        await rig.Ok("VIDEO SEEK 0:07");
        Assert.Equal(7000, rig.State().GetProperty("video").GetProperty("positionMs").GetInt64());
        await rig.Ok("VIDEO END 2");
        Assert.Equal(8000, rig.State().GetProperty("video").GetProperty("positionMs").GetInt64());
        await rig.Ok("VIDEO STOP");
        Assert.Equal(0, rig.State().GetProperty("video").GetProperty("positionMs").GetInt64());
        Assert.False((await rig.Run("VIDEO STOP")).Ok);
        Assert.False((await rig.Run("VIDEO PLAY now")).Ok, "extra words are refused");
    }

    [Fact]
    public async Task TheNextItemTakesOverOnTheExactFrameItWasScheduledFor()
    {
        await using var rig = new Rig();
        var (list, a, b) = await rig.TwoItems();
        await rig.Ok($"PLAYLIST GO {list} AT +500");
        var lane = rig.Engine.Snapshot.Playback.Playlists[list];
        var start = rig.Engine.LeaderNowNs + 500_000_000;
        Assert.Equal(start + 10_000_000_000, lane.SwitchAtLeaderNs);
        // One nanosecond before the switch the first item shows its last instant; at the switch, the second.
        Assert.Equal(a, lane.At(lane.SwitchAtLeaderNs - 1).ItemId);
        var (_, clip, item) = lane.At(lane.SwitchAtLeaderNs);
        Assert.Equal(b, item);
        Assert.Equal(0, clip.PositionAt(lane.SwitchAtLeaderNs));
        rig.Clock.AdvanceMs(10_600);
        await rig.Tick();
        var after = rig.Engine.Snapshot.Playback.Playlists[list];
        Assert.Equal(b, after.ItemId);
        Assert.Equal(100_000_000, after.Clip.PositionAt(rig.Engine.LeaderNowNs));
    }

    [Fact]
    public async Task AScheduledStartHoldsTheFirstFrameUntilItsInstant()
    {
        await using var rig = new Rig();
        var (list, _, _) = await rig.TwoItems();
        await rig.Ok($"PLAYLIST GO {list} AT +2000");
        rig.Clock.AdvanceMs(1000);
        Assert.Equal(0, rig.State().GetProperty("video").GetProperty("positionMs").GetInt64());
        rig.Clock.AdvanceMs(1500);
        Assert.Equal(500, rig.State().GetProperty("video").GetProperty("positionMs").GetInt64());
    }

    [Fact]
    public async Task NextAndPrevWalkTheListAndStopAtItsEnds()
    {
        await using var rig = new Rig();
        var (list, a, b) = await rig.TwoItems();
        await rig.Ok($"PLAYLIST GO {list}");
        await rig.Ok($"PLAYLIST NEXT {list}");
        Assert.Equal(b, rig.Engine.Snapshot.Playback.Playlists[list].ItemId);
        Assert.False((await rig.Run($"PLAYLIST NEXT {list}")).Ok);
        await rig.Ok($"PLAYLIST LOOP {list} ON");
        await rig.Ok($"PLAYLIST NEXT {list}");
        Assert.Equal(a, rig.Engine.Snapshot.Playback.Playlists[list].ItemId);
        await rig.Ok($"PLAYLIST PREV {list}");
        Assert.Equal(b, rig.Engine.Snapshot.Playback.Playlists[list].ItemId);
    }

    [Fact]
    public async Task AFieldIsSetByPathAndUndoTakesItBack()
    {
        await using var rig = new Rig();
        var output = rig.Engine.Store.Project.Outputs[0].Id;
        await rig.Ok($"SET {output} name \"Projector A\"");
        Assert.Equal("Projector A", rig.Engine.Store.Project.Outputs[0].Name);
        await rig.Ok($"SET {output} warp.pin.topLeft {{\"x\":0.02,\"y\":0.01}}");
        Assert.Equal(0.02, rig.Engine.Store.Project.Outputs[0].Warp.Pin.TopLeft.X);
        var bad = await rig.Run($"SET {output} rotation 45");
        Assert.False(bad.Ok);
        Assert.Contains("rotation", bad.Payload, StringComparison.Ordinal);
        Assert.False((await rig.Run($"SET {output} id \"x\"")).Ok);
        await rig.Ok("UNDO");
        Assert.Equal(0, rig.Engine.Store.Project.Outputs[0].Warp.Pin.TopLeft.X);
        await rig.Ok("UNDO");
        Assert.Equal("Output 1", rig.Engine.Store.Project.Outputs[0].Name);
    }

    [Fact]
    public async Task AddReadsJsonOverTheKindsDefaultAndGivesAFreshId()
    {
        await using var rig = new Rig();
        var id = await rig.Ok("ADD output {\"name\":\"Left projector\",\"width\":3840,\"height\":2160}");
        var o = rig.Engine.Store.Project.FindOutput(id)!;
        Assert.Equal("Left projector", o.Name);
        Assert.Equal(3840, o.Width);
        Assert.Equal(60, o.RefreshHz);
        Assert.StartsWith(Ids.Output, id, StringComparison.Ordinal);
        Assert.False((await rig.Run("ADD output {\"width\":5}")).Ok);
        var pj = await rig.Ok("ADD projector {\"name\":\"Stage left\"}");
        var pin = await rig.Ok($"ADD pin {pj} {{\"world\":{{\"x\":1,\"y\":2,\"z\":3}},\"pixel\":{{\"x\":100,\"y\":200}}}}");
        Assert.Single(rig.Engine.Store.Project.FindProjector(pj)!.Pins);
        var got = JsonDocument.Parse(await rig.Ok($"GET {pin}")).RootElement;
        Assert.Equal(2, got.GetProperty("world").GetProperty("y").GetDouble());
    }

    [Fact]
    public async Task AnOwnedEntityNamesItsOwnerAsParentInsideItsJson()
    {
        await using var rig = new Rig();
        var pj = await rig.Ok("ADD projector {\"name\":\"Stage left\"}");
        var pin = await rig.Ok($"ADD pin {{\"parent\":\"{pj}\",\"world\":{{\"x\":1,\"y\":2,\"z\":3}},\"pixel\":{{\"x\":100,\"y\":200}}}}");
        Assert.Equal(pin, Assert.Single(rig.Engine.Store.Project.FindProjector(pj)!.Pins).Id);
        Assert.DoesNotContain("parent", await rig.Ok($"GET {pin}"), StringComparison.OrdinalIgnoreCase);

        var list = await rig.Ok("ADD playlist {\"name\":\"Loop\"}");
        var gen = rig.Engine.Store.Project.Generators[0].Id;
        var item = await rig.Ok($"ADD item {{\"parent\":\"{list}\",\"source\":{{\"kind\":\"generator\",\"id\":\"{gen}\"}},\"label\":\"Card\"}}");
        Assert.Equal("Card", Assert.Single(rig.Engine.Store.Project.FindPlaylist(list)!.Items).Label);
        Assert.StartsWith(Ids.PlaylistItem, item, StringComparison.Ordinal);

        var orphan = await rig.Run("ADD layer {\"name\":\"orphan\"}");
        Assert.False(orphan.Ok);
        Assert.Contains("parent", orphan.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.False((await rig.Run($"ADD item {{\"parent\":\"{list}\"}}")).Ok);
    }

    [Fact]
    public async Task RemovingSomethingInUseIsRefusedWithWhoUsesIt()
    {
        await using var rig = new Rig();
        var (list, _, _) = await rig.TwoItems();
        var gen = rig.Engine.Store.Project.Generators.First(g => g.Name == "Card").Id;
        var r = await rig.Run($"REMOVE {gen}");
        Assert.False(r.Ok);
        Assert.Contains("Show/Opening", r.Payload, StringComparison.Ordinal);
        await rig.Ok($"PLAYLIST GO {list}");
        Assert.False((await rig.Run($"REMOVE {list}")).Ok);
    }

    [Fact]
    public async Task GeneratorParametersAreValidatedAndNormalised()
    {
        await using var rig = new Rig();
        var g = await rig.Ok("GENERATOR NEW grid");
        await rig.Ok($"GENERATOR SET {g} cell 99999");
        Assert.Equal("4096", rig.Engine.Store.Project.FindGenerator(g)!.Params.Get("cell"));
        Assert.False((await rig.Run($"GENERATOR SET {g} color blue")).Ok);
        Assert.False((await rig.Run($"GENERATOR SET {g} wobble 3")).Ok);
        Assert.False((await rig.Run($"GENERATOR SET {g} cell 4096")).Ok, "a value it already has changes nothing");
    }

    [Fact]
    public async Task APatternGoesOverTheOutputsAndComesOff()
    {
        await using var rig = new Rig();
        await rig.Ok("PATTERN grid");
        Assert.Equal("pattern.grid", rig.Engine.Air.PatternKind);
        Assert.False((await rig.Run("PATTERN grid")).Ok);
        Assert.False((await rig.Run("PATTERN fractal")).Ok);
        Assert.Equal("pattern.grid", rig.State().GetProperty("outputs")[0].GetProperty("pattern").GetString());
        await rig.Ok("PATTERN OFF");
        Assert.Null(rig.Engine.Air.PatternKind);
    }

    [Fact]
    public async Task StateKeepsItsShapeAndTheContextItsContract()
    {
        await using var rig = new Rig();
        await rig.TwoItems();
        var keys = rig.State().EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["v", "seq", "id", "t", "name", "version", "at", "light", "headline", "air", "video", "playlists", "outputs",
            "media", "stage", "scan", "sync", "links", "peers", "machine", "project", "faults", "ai", "devices", "solve", "cameras"], keys);
        Assert.Equal("playmap", rig.State().GetProperty("t").GetString());
        var ctx = JsonDocument.Parse(rig.Engine.Context()).RootElement;
        Assert.Equal("playmap", ctx.GetProperty("node").GetProperty("t").GetString());
        Assert.Contains("nothing playing", ctx.GetProperty("headline").GetString(), StringComparison.Ordinal);
        Assert.Contains(ctx.GetProperty("commands").EnumerateArray(), c => c.GetProperty("verb").GetString()!.StartsWith("VIDEO PLAY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APlaylistMayPlayAnotherButNeverOneThatComesBackToItself()
    {
        await using var rig = new Rig();
        var show = await rig.Ok("PLAYLIST NEW Show");
        var loops = await rig.Ok("PLAYLIST NEW Loops");
        var bed = await rig.Ok("PLAYLIST NEW Bed");
        await rig.Ok($"PLAYLIST ADD {bed} color:#202020 Grey");
        await rig.Ok($"PLAYLIST ADD {loops} playlist:{bed} Bed");
        var nested = await rig.Ok($"PLAYLIST ADD {show} playlist:{loops} Loops");

        var self = await rig.Run($"PLAYLIST ADD {show} playlist:{show}");
        Assert.False(self.Ok);
        Assert.Equal("Show cannot play itself", self.Payload);
        var round = await rig.Run($"PLAYLIST ADD {bed} playlist:{show}");
        Assert.False(round.Ok);
        Assert.Equal("Show already plays Bed — a playlist cannot play itself through another", round.Payload);
        // The generic edits are fenced the same way: an item's source, an item added as JSON.
        var grey = rig.Engine.Store.Project.FindPlaylist(bed)!.Items[0].Id;
        var set = await rig.Run($"SET {grey} source {{\"kind\":\"playlist\",\"id\":\"{loops}\"}}");
        Assert.False(set.Ok);
        Assert.Contains("a playlist cannot play itself through another", set.Payload, StringComparison.Ordinal);
        var added = await rig.Run($"ADD item {bed} {{\"source\":{{\"kind\":\"playlist\",\"id\":\"{show}\"}},\"label\":\"Back\"}}");
        Assert.False(added.Ok);
        Assert.Contains("a playlist cannot play itself through another", added.Payload, StringComparison.Ordinal);
        Assert.Single(rig.Engine.Store.Project.FindPlaylist(bed)!.Items);
        Assert.Equal(SourceRef.Playlist(loops), rig.Engine.Store.Project.FindPlaylist(show)!.Items.Single(i => i.Id == nested).Source);
        Assert.All(rig.State().GetProperty("playlists").EnumerateArray(), l => Assert.False(l.TryGetProperty("problem", out _)));
    }

    [Fact]
    public async Task TheJournalRecordsWhatChangedAndWhatWasRefused()
    {
        await using var rig = new Rig();
        await rig.Ok("BLACKOUT ON");
        await rig.Run("BLACKOUT ON");
        var recent = rig.Engine.Journal.Recent(10);
        Assert.Contains(recent, r => r.Ok && r.Text == "every output to black");
        Assert.Contains(recent, r => !r.Ok && r.Text == "already blacked out");
    }

    [Fact]
    public async Task ATokenSentWithALinkNeverReachesTheJournal()
    {
        await using var rig = new Rig();
        // No network edge on the test rig, so the line is refused — and refusals are journalled too.
        await rig.Run("LINK PATTERNS 10.0.0.5 s3cr3t-token");
        await rig.Run("LINK STAGEWATCH 10.0.0.6 key-123");
        var rows = rig.Engine.Journal.Recent(10);
        Assert.NotEmpty(rows);
        Assert.DoesNotContain(rows, r => (r.Text + " " + r.Line).Contains("s3cr3t-token", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => (r.Text + " " + r.Line).Contains("key-123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProjectSavesAndOpensAgain()
    {
        await using var rig = new Rig();
        await rig.TwoItems();
        var path = Path.Combine(Path.GetTempPath(), $"playmap-{Guid.NewGuid():N}.playmap");
        try
        {
            await rig.Ok($"PROJECT SAVE {path}");
            Assert.False(rig.Engine.Dirty);
            await rig.Ok("PROJECT NEW Blank");
            Assert.Empty(rig.Engine.Store.Project.Playlists);
            await rig.Ok($"PROJECT OPEN {path}");
            Assert.Single(rig.Engine.Store.Project.Playlists);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AFollowerShowsTheLeadersFrameFromTheLeadersAnchor()
    {
        await using var leader = new Rig();
        await using var follower = new Rig();
        var (list, _, b) = await leader.TwoItems();
        await leader.Ok($"PLAYLIST SYNC {list} show");
        string? anchor = null;
        leader.Engine.AnchorPublished += (_, json) => anchor = json;
        await leader.Ok($"PLAYLIST GO {list} #2 AT +100");
        Assert.NotNull(anchor);
        // The follower holds the same project (as a synced node would) and joins the same group.
        await follower.Engine.Lane.RunAsync(() => follower.Engine.Store.Replace(leader.Engine.Store.Project, "same show"));
        await follower.Ok($"PLAYLIST ANCHOR {list} {anchor}");
        var t = leader.Engine.LeaderNowNs + 3_000_000_000;
        var l = leader.Engine.Snapshot.Playback.Playlists[list].At(t);
        var f = follower.Engine.Snapshot.Playback.Playlists[list].At(t);
        Assert.Equal(b, f.ItemId);
        Assert.Equal(l.Clip.PositionAt(t), f.Clip.PositionAt(t));
        Assert.False((await follower.Run($"PLAYLIST ANCHOR {list} {anchor}")).Ok, "the same anchor changes nothing");
    }

    [Fact]
    public async Task ARestartedNodeSeeksToWhereTheShowIsNow()
    {
        await using var before = new Rig();
        var (list, a, _) = await before.TwoItems();
        await before.Ok($"PLAYLIST GO {list}");
        before.Clock.AdvanceMs(2000);
        var record = RecoveryRecord.Capture(before.Engine);
        await using var after = new Rig();
        // Away for three seconds.
        after.Clock.NowNs = before.Clock.NowNs + 3_000_000_000;
        var words = await after.Engine.RestoreAsync(record with { WrittenUnixMs = after.Engine.Services.Wall.UnixMs - 3000 });
        Assert.StartsWith("recovered", words, StringComparison.Ordinal);
        var lane = after.Engine.Snapshot.Playback.Playlists[list];
        Assert.Equal(a, lane.ItemId);
        Assert.Equal(5_000_000_000, lane.Clip.PositionAt(after.Engine.LeaderNowNs));
    }

    [Fact]
    public async Task APinnedProjectorSolvesToTheTruthAndSaysHowWell()
    {
        await using var rig = new Rig();
        var truthK = PlayMap.Core.Model.Intrinsics.FromThrowRatio(1920, 1080, 1.3, 0, 0.4);
        var truthPose = PlayMap.Core.Model.Pose.LookAt(new PlayMap.Core.Numerics.Vec3d(0.4, 1.6, -6), new PlayMap.Core.Numerics.Vec3d(0, 1.2, 0), PlayMap.Core.Numerics.Vec3d.UnitY);
        var pj = await rig.Ok("ADD projector {\"name\":\"Left\"}");
        // Pins on a box and a wall: not coplanar, well spread.
        var world = new[]
        {
            (0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (1.0, 1.0, 0.0), (0.0, 1.0, 0.0), (-1.5, 0.0, 1.0), (-1.5, 2.5, 1.0),
            (1.8, 2.4, 1.0), (1.8, 0.2, 1.0), (0.5, 0.5, -0.5), (0.2, 1.8, 0.6),
        };
        foreach (var (x, y, z) in world)
        {
            var w = new PlayMap.Core.Numerics.Vec3d(x, y, z);
            var px = PlayMap.Core.Geometry.CameraModel.Project(w, truthK, PlayMap.Core.Model.LensDistortion.None, truthPose)!.Value;
            await rig.Ok(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"ADD pin {pj} {{\"world\":{{\"x\":{x},\"y\":{y},\"z\":{z}}},\"pixel\":{{\"x\":{px.X},\"y\":{px.Y}}}}}"));
        }
        var reply = JsonDocument.Parse(await rig.Ok($"PROJECTOR SOLVE {pj}")).RootElement;
        Assert.True(reply.GetProperty("rmsPx").GetDouble() < 0.1);
        var solved = rig.Engine.Store.Project.FindProjector(pj)!;
        Assert.Equal(truthK.Fx, solved.Intrinsics.Fx, 0);
        Assert.True(PlayMap.Core.Numerics.Vec3d.Distance(truthPose.Position, solved.Pose.Position) < 0.01);
        Assert.True(solved.SolveResidualPx > 0);
        Assert.Equal("green", JsonDocument.Parse(rig.Engine.Context()).RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetString() == "projector:" + pj).GetProperty("light").GetString());
    }
}
