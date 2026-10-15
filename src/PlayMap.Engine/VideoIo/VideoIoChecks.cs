using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;

namespace PlayMap.Engine.VideoIo;

/// <summary>Video I/O's readiness rows (PREFLIGHT's "videoio" section and VIDEOIO CHECK): each runtime the show uses is present
/// (red with the fix line when it is not), every live input on air delivers (red when one is lost, amber stale), every enabled send
/// has its sender (red when one failed). A show with no live inputs or sends answers one green row.</summary>
internal static class VideoIoChecks
{
    public const string Section = "videoio";

    public static IReadOnlyList<PreflightRow> Rows(EngineCore engine)
    {
        var hub = engine.VideoIo;
        var project = engine.Store.Project;
        var node = engine.Services.Settings.NodeId;
        var rows = new List<PreflightRow>();
        var inputs = hub.InputsOf(project);
        if (inputs.Count == 0 && project.Sends.IsEmpty)
        {
            rows.Add(new PreflightRow("videoio", node, Section, "Video I/O", Light.Green, "not used", "no live inputs and no sends in the show", null));
            return rows;
        }
        foreach (var protocol in new[] { LiveProtocol.Ndi, LiveProtocol.Omt, LiveProtocol.Spout })
        {
            var used = inputs.Any(i => i.Protocol == protocol)
                || project.Sends.Any(s => s.Enabled && VideoIoHub.ProtocolOf(s.Protocol) == protocol);
            if (!used)
                continue;
            var name = VideoIoHub.Name(protocol);
            var id = $"videoio.{name.ToLowerInvariant()}.runtime";
            var why = hub.Unavailable(protocol, sending: false) ?? hub.Unavailable(protocol, sending: true);
            var status = hub.InputProvider(protocol)?.Status ?? hub.SendProvider(protocol)?.Status;
            var ready = status is { Version.Length: > 0 } ? $"{name} {status.Version}" + (status.Path.Length > 0 ? $" at {status.Path}" : "") : $"{name} is ready";
            rows.Add(why is not null
                ? new PreflightRow(id, node, Section, $"{name} runtime", Light.Red, "missing", why, FixFor(protocol, hub.Settings))
                // Running now, off from the next start: the show's inputs and sends of it will receive and send nothing after a restart.
                : hub.OffAtNextStart(protocol) is { } off
                    ? new PreflightRow(id, node, Section, $"{name} runtime", Light.Amber, "off at next start", $"{ready} — {off}", FixFor(protocol, hub.Settings))
                    : new PreflightRow(id, node, Section, $"{name} runtime", Light.Green, "ready", ready, null));
        }
        foreach (var input in inputs)
        {
            var onAir = input.Tally == "program";
            if (input.Source is not { } source)
            {
                rows.Add(new PreflightRow("videoio.input." + input.Id, node, Section, input.Item.Name, onAir ? Light.Red : Light.Amber, "not receiving",
                    input.Problem.Length > 0 ? input.Problem : "cold: it connects when something shows it", null));
                continue;
            }
            var live = source.Live;
            var row = live.State switch
            {
                LiveState.Ok => new PreflightRow("videoio.input." + input.Id, node, Section, input.Item.Name, Light.Green, "delivering",
                    string.Create(CultureInfo.InvariantCulture, $"{live.Width}×{live.Height} at {live.Fps:0.##} fps{(onAir ? ", on air" : "")}"), null),
                LiveState.Stale => new PreflightRow("videoio.input." + input.Id, node, Section, input.Item.Name, Light.Amber, "stale", live.Words,
                    $"INPUT RECONNECT {input.Id}"),
                LiveState.Connecting => new PreflightRow("videoio.input." + input.Id, node, Section, input.Item.Name, onAir ? Light.Red : Light.Amber,
                    "connecting", live.Words, $"INPUT RECONNECT {input.Id}"),
                _ => new PreflightRow("videoio.input." + input.Id, node, Section, input.Item.Name, onAir ? Light.Red : Light.Amber,
                    LiveSourceWord(live.State), live.Words, $"INPUT RECONNECT {input.Id}"),
            };
            rows.Add(row);
        }
        foreach (var slot in hub.SendsOf(project))
        {
            if (!slot.Send.Enabled)
                continue;
            var id = "videoio.send." + slot.Send.Id;
            if ((slot.Problem.Length > 0 ? slot.Problem : slot.Plan?.Refused ?? "") is { Length: > 0 } failed)
                rows.Add(new PreflightRow(id, node, Section, slot.Send.Name, Light.Red, "failed", failed, $"SEND ENABLE {slot.Send.Id} OFF"));
            else if (slot.Sink is { } sink)
                rows.Add(new PreflightRow(id, node, Section, slot.Send.Name, Light.Green, "sending",
                    $"{sink.FullName} · {sink.Connections} receiver{(sink.Connections == 1 ? "" : "s")}", null));
            else
                rows.Add(new PreflightRow(id, node, Section, slot.Send.Name, Light.Amber, "starting", "its sender is being made", null));
        }
        return rows;
    }

    private static string LiveSourceWord(LiveState state) => PlayMap.Media.Video.LiveSource.StateWord(state);

    /// <summary>The line that fixes a missing runtime: turning a protocol back on, or (NDI) nothing the wire can install — the words
    /// say where to get it, and the fix line asks the runtimes again.</summary>
    private static string FixFor(LiveProtocol protocol, VideoIoSettings settings) => protocol switch
    {
        LiveProtocol.Ndi when !settings.NdiEnabled => "VIDEOIO SET NDI ON",
        LiveProtocol.Omt when !settings.OmtEnabled => "VIDEOIO SET OMT ON",
        _ => "VIDEOIO STATUS",
    };
}

/// <summary>PREFLIGHT's video I/O section, from published state only.</summary>
internal sealed class VideoIoPreflight : IPreflightCheck
{
    public string Section => VideoIoChecks.Section;

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(VideoIoChecks.Rows(context.Engine));
    }
}
