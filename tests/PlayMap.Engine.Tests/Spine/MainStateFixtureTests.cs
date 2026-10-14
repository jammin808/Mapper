using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Assistant;
using PlayMap.Engine.Tests.Rendering;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>tests/fixtures/state/main.json is today's STATE from a real scenario — two projectors on a wall blended, the scan runner's
/// cameras, the assistant on its door — the shapes the desk's cards of solve, cameras and ai read. This test writes
/// it (PLAYMAP_WRITE_FIXTURES=1) and otherwise checks the committed file is what the engine writes now, with the numbers that change
/// from run to run (time, memory, ids, the revision counter) set to fixed values.</summary>
public sealed class MainStateFixtureTests
{
    private static string FixturePath(string name) => Path.Combine(NodeExe.RepoRoot(), "tests", "fixtures", "state", name);

    /// <summary>STATE with what moves between runs fixed, every other value as written.</summary>
    internal static string Normalise(string stateJson)
    {
        var root = JsonNode.Parse(stateJson)!.AsObject();
        root["seq"] = 0;
        root["id"] = "0badc0de";
        root["at"] = 1_760_000_000_000;
        var machine = root["machine"]!.AsObject();
        machine["memMb"] = 0;
        machine["gcPauseMsTotal"] = 0;
        var project = root["project"]!.AsObject();
        project["id"] = "pr_fixture";
        project["path"] = null;
        if (root["solve"] is JsonObject solve)
            solve["id"] = "bs_fixture";
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    [Fact]
    public async Task TodaysStateIsTheCommittedFixture()
    {
        // A local model on its own endpoint: the card shows the settings in force, whatever the node's default provider is.
        var rig = new OpsRig(assistant: AssistantSettings.Local("show-model", "http://127.0.0.1:11434/v1"), project: BlendSolveTests.TwoProjectorsOnAWall());
        try
        {
            var solve = await rig.Run("BLEND SOLVE", OpsRig.Desk);
            Assert.True(solve.Ok, solve.Payload);
            var end = await rig.Engine.WhenSolved.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal("done", end!.State);

            var state = Normalise(rig.Engine.State().Json);
            using (var doc = JsonDocument.Parse(state))
            {
                var s = doc.RootElement;
                Assert.Equal("done", s.GetProperty("solve").GetProperty("state").GetString());
                Assert.Equal(2, s.GetProperty("solve").GetProperty("outputs").GetArrayLength());
                Assert.Equal(JsonValueKind.Array, s.GetProperty("cameras").ValueKind);
                Assert.Equal(JsonValueKind.Object, s.GetProperty("ai").GetProperty("assistant").ValueKind);
                Assert.Equal(JsonValueKind.Array, s.GetProperty("devices").ValueKind);
            }

            var path = FixturePath("main.json");
            if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
                await File.WriteAllTextAsync(path, state);
            Assert.True(File.Exists(path), "main.json is written by this test: run it once with PLAYMAP_WRITE_FIXTURES=1");
            Assert.Equal(await File.ReadAllTextAsync(path), state);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public void EveryPackageHasAFixtureAndACardDescriptorThatParse()
    {
        string[] packages = ["show", "fleet", "sync", "output", "decode", "optimise", "videoio", "control", "companion", "fx", "audio",
            "content", "led", "facade", "align", "phonescan", "wall"];
        foreach (var pkg in packages)
        {
            using var state = JsonDocument.Parse(File.ReadAllText(FixturePath(pkg + ".json")));
            Assert.Equal(JsonValueKind.Object, state.RootElement.ValueKind);
            using var cards = JsonDocument.Parse(File.ReadAllText(FixturePath(pkg + ".cards.json")));
            Assert.Equal(JsonValueKind.Array, cards.RootElement.ValueKind);
        }
    }
}
