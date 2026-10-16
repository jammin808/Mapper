using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Model;

/// <summary>A project holding one of every round-2 entity kind and every round-2 nested record, each field set away from its
/// default, so a round trip that dropped or mangled one would show.</summary>
internal static class RoundTwoSample
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root holds PlayMap.sln");
    }

    public static string RoundOneFile() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "tests", "fixtures", "projects", "round1.playmap.json"));

    private static readonly ParamBinding Bass = new("opacity", "audio:bass~10/250", 0.5, 0.1, BindingCurve.Ease, 0.2)
    {
        Mode = BindingMode.Scale, AttackSeconds = 0.05, ReleaseSeconds = 0.4, RangeMin = 0.2, RangeMax = 0.9, Invert = true, Steps = 4,
        AnchorNs = 77, Enabled = false,
    };

    private static FxSlot Slot(string id) =>
        new(id, "fx.blur", "Blur " + id, ParamSet.Empty.With("radius", 4), true, 0.5, FxBlend.Screen, 0.5, 1_000) { Bindings = [Bass] };

    public static WarpLattice Lattice() => WarpLattice.AtRest(2, 2) with { Offsets = [new Vec2d(0.1, 0), Vec2d.Zero, Vec2d.Zero, new Vec2d(0, -0.1)] };

    public static MeasuredCorrection Measured() => new(new MeasuredWarp(33, 19, "align/op_1.pfm", "sha256:1", 0.4), Lattice(),
        new MeasuredBlend(480, 270, "align/op_1.blend", "sha256:2"), new MeasuredColor(0.98, 1, 1.02, 2.2, 0.01), 0.6, "sc1", "sk0a0b0c01",
        "ar0a0b0c01", 1_760_000_000_000)
    {
        FittedTo = new FittedPoses(new Pose(new Vec3d(0.001, 0, 0), new Vec3d(0, 0.0002, 0.009)), Intrinsics.FromThrowRatio(1920, 1080, 1.5),
            LensDistortion.None, Intrinsics.FromThrowRatio(1920, 1080, 1.505), new LensDistortion(0.01, 0, 0, 0, 0)),
    };

    public static Project Build()
    {
        var p = ProjectJson.Deserialize(RoundOneFile());
        var generator = p.Generators[1] with
        {
            OriginNs = 5_000_000_000,
            Bindings = [Bass],
            Morph = new ParamMorph(ParamSet.Empty.With("zoom", 1), 10, 2_000_000_000, MorphCurve.Ease),
            Inputs = ImmutableSortedDictionary.Create<string, SourceRef>(StringComparer.Ordinal).Add("source", SourceRef.Media("md0a0b0c02")),
        };
        var list = p.Playlists[0] with
        {
            Chase = new TimecodeChase(["tc0a0b0c01"]) { DelayNs = 40_000_000, OnLoss = ChaseLoss.Stop, FreewheelNs = 1, JumpNs = 2, Gap = ChaseGap.Hold },
            Items = p.Playlists[0].Items.SetItem(0, p.Playlists[0].Items[0] with { TcIn = "01:00:00:00", Fx = new FxChain([Slot("fx0a0b0c01")]) }),
        };
        var canvas = p.Canvases[0] with
        {
            Fx = new FxChain([Slot("fx0a0b0c02")]),
            Layers = p.Canvases[0].Layers.SetItem(1, p.Canvases[0].Layers[1] with { Fx = new FxChain([Slot("fx0a0b0c03")]), Bindings = [Bass] }),
        };
        var media = p.Media[0] with
        {
            Color = new ColorOverride("pq", 203, "full", "bt2020"),
            Use = "mv0a0b0c01",
            Live = LiveSettings.Default with { Pacing = LivePacing.Locked, Backup = "ndi:B (Cam 2)", OnLoss = LiveLoss.Fallback, Fallback = "color:#000000" },
            Versions =
            [
                new MediaVersion("mv0a0b0c01", "hap-q", "{\"to\":\"hap-q\"}", "ok:" + new string('c', 32), "sha256:" + new string('d', 64), 9_999,
                    MediaInfo.Unknown with { VideoCodec = "hap" }, new VersionQuality(48.5, 41.2, 32, 12_000), "playmap-hap 1", "nd1", 1_760_000_000_001) { Review = true },
            ],
        };
        var surface = p.Stage.Surfaces[0] with
        {
            Fx = new FxChain([Slot("fx0a0b0c04")]),
            Bindings = [Bass],
            Pixel = new PixelQuad("pj0a0b0c01", new Vec2d(0.1, 0.1), new Vec2d(0.9, 0.1), new Vec2d(0.9, 0.9), new Vec2d(0.1, 0.9), Lattice()),
        };
        var projector = p.Stage.Projectors[0] with
        {
            Priors = new SolvePriors(1.5, 3, new Vec3d(0, 2, -8), "cp0a0b0c01", 0.05, "datasheet"),
            Uncertainty = new SolveUncertainty(0.8, 0.3, 1.2, 0.04, 0.01, 1e4, "zoom↔distance"),
            CalibrationId = "cs0a0b0c01", FallbackId = "cs0a0b0c01", StagedId = null, FocalLock = FocalLockSource.Lens,
            Pins = p.Stage.Projectors[0].Pins.SetItem(0, p.Stage.Projectors[0].Pins[0] with { ControlId = "cp0a0b0c01" }),
        };
        var camera = p.Stage.Cameras[0] with
        {
            Phone = new PhoneProfile("ios-safari", "iPhone", "Back Camera", 1920, 1440, "steady"),
            Folder = new FolderCapture("/shots", FolderAdvance.Manual, 250),
        };
        var device = p.Stage.Devices[0] with
        {
            Snapshot = new NativeSnapshot(1_760_000_000_002, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal).Add("keystone", "0"),
                ["edgeblend"], true),
        };
        var snapshot = new CalibrationSnapshot("cs0a0b0c01", "First light", CalibrationTarget.Projector, "pj0a0b0c01", 1_760_000_000_003,
            CalibrationSource.Pins, null, null, new QualityScore(0.8, "px", "A", 0.7, 1.4, 0.9,
                [new ScoreTerm(ScoreTermCodes.Rms, "pins agree", 0.7, "px", 0.5, "green", "", "", "")], "good") { Source = "pins" }, "desk", true)
        {
            Projector = new ProjectorCalibration(projector.Intrinsics, projector.Lens, projector.Pose, SolveLocks.Shift | SolveLocks.Focal, projector.Pins)
            {
                FocalLock = FocalLockSource.Lens,
            },
            VerifiedP50Px = 0.6, VerifiedP95Px = 1.1, MeshHash = "sha256:3", RunId = "kt0a0b0c01", DataPath = "calibration/cs0a0b0c01",
        };
        var outputSnapshot = snapshot with { Id = "cs0a0b0c02", Target = CalibrationTarget.Output, TargetId = "op_1", Projector = null, Output = Measured() };
        var output = p.Outputs[0] with
        {
            LatencyMs = 33.3, Fx = new FxChain([Slot("fx0a0b0c05")]), Measured = Measured(),
            CalibrationId = "cs0a0b0c02", FallbackId = "cs0a0b0c02", StagedId = "cs0a0b0c02",
        };
        var stage = p.Stage with
        {
            Surfaces = [surface], Projectors = [projector], Cameras = [camera], Devices = [device],
            Site = new SiteFrame("EPSG:25832", "ETRS89 / UTM 32N", new Vec3d(500_000.123, 5_400_000.456, 120.5), 12.5, 0.9996, 50),
            Controls = [new ControlPoint("cp0a0b0c01", "CP1", new Vec3d(500_010, 5_400_020, 121), 0.005, "BOLT", true)],
            Surveys = [new SurveySource("sv0a0b0c01", "Facade", SurveyFormat.Las, "survey/facade.las", "sha256:4", "EPSG:25832", 1_000_000, 0,
                null, "survey/facade.cloud", 0.02, 0.003, SurveyState.Ready, 1_760_000_000_004, "decimated")],
            Stacks = [new StackGroup("sk0a0b0c01", "Double", ["pj0a0b0c01", "pj0a0b0c09"], "pj0a0b0c01", StackFailover.Match)],
            Aligns = [new AlignGroup("al0a0b0c01", "Screens", "cm0a0b0c01", ["op_1"], "cv_main", ScreenShape.Cylinder,
                new ScreenModel(ScreenShape.Cylinder, Vec3d.Zero, Vec3d.UnitY, Vec3d.UnitY, 4, 120, 8, 3, 0.002, true),
                new AlignTarget(CanvasFit.Corners, 16.0 / 9, [new Vec2d(0, 0), new Vec2d(1, 0), new Vec2d(1, 1), new Vec2d(0, 1)], 2),
                AlignOptions.Default with { Color = ColorMatchMode.Off, GridColumns = 17, Mode = "ROBUST" }, "ar0a0b0c01")],
            Calibrations = [snapshot, outputSnapshot],
        };
        return p with
        {
            Media = p.Media.SetItem(0, media),
            Generators = p.Generators.SetItem(1, generator),
            Playlists = [list],
            Canvases = [canvas],
            Outputs = p.Outputs.SetItem(0, output),
            Stage = stage,
            Settings = p.Settings with { SyncLatencyMs = 40, CalibrationKeep = 5, CalibrationGate = 2 },
            Show = new ShowPlan
            {
                Rules = ShowRules.Default with { MinGoMsArmed = 500, RemoteViewOnlyWhenArmed = true, DisarmFrom = "desk", DefaultOutcome = PanicOutcome.Safe, ExpectArmed = true },
                Panic = [new OutputPanicPlan("op_1", PanicOutcome.Safe, 500, SourceRef.Solid("#000000"))],
                Locks = [new ShowLock("sf0a0b0c01", LockKind.Entity, "desk", 1_760_000_000_005, "focus done"), new ShowLock(LockAreas.Mapping, LockKind.Area, "desk", 1, "")],
            },
            Fleet = new FleetPlan
            {
                Members = [new FleetMember("fm0a0b0c01", "nd2", "Backup", FleetRole.Backup, "nd1", BackupMode.Hot, 1500)],
                Succession = ["nd1", "nd2"],
            },
            Control = new ControlSet
            {
                Surfaces = [new ControlSurface("ct0a0b0c01", "APC40", "midi", "APC40*", "akai-apc40", "", true) { Merge = "htp", OnLoss = "release", AcceptPreview = true }],
                Bindings = [new ControlBinding("bd0a0b0c01", "Fader 1", "MIDI APC40 CC 1 48", "SET sf0a0b0c01 opacity *",
                    ControlShape.Default with { Behaviour = ControlBehaviour.Absolute, OutMin = 0.1, Curve = ControlCurve.SCurve, Takeover = TakeoverMode.PickUp },
                    ControlFeedback.Default with { Mode = FeedbackMode.Value, To = "MIDI APC40 CC 1 48" }, true)],
            },
            Timecode = new TimecodeModel { Sources = [new TimecodeSource("tc0a0b0c01", "LTC", TimecodeKind.Ltc) { Device = "default", Channel = 2, Format = "25", LatencyNs = 5 }] },
            Sends = [new VideoSend("sd0a0b0c01", "Program", SendProtocol.Omt, "output:op_1", 1920, 1080, 50, SendFormat.Uyvy, true)
                { RateDivider = 2, PublishedName = "PlayMap Program", FollowAir = false, Quality = "high", Groups = "a,b", Fit = SendFit.Fill, OnlyWhenWatched = false }],
            FxAssets = [new FxAsset("fa0a0b0c01", "Glow", FxFormat.Isf, "fx/glow.fs", "sha256:5")],
            GeneratorPresets = [new GeneratorPreset("gp0a0b0c01", "Deep", "fractal.julia", ParamSet.Empty.With("zoom", 9))],
            Audio = AudioSettings.Default with { Input = "system", Channels = "M", GainDb = 3, AutoGain = false, ListenerDistanceM = 34 },
            Tempo = TempoSettings.Default with { Source = "detect", Bpm = 128, BeatsPerBar = 3 },
            Leds = new LedRig
            {
                Nodes = [new LedNode("ln0a0b0c01", "Truss", LedProtocol.Sacn, "", 0, true, 44, 12, LedSync.Latch, 510, "#FF0000") { SacnPriority = 150, Vendor = "acme" }],
                Types = [new LedFixtureType("lt0a0b0c01", "RGBW tape", [new LedChannelSlot(LedChannel.Dimmer, 8, 255)],
                    [new LedChannelSlot(LedChannel.R, 8, 0), new LedChannelSlot(LedChannel.G, 8, 0), new LedChannelSlot(LedChannel.B, 8, 0), new LedChannelSlot(LedChannel.W, 8, 0)],
                    [], 0.3, "csv", "acme")],
                Fixtures = [new LedFixture("lf0a0b0c01", "Strip", "lt0a0b0c01", LedSpace.World, "", Transform3.Identity,
                    new LedLayout(LedShape.Grid, 64, 4, new Vec3d(2, 0.5, 0), LedStart.BottomRight, true, true, []),
                    new LedPatch("ln0a0b0c01", 3, 1), LedSampling.Area, 0.5, LedColor.Neutral with { Brightness = 0.8 }, true, false, true, "#00FF00", ["truss"])
                    { ViewpointId = "vp0a0b0c01", BindDistance = 0.5 }],
            },
            Optimise = new OptimiseSettings
            {
                Mode = OptimiseMode.Auto,
                Rules = [new OptimiseRule("h264 to hap", new OptimiseMatch { Codecs = ["h264"], Kinds = [MediaKind.Video], Alpha = false, MinWidth = 1920 }, "hap", RuleMode.Auto)
                    { Quality = EncodeQuality.Archive }],
                Quality = EncodeQuality.Fast,
                Targets = ["nd1"],
            },
        };
    }

    /// <summary>Every round-2 id <see cref="Build"/> puts in the project.</summary>
    public static readonly string[] NewIds =
    [
        "fx0a0b0c01", "fx0a0b0c02", "fx0a0b0c03", "fx0a0b0c04", "fx0a0b0c05", "mv0a0b0c01", "cp0a0b0c01", "sv0a0b0c01", "sk0a0b0c01",
        "al0a0b0c01", "cs0a0b0c01", "cs0a0b0c02", "fm0a0b0c01", "ct0a0b0c01", "bd0a0b0c01", "tc0a0b0c01", "sd0a0b0c01", "fa0a0b0c01",
        "gp0a0b0c01", "ln0a0b0c01", "lt0a0b0c01", "lf0a0b0c01",
    ];
}
