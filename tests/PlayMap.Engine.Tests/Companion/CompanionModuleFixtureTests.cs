using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Companion;

/// <summary>The Companion module's fixtures (integrations/companion-module-playmap/test/fixtures/state.json and index.json) are STATE
/// and INDEX as this engine writes them for a small show — two playlists, a generator, colour items, the show section, one item playing
/// a second in, a fault and a peer heard — so the module's tests read what a node says and cannot drift from it. The show's random ids are renamed in order of
/// appearance and the two signatures fixed, so the files are the same on every run. PLAYMAP_WRITE_FIXTURES=1 writes them; otherwise
/// the committed files must be what the engine writes now.</summary>
public sealed partial class CompanionModuleFixtureTests
{
    private static string FixturePath(string name) =>
        Path.Combine(NodeExe.RepoRoot(), "integrations", "companion-module-playmap", "test", "fixtures", name);

    [Fact]
    public async Task TheModulesStateAndIndexAreWhatTheEngineWrites()
    {
        await using var rig = new CompanionRig(s => s with { Faults = new OneFault(), Network = new OnePeer() });
        var show = await rig.Ok("PLAYLIST NEW Show");
        var loops = await rig.Ok("PLAYLIST NEW Loops");
        var gen = await rig.Ok("GENERATOR NEW flat White");
        var intro = await rig.Ok($"PLAYLIST ADD {show} generator:{gen} Intro");
        var red = await rig.Ok($"PLAYLIST ADD {show} color:#FF0000 Red");
        await rig.Ok($"PLAYLIST ADD {show} color:#0000FF Blue");
        await rig.Ok($"PLAYLIST ADD {loops} color:#00FF00 Green");
        await rig.Ok($"PLAYLIST GO {show} {red}");
        rig.Clock.AdvanceMs(1000);

        var index = await rig.Ok("INDEX");
        var state = rig.Engine.State().Json;
        using (var s = JsonDocument.Parse(state))
        {
            var playing = s.RootElement.GetProperty("playlists").EnumerateArray().Single(p => p.GetProperty("id").GetString() == show);
            Assert.Equal(red, playing.GetProperty("item").GetProperty("id").GetString());
            Assert.NotEqual("idle", playing.GetProperty("state").GetString());
        }
        Assert.Contains(intro, index, StringComparison.Ordinal);

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var stateFixture = Stable(state, ids);
        var indexFixture = Stable(index, ids);

        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(FixturePath("state.json"), stateFixture);
            await File.WriteAllTextAsync(FixturePath("index.json"), indexFixture);
        }
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("state.json"))).ReplaceLineEndings("\n"), stateFixture);
        Assert.Equal((await File.ReadAllTextAsync(FixturePath("index.json"))).ReplaceLineEndings("\n"), indexFixture);
    }

    /// <summary>The document with every show id renamed in order of first appearance (shared across the two files) and the
    /// signatures fixed, indented.</summary>
    private static string Stable(string json, Dictionary<string, string> ids)
    {
        var text = ShowId().Replace(json, m =>
        {
            if (!ids.TryGetValue(m.Value, out var stable))
            {
                stable = m.Groups[1].Value + (ids.Count + 1).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
                ids[m.Value] = stable;
            }
            return stable;
        });
        var root = JsonNode.Parse(text)!.AsObject();
        if (root["sig"] is not null)
        {
            root["sig"] = "9f1c2ab07d3e4410";
            root["labels"] = "77aa0c1e93b2d5f0";
        }
        if (root["project"] is JsonObject project && project["index"] is not null)
            project["index"] = "9f1c2ab07d3e4410";
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private sealed class OneFault : INodeFaults
    {
        public IReadOnlyList<NodeFault> Faults() => [new("mdns", "the network announcement could not open: port 5353 is taken")];
    }

    /// <summary>The network edge as a node with one peer heard and both links off.</summary>
    private sealed class OnePeer : INetworkView
    {
        public IReadOnlyList<PeerView> Peers() => [new("playmap", "a1b2c3d4", "Stage left", "amber", "Stage left · output 2 dropping frames", true)];

        public LinkView? Patterns() => null;

        public LinkView? Stagewatch() => null;

        public string? StagewatchTimerJson() => null;

        public SyncView Sync() => NullBackends.Instance.Sync();
    }

    /// <summary>A show entity's id: its two-letter kind and eight lowercase hex digits (playlists, items, generators, the project).</summary>
    [GeneratedRegex(@"\b(pl|it|gn|pr)[0-9a-f]{8}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ShowId();
}
