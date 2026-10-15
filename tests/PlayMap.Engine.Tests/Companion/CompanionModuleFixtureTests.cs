using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Companion;

/// <summary>The Companion module's fixtures (integrations/companion-module-playmap/test/fixtures/state.json and index.json) are STATE
/// and INDEX as this engine writes them for a small show — two playlists, a generator, colour items, the show section, one item playing
/// a second in, a fault, a peer heard, a scan running, a media pull and a Stagewatch timer — so the module's tests read what a node says and cannot drift from it. The show's random ids are renamed in order of
/// appearance and the two signatures fixed, so the files are the same on every run. PLAYMAP_WRITE_FIXTURES=1 writes them; otherwise
/// the committed files must be what the engine writes now.</summary>
public sealed partial class CompanionModuleFixtureTests
{
    private static string FixturePath(string name) =>
        Path.Combine(NodeExe.RepoRoot(), "integrations", "companion-module-playmap", "test", "fixtures", name);

    [Fact]
    public async Task TheModulesStateAndIndexAreWhatTheEngineWrites()
    {
        await using var rig = new CompanionRig(s => s with
        {
            Faults = new OneFault(), Network = new OnePeer(), Scan = new OneScan(), Transfers = new OneTransfer(),
        });
        var show = await rig.Ok("PLAYLIST NEW Show");
        var loops = await rig.Ok("PLAYLIST NEW Loops");
        var gen = await rig.Ok("GENERATOR NEW flat White");
        var intro = await rig.Ok($"PLAYLIST ADD {show} generator:{gen} Intro");
        var red = await rig.Ok($"PLAYLIST ADD {show} color:#FF0000 Red");
        await rig.Ok($"PLAYLIST ADD {show} color:#0000FF Blue");
        await rig.Ok($"PLAYLIST ADD {loops} color:#00FF00 Green");
        // A projector box linked (no projector control on this node: its power unknown, its shutter not reported).
        await rig.Ok("DEVICE ADD \"Left box\" pjlink 10.0.0.20");
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
    /// signatures, STATE's seq and the process's memory figures fixed, indented.</summary>
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
        // STATE's seq counts the node's facts as they arrive (a fault, a peer heard), so it is true of one run only.
        if (root["seq"] is not null)
            root["seq"] = 8030048;
        if (root["sig"] is not null)
        {
            root["sig"] = "9f1c2ab07d3e4410";
            root["labels"] = "77aa0c1e93b2d5f0";
        }
        if (root["project"] is JsonObject project && project["index"] is not null)
            project["index"] = "9f1c2ab07d3e4410";
        if (root["machine"] is JsonObject machine)
        {
            // The test process's own memory and GC pauses: true of this run only.
            machine["memMb"] = 0;
            machine["gcPauseMsTotal"] = 0;
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private sealed class OneFault : INodeFaults
    {
        public IReadOnlyList<NodeFault> Faults() => [new("mdns", "the network announcement could not open: port 5353 is taken")];
    }

    /// <summary>The network edge as a node with one peer heard, Patterns off and Stagewatch live with its timer (the shape
    /// NetworkHost writes).</summary>
    private sealed class OnePeer : INetworkView
    {
        public IReadOnlyList<PeerView> Peers() => [new("playmap", "a1b2c3d4", "Stage left", "amber", "Stage left · output 2 dropping frames", true)];

        public LinkView? Patterns() => null;

        public LinkView? Stagewatch() => new("live", "10.0.0.9", true, "Keynote");

        public string? StagewatchTimerJson() =>
            "{\"state\":\"running\",\"text\":\"4:59\",\"seconds\":299,\"phase\":\"running\",\"color\":\"green\",\"title\":\"Keynote\"," +
            "\"message\":\"\",\"trusted\":true,\"why\":\"\"}";

        public SyncView Sync() => NullBackends.Instance.Sync();
    }

    /// <summary>A structured-light scan twelve patterns into forty.</summary>
    private sealed class OneScan : IScanBackend
    {
        public ScanProgress? Current => new("projecting", "pj_left", 12, 40, 0.9, "pattern 12 of 40 on Left");

        public Task<string> StartAsync(string cameraId, IReadOnlyList<string> projectorIds, string mode, CancellationToken ct) =>
            Task.FromException<string>(new InvalidOperationException("a scan is already running"));

        public bool Cancel() => false;
    }

    /// <summary>One media file being pulled from a peer, a quarter done.</summary>
    private sealed class OneTransfer : IMediaTransfers
    {
        public IReadOnlyList<MediaTransferView> Transfers() =>
            [new("9f1c2ab07d3e4410", "md00000099", "intro.mov", "Stage left", 25_000_000, 100_000_000, "pulling")];
    }

    /// <summary>A show entity's id: its two-letter kind and eight lowercase hex digits (playlists, items, generators, the project).</summary>
    [GeneratedRegex(@"\b(pl|it|gn|pr|dv)[0-9a-f]{8}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ShowId();
}
