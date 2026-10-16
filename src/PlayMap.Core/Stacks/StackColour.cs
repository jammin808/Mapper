using System.Globalization;
using PlayMap.Core.Align;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Stacks;

/// <summary>A member's colour gains (linear light, each at most one) and its white before them: u′v′ and the distance from the
/// master's white.</summary>
public sealed record MemberGains(double R, double G, double B, double UBefore, double VBefore, double DuvBefore);

/// <summary>A stack's colour match: each member's gains (in the order given), the master's white u′v′, and words.</summary>
public sealed record StackColourMatch(IReadOnlyList<MemberGains> Members, double U, double V, string Words);

/// <summary>
/// A stack's colour match (facade §3.9, STACK MATCH): every member brought to the master's white chromaticity with per-channel gains
/// of at most one, through align's colour arithmetic (<see cref="ColourMatch"/>: camera-linear sRGB, u′v′). With a member's primaries
/// measured (its red, green and blue alone) the gains are M⁻¹·white, exact for any primaries; with only its white (a scan's colour
/// white frame), the primaries are taken as the camera's own (<see cref="FromWhite"/>), and the gains are the white's per-channel
/// ratio to the target. Level is left alone: stacked light adds, and the sum's evenness is the blend's work.
/// </summary>
public static class StackColour
{
    /// <summary>A member's colour from its white alone: each primary the white's own channel (the camera's primaries).</summary>
    public static ProjectorColour FromWhite(Vec3d black, Vec3d white)
    {
        var w = white - black;
        return new ProjectorColour(black, black + new Vec3d(w.X, 0, 0), black + new Vec3d(0, w.Y, 0), black + new Vec3d(0, 0, w.Z), white);
    }

    /// <summary>The members' gains to the white of member <paramref name="master"/>; null with the words when one cannot be matched.</summary>
    public static (StackColourMatch? Match, string Words) ToMaster(IReadOnlyList<ProjectorColour> colours, int master)
    {
        ArgumentNullException.ThrowIfNull(colours);
        if (master < 0 || master >= colours.Count)
            return (null, "no master to match to");
        var (tu, tv) = ColourMatch.Uv(colours[master].White - colours[master].Black);
        if (!double.IsFinite(tu))
            return (null, "the master's white was too dim to measure");
        var x = 9 * tu / (4 * tv);
        var z = (12 - 3 * tu - 20 * tv) / (4 * tv);
        var target = ColourMatch.FromXyz(new Vec3d(x, 1, z));
        var members = new List<MemberGains>(colours.Count);
        var worst = 0.0;
        for (var j = 0; j < colours.Count; j++)
        {
            var c = colours[j];
            var m = Mat3d.FromColumns(c.Red - c.Black, c.Green - c.Black, c.Blue - c.Black);
            if (Math.Abs(m.Determinant) < 1e-15)
                return (null, $"member {j + 1}'s primaries were not told apart by the camera — is it a colour camera, and was the member lit?");
            var (u, v) = ColourMatch.Uv(c.White - c.Black);
            if (!double.IsFinite(u))
                return (null, $"member {j + 1}'s white was too dim to measure");
            var g = m.Inverse() * target;
            var top = Math.Max(g.X, Math.Max(g.Y, g.Z));
            if (!(top > 0) || g.X < 0 || g.Y < 0 || g.Z < 0)
                return (null, $"member {j + 1} cannot reach the master's white (its primaries leave it outside) — colour was not matched");
            g /= top;
            var duv = Math.Sqrt((u - tu) * (u - tu) + (v - tv) * (v - tv));
            worst = Math.Max(worst, duv);
            members.Add(new MemberGains(g.X, g.Y, g.Z, u, v, duv));
        }
        var words = string.Create(CultureInfo.InvariantCulture,
            $"{colours.Count} members matched to the master's white u′v′ {tu:0.0000}, {tv:0.0000} (they were up to Δu′v′ {worst:0.0000} from it); levels are left as they are");
        return (new StackColourMatch(members, tu, tv, words), words);
    }
}
