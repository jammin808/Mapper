using System.Collections.Immutable;
using PlayMap.Core.Model;

namespace PlayMap.Core.Led;

/// <summary>The fixture types every node knows without a file: answered by id ("builtin:grb"), never stored in a project. A
/// fixture names one as its type, or a type of the project's own (<see cref="LedRig.Types"/>).</summary>
public static class LedBuiltinTypes
{
    private static LedChannelSlot S(LedChannel c, int bits = 8, int value = 0) => new(c, bits, value);

    private static LedFixtureType T(string id, string name, ImmutableArray<LedChannelSlot> head, params LedChannelSlot[] perPixel) =>
        new("builtin:" + id, name, head, [.. perPixel], [], 0, "builtin:" + id, "");

    public static readonly ImmutableArray<LedFixtureType> All =
    [
        T("rgb", "RGB", [], S(LedChannel.R), S(LedChannel.G), S(LedChannel.B)),
        T("grb", "GRB (WS2812)", [], S(LedChannel.G), S(LedChannel.R), S(LedChannel.B)),
        T("brg", "BRG", [], S(LedChannel.B), S(LedChannel.R), S(LedChannel.G)),
        T("rgbw", "RGBW", [], S(LedChannel.R), S(LedChannel.G), S(LedChannel.B), S(LedChannel.W)),
        T("grbw", "GRBW (SK6812)", [], S(LedChannel.G), S(LedChannel.R), S(LedChannel.B), S(LedChannel.W)),
        T("rgb16", "RGB 16-bit", [], S(LedChannel.R, 16), S(LedChannel.G, 16), S(LedChannel.B, 16)),
        T("rgbw16", "RGBW 16-bit", [], S(LedChannel.R, 16), S(LedChannel.G, 16), S(LedChannel.B, 16), S(LedChannel.W, 16)),
        T("l", "Luminance", [], S(LedChannel.L)),
        T("dimmer+rgb", "Dimmer + RGB", [S(LedChannel.Dimmer, 8, 255)], S(LedChannel.R), S(LedChannel.G), S(LedChannel.B)),
    ];

    /// <summary>A built-in type by its id ("builtin:grb") or its short word ("grb"), or null.</summary>
    public static LedFixtureType? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        var key = id.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase) ? id : "builtin:" + id;
        foreach (var t in All)
            if (string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }

    /// <summary>A fixture's type: the project's own by id, else a built-in one; null when neither has it.</summary>
    public static LedFixtureType? Resolve(LedRig rig, string? typeId)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (typeId is not null)
            foreach (var t in rig.Types)
                if (string.Equals(t.Id, typeId, StringComparison.Ordinal))
                    return t;
        return Find(typeId);
    }

    /// <summary>Channels one pixel takes.</summary>
    public static int Stride(LedFixtureType type) => Slots(type.PerPixel);

    /// <summary>Slots a run of channels takes (a 16-bit channel takes two).</summary>
    public static int Slots(ImmutableArray<LedChannelSlot> channels)
    {
        var n = 0;
        foreach (var c in channels)
            n += c.Bits == 16 ? 2 : 1;
        return n;
    }

    /// <summary>True when any channel of the type is 16-bit.</summary>
    public static bool Has16Bit(LedFixtureType type) =>
        type.Head.Any(c => c.Bits == 16) || type.PerPixel.Any(c => c.Bits == 16) || type.Tail.Any(c => c.Bits == 16);

    /// <summary>Why a type cannot be sent at all ("" when it can): channels must be 8 or 16 bits and a pixel needs one slot.</summary>
    public static string Problem(LedFixtureType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.PerPixel.IsDefaultOrEmpty)
            return $"{type.Name} has no channels per pixel";
        foreach (var c in type.Head.Concat(type.PerPixel).Concat(type.Tail))
            if (c.Bits is not (8 or 16))
                return $"{type.Name} has a {c.Bits}-bit channel — channels are 8 or 16 bits";
        return "";
    }
}
