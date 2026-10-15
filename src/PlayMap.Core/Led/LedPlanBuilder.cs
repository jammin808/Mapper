using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Led;

/// <summary>Compiles a project's rig into a <see cref="LedPlan"/>: pure, deterministic, off the render thread. 2D positions name
/// pixels (x = 3 is the fourth column's centre); surface and source positions are 0–1 (v down). Fixtures that cannot be sampled or
/// sent are kept out with a warning that says why; slots two fixtures claim stay the first one's and are reported as a conflict.</summary>
public static class LedPlanBuilder
{
    /// <summary>The size a generator or a video an LED fixture samples is resolved at when nothing else gives one.</summary>
    public const int SourceSize = 512;

    /// <summary>Ethernet, IPv4 and UDP headers a packet carries on the wire.</summary>
    public const int WireOverhead = 42;

    private sealed record Pending(LedFixture Fixture, LedFixtureType Type, Vec3d[] Points, int Group, float[] Attributes, string AirOutput);

    public static LedPlan Build(Project project, int maxMbps, long serial)
    {
        ArgumentNullException.ThrowIfNull(project);
        var rig = project.Leds;
        var warnings = new List<string>();
        var groupKeys = new List<(LedBinding Binding, string Key, SourceRef Source, int W, int H, int Taps, float Gamma)>();
        var pending = new List<Pending>();
        foreach (var f in rig.Fixtures)
        {
            if (!f.Enabled)
                continue;
            var type = LedBuiltinTypes.Resolve(rig, f.TypeId);
            if (type is null)
            {
                warnings.Add($"{f.Name} names a type this show does not have ({f.TypeId}) — it is not sent");
                continue;
            }
            if (LedBuiltinTypes.Problem(type) is { Length: > 0 } problem)
            {
                warnings.Add($"{f.Name}: {problem} — it is not sent");
                continue;
            }
            var points = LedLayouts.Placed(f);
            var (binding, key, source, w, h, air, map, words) = Bind(project, f);
            if (words.Length > 0)
                warnings.Add($"{f.Name} {words}");
            var taps = f.Sampling == LedSampling.Area ? 9 : 1;
            var gamma = (float)(f.Color.ContentGamma > 0 ? f.Color.ContentGamma : 2.2);
            var gk = (binding, key, source, w, h, taps, gamma);
            var g = groupKeys.IndexOf(gk);
            if (g < 0)
            {
                groupKeys.Add(gk);
                g = groupKeys.Count - 1;
            }
            var radius = f.Footprint > 0 ? f.Footprint : LedLayouts.HalfPitch(points);
            var attr = new float[points.Length * 4];
            for (var i = 0; i < points.Length; i++)
            {
                var (u, v, rx, ry) = binding == LedBinding.None ? (-1.0, -1.0, 0.0, 0.0) : map(points[i], radius);
                attr[i * 4] = (float)u;
                attr[i * 4 + 1] = (float)v;
                attr[i * 4 + 2] = (float)rx;
                attr[i * 4 + 3] = (float)ry;
            }
            pending.Add(new Pending(f, type, points, g, attr, air));
        }

        // Pixels by group, each fixture's contiguous; fixtures keep rig order for their index.
        var first = new int[pending.Count];
        var groups = ImmutableArray.CreateBuilder<LedGroup>(groupKeys.Count);
        var cursor = 0;
        for (var g = 0; g < groupKeys.Count; g++)
        {
            var start = cursor;
            for (var i = 0; i < pending.Count; i++)
            {
                if (pending[i].Group != g)
                    continue;
                first[i] = cursor;
                cursor += pending[i].Points.Length;
            }
            var k = groupKeys[g];
            groups.Add(new LedGroup(k.Binding, k.Key, k.Source, k.W, k.H, start, cursor - start, k.Taps, k.Gamma));
        }
        var pixels = cursor;
        var attributes = new float[Math.Max(1, (pixels + LedPlan.ColorsWidth - 1) / LedPlan.ColorsWidth) * LedPlan.ColorsWidth * 4];
        for (var i = 0; i < pending.Count; i++)
            pending[i].Attributes.CopyTo(attributes, first[i] * 4);

        // Patch: every slot of every patched fixture on an enabled node, the first claim of a slot kept.
        var nodes = rig.Nodes.Where(n => n.Enabled).ToList();
        var owners = new Dictionary<(string Node, int Universe), (int[] Owner, int[] Op, int[] Pixel, int[] Value)>();
        var conflictsRaw = new List<(string Node, int Universe, int Address, int A, int B)>();
        var fixtures = ImmutableArray.CreateBuilder<LedFixturePlan>(pending.Count);
        var texels = new float[Math.Max(1, pending.Count) * LedPlan.FixtureTexels * 4];
        var uses = new List<LedSlotUse>();
        for (var i = 0; i < pending.Count; i++)
        {
            var (f, type) = (pending[i].Fixture, pending[i].Type);
            var nodeId = "";
            var range = "unpatched";
            var follow = true;
            if (f.Patch is { } patch)
            {
                var node = rig.Nodes.FirstOrDefault(n => n.Id == patch.NodeId);
                if (node is null)
                {
                    warnings.Add($"{f.Name} is patched to a node the show no longer has ({patch.NodeId})");
                }
                else if (!node.Enabled)
                {
                    range = $"{node.Name} (off)";
                    follow = node.FollowBlackout;
                }
                else if (LedPatcher.DdpEightBitOnly(node) && LedBuiltinTypes.Has16Bit(type))
                {
                    warnings.Add($"{f.Name}: {LedPatcher.Wled16Words(node, type)} — it is not sent");
                }
                else
                {
                    uses.Clear();
                    if (!LedPatcher.TryPlace(node, type, pending[i].Points.Length, patch.Universe, patch.Address, uses, out _, out _, out var why))
                    {
                        warnings.Add($"{f.Name} cannot be placed on {node.Name} at {LedPatcher.Words(node.Protocol, patch.Universe, patch.Address)}: {why}");
                    }
                    else
                    {
                        nodeId = node.Id;
                        follow = node.FollowBlackout;
                        range = RangeWords(node, patch, uses);
                        foreach (var s in uses)
                        {
                            if (!owners.TryGetValue((node.Id, s.Universe), out var row))
                            {
                                row = (Filled(-1), Filled(-1), Filled(-1), new int[LedPlan.SlotsPerRow]);
                                owners[(node.Id, s.Universe)] = row;
                            }
                            var at = s.Address - 1;
                            if (row.Owner[at] >= 0)
                            {
                                conflictsRaw.Add((node.Id, s.Universe, s.Address, row.Owner[at], i));
                                continue;
                            }
                            row.Owner[at] = i;
                            row.Op[at] = (int)s.Channel * 4 + s.Part;
                            row.Pixel[at] = s.Pixel < 0 ? first[i] : first[i] + s.Pixel;
                            row.Value[at] = s.Value;
                        }
                    }
                }
            }
            var hasWhite = type.PerPixel.Any(c => c.Channel is LedChannel.W or LedChannel.WW or LedChannel.CW);
            fixtures.Add(new LedFixturePlan(f.Id, f.Name, i, first[i], pending[i].Points.Length, nodeId, pending[i].AirOutput, follow, hasWhite, range));
            var t = texels.AsSpan(i * LedPlan.FixtureTexels * 4, LedPlan.FixtureTexels * 4);
            var c = f.Color;
            var (er, eg, eb) = Hex(f.EditorColor);
            t[0] = (float)c.GainR;
            t[1] = (float)c.GainG;
            t[2] = (float)c.GainB;
            t[3] = (float)c.GainW;
            t[4] = (float)c.Brightness;
            t[5] = (float)(c.OutputGamma > 0 ? c.OutputGamma : 2.2);
            t[6] = first[i];
            t[7] = pending[i].Points.Length;
            t[8] = er;
            t[9] = eg;
            t[10] = eb;
            t[11] = (float)(c.ContentGamma > 0 ? c.ContentGamma : 2.2);
            t[12] = hasWhite ? 1 : 0;
            t[13] = follow ? 1 : 0;
        }

        // Rows: nodes in rig order, universes ascending.
        var rows = ImmutableArray.CreateBuilder<LedRow>();
        var nodePlans = ImmutableArray.CreateBuilder<LedNodePlan>();
        var patchData = new List<float>();
        var total = 0.0;
        foreach (var node in nodes)
        {
            var keys = owners.Keys.Where(k => k.Node == node.Id).Select(k => k.Universe).Order().ToList();
            var firstRow = rows.Count;
            var rate = RateOf(node);
            var mbps = 0.0;
            var ddpBytes = 0;
            foreach (var u in keys)
            {
                var row = owners[(node.Id, u)];
                var used = 0;
                for (var s = 0; s < LedPlan.SlotsPerRow; s++)
                {
                    if (row.Owner[s] >= 0)
                        used = s + 1;
                    patchData.Add(row.Owner[s] < 0 ? -1 : row.Pixel[s]);
                    patchData.Add(row.Owner[s] < 0 ? -1 : row.Op[s]);
                    patchData.Add(row.Owner[s] < 0 ? -1 : row.Owner[s]);
                    patchData.Add(row.Owner[s] < 0 ? 0 : row.Value[s]);
                }
                rows.Add(new LedRow(node.Id, u, used));
                if (node.Protocol == LedProtocol.Ddp)
                    ddpBytes = Math.Max(ddpBytes, u * LedPlan.SlotsPerRow + used);
                else
                    mbps += PacketBytes(node.Protocol, used) * 8.0 * rate / 1e6;
            }
            if (node.Protocol == LedProtocol.Ddp && ddpBytes > 0)
            {
                var packets = (ddpBytes + Ddp.MaxData - 1) / Ddp.MaxData;
                mbps += (ddpBytes + packets * (Ddp.HeaderLength + WireOverhead)) * 8.0 * rate / 1e6;
            }
            total += mbps;
            var fixturesOn = fixtures.Where(f => f.NodeId == node.Id).ToList();
            var types = fixturesOn.Select(f => LedBuiltinTypes.Resolve(rig, rig.Fixtures.First(x => x.Id == f.Id).TypeId)!).ToList();
            var white = fixturesOn.Any(f => f.HasWhite);
            var deep = types.Any(LedBuiltinTypes.Has16Bit);
            var ddpType = deep ? (white ? Ddp.Rgbw16 : Ddp.Rgb16) : white ? Ddp.Rgbw8 : Ddp.Rgb8;
            nodePlans.Add(new LedNodePlan(node.Id, node.Name, node.Protocol, firstRow, rows.Count - firstRow, rate, mbps, ddpType,
                keys.Count == 0 ? "nothing is patched to it" : ""));
        }

        var conflicts = Conflicts(conflictsRaw, pending);
        var refused = total > maxMbps
            ? string.Create(CultureInfo.InvariantCulture,
                $"{total:0.0} Mb/s is over this machine's {maxMbps} Mb/s guard ({rows.Count} universes at {string.Join(", ", nodePlans.Where(n => n.Rows > 0).Select(n => $"{n.RateHz:0.#} Hz to {n.Name}"))}) — nothing is sent; patch fewer pixels, lower a node's rate, or raise the guard")
            : "";
        return new LedPlan
        {
            Serial = serial,
            Pixels = pixels,
            Groups = groups.ToImmutable(),
            Attributes = attributes,
            Fixtures = fixtures.ToImmutable(),
            FixtureTexelData = texels,
            Rows = rows.ToImmutable(),
            Patch = [.. patchData],
            Nodes = nodePlans.ToImmutable(),
            Conflicts = conflicts,
            Warnings = [.. warnings],
            Mbps = Math.Round(total, 3),
            Refused = refused,
            Rig = rig,
        };
    }

    private static int[] Filled(int v)
    {
        var a = new int[LedPlan.SlotsPerRow];
        Array.Fill(a, v);
        return a;
    }

    /// <summary>A node's rate: its own, else the protocol's default (Art-Net and sACN 44 Hz, DDP 60 Hz).</summary>
    public static double RateOf(LedNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.RateHz > 0 ? node.RateHz : node.Protocol == LedProtocol.Ddp ? 60 : 44;
    }

    /// <summary>One universe's packet on the wire, bytes: Art-Net 18 + the even slot count, sACN 126 + the slots, plus the headers
    /// below UDP.</summary>
    public static int PacketBytes(LedProtocol protocol, int slots) => protocol switch
    {
        LedProtocol.ArtNet => 18 + Math.Max(2, slots + (slots & 1)) + WireOverhead,
        LedProtocol.Sacn => 126 + slots + WireOverhead,
        _ => Ddp.HeaderLength + slots + WireOverhead,
    };

    private static string RangeWords(LedNode node, LedPatch patch, List<LedSlotUse> uses)
    {
        if (uses.Count == 0)
            return $"{node.Name} {LedPatcher.Words(node.Protocol, patch.Universe, patch.Address)}";
        var a = uses[0];
        var b = uses[^1];
        if (node.Protocol == LedProtocol.Ddp)
            return string.Create(CultureInfo.InvariantCulture, $"{node.Name} bytes {a.Universe * 512 + a.Address}–{b.Universe * 512 + b.Address}");
        return string.Create(CultureInfo.InvariantCulture, $"{node.Name} {a.Universe}.{a.Address}–{b.Universe}.{b.Address}");
    }

    private static ImmutableArray<LedConflict> Conflicts(List<(string Node, int Universe, int Address, int A, int B)> raw, List<Pending> pending)
    {
        var list = ImmutableArray.CreateBuilder<LedConflict>();
        foreach (var g in raw.GroupBy(r => (r.Node, r.Universe, r.A, r.B)))
        {
            var addresses = g.Select(r => r.Address).Order().ToList();
            var from = addresses[0];
            var prev = from;
            for (var i = 1; i <= addresses.Count; i++)
            {
                if (i < addresses.Count && addresses[i] == prev + 1)
                {
                    prev = addresses[i];
                    continue;
                }
                list.Add(new LedConflict(g.Key.Node, g.Key.Universe, from, prev, [pending[g.Key.A].Fixture.Id, pending[g.Key.B].Fixture.Id]));
                if (i < addresses.Count)
                    from = prev = addresses[i];
            }
        }
        return list.ToImmutable();
    }

    /// <summary>"#RRGGBB" as 0–1 code values (white when it does not read).</summary>
    public static (float R, float G, float B) Hex(string? hex)
    {
        if (hex is { Length: 7 } h && h[0] == '#' && int.TryParse(h.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return (((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
        return (1, 1, 1);
    }

    private delegate (double U, double V, double Rx, double Ry) Mapper(Vec3d p, double radius);

    /// <summary>What a fixture samples and how its positions map to the picture's normalised coordinates; words when it samples
    /// black.</summary>
    private static (LedBinding Binding, string Key, SourceRef Source, int W, int H, string AirOutput, Mapper Map, string Words) Bind(Project p, LedFixture f)
    {
        Mapper none = (_, _) => (-1, -1, 0, 0);
        switch (f.Space)
        {
            case LedSpace.Canvas:
                {
                    if (p.FindCanvas(f.SpaceId) is not { Width: > 0, Height: > 0 } c)
                        return (LedBinding.None, "", SourceRef.None, 0, 0, "", none, $"samples a canvas the show does not have ({f.SpaceId}) — black");
                    return (LedBinding.Canvas, c.Id, SourceRef.Canvas(c.Id), c.Width, c.Height, "", Pixels(c.Width, c.Height), "");
                }
            case LedSpace.Output:
                {
                    if (p.FindOutput(f.SpaceId) is not { Width: > 0, Height: > 0 } o)
                        return (LedBinding.None, "", SourceRef.None, 0, 0, "", none, $"samples an output the show does not have ({f.SpaceId}) — black");
                    return (LedBinding.Output, o.Id, SourceRef.None, o.Width, o.Height, o.Id, Pixels(o.Width, o.Height),
                        o.Enabled ? "" : $"samples {o.Name}, which is off — black until it is on");
                }
            case LedSpace.Source:
                {
                    if (!SourceRef.TryParse(f.SpaceId, out var s) || s.IsNone)
                        return (LedBinding.None, "", SourceRef.None, 0, 0, "", none, $"samples no source ('{f.SpaceId}') — black");
                    var (w, h) = SizeOf(p, s);
                    return (LedBinding.Source, s.ToString(), s, w, h, "", Unit(0, 0, 1, 1), "");
                }
            case LedSpace.Surface:
                {
                    if (p.FindSurface(f.SpaceId) is not { } sf)
                        return (LedBinding.None, "", SourceRef.None, 0, 0, "", none, $"samples a surface the show does not have ({f.SpaceId}) — black");
                    var content = sf.Content;
                    if (content.Source.IsNone)
                        return (LedBinding.None, "", SourceRef.None, 0, 0, "", none, "");
                    var (w, h) = SizeOf(p, content.Source);
                    var r = content.Region;
                    switch (content.Mode)
                    {
                        case SurfaceMapping.CanvasRegion:
                            return (LedBinding.Source, content.Source.ToString(), content.Source, w, h, "", Unit(r.X / w, r.Y / h, r.W / w, r.H / h), "");
                        case SurfaceMapping.Uv:
                            return (LedBinding.Source, content.Source.ToString(), content.Source, w, h, "",
                                r.IsEmpty ? Unit(0, 0, 1, 1) : Unit(r.X, r.Y, r.W, r.H), "");
                        default:
                            return (LedBinding.None, "", SourceRef.None, 0, 0, "", none,
                                $"samples {sf.Name}, whose content is projected from a viewpoint — black for LEDs this round");
                    }
                }
            default:
                return (LedBinding.None, "", SourceRef.None, 0, 0, "", none, "is a world fixture — world sampling is next round, black for now");
        }
    }

    private static Mapper Pixels(int w, int h) => (pt, r) => ((pt.X + 0.5) / w, (pt.Y + 0.5) / h, r / w, r / h);

    private static Mapper Unit(double x, double y, double w, double h) => (pt, r) => (x + pt.X * w, y + pt.Y * h, r * w, r * h);

    /// <summary>The size a source is resolved at: a canvas's own, else <see cref="SourceSize"/> square.</summary>
    private static (int W, int H) SizeOf(Project p, SourceRef s) =>
        s.Kind == SourceKind.Canvas && p.FindCanvas(s.Id) is { Width: > 0, Height: > 0 } c ? (c.Width, c.Height) : (SourceSize, SourceSize);
}
