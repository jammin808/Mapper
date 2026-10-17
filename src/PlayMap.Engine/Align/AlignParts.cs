using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Align;

/// <summary>Preflight's align rows: every output with a measured correction has its files where the project names them, unchanged,
/// and read (red with the line that returns it to its fallback when not; a stack member's lattice alone, drawn from the project, has
/// nothing to read and is green with its residual); an output whose group has a staged run waiting is amber
/// with the line that accepts it; a projector whose own geometry or blend fights a measured correction (the double-correction guard's
/// last read) is amber with the line that turns it off. A node with no measured correction answers one green row.</summary>
internal sealed class AlignPreflight : IPreflightCheck
{
    public string Section => "align";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var p = engine.Store.Project;
        var node = engine.Services.Settings.NodeId;
        var folder = engine.ProjectFolderForFiles;
        await engine.AlignFiles.Settled.WaitAsync(ct).ConfigureAwait(false);
        var set = engine.AlignMeasured;
        var rows = new List<PreflightRow>();
        foreach (var o in p.Outputs)
        {
            if (o.Measured is not { } m)
                continue;
            var group = p.Stage.Aligns.FirstOrDefault(g => g.OutputIds.Contains(o.Id));
            var fix = o.FallbackId is not null && group is not null ? $"ALIGN REVERT {group.Id}" : $"ALIGN CLEAR {o.Id}";
            var bad = await Task.Run(() => Unreadable(folder, m), ct).ConfigureAwait(false);
            if (bad.Count > 0)
            {
                rows.Add(new PreflightRow("align:" + o.Id, node, Section, o.Name, Light.Red, "missing",
                    $"{string.Join(", ", bad)} missing or changed — {o.Name} runs without its measured correction", fix));
                continue;
            }
            if (set.Faults.TryGetValue(o.Id, out var fault) || (MeasuredLibrary.Reads(m) && !set.Loads.ContainsKey(o.Id)))
            {
                rows.Add(new PreflightRow("align:" + o.Id, node, Section, o.Name, Light.Red, "not loaded", fault ?? $"{o.Name}'s measured correction is not loaded", fix));
                continue;
            }
            if (group is not null && engine.AlignBackend.View is { State: "staged" } v && v.GroupId == group.Id)
            {
                rows.Add(new PreflightRow("align:" + o.Id, node, Section, o.Name, Light.Amber, "staged", $"a run waits for a person: {v.Id}", $"ALIGN ACCEPT {v.Id}"));
                continue;
            }
            rows.Add(new PreflightRow("align:" + o.Id, node, Section, o.Name, Light.Green, "ok", m.Warp is null && m.Lattice is not null
                ? string.Create(CultureInfo.InvariantCulture, $"{StackName(p, m.StackId)}'s lattice, {m.ResidualPx:0.00} px from its master{(m.Color is null ? "" : "; gains loaded")}")
                : string.Create(CultureInfo.InvariantCulture, $"{m.RunId}: warp {m.ResidualPx:0.00} px, files present and loaded"), null));
        }
        foreach (var (id, name, words, fix) in engine.NativeFights())
            rows.Add(new PreflightRow("align:native:" + id, node, Section, name, Light.Amber, "double correction", words, fix));
        if (rows.Count == 0)
            rows.Add(new PreflightRow("align", node, Section, "Alignment", Light.Green, "none", "no output has a measured correction", null));
        return rows;
    }

    private static string StackName(Project p, string stackId) => p.FindStack(stackId)?.Name ?? (stackId.Length > 0 ? stackId : "a stack");

    /// <summary>The files a correction names that are missing, outside the project's folder or no longer what was measured.</summary>
    internal static List<string> Unreadable(string? folder, MeasuredCorrection m)
    {
        var bad = new List<string>();
        foreach (var (file, hash) in new[] { (m.Warp?.File, m.Warp?.Hash), (m.Blend?.File, m.Blend?.Hash) })
        {
            if (file is null)
                continue;
            try
            {
                if (AlignArchive.Resolve(folder, file) is not { } path || !File.Exists(path)
                    || !string.Equals(Core.Align.Pfm.Hash(PlayMap.Core.Storage.DurableFile.ReadAllBytes(path)), hash, StringComparison.Ordinal))
                    bad.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                bad.Add(file);
            }
        }
        return bad;
    }
}

/// <summary>INDEX section "align": one row per alignment group with its grade's light and its score (or "staged", "running",
/// "none") as the state, and a preset per group — run, accept what is staged, revert to the fallbacks.</summary>
internal sealed class AlignIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "align";

    public IndexPresetHint Hint { get; } = new("Align", "align",
    [
        new IndexKey("Align", "ALIGN RUN $(id)", "align.run", "double"),
        new IndexKey("Accept", "ALIGN ACCEPT $(id)", "align.staged", "instant"),
        new IndexKey("Revert", "ALIGN REVERT $(id)", "align.grade", "hold"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartArray();
        foreach (var g in project.Stage.Aligns)
        {
            var (score, staged, light, _) = engine.AlignGroupState(project, g);
            var running = engine.AlignBackend.View is { IsRunning: true } v && v.GroupId == g.Id;
            w.WriteStartObject();
            w.WriteString("id", g.Id);
            w.WriteString("kind", "align");
            w.WriteString("label", g.Name);
            w.WriteString("parent", "");
            w.WriteString("light", light.ToString().ToLowerInvariant());
            w.WriteString("state", running ? "running" : staged.Length > 0 ? "staged"
                : score is null ? "none" : string.Create(CultureInfo.InvariantCulture, $"{score.Grade} {score.Value:0}"));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

/// <summary>wp2-align's part of the node: the runner, with the render host as the display its patterns go up on, and its preflight
/// check, in the services; the runner stops with the node.</summary>
internal sealed class AlignExtension(IScanDisplay display) : IEngineExtension
{
    private AlignRunner? _runner;
    private volatile EngineCore? _engine;

    public string Name => "align";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var wall = services.Wall;
        // A rig's camera opens as a scan's does: a phone through the node's phone hub (wp2-phonescan), else a capture device.
        _runner = new AlignRunner(display, new AlignRunnerOptions { UnixMs = () => wall.UnixMs, OpenCamera = rig => Scanning.ScanCameras.Open(_engine, rig) });
        return services with { Align = _runner, PreflightChecks = [.. services.PreflightChecks, new AlignPreflight()] };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        _engine = engine;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => _runner?.DisposeAsync() ?? ValueTask.CompletedTask;
}
