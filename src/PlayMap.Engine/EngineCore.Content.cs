using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;

using ProjectFile = PlayMap.Core.Model.ProjectJson;

namespace PlayMap.Engine;

/// <summary>Content, outputs, mapping, media and scan verbs, and the JSON the list verbs answer with.</summary>
public sealed partial class EngineCore
{
    private CommandReply Pattern(List<string> args)
    {
        var on = TakeOn(args);
        if (args.Count < 1)
            return CommandReply.Fail("PATTERN <kind>|OFF [ON <output>|ALL]");
        if (IsOff(args[0]))
        {
            if (_air.PatternKind is null)
                return CommandReply.Fail("no pattern is up");
            SetAir(a => a with { PatternKind = null, PatternOutputs = [] });
            return CommandReply.Success(effect: "pattern off — the outputs show the show");
        }
        var kind = GeneratorCatalog.FindLoose(args[0]);
        if (kind is null || kind.Family is not ("pattern"))
            return CommandReply.Fail($"no test pattern '{args[0]}' — {string.Join(", ", GeneratorCatalog.All.Where(k => k.Family == "pattern").Select(k => k.Id[8..]))}");
        System.Collections.Immutable.ImmutableArray<string> outputs = [];
        if (on is not null && !on.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            var o = ResolveOutput(on);
            if (o is null)
                return CommandReply.Fail($"no output {on}");
            outputs = [o.Id];
        }
        if (_air.PatternKind == kind.Id && _air.PatternOutputs.SequenceEqual(outputs))
            return CommandReply.Fail($"{kind.Name} is already up");
        SetAir(a => a with { PatternKind = kind.Id, PatternOutputs = outputs });
        return CommandReply.Success(effect: $"{kind.Name} on {(outputs.IsEmpty ? "every output" : Store.Project.FindOutput(outputs[0])!.Name)}");
    }

    private static string GeneratorKindsJson() => JsonOut(w =>
    {
        w.WriteStartArray();
        foreach (var k in GeneratorCatalog.All)
        {
            w.WriteStartObject();
            w.WriteString("id", k.Id);
            w.WriteString("family", k.Family);
            w.WriteString("name", k.Name);
            w.WriteString("words", k.Words);
            w.WriteBoolean("animated", k.Animated);
            w.WriteStartArray("params");
            foreach (var p in k.Params)
            {
                w.WriteStartObject();
                w.WriteString("name", p.Name);
                w.WriteString("type", p.Type.ToString().ToLowerInvariant());
                w.WriteString("default", p.Default);
                if (p.Type is ParamType.Number or ParamType.Integer)
                {
                    w.WriteNumber("min", p.Min);
                    w.WriteNumber("max", p.Max);
                }
                if (p.Choices.Count > 0)
                {
                    w.WriteStartArray("choices");
                    foreach (var c in p.Choices)
                        w.WriteStringValue(c);
                    w.WriteEndArray();
                }
                w.WriteString("words", p.Words);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    private CommandReply GeneratorNew(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("GENERATOR NEW <kind> [<name>]");
        var kind = GeneratorCatalog.FindLoose(args[0]);
        if (kind is null)
            return CommandReply.Fail($"no generator kind '{args[0]}' — GENERATOR KINDS lists them");
        var name = args.Count > 1 ? string.Join(' ', args.Skip(1)) : kind.Name;
        string id = "";
        var r = Store.Apply($"new generator {name}", p =>
        {
            id = p.NewId(Ids.Generator);
            return p with { Generators = p.Generators.Add(new Generator(id, name, kind.Id, ParamSet.Empty)) };
        });
        return r.Changed ? CommandReply.Success(id, $"made {kind.Name} generator {name}") : FromEdit(r);
    }

    private CommandReply GeneratorSet(List<string> args)
    {
        if (args.Count < 3)
            return CommandReply.Fail("GENERATOR SET <generator> <param> <value>");
        var g = Store.Project.FindGenerator(args[0]) ?? Store.Project.Generators.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (g is null)
            return CommandReply.Fail($"no generator {args[0]}");
        var kind = GeneratorCatalog.Find(g.Kind);
        var spec = kind?.Param(args[1]);
        if (kind is null || spec is null)
            return CommandReply.Fail($"{g.Name} has no parameter {args[1]}{(kind is null ? "" : " — it has " + string.Join(", ", kind.Params.Select(p => p.Name)))}");
        var value = string.Join(' ', args.Skip(2));
        if (!spec.TryNormalise(value, out var normalised, out var why))
            return CommandReply.Fail(why);
        if (kind.Value(g.Params, spec.Name) == normalised)
            return CommandReply.Fail($"{spec.Name} is already {normalised}");
        return FromEdit(Store.Apply($"{g.Name} {spec.Name} {normalised}",
            p => p with { Generators = p.Generators.Update(g.Id, x => x with { Params = x.Params.With(spec.Name, normalised) }) },
            coalesceKey: $"gen {g.Id} {spec.Name}"));
    }

    private Output? ResolveOutput(string key)
    {
        var p = Store.Project;
        if (p.FindOutput(key) is { } o)
            return o;
        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= p.Outputs.Length)
            return p.Outputs[n - 1];
        return p.Outputs.FirstOrDefault(x => x.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private CommandReply OutputEnable(List<string> args)
    {
        if (args.Count < 2 || !TryOnOff(args[1], out var on))
            return CommandReply.Fail("OUTPUT ENABLE <output> ON|OFF");
        var o = ResolveOutput(args[0]);
        if (o is null)
            return CommandReply.Fail($"no output {args[0]}");
        if (o.Enabled == on)
            return CommandReply.Fail($"{o.Name} is already {(on ? "on" : "off")}");
        return FromEdit(Store.Apply($"{o.Name} {(on ? "on" : "off")}", p => p with { Outputs = p.Outputs.Update(o.Id, x => x with { Enabled = on }) }));
    }

    private CommandReply OutputSource(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("OUTPUT SOURCE <output> <source>");
        var o = ResolveOutput(args[0]);
        if (o is null)
            return CommandReply.Fail($"no output {args[0]}");
        if (!SourceRef.TryParse(args[1], out var source))
            return CommandReply.Fail("a source is media:<id>, playlist:<id>, generator:<id>, canvas:<id>, live:<id>, timer:<id>, color:#RRGGBB or none");
        if (!source.IsNone && ResolveSourceProblem(source) is { } problem)
            return CommandReply.Fail(problem);
        if (o.Source == source)
            return CommandReply.Fail($"{o.Name} already shows that");
        var mode = source.Kind == SourceKind.Canvas ? OutputMode.Canvas : o.Mode == OutputMode.Projector && source.IsNone ? OutputMode.Projector : OutputMode.Source;
        return FromEdit(Store.Apply($"{o.Name} shows {SourceLabel(source)}", p => p with { Outputs = p.Outputs.Update(o.Id, x => x with { Source = source, Mode = mode }) }));
    }

    private CommandReply Identify(List<string> args)
    {
        var seconds = 5.0;
        if (args.Count > 0 && args[^1].All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            seconds = Math.Clamp(double.Parse(args[^1], CultureInfo.InvariantCulture), 1, 60);
            args.RemoveAt(args.Count - 1);
        }
        var until = LeaderNowNs + (long)(seconds * 1e9);
        SetAir(a => a with { IdentifyUntilLeaderNs = until });
        return CommandReply.Success(effect: string.Create(CultureInfo.InvariantCulture, $"identifying every output for {seconds:0.#} s"));
    }

    private CommandReply Blackout(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("BLACKOUT ON|OFF|TOGGLE");
        bool on;
        if (args[0].Equals("TOGGLE", StringComparison.OrdinalIgnoreCase))
            on = !_air.Blackout;
        else if (!TryOnOff(args[0], out on))
            return CommandReply.Fail("BLACKOUT ON|OFF|TOGGLE");
        if (_air.Blackout == on)
            return CommandReply.Fail(on ? "already blacked out" : "not blacked out");
        SetAir(a => a with { Blackout = on });
        return CommandReply.Success(effect: on ? "every output to black" : "out of blackout");
    }

    private CommandReply Freeze(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("FREEZE ON|OFF|TOGGLE");
        bool on;
        if (args[0].Equals("TOGGLE", StringComparison.OrdinalIgnoreCase))
            on = !_air.Frozen;
        else if (!TryOnOff(args[0], out on))
            return CommandReply.Fail("FREEZE ON|OFF|TOGGLE");
        if (_air.Frozen == on)
            return CommandReply.Fail(on ? "already frozen" : "not frozen");
        SetAir(a => a with { Frozen = on });
        return CommandReply.Success(effect: on ? "every output holds its frame" : "unfrozen");
    }

    private CommandReply SurfaceSource(List<string> args)
    {
        if (args.Count < 2)
            return CommandReply.Fail("SURFACE SOURCE <surface> <source>");
        var s = Store.Project.FindSurface(args[0]) ?? Store.Project.Stage.Surfaces.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (s is null)
            return CommandReply.Fail($"no surface {args[0]}");
        if (!SourceRef.TryParse(args[1], out var source))
            return CommandReply.Fail("a source is media:<id>, playlist:<id>, generator:<id>, canvas:<id>, live:<id>, timer:<id>, color:#RRGGBB or none");
        if (!source.IsNone && ResolveSourceProblem(source) is { } problem)
            return CommandReply.Fail(problem);
        if (s.Content.Source == source)
            return CommandReply.Fail($"{s.Name} already shows that");
        return FromEdit(Store.Apply($"{s.Name} shows {SourceLabel(source)}",
            p => p.WithStage(st => st with { Surfaces = st.Surfaces.Update(s.Id, x => x with { Content = x.Content with { Source = source } }) })));
    }

    private CommandReply SurfaceShow(List<string> args)
    {
        if (args.Count < 2 || !TryOnOff(args[1], out var on))
            return CommandReply.Fail("SURFACE SHOW <surface> ON|OFF");
        var s = Store.Project.FindSurface(args[0]);
        if (s is null)
            return CommandReply.Fail($"no surface {args[0]}");
        if (s.Visible == on)
            return CommandReply.Fail($"{s.Name} is already {(on ? "shown" : "hidden")}");
        return FromEdit(Store.Apply($"{s.Name} {(on ? "shown" : "hidden")}",
            p => p.WithStage(st => st with { Surfaces = st.Surfaces.Update(s.Id, x => x with { Visible = on }) })));
    }

    private CommandReply WarpReset(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("WARP RESET <output>");
        var o = ResolveOutput(args[0]);
        if (o is null)
            return CommandReply.Fail($"no output {args[0]}");
        if (o.Warp == OutputWarp.None || (o.Warp.Pin.IsNone && (o.Warp.Lattice?.IsAtRest ?? true)))
            return CommandReply.Fail($"{o.Name}'s warp is already at rest");
        return FromEdit(Store.Apply($"{o.Name} warp reset", p => p with { Outputs = p.Outputs.Update(o.Id, x => x with { Warp = OutputWarp.None }) }));
    }

    private CommandReply WarpNode(List<string> args)
    {
        if (args.Count < 5
            || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var col)
            || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var row)
            || !double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var dx)
            || !double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var dy))
            return CommandReply.Fail("WARP NODE <output> <column> <row> <dx> <dy>");
        var o = ResolveOutput(args[0]);
        if (o is null)
            return CommandReply.Fail($"no output {args[0]}");
        var lattice = o.Warp.Lattice ?? WarpLattice.AtRest(5, 5);
        if (col < 0 || row < 0 || col >= lattice.Columns || row >= lattice.Rows)
            return CommandReply.Fail($"{o.Name}'s lattice is {lattice.Columns}×{lattice.Rows}");
        if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1)
            return CommandReply.Fail("a node moves at most a whole output (±1)");
        var index = row * lattice.Columns + col;
        var moved = lattice with { Offsets = lattice.Offsets.SetItem(index, new Vec2d(dx, dy)) };
        return FromEdit(Store.Apply($"{o.Name} warp node {col},{row}",
            p => p with { Outputs = p.Outputs.Update(o.Id, x => x with { Warp = x.Warp with { Enabled = true, Lattice = moved } }) },
            coalesceKey: $"warp {o.Id} {col} {row}"));
    }

    private CommandReply ProjectorSolve(List<string> args) =>
        args.Count < 1 ? CommandReply.Fail("PROJECTOR SOLVE <projector> [LOCK SHIFT|FOCAL|NONE]") : SolveProjector(args);

    private CommandReply BlendSolve(List<string> args) => SolveBlend(args);

    private CommandReply ScanStart(List<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
            return CommandReply.Fail("SCAN START <camera> [<projector>…] [MODE FAST|ROBUST|DIFFICULT]");
        var mode = "robust";
        var mi = args.FindIndex(a => a.Equals("MODE", StringComparison.OrdinalIgnoreCase));
        if (mi >= 0)
        {
            if (mi + 1 >= args.Count)
                return CommandReply.Fail("MODE takes FAST, ROBUST or DIFFICULT");
            mode = args[mi + 1].ToLowerInvariant();
            args.RemoveRange(mi, 2);
            if (mode is not ("fast" or "robust" or "difficult"))
                return CommandReply.Fail("MODE takes FAST, ROBUST or DIFFICULT");
        }
        var p = Store.Project;
        var camera = p.Stage.Cameras.Find(args[0]);
        var simulated = camera is null && args[0].Equals(Scanning.SimulatedRig.CameraId, StringComparison.OrdinalIgnoreCase);
        if (camera is null && !simulated)
            return CommandReply.Fail($"no camera {args[0]} (\"{Scanning.SimulatedRig.CameraId}\" scans the simulator)");
        var projectors = args.Count > 1 ? args.Skip(1).ToList() : p.Stage.Projectors.Select(x => x.Id).ToList();
        if (projectors.Count == 0)
            return CommandReply.Fail("no projector to scan — add one first");
        foreach (var id in projectors)
            if (p.FindProjector(id) is null)
                return CommandReply.Fail($"no projector {id}");
        if (Services.Scan.Current is { State: not ("done" or "failed" or "cancelled") })
            return CommandReply.Fail("a scan is already running — SCAN CANCEL stops it");
        var task = Services.Scan.StartAsync(camera?.Id ?? Scanning.SimulatedRig.CameraId, projectors, mode, ct);
        if (task.IsFaulted)
            return CommandReply.Fail(task.Exception?.InnerException?.Message ?? "the scan did not start");
        _ = task.ContinueWith(t => _lane.Post(() =>
        {
            Journal.Add(t.IsCompletedSuccessfully ? "scan" : "refused", t.IsCompletedSuccessfully ? $"scan {t.Result} finished" : t.Exception?.InnerException?.Message ?? "scan failed", ok: t.IsCompletedSuccessfully);
            FactsChanged();
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var effect = $"scanning {projectors.Count} projector{(projectors.Count == 1 ? "" : "s")} through {camera?.Name ?? "the simulated camera"} ({mode})";
        return ScanStarted(effect);
    }

    private CommandReply ScanApply(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("SCAN APPLY <scan>");
        var scan = Store.Project.Stage.Scans.Find(args[0]);
        return scan is null ? CommandReply.Fail($"no scan {args[0]}") : ApplyScan(scan);
    }

    private CommandReply MediaImport(List<string> args, WireLine line, CancellationToken ct)
    {
        if (args.Count < 1)
            return CommandReply.Fail("MEDIA IMPORT <path|url> [<name>]");
        if (Services.Media is not { } media)
            return CommandReply.Fail("no media engine on this node");
        var location = args[0];
        var isUrl = location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || location.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("srt://", StringComparison.OrdinalIgnoreCase);
        if (!isUrl && !File.Exists(location) && !Directory.Exists(location))
            return CommandReply.Fail($"no file {location}");
        var name = args.Count > 1 ? string.Join(' ', args.Skip(1)) : (isUrl ? location : Path.GetFileNameWithoutExtension(location));
        string id = "";
        var kind = isUrl ? (location.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || location.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
            ? MediaKind.Embed : location.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? MediaKind.Web : MediaKind.Stream) : MediaKind.Video;
        var r = Store.Apply($"import {name}", p =>
        {
            id = p.NewId(Ids.Media);
            return p with { Media = p.Media.Add(new MediaItem(id, name, kind, location, "", 0, MediaInfo.Unknown, [])) };
        });
        if (!r.Changed)
            return FromEdit(r);
        if (isUrl)
            return CommandReply.Success(id, $"added {name}");
        var mediaId = id;
        var task = media.ImportAsync(location, null, ct);
        _ = task.ContinueWith(t => _lane.Post(() =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                Journal.Add("refused", $"could not import {name}: {t.Exception?.InnerException?.Message ?? "cancelled"}", ok: false);
                return;
            }
            var found = t.Result;
            Store.Apply($"probed {name}", p => p with
            {
                Media = p.Media.Update(mediaId, m => m with { Kind = found.Kind, Info = found.Info, ContentHash = found.ContentHash, SizeBytes = found.SizeBytes }),
            });
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", mediaId);
            w.WriteString("stage", "accepted");
            w.WriteString("effect", $"importing {name} (hashing and probing)");
            w.WriteEndObject();
        }), $"importing {name}") with { Applied = false };
    }

    private CommandReply MediaHas(List<string> args)
    {
        if (args.Count < 1 || !args[0].StartsWith("sha256:", StringComparison.Ordinal))
            return CommandReply.Fail("MEDIA HAS sha256:<64 hex>");
        var path = Services.Media?.PathForHash(args[0]);
        return CommandReply.Success(path is null ? "no" : "yes");
    }

    private CommandReply MediaFetch(List<string> args, CancellationToken ct)
    {
        if (args.Count < 1)
            return CommandReply.Fail("MEDIA FETCH <media>|ALL [FROM <node>]");
        string? from = null;
        var fi = args.FindIndex(a => a.Equals("FROM", StringComparison.OrdinalIgnoreCase));
        if (fi >= 0 && fi + 1 < args.Count)
        {
            from = args[fi + 1];
            args.RemoveRange(fi, 2);
        }
        var p = Store.Project;
        var wanted = args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? p.Media.Where(m => m.IsFile && m.ContentHash.Length > 0 && Services.Media?.PathForHash(m.ContentHash) is null).ToList()
            : p.FindMedia(args[0]) is { } one ? [one] : [];
        if (wanted.Count == 0)
            return CommandReply.Fail(args[0].Equals("ALL", StringComparison.OrdinalIgnoreCase) ? "nothing is missing" : $"no media {args[0]}");
        if (Services.NetworkControl is not { } nc)
            return CommandReply.Fail("the network is not running on this node");
        var task = nc.FetchAsync(wanted, from, ct);
        _ = task.ContinueWith(t => Journal.Add(t.IsCompletedSuccessfully ? "media" : "refused",
            t.IsCompletedSuccessfully ? t.Result : t.Exception?.InnerException?.Message ?? "fetch failed", ok: t.IsCompletedSuccessfully),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return CommandReply.Accepted($"fetching {wanted.Count} file{(wanted.Count == 1 ? "" : "s")}");
    }

    private string PlaylistsJson() => JsonOut(w =>
    {
        w.WriteStartArray();
        foreach (var l in Store.Project.Playlists)
            StateWriter.WritePlaylist(w, this, l);
        w.WriteEndArray();
    });

    private string PlaylistJson(Playlist list) => JsonOut(w =>
    {
        w.WriteStartObject();
        w.WritePropertyName("playlist");
        JsonSerializer.Serialize(w, list, ProjectFile.Compact);
        w.WritePropertyName("transport");
        StateWriter.WritePlaylist(w, this, list);
        w.WriteEndObject();
    });

    private string MediaListJson() => JsonOut(w =>
    {
        w.WriteStartArray();
        foreach (var m in Store.Project.Media)
        {
            w.WriteStartObject();
            w.WriteString("id", m.Id);
            w.WriteString("name", m.Name);
            w.WriteString("kind", m.Kind.ToString().ToLowerInvariant());
            w.WriteString("hash", m.ContentHash);
            w.WriteNumber("durationMs", m.Info.DurationNs / 1_000_000);
            w.WriteNumber("width", m.Info.Width);
            w.WriteNumber("height", m.Info.Height);
            var ready = Services.Media?.Readiness(m);
            w.WriteBoolean("present", ready?.Present ?? !m.IsFile);
            w.WriteBoolean("ready", ready is { Present: true, Decodable: true } || !m.IsFile);
            w.WriteString("words", ready?.Words ?? (m.IsFile ? "no media engine" : "a URL"));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    private string MediaStatusJson() => JsonOut(w =>
    {
        var media = Store.Project.Media;
        var missing = media.Count(m => m.IsFile && Services.Media?.Readiness(m) is not { Present: true });
        w.WriteStartObject();
        w.WriteNumber("items", media.Length);
        w.WriteNumber("missing", missing);
        w.WriteBoolean("ready", missing == 0);
        w.WriteEndObject();
    });

    private string OutputsJson() => JsonOut(w =>
    {
        w.WriteStartArray();
        foreach (var o in Store.Project.Outputs)
            StateWriter.WriteOutput(w, this, o);
        w.WriteEndArray();
    });

    private string DisplaysJson() => JsonOut(w =>
    {
        w.WriteStartArray();
        foreach (var d in Services.Outputs.Displays())
        {
            w.WriteStartObject();
            w.WriteString("key", d.Key);
            w.WriteString("name", d.Name);
            w.WriteNumber("x", d.X);
            w.WriteNumber("y", d.Y);
            w.WriteNumber("width", d.Width);
            w.WriteNumber("height", d.Height);
            w.WriteNumber("refreshHz", d.RefreshHz);
            w.WriteBoolean("primary", d.Primary);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    private string ScanStatusJson() => JsonOut(WriteScanState);

    private string NodesJson() => JsonOut(w =>
    {
        w.WriteStartArray();
        foreach (var p in Services.Network.Peers())
            StateWriter.WritePeer(w, p);
        w.WriteEndArray();
    });

    private string SyncJson() => JsonOut(w => StateWriter.WriteSync(w, this));
}
