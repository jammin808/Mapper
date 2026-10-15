using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Optimise;
using PlayMap.Engine.Tests.Align;
using PlayMap.Engine.Tests.Fleet;
using PlayMap.Engine.Tests.Rendering;

namespace PlayMap.Engine.Tests.Optimise;

/// <summary>The optimise service on an engine with no worker started (nothing is queued): the governor in STATE as the show plays,
/// arms and stops; the preflight check with a fault injected (a 4K clip the show decodes on the CPU); the verbs' refusals and the
/// project check of an item's use.</summary>
public sealed class OptimiseEngineTests : IAsyncDisposable
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("playmap-optimise-engine-");
    private readonly ManualClock _clock = new(5_000_000_000);
    private readonly SwitchGate _gate = new();
    private readonly OptimiseService _service;
    private readonly EngineCore _engine;

    public OptimiseEngineTests() : this(EyeContextTests.Busy())
    {
    }

    private OptimiseEngineTests(Project project)
    {
        _service = new OptimiseService(new NodeHome(_dir.FullName), OptimiseNodeSettings.Default, NullLogger.Instance,
            new OptimiseOptions { Worker = new WorkerLaunch("/nonexistent/worker", []), Step = TimeSpan.FromHours(1) });
        _engine = new EngineCore(EngineServices.ForTests(_clock) with { Optimise = _service, ShowGate = _gate }, project);
        _service.Attach(_engine);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        await _engine.DisposeAsync();
        _dir.Delete(recursive: true);
    }

    private async Task<CommandReply> Run(string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await _engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    private JsonElement Optimise()
    {
        _engine.FactsChanged();
        using var doc = JsonDocument.Parse(_engine.State().Json);
        return doc.RootElement.GetProperty("media").GetProperty("optimise").Clone();
    }

    [Fact]
    public async Task TheGovernorTricklesWhileTheShowPlaysWaitsWhileArmedAndRunsTenSecondsAfterItStops()
    {
        _service.StepOnce();
        Assert.Equal(("run", "ask"), (Optimise().GetProperty("governor").GetString(), Optimise().GetProperty("mode").GetString()));
        Assert.True((await Run("PLAYLIST GO pl_show")).Ok);
        Assert.True(_engine.IsAnimating);
        _service.StepOnce();
        Assert.Equal(("trickle", "a show plays: one stream, GPU ≤ 15 %"), (Optimise().GetProperty("governor").GetString(), Optimise().GetProperty("words").GetString()));
        _gate.IsArmed = true;
        _service.StepOnce();
        Assert.Equal("waiting", Optimise().GetProperty("governor").GetString());
        _gate.IsArmed = false;
        Assert.True((await Run("VIDEO STOP ON pl_show")).Ok);
        _clock.Advance(9_000_000_000);
        _service.StepOnce();
        Assert.Equal("trickle", Optimise().GetProperty("governor").GetString());
        _clock.Advance(1_000_000_000);
        _service.StepOnce();
        Assert.Equal("run", Optimise().GetProperty("governor").GetString());
        Assert.True(Optimise().GetProperty("worker").GetProperty("pid").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task AFourKClipTheShowDecodesOnTheCpuIsAmberWithTheLineThatMakesAVersion()
    {
        var clip = new MediaItem("md_4k", "Facade", MediaKind.Video, "/nowhere/facade.mp4", "", 0,
            MediaInfo.Unknown with { Width = 3840, Height = 2160, VideoCodec = "h264", FrameRate = "60" }, []);
        var small = clip with { Id = "md_hd", Name = "Side", Info = clip.Info with { Width = 1920, Height = 1080 } };
        var unshown = clip with { Id = "md_lib", Name = "Library" };
        var check = new OptimisePreflight();
        var green = Assert.Single(await check.RunAsync(new PreflightContext(_engine, PreflightDepth.Quick, false), CancellationToken.None));
        Assert.Equal(("optimise.cpu", Light.Green, (string?)null), (green.Id, green.Light, green.Fix));

        // The fault: the show plays a 4K H.264 on this node.
        var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Media("md_4k"), 1920, 1080), HostRig.Screen("op_2", SourceRef.Media("md_hd"), 1920, 1080))
            with { Media = [clip, small, unshown] };
        await using var engine = new EngineCore(EngineServices.ForTests(), project);
        var rows = await check.RunAsync(new PreflightContext(engine, PreflightDepth.Quick, false), CancellationToken.None);
        var amber = Assert.Single(rows);
        Assert.Equal(("optimise.cpu:md_4k", "optimise", Light.Amber, "MEDIA OPTIMISE md_4k TO hap-q"), (amber.Id, amber.Section, amber.Light, amber.Fix));
        Assert.Equal("Facade is 3840×2160 h264: this node decodes it on the CPU — a HAP Q version plays on the GPU", amber.Note);
    }

    [Fact]
    public async Task TheNodeComposesTheServiceAndItsCheckFromTheOptimiseExtension()
    {
        var parts = global::PlayMap.Engine.Host.EngineParts.Bare with
        {
            Extensions = [setup => new global::PlayMap.Engine.Host.OptimiseExtension(setup, new OptimiseOptions { Worker = new WorkerLaunch("/nonexistent/worker", []) })],
        };
        await using var rig = await PlayMap.Engine.Tests.Host.HostRig.StartAsync(parts: parts);
        Assert.NotNull(rig.Engine.Services.Optimise);
        Assert.Contains(rig.Engine.Services.PreflightChecks, c => c is OptimisePreflight);
        var optimise = rig.State().GetProperty("media").GetProperty("optimise");
        Assert.Equal(("ask", "run", 0), (optimise.GetProperty("mode").GetString(), optimise.GetProperty("governor").GetString(), optimise.GetProperty("queued").GetInt32()));
    }

    [Fact]
    public async Task TheVerbsRefuseWithTheirWords()
    {
        Assert.Equal("MEDIA RULE MODE is AUTO or NEVER, not ASK (ASK, with its suggestions, arrives next round)", (await Run("MEDIA RULE MODE ASK")).Payload);
        Assert.True((await Run("MEDIA RULE MODE NEVER")).Ok);
        Assert.Equal("the optimise mode is never already", (await Run("MEDIA RULE MODE NEVER")).Payload);
        Assert.Equal("no media md_none", (await Run("MEDIA OPTIMISE md_none")).Payload);
        Assert.StartsWith("TO rechunk: the lossless HAP rewriter arrives next round", (await Run("MEDIA OPTIMISE ALL TO rechunk")).Payload, StringComparison.Ordinal);
        Assert.Equal("QUALITY is FAST or SHOW, not ARCHIVE (Archive arrives next round)", (await Run("MEDIA OPTIMISE ALL QUALITY ARCHIVE")).Payload);
        Assert.Equal("no job is queued or running", (await Run("MEDIA OPTIMISE CANCEL")).Payload);
        Assert.Equal("no job is paused", (await Run("MEDIA OPTIMISE RESUME ALL")).Payload);
        using (var jobs = JsonDocument.Parse((await Run("MEDIA JOBS")).Payload!))
            Assert.Equal(("never", 0), (jobs.RootElement.GetProperty("mode").GetString(), jobs.RootElement.GetProperty("jobs").GetArrayLength()));
        using (var rules = JsonDocument.Parse((await Run("MEDIA RULES")).Payload!))
            Assert.Equal("hap-keep", rules.RootElement.GetProperty("builtIn")[0].GetString());
    }

    [Fact]
    public async Task AnItemsUseIsAutoSourceOrOneOfItsVersions()
    {
        var item = new MediaItem("md_v", "Clip", MediaKind.Video, "/nowhere/clip.mp4", "sha256:" + new string('1', 64), 10,
            MediaInfo.Unknown with { VideoCodec = "h264", Width = 1920, Height = 1080 }, []);
        var version = new MediaVersion("mv_1", "hap-q", Core.Media.Optimise.Recipe.For("hap-q", EncodeQuality.Show).Canonical(), "ok:x",
            "sha256:" + new string('2', 64), 20, item.Info with { VideoCodec = "hap_q" }, new VersionQuality(44, 43, 3, 0), "hapenc/1", "0badc0de", 1);
        await using var engine = new EngineCore(EngineServices.ForTests(), Project.CreateDefault("Use") with { Media = [item with { Versions = [version] }] });
        Assert.True(WireGrammar.TryParse("MEDIA USE md_v mv_1", out var use, out _));
        var r = await engine.HandleAsync(use, Desk, CancellationToken.None);
        Assert.True(r.Ok, r.Payload);
        Assert.Equal("mv_1", engine.Store.Project.FindMedia("md_v")!.Use);
        Assert.True(WireGrammar.TryParse("MEDIA USE md_v mv_9", out var bad, out _));
        Assert.StartsWith("mv_9 is none of AUTO, SOURCE or a version of Clip", (await engine.HandleAsync(bad, Desk, CancellationToken.None)).Payload, StringComparison.Ordinal);
        Assert.True(WireGrammar.TryParse("SET md_v use \"mv_9\"", out var set, out _));
        var refused = await engine.HandleAsync(set, Desk, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("use is auto, source or one of its versions, not \"mv_9\"", refused.Payload, StringComparison.Ordinal);
        Assert.True(WireGrammar.TryParse("MEDIA VERSIONS md_v", out var versions, out _));
        using var v = JsonDocument.Parse((await engine.HandleAsync(versions, Desk, CancellationToken.None)).Payload!);
        Assert.Equal(("mv_1", "source"), (v.RootElement.GetProperty("use").GetString(), v.RootElement.GetProperty("playsHere").GetString()));
        Assert.Equal("mv_1's file is not on this node", v.RootElement.GetProperty("versions")[0].GetProperty("unplayable").GetString());
        // A bare core has no service: the queue's verbs say so.
        Assert.True(WireGrammar.TryParse("MEDIA JOBS", out var jobs, out _));
        Assert.Equal("optimise is not running on this node", (await engine.HandleAsync(jobs, Desk, CancellationToken.None)).Payload);
    }
}
