using System.Globalization;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;
using PlayMap.Core.Wire;
using PlayMap.Engine.Decode;
using PlayMap.Engine.Host;
using PlayMap.Media;
using PlayMap.Media.Hap;
using PlayMap.Render.Decode;
using PlayMap.Render.Hap;

namespace PlayMap.Engine;

/// <summary>wp2-decode's and wp2-zerocopy's hooks into the engine (EngineCore.Modules.cs): MEDIA INSPECT, DECODE STATUS and DECODE
/// POLICY, STATE.decode (the policy, what the renderer samples and imports, its upload arena, the measured 4K60 HAP layers, every
/// open clip decoder with the path its frames actually come by and why not a better one), machine.gl, the context's decode item,
/// and the check of an item's colour override.</summary>
public sealed partial class EngineCore
{
    private DecodeFacts? _decodeFacts;

    /// <summary>The node-local decode facts (renderer families, measured layers, inspections).</summary>
    public DecodeFacts DecodeFacts
    {
        get
        {
            if (Volatile.Read(ref _decodeFacts) is { } facts)
                return facts;
            var made = new DecodeFacts();
            made.SetPolicy(DecodeLadder.TryParse(Services.Settings.Decode.Policy, out var policy) ? policy : DecodePolicy.Auto, false);
            if (Interlocked.CompareExchange(ref _decodeFacts, made, null) is null)
            {
                made.Changed += FactsChanged;
                return made;
            }
            return _decodeFacts;
        }
    }

    partial void RouteDecode(VerbRoutes routes)
    {
        routes.OffLane("MEDIA INSPECT", MediaInspectAsync);
        routes.Lane("DECODE STATUS", DecodeStatus);
        routes.Lane("DECODE POLICY", DecodePolicyVerb);
    }

    // ---- DECODE STATUS / DECODE POLICY ------------------------------------------------------------------------------------

    private CommandReply DecodeStatus(VerbCall call)
    {
        if (call.Args.Count > 0)
            return CommandReply.Fail($"DECODE STATUS takes nothing, not '{string.Join(' ', call.Args)}'");
        return CommandReply.Success(JsonOut(w => WriteDecodeBody(w)));
    }

    private CommandReply DecodePolicyVerb(VerbCall call)
    {
        if (call.Args.Count is < 1 or > 2 || !DecodeLadder.TryParse(call.Args[0], out var policy)
            || (call.Args.Count == 2 && !call.Args[1].Equals("FORCE", StringComparison.OrdinalIgnoreCase)))
            return CommandReply.Fail("DECODE POLICY AUTO|DIRECT|GPU-COPY|COPY-BACK|SOFTWARE [FORCE]");
        var force = call.Args.Count == 2;
        if (force && policy != DecodePolicy.Direct)
            return CommandReply.Fail($"FORCE goes with DIRECT only (it takes direct sampling unproven), not with {DecodeLadder.Word(policy).ToUpperInvariant()}");
        var zero = DecodeFacts.ZeroCopy;
        var (current, forced) = DecodeFacts.Policy;
        // Direct sampling of D3D11 decoder arrays needs the proof for this driver; VAAPI images are sampled in place on every driver.
        if (policy == DecodePolicy.Direct && !force && zero.Api == "d3d11" && !zero.DirectProven)
            return CommandReply.Fail("direct sampling is not proven on this driver (the proof runs next round): DECODE POLICY DIRECT FORCE takes it unproven, or AUTO takes one GPU copy");
        // FORCE only takes direct sampling unproven on a D3D11 renderer; anywhere else it changes no rung.
        if (force && zero.Api != "d3d11")
            return CommandReply.Fail("FORCE changes nothing here: only a D3D11 renderer needs a proof for direct sampling (VAAPI images are sampled in place) — DECODE POLICY DIRECT");
        if (policy == current && force == forced)
            return CommandReply.Fail($"the decode policy is already {DecodeLadder.Word(policy)}{(forced ? " (forced)" : "")}");
        if (DecodeFacts.Persist?.Invoke(policy) is { } failed)
            return CommandReply.Fail($"decode.policy could not be written: {failed}");
        DecodeFacts.SetPolicy(policy, force);
        var open = (Services.Media as MediaBackend)?.OpenDecoders().Count ?? 0;
        var c = CultureInfo.InvariantCulture;
        var effect = string.Create(c, $"decoders opened from now on; {open} open keep{(open == 1 ? "s" : "")} {(open == 1 ? "its" : "their")} path");
        if (!zero.Ready && policy is DecodePolicy.Auto or DecodePolicy.Direct or DecodePolicy.GpuCopy)
            effect += " (zero-copy is not ready here: " + zero.Words + ")";
        Journal.Add("decode", $"decode policy {DecodeLadder.Word(policy)}{(force ? " (forced)" : "")}: {effect}", ok: true);
        FactsChanged();
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("stage", "applied");
            w.WriteString("policy", DecodeLadder.Word(policy));
            w.WriteBoolean("forced", force);
            w.WriteNumber("open", open);
            w.WriteString("effect", effect);
            w.WriteEndObject();
        }), $"decode policy {DecodeLadder.Word(policy)}: {effect}");
    }

    // ---- MEDIA INSPECT ------------------------------------------------------------------------------------------------------

    private async Task<CommandReply> MediaInspectAsync(VerbCall call)
    {
        if (call.Args.Count < 1)
            return CommandReply.Fail("MEDIA INSPECT <media>");
        // Fail closed: a word this verb does not take (ON <node> among them) is refused, never ignored into this node's answer.
        if (call.Args.Count > 1)
            return CommandReply.Fail($"MEDIA INSPECT takes one media, not '{string.Join(' ', call.Args.Skip(1))}' — MEDIA INSPECT <media> (ask the node itself for its budget)");
        var item = Store.Project.FindMedia(call.Args[0]);
        if (item is null)
            return CommandReply.Fail($"no media {call.Args[0]}");
        if (!item.IsFile)
            return CommandReply.Fail($"{item.Name} is not a file: there is nothing to inspect");
        if (Services.ShowGate.Armed)
            return CommandReply.Fail("the show is armed: MEDIA INSPECT reads the whole file from disk — disarm first, or inspect it on a node that is not playing");
        var path = LocalPath(item);
        if (path is null)
            return CommandReply.Fail($"{item.Name} is not on this node");
        // The reading of HAP HDR depends on the item's colour override as well as the bytes: both name the answer kept.
        var key = item.ContentHash.Length == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
            $"{item.ContentHash}|{item.Color.Transfer}|{item.Color.WhiteNits:R}");
        HapInspection inspection;
        if (!DecodeFacts.TryInspection(key, out inspection))
        {
            try
            {
                inspection = await Task.Run(() => HapInspect.Inspect(path, item.Color, 8, call.Ct), call.Ct);
            }
            catch (Exception ex) when (ex is PlayMap.Media.Ffmpeg.MediaOpenException or IOException or PlayMap.Media.Ffmpeg.FfmpegUnavailableException)
            {
                return CommandReply.Fail($"{item.Name} could not be read: {ex.Message}");
            }
            DecodeFacts.Remember(key, inspection);
        }
        return CommandReply.Success(InspectJson(item, inspection));
    }

    private string? LocalPath(MediaItem item)
    {
        if (ContentHasherValid(item.ContentHash) && Services.Media?.PathForHash(item.ContentHash) is { } indexed)
            return indexed;
        var full = MediaLibrary.ResolvePath(item, Services.ProjectFolder ?? (ProjectPath is { } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : null));
        return File.Exists(full) ? full : null;
    }

    private static bool ContentHasherValid(string hash) => ContentHasher.IsValid(hash);

    private string InspectJson(MediaItem item, HapInspection i) => JsonOut(w =>
    {
        var cores = Environment.ProcessorCount;
        var upload = HapUploadMeter.MbPerSecond;
        var cost = new HapLayerCost(i.MbPerFrameMax, i.DecodeMsPerFrame, i.TextureMbPerFrame, i.Fps, i.Texture);
        var budget = HapBudget.Layers(new HapNodeFacts(i.DiskMbPerSecond, cores, upload), cost);
        // The same clip at 4K60: its per-frame costs scaled by the pixel ratio, at 60 frames a second.
        var scale = i.Width > 0 && i.Height > 0 ? 3840.0 * 2160 / (i.Width * (double)i.Height) : 1;
        var at4K = HapBudget.Layers(new HapNodeFacts(i.DiskMbPerSecond, cores, upload),
            cost with { PeakMbPerFrame = i.MbPerFrameMax * scale, DecodeMsPerFrame = i.DecodeMsPerFrame * scale, TextureMbPerFrame = i.TextureMbPerFrame * scale, Fps = 60 });
        w.WriteStartObject();
        w.WriteString("media", item.Id);
        w.WriteString("codec", i.Codec);
        w.WriteString("texture", i.Texture);
        w.WriteNumber("width", i.Width);
        w.WriteNumber("height", i.Height);
        w.WriteBoolean("oddSize", i.OddSize);
        w.WriteNumber("frames", i.Frames);
        w.WriteNumber("fps", Math.Round(i.Fps, 3));
        w.WriteStartObject("chunks");
        w.WriteNumber("min", i.ChunksMin);
        w.WriteNumber("max", i.ChunksMax);
        w.WriteNumber("typical", i.ChunksTypical);
        w.WriteEndObject();
        w.WriteStartObject("secondStage");
        w.WriteNumber("snappy", i.SnappyShare);
        w.WriteNumber("raw", i.RawShare);
        w.WriteEndObject();
        w.WriteStartObject("mbPerFrame");
        w.WriteNumber("avg", Math.Round(i.MbPerFrameAvg, 3));
        w.WriteNumber("max", Math.Round(i.MbPerFrameMax, 3));
        w.WriteEndObject();
        w.WriteNumber("mbPerSecond", Math.Round(i.MbPerSecond, 1));
        w.WriteNumber("refused", i.Refused);
        if (i.Hdr.Length > 0)
            w.WriteString("hdr", i.Hdr);
        else
            w.WriteNull("hdr");
        w.WriteStartObject("budget");
        w.WriteString("node", Services.Settings.NodeId);
        w.WriteNumber("layers", budget.Layers);
        w.WriteNumber("layers4k60", at4K.Layers);
        w.WriteString("limit", budget.Limit);
        w.WriteString("words", budget.Words);
        w.WriteEndObject();
        w.WriteStartArray("fixes");
        foreach (var (words, line) in InspectFixes(item, i))
        {
            w.WriteStartObject();
            w.WriteString("words", words);
            w.WriteString("line", line);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    private IEnumerable<(string Words, string Line)> InspectFixes(MediaItem item, HapInspection i)
    {
        var c = CultureInfo.InvariantCulture;
        if (!i.IsHap)
        {
            yield return ($"{i.Codec} decodes on the CPU here: a HAP version plays on the GPU", $"MEDIA OPTIMISE {item.Id} TO hap-q");
            yield break;
        }
        if (HapCodecs.TryFromName(i.Codec, out var codec) && HapCodecs.Missing(codec, DecodeFacts.Formats ?? BlockFormats.Bc1 | BlockFormats.Bc3 | BlockFormats.Bc4) is { Length: > 0 } missing
            && !HapCodecs.FfmpegDecodes(codec))
            yield return (HapVideoSource.NotOnThisGpu(codec, missing), $"MEDIA OPTIMISE {item.Id} TO hap-q");
        if (i.ChunksTypical == 1 && i.Width * (long)i.Height >= 1920L * 1080 && i.Fps > 0)
            yield return (string.Create(c, $"1 chunk: each layer's frame decodes on one core ({i.DecodeMsPerFrame:0.0} ms of its {1000 / i.Fps:0.0} ms)"),
                $"MEDIA OPTIMISE {item.Id} TO rechunk");
        if (i.Refused > 0)
            yield return (string.Create(c, $"{i.Refused} frame{(i.Refused == 1 ? "" : "s")} refused: {i.FirstRefusal}"), $"MEDIA OPTIMISE {item.Id} TO {HapTarget(codec)}");
    }

    private static string HapTarget(HapCodec codec) => codec switch
    {
        HapCodec.Hap => "hap",
        HapCodec.HapAlpha => "hap-alpha",
        HapCodec.HapQAlpha => "hap-q-alpha",
        _ => "hap-q",
    };

    // ---- STATE --------------------------------------------------------------------------------------------------------------

    partial void StateDecode(Utf8JsonWriter w)
    {
        w.WritePropertyName("decode");
        WriteDecodeBody(w);
    }

    /// <summary>The decode object (STATE.decode, DECODE STATUS's answer).</summary>
    private void WriteDecodeBody(Utf8JsonWriter w)
    {
        var facts = DecodeFacts;
        var formats = facts.Formats;
        var media = Services.Media as MediaBackend;
        var decoders = media?.OpenDecoders() ?? [];
        var zero = facts.ZeroCopy;
        w.WriteStartObject();
        w.WriteStartObject("platform");
        w.WriteStartObject("blocks");
        var f = formats ?? BlockFormats.None;
        w.WriteBoolean("bc1", (f & BlockFormats.Bc1) != 0);
        w.WriteBoolean("bc3", (f & BlockFormats.Bc3) != 0);
        w.WriteBoolean("bc4", (f & BlockFormats.Bc4) != 0);
        w.WriteBoolean("bc7", (f & BlockFormats.Bc7) != 0);
        w.WriteBoolean("bc6h", (f & BlockFormats.Bc6h) != 0);
        var arena = formats is null ? null : zero.Arena;
        w.WriteString("staging", formats is null ? "none" : arena is not null ? "persistent" : "copy");
        w.WriteNumber("arenaMb", arena is null ? 0 : arena.CapacityBytes / (1024 * 1024));
        w.WriteNumber("arenaFreeMb", arena is null ? 0 : arena.FreeBytes / (1024 * 1024));
        w.WriteString("arenaWords", arena is not null ? "" : zero.ArenaWords);
        w.WriteEndObject();
        if (facts.Layers is { } layers)
        {
            w.WriteStartObject("hapLayers4k60");
            w.WriteNumber("value", layers.Value);
            w.WriteString("limit", layers.Limit);
            w.WriteNumber("measuredAt", layers.MeasuredAtUnixMs);
            w.WriteNumber("decodeMs", layers.DecodeMs);
            w.WriteNumber("cores", layers.Cores);
            w.WriteString("words", layers.Words);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("hapLayers4k60");
        }
        var (light, words) = DecodeLight(decoders, formats);
        w.WriteString("light", LightWord(light));
        w.WriteString("words", words);
        w.WriteString("api", zero.Api);
        w.WriteString("describe", zero.Describe);
        var gl = facts.Binding;
        w.WriteString("gl", gl.Length > 0 ? gl : formats is null ? "none" : "unknown");
        w.WriteString("zeroCopy", zero.Ready ? "ready" : "unavailable");
        w.WriteString("direct", zero.Api is "dmabuf" or "d3d11" ? zero.DirectProven ? "proven" : "unproven" : "n/a");
        w.WriteString("why", zero.Ready ? "" : zero.Words);
        w.WriteEndObject();
        w.WriteStartArray("sources");
        foreach (var d in decoders)
            WriteDecoder(w, d);
        w.WriteEndArray();
        var (policy, forced) = facts.Policy;
        w.WriteString("policy", DecodeLadder.Word(policy));
        w.WriteBoolean("forced", forced);
        w.WriteEndObject();
    }

    private static void WriteDecoder(Utf8JsonWriter w, MediaBackend.OpenDecoder d)
    {
        var h = d.Source.Health;
        w.WriteStartObject();
        w.WriteString("key", d.Source.Key);
        w.WriteString("media", d.ItemId);
        w.WriteString("name", d.Name);
        w.WriteString("codec", h.Hap?.Codec ?? d.Codec);
        w.WriteString("path", PathWord(h));
        w.WriteBoolean("zeroCopy", h.ZeroCopy);
        w.WriteNumber("decodeMsP95", h.DecodeMsP95);
        w.WriteNumber("busMbPerFrame", Math.Round(h.BusBytesPerFrame / 1e6, 3));
        w.WriteNumber("dropped", h.FramesDropped);
        w.WriteNumber("late", h.FramesLate);
        w.WriteString("status", h.Status);
        w.WriteString("why", !h.Ready ? h.Status : d.Why.Length > 0 ? d.Why : Join(h.Why, d.Ladder));
        w.WriteString("light", LightWord(DecoderLight(h)));
        if (h.Hap is { } hap)
        {
            w.WriteStartObject("hap");
            w.WriteString("texture", hap.Texture);
            w.WriteNumber("chunks", hap.Chunks);
            w.WriteNumber("rawShare", hap.RawShare);
            w.WriteString("secondStage", hap.SecondStage);
            w.WriteNumber("stageMsP95", hap.StageMsP95);
            w.WriteNumber("uploadMb", Math.Round(hap.UploadBytes / 1e6, 3));
            w.WriteString("staging", hap.Staging);
            w.WriteNumber("corrupt", hap.Corrupt);
            w.WriteString("lastRefusal", hap.LastRefusal);
            if (hap.Hdr.Length > 0)
                w.WriteString("hdr", hap.Hdr);
            else
                w.WriteNull("hdr");
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("hap");
        }
        w.WriteString("device", h.Device);
        w.WriteNumber("gpuCopies", h.GpuCopies);
        w.WriteNumber("width", d.Source.Info.Width);
        w.WriteNumber("height", d.Source.Info.Height);
        w.WriteNumber("surfaces", h.Surfaces);
        w.WriteNumber("vramMb", Math.Round(h.VramBytes / 1e6, 1));
        w.WriteNumber("importMsP95", h.ImportMsP95);
        w.WriteEndObject();
    }

    // Words of the open choice and of the source itself, each once.
    private static string Join(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 || a.Contains(b, StringComparison.Ordinal) ? a : b.Contains(a, StringComparison.Ordinal) ? b : a + "; " + b;

    /// <summary>A decoder's path word: the path its frames actually come by (DecodeLadder.Word): hap-gpu, hw-direct, hw-gpu-copy,
    /// hw-copy-back (a hardware decoder whose frames come back to system memory), software.</summary>
    internal static string PathWord(SourceHealth h) => h.Path == DecodePath.Software && h.Hardware ? "hw-copy-back" : DecodeLadder.Word(h.Path);

    private static Light DecoderLight(SourceHealth h) =>
        !h.Ready && h.Status is not ("opening" or "end" or "closed") ? Light.Red
        : h.Hap is { Corrupt: > 0 } ? Light.Amber
        : Light.Green;

    private (Light Light, string Words) DecodeLight(IReadOnlyList<MediaBackend.OpenDecoder> decoders, BlockFormats? formats)
    {
        var c = CultureInfo.InvariantCulture;
        foreach (var d in decoders)
        {
            var h = d.Source.Health;
            if (DecoderLight(h) == Light.Red)
                return (Light.Red, $"{d.Name}: {h.Status}");
        }
        if (formats is { } f)
        {
            // Only what the show plays: a library item nothing shows is not the show's problem.
            var project = Store.Project;
            foreach (var id in HapDemand.Of(project).ItemIds)
            {
                if (project.FindMedia(id) is { } item && HapCodecs.TryFromName(item.Info.VideoCodec, out var codec) && !HapCodecs.FfmpegDecodes(codec)
                    && HapCodecs.Missing(codec, f) is { Length: > 0 } missing)
                    return (Light.Red, $"{item.Name}: {HapVideoSource.NotOnThisGpu(codec, missing)}");
            }
        }
        foreach (var d in decoders)
        {
            if (d.Source.Health.Hap is { Corrupt: > 0 } hap)
                return (Light.Amber, $"{d.Name}: {hap.Corrupt} frame{(hap.Corrupt == 1 ? "" : "s")} refused — {hap.LastRefusal}");
        }
        // A clip of 4K or more whose frames come through system memory: the CPU carries what the GPU could (amber, with each named).
        var cpu = decoders.Where(d => d.Source.Health is { Ready: true, ZeroCopy: false } && IsFourK(d.Source.Info.Width, d.Source.Info.Height)).ToList();
        if (cpu.Count > 0)
            return (Light.Amber, string.Create(c, $"{cpu.Count} clip{(cpu.Count == 1 ? "" : "s")} at 4K decode{(cpu.Count == 1 ? "s" : "")} on the CPU: ")
                + string.Join(", ", cpu.Select(d => $"{d.Name} ({PathWord(d.Source.Health)})")));
        var gpu = decoders.Count(d => d.Source.Health.Path == DecodePath.HapGpu);
        var zero = decoders.Count(d => d.Source.Health.Path is DecodePath.HwDirect or DecodePath.HwGpuCopy);
        if (decoders.Count == 0)
            return (Light.Green, formats is null ? "no decoders open; no renderer yet" : "no decoders open");
        return (Light.Green, string.Create(c, $"{decoders.Count} decoder{(decoders.Count == 1 ? "" : "s")} · {gpu} HAP on the GPU")
            + (zero > 0 ? string.Create(c, $" · {zero} zero-copy") : ""));
    }

    private static string LightWord(Light light) => light switch
    {
        Light.Red => "red",
        Light.Amber => "amber",
        Light.Green => "green",
        _ => "grey",
    };

    partial void MachineDecode(Utf8JsonWriter w)
    {
        if (DecodeFacts.Formats is null)
            return;
        // The window-system binding the render context came through, as its own library answered on the render thread.
        var gl = DecodeFacts.Binding;
        w.WriteString("gl", gl.Length > 0 ? gl : "unknown");
    }

    partial void ContextDecode(List<ContextItem> items, List<ContextLink> links)
    {
        var decoders = (Services.Media as MediaBackend)?.OpenDecoders() ?? [];
        if (decoders.Count == 0 && HapDemand.Of(Store.Project).Items == 0)
            return;
        var (light, words) = DecodeLight(decoders, DecodeFacts.Formats);
        items.Add(new ContextItem("decode", "decode", "Decode", light, words));
    }

    partial void ValidateDecode(Project p, object entity, ref string? why)
    {
        if (why is not null || entity is not MediaItem { Color: { } color } item || color == ColorOverride.Auto)
            return;
        if (color.Transfer is not ("auto" or "sdr" or "pq" or "hlg" or "linear"))
            why = $"{item.Name}: color.transfer is auto, sdr, pq, hlg or linear, not \"{color.Transfer}\"";
        else if (!double.IsFinite(color.WhiteNits) || color.WhiteNits < 0 || color.WhiteNits > 10_000)
            why = string.Create(CultureInfo.InvariantCulture, $"{item.Name}: color.whiteNits is 0 (the rule's) or 1–10000 nits, not {color.WhiteNits}");
        else if (color.Range is not ("auto" or "full" or "limited"))
            why = $"{item.Name}: color.range is auto, full or limited, not \"{color.Range}\"";
        else if (color.Matrix is not ("auto" or "bt601" or "bt709" or "bt2020"))
            why = $"{item.Name}: color.matrix is auto, bt601, bt709 or bt2020, not \"{color.Matrix}\"";
    }

    /// <summary>The render host's word that its context came (with the families it samples) or went: the media backend opens
    /// HAP for them and STATE says them.</summary>
    internal void DecodeRenderer(BlockFormats? formats, string binding = "", DecodeContext? context = null)
    {
        Services.Media?.UseRenderer(formats ?? BlockFormats.None, context?.Arena);
        if (Services.Media is MediaBackend media)
            media.UseZeroCopy(context?.Share, context?.Platform);
        DecodeFacts.UseZeroCopy(context is null ? ZeroCopyFacts.None
            : new ZeroCopyFacts(ZeroCopyFacts.ApiWord(context.Share?.Api ?? GpuApi.None), DescribeShare(context), context.Platform.Ready,
                context.Platform.DirectProven, context.Platform.Words, context.Arena, context.ArenaWords));
        DecodeFacts.Renderer(formats, binding);
    }

    /// <summary>The share in words: a zero-copy share's own description (its render node, its adapter); a plain GL share's API and
    /// binding only (the renderer's name is machine.renderer's).</summary>
    private static string DescribeShare(DecodeContext context) => context.Share switch
    {
        { Api: GpuApi.DmaBuf or GpuApi.D3D11 } share => share.Describe,
        null => "",
        _ => $"OpenGL through {(context.Binding.Length > 0 ? context.Binding.ToUpperInvariant() : "an unknown binding")}: no zero-copy imports",
    };

    /// <summary>At least 4K: the 3840×2160 class (a DCI 4096 or a slightly cropped UHD frame included).</summary>
    internal static bool IsFourK(int width, int height) => (long)width * height >= 3840L * 2000;
}
