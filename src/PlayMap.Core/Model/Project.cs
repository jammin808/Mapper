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
