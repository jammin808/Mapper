using PlayMap.Core.Bus;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Tc;
using PlayMap.Engine.Host;
using PlayMap.Net.Control;

namespace PlayMap.Engine.Control;

/// <summary>wp2-control's part of the node: the one Art-Net socket (in the services for every consumer: DMX input, Art-Net timecode,
/// LED output; it opens when the first of them needs it), the control service (attached once the engine runs), its faults and its
/// preflight checks. Changed control settings are written back into the node's settings file.</summary>
public sealed class ControlExtension : IEngineExtension
{
    private readonly ExtensionSetup _setup;
    private ArtNetEndpoint? _artNet;
    private ControlService? _service;

    public ControlExtension(ExtensionSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
    }

    public string Name => "control";

    public EngineServices Configure(EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var settings = _setup.Settings.Control;
        try
        {
            _artNet = new ArtNetEndpoint(settings.ArtNetInterface);
        }
        catch (ArgumentException)
        {
            _artNet = new ArtNetEndpoint("");
        }
        _service = new ControlService(new ControlServiceOptions { ArtNet = _artNet, Persist = Persist });
        _setup.AddFaults?.Invoke(_service);
        return services with
        {
            ArtNet = _artNet,
            Control = _service,
            PreflightChecks = [.. services.PreflightChecks, new ControlPreflight(), new TimecodePreflight()],
        };
    }

    /// <summary>Writes the control settings into the node's settings file (read fresh, so nothing else in it is lost).</summary>
    private string? Persist(ControlSettings settings)
    {
        try
        {
            var current = _setup.Home.LoadSettings(_setup.Settings.Name);
            _setup.Home.SaveSettings(current with { Control = settings });
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    public Task StartAsync(EngineCore engine, CancellationToken ct) => _service?.AttachAsync(engine) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
            await _service.DisposeAsync().ConfigureAwait(false);
        if (_artNet is not null)
            await _artNet.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Preflight's control rows: every surface a binding names is heard (its MIDI port is here, its OSC, Art-Net or sACN socket
/// is open). A surface whose port is missing is amber with the alias line that names this machine's port; Art-Net or sACN bindings
/// with that input off are red with the line that turns it on; a socket the node could not open is red. No bindings: one grey row.</summary>
internal sealed class ControlPreflight : IPreflightCheck
{
    public string Section => "control";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var p = engine.Store.Project;
        var settings = engine.ControlInput.Settings;
        var service = engine.Services.Control;
        var rows = new List<PreflightRow>();
        var used = p.Control.Bindings.Where(b => b.Enabled)
            .Select(b => Core.Control.InputAddress.Parse(b.Input, out _)).Where(a => a is not null).Select(a => a!.Value).ToList();
        if (used.Count == 0)
        {
            rows.Add(new PreflightRow("control", node, Section, "Control input", Light.Grey, "none", "no control is bound", null));
            return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
        }
        if (!settings.InputEnabled)
            rows.Add(new PreflightRow("control:off", node, Section, "Control input", Light.Red, "off", "every control input is off — the bindings are paused", "CONTROL ENABLE ON"));
        if (used.Any(a => a.Source == Core.Control.InputSource.Osc))
        {
            var open = service?.OscOpen ?? false;
            rows.Add(new PreflightRow("control:osc", node, Section, "OSC", open ? Light.Green : Light.Red, open ? "open" : "closed",
                open ? "listening" : service?.OscProblem ?? "no control service on this node", null));
        }
        if (used.Any(a => a.Source == Core.Control.InputSource.ArtNet))
        {
            var (light, value, note, fix) = !settings.ArtNetEnabled ? (Light.Red, "off", "Art-Net input is off while bindings read DMX", "CONTROL DMX ARTNET ON")
                : service?.ArtNetProblem is { } problem ? (Light.Red, "refused", problem, (string?)null)
                : service is null ? (Light.Grey, "none", "no control service on this node", (string?)null)
                : (Light.Green, "open", "listening for DMX", (string?)null);
            rows.Add(new PreflightRow("control:artnet", node, Section, "Art-Net", light, value, note, fix));
        }
        if (used.Any(a => a.Source == Core.Control.InputSource.Sacn))
        {
            var (light, value, note, fix) = !settings.SacnEnabled ? (Light.Red, "off", "sACN input is off while bindings read DMX", "CONTROL DMX SACN ON")
                : service?.SacnProblem is { } problem ? (Light.Red, "refused", problem, (string?)null)
                : service is null ? (Light.Grey, "none", "no control service on this node", (string?)null)
                : (Light.Green, "open", "listening for DMX", (string?)null);
            rows.Add(new PreflightRow("control:sacn", node, Section, "sACN", light, value, note, fix));
        }
        foreach (var s in p.Control.Surfaces.Where(s => s.Enabled && s.Kind == "midi"))
        {
            if (!used.Any(a => string.Equals(a.Surface, s.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            var port = service?.PortOf(s);
            rows.Add(port is not null
                ? new PreflightRow("control:" + s.Id, node, Section, s.Name, Light.Green, port, "connected", null)
                : new PreflightRow("control:" + s.Id, node, Section, s.Name, Light.Amber, "unplugged",
                    service?.RefusedFor(s) ?? $"{s.Name}'s port is not on this machine — plug it in, or name this machine's port", $"CONTROL ALIAS {s.Name} <port glob>"));
        }
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}

/// <summary>Preflight's timecode rows: every chased playlist's source is present and rolling. Not rolling is amber (red when the
/// input could not open), with the line that hands the playlist back to the operator; a disengaged chase is amber with the line
/// that engages it. No chase: one grey row.</summary>
internal sealed class TimecodePreflight : IPreflightCheck
{
    public string Section => "timecode";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var p = engine.Store.Project;
        var mono = engine.Services.Mono.NowNs;
        var rows = new List<PreflightRow>();
        foreach (var list in p.Playlists.Where(l => l.Chase is not null))
        {
            var chase = list.Chase!;
            var view = engine.ChaseOf(list.Id);
            if (view is { Engaged: false })
            {
                rows.Add(new PreflightRow("chase:" + list.Id, node, Section, list.Name, Light.Amber, "disengaged", $"{list.Name} plays by hand, not on its timecode",
                    $"TIMECODE ENGAGE {list.Id} ON"));
                continue;
            }
            if (chase.Sources.Length == 0 || p.FindTimecodeSource(chase.Sources[0]) is not { } first)
            {
                rows.Add(new PreflightRow("chase:" + list.Id, node, Section, list.Name, Light.Red, "no source", $"{list.Name} chases a source the show no longer has",
                    $"TIMECODE CHASE {list.Id} OFF"));
                continue;
            }
            // The sources as this chase reads them: their code judged at its jump threshold.
            var readings = chase.Sources.Select(s => (Id: s, Reading: engine.TimecodeSources.ReadingOf(s, mono, chase.JumpNs))).ToList();
            var rolling = readings.FirstOrDefault(r => r.Reading.State is "rolling" or "parked");
            if (rolling.Id is not null)
            {
                var name = p.FindTimecodeSource(rolling.Id)?.Name ?? rolling.Id;
                rows.Add(new PreflightRow("chase:" + list.Id, node, Section, list.Name, Light.Green, rolling.Reading.State,
                    $"{name} {rolling.Reading.State} at {rolling.Reading.Text}", null));
                continue;
            }
            var r0 = readings[0].Reading;
            var light = r0.State is "unavailable" ? Light.Red : Light.Amber;
            rows.Add(new PreflightRow("chase:" + list.Id, node, Section, list.Name, light, r0.State,
                $"{first.Name}: {r0.Words} — the playlist waits for code", $"TIMECODE ENGAGE {list.Id} OFF"));
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("timecode", node, Section, "Timecode", Light.Grey, "none", "no playlist chases timecode", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}
