using System.Diagnostics;
using System.Globalization;
using PlayMap.Engine.Tests.Host;
using PlayMap.Net.Fleet;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>Waits on what a node's fleet monitor holds of a peer while the fleet runs on clocks the test moves by hand. A monitor whose
/// subscription to a peer ended without a record (refused, cut, or made to another machine under the peer's id) dials again only once
/// its backoff (0.5 s doubling to 8 s, spread by up to a fifth) has run out on its fleet clock, and until then takes no newer hello of
/// that peer, so a wait that never moved that clock would wait for good.</summary>
internal static class FleetWait
{
    /// <summary>How far a look moves the clocks unless the caller says: at 50 ms a look (20 ms apart), the monitor's longest wait passes
    /// within 192 looks.</summary>
    public static readonly TimeSpan RedialStep = TimeSpan.FromMilliseconds(50);

    /// <summary>Looks every 20 ms, for the 10 s of <see cref="Eventually"/>'s patience, until <paramref name="holds"/> is true. Each look
    /// that finds it false while <paramref name="monitor"/> waits to dial any machine again (<see cref="FleetMonitor.Redialling"/>, so
    /// the wait's timer stands) moves each of <paramref name="clocks"/> a <paramref name="step"/> (<see cref="RedialStep"/> unless
    /// given); nothing else gates the step, so a clock other nodes share moves for them too (a dial one has under way, whose connect
    /// timeout runs on it, and the age of every record one holds). Answers whether it came true and how far the clocks moved, zero when
    /// no look found the monitor waiting.</summary>
    public static async Task<(bool Held, TimeSpan Moved)> UntilAsync(Func<bool> holds, FleetMonitor? monitor, IReadOnlyList<ManualTime> clocks,
        TimeSpan? step = null)
    {
        var by = step ?? RedialStep;
        var moved = TimeSpan.Zero;
        var waited = Stopwatch.StartNew();
        while (!holds())
        {
            if (waited.ElapsedMilliseconds >= 10_000)
                return (holds(), moved);
            if (monitor?.Redialling > 0)
            {
                foreach (var clock in clocks)
                    clock.Advance(by);
                moved += by;
            }
            await Task.Delay(20);
        }
        return (true, moved);
    }

    /// <summary>What a node's hello and fleet monitor hold of a peer, for a wait that fails.</summary>
    public static string Knows(HostRig a, HostRig b)
    {
        var peer = a.Network.Hello!.Peers.Find(b.Id);
        var table = peer is null
            ? $"its hello has no {b.Id}"
            : string.Create(CultureInfo.InvariantCulture, $"its hello has {b.Id} at {peer.Address}:{peer.Hello.Port("http")} ({peer.Liveness})");
        var monitor = a.Engine.Services.FleetPeers as FleetMonitor;
        return string.Create(CultureInfo.InvariantCulture,
            $"{table}, and {b.Id} serves {b.Network.HttpPort}; its monitor keeps {monitor?.Count} machines, {monitor?.Subscriptions} subscribed or waiting to dial again ({monitor?.Redialling} waiting), and says of {b.Id}: {monitor?.WordsOf(b.Id) ?? "nothing"}");
    }
}
