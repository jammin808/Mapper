using Avalonia.Headless.XUnit;
using PlayMap.App.State;
using PlayMap.App.ViewModels;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine;

namespace PlayMap.App.Tests;

/// <summary>The desk reads STATE as a real engine writes it: STATE.air.pattern is an object (its kind and the outputs it
/// covers), null when no pattern is up; the round-2 sections are kept whole in Root for the packages' readers.</summary>
public class StateSnapshotTests
{
    private static readonly SessionInfo Desk = new("d1", "desk", "127.0.0.1", true, "PlayMapDesk", true);

    [Fact]
    public async Task ARealEnginesStateLineWithAirPatternAsAnObjectParses()
    {
        var clock = new ManualClock(5_000_000_000);
        await using var engine = new EngineCore(EngineServices.ForTests(clock));
        var none = StateSnapshot.Parse(engine.State().Json);
        Assert.Null(none.Air.Pattern);

        Assert.True(WireGrammar.TryParse("PATTERN grid", out var line, out _));
        var reply = await engine.HandleAsync(line, Desk, CancellationToken.None);
        Assert.True(reply.Ok, reply.Payload);

        // The line exactly as the node pushes it on the wire, read the way the desk's link reads it.
        var push = "STATE " + engine.State().Json;
        Assert.Contains("\"pattern\":{\"kind\":\"pattern.grid\"", push, StringComparison.Ordinal);
        var s = StateSnapshot.Parse(push["STATE ".Length..]);
        Assert.Equal("pattern.grid", s.Air.Pattern);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, s.Root.GetProperty("air").GetProperty("pattern").ValueKind);
        Assert.False(s.Air.Blackout);

        // A STATE from before round 2 (the pattern as a bare word) still reads.
        Assert.Equal("pattern.grid", StateSnapshot.Parse("{\"air\":{\"pattern\":\"pattern.grid\"}}").Air.Pattern);
        Assert.Null(StateSnapshot.Parse("{\"air\":{\"pattern\":null}}").Air.Pattern);
    }
}

/// <summary>The desk reads every STATE fixture as the engine writes it now — main.json, output.json and each package's (each scenario
/// of a fixture of several nodes) — and labels the fields whose meaning is not their name: sync.offsetMs is how far the leader's time
/// can be off (half the round trip), outputs[].dropped the late presents past the budget in 10 s, latencyMs the display's lag the node
/// holds the output to, present.path how its frames reach the display.</summary>
public class StateFixtureReadTests
{
    [Fact]
    public void EveryFixtureParsesAndTheOutputsTimingReadsWithItsMeaning()
    {
        foreach (var package in StateFixtures.Packages)
        {
            foreach (var (_, sections) in StateFixtures.Scenarios(package))
            {
                var root = CardTests.FixtureRoot(package, sections);
                var s = StateSnapshot.Parse(root.GetRawText());
                Assert.Equal(root.GetProperty("outputs").GetArrayLength(), s.Outputs.Length);
                Assert.Equal(root.GetProperty("playlists").GetArrayLength(), s.Playlists.Length);
                Assert.Equal(root.GetProperty("sync").GetProperty("role").GetString(), s.Sync.Role);
                // Each package's own block is kept whole for its readers.
                foreach (var (key, _) in sections)
                    Assert.True(s.Root.TryGetProperty(key, out _), $"{package}: {key}");
            }
        }

        var output = StateSnapshot.Parse(StateFixtures.Read("output"));
        var left = output.FindOutput("op_left")!;
        Assert.Equal((33d, "offscreen"), (left.LatencyMs, left.PresentPath));
        Assert.Equal("64×36 @ 60 Hz · closed · display latency 33 ms · offscreen", OutputInspector.OutputFacts(left));
        Assert.Equal("1920×1080 @ 60 Hz · open · 60 fps · 2 late in 10 s · frame p95 16.9 ms",
            OutputInspector.OutputFacts(new OutputState("op_1", "Left", "source", "HDMI-1", 1920, 1080, 60, true, 60, 2, 16.9, null, "amber")));
        // A STATE from before outputs carried their latency reads it as unknown, and the words leave it out.
        Assert.Equal(-1, StateSnapshot.Parse("""{"outputs":[{"id":"op_1"}]}""").Outputs[0].LatencyMs);
    }

    [AvaloniaFact]
    public async Task AFollowersOffsetIsShownAsTheLeadersTimeUncertainty()
    {
        await using var desk = new DeskHarness();
        desk.Link.SetSection("sync", """{"role":"follower","leader":"9f8e7d6c","offsetMs":0.21,"rttMs":0.42,"driftPpm":3.2,"groups":["show"]}""");
        DeskHarness.Pump();
        Assert.Equal("± 0.21 ms", desk.Main.SyncText);
        Assert.Contains("leader time ± 0.21 ms (half the round trip)", desk.Main.NetworkTip, StringComparison.Ordinal);
        Assert.StartsWith("Following 9f8e7d6c · leader time ± 0.21 ms (half the round trip) · round trip 0.42 ms", desk.Main.Network.SyncText, StringComparison.Ordinal);
        Assert.DoesNotContain("offset", desk.Main.Network.SyncText, StringComparison.Ordinal);
    }
}
