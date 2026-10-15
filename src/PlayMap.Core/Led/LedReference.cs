using PlayMap.Core.Model;

namespace PlayMap.Core.Led;

/// <summary>The CPU reference of the two GPU passes, written as the shaders are (single precision, the same order of operations), so
/// a test can hold the read-back bytes against it: exact for point sampling, within one code for area sampling.</summary>
public static class LedReference
{
    /// <summary>The area footprint's taps: a 3 × 3 grid over the footprint (± two thirds of its radii) and the centre.</summary>
    public const float TapSpread = 0.6666667f;

    /// <summary>A picture as the reference reads it: RGBA floats, top row first.</summary>
    public sealed record Picture(float[] Rgba, int Width, int Height);

    /// <summary>Pass 1: every pixel's linear colour (RGBA floats, alpha 1), from each group's picture (null = black).</summary>
    public static float[] Sample(LedPlan plan, Func<LedGroup, Picture?> pictures)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(pictures);
        var colors = new float[Math.Max(1, plan.Pixels) * 4];
        foreach (var g in plan.Groups)
        {
            var pic = g.Binding == LedBinding.None ? null : pictures(g);
            for (var i = g.First; i < g.First + g.Count; i++)
            {
                var a = plan.Attributes.AsSpan(i * 4, 4);
                var (r, gg, b) = pic is null ? (0f, 0f, 0f) : SampleOne(a[0], a[1], a[2], a[3], g.Taps, g.Gamma, pic);
                colors[i * 4] = r;
                colors[i * 4 + 1] = gg;
                colors[i * 4 + 2] = b;
                colors[i * 4 + 3] = 1;
            }
        }
        return colors;
    }

    /// <summary>One pixel: point sampling reads the texel under (u, v) (0–1 inclusive, else black); area sampling averages nine
    /// bilinear taps (a tap outside the picture is black). Each tap is decoded to linear light before averaging.</summary>
    public static (float R, float G, float B) SampleOne(float u, float v, float rx, float ry, int taps, float gamma, Picture pic)
    {
        ArgumentNullException.ThrowIfNull(pic);
        if (taps <= 1)
        {
            if (!(u >= 0f && u <= 1f && v >= 0f && v <= 1f))
                return (0, 0, 0);
            var x = Math.Min((int)MathF.Floor(u * pic.Width), pic.Width - 1);
            var y = Math.Min((int)MathF.Floor(v * pic.Height), pic.Height - 1);
            var o = (y * pic.Width + x) * 4;
            return (Decode(pic.Rgba[o], gamma), Decode(pic.Rgba[o + 1], gamma), Decode(pic.Rgba[o + 2], gamma));
        }
        float ar = 0, ag = 0, ab = 0;
        for (var j = -1; j <= 1; j++)
        {
            for (var i = -1; i <= 1; i++)
            {
                var su = u + i * TapSpread * rx;
                var sv = v + j * TapSpread * ry;
                if (!(su >= 0f && su <= 1f && sv >= 0f && sv <= 1f))
                    continue;
                var (r, g, b) = Bilinear(pic, su, sv);
                ar += Decode(r, gamma);
                ag += Decode(g, gamma);
                ab += Decode(b, gamma);
            }
        }
        return (ar / 9f, ag / 9f, ab / 9f);
    }

    private static float Decode(float c, float gamma) => MathF.Pow(MathF.Max(c, 0f), gamma);

    private static (float R, float G, float B) Bilinear(Picture pic, float s, float t)
    {
        var x = s * pic.Width - 0.5f;
        var y = t * pic.Height - 0.5f;
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        (float, float, float) At(int xi, int yi)
        {
            xi = Math.Clamp(xi, 0, pic.Width - 1);
            yi = Math.Clamp(yi, 0, pic.Height - 1);
            var o = (yi * pic.Width + xi) * 4;
            return (pic.Rgba[o], pic.Rgba[o + 1], pic.Rgba[o + 2]);
        }
        var (a0, a1, a2) = At(x0, y0);
        var (b0, b1, b2) = At(x0 + 1, y0);
        var (c0, c1, c2) = At(x0, y0 + 1);
        var (d0, d1, d2) = At(x0 + 1, y0 + 1);
        float L(float a, float b, float c, float d) => (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
        return (L(a0, b0, c0, d0), L(a1, b1, c1, d1), L(a2, b2, c2, d2));
    }

    /// <summary>Pass 2: every row's 512 slots from the pixels' linear colours, the frame's per-fixture air (<see cref="LedAir"/>),
    /// the master level, and the test pattern with its step.</summary>
    public static byte[] Patch(LedPlan plan, float[] colors, ReadOnlySpan<float> air, float level, LedTestKind test, int step)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(colors);
        var bytes = new byte[plan.Rows.Length * LedPlan.SlotsPerRow];
        for (var row = 0; row < plan.Rows.Length; row++)
            for (var s = 0; s < LedPlan.SlotsPerRow; s++)
                bytes[row * LedPlan.SlotsPerRow + s] = (byte)Slot(plan, colors, air, level, test, step, row, s);
        return bytes;
    }

    private static readonly float[] Luma = [0.2126f, 0.7152f, 0.0722f];

    private static uint Slot(LedPlan plan, float[] colors, ReadOnlySpan<float> air, float level, LedTestKind test, int step, int row, int slot)
    {
        var e = plan.Patch.AsSpan((row * LedPlan.SlotsPerRow + slot) * 4, 4);
        if (e[1] < 0f)
            return 0;
        var op = (int)e[1];
        var comp = (LedChannel)(op >> 2);
        var sel = op & 3;
        var fi = (int)e[2];
        var cst = (int)e[3];
        var f = plan.FixtureTexelData.AsSpan(fi * LedPlan.FixtureTexels * 4, LedPlan.FixtureTexels * 4);
        var a = air.Slice(fi * 4, 4);
        if (comp == LedChannel.Gap)
            return 0;
        if (comp == LedChannel.Constant)
            return Raw(cst, sel);
        var black = a[2] > 0.5f;
        if (comp == LedChannel.Dimmer)
            return black ? 0 : Raw(cst, sel);
        if (black)
            return 0;
        var pix = (int)e[0];
        var hasWhite = f[12] > 0.5f;
        if (a[3] > 0.5f || (test != LedTestKind.None && a[1] > 0.5f))
        {
            var locate = a[3] > 0.5f;
            var full = !locate && test == LedTestKind.Full;
            var code = locate
                ? (a[3] > 1.5f ? (f[8], f[9], f[10]) : (1f, 1f, 1f))
                : LedTestPatterns.Code(test, pix - (int)f[6], (int)f[7], fi, plan.Fixtures.Length, row, step);
            var c4 = full ? (1f, 1f, 1f, 1f) : hasWhite ? Extract(code) : (code.Item1, code.Item2, code.Item3, 0f);
            var lum = full ? 1f : Lum(code.Item1, code.Item2, code.Item3);
            var v = Component(c4, lum, comp, full) * (level * a[0]);
            return Quantise(v, sel);
        }
        var p = pix * 4;
        var scale = f[4] * level * MathF.Pow(a[0], f[11]);
        var lin = (colors[p] * scale, colors[p + 1] * scale, colors[p + 2] * scale);
        var c = hasWhite ? Extract(lin) : (lin.Item1, lin.Item2, lin.Item3, 0f);
        var w = Component(c, Lum(lin.Item1, lin.Item2, lin.Item3), comp, false) * Gain(f, comp);
        w = w <= 0f ? 0f : MathF.Pow(MathF.Min(w, 1f), 1f / f[5]);
        return Quantise(w, sel);
    }

    private static float Lum(float r, float g, float b) => r * Luma[0] + g * Luma[1] + b * Luma[2];

    /// <summary>White extraction: w = min(r, g, b), taken out of each.</summary>
    private static (float, float, float, float) Extract((float R, float G, float B) c)
    {
        var w = MathF.Min(c.R, MathF.Min(c.G, c.B));
        return (c.R - w, c.G - w, c.B - w, w);
    }

    private static float Component((float R, float G, float B, float W) c, float lum, LedChannel comp, bool full) => comp switch
    {
        LedChannel.R => c.R,
        LedChannel.G => c.G,
        LedChannel.B => c.B,
        LedChannel.W or LedChannel.WW or LedChannel.CW => c.W,
        LedChannel.L => lum,
        LedChannel.A or LedChannel.UV => full ? 1f : 0f,
        _ => 0f,
    };

    private static float Gain(ReadOnlySpan<float> f, LedChannel comp) => comp switch
    {
        LedChannel.R => f[0],
        LedChannel.G => f[1],
        LedChannel.B => f[2],
        LedChannel.W or LedChannel.WW or LedChannel.CW => f[3],
        _ => 1f,
    };

    private static uint Quantise(float v, int sel)
    {
        v = Math.Clamp(v, 0f, 1f);
        if (sel == 0)
            return (uint)MathF.Floor(v * 255f + 0.5f);
        var q = (uint)MathF.Floor(v * 65535f + 0.5f);
        return sel == 1 ? q >> 8 : q & 255u;
    }

    private static uint Raw(int value, int sel) => sel switch
    {
        0 => (uint)Math.Clamp(value, 0, 255),
        1 => ((uint)Math.Clamp(value, 0, 65535) >> 8) & 255u,
        _ => (uint)Math.Clamp(value, 0, 65535) & 255u,
    };

    /// <summary>The bytes a black frame sends: every colour and dimmer slot 0, constants kept (what OUTPUT OFF sends before it
    /// stops).</summary>
    public static byte[] Black(LedPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var bytes = new byte[plan.Rows.Length * LedPlan.SlotsPerRow];
        for (var i = 0; i < bytes.Length; i++)
        {
            var op = plan.Patch[i * 4 + 1];
            if (op >= 0 && (LedChannel)((int)op >> 2) == LedChannel.Constant)
                bytes[i] = (byte)Raw((int)plan.Patch[i * 4 + 3], (int)op & 3);
        }
        return bytes;
    }
}
