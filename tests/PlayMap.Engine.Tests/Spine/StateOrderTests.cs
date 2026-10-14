using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The spine's STATE is today's: its keys in today's order and nothing else — every package hook is in place and writes
/// nothing until its package fills it — and the context carries no package item yet.</summary>
public sealed class StateOrderTests
{
    private static string[] Keys(JsonElement e) => [.. e.EnumerateObject().Select(p => p.Name)];

    [Fact]
    public async Task StateHasTodaysKeysInTodaysOrderAndNothingElse()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        foreach (var line in new[] { "PLAYLIST NEW Show", "ADD projector {\"name\":\"Stage left\"}" })
        {
            Assert.True(WireGrammar.TryParse(line, out var parsed, out _));
            Assert.True((await engine.HandleAsync(parsed, SessionInfo.Desk("t"), CancellationToken.None)).Ok, line);
        }
        using var doc = JsonDocument.Parse(engine.State().Json);
        var s = doc.RootElement;

        Assert.Equal(
            ["v", "seq", "id", "t", "name", "version", "at", "light", "headline", "air", "video", "playlists", "outputs", "media", "stage",
             "scan", "sync", "links", "peers", "machine", "project", "faults", "ai", "devices", "solve", "cameras"],
            Keys(s));
        Assert.Equal(["blackout", "frozen", "pattern", "identify", "patternsBlackout", "followPatternsBlackout"], Keys(s.GetProperty("air")));
        Assert.Equal(["id", "name", "mode", "kind", "display", "width", "height", "refreshHz", "enabled", "open", "fps", "dropped", "frameMsP95",
            "pattern", "projector", "light"], Keys(s.GetProperty("outputs")[0]));
        Assert.Equal("items", Keys(s.GetProperty("playlists")[0])[^1]);
        Assert.Equal(["items", "missing", "ready", "transfers"], Keys(s.GetProperty("media")));
        Assert.Equal(["surfaces", "meshes", "projectors", "scans"], Keys(s.GetProperty("stage")));
        Assert.Equal(["id", "name", "solved", "residualPx", "pins"], Keys(s.GetProperty("stage").GetProperty("projectors")[0]));
        Assert.Equal(["role", "leader", "offsetMs", "rttMs", "driftPpm", "groups"], Keys(s.GetProperty("sync")));
        Assert.Equal(["renderer", "memMb", "gcPauseMsTotal"], Keys(s.GetProperty("machine")));
        Assert.Equal(["id", "name", "revision", "path", "dirty"], Keys(s.GetProperty("project")));
    }

    [Fact]
    public async Task TheContextHasNoPackageItemAndItsHeadlineIsTodays()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        using var doc = JsonDocument.Parse(engine.Context());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()).ToList();
        Assert.All(items, k => Assert.Contains(k, new[] { "air", "playlist", "output", "projector", "media", "link", "clock", "scan", "fault", "device", "assistant" }));
        Assert.Equal($"{engine.Store.Project.Name} · nothing playing · outputs not measured", doc.RootElement.GetProperty("headline").GetString());
    }
}
