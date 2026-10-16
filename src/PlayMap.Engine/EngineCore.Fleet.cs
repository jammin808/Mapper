using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Fleet;

namespace PlayMap.Engine;

/// <summary>wp2-fleet's hooks into the engine: HEALTH (this machine in one record, and its push), FLEET/EYE (the board of every machine
/// this node hears, in Patterns' Eye shape) with its view verbs, STATE.fleet and STATE.eye, the context's <c>machine</c> item and the
/// context's order for the Eye's 12-thing window, the fleet's words in the headline, and the machines known across a restart.</summary>
public sealed partial class EngineCore
{
    private FleetParts? _fleet;

    /// <summary>The fleet's runtime (made with the engine, in its routes).</summary>
    internal FleetParts Fleet => _fleet ?? throw new InvalidOperationException("the engine is still being made");

    partial void RouteFleet(VerbRoutes routes)
    {
        var parts = _fleet = new FleetParts(this);
        routes.OffLane("HEALTH", call => Task.FromResult(call.Args.Count == 0
            ? CommandReply.Success(HealthJson.Write(HealthNow()))
            : CommandReply.Fail("HEALTH takes no words after it")));
        routes.OffLane("FLEET", call => Task.FromResult(FleetReply(call)));
        routes.OffLane("FLEET FOCUS", call => Task.FromResult(parts.Focus(Board(), call.Line.RestAfter(call.Spec.WordCount))));
        routes.OffLane("FLEET LENS", call => Task.FromResult(parts.Lens(call.Args)));
        routes.OffLane("FLEET NEXT", call => Task.FromResult(call.Args.Count == 0 ? parts.Step(Board(), next: true) : CommandReply.Fail("FLEET NEXT takes no words after it")));
        routes.OffLane("FLEET PREV", call => Task.FromResult(call.Args.Count == 0 ? parts.Step(Board(), next: false) : CommandReply.Fail("FLEET PREV takes no words after it")));
        routes.OffLane("FLEET RESET", call => Task.FromResult(call.Args.Count == 0 ? parts.Reset() : CommandReply.Fail("FLEET RESET takes no words after it")));
        routes.Topic("HEALTH", parts.OpenHealth);
        routes.Index(new FleetIndex(this));
        RouteBackup(routes);
        StateChanged += _ => parts.OnStateMoved();
        if (Services.FleetPeers is { } peers)
        {
            peers.Changed += () =>
            {
                parts.Remember(peers.Peers());
                FactsChanged();
            };
        }
    }

    partial void TickFleet(long leaderNs)
    {
        _fleet?.LaneTick();
        Services.Backup?.Tick(this, leaderNs);
    }

    partial void StateFleet(Utf8JsonWriter w)
    {
        var parts = Fleet;
        var board = Board();
        w.WriteStartObject("fleet");
        WriteBackupState(w);
        // The other machines this node hears (this node is STATE itself: its id, name and light are STATE's own).
        w.WriteStartArray("nodes");
        foreach (var m in board.Machines.Where(m => !m.Self))
        {
            var (light, headline) = FleetGraph.MachineLight(m);
            w.WriteStartObject();
            w.WriteString("id", m.Id);
            w.WriteString("name", m.Name);
            w.WriteString("role", m.Health?.Facts.Role ?? "solo");
            w.WriteString("light", ShowContext.LightWord(light));
            w.WriteString("headline", headline);
            w.WriteString("heard", FleetPeer.HeardWord(m.Heard));
            w.WriteString("build", m.Health?.Facts.Build ?? "");
            w.WriteNumber("rev", m.Health?.Facts.Rev ?? -1);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("light", ShowContext.LightWord(HealthLights.Worst(board.Machines.Select(m => FleetGraph.MachineLight(m).Light))));
        w.WriteString("headline", board.Headline);
        WriteRoutesState(w);
        w.WriteEndObject();
        var (focus, lens) = parts.View;
        w.WritePropertyName("eye");
        FleetJson.WriteEye(w, board, focus, lens);
    }

    partial void SyncFleet(Utf8JsonWriter w) => w.WriteNumber("epoch", FleetRole.Epoch);

    partial void ContextFleet(List<ContextItem> items, List<ContextLink> links)
    {
        var health = HealthNow();
        var f = health.Facts;
        var words = string.Join(" · ", new[]
        {
            f.Role is "solo" or "" ? "solo" : f.Role + (f.Epoch > 0 ? string.Create(CultureInfo.InvariantCulture, $" (epoch {f.Epoch})") : ""),
            "build " + f.Build,
            f.Renderer.Length > 0 && f.Renderer != "none" ? f.Renderer + (f.RenderPath.Length > 0 ? " · " + f.RenderPath : "") : "no renderer",
            health.Light is Light.Red or Light.Amber ? health.Headline : null,
        }.Where(s => s is not null));
        items.Add(new ContextItem("machine", "machine", Services.Settings.Name, health.Light, words));
        ContextBackup(items, links);
    }

    partial void ShapeContext(List<ContextItem> items, List<ContextLink> links) =>
        ContextShape.Shape(items, links, ProjectorFolds());

    partial void HeadlineFleet(ref string headline)
    {
        if (Services.FleetPeers is null)
            return;
        var board = Board();
        if (board.Machines.Count < 2)
            return;
        headline += " · " + FleetHeadline(board);
    }

    partial void ValidateFleet(Project p, object entity, ref string? why)
    {
        // The fleet's plan (members, succession) is wp2-backup's to check; HEALTH and the board edit nothing.
    }

    partial void RecoverySaveFleet(IDictionary<string, string> into) => into["fleet"] = Fleet.KnownJson();

    partial void RecoveryLoadFleet(IReadOnlyDictionary<string, string> from)
    {
        if (from.TryGetValue("fleet", out var json))
            Fleet.LoadKnown(json);
    }

    /// <summary>This machine's health now, from published state and the sampler's latest numbers (any thread).</summary>
    internal HealthRecord HealthNow() => HealthRecord.Of(HealthFactsNow());

    internal HealthFacts HealthFactsNow()
    {
        var s = Services;
        var snapshot = Store.Current;
        var p = snapshot.Project;
        var health = s.Outputs.Health();
        var present = PresentBackend?.Present();
        var machinePresent = PresentBackend?.Machine();
        var outputs = ImmutableArray.CreateBuilder<HealthOutput>();
        foreach (var o in p.Outputs.Where(o => o.Enabled && o.Kind != OutputKind.Virtual))
        {
            var h = health.FirstOrDefault(x => x.OutputId == o.Id);
            var view = present?.FirstOrDefault(v => v.OutputId == o.Id);
            outputs.Add(new HealthOutput(o.Id, o.Name, h?.Open ?? true, h?.Fps ?? -1, h?.Dropped ?? 0, view?.Present.Missed60s ?? 0,
                view?.Present.PathWord ?? ""));
        }
        int devices = 0, green = 0, amber = 0, red = 0;
        foreach (var d in p.Stage.Devices)
        {
            devices++;
            switch (s.Devices?.State(d.Id)?.Light ?? Light.Grey)
            {
                case Light.Green: green++; break;
                case Light.Amber: amber++; break;
                case Light.Red: red++; break;
            }
        }
        var files = s.Media is null ? [] : p.Media.Where(m => m.IsFile).ToList();
        var missing = s.Media is null ? 0 : files.Count(m => s.Media.Readiness(m) is not { Present: true });
        var machine = s.Machine?.Latest ?? MachineSample.Unknown;
        var sync = s.Network.Sync();
        var settings = s.Settings;
        return new HealthFacts
        {
            Node = settings.NodeId,
            Name = settings.Name,
            AtUnixMs = s.Wall.UnixMs,
            Role = FleetRole.Role,
            Of = FleetRole.Of,
            Epoch = FleetRole.Epoch,
            Mirror = s.Backup?.Mirror()?.State ?? "",
            SyncGroup = s.GroupSync is { } group && group.Role != "solo" ? group.Settings.Group : "",
            Build = StateWriter.Version,
            Rev = snapshot.Revision,
            Outputs = outputs.ToImmutable(),
            Projectors = [.. ProjectorFolds().Select(x => new HealthProjector(x.Id, x.Name, x.Light, x.Words, x.Output))],
            Renderer = s.Outputs.RendererName,
            RenderPath = machinePresent?.Path ?? "",
            Resets = machinePresent?.Resets ?? 0,
            CpuPct = machine.CpuPct,
            MemMb = machine.MemMb,
            MemFreeMb = machine.MemFreeMb,
            DiskFreeGb = machine.DiskFreeGb,
            GpuPct = machine.GpuPct,
            GpuTempC = machine.GpuTempC,
            GpuWhy = s.Machine is null ? "the GPU is not sampled on this node" : machine.GpuWhy,
            NetMbps = machine.NetMbps,
            UptimeS = machine.UptimeS,
            SyncRole = sync.Role,
            SyncLeader = sync.Role == "follower" ? sync.Leader ?? "" : "",
            SyncUncertaintyMs = sync.Role switch { "follower" => sync.RttMs > 0 ? sync.RttMs / 2 : -1, "leader" => 0, _ => -1 },
            DevicesTotal = devices,
            DevicesGreen = green,
            DevicesAmber = amber,
            DevicesRed = red,
            MediaItems = files.Count,
            MediaMissing = missing,
            LaneMs = s.Machine is null ? -1 : _fleet?.LaneGapMs() ?? -1,
            RenderMs = health.Count == 0 ? -1 : health.Max(h => h.FrameMsP95),
        };
    }

    /// <summary>Each projector with its device's facts folded in: the light is the worse of its calibration and its device, the words
    /// say both ("calibrated, 0.8 px · on · shutter open · input 31"), and the output that drives it.</summary>
    internal IReadOnlyList<FoldedProjector> ProjectorFolds()
    {
        var p = Store.Project;
        var devices = Services.Devices;
        var folds = new List<FoldedProjector>(p.Stage.Projectors.Length);
        foreach (var pj in p.Stage.Projectors)
        {
            var calibration = pj.SolveResidualPx <= 0 ? Light.Grey : pj.SolveResidualPx < 1.5 ? Light.Green : pj.SolveResidualPx < 4 ? Light.Amber : Light.Red;
            var words = new List<string>
            {
                pj.SolveResidualPx <= 0 ? "not calibrated" : string.Create(CultureInfo.InvariantCulture, $"calibrated, {pj.SolveResidualPx:0.0} px"),
            };
            var light = calibration;
            foreach (var d in p.Stage.Devices.Where(d => d.ProjectorId == pj.Id))
            {
                var state = devices?.State(d.Id);
                var dl = state?.Light ?? Light.Grey;
                light = HealthLights.Worst([light, dl]);
                words.Add(Redact(devices is null ? d.Name + ": projector control is not on this node" : state is null ? d.Name + ": not polled yet" : state.Headline()));
            }
            var output = p.Outputs.FirstOrDefault(o => o.ProjectorId == pj.Id)?.Id ?? "";
            folds.Add(new FoldedProjector(pj.Id, pj.Name, light, string.Join(" · ", words), output));
        }
        return folds;
    }

    /// <summary>The board now: this machine and every machine the monitor hears.</summary>
    internal FleetGraph Board()
    {
        var self = HealthNow();
        var machines = new List<FleetMachine> { new(Services.Settings.NodeId, Services.Settings.Name, FleetHeard.Live, self, "", Self: true) };
        foreach (var peer in Services.FleetPeers?.Peers() ?? [])
        {
            if (peer.Id == Services.Settings.NodeId)
                continue;
            machines.Add(new FleetMachine(peer.Id, peer.Name, peer.Heard, peer.Health, peer.Words)
            {
                Refused = peer.PinRefused,
                SilentS = peer.SilentS,
            });
        }
        return FleetGraph.Build(machines);
    }

    /// <summary>The board as FLEET answers it, in the view the operator left it in (any thread).</summary>
    internal string FleetBoardJson()
    {
        var (focus, lens) = Fleet.View;
        return FleetJson.Board(Board(), focus, lens);
    }

    private CommandReply FleetReply(VerbCall call)
    {
        if (call.Args.Count > 1 || (call.Args.Count == 1 && !call.Args[0].Equals("STATUS", StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail("FLEET takes nothing, or STATUS");
        return CommandReply.Success(FleetBoardJson());
    }

    /// <summary>"3 machines green", or "3 machines · 1 red: FOH-B gone quiet".</summary>
    private static string FleetHeadline(FleetGraph board)
    {
        var lights = board.Machines.Select(m => FleetGraph.MachineLight(m)).ToList();
        var n = lights.Count;
        var red = lights.Count(l => l.Light == Light.Red);
        var amber = lights.Count(l => l.Light == Light.Amber);
        if (red + amber == 0)
            return string.Create(CultureInfo.InvariantCulture, $"{n} machines");
        var worst = red > 0 ? Light.Red : Light.Amber;
        var first = board.Machines.First(m => FleetGraph.MachineLight(m).Light == worst);
        return string.Create(CultureInfo.InvariantCulture,
            $"{n} machines · {(red > 0 ? $"{red} red" : $"{amber} amber")}: {first.Name} {FleetGraph.MachineLight(first).Sub}");
    }
}
