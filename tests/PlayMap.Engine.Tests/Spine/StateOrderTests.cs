using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>STATE keeps today's keys in today's order, and a package's key comes only after them, in the order SPINE §9 gives and then
/// round 3's (docs/round3/PLAN.md §3.1 item 5) — a merged package's keys may be there; with its stub empty there are none: nothing
/// existing moves. The context keeps today's items and today's headline, which the show and fleet packages may only put words around.
/// These run at every merge.</summary>
public sealed class StateOrderTests
{
    private static string[] Keys(JsonElement e) => [.. e.EnumerateObject().Select(p => p.Name)];

    /// <summary>Today's keys first, exactly; after them only the packages' keys of this object, each at most once, in their order.</summary>
    private static void TodayThenOnly(string where, string[] today, string[] packages, string[] actual)
    {
        Assert.True(actual.Length >= today.Length, $"{where}: [{string.Join(", ", actual)}]");
        Assert.Equal(today, actual[..today.Length]);
        var next = 0;
        foreach (var key in actual[today.Length..])
        {
            var at = Array.IndexOf(packages, key, next);
            Assert.True(at >= 0, $"{where}: '{key}' is not a package key here, or is out of SPINE §9's order");
            next = at + 1;
        }
    }

    [Fact]
    public async Task StateHasTodaysKeysInTodaysOrderAndThePackagesKeysOnlyAfterThem()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        foreach (var line in new[] { "PLAYLIST NEW Show", "ADD projector {\"name\":\"Stage left\"}" })
        {
            Assert.True(WireGrammar.TryParse(line, out var parsed, out _));
            Assert.True((await engine.HandleAsync(parsed, SessionInfo.Desk("t"), CancellationToken.None)).Ok, line);
        }
        using var doc = JsonDocument.Parse(engine.State().Json);
        var s = doc.RootElement;

        TodayThenOnly("STATE",
            ["v", "seq", "id", "t", "name", "version", "at", "light", "headline", "air", "video", "playlists", "outputs", "media", "stage",
             "scan", "sync", "links", "peers", "machine", "project", "faults", "ai", "devices", "solve", "cameras"],
            ["show", "fleet", "eye", "decode", "videoio", "control", "timecode", "clients", "events", "selection", "fx", "audio",
             "modulation", "content", "led", "calibration", "onboarding", "align", "phones",
             "render", "observe", "previews", "guides"],
            Keys(s));
        TodayThenOnly("air", ["blackout", "frozen", "pattern", "identify", "patternsBlackout", "followPatternsBlackout"],
            ["master", "panic", "overlay"], Keys(s.GetProperty("air")));
        TodayThenOnly("air.overlay", ["on", "outputs", "layers", "handle", "grid", "cursors", "cursorP95Ms", "armed"], ["handleP95Ms"],
            Keys(s.GetProperty("air").GetProperty("overlay")));
        TodayThenOnly("outputs[]", ["id", "name", "mode", "kind", "display", "width", "height", "refreshHz", "enabled", "open", "fps", "dropped",
            "frameMsP95", "pattern", "projector", "light"], ["latencyMs", "present", "lock", "fx", "measured", "observed", "showing", "hand", "passThrough"],
            Keys(s.GetProperty("outputs")[0]));
        var playlist = Keys(s.GetProperty("playlists")[0]);
        var items = Array.IndexOf(playlist, "items");
        Assert.True(items >= 0);
        TodayThenOnly("playlists[]", playlist[..(items + 1)], ["chase"], playlist);
        TodayThenOnly("media", ["items", "missing", "ready", "transfers"], ["optimise", "shownOn"], Keys(s.GetProperty("media")));
        TodayThenOnly("stage", ["surfaces", "meshes", "projectors", "scans"], ["site", "controls", "surveys", "stacks"], Keys(s.GetProperty("stage")));
        TodayThenOnly("stage.projectors[]", ["id", "name", "solved", "residualPx", "pins"], ["uncertainty", "priors", "stack", "grade", "quads"],
            Keys(s.GetProperty("stage").GetProperty("projectors")[0]));
        TodayThenOnly("sync", ["role", "leader", "offsetMs", "rttMs", "driftPpm", "groups"],
            ["group", "feed", "slewing", "steps", "freewheelS", "outputs", "pairs", "verdict", "words", "groupLatencyMs", "latencyMs", "lock", "epoch", "light"],
            Keys(s.GetProperty("sync")));
        TodayThenOnly("machine", ["renderer", "memMb", "gcPauseMsTotal"],
            ["present", "views", "gl", "identity", "capabilities", "platform", "locked", "install"], Keys(s.GetProperty("machine")));
        TodayThenOnly("project", ["id", "name", "revision", "path", "dirty"], ["index"], Keys(s.GetProperty("project")));
    }

    /// <summary>A live input's row (in STATE and INPUT LIST alike) keeps round 2's keys in their order; round 3's capture keys come
    /// only after them.</summary>
    [Fact]
    public async Task ALiveInputKeepsItsKeysAndTheCaptureKeysComeAfterThem()
    {
        await using var rig = new VideoIo.VideoIoRig();
        await rig.Ok("INPUT ADD omt://10.0.0.7:6400 Program");
        using var doc = JsonDocument.Parse(rig.Engine.State().Json);
        var input = doc.RootElement.GetProperty("videoio").GetProperty("inputs")[0];
        TodayThenOnly("videoio.inputs[]", ["id", "name", "uri", "proto", "state", "words", "w", "h", "fps", "ageMs", "received", "dropped",
            "repeated", "using", "tally", "onAir", "onLoss", "fallback", "loss", "backup", "backupState", "format", "alpha", "pacing", "warm",
            "mbps"], ["capture", "latency"], Keys(input));
        using var list = JsonDocument.Parse((await rig.Ok("INPUT LIST")).Payload ?? "");
        Assert.Equal(Keys(input), Keys(list.RootElement[0]));
    }

    [Fact]
    public async Task TheContextKeepsTodaysItemsAndItsHeadlineIsTodaysWithWordsAroundIt()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        using var doc = JsonDocument.Parse(engine.Context());
        var kinds = doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()).ToList();
        string[] today = ["air", "playlist", "output", "projector", "media", "link", "clock", "scan", "fault", "device", "assistant"];
        // SPINE §10.1's kinds a package's items may have, then round 3's (the guides' items).
        string[] packages = ["show", "preflight", "machine", "mirror", "sync", "decode", "optimise", "videoio", "input", "send", "control",
            "timecode", "chase", "deck", "fx", "audio", "tempo", "content", "led", "site", "survey", "stack", "align", "phone", "calibration",
            "overlay", "outputs", "playlists",
            "guide"];
        Assert.All(kinds, k => Assert.True(today.Contains(k) || packages.Contains(k), $"'{k}' is no item kind of today or of a package"));
        Assert.Contains("output", kinds);
        Assert.Contains($"{engine.Store.Project.Name} · nothing playing · outputs not measured", doc.RootElement.GetProperty("headline").GetString(),
            StringComparison.Ordinal);
    }
}
