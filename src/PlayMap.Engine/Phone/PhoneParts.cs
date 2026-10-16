using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;
using PlayMap.Net.Phone;

namespace PlayMap.Engine.Phone;

/// <summary>
/// wp2-phonescan's part of the node: the phone hub with its keys in the node's folder (phones.json), its page and socket under /scan of
/// the web port (added before the port opens), the hub and the invite's door in the services, the preflight row; once the engine
/// runs, a joining phone becomes a camera rig on the lane and every phone fact moves STATE.
/// </summary>
internal sealed class PhoneExtension : IEngineExtension
{
    /// <summary>The factory the exe's composition adds (EngineParts.Phone.cs).</summary>
    public static readonly EngineExtensionFactory Factory = setup => new PhoneExtension(setup);

    private readonly PhoneHub _hub;
    private readonly NetworkHost _network;
    private readonly NodeSettings _settings;

    public PhoneExtension(ExtensionSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _settings = setup.Settings;
        _network = setup.Network;
        var phone = setup.Settings.Phone;
        _hub = new PhoneHub(new PhoneHubOptions
        {
            KeysPath = Path.Combine(setup.Home.Folder, "phones.json"),
            NodeId = setup.Settings.NodeId,
            NodeName = setup.Settings.Name,
            MaxConnected = Math.Clamp(phone.MaxConnected, 1, 16),
            Enabled = () => phone.Enabled,
            Log = setup.Logs.CreateLogger("PlayMap.Phone"),
        });
        setup.Network.AddWebModule(_hub.Module);
    }

    public string Name => "phones";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var network = _network;
        var bind = _settings.Bind;
        return services with
        {
            Phones = _hub,
            PhoneDoor = (session, ct) => PhoneDoors.FindAsync(network.HttpPort, bind, session, ct),
            PreflightChecks = [.. services.PreflightChecks, new PhonePreflight(), new ScanDataPreflight()],
        };
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.AttachPhones(_hub);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => _hub.DisposeAsync();
}

/// <summary>Preflight's phone rows: each phone camera rig the project has, amber with the line that invites it again when its phone is
/// not connected (a scan through it would fail), green when it is.</summary>
internal sealed class PhonePreflight : IPreflightCheck
{
    public string Section => "scan";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var hub = engine.Services.Phones;
        var rows = new List<PreflightRow>();
        foreach (var rig in engine.Store.Project.Stage.Cameras.Where(c => c.SourceKind == CameraSourceKind.Phone))
        {
            var facts = hub?.Phones().FirstOrDefault(p => string.Equals(p.Id, rig.Source, StringComparison.Ordinal));
            var invite = string.Create(CultureInfo.InvariantCulture, $"PHONE INVITE {Quote(rig.Name)}");
            rows.Add(facts is { Connected: true }
                ? new PreflightRow("phone:" + rig.Id, node, Section, rig.Name, Light.Green, "connected", facts.Words, null)
                // A phone the node refused says what to do in its own words (the show's lock); an invite would be refused the same way.
                : facts is { Refused: true }
                    ? new PreflightRow("phone:" + rig.Id, node, Section, rig.Name, Light.Amber, "refused", $"{facts.Words}: a scan through it would fail", null)
                : new PreflightRow("phone:" + rig.Id, node, Section, rig.Name, Light.Amber, "not connected",
                    hub is null ? "this node has no phone hub, so a scan through this phone cannot run"
                    : facts is null ? $"{rig.Name} is not paired with this node (its phone was forgotten, or never finished joining): an invite under its name pairs this rig again"
                    : $"{facts.Words}: a scan through it would fail", hub is null ? null : invite));
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("phone", node, Section, "Phones", Light.Green, "none", "no phone is a camera of this project", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }

    private static string Quote(string name) => name.Contains(' ', StringComparison.Ordinal) ? "\"" + name.Replace("\"", "", StringComparison.Ordinal) + "\"" : name;
}

/// <summary>Preflight's scan-data rows (wp2-multiscan): each scan the stage depends on — its mesh on the stage, or a projector's current
/// calibration from it — is on disk (its
/// folder's scan.json beside the project), so its projectors' placement can be re-solved, a view added or a surface made after a
/// restart. Green when it is; amber, with the line that scans those projectors again, when it is not.</summary>
internal sealed class ScanDataPreflight : IPreflightCheck
{
    public string Section => "scan";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var project = engine.Store.Project;
        var folder = engine.ProjectFolderForFiles;
        var rows = new List<PreflightRow>();
        // What the stage depends on: a scan whose mesh is on it, or one a projector's current calibration came from.
        var placedFrom = project.Stage.Projectors.Select(p => project.FindCalibration(p.CalibrationId)?.ScanId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var scan in project.Stage.Scans.Where(s => s.Kind == ScanKind.StructuredLight && (s.MeshId is not null || placedFrom.Contains(s.Id))))
        {
            var placed = scan.ProjectorIds.Select(id => project.FindProjector(id)?.Name).OfType<string>().ToList();
            var on = folder is not null && scan.DataPath.Length > 0 && File.Exists(Path.Combine(folder, scan.DataPath, "scan.json"));
            var again = string.Create(CultureInfo.InvariantCulture, $"SCAN START {scan.CameraId ?? "<camera>"} {string.Join(' ', scan.ProjectorIds)}");
            rows.Add(on
                ? new PreflightRow("scan:" + scan.Id, node, Section, scan.Name, Light.Green, "on disk",
                    $"{scan.DataPath} holds what {string.Join(", ", placed)} {(placed.Count == 1 ? "was" : "were")} placed from", null)
                : new PreflightRow("scan:" + scan.Id, node, Section, scan.Name, Light.Amber, "not on disk",
                    $"{(scan.DataPath.Length > 0 ? scan.DataPath + "/scan.json is missing" : "it was kept in memory by a node that has stopped")}: " +
                    $"{string.Join(", ", placed)} cannot be re-solved, given a view or made into surfaces from it — scan again", again));
        }
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}

/// <summary>INDEX section "scan": one row per phone camera with its connection as the light, one per surface suggestion waiting for a
/// person (its row id the proposal that approves it), and a preset per phone — the picture again (SCAN NEXT), cancel the scan, the
/// phones' list.</summary>
internal sealed class PhoneIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "scan";

    public IndexPresetHint Hint { get; } = new("Scan", "phone",
    [
        new IndexKey("Next picture", "SCAN NEXT", "scan.waiting", "instant"),
        new IndexKey("Cancel scan", "SCAN CANCEL", "scan.running", "hold"),
        new IndexKey("Phones", "PHONE LIST", "phone.connected", "instant"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(w);
        var phones = engine.Services.Phones?.Phones() ?? [];
        var running = engine.Services.Scan.Current is { State: "starting" or "running" };
        w.WriteStartArray();
        foreach (var rig in project.Stage.Cameras.Where(c => c.SourceKind == CameraSourceKind.Phone))
        {
            var facts = phones.FirstOrDefault(p => string.Equals(p.Id, rig.Source, StringComparison.Ordinal));
            w.WriteStartObject();
            w.WriteString("id", rig.Id);
            w.WriteString("kind", "phone");
            w.WriteString("label", rig.Name);
            w.WriteString("parent", "");
            w.WriteString("light", facts is not { Connected: true } ? "grey" : running ? "amber" : "green");
            w.WriteString("state", facts is not { Connected: true } ? "not connected" : running ? "scanning" : "ready");
            w.WriteEndObject();
        }
        // The surface suggestions waiting for a person (wp2-multiscan): one row each, under its scan, with the line that approves it.
        if (engine.Services.Scan is Scanning.ScanRunner runner)
        {
            foreach (var (scan, s, _) in runner.Waiting())
            {
                w.WriteStartObject();
                w.WriteString("id", s.Proposals.FirstOrDefault() ?? s.Id);
                w.WriteString("kind", "suggestion");
                w.WriteString("label", s.Label);
                w.WriteString("parent", scan);
                w.WriteString("light", "amber");
                w.WriteString("state", "waits for a person — APPROVE " + (s.Proposals.FirstOrDefault() ?? s.Id));
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
    }
}
