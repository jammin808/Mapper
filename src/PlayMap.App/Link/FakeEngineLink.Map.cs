using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;

namespace PlayMap.App.Link;

/// <summary>The quad on the wall in the demo, with the engine's meaning, words and shapes: LIGHT WALL (an output full screen on a
/// display as a projector, the edit overlay on), SURFACE QUAD (four points in any order, wall pixels by default, through the output's
/// warp, ordered and checked as Core's <see cref="PixelQuadMath"/> does; answers its id), SURFACE CORNER … TO|BY (a burst of one
/// corner's moves is one undo step), HANDLE SELECT|NEXT|PREV, OVERLAY ON|OFF|TOGGLE|KEEP, CURSOR, and SURFACE SOURCE noting the first
/// content on a quad; STATE carries air.overlay and onboarding as the engine writes them. A verb that would change nothing is refused
/// with the reason.</summary>
public sealed partial class FakeEngineLink
{
    private static readonly string[] ProjectorColours = ["#FF8800", "#3EC1F3", "#7ED957", "#E05AD8", "#F2D03B", "#FF5A5A"];

    /// <summary>The demo machine's displays, as DISPLAYS answers them.</summary>
    private static readonly (string Key, string Name, int Width, int Height, double Hz)[] DemoDisplays =
    [
        ("DISPLAY1", "Desk monitor", 2560, 1440, 60),
        ("DISPLAY2", "Projector A", 1920, 1080, 60),
        ("DISPLAY3", "Projector B", 1920, 1080, 60),
    ];

    private bool _overlayOn;
    private List<string> _overlayOutputs = [];
    private (string Surface, int Corner)? _handle;
    private int _wallCursors;
    private long _onboardingStartMs;
    private long _litAtMs;
    private long _quadAtMs;
    private long _contentAtMs;

    partial void TryMapVerbs(WireLine line, ref WireReply? reply)
    {
        reply = (line.Verb, line.WordUpper(1)) switch
        {
            ("LIGHT", "WALL") => LightWall(line),
            ("SURFACE", "QUAD") => SurfaceQuad(line),
            ("SURFACE", "CORNER") => SurfaceCorner(line),
            ("SURFACE", "SOURCE") => SourceOnQuad(line),
            ("HANDLE", "SELECT") => HandleSelect(line),
            ("HANDLE", "NEXT") => HandleStep(+1),
            ("HANDLE", "PREV") => HandleStep(-1),
            ("OVERLAY", _) => Overlay(line),
            ("CURSOR", _) => WallCursor(line),
            _ => null,
        };
    }

    // ---- STATE ------------------------------------------------------------------------------------------------------------

    /// <summary>air.overlay as the engine writes it.</summary>
    private void WriteOverlay(Utf8JsonWriter w)
    {
        w.WriteStartObject("overlay");
        w.WriteBoolean("on", _overlayOn);
        w.WriteStartArray("outputs");
        foreach (var o in _overlayOutputs)
            w.WriteStringValue(o);
        w.WriteEndArray();
        w.WriteStartArray("layers");
        foreach (var l in (string[])["cursor", "outlines", "handles", "lattice"])
            w.WriteStringValue(l);
        w.WriteEndArray();
        if (_handle is { } h)
        {
            w.WriteStartObject("handle");
            w.WriteString("surface", h.Surface);
            w.WriteString("kind", "corner");
            w.WriteNumber("index", h.Corner);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("handle");
        }
        w.WriteBoolean("grid", false);
        w.WriteNumber("cursors", _wallCursors);
        w.WriteNumber("cursorP95Ms", -1);
        w.WriteBoolean("armed", Armed);
        w.WriteEndObject();
    }

    /// <summary>onboarding as the engine writes it: the first light, the first quad and the first content on a quad.</summary>
    private void WriteOnboarding(Utf8JsonWriter w)
    {
        if (_onboardingStartMs == 0)
            _onboardingStartMs = UnixMs;
        var stage = _contentAtMs != 0 ? "content" : _quadAtMs != 0 ? "quad" : _litAtMs != 0 ? "lit" : "none";
        var latest = Math.Max(_litAtMs, Math.Max(_quadAtMs, _contentAtMs));
        w.WriteStartObject("onboarding");
        w.WriteString("stage", stage);
        Instant(w, "startedAt", latest == 0 ? 0 : _onboardingStartMs);
        Instant(w, "litAt", _litAtMs);
        Instant(w, "quadAt", _quadAtMs);
        Instant(w, "contentAt", _contentAtMs);
        w.WriteNumber("seconds", latest == 0 ? -1 : Math.Round((latest - _onboardingStartMs) / 1000.0, 1));
        w.WriteEndObject();
    }

    private static void Instant(Utf8JsonWriter w, string name, long unixMs)
    {
        if (unixMs == 0)
            w.WriteNull(name);
        else
            w.WriteNumber(name, unixMs);
    }

    // ---- the wall's frame ---------------------------------------------------------------------------------------------------

    /// <summary>How a point on the wall maps to a projector's raster, as the engine's frame: the output that shows the projector (its
    /// raster is the wall's pixels) and its warp; with no output only raster coordinates mean anything.</summary>
    private sealed class Frame
    {
        private readonly WarpField _field;

        public Frame(Projector projector, Output? output)
        {
            Projector = projector;
            Output = output;
            Width = output?.Width ?? projector.Intrinsics.Width;
            Height = output?.Height ?? projector.Intrinsics.Height;
            _field = output is null ? WarpField.Identity : new WarpField(output.Warp);
        }

        public Projector Projector { get; }

        public Output? Output { get; }

        public int Width { get; }

        public int Height { get; }

        public static Frame Of(Project p, Projector pj)
        {
            Output? shows = null;
            foreach (var o in p.Outputs.Where(o => o.Mode == OutputMode.Projector && o.ProjectorId == pj.Id))
            {
                if (o.Enabled)
                    return new Frame(pj, o);
                shows ??= o;
            }
            return new Frame(pj, shows);
        }

        public Vec2d? ToRaster(Vec2d wall) => _field.Invert(wall.X, wall.Y);

        public Vec2d ToWall(Vec2d raster) => _field.Evaluate(raster.X, raster.Y);
    }

    private static bool Number(string word, out double value) =>
        double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>A trailing PX or RASTER taken from the words; true for RASTER.</summary>
    private static bool TakeRaster(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var w = args[i].ToUpperInvariant();
            if (w is "PX" or "RASTER")
            {
                args.RemoveAt(i);
                return w == "RASTER";
            }
        }
        return false;
    }

    private static List<string> Args(WireLine line, int from)
    {
        var list = new List<string>();
        for (var i = from; i < line.Count; i++)
            list.Add(line.Word(i));
        return list;
    }

    // ---- LIGHT WALL -----------------------------------------------------------------------------------------------------------

    private WireReply LightWall(WireLine line)
    {
        const string Syntax = "LIGHT WALL <display>|#<n>|<output> [AS <name>] [THROW <ratio>]";
        var args = Args(line, 2);
        double? throwRatio = null;
        var ti = args.FindIndex(a => a.Equals("THROW", StringComparison.OrdinalIgnoreCase));
        if (ti >= 0)
        {
            if (ti + 1 >= args.Count || !Number(args[ti + 1], out var t) || t is < 0.1 or > 20)
                return Err("THROW takes a throw ratio from 0.1 to 20 (distance ÷ picture width)");
            throwRatio = t;
            args.RemoveRange(ti, 2);
        }
        string? name = null;
        var ai = args.FindIndex(a => a.Equals("AS", StringComparison.OrdinalIgnoreCase));
        if (ai >= 0)
        {
            name = string.Join(' ', args.Skip(ai + 1)).Trim();
            if (name.Length == 0)
                return Err("AS takes a name");
            args.RemoveRange(ai, args.Count - ai);
        }
        if (args.Count != 1)
            return Err(Syntax);
        var p = _store.Project;
        var word = args[0];
        (string Key, string Name, int Width, int Height, double Hz)? display = null;
        Output? named = null;
        if (word.StartsWith('#'))
        {
            if (!int.TryParse(word.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > DemoDisplays.Length)
                return Err($"there are {DemoDisplays.Length} displays — #1 to #{DemoDisplays.Length}");
            display = DemoDisplays[n - 1];
        }
        else
        {
            foreach (var d in DemoDisplays)
            {
                if (d.Key == word || d.Name.Equals(word, StringComparison.OrdinalIgnoreCase))
                    display = d;
            }
            if (display is null)
            {
                named = p.FindOutput(word) ?? p.Outputs.FirstOrDefault(o => o.Name.Equals(word, StringComparison.OrdinalIgnoreCase));
                if (named is null)
                    return Err($"no display or output {word} — displays: " + string.Join(", ", DemoDisplays.Select((d, i) => $"#{i + 1} {d.Name} ({d.Key})")));
                foreach (var d in DemoDisplays.Where(d => d.Key == named.DisplayKey))
                    display = d;
            }
        }
        var output = named
            ?? p.Outputs.FirstOrDefault(o => display is { } d && o.DisplayKey == d.Key)
            ?? p.Outputs.FirstOrDefault(o => o.Enabled && o.DisplayKey.Length == 0 && o.Kind == OutputKind.Display && o.Mode != OutputMode.Projector);
        var projector = output?.ProjectorId is { } pid ? p.FindProjector(pid) : null;
        var (w, h, hz) = display is { } dd ? (dd.Width, dd.Height, dd.Hz) : (output!.Width, output.Height, output.RefreshHz);
        var outputId = output?.Id ?? "";
        var projectorId = projector?.Id ?? "";
        var already = output is not null && projector is not null && output.Enabled && output.Mode == OutputMode.Projector
            && (display is null || (output.Kind == OutputKind.Display && output.DisplayKey == display.Value.Key)) && output.Width == w && output.Height == h
            && (name is null || output.Name == name);
        if (already && _overlayOn && (_overlayOutputs.Count == 0 || _overlayOutputs.Contains(outputId)))
            return Err($"{output!.Name} already lights {display?.Name ?? output.DisplayKey} as {projector!.Name}, with the overlay on — SURFACE QUAD {projector.Id} … draws a quad");
        if (!already)
        {
            var r = Edit($"light the wall on {display?.Name ?? output!.Name}", project =>
            {
                var o = output is null ? null : project.FindOutput(output.Id);
                var pj = projector is null ? null : project.FindProjector(projector.Id);
                if (pj is null)
                {
                    projectorId = project.NewId(Ids.Projector);
                    var count = project.Stage.Projectors.Length;
                    pj = new Projector(projectorId, $"Projector {count + 1}", Intrinsics.FromThrowRatio(w, h, throwRatio ?? 1.5), LensDistortion.None,
                        Pose.LookAt(new Vec3d(0, 1.5, 4), new Vec3d(0, 1.5, 0), Vec3d.UnitY), 0, ProjectorColours[count % ProjectorColours.Length],
                        false, [], SolveLocks.None, 0);
                    project = project.WithStage(st => st with { Projectors = st.Projectors.Add(pj) });
                }
                if (o is null)
                {
                    outputId = project.NewId(Ids.Output);
                    var n = project.Outputs.Length + 1;
                    var x = project.Outputs.Length == 0 ? 0 : project.Outputs.Max(q => q.Plan.Right);
                    o = new Output(outputId, name ?? $"Output {n}", OutputKind.Display, display?.Key ?? "", w, h, hz, OutputMode.Projector, pj.Id,
                        SourceRef.None, new RectD(0, 0, w, h), OutputWarp.None, BlendSettings.Default, ColorTrim.Neutral, [], new RectD(x, 0, w, h), 0, true);
                    return project with { Outputs = project.Outputs.Add(o) };
                }
                var lit = o with
                {
                    Name = name ?? o.Name,
                    Kind = display is null ? o.Kind : OutputKind.Display,
                    DisplayKey = display?.Key ?? o.DisplayKey,
                    Width = w,
                    Height = h,
                    RefreshHz = hz,
                    Mode = OutputMode.Projector,
                    ProjectorId = pj.Id,
                    Enabled = true,
                };
                return project with { Outputs = project.Outputs.Update(o.Id, _ => lit) };
            });
            if (!r.Ok)
                return r;
        }
        _overlayOn = true;
        _overlayOutputs = [outputId];
        if (_litAtMs == 0)
            _litAtMs = UnixMs;
        Moved();
        var pq = _store.Project.FindProjector(projectorId)!;
        var lit2 = _store.Project.FindOutput(outputId)!;
        var raster = string.Create(CultureInfo.InvariantCulture, $"{lit2.Width}x{lit2.Height}@{lit2.RefreshHz:0.###}");
        var words = $"{lit2.Name} lights {display?.Name ?? "its display"} as {pq.Name} ({raster}); the overlay is on — click four corners on the wall";
        return Ok(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["output"] = outputId, ["projector"] = projectorId, ["display"] = display?.Key ?? lit2.DisplayKey, ["raster"] = raster,
            ["next"] = $"SURFACE QUAD {projectorId} <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> (click four corners on the wall)", ["words"] = words,
        }));
    }

    // ---- SURFACE QUAD, SURFACE CORNER -----------------------------------------------------------------------------------------

    private WireReply SurfaceQuad(WireLine line)
    {
        const string Syntax = "SURFACE QUAD <projector>|<output> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> [PX|RASTER] [AS <name>] [SOURCE <source>]";
        var args = Args(line, 2);
        var source = SourceRef.None;
        var si = args.FindIndex(a => a.Equals("SOURCE", StringComparison.OrdinalIgnoreCase));
        if (si >= 0)
        {
            if (si + 2 != args.Count || !SourceRef.TryParse(args[si + 1], out source))
                return Err("SOURCE takes one source (media:<id>, playlist:<id>, generator:<id>, canvas:<id>, color:#rrggbb)");
            args.RemoveRange(si, 2);
        }
        string? name = null;
        var ai = args.FindIndex(a => a.Equals("AS", StringComparison.OrdinalIgnoreCase));
        if (ai >= 0)
        {
            name = string.Join(' ', args.Skip(ai + 1)).Trim();
            if (name.Length == 0)
                return Err("AS takes a name");
            args.RemoveRange(ai, args.Count - ai);
        }
        var raster = TakeRaster(args);
        if (args.Count != 9)
            return Err(Syntax);
        var p = _store.Project;
        Frame frame;
        if ((p.FindProjector(args[0]) ?? p.Stage.Projectors.FirstOrDefault(x => x.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase))) is { } pj)
            frame = Frame.Of(p, pj);
        else if (p.FindOutput(args[0]) is { } o)
        {
            if (o.Mode != OutputMode.Projector || o.ProjectorId is null || p.FindProjector(o.ProjectorId) is not { } opj)
                return Err($"{o.Name} is not in Projector mode — LIGHT WALL {o.Id}, or SET {o.Id} mode projector");
            frame = new Frame(opj, o);
        }
        else
            return Err($"no projector or output {args[0]}");
        Span<Vec2d> clicks = stackalloc Vec2d[4];
        for (var i = 0; i < 4; i++)
        {
            if (!Number(args[1 + i * 2], out var x) || !Number(args[2 + i * 2], out var y))
                return Err(Syntax);
            Vec2d r;
            if (raster)
                r = new Vec2d(x / frame.Width, y / frame.Height);
            else if (frame.Output is null)
                return Err($"no output shows {frame.Projector.Name} — give RASTER pixels, or LIGHT WALL first");
            else if (frame.ToRaster(new Vec2d(x / frame.Width, y / frame.Height)) is { } rr)
                r = rr;
            else
                return Err($"that point is outside {frame.Output.Name}'s warped picture");
            clicks[i] = new Vec2d(r.X * frame.Width, r.Y * frame.Height);
        }
        var (tl, tr, br, bl) = PixelQuadMath.Order(clicks);
        Vec2d N(Vec2d px) => new(px.X / frame.Width, px.Y / frame.Height);
        var quad = new PixelQuad(frame.Projector.Id, N(tl), N(tr), N(br), N(bl), null);
        if (PixelQuadMath.Check(quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft, frame.Width, frame.Height) is { } refused)
            return Err(refused);
        var area = PixelQuadMath.AreaPx(quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft, frame.Width, frame.Height);
        var id = "";
        var label = "";
        var made = Edit($"quad on {frame.Projector.Name}", project =>
        {
            id = project.NewId(Ids.Surface);
            label = name ?? $"Quad {project.Stage.Surfaces.Count(s => s.Pixel is not null) + 1}";
            var surface = new Surface(id, label, "", Transform3.Identity, [], SurfaceContent.Empty with { Source = source }, 1, [], true, false,
                frame.Projector.Color, ["quad"]) { Pixel = quad };
            return project.WithStage(st => st with { Surfaces = st.Surfaces.Add(surface) });
        });
        if (!made.Ok)
            return made;
        if (_quadAtMs == 0)
            _quadAtMs = UnixMs;
        NoteContent();
        Moved();
        var words = string.Create(CultureInfo.InvariantCulture,
            $"{label} ({id}) on {frame.Projector.Name}: {area:N0} px²{(source.IsNone ? " — the UV checker until SURFACE SOURCE gives it a picture" : "")}");
        return Ok(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = id, ["projector"] = frame.Projector.Id, ["areaPx"] = Math.Round(area, 1), ["words"] = words,
        }));
    }

    /// <summary>A quad a verb names (by id or name), with its frame, or the words why there is none.</summary>
    private (Surface? Surface, Frame? Frame, string? Why) QuadFor(string word, bool moving)
    {
        var p = _store.Project;
        if ((p.FindSurface(word) ?? p.Stage.Surfaces.FirstOrDefault(x => x.Name.Equals(word, StringComparison.OrdinalIgnoreCase))) is not { } s)
            return (null, null, $"no surface {word}");
        if (s.Pixel is not { } q)
            return (null, null, $"{s.Name} is a 3D surface — its corners are its mesh's; SURFACE QUAD draws a quad");
        if (moving && s.Locked)
            return (null, null, $"{s.Name} is locked — unlock it to move its corners (SET {s.Id} locked false)");
        if (p.FindProjector(q.ProjectorId) is not { } pj)
            return (null, null, $"{s.Name}'s projector {q.ProjectorId} is gone");
        return (s, Frame.Of(p, pj), null);
    }

    private WireReply SurfaceCorner(WireLine line)
    {
        const string Syntax = "SURFACE CORNER <surface> TL|TR|BR|BL|0-3 TO <x> <y> [PX|RASTER] | SURFACE CORNER <surface> <corner> BY <dx> <dy> [PX|RASTER]";
        var args = Args(line, 2);
        var by = args.Count > 2 && args[2].Equals("BY", StringComparison.OrdinalIgnoreCase);
        var hasUnits = args.Any(a => a.Equals("PX", StringComparison.OrdinalIgnoreCase) || a.Equals("RASTER", StringComparison.OrdinalIgnoreCase));
        var raster = TakeRaster(args);
        // TO is wall-normalised (0–1) unless PX or RASTER says otherwise; BY is wall pixels.
        var normalised = !by && !hasUnits;
        if (args.Count != 5 || !(by || args[2].Equals("TO", StringComparison.OrdinalIgnoreCase)) || !Number(args[3], out var x) || !Number(args[4], out var y))
            return Err(Syntax);
        var corner = PixelQuadMath.CornerIndex(args[1]);
        if (corner < 0)
            return Err($"a corner is TL, TR, BR, BL or 0–3 — not {args[1]}");
        var (s, frame, why) = QuadFor(args[0], moving: true);
        if (s is null)
            return Err(why!);
        var f = frame!;
        var quad = s.Pixel!;
        var current = PixelQuadMath.Corner(quad, corner);
        Vec2d target;
        if (raster)
            target = by ? current + new Vec2d(x / f.Width, y / f.Height) : new Vec2d(x / f.Width, y / f.Height);
        else if (f.Output is null)
            return Err($"no output shows {f.Projector.Name} — give RASTER pixels, or LIGHT WALL first");
        else
        {
            var wall = by ? f.ToWall(current) + new Vec2d(x / f.Width, y / f.Height) : normalised ? new Vec2d(x, y) : new Vec2d(x / f.Width, y / f.Height);
            if (f.ToRaster(wall) is not { } r)
                return Err(by ? $"{f.Output.Name}'s warp folds there — move it in RASTER pixels" : $"that point is outside {f.Output.Name}'s warped picture");
            target = r;
        }
        var what = PixelQuadMath.CornerNames[corner];
        var movedPx = Math.Sqrt(Math.Pow((target.X - current.X) * f.Width, 2) + Math.Pow((target.Y - current.Y) * f.Height, 2));
        if (movedPx < 1e-9)
            return Err($"{s.Name} {what} is there already — that moves nothing");
        var next = PixelQuadMath.WithCorner(quad, corner, target);
        if (PixelQuadMath.Check(next.TopLeft, next.TopRight, next.BottomRight, next.BottomLeft, f.Width, f.Height) is { } refused)
            return Err(refused);
        return Edit(string.Create(CultureInfo.InvariantCulture, $"{s.Name} {what} moved {movedPx:0.0} px"),
            p => p.WithStage(st => st with { Surfaces = st.Surfaces.Update(s.Id, q => q with { Pixel = next }) }), $"pq:{s.Id}:{what}",
            string.Create(CultureInfo.InvariantCulture, $"{s.Name} {what} moved {movedPx:0.0} px"));
    }

    /// <summary>SURFACE SOURCE as the demo runs it, then the first content on a quad is noted (the onboarding's last step).</summary>
    private WireReply SourceOnQuad(WireLine line)
    {
        var r = SurfaceSource(line);
        if (r.Ok)
            NoteContent();
        return r;
    }

    /// <summary>The first visible pixel quad showing media or a playlist on a projector an enabled output shows: the first content.</summary>
    private void NoteContent()
    {
        if (_quadAtMs == 0 || _contentAtMs != 0)
            return;
        var p = _store.Project;
        var shown = p.Stage.Surfaces.Any(s => s.Pixel is { } q && s.Visible && s.Opacity > 0 && s.Content.Source.Kind is SourceKind.Media or SourceKind.Playlist
            && p.Outputs.Any(o => o.Enabled && o.Mode == OutputMode.Projector && o.ProjectorId == q.ProjectorId));
        if (!shown)
            return;
        _contentAtMs = UnixMs;
        Moved();
    }

    // ---- HANDLE ---------------------------------------------------------------------------------------------------------------

    private WireReply HandleSelect(WireLine line)
    {
        if (line.Count == 3 && line.WordUpper(2) == "NONE")
        {
            if (_handle is null)
                return Err("no handle is selected");
            _handle = null;
            Moved();
            return Ok("no handle is selected");
        }
        if (line.Count != 4)
            return Err("HANDLE SELECT <surface> TL|TR|BR|BL|<col>,<row> | HANDLE SELECT NONE");
        var (s, _, why) = QuadFor(line.Word(2), moving: false);
        if (s is null)
            return Err(why!);
        var corner = PixelQuadMath.CornerIndex(line.Word(3));
        if (corner < 0)
            return Err($"a handle is TL, TR, BR, BL or <col>,<row> — not {line.Word(3)}");
        if (_handle == (s.Id, corner))
            return Err($"{s.Name} {PixelQuadMath.CornerNames[corner]} is selected already");
        _handle = (s.Id, corner);
        Moved();
        return Ok($"{s.Name} {PixelQuadMath.CornerNames[corner]} selected");
    }

    private WireReply HandleStep(int step)
    {
        var all = _store.Project.Stage.Surfaces.Where(s => s.Pixel is not null && s.Visible).SelectMany(s => Enumerable.Range(0, 4).Select(c => (s.Id, c))).ToList();
        if (all.Count == 0)
            return Err("no quad on the wall has handles — SURFACE QUAD draws one");
        var at = _handle is { } h ? all.IndexOf((h.Surface, h.Corner)) : -1;
        var next = all[((at < 0 ? (step > 0 ? -1 : 0) : at) + step + all.Count) % all.Count];
        _handle = next;
        Moved();
        return Ok($"{next.Id} {PixelQuadMath.CornerNames[next.c]} selected");
    }

    // ---- OVERLAY, CURSOR ------------------------------------------------------------------------------------------------------

    private WireReply Overlay(WireLine line)
    {
        const string Syntax = "OVERLAY ON|OFF|TOGGLE|KEEP [ON <output>|ALL] [LAYERS <cursor,outlines,handles,lattice>|EDIT|ALL]";
        if (line.Count < 2)
            return Err(Syntax);
        var mode = line.WordUpper(1);
        List<string>? outputs = null;
        for (var i = 2; i < line.Count; i++)
        {
            if (line.WordUpper(i) == "ON" && i + 1 < line.Count)
            {
                var word = line.Word(++i);
                if (word.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                    outputs = [];
                else if (_store.Project.FindOutput(word) is { } o)
                    outputs = [o.Id];
                else
                    return Err("no output " + word);
            }
            else if (line.WordUpper(i) == "LAYERS" && i + 1 < line.Count)
                i++;
            else
                return Err(Syntax);
        }
        string Where(List<string> outs) => outs.Count == 0 ? "every output" : string.Join(", ", outs.Select(id => _store.Project.FindOutput(id)?.Name ?? id));
        switch (mode)
        {
            case "ON":
            case "TOGGLE" when !_overlayOn:
                var next = outputs ?? (_overlayOn ? _overlayOutputs : []);
                if (_overlayOn && next.SequenceEqual(_overlayOutputs))
                    return Err($"the overlay is on already ({Where(_overlayOutputs)}) — OVERLAY KEEP renews its lease");
                _overlayOn = true;
                _overlayOutputs = [.. next];
                break;
            case "OFF":
            case "TOGGLE":
                if (!_overlayOn)
                    return Err("the overlay is off already");
                if (outputs is { Count: > 0 } only && _overlayOutputs.Count > 0 && !only.SequenceEqual(_overlayOutputs))
                    return Err($"the overlay is not on {only[0]} — it is on {Where(_overlayOutputs)}");
                _overlayOn = false;
                break;
            case "KEEP":
                return _overlayOn ? Ok() : Err("the overlay is off — OVERLAY ON first");
            default:
                return Err(Syntax);
        }
        Moved();
        return Ok(_overlayOn ? $"the edit overlay is on {Where(_overlayOutputs)} (cursor, outlines, handles, lattice)" : "the edit overlay is off");
    }

    private WireReply WallCursor(WireLine line)
    {
        if (line.Count == 2 && line.WordUpper(1) == "OFF")
        {
            if (_wallCursors > 0)
            {
                _wallCursors = 0;
                Moved();
            }
            return Ok();
        }
        if (line.Count < 4 || _store.Project.FindOutput(line.Word(1)) is null)
            return Err(line.Count < 4 ? "CURSOR <output> <x> <y> [TRAIL <x> <y>…] [COLOR #rrggbb] | CURSOR OFF" : "no output " + line.Word(1));
        for (var i = 2; i < line.Count; i++)
        {
            var w = line.WordUpper(i);
            if (w == "TRAIL")
                continue;
            if (w == "COLOR")
            {
                i++;
                continue;
            }
            if (!Number(line.Word(i), out var v) || v is < 0 or > 1)
                return Err("a cursor is 0–1 across and down the output — not " + line.Word(i));
        }
        if (_wallCursors == 0)
        {
            _wallCursors = 1;
            Moved();
        }
        return Ok();
    }
}
