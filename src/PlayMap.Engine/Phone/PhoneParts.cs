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
            PreflightChecks = [.. services.PreflightChecks, new PhonePreflight()],
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
                : new PreflightRow("phone:" + rig.Id, node, Section, rig.Name, Light.Amber, "not connected",
                    hub is null ? "this node has no phone hub, so a scan through this phone cannot run"
                    : facts is null ? $"{rig.Name} is not paired with this node (its phone was forgotten, or never finished joining): a scan through it would fail"
                    : $"{facts.Words}: a scan through it would fail", hub is null ? null : invite));
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("phone", node, Section, "Phones", Light.Green, "none", "no phone is a camera of this project", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }

    private static string Quote(string name) => name.Contains(' ', StringComparison.Ordinal) ? "\"" + name.Replace("\"", "", StringComparison.Ordinal) + "\"" : name;
}

/// <summary>INDEX section "scan": one row per phone camera with its connection as the light, and a preset per phone — the picture
/// again (SCAN NEXT), cancel the scan, the phones' list.</summary>
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
        w.WriteEndArray();
    }
}
