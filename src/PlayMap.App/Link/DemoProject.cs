using System.Collections.Immutable;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Link;

/// <summary>The show the demo engine opens with: a small room (a back wall, a cube, a column) lit by two
/// projectors, a media library with one file missing, three playlists, a few generators and three outputs —
/// enough that every panel of the desk has something real to show and edit. Deterministic: the same
/// project every time.</summary>
public static class DemoProject
{
    public const long Second = 1_000_000_000;

    public static Project Create()
    {
        var media = ImmutableArray.Create(
            Video("md_intro", "Intro.mp4", "/shows/demo/Intro.mp4", 210, "h264"),
            Video("md_main", "Main loop.mov", "/shows/demo/Main loop.mov", 300, "prores"),
            Video("md_outro", "Outro.mp4", "/shows/demo/Outro.mp4", 80, "h264"),
            new MediaItem("md_logo", "Logo.png", MediaKind.Image, "/shows/demo/Logo.png", Hash('4'), 812_000,
                MediaInfo.Unknown with { Width = 1920, Height = 1080, Container = "png", HasAlpha = true }, ["brand"]),
            new MediaItem("md_amb", "Ambience.wav", MediaKind.Audio, "/shows/demo/Ambience.wav", Hash('5'), 52_000_000,
                MediaInfo.Unknown with { DurationNs = 600 * Second, AudioCodec = "pcm_s24le", AudioChannels = 2, AudioRate = 48000, Container = "wav" }, []),
            Video("md_spare", "Spare angle.mp4", "/shows/demo/missing/Spare angle.mp4", 95, "hevc"));

        var generators = ImmutableArray.Create(
            new Generator("gn_testcard", "Test card", "pattern.testcard", ParamSet.Empty),
            new Generator("gn_snow", "Snow", "particles.field", ParamSet.Empty.With("preset", "Snow").With("count", 1200)),
            new Generator("gn_julia", "Julia drift", "fractal.julia", ParamSet.Empty.With("zoom", 1.4).With("speed", 0.6)),
            new Generator("gn_sweep", "Sweep", "effect.sweep", ParamSet.Empty.With("speed", 0.8).With("color", "#7FD4FF")));

        var playlists = ImmutableArray.Create(
            new Playlist("pl_show", "Show",
            [
                PlaylistItem.For("it_intro", SourceRef.Media("md_intro"), "Intro") with { Transition = TransitionKind.Fade, TransitionNs = Second },
                PlaylistItem.For("it_main", SourceRef.Media("md_main"), "Main loop") with { End = EndAction.Loop },
                PlaylistItem.For("it_outro", SourceRef.Media("md_outro"), "Outro") with { End = EndAction.Stop },
            ], false, "show"),
            new Playlist("pl_amb", "Ambient",
            [
                PlaylistItem.For("it_snow", SourceRef.Generator("gn_snow"), "Snow") with { DurationNs = 60 * Second },
                PlaylistItem.For("it_julia", SourceRef.Generator("gn_julia"), "Julia drift") with { DurationNs = 90 * Second, Transition = TransitionKind.Fade, TransitionNs = 2 * Second },
            ], true, null),
            new Playlist("pl_logo", "Logo",
            [
                PlaylistItem.For("it_logo", SourceRef.Media("md_logo"), "Logo") with { DurationNs = 10 * Second, End = EndAction.Hold },
            ], false, null));

        var canvas = new Canvas("cv_main", "Main canvas", 1920, 1080, "#000000",
        [
            new Layer("ly_show", "Show", SourceRef.Playlist("pl_show"), new RectD(0, 0, 1920, 1080), 1, LayerBlend.Normal, true),
            new Layer("ly_logo", "Logo", SourceRef.Playlist("pl_logo"), new RectD(1520, 60, 340, 190), 1, LayerBlend.Normal, true),
        ]);

        var meshes = ImmutableArray.Create(
            new MeshAsset("ms_wall", "Back wall", MeshOrigin.Primitive, "", "", "plane", ParamSet.Empty.With("width", 4.0).With("height", 2.5), 4, 2),
            new MeshAsset("ms_cube", "Cube", MeshOrigin.Primitive, "", "", "box", ParamSet.Empty.With("width", 0.8).With("height", 0.8).With("depth", 0.8), 24, 12),
            new MeshAsset("ms_column", "Column", MeshOrigin.Primitive, "", "", "cylinder", ParamSet.Empty.With("radius", 0.22).With("height", 2.2).With("segments", 24), 50, 48),
            new MeshAsset("ms_scan", "Room scan", MeshOrigin.Scanned, "scans/sc_first/mesh.ply", Hash('9'), "", ParamSet.Empty, 184_220, 366_118));

        var surfaces = ImmutableArray.Create(
            new Surface("sf_wall", "Back wall", "ms_wall", new Transform3(new Vec3d(0, 1.25, 0), Vec3d.Zero, new Vec3d(1, 1, 1)), [],
                new SurfaceContent(SurfaceMapping.CanvasRegion, SourceRef.Canvas("cv_main"), new RectD(0, 0, 1920, 1080), null), 1,
                [new Mask("mk_door", "Door cut-out", BezierPath.Rectangle(0.72, 0.35, 0.14, 0.65), MaskMode.Subtract, 0.01, false, true)],
                true, false, "#4FC3F7", ["wall"]),
            new Surface("sf_cube", "Cube", "ms_cube", new Transform3(new Vec3d(-0.9, 0.4, 1.0), new Vec3d(0, 0.45, 0), new Vec3d(1, 1, 1)), [],
                new SurfaceContent(SurfaceMapping.Uv, SourceRef.Generator("gn_sweep"), new RectD(0, 0, 1, 1), null), 1, [], true, false, "#FFB74D", ["object"]),
            new Surface("sf_column", "Column", "ms_column", new Transform3(new Vec3d(1.1, 1.1, 0.9), Vec3d.Zero, new Vec3d(1, 1, 1)), [],
                new SurfaceContent(SurfaceMapping.Uv, SourceRef.Playlist("pl_amb"), new RectD(0, 0, 1, 1), null), 0.85, [], true, true, "#A5D6A7", ["column"]));

        var left = MakeProjector("pj_left", "Left", new Vec3d(-2.4, 1.7, 5.2), new Vec3d(-0.4, 1.0, 0.3), "#4FC3F7");
        var right = MakeProjector("pj_right", "Right", new Vec3d(2.5, 1.8, 5.0), new Vec3d(0.5, 1.0, 0.3), "#FFB74D");
        left = left with { Pins = DemoPins(left), Locks = SolveLocks.Shift, SolveResidualPx = 1.4 };

        var stage = new Stage(
            surfaces,
            meshes,
            [left, right],
            [new CameraRig("cm_webcam", "USB camera", CameraSourceKind.Device, "video0", Intrinsics.FromFieldOfView(1920, 1080, 70), LensDistortion.None,
                Pose.LookAt(new Vec3d(-2.2, 1.6, 5.0), new Vec3d(0, 1, 0), Vec3d.UnitY), false)],
            [new Viewpoint("vp_audience", "Audience", Intrinsics.FromFieldOfView(1920, 1080, 55), Pose.LookAt(new Vec3d(0, 1.6, 7), new Vec3d(0, 1.1, 0), Vec3d.UnitY))],
            [new ScanRecord("sc_first", "First scan", ScanKind.StructuredLight, 1_760_000_000_000, "cm_webcam", ["pj_left", "pj_right"], "scans/sc_first",
                "ms_scan", 0.8, 184_220, "Robust mode, lights off")]);

        var rest = WarpLattice.AtRest(5, 4);
        var lattice = rest with { Offsets = rest.Offsets.SetItem(6, new Vec2d(0.006, -0.004)).SetItem(13, new Vec2d(-0.005, 0.003)) };
        var outputs = ImmutableArray.Create(
            new Output("op_1", "Left projector", OutputKind.Display, "DISPLAY2", 1920, 1080, 60, OutputMode.Projector, "pj_left", SourceRef.None,
                new RectD(0, 0, 1920, 1080), new OutputWarp(true, CornerPin.None, lattice), BlendSettings.Default, ColorTrim.Neutral,
                [new Mask("mk_flare", "Lens flare trim", new BezierPath(
                [
                    BezierNode.Corner(0.04, 0.05), new BezierNode(new Vec2d(0.22, 0.03), new Vec2d(-0.05, 0), new Vec2d(0.05, 0), true),
                    BezierNode.Corner(0.3, 0.12), new BezierNode(new Vec2d(0.12, 0.2), new Vec2d(0.06, 0.02), new Vec2d(-0.06, -0.02), true),
                ], true), MaskMode.Subtract, 0.02, false, true)],
                new RectD(0, 0, 1920, 1080), 0, true),
            new Output("op_2", "Right projector", OutputKind.Display, "DISPLAY3", 1920, 1080, 60, OutputMode.Projector, "pj_right", SourceRef.None,
                new RectD(0, 0, 1920, 1080), OutputWarp.None, BlendSettings.Default, ColorTrim.Neutral, [], new RectD(1920, 0, 1920, 1080), 0, true),
            new Output("op_3", "Confidence", OutputKind.Window, "", 1280, 720, 60, OutputMode.Canvas, null, SourceRef.Canvas("cv_main"),
                new RectD(0, 0, 1920, 1080), OutputWarp.None, BlendSettings.Default with { Auto3D = false }, ColorTrim.Neutral, [],
                new RectD(960, 1200, 1280, 720), 0, false));

        return new Project("pr_demo", "Demo show", Project.CurrentSchema, media, playlists, generators, [canvas], stage, outputs,
            ProjectSettings.Default with { DefaultTransition = TransitionKind.Fade, DefaultTransitionNs = Second / 2 });
    }

    private static string Hash(char c) => "sha256:" + new string(c, 64);

    private static MediaItem Video(string id, string name, string path, int seconds, string codec) =>
        new(id, name, MediaKind.Video, path, Hash(id[^1]), seconds * 24_000_000L,
            new MediaInfo(seconds * Second, 1920, 1080, "25", codec, codec == "prores" ? "yuv422p10le" : "yuv420p", "aac", 2, 48000, false,
                path.EndsWith(".mov", StringComparison.Ordinal) ? "mov" : "mp4"), []);

    private static Projector MakeProjector(string id, string name, Vec3d eye, Vec3d target, string color) =>
        new(id, name, Intrinsics.FromThrowRatio(1920, 1080, 1.35, 0, -0.1), LensDistortion.None, Pose.LookAt(eye, target, Vec3d.UnitY),
            6500, color, false, [], SolveLocks.None, 0);

    /// <summary>Eight pins on the wall's and the cube's corners, placed where the projector's current pose
    /// puts them plus a deliberate miss on two, so the pin table shows good, fair and poor residuals.</summary>
    private static ImmutableArray<CalibrationPin> DemoPins(Projector pj)
    {
        Vec3d[] world =
        [
            new(-2, 0, 0), new(2, 0, 0), new(2, 2.5, 0), new(-2, 2.5, 0),
            new(-0.6, 2.5, 0), new(-1.3, 0.8, 1.2), new(-0.5, 0.8, 0.6), new(0.4, 1.4, 0),
        ];
        Vec2d[] miss = [new(0.2, -0.1), new(-0.3, 0.2), new(0.1, 0.4), new(0.9, -1.1), new(-0.2, 0.1), new(3.1, 2.2), new(0.4, -0.2), new(-0.1, -0.3)];
        var pins = ImmutableArray.CreateBuilder<CalibrationPin>();
        for (var i = 0; i < world.Length; i++)
        {
            var px = CameraModel.Project(world[i], pj.Intrinsics, pj.Lens, pj.Pose) ?? new Vec2d(960, 540);
            pins.Add(new CalibrationPin("pn_" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), world[i], px + miss[i], true));
        }
        return pins.ToImmutable();
    }
}
