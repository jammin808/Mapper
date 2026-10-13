using System.Collections.Immutable;

namespace PlayMap.Core.Model;

/// <summary>Show-wide settings that travel with the project (machine settings — ports, keys, the node's
/// name — do not; they are the node's own, <see cref="PlayMap.Core.Bus.NodeSettings"/>).</summary>
public sealed record ProjectSettings(
    /// <summary>The show's frame rate ("50", "59.94"): generators and the sync grid step on it.</summary>
    string FrameRate,
    /// <summary>Default transition for new playlist items.</summary>
    TransitionKind DefaultTransition,
    long DefaultTransitionNs,
    /// <summary>The photosensitivity guard on generators (≤ 3 flashes a second).</summary>
    bool FlashGuard)
{
    public static readonly ProjectSettings Default = new("60", TransitionKind.Cut, 0, true);

    /// <summary>The sync group's air latency, ms; −1 = the largest any member reports (wp2-output).</summary>
    public double SyncLatencyMs { get; init; } = -1;

    /// <summary>Calibration results kept per projector or output besides current, fallback, staged and pinned, 1–100 (wp2-wall).</summary>
    public int CalibrationKeep { get; init; } = 20;

    /// <summary>A re-solve applies itself only when its score ≤ max(gate, 1.25 × the fallback's) (wp2-wall).</summary>
    public double CalibrationGate { get; init; } = 1.5;
}

/// <summary>The document: everything a show is made of. Immutable — an edit makes a new root that shares
/// every untouched branch with the old one, so undo is a list of roots and the render thread reads a root
/// with no lock (the published snapshot is never written again).</summary>
public sealed record Project(
    string Id,
    string Name,
    int Schema,
    ImmutableArray<MediaItem> Media,
    ImmutableArray<Playlist> Playlists,
    ImmutableArray<Generator> Generators,
    ImmutableArray<Canvas> Canvases,
    Stage Stage,
    ImmutableArray<Output> Outputs,
    ProjectSettings Settings)
{
    public const int CurrentSchema = 1;

    /// <summary>Show Mode rules, panic plans and locks (wp2-show).</summary>
    public ShowPlan Show { get; init; } = ShowPlan.Default;

    /// <summary>Who backs up whom and who leads next (wp2-fleet).</summary>
    public FleetPlan Fleet { get; init; } = FleetPlan.Default;

    /// <summary>Controllers and their bindings (wp2-control).</summary>
    public ControlSet Control { get; init; } = ControlSet.Empty;

    /// <summary>Timecode sources (wp2-control).</summary>
    public TimecodeModel Timecode { get; init; } = TimecodeModel.Empty;

    /// <summary>Pictures this show sends: NDI, OMT, Spout (wp2-videoio).</summary>
    public ImmutableArray<VideoSend> Sends { get; init; } = [];

    /// <summary>ISF effects carried with the show, by content hash (wp2-fx).</summary>
    public ImmutableArray<FxAsset> FxAssets { get; init; } = [];

    /// <summary>Generator looks saved in the show (wp2-content).</summary>
    public ImmutableArray<GeneratorPreset> GeneratorPresets { get; init; } = [];

    /// <summary>What the show listens to (wp2-audio).</summary>
    public AudioSettings Audio { get; init; } = AudioSettings.Default;

    /// <summary>The show's beat clock settings (wp2-audio).</summary>
    public TempoSettings Tempo { get; init; } = TempoSettings.Default;

    /// <summary>LED pixel mapping (wp2-led).</summary>
    public LedRig Leds { get; init; } = LedRig.Empty;

    /// <summary>Optimise-on-import mode and this show's own rules (wp2-optimise).</summary>
    public OptimiseSettings Optimise { get; init; } = OptimiseSettings.Default;

    /// <summary>A new project as a fresh install comes up: one 1920×1080 canvas showing the test card,
    /// one output showing that canvas — something on the wall before anything is configured.</summary>
    public static Project CreateDefault(string name = "Untitled")
    {
        var card = new Generator("gn_testcard", "Test card", "pattern.testcard", ParamSet.Empty);
        var canvas = new Canvas("cv_main", "Main canvas", 1920, 1080, "#000000",
            [new Layer("ly_base", "Test card", SourceRef.Generator(card.Id), new RectD(0, 0, 1920, 1080), 1, LayerBlend.Normal, true)]);
        var output = new Output("op_1", "Output 1", OutputKind.Display, "", 1920, 1080, 60,
            OutputMode.Canvas, null, SourceRef.Canvas(canvas.Id), new RectD(0, 0, 1920, 1080),
            OutputWarp.None, BlendSettings.Default with { Auto3D = false }, ColorTrim.Neutral, [],
            new RectD(0, 0, 1920, 1080), 0, true);
        return new Project(Ids.New(Ids.Project), name, CurrentSchema, [], [], [card], [canvas], Stage.Empty, [output], ProjectSettings.Default);
    }

    public MediaItem? FindMedia(string? id) => Media.Find(id);
    public Playlist? FindPlaylist(string? id) => Playlists.Find(id);
    public Generator? FindGenerator(string? id) => Generators.Find(id);
    public Canvas? FindCanvas(string? id) => Canvases.Find(id);
    public Output? FindOutput(string? id) => Outputs.Find(id);
    public Surface? FindSurface(string? id) => Stage.Surfaces.Find(id);
    public Projector? FindProjector(string? id) => Stage.Projectors.Find(id);
    public MeshAsset? FindMesh(string? id) => Stage.Meshes.Find(id);
    public DeviceLink? FindDevice(string? id) => Stage.Devices.Find(id);
    public VideoSend? FindSend(string? id) => Sends.Find(id);
    public ControlBinding? FindBinding(string? id) => Control.Bindings.Find(id);
    public ControlSurface? FindControlSurface(string? id) => Control.Surfaces.Find(id);
    public TimecodeSource? FindTimecodeSource(string? id) => Timecode.Sources.Find(id);
    public GeneratorPreset? FindGeneratorPreset(string? id) => GeneratorPresets.Find(id);
    public StackGroup? FindStack(string? id) => Stage.Stacks.Find(id);
    public AlignGroup? FindAlign(string? id) => Stage.Aligns.Find(id);
    public CalibrationSnapshot? FindCalibration(string? id) => Stage.Calibrations.Find(id);

    /// <summary>An effect slot by id with the entity whose chain holds it (an item, layer, canvas, surface or output).</summary>
    public (FxSlot Slot, string OwnerId, int Index)? FindFx(string? id)
    {
        if (id is null)
            return null;
        static (FxSlot, string, int)? In(FxChain chain, string owner, string id)
        {
            var i = chain.Slots.IndexOfId(id);
            return i < 0 ? null : (chain.Slots[i], owner, i);
        }
        foreach (var l in Playlists)
            foreach (var it in l.Items)
                if (In(it.Fx, it.Id, id) is { } a) return a;
        foreach (var c in Canvases)
        {
            if (In(c.Fx, c.Id, id) is { } b) return b;
            foreach (var ly in c.Layers)
                if (In(ly.Fx, ly.Id, id) is { } d) return d;
        }
        foreach (var sf in Stage.Surfaces)
            if (In(sf.Fx, sf.Id, id) is { } e) return e;
        foreach (var o in Outputs)
            if (In(o.Fx, o.Id, id) is { } f) return f;
        return null;
    }

    /// <summary>Every id in the project, for uniqueness checks.</summary>
    public IEnumerable<string> AllIds()
    {
        foreach (var m in Media) yield return m.Id;
        foreach (var p in Playlists)
        {
            yield return p.Id;
            foreach (var i in p.Items) yield return i.Id;
        }
        foreach (var g in Generators) yield return g.Id;
        foreach (var c in Canvases)
        {
            yield return c.Id;
            foreach (var l in c.Layers) yield return l.Id;
        }
        foreach (var s in Stage.Surfaces) yield return s.Id;
        foreach (var m in Stage.Meshes) yield return m.Id;
        foreach (var p in Stage.Projectors) yield return p.Id;
        foreach (var c in Stage.Cameras) yield return c.Id;
        foreach (var v in Stage.Viewpoints) yield return v.Id;
        foreach (var s in Stage.Scans) yield return s.Id;
        foreach (var d in Stage.Devices) yield return d.Id;
        foreach (var o in Outputs) yield return o.Id;
        foreach (var x in Sends) yield return x.Id;
        foreach (var x in Control.Surfaces) yield return x.Id;
        foreach (var x in Control.Bindings) yield return x.Id;
        foreach (var x in Timecode.Sources) yield return x.Id;
        foreach (var x in FxAssets) yield return x.Id;
        foreach (var x in GeneratorPresets) yield return x.Id;
        foreach (var chain in FxChains())
            foreach (var slot in chain.Slots)
                yield return slot.Id;
        foreach (var x in Leds.Nodes) yield return x.Id;
        foreach (var x in Leds.Types) yield return x.Id;
        foreach (var x in Leds.Fixtures) yield return x.Id;
        foreach (var m in Media)
            foreach (var v in m.Versions)
                yield return v.Id;
        foreach (var x in Stage.Controls) yield return x.Id;
        foreach (var x in Stage.Surveys) yield return x.Id;
        foreach (var x in Stage.Stacks) yield return x.Id;
        foreach (var x in Stage.Aligns) yield return x.Id;
        foreach (var x in Stage.Calibrations) yield return x.Id;
        foreach (var x in Fleet.Members) yield return x.Id;
    }

    /// <summary>Every effect chain in the project: each playlist item's, canvas's, layer's, surface's and output's.</summary>
    public IEnumerable<FxChain> FxChains()
    {
        foreach (var l in Playlists)
            foreach (var it in l.Items)
                yield return it.Fx;
        foreach (var c in Canvases)
        {
            yield return c.Fx;
            foreach (var ly in c.Layers)
                yield return ly.Fx;
        }
        foreach (var sf in Stage.Surfaces)
            yield return sf.Fx;
        foreach (var o in Outputs)
            yield return o.Fx;
    }

    /// <summary>A new id with the prefix that no entity in the project has.</summary>
    public string NewId(string prefix)
    {
        var taken = AllIds().ToHashSet(StringComparer.Ordinal);
        string id;
        do
            id = Ids.New(prefix);
        while (taken.Contains(id));
        return id;
    }

    public Project WithStage(Func<Stage, Stage> edit) => this with { Stage = edit(Stage) };
}
