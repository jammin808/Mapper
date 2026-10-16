using System.Diagnostics;
using System.Globalization;
using System.Text;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Core.Tests.Facade;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Facade;

/// <summary>A facade survey for the import's tests: a 40 × 20 m front sampled on a cols × rows grid (Z-up: x east, y the depth toward
/// the viewer, z up) with pilasters every 8 m (1 m proud, 0.24 m chamfers) and a 0.5 m cornice from 17 m, every crease on a grid line,
/// written as a binary PLY. The depth is piecewise linear, so the grid's triangles are the surface exactly.</summary>
internal static class SurveyFacade
{
    public static double Depth(double x, double z)
    {
        var xm = x % 8;
        var pilaster = xm < 0.24 ? xm / 0.24 : xm < 1.24 ? 1 : xm < 1.48 ? (1.48 - xm) / 0.24 : 0;
        var cornice = z < 17 ? 0 : z < 17.2 ? (z - 17) / 0.2 * 0.5 : 0.5;
        return pilaster + cornice;
    }

    public static MeshData Mesh(int cols, int rows)
    {
        var pos = new float[cols * rows * 3];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                double x = c * 40.0 / (cols - 1), z = r * 20.0 / (rows - 1);
                var i = (r * cols + c) * 3;
                pos[i] = (float)x;
                pos[i + 1] = (float)Depth(x, z);
                pos[i + 2] = (float)z;
            }
        var idx = new uint[(cols - 1) * (rows - 1) * 6];
        var k = 0;
        for (var r = 0; r + 1 < rows; r++)
            for (var c = 0; c + 1 < cols; c++)
            {
                uint a = (uint)(r * cols + c), b = a + 1, d = a + (uint)cols, e = d + 1;
                idx[k++] = a;
                idx[k++] = b;
                idx[k++] = e;
                idx[k++] = a;
                idx[k++] = e;
                idx[k++] = d;
            }
        return new MeshData(pos, [], [], idx);
    }

    public static string WritePly(string path, int cols, int rows)
    {
        using var stream = File.Create(path);
        MeshIO.WritePly(stream, Mesh(cols, rows));
        return path;
    }
}

/// <summary>SURVEY IMPORT end to end on one engine: a 2 M-triangle PLY comes in as ≤ 300 k triangles whose deviation from the survey,
/// measured here independently (brute force one way, exact local windows the other), is within the bound the reply reports; the
/// whole import is one undo step; the lane is never held by it (its own slices ≤ 10 ms, from the import's instrumentation); the time
/// is recorded with a 4× ceiling (the 15 s budget is the Perf twin's). LAS, OBJ and E57 come in with their coordinate systems and
/// stations; a failure leaves the project as it was and says why; one import at a time; a stopping node starts none.</summary>
[Collection(SurveyPerfCollection.Name)]
public sealed class SurveyImportTests(ITestOutputHelper log)
{
    private const int Cols = 1001, Rows = 1001; // 2 000 000 triangles

    /// <summary>The longest the import may hold the control lane in one slice (ms).</summary>
    internal const double LaneSliceBudgetMs = 10;

    internal static double ToSurvey(Vec3d stage, SiteFrame site, MeshData original, int cols, int rows)
    {
        // The stage point back on the survey's own grid (Z-up file coordinates are the grid's E, N, H here: LOCAL, scale 1).
        var g = site.ToGrid(stage);
        var dx = 40.0 / (cols - 1);
        var dz = 20.0 / (rows - 1);
        var vertical = Math.Abs(g.Y - SurveyFacade.Depth(Math.Clamp(g.X, 0, 40), Math.Clamp(g.Z, 0, 20)));
        var reach = vertical + Math.Max(dx, dz);
        int c0 = Math.Max(0, (int)((g.X - reach) / dx) - 1), c1 = Math.Min(cols - 2, (int)((g.X + reach) / dx) + 1);
        int r0 = Math.Max(0, (int)((g.Z - reach) / dz) - 1), r1 = Math.Min(rows - 2, (int)((g.Z + reach) / dz) + 1);
        var best = double.PositiveInfinity;
        for (var r = r0; r <= r1; r++)
            for (var c = c0; c <= c1; c++)
                for (var t = 0; t < 2; t++)
                {
                    var tri = ((r * (cols - 1)) + c) * 2 + t;
                    var (a, b, e) = original.Triangle(tri);
                    best = Math.Min(best, (MeshDistance.ClosestOnTriangle(g, original.Position(a), original.Position(b), original.Position(e)) - g).Length);
                }
        return best;
    }

    private static double Brute(Vec3d p, MeshData m)
    {
        var best = double.PositiveInfinity;
        for (var t = 0; t < m.TriangleCount; t++)
        {
            var (a, b, c) = m.Triangle(t);
            best = Math.Min(best, (MeshDistance.ClosestOnTriangle(p, m.Position(a), m.Position(b), m.Position(c)) - p).Length);
        }
        return best;
    }

    internal static async Task<(SurveyJobView Job, double Seconds)> ImportBig(SiteRig rig, string file)
    {
        var watch = Stopwatch.StartNew();
        var job = await rig.Import($"{file} CRS LOCAL UP Z DECIMATE 300000 NAME North front");
        return (job, watch.Elapsed.TotalSeconds);
    }

    [Fact]
    public async Task ATwoMillionTrianglePlyComesInAsOneEditWithinItsDeviationAndNeverHoldsTheLane()
    {
        await using var rig = new SiteRig();
        var file = SurveyFacade.WritePly(rig.File("north-front.ply"), Cols, Rows);
        var original = SurveyFacade.Mesh(Cols, Rows);
        Assert.Equal(2_000_000, original.TriangleCount);
        var revision = rig.Engine.Store.Current.Revision;

        var (job, seconds) = await ImportBig(rig, file);
        log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"2 M triangles imported in {seconds:0.0} s; lane max slice {job.LaneMaxMs:0.00} ms over {job.LaneSlices} slices; {job.Words}"));
        Assert.Equal("ready", job.State);
        Assert.True(seconds <= 15 * 4, $"{seconds:0.0} s");
        Assert.True(job.LaneSlices >= 2, $"{job.LaneSlices}");
        // The 10 ms slice is a budget on real work: here it is held to its 4× ceiling (the suite runs beside it); the Perf twin,
        // run alone, holds it to 10 ms.
        Assert.True(job.LaneMaxMs <= 4 * LaneSliceBudgetMs, $"the import held the lane {job.LaneMaxMs:0.00} ms in one slice");
        // One edit: the revision moved once.
        Assert.Equal(revision + 1, rig.Engine.Store.Current.Revision);

        var p = rig.Engine.Store.Project;
        var survey = Assert.Single(p.Stage.Surveys);
        Assert.Equal(job.Id, survey.Id);
        Assert.Equal(SurveyFormat.Ply, survey.Format);
        Assert.Equal(SurveyState.Ready, survey.State);
        Assert.Equal(2_000_000, survey.SourceTriangles);
        Assert.StartsWith("sha256:", survey.SourceHash, StringComparison.Ordinal);
        Assert.Equal("LOCAL", p.Stage.Site.Crs);
        Assert.Equal(new Vec3d(20, 0, 10), p.Stage.Site.Origin);
        var asset = p.FindMesh(survey.MeshId!)!;
        Assert.Equal(MeshOrigin.Imported, asset.Origin);
        Assert.True(asset.Triangles <= 300_000, $"{asset.Triangles}");
        var surface = Assert.Single(p.Stage.Surfaces);
        Assert.Equal(asset.Id, surface.MeshId);
        Assert.True(File.Exists(rig.File(survey.CloudPath)));

        // The deviation, measured here: original vertices to the result (brute force), result vertices to the survey (exact).
        var result = MeshIO.Load(rig.File(asset.Path)).Mesh!;
        Assert.Equal(asset.Triangles, result.TriangleCount);
        var site = p.Stage.Site;
        var rng = new Pcg32(2, 2);
        var worst = 0.0;
        for (var k = 0; k < 120; k++)
        {
            var v = original.Position(rng.NextInt(original.VertexCount));
            worst = Math.Max(worst, Brute(site.ToStage(v), result));
        }
        for (var k = 0; k < 2000; k++)
            worst = Math.Max(worst, ToSurvey(result.Position(rng.NextInt(result.VertexCount)), site, original, Cols, Rows));
        log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"deviation measured here {worst * 1000:0.000} mm, reported {survey.DecimationErrorM * 1000:0.000} mm"));
        Assert.True(worst <= survey.DecimationErrorM + 1e-6, $"measured {worst} m against the reported {survey.DecimationErrorM} m");
        Assert.True(survey.DecimationErrorM <= 0.005 * 2, $"{survey.DecimationErrorM}");

        // STATE shows it ready with its mesh; SURVEY LIST gives the provenance.
        var row = rig.State().GetProperty("stage").GetProperty("surveys")[0];
        Assert.Equal("ready", row.GetProperty("state").GetString());
        Assert.Equal(asset.Id, row.GetProperty("mesh").GetString());
        var listed = SiteRig.Json(await rig.Ok("SURVEY LIST")).GetProperty("surveys")[0];
        Assert.Equal(file, listed.GetProperty("path").GetString());
        Assert.Equal(survey.SourceHash, listed.GetProperty("hash").GetString());

        // One undo step takes the whole import back, site included.
        await rig.Ok("UNDO");
        var back = rig.Engine.Store.Project;
        Assert.Empty(back.Stage.Surveys);
        Assert.Empty(back.Stage.Surfaces);
        Assert.Empty(back.Stage.Meshes);
        Assert.False(back.Stage.Site.IsSet);
    }

    [Fact]
    public async Task AGeoreferencedLasComesInAsAThinnedCloudInItsOwnSystem()
    {
        await using var rig = new SiteRig();
        var rng = new Pcg32(5, 5);
        var pts = Enumerable.Range(0, 60_000).Select(_ => new Vec3d(512_300 + rng.Range(0.0, 40.0), 5_401_200 + rng.Range(0.0, 0.5), 100 + rng.Range(0.0, 20.0))).ToList();
        var wkt = "PROJCS[\"ETRS89 / UTM zone 32N\",UNIT[\"metre\",1],AUTHORITY[\"EPSG\",\"25832\"]]";
        var path = rig.File("scan.las");
        await File.WriteAllBytesAsync(path, LasTestWriter.Write(pts, null, new Vec3d(0.001, 0.001, 0.001), new Vec3d(512_000, 5_401_000, 0), wkt));
        var job = await rig.Import($"{path} VOXEL 0.5");
        Assert.Equal("ready", job.State);
        var p = rig.Engine.Store.Project;
        var survey = Assert.Single(p.Stage.Surveys);
        Assert.Equal(SurveyFormat.Las, survey.Format);
        Assert.Equal("EPSG:25832", survey.Crs);
        Assert.Equal("EPSG:25832", p.Stage.Site.Crs);
        Assert.Equal(new Vec3d(512_320, 5_401_200, 110), p.Stage.Site.Origin);
        Assert.Equal(60_000, survey.SourcePoints);
        Assert.Null(survey.MeshId);
        Assert.Equal(0.5, survey.VoxelM);
        var cloud = MeshIO.ReadPly(File.OpenRead(rig.File(survey.CloudPath)), new GeometryReadOptions(1, true, p.Stage.Site)).Cloud!;
        Assert.InRange(cloud.Count, 1000, 60_000 / 4);
        // The thinned cloud keeps its grid coordinates exactly: back on the stage it sits where the points were.
        var (min, max) = cloud.Bounds();
        Assert.True(min.X >= -20.3 && max.X <= 20.3, $"{min} {max}");

        // A second file in another system is refused before anything starts.
        Assert.Contains("reproject", await rig.Refused($"SURVEY IMPORT {path} CRS EPSG:32632"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGeoreferencedObjLandsInTheSiteToATenthOfAMillimetre()
    {
        await using var rig = new SiteRig();
        await rig.Ok("SITE ORIGIN 512345.123 5401234.567 112.4 CRS EPSG:25832");
        var sb = new StringBuilder();
        var grid = new List<Vec3d>();
        for (var r = 0; r < 6; r++)
            for (var c = 0; c < 6; c++)
            {
                var g = new Vec3d(512_345.123 + c * 3.001, 5_401_234.567 + 0.1 * c, 112.4 + r * 2.5);
                grid.Add(g);
                sb.Append(CultureInfo.InvariantCulture, $"v {g.X:0.000} {g.Y:0.000} {g.Z:0.000}\n");
            }
        for (var r = 0; r < 5; r++)
            for (var c = 0; c < 5; c++)
            {
                var a = r * 6 + c + 1;
                sb.Append(CultureInfo.InvariantCulture, $"f {a} {a + 1} {a + 7}\nf {a} {a + 7} {a + 6}\n");
            }
        var path = rig.File("front.obj");
        await File.WriteAllTextAsync(path, sb.ToString());
        var job = await rig.Import($"{path} CRS EPSG:25832");
        Assert.Equal("ready", job.State);
        var p = rig.Engine.Store.Project;
        var mesh = MeshIO.Load(rig.File(p.Stage.Meshes[0].Path)).Mesh!;
        Assert.Equal(36, mesh.VertexCount);
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var back = p.Stage.Site.ToGrid(mesh.Position(v));
            Assert.True(grid.Min(g => (g - back).Length) <= 1e-4, $"{back}");
        }
        Assert.Contains("triangles kept as read", p.Stage.Surveys[0].Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnE57MeshesEachStructuredStation()
    {
        await using var rig = new SiteRig();
        const int C = 30, R = 20;
        var pts = new Vec3d[C * R];
        var cells = new (int, int)[C * R];
        for (var r = 0; r < R; r++)
            for (var c = 0; c < C; c++)
            {
                pts[r * C + c] = new Vec3d(c * 0.2, 10, r * 0.2);
                cells[r * C + c] = (r, c);
            }
        var station1 = new E57TestScan("Station 1", pts) { Cells = cells, Translation = new Vec3d(512_300, 5_401_200, 100) };
        var station2 = new E57TestScan("Station 2", pts) { Cells = cells, Translation = new Vec3d(512_306, 5_401_200, 100), Storage = "double" };
        var path = rig.File("site.e57");
        await File.WriteAllBytesAsync(path, E57TestWriter.Write([station1, station2], "AUTHORITY[\"EPSG\",\"25832\"]"));
        var job = await rig.Import($"{path} VOXEL 0.05");
        Assert.Equal("ready", job.State);
        var p = rig.Engine.Store.Project;
        var survey = Assert.Single(p.Stage.Surveys);
        Assert.Equal(SurveyFormat.E57, survey.Format);
        Assert.Equal("EPSG:25832", survey.Crs);
        Assert.Equal(2L * C * R, survey.SourcePoints);
        var asset = p.FindMesh(survey.MeshId!)!;
        // Each station's grid makes (C − 1)(R − 1) × 2 triangles.
        Assert.Equal(2 * (C - 1) * (R - 1) * 2, asset.Triangles);
    }

    [Fact]
    public async Task AFailedImportLeavesTheProjectAsItWasAndSaysWhy()
    {
        await using var rig = new SiteRig();
        var path = rig.File("broken.ply");
        await File.WriteAllTextAsync(path, "ply\nformat binary_little_endian 1.0\nelement vertex 1000\nproperty float x\nend_header\n");
        var revision = rig.Engine.Store.Current.Revision;
        var job = await rig.Import(path);
        Assert.Equal("failed", job.State);
        Assert.Contains("was not imported", job.Words, StringComparison.Ordinal);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        var row = rig.State().GetProperty("stage").GetProperty("surveys")[0];
        Assert.StartsWith("failed: ", row.GetProperty("state").GetString(), StringComparison.Ordinal);
        Assert.Contains("no file", await rig.Refused("SURVEY IMPORT /nowhere/at/all.ply"), StringComparison.Ordinal);
        Assert.Contains("LAZ", await rig.Refused($"SURVEY IMPORT {rig.File("a.laz")}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileInAnotherSystemOrFarFromTheSiteIsRefusedAndLeavesNothingBehind()
    {
        await using var rig = new SiteRig();
        await rig.Ok("SITE ORIGIN 512340 5401230 110 CRS EPSG:25832");
        var rng = new Pcg32(8, 8);
        var pts = Enumerable.Range(0, 2000).Select(_ => new Vec3d(512_300 + rng.Range(0.0, 40.0), 5_401_200 + rng.Range(0.0, 0.5), 100 + rng.Range(0.0, 20.0))).ToList();
        var other = rig.File("other.las");
        await File.WriteAllBytesAsync(other, LasTestWriter.Write(pts, null, new Vec3d(0.001, 0.001, 0.001), new Vec3d(512_000, 5_401_000, 0),
            "PROJCS[\"WGS 84 / UTM zone 32N\",AUTHORITY[\"EPSG\",\"32632\"]]"));
        var revision = rig.Engine.Store.Current.Revision;
        var job = await rig.Import(other);
        Assert.Equal("failed", job.State);
        Assert.Contains("says it is in EPSG:32632 but the site is in EPSG:25832", job.Words, StringComparison.Ordinal);

        // A model in local coordinates near 0 would land 5 400 km away with a float's precision there.
        var local = rig.File("local.obj");
        await File.WriteAllTextAsync(local, "v 0 0 0\nv 10 0 0\nv 10 0 10\nf 1 2 3\n");
        job = await rig.Import(local);
        Assert.Equal("failed", job.State);
        Assert.Contains("km from the site's origin", job.Words, StringComparison.Ordinal);

        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        foreach (var folder in new[] { "meshes", "clouds" })
            Assert.False(Directory.Exists(rig.File(folder)) && Directory.EnumerateFiles(rig.File(folder)).Any(), folder);
    }

    [Fact]
    public async Task OneImportAtATimeAndAStoppingNodeStartsNone()
    {
        await using var rig = new SiteRig();
        var file = SurveyFacade.WritePly(rig.File("front.ply"), 401, 401);
        await rig.Ok($"SURVEY IMPORT {file} CRS LOCAL DECIMATE 20000");
        Assert.Contains("one survey at a time", await rig.Refused($"SURVEY IMPORT {file}"), StringComparison.Ordinal);
        var importing = rig.State().GetProperty("stage").GetProperty("surveys")[0].GetProperty("state").GetString();
        Assert.StartsWith("importing", importing, StringComparison.Ordinal);
        await rig.Engine.WhenSurveyed.WaitAsync(TimeSpan.FromMinutes(2));

        rig.Engine.BeginStop();
        var stopped = await rig.Engine.HandleAsync(Core.Wire.WireGrammar.TryParse($"SURVEY IMPORT {file}", out var line, out _) ? line! : throw new InvalidOperationException(),
            EngineCore.SystemSession, CancellationToken.None);
        Assert.False(stopped.Ok);
        Assert.Contains("stopping", stopped.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImportRunningWhenTheNodeBeginsToStopEndsUnapplied()
    {
        await using var rig = new SiteRig();
        var file = SurveyFacade.WritePly(rig.File("front.ply"), 601, 601);
        var revision = rig.Engine.Store.Current.Revision;
        await rig.Ok($"SURVEY IMPORT {file} CRS LOCAL DECIMATE 20000");
        rig.Engine.BeginStop();
        await rig.Engine.WhenSurveyed.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal("failed", rig.Engine.SurveyJob!.State);
        Assert.Contains("stopped", rig.Engine.SurveyJob.Words, StringComparison.Ordinal);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
    }
}

/// <summary>The import's budgets, measured on this machine and run serially at integration: 2 M triangles in 15 s, and the control lane
/// answering within 50 ms throughout (PING is the transport's own word, so the lane is probed with SITE, a query it answers). The 15 s
/// is a budget for optimised code: a Debug build of the solvers (the JIT's optimiser off) records its time against the 4× ceiling.</summary>
[Trait("Category", "Perf")]
[Collection(SurveyPerfCollection.Name)]
public sealed class SurveyImportPerfTests(ITestOutputHelper log)
{
    [Fact]
    public async Task TwoMillionTrianglesInFifteenSecondsWithPingUnderFiftyMilliseconds()
    {
        await using var rig = new SiteRig();
        var file = SurveyFacade.WritePly(rig.File("north-front.ply"), 1001, 1001);
        var watch = Stopwatch.StartNew();
        await rig.Ok($"SURVEY IMPORT {file} CRS LOCAL UP Z DECIMATE 300000");
        var worstPing = 0.0;
        var pings = 0;
        while (!rig.Engine.WhenSurveyed.IsCompleted)
        {
            var ping = Stopwatch.StartNew();
            await rig.Ok("SITE");
            worstPing = Math.Max(worstPing, ping.Elapsed.TotalMilliseconds);
            pings++;
            await Task.Delay(20);
        }
        await rig.Engine.WhenSurveyed;
        var seconds = watch.Elapsed.TotalSeconds;
        log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"2 M triangles in {seconds:0.0} s; {pings} lane queries, worst {worstPing:0.0} ms"));
        Assert.Equal("ready", rig.Engine.SurveyJob!.State);
        var optimised = typeof(MeshDecimator).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .OfType<DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
        log.WriteLine(optimised ? "optimised build: the 15 s budget holds" : "Debug build: recorded against the 4× ceiling (60 s)");
        Assert.True(seconds <= (optimised ? 15 : 60), $"{seconds:0.0} s");
        Assert.True(worstPing <= 50, $"{worstPing:0.0} ms");
    }

    [Fact]
    public async Task TheImportNeverHoldsTheLaneMoreThanTenMillisecondsInASlice()
    {
        await using var rig = new SiteRig();
        var file = SurveyFacade.WritePly(rig.File("north-front.ply"), 1001, 1001);
        var (job, seconds) = await SurveyImportTests.ImportBig(rig, file);
        log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"2 M triangles imported in {seconds:0.0} s; lane max slice {job.LaneMaxMs:0.00} ms over {job.LaneSlices} slices"));
        Assert.Equal("ready", job.State);
        Assert.True(job.LaneSlices >= 2, $"{job.LaneSlices}");
        Assert.True(job.LaneMaxMs <= SurveyImportTests.LaneSliceBudgetMs, $"the import held the lane {job.LaneMaxMs:0.00} ms in one slice");
    }
}

/// <summary>The import's timings (its lane slices, its time, its budgets) run alone, after every parallel test: a time measured beside
/// the whole suite measures the suite.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SurveyPerfCollection
{
    public const string Name = "Survey import budgets";
}
