using PlayMap.Core.Bus;
using PlayMap.Core.Fleet;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Media;

namespace PlayMap.Engine.Fleet;

/// <summary>Preflight's backup rows (wp2-backup): on a backup, its mirror identical (the primary's revision, clock and feed) and every
/// file and optimised version the show names here; on a primary, each backup identical; on a node that holds the show, every route
/// reachable (its projector answers and says its input); a fenced node red with the way out; a solo node with no routes one grey row.</summary>
internal sealed class BackupPreflight : IPreflightCheck
{
    public string Section => "backup";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(4);

    public async Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var rows = new List<PreflightRow>();
        if (engine.Services.Backup is not { } backup)
            return rows;
        var role = backup.Settings;
        var project = engine.Store.Project;
        switch (role.Role)
        {
            case FleetRoles.Fenced:
                rows.Add(new PreflightRow("backup:fenced", node, Section, "Fenced", Light.Red, "fenced",
                    $"{role.Of} has the show (epoch {role.Epoch}): this node's outputs are held black", $"FLEET BACKUP {role.Of}"));
                return rows;
            case FleetRoles.Backup:
                var mirror = backup.Mirror();
                var state = mirror?.State ?? "joining";
                rows.Add(new PreflightRow("backup:mirror", node, Section, "Mirror of " + (mirror?.Name is { Length: > 0 } n ? n : role.Of),
                    state switch { "identical" => Light.Green, "lost" => Light.Red, _ => Light.Amber }, state, mirror?.Words ?? "",
                    state == "lost" ? $"FLEET BACKUP {role.Of}" : null));
                var missing = Missing(engine, project);
                rows.Add(missing.Count == 0
                    ? new PreflightRow("backup:files", node, Section, "Files and versions", Light.Green, "all here", "every file and optimised version the show names is here, verified by its hash", null)
                    : new PreflightRow("backup:files", node, Section, "Files and versions", Light.Amber,
                        missing.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " missing",
                        $"{string.Join(", ", missing.Take(3).Select(m => m.Words))}{(missing.Count > 3 ? " …" : "")} — the backup cannot play what the primary plays",
                        missing[0].Fix(role.Of)));
                break;
            default:
                foreach (var m in backup.Feed.Members)
                {
                    var facts = engine.Services.FleetPeers?.Peers().FirstOrDefault(p => p.Id == m.Node)?.Health?.Facts;
                    var mirrored = facts is { Role: FleetRoles.Backup } f && f.Of == node ? f.Mirror : "";
                    rows.Add(new PreflightRow("backup:" + m.Node, node, Section, (facts?.Name ?? m.Name) + " (hot backup)",
                        mirrored == "identical" ? Light.Green : Light.Amber, mirrored.Length > 0 ? mirrored : "not heard",
                        mirrored == "identical" ? "same show, versions, clock and air" : $"its mirror is {(mirrored.Length > 0 ? mirrored : "not heard in its health")}", null));
                }
                break;
        }
        if (!project.Fleet.Routes.IsDefaultOrEmpty)
        {
            if (engine.Services.Devices is not { } devices)
            {
                rows.Add(new PreflightRow("backup:routes", node, Section, "Routes", Light.Grey, "not read", "projector control is not on this node: its routes cannot be read", null));
            }
            else
            {
                foreach (var r in project.Fleet.Routes)
                {
                    var name = project.FindDevice(r.DeviceId)?.Name ?? r.DeviceId;
                    var words = await EngineCore.RouteReadAsync(devices, r, name).ConfigureAwait(false);
                    var reached = words.EndsWith("· reachable", StringComparison.Ordinal);
                    rows.Add(new PreflightRow("route:" + r.OutputId, node, Section, "Route " + (project.FindOutput(r.OutputId)?.Name ?? r.OutputId),
                        reached ? Light.Green : Light.Red, reached ? "reachable" : "unreachable", words, reached ? null : "FLEET ROUTE TEST"));
                }
            }
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("backup", node, Section, "Hot backup", Light.Grey, "none", "no machine backs this one up and it backs up none", null));
        return rows;
    }

    private sealed record MissingFile(string MediaId, string? VersionId, string Words)
    {
        public string Fix(string of) => VersionId is null ? $"MEDIA FETCH {MediaId} FROM {of}" : $"MEDIA FETCH {MediaId} VERSION {VersionId} FROM {of}";
    }

    private static List<MissingFile> Missing(EngineCore engine, Project project)
    {
        var store = engine.Services.Media;
        var list = new List<MissingFile>();
        if (store is null)
            return list;
        foreach (var m in project.Media.Where(m => m.IsFile))
        {
            if (ContentHasher.IsValid(m.ContentHash) && store.PathForHash(m.ContentHash) is null)
                list.Add(new MissingFile(m.Id, null, m.Name));
            foreach (var v in m.Versions.Where(v => ContentHasher.IsValid(v.ContentHash) && store.PathForHash(v.ContentHash) is null))
                list.Add(new MissingFile(m.Id, v.Id, $"{m.Name} {v.Id} ({v.To})"));
        }
        return list;
    }
}
