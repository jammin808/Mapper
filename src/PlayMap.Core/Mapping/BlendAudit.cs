using System.Globalization;
using PlayMap.Core.Model;

namespace PlayMap.Core.Mapping;

/// <summary>A word about one output's blend: a join that is right, or what is wrong with it.</summary>
public readonly record struct BlendNote(bool Warning, string Text);

/// <summary>The edges of a raster, as the audit names them.</summary>
public enum BlendEdge
{
    None,
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>Audits the hand-set (2D) soft edges of outputs laid out on the plan, join by join: a join is
/// right when both sides fade it, over the same width, with the same curve and gamma — two fades that
/// differ by a pixel or a curve show as a fine line — and an output's zones must leave a picture between
/// them. Only enabled outputs blended by hand take part (an automatic 3D blend is the solver's). Overlaps
/// are read from <see cref="Output.Plan"/> (plan pixels) and compared in each output's own pixels.</summary>
public static class BlendAudit
{
    /// <summary>Widths agree when they differ by less than this many output pixels.</summary>
    public const double WidthTolerancePx = 0.5;

    /// <summary>The edge of <paramref name="me"/> a neighbour's overlap lies along, and the overlap's depth
    /// across that edge (plan pixels): a strip taller than wide lies along a side, wider than tall along the
    /// top or bottom; a square overlap is a pure corner, which the two edges' own zones already cover as
    /// their product, and the same place twice is no join.</summary>
    public static BlendEdge EdgeOf(RectD me, RectD other, out double zone)
    {
        zone = 0;
        var w = Math.Min(me.Right, other.Right) - Math.Max(me.X, other.X);
        var h = Math.Min(me.Bottom, other.Bottom) - Math.Max(me.Y, other.Y);
        if (w <= 0 || h <= 0 || (w >= me.W && h >= me.H))
            return BlendEdge.None;
        if (h > w)
        {
            zone = w;
            return other.X < me.X ? BlendEdge.Left : BlendEdge.Right;
        }
        if (w > h)
        {
            zone = h;
            return other.Y < me.Y ? BlendEdge.Top : BlendEdge.Bottom;
        }
        return BlendEdge.None;
    }

    /// <summary>The edge a neighbour fades to meet mine.</summary>
    public static BlendEdge Facing(BlendEdge edge) => edge switch
    {
        BlendEdge.Left => BlendEdge.Right,
        BlendEdge.Right => BlendEdge.Left,
        BlendEdge.Top => BlendEdge.Bottom,
        BlendEdge.Bottom => BlendEdge.Top,
        _ => BlendEdge.None,
    };

    /// <summary>An output's zone on an edge, in its own pixels.</summary>
    public static double ZoneOn(EdgeBlend edges, BlendEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edges);
        return edge switch
        {
            BlendEdge.Left => edges.Left,
            BlendEdge.Top => edges.Top,
            BlendEdge.Right => edges.Right,
            BlendEdge.Bottom => edges.Bottom,
            _ => 0,
        };
    }

    /// <summary>The soft edges the plan's overlaps call for (output pixels): each neighbour's overlap on
    /// the edge it lies along, the widest when two share an edge — what "match the plan" sets.</summary>
    public static EdgeBlend Derive(Output me, IEnumerable<Output> others)
    {
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(others);
        double l = 0, t = 0, r = 0, b = 0;
        foreach (var o in others)
        {
            if (o.Id == me.Id || !o.Enabled)
                continue;
            var edge = EdgeOf(me.Plan, o.Plan, out var zone);
            var px = ToOutputPixels(me, edge, zone);
            switch (edge)
            {
                case BlendEdge.Left: l = Math.Max(l, px); break;
                case BlendEdge.Right: r = Math.Max(r, px); break;
                case BlendEdge.Top: t = Math.Max(t, px); break;
                case BlendEdge.Bottom: b = Math.Max(b, px); break;
                default: break;
            }
        }
        return new EdgeBlend(l, t, r, b);
    }

    private static double ToOutputPixels(Output o, BlendEdge edge, double planPx) => edge switch
    {
        BlendEdge.Left or BlendEdge.Right => o.Plan.W > 0 ? planPx * o.Width / o.Plan.W : planPx,
        BlendEdge.Top or BlendEdge.Bottom => o.Plan.H > 0 ? planPx * o.Height / o.Plan.H : planPx,
        _ => 0,
    };

    /// <summary>The notes for one output against every other on the plan.</summary>
    public static IReadOnlyList<BlendNote> For(Output me, IReadOnlyList<Output> outputs)
    {
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(outputs);
        var notes = new List<BlendNote>();
        if (!me.Enabled || me.Blend.Auto3D)
            return notes;
        var mine = me.Blend.Edges;
        if (mine.Left + mine.Right >= me.Width)
            notes.Add(new BlendNote(true, Words($"The left and right zones ({mine.Left:0.#} + {mine.Right:0.#} px) meet on a {me.Width} px wide output — the overlaps leave no picture between them.")));
        if (mine.Top + mine.Bottom >= me.Height)
            notes.Add(new BlendNote(true, Words($"The top and bottom zones ({mine.Top:0.#} + {mine.Bottom:0.#} px) meet on a {me.Height} px tall output — the overlaps leave no picture between them.")));
        foreach (var other in outputs)
        {
            if (other.Id == me.Id || !other.Enabled || other.Blend.Auto3D)
                continue;
            var edge = EdgeOf(me.Plan, other.Plan, out var zonePlan);
            if (edge == BlendEdge.None)
                continue;
            var overlap = ToOutputPixels(me, edge, zonePlan);
            var myZone = ZoneOn(mine, edge);
            var facing = Facing(edge);
            var theirZone = ZoneOn(other.Blend.Edges, facing);
            var side = Name(edge);
            var name = other.Name;
            if (myZone <= 0 && theirZone <= 0)
            {
                notes.Add(new BlendNote(true, Words($"{name} overlaps this output on the {side} by {overlap:0.#} px and neither fades the join — it will show twice as bright: set {overlap:0.#} px on both.")));
                continue;
            }
            if (myZone <= 0)
            {
                notes.Add(new BlendNote(true, Words($"{name} fades its {Name(facing)} edge into this output, but this output does not fade its {side} edge — set {Cap(side)} to {overlap:0.#} px.")));
                continue;
            }
            if (theirZone <= 0)
            {
                notes.Add(new BlendNote(true, Words($"This output fades its {side} edge {myZone:0.#} px into {name}, but {name} does not fade its {Name(facing)} edge — the join will be bright: set it to {myZone:0.#} px.")));
                continue;
            }
            if (Math.Abs(myZone - theirZone) >= WidthTolerancePx)
            {
                notes.Add(new BlendNote(true, Words($"This output fades {myZone:0.#} px and {name} {theirZone:0.#} px across the same {side} join — the light will not add up flat: use the overlap, {overlap:0.#} px, on both.")));
                continue;
            }
            if (other.Blend.Curve != me.Blend.Curve)
            {
                notes.Add(new BlendNote(true, Words($"{Cap(side)} join with {name}: {myZone:0.#} px, but this output's curve is {me.Blend.Curve} and {name}'s is {other.Blend.Curve} — use the same curve on both.")));
                continue;
            }
            if (Math.Abs(other.Blend.Gamma - me.Blend.Gamma) > 0.01)
            {
                notes.Add(new BlendNote(true, Words($"{Cap(side)} join with {name}: {myZone:0.#} px, but the gammas differ ({me.Blend.Gamma:0.##} and {other.Blend.Gamma:0.##}) — the fades will not sum to one in light.")));
                continue;
            }
            if (Math.Abs(myZone - overlap) >= WidthTolerancePx)
            {
                notes.Add(new BlendNote(true, Words($"{Cap(side)} join with {name}: both fade {myZone:0.#} px, but the plan overlaps them by {overlap:0.#} px — the fade should span the overlap.")));
                continue;
            }
            notes.Add(new BlendNote(false, Words($"{Cap(side)} join with {name}: {myZone:0.#} px, both fade, {me.Blend.Curve}.")));
        }
        return notes;
    }

    /// <summary>The notes one per line, warnings first.</summary>
    public static string Summary(IReadOnlyList<BlendNote> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        return string.Join("\n", notes.OrderByDescending(n => n.Warning).Select(n => (n.Warning ? "! " : "ok ") + n.Text));
    }

    private static string Words(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static string Name(BlendEdge edge) => edge switch
    {
        BlendEdge.Left => "left",
        BlendEdge.Top => "top",
        BlendEdge.Right => "right",
        BlendEdge.Bottom => "bottom",
        _ => "",
    };

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
