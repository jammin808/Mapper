using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PlayMap.Core.Dmx;
using PlayMap.Core.Model;

namespace PlayMap.Core.Led;

/// <summary>Builds the sender's program from a plan (pure): each node's target, rate, latency, latch and rows.</summary>
public static class LedPrograms
{
    public static LedSendProgram Build(LedPlan plan, Guid cid, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var nodes = ImmutableArray.CreateBuilder<LedNodeProgram>(plan.Nodes.Length);
        foreach (var np in plan.Nodes)
        {
            var node = plan.Rig.Nodes.First(n => n.Id == np.NodeId);
            var target = TargetOf(node, out var why);
            var rows = plan.Rows.Skip(np.FirstRow).Take(np.Rows).ToList();
            nodes.Add(new LedNodeProgram(node.Id, node.Name, node.Protocol, node.Host, target, (long)Math.Round(np.RateHz * 1000),
                (long)Math.Round(Math.Max(0, node.LatencyMs) * 1e6), node.Sync == LedSync.Latch, Math.Clamp(node.SacnPriority, 0, 200),
                Math.Clamp(node.SacnSyncUniverse, 0, Sacn.MaxUniverse), node.Protocol == LedProtocol.Sacn && (node.SacnMulticast || node.Host.Length == 0),
                np.FirstRow, [.. rows.Select(r => r.Universe)], [.. rows.Select(r => r.Used)], np.DdpType, why.Length > 0 ? why : np.Why));
        }
        return new LedSendProgram(plan.Serial, nodes.ToImmutable(), LedReference.Black(plan), cid, sourceName, plan.Refused);
    }

    /// <summary>Where a node's packets go: its address and the protocol's port unless it names one; null with the reason when the
    /// host is not an address (sACN multicast needs none).</summary>
    public static IPEndPoint? TargetOf(LedNode node, out string why)
    {
        ArgumentNullException.ThrowIfNull(node);
        why = "";
        var port = node.Port > 0 ? node.Port : node.Protocol switch
        {
            LedProtocol.ArtNet => ArtNet.Port,
            LedProtocol.Sacn => Sacn.Port,
            _ => Ddp.Port,
        };
        if (node.Host.Length == 0)
        {
            if (node.Protocol != LedProtocol.Sacn)
                why = "it has no address — SET its host";
            return null;
        }
        if (!IPAddress.TryParse(node.Host, out var address))
        {
            why = $"{node.Host} is not an address (give the node's IP address)";
            return null;
        }
        return new IPEndPoint(address, port);
    }

    /// <summary>A machine's stable sACN source id (CID), made from its node id.</summary>
    public static Guid CidOf(string nodeId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("playmap-led:" + nodeId));
        // Version 5 and the RFC 4122 variant, so the bytes read as a well-formed name-based UUID.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>The leader-time step of a frame for a node's rate: floor(leader seconds × rate), in integers, so every machine
    /// decimates the same frames.</summary>
    public static long StepOf(long leaderNs, long rateMilliHz)
    {
        if (rateMilliHz <= 0)
            return leaderNs;
        var x = (Int128)leaderNs * rateMilliHz;
        var d = (Int128)1_000_000_000_000;
        var q = x / d;
        if (x < 0 && q * d != x)
            q--;
        return (long)q;
    }
}
