using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Led;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;
using PlayMap.Net.Led;

namespace PlayMap.Engine.Led;

/// <summary>wp2-led's part of the node: the sender (on the shared Art-Net socket, which it opens only when it first sends Art-Net),
/// the settings writer for the machine master, and the preflight check. The send thread starts with the engine.</summary>
internal sealed class LedExtension : IEngineExtension
{
    private readonly ExtensionSetup _setup;
    private LedSender? _sender;

    public LedExtension(ExtensionSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
    }

    public string Name => "led";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _sender = new LedSender(new LedSenderOptions
        {
            ArtNet = services.ArtNet,
            MulticastTtl = _setup.Settings.Led.MulticastTtl,
            Clock = services.Mono,
        });
        return services with
        {
            LedOutput = _sender,
            LedPersist = Persist,
            PreflightChecks = [.. services.PreflightChecks, new LedPreflight()],
        };
    }

    /// <summary>Keeps the LED settings for the next start through the network host, the settings file's one writer (it reads the
    /// file fresh under its own lock, so nothing else in it is lost); null, or the words of a failure.</summary>
    private string? Persist(LedSettings settings)
    {
        var failed = _setup.Network.PersistSettings(s => s with { Led = settings });
        return failed.Length == 0 ? null : failed;
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct)
    {
        _sender?.Start();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender is not null)
            await _sender.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Preflight's LED rows: every patched node on this machine reachable — an Art-Net node answered ArtPoll (red with the line
/// that takes it off when it did not, or the sender could not reach it); sACN and DDP receivers do not answer, so they are grey;
/// overlaps are red with the line that re-patches the rig; the bandwidth guard's refusal is red; output off on this machine with a
/// patched rig is amber with the line that turns it on. No rig: one grey row.</summary>
internal sealed class LedPreflight : IPreflightCheck
{
    public string Section => "led";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var p = engine.Store.Project;
        var plan = engine.Led.PlanFor(p);
        var rows = new List<PreflightRow>();
        if (p.Leds.Nodes.IsEmpty)
        {
            rows.Add(new PreflightRow("led", node, Section, "LED", Light.Grey, "none", "the show has no LED rig", null));
            return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
        }
        if (plan.Universes > 0 && !engine.Led.Master)
            rows.Add(new PreflightRow("led:output", node, Section, "LED output", Light.Amber, "off", "this machine does not send LED data", "LED OUTPUT ON"));
        if (plan.Refused.Length > 0)
            rows.Add(new PreflightRow("led:bandwidth", node, Section, "LED bandwidth", Light.Red, "refused", plan.Refused, null));
        foreach (var c in plan.Conflicts)
            rows.Add(new PreflightRow($"led:conflict:{c.NodeId}:{c.Universe}:{c.From}", node, Section, "LED patch", Light.Red, "overlap",
                $"{string.Join(" and ", c.Fixtures)} share {c.Universe}.{c.From}–{c.Universe}.{c.To}", "LED AUTOPATCH ALL"));
        var view = engine.Led.Output?.View();
        foreach (var r in engine.LedNodeRows(p, plan, view))
        {
            if (!r.Node.Enabled || r.Universes == 0)
                continue;
            var item = $"{r.Node.Name} ({r.Node.Host})";
            if (r.Node.Protocol != LedProtocol.ArtNet)
            {
                rows.Add(new PreflightRow("led:" + r.Node.Id, node, Section, item, Light.Grey, r.State,
                    r.Node.Protocol == LedProtocol.Sacn ? "sACN receivers do not answer: reachability is not known" : "DDP devices are not asked this round: reachability is not known", null));
                continue;
            }
            if (r.State is "standby" or "held")
            {
                rows.Add(new PreflightRow("led:" + r.Node.Id, node, Section, item, r.State == "standby" ? Light.Green : Light.Grey, r.State, "not polled: " + r.Why, null));
                continue;
            }
            if (!engine.Led.Master || r.Sent is null)
            {
                rows.Add(new PreflightRow("led:" + r.Node.Id, node, Section, item, Light.Grey, r.State, "not polled: this machine is not sending LED data", null));
                continue;
            }
            var (light, note) = r.State == "unreachable" ? (Light.Red, r.Why)
                : r.Sent.Seen is { } seen ? (Light.Green, $"answered ArtPoll as {seen.Name} ({seen.RefreshHz} Hz)")
                : (Light.Red, "has not answered ArtPoll — check its address and cable, or take it off");
            rows.Add(new PreflightRow("led:" + r.Node.Id, node, Section, item, light, r.State, note, light == Light.Red ? $"LED OUTPUT OFF {r.Node.Id}" : null));
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("led", node, Section, "LED", Light.Grey, "idle", "nothing is patched", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}

/// <summary>INDEX section "led": one row per node (its light and state as STATE tells them), and a preset per node — output on and
/// off, a chase test on it — with the rig's level on an encoder (a turn is a BY step).</summary>
internal sealed class LedIndex(EngineCore engine) : IIndexContributor
{
    public string Section => "led";

    public IndexPresetHint Hint { get; } = new("LED", "lednode",
    [
        new IndexKey("On", "LED OUTPUT ON $(id)", "led.node", "instant"),
        new IndexKey("Off", "LED OUTPUT OFF $(id)", "led.node", "instant"),
        new IndexKey("Test", "LED TEST CHASE ON $(id)", "led.test", "instant"),
        new IndexKey("Level +", "LED LEVEL BY 5", "led.level", "hold"),
        new IndexKey("Level −", "LED LEVEL BY -5", "led.level", "hold"),
    ]);

    public void WriteRows(Project project, EngineSnapshot snapshot, Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(w);
        var plan = engine.Led.PlanFor(project);
        w.WriteStartArray();
        foreach (var r in engine.LedNodeRows(project, plan, engine.Led.Output?.View()))
        {
            w.WriteStartObject();
            w.WriteString("id", r.Node.Id);
            w.WriteString("kind", "lednode");
            w.WriteString("label", r.Node.Name);
            w.WriteString("parent", "");
            w.WriteString("light", r.State switch
            {
                "sending" or "standby" => "green",
                "late" => "amber",
                "unreachable" or "refused" => "red",
                _ => "grey",
            });
            w.WriteString("state", r.State);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
