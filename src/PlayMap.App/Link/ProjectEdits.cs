using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Model;

namespace PlayMap.App.Link;

/// <summary>The generic edits (SET, ADD, REMOVE, MOVE) as pure functions of a project, the way the desk's
/// demo engine applies them: any entity by id wherever it lives in the tree, one dotted field path through
/// <see cref="ProjectJson.SetPath{T}"/>. A refusal throws <see cref="EditRefusedException"/> with the reason.
/// Nested kinds (layer, mask, pin, item) name their owner with a "parent" field in the ADD JSON.</summary>
public static class ProjectEdits
{
    /// <summary>The id prefix a new entity of each ADD kind gets.</summary>
    public static readonly IReadOnlyDictionary<string, string> Prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["media"] = Ids.Media,
        ["playlist"] = Ids.Playlist,
        ["item"] = Ids.PlaylistItem,
        ["generator"] = Ids.Generator,
        ["canvas"] = Ids.Canvas,
        ["layer"] = Ids.Layer,
        ["surface"] = Ids.Surface,
        ["mesh"] = Ids.Mesh,
        ["projector"] = Ids.Projector,
        ["output"] = Ids.Output,
        ["mask"] = Ids.Mask,
        ["pin"] = "pn",
        ["camera"] = Ids.Camera,
        ["viewpoint"] = "vp",
    };

    public static Project SetField(Project p, string id, string path, string json)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (path.Equals("id", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".id", StringComparison.OrdinalIgnoreCase))
            throw new EditRefusedException("an id never changes — ADD a new entity instead");
        T Edit<T>(T e) => ProjectJson.SetPath(e, path, json);
        return Replace(p, id, Edit, Edit, Edit, Edit, Edit, Edit, Edit, Edit, Edit, Edit, Edit, Edit, Edit, Edit, Edit, Edit)
            ?? (p.Id == id ? Edit(p) : throw new EditRefusedException($"no entity '{id}'"));
    }

    public static Project Remove(Project p, string id)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.Media.IndexOfId(id) >= 0)
            return p with { Media = p.Media.RemoveId(id) };
        if (p.Playlists.IndexOfId(id) >= 0)
            return p with { Playlists = p.Playlists.RemoveId(id) };
        foreach (var pl in p.Playlists)
            if (pl.Items.IndexOfId(id) >= 0)
                return p with { Playlists = p.Playlists.Update(pl.Id, l => l with { Items = l.Items.RemoveId(id) }) };
        if (p.Generators.IndexOfId(id) >= 0)
            return p with { Generators = p.Generators.RemoveId(id) };
        if (p.Canvases.IndexOfId(id) >= 0)
            return p with { Canvases = p.Canvases.RemoveId(id) };
        foreach (var c in p.Canvases)
            if (c.Layers.IndexOfId(id) >= 0)
                return p with { Canvases = p.Canvases.Update(c.Id, x => x with { Layers = x.Layers.RemoveId(id) }) };
        var s = p.Stage;
        if (s.Surfaces.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Surfaces = x.Surfaces.RemoveId(id) });
        foreach (var sf in s.Surfaces)
            if (sf.Masks.IndexOfId(id) >= 0)
                return p.WithStage(x => x with { Surfaces = x.Surfaces.Update(sf.Id, f => f with { Masks = f.Masks.RemoveId(id) }) });
        if (s.Meshes.IndexOfId(id) >= 0)
        {
            if (s.Surfaces.Any(f => f.MeshId == id))
                throw new EditRefusedException($"mesh '{id}' carries surfaces — remove them first");
            return p.WithStage(x => x with { Meshes = x.Meshes.RemoveId(id) });
        }
        if (s.Projectors.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Projectors = x.Projectors.RemoveId(id) });
        foreach (var pj in s.Projectors)
            if (pj.Pins.IndexOfId(id) >= 0)
                return p.WithStage(x => x with { Projectors = x.Projectors.Update(pj.Id, j => j with { Pins = j.Pins.RemoveId(id) }) });
        if (s.Cameras.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Cameras = x.Cameras.RemoveId(id) });
        if (s.Viewpoints.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Viewpoints = x.Viewpoints.RemoveId(id) });
        if (s.Scans.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Scans = x.Scans.RemoveId(id) });
        if (s.Stacks.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Stacks = x.Stacks.RemoveId(id) });
        if (p.Outputs.IndexOfId(id) >= 0)
            return p with { Outputs = p.Outputs.RemoveId(id) };
        foreach (var o in p.Outputs)
            if (o.Masks.IndexOfId(id) >= 0)
                return p with { Outputs = p.Outputs.Update(o.Id, x => x with { Masks = x.Masks.RemoveId(id) }) };
        throw new EditRefusedException($"no entity '{id}'");
    }

    public static Project Move(Project p, string id, int index)
    {
        ArgumentNullException.ThrowIfNull(p);
        ImmutableArray<T> M<T>(ImmutableArray<T> list) where T : IEntity => list.Move(id, index);
        if (p.Media.IndexOfId(id) >= 0)
            return p with { Media = M(p.Media) };
        if (p.Playlists.IndexOfId(id) >= 0)
            return p with { Playlists = M(p.Playlists) };
        foreach (var pl in p.Playlists)
            if (pl.Items.IndexOfId(id) >= 0)
                return p with { Playlists = p.Playlists.Update(pl.Id, l => l with { Items = M(l.Items) }) };
        if (p.Generators.IndexOfId(id) >= 0)
            return p with { Generators = M(p.Generators) };
        foreach (var c in p.Canvases)
            if (c.Layers.IndexOfId(id) >= 0)
                return p with { Canvases = p.Canvases.Update(c.Id, x => x with { Layers = M(x.Layers) }) };
        if (p.Stage.Surfaces.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Surfaces = M(x.Surfaces) });
        foreach (var sf in p.Stage.Surfaces)
            if (sf.Masks.IndexOfId(id) >= 0)
                return p.WithStage(x => x with { Surfaces = x.Surfaces.Update(sf.Id, f => f with { Masks = M(f.Masks) }) });
        if (p.Stage.Projectors.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Projectors = M(x.Projectors) });
        if (p.Outputs.IndexOfId(id) >= 0)
            return p with { Outputs = M(p.Outputs) };
        foreach (var o in p.Outputs)
            if (o.Masks.IndexOfId(id) >= 0)
                return p with { Outputs = p.Outputs.Update(o.Id, x => x with { Masks = M(x.Masks) }) };
        throw new EditRefusedException($"no entity '{id}' in a list that has an order");
    }

    /// <summary>Adds an entity of a kind from its JSON. An empty or taken id is replaced by a fresh one; the
    /// id the entity got comes back in <paramref name="newId"/>.</summary>
    public static Project Add(Project p, string kind, string json, out string newId)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (!Prefixes.TryGetValue(kind, out var prefix))
            throw new EditRefusedException($"ADD takes {string.Join('|', Prefixes.Keys)} — not '{kind}'");
        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject ?? throw new EditRefusedException("ADD takes a JSON object");
        }
        catch (JsonException ex)
        {
            throw new EditRefusedException("ADD takes a JSON object (" + ex.Message.Split('.')[0] + ")");
        }
        var parent = obj["parent"]?.GetValue<string>();
        obj.Remove("parent");
        var given = obj["id"]?.GetValue<string>();
        var taken = p.AllIds().ToHashSet(StringComparer.Ordinal);
        foreach (var pj in p.Stage.Projectors)
            foreach (var pin in pj.Pins)
                taken.Add(pin.Id);
        foreach (var sf in p.Stage.Surfaces)
            foreach (var m in sf.Masks)
                taken.Add(m.Id);
        foreach (var o in p.Outputs)
            foreach (var m in o.Masks)
                taken.Add(m.Id);
        var id = Ids.IsValid(given) && !taken.Contains(given!) ? given! : NewId(prefix, taken);
        obj["id"] = id;
        newId = id;
        var text = obj.ToJsonString();
        T Read<T>() => Deserialize<T>(text, kind);
        switch (kind.ToLowerInvariant())
        {
            case "media":
                return p with { Media = p.Media.Add(Read<MediaItem>()) };
            case "playlist":
                return p with { Playlists = p.Playlists.Add(Read<Playlist>()) };
            case "item":
                var list = p.FindPlaylist(parent) ?? throw new EditRefusedException($"an item needs \"parent\": a playlist (got '{parent}')");
                return p with { Playlists = p.Playlists.Update(list.Id, l => l with { Items = l.Items.Add(Read<PlaylistItem>()) }) };
            case "generator":
                return p with { Generators = p.Generators.Add(Read<Generator>()) };
            case "canvas":
                return p with { Canvases = p.Canvases.Add(Read<Canvas>()) };
            case "layer":
                var canvas = p.FindCanvas(parent) ?? throw new EditRefusedException($"a layer needs \"parent\": a canvas (got '{parent}')");
                return p with { Canvases = p.Canvases.Update(canvas.Id, c => c with { Layers = c.Layers.Add(Read<Layer>()) }) };
            case "surface":
                var surface = Read<Surface>();
                if (p.FindMesh(surface.MeshId) is null)
                    throw new EditRefusedException($"a surface needs a mesh this project has (got '{surface.MeshId}')");
                return p.WithStage(s => s with { Surfaces = s.Surfaces.Add(surface) });
            case "mesh":
                return p.WithStage(s => s with { Meshes = s.Meshes.Add(Read<MeshAsset>()) });
            case "projector":
                return p.WithStage(s => s with { Projectors = s.Projectors.Add(Read<Projector>()) });
            case "camera":
                return p.WithStage(s => s with { Cameras = s.Cameras.Add(Read<CameraRig>()) });
            case "viewpoint":
                return p.WithStage(s => s with { Viewpoints = s.Viewpoints.Add(Read<Viewpoint>()) });
            case "output":
                return p with { Outputs = p.Outputs.Add(Read<Output>()) };
            case "pin":
                var pjr = p.FindProjector(parent) ?? throw new EditRefusedException($"a pin needs \"parent\": a projector (got '{parent}')");
                return p.WithStage(s => s with { Projectors = s.Projectors.Update(pjr.Id, j => j with { Pins = j.Pins.Add(Read<CalibrationPin>()) }) });
            case "mask":
                var mask = Read<Mask>();
                if (mask.Path is null || mask.Path.Nodes.Length < 3)
                    throw new EditRefusedException("a mask needs a path of three or more nodes");
                if (p.FindSurface(parent) is { } sf2)
                    return p.WithStage(s => s with { Surfaces = s.Surfaces.Update(sf2.Id, f => f with { Masks = f.Masks.Add(mask) }) });
                if (p.FindOutput(parent) is { } op)
                    return p with { Outputs = p.Outputs.Update(op.Id, o => o with { Masks = o.Masks.Add(mask) }) };
                throw new EditRefusedException($"a mask needs \"parent\": a surface or an output (got '{parent}')");
            default:
                throw new EditRefusedException($"ADD does not take '{kind}'");
        }
    }

    private static string NewId(string prefix, HashSet<string> taken)
    {
        string id;
        do
            id = Ids.New(prefix);
        while (taken.Contains(id));
        return id;
    }

    private static T Deserialize<T>(string json, string kind)
    {
        try
        {
            return ProjectJson.DeserializeEntity<T>(json);
        }
        catch (JsonException ex)
        {
            throw new EditRefusedException($"that is not a {kind} ({ex.Message.Split('.')[0]})");
        }
        catch (NotSupportedException ex)
        {
            throw new EditRefusedException($"that is not a {kind} ({ex.Message.Split('.')[0]})");
        }
    }

    /// <summary>Finds the entity with the id anywhere in the tree and returns the project with it replaced by
    /// the edit for its type; null when no entity has the id.</summary>
    private static Project? Replace(Project p, string id,
        Func<MediaItem, MediaItem> media, Func<Playlist, Playlist> playlist, Func<PlaylistItem, PlaylistItem> item,
        Func<Generator, Generator> generator, Func<Canvas, Canvas> canvas, Func<Layer, Layer> layer,
        Func<Surface, Surface> surface, Func<MeshAsset, MeshAsset> mesh, Func<Projector, Projector> projector,
        Func<CameraRig, CameraRig> camera, Func<Viewpoint, Viewpoint> viewpoint, Func<ScanRecord, ScanRecord> scan,
        Func<Output, Output> output, Func<Mask, Mask> mask, Func<CalibrationPin, CalibrationPin> pin, Func<Mask, Mask> outputMask)
    {
        if (p.Media.IndexOfId(id) >= 0)
            return p with { Media = p.Media.Update(id, media) };
        if (p.Playlists.IndexOfId(id) >= 0)
            return p with { Playlists = p.Playlists.Update(id, playlist) };
        foreach (var pl in p.Playlists)
            if (pl.Items.IndexOfId(id) >= 0)
                return p with { Playlists = p.Playlists.Update(pl.Id, l => l with { Items = l.Items.Update(id, item) }) };
        if (p.Generators.IndexOfId(id) >= 0)
            return p with { Generators = p.Generators.Update(id, generator) };
        if (p.Canvases.IndexOfId(id) >= 0)
            return p with { Canvases = p.Canvases.Update(id, canvas) };
        foreach (var c in p.Canvases)
            if (c.Layers.IndexOfId(id) >= 0)
                return p with { Canvases = p.Canvases.Update(c.Id, x => x with { Layers = x.Layers.Update(id, layer) }) };
        var s = p.Stage;
        if (s.Surfaces.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Surfaces = x.Surfaces.Update(id, surface) });
        foreach (var sf in s.Surfaces)
            if (sf.Masks.IndexOfId(id) >= 0)
                return p.WithStage(x => x with { Surfaces = x.Surfaces.Update(sf.Id, f => f with { Masks = f.Masks.Update(id, mask) }) });
        if (s.Meshes.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Meshes = x.Meshes.Update(id, mesh) });
        if (s.Projectors.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Projectors = x.Projectors.Update(id, projector) });
        foreach (var pj in s.Projectors)
            if (pj.Pins.IndexOfId(id) >= 0)
                return p.WithStage(x => x with { Projectors = x.Projectors.Update(pj.Id, j => j with { Pins = j.Pins.Update(id, pin) }) });
        if (s.Cameras.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Cameras = x.Cameras.Update(id, camera) });
        if (s.Viewpoints.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Viewpoints = x.Viewpoints.Update(id, viewpoint) });
        if (s.Scans.IndexOfId(id) >= 0)
            return p.WithStage(x => x with { Scans = x.Scans.Update(id, scan) });
        if (p.Outputs.IndexOfId(id) >= 0)
            return p with { Outputs = p.Outputs.Update(id, output) };
        foreach (var o in p.Outputs)
            if (o.Masks.IndexOfId(id) >= 0)
                return p with { Outputs = p.Outputs.Update(o.Id, x => x with { Masks = x.Masks.Update(id, outputMask) }) };
        return null;
    }
}
