using System.Collections.Immutable;
using System.Globalization;
using PlayMap.Assistant;
using PlayMap.Core.Bus;
using PlayMap.Core.Calibration;
using PlayMap.Core.Model;
using PlayMap.Core.Vision;
using PlayMap.Core.Wire;
using PlayMap.Engine.Assistant;
using PlayMap.Engine.Devices;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine;

/// <summary>wp2-multiscan's verbs (routed from the phone hook, EngineCore.Phone.cs): SCAN SOLVE, SCAN VIEWS, SCAN DROP, SCAN SUGGEST,
/// and SCAN SURFACE's ON form and multi-view scans (a single-view scan without ON goes to the first round's SCAN SURFACE).</summary>
public sealed partial class EngineCore
{
    private void RouteMultiscan(VerbRoutes routes)
    {
        routes.OffLane("SCAN SOLVE", ScanSolveAsync);
        routes.OffLane("SCAN VIEWS", ScanViewsAsync);
        routes.OffLane("SCAN DROP", ScanDropAsync);
        routes.OffLane("SCAN SUGGEST", ScanSuggestAsync);
    }

    private ScanRunner? Runner => Services.Scan as ScanRunner;

    /// <summary>SCAN SOLVE &lt;scan&gt; [HOLD &lt;projector&gt;…]: every view of the scan solved together again; HOLD keeps those projectors at
    /// their stage poses (re-solve only the projector that moved). Answers once the solve is kept and recorded.</summary>
    private async Task<CommandReply> ScanSolveAsync(VerbCall call)
    {
        const string Syntax = "SCAN SOLVE <scan> [HOLD <projector>…]";
        var args = call.Args;
        if (args.Count < 1 || (args.Count > 1 && !args[1].Equals("HOLD", StringComparison.OrdinalIgnoreCase)) || args.Count == 2)
            return CommandReply.Fail(Syntax);
        if (Runner is not { } runner)
            return CommandReply.Fail("this node's scans are not the scan runner's: SCAN SOLVE needs it");
        var hold = new List<string>();
        foreach (var word in args.Skip(2))
        {
            if (FindProjectorWord(Store.Project, word) is not { } pj)
                return CommandReply.Fail($"no projector {word}");
            hold.Add(pj.Id);
        }
        try
        {
            var outcome = await runner.SolveAsync(args[0], hold, call.Ct).ConfigureAwait(false);
            return Solved(outcome, hold.Count > 0
                ? $"solved {outcome.Name} again holding {string.Join(", ", hold)}"
                : $"solved {outcome.Name} again from every view");
        }
        catch (InvalidOperationException ex)
        {
            return CommandReply.Fail(ex.Message);
        }
    }

    /// <summary>SCAN DROP &lt;scan&gt; &lt;view&gt;: the scan solved again without that view.</summary>
    private async Task<CommandReply> ScanDropAsync(VerbCall call)
    {
        if (call.Args.Count != 2)
            return CommandReply.Fail("SCAN DROP <scan> <view>");
        if (Runner is not { } runner)
            return CommandReply.Fail("this node's scans are not the scan runner's: SCAN DROP needs it");
        var before = runner.Find(call.Args[0])?.Multi?.Result.RmsPx;
        try
        {
            var outcome = await runner.DropAsync(call.Args[0], call.Args[1], call.Ct).ConfigureAwait(false);
            var after = outcome.Multi!.Result.RmsPx;
            return Solved(outcome, before is { } b
                ? string.Create(CultureInfo.InvariantCulture, $"dropped {call.Args[1]} from {outcome.Name}: {b:0.00} → {after:0.00} px")
                : string.Create(CultureInfo.InvariantCulture, $"dropped {call.Args[1]} from {outcome.Name}: {after:0.00} px"));
        }
        catch (InvalidOperationException ex)
        {
            return CommandReply.Fail(ex.Message);
        }
    }

    private CommandReply Solved(ScanOutcome outcome, string effect) => CommandReply.Success(ViewsJson(outcome), effect + ". " + outcome.Solution.Words);

    /// <summary>SCAN VIEWS &lt;scan&gt;: the scan's views with their residuals, tracks, pairing and state, coverage per projector, and the
    /// view that disagrees with the rest when one does.</summary>
    private async Task<CommandReply> ScanViewsAsync(VerbCall call)
    {
        if (call.Args.Count != 1)
            return CommandReply.Fail("SCAN VIEWS <scan>");
        if (Store.Project.Stage.Scans.Find(call.Args[0]) is not { } record)
            return CommandReply.Fail($"no scan {call.Args[0]}");
        var outcome = await Task.Run(() => OutcomeOf(record), call.Ct).ConfigureAwait(false);
        if (outcome is null)
            return CommandReply.Fail(NoData(record));
        if (outcome.Captured.IsDefaultOrEmpty)
            return CommandReply.Fail($"{record.Name} keeps no views (it was made before scans kept them)");
        return CommandReply.Success(ViewsJson(outcome));
    }

    private string ViewsJson(ScanOutcome outcome) => JsonOut(w =>
    {
        w.WriteStartObject();
        w.WriteString("scan", outcome.Id);
        w.WriteStartArray("views");
        foreach (var v in outcome.Captured)
            ScanRunner.WriteView(w, v, outcome.Id);
        w.WriteEndArray();
        w.WriteStartArray("coverage");
        foreach (var c in outcome.Multi?.Coverage ?? [])
        {
            w.WriteStartObject();
            w.WriteString("projector", c.ProjectorId);
            w.WriteNumber("seen", Math.Round(c.Seen, 4));
            w.WriteNumber("twice", Math.Round(c.Twice, 4));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteNumber("rmsPx", Math.Round(outcome.Multi?.Result.RmsPx ?? outcome.WorstResidualPx, 4));
        w.WriteStartArray("held");
        foreach (var h in outcome.Multi?.Held ?? [])
            w.WriteStringValue(h);
        w.WriteEndArray();
        if (outcome.Multi?.Disagreeing is { } bad)
            w.WriteString("disagreeing", bad);
        else
            w.WriteNull("disagreeing");
        w.WriteBoolean("metric", outcome.Solution.Metric);
        w.WriteString("words", outcome.Solution.Words);
        w.WriteEndObject();
    });

    /// <summary>SCAN SUGGEST &lt;scan&gt; [ASSISTANT]: the scan's measured regions ranked with their reasons, each queued by the engine as
    /// proposals on the node's own list (SCAN SURFACE, and SURFACE QUAD for a plane) that only a person's APPROVE runs. ASSISTANT asks
    /// the node's assistant to name and rank them from text descriptors only. A later SCAN SUGGEST of the same scan replaces the
    /// suggestions it made before (their proposals still waiting are rejected with those words).</summary>
    private async Task<CommandReply> ScanSuggestAsync(VerbCall call)
    {
        var args = call.Args;
        var ask = args.Count == 2 && args[1].Equals("ASSISTANT", StringComparison.OrdinalIgnoreCase);
        if (args.Count is < 1 or > 2 || (args.Count == 2 && !ask))
            return CommandReply.Fail("SCAN SUGGEST <scan> [ASSISTANT]");
        if (Runner is not { } runner)
            return CommandReply.Fail("this node's scans are not the scan runner's: SCAN SUGGEST needs it");
        if (Services.Assistant is not { } assistant)
            return CommandReply.Fail("this node keeps no approval list (it has no assistant service): suggestions wait for a person there");
        // An assistant's own line never asks the assistant, nor replaces suggestions a person has not decided yet.
        if (ask && IsAssistantTransport(call.Session))
            return CommandReply.Fail("an assistant's line cannot ask the assistant: SCAN SUGGEST without ASSISTANT ranks the regions itself");
        if (Store.Project.Stage.Scans.Find(args[0]) is not { } record)
            return CommandReply.Fail($"no scan {args[0]}");
        if (IsAssistantTransport(call.Session) && runner.SuggestionsFor(record.Id).Any(x => runner.StateOf(x) == "pending"))
            return CommandReply.Fail($"{record.Name}'s suggestions wait for a person: a person decides them, or asks for new ones");
        if (!runner.BeginSuggest(record.Id))
            return CommandReply.Fail($"{record.Name}'s suggestions are being made already, or the node is stopping");
        try
        {
            return await SuggestAsync(call, runner, assistant, record, ask).ConfigureAwait(false);
        }
        finally
        {
            runner.EndSuggest(record.Id);
        }
    }

    private async Task<CommandReply> SuggestAsync(VerbCall call, ScanRunner runner, AssistantService assistant, ScanRecord record, bool ask)
    {
        var outcome = await Task.Run(() => OutcomeOf(record), call.Ct).ConfigureAwait(false);
        if (outcome is null)
            return CommandReply.Fail(NoData(record));
        var project = Store.Project;
        var candidates = await Task.Run(() => ScanSuggestions.Candidates(outcome, project), call.Ct).ConfigureAwait(false);
        if (candidates.Count == 0)
            return CommandReply.Fail($"{record.Name} measured no region worth a surface — add a view (SCAN START <camera> INTO {record.Id}) or click on its smart scan (SCAN SURFACE)");

        IReadOnlyList<SuggestionName> names = [];
        var from = "scan suggestions";
        if (ask)
        {
            var settings = assistant.Settings;
            if (!settings.Enabled || settings.Provider == AssistantProviderKind.None)
                return CommandReply.Fail("the assistant is off on this node — SCAN SUGGEST without ASSISTANT ranks the regions itself");
            var key = assistant.Credentials.Find(NodeCredentialStore.AssistantKey)?.Secret;
            var make = runner.Options.SuggestModel ?? ((s, k) => AssistantProviders.Create(s, k));
            using var model = make(settings, string.IsNullOrEmpty(key) ? null : key);
            if (model is null)
                return CommandReply.Fail("no model could be reached with the assistant's settings — SCAN SUGGEST without ASSISTANT ranks the regions itself");
            AssistantReply reply;
            try
            {
                reply = await model.CompleteAsync(ScanSuggestions.Request([.. candidates.Select(c => c.Descriptor)]), null, call.Ct).ConfigureAwait(false);
            }
            catch (AssistantProviderException ex)
            {
                return CommandReply.Fail("the assistant could not name the regions (" + ex.Message + ") — SCAN SUGGEST without ASSISTANT ranks them itself");
            }
            names = ScanSuggestions.Read(reply);
            from = $"assistant ({model.Model}) via scan suggestions";
        }

        // The assistant's ranking orders the measured candidates it named; the rest keep the geometry's order after them.
        var byId = names.Where(n => candidates.Any(c => c.Descriptor.Id == n.Id)).GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var ordered = candidates.Select((c, i) => (c, i)).OrderBy(x => byId.TryGetValue(x.c.Descriptor.Id, out var n) ? n.Rank : int.MaxValue).ThenBy(x => x.i)
            .Select(x => x.c).ToList();
        runner.WithdrawSuggestions(this, record.Id, "replaced by a newer SCAN SUGGEST");
        var list = ImmutableArray.CreateBuilder<ScanSuggestion>();
        var spec = VerbCatalog.Find("SCAN SURFACE")!;
        var quadSpec = VerbCatalog.Find("SURFACE QUAD");
        var stamp = Services.Wall.UnixMs.ToString(CultureInfo.InvariantCulture);
        foreach (var c in ordered)
        {
            var named = byId.GetValueOrDefault(c.Descriptor.Id);
            var name = named is { Name.Length: > 0 } ? named.Name : c.Label;
            var reasons = c.Reasons;
            if (named is { Reason.Length: > 0 })
                reasons = reasons.Add("the assistant: " + named.Reason);
            var id = "sg" + (list.Count + 1).ToString(CultureInfo.InvariantCulture);
            var proposals = ImmutableArray.CreateBuilder<string>();
            var lines = ScanSuggestions.Lines(record.Id, c, name);
            for (var k = 0; k < lines.Count; k++)
            {
                var lineSpec = k == 0 ? spec : quadSpec;
                if (lineSpec is null)
                    continue;
                // One batch per line: a person approves any of them in any order, and each alone.
                var p = await assistant.Queue.EnqueueAsync(new ProposalDraft(lines[k], lineSpec, string.Join("; ", reasons), from, call.Session.SessionId,
                    $"suggest/{record.Id}/{stamp}/{id}/{k}", 0), call.Ct).ConfigureAwait(false);
                proposals.Add(p.Id);
            }
            list.Add(new ScanSuggestion(id, c.Descriptor.Id, name, c.Descriptor.Kind, c.Confidence, reasons, true, c.ProjectorIds, proposals.ToImmutable()));
        }
        // A name for an id the geometry did not find: shown grey, never a proposal.
        foreach (var stray in names.Where(n => !candidates.Any(c => c.Descriptor.Id == n.Id)).GroupBy(n => n.Id).Select(g => g.First()))
        {
            var id = "sg" + (list.Count + 1).ToString(CultureInfo.InvariantCulture);
            list.Add(new ScanSuggestion(id, "", stray.Name.Length > 0 ? stray.Name : "(unnamed)", "unknown", 0, ["AI only — not measured"], false, [], []));
        }
        var suggestions = list.ToImmutable();
        runner.Suggested(record.Id, suggestions);
        runner.Show(outcome, $"{suggestions.Count(s => s.Measured)} surface suggestion(s) wait for a person — APPROVE or REJECT each");
        var json = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("scan", record.Id);
            w.WriteString("from", from);
            w.WriteStartArray("suggestions");
            foreach (var s in suggestions)
                ScanRunner.WriteSuggestion(w, s, runner.StateOf(s), record.Id);
            w.WriteEndArray();
            w.WriteEndObject();
        });
        return CommandReply.Success(json, string.Create(CultureInfo.InvariantCulture,
            $"{suggestions.Count(s => s.Measured)} surface suggestion(s) for {record.Name} queued for a person: {string.Join(", ", suggestions.Where(s => s.Measured).Select(s => s.Label))}"));
    }

    /// <summary>SCAN SURFACE's ON form and a multi-view scan's surfaces (the first round's handler asks here first): the reply, or
    /// null for a single-view scan without ON, which the first round's handler answers.</summary>
    private async Task<CommandReply?> ScanSurfaceMultiscanAsync(List<string> words, WireLine line, SessionInfo session, CancellationToken ct)
    {
        var args = new List<string>(words);
        string? on = null;
        // ON is the keyword only when a projector follows it: "On stage left" stays a name.
        if (args.Count >= 5 && args[3].Equals("ON", StringComparison.OrdinalIgnoreCase) && FindProjectorWord(Store.Project, args[4]) is not null)
        {
            on = args[4];
            args.RemoveRange(3, 2);
        }
        if (args.Count < 3 || Store.Project.Stage.Scans.Find(args[0]) is not { } record)
            return null;
        var outcome = await Task.Run(() => OutcomeOf(record), ct).ConfigureAwait(false);
        if (outcome is null || (outcome.Multi is null && on is null))
            return null;
        var call = new VerbCall(VerbCatalog.Find("SCAN SURFACE")!, args, line, session, ct);
        string? projectorId = null;
        if (on is not null)
        {
            var pj = FindProjectorWord(Store.Project, on)!;
            if (!outcome.Solution.Projectors.Any(p => p.Solved && p.ProjectorId == pj.Id))
                return CommandReply.Fail($"{record.Name} did not solve {pj.Name}: ON names a projector the scan placed");
            projectorId = pj.Id;
        }
        var byRegion = args[1].Equals("REGION", StringComparison.OrdinalIgnoreCase);
        double x = 0, y = 0;
        if (!byRegion && (!double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            || !double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y) || x is < 0 or > 1 || y is < 0 or > 1))
            return CommandReply.Fail("SCAN SURFACE <scan> <x> <y> [ON <projector>] [<name>] — x and y from 0 to 1 across and down the smart scan — or SCAN SURFACE <scan> REGION <region> [ON <projector>] [<name>]");
        var given = string.Join(' ', args.Skip(3)).Trim();
        if (!outcome.Solution.Projectors.Any(p => p.Solved))
            return CommandReply.Fail($"{record.Name} placed no projector: there is no smart scan to select from");
        return outcome.Multi is { } multi
            ? await MultiViewSurfaceAsync(call, record, outcome, multi, byRegion ? args[2] : null, x, y, projectorId, given).ConfigureAwait(false)
            : await SingleViewSurfaceOnAsync(call, record, outcome, byRegion ? args[2] : null, x, y, projectorId!, given).ConfigureAwait(false);
    }

    private async Task<CommandReply> MultiViewSurfaceAsync(VerbCall call, ScanRecord record, ScanOutcome outcome, MultiViewOutcome multi, string? regionId,
        double x, double y, string? on, string given)
    {
        Core.Scan.MultiView.MultiProjectorRegion region;
        if (regionId is not null)
        {
            if (multi.Regions.FirstOrDefault(r => r.Id.Equals(regionId, StringComparison.OrdinalIgnoreCase)) is not { } found)
                return CommandReply.Fail($"{record.Name} has no region {regionId} — STATE.scan.regions lists them");
            region = found;
        }
        else
        {
            var pj = on ?? outcome.Solution.Projectors.First(p => p.Solved).ProjectorId;
            if (MultiViewScan.RegionAt(multi, pj, x, y) is not { } found)
                return CommandReply.Fail($"{NameOfProjector(pj)} lights nothing {record.Name} measured there — click on the scanned picture");
            region = found;
        }
        if (on is not null && !region.Projectors.Contains(on, StringComparer.Ordinal))
            return CommandReply.Fail($"{NameOfProjector(on)} does not light {region.Id}: it is lit by {string.Join(", ", region.Projectors.Select(NameOfProjector))}");
        var faces = MultiViewScan.Faces(multi, region, on);
        if (faces.Length == 0)
            return CommandReply.Fail($"{region.Id} covers no whole triangle of the scan mesh{(on is null ? "" : $" that {NameOfProjector(on)} lights")}");
        var shown = outcome.Regions.FirstOrDefault(r => r.Id == region.Id);
        var name = given.Length > 0 ? given : shown is { } s ? char.ToUpperInvariant(s.Label[0]) + s.Label[1..] : "Scanned surface";
        var tags = new List<string> { "scan" };
        if (shown is not null)
            tags.AddRange(shown.Tags.Where(t => !tags.Contains(t, StringComparer.Ordinal)));
        var kind = region.Planar ? MultiViewScan.Kind(region.Normal) : "curved";
        return await MakeScanSurfaceAsync(call, record, outcome, name, faces, _ => [], tags, kind).ConfigureAwait(false);
    }

    private async Task<CommandReply> SingleViewSurfaceOnAsync(VerbCall call, ScanRecord record, ScanOutcome outcome, string? regionId, double x, double y,
        string on, string given)
    {
        if (outcome.Geometry is not { } g)
            return CommandReply.Fail($"{record.Name} has no scanned surface to select from — nothing was triangulated");
        var solved = outcome.Solution.Projectors.First(p => p.Solved && p.ProjectorId == on);
        var device = new DeviceView(solved.Intrinsics, solved.Lens, solved.Pose);
        SegmentationMask mask;
        if (regionId is not null)
        {
            if (outcome.Regions.FirstOrDefault(r => r.Id.Equals(regionId, StringComparison.OrdinalIgnoreCase)) is not { } region)
                return CommandReply.Fail($"{record.Name} suggests no region {regionId} — STATE.scan.regions lists them");
            if (!region.Projectors.IsDefaultOrEmpty && !region.Projectors.Contains(on, StringComparer.Ordinal))
                return CommandReply.Fail($"{solved.Name} does not light {region.Id}: it is lit by {string.Join(", ", region.Projectors.Select(NameOfProjector))}");
            mask = region.Mask;
        }
        else
        {
            if (ScanSurfaces.CameraPixelAt(g, device, x, y) is not { } pixel)
                return CommandReply.Fail($"{solved.Name} lights nothing the scan reached there — click on its scanned picture");
            var segmenter = Runner?.Segmenter ?? new Vision.Segmentation.SegmenterRouter([], new Vision.Segmentation.ClassicalSegmenter(), null);
            var result = await segmenter.SegmentAsync(new VisionFrame(g.Image, g.Cloud), SegmentationPrompt.Click(pixel.X, pixel.Y), call.Ct).ConfigureAwait(false);
            if (result.Best is not { } best)
                return CommandReply.Fail("nothing to select there — " + (result.Words.Length > 0 ? result.Words : "the segmenter found no region"));
            mask = best;
        }
        // What that projector lights of it: the points in its picture.
        var coverage = new byte[mask.Coverage.Length];
        var k = device.Intrinsics;
        for (var i = 0; i < coverage.Length; i++)
        {
            if (mask.Coverage[i] >= 128 && Core.Geometry.PointCloud.IsValid(g.Cloud.Points[i]) && device.Project(g.Cloud.Points[i]) is { } q
                && q.X >= 0 && q.Y >= 0 && q.X < k.Width && q.Y < k.Height)
                coverage[i] = 255;
        }
        var lit = new SegmentationMask(mask.Width, mask.Height, coverage, mask.Score, mask.Label);
        var faces = ScanSurfaces.Faces(g, lit);
        if (faces.Length == 0)
            return CommandReply.Fail($"the region there covers no whole triangle of the scan mesh that {solved.Name} lights — click nearer its middle");
        var proposals = Vision.Surfaces.SurfaceSuggestion.Propose([lit], g.Cloud);
        var proposal = proposals.Count > 0 ? proposals[0] : null;
        var name = given.Length > 0 ? given : proposal?.Name is { Length: > 0 } guess ? char.ToUpperInvariant(guess[0]) + guess[1..] : "Scanned surface";
        var tags = new List<string> { "scan" };
        if (proposal is not null)
            tags.AddRange(proposal.Tags.Where(t => !tags.Contains(t, StringComparer.Ordinal)));
        var kind = proposal?.Plane is null ? "curved" : tags.Contains("floor") ? "floor" : tags.Contains("wall") ? "wall" : "top";
        return await MakeScanSurfaceAsync(call, record, outcome, name, faces, fresh => ScanSurfaces.Masks(lit, fresh), tags, kind).ConfigureAwait(false);
    }

    /// <summary>The surface made, one undoable edit: the scan's mesh joins the stage when it is not there yet (written beside the project),
    /// and the surface holds its faces (and masks, when the mesh's UVs are the camera's grid). Answers its id.</summary>
    private async Task<CommandReply> MakeScanSurfaceAsync(VerbCall call, ScanRecord record, ScanOutcome outcome, string name, int[] faces,
        Func<Func<string>, ImmutableArray<Mask>> masksOf, List<string> tags, string kind)
    {
        if (ProjectFolderForFiles is not { } folder)
            return CommandReply.Fail("the project has no folder yet — PROJECT SAVE it first: the scan's mesh is written beside it");
        MeshAsset? written = null;
        // The stage's mesh must be the one these faces index: a scan solved again since (and its record taken back by UNDO) is not.
        if (ScanMesh(Store.Project, record) is { } onStage && outcome.Solution.Mesh is { } now
            && (onStage.Triangles != now.TriangleCount || onStage.Vertices != now.VertexCount))
            return CommandReply.Fail($"{record.Name} was solved again since its mesh joined the stage — SCAN APPLY {record.Id} puts the new one on first");
        if (ScanMesh(Store.Project, record) is null)
        {
            if (outcome.Solution.Mesh is not { } mesh)
                return CommandReply.Fail($"{record.Name} has no mesh");
            try
            {
                var meshId = Store.Project.NewId(Ids.Mesh);
                written = await Task.Run(() => WriteScanMesh(folder, record, meshId, mesh), call.Ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return CommandReply.Fail($"the scan's mesh could not be written beside the project: {ex.Message}");
            }
        }
        return await _lane.RunAsync(() =>
        {
            string surfaceId = "";
            var paths = 0;
            var r = EditsFor(call.Session).Apply($"surface {name} from {record.Name}", p =>
            {
                var current = p.Stage.Scans.Find(record.Id) ?? throw new EditRefusedException($"no scan {record.Id}");
                var asset = ScanMesh(p, current) ?? written ?? throw new EditRefusedException($"{record.Name}'s mesh is not on the stage");
                var taken = new HashSet<string>(p.AllIds(), StringComparer.Ordinal) { asset.Id };
                string Fresh(string prefix)
                {
                    string id;
                    do
                        id = Ids.New(prefix);
                    while (!taken.Add(id));
                    return id;
                }
                surfaceId = Fresh(Ids.Surface);
                var masks = masksOf(() => Fresh(Ids.Mask));
                paths = masks.Length;
                var surface = new Surface(surfaceId, name, asset.Id, Transform3.Identity, [.. faces], SurfaceContent.Empty, 1, masks, true, false,
                    PlaneColour(kind), [.. tags]);
                return p.WithStage(st => st with
                {
                    Meshes = st.Meshes.Find(asset.Id) is null ? st.Meshes.Add(asset) : st.Meshes,
                    Surfaces = st.Surfaces.Add(surface),
                    Scans = st.Scans.Update(record.Id, sc => sc.MeshId == asset.Id ? sc : sc with { MeshId = asset.Id }),
                });
            });
            return r.Changed
                ? CommandReply.Success(surfaceId, string.Create(CultureInfo.InvariantCulture,
                    $"made surface {name} ({surfaceId}): {faces.Length:N0} triangles of {record.Name}{(paths > 0 ? $", outlined by {paths} mask path{(paths == 1 ? "" : "s")}" : "")}"))
                : FromEdit(r);
        }).ConfigureAwait(false);
    }

    private string NameOfProjector(string id) => Store.Project.FindProjector(id)?.Name ?? id;

    /// <summary>A projector by id or name.</summary>
    private static Projector? FindProjectorWord(Project p, string word) =>
        p.FindProjector(word) ?? p.Stage.Projectors.FirstOrDefault(x => string.Equals(x.Name, word, StringComparison.OrdinalIgnoreCase));
}
