using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;

namespace PlayMap.Engine.Fleet;

/// <summary>The network as STATE's peers and the context's peers read it, with each PlayMap peer's own light and headline from its
/// HEALTH (the hello alone says nothing of how a machine is, so without a record it stays grey); everything else is the network's.</summary>
internal sealed class FleetNetworkView(INetworkView inner, IFleetPeers fleet) : INetworkView
{
    public IReadOnlyList<PeerView> Peers()
    {
        var heard = fleet.Peers().ToDictionary(p => p.Id, StringComparer.Ordinal);
        return inner.Peers().Select(p =>
        {
            if (p.Kind != ShowBusConstants.KindPlayMap || !heard.TryGetValue(p.Id, out var f) || (f.Health is null && !f.PinRefused))
                return p;
            var (light, headline) = FleetGraph.MachineLight(new FleetMachine(f.Id, f.Name, f.Heard, f.Health, f.Words)
            {
                Refused = f.PinRefused,
                SilentS = f.SilentS,
            });
            return p with { Light = ShowContext.LightWord(light), Headline = headline };
        }).ToList();
    }

    public LinkView? Patterns() => inner.Patterns();

    public LinkView? Stagewatch() => inner.Stagewatch();

    public string? StagewatchTimerJson() => inner.StagewatchTimerJson();

    public SyncView Sync() => inner.Sync();
}
