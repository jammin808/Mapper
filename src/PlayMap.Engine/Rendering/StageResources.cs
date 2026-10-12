using System.Collections.Immutable;
using PlayMap.Core.Calibration;
using PlayMap.Core.Geometry;
using PlayMap.Core.Mapping;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;

namespace PlayMap.Engine.Rendering;

/// <summary>How the stage's maps are sized and how long the automatic blend waits for the stage to settle.</summary>
public sealed record StageResourceOptions
{
    public static readonly StageResourceOptions Default = new();

    /// <summary>The quiet time after the last stage change before the automatic 3D blend is solved again.</summary>
    public TimeSpan BlendDebounce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>A manual edge-blend map's size as a fraction of the output's raster (the GPU upsamples it).</summary>
    public double EdgeMapScale { get; init; } = 0.25;

    /// <summary>An output mask's raster as a fraction of the output's (feathers are in output pixels).</summary>
    public double OutputMaskScale { get; init; } = 0.5;

    /// <summary>The side of a surface mask's square raster over its UV space.</summary>
    public int SurfaceMaskSize { get; init; } = 1024;

    /// <summary>A lens lookup's size as a fraction of the output's raster (real lenses are smooth).</summary>
    public double LensMapScale { get; init; } = 0.25;
}

/// <summary>The stage's geometry and maps for the renderer, over the project snapshot: meshes from
/// <see cref="StageMeshes"/>, warps from <see cref="WarpMeshBuilder"/>, manual blends from
/// <see cref="BlendMath.EdgeBlendMap"/>, automatic blends from <see cref="BlendSolver3D"/>, masks from
/// <see cref="MaskRasterizer"/>, lens lookups from <see cref="LensPrewarp"/>.
///
/// Every object is built off the render thread and is immutable once published; an object whose inputs are
/// the same instance as last time is handed back as the same object, so the renderer (which uploads by
/// identity) uploads only what changed — dragging one warp node rebuilds that output's warp and nothing else.
/// The render thread calls <see cref="Observe"/> once a frame: it swaps in the tables the builder finished
/// (one reference read) and, when the project snapshot moved, wakes the builder; the lookups read the tables
/// in use with no lock and no allocation. A mesh still loading is null (its surfaces are not drawn) and its
/// tables are published again once it is in. The automatic blend is solved when the stage has been quiet for
/// <see cref="StageResourceOptions.BlendDebounce"/>, a solve under way is cancelled by a newer stage, and the
/// last good maps stay up meanwhile; a solve BLEND SOLVE made for the same inputs is taken as it is.</summary>
public sealed class StageResources : IStageResources, IAsyncDisposable
{
    private sealed record WarpEntry(OutputWarp Source, WarpMeshGeometry? Geometry);

    private sealed record EdgeEntry(BlendSettings Settings, int Width, int Height, BlendMap? Map);

    private sealed record MaskEntry(ImmutableArray<Mask> Source, int Width, int Height, CoverageMap? Map);

    private sealed record LensEntry(Intrinsics K, LensDistortion Lens, int Width, int Height, LensLookup? Map);

    /// <summary>One published set of lookups; never written after it is published.</summary>
    private sealed class Tables
    {
        public static readonly Tables Empty = new(null, -1, true, new Dictionary<string, MeshData>(StringComparer.Ordinal));

        public Tables(Project? project, long revision, bool settled, Dictionary<string, MeshData> meshes)
        {
            Project = project;
            Revision = revision;
            Settled = settled;
            Meshes = meshes;
        }

        public Project? Project { get; }
        public long Revision { get; }

        /// <summary>Every mesh the stage names was tried (loaded, or failed with its reason).</summary>
        public bool Settled { get; }

        public Dictionary<string, MeshData> Meshes { get; }
        public Dictionary<string, WarpEntry> Warps { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, EdgeEntry> Edges { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, BlendMap> Blends { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, MaskEntry> OutputMasks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, MaskEntry> SurfaceMasks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, LensEntry> Lenses { get; } = new(StringComparer.Ordinal);
    }

    private readonly StageMeshes _meshes;
    private readonly StageResourceOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _buildWake = new(0, 1);
    private readonly SemaphoreSlim _blendWake = new(0, 1);
    private readonly Lock _gate = new();
    private readonly List<(long Revision, TaskCompletionSource Done)> _waiters = [];
    private readonly Task _builder;
    private readonly Task _blender;

    // Render thread.
    private Tables _current = Tables.Empty;
    private ProjectSnapshot? _observed;

    // Shared, published by reference.
    private Tables _published = Tables.Empty;
    private ProjectSnapshot? _wanted;
    private int _buildSignalled;
    private int _blendSignalled;
    private BlendInputs? _blendWanted;
    private long _blendWantedVersion;
    private BlendSolveResult? _solved;
    private BlendSolveResult? _offered;
    private CancellationTokenSource? _solving;

    // Builder thread.
    private Tables _built = Tables.Empty;

    public StageResources(StageMeshes meshes, StageResourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        _meshes = meshes;
        _options = options ?? StageResourceOptions.Default;
        _builder = Task.Run(() => BuildLoopAsync(_stop.Token), CancellationToken.None);
        _blender = Task.Run(() => BlendLoopAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>Warp meshes tessellated so far (an unchanged warp is handed back, not built again).</summary>
    public int WarpBuilds { get; private set; }

    /// <summary>Automatic blend solves finished so far (a solve BLEND SOLVE handed over is not counted).</summary>
    public int BlendSolves { get; private set; }

    /// <summary>The automatic blend's last words (empty before the first), and the last fault a build hit.</summary>
    public string BlendWords { get; private set; } = "";

    public string? LastProblem { get; private set; }

    /// <summary>The project revision the tables in use on the render thread were built for (−1 before any).</summary>
    public long Revision => _current.Revision;

    // ---- render thread --------------------------------------------------------------------------------

    /// <summary>Once a frame on the render thread: takes the tables the builder finished and, when the
    /// snapshot moved, asks for new ones. Allocates nothing while the project stands still.</summary>
    public void Observe(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _current = Volatile.Read(ref _published);
        if (ReferenceEquals(snapshot, _observed))
            return;
        _observed = snapshot;
        Volatile.Write(ref _wanted, snapshot);
        Signal(ref _buildSignalled, _buildWake);
    }

    public MeshData? Mesh(string meshId) => _current.Meshes.GetValueOrDefault(meshId);

    public WarpMeshGeometry? Warp(string outputId) =>
        _current.Warps.TryGetValue(outputId, out var w) ? w.Geometry : null;

    public BlendMap? Blend(string outputId) =>
        _current.Blends.TryGetValue(outputId, out var b) ? b : _current.Edges.TryGetValue(outputId, out var e) ? e.Map : null;

    public CoverageMap? OutputMask(string outputId) =>
        _current.OutputMasks.TryGetValue(outputId, out var m) ? m.Map : null;

    public CoverageMap? SurfaceMask(string surfaceId) =>
        _current.SurfaceMasks.TryGetValue(surfaceId, out var m) ? m.Map : null;

    public LensLookup? LensPredistortion(string outputId) =>
        _current.Lenses.TryGetValue(outputId, out var l) ? l.Map : null;

    // ---- any thread -----------------------------------------------------------------------------------

    /// <summary>Completes once tables for the revision (or a later one) are published with every mesh tried.</summary>
    public Task WhenSettledAsync(long revision)
    {
        lock (_gate)
        {
            var t = Volatile.Read(ref _published);
            if (t.Settled && t.Revision >= revision)
                return Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((revision, done));
            return done.Task;
        }
    }

    /// <summary>Takes a blend BLEND SOLVE made: installed at once while the project's inputs are the ones it
    /// was solved for (a pending automatic solve for them is dropped).</summary>
    public void Offer(BlendSolveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Volatile.Write(ref _offered, result);
        Signal(ref _buildSignalled, _buildWake);
    }

    // ---- the builder ----------------------------------------------------------------------------------

    private static void Signal(ref int flag, SemaphoreSlim wake)
    {
        if (Interlocked.Exchange(ref flag, 1) == 0)
            wake.Release();
    }

    private async Task BuildLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _buildWake.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Volatile.Write(ref _buildSignalled, 0);
            var snap = Volatile.Read(ref _wanted);
            if (snap is null)
                continue;
            try
            {
                Build(snap);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A build that faults keeps the tables in use; the next change tries again.
                LastProblem = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    private void Build(ProjectSnapshot snap)
    {
        var project = snap.Project;
        // The automatic blend first: what it has solved goes into these tables.
        var inputs = BlendInputs.Of(project);
        if (!inputs.Equals(Volatile.Read(ref _blendWanted)))
        {
            Volatile.Write(ref _blendWanted, inputs);
            Interlocked.Increment(ref _blendWantedVersion);
            CancelSolve();
            if (!inputs.IsEmpty)
                Signal(ref _blendSignalled, _blendWake);
        }
        if (Volatile.Read(ref _offered) is { } offered && offered.Inputs.Equals(inputs) && !ReferenceEquals(offered, Volatile.Read(ref _solved)))
        {
            Volatile.Write(ref _solved, offered);
            CancelSolve();
            BlendWords = offered.Solution.Words;
        }

        var tables = Assemble(project, snap.Revision, loadMeshes: false);
        Publish(tables);
        if (!tables.Settled)
        {
            // Meshes load on this thread after the quick lookups are up, so a warp drag never waits on a file.
            Publish(Assemble(project, snap.Revision, loadMeshes: true));
        }
        _meshes.Retain(project.Stage);
    }

    private Tables Assemble(Project project, long revision, bool loadMeshes)
    {
        var prev = _built;
        var meshes = new Dictionary<string, MeshData>(StringComparer.Ordinal);
        var missing = false;
        foreach (var asset in project.Stage.Meshes)
        {
            var mesh = loadMeshes ? _meshes.Get(asset) : _meshes.Peek(asset);
            if (mesh is not null)
                meshes[asset.Id] = mesh;
            else if (!loadMeshes && _meshes.Problem(asset.Id) is null)
                missing = true;
        }
        var next = new Tables(project, revision, settled: !missing, meshes);
        foreach (var output in project.Outputs)
        {
            next.Warps[output.Id] = WarpFor(output, prev);
            if (EdgeFor(output, prev) is { } edge)
                next.Edges[output.Id] = edge;
            if (OutputMaskFor(output, prev) is { } mask)
                next.OutputMasks[output.Id] = mask;
            if (LensFor(output, project, prev) is { } lens)
                next.Lenses[output.Id] = lens;
        }
        foreach (var surface in project.Stage.Surfaces)
            if (SurfaceMaskFor(surface, prev) is { } mask)
                next.SurfaceMasks[surface.Id] = mask;
        if (Volatile.Read(ref _solved) is { } solved)
        {
            // The last good automatic blend stays up while a newer stage is solved.
            foreach (var o in solved.Solution.Outputs)
                if (project.FindOutput(o.OutputId) is { } output && BlendInputs.Blends(output))
                    next.Blends[o.OutputId] = o.Map;
        }
        return next;
    }

    private WarpEntry WarpFor(Output output, Tables prev)
    {
        if (prev.Warps.TryGetValue(output.Id, out var w) && ReferenceEquals(w.Source, output.Warp))
            return w;
        var warp = output.Warp;
        var resting = !warp.Enabled || (warp.Pin.IsNone && (warp.Lattice?.IsAtRest ?? true));
        if (resting)
            return new WarpEntry(warp, null);
        WarpBuilds++;
        return new WarpEntry(warp, WarpMeshBuilder.Build(warp));
    }

    private EdgeEntry? EdgeFor(Output output, Tables prev)
    {
        var s = output.Blend;
        if (BlendInputs.Blends(output) || (s.Edges == EdgeBlend.None && s.BlackLevel <= 0))
            return null;
        var w = Math.Max(16, (int)Math.Round(output.Width * _options.EdgeMapScale));
        var h = Math.Max(16, (int)Math.Round(output.Height * _options.EdgeMapScale));
        if (prev.Edges.TryGetValue(output.Id, out var e) && e.Settings == s && e.Width == w && e.Height == h)
            return e;
        return new EdgeEntry(s, w, h, BlendMath.EdgeBlendMap(output.Id, w, h, s, output.Width, output.Height));
    }

    private MaskEntry? OutputMaskFor(Output output, Tables prev)
    {
        if (!output.Masks.Any(m => m.Enabled))
            return null;
        var w = Math.Clamp((int)Math.Round(output.Width * _options.OutputMaskScale), 16, 4096);
        var h = Math.Clamp((int)Math.Round(output.Height * _options.OutputMaskScale), 16, 4096);
        if (prev.OutputMasks.TryGetValue(output.Id, out var e) && e.Source == output.Masks && e.Width == w && e.Height == h)
            return e;
        var values = MaskRasterizer.RasterizeOutputMasks(output.Masks, w, h, output.Width, output.Height);
        return new MaskEntry(output.Masks, w, h, new CoverageMap(w, h, values));
    }

    private MaskEntry? SurfaceMaskFor(Surface surface, Tables prev)
    {
        if (!surface.Masks.Any(m => m.Enabled))
            return null;
        var n = Math.Clamp(_options.SurfaceMaskSize, 16, 4096);
        if (prev.SurfaceMasks.TryGetValue(surface.Id, out var e) && e.Source == surface.Masks && e.Width == n)
            return e;
        var values = MaskRasterizer.RasterizeSurfaceMasks(surface.Masks, n, n);
        return new MaskEntry(surface.Masks, n, n, new CoverageMap(n, n, values));
    }

    private LensEntry? LensFor(Output output, Project project, Tables prev)
    {
        if (output.Mode != OutputMode.Projector || project.FindProjector(output.ProjectorId) is not { } pj || pj.Lens.IsNone
            || output.Width < 1 || output.Height < 1)
            return null;
        // The optics at the output's raster: the pre-distortion runs over the output, not the panel it was measured on.
        var k = pj.Intrinsics.Width == output.Width && pj.Intrinsics.Height == output.Height ? pj.Intrinsics : pj.Intrinsics.Scaled(output.Width, output.Height);
        var w = Math.Max(16, (int)Math.Round(output.Width * _options.LensMapScale));
        var h = Math.Max(16, (int)Math.Round(output.Height * _options.LensMapScale));
        if (prev.Lenses.TryGetValue(output.Id, out var e) && e.K == k && e.Lens == pj.Lens && e.Width == w && e.Height == h)
            return e;
        var map = LensPrewarp.Build(k, pj.Lens, w, h);
        return new LensEntry(k, pj.Lens, w, h, new LensLookup(map.Width, map.Height, map.Uv));
    }

    private void Publish(Tables tables)
    {
        _built = tables;
        List<TaskCompletionSource>? release = null;
        lock (_gate)
        {
            Volatile.Write(ref _published, tables);
            if (tables.Settled)
            {
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Revision > tables.Revision)
                        continue;
                    (release ??= []).Add(_waiters[i].Done);
                    _waiters.RemoveAt(i);
                }
            }
        }
        if (release is not null)
            foreach (var r in release)
                r.TrySetResult();
    }

    /// <summary>Stops a solve under way (its inputs are no longer the project's, or BLEND SOLVE answered them).</summary>
    private void CancelSolve()
    {
        try
        {
            Interlocked.Exchange(ref _solving, null)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The solve had already finished and let its token go.
        }
    }

    // ---- the automatic blend --------------------------------------------------------------------------

    private async Task BlendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _blendWake.WaitAsync(ct).ConfigureAwait(false);
                Volatile.Write(ref _blendSignalled, 0);
                // Quiet first: a drag moves the stage every few milliseconds and only its end is solved.
                long seen;
                do
                {
                    seen = Interlocked.Read(ref _blendWantedVersion);
                    await Task.Delay(_options.BlendDebounce, ct).ConfigureAwait(false);
                }
                while (seen != Interlocked.Read(ref _blendWantedVersion));
                var inputs = Volatile.Read(ref _blendWanted);
                if (inputs is null || inputs.IsEmpty || (Volatile.Read(ref _solved) is { } done && done.Inputs.Equals(inputs)))
                    continue;
                using var solving = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Volatile.Write(ref _solving, solving);
                var solution = inputs.Solve(_meshes, solving.Token);
                Interlocked.CompareExchange(ref _solving, null, solving);
                if (!inputs.Equals(Volatile.Read(ref _blendWanted)))
                    continue;
                Volatile.Write(ref _solved, new BlendSolveResult(inputs, solution, -1));
                BlendSolves++;
                BlendWords = solution.Words;
                Signal(ref _buildSignalled, _buildWake);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A newer stage cancelled this solve; its own wake-up solves it after the quiet time.
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The last good blend stays up; the next stage change tries again.
                LastProblem = $"blend: {ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_builder, _blender).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is the point.
        }
        lock (_gate)
        {
            foreach (var (_, done) in _waiters)
                done.TrySetCanceled(_stop.Token);
            _waiters.Clear();
        }
        _stop.Dispose();
        _buildWake.Dispose();
        _blendWake.Dispose();
    }
}
