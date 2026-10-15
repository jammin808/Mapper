using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Preflight;

namespace PlayMap.Engine.Fleet;

/// <summary>Preflight's fleet rows. Every expected machine heard: a machine is expected when this node knew it before (kept across a
/// restart), when the show's fleet plan names it, or when the monitor hears it now — green when its health is live (its own light in
/// the note), amber when quiet, red when gone or never heard since the start, with the line that finds out who is on the network.
/// And every pin the monitor holds valid: a certificate that changed or is not the one the hello names is red, refused until the
/// operator decides. A node whose monitor is off answers one grey row that says so.</summary>
internal sealed class FleetPreflight(Func<string>? pinFile = null) : IPreflightCheck
{
    public string Section => "fleet";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var rows = new List<PreflightRow>();
        if (engine.Services.FleetPeers is not { } fleet)
        {
            rows.Add(new PreflightRow("fleet", node, Section, "Fleet monitor", Light.Grey, "off",
                "this node does not watch other machines' health (its fleet monitor is off)", null));
            return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
        }
        var peers = fleet.Peers().Where(p => p.Id != node).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, name) in engine.Fleet.Known)
            expected[id] = name;
        foreach (var m in engine.Store.Project.Fleet.Members.Where(m => m.NodeId != node))
            expected.TryAdd(m.NodeId, m.Name);
        foreach (var p in peers.Values)
            expected[p.Id] = p.Name;

        foreach (var (id, name) in expected)
        {
            if (!peers.TryGetValue(id, out var p) || p.Health is null)
            {
                rows.Add(new PreflightRow("machine:" + id, node, Section, name, Light.Red, "not heard",
                    $"{name} is expected (known before, or in the show's fleet plan) and its health is not heard — check it is on and on the show network",
                    "NODES"));
                continue;
            }
            var (light, words) = FleetGraph.MachineLight(new FleetMachine(p.Id, p.Name, p.Heard, p.Health, p.Words) { Refused = p.PinRefused, SilentS = p.SilentS });
            rows.Add(p.Heard switch
            {
                FleetHeard.Live => new PreflightRow("machine:" + id, node, Section, p.Name, light == Light.Grey ? Light.Grey : Light.Green, "heard",
                    $"heard: {words}", null),
                FleetHeard.Stale => new PreflightRow("machine:" + id, node, Section, p.Name, Light.Amber, "quiet", $"{p.Name} is quiet: {words}", "NODES"),
                _ => new PreflightRow("machine:" + id, node, Section, p.Name, Light.Red, "gone", $"{p.Name}: {words} — check it is on and on the show network", "NODES"),
            });
        }

        foreach (var p in peers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = p.Pin switch
            {
                "pinned" => new PreflightRow("pin:" + p.Id, node, Section, p.Name + " certificate", Light.Green, "pinned", "its certificate is the one pinned", null),
                "first use" => new PreflightRow("pin:" + p.Id, node, Section, p.Name + " certificate", Light.Amber, "not pinned yet",
                    "its certificate is new here and is pinned when its first record arrives", null),
                "refused" => new PreflightRow("pin:" + p.Id, node, Section, p.Name + " certificate", Light.Red, "refused",
                    $"{p.Words} — if {p.Name} was reinstalled, remove its line from {pinFile?.Invoke() ?? "the fleet's pin file"} (read again when it changes, no restart) and it is pinned again on its next record; otherwise find out what answers at its address",
                    null),
                _ => new PreflightRow("pin:" + p.Id, node, Section, p.Name + " certificate", Light.Grey, "no TLS", "it offers no TLS: its health is read in the clear", null),
            };
            rows.Add(row);
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("fleet", node, Section, "Fleet", Light.Grey, "alone", "no other PlayMap machine is heard or expected", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}
