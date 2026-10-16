using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Decode;

/// <summary>Preflight, section "decode": "Show item decodes in software at ≥ 4K" — amber for each clip of 4K or more the show plays
/// whose frames come through system memory (software, or a hardware decoder copying back), fixed by a GPU version
/// (<c>MEDIA OPTIMISE md… TO hap-q</c>). The path is the one actually used: an open decoder's own (the path of its latest frame);
/// a clip not open now is read from the rung it would open on (the policy and the renderer's share), and the row says "would".
/// HAP and live inputs are not this ladder's (HAP decodes on the GPU; a live input's frames come from its receiver).</summary>
public sealed class DecodePathCheck : IPreflightCheck
{
    public string Section => "decode";

    public PreflightDepth MinDepth => PreflightDepth.Quick;

    public TimeSpan Budget => TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<PreflightRow>> RunAsync(PreflightContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var engine = context.Engine;
        var node = engine.Services.Settings.NodeId;
        var project = engine.Store.Project;
        var media = engine.Services.Media as MediaBackend;
        var open = media?.OpenDecoders() ?? [];
        var rows = new List<PreflightRow>();
        var c = CultureInfo.InvariantCulture;
        foreach (var item in ShownClips(project))
        {
            if (!EngineCore.IsFourK(item.Info.Width, item.Info.Height))
                continue;
            string path;
            string why;
            var would = false;
            if (open.FirstOrDefault(d => d.ItemId == item.Id && d.Source.Health.Ready) is { } d)
            {
                var h = d.Source.Health;
                if (h.ZeroCopy)
                    continue;
                path = EngineCore.PathWord(h);
                why = h.Why.Length > 0 ? h.Why : d.Why;
            }
            else
            {
                var (policy, forced) = engine.DecodeFacts.Policy;
                var platform = media?.ZeroCopy.Platform ?? ZeroCopyPlatform.None;
                var choice = DecodeLadder.Choose(policy, platform, forced);
                if (DecodeLadder.Rank(choice.Ceiling) is 1 or 2)
                    continue;
                path = DecodeLadder.Word(choice.Ceiling);
                why = choice.Why;
                would = true;
            }
            var size = string.Create(c, $"{item.Info.Width}×{item.Info.Height}");
            var how = path == "software" ? "decodes in software" : "decodes on the GPU but copies every frame back through memory";
            var note = $"{item.Name} {(would ? "would " + (path == "software" ? "decode in software" : "copy every frame back through memory") : how)} at {size}"
                + (why.Length > 0 ? " — " + why : "");
            rows.Add(new PreflightRow("decode.cpu-4k:" + item.Id, node, Section, item.Name, Light.Amber, path, note,
                $"MEDIA OPTIMISE {item.Id} TO hap-q"));
        }
        if (rows.Count == 0)
            rows.Add(new PreflightRow("decode.cpu-4k", node, Section, "4K clips on the CPU", Light.Green, "none",
                "No clip of 4K or more in the show decodes in software", null));
        return Task.FromResult<IReadOnlyList<PreflightRow>>(rows);
    }

    /// <summary>The non-HAP video files the show plays: on outputs and surfaces directly, in their playlists and canvases.</summary>
    internal static IReadOnlyList<MediaItem> ShownClips(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var found = new List<MediaItem>();
        var seen = new HashSet<SourceRef>();
        void Add(SourceRef source, int depth)
        {
            if (depth > 8 || !seen.Add(source))
                return;
            switch (source.Kind)
            {
                case SourceKind.Media when project.FindMedia(source.Id) is { Kind: MediaKind.Video } item
                                           && !HapCodecs.TryFromName(item.Info.VideoCodec, out _) && item.Info.VideoCodec != "hap":
                    found.Add(item);
                    break;
                case SourceKind.Playlist when project.FindPlaylist(source.Id) is { } list:
                    foreach (var entry in list.Items)
                        Add(entry.Source, depth + 1);
                    break;
                case SourceKind.Canvas when project.Canvases.FirstOrDefault(cv => cv.Id == source.Id) is { } canvas:
                    foreach (var layer in canvas.Layers)
                        if (layer.Visible)
                            Add(layer.Source, depth + 1);
                    break;
            }
        }
        foreach (var o in project.Outputs)
            Add(o.Source, 0);
        foreach (var s in project.Stage.Surfaces)
            Add(s.Content.Source, 0);
        return found;
    }
}
