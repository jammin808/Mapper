using PlayMap.Core.Geometry;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Rendering;

/// <summary>The project's meshes, loaded once per asset and shared by the solvers and the render host's stage
/// resources: a primitive is made from its parameters (<see cref="Primitives.FromAsset"/>), a file is read
/// with <see cref="MeshIO.Load"/> from the project folder. An asset is the same while its record is equal
/// (the model shares unchanged branches, so an untouched asset is the same instance); a changed asset loads
/// again. A load that fails is remembered with its reason until the asset changes, never retried per frame.
/// Thread-safe; <see cref="Get"/> loads on the caller's thread, so the render thread only ever calls
/// <see cref="Peek"/>. Every mesh handed out is immutable and kept while its asset stays.</summary>
public sealed class StageMeshes
{
    private readonly record struct Loaded(MeshData? Mesh, string? Problem);

    private sealed class Entry(MeshAsset asset, Lazy<Loaded> load)
    {
        public MeshAsset Asset { get; } = asset;
        public Lazy<Loaded> Load { get; } = load;
    }

    private readonly Func<string?> _folder;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <param name="projectFolder">The folder relative mesh paths are read from, asked at each load (it moves
    /// when another project is opened); null while the project has none.</param>
    public StageMeshes(Func<string?> projectFolder)
    {
        ArgumentNullException.ThrowIfNull(projectFolder);
        _folder = projectFolder;
    }

    /// <summary>Loads (files read so far, primitives made) since the cache was made.</summary>
    public int Loads { get; private set; }

    /// <summary>The asset's mesh, loading it now when it is not loaded yet; null when it cannot be
    /// (<see cref="Problem"/> says why). Never call it on the render thread.</summary>
    public MeshData? Get(MeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return EntryFor(asset).Load.Value.Mesh;
    }

    /// <summary>The asset's mesh when it is loaded, without loading.</summary>
    public MeshData? Peek(MeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        lock (_gate)
            return _entries.TryGetValue(asset.Id, out var e) && e.Asset == asset && e.Load.IsValueCreated ? e.Load.Value.Mesh : null;
    }

    /// <summary>Why a mesh could not be loaded, or null.</summary>
    public string? Problem(string meshId)
    {
        lock (_gate)
            return _entries.TryGetValue(meshId, out var e) && e.Load.IsValueCreated ? e.Load.Value.Problem : null;
    }

    /// <summary>A lookup by mesh id over a stage for the solvers: loads what is missing on the caller's thread.</summary>
    public Func<string, MeshData?> For(Stage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return id => stage.Meshes.Find(id) is { } asset ? Get(asset) : null;
    }

    /// <summary>Lets go of the meshes of assets the stage no longer has.</summary>
    public void Retain(Stage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        lock (_gate)
        {
            List<string>? gone = null;
            foreach (var id in _entries.Keys)
                if (stage.Meshes.Find(id) is null)
                    (gone ??= []).Add(id);
            if (gone is not null)
                foreach (var id in gone)
                    _entries.Remove(id);
        }
    }

    private Entry EntryFor(MeshAsset asset)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(asset.Id, out var e) && e.Asset == asset)
                return e;
            // One load per asset however many threads ask at once; the load itself runs outside the lock.
            e = new Entry(asset, new Lazy<Loaded>(() => Load(asset), LazyThreadSafetyMode.ExecutionAndPublication));
            _entries[asset.Id] = e;
            return e;
        }
    }

    private Loaded Load(MeshAsset asset)
    {
        lock (_gate)
            Loads++;
        try
        {
            if (asset.Origin == MeshOrigin.Primitive || (asset.Path.Length == 0 && asset.Primitive.Length > 0))
                return new Loaded(Primitives.FromAsset(asset), null);
            if (string.IsNullOrWhiteSpace(asset.Path))
                return new Loaded(null, $"{asset.Name} names no file and no primitive");
            var path = asset.Path;
            if (!Path.IsPathRooted(path))
            {
                var folder = _folder();
                if (string.IsNullOrEmpty(folder))
                    return new Loaded(null, $"{asset.Name}'s file {asset.Path} is relative and the project has no folder yet — save the project first");
                path = Path.Combine(folder, path);
            }
            if (!File.Exists(path))
                return new Loaded(null, $"{asset.Name}'s file {path} is missing");
            var file = MeshIO.Load(path);
            return file.Mesh is { TriangleCount: > 0 } mesh
                ? new Loaded(mesh, null)
                : new Loaded(null, $"{asset.Name}: {Path.GetFileName(path)} holds no triangles (a point cloud is meshed first)");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or FormatException
            or UnauthorizedAccessException or OverflowException or IndexOutOfRangeException)
        {
            return new Loaded(null, $"{asset.Name}: {ex.Message}");
        }
    }
}
