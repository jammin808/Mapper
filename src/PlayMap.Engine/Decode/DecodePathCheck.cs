using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Engine.Host;
using PlayMap.Engine.Optimise;

namespace PlayMap.Engine.Decode;

/// <summary>Preflight, section "decode": "Show item decodes in software at ≥ 4K" — amber for each clip of 4K or more the show plays
/// whose frames come through system memory (software, or a hardware decoder copying back), fixed by a GPU version
/// (<c>MEDIA OPTIMISE md… TO hap-q</c>). What the show plays is <see cref="ShowMedia"/>'s (outputs, surfaces, playlists, canvases,
/// generator inputs, LED fixtures, sends, live fallbacks), as the version this node plays (<see cref="VersionChoice.Played"/>: a
/// HAP version plays on the GPU). The path is the one actually used: an open decoder's own (the path of its latest frame); a clip
/// not open now is read from the rung it would open on (the policy and the renderer's share), and the row says "would". HAP and
/// live inputs are not this ladder's (HAP decodes on the GPU; a live input's frames come from its receiver).</summary>
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
        var rows = new List<PreflightRow>();
        var c = CultureInfo.InvariantCulture;
        foreach (var (item, played) in FourKClips(project, media))
        {
            if (CpuPath(engine, media, item) is not { } cpu)
                continue;
            var (path, why, would) = cpu;
            var size = string.Create(c, $"{played.Info.Width}×{played.Info.Height}");
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

    /// <summary>The video items the show plays at 4K or more whose played version FFmpeg decodes (not HAP), with that version.</summary>
    internal static List<(MediaItem Item, MediaItem Played)> FourKClips(Project project, MediaBackend? media)
    {
        ArgumentNullException.ThrowIfNull(project);
        var formats = media?.Formats ?? BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4;
        var clips = new List<(MediaItem, MediaItem)>();
        foreach (var id in ShowMedia.Of(project))
        {
            if (project.FindMedia(id) is not { Kind: MediaKind.Video } item)
                continue;
            var played = VersionChoice.Played(item, formats, h => media?.PathForHash(h) is not null);
            var info = played.Info;
            if (info.VideoCodec.Length == 0 || HapCodecs.TryFromName(info.VideoCodec, out _) || info.VideoCodec == "hap"
                || !EngineCore.IsFourK(info.Width, info.Height))
                continue;
            clips.Add((item, played));
        }
        return clips;
    }

    /// <summary>How a clip's frames come to the renderer when that is through system memory: its open decoder's path (the path of its
    /// latest frame) and why, or — not open now — the rung it would open on ("would"); null when they stay on the GPU (zero-copy).</summary>
    internal static (string Path, string Why, bool Would)? CpuPath(EngineCore engine, MediaBackend? media, MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(item);
        if ((media?.OpenDecoders() ?? []).FirstOrDefault(d => d.ItemId == item.Id && d.Source.Health.Ready) is { } d)
        {
            var h = d.Source.Health;
            return h.ZeroCopy ? null : (EngineCore.PathWord(h), h.Why.Length > 0 ? h.Why : d.Why, false);
        }
        var (policy, forced) = engine.DecodeFacts.Policy;
        var platform = media?.ZeroCopy.Platform ?? ZeroCopyPlatform.None;
        var choice = DecodeLadder.Choose(policy, platform, forced);
        return DecodeLadder.Rank(choice.Ceiling) is 1 or 2 ? null : (DecodeLadder.Word(choice.Ceiling), choice.Why, true);
    }
}
