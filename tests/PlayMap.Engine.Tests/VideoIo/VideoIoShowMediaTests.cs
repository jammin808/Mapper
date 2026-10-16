using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Optimise;
using PlayMap.Engine.Show;
using PlayMap.Engine.Tests.Rendering;

namespace PlayMap.Engine.Tests.VideoIo;

/// <summary>What the show plays, as preflight's media check and optimise's check count it: a file a send taps and a file a live
/// input on the air falls back to are played by this node as surely as an output's source, so a missing one is red and a 4K one
/// decoded on the CPU is amber; a live input itself is no file and no clip — never checked as one, never a version to make.</summary>
public sealed class VideoIoShowMediaTests
{
    /// <summary>Every item's readiness one value the test sets.</summary>
    private sealed class Backend(MediaReadiness readiness) : IMediaBackend
    {
        public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException("tests import nothing");

        public IVideoSource? Open(MediaItem item) => null;

        public MediaReadiness Readiness(MediaItem item) => readiness;

        public string? PathForHash(string contentHash) => null;
    }

    private static readonly MediaItem Standby = new("md_standby", "Standby", MediaKind.Video, "/nowhere/standby.mov", "", 0, MediaInfo.Unknown, []);

    private static readonly MediaItem Logo = new("md_logo", "Logo", MediaKind.Video, "/nowhere/logo.mov", "", 0, MediaInfo.Unknown, []);

    private static readonly MediaItem Library = new("md_lib", "Library", MediaKind.Video, "/nowhere/library.mov", "", 0, MediaInfo.Unknown, []);

    /// <summary>A live input that falls back to <paramref name="fallback"/> when its feed is lost.</summary>
    private static MediaItem Camera(string fallback) => new("md_cam", "Cam", MediaKind.Stream, "omt:STUDIO (Cam 1)", "", 0,
        MediaInfo.Unknown with { Width = 3840, Height = 2160, VideoCodec = "h264" }, ["live"])
    {
        Live = LiveSettings.Default with { OnLoss = LiveLoss.Fallback, Fallback = fallback },
    };

    private static VideoSend Send(string tap, bool enabled = true) =>
        new("sd_1", "Out", SendProtocol.Omt, tap, 0, 0, 0, SendFormat.Uyvy, enabled);

    private static async Task<IReadOnlyList<PreflightRow>> Rows(IPreflightCheck check, Project project, IMediaBackend? media = null)
    {
        var services = EngineServices.ForTests();
        await using var engine = new EngineCore(media is null ? services : services with { Media = media }, project);
        return await check.RunAsync(new PreflightContext(engine, PreflightDepth.Quick, false), CancellationToken.None);
    }

    [Fact]
    public async Task AFileASendTapsOrALiveInputFallsBackToIsMediaThePreflightChecks()
    {
        var missing = new Backend(new MediaReadiness(false, false, false, false, "missing"));
        // The camera is on the air on op_1 and falls back to Standby; the send carries Logo; no output shows either file.
        var project = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Media("md_cam"), 64, 36)) with
        {
            Media = [Camera("media:md_standby"), Standby, Logo, Library],
            Sends = [Send("source:media:md_logo")],
        };
        var rows = await Rows(new MediaCheck(), project, missing);
        Assert.Equal(["media:md_logo", "media:md_standby"], rows.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.All(rows, r => Assert.Equal(Light.Red, r.Light));
        Assert.Contains(rows, r => r.Fix == "MEDIA FETCH md_standby");
        // A send that is off plays nothing; a fallback the policy does not use (the input holds its last picture) is not shown.
        var idle = project with
        {
            Media = [Camera("") with { Live = LiveSettings.Default with { OnLoss = LiveLoss.Hold } }, Standby, Logo, Library],
            Sends = [Send("source:media:md_logo", enabled: false)],
        };
        var none = Assert.Single(await Rows(new MediaCheck(), idle, missing));
        Assert.Equal("media:none", none.Id);
    }

    [Fact]
    public async Task AFourKClipASendTapsOrALiveInputFallsBackToIsAmberAndTheLiveInputItselfNeverIs()
    {
        var clip = new MediaItem("md_4k", "Facade", MediaKind.Video, "/nowhere/facade.mp4", "", 0,
            MediaInfo.Unknown with { Width = 3840, Height = 2160, VideoCodec = "h264", FrameRate = "60" }, []);
        var check = new OptimisePreflight();
        async Task<PreflightRow> Row(Project p) => Assert.Single(await Rows(check, p));

        // A send of the clip, and of a canvas whose layer shows it; no output shows either.
        var bySend = Project.CreateDefault("Send") with { Media = [clip], Sends = [Send("source:media:md_4k")] };
        var s = await Row(bySend);
        Assert.Equal(("optimise.cpu:md_4k", Light.Amber, "MEDIA OPTIMISE md_4k TO hap-q"), (s.Id, s.Light, s.Fix));
        var canvas = new Canvas("cv00000001", "Wall", 64, 32, "#000000",
            [new Layer("ly_clip", "Clip", SourceRef.Media("md_4k"), new RectD(0, 0, 64, 32), 1, LayerBlend.Normal, true)]);
        var c = await Row(bySend with { Canvases = [canvas], Sends = [Send("canvas:cv00000001")] });
        Assert.Equal(("optimise.cpu:md_4k", Light.Amber), (c.Id, c.Light));
        // A send that is off plays nothing.
        Assert.Equal(Light.Green, (await Row(bySend with { Sends = [Send("source:media:md_4k", enabled: false)] })).Light);

        // A live input on the air that falls back to the clip: the clip is amber; the input (a stream, whatever size it says) is no clip.
        var byFallback = HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Media("md_cam"), 64, 36)) with { Media = [Camera("media:md_4k"), clip] };
        var f = await Row(byFallback);
        Assert.Equal(("optimise.cpu:md_4k", Light.Amber), (f.Id, f.Light));
        var live = await Row(byFallback with { Media = [Camera("media:md_4k") with { Live = null }, clip] });
        Assert.Equal(("optimise.cpu", Light.Green), (live.Id, live.Light));
    }
}
