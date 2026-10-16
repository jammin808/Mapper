using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Preflight;
using PlayMap.Net.Sync;

namespace PlayMap.Engine.Sync;

/// <summary>Preflight's sync rows from facts: a follower's clock uncertainty under the group's bound and its leader heard; a leader's
/// group at one rate. A node in no group says so in grey. Each failing row carries the line that fixes it.</summary>
public sealed class SyncPreflight(SyncNode sync) : IPreflightCheck
{
    /// <summary>The clock uncertainty a group allows: a quarter of a 60 Hz refresh.</summary>
    public const double UncertaintyBoundMs = 4;

    public string Section => "sync";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var node = context.Engine.Services.Settings.Name;
        var s = sync.Settings;
        var rows = new List<PreflightRow>();
        switch (sync.Role)
        {
            case "follower":
            {
                var clock = sync.Clock;
                var fix = $"SYNC FOLLOW {s.Group} {s.Leader}";
                var heard = clock.IsFollowing && !clock.IsHolding;
                rows.Add(heard
                    ? new PreflightRow("sync:leader", node, Section, "Sync leader", Light.Green, "heard", sync.Words, null)
                    : new PreflightRow("sync:leader", node, Section, "Sync leader", Light.Red, "not heard",
                        $"{s.Leader} has not answered: this node free-wheels on its last estimate", fix));
                var u = clock.UncertaintyNs / 1e6;
                var value = string.Create(CultureInfo.InvariantCulture, $"± {u:0.###} ms");
                rows.Add(u <= UncertaintyBoundMs
                    ? new PreflightRow("sync:clock", node, Section, "Clock", Light.Green, value, "within the group's bound", null)
                    : new PreflightRow("sync:clock", node, Section, "Clock", Light.Amber, value,
                        string.Create(CultureInfo.InvariantCulture, $"over the group's ± {UncertaintyBoundMs} ms: the network to the leader is slow or loaded"), fix));
                rows.Add(sync.FeedWord == "live"
                    ? new PreflightRow("sync:feed", node, Section, "Anchor feed", Light.Green, "live", "the leader's anchors and air reach this node", null)
                    : new PreflightRow("sync:feed", node, Section, "Anchor feed", Light.Red, sync.FeedWord,
                        "the leader's anchors and air do not reach this node", fix));
                break;
            }
            case "leader":
            {
                var picture = sync.Picture(context.Engine.IsAnimating);
                if (picture.MixedRate)
                {
                    // The reference's rate is the group's (the leader's first output in its project, else the first member's heard);
                    // the odd output is the first at another rate in the board's order, set to it where it is: one on another node is
                    // fixed there. The board's order is the projects', so the same outputs give the same row whatever order their
                    // reports came in.
                    var pair = picture.Pairs.First(p => p.MixedRate);
                    var reference = picture.Outputs.FirstOrDefault(o => o.Output == pair.A && o.Node == pair.ANode && o.Heard);
                    var odd = pair.B;
                    // Output ids repeat across nodes: the odd one is the pair's, on the pair's node.
                    var row = picture.Outputs.FirstOrDefault(o => o.Output == odd && o.Node == pair.BNode && o.Heard);
                    var rate = SyncBoard.Nominal(reference?.RefreshHz ?? 60).ToString("0.###", CultureInfo.InvariantCulture);
                    var fix = $"SET {odd} refreshHz {rate}";
                    rows.Add(row is { Local: true }
                        ? new PreflightRow("sync:rates", node, Section, "Refresh rates", Light.Red, "mixed", picture.Words, fix)
                        : new PreflightRow("sync:rates", node, Section, "Refresh rates", Light.Red, "mixed", $"{picture.Words} — on {row?.Node}: {fix}", null));
                }
                else
                {
                    rows.Add(new PreflightRow("sync:rates", node, Section, "Refresh rates", picture.Outputs.Any(o => o.Heard) ? Light.Green : Light.Grey,
                        picture.Verdict, picture.Words, null));
                }
                var members = sync.Members.Count;
                rows.Add(new PreflightRow("sync:members", node, Section, "Members", members > 0 ? Light.Green : Light.Grey,
                    members.ToString(CultureInfo.InvariantCulture), members > 0 ? "members take this node's anchors and air" : "no member has joined the feed yet", null));
                break;
            }
            default:
                rows.Add(s.Role == "follow"
                    ? new PreflightRow("sync:leader", node, Section, "Sync leader", Light.Red, "not heard", sync.Words, $"SYNC FOLLOW {s.Group} {s.Leader}")
                    : new PreflightRow("sync:none", node, Section, "Sync", Light.Grey, "solo", "not in a sync group", null));
                break;
        }
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}
