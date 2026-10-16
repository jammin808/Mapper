using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Site;
using PlayMap.Core.Wire;
using PlayMap.Engine.Site;

namespace PlayMap.Engine;

/// <summary>A survey import as the node sees it while it runs and after: its id and name, the file, importing / ready / failed with
/// the progress and words, and the longest the import held the control lane in one slice (ms, from its own instrumentation).</summary>
public sealed record SurveyJobView(string Id, string Name, SurveyFormat Format, string Path, string State, double Progress, string Words,
    double LaneMaxMs, int LaneSlices);

/// <summary>SURVEY IMPORT and SURVEY LIST. The lane checks the line and the node and answers accepted at once; a worker reads,
/// places, thins, decimates and writes (<see cref="SurveyImporter"/>); the lane then applies the result as one edit — the mesh asset,
/// a surface over all of it, the survey with its provenance and, when the site was unset, the site the import picked — so one UNDO
/// takes the whole import back, on the live stage (a session holding a blind draft is refused: heavy work is never staged). One
/// import runs at a time; a stopping node starts none, and one running stops at its next step.
/// While it runs, STATE.stage.surveys shows it "importing n %"; a failure is shown "failed: …" until the next import.</summary>
public sealed partial class EngineCore
{
    /// <summary>The default triangle budget of an imported mesh.</summary>
    internal const int SurveyDefaultTriangles = 400_000;

    /// <summary>The default deviation limit of decimation (metres).</summary>
    internal const double SurveyDefaultMaxErrorM = 0.005;

    /// <summary>The default voxel of a thinned cloud (metres).</summary>
    internal const double SurveyDefaultVoxelM = 0.01;

    private const double SurveyProgressStep = 0.02;

    private SurveyJobView? _survey;
    private Task? _surveyTask;
    private double _surveyShown;
    private readonly Lock _surveyGate = new();

    /// <summary>The running or last import (null before the first).</summary>
    public SurveyJobView? SurveyJob => Volatile.Read(ref _survey);

    /// <summary>Completes when the running import has been applied or has failed (at once when none runs).</summary>
    public Task WhenSurveyed => Volatile.Read(ref _surveyTask) ?? Task.CompletedTask;

    internal static SurveyFormat? SurveyFormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".obj" => SurveyFormat.Obj,
        ".ply" => SurveyFormat.Ply,
        ".gltf" or ".glb" => SurveyFormat.Gltf,
        ".las" => SurveyFormat.Las,
        ".e57" => SurveyFormat.E57,
        ".xyz" or ".txt" or ".asc" => SurveyFormat.Xyz,
        ".pts" => SurveyFormat.Pts,
        ".csv" => SurveyFormat.Csv,
        _ => null,
    };

    private CommandReply SurveyImport(VerbCall call)
    {
        var watch = Stopwatch.StartNew();
        const string Usage = "SURVEY IMPORT <path> [CRS <code>|LOCAL] [UNITS m|cm|mm|ft|usft] [UP Z|Y] [VOXEL <m>] [DECIMATE <triangles>|OFF] [NAME <name>]";
        var args = call.Args;
        if (args.Count < 1)
            return CommandReply.Fail(Usage);
        if (Stopping)
            return CommandReply.Fail("the node is stopping — no survey import starts");
        // Heavy work is never staged (blind edit's rule), and an import's model lands on the stage: from a session with a draft it
        // would write the stage behind the draft's back.
        if (OwnsDraft(call.Session) || DraftForTests?.Invoke(call.Session) is not null)
            return CommandReply.Fail("a survey import is heavy work and is not staged in a blind draft: its model lands on the stage — BLIND COMMIT or BLIND DISCARD first");
        var path = args[0];
        string? crs = null;
        double unit = 1, voxel = SurveyDefaultVoxelM;
        bool? zUp = null;
        var decimate = SurveyDefaultTriangles;
        var name = "";
        for (var i = 1; i < args.Count; i++)
        {
            var word = args[i].ToUpperInvariant();
            if (word == "LOCAL")
            {
                crs = "LOCAL";
                continue;
            }
            if (i + 1 >= args.Count)
                return CommandReply.Fail(Usage);
            var value = args[++i];
            switch (word)
            {
                case "CRS":
                    crs = CrsWord(value);
                    if (crs is null)
                        return CommandReply.Fail($"'{value}' is not a coordinate system — EPSG:<code> or LOCAL");
                    break;
                case "UNITS":
                    if (ControlPointFile.UnitScale(value) is not { } s)
                        return CommandReply.Fail($"UNITS takes m, cm, mm, ft or usft — not {value}");
                    unit = s;
                    break;
                case "UP" when value.Equals("Z", StringComparison.OrdinalIgnoreCase) || value.Equals("Y", StringComparison.OrdinalIgnoreCase):
                    zUp = value.Equals("Z", StringComparison.OrdinalIgnoreCase);
                    break;
                case "VOXEL" when TryNumber(value, out var v) && v is >= 0.001 and <= 10:
                    voxel = v;
                    break;
                case "VOXEL":
                    return CommandReply.Fail("VOXEL is a cube's side in metres, from 0.001 to 10");
                case "DECIMATE" when value.Equals("OFF", StringComparison.OrdinalIgnoreCase):
                    decimate = 0;
                    break;
                case "DECIMATE" when int.TryParse(value, NumberStyles.None, SiteInv, out var t) && t >= 1000:
                    decimate = t;
                    break;
                case "DECIMATE":
                    return CommandReply.Fail("DECIMATE is a triangle budget of 1000 or more, or OFF");
                case "NAME":
                    name = string.Join(' ', args.Skip(i));
                    i = args.Count;
                    break;
                default:
                    return CommandReply.Fail(Usage);
            }
        }
        if (Path.GetExtension(path).Equals(".laz", StringComparison.OrdinalIgnoreCase))
            return CommandReply.Fail("compressed LAS (LAZ) is not read on this node yet — decompress it to LAS (LASzip at run time is next)");
        if (SurveyFormatOf(path) is not { } format)
            return CommandReply.Fail($"no survey reader for '{Path.GetExtension(path)}' — OBJ, PLY, glTF, LAS, E57, XYZ, PTS or CSV");
        if (!File.Exists(path))
            return CommandReply.Fail($"no file {path} on this machine");
        if (ProjectFolderForFiles is not { } folder)
            return CommandReply.Fail("the project has no folder yet — PROJECT SAVE it first: the survey's mesh and cloud are written beside it");
        var site = Store.Project.Stage.Site;
        if (crs is not null && site.IsSet && crs.StartsWith("EPSG:", StringComparison.Ordinal) && site.Crs.StartsWith("EPSG:", StringComparison.Ordinal)
            && !string.Equals(crs, site.Crs, StringComparison.Ordinal))
            return CommandReply.Fail($"the file is in {crs} but the site is in {site.Crs} — reproject it in your survey software, or import it in the site's system");
        if (name.Length == 0)
            name = Path.GetFileNameWithoutExtension(path);
        var id = Store.Project.NewId(Ids.Survey);
        // A lock that holds the stage (LOCK AREA mapping, or the show armed) would refuse the import's edit at its end: it is refused
        // before the file is read, with the lock's words.
        var live = Store.Project;
        var probe = live.WithStage(st => st with
        {
            Surveys = st.Surveys.Add(new SurveySource(id, name, format, path, "", "", 0, 0, null, "", 0, 0, SurveyState.Importing, 0, "")),
        });
        if (Store.Guard?.Invoke(live, probe) is { } locked)
            return CommandReply.Fail(locked);
        SurveyJobView view;
        lock (_surveyGate)
        {
            if (Volatile.Read(ref _survey) is { State: "importing" } running)
                return CommandReply.Fail(string.Create(SiteInv, $"{running.Name} is importing ({running.Progress:P0}) — one survey at a time; STATE.stage.surveys follows it"));
            view = new SurveyJobView(id, name, format, path, "importing", 0, $"importing {Path.GetFileName(path)}", 0, 1);
            Volatile.Write(ref _survey, view);
            _surveyShown = 0;
        }
        var request = new SurveyRequest(id, name, path, format, unit, zUp ?? format != SurveyFormat.Gltf, crs ?? "", voxel, decimate,
            SurveyDefaultMaxErrorM, site, folder, live.Id);
        var session = call.Session;
        // Its own thread below the lane's priority, never the pool's: the replies every other line awaits continue on the pool.
        var task = Task.Factory.StartNew(() => RunSurvey(request, session), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        Volatile.Write(ref _surveyTask, task);
        FactsChanged();
        var words = $"importing {name} ({format.ToString().ToUpperInvariant()}) as {id}";
        var payload = JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("stage", "accepted");
            w.WriteString("survey", id);
            w.WriteString("effect", words);
            w.WriteEndObject();
        });
        NoteSurveySlice(watch.Elapsed.TotalMilliseconds);
        return CommandReply.Success(payload, words) with { Applied = false };
    }

    /// <summary>The longest single slice of the lane the import has taken, kept on its view.</summary>
    private void NoteSurveySlice(double ms)
    {
        lock (_surveyGate)
        {
            if (Volatile.Read(ref _survey) is { } v)
                Volatile.Write(ref _survey, v with { LaneMaxMs = Math.Max(v.LaneMaxMs, ms) });
        }
    }

    private void SetSurvey(Func<SurveyJobView, SurveyJobView> change, bool shown)
    {
        lock (_surveyGate)
        {
            if (Volatile.Read(ref _survey) is { } v)
                Volatile.Write(ref _survey, change(v));
        }
        if (shown)
            FactsChanged();
    }

    /// <summary>The worker: the import, then its result applied on the lane as one edit — or the failure kept with its words and the
    /// files it wrote removed. Every way out leaves the job ready or failed, never importing.</summary>
    private async Task RunSurvey(SurveyRequest request, SessionInfo session)
    {
        const string StoppedWords = "the node stopped before the survey was applied";
        SurveyProduct product;
        using (var stop = new CancellationTokenSource())
        {
            var progress = new SurveyProgress(this, stop);
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            try
            {
                product = SurveyImporter.Run(request, progress, stop.Token);
            }
            catch (OperationCanceledException)
            {
                FailSurvey(request, null, StoppedWords, session);
                return;
            }
            catch (OutOfMemoryException)
            {
                FailSurvey(request, null, "the file is too large for this machine's memory — thin or split it first", session);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FailSurvey(request, null, ex.Message, session);
                return;
            }
            finally
            {
                Thread.CurrentThread.Priority = ThreadPriority.Normal;
            }
        }
        if (Stopping)
        {
            FailSurvey(request, product, StoppedWords, session);
            return;
        }
        string words;
        try
        {
            var apply = _lane.RunAsync(() => ApplySurvey(request, product, session));
            // A lane that stops drops what is queued on it: once the node is stopping, the apply is waited for a short while only.
            var waitedStopping = 0;
            while (!apply.IsCompleted)
            {
                await Task.WhenAny(apply, Task.Delay(100, CancellationToken.None)).ConfigureAwait(false);
                if (!apply.IsCompleted && Stopping && ++waitedStopping >= 20)
                {
                    FailSurvey(request, product, StoppedWords, session);
                    return;
                }
            }
            words = await apply.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            words = StoppedWords;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            words = ex.Message;
        }
        if (words.Length > 0)
            FailSurvey(request, product, words, session);
    }

    /// <summary>On the lane: the product applied as one edit while the site is the one the import placed it in; "" when applied, else
    /// the reason it was not. The slice is timed into the job's view.</summary>
    private string ApplySurvey(SurveyRequest request, SurveyProduct product, SessionInfo session)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var meshId = "";
            var surfaceId = "";
            var surveyId = request.Id;
            // Live, as the line was decided when it was accepted (it is refused from a session with a blind draft).
            var r = Store.Apply($"survey {request.Name}", p =>
            {
                // The mesh and cloud were written beside the project the import was asked for: another project, or this one saved
                // into another folder, would name files that are not beside it.
                if (p.Id != request.ProjectId)
                    throw new EditRefusedException("another project was opened while the survey was read — import it again into that project");
                if (!string.Equals(ProjectFolderForFiles, request.ProjectFolder, StringComparison.Ordinal))
                    throw new EditRefusedException("the project was saved into another folder while the survey was read — its files are beside the old one; import it again");
                var site = p.Stage.Site;
                if (request.SiteAtStart.IsSet ? site != request.SiteAtStart : site.IsSet)
                    throw new EditRefusedException("the site changed while the survey was read — import it again");
                var taken = new HashSet<string>(p.AllIds(), StringComparer.Ordinal);
                if (taken.Contains(surveyId))
                    surveyId = p.NewId(Ids.Survey);
                MeshAsset? asset = null;
                Surface? surface = null;
                if (product.Mesh is { } m)
                {
                    meshId = p.NewId(Ids.Mesh);
                    taken.Add(meshId);
                    do
                        surfaceId = Ids.New(Ids.Surface);
                    while (!taken.Add(surfaceId));
                    asset = m with { Id = meshId };
                    surface = new Surface(surfaceId, request.Name, meshId, Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#9AA5B1", ["survey"]);
                }
                var crs = product.Crs.Length > 0 ? product.Crs : product.Frame.Crs;
                var source = new SurveySource(surveyId, request.Name, request.Format, request.Path, product.SourceHash, crs.Length > 0 ? crs : "LOCAL",
                    product.SourcePoints, product.Mesh is null ? 0 : (long)Math.Max(product.SourceTriangles, 0), asset?.Id, product.CloudPath,
                    product.Mesh is null ? request.VoxelM : 0, product.DecimationErrorM, SurveyState.Ready, Services.Wall.UnixMs, product.Words);
                return p.WithStage(st => Core.Site.ControlBinding.Rebind(st with
                {
                    Site = request.SiteAtStart.IsSet ? st.Site : product.Frame,
                    Meshes = asset is null ? st.Meshes : st.Meshes.Add(asset),
                    Surfaces = surface is null ? st.Surfaces : st.Surfaces.Add(surface),
                    Surveys = st.Surveys.Add(source),
                }));
            });
            if (!r.Changed)
                return r.Effect;
            var words = $"imported {request.Name} ({surveyId}): {product.Words}"
                + (meshId.Length > 0 ? $"; mesh {meshId} on surface {surfaceId}" : "")
                + (request.SiteAtStart.IsSet ? "" : string.Create(SiteInv, $"; the site is {product.Frame.Crs} at {product.Frame.Origin.X:0.###} {product.Frame.Origin.Y:0.###} {product.Frame.Origin.Z:0.###}"));
            Journal.Add("verb", words, session.ClientName, $"SURVEY IMPORT {request.Path}", true);
            SetSurvey(v => v with { Id = surveyId, State = "ready", Progress = 1, Words = words }, shown: false);
            return "";
        }
        finally
        {
            NoteSurveySlice(watch.Elapsed.TotalMilliseconds);
            SetSurvey(v => v with { LaneSlices = v.LaneSlices + 1 }, shown: true);
        }
    }

    /// <summary>The import failed: its words kept on the job (and journalled while the node runs), and the files it wrote beside
    /// the project removed — nothing references them.</summary>
    private void FailSurvey(SurveyRequest request, SurveyProduct? product, string why, SessionInfo session)
    {
        foreach (var relative in product?.Files ?? [])
        {
            try
            {
                File.Delete(Path.Combine(request.ProjectFolder, relative));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                why += $" ({relative} is left beside the project: {ex.Message})";
            }
        }
        var words = $"{request.Name} was not imported: {why}";
        if (!Stopping)
            Journal.Add("refused", words, session.ClientName, $"SURVEY IMPORT {request.Path}", false);
        SetSurvey(v => v with { State = "failed", Words = words }, shown: true);
    }

    /// <summary>The worker's progress: kept for STATE (its revision moved every <see cref="SurveyProgressStep"/>); a node that has
    /// begun to stop cancels the import at its next report.</summary>
    private sealed class SurveyProgress(EngineCore engine, CancellationTokenSource stop) : IProgress<double>
    {
        public void Report(double value)
        {
            if (engine.Stopping)
                stop.Cancel();
            var v = Math.Clamp(value, 0, 1);
            var shown = v - engine._surveyShown >= SurveyProgressStep || v >= 1;
            if (shown)
                engine._surveyShown = v;
            engine.SetSurvey(s => s.State == "importing" ? s with { Progress = v } : s, shown);
        }
    }

    // ---- SURVEY LIST and the rows STATE shows ----

    internal sealed record SurveyRow(string Id, string Name, string Format, string State, long Points, long Triangles, string? Mesh, SurveySource? Source);

    /// <summary>The project's surveys, then the running import or the last failed one.</summary>
    internal List<SurveyRow> SurveyRows(Stage stage)
    {
        var rows = stage.Surveys.Select(s => new SurveyRow(s.Id, s.Name, s.Format.ToString().ToLowerInvariant(),
            s.State switch { SurveyState.Ready => "ready", SurveyState.Failed => "failed: " + s.Notes, _ => "importing" },
            s.SourcePoints, s.SourceTriangles, s.MeshId, s)).ToList();
        if (SurveyJob is { State: "importing" or "failed" } job && rows.TrueForAll(r => r.Id != job.Id))
        {
            var state = job.State == "importing"
                ? string.Create(SiteInv, $"importing {Math.Round(job.Progress * 100):0} %")
                : "failed: " + job.Words;
            rows.Add(new SurveyRow(job.Id, job.Name, job.Format.ToString().ToLowerInvariant(), state, 0, 0, null, null));
        }
        return rows;
    }

    private void WriteSurveyRows(Utf8JsonWriter w, Stage stage, bool provenance)
    {
        w.WriteStartArray();
        foreach (var row in SurveyRows(stage))
        {
            w.WriteStartObject();
            w.WriteString("id", row.Id);
            w.WriteString("name", row.Name);
            w.WriteString("format", row.Format);
            w.WriteString("state", row.State);
            w.WriteNumber("points", row.Points);
            w.WriteNumber("triangles", row.Triangles);
            if (row.Mesh is null)
                w.WriteNull("mesh");
            else
                w.WriteString("mesh", row.Mesh);
            if (provenance && row.Source is { } s)
            {
                w.WriteString("path", s.SourcePath);
                w.WriteString("hash", s.SourceHash);
                w.WriteString("crs", s.Crs);
                w.WriteString("cloud", s.CloudPath);
                w.WriteNumber("voxelM", s.VoxelM);
                w.WriteNumber("decimationErrorM", Math.Round(s.DecimationErrorM, 6));
                w.WriteNumber("importedAt", s.ImportedUnixMs);
                w.WriteString("notes", s.Notes);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private CommandReply SurveyList()
    {
        var stage = Edits.Project.Stage;
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("surveys");
            WriteSurveyRows(w, stage, provenance: true);
            w.WriteEndObject();
        }));
    }
}
