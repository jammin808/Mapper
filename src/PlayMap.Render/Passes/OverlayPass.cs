using System.Collections.Immutable;
using PlayMap.Core.Content;
using PlayMap.Core.Gpu;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Passes;

/// <summary>The edit overlay on the wall: each pixel quad of the projector an output shows, outlined in its colour, with its corner
/// handles (the selected one larger, ringed in white) and its lattice, drawn over the output's final raster after the output stage.
/// Every point is taken through the output's own warp (corner pin and lattice) and quarter turns, as the picture is, so the lines
/// sit on the content they edit; the blend, the mask, the trim and the air's gain never touch them. Nothing is drawn while the
/// show is armed, on a black air, on an output the overlay does not name, or when it is off: then the output is byte for byte what
/// it would be with no overlay at all. Geometry is rebuilt only when the project, the overlay or the output changes; a frame that
/// reuses it allocates nothing.</summary>
public sealed class OverlayPass : IFramePass
{
    private const int Floats = 6;
    private const float Line = 2f;
    private const float Edge = 1f;
    private const float Handle = 11f;
    private const float Selected = 17f;
    private const int Segments = 32;

    private static readonly VertexAttribute[] Layout =
    [
        new(0, 2, Floats * 4, 0),
        new(1, 4, Floats * 4, 8),
    ];

    /// <summary>The overlay's state: straight-alpha triangles over the picture, no depth, no culling.</summary>
    private static readonly DrawState Shapes = new(BlendMode.Normal, DepthMode.Off, CullMode.None, Topology.Triangles);

    private sealed class Entry : IDisposable
    {
        public required GpuBuffer Buffer { get; init; }
        public required GpuMesh Mesh { get; init; }
        public required GpuBuffer CursorBuffer { get; init; }
        public required GpuMesh CursorMesh { get; init; }
        public Project? Project;
        public OverlayState? State;
        public Output? Output;
        public int Count;
        public ImmutableArray<WallCursor> Cursors;
        public int CursorCount;
        public long LastUsed;

        public void Dispose()
        {
            Mesh.Dispose();
            Buffer.Dispose();
            CursorMesh.Dispose();
            CursorBuffer.Dispose();
        }
    }

    private readonly IGpuDevice _device;
    private readonly ProgramId _shapes;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _stale = [];
    private float[] _scratch = new float[Floats * 1024];
    private int _count;
    private OverlayState? _state;
    private ImmutableArray<WallCursor> _cursors;
    private bool _armed;

    public OverlayPass(IGpuDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _shapes = device.Programs.Id("overlay.shapes");
    }

    public string Name => "overlay";

    /// <summary>Last of the passes: the overlay sits on top of everything an output shows.</summary>
    public int Order => 10_000;

    /// <summary>Builds of an output's overlay geometry so far (an unchanged overlay is built once).</summary>
    public int Builds { get; private set; }

    private const int MaxDrawnOutputs = 16;
    private readonly string?[] _drawnOutputs = new string?[MaxDrawnOutputs];
    private int _drawnCount;
    private ImmutableArray<WallCursor> _drawnCursors;

    /// <summary>The cursors the last frame drew (the board as it was then), and on which outputs: a cursor was drawn when it is in
    /// that board and its output is one of those (render thread, between frames).</summary>
    public bool Drew(WallCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var found = false;
        foreach (var c in _drawnCursors.IsDefault ? [] : _drawnCursors)
            found |= ReferenceEquals(c, cursor);
        if (!found)
            return false;
        for (var i = 0; i < _drawnCount; i++)
            if (string.Equals(_drawnOutputs[i], cursor.OutputId, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Takes the frame's overlay and whether the show is armed (render thread, before the frame).</summary>
    public void Prepare(in FrameInputs inputs)
    {
        _drawnCount = 0;
        _drawnCursors = default;
        _state = inputs.Overlay;
        _cursors = inputs.Cursors;
        _armed = inputs.Armed;
    }

    /// <summary>True when the overlay would draw on this output this frame.</summary>
    public static bool Covers(OverlayState? state, bool armed, string outputId)
    {
        if (armed || state is not { On: true } s)
            return false;
        if (s.Outputs.IsDefaultOrEmpty)
            return true;
        foreach (var id in s.Outputs)
            if (string.Equals(id, outputId, StringComparison.Ordinal))
                return true;
        return false;
    }

    public void Final(in PassFrame frame, Output output, RenderTarget final)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(final);
        if (frame.Blackout || !Covers(_state, _armed, output.Id) || output.Mode != OutputMode.Projector || output.ProjectorId is null)
            return;
        if (!_entries.TryGetValue(output.Id, out var e))
        {
            var buffer = _device.CreateBuffer(new BufferDesc(0, BufferUsage.Vertex));
            _device.SetBufferData<float>(buffer, new float[Floats * 3]);
            var cursors = _device.CreateBuffer(new BufferDesc(0, BufferUsage.Vertex));
            _device.SetBufferData<float>(cursors, new float[Floats * 3]);
            e = new Entry
            {
                Buffer = buffer,
                Mesh = _device.CreateMesh(Layout, buffer),
                CursorBuffer = cursors,
                CursorMesh = _device.CreateMesh(Layout, cursors),
            };
            _entries[output.Id] = e;
        }
        e.LastUsed = frame.Frame;
        if (!ReferenceEquals(e.Project, frame.Project) || !ReferenceEquals(e.State, _state) || !ReferenceEquals(e.Output, output))
        {
            Build(frame.Project, _state!, output, final.Width, final.Height);
            _device.SetBufferData<float>(e.Buffer, _scratch.AsSpan(0, _count * Floats));
            (e.Project, e.State, e.Output, e.Count) = (frame.Project, _state, output, _count);
            Builds++;
        }
        if ((_state!.Layers & OverlayLayers.Cursor) == 0)
        {
            // Forgotten with the layer, so the cursors are built again when it comes back on.
            (e.Cursors, e.CursorCount) = (default, 0);
        }
        else if (e.Cursors != _cursors)
        {
            BuildCursors(output.Id, final.Width, final.Height);
            if (_count > 0)
                _device.SetBufferData<float>(e.CursorBuffer, _scratch.AsSpan(0, _count * Floats));
            (e.Cursors, e.CursorCount) = (_cursors, _count);
        }
        if (e.Count == 0 && e.CursorCount == 0)
            return;
        var list = frame.List;
        using (list.Pass(final, PassDesc.Keep))
        {
            if (e.Count > 0)
                list.Draw(_shapes, Shapes).Set("uTarget", (float)final.Width, (float)final.Height).Vertices(e.Mesh, e.Count);
            if (e.CursorCount > 0)
            {
                list.Draw(_shapes, Shapes).Set("uTarget", (float)final.Width, (float)final.Height).Vertices(e.CursorMesh, e.CursorCount);
                _drawnCursors = e.Cursors;
                if (_drawnCount < MaxDrawnOutputs)
                    _drawnOutputs[_drawnCount++] = output.Id;
            }
        }
        final.Color.Version++;
    }

    /// <summary>Every cursor on this output in its session's colour, in the output's own pixels (cursors are on the wall, past the
    /// warp): a crosshair of four arms around a gap, the clicks so far as dots joined to the live point (the rubber band).</summary>
    private void BuildCursors(string outputId, int width, int height)
    {
        _count = 0;
        if (_cursors.IsDefaultOrEmpty)
            return;
        Span<Vec2d> band = stackalloc Vec2d[MaxTrail + 1];
        Span<Vec2d> arm = stackalloc Vec2d[2];
        foreach (var cursor in _cursors)
        {
            if (!string.Equals(cursor.OutputId, outputId, StringComparison.Ordinal))
                continue;
            var c = ColorRgb.Parse(cursor.Color, new ColorRgb(1, 1, 1));
            var at = new Vec2d(cursor.At.X * width, cursor.At.Y * height);
            if (!cursor.Trail.IsDefaultOrEmpty)
            {
                var n = Math.Min(cursor.Trail.Length, MaxTrail);
                for (var i = 0; i < n; i++)
                    band[i] = new Vec2d(cursor.Trail[i].X * width, cursor.Trail[i].Y * height);
                band[n] = at;
                Polyline(band[..(n + 1)], 2, c.R, c.G, c.B, 0.9f);
                for (var i = 0; i < n; i++)
                    Square(band[i], 7, c.R, c.G, c.B, 1);
            }
            for (var pass = 0; pass < 2; pass++)
            {
                var (w, r, g, b, a) = pass == 0 ? (CursorArm + 2, 0f, 0f, 0f, 0.6f) : (CursorArm, c.R, c.G, c.B, 1f);
                foreach (var (dx, dy) in Arms)
                {
                    arm[0] = new Vec2d(at.X + dx * CursorGap, at.Y + dy * CursorGap);
                    arm[1] = new Vec2d(at.X + dx * (CursorGap + CursorLength), at.Y + dy * (CursorGap + CursorLength));
                    Polyline(arm, w, r, g, b, a);
                }
            }
        }
    }

    /// <summary>The clicks so far a cursor shows (a quad's four, and a lattice's worth more).</summary>
    private const int MaxTrail = 8;

    private const float CursorArm = 3f;
    private const float CursorGap = 6f;
    private const float CursorLength = 24f;
    private static readonly (int Dx, int Dy)[] Arms = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    public void FrameEnd(in PassFrame frame)
    {
        foreach (var (id, e) in _entries)
            if (frame.Frame - e.LastUsed > 240)
                _stale.Add(id);
        foreach (var id in _stale)
        {
            _entries[id].Dispose();
            _entries.Remove(id);
        }
        _stale.Clear();
    }

    /// <summary>The overlay's triangles for one output into the scratch array (final-raster pixels and a colour per vertex).</summary>
    private void Build(Project project, OverlayState state, Output output, int width, int height)
    {
        _count = 0;
        var field = new WarpField(output.Warp);
        var quarters = (int)(((Math.Round(output.Rotation / 90.0) % 4) + 4) % 4);
        Span<Vec2d> points = stackalloc Vec2d[Segments + 1];
        foreach (var s in project.Stage.Surfaces)
        {
            if (s.Pixel is not { } quad || !s.Visible || !string.Equals(quad.ProjectorId, output.ProjectorId, StringComparison.Ordinal)
                || PixelQuadShape.Of(quad) is not { } shape)
                continue;
            var c = ColorRgb.Parse(s.Color, new ColorRgb(0.24f, 0.76f, 0.95f));
            if ((state.Layers & OverlayLayers.Outlines) != 0)
            {
                for (var edge = 0; edge < 4; edge++)
                {
                    for (var i = 0; i <= Segments; i++)
                    {
                        var t = i / (double)Segments;
                        var (u, v) = edge switch { 0 => (t, 0.0), 1 => (1.0, t), 2 => (1 - t, 1.0), _ => (0.0, 1 - t) };
                        points[i] = ToFinal(field, quarters, shape.Evaluate(u, v), width, height);
                    }
                    Polyline(points, Line + 2 * Edge, 0, 0, 0, 0.6f);
                    Polyline(points, Line, c.R, c.G, c.B, 1);
                }
            }
            if ((state.Layers & OverlayLayers.Lattice) != 0 && quad.Lattice is { } l)
            {
                for (var row = 0; row < l.Rows; row++)
                {
                    var v = row / (double)(l.Rows - 1);
                    for (var i = 0; i <= Segments; i++)
                        points[i] = ToFinal(field, quarters, shape.Evaluate(i / (double)Segments, v), width, height);
                    Polyline(points, 1, c.R, c.G, c.B, 0.7f);
                }
                for (var col = 0; col < l.Columns; col++)
                {
                    var u = col / (double)(l.Columns - 1);
                    for (var i = 0; i <= Segments; i++)
                        points[i] = ToFinal(field, quarters, shape.Evaluate(u, i / (double)Segments), width, height);
                    Polyline(points, 1, c.R, c.G, c.B, 0.7f);
                }
            }
            if ((state.Layers & OverlayLayers.Handles) != 0)
            {
                for (var k = 0; k < 4; k++)
                {
                    var selected = state.Handle is { Kind: HandleKind.Corner } h && h.SurfaceId == s.Id && h.Column == k;
                    HandleSquare(ToFinal(field, quarters, PixelQuadMath.Corner(quad, k), width, height), selected, c);
                }
                if ((state.Layers & OverlayLayers.Lattice) != 0 && quad.Lattice is { } lat)
                    for (var row = 0; row < lat.Rows; row++)
                        for (var col = 0; col < lat.Columns; col++)
                        {
                            var selected = state.Handle is { Kind: HandleKind.Node } h && h.SurfaceId == s.Id && h.Column == col && h.Row == row;
                            var at = ToFinal(field, quarters, PixelQuadMath.Node(shape, col, row), width, height);
                            if (selected)
                                HandleSquare(at, true, c);
                            else
                                Square(at, 7, c.R, c.G, c.B, 1);
                        }
            }
        }
    }

    /// <summary>A raster-normalised point on the output's final raster, in pixels: through the warp, then the quarter turns.</summary>
    public static Vec2d ToFinal(WarpField field, int quarters, Vec2d raster, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(field);
        var a = field.Evaluate(raster.X, raster.Y);
        var f = quarters switch
        {
            1 => new Vec2d(1 - a.Y, a.X),
            2 => new Vec2d(1 - a.X, 1 - a.Y),
            3 => new Vec2d(a.Y, 1 - a.X),
            _ => a,
        };
        return new Vec2d(f.X * width, f.Y * height);
    }

    private void HandleSquare(Vec2d at, bool selected, ColorRgb c)
    {
        if (selected)
        {
            Square(at, Selected + 4, 1, 1, 1, 1);
            Square(at, Selected, c.R, c.G, c.B, 1);
        }
        else
        {
            Square(at, Handle + 2, 0, 0, 0, 0.6f);
            Square(at, Handle, c.R, c.G, c.B, 1);
        }
    }

    private void Polyline(ReadOnlySpan<Vec2d> points, float width, float r, float g, float b, float a)
    {
        for (var i = 0; i + 1 < points.Length; i++)
        {
            var p0 = points[i];
            var p1 = points[i + 1];
            var d = p1 - p0;
            var len = d.Length;
            if (len < 1e-6)
                continue;
            // Each segment is lengthened by half the width at both ends, so neighbouring segments overlap at the joins.
            var dir = d * (1 / len);
            var across = -dir.Y;
            var down = dir.X;
            var n = new Vec2d(across, down) * (width / 2.0);
            var a0 = p0 - dir * (width / 2.0);
            var a1 = p1 + dir * (width / 2.0);
            Quad(a0 + n, a1 + n, a1 - n, a0 - n, r, g, b, a);
        }
    }

    private void Square(Vec2d at, float size, float r, float g, float b, float a)
    {
        var h = size / 2.0;
        Quad(new Vec2d(at.X - h, at.Y - h), new Vec2d(at.X + h, at.Y - h), new Vec2d(at.X + h, at.Y + h), new Vec2d(at.X - h, at.Y + h), r, g, b, a);
    }

    private void Quad(Vec2d p0, Vec2d p1, Vec2d p2, Vec2d p3, float r, float g, float b, float a)
    {
        Vertex(p0, r, g, b, a);
        Vertex(p1, r, g, b, a);
        Vertex(p2, r, g, b, a);
        Vertex(p0, r, g, b, a);
        Vertex(p2, r, g, b, a);
        Vertex(p3, r, g, b, a);
    }

    private void Vertex(Vec2d p, float r, float g, float b, float a)
    {
        if ((_count + 1) * Floats > _scratch.Length)
            Array.Resize(ref _scratch, _scratch.Length * 2);
        var o = _count * Floats;
        _scratch[o] = (float)p.X;
        _scratch[o + 1] = (float)p.Y;
        _scratch[o + 2] = r;
        _scratch[o + 3] = g;
        _scratch[o + 4] = b;
        _scratch[o + 5] = a;
        _count++;
    }

    public void Dispose()
    {
        foreach (var e in _entries.Values)
            e.Dispose();
        _entries.Clear();
    }
}
