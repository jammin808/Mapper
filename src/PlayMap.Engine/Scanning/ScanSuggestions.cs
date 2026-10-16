using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Wire;
using PlayMap.Vision.Surfaces;

namespace PlayMap.Engine.Scanning;

/// <summary>A surface the scan suggests, for a person to approve: the measured region it is, its name, kind ("plane" or "curved"),
/// how sure the geometry is (0–1), its reasons in words, the projectors that light it, and the proposals queued for it — a SCAN
/// SURFACE line, and for a plane a SURFACE QUAD four-corner layer on its main projector. <see cref="Measured"/> is false for a
/// name an assistant gave to something the geometry did not find: it is shown, grey, and has no proposal.</summary>
public sealed record ScanSuggestion(string Id, string RegionId, string Label, string Kind, double Confidence, ImmutableArray<string> Reasons,
    bool Measured, ImmutableArray<string> Projectors, ImmutableArray<string> Proposals);

/// <summary>A projector as a suggestion's descriptor names it: its name and the share of its picture the region takes.</summary>
public sealed record SuggestionProjector(string Name, double CoveragePct);

/// <summary>What an assistant is told of a measured region — text and numbers only: no picture, no pixel, no path. The request
/// builder takes nothing else.</summary>
public sealed record SuggestionDescriptor(string Id, string Kind, double AreaM2, double PlanarityRmsMm, double NormalToViewDeg, double HeightM,
    ImmutableArray<SuggestionProjector> Projectors, string MeasuredLabel);

/// <summary>A measured candidate before it is queued: its descriptor, the words, the lines a person may approve.</summary>
public sealed record SuggestionCandidate(SuggestionDescriptor Descriptor, string Label, double Confidence, ImmutableArray<string> Reasons,
    ImmutableArray<string> ProjectorIds, string? QuadProjector, ImmutableArray<Vec2d> Quad);

/// <summary>An assistant's naming of one region, after validation.</summary>
public sealed record SuggestionName(string Id, string Name, int Rank, string Reason);

/// <summary>
/// Safe surface suggestions from a scan. The candidates are the scan's measured regions (across projectors for a multi-view
/// scan), each judged from its own geometry (flatness, area, which projectors light it); ranking needs no model. An assistant, when
/// asked, gets text descriptors only (<see cref="SuggestionDescriptor"/>) and may only name and rank them through one tool: an id
/// it invents makes a grey "not measured" entry with no proposal; a name longer than 40 characters or carrying a verb's word is
/// dropped. The engine — never the assistant's tool loop — queues each suggestion's lines as proposals, so nothing reaches the
/// stage until a person approves it.
/// </summary>
public static class ScanSuggestions
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The tool the assistant answers through.</summary>
    public const string ToolName = "name_surfaces";

    public const int MaxNameLength = 40;

    /// <summary>The catalogue's verbs' first words: a name carrying one is not put in a line.</summary>
    private static readonly FrozenSet<string> VerbWords = VerbCatalog.All.Select(v => v.Name.Split(' ')[0]).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The catalogue's verbs of more than one word.</summary>
    private static readonly string[] VerbNames = [.. VerbCatalog.All.Select(v => v.Name).Where(n => n.Contains(' ', StringComparison.Ordinal))];

    /// <summary>The measured candidates of a scan, best first (confidence, then size, then id).</summary>
    public static IReadOnlyList<SuggestionCandidate> Candidates(ScanOutcome outcome, Project project)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(project);
        var list = outcome.Multi is { } multi ? FromMulti(multi, project) : FromSingle(outcome, project);
        return [.. list.OrderByDescending(c => c.Confidence).ThenByDescending(c => c.Descriptor.AreaM2).ThenBy(c => c.Descriptor.Id, StringComparer.Ordinal)];
    }

    private static List<SuggestionCandidate> FromMulti(MultiViewOutcome multi, Project project)
    {
        var named = MultiViewScan.Proposals(multi);
        var list = new List<SuggestionCandidate>();
        foreach (var region in multi.Regions.Where(r => MultiViewScan.Cells(r) >= MultiViewScan.MinRegionCells))
        {
            var proposal = named.GetValueOrDefault(region.Id);
            var main = region.Parts.MaxBy(p => p.Cells.Length)!;
            var projectors = new List<SuggestionProjector>();
            foreach (var part in region.Parts)
            {
                var valid = multi.DepthOf(part.ProjectorId)?.Valid ?? 0;
                projectors.Add(new SuggestionProjector(NameOf(project, part.ProjectorId), valid > 0 ? Math.Round(100.0 * part.Cells.Length / valid, 1) : 0));
            }
            var device = multi.Result.Projectors.FirstOrDefault(p => p.Id == main.ProjectorId);
            var toView = device is null ? 0 : AngleDeg(region.Normal, device.Pose.Position - region.Centroid);
            var area = proposal?.AreaSquareMetres ?? 0;
            var kind = region.Planar ? "plane" : "curved";
            var label = Label(proposal?.Name, region.Planar ? MultiViewScan.Kind(region.Normal) : "curved surface");
            var descriptor = new SuggestionDescriptor(region.Id, kind, Math.Round(area, 2), Math.Round(region.RmsDistance * 1000, 1), Math.Round(toView, 1),
                Math.Round(region.Centroid.Y, 2), [.. projectors], label);
            var confidence = Confidence(proposal?.Confidence ?? 0.5, MultiViewScan.Cells(region), region.Projectors.Length);
            ImmutableArray<Vec2d> quad = [];
            if (region.Planar && device is not null && multi.DepthOf(main.ProjectorId) is { } map)
                quad = QuadOf(main.Cells.Select(c => new Vec2d((c % map.Width + 0.5) * map.Stride, (c / map.Width + 0.5) * map.Stride)).ToList());
            list.Add(new SuggestionCandidate(descriptor, label, confidence, Reasons(descriptor, multi.Result.Views.Length), region.Projectors,
                quad.IsDefaultOrEmpty ? null : main.ProjectorId, quad));
        }
        return list;
    }

    private static List<SuggestionCandidate> FromSingle(ScanOutcome outcome, Project project)
    {
        var list = new List<SuggestionCandidate>();
        if (outcome.Geometry is not { } g)
            return list;
        var proposals = SurfaceSuggestion.Propose([.. outcome.Regions.Select(r => r.Mask)], g.Cloud);
        foreach (var region in outcome.Regions)
        {
            var proposal = proposals.FirstOrDefault(p => ReferenceEquals(p.Mask, region.Mask));
            var lit = region.Projectors.IsDefaultOrEmpty ? [outcome.Solution.Projectors.FirstOrDefault(p => p.Solved)?.ProjectorId ?? ""] : region.Projectors;
            var points = region.Mask.Coverage.Count(c => c >= 128);
            var main = lit[0];
            var solved = outcome.Solution.Projectors.FirstOrDefault(p => p.Solved && p.ProjectorId == main);
            var planar = proposal?.Plane is not null;
            var toView = proposal?.Plane is { } plane && solved is not null ? AngleDeg(plane.Normal, solved.Pose.Position - plane.Centroid) : 0;
            var descriptor = new SuggestionDescriptor(region.Id, planar ? "plane" : "curved", Math.Round(proposal?.AreaSquareMetres ?? 0, 2),
                Math.Round((proposal?.Plane?.Rms ?? 0) * 1000, 1), Math.Round(toView, 1), Math.Round(proposal is null ? 0 : (proposal.Min.Y + proposal.Max.Y) / 2, 2),
                [.. lit.Select(id => new SuggestionProjector(NameOf(project, id), 0))], Label(proposal?.Name, region.Label));
            ImmutableArray<Vec2d> quad = [];
            if (planar && solved is not null)
            {
                var device = new DeviceView(solved.Intrinsics, solved.Lens, solved.Pose);
                var seen = new List<Vec2d>();
                for (var i = 0; i < region.Mask.Coverage.Length; i++)
                {
                    if (region.Mask.Coverage[i] >= 128 && PointCloud.IsValid(g.Cloud.Points[i]) && device.Project(g.Cloud.Points[i]) is { } q
                        && q.X >= 0 && q.Y >= 0 && q.X < solved.Intrinsics.Width && q.Y < solved.Intrinsics.Height)
                        seen.Add(q);
                }
                quad = QuadOf(seen);
            }
            list.Add(new SuggestionCandidate(descriptor, descriptor.MeasuredLabel, Confidence(region.Confidence, points, lit.Length), Reasons(descriptor, 1),
                lit, quad.IsDefaultOrEmpty ? null : main, quad));
        }
        return list;
    }

    private static string NameOf(Project project, string projectorId) => project.FindProjector(projectorId)?.Name ?? projectorId;

    private static string Label(string? proposed, string fallback)
    {
        var name = proposed is { Length: > 0 } n ? n : fallback;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>How sure the geometry is: the region's own judgement, more for a larger region and for one more projectors light.</summary>
    private static double Confidence(double judged, int cells, int projectors) =>
        Math.Round(Math.Clamp(0.6 * judged + 0.3 * Math.Min(1, cells / 4000.0) + 0.05 * Math.Max(0, projectors - 1) + 0.05, 0, 1), 3);

    private static double AngleDeg(Vec3d a, Vec3d b)
    {
        if (a.Length < 1e-12 || b.Length < 1e-12)
            return 0;
        var c = Math.Abs(Vec3d.Dot(a.Normalized(), b.Normalized()));
        return Math.Acos(Math.Clamp(c, -1, 1)) * 180 / Math.PI;
    }

    private static ImmutableArray<string> Reasons(SuggestionDescriptor d, int views)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        reasons.Add(d.Kind == "plane"
            ? string.Create(Inv, $"flat: {d.PlanarityRmsMm:0.0} mm RMS over {d.AreaM2:0.##} m²")
            : string.Create(Inv, $"curved: {d.AreaM2:0.##} m², followed by the depth grid"));
        reasons.Add("lit by " + string.Join(" and ", d.Projectors.Select(p => p.CoveragePct > 0 ? string.Create(Inv, $"{p.Name} ({p.CoveragePct:0} % of its picture)") : p.Name)));
        reasons.Add(string.Create(Inv, $"faces the projector at {d.NormalToViewDeg:0}°; measured from {views} view{(views == 1 ? "" : "s")}"));
        return reasons.ToImmutable();
    }

    /// <summary>Four corners of a planar region in its projector's raster: the hull points furthest toward each corner (top-left,
    /// top-right, bottom-right, bottom-left), so a face seen square-on gives its own corners. Empty for fewer than four points.</summary>
    public static ImmutableArray<Vec2d> QuadOf(IReadOnlyList<Vec2d> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 4)
            return [];
        var tl = points.MinBy(p => p.X + p.Y);
        var br = points.MaxBy(p => p.X + p.Y);
        var tr = points.MaxBy(p => p.X - p.Y);
        var bl = points.MinBy(p => p.X - p.Y);
        ImmutableArray<Vec2d> quad = [tl, tr, br, bl];
        return quad.Distinct().Count() == 4 ? quad : [];
    }

    /// <summary>The lines a candidate proposes: SCAN SURFACE of its region (ON its main projector when it has several), and for a
    /// plane a SURFACE QUAD on its main projector in raster pixels.</summary>
    public static IReadOnlyList<string> Lines(string scanId, SuggestionCandidate c, string name)
    {
        ArgumentNullException.ThrowIfNull(scanId);
        ArgumentNullException.ThrowIfNull(c);
        var safe = SafeName(name) ?? SafeName(c.Label) ?? "Surface";
        var lines = new List<string>
        {
            c.ProjectorIds.Length > 1
                ? $"SCAN SURFACE {scanId} REGION {c.Descriptor.Id} ON {c.ProjectorIds[0]} {safe}"
                : $"SCAN SURFACE {scanId} REGION {c.Descriptor.Id} {safe}",
        };
        if (c.QuadProjector is { } pj && c.Quad.Length == 4)
        {
            var corners = string.Join(' ', c.Quad.Select(q => string.Create(Inv, $"{q.X:0.#} {q.Y:0.#}")));
            lines.Add($"SURFACE QUAD {pj} {corners} RASTER AS {safe}");
        }
        return lines;
    }

    /// <summary>A name fit for a line: letters, digits, spaces, hyphens and apostrophes, at most 40 characters, nothing that reads as a
    /// verb (a whole verb of the catalogue, or a verb's word in capitals); null otherwise.</summary>
    public static string? SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var trimmed = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (trimmed.Length > MaxNameLength || trimmed.Any(ch => !(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '\'')))
            return null;
        // A name that reads as a line is not a name: one holding a whole verb ("SCAN START"), or a verb's word in capitals; nor one
        // starting with ON, which SCAN SURFACE would read as the projector it names.
        if (trimmed.StartsWith("on ", StringComparison.OrdinalIgnoreCase))
            return null;
        var upper = " " + trimmed.ToUpperInvariant() + " ";
        if (VerbNames.Any(v => upper.Contains(" " + v + " ", StringComparison.Ordinal)))
            return null;
        return trimmed.Split(' ').Any(w => w.Length > 1 && w == w.ToUpperInvariant() && VerbWords.Contains(w)) ? null : trimmed;
    }

    /// <summary>The one request an assistant gets: the descriptors and the projectors' names as JSON text, a fixed instruction, and the
    /// naming tool. Nothing but the descriptors goes in.</summary>
    public static AssistantRequest Request(IReadOnlyList<SuggestionDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var json = JsonSerializer.Serialize(descriptors, ProjectJson.Compact);
        const string System = "You name surfaces a projection-mapping scan measured. You see only numbers about each region, never a picture. "
            + "Give each region a short name an operator would use (at most 40 characters, letters and spaces), a rank (1 is the most useful surface "
            + "to map content on) and one short reason. Use only the ids you are given. Answer only through the " + ToolName + " tool.";
        const string Schema = """{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"rank":{"type":"integer"},"reason":{"type":"string"}},"required":["id","name","rank","reason"],"additionalProperties":false}}},"required":["items"],"additionalProperties":false}""";
        return new AssistantRequest(System, [AssistantMessage.User(new TextPart("Regions measured by the scan:\n" + json))],
            [new ToolSpec(ToolName, "Names and ranks the measured regions by their ids.", Schema)]);
    }

    /// <summary>The assistant's answer read and checked: the naming tool's items, each id as given (the caller tells measured from not),
    /// a name that is not safe for a line replaced by "" (the measured name stands), a reason cut to 120 characters.</summary>
    public static IReadOnlyList<SuggestionName> Read(AssistantReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var names = new List<SuggestionName>();
        foreach (var call in reply.Parts.OfType<ToolCallPart>().Where(c => c.Name == ToolName))
        {
            try
            {
                using var doc = JsonDocument.Parse(call.ArgumentsJson);
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                        continue;
                    var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? SafeName(n.GetString()) ?? "" : "";
                    var rank = item.TryGetProperty("rank", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var k) ? k : int.MaxValue;
                    var reason = item.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() ?? "" : "";
                    names.Add(new SuggestionName(id.GetString()!, name, rank, reason.Length > 120 ? reason[..120] : reason));
                }
            }
            catch (JsonException)
            {
                // A tool call whose arguments are not JSON names nothing: the measured names stand.
                continue;
            }
        }
        return names;
    }
}
