using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Model;

namespace PlayMap.Core.Led;

/// <summary>One DMX slot a fixture fills: where (a universe and a 1-based address; a DDP device's buffer counts in 512-byte rows), what
/// (a pixel's channel, or a head or tail channel with pixel -1) and which byte of it (0: an 8-bit channel; 1 and 2: the high and
/// low bytes of a 16-bit one).</summary>
public readonly record struct LedSlotUse(int Universe, int Address, int Pixel, LedChannel Channel, int Part, int Value);

/// <summary>Where a fixture's channels land, and the auto-patch. One rule places every fixture from its patch's start, so the
/// patcher and the plan agree: the head, each pixel and the tail are units that never straddle a universe — a unit that does not
/// fit in what is left of the node's universe (its <see cref="LedNode.UniverseChannels"/>) starts the next universe at address 1.
/// A DDP device is one buffer: its units run on byte after byte.</summary>
public static class LedPatcher
{
    public const int ArtNetMaxUniverse = 32767;

    public const int SacnMaxUniverse = 63999;

    /// <summary>A node's slots per universe: its own setting (0 = 510, 170 RGB pixels), at most 512.</summary>
    public static int Capacity(LedNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.UniverseChannels <= 0 ? 510 : Math.Min(512, node.UniverseChannels);
    }

    public static int MinUniverse(LedProtocol p) => p == LedProtocol.Sacn ? 1 : 0;

    public static int MaxUniverse(LedProtocol p) => p switch
    {
        LedProtocol.ArtNet => ArtNetMaxUniverse,
        LedProtocol.Sacn => SacnMaxUniverse,
        _ => 0,
    };

    /// <summary>A patch start in the words the desk and the verbs use: "3.1" (universe.address), or "byte 1" on a DDP device.</summary>
    public static string Words(LedProtocol protocol, int universe, int address) =>
        protocol == LedProtocol.Ddp ? string.Create(CultureInfo.InvariantCulture, $"byte {address}") : string.Create(CultureInfo.InvariantCulture, $"{universe}.{address}");

    /// <summary>Places a fixture of <paramref name="pixels"/> pixels from (universe, address) on the node: every slot it fills, in
    /// address order, and where the next fixture would start. False with the reason when a unit is larger than a universe, or the
    /// fixture runs past the protocol's last universe.</summary>
    public static bool TryPlace(LedNode node, LedFixtureType type, int pixels, int universe, int address, List<LedSlotUse>? into,
        out int nextUniverse, out int nextAddress, out string why)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(type);
        nextUniverse = universe;
        nextAddress = address;
        why = "";
        var ddp = node.Protocol == LedProtocol.Ddp;
        var capacity = ddp ? int.MaxValue : Capacity(node);
        if (address < 1 || (!ddp && address > 512))
        {
            why = ddp ? "a DDP start is a byte from 1" : "an address is 1 to 512";
            return false;
        }
        if (!ddp && (universe < MinUniverse(node.Protocol) || universe > MaxUniverse(node.Protocol)))
        {
            why = $"{node.Protocol switch { LedProtocol.Sacn => "sACN", _ => "Art-Net" }} universes are {MinUniverse(node.Protocol)} to {MaxUniverse(node.Protocol)}";
            return false;
        }
        var u = ddp ? 0 : universe;
        var a = address;
        bool Unit(ImmutableArray<LedChannelSlot> channels, int pixel, List<LedSlotUse>? list, ref int uu, ref int aa, out string w)
        {
            w = "";
            var size = LedBuiltinTypes.Slots(channels);
            if (size == 0)
                return true;
            if (!ddp)
            {
                if (size > capacity)
                {
                    w = $"{size} channels do not fit a universe of {capacity}";
                    return false;
                }
                if (aa - 1 + size > capacity)
                {
                    uu++;
                    aa = 1;
                }
                if (uu > MaxUniverse(node.Protocol))
                {
                    w = $"the fixture runs past universe {MaxUniverse(node.Protocol)}";
                    return false;
                }
            }
            foreach (var c in channels)
            {
                // The address moves whether or not the slots are listed.
                if (c.Bits == 16)
                {
                    list?.Add(Use(ddp, uu, aa, pixel, c, 1));
                    list?.Add(Use(ddp, uu, aa + 1, pixel, c, 2));
                    aa += 2;
                }
                else
                {
                    list?.Add(Use(ddp, uu, aa, pixel, c, 0));
                    aa++;
                }
            }
            return true;
        }
        if (!Unit(type.Head, -1, into, ref u, ref a, out why))
            return false;
        for (var i = 0; i < pixels; i++)
            if (!Unit(type.PerPixel, i, into, ref u, ref a, out why))
                return false;
        if (!Unit(type.Tail, -1, into, ref u, ref a, out why))
            return false;
        nextUniverse = u;
        nextAddress = a;
        return true;
    }

    private static LedSlotUse Use(bool ddp, int universe, int address, int pixel, LedChannelSlot c, int part) =>
        ddp
            ? new LedSlotUse((address - 1) / LedPlan.SlotsPerRow, (address - 1) % LedPlan.SlotsPerRow + 1, pixel, c.Channel, part, c.Value)
            : new LedSlotUse(universe, address, pixel, c.Channel, part, c.Value);

    /// <summary>The auto-patch: places <paramref name="scope"/> (in rig order) one after another on the nodes in turn from
    /// (fromUniverse, fromAddress) on the first; <paramref name="each"/> starts every fixture on a fresh universe. A fixture that
    /// would run past a node's last universe moves to the next node's start; with no next node the patch is refused with the reason.
    /// Answers each fixture's new patch.</summary>
    public static bool TryAutoPatch(LedRig rig, IReadOnlyList<LedFixture> scope, IReadOnlyList<LedNode> nodes, int fromUniverse, int fromAddress,
        bool each, out ImmutableArray<(string FixtureId, LedPatch Patch)> patches, out string why)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(nodes);
        patches = [];
        why = "";
        if (nodes.Count == 0)
        {
            why = "there is no LED node to patch to — ADD lednode first";
            return false;
        }
        var result = ImmutableArray.CreateBuilder<(string, LedPatch)>(scope.Count);
        var n = 0;
        var node = nodes[0];
        var u = fromUniverse;
        var a = fromAddress;
        foreach (var f in scope)
        {
            var type = LedBuiltinTypes.Resolve(rig, f.TypeId);
            if (type is null)
            {
                why = $"{f.Name} has no type {f.TypeId}";
                return false;
            }
            var count = LedLayouts.Local(f.Layout).Length;
            while (true)
            {
                if (node.Protocol == LedProtocol.Ddp && LedBuiltinTypes.Has16Bit(type) && DdpEightBitOnly(node))
                {
                    why = Wled16Words(node, type);
                    return false;
                }
                if (each && node.Protocol != LedProtocol.Ddp && a != 1)
                {
                    u++;
                    a = 1;
                }
                // Where the first unit really starts (a unit that does not fit moves to the next universe): the patch names it.
                var probe = new List<LedSlotUse>(4);
                if (TryPlace(node, type, Math.Min(count, 1), u, a, probe, out _, out _, out _) && probe.Count > 0
                    && TryPlace(node, type, count, u, a, null, out var nu, out var na, out _))
                {
                    var start = node.Protocol == LedProtocol.Ddp ? (0, a) : (probe[0].Universe, probe[0].Address);
                    result.Add((f.Id, new LedPatch(node.Id, start.Item1, start.Item2)));
                    u = nu;
                    a = na;
                    break;
                }
                if (++n >= nodes.Count)
                {
                    why = $"{f.Name} does not fit on {node.Name} from {Words(node.Protocol, u, a)}"
                        + (TryPlace(node, type, count, u, a, null, out _, out _, out var w) ? "" : $" ({w})")
                        + " and there is no next node — name another with NODE";
                    return false;
                }
                node = nodes[n];
                u = MinUniverse(node.Protocol);
                a = 1;
            }
        }
        patches = result.ToImmutable();
        return true;
    }

    /// <summary>True when a DDP device takes 8 bits a channel only (WLED's DDP receiver).</summary>
    public static bool DdpEightBitOnly(LedNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Protocol == LedProtocol.Ddp && string.Equals(node.Vendor, "wled", StringComparison.OrdinalIgnoreCase);
    }

    public static string Wled16Words(LedNode node, LedFixtureType type) =>
        $"{node.Name} is a WLED device, whose DDP takes 8 bits a channel — {type.Name} has 16-bit channels; use an 8-bit type";
}
