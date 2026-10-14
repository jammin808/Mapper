using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Decode;

/// <summary>Preflight, section "decode": "The show needs N HAP layers at 4K60; this node sustains M" — amber when N > M, with the
/// line that shows why (MEDIA INSPECT of the heaviest clip); red for each HAP R or HAP HDR item the show plays that this node's
/// GPU cannot sample, with the line that makes a version it can. A show with no HAP is one green row.</summary>
public sealed class HapLayersCheck : IPreflightCheck
{
    public string Section => "decode";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var project = engine.Store.Project;
        var rows = new List<PreflightRow>();
        var demand = HapDemand.Of(project);
        if (demand.Items == 0)
        {
            rows.Add(new PreflightRow("decode.hap-layers", node, Section, "HAP layers at 4K60", Light.Green, "none", "The show plays no HAP media", null));
            return rows;
        }
        var measured = await engine.DecodeFacts.EnsureMeasuredAsync(ct).ConfigureAwait(false);
        var need = demand.Needed;
        var c = CultureInfo.InvariantCulture;
        var over = need > measured.Value;
        var note = string.Create(c, $"The show needs {need} HAP layer{(need == 1 ? "" : "s")} at 4K60; this node sustains {measured.Value}");
        rows.Add(new PreflightRow("decode.hap-layers", node, Section, "HAP layers at 4K60", over ? Light.Amber : Light.Green,
            string.Create(c, $"{need} of {measured.Value}"), over ? note + " — " + measured.Words : note,
            over && demand.Heaviest.Length > 0 ? "MEDIA INSPECT " + demand.Heaviest : null));

        // Only the items the show plays: a library item nothing shows is no reason the show is not ready.
        var shown = demand.ItemIds.Select(project.FindMedia).OfType<MediaItem>().ToList();
        if (engine.Services.Media is MediaBackend media && media.RendererKnown)
        {
            foreach (var item in shown)
            {
                if (media.HapRefusal(item) is { } words)
                    rows.Add(new PreflightRow("decode.hap-gpu:" + item.Id, node, Section, item.Name, Light.Red, "not on this GPU", words,
                        $"MEDIA OPTIMISE {item.Id} TO hap-q"));
            }
        }
        else if (engine.DecodeFacts.Formats is { } formats)
        {
            foreach (var item in shown)
            {
                if (HapCodecs.TryFromName(item.Info.VideoCodec, out var codec) && !HapCodecs.FfmpegDecodes(codec)
                    && HapCodecs.Missing(codec, formats) is { Length: > 0 } missing)
                {
                    rows.Add(new PreflightRow("decode.hap-gpu:" + item.Id, node, Section, item.Name, Light.Red, "not on this GPU",
                        PlayMap.Media.Hap.HapVideoSource.NotOnThisGpu(codec, missing), $"MEDIA OPTIMISE {item.Id} TO hap-q"));
                }
            }
        }
        return rows;
    }
}
