using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;

using MediaCheck = PlayMap.Engine.Show.MediaCheck;
using PrivateAddress = PlayMap.Core.Media.PrivateAddress;
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
            if (o is null && (Edits.Project.FindCanvas(on) ?? Edits.Project.Canvases.FirstOrDefault(c => c.Name.Equals(on, StringComparison.OrdinalIgnoreCase))) is not null)
                return CommandReply.Fail(VerbCatalog.NotAvailableYet("PATTERN … ON <canvas>") + " — PATTERN <kind> ON <output>|ALL is");
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
            var traits = GeneratorTraits.Of(k.Id);
            w.WriteStartObject("traits");
            w.WriteString("time", GeneratorTraits.Word(traits.Time));
            w.WriteNumber("periodSeconds", traits.PeriodSeconds);
            w.WriteNumber("maxLifeSeconds", traits.MaxLifeSeconds);
            w.WriteBoolean("readsAudio", traits.ReadsAudio);
            w.WriteBoolean("mayFlash", traits.MayFlash);
            w.WriteBoolean("drawsBindings", traits.DrawsBindings);
            w.WriteEndObject();
            w.WriteStartArray("inputs");
            foreach (var input in InputsOf(k.Id))
                w.WriteStringValue(input);
            w.WriteEndArray();
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
            return CommandReply.Fail("GENERATOR NEW <kind> [<name>] [PRESET <preset>]");
        var kind = GeneratorCatalog.FindLoose(args[0]);
        if (kind is null)
            return CommandReply.Fail($"no generator kind '{args[0]}' — GENERATOR KINDS lists them");
        var presetName = TakePreset(args);
        var parameters = ParamSet.Empty;
        if (presetName is not null)
        {
            // A look the kind has (saved in the show or curated), written whole as RECALL writes it.
            if (LookFor(kind, presetName, out var why) is not { } look)
                return CommandReply.Fail(why);
            parameters = look.Params;
            presetName = look.Name;
        }
        var name = args.Count > 1 ? string.Join(' ', args.Skip(1)) : kind.Name;
        // Its own time starts now: the same instant on every node that applies this edit.
        var origin = LeaderNowNs;
        string id = "";
        var r = Edits.Apply($"new generator {name}", p =>
        {
            id = p.NewId(Ids.Generator);
            return p with { Generators = p.Generators.Add(new Generator(id, name, kind.Id, parameters) { OriginNs = origin }) };
        });
        return r.Changed
            ? CommandReply.Success(id, $"made {kind.Name} generator {name}{(presetName is null ? "" : " from " + presetName)}")
            : FromEdit(r);
    }

    private CommandReply GeneratorSet(List<string> args)
    {
        if (args.Count < 3)
            return CommandReply.Fail("GENERATOR SET <generator> <param> <value> | GENERATOR SET <generator> <param> BY <delta>");
        var g = Edits.Project.FindGenerator(args[0]) ?? Edits.Project.Generators.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (g is null)
            return CommandReply.Fail($"no generator {args[0]}");
        var kind = GeneratorCatalog.Find(g.Kind);
        var spec = kind?.Param(args[1]);
        if (kind is null || spec is null)
            return CommandReply.Fail($"{g.Name} has no parameter {args[1]}{(kind is null ? "" : " — it has " + string.Join(", ", kind.Params.Select(p => p.Name)))}");
        string normalised;
        if (args.Count == 4 && args[2].Equals("BY", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var delta) || !double.IsFinite(delta) || delta == 0)
                return CommandReply.Fail("BY takes a step count other than 0 (a number: 1 is a hundredth of the range)");
            if (Stepped(kind, spec, g.Params, delta, out var stepWhy) is not { } stepped)
                return CommandReply.Fail(stepWhy);
            normalised = stepped;
        }
        else
        {
            var value = string.Join(' ', args.Skip(2));
            if (!spec.TryNormalise(value, out normalised, out var why))
                return CommandReply.Fail(why);
        }
        if (kind.Value(g.Params, spec.Name) == normalised)
            return CommandReply.Fail($"{spec.Name} is already {normalised}");
        return FromEdit(Edits.Apply($"{g.Name} {spec.Name} {normalised}",
            p => p with { Generators = p.Generators.Update(g.Id, x => x with { Params = x.Params.With(spec.Name, normalised) }) },
            coalesceKey: $"gen {g.Id} {spec.Name}"));
    }

    private Output? ResolveOutput(string key)
    {
        var p = Edits.Project;
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
        return FromEdit(Edits.Apply($"{o.Name} {(on ? "on" : "off")}", p => p with { Outputs = p.Outputs.Update(o.Id, x => x with { Enabled = on }) }));
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
        return FromEdit(Edits.Apply($"{o.Name} shows {SourceLabel(source)}", p => p with { Outputs = p.Outputs.Update(o.Id, x => x with { Source = source, Mode = mode }) }));
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
        var s = Edits.Project.FindSurface(args[0]) ?? Edits.Project.Stage.Surfaces.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (s is null)
            return CommandReply.Fail($"no surface {args[0]}");
        if (!SourceRef.TryParse(args[1], out var source))
            return CommandReply.Fail("a source is media:<id>, playlist:<id>, generator:<id>, canvas:<id>, live:<id>, timer:<id>, color:#RRGGBB or none");
        if (!source.IsNone && ResolveSourceProblem(source) is { } problem)
            return CommandReply.Fail(problem);
        if (s.Content.Source == source)
            return CommandReply.Fail($"{s.Name} already shows that");
        return FromEdit(Edits.Apply($"{s.Name} shows {SourceLabel(source)}",
            p => p.WithStage(st => st with { Surfaces = st.Surfaces.Update(s.Id, x => x with { Content = x.Content with { Source = source } }) })));
    }

    private CommandReply SurfaceShow(List<string> args)
    {
        if (args.Count < 2 || !TryOnOff(args[1], out var on))
            return CommandReply.Fail("SURFACE SHOW <surface> ON|OFF");
        var s = Edits.Project.FindSurface(args[0]);
        if (s is null)
            return CommandReply.Fail($"no surface {args[0]}");
        if (s.Visible == on)
            return CommandReply.Fail($"{s.Name} is already {(on ? "shown" : "hidden")}");
        return FromEdit(Edits.Apply($"{s.Name} {(on ? "shown" : "hidden")}",
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
        return FromEdit(Edits.Apply($"{o.Name} warp reset", p => p with { Outputs = p.Outputs.Update(o.Id, x => x with { Warp = OutputWarp.None }) }));
    }

    private CommandReply WarpNode(List<string> args)
    {
        if (args.Count >= 4 && args[3].ToUpperInvariant() is "TO" or "BY")
            return CommandReply.Fail(VerbCatalog.NotAvailableYet("WARP NODE … TO|BY … PX") + " — WARP NODE <output> <column> <row> <dx> <dy> moves a node in output units");
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
        return FromEdit(Edits.Apply($"{o.Name} warp node {col},{row}",
            p => p with { Outputs = p.Outputs.Update(o.Id, x => x with { Warp = x.Warp with { Enabled = true, Lattice = moved } }) },
            coalesceKey: $"warp {o.Id} {col} {row}"));
    }

    private CommandReply BlendSolve(List<string> args) => SolveBlend(args);

    private CommandReply ScanStart(List<string> args, SessionInfo session, CancellationToken ct)
    {
        if (args.Count < 1)
            return CommandReply.Fail("SCAN START <camera>|phone:<phone> [<projector>…] [MODE FAST|ROBUST|DIFFICULT] [INTO <scan>]");
        // A scan lights the outputs and lands its record on the live stage: from the blind draft's session it is refused, never staged.
        if (FromDraft(session))
            return NotStaged("SCAN START", "a scan lights the outputs and is recorded on the live stage");
        // wp2-multiscan: INTO adds a view to a scan; phone:<id> names a phone's camera rig.
        string? into = null;
        var ii = args.FindIndex(a => a.Equals("INTO", StringComparison.OrdinalIgnoreCase));
        if (ii >= 0)
        {
            if (ii + 1 >= args.Count)
                return CommandReply.Fail("INTO takes the scan the view is added to");
            into = args[ii + 1];
            args.RemoveRange(ii, 2);
        }
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
        var camera = Scanning.ScanCameras.Find(p, args[0]);
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
        var task = Services.Scan is Scanning.ScanRunner runner
            ? runner.StartAsync(camera?.Id ?? Scanning.SimulatedRig.CameraId, projectors, mode, into, ct)
            : into is null ? Services.Scan.StartAsync(camera?.Id ?? Scanning.SimulatedRig.CameraId, projectors, mode, ct)
            : Task.FromException<string>(new InvalidOperationException("this node's scans take no views INTO them"));
        if (task.IsFaulted)
            return CommandReply.Fail(task.Exception?.InnerException?.Message ?? "the scan did not start");
        // The scan's end is journalled behind this line's row (the runner begins its run there too), whatever backend ends it.
        AfterRow(() => _ = task.ContinueWith(t => _lane.Post(() =>
        {
            Journal.Add(t.IsCompletedSuccessfully ? "scan" : "refused", t.IsCompletedSuccessfully ? $"scan {t.Result} finished" : t.Exception?.InnerException?.Message ?? "scan failed", ok: t.IsCompletedSuccessfully);
            FactsChanged();
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        var effect = $"scanning {projectors.Count} projector{(projectors.Count == 1 ? "" : "s")} through {camera?.Name ?? "the simulated camera"} ({mode})";
        return ScanStarted(effect);
    }

    private CommandReply ScanApply(List<string> args)
    {
        if (args.Count < 1)
            return CommandReply.Fail("SCAN APPLY <scan>");
        var scan = Edits.Project.Stage.Scans.Find(args[0]);
        return scan is null ? CommandReply.Fail($"no scan {args[0]}") : ApplyScan(scan);
    }

    private CommandReply MediaImport(List<string> args, WireLine line, CancellationToken ct)
    {
        if (args.Count < 1)
            return CommandReply.Fail("MEDIA IMPORT <path|url> [<name>]");
        // An address that carries a pass is never kept, and no word of the line is repeated in the refusal (its row shows the line with
        // the address as the marker): checked before anything else reads the location.
        if (KeptPassRefusal(args) is { } pass)
            return CommandReply.Fail(pass);
        // A survey or model file is never media: no decoder plays it, and the library would keep a clip that cannot play.
        if (Path.GetExtension(args[0]).ToLowerInvariant() is ".obj" or ".ply" or ".gltf" or ".glb" or ".las" or ".laz" or ".e57" or ".xyz" or ".pts")
            return CommandReply.Fail($"{Path.GetFileName(args[0])} is a survey or model, not media — SURVEY IMPORT {WireGrammar.Quote(args[0])} brings it onto the stage");
        if (Services.Media is not { } media)
            return CommandReply.Fail("no media engine on this node");
        var location = args[0];
        var (kind, refused) = ImportKind(location);
        if (refused is not null)
            return CommandReply.Fail(refused);
        var isUrl = kind == MediaKind.Stream;
        if (!isUrl && !File.Exists(location) && !Directory.Exists(location))
            return CommandReply.Fail($"no file {location}");
        var name = args.Count > 1 ? string.Join(' ', args.Skip(1)) : (isUrl ? location : Path.GetFileNameWithoutExtension(location));
        string id = "";
        // The probe's result lands where the item was added (a blind draft keeps its own).
        var store = Edits;
        var r = store.Apply($"import {name}", p =>
        {
            id = p.NewId(Ids.Media);
            return p with { Media = p.Media.Add(new MediaItem(id, name, kind, location, "", 0, MediaInfo.Unknown, [])) };
        });
        if (!r.Changed)
            return FromEdit(r);
        if (isUrl)
            return CommandReply.Success(id, $"added {name}");
        var mediaId = id;
        // The import journals what it finds (a file that will not play here, FFmpeg missing) and can find it at once, and what it comes
        // to is journalled when it ends: begun behind this line's row, so both follow the verb.
        AfterRow(() => _ = media.ImportAsync(location, null, ct).ContinueWith(t => _lane.Post(() =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                Journal.Add("refused", $"could not import {name}: {t.Exception?.InnerException?.Message ?? "cancelled"}", ok: false);
                return;
            }
            var found = t.Result;
            store.Apply($"probed {name}", p => p with
            {
                Media = p.Media.Update(mediaId, m => m with { Kind = found.Kind, Info = found.Info, ContentHash = found.ContentHash, SizeBytes = found.SizeBytes }),
            });
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", mediaId);
            w.WriteString("stage", "accepted");
            w.WriteString("effect", $"importing {name} (hashing and probing)");
            w.WriteEndObject();
        }), $"importing {name}") with { Applied = false };
    }

    /// <summary>What every refusal of an address that carries a pass says of it: no copy of the pass is kept anywhere.</summary>
    private const string KeptNowhere = "PlayMap keeps no pass in its project, its journal or a reply, so nothing was added";

    /// <summary>Why MEDIA IMPORT refuses a Stagewatch window's address: its token is the window's pass.</summary>
    internal const string WindowAddressWords = "that is a Stagewatch window's private address: " + KeptNowhere
        + "; PlayMap draws no Stagewatch window yet — to show a Stagewatch's timer as a timer source, LINK STAGEWATCH <its host>[:<port>],"
        + " the host alone, never the window's address";

    /// <summary>Why MEDIA IMPORT refuses any other address that carries a pass (<see cref="PrivateAddress"/>).</summary>
    internal const string PassAddressWords = "that address carries a pass (a user, a PIN, a key, a token or a viewing code): " + KeptNowhere;

    /// <summary>Why MEDIA IMPORT refuses a name that holds an address carrying a pass.</summary>
    internal const string PassNameWords = "the name holds an address that carries a pass: " + KeptNowhere + "; name the item in words";

    /// <summary>Why MEDIA IMPORT refuses a page: nothing on this node draws one.</summary>
    internal const string PageImportWords = "an http(s) address is a web page, and PlayMap has no browser source for pages (ROADMAP L10) — nothing was added;"
        + " send the page as NDI from a browser, or import a media file by its path on this machine";

    /// <summary>Why MEDIA IMPORT refuses an embed: nothing on this node draws one.</summary>
    internal const string EmbedImportWords = "a YouTube address is an embed, and PlayMap has no browser source for embeds (ROADMAP L10) — nothing was added;"
        + " send it as NDI from a browser";

    /// <summary>Why MEDIA IMPORT refuses an HLS or DASH address over https: the shipped FFmpeg is not shown to open TLS.</summary>
    internal const string HttpsStreamWords = "this node's FFmpeg is not known to open https streams — nothing was added; use an http://, rtsp:// or srt://"
        + " address, or NDI";

    /// <summary>Why MEDIA IMPORT refuses its words, when one is an address that carries a pass (<see cref="PrivateAddress.IsWord"/>):
    /// the location, or any word of the name; null when none is. The words repeat nothing of the address.</summary>
    private static string? KeptPassRefusal(List<string> args)
    {
        if (PrivateAddress.IsWord(args[0]))
            return PrivateAddress.IsStagewatchWindow(args[0]) ? WindowAddressWords : PassAddressWords;
        for (var i = 1; i < args.Count; i++)
        {
            if (PrivateAddress.InText(args[i]))
                return PassNameWords;
        }
        return null;
    }

    /// <summary>The kind MEDIA IMPORT files a location as, and why it refuses it (null when it does not): rtsp://, srt:// and an http://
    /// HLS or DASH address are streams FFmpeg reads; the same over https is refused (this node's FFmpeg is not shown to open TLS); any
    /// other http(s) address is a page or an embed, which nothing on this node draws; anything else is a file to probe.</summary>
    private static (MediaKind Kind, string? Refused) ImportKind(string location)
    {
        if (location.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("srt://", StringComparison.OrdinalIgnoreCase))
            return (MediaKind.Stream, null);
        var https = location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!https && !location.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return (MediaKind.Video, null);
        if (IsManifest(location))
            return (MediaKind.Stream, https ? HttpsStreamWords : null);
        return location.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || location.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
            ? (MediaKind.Embed, EmbedImportWords)
            : (MediaKind.Web, PageImportWords);
    }

    /// <summary>True for an address whose path names an HLS playlist (.m3u8) or a DASH manifest (.mpd), its query and fragment aside.</summary>
    private static bool IsManifest(string location)
    {
        var end = location.AsSpan().IndexOfAny('?', '#');
        var path = end < 0 ? location.AsSpan() : location.AsSpan(0, end);
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A page or an embed: kept in an older project, never drawn on this node (no browser source, ROADMAP L10), never fetched.</summary>
    private static bool IsPage(MediaItem item) => item.Kind is MediaKind.Web or MediaKind.Embed;

    /// <summary>What this file says of an item no media edge can make playable — a page, an embed, a stream with no address (one whose
    /// address carried a pass is cleared) — the same with a media edge and without one: never decodable. Null for any other item, which
    /// the media edge answers for.</summary>
    private static MediaReadiness? NeverPlays(MediaItem item)
    {
        if (IsPage(item))
        {
            var what = item.Kind == MediaKind.Embed ? "an embed" : "a web page";
            return item.Location.Length > 0
                ? new MediaReadiness(true, false, false, false, what + " — PlayMap has no browser source for pages and embeds (ROADMAP L10)")
                : new MediaReadiness(false, false, false, false, what + " whose address carried a pass and was cleared — PlayMap has no browser source for pages and embeds (ROADMAP L10)");
        }
        return item.Kind == MediaKind.Stream && item.Location.Length == 0
            ? new MediaReadiness(false, false, false, false, "a stream with no address (one that carried a pass is cleared) — import it again by an address that carries none")
            : null;
    }

    /// <summary>True when an item's location is an address carrying a pass that PlayMap keeps nowhere: any such address but a stream's
    /// whose only pass is a user in its authority, which it plays from (a stream credential in the node's store is the open fix). A
    /// capture: location is the capture grammar's.</summary>
    private static bool ClearsLocation(MediaItem item) =>
        !Core.Media.CaptureUri.Claims(item.Location)
        && (item.Kind == MediaKind.Stream ? PrivateAddress.PassBeyondUser(item.Location) : PrivateAddress.IsWord(item.Location));

    /// <summary>True when an item holds an address that carries a pass where PlayMap keeps none: in its name, or as a location it does
    /// not play from (<see cref="ClearsLocation"/>).</summary>
    private static bool HoldsKeptPass(MediaItem item) => PrivateAddress.InText(item.Name) || ClearsLocation(item);

    /// <summary>The project with each item that holds a kept pass scrubbed — such a location cleared, each address in a name shown as its
    /// host and the marker — or the project itself when none does. Each scrubbed item's new name is added to <paramref name="named"/>.</summary>
    private static Project Scrubbed(Project p, List<string>? named)
    {
        if (!p.Media.Any(HoldsKeptPass))
            return p;
        var media = p.Media.ToBuilder();
        for (var i = 0; i < media.Count; i++)
        {
            var m = media[i];
            if (!HoldsKeptPass(m))
                continue;
            media[i] = m with
            {
                Name = PrivateAddress.ShownText(m.Name),
                Location = ClearsLocation(m) ? "" : m.Location,
            };
            named?.Add(media[i].Name);
        }
        return p with { Media = media.ToImmutable() };
    }

    /// <summary>The media list the last tick read for kept passes: the project's media is read again only when it is another list.</summary>
    private ImmutableArray<MediaItem> _passesReadIn;

    /// <summary>On the lane, each tick: a project that holds an address carrying a pass (<see cref="HoldsKeptPass"/>), whatever road it
    /// came by — opened, recovered after a restart, mirrored, made before this node refused one — is scrubbed within the tick by one
    /// unrecorded edit whose keep scrubs every undo and redo root too, so no undo brings a pass back, with one journal row that names the
    /// items as they now read and never the pass.</summary>
    private void ScrubKeptPasses()
    {
        var media = Store.Project.Media;
        if (media == _passesReadIn)
            return;
        _passesReadIn = media;
        if (!media.Any(HoldsKeptPass))
            return;
        var named = new List<string>();
        var r = Store.ApplyUnrecorded("cleared addresses that carry a pass", p => Scrubbed(p, named), (older, _) => Scrubbed(older, null));
        _passesReadIn = Store.Project.Media;
        if (!r.Changed)
            return;
        var items = named.Count == 1 ? "1 media item" : $"{named.Count} media items";
        Journal.Add("media", $"{items} held an address that carries a pass, which PlayMap keeps nowhere: {string.Join(", ", named)}"
            + " — such an address is cleared where the item kept it (a stream whose only pass is a user keeps it to play from), and one in a name"
            + " reads as its host");
    }

    /// <summary>A media item an edit (ADD, SET) makes is refused by MEDIA IMPORT's rules, with its words, so no road takes what the
    /// import refuses: a location or a name that brings an address carrying a pass in; a page or an embed made; and a location given anew,
    /// or a kind changed, that the import refuses by kind (an https HLS or DASH address, an http(s) address that is a page, whatever kind
    /// the edit files it as). An item an older project kept is still renamed and removed. A capture: location is the capture grammar's to
    /// answer (ValidateCapture), with words that repeat nothing of it.</summary>
    private static void ValidateMedia(Project p, object entity, ref string? why)
    {
        if (why is not null || entity is not MediaItem m)
            return;
        var was = p.FindMedia(m.Id);
        var located = !string.Equals(was?.Location, m.Location, StringComparison.Ordinal);
        var capture = Core.Media.CaptureUri.Claims(m.Location);
        if (!capture && located && PrivateAddress.IsWord(m.Location))
            why = PrivateAddress.IsStagewatchWindow(m.Location) ? WindowAddressWords : PassAddressWords;
        else if (PrivateAddress.InText(m.Name) && !string.Equals(was?.Name, m.Name, StringComparison.Ordinal))
            why = PassNameWords;
        else if (IsPage(m) && (was is null || !IsPage(was)))
            why = m.Kind == MediaKind.Embed ? EmbedImportWords : PageImportWords;
        else if (!capture && !IsPage(m) && (located || was?.Kind != m.Kind) && ImportKind(m.Location).Refused is { } byKind)
            why = byKind;
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
        // A live input is received and a page or an embed is drawn by nothing here: neither is a file to copy (its location is not said:
        // it may carry a password).
        if (args.Count > 0 && Store.Project.FindMedia(args[0]) is { IsFile: false } live)
            return CommandReply.Fail(IsPage(live)
                ? $"{live.Name} is {(live.Kind == MediaKind.Embed ? "an embed" : "a web page")}, not a file, and PlayMap has no browser source for it (ROADMAP L10): never fetched"
                : $"{live.Name} is a live input: it is received from the network, never fetched");
        // With the fleet part, pulls go with the peer's proof over its pinned HTTPS; VERSION fetches one optimised version (wp2-backup).
        if (FleetFetch(args) is { } fleet)
            return fleet;
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
        // The pulls journal their rows as they go and a peer can end them at once: begun behind this line's row, so the verb is said
        // first. The edge journals what it queued itself, before its pulls begin; a fetch it cannot start is refused here.
        AfterRow(() => _ = nc.FetchAsync(wanted, from, ct).ContinueWith(t => Journal.Add("refused", t.Exception?.InnerException?.Message ?? "fetch failed", ok: false),
            CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
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
        var transfers = Services.Transfers?.Transfers() ?? [];
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
            // A page, an embed and a stream with no address read the same with a media edge or without one: never ready.
            var never = NeverPlays(m);
            var ready = never ?? Services.Media?.Readiness(m);
            w.WriteBoolean("present", ready?.Present ?? !m.IsFile);
            w.WriteBoolean("ready", ready is { Present: true, Decodable: true } || (!m.IsFile && never is null));
            w.WriteString("words", ready?.Words ?? (m.IsFile ? "no media engine" : "a URL"));
            var (state, progress) = Availability(m, ready, transfers);
            w.WriteString("state", state);
            w.WriteNumber("progress", Math.Round(progress, 3));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// <summary>Where an item stands on this node, in the desk's words: fetching (with how far); missing (no file here, or
    /// a file whose bytes are not the ones the project names — a fetch brings the right one); unplayable (the right bytes,
    /// but nothing here decodes them; every page and embed, which nothing here draws; a stream with no address); ready — or unknown while it is not
    /// hashed yet, its first check runs, or the node has no media engine.</summary>
    private static (string State, double Progress) Availability(MediaItem m, MediaReadiness? ready, IReadOnlyList<MediaTransferView> transfers)
    {
        if (transfers.FirstOrDefault(t => t.MediaId == m.Id && t.Moving) is { } pulling)
            return ("fetching", pulling.Progress);
        if (NeverPlays(m) is not null)
            return ("unplayable", 0);
        if (!m.IsFile)
            return ("ready", 1);
        return ready switch
        {
            null => ("unknown", 0),
            { Present: false } => ("missing", 0),
            { Words: Host.MediaBackend.Checking } => ("unknown", 0),
            { Verified: false } when m.ContentHash.Length == 0 => ("unknown", 0),
            { Verified: false } => ("missing", 0),
            { Decodable: true } => ("ready", 1),
            _ => ("unplayable", 0),
        };
    }

    /// <summary>The library's readiness: files missing, and items the show uses that never play here (a page, an embed, a stream with
    /// no address: <see cref="NeverPlays"/>); ready only when there are neither.</summary>
    private string MediaStatusJson() => JsonOut(w =>
    {
        var p = Store.Project;
        var media = p.Media;
        var missing = media.Count(m => m.IsFile && Services.Media?.Readiness(m) is not { Present: true });
        var used = media.Any(m => NeverPlays(m) is not null) ? MediaCheck.UsedMedia(p) : null;
        var unplayable = used is null ? 0 : media.Count(m => used.Contains(m.Id) && NeverPlays(m) is not null);
        w.WriteStartObject();
        w.WriteNumber("items", media.Length);
        w.WriteNumber("missing", missing);
        w.WriteBoolean("ready", missing == 0 && unplayable == 0);
        w.WriteNumber("unplayable", unplayable);
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

/// <summary>wp2-content's hooks into the engine (EngineCore.Modules.cs): its verbs and INDEX sections, the tick that lets a finished
/// sting leave STATE and scrubs a kept pass, STATE.content, the context's content item, and a generator's inputs and a media item's
/// addresses checked on every edit.</summary>
public sealed partial class EngineCore
{
    partial void RouteContent(VerbRoutes routes) => RouteContentVerbs(routes);

    partial void TickContent(long leaderNs)
    {
        TickStings(leaderNs);
        ScrubKeptPasses();
    }

    partial void StateContent(Utf8JsonWriter w) => WriteContentState(w);

    partial void ContextContent(List<ContextItem> items, List<ContextLink> links) => AddContentContext(items);

    partial void ValidateContent(Project p, object entity, ref string? why)
    {
        ValidateGenerator(entity, ref why);
        ValidateMedia(p, entity, ref why);
    }
}
