using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;
using PlayMap.Engine.Wall;

namespace PlayMap.Engine;

/// <summary>The quad on the wall: first light (LIGHT WALL), a quad drawn by four clicks in a projector's picture (SURFACE QUAD), its
/// corners and lattice nodes moved (SURFACE CORNER, SURFACE LATTICE, SURFACE NODE), the handle the wall shows and the arrows move
/// (HANDLE …), the edit overlay (OVERLAY), and the onboarding milestones. Numbers are wall pixels unless a verb says otherwise; a
/// quad's corners are kept raster-normalised, so a quad stays put when the output's warp or raster changes.</summary>
public sealed partial class EngineCore
{
    private static readonly string[] ProjectorColours = ["#FF8800", "#3EC1F3", "#7ED957", "#E05AD8", "#F2D03B", "#FF5A5A"];

    private long _onboardingStartUnixMs;
    private long _litAtUnixMs;
    private long _quadAtUnixMs;
    private long _contentAtUnixMs;

    /// <summary>The words a wall verb's mapping refusal carries while the show is armed — the show's own gate's, asked again here
    /// so a gate standing in for the show's (a fleet's, a test's) is obeyed by the wall's verbs too.</summary>
    private CommandReply? WallGate(VerbCall c)
    {
        var gate = Services.ShowGate;
        if (!gate.Armed)
            return null;
        var d = gate.Decide(c.Spec, c.Line, c.Session);
        return d.Verdict == ShowVerdict.Allow ? null
            : CommandReply.Fail(d.Words ?? "the show is armed — mapping is locked (SHOW DISARM, or blind-edit)");
    }

    private Func<VerbCall, CommandReply> Guarded(Func<VerbCall, CommandReply> handler) => c => WallGate(c) ?? handler(c);

    private void RouteQuads(VerbRoutes routes)
    {
        _onboardingStartUnixMs = Services.Wall.UnixMs;
        routes.Lane("LIGHT WALL", Guarded(LightWall));
        routes.Lane("SURFACE QUAD", Guarded(SurfaceQuad));
        routes.Lane("SURFACE CORNER", Guarded(SurfaceCorner));
        routes.Lane("SURFACE LATTICE", Guarded(SurfaceLattice));
        routes.Lane("SURFACE NODE", Guarded(SurfaceNode));
        routes.Lane("HANDLE SELECT", Guarded(HandleSelect));
        routes.Lane("HANDLE NEXT", Guarded(c => HandleStep(c, +1)));
        routes.Lane("HANDLE PREV", Guarded(c => HandleStep(c, -1)));
        routes.Lane("HANDLE NUDGE", Guarded(HandleNudge));
        routes.Lane("OVERLAY", Guarded(Overlay));
        // Off the lane: a cursor line goes on the board as it arrives (stamped then), never queued behind edits.
        routes.OffLane("CURSOR", c => Task.FromResult(WallGate(c) ?? Cursor(c)));
    }

    // ---- parsing ---------------------------------------------------------------------------------------------------------

    private static bool Number(string word, out double value) =>
        double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>A trailing PX or RASTER (removed from the words), else the verb's default.</summary>
    private static WallUnits TakeUnits(List<string> args, WallUnits fallback)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("PX", StringComparison.OrdinalIgnoreCase))
            {
                args.RemoveAt(i);
                return WallUnits.WallPx;
            }
            if (args[i].Equals("RASTER", StringComparison.OrdinalIgnoreCase))
            {
                args.RemoveAt(i);
                return WallUnits.RasterPx;
            }
        }
        return fallback;
    }

    private Projector? ProjectorNamed(Project p, string word) =>
        p.FindProjector(word) ?? p.Stage.Projectors.FirstOrDefault(x => x.Name.Equals(word, StringComparison.OrdinalIgnoreCase));

    private static Output? OutputNamed(Project p, string word) =>
        p.FindOutput(word) ?? p.Outputs.FirstOrDefault(x => x.Name.Equals(word, StringComparison.OrdinalIgnoreCase));

    private static Surface? SurfaceNamed(Project p, string word) =>
        p.FindSurface(word) ?? p.Stage.Surfaces.FirstOrDefault(x => x.Name.Equals(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A point in the verb's units as raster-normalised, or the words why it is not one.</summary>
    private static (Vec2d? Raster, string? Why) PointToRaster(WallFrame f, Vec2d p, WallUnits units)
    {
        if (units == WallUnits.RasterPx)
            return (new Vec2d(p.X / f.Width, p.Y / f.Height), null);
        if (f.Output is null)
            return (null, $"no output shows {f.Projector.Name} — give RASTER pixels, or LIGHT WALL first");
        var wall = units == WallUnits.WallPx ? new Vec2d(p.X / f.Width, p.Y / f.Height) : p;
        return f.ToRaster(wall) is { } r ? (r, null) : (null, $"that point is outside {f.Output.Name}'s warped picture");
    }

    /// <summary>A move in the verb's units as a raster-normalised move at raster point <paramref name="at"/>.</summary>
    private static (Vec2d? Delta, string? Why) DeltaToRaster(WallFrame f, Vec2d at, Vec2d d, WallUnits units)
    {
        if (units == WallUnits.RasterPx)
            return (new Vec2d(d.X / f.Width, d.Y / f.Height), null);
        var px = units == WallUnits.WallPx ? d : new Vec2d(d.X * f.Width, d.Y * f.Height);
        return f.WallDeltaToRaster(at, px) is { } r ? (r, null) : (null, $"{f.Where}'s warp folds there — move it in RASTER pixels");
    }

    // ---- LIGHT WALL ------------------------------------------------------------------------------------------------------

    /// <summary>LIGHT WALL &lt;display&gt;|#&lt;n&gt;|&lt;output&gt; [AS &lt;name&gt;] [THROW &lt;ratio&gt;]: an output full screen on that display as a
    /// projector (one already there, else the first free one, else a new one), its projector (the output's, else a new one with a
    /// nominal pose and the throw given or 1.5), one undoable edit; then the edit overlay on that output and its card shown for 3 s.</summary>
    private CommandReply LightWall(VerbCall c)
    {
        const string Syntax = "LIGHT WALL <display>|#<n>|<output> [AS <name>] [THROW <ratio>]";
        var args = new List<string>(c.Args);
        double? throwRatio = null;
        var ti = args.FindIndex(a => a.Equals("THROW", StringComparison.OrdinalIgnoreCase));
        if (ti >= 0)
        {
            if (ti + 1 >= args.Count || !Number(args[ti + 1], out var t) || t is < 0.1 or > 20)
                return CommandReply.Fail("THROW takes a throw ratio from 0.1 to 20 (distance ÷ picture width)");
            throwRatio = t;
            args.RemoveRange(ti, 2);
        }
        string? name = null;
        var ai = args.FindIndex(a => a.Equals("AS", StringComparison.OrdinalIgnoreCase));
        if (ai >= 0)
        {
            name = string.Join(' ', args.Skip(ai + 1)).Trim();
            if (name.Length == 0)
                return CommandReply.Fail("AS takes a name");
            args.RemoveRange(ai, args.Count - ai);
        }
        if (args.Count != 1)
            return CommandReply.Fail(Syntax);
        var p = Edits.Project;
        var displays = Services.Outputs.Displays();
        var word = args[0];
        DisplayInfo? display = null;
        Output? named = null;
        if (word.StartsWith('#'))
        {
            if (!int.TryParse(word.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > displays.Count)
                return CommandReply.Fail(displays.Count == 0 ? "this node sees no display" : $"there are {displays.Count} displays — #1 to #{displays.Count}");
            display = displays[n - 1];
        }
        else
        {
            display = displays.FirstOrDefault(d => d.Key.Equals(word, StringComparison.Ordinal))
                ?? displays.FirstOrDefault(d => d.Name.Equals(word, StringComparison.OrdinalIgnoreCase));
            if (display is null)
            {
                named = OutputNamed(p, word);
                if (named is null)
                    return CommandReply.Fail($"no display or output {word} — {(displays.Count == 0 ? "this node sees no display" : "displays: " + string.Join(", ", displays.Select((d, i) => $"#{i + 1} {d.Name} ({d.Key})")))}");
                display = displays.FirstOrDefault(d => d.Key.Equals(named.DisplayKey, StringComparison.Ordinal));
            }
        }

        var output = named
            ?? p.Outputs.FirstOrDefault(o => display is not null && o.DisplayKey.Equals(display.Key, StringComparison.Ordinal))
            ?? p.Outputs.FirstOrDefault(o => o.Enabled && o.DisplayKey.Length == 0 && o.Kind == OutputKind.Display && o.Mode != OutputMode.Projector);
        var projector = output?.ProjectorId is { } pid ? p.FindProjector(pid) : null;
        var (w, h, hz) = display is not null ? (display.Width, display.Height, display.RefreshHz) : (output!.Width, output.Height, output.RefreshHz);
        var outputId = output?.Id ?? "";
        var projectorId = projector?.Id ?? "";
        var already = output is not null && projector is not null && output.Enabled && output.Mode == OutputMode.Projector
            && (display is null || (output.Kind == OutputKind.Display && output.DisplayKey == display.Key)) && output.Width == w && output.Height == h
            && (name is null || output.Name == name);
        if (already && _air.Overlay.On && OverlayCovers(_air.Overlay, outputId))
            return CommandReply.Fail($"{output!.Name} already lights {display?.Name ?? output.DisplayKey} as {projector!.Name}, with the overlay on — SURFACE QUAD {projector.Id} … draws a quad");
        if (!already)
        {
            var r = Edits.Apply($"light the wall on {display?.Name ?? output!.Name}", project =>
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
                else if (throwRatio is { } tr)
                {
                    project = project.WithStage(st => st with { Projectors = st.Projectors.Update(pj.Id, x => x with { Intrinsics = Intrinsics.FromThrowRatio(w, h, tr) }) });
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
                // An output named with no display this node sees (a headless one) keeps its kind: only a display is made full screen.
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
            if (!r.Changed)
                return FromEdit(r);
        }
        var now = LeaderNowNs;
        var lease = Services.Mono.NowNs + Services.Settings.Wall.OverlayLeaseMs * 1_000_000L;
        SetAir(a => a with
        {
            Overlay = new OverlayState(true, [outputId], Drawn, a.Overlay.Handle, 0, c.Session.SessionId, lease),
            IdentifyUntilLeaderNs = now + 3_000_000_000,
        });
        if (_litAtUnixMs == 0)
            _litAtUnixMs = Services.Wall.UnixMs;
        FactsChanged();
        var pq = Store.Project.FindProjector(projectorId)!;
        var lit2 = Store.Project.FindOutput(outputId)!;
        var raster = string.Create(CultureInfo.InvariantCulture, $"{lit2.Width}x{lit2.Height}@{lit2.RefreshHz:0.###}");
        var words = $"{lit2.Name} lights {display?.Name ?? "its display"} as {pq.Name} ({raster}); the overlay is on — click four corners on the wall";
        return CommandReply.Success(JsonOut(w2 =>
        {
            w2.WriteStartObject();
            w2.WriteString("output", outputId);
            w2.WriteString("projector", projectorId);
            w2.WriteString("display", display?.Key ?? lit2.DisplayKey);
            w2.WriteString("raster", raster);
            w2.WriteString("next", $"SURFACE QUAD {projectorId} <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> (click four corners on the wall)");
            w2.WriteString("words", words);
            w2.WriteEndObject();
        }), words);
    }

    // ---- SURFACE QUAD ----------------------------------------------------------------------------------------------------

    /// <summary>SURFACE QUAD &lt;projector&gt;|&lt;output&gt; x1 y1 … x4 y4 [PX|RASTER] [AS &lt;name&gt;] [SOURCE &lt;source&gt;]: four points in any
    /// order (wall pixels by default) become a pixel quad of that projector, top-left first and clockwise, one undoable edit; a quad
    /// with no source shows the UV checker. Answers its id.</summary>
    private CommandReply SurfaceQuad(VerbCall c)
    {
        const string Syntax = "SURFACE QUAD <projector>|<output> <x1> <y1> <x2> <y2> <x3> <y3> <x4> <y4> [PX|RASTER] [AS <name>] [SOURCE <source>]";
        var args = new List<string>(c.Args);
        var source = SourceRef.None;
        var si = args.FindIndex(a => a.Equals("SOURCE", StringComparison.OrdinalIgnoreCase));
        if (si >= 0)
        {
            if (si + 2 != args.Count || !SourceRef.TryParse(args[si + 1], out source))
                return CommandReply.Fail("SOURCE takes one source (media:<id>, playlist:<id>, generator:<id>, canvas:<id>, color:#rrggbb)");
            args.RemoveRange(si, 2);
        }
        string? name = null;
        var ai = args.FindIndex(a => a.Equals("AS", StringComparison.OrdinalIgnoreCase));
        if (ai >= 0)
        {
            name = string.Join(' ', args.Skip(ai + 1)).Trim();
            if (name.Length == 0)
                return CommandReply.Fail("AS takes a name");
            args.RemoveRange(ai, args.Count - ai);
        }
        var units = TakeUnits(args, WallUnits.WallPx);
        if (args.Count != 9)
            return CommandReply.Fail(Syntax);
        var p = Edits.Project;
        WallFrame frame;
        if (ProjectorNamed(p, args[0]) is { } pj)
        {
            frame = WallFrame.Of(p, pj);
        }
        else if (OutputNamed(p, args[0]) is { } o)
        {
            if (o.Mode != OutputMode.Projector || o.ProjectorId is null || p.FindProjector(o.ProjectorId) is not { } opj)
                return CommandReply.Fail($"{o.Name} is not in Projector mode — LIGHT WALL {o.Id}, or SET {o.Id} mode projector");
            frame = WallFrame.Of(opj, o);
        }
        else
        {
            return CommandReply.Fail($"no projector or output {args[0]}");
        }
        if (source is { IsNone: false } && SourceProblem(p, source) is { } bad)
            return CommandReply.Fail(bad);
        Span<Vec2d> clicks = stackalloc Vec2d[4];
        for (var i = 0; i < 4; i++)
        {
            if (!Number(args[1 + i * 2], out var x) || !Number(args[2 + i * 2], out var y))
                return CommandReply.Fail(Syntax);
            var (r, why) = PointToRaster(frame, new Vec2d(x, y), units);
            if (r is not { } raster)
                return CommandReply.Fail(why!);
            clicks[i] = new Vec2d(raster.X * frame.Width, raster.Y * frame.Height);
        }
        var (tl, tr, br, bl) = PixelQuadMath.Order(clicks);
        Vec2d N(Vec2d px) => new(px.X / frame.Width, px.Y / frame.Height);
        var quad = new PixelQuad(frame.Projector.Id, N(tl), N(tr), N(br), N(bl), null);
        if (PixelQuadMath.Check(quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft, frame.Width, frame.Height) is { } refused)
            return CommandReply.Fail(refused);
        var area = PixelQuadMath.AreaPx(quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft, frame.Width, frame.Height);
        var id = "";
        var label = "";
        var r2 = Edits.Apply($"quad on {frame.Projector.Name}", project =>
        {
            id = project.NewId(Ids.Surface);
            var count = project.Stage.Surfaces.Count(s => s.Pixel is not null);
            label = name ?? $"Quad {count + 1}";
            var surface = new Surface(id, label, "", Transform3.Identity, [], SurfaceContent.Empty with { Source = source }, 1, [], true, false,
                frame.Projector.Color, ["quad"]) { Pixel = quad };
            return project.WithStage(st => st with { Surfaces = st.Surfaces.Add(surface) });
        });
        if (!r2.Changed)
            return FromEdit(r2);
        if (_quadAtUnixMs == 0)
        {
            _quadAtUnixMs = Services.Wall.UnixMs;
            FactsChanged();
        }
        var words = string.Create(CultureInfo.InvariantCulture,
            $"{label} ({id}) on {frame.Projector.Name}: {area:N0} px² from TL {frame.Px(quad.TopLeft)}{(source.IsNone ? " — the UV checker until SURFACE SOURCE gives it a picture" : "")}");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("projector", frame.Projector.Id);
            w.WriteStartObject("corners");
            for (var i = 0; i < 4; i++)
            {
                var k = PixelQuadMath.Corner(quad, i);
                w.WriteStartArray(PixelQuadMath.CornerNames[i].ToLowerInvariant());
                w.WriteNumberValue(Math.Round(k.X * frame.Width, 3));
                w.WriteNumberValue(Math.Round(k.Y * frame.Height, 3));
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteNumber("areaPx", Math.Round(area, 1));
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>Why a source cannot light a quad (it names nothing in the project), or null.</summary>
    private static string? SourceProblem(Project p, SourceRef source) => source.Kind switch
    {
        SourceKind.Media when p.FindMedia(source.Id) is null => $"no media {source.Id}",
        SourceKind.Playlist when p.FindPlaylist(source.Id) is null => $"no playlist {source.Id}",
        SourceKind.Generator when p.FindGenerator(source.Id) is null => $"no generator {source.Id}",
        SourceKind.Canvas when p.FindCanvas(source.Id) is null => $"no canvas {source.Id}",
        _ => null,
    };

    // ---- corners and nodes -----------------------------------------------------------------------------------------------

    /// <summary>The quad a verb names, its frame, or the words why there is none to move.</summary>
    private (Surface? Surface, WallFrame? Frame, string? Why) QuadFor(string word, bool moving)
    {
        var p = Edits.Project;
        if (SurfaceNamed(p, word) is not { } s)
            return (null, null, $"no surface {word}");
        if (s.Pixel is not { } q)
            return (null, null, $"{s.Name} is a 3D surface — its corners are its mesh's; SURFACE QUAD draws a quad");
        if (moving && s.Locked)
            return (null, null, $"{s.Name} is locked — unlock it to move its corners (SET {s.Id} locked false)");
        if (p.FindProjector(q.ProjectorId) is not { } pj)
            return (null, null, $"{s.Name}'s projector {q.ProjectorId} is gone");
        return (s, WallFrame.Of(p, pj), null);
    }

    /// <summary>SURFACE CORNER &lt;surface&gt; TL|TR|BR|BL|0-3 TO &lt;x&gt; &lt;y&gt; [PX|RASTER] | … BY &lt;dx&gt; &lt;dy&gt; [PX|RASTER]: TO is wall-normalised
    /// (0–1) by default, BY wall pixels. A burst of moves of one corner is one undo step.</summary>
    private CommandReply SurfaceCorner(VerbCall c)
    {
        const string Syntax = "SURFACE CORNER <surface> TL|TR|BR|BL|0-3 TO <x> <y> [PX|RASTER] | SURFACE CORNER <surface> <corner> BY <dx> <dy> [PX|RASTER]";
        var args = new List<string>(c.Args);
        var by = args.Count > 2 && args[2].Equals("BY", StringComparison.OrdinalIgnoreCase);
        var units = TakeUnits(args, by ? WallUnits.WallPx : WallUnits.WallNormalised);
        if (args.Count != 5 || !(by || args[2].Equals("TO", StringComparison.OrdinalIgnoreCase)) || !Number(args[3], out var x) || !Number(args[4], out var y))
            return CommandReply.Fail(Syntax);
        var corner = PixelQuadMath.CornerIndex(args[1]);
        if (corner < 0)
            return CommandReply.Fail($"a corner is TL, TR, BR, BL or 0–3 — not {args[1]}");
        var (s, frame, why) = QuadFor(args[0], moving: true);
        if (s is null)
            return CommandReply.Fail(why!);
        return MoveHandle(s, frame!, new OverlayHandle(s.Id, HandleKind.Corner, corner, 0), by, new Vec2d(x, y), units);
    }

    /// <summary>SURFACE NODE &lt;surface&gt; &lt;col&gt; &lt;row&gt; TO|BY &lt;x&gt; &lt;y&gt; [PX|RASTER]: a lattice node, as SURFACE CORNER moves a corner.</summary>
    private CommandReply SurfaceNode(VerbCall c)
    {
        const string Syntax = "SURFACE NODE <surface> <col> <row> TO|BY <x> <y> [PX|RASTER]";
        var args = new List<string>(c.Args);
        var by = args.Count > 3 && args[3].Equals("BY", StringComparison.OrdinalIgnoreCase);
        var units = TakeUnits(args, by ? WallUnits.WallPx : WallUnits.WallNormalised);
        if (args.Count != 6 || !(by || args[3].Equals("TO", StringComparison.OrdinalIgnoreCase))
            || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var col)
            || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var row)
            || !Number(args[4], out var x) || !Number(args[5], out var y))
            return CommandReply.Fail(Syntax);
        var (s, frame, why) = QuadFor(args[0], moving: true);
        if (s is null)
            return CommandReply.Fail(why!);
        return MoveHandle(s, frame!, new OverlayHandle(s.Id, HandleKind.Node, col, row), by, new Vec2d(x, y), units);
    }

    /// <summary>Moves a corner or a lattice node of a quad, TO a point or BY a move, refusing a move that changes nothing or a shape
    /// that crosses; one edit, coalesced per handle.</summary>
    private CommandReply MoveHandle(Surface s, WallFrame frame, OverlayHandle handle, bool by, Vec2d value, WallUnits units)
    {
        var quad = s.Pixel!;
        var shape = PixelQuadShape.Of(quad);
        if (shape is null)
            return CommandReply.Fail($"{s.Name}'s corners make no quad — move them with RASTER pixels");
        Vec2d current;
        string what;
        if (handle.Kind == HandleKind.Corner)
        {
            current = PixelQuadMath.Corner(quad, handle.Column);
            what = PixelQuadMath.CornerNames[handle.Column];
        }
        else
        {
            if (quad.Lattice is not { } l)
                return CommandReply.Fail($"{s.Name} has no lattice — SURFACE LATTICE {s.Id} 3x3 gives it one");
            if (handle.Column < 0 || handle.Column >= l.Columns || handle.Row < 0 || handle.Row >= l.Rows)
                return CommandReply.Fail($"{s.Name}'s lattice is {l.Columns}x{l.Rows} — nodes 0..{l.Columns - 1}, 0..{l.Rows - 1}");
            current = PixelQuadMath.Node(shape, handle.Column, handle.Row);
            what = string.Create(CultureInfo.InvariantCulture, $"node {handle.Column},{handle.Row}");
        }
        Vec2d target;
        if (by)
        {
            var (d, why) = DeltaToRaster(frame, current, value, units);
            if (d is not { } delta)
                return CommandReply.Fail(why!);
            target = current + delta;
        }
        else
        {
            var (r, why) = PointToRaster(frame, value, units);
            if (r is not { } raster)
                return CommandReply.Fail(why!);
            target = raster;
        }
        var movedPx = Math.Sqrt(Sq((target.X - current.X) * frame.Width) + Sq((target.Y - current.Y) * frame.Height));
        if (movedPx < 1e-9)
            return CommandReply.Fail($"{s.Name} {what} is there already — that moves nothing");
        var next = handle.Kind == HandleKind.Corner
            ? PixelQuadMath.WithCorner(quad, handle.Column, target)
            : PixelQuadMath.WithNode(quad, handle.Column, handle.Row, target);
        if (PixelQuadMath.Check(next.TopLeft, next.TopRight, next.BottomRight, next.BottomLeft, frame.Width, frame.Height) is { } refused)
            return CommandReply.Fail(refused);
        var key = handle.Kind == HandleKind.Corner ? what : string.Create(CultureInfo.InvariantCulture, $"{handle.Column},{handle.Row}");
        var r2 = Edits.Apply(string.Create(CultureInfo.InvariantCulture, $"{s.Name} {what} moved {movedPx:0.0} px"),
            p => p.WithStage(st => st with { Surfaces = st.Surfaces.Update(s.Id, x => x with { Pixel = next }) }), $"pq:{s.Id}:{key}");
        if (!r2.Changed)
            return FromEdit(r2);
        var wall = frame.Output is null ? (Vec2d?)null : frame.ToWall(target);
        var words = string.Create(CultureInfo.InvariantCulture, $"{s.Name} {what} at {frame.Px(target)} raster px (moved {movedPx:0.0} px)");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", s.Id);
            if (handle.Kind == HandleKind.Corner)
            {
                w.WriteString("corner", what);
            }
            else
            {
                w.WriteStartArray("node");
                w.WriteNumberValue(handle.Column);
                w.WriteNumberValue(handle.Row);
                w.WriteEndArray();
            }
            w.WriteStartArray("raster");
            w.WriteNumberValue(Math.Round(target.X * frame.Width, 3));
            w.WriteNumberValue(Math.Round(target.Y * frame.Height, 3));
            w.WriteEndArray();
            if (wall is { } wl)
            {
                w.WriteStartArray("wall");
                w.WriteNumberValue(Math.Round(wl.X * frame.Width, 3));
                w.WriteNumberValue(Math.Round(wl.Y * frame.Height, 3));
                w.WriteEndArray();
            }
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    private static double Sq(double v) => v * v;

    /// <summary>SURFACE LATTICE &lt;surface&gt; &lt;c&gt;x&lt;r&gt;|OFF [LINEAR|SMOOTH|BEZIER]: a lattice on top of the quad's corners, its shape kept
    /// (a resize samples the old offsets at the new nodes, and the reply says how far the shape moved).</summary>
    private CommandReply SurfaceLattice(VerbCall c)
    {
        const string Syntax = "SURFACE LATTICE <surface> <c>x<r>|OFF [LINEAR|SMOOTH|BEZIER]";
        if (c.Args.Count is < 2 or > 3)
            return CommandReply.Fail(Syntax);
        var (s, frame, why) = QuadFor(c.Args[0], moving: true);
        if (s is null)
            return CommandReply.Fail(why!);
        var quad = s.Pixel!;
        int cols = 0, rows = 0;
        if (!c.Args[1].Equals("OFF", StringComparison.OrdinalIgnoreCase))
        {
            var parts = c.Args[1].Split('x', 'X');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out cols)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rows)
                || cols < WarpLattice.MinSize || rows < WarpLattice.MinSize || cols > WarpLattice.MaxSize || rows > WarpLattice.MaxSize)
                return CommandReply.Fail($"a lattice is <c>x<r>, each {WarpLattice.MinSize}–{WarpLattice.MaxSize} (3x3), or OFF");
        }
        var interpolation = quad.Lattice?.Interpolation ?? WarpInterpolation.CatmullRom;
        if (c.Args.Count == 3)
        {
            interpolation = c.Args[2].ToUpperInvariant() switch
            {
                "LINEAR" => WarpInterpolation.Linear,
                "SMOOTH" => WarpInterpolation.CatmullRom,
                "BEZIER" => WarpInterpolation.Bezier,
                _ => throw new EditRefusedException($"a lattice is LINEAR, SMOOTH or BEZIER — not {c.Args[2]}"),
            };
        }
        if (cols == 0 && quad.Lattice is null)
            return CommandReply.Fail($"{s.Name} has no lattice — nothing to take off");
        if (quad.Lattice is { } l && l.Columns == cols && l.Rows == rows && l.Interpolation == interpolation)
            return CommandReply.Fail($"{s.Name}'s lattice is {cols}x{rows} {Interp(interpolation)} already");
        var (next, change) = PixelQuadMath.WithLattice(quad, cols, rows, interpolation, frame!.Width, frame.Height);
        if (next.Lattice is { } same && quad.Lattice is { } was && same.Columns == was.Columns && same.Rows == was.Rows && cols > 0)
            next = next with { Lattice = same with { Offsets = was.Offsets, HandlesU = was.HandlesU, HandlesV = was.HandlesV } };
        var size = cols == 0 ? "off" : string.Create(CultureInfo.InvariantCulture, $"{cols}x{rows}");
        var r = Edits.Apply($"{s.Name} lattice {size}", p => p.WithStage(st => st with { Surfaces = st.Surfaces.Update(s.Id, x => x with { Pixel = next }) }));
        if (!r.Changed)
            return FromEdit(r);
        var words = string.Create(CultureInfo.InvariantCulture, $"{s.Name}'s lattice {size}{(cols == 0 ? "" : " " + Interp(interpolation))}; its shape moved {change:0.000} px");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", s.Id);
            w.WriteString("lattice", size);
            w.WriteNumber("shapeChangePx", Math.Round(change, 3));
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    private static string Interp(WarpInterpolation i) => i switch
    {
        WarpInterpolation.Linear => "linear",
        WarpInterpolation.Bezier => "bezier",
        _ => "smooth",
    };

    // ---- handles ---------------------------------------------------------------------------------------------------------

    private static string HandleWords(Project p, OverlayHandle h)
    {
        var name = p.FindSurface(h.SurfaceId)?.Name ?? h.SurfaceId;
        return h.Kind == HandleKind.Corner
            ? $"{name} {PixelQuadMath.CornerNames[Math.Clamp(h.Column, 0, 3)]}"
            : string.Create(CultureInfo.InvariantCulture, $"{name} node {h.Column},{h.Row}");
    }

    private CommandReply HandleReply(OverlayHandle? h)
    {
        var words = h is { } handle ? "the wall shows " + HandleWords(Store.Project, handle) : "no handle selected";
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("handle");
            WriteHandle(w, h);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    private static void WriteHandle(Utf8JsonWriter w, OverlayHandle? h)
    {
        if (h is not { } handle)
        {
            w.WriteNullValue();
            return;
        }
        w.WriteStartObject();
        w.WriteString("surface", handle.SurfaceId);
        w.WriteString("kind", handle.Kind == HandleKind.Corner ? "corner" : "node");
        if (handle.Kind == HandleKind.Corner)
        {
            w.WriteNumber("index", handle.Column);
        }
        else
        {
            w.WriteNumber("col", handle.Column);
            w.WriteNumber("row", handle.Row);
        }
        w.WriteEndObject();
    }

    /// <summary>HANDLE SELECT &lt;surface&gt; TL|TR|BR|BL|&lt;col&gt;,&lt;row&gt; | HANDLE SELECT NONE: the handle the wall shows enlarged and the
    /// arrows move (the air's, not the document's: no undo, no journal).</summary>
    private CommandReply HandleSelect(VerbCall c)
    {
        OverlayHandle? next;
        if (c.Args.Count == 1 && c.Args[0].Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            if (_air.Overlay.Handle is null)
                return CommandReply.Fail("no handle is selected");
            next = null;
        }
        else
        {
            if (c.Args.Count != 2)
                return CommandReply.Fail("HANDLE SELECT <surface> TL|TR|BR|BL|<col>,<row> | HANDLE SELECT NONE");
            var (s, _, why) = QuadFor(c.Args[0], moving: false);
            if (s is null)
                return CommandReply.Fail(why!);
            var corner = PixelQuadMath.CornerIndex(c.Args[1]);
            if (corner >= 0)
            {
                next = new OverlayHandle(s.Id, HandleKind.Corner, corner, 0);
            }
            else
            {
                var parts = c.Args[1].Split(',');
                if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var col)
                    || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var row))
                    return CommandReply.Fail($"a handle is TL, TR, BR, BL or <col>,<row> — not {c.Args[1]}");
                if (s.Pixel!.Lattice is not { } l || col < 0 || row < 0 || col >= l.Columns || row >= l.Rows)
                    return CommandReply.Fail(s.Pixel.Lattice is { } l2 ? $"{s.Name}'s lattice is {l2.Columns}x{l2.Rows}" : $"{s.Name} has no lattice");
                next = new OverlayHandle(s.Id, HandleKind.Node, col, row);
            }
            if (_air.Overlay.Handle == next)
                return CommandReply.Fail(HandleWords(Edits.Project, next.Value) + " is selected already");
        }
        SetAir(a => a with { Overlay = a.Overlay with { Handle = next } });
        return HandleReply(next);
    }

    /// <summary>Every handle on the wall in order: each pixel quad's corners (TL, TR, BR, BL), then its lattice nodes row by row.</summary>
    private static List<OverlayHandle> AllHandles(Project p)
    {
        var list = new List<OverlayHandle>();
        foreach (var s in p.Stage.Surfaces)
        {
            if (s.Pixel is not { } q || !s.Visible)
                continue;
            for (var i = 0; i < 4; i++)
                list.Add(new OverlayHandle(s.Id, HandleKind.Corner, i, 0));
            if (q.Lattice is { } l)
                for (var row = 0; row < l.Rows; row++)
                    for (var col = 0; col < l.Columns; col++)
                        list.Add(new OverlayHandle(s.Id, HandleKind.Node, col, row));
        }
        return list;
    }

    /// <summary>HANDLE NEXT / HANDLE PREV: the next (or previous) handle on the wall, wrapping across quads.</summary>
    private CommandReply HandleStep(VerbCall c, int step)
    {
        if (c.Args.Count != 0)
            return CommandReply.Fail(step > 0 ? "HANDLE NEXT" : "HANDLE PREV");
        var all = AllHandles(Edits.Project);
        if (all.Count == 0)
            return CommandReply.Fail("no quad on the wall has handles — SURFACE QUAD draws one");
        var at = _air.Overlay.Handle is { } h ? all.IndexOf(h) : -1;
        var next = at < 0 ? (step > 0 ? all[0] : all[^1]) : all[((at + step) % all.Count + all.Count) % all.Count];
        if (_air.Overlay.Handle == next)
            return CommandReply.Fail("there is one handle — it is selected");
        SetAir(a => a with { Overlay = a.Overlay with { Handle = next } });
        return HandleReply(next);
    }

    /// <summary>HANDLE NUDGE &lt;dx&gt; &lt;dy&gt; [PX|RASTER]: the selected handle moved (wall pixels by default), as SURFACE CORNER … BY.</summary>
    private CommandReply HandleNudge(VerbCall c)
    {
        var args = new List<string>(c.Args);
        var units = TakeUnits(args, WallUnits.WallPx);
        if (args.Count != 2 || !Number(args[0], out var dx) || !Number(args[1], out var dy))
            return CommandReply.Fail("HANDLE NUDGE <dx> <dy> [PX|RASTER]");
        if (_air.Overlay.Handle is not { } h)
            return CommandReply.Fail("no handle is selected — HANDLE SELECT or HANDLE NEXT first");
        var (s, frame, why) = QuadFor(h.SurfaceId, moving: true);
        if (s is null)
            return CommandReply.Fail(why!);
        return MoveHandle(s, frame!, h, by: true, new Vec2d(dx, dy), units);
    }

    // ---- the overlay -----------------------------------------------------------------------------------------------------

    private static bool OverlayCovers(OverlayState o, string outputId) =>
        o.On && (o.Outputs.IsDefaultOrEmpty || o.Outputs.Contains(outputId, StringComparer.Ordinal));

    /// <summary>The layers the overlay draws on this node, by their words.</summary>
    private static readonly (string Word, OverlayLayers Layer)[] LayerWords =
    [
        ("cursor", OverlayLayers.Cursor), ("outlines", OverlayLayers.Outlines), ("handles", OverlayLayers.Handles), ("lattice", OverlayLayers.Lattice),
    ];

    /// <summary>Every layer this node draws: EDIT and ALL.</summary>
    private const OverlayLayers Drawn = OverlayLayers.Cursor | OverlayLayers.Outlines | OverlayLayers.Handles | OverlayLayers.Lattice;

    private static IEnumerable<string> LayerNames(OverlayLayers layers) =>
        LayerWords.Where(x => (layers & x.Layer) != 0).Select(x => x.Word);

    /// <summary>OVERLAY ON|OFF|TOGGLE|KEEP [ON &lt;output&gt;|ALL] [LAYERS &lt;a,b,…&gt;|EDIT|ALL]: the edit overlay on the wall, drawn through
    /// each output's warp and never through its blend. ON and KEEP hold a lease the asking session renews (it lapses after the
    /// node's OverlayLeaseMs); the overlay is never drawn while the show is armed, and an armed show turns it off.</summary>
    private CommandReply Overlay(VerbCall c)
    {
        const string Syntax = "OVERLAY ON|OFF|TOGGLE|KEEP [ON <output>|ALL] [LAYERS <cursor,outlines,handles,lattice>|EDIT|ALL]";
        if (c.Args.Count == 0)
            return CommandReply.Fail(Syntax);
        var mode = c.Args[0].ToUpperInvariant();
        var rest = c.Args.Skip(1).ToList();
        var cur = _air.Overlay;
        ImmutableArray<string>? outputs = null;
        OverlayLayers? layers = null;
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i].Equals("ON", StringComparison.OrdinalIgnoreCase) && i + 1 < rest.Count)
            {
                var word = rest[++i];
                if (word.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                {
                    outputs = [];
                }
                else
                {
                    if (OutputNamed(Store.Project, word) is not { } o)
                        return CommandReply.Fail($"no output {word}");
                    outputs = [o.Id];
                }
            }
            else if (rest[i].Equals("LAYERS", StringComparison.OrdinalIgnoreCase) && i + 1 < rest.Count)
            {
                var word = rest[++i];
                if (word.Equals("EDIT", StringComparison.OrdinalIgnoreCase) || word.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                {
                    layers = Drawn;
                }
                else
                {
                    var set = OverlayLayers.None;
                    foreach (var part in word.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var hit = LayerWords.FirstOrDefault(x => x.Word.Equals(part, StringComparison.OrdinalIgnoreCase));
                        if (hit.Word is null)
                            return CommandReply.Fail(part.ToLowerInvariant() is "names" or "pins"
                                ? $"the overlay draws no {part.ToLowerInvariant()} on this node yet — cursor, outlines, handles, lattice"
                                : $"a layer is cursor, outlines, handles or lattice — not {part}");
                        set |= hit.Layer;
                    }
                    layers = set;
                }
            }
            else
            {
                return CommandReply.Fail(Syntax);
            }
        }
        var leaseNs = Services.Mono.NowNs + Services.Settings.Wall.OverlayLeaseMs * 1_000_000L;
        OverlayState next;
        switch (mode)
        {
            case "ON":
            case "TOGGLE" when !cur.On:
                next = new OverlayState(true, outputs ?? (cur.On ? cur.Outputs : []), layers ?? cur.Layers, cur.Handle, cur.GridUntilLeaderNs,
                    c.Session.SessionId, leaseNs);
                if (cur.On && next.Outputs.SequenceEqual(cur.Outputs) && next.Layers == cur.Layers)
                    return CommandReply.Fail($"the overlay is on already ({OverlayWhere(cur)}) — OVERLAY KEEP renews its lease");
                break;
            case "OFF":
            case "TOGGLE":
                if (!cur.On)
                    return CommandReply.Fail("the overlay is off already");
                if (outputs is { IsEmpty: false } only && !cur.Outputs.IsDefaultOrEmpty && !only.SequenceEqual(cur.Outputs))
                    return CommandReply.Fail($"the overlay is not on {only[0]} — it is on {OverlayWhere(cur)}");
                next = cur with { On = false, LeaseUntilMonoNs = 0 };
                break;
            case "KEEP":
                if (!cur.On)
                    return CommandReply.Fail("the overlay is off — OVERLAY ON first");
                SetAir(a => a with { Overlay = a.Overlay with { LeaseUntilMonoNs = leaseNs, Session = c.Session.SessionId } });
                return new CommandReply(true, null, "", Silent: true);
            default:
                return CommandReply.Fail(Syntax);
        }
        SetAir(a => a with { Overlay = next });
        var words = next.On ? $"the edit overlay is on {OverlayWhere(next)} ({string.Join(", ", LayerNames(next.Layers))})" : "the edit overlay is off";
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("on", next.On);
            w.WriteStartArray("outputs");
            foreach (var o in next.Outputs)
                w.WriteStringValue(o);
            w.WriteEndArray();
            w.WriteStartArray("layers");
            foreach (var l in LayerNames(next.Layers))
                w.WriteStringValue(l);
            w.WriteEndArray();
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    private string OverlayWhere(OverlayState o) =>
        o.Outputs.IsDefaultOrEmpty ? "every output" : string.Join(", ", o.Outputs.Select(id => Store.Project.FindOutput(id)?.Name ?? id));

    private readonly CursorBoard _cursors = new();

    /// <summary>Every session's cursor on the wall now (any thread; the render host draws them).</summary>
    internal System.Collections.Immutable.ImmutableArray<WallCursor> WallCursors => _cursors.Cursors;

    /// <summary>The cursors with when each line arrived (the render host measures how late each is drawn).</summary>
    internal CursorBoard.Board WallCursorBoard => _cursors.Current;

    private double _cursorP95Ms = -1;

    /// <summary>The render host's measure of how late a cursor is drawn: the p95 over the last moves, in ms (frames × the output's
    /// frame period); STATE moves with it (render thread → the facts signal).</summary>
    internal double CursorP95Ms
    {
        get => Volatile.Read(ref _cursorP95Ms);
        set => Volatile.Write(ref _cursorP95Ms, value);
    }

    /// <summary>CURSOR &lt;output&gt; &lt;x&gt; &lt;y&gt; [TRAIL &lt;x&gt; &lt;y&gt;…] [COLOR #rrggbb] | CURSOR OFF: this session's cursor on the wall, 0–1
    /// across and down the output, with the clicks so far; its lease lapses after the node's CursorLeaseMs unless sent again. No
    /// journal, no undo, no answer but OK; STATE moves only when the number of cursors does.</summary>
    private CommandReply Cursor(VerbCall c)
    {
        // The render clock's instant the line arrived: the frame that first draws the move counts from it.
        var arrived = Core.Time.MonoClock.Instance.NowNs;
        const string Syntax = "CURSOR <output> <x> <y> [TRAIL <x> <y>…] [COLOR #rrggbb] | CURSOR OFF";
        if (c.Args.Count == 1 && c.Args[0].Equals("OFF", StringComparison.OrdinalIgnoreCase))
        {
            if (!_cursors.Remove(c.Session.SessionId))
                return CommandReply.Fail("this session has no cursor on the wall");
            FactsChanged();
            return CommandReply.NoReply();
        }
        var args = new List<string>(c.Args);
        var color = "#FFFFFF";
        var ci = args.FindIndex(a => a.Equals("COLOR", StringComparison.OrdinalIgnoreCase));
        if (ci >= 0)
        {
            if (ci + 1 >= args.Count || args[ci + 1].Length != 7 || args[ci + 1][0] != '#'
                || !int.TryParse(args[ci + 1].AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                return CommandReply.Fail("COLOR takes #rrggbb");
            color = args[ci + 1].ToUpperInvariant();
            args.RemoveRange(ci, 2);
        }
        if (args.Count < 3 || OutputNamed(Store.Project, args[0]) is not { } output)
            return CommandReply.Fail(args.Count < 3 ? Syntax : $"no output {args[0]}");
        static bool Wall(string word, out double v) => Number(word, out v) && v is >= -0.5 and <= 1.5;
        if (!Wall(args[1], out var x) || !Wall(args[2], out var y))
            return CommandReply.Fail("x and y are 0–1 across and down the output");
        var trail = System.Collections.Immutable.ImmutableArray.CreateBuilder<Vec2d>();
        if (args.Count > 3)
        {
            if (!args[3].Equals("TRAIL", StringComparison.OrdinalIgnoreCase) || (args.Count - 4) % 2 != 0 || args.Count - 4 > 16)
                return CommandReply.Fail(Syntax + " (a trail is up to 8 points)");
            for (var i = 4; i < args.Count; i += 2)
            {
                if (!Wall(args[i], out var tx) || !Wall(args[i + 1], out var ty))
                    return CommandReply.Fail("a trail point is 0–1 across and down the output");
                trail.Add(new Vec2d(tx, ty));
            }
        }
        var now = Services.Mono.NowNs;
        var cursor = new WallCursor(c.Session.SessionId, c.Session.ClientName, output.Id, new Vec2d(x, y), trail.ToImmutable(), color, now,
            now + Services.Settings.Wall.CursorLeaseMs * 1_000_000L);
        if (_cursors.Put(cursor, Services.Settings.Wall.MaxCursors, arrived))
            FactsChanged();
        return CommandReply.NoReply();
    }

    /// <summary>The lane's tick for the wall: an overlay whose lease lapsed, or any overlay while the show is armed, goes off; the
    /// first content on a quad is noted.</summary>
    private void TickQuads()
    {
        var o = _air.Overlay;
        if (o.On && (Services.ShowGate.Armed || (o.LeaseUntilMonoNs > 0 && Services.Mono.NowNs >= o.LeaseUntilMonoNs)))
            SetAir(a => a with { Overlay = a.Overlay with { On = false, LeaseUntilMonoNs = 0 } });
        if (_cursors.Prune(Services.Mono.NowNs))
            FactsChanged();
        if (_quadAtUnixMs != 0 && _contentAtUnixMs == 0 && (_renderWitness ? _contentWitnessed : QuadContent(Store.Project) is not null))
        {
            _contentAtUnixMs = Services.Wall.UnixMs;
            FactsChanged();
        }
    }

    private volatile bool _renderWitness;
    private volatile bool _contentWitnessed;

    /// <summary>A quad is drawn and no content has been seen on one yet (any thread: the render host asks each frame).</summary>
    internal bool AwaitingFirstContent => Volatile.Read(ref _quadAtUnixMs) != 0 && Volatile.Read(ref _contentAtUnixMs) == 0;

    /// <summary>The render host draws this node's frames: the first content is stamped when it says a frame drew some, not when the
    /// project first names it (render thread; once).</summary>
    internal void RenderWitnessesContent() => _renderWitness = true;

    /// <summary>The render host drew a frame of a quad's content (render thread); the lane stamps it on its next tick.</summary>
    internal void FirstContentDrawn() => _contentWitnessed = true;

    /// <summary>What the first visible pixel quad showing media or a playlist on a projector an enabled output puts on the wall shows,
    /// or null.</summary>
    internal static SourceRef? QuadContent(Project p)
    {
        foreach (var s in p.Stage.Surfaces)
        {
            if (s.Pixel is not { } q || !s.Visible || s.Opacity <= 0 || s.Content.Source.Kind is not (SourceKind.Media or SourceKind.Playlist))
                continue;
            foreach (var o in p.Outputs)
                if (o.Enabled && o.Mode == OutputMode.Projector && string.Equals(o.ProjectorId, q.ProjectorId, StringComparison.Ordinal))
                    return s.Content.Source;
        }
        return null;
    }

    /// <summary>air.overlay: on, the outputs and layers, the selected handle, the grid flash and the lease left.</summary>
    private void WriteOverlay(Utf8JsonWriter w)
    {
        var o = _snapshot.Air.Overlay;
        w.WriteStartObject("overlay");
        w.WriteBoolean("on", o.On);
        w.WriteStartArray("outputs");
        foreach (var id in o.Outputs.IsDefault ? [] : o.Outputs)
            w.WriteStringValue(id);
        w.WriteEndArray();
        w.WriteStartArray("layers");
        foreach (var l in LayerNames(o.Layers))
            w.WriteStringValue(l);
        w.WriteEndArray();
        w.WritePropertyName("handle");
        WriteHandle(w, o.Handle);
        w.WriteBoolean("grid", false);
        w.WriteNumber("cursors", _cursors.Cursors.Length);
        var p95 = CursorP95Ms;
        w.WriteNumber("cursorP95Ms", p95 < 0 ? -1 : Math.Round(p95, 1));
        w.WriteBoolean("armed", Services.ShowGate.Armed);
        w.WriteEndObject();
    }

    /// <summary>onboarding: the first light, the first quad and the first content on a quad, as wall-clock instants, and the seconds
    /// from the node's start to the latest of them; before the first light every instant is null and the seconds −1.</summary>
    private void WriteOnboarding(Utf8JsonWriter w)
    {
        var stage = _contentAtUnixMs != 0 ? "content" : _quadAtUnixMs != 0 ? "quad" : _litAtUnixMs != 0 ? "lit" : "none";
        var latest = Math.Max(_litAtUnixMs, Math.Max(_quadAtUnixMs, _contentAtUnixMs));
        w.WriteStartObject("onboarding");
        w.WriteString("stage", stage);
        WriteInstant(w, "startedAt", latest == 0 ? 0 : _onboardingStartUnixMs);
        WriteInstant(w, "litAt", _litAtUnixMs);
        WriteInstant(w, "quadAt", _quadAtUnixMs);
        WriteInstant(w, "contentAt", _contentAtUnixMs);
        w.WriteNumber("seconds", latest == 0 ? -1 : Math.Round((latest - _onboardingStartUnixMs) / 1000.0, 1));
        w.WriteEndObject();
    }

    private static void WriteInstant(Utf8JsonWriter w, string name, long unixMs)
    {
        if (unixMs == 0)
            w.WriteNull(name);
        else
            w.WriteNumber(name, unixMs);
    }

    /// <summary>A pixel quad's own check for a generic SET or ADD: its projector exists and its corners make a convex quad.</summary>
    private static string? ValidateQuad(Project p, Surface s)
    {
        if (s.Pixel is not { } q)
            return null;
        if (p.FindProjector(q.ProjectorId) is not { } pj)
            return $"{s.Name}'s pixel quad names no projector of this project ({q.ProjectorId})";
        if (PixelQuadShape.Of(q) is null || !QuadHomography.IsConvex(q.TopLeft, q.TopRight, q.BottomRight, q.BottomLeft))
            return $"{s.Name}'s corners cross or fold — a quad on {pj.Name} must be convex";
        if (q.Lattice is { } l && (l.Columns is < WarpLattice.MinSize or > WarpLattice.MaxSize || l.Rows is < WarpLattice.MinSize or > WarpLattice.MaxSize))
            return $"{s.Name}'s lattice is {l.Columns}x{l.Rows} — each side {WarpLattice.MinSize}–{WarpLattice.MaxSize}";
        if (q.Lattice is { } m && m.Offsets.Length != m.Columns * m.Rows)
            return $"{s.Name}'s lattice has {m.Offsets.Length} offsets for {m.Columns}x{m.Rows} nodes";
        return null;
    }
}
