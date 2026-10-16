using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Optimise;

/// <summary>Preflight, section "optimise": each video the show plays at 4K or more that this node still decodes on the CPU is amber,
/// with the line that makes a version the GPU plays; a show with none is one green row. The clips and their path are the decode
/// section's (<see cref="Decode.DecodePathCheck.FourKClips"/>, <see cref="Decode.DecodePathCheck.CpuPath"/>): the version this node
/// plays is not a HAP codec, it is of the 4K class, and its frames come through system memory (a clip the decoder serves zero-copy is
/// on the GPU already), so the two sections always name the same clips.</summary>
public sealed class OptimisePreflight : IPreflightCheck
{
    public string Section => "optimise";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var project = engine.Store.Project;
        var media = engine.Services.Media as MediaBackend;
        var rows = new List<PreflightRow>();
        foreach (var (item, played) in Decode.DecodePathCheck.FourKClips(project, media))
        {
            if (Decode.DecodePathCheck.CpuPath(engine, media, item) is null)
                continue;
            var info = played.Info;
            rows.Add(new PreflightRow("optimise.cpu:" + item.Id, node, Section, item.Name, Light.Amber, "decodes on the CPU",
                string.Create(CultureInfo.InvariantCulture, $"{item.Name} is {info.Width}×{info.Height} {info.VideoCodec}: this node decodes it on the CPU — a HAP Q version plays on the GPU"),
                $"MEDIA OPTIMISE {item.Id} TO hap-q"));
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("optimise.cpu", node, Section, "4K on the CPU", Light.Green, "none", "no 4K clip the show plays decodes on the CPU here", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }
}

/// <summary>The library items the show plays: what outputs and surfaces show, every item of a playlist they show, every visible
/// layer of a canvas they show, every input a generator they show reads (GENERATOR INPUT), what an enabled LED fixture samples
/// (a source, or a canvas's sources), what an enabled send taps (a canvas or a source), and the fallback a live input it plays shows
/// on loss; nested eight deep.</summary>
public static class ShowMedia
{
    public static IReadOnlySet<string> Of(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var items = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<SourceRef>();
        void Add(SourceRef source, int depth)
        {
            if (depth > 8 || !seen.Add(source))
                return;
            switch (source.Kind)
            {
                case SourceKind.Media:
                    items.Add(source.Id);
                    break;
                case SourceKind.Playlist when project.FindPlaylist(source.Id) is { } list:
                    foreach (var item in list.Items)
                        Add(item.Source, depth + 1);
                    break;
                case SourceKind.Canvas when project.Canvases.FirstOrDefault(c => c.Id == source.Id) is { } canvas:
                    foreach (var layer in canvas.Layers)
                        if (layer.Visible)
                            Add(layer.Source, depth + 1);
                    break;
                case SourceKind.Generator when project.FindGenerator(source.Id) is { } generator:
                    foreach (var input in generator.Inputs.Values)
                        Add(input, depth + 1);
                    break;
            }
        }
        foreach (var o in project.Outputs)
            Add(o.Source, 0);
        foreach (var s in project.Stage.Surfaces)
            Add(s.Content.Source, 0);
        // LED fixtures sampling an output, a surface or the world read what those show (above); a source or a canvas is their own.
        foreach (var f in project.Leds.Fixtures)
        {
            if (!f.Enabled)
                continue;
            if (f.Space == LedSpace.Canvas)
                Add(SourceRef.Canvas(f.SpaceId), 0);
            else if (f.Space == LedSpace.Source && SourceRef.TryParse(f.SpaceId, out var sampled))
                Add(sampled, 0);
        }
        // A send of an output reads what the output shows (above); a canvas or a source is the send's own.
        foreach (var send in project.Sends)
        {
            if (!send.Enabled || !VideoIo.SendShape.TryTap(send.Tap, out var kind, out var target, out var tapped, out _))
                continue;
            if (kind == PlayMap.Render.Passes.SendTapKind.Canvas)
                Add(SourceRef.Canvas(target), 0);
            else if (kind == PlayMap.Render.Passes.SendTapKind.Source && tapped is not null)
                Add(tapped, 0);
        }
        // A live input's fallback is played when its feed is lost (a fallback is never itself live, so this goes one step).
        foreach (var m in project.Media)
        {
            if (items.Contains(m.Id) && VideoIo.Fallbacks.Of(m) is { } fallback)
                Add(fallback, 0);
        }
        return items;
    }
}
