using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Show;
using PlayMap.Devices.Model;

namespace PlayMap.Engine.Show;

/// <summary>Runs a preflight's checks side by side, each under its own budget measured on the node's monotonic clock: a check
/// past its budget is cancelled and reported as a grey row "did not finish in 5 s" (unknown stays grey), a check that throws as a
/// grey row with its words, and the report is complete only when every check finished.</summary>
public static class PreflightRunner
{
    /// <summary>How often the runner looks at the clock while checks run (the budget itself is on the monotonic clock).</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    public static async Task<(IReadOnlyList<PreflightRow> Rows, bool Complete)> RunAsync(IReadOnlyList<IPreflightCheck> checks,
        PreflightContext context, Core.Time.IMonoClock mono, string node, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mono);
        var start = mono.NowNs;
        var runs = checks.Where(c => c.MinDepth <= context.Depth).Select(c =>
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            return (Check: c, Cts: cts, Task: Task.Run(() => c.RunAsync(context, cts.Token), CancellationToken.None));
        }).ToList();
        var rows = new IReadOnlyList<PreflightRow>?[runs.Count];
        var complete = true;
        try
        {
            while (true)
            {
                var open = 0;
                for (var i = 0; i < runs.Count; i++)
                {
                    if (rows[i] is not null)
                        continue;
                    var (check, cts, task) = runs[i];
                    if (task.IsCompletedSuccessfully)
                    {
                        rows[i] = await task.ConfigureAwait(false);
                    }
                    else if (task.IsCompleted)
                    {
                        rows[i] = [Grey(node, check, "could not check: " + (task.Exception?.InnerException?.Message ?? "cancelled"))];
                        complete = false;
                    }
                    else if (mono.NowNs - start >= check.Budget.Ticks * 100)
                    {
                        await cts.CancelAsync().ConfigureAwait(false);
                        rows[i] = [Grey(node, check, $"did not finish in {Seconds(check.Budget)}")];
                        complete = false;
                    }
                    else
                    {
                        open++;
                    }
                }
                if (open == 0)
                    break;
                await Task.WhenAny(Task.WhenAny(runs.Where((_, i) => rows[i] is null).Select(r => r.Task)), Task.Delay(Poll, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            foreach (var r in runs)
                r.Cts.Dispose();
        }
        return ([.. rows.SelectMany(r => r ?? [])], complete);
    }

    private static PreflightRow Grey(string node, IPreflightCheck check, string note) =>
        new($"{check.Section}:check", node, check.Section, check.Section, Light.Grey, "unknown", note, null);

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
}

/// <summary>Outputs on present displays: every enabled display output names a display this machine has now (or takes the first
/// free one, when it names none and one exists). Fix: point it at a present display, or turn it off.</summary>
public sealed class OutputsCheck : IPreflightCheck
{
    public string Section => "outputs";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var e = context.Engine;
        var node = e.Services.Settings.Name;
        var displays = e.Services.Outputs.Displays();
        var rows = new List<PreflightRow>();
        foreach (var o in e.Store.Project.Outputs.Where(o => o.Enabled && o.Kind != OutputKind.Virtual))
        {
            var id = "outputs:" + o.Id;
            if (o.DisplayKey.Length == 0)
            {
                rows.Add(displays.Count > 0
                    ? new PreflightRow(id, node, Section, o.Name, Light.Green, displays[0].Name, "takes the first free display", null)
                    : new PreflightRow(id, node, Section, o.Name, Light.Red, "no display", "this machine has no display for it", $"OUTPUT ENABLE {o.Id} OFF"));
                continue;
            }
            if (displays.FirstOrDefault(d => d.Key == o.DisplayKey) is { } shown)
            {
                rows.Add(new PreflightRow(id, node, Section, o.Name, Light.Green, shown.Name,
                    string.Create(CultureInfo.InvariantCulture, $"{shown.Width}×{shown.Height} at {shown.RefreshHz:0.##} Hz"), null));
                continue;
            }
            var fix = displays.Count > 0 ? $"SET {o.Id} displayKey \"{displays[0].Key}\"" : $"OUTPUT ENABLE {o.Id} OFF";
            rows.Add(new PreflightRow(id, node, Section, o.Name, Light.Red, o.DisplayKey,
                $"its display {o.DisplayKey} is not connected{(displays.Count > 0 ? $" ({displays.Count} other{(displays.Count == 1 ? " is" : "s are")})" : "")}", fix));
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("outputs:none", node, Section, "Outputs", Light.Grey, "none", "no display output is on", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}

/// <summary>Media present and readable: every file the show uses (a playlist's items, a layer, a surface, an output's source) is
/// on this machine and decodable. Fix: fetch it from a peer.</summary>
public sealed class MediaCheck : IPreflightCheck
{
    public string Section => "media";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(5);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var e = context.Engine;
        var node = e.Services.Settings.Name;
        var p = e.Store.Project;
        var rows = new List<PreflightRow>();
        var used = UsedMedia(p);
        if (e.Services.Media is not { } media)
        {
            rows.Add(new PreflightRow("media:backend", node, Section, "Media", used.Count > 0 ? Light.Red : Light.Grey, "no decoder",
                "this node has no media backend", null));
            return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
        }
        foreach (var m in p.Media.Where(m => m.IsFile && used.Contains(m.Id)))
        {
            ct.ThrowIfCancellationRequested();
            var r = media.Readiness(m);
            rows.Add(!r.Present ? new PreflightRow("media:" + m.Id, node, Section, m.Name, Light.Red, "missing", $"{m.Name} is not on this machine", $"MEDIA FETCH {m.Id}")
                : !r.Decodable ? new PreflightRow("media:" + m.Id, node, Section, m.Name, Light.Red, "unreadable", r.Words.Length > 0 ? r.Words : "it does not decode", null)
                : !r.Verified ? new PreflightRow("media:" + m.Id, node, Section, m.Name, Light.Amber, "unverified", "present but its hash is not checked yet", null)
                : new PreflightRow("media:" + m.Id, node, Section, m.Name, Light.Green, "ready", "present and readable", null));
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("media:none", node, Section, "Media", Light.Grey, "none", "the show uses no media files", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }

    /// <summary>The media ids the show uses: playlist items, layers, surfaces and outputs, nested playlists and canvases included.</summary>
    public static HashSet<string> UsedMedia(Project p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Add(SourceRef s)
        {
            if (s.Kind == SourceKind.Media)
                used.Add(s.Id);
        }
        foreach (var l in p.Playlists)
            foreach (var i in l.Items)
                Add(i.Source);
        foreach (var c in p.Canvases)
            foreach (var l in c.Layers)
                Add(l.Source);
        foreach (var s in p.Stage.Surfaces)
            Add(s.Content.Source);
        foreach (var o in p.Outputs)
            Add(o.Source);
        return used;
    }
}

/// <summary>Linked projectors reachable: every device link answers, is on, and its shutter is open. Fix: power it on, open it.</summary>
public sealed class DevicesCheck(Func<string, DeviceState?>? states = null) : IPreflightCheck
{
    public string Section => "devices";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var e = context.Engine;
        var node = e.Services.Settings.Name;
        var stateOf = states ?? (id => e.Services.Devices?.State(id));
        var rows = new List<PreflightRow>();
        foreach (var d in e.Store.Project.Stage.Devices)
        {
            var id = "devices:" + d.Id;
            var s = stateOf(d.Id);
            if (s is null || s.Liveness == DeviceLiveness.Unknown)
                rows.Add(new PreflightRow(id, node, Section, d.Name, Light.Grey, "not heard", $"{d.Name} has not answered yet", null));
            else if (!s.Online || s.Liveness is DeviceLiveness.Gone or DeviceLiveness.Stale)
                rows.Add(new PreflightRow(id, node, Section, d.Name, Light.Red, "unreachable", $"{d.Name} does not answer at {d.Host} — check its network (DEVICE GET {d.Id})", null));
            else if (s.Power is PowerState.Standby or PowerState.CoolingDown)
                rows.Add(new PreflightRow(id, node, Section, d.Name, Light.Red, "standby", $"{d.Name} is in standby", $"DEVICE POWER {d.Id} ON"));
            else if (s.ShutterClosed == true)
                rows.Add(new PreflightRow(id, node, Section, d.Name, Light.Amber, "shutter closed", $"{d.Name}'s shutter is closed", $"DEVICE SHUTTER {d.Id} OPEN"));
            else
                rows.Add(new PreflightRow(id, node, Section, d.Name, Light.Green, s.Power == PowerState.WarmingUp ? "warming up" : "on", "answers, on, shutter open", null));
        }
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}

/// <summary>Free disk on the media volume: red under twice the largest file the show uses (a fetch or a re-copy would not fit),
/// amber under 10 GB.</summary>
public sealed class DiskCheck(Func<string, long?>? freeBytes = null) : IPreflightCheck
{
    private const long Gb = 1L << 30;

    public string Section => "disk";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var e = context.Engine;
        var node = e.Services.Settings.Name;
        var p = e.Store.Project;
        var folder = MediaFolder(e);
        if (folder is null)
            return Rows(new PreflightRow("disk:media", node, Section, "Media volume", Light.Grey, "unknown", "no media folder on this node", null));
        var free = (freeBytes ?? Free)(folder);
        if (free is not { } bytes)
            return Rows(new PreflightRow("disk:media", node, Section, "Media volume", Light.Grey, "unknown", $"the free space of {folder} cannot be read", null));
        var used = MediaCheck.UsedMedia(p);
        var largest = p.Media.Where(m => used.Contains(m.Id)).Select(m => m.SizeBytes).DefaultIfEmpty(0).Max();
        var value = string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)Gb:0.0} GB free");
        var row = bytes < 2 * largest
            ? new PreflightRow("disk:media", node, Section, "Media volume", Light.Red, value,
                string.Create(CultureInfo.InvariantCulture, $"less than twice the largest file ({largest / (double)Gb:0.0} GB)"), null)
            : bytes < 10 * Gb
                ? new PreflightRow("disk:media", node, Section, "Media volume", Light.Amber, value, "under 10 GB free", null)
                : new PreflightRow("disk:media", node, Section, "Media volume", Light.Green, value, folder, null);
        return Rows(row);
    }

    private static Task<IReadOnlyList<PreflightRow>> Rows(PreflightRow row) => Task.FromResult<IReadOnlyList<PreflightRow>>([row]);

    /// <summary>Where the show's media lives: beside the first media file it uses, else the node's project folder.</summary>
    private static string? MediaFolder(EngineCore e)
    {
        var p = e.Store.Project;
        var used = MediaCheck.UsedMedia(p);
        var first = p.Media.FirstOrDefault(m => m.IsFile && used.Contains(m.Id) && Path.IsPathRooted(m.Location));
        return first is not null ? Path.GetDirectoryName(first.Location) : e.ProjectFolderForFiles;
    }

    private static long? Free(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The show's safety: armed (grey in rehearsal, amber when the show expects to be armed), and every SAFE plan's look
/// present. Fix: SHOW ARM; plan black instead.</summary>
public sealed class SafetyCheck : IPreflightCheck
{
    public string Section => "safety";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var e = context.Engine;
        var node = e.Services.Settings.Name;
        var p = e.Store.Project;
        var rules = p.Show.Rules;
        var rows = new List<PreflightRow>
        {
            context.Armed
                ? new PreflightRow("safety:armed", node, Section, "Show Mode", Light.Green, "armed", $"armed by {e.ShowNow.By}", null)
                : new PreflightRow("safety:armed", node, Section, "Show Mode", rules.ExpectArmed ? Light.Amber : Light.Grey, "rehearsal",
                    rules.ExpectArmed ? "the show expects to be armed" : "rehearsal: every edit goes live", "SHOW ARM"),
        };
        foreach (var plan in p.Show.Panic.Where(x => x.Outcome == PanicOutcome.Safe))
        {
            var name = p.FindOutput(plan.OutputId)?.Name ?? plan.OutputId;
            var look = plan.SafeLook;
            var missing = look is not { } l || (l.Kind == SourceKind.Media && p.FindMedia(l.Id) is null)
                || (l.Kind == SourceKind.Generator && p.FindGenerator(l.Id) is null) || (l.Kind == SourceKind.Canvas && p.FindCanvas(l.Id) is null)
                || (l.Kind == SourceKind.Playlist && p.FindPlaylist(l.Id) is null);
            rows.Add(missing
                ? new PreflightRow("safety:" + plan.OutputId, node, Section, name, Light.Red, "no safe look", $"PANIC on {name} would show a look that is not in the show", $"PANIC SET {plan.OutputId} BLACK")
                : new PreflightRow("safety:" + plan.OutputId, node, Section, name, Light.Green, "safe look", look!.ToString(), null));
        }
        rows.Add(new PreflightRow("safety:panic", node, Section, "Panic", Light.Green, PanicMath.Json(rules.DefaultOutcome),
            string.Create(CultureInfo.InvariantCulture, $"{p.Show.Panic.Length} output plan{(p.Show.Panic.Length == 1 ? "" : "s")}; the rest {PanicMath.Json(rules.DefaultOutcome)} over {rules.DefaultFadeMs} ms"), null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}
