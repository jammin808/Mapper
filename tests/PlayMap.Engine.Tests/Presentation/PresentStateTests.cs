using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Presentation;
using PlayMap.Core.Sync;
using PlayMap.Engine.Presentation;
using PlayMap.Engine.Rendering;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Render.Platform;

namespace PlayMap.Engine.Tests.Presentation;

/// <summary>outputs[].present, outputs[].lock, sync.lock and machine.present: their keys and types, and the output's light from the
/// display side's counts against its own refresh — amber only past 0.5 % late over 10 s.</summary>
public sealed class PresentStateTests
{
    private const long Second = 1_000_000_000;
    private const double Period = 1e9 / 60;

    private static readonly (string Key, JsonValueKind Kind)[] PresentKeys =
    [
        ("path", JsonValueKind.String), ("mode", JsonValueKind.String), ("evidence", JsonValueKind.String), ("refreshHz", JsonValueKind.Number),
        ("nominalHz", JsonValueKind.Number), ("ppm", JsonValueKind.Number), ("settled", JsonValueKind.True), ("missedVblanks", JsonValueKind.Number),
        ("missed60s", JsonValueKind.Number), ("held", JsonValueKind.Number), ("delayFrames", JsonValueKind.Number),
        ("latencyResidualMs", JsonValueKind.Number), ("statsAgeMs", JsonValueKind.Number), ("words", JsonValueKind.String),
        ("presented", JsonValueKind.Number), ("late10s", JsonValueKind.Number), ("phase", JsonValueKind.String), ("light", JsonValueKind.String),
    ];

    /// <summary>A measured 60 Hz clock fed <paramref name="seconds"/> of presents, <paramref name="late"/> of them a refresh late (spread
    /// over the last 10 s).</summary>
    private static PresentClock Clock(int seconds, int late, long start = 10 * Second)
    {
        var clock = new PresentClock(60, measured: true, PresentPath.GlWindow);
        var frames = seconds * 60;
        var lateAt = new HashSet<int>(Enumerable.Range(0, late).Select(i => frames - 30 - i * 97));
        var k = 0;
        for (var f = 0; f < frames; f++)
        {
            clock.BeginFrame(start + (long)Math.Round(k * Period) + 1_000_000);
            k += lateAt.Contains(f) ? 2 : 1;
            clock.OnPresented(PresentResult.Done(start + (long)Math.Round(k * Period) + 150_000));
        }
        return clock;
    }

    /// <summary>Just before the last of <paramref name="seconds"/> ends: the last 10 s are ten whole seconds of presents.</summary>
    private static long End(int seconds, long start = 10 * Second) => start + seconds * Second - 1_000_000;

    [Fact]
    public async Task PresentAndLockKeysHaveTheirTypes()
    {
        var outputs = new ScriptedOutputs { NowNs = End(12) }.WithFreeLocks("op_a");
        outputs.Clocks["op_a"] = Clock(12, 0);
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), outputs);
        var s = rig.State();
        var o = s.GetProperty("outputs")[0];
        Assert.Equal(JsonValueKind.Number, o.GetProperty("latencyMs").ValueKind);
        var present = o.GetProperty("present");
        Assert.Equal(PresentKeys.Select(k => k.Key), present.EnumerateObject().Select(p => p.Name));
        foreach (var (key, kind) in PresentKeys)
        {
            var actual = present.GetProperty(key).ValueKind;
            Assert.True(actual == kind || (kind == JsonValueKind.True && actual == JsonValueKind.False), $"present.{key} is {actual}");
        }
        Assert.Equal("gl-window", present.GetProperty("path").GetString());
        Assert.Equal("gl", present.GetProperty("mode").GetString());
        Assert.Equal("none", present.GetProperty("evidence").GetString());
        Assert.True(present.GetProperty("settled").GetBoolean());
        Assert.InRange(present.GetProperty("refreshHz").GetDouble(), 59.99, 60.01);
        Assert.Equal(12 * 60, present.GetProperty("presented").GetInt64());
        Assert.Equal("approximate", present.GetProperty("phase").GetString());

        var lockJson = o.GetProperty("lock");
        Assert.Equal(["state", "words", "reason", "fellOut", "rejoins"], lockJson.EnumerateObject().Select(p => p.Name));
        Assert.Equal("free-measured", lockJson.GetProperty("state").GetString());

        var sync = s.GetProperty("sync");
        Assert.Equal(JsonValueKind.Number, sync.GetProperty("latencyMs").ValueKind);
        var group = sync.GetProperty("lock");
        Assert.Equal(["state", "driver", "board", "words", "applied", "locked", "fellOut", "rejoins", "time", "refused"],
            group.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Quadro Sync II (P2061)", group.GetProperty("board").GetString());
        Assert.Equal("fake", group.GetProperty("driver").GetString());
        Assert.Equal(JsonValueKind.Null, group.GetProperty("refused").ValueKind);

        var machine = s.GetProperty("machine").GetProperty("present");
        Assert.Equal(["api", "adapter", "path", "experimental", "why", "resets", "presentMsP95", "dialect", "context", "asked", "kept", "fuse", "airLatencyMs", "latencyWords"],
            machine.EnumerateObject().Select(p => p.Name));
        Assert.Equal("core330", machine.GetProperty("dialect").GetString());
    }

    [Fact]
    public async Task TheLightGoesAmberPastHalfAPercentLateOverTenSeconds()
    {
        // About 600 refreshes in the last 10 s: 3 late is within the 0.5 % budget, 4 late is past it.
        var outputs = new ScriptedOutputs { NowNs = End(30) }.WithFreeLocks("op_a");
        outputs.Clocks["op_a"] = Clock(30, 3);
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), outputs);
        var o = rig.State().GetProperty("outputs")[0];
        Assert.Equal(3, o.GetProperty("present").GetProperty("late10s").GetInt64());
        Assert.Equal("green", o.GetProperty("present").GetProperty("light").GetString());
        Assert.Equal("green", o.GetProperty("light").GetString());
        Assert.Equal(0, o.GetProperty("dropped").GetInt64());

        outputs.Clocks["op_a"] = Clock(30, 4);
        o = rig.State().GetProperty("outputs")[0];
        Assert.Equal(4, o.GetProperty("present").GetProperty("late10s").GetInt64());
        Assert.Equal("amber", o.GetProperty("present").GetProperty("light").GetString());
        Assert.Equal("amber", o.GetProperty("light").GetString());
        Assert.Equal(4, o.GetProperty("dropped").GetInt64());
        Assert.Contains("% late in 10 s", o.GetProperty("present").GetProperty("words").GetString(), StringComparison.Ordinal);

        // Ten seconds on, with nothing late since, it is green again: the light is the last 10 s, not the whole life.
        outputs.NowNs = End(30) + 11 * Second;
        o = rig.State().GetProperty("outputs")[0];
        Assert.Equal("green", o.GetProperty("light").GetString());
        Assert.Equal(4, o.GetProperty("present").GetProperty("missedVblanks").GetInt64());
    }

    [Fact]
    public async Task ANodeWithoutARenderHostWritesTheKeysWithNothingMeasured()
    {
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a") with { LatencyMs = 33 }), scripted: false);
        var s = rig.State();
        var o = s.GetProperty("outputs")[0];
        Assert.Equal(33, o.GetProperty("latencyMs").GetDouble());
        Assert.Equal(JsonValueKind.Null, o.GetProperty("present").ValueKind);
        Assert.Equal("free-unknown", o.GetProperty("lock").GetProperty("state").GetString());
        Assert.Equal(33, s.GetProperty("sync").GetProperty("latencyMs").GetDouble());
        Assert.Equal("none", s.GetProperty("sync").GetProperty("lock").GetProperty("driver").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("machine").GetProperty("present").ValueKind);
    }

    [Fact]
    public async Task TheGroupLatencyIsTheSettingOrTheLargestOutputLatency()
    {
        var project = PresentRig.With(PresentRig.Screen("op_a") with { LatencyMs = 20 }, PresentRig.Screen("op_b") with { LatencyMs = 48 });
        await using (var rig = PresentRig.Start(project))
            Assert.Equal(48, rig.State().GetProperty("sync").GetProperty("latencyMs").GetDouble());
        await using (var set = PresentRig.Start(project with { Settings = project.Settings with { SyncLatencyMs = 60 } }))
            Assert.Equal(60, set.State().GetProperty("sync").GetProperty("latencyMs").GetDouble());
    }

    [Fact]
    public async Task AnOutputLatencyOutOfRangeIsRefusedWithWords()
    {
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")));
        var r = await rig.Run("SET op_a latencyMs 900");
        Assert.False(r.Ok);
        Assert.Contains("latencyMs is 0–500 ms", r.Payload, StringComparison.Ordinal);
    }
}

/// <summary>The real render host's presentation, headless: each output counted against its own refresh, published in STATE.</summary>
[Collection("render host")]
public sealed class RenderHostPresentTests
{
    [Fact]
    public async Task AHeadlessOutputIsCountedOffscreenAgainstItsOwnRefresh()
    {
        var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#0000FF"), 64, 36) with { RefreshHz = 50 });
        await using var rig = HostRig.Start(project);
        await rig.Host.FramesAsync(60).WaitAsync(HostRig.Patience);
        rig.Engine.FactsChanged();
        using var doc = JsonDocument.Parse(rig.Engine.State().Json);
        var present = doc.RootElement.GetProperty("outputs")[0].GetProperty("present");
        Assert.Equal("offscreen", present.GetProperty("path").GetString());
        Assert.Equal("offscreen", present.GetProperty("mode").GetString());
        Assert.Equal(50, present.GetProperty("nominalHz").GetDouble());
        Assert.Equal("nominal", present.GetProperty("phase").GetString());
        Assert.True(present.GetProperty("presented").GetInt64() >= 60);
        Assert.StartsWith("50 Hz target · offscreen", present.GetProperty("words").GetString(), StringComparison.Ordinal);
        var machine = doc.RootElement.GetProperty("machine").GetProperty("present");
        Assert.Equal("gl", machine.GetProperty("api").GetString());
        Assert.Equal("offscreen", machine.GetProperty("path").GetString());
        Assert.Equal("core330", machine.GetProperty("dialect").GetString());
        Assert.Equal(HostRig.NativeContext, machine.GetProperty("context").GetString());
        var lockJson = doc.RootElement.GetProperty("outputs")[0].GetProperty("lock");
        Assert.Equal("free-unknown", lockJson.GetProperty("state").GetString());
        Assert.Equal("none", doc.RootElement.GetProperty("sync").GetProperty("lock").GetProperty("driver").GetString());
    }

    [Fact]
    public async Task ANodeAskedForEs300RendersOnAnEsContextAndSaysSo()
    {
        var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#0000FF"), 64, 36));
        await using var rig = HostRig.Start(project, options: RenderHostOptions.Default with
        {
            Windows = false,
            Platform = new PlatformRequest(Dialect: "es300"),
        });
        Assert.StartsWith("OpenGL ES 3.", rig.Host.RendererName, StringComparison.Ordinal);
        Assert.Equal((0, 0, 255), HostRig.At(await rig.Read("op_1"), 64, 10, 10));
        var machine = rig.Host.Machine();
        Assert.Equal("es300", machine.Dialect);
        // On Linux ES is made on EGL (or on GLX, saying so); elsewhere the window system's own API makes it and has nothing to say.
        if (OperatingSystem.IsLinux())
            Assert.True(machine.Context == "egl" || machine.Why.StartsWith("ES through GLX", StringComparison.Ordinal), $"{machine.Context}: {machine.Why}");
        else
            Assert.Equal((HostRig.NativeContext, ""), (machine.Context, machine.Why));
    }

    [Fact]
    public async Task PresentResetZeroesTheCountersAndJournalsTheOldTotals()
    {
        var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Solid("#0000FF"), 64, 36));
        await using var rig = HostRig.Start(project);
        await rig.Host.FramesAsync(30).WaitAsync(HostRig.Patience);
        var before = rig.Host.Present().Single().Present.Presented;
        Assert.True(before >= 30);
        var r = await rig.Run("PRESENT RESET op_1");
        Assert.True(r.Ok, r.Payload);
        using (var doc = JsonDocument.Parse(r.Payload!))
            Assert.True(doc.RootElement[0].GetProperty("presented").GetInt64() >= before);
        Assert.Contains(rig.Engine.Journal.Recent(20), j => j.Kind == "present" && j.Text.StartsWith("op_1 counters reset", StringComparison.Ordinal));
        // The next frame publishes the counters counted since the reset.
        await rig.Host.FramesAsync(2).WaitAsync(HostRig.Patience);
        Assert.True(rig.Host.Present().Single().Present.Presented < before);
        var unknown = await rig.Run("PRESENT RESET op_x");
        Assert.False(unknown.Ok);
        Assert.StartsWith("no output 'op_x'", unknown.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresentResetWithNothingPresentedIsRefusedAndOutputStatsAnswersEveryOutput()
    {
        var outputs = new ScriptedOutputs().WithFreeLocks("op_a");
        outputs.Clocks["op_a"] = new PresentClock(60, measured: false, PresentPath.Offscreen);
        await using var rig = PresentRig.Start(PresentRig.With(PresentRig.Screen("op_a")), outputs);
        var r = await rig.Run("PRESENT RESET op_a");
        Assert.False(r.Ok);
        Assert.StartsWith("nothing to reset", r.Payload, StringComparison.Ordinal);

        var stats = await rig.Run("OUTPUT STATS");
        Assert.True(stats.Ok, stats.Payload);
        using var doc = JsonDocument.Parse(stats.Payload!);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("op_a", row.GetProperty("id").GetString());
        Assert.Equal("offscreen", row.GetProperty("present").GetProperty("path").GetString());
        Assert.Equal("free-measured", row.GetProperty("lock").GetProperty("state").GetString());
        Assert.False((await rig.Run("OUTPUT STATS nope")).Ok);
    }
}
