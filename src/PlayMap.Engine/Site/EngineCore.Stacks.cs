using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Align;
using PlayMap.Core.Bus;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;
using PlayMap.Core.Stacks;
using PlayMap.Core.Wire;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Wall;

namespace PlayMap.Engine;

/// <summary>
/// Stacks (facade §3.9): projectors lighting one picture. STACK NEW groups them (the 3D blend treats the group as one projector, the
/// members' light adding), STACK LIST tells them; STACK ALIGN fits each member's measured residual lattice to the master's picture
/// from a scan that saw every member through one camera, and STACK MATCH brings each member's white to the master's from the same
/// scan's colour white frames. Both land on the members' outputs as their measured correction (the lattice drawn as the warp's
/// first layer, the gains by the output stage) through the calibration ledger, in one edit, the correction each had kept as its
/// fallback. They are mapping edits: from the session holding the blind draft they stage into it; a stopping node refuses them.
/// </summary>
public sealed partial class EngineCore
{
    /// <summary>A member covering less of the master's picture than this is not stacked on it.</summary>
    private const double StackShareMin = 0.8;

    /// <summary>A stack whose worst member is farther from the master than this (px) is amber.</summary>
    internal const double StackResidualMaxPx = 0.5;

    private void RouteStacks(VerbRoutes routes)
    {
        routes.Lane("STACK NEW", c => StackNew(c.Args));
        routes.Lane("STACK LIST", _ => CommandReply.Success(JsonOut(w => WriteStacks(w, Store.Project, list: true))));
        routes.OffLane("STACK ALIGN", StackAlignAsync);
        routes.OffLane("STACK MATCH", StackMatchAsync);
        routes.Lane("STACK FAILOVER", c => StackFailoverVerb(c.Args));
        routes.OffLane("PLAN LIGHT", PlanLightAsync);
        routes.Index(new Site.SiteIndex(this));
    }

    internal static StackGroup? StackNamed(Project p, string word) =>
        p.FindStack(word) ?? p.Stage.Stacks.FirstOrDefault(s => s.Name.Equals(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The output that draws a projector's picture (enabled first), or null.</summary>
    internal static Output? OutputOf(Project p, string projectorId) =>
        p.Outputs.Where(o => o.Mode == OutputMode.Projector && o.ProjectorId == projectorId).OrderByDescending(o => o.Enabled).FirstOrDefault();

    // ---- STACK NEW ---------------------------------------------------------------------------------------------------------

    /// <summary>STACK NEW &lt;name&gt; &lt;projector&gt; &lt;projector&gt; [&lt;projector&gt;…] [MASTER &lt;projector&gt;]. A member in another stack, named
    /// twice, or lighting less than 80 % of the master's picture (where the master's raster lands on the stage, or on a wall at its
    /// throw when there is no model) is refused.</summary>
    private CommandReply StackNew(List<string> args)
    {
        const string Usage = "STACK NEW <name> <projector> <projector> [<projector>…] [MASTER <projector>]";
        var words = args.ToList();
        var masterWord = TakeOption(words, "MASTER");
        if (words.Count < 3 || masterWord == "")
            return CommandReply.Fail(Usage);
        var p = Edits.Project;
        var name = words[0];
        if (p.Stage.Stacks.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail($"there is a stack {name} already — STACK LIST names them");
        var members = new List<Projector>();
        foreach (var word in words.Skip(1))
        {
            if (ProjectorNamed(p, word) is not { } pj)
                return CommandReply.Fail($"no projector {word}");
            if (members.Any(m => m.Id == pj.Id))
                return CommandReply.Fail($"{pj.Name} is named twice");
            if (p.Stage.Stacks.FirstOrDefault(s => s.Members.Contains(pj.Id, StringComparer.Ordinal)) is { } other)
                return CommandReply.Fail($"{pj.Name} is in stack {other.Name} already — REMOVE {other.Id} first, or name other projectors");
            members.Add(pj);
        }
        var master = members[0];
        if (masterWord is not null)
        {
            if (ProjectorNamed(p, masterWord) is not { } m || members.All(x => x.Id != m.Id))
                return CommandReply.Fail($"the master must be one of the members ({string.Join(", ", members.Select(x => x.Name))})");
            master = m;
        }
        var scene = StackScene(p);
        foreach (var pj in members.Where(x => x.Id != master.Id))
        {
            var share = SharedShare(master, pj, scene);
            if (share < StackShareMin)
                return CommandReply.Fail(string.Create(CultureInfo.InvariantCulture,
                    $"{pj.Name} lights {share * 100:0} % of {master.Name}'s picture — a stack shares one area; blend them instead (BLEND SOLVE)"));
        }
        var notes = new List<string>();
        foreach (var pj in members)
            if (OutputOf(p, pj.Id) is null)
                notes.Add($"{pj.Name} drives no output in Projector mode yet: its correction lands on the output that does");
        var id = "";
        var r = Edits.Apply($"stack {name}", project =>
        {
            id = project.NewId(Ids.Stack);
            var group = new StackGroup(id, name, [.. members.Select(m => m.Id)], master.Id, StackFailover.Hold);
            return project.WithStage(st => st with { Stacks = st.Stacks.Add(group) });
        });
        if (!r.Changed)
            return FromEdit(r);
        var said = Staged(EditingBlind, $"{name} ({id}) stacks {string.Join(", ", members.Select(m => m.Name))} on {master.Name}: the blend treats them as one projector"
            + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "") + $" — STACK ALIGN {id} FROM <scan> fits them to {master.Name}");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteStartArray("members");
            foreach (var m in members)
                w.WriteStringValue(m.Id);
            w.WriteEndArray();
            w.WriteString("master", master.Id);
            w.WriteBoolean("staged", EditingBlind);
            w.WriteString("words", said);
            w.WriteEndObject();
        }), said);
    }

    /// <summary>The stage's visible model for the stack checks, or null when none loads.</summary>
    private StageScene? StackScene(Project p)
    {
        try
        {
            var scene = StageScene.Build(p.Stage, Meshes.For(p.Stage));
            return scene.World.TriangleCount > 0 ? scene : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or NotSupportedException or InvalidDataException)
        {
            // A model that does not load leaves the checks on a wall at the master's throw.
            return null;
        }
    }

    /// <summary>The share of the master's picture (a 9 × 9 grid of its raster) the member's raster also covers: each ray lands on the
    /// stage, or on a plane across the master's axis at its throw (10 m) without a model.</summary>
    private static double SharedShare(Projector master, Projector member, StageScene? scene)
    {
        var mv = DeviceView.Of(master);
        var jv = DeviceView.Of(member);
        var forward = master.Pose.Forward;
        var plane = master.Pose.Position + forward * 10;
        int hit = 0, total = 0;
        for (var j = 0; j <= 8; j++)
            for (var i = 0; i <= 8; i++)
            {
                var px = new Vec2d((0.05 + 0.9 * i / 8) * master.Intrinsics.Width, (0.05 + 0.9 * j / 8) * master.Intrinsics.Height);
                var ray = mv.Ray(px);
                Vec3d? at = scene?.Raycaster.Cast(ray)?.Point;
                if (at is null)
                {
                    var den = Vec3d.Dot(ray.Direction, forward);
                    if (Math.Abs(den) < 1e-9)
                        continue;
                    at = ray.At(Vec3d.Dot(plane - ray.Origin, forward) / den);
                }
                total++;
                if (jv.Project(at.Value) is { } q && CameraModel.InRaster(q, member.Intrinsics))
                    hit++;
            }
        return total == 0 ? 0 : hit / (double)total;
    }

    // ---- STACK ALIGN, STACK MATCH --------------------------------------------------------------------------------------------

    /// <summary>A stack, a scan that saw its members, the view of the scan their pairs come from ("" for a scan that keeps no views),
    /// and each member's view and output, as read on the lane; or the words why not.</summary>
    private sealed record StackSource(Project Project, bool Staged, StackGroup Stack, ScanRecord Scan, ScanOutcome Outcome, string ViewId,
        IReadOnlyList<(Projector Projector, Output Output, ProjectorView View)> Members, int Master);

    private async Task<(StackSource? Source, string? Why)> StackSourceAsync(VerbCall c, string usage)
    {
        var args = c.Args.ToList();
        var from = TakeOption(args, "FROM");
        var grid = TakeOption(args, "GRID");
        if (args.Count != 1 || from is null or "" || grid == "")
            return (null, usage);
        var (p, staged) = await _lane.RunAsync(() =>
        {
            var store = EditsFor(c.Session);
            return (store.Project, !ReferenceEquals(store, Store));
        }).ConfigureAwait(false);
        if (StackNamed(p, args[0]) is not { } stack)
            return (null, $"no stack {args[0]} — STACK NEW makes one");
        var scan = p.Stage.Scans.Find(from) ?? p.Stage.Scans.FirstOrDefault(s => s.Name.Equals(from, StringComparison.OrdinalIgnoreCase));
        if (scan is null)
            return (null, $"no scan {from} — SCAN START <camera> {string.Join(" ", stack.Members)} scans every member through one camera");
        if (OutcomeOf(scan) is not { } outcome)
            return (null, NoData(scan));
        if (stack.Members.FirstOrDefault(id => p.FindProjector(id) is null) is { } gone)
            return (null, $"{stack.Name}'s member {gone} is gone — REMOVE {stack.Id} and STACK NEW again");
        var (views, viewId, unseen) = StackViews(outcome, stack, scan, p);
        if (unseen is not null)
            return (null, unseen);
        var members = new List<(Projector, Output, ProjectorView)>();
        var master = -1;
        foreach (var id in stack.Members)
        {
            var pj = p.FindProjector(id)!;
            var view = views.GetValueOrDefault(id);
            if (view is null)
                return (null, outcome.Views.Count == 0
                    ? $"{scan.Name} keeps no projector views on this node (they are held in memory until the node stops) — scan the stack again: SCAN START {outcome.CameraId} {string.Join(" ", stack.Members)}"
                    : $"{scan.Name} did not scan {pj.Name} — every member must be scanned through one camera: SCAN START {outcome.CameraId} {string.Join(" ", stack.Members)}");
            if (OutputOf(p, id) is not { } output)
                return (null, $"{pj.Name} drives no output in Projector mode — the correction lives on its output");
            if (id == stack.MasterId)
                master = members.Count;
            members.Add((pj, output, view));
        }
        if (master < 0)
            return (null, $"{stack.Name}'s master {stack.MasterId} is not one of its members");
        return (new StackSource(p, staged, stack, scan, outcome, viewId, members, master), null);
    }

    /// <summary>The members' projector views from one place. Pairs compare the camera pixels each member's decode saw, so every
    /// member's view must come from one camera standing in one place: of a scan of many views, the view the last solve used that saw
    /// every member (the one whose least-decoded member decoded most; dropped and unreadable views never), its decodes built into
    /// projector views; of a scan of one view, its smart scans. Null words when they are found; the words and the line that adds such
    /// a view when no view saw them all.</summary>
    private static (Dictionary<string, ProjectorView> Views, string ViewId, string? Why) StackViews(ScanOutcome outcome, StackGroup stack,
        ScanRecord scan, Project p)
    {
        if (outcome.Captured.IsDefaultOrEmpty || outcome.Captured.Length == 1)
        {
            var smart = new Dictionary<string, ProjectorView>(StringComparer.Ordinal);
            foreach (var (id, view) in outcome.Views)
                if (stack.Members.Contains(id, StringComparer.Ordinal))
                    smart.TryAdd(id, view);
            return (smart, outcome.Captured.IsDefaultOrEmpty ? "" : outcome.Captured[0].Id, null);
        }
        ScanViewData? best = null;
        var bestLeast = 0;
        foreach (var v in outcome.Captured)
        {
            if (v.State != "done")
                continue;
            var least = int.MaxValue;
            foreach (var id in stack.Members)
                least = Math.Min(least, v.Maps.TryGetValue(id, out var map) && map.Axes == ScanAxes.Both ? Decoded(map) : 0);
            if (least > bestLeast)
                (best, bestLeast) = (v, least);
        }
        if (best is null)
        {
            var names = string.Join(", ", stack.Members.Select(id => p.FindProjector(id)?.Name ?? id));
            return ([], "", $"no view of {scan.Name} saw every member of {stack.Name} ({names}) from one place — its views each saw some of them, and "
                + $"pairs need one camera place: SCAN START {outcome.CameraId} {string.Join(" ", stack.Members)} INTO {scan.Id} adds such a view");
        }
        var views = new Dictionary<string, ProjectorView>(StringComparer.Ordinal);
        foreach (var id in stack.Members)
            views[id] = ProjectorView.Build(best.Maps[id]);
        return (views, best.Id, null);
    }

    /// <summary>The camera pixels a decode read.</summary>
    private static int Decoded(CorrespondenceMap map)
    {
        var n = 0;
        foreach (var f in map.Flags)
            if ((f & Core.Scan.DecodeStatus.Valid) != 0)
                n++;
        return n;
    }

    /// <summary>The master's render → where the member's render puts the same picture point: a scale between rasters when both share
    /// one model pose; else the master's ray onto the stage, seen by the member; null when neither can say.</summary>
    private static Func<Vec2d, Vec2d?>? MasterToRender(Projector master, Projector member, Lazy<StageScene?> stage, out string? why)
    {
        why = null;
        var km = master.Intrinsics;
        var kj = member.Intrinsics;
        var sx = kj.Width / (double)km.Width;
        var sy = kj.Height / (double)km.Height;
        var rm = master.Pose.CameraToWorld;
        var rj = member.Pose.CameraToWorld;
        var samePose = (master.Pose.Position - member.Pose.Position).Length < 1e-3
            && Vec3d.Dot(rm.Column(0), rj.Column(0)) > Math.Cos(1e-4) && Vec3d.Dot(rm.Column(2), rj.Column(2)) > Math.Cos(1e-4)
            && Math.Abs(km.Fx * sx - kj.Fx) < 1e-6 * kj.Fx && Math.Abs(km.Fy * sy - kj.Fy) < 1e-6 * kj.Fy
            && Math.Abs(km.Cx * sx - kj.Cx) < 1e-6 * kj.Width && Math.Abs(km.Cy * sy - kj.Cy) < 1e-6 * kj.Height
            && master.Lens == member.Lens;
        if (samePose)
            return StackPairs.Scaled(km.Width, km.Height, kj.Width, kj.Height);
        if (stage.Value is not { } scene)
        {
            why = $"{member.Name} and {master.Name} are modelled with different poses or optics and the stage has no model to carry one's picture to the other — SURVEY IMPORT or SCAN APPLY a model";
            return null;
        }
        var mv = DeviceView.Of(master);
        var jv = DeviceView.Of(member);
        return m => scene.Raycaster.Cast(mv.Ray(m)) is { } hit ? jv.Project(hit.Point) : null;
    }

    /// <summary>STACK ALIGN &lt;stack&gt; FROM &lt;scan&gt; [GRID &lt;c&gt;x&lt;r&gt;]: each member's residual lattice fitted to the master's
    /// picture (the master is the target and keeps no lattice), applied through the ledger as one edit.</summary>
    private async Task<CommandReply> StackAlignAsync(VerbCall c)
    {
        const string Usage = "STACK ALIGN <stack> FROM <scan> [GRID <c>x<r>]";
        if (Stopping)
            return CommandReply.Fail(StoppingWords);
        var options = StackFitOptions.Default;
        var gridWord = c.Args.SkipWhile(a => !a.Equals("GRID", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        if (gridWord is not null)
        {
            if (gridWord.Split('x', 'X') is not [var cs, var rs] || !int.TryParse(cs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cols)
                || !int.TryParse(rs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows)
                || cols is < WarpLattice.MinSize or > WarpLattice.MaxSize || rows is < WarpLattice.MinSize or > WarpLattice.MaxSize)
                return CommandReply.Fail($"GRID is <columns>x<rows>, each {WarpLattice.MinSize}–{WarpLattice.MaxSize} (33x19 by default)");
            options = options with { Columns = cols, Rows = rows };
        }
        var (source, why) = await StackSourceAsync(c, Usage).ConfigureAwait(false);
        if (source is null)
            return CommandReply.Fail(why!);
        foreach (var (pj, output, _) in source.Members)
            if (AlignWarped(output, pj) is { } twice)
                return CommandReply.Fail(twice);
        var fits = await Task.Run(() => FitStack(source, options, c.Ct), c.Ct).ConfigureAwait(false);
        if (fits.Why is { } refused)
            return CommandReply.Fail(refused);
        if (Stopping)
            return CommandReply.Fail(StoppingWords);
        var stack = source.Stack;
        var masterId = source.Members[source.Master].Projector.Id;
        var who = c.Session.ClientName;
        var now = Services.Wall.UnixMs;
        var applied = new List<(string Output, string Snapshot, string Fallback)>();
        var r = await EditAsync(c.Session, $"align stack {stack.Name}", p =>
        {
            applied.Clear();
            if (p.FindStack(stack.Id) is null)
                throw new EditRefusedException($"the stack {stack.Name} is gone");
            foreach (var (member, _) in fits.Members)
                if (OutputOf(p, member.Projector.Id) is { } drawn && AlignWarped(drawn, member.Projector) is { } twice)
                    throw new EditRefusedException(twice);
            foreach (var (member, fit) in fits.Members)
            {
                // The master is the target: its correction stays as it is.
                if (fit is null)
                    continue;
                var o = OutputOf(p, member.Projector.Id) ?? throw new EditRefusedException($"{member.Projector.Name}'s output is gone");
                var measured = (o.Measured ?? new MeasuredCorrection(null, null, null, null, 0, "", "", "", 0)) with
                {
                    Lattice = fit.Lattice,
                    ResidualPx = Math.Round(fit.ResidualPx, 4),
                    ScanId = source.Scan.Id,
                    StackId = stack.Id,
                    MeasuredUnixMs = now,
                    FittedTo = StackPoses.Of(p, stack.MasterId, member.Projector.Id),
                };
                p = RecordStackResult(p, o, measured, StackScore(stack, source.Scan, member.Projector, fit), source.Scan.Id, who, now, applied);
            }
            if (applied.Count == 0)
                throw new EditRefusedException($"every member of {stack.Name} stands as this fit has it already — this would change nothing");
            return p;
        }).ConfigureAwait(false);
        if (!r.Changed)
            return FromEdit(r);
        NativeDueNow();
        var staged = source.Staged;
        var worst = fits.Members.Where(m => m.Fit is not null).Select(m => m.Fit!.ResidualPx).DefaultIfEmpty(0).Max();
        var words = Staged(staged, string.Create(CultureInfo.InvariantCulture,
            $"{stack.Name} aligned to {source.Members[source.Master].Projector.Name} from {source.Scan.Name}: ")
            + string.Join("; ", fits.Members.Where(m => m.Fit is not null).Select(m => $"{m.Member.Projector.Name} {m.Fit!.Words}"))
            + (worst > StackResidualMaxPx ? string.Create(CultureInfo.InvariantCulture, $" — the worst member is {worst:0.00} px from the master: scan closer (more camera pixels per projector pixel)") : ""));
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("stack", stack.Id);
            w.WriteString("scan", source.Scan.Id);
            w.WriteString("view", source.ViewId);
            w.WriteString("master", masterId);
            w.WriteStartArray("members");
            foreach (var (member, fit) in fits.Members)
            {
                w.WriteStartObject();
                w.WriteString("projector", member.Projector.Id);
                w.WriteString("output", member.Output.Id);
                w.WriteBoolean("master", fit is null);
                w.WriteNumber("residualPx", fit is null ? 0 : Math.Round(fit.ResidualPx, 3));
                w.WriteNumber("p95Px", fit is null ? 0 : Math.Round(fit.P95Px, 3));
                w.WriteNumber("beforePx", fit is null ? 0 : Math.Round(fit.BeforePx, 3));
                w.WriteNumber("pairs", fit?.Pairs ?? 0);
                w.WriteNumber("heldOut", fit?.HeldOut ?? 0);
                var snap = applied.FirstOrDefault(a => a.Output == member.Output.Id);
                w.WriteString("snapshot", snap.Snapshot ?? "");
                w.WriteString("fallback", snap.Fallback ?? "");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("residualPx", Math.Round(worst, 3));
            w.WriteBoolean("staged", staged);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>The refusal for a member whose output an alignment draws through its measured warp grid (ALIGN ACCEPT), or null. That
    /// output shows the canvas it was registered to, not its render, so a lattice fitted to the render's pairs would register it twice;
    /// its align group registers the stack (ALIGN RUN), or ALIGN CLEAR takes the measured warp off first.</summary>
    private static string? AlignWarped(Output output, Projector pj) => output.Measured?.Warp is null ? null
        : $"{output.Name} ({pj.Name}) is drawn through an alignment's measured warp — a stack lattice on it would register it twice: "
          + $"ALIGN CLEAR {output.Id} takes that off first (or ALIGN RUN registers the stack on its canvas)";

    private sealed record StackFits(IReadOnlyList<((Projector Projector, Output Output, ProjectorView View) Member, StackFit? Fit)> Members, string? Why);

    /// <summary>The raster an output draws its projector's picture at (its own size, else the projector's).</summary>
    private static (int Width, int Height) RasterOf((Projector Projector, Output Output, ProjectorView View) member) =>
        (member.Output.Width > 0 ? member.Output.Width : member.Projector.Intrinsics.Width,
         member.Output.Height > 0 ? member.Output.Height : member.Projector.Intrinsics.Height);

    /// <summary>Every member but the master fitted to the master's picture (the master: no lattice). The master's picture is what its
    /// output draws: the light the scan saw at one of its raster's pixels carries the picture point its whole warp (an operator's
    /// corner pin or lattice, a lattice measured before) puts there, so each master pixel is taken back through that warp first.</summary>
    private StackFits FitStack(StackSource source, StackFitOptions options, CancellationToken ct)
    {
        // The model loads only when a member's modelled pose or optics differ from the master's.
        var scene = new Lazy<StageScene?>(() => StackScene(source.Project), LazyThreadSafetyMode.None);
        var master = source.Members[source.Master];
        foreach (var member in source.Members)
        {
            var (w, h) = RasterOf(member);
            if (member.View.Width != w || member.View.Height != h)
                return new StackFits([], $"{member.Projector.Name} was scanned at {member.View.Width}×{member.View.Height} and its output is {w}×{h} — scan it at its output's raster");
        }
        var (masterWidth, masterHeight) = RasterOf(master);
        var masterWarp = new WarpField(master.Output.Warp, null, master.Output.Measured?.Lattice);
        var list = new List<((Projector, Output, ProjectorView), StackFit?)>();
        foreach (var member in source.Members)
        {
            ct.ThrowIfCancellationRequested();
            if (member.Projector.Id == master.Projector.Id)
            {
                list.Add((member, null));
                continue;
            }
            if (MasterToRender(master.Projector, member.Projector, scene, out var why) is not { } toRender)
                return new StackFits([], why);
            var map = masterWarp.IsIdentity ? toRender
                : m => masterWarp.Invert(m.X / masterWidth, m.Y / masterHeight) is { } u ? toRender(new Vec2d(u.X * masterWidth, u.Y * masterHeight)) : null;
            var (width, height) = RasterOf(member);
            var pairs = StackPairs.Against(master.View, member.View, map);
            // The lattice is drawn under the member's own warp (W(L(u))), and the scan saw the raw raster: where the light must leave
            // is taken back through the operator's warp, so registration holds with a corner pin or lattice on the member.
            var warp = new WarpField(member.Output.Warp);
            if (!warp.IsIdentity)
            {
                var through = new List<StackPair>(pairs.Count);
                foreach (var pair in pairs)
                    if (warp.Invert(pair.Emit.X / width, pair.Emit.Y / height) is { } back)
                        through.Add(pair with { Emit = new Vec2d(back.X * width, back.Y * height) });
                pairs = through;
            }
            var fit = StackAlignment.Fit(pairs, width, height, options, ct);
            if (!double.IsFinite(fit.ResidualPx))
                return new StackFits([], $"{member.Projector.Name}: {fit.Words} — did the camera see it and {master.Projector.Name} in one place?");
            list.Add((member, fit));
        }
        return new StackFits(list, null);
    }

    /// <summary>A member's result: its current correction kept as a result when it is not one, the new one recorded and applied
    /// (source stack), the kept one its fallback.</summary>
    private static Project RecordStackResult(Project p, Output o, MeasuredCorrection measured, QualityScore score, string scanId, string who, long now,
        List<(string Output, string Snapshot, string Fallback)> applied)
    {
        if (SameMeasure(o.Measured, measured))
            return p;
        (p, var previous) = KeepAsItStands(p, o, who, now);
        var id = p.NewId(Ids.Calibration);
        var snap = new CalibrationSnapshot(id, CalibrationLedger.NameFor(p, o.Id, CalibrationSource.Stack), CalibrationTarget.Output, o.Id, now,
            CalibrationSource.Stack, scanId, previous, score, who, false)
        {
            Output = measured,
        };
        (p, _) = CalibrationLedger.Record(p, snap, apply: true);
        p = p with { Outputs = p.Outputs.Update(o.Id, x => x with { FallbackId = previous }) };
        applied.Add((o.Id, id, previous));
        return p;
    }

    /// <summary>True when two corrections draw the same (when they were measured aside): the lattice by its nodes.</summary>
    private static bool SameMeasure(MeasuredCorrection? a, MeasuredCorrection? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        var lattices = a.Lattice is null || b.Lattice is null ? a.Lattice is null && b.Lattice is null
            : a.Lattice.Columns == b.Lattice.Columns && a.Lattice.Rows == b.Lattice.Rows && a.Lattice.Interpolation == b.Lattice.Interpolation
              && a.Lattice.Offsets.SequenceEqual(b.Lattice.Offsets) && a.Lattice.HandlesU.SequenceEqual(b.Lattice.HandlesU)
              && a.Lattice.HandlesV.SequenceEqual(b.Lattice.HandlesV);
        return lattices && Equals(a.Warp, b.Warp) && Equals(a.Blend, b.Blend) && Equals(a.Color, b.Color) && a.ResidualPx == b.ResidualPx
            && a.ScanId == b.ScanId && a.StackId == b.StackId && a.RunId == b.RunId && Equals(a.FittedTo, b.FittedTo);
    }

    /// <summary>A member's alignment score: the held-out residual against the master as the stack term.</summary>
    private static QualityScore StackScore(StackGroup stack, ScanRecord scan, Projector member, StackFit fit)
    {
        var fix = $"STACK ALIGN {stack.Id} FROM {scan.Id}";
        var light = fit.P95Px <= StackResidualMaxPx ? "green" : fit.P95Px <= 1 ? "amber" : "red";
        var term = new ScoreTerm(ScoreTermCodes.StackConvergence,
            string.Create(CultureInfo.InvariantCulture, $"{member.Name} lands {fit.ResidualPx:0.00} px RMS (p95 {fit.P95Px:0.00} px) from the master on pairs it was not fitted to"),
            Math.Round(fit.P95Px, 3), "px", Math.Round(fit.P95Px, 3), light, member.Id, fix,
            fit.P95Px <= StackResidualMaxPx ? "" : "scan with the camera closer, so it sees single projector pixels");
        return new QualityScore(Math.Round(fit.P95Px, 3), "px", QualityScore.GradeOf(fit.P95Px, "px", false), Math.Round(fit.ResidualPx, 3),
            Math.Round(fit.P95Px, 3), Math.Clamp(fit.HeldOut / 1000.0, 0, 1), [term], term.Words) { Source = "stack" };
    }

    /// <summary>STACK MATCH &lt;stack&gt; FROM &lt;scan&gt;: each member's white brought to the master's with gains of at most one, from the
    /// scan's colour white frames, applied through the ledger as one edit.</summary>
    private async Task<CommandReply> StackMatchAsync(VerbCall c)
    {
        const string Usage = "STACK MATCH <stack> FROM <scan>";
        if (Stopping)
            return CommandReply.Fail(StoppingWords);
        if (c.Args.Any(a => a.Equals("GRID", StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail(Usage);
        var (source, why) = await StackSourceAsync(c, Usage).ConfigureAwait(false);
        if (source is null)
            return CommandReply.Fail(why!);
        var (match, words0) = await Task.Run(() => MatchStack(source), c.Ct).ConfigureAwait(false);
        if (match is null)
            return CommandReply.Fail(words0);
        if (Stopping)
            return CommandReply.Fail(StoppingWords);
        var stack = source.Stack;
        var who = c.Session.ClientName;
        var now = Services.Wall.UnixMs;
        var applied = new List<(string Output, string Snapshot, string Fallback)>();
        var r = await EditAsync(c.Session, $"match stack {stack.Name}", p =>
        {
            applied.Clear();
            if (p.FindStack(stack.Id) is null)
                throw new EditRefusedException($"the stack {stack.Name} is gone");
            for (var j = 0; j < source.Members.Count; j++)
            {
                var member = source.Members[j];
                var g = match.Members[j];
                var o = OutputOf(p, member.Projector.Id) ?? throw new EditRefusedException($"{member.Projector.Name}'s output is gone");
                var was = o.Measured ?? new MeasuredCorrection(null, null, null, null, 0, "", "", "", 0);
                var colour = new MeasuredColor(Math.Round(g.R, 4), Math.Round(g.G, 4), Math.Round(g.B, 4),
                    was.Color is { Gamma: > 0 } wc ? wc.Gamma : o.Blend.Gamma, was.Color is { Level: > 0 } lc ? lc.Level : 1);
                var measured = was with { Color = colour, ScanId = source.Scan.Id, StackId = stack.Id, MeasuredUnixMs = now };
                var term = new ScoreTerm(ScoreTermCodes.ColourMatch,
                    string.Create(CultureInfo.InvariantCulture, $"{member.Projector.Name}'s white was Δu′v′ {g.DuvBefore:0.0000} from the master's; gains {g.R:0.000} {g.G:0.000} {g.B:0.000}"),
                    Math.Round(g.DuvBefore, 5), "duv", 0, "green", member.Projector.Id, $"STACK MATCH {stack.Id} FROM {source.Scan.Id}", "");
                var score = new QualityScore(0, "px", "A", 0, 0, 1, [term], term.Words) { Source = "stack" };
                p = RecordStackResult(p, o, measured, score, source.Scan.Id, who, now, applied);
            }
            if (applied.Count == 0)
                throw new EditRefusedException($"every member of {stack.Name} is matched so already — this would change nothing");
            return p;
        }).ConfigureAwait(false);
        if (!r.Changed)
            return FromEdit(r);
        NativeDueNow();
        var staged = source.Staged;
        var words = Staged(staged, $"{stack.Name}: {match.Words}");
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("stack", stack.Id);
            w.WriteString("scan", source.Scan.Id);
            w.WriteString("view", source.ViewId);
            w.WriteStartArray("members");
            for (var j = 0; j < source.Members.Count; j++)
            {
                var g = match.Members[j];
                w.WriteStartObject();
                w.WriteString("projector", source.Members[j].Projector.Id);
                w.WriteString("output", source.Members[j].Output.Id);
                w.WriteStartArray("gain");
                w.WriteNumberValue(Math.Round(g.R, 4));
                w.WriteNumberValue(Math.Round(g.G, 4));
                w.WriteNumberValue(Math.Round(g.B, 4));
                w.WriteEndArray();
                w.WriteNumber("duv", Math.Round(g.DuvBefore, 5));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("staged", staged);
            w.WriteString("words", words);
            w.WriteEndObject();
        }), words);
    }

    /// <summary>Each member's white from the scan's colour white frame (camera-linear, trimmed mean over the pixels the scan measured
    /// where the camera saw every member: the area the stack shares), matched to the master's. The white frame holds the ambient light
    /// as well, the same for every member, so a dark scene matches best; the words say so.</summary>
    private static (StackColourMatch? Match, string Words) MatchStack(StackSource source)
    {
        var colours = new List<ProjectorColour>(source.Members.Count);
        var inverses = source.Members.Select(m => new ViewInverse(m.View)).ToList();
        for (var j = 0; j < source.Members.Count; j++)
        {
            var (pj, _, view) = source.Members[j];
            if (view.SmartScanColour is not { } colour)
                return (null, $"{source.Scan.Name} has no colour white frame for {pj.Name} — scan the stack with a colour camera");
            var rgb = new byte[colour.Length * 3];
            var mask = new bool[colour.Length];
            var n = 0;
            for (var k = 0; k < colour.Length; k++)
            {
                rgb[k * 3] = (byte)(colour[k] >> 16);
                rgb[k * 3 + 1] = (byte)(colour[k] >> 8);
                rgb[k * 3 + 2] = (byte)colour[k];
                if (view.Coverage[k] != ProjectorCoverage.Measured)
                    continue;
                var camera = new Vec2d(view.CameraX[k], view.CameraY[k]);
                var shared = true;
                for (var other = 0; other < inverses.Count && shared; other++)
                    shared = other == j || inverses[other].At(camera) is not null;
                if (!shared)
                    continue;
                mask[k] = true;
                n++;
            }
            if (n < 64)
                return (null, $"{source.Scan.Name} saw too little of {pj.Name} where every member lights to read its white");
            colours.Add(StackColour.FromWhite(Vec3d.Zero, ColourMatch.Mean(rgb, mask)));
        }
        var (match, words) = StackColour.ToMaster(colours, source.Master);
        return match is null ? (null, words) : (match with { Words = words + " (from the scan's white frames, ambient light included: a dark scene matches best)" }, words);
    }

    // ---- STATE, context ------------------------------------------------------------------------------------------------------

    /// <summary>A stack as STATE and STACK LIST tell it: aligned when every member but the master carries this stack's lattice, fitted
    /// to the poses its projectors stand at (<see cref="StackPoses"/>); its worst member's held-out residual; the members' gains; and
    /// the member whose lattice was fitted to poses that are gone (it or the master solved again since), when one was (null otherwise).</summary>
    internal static (bool Aligned, double ResidualPx, bool Matched, string? Moved) StackFacts(Project p, StackGroup sk)
    {
        // A stack of fewer than two (made by a generic ADD) stacks nothing: never counted aligned.
        if (sk.ProjectorIds.IsDefault || sk.Members.Length < 2)
            return (false, 0, false, null);
        var aligned = true;
        var matched = true;
        var worst = 0.0;
        string? moved = null;
        foreach (var id in sk.Members)
        {
            var m = OutputOf(p, id)?.Measured;
            var mine = m is not null && m.StackId == sk.Id;
            if (id != sk.MasterId && !(mine && m!.Lattice is not null))
                aligned = false;
            if (!(mine && m!.Color is not null))
                matched = false;
            if (mine && id != sk.MasterId)
            {
                worst = Math.Max(worst, m!.ResidualPx);
                if (moved is null && StackPoses.MovedSince(p, sk, id, m))
                    moved = id;
            }
        }
        return (aligned && moved is null, worst, matched, aligned ? moved : null);
    }

    /// <summary>What a solve that moved projectors says of the stacks it unaligned ("" when none): each stack aligned before the edit
    /// whose lattice the edit left fitted to poses that are gone.</summary>
    internal static string StacksMovedWords(Project before, Project after)
    {
        var words = new List<string>();
        foreach (var sk in after.Stage.Stacks)
        {
            if (before.FindStack(sk.Id) is not { } was || !StackFacts(before, was).Aligned || StackFacts(after, sk).Moved is not { } moved)
                continue;
            words.Add($"{sk.Name} was aligned with {NameOfProjector(after, moved)} where it stood — STACK ALIGN {sk.Id} FROM <scan> fits it again (a scan taken since, if it moved)");
        }
        return words.Count == 0 ? "" : "; " + string.Join("; ", words);
    }

    private void WriteStacks(Utf8JsonWriter w, Project p, bool list)
    {
        if (list)
        {
            w.WriteStartObject();
            w.WritePropertyName("stacks");
        }
        w.WriteStartArray();
        foreach (var sk in p.Stage.Stacks)
        {
            var (aligned, residual, matched, _) = StackFacts(p, sk);
            w.WriteStartObject();
            w.WriteString("id", sk.Id);
            w.WriteString("name", sk.Name);
            w.WriteStartArray("members");
            foreach (var id in sk.Members)
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteString("master", sk.MasterId);
            w.WriteBoolean("aligned", aligned);
            w.WriteBoolean("matched", matched);
            w.WriteNumber("residualPx", Math.Round(residual, 3));
            w.WriteString("failover", sk.Failover == StackFailover.Match ? "match" : "hold");
            var lost = StackDegradations.GetValueOrDefault(sk.Id);
            w.WriteStartArray("degraded");
            foreach (var id in lost?.Members.Keys ?? Enumerable.Empty<string>())
                w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteNumber("gain", Math.Round(lost?.Gain ?? 1, 3));
            if (list)
            {
                w.WriteStartArray("gains");
                foreach (var id in sk.Members)
                {
                    var c = OutputOf(p, id)?.Measured?.Color;
                    w.WriteStartArray();
                    w.WriteNumberValue(c?.GainR ?? 1);
                    w.WriteNumberValue(c?.GainG ?? 1);
                    w.WriteNumberValue(c?.GainB ?? 1);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (list)
            w.WriteEndObject();
    }

    private void ContextStacks(Project p, List<ContextItem> items)
    {
        foreach (var sk in p.Stage.Stacks)
        {
            var (aligned, residual, _, moved) = StackFacts(p, sk);
            var lost = StackDegradations.GetValueOrDefault(sk.Id);
            var (light, state) = lost is not null
                ? (Light.Red, $"{sk.Name} lost {string.Join(", ", lost.Members.Keys.Select(id => p.FindProjector(id)?.Name ?? id))} — "
                    + (lost.Gain < 1 ? string.Create(CultureInfo.InvariantCulture, $"matched: the building at {lost.Gain * 100:0} %") : "held"))
                : moved is not null ? (Light.Amber, $"{NameOfProjector(p, moved)} or {NameOfProjector(p, sk.MasterId)} moved since {sk.Name} was aligned — STACK ALIGN {sk.Id} FROM <scan>")
                : !aligned ? (Light.Amber, $"{sk.Name} is not aligned — STACK ALIGN {sk.Id} FROM <scan>")
                : residual > StackResidualMaxPx ? (Light.Amber, string.Create(CultureInfo.InvariantCulture, $"{sk.Name}: a member lands {residual:0.00} px from the master"))
                : (Light.Green, string.Create(CultureInfo.InvariantCulture, $"{sk.Name}: {sk.Members.Length} projectors within {residual:0.00} px"));
            items.Add(new ContextItem("stack:" + sk.Id, "stack", sk.Name, light, state));
        }
    }
}
