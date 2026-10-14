using System.Globalization;

namespace PlayMap.Core.Media.Hap;

/// <summary>What a node can move per second: disk reads, decode cores, uploads to the GPU. A term that was not measured is 0
/// and takes no part in a budget.</summary>
public readonly record struct HapNodeFacts(double DiskMbPerSecond, int Cores, double UploadMbPerSecond);

/// <summary>What one layer of a clip costs: its largest frame as stored (MB), its decode time on one core (ms), its
/// texture bytes per frame (MB) and its rate. <see cref="Texture"/> names the texture for the words.</summary>
public readonly record struct HapLayerCost(double PeakMbPerFrame, double DecodeMsPerFrame, double TextureMbPerFrame, double Fps, string Texture);

/// <summary>How many layers of a clip a node sustains, the term that binds ("disk", "cores", "upload"; "unmeasured" when no
/// term was measured), each term's own count (−1 = not measured) and the arithmetic in words, binding term first.</summary>
public readonly record struct HapLayerBudget(int Layers, string Limit, int ByDisk, int ByCores, int ByUpload, string Words);

/// <summary>How far a HAP source reads ahead: frames held, the cap that binds ("time" or "bytes") and the bytes they hold.</summary>
public readonly record struct HapReadAhead(int Frames, string Binds, long Bytes);

/// <summary>The HAP layer budget and the read-ahead caps, as arithmetic (MB = 10⁶ bytes, as the bytes-per-frame figures are
/// quoted). The budget is min(disk ÷ peak MB/s per layer, cores × frame time ÷ decode ms per layer, upload ÷ texture MB/s per
/// layer); a term not measured is left out and the words say so.</summary>
public static class HapBudget
{
    public const double Mb = 1_000_000;

    public static HapLayerBudget Layers(HapNodeFacts node, HapLayerCost layer)
    {
        var c = CultureInfo.InvariantCulture;
        var fps = layer.Fps > 0 ? layer.Fps : 60;
        var frameMs = 1000 / fps;
        var peakPerSecond = layer.PeakMbPerFrame * fps;
        var texturePerSecond = layer.TextureMbPerFrame * fps;
        var disk = node.DiskMbPerSecond > 0 && peakPerSecond > 0 ? (int)Math.Floor(node.DiskMbPerSecond / peakPerSecond) : -1;
        var cores = node.Cores > 0 && layer.DecodeMsPerFrame > 0 ? (int)Math.Floor(node.Cores * frameMs / layer.DecodeMsPerFrame) : -1;
        var upload = node.UploadMbPerSecond > 0 && texturePerSecond > 0 ? (int)Math.Floor(node.UploadMbPerSecond / texturePerSecond) : -1;

        var diskWords = disk < 0
            ? "disk not measured"
            : string.Create(c, $"disk {Rate(node.DiskMbPerSecond)} ÷ {peakPerSecond:0} MB/s at peak = {disk}");
        var coreWords = cores < 0
            ? "cores not measured"
            : string.Create(c, $"cores {node.Cores} × {frameMs:0.0} ms ÷ {layer.DecodeMsPerFrame:0.0} ms = {cores}");
        var uploadWords = upload < 0
            ? "upload not measured"
            : string.Create(c, $"upload {Rate(node.UploadMbPerSecond)} ÷ {texturePerSecond:0} MB/s per layer ({layer.TextureMbPerFrame:0.0} MB of {Name(layer.Texture)} at {fps:0.##} fps) = {upload}");

        // The binding term is the smallest measured one; ties go to the order disk, cores, upload.
        var limit = "unmeasured";
        var layers = 0;
        foreach (var (name, n) in new[] { ("disk", disk), ("cores", cores), ("upload", upload) })
        {
            if (n >= 0 && (limit == "unmeasured" || n < layers))
            {
                limit = name;
                layers = n;
            }
        }
        var ordered = limit switch
        {
            "disk" => new[] { diskWords, coreWords, uploadWords },
            "upload" => [uploadWords, diskWords, coreWords],
            _ => [coreWords, diskWords, uploadWords],
        };
        return new HapLayerBudget(layers, limit, disk, cores, upload, string.Join("; ", ordered));
    }

    /// <summary>The cost of one 4K60 layer of a codec decoding in <paramref name="decodeMs"/> per frame on one core, with frames
    /// stored at <paramref name="storedMbPerFrame"/> (0 = the texture's size, a frame stored raw).</summary>
    public static HapLayerCost Layer4K60(HapCodec codec, double decodeMs, double storedMbPerFrame = 0)
    {
        var texture = HapCodecs.FrameBytes(codec, 3840, 2160) / Mb;
        var layout = HapCodecs.Layout(codec, HapCodecs.FirstTexture(codec));
        return new HapLayerCost(storedMbPerFrame > 0 ? storedMbPerFrame : texture, decodeMs, texture, 60, HapCodecs.TextureWord(layout));
    }

    /// <summary>How many frames a source reads ahead: as many as fill <paramref name="readAheadMs"/> at the clip's rate, and no
    /// more than fit in <paramref name="readAheadMb"/>; at least one.</summary>
    public static HapReadAhead ReadAhead(int readAheadMs, int readAheadMb, double fps, long bytesPerFrame)
    {
        var rate = fps > 0 && double.IsFinite(fps) ? fps : 30;
        var byTime = Math.Max(1, (int)Math.Floor(Math.Max(1, readAheadMs) * rate / 1000 + 1e-9));
        var byBytes = bytesPerFrame > 0 ? Math.Max(1, (int)Math.Min(int.MaxValue, (long)(Math.Max(1, readAheadMb) * Mb) / bytesPerFrame)) : int.MaxValue;
        return byTime <= byBytes
            ? new HapReadAhead(byTime, "time", byTime * Math.Max(0, bytesPerFrame))
            : new HapReadAhead(byBytes, "bytes", byBytes * bytesPerFrame);
    }

    private static string Rate(double mbPerSecond) => mbPerSecond >= 1000
        ? string.Create(CultureInfo.InvariantCulture, $"{mbPerSecond / 1000:0.0} GB/s")
        : string.Create(CultureInfo.InvariantCulture, $"{mbPerSecond:0} MB/s");

    private static string Name(string texture) => texture switch
    {
        "ycocg-bc3" => "BC3",
        "ycocg-bc3+bc4" => "BC3+BC4",
        "" => "blocks",
        _ => texture.ToUpperInvariant(),
    };
}
