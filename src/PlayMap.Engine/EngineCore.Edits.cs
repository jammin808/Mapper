using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;

using ProjectFile = PlayMap.Core.Model.ProjectJson;
using PlayMap.Core.Storage;

namespace PlayMap.Engine;

/// <summary>Generic edits — GET, SET, ADD, REMOVE, MOVE by id and field path — and the project verbs. This
/// is how the desk edits anything, and therefore how Patterns or an AI can too.</summary>
public sealed partial class EngineCore
{
    private sealed record Located(
        string Kind,
        object Entity,
        Type Type,
        Func<Project, object, Project> Replace,
        Func<Project, Project> Remove,
        Func<Project, int, Project> MoveTo);

    private static Located? Locate(Project p, string id)
    {
        // The lists are re-read from the project being edited (it may be newer than the one located in).
        Located? Top<T>(string kind, ImmutableArray<T> items, Func<Project, ImmutableArray<T>, Project> set) where T : class, IEntity
        {
            var e = items.Find(id);
            return e is null ? null : new Located(kind, e, typeof(T),
                (pp, v) => set(pp, (ListOf<T>(pp) ?? items).Update(id, _ => (T)v)),
                pp => set(pp, (ListOf<T>(pp) ?? items).RemoveId(id)),
                (pp, i) => set(pp, (ListOf<T>(pp) ?? items).Move(id, i)));
        }

        return Top("media", p.Media, (pp, v) => pp with { Media = v })
            ?? Top("playlist", p.Playlists, (pp, v) => pp with { Playlists = v })
            ?? Top("generator", p.Generators, (pp, v) => pp with { Generators = v })
            ?? Top("canvas", p.Canvases, (pp, v) => pp with { Canvases = v })
            ?? Top("output", p.Outputs, (pp, v) => pp with { Outputs = v })
            ?? Top("surface", p.Stage.Surfaces, (pp, v) => pp.WithStage(s => s with { Surfaces = v }))
            ?? Top("mesh", p.Stage.Meshes, (pp, v) => pp.WithStage(s => s with { Meshes = v }))
            ?? Top("projector", p.Stage.Projectors, (pp, v) => pp.WithStage(s => s with { Projectors = v }))
            ?? Top("camera", p.Stage.Cameras, (pp, v) => pp.WithStage(s => s with { Cameras = v }))
            ?? Top("viewpoint", p.Stage.Viewpoints, (pp, v) => pp.WithStage(s => s with { Viewpoints = v }))
            ?? Top("scan", p.Stage.Scans, (pp, v) => pp.WithStage(s => s with { Scans = v }))
            ?? Top("device", p.Stage.Devices, (pp, v) => pp.WithStage(s => s with { Devices = v }))
            ?? Top("send", p.Sends, (pp, v) => pp with { Sends = v })
            ?? Top("controlsurface", p.Control.Surfaces, (pp, v) => pp with { Control = pp.Control with { Surfaces = v } })
            ?? Top("binding", p.Control.Bindings, (pp, v) => pp with { Control = pp.Control with { Bindings = v } })
            ?? Top("timecodesource", p.Timecode.Sources, (pp, v) => pp with { Timecode = pp.Timecode with { Sources = v } })
            ?? Top("fxasset", p.FxAssets, (pp, v) => pp with { FxAssets = v })
            ?? Top("generatorpreset", p.GeneratorPresets, (pp, v) => pp with { GeneratorPresets = v })
            ?? Top("lednode", p.Leds.Nodes, (pp, v) => pp with { Leds = pp.Leds with { Nodes = v } })
            ?? Top("fixturetype", p.Leds.Types, (pp, v) => pp with { Leds = pp.Leds with { Types = v } })
            ?? Top("fixture", p.Leds.Fixtures, (pp, v) => pp with { Leds = pp.Leds with { Fixtures = v } })
            ?? Top("controlpoint", p.Stage.Controls, (pp, v) => pp.WithStage(s => s with { Controls = v }))
            ?? Top("survey", p.Stage.Surveys, (pp, v) => pp.WithStage(s => s with { Surveys = v }))
            ?? Top("stack", p.Stage.Stacks, (pp, v) => pp.WithStage(s => s with { Stacks = v }))
            ?? Top("align", p.Stage.Aligns, (pp, v) => pp.WithStage(s => s with { Aligns = v }))
            ?? Top("calibration", p.Stage.Calibrations, (pp, v) => pp.WithStage(s => s with { Calibrations = v }))
            ?? Top("fleetmember", p.Fleet.Members, (pp, v) => pp with { Fleet = pp.Fleet with { Members = v } })
            ?? Nested(p, id);
    }

    private static ImmutableArray<T>? ListOf<T>(Project p) where T : class, IEntity => typeof(T) switch
    {
        var t when t == typeof(MediaItem) => (ImmutableArray<T>)(object)p.Media,
        var t when t == typeof(Playlist) => (ImmutableArray<T>)(object)p.Playlists,
        var t when t == typeof(Generator) => (ImmutableArray<T>)(object)p.Generators,
        var t when t == typeof(Canvas) => (ImmutableArray<T>)(object)p.Canvases,
        var t when t == typeof(Output) => (ImmutableArray<T>)(object)p.Outputs,
        var t when t == typeof(Surface) => (ImmutableArray<T>)(object)p.Stage.Surfaces,
        var t when t == typeof(MeshAsset) => (ImmutableArray<T>)(object)p.Stage.Meshes,
        var t when t == typeof(Projector) => (ImmutableArray<T>)(object)p.Stage.Projectors,
        var t when t == typeof(CameraRig) => (ImmutableArray<T>)(object)p.Stage.Cameras,
        var t when t == typeof(Viewpoint) => (ImmutableArray<T>)(object)p.Stage.Viewpoints,
        var t when t == typeof(ScanRecord) => (ImmutableArray<T>)(object)p.Stage.Scans,
        var t when t == typeof(DeviceLink) => (ImmutableArray<T>)(object)p.Stage.Devices,
        var t when t == typeof(VideoSend) => (ImmutableArray<T>)(object)p.Sends,
        var t when t == typeof(ControlSurface) => (ImmutableArray<T>)(object)p.Control.Surfaces,
        var t when t == typeof(ControlBinding) => (ImmutableArray<T>)(object)p.Control.Bindings,
        var t when t == typeof(TimecodeSource) => (ImmutableArray<T>)(object)p.Timecode.Sources,
        var t when t == typeof(FxAsset) => (ImmutableArray<T>)(object)p.FxAssets,
        var t when t == typeof(GeneratorPreset) => (ImmutableArray<T>)(object)p.GeneratorPresets,
        var t when t == typeof(LedNode) => (ImmutableArray<T>)(object)p.Leds.Nodes,
        var t when t == typeof(LedFixtureType) => (ImmutableArray<T>)(object)p.Leds.Types,
        var t when t == typeof(LedFixture) => (ImmutableArray<T>)(object)p.Leds.Fixtures,
        var t when t == typeof(ControlPoint) => (ImmutableArray<T>)(object)p.Stage.Controls,
        var t when t == typeof(SurveySource) => (ImmutableArray<T>)(object)p.Stage.Surveys,
        var t when t == typeof(StackGroup) => (ImmutableArray<T>)(object)p.Stage.Stacks,
        var t when t == typeof(AlignGroup) => (ImmutableArray<T>)(object)p.Stage.Aligns,
        var t when t == typeof(CalibrationSnapshot) => (ImmutableArray<T>)(object)p.Stage.Calibrations,
        var t when t == typeof(FleetMember) => (ImmutableArray<T>)(object)p.Fleet.Members,
        _ => null,
    };

    /// <summary>Entities that live inside another: playlist items, layers, pins, masks, effect slots, media versions.</summary>
    private static Located? Nested(Project p, string id)
    {
        if (p.FindFx(id) is { } fx)
        {
            var owner = fx.OwnerId;
            return new Located("fx", fx.Slot, typeof(FxSlot),
                (pp, v) => WithChain(pp, owner, c => c with { Slots = c.Slots.Update(id, _ => (FxSlot)v) }),
                pp => WithChain(pp, owner, c => c with { Slots = c.Slots.RemoveId(id) }),
                (pp, i) => WithChain(pp, owner, c => c with { Slots = c.Slots.Move(id, i) }));
        }
        foreach (var m in p.Media)
        {
            if (m.Versions.Find(id) is { } version)
                return new Located("version", version, typeof(MediaVersion),
                    (pp, v) => pp with { Media = pp.Media.Update(m.Id, x => x with { Versions = x.Versions.Update(id, _ => (MediaVersion)v) }) },
                    pp => pp with { Media = pp.Media.Update(m.Id, x => x with { Versions = x.Versions.RemoveId(id) }) },
                    (pp, i) => pp with { Media = pp.Media.Update(m.Id, x => x with { Versions = x.Versions.Move(id, i) }) });
        }
        foreach (var list in p.Playlists)
        {
            if (list.Items.Find(id) is { } item)
                return new Located("item", item, typeof(PlaylistItem),
                    (pp, v) => pp with { Playlists = pp.Playlists.Update(list.Id, l => l with { Items = l.Items.Update(id, _ => (PlaylistItem)v) }) },
                    pp => pp with { Playlists = pp.Playlists.Update(list.Id, l => l with { Items = l.Items.RemoveId(id) }) },
                    (pp, i) => pp with { Playlists = pp.Playlists.Update(list.Id, l => l with { Items = l.Items.Move(id, i) }) });
        }
        foreach (var canvas in p.Canvases)
        {
            if (canvas.Layers.Find(id) is { } layer)
                return new Located("layer", layer, typeof(Layer),
                    (pp, v) => pp with { Canvases = pp.Canvases.Update(canvas.Id, c => c with { Layers = c.Layers.Update(id, _ => (Layer)v) }) },
                    pp => pp with { Canvases = pp.Canvases.Update(canvas.Id, c => c with { Layers = c.Layers.RemoveId(id) }) },
                    (pp, i) => pp with { Canvases = pp.Canvases.Update(canvas.Id, c => c with { Layers = c.Layers.Move(id, i) }) });
        }
        foreach (var pj in p.Stage.Projectors)
        {
            if (pj.Pins.Find(id) is { } pin)
                return new Located("pin", pin, typeof(CalibrationPin),
                    (pp, v) => pp.WithStage(s => s with { Projectors = s.Projectors.Update(pj.Id, x => x with { Pins = x.Pins.Update(id, _ => (CalibrationPin)v) }) }),
                    pp => pp.WithStage(s => s with { Projectors = s.Projectors.Update(pj.Id, x => x with { Pins = x.Pins.RemoveId(id) }) }),
                    (pp, i) => pp.WithStage(s => s with { Projectors = s.Projectors.Update(pj.Id, x => x with { Pins = x.Pins.Move(id, i) }) }));
        }
        foreach (var o in p.Outputs)
        {
            if (o.Masks.Find(id) is { } mask)
                return new Located("mask", mask, typeof(Mask),
                    (pp, v) => pp with { Outputs = pp.Outputs.Update(o.Id, x => x with { Masks = x.Masks.Update(id, _ => (Mask)v) }) },
                    pp => pp with { Outputs = pp.Outputs.Update(o.Id, x => x with { Masks = x.Masks.RemoveId(id) }) },
                    (pp, i) => pp with { Outputs = pp.Outputs.Update(o.Id, x => x with { Masks = x.Masks.Move(id, i) }) });
        }
        foreach (var sf in p.Stage.Surfaces)
        {
            if (sf.Masks.Find(id) is { } mask)
                return new Located("mask", mask, typeof(Mask),
                    (pp, v) => pp.WithStage(s => s with { Surfaces = s.Surfaces.Update(sf.Id, x => x with { Masks = x.Masks.Update(id, _ => (Mask)v) }) }),
                    pp => pp.WithStage(s => s with { Surfaces = s.Surfaces.Update(sf.Id, x => x with { Masks = x.Masks.RemoveId(id) }) }),
                    (pp, i) => pp.WithStage(s => s with { Surfaces = s.Surfaces.Update(sf.Id, x => x with { Masks = x.Masks.Move(id, i) }) }));
        }
        return null;
    }

    /// <summary>The project with the effect chain of the entity <paramref name="ownerId"/> (a playlist item, canvas, layer,
    /// surface or output) edited.</summary>
    private static Project WithChain(Project p, string ownerId, Func<FxChain, FxChain> edit)
    {
        foreach (var list in p.Playlists)
            if (list.Items.IndexOfId(ownerId) >= 0)
                return p with { Playlists = p.Playlists.Update(list.Id, l => l with { Items = l.Items.Update(ownerId, it => it with { Fx = edit(it.Fx) }) }) };
        if (p.FindCanvas(ownerId) is not null)
            return p with { Canvases = p.Canvases.Update(ownerId, c => c with { Fx = edit(c.Fx) }) };
        foreach (var canvas in p.Canvases)
            if (canvas.Layers.IndexOfId(ownerId) >= 0)
                return p with { Canvases = p.Canvases.Update(canvas.Id, c => c with { Layers = c.Layers.Update(ownerId, l => l with { Fx = edit(l.Fx) }) }) };
        if (p.FindSurface(ownerId) is not null)
            return p.WithStage(s => s with { Surfaces = s.Surfaces.Update(ownerId, x => x with { Fx = edit(x.Fx) }) });
        if (p.FindOutput(ownerId) is not null)
            return p with { Outputs = p.Outputs.Update(ownerId, o => o with { Fx = edit(o.Fx) }) };
        throw new EditRefusedException($"effects belong to a playlist item, canvas, layer, surface or output — {ownerId} is none of them");
    }

    private CommandReply EntityGet(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("GET <id>");
        var found = Locate(Edits.Project, args[0]);
        return found is null
            ? CommandReply.Fail($"nothing has the id {args[0]}")
            : CommandReply.Success(JsonSerializer.Serialize(found.Entity, found.Type, ProjectFile.Compact));
    }

    private CommandReply Set(List<string> args, WireLine line)
    {
        if (args.Count < 3)
            return CommandReply.Fail("SET <id> <field.path> <json>");
        var id = args[0];
        var path = args[1];
        var value = line.RestAfter(3);
        if (path.Equals("id", StringComparison.OrdinalIgnoreCase))
            return CommandReply.Fail("an id never changes");
        var located = Locate(Edits.Project, id);
        if (located is null)
            return CommandReply.Fail($"nothing has the id {id}");
        if (located.Kind == "calibration" && !(path.Equals("name", StringComparison.OrdinalIgnoreCase) || path.Equals("pinned", StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail("a calibration result is never edited — CALIBRATION RECALL makes a new one");
        // Drags coalesce: the same field of the same entity within the window is one undo step.
        return FromEdit(Edits.Apply($"set {located.Kind} {Label(located.Entity)} {path}", p =>
        {
            var current = Locate(p, id) ?? throw new EditRefusedException($"nothing has the id {id}");
            var edited = OperatorsLocks(current.Entity, SetPathTyped(current.Entity, current.Type, path, value));
            Validate(p, edited);
            return current.Replace(p, edited);
        }, coalesceKey: $"set {id} {path}"));
    }

    /// <summary>A projector's locks set by hand (the desk's lock buttons, SET) are the operator's: a focal lock they leave on is held as
    /// the operator's and never let go by a solve, unless the same SET names who holds it.</summary>
    private static object OperatorsLocks(object before, object after) =>
        before is Projector was && after is Projector now && now.Locks != was.Locks && now.FocalLock == was.FocalLock
            && (now.Locks & SolveLocks.Focal) != 0
            ? now with { FocalLock = FocalLockSource.Operator }
            : after;

    private static object SetPathTyped(object entity, Type type, string path, string value)
    {
        var method = typeof(ProjectFile).GetMethod(nameof(ProjectFile.SetPath))!.MakeGenericMethod(type);
        try
        {
            return method.Invoke(null, [entity, path, value])!;
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is EditRefusedException refused)
        {
            throw refused;
        }
    }

    private CommandReply Add(List<string> args, WireLine line)
    {
        if (args.Count < 2)
            return CommandReply.Fail("ADD <kind> [<owner>] <json>");
        var kind = args[0].ToLowerInvariant();
        var owned = kind is "layer" or "pin" or "mask" or "item" or "fx";
        // An owned entity names its owner as a word before the JSON, or as "parent" inside it.
        var ownerWord = owned && args.Count >= 3 && !args[1].StartsWith('{');
        var owner = ownerWord ? args[1] : null;
        var json = line.RestAfter(ownerWord ? 3 : 2);
        if (owned && owner is null)
        {
            try
            {
                (owner, json) = TakeParent(json);
            }
            catch (EditRefusedException ex)
            {
                return CommandReply.Fail(ex.Message);
            }
            if (owner is null)
                return CommandReply.Fail($"ADD {kind} <owner id> <json>, or \"parent\":\"<owner id>\" in the JSON");
        }
        string newId = "";
        var r = Edits.Apply($"add {kind}", p =>
        {
            (Project next, string id) result = kind switch
            {
                "media" => AddTop<MediaItem>(p, json, Ids.Media, (pp, e) => pp with { Media = pp.Media.Add(e) }),
                "playlist" => AddTop<Playlist>(p, json, Ids.Playlist, (pp, e) => pp with { Playlists = pp.Playlists.Add(e) }),
                "generator" => AddTop<Generator>(p, json, Ids.Generator, (pp, e) => pp with { Generators = pp.Generators.Add(e) }),
                "canvas" => AddTop<Canvas>(p, json, Ids.Canvas, (pp, e) => pp with { Canvases = pp.Canvases.Add(e) }),
                "output" => AddTop<Output>(p, json, Ids.Output, (pp, e) => pp with { Outputs = pp.Outputs.Add(e) }),
                "surface" => AddTop<Surface>(p, json, Ids.Surface, (pp, e) => pp.WithStage(s => s with { Surfaces = s.Surfaces.Add(e) })),
                "mesh" => AddTop<MeshAsset>(p, json, Ids.Mesh, (pp, e) => pp.WithStage(s => s with { Meshes = s.Meshes.Add(e) })),
                "projector" => AddTop<Projector>(p, json, Ids.Projector, (pp, e) => pp.WithStage(s => s with { Projectors = s.Projectors.Add(e) })),
                "camera" => AddTop<CameraRig>(p, json, Ids.Camera, (pp, e) => pp.WithStage(s => s with { Cameras = s.Cameras.Add(e) })),
                "viewpoint" => AddTop<Viewpoint>(p, json, Ids.Viewpoint, (pp, e) => pp.WithStage(s => s with { Viewpoints = s.Viewpoints.Add(e) })),
                "device" => AddDevice(p, json),
                "layer" => AddLayer(p, owner!, json),
                "pin" => AddPin(p, owner!, json),
                "mask" => AddMask(p, owner!, json),
                "item" => AddItem(p, owner!, json),
                "binding" => AddTop<ControlBinding>(p, json, Ids.ControlBinding, (pp, e) => pp with { Control = pp.Control with { Bindings = pp.Control.Bindings.Add(e) } }),
                "controlsurface" => AddTop<ControlSurface>(p, json, Ids.ControlSurface, (pp, e) => pp with { Control = pp.Control with { Surfaces = pp.Control.Surfaces.Add(e) } }),
                "timecodesource" => AddTop<TimecodeSource>(p, json, Ids.TimecodeSource, (pp, e) => pp with { Timecode = pp.Timecode with { Sources = pp.Timecode.Sources.Add(e) } }),
                "send" => AddTop<VideoSend>(p, json, Ids.Send, (pp, e) => pp with { Sends = pp.Sends.Add(e) }),
                "lednode" => AddTop<LedNode>(p, json, Ids.LedNode, (pp, e) => pp with { Leds = pp.Leds with { Nodes = pp.Leds.Nodes.Add(e) } }),
                "fixturetype" => AddTop<LedFixtureType>(p, json, Ids.LedType, (pp, e) => pp with { Leds = pp.Leds with { Types = pp.Leds.Types.Add(e) } }),
                "fixture" => AddTop<LedFixture>(p, json, Ids.LedFixture, (pp, e) => pp with { Leds = pp.Leds with { Fixtures = pp.Leds.Fixtures.Add(e) } }),
                "controlpoint" => AddTop<ControlPoint>(p, json, Ids.ControlPoint, (pp, e) => pp.WithStage(s => s with { Controls = s.Controls.Add(e) })),
                "stack" => AddTop<StackGroup>(p, json, Ids.Stack, (pp, e) => pp.WithStage(s => s with { Stacks = s.Stacks.Add(e) })),
                "fx" => AddFx(p, owner!, json),
                "fxasset" => throw new EditRefusedException("use FX IMPORT"),
                "generatorpreset" => throw new EditRefusedException("use GENERATOR SAVE"),
                "survey" => throw new EditRefusedException("use SURVEY IMPORT"),
                "align" => throw new EditRefusedException("use ALIGN NEW"),
                "calibration" => throw new EditRefusedException("calibration results are made by solves"),
                "version" => throw new EditRefusedException("use MEDIA OPTIMISE"),
                "fleetmember" => throw new EditRefusedException("use FLEET BACKUP on the backup"),
                _ => throw new EditRefusedException($"ADD takes media, playlist, item, generator, canvas, layer, surface, mesh, projector, pin, camera, viewpoint, output, mask, device, binding, controlsurface, timecodesource, send, fx, lednode, fixturetype, fixture, controlpoint or stack — not {kind}"),
            };
            newId = result.id;
            return result.next;
        });
        return r.Changed ? CommandReply.Success(newId, $"added {kind} {newId}") : FromEdit(r);
    }

    /// <summary>Reads an entity from JSON over the kind's default (missing fields keep their defaults), gives
    /// it a fresh id when it has none or one already taken, validates it and adds it.</summary>
    private (Project, string) AddTop<T>(Project p, string json, string prefix, Func<Project, T, Project> add) where T : class, IEntity
    {
        var entity = ReadOverDefault<T>(p, json);
        if (!Ids.IsValid(entity.Id) || p.AllIds().Contains(entity.Id, StringComparer.Ordinal))
            entity = WithId(entity, p.NewId(prefix));
        Validate(p, entity);
        return (add(p, entity), entity.Id);
    }

    private (Project, string) AddLayer(Project p, string canvasId, string json)
    {
        var canvas = p.FindCanvas(canvasId) ?? throw new EditRefusedException($"no canvas {canvasId}");
        var layer = WithId(ReadOverDefault<Layer>(p, json, canvas), p.NewId(Ids.Layer));
        Validate(p, layer);
        return (p with { Canvases = p.Canvases.Update(canvas.Id, c => c with { Layers = c.Layers.Add(layer) }) }, layer.Id);
    }

    private (Project, string) AddFx(Project p, string ownerId, string json)
    {
        var slot = WithId(ReadOverDefault<FxSlot>(p, json), p.NewId(Ids.Fx));
        Validate(p, slot);
        return (WithChain(p, ownerId, c => c with { Slots = c.Slots.Add(slot) }), slot.Id);
    }

    private (Project, string) AddPin(Project p, string projectorId, string json)
    {
        var pj = p.FindProjector(projectorId) ?? throw new EditRefusedException($"no projector {projectorId}");
        var pin = WithId(ReadOverDefault<CalibrationPin>(p, json), p.NewId(Ids.Pin));
        return (p.WithStage(s => s with { Projectors = s.Projectors.Update(pj.Id, x => x with { Pins = x.Pins.Add(pin) }) }), pin.Id);
    }

    /// <summary>Splits "parent" off an ADD's JSON: the owner's id and the entity's own fields.</summary>
    private static (string? Parent, string Json) TakeParent(string json)
    {
        System.Text.Json.Nodes.JsonObject obj;
        try
        {
            obj = ProjectFile.ParseTree(json) as System.Text.Json.Nodes.JsonObject
                ?? throw new EditRefusedException("ADD takes a JSON object");
        }
        catch (JsonException ex)
        {
            throw new EditRefusedException($"ADD takes a JSON object ({ex.Message.Split('.')[0]})");
        }
        var key = obj.Select(kv => kv.Key).FirstOrDefault(k => k.Equals("parent", StringComparison.OrdinalIgnoreCase));
        if (key is null)
            return (null, json);
        var parent = obj[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        obj.Remove(key);
        return (parent, obj.ToJsonString());
    }

    private (Project, string) AddItem(Project p, string playlistId, string json)
    {
        var list = p.FindPlaylist(playlistId) ?? throw new EditRefusedException($"no playlist {playlistId}");
        var item = WithId(ReadOverDefault<PlaylistItem>(p, json), p.NewId(Ids.PlaylistItem));
        if (item.Source.IsNone)
            throw new EditRefusedException("an item needs a source");
        if ((ResolveSourceProblem(item.Source) ?? PlaylistNesting.Problem(p, list.Id, item.Source)) is { } problem)
            throw new EditRefusedException(problem);
        return (p with { Playlists = p.Playlists.Update(list.Id, l => l with { Items = l.Items.Add(item) }) }, item.Id);
    }

    private (Project, string) AddMask(Project p, string ownerId, string json)
    {
        var mask = WithId(ReadOverDefault<Mask>(p, json), p.NewId(Ids.Mask));
        if (mask.Path.Nodes.Length < 3)
            throw new EditRefusedException("a mask needs a closed path of at least three nodes");
        if (p.FindOutput(ownerId) is { } o)
            return (p with { Outputs = p.Outputs.Update(o.Id, x => x with { Masks = x.Masks.Add(mask) }) }, mask.Id);
        if (p.FindSurface(ownerId) is { } sf)
            return (p.WithStage(s => s with { Surfaces = s.Surfaces.Update(sf.Id, x => x with { Masks = x.Masks.Add(mask) }) }), mask.Id);
        throw new EditRefusedException($"masks belong to an output or a surface — {ownerId} is neither");
    }

    /// <summary>The kind's default with the JSON's fields laid over it.</summary>
    private T ReadOverDefault<T>(Project p, string json, Canvas? canvas = null) where T : class
    {
        object def = typeof(T) switch
        {
            var t when t == typeof(MediaItem) => new MediaItem("", "Media", MediaKind.Video, "", "", 0, MediaInfo.Unknown, []),
            var t when t == typeof(Playlist) => new Playlist("", "Playlist", [], false, null),
            var t when t == typeof(Generator) => new Generator("", "Generator", "pattern.grid", ParamSet.Empty),
            var t when t == typeof(Canvas) => new Canvas("", "Canvas", 1920, 1080, "#000000", []),
            var t when t == typeof(Layer) => new Layer("", "Layer", SourceRef.None, new RectD(0, 0, canvas?.Width ?? 1920, canvas?.Height ?? 1080), 1, LayerBlend.Normal, true),
            var t when t == typeof(Output) => DefaultOutput(p),
            var t when t == typeof(Surface) => new Surface("", "Surface", "", Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#3AA0FF", []),
            var t when t == typeof(MeshAsset) => new MeshAsset("", "Mesh", MeshOrigin.Primitive, "", "", "plane", ParamSet.Empty, 0, 0),
            var t when t == typeof(Projector) => new Projector("", "Projector", Intrinsics.FromThrowRatio(1920, 1080, 1.5, 0, 0.5), LensDistortion.None,
                Pose.LookAt(new Vec3d(0, 1.5, -6), new Vec3d(0, 1.5, 0), Vec3d.UnitY), 0, "#FF8800", false, [], SolveLocks.None, 0),
            var t when t == typeof(CameraRig) => new CameraRig("", "Camera", CameraSourceKind.Device, "", Intrinsics.FromFieldOfView(1920, 1080, 70),
                LensDistortion.None, Pose.Identity, false),
            var t when t == typeof(Viewpoint) => new Viewpoint("", "Viewpoint", Intrinsics.FromFieldOfView(1920, 1080, 60), Pose.Identity),
            var t when t == typeof(CalibrationPin) => new CalibrationPin("", Vec3d.Zero, Vec2d.Zero, true),
            var t when t == typeof(Mask) => new Mask("", "Mask", BezierPath.Empty, MaskMode.Add, 0, false, true),
            var t when t == typeof(DeviceLink) => new DeviceLink("", "Projector", DeviceLink.DefaultProtocol, "", 0, "", "", "", 0, false),
            var t when t == typeof(PlaylistItem) => PlaylistItem.For("", SourceRef.None, "Item") with
            {
                Transition = p.Settings.DefaultTransition,
                TransitionNs = p.Settings.DefaultTransitionNs,
            },
            var t when t == typeof(ControlBinding) => new ControlBinding("", "Binding", "", "", ControlShape.Default, ControlFeedback.Default, true),
            var t when t == typeof(ControlSurface) => new ControlSurface("", "Surface", "osc", "", "", "", true),
            var t when t == typeof(TimecodeSource) => new TimecodeSource("", "Timecode", TimecodeKind.Internal),
            // A send and an LED node start off: adding one never puts a picture on the network.
            var t when t == typeof(VideoSend) => new VideoSend("", "Send", SendProtocol.Omt, "", 0, 0, 0, SendFormat.Auto, false),
            var t when t == typeof(LedNode) => new LedNode("", "LED node", LedProtocol.ArtNet, "", 0, false, 0, 0, LedSync.None, 510, "#FF8800"),
            var t when t == typeof(LedFixtureType) => new LedFixtureType("", "Fixture type", [],
                [new LedChannelSlot(LedChannel.R, 8, 0), new LedChannelSlot(LedChannel.G, 8, 0), new LedChannelSlot(LedChannel.B, 8, 0)], [], 0, "", ""),
            var t when t == typeof(LedFixture) => new LedFixture("", "Fixture", "builtin:rgb", LedSpace.Canvas, "", Transform3.Identity,
                new LedLayout(LedShape.Line, 10, 1, new Vec3d(1, 0, 0), LedStart.TopLeft, false, false, []), null, LedSampling.Point, 1,
                LedColor.Neutral, true, false, false, "#FF8800", []),
            var t when t == typeof(ControlPoint) => new ControlPoint("", "Point", Vec3d.Zero, 0.01, "", true),
            var t when t == typeof(StackGroup) => new StackGroup("", "Stack", [], "", StackFailover.Hold),
            var t when t == typeof(FxSlot) => new FxSlot("", "", "Effect", ParamSet.Empty, false, 1, FxBlend.Normal, 1, 0),
            _ => throw new EditRefusedException($"cannot add a {typeof(T).Name}"),
        };
        if (string.IsNullOrWhiteSpace(json))
            return (T)def;
        try
        {
            var baseNode = JsonSerializer.SerializeToNode(def, typeof(T), ProjectFile.Compact)!.AsObject();
            var overlay = ProjectFile.ParseTree(json)?.AsObject()
                ?? throw new EditRefusedException("ADD takes a JSON object");
            foreach (var (key, value) in overlay.ToList())
            {
                var existing = baseNode.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
                overlay.Remove(key);
                baseNode[existing] = value;
            }
            return ProjectFile.Sanitize((T)baseNode.Deserialize(typeof(T), ProjectFile.Options)!);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new EditRefusedException($"the JSON does not make a {typeof(T).Name.ToLowerInvariant()}: {ex.Message.Split('.')[0]}");
        }
    }

    private static Output DefaultOutput(Project p)
    {
        var n = p.Outputs.Length + 1;
        var x = p.Outputs.Length == 0 ? 0 : p.Outputs.Max(o => o.Plan.Right);
        return new Output("", $"Output {n}", OutputKind.Display, "", 1920, 1080, 60, OutputMode.Canvas, null,
            p.Canvases.Length > 0 ? SourceRef.Canvas(p.Canvases[0].Id) : SourceRef.None, new RectD(0, 0, 1920, 1080),
            OutputWarp.None, BlendSettings.Default, ColorTrim.Neutral, [], new RectD(x, 0, 1920, 1080), 0, true);
    }

    private static T WithId<T>(T entity, string id) where T : class =>
        (T)SetPathTyped(entity, typeof(T), "id", JsonSerializer.Serialize(id));

    /// <summary>References must resolve and numbers must make sense; an edit that breaks either is refused.</summary>
    private void Validate(Project p, object entity)
    {
        switch (entity)
        {
            case Output o:
                if (o.Width is < 16 or > 16384 || o.Height is < 16 or > 16384)
                    throw new EditRefusedException("an output is 16 to 16384 pixels each way");
                if (o.Rotation is not (0 or 90 or 180 or 270))
                    throw new EditRefusedException("rotation is 0, 90, 180 or 270");
                if (o.ProjectorId is not null && p.FindProjector(o.ProjectorId) is null)
                    throw new EditRefusedException($"no projector {o.ProjectorId}");
                if (!o.Source.IsNone && ResolveSourceProblem(o.Source) is { } op)
                    throw new EditRefusedException(op);
                if (o.Warp.Lattice is { } lat && lat.Offsets.Length != lat.Columns * lat.Rows)
                    throw new EditRefusedException("a lattice has one offset per node");
                break;
            case Surface s:
                if (s.MeshId.Length > 0 && p.FindMesh(s.MeshId) is null)
                    throw new EditRefusedException($"no mesh {s.MeshId}");
                if (!s.Content.Source.IsNone && ResolveSourceProblem(s.Content.Source) is { } sp)
                    throw new EditRefusedException(sp);
                if (s.Opacity is < 0 or > 1)
                    throw new EditRefusedException("opacity is 0 to 1");
                break;
            case Layer l:
                if (!l.Source.IsNone && ResolveSourceProblem(l.Source) is { } lp)
                    throw new EditRefusedException(lp);
                break;
            case PlaylistItem it:
                // An item may show another playlist, never one that comes back to its own.
                if (p.Playlists.FirstOrDefault(l => l.Items.IndexOfId(it.Id) >= 0) is { } owner
                    && ((it.Source.IsNone ? null : ResolveSourceProblem(it.Source)) ?? PlaylistNesting.Problem(p, owner.Id, it.Source)) is { } ip)
                    throw new EditRefusedException(ip);
                break;
            case Playlist pl:
                foreach (var item in pl.Items)
                    if (PlaylistNesting.Problem(p with { Playlists = p.Playlists.Update(pl.Id, _ => pl) }, pl.Id, item.Source) is { } np)
                        throw new EditRefusedException(np);
                break;
            case Generator g:
                var kind = Core.Content.GeneratorCatalog.Find(g.Kind) ?? throw new EditRefusedException($"no generator kind {g.Kind} — GENERATOR KINDS lists them");
                foreach (var (name, value) in g.Params.Values)
                {
                    var spec = kind.Param(name) ?? throw new EditRefusedException($"{kind.Name} has no parameter {name}");
                    if (!spec.TryNormalise(value, out _, out var why))
                        throw new EditRefusedException(why);
                }
                break;
            case Projector pj:
                if (pj.Intrinsics.Fx <= 0 || pj.Intrinsics.Fy <= 0 || pj.Intrinsics.Width < 16 || pj.Intrinsics.Height < 16)
                    throw new EditRefusedException("a projector needs a positive focal length and a raster");
                break;
            case Canvas c:
                if (c.Width is < 16 or > 16384 || c.Height is < 16 or > 16384)
                    throw new EditRefusedException("a canvas is 16 to 16384 pixels each way");
                break;
            case DeviceLink d:
                ValidateDevice(p, d);
                break;
            case FxSlot fx:
                if (fx.Kind.Length == 0)
                    throw new EditRefusedException("an effect needs a kind — FX KINDS lists them");
                if (fx.Mix is < 0 or > 1 || fx.Scale is < 0.25 or > 1)
                    throw new EditRefusedException("an effect's mix is 0 to 1 and its scale 0.25 to 1");
                break;
            case StackGroup sk:
                foreach (var pj in sk.ProjectorIds.Append(sk.MasterId).Where(x => x.Length > 0))
                    if (p.FindProjector(pj) is null)
                        throw new EditRefusedException($"no projector {pj}");
                break;
        }
        if (ValidateModules(p, entity) is { } refused)
            throw new EditRefusedException(refused);
    }

    private CommandReply Remove(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("REMOVE <id>");
        var id = args[0];
        var located = Locate(Edits.Project, id);
        if (located is null)
            return CommandReply.Fail($"nothing has the id {id}");
        if (located.Kind == "playlist" && Playback.Lane(id) is { IsIdle: false } lane && lane.Clip.State == Core.Playback.TransportState.Playing)
            return CommandReply.Fail("that playlist is playing — stop it before removing it");
        var users = UsersOf(Edits.Project, id);
        if (users.Count > 0)
            return CommandReply.Fail($"{located.Kind} {Label(located.Entity)} is in use by {string.Join(", ", users.Take(3))}{(users.Count > 3 ? "…" : "")} — remove those first");
        return FromEdit(Edits.Apply($"remove {located.Kind} {Label(located.Entity)}", p => (Locate(p, id) ?? throw new EditRefusedException($"nothing has the id {id}")).Remove(p)));
    }

    /// <summary>What refers to an id (so a removal never leaves a dangling reference on the air).</summary>
    private static List<string> UsersOf(Project p, string id)
    {
        var users = new List<string>();
        foreach (var l in p.Playlists)
            foreach (var i in l.Items)
                if (i.Source.Id == id)
                    users.Add($"{l.Name}/{i.Label}");
        foreach (var c in p.Canvases)
            foreach (var l in c.Layers)
                if (l.Source.Id == id)
                    users.Add($"{c.Name}/{l.Name}");
        foreach (var o in p.Outputs)
            if (o.Source.Id == id || o.ProjectorId == id)
                users.Add(o.Name);
        foreach (var s in p.Stage.Surfaces)
            if (s.Content.Source.Id == id || s.MeshId == id || s.Content.ViewpointId == id)
                users.Add(s.Name);
        foreach (var d in p.Stage.Devices)
            if (d.ProjectorId == id || d.OutputId == id)
                users.Add(d.Name);
        foreach (var pj in p.Stage.Projectors)
        {
            if (pj.CalibrationId == id || pj.FallbackId == id || pj.StagedId == id || pj.Priors.ControlId == id)
                users.Add(pj.Name);
            foreach (var pin in pj.Pins)
                if (pin.ControlId == id)
                    users.Add($"{pj.Name}/pin {pin.Id}");
        }
        foreach (var o in p.Outputs)
            if (o.CalibrationId == id || o.FallbackId == id || o.StagedId == id)
                users.Add(o.Name);
        foreach (var m in p.Media)
            if (m.Use == id)
                users.Add(m.Name);
        foreach (var sk in p.Stage.Stacks)
            if (sk.MasterId == id || sk.ProjectorIds.Contains(id, StringComparer.Ordinal))
                users.Add(sk.Name);
        foreach (var f in p.Leds.Fixtures)
            if (f.TypeId == id || f.Patch?.NodeId == id)
                users.Add(f.Name);
        foreach (var l in p.Playlists)
            if (l.Chase?.Sources.Contains(id, StringComparer.Ordinal) == true)
                users.Add(l.Name);
        foreach (var s in p.Stage.Surfaces)
            if (s.Pixel?.ProjectorId == id)
                users.Add(s.Name);
        foreach (var g in p.Generators)
            if (g.Inputs.Values.Any(x => x.Id == id))
                users.Add(g.Name);
        foreach (var m in p.Media)
            if (m.Live is { } live && SourceRef.TryParse(live.Fallback, out var fallback) && fallback.Id == id)
                users.Add(m.Name);
        foreach (var a in p.Stage.Aligns)
            if (a.CameraId == id || a.CanvasId == id || a.OutputIds.Contains(id, StringComparer.Ordinal))
                users.Add(a.Name);
        foreach (var f in p.Leds.Fixtures)
            if (f.SpaceId == id || f.ViewpointId == id)
                users.Add(f.Name);
        foreach (var x in p.Sends)
            if (x.Tap.EndsWith(":" + id, StringComparison.Ordinal))
                users.Add(x.Name);
        foreach (var plan in p.Show.Panic)
            if (plan.SafeLook?.Id == id)
                users.Add("the safe look of " + plan.OutputId);
        return users;
    }

    private CommandReply Move(List<string> args)
    {
        if (args.Count < 2 || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return CommandReply.Fail("MOVE <id> <index>");
        var located = Locate(Edits.Project, args[0]);
        if (located is null)
            return CommandReply.Fail($"nothing has the id {args[0]}");
        return FromEdit(Edits.Apply($"move {located.Kind} {Label(located.Entity)}", p => located.MoveTo(p, index)));
    }

    private static string Label(object entity) => entity switch
    {
        MediaItem m => m.Name,
        Playlist l => l.Name,
        PlaylistItem i => i.Label,
        Generator g => g.Name,
        Canvas c => c.Name,
        Layer l => l.Name,
        Output o => o.Name,
        Surface s => s.Name,
        MeshAsset m => m.Name,
        Projector p => p.Name,
        CameraRig c => c.Name,
        Viewpoint v => v.Name,
        ScanRecord s => s.Name,
        Mask m => m.Name,
        DeviceLink d => d.Name,
        VideoSend x => x.Name,
        ControlSurface x => x.Name,
        ControlBinding x => x.Name,
        TimecodeSource x => x.Name,
        FxSlot x => x.Name,
        FxAsset x => x.Name,
        GeneratorPreset x => x.Name,
        LedNode x => x.Name,
        LedFixtureType x => x.Name,
        LedFixture x => x.Name,
        ControlPoint x => x.Name,
        SurveySource x => x.Name,
        StackGroup x => x.Name,
        AlignGroup x => x.Name,
        CalibrationSnapshot x => x.Name,
        FleetMember x => x.Name,
        MediaVersion x => x.To,
        IEntity e => e.Id,
        _ => "?",
    };

    private CommandReply ProjectSave(List<string> args)
    {
        var path = args.Count > 0 ? string.Join(' ', args) : _projectPath;
        if (string.IsNullOrWhiteSpace(path))
            return CommandReply.Fail("PROJECT SAVE <path> — this project has not been saved before");
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temp = full + ".saving";
            File.WriteAllText(temp, ProjectFile.Serialize(Store.Project));
            DurableFile.MoveIntoPlace(temp, full);
            _projectPath = full;
            _dirty = false;
            FactsChanged();
            return CommandReply.Success(full, $"saved {Path.GetFileName(full)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return CommandReply.Fail($"could not save: {ex.Message}");
        }
    }

    private CommandReply ProjectOpen(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("PROJECT OPEN <path>");
        var path = string.Join(' ', args);
        Project project;
        try
        {
            project = ProjectFile.Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            return CommandReply.Fail($"could not open {Path.GetFileName(path)}: {ex.Message}");
        }
        Store.Replace(project, $"opened {project.Name}");
        _projectPath = Path.GetFullPath(path);
        _dirty = false;
        return CommandReply.Success(effect: $"opened {project.Name}");
    }

    private CommandReply ProjectNew(List<string> args)
    {
        var name = args.Count > 0 ? string.Join(' ', args) : "Untitled";
        Store.Replace(Project.CreateDefault(name), $"new project {name}");
        _projectPath = null;
        _dirty = false;
        return CommandReply.Success(effect: $"new project {name}");
    }
}
