using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;
using PlayMap.Media.Ffmpeg;

namespace PlayMap.Media.Hap;

/// <summary>What a clip is to the decoder, read from every packet header: codec and texture, size, frame count and rate,
/// chunks per frame, the second stage's mix, stored MB per frame and per second, refused frames, the HDR reading, the disk
/// rate the pass read at, and the second stage's time per frame on one core (from a sample of frames).</summary>
public sealed record HapInspection(
    bool IsHap,
    string Codec,
    string Texture,
    int Width,
    int Height,
    long Frames,
    double Fps,
    int ChunksMin,
    int ChunksMax,
    int ChunksTypical,
    double SnappyShare,
    double RawShare,
    double MbPerFrameAvg,
    double MbPerFrameMax,
    long Refused,
    string FirstRefusal,
    double DecodeMsPerFrame,
    double DiskMbPerSecond,
    string Hdr)
{
    public bool OddSize => Width % 4 != 0 || Height % 4 != 0;

    public double MbPerSecond => MbPerFrameAvg * Fps;

    /// <summary>Texture MB per frame (what each frame uploads).</summary>
    public double TextureMbPerFrame =>
        IsHap && HapCodecs.TryFromName(Codec, out var c) ? HapCodecs.FrameBytes(c, Width, Height) / HapBudget.Mb : 0;
}

/// <summary>Reads a clip once, end to end (a disk pass: never while the show is armed), for MEDIA INSPECT.</summary>
public static unsafe class HapInspect
{
    /// <summary>Inspects the clip at <paramref name="path"/>; the first <paramref name="sample"/> parsed frames are decoded on
    /// this thread to time the second stage. Throws <see cref="MediaOpenException"/> when the file cannot be read.</summary>
    public static HapInspection Inspect(string path, ColorOverride? color = null, int sample = 8, CancellationToken ct = default)
    {
        using var input = MediaInput.Open(path);
        var stream = input.BestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
        if (stream < 0)
            throw new MediaOpenException(MediaProblem.NoStream, "no video stream in " + Path.GetFileName(path));
        input.KeepOnly(stream);
        var s = input.Stream(stream);
        var p = s->codecpar;
        var fr = input.FrameRate(stream);
        var fps = fr.num > 0 && fr.den > 0 ? fr.num / (double)fr.den : 0;
        var isHap = HapCodecs.TryFromTag(p->codec_tag, out var codec);
        var name = isHap ? HapCodecs.Name(codec) : ffmpeg.avcodec_get_name(p->codec_id) ?? "";
        var texture = isHap ? HapCodecs.TextureWord(HapCodecs.Layout(codec, HapCodecs.FirstTexture(codec))) : "";
        var hdr = isHap && codec == HapCodec.HapHdr ? HapHdr.Read(color, (int)p->color_trc, HapVideoSource.WhiteTag(input.Format, s)).Words : "";
        var frame = new HapFrame();
        var chunkCounts = new List<int>();
        long frames = 0, bytes = 0, maxBytes = 0, refused = 0, snappy = 0, raw = 0;
        var firstRefusal = "";
        var decodeTicks = 0L;
        var decoded = 0;
        var readTicks = 0L;
        byte* scratch = null;
        long scratchBytes = 0;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var t0 = Stopwatch.GetTimestamp();
                var r = ffmpeg.av_read_frame(input.Format, packet);
                readTicks += Stopwatch.GetTimestamp() - t0;
                if (r < 0)
                    break;
                if (packet->stream_index != stream)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }
                frames++;
                bytes += packet->size;
                maxBytes = Math.Max(maxBytes, packet->size);
                if (isHap)
                {
                    var data = new ReadOnlySpan<byte>(packet->data, packet->size);
                    if (!frame.TryParse(data, p->width, p->height, codec, out _))
                    {
                        if (refused++ == 0)
                            firstRefusal = frame.Words(frames - 1);
                    }
                    else
                    {
                        var chunks = frame.AllChunks.Length;
                        chunkCounts.Add(chunks);
                        raw += frame.RawChunks;
                        snappy += chunks - frame.RawChunks;
                        if (decoded < sample)
                        {
                            var need = HapCodecs.FrameBytes(codec, p->width, p->height);
                            if (need > scratchBytes)
                            {
                                NativeMemory.Free(scratch);
                                scratch = (byte*)NativeMemory.Alloc((nuint)need);
                                scratchBytes = need;
                            }
                            var d0 = Stopwatch.GetTimestamp();
                            var offset = 0;
                            for (var t = 0; t < frame.TextureCount; t++)
                            {
                                foreach (var c in frame.Chunks(t))
                                    HapWorkers.Decode(packet->data + c.SourceOffset, c.SourceLength, scratch + offset + c.TargetOffset, c.TargetLength, c.Compressor);
                                offset += frame.Texture(t).Bytes;
                            }
                            decodeTicks += Stopwatch.GetTimestamp() - d0;
                            decoded++;
                        }
                    }
                }
                ffmpeg.av_packet_unref(packet);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            NativeMemory.Free(scratch);
        }
        chunkCounts.Sort();
        var allChunks = snappy + raw;
        var readSeconds = Stopwatch.GetElapsedTime(0, readTicks).TotalSeconds;
        return new HapInspection(isHap, name, texture, p->width, p->height, frames, fps,
            chunkCounts.Count > 0 ? chunkCounts[0] : 0,
            chunkCounts.Count > 0 ? chunkCounts[^1] : 0,
            chunkCounts.Count > 0 ? chunkCounts[chunkCounts.Count / 2] : 0,
            allChunks > 0 ? Math.Round(snappy / (double)allChunks, 3) : 0,
            allChunks > 0 ? Math.Round(raw / (double)allChunks, 3) : 0,
            frames > 0 ? bytes / (double)frames / HapBudget.Mb : 0,
            maxBytes / HapBudget.Mb,
            refused,
            firstRefusal,
            decoded > 0 ? Stopwatch.GetElapsedTime(0, decodeTicks).TotalMilliseconds / decoded : 0,
            readSeconds > 0 ? bytes / HapBudget.Mb / readSeconds : 0,
            hdr);
    }
}

/// <summary>This node's HAP second stage measured on a synthetic 4K HAP Q frame (sixteen chunks of Snappy elements, literals and
/// back-references mixed as texture blocks compress): the time one core takes per frame. Upload is not part of it.</summary>
public static unsafe class HapThroughput
{
    /// <summary>Milliseconds per 4K HAP Q frame on one core: the median of <paramref name="frames"/> decodes after one warm-up.</summary>
    public static double DecodeMsPer4KFrame(int frames = 9, CancellationToken ct = default)
    {
        const int w = 3840, h = 2160, chunks = 16;
        var texture = (int)HapCodecs.FrameBytes(HapCodec.HapQ, w, h);
        var plan = new HapChunkOut[chunks];
        var count = HapChunking.Plan(texture / 16, 16, chunks, plan);
        var streams = new byte[count][];
        var seed = 0x9E3779B9u;
        for (var i = 0; i < count; i++)
            streams[i] = Synthetic(plan[i].Length, ref seed);
        var target = (byte*)NativeMemory.Alloc((nuint)texture);
        try
        {
            var times = new double[Math.Max(1, frames)];
            for (var f = -1; f < times.Length; f++)
            {
                ct.ThrowIfCancellationRequested();
                var t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < count; i++)
                {
                    fixed (byte* src = streams[i])
                    {
                        if (!HapWorkers.Decode(src, streams[i].Length, target + plan[i].Offset, plan[i].Length, HapCompressor.Snappy))
                            throw new InvalidOperationException("the synthetic HAP frame did not decode");
                    }
                }
                if (f >= 0)
                    times[f] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            Array.Sort(times);
            return times[times.Length / 2];
        }
        finally
        {
            NativeMemory.Free(target);
        }
    }

    /// <summary>A Snappy block decoding to <paramref name="length"/> bytes: seven parts literal to three parts back-reference
    /// (lengths 4–64, offsets up to 4 KB), the mix noisy HAP Q content compresses to.</summary>
    internal static byte[] Synthetic(int length, ref uint seed)
    {
        var o = new List<byte>(length + length / 8 + 16);
        var n = (uint)length;
        while (n >= 0x80)
        {
            o.Add((byte)(n | 0x80));
            n >>= 7;
        }
        o.Add((byte)n);
        var produced = 0;
        while (produced < length)
        {
            var left = length - produced;
            var x = Next(ref seed);
            if (produced < 64 || left < 4 || x % 10 < 7)
            {
                var len = Math.Min(left, 16 + (int)(x >> 8) % 45);
                o.Add((byte)((len - 1) << 2));
                for (var i = 0; i < len; i++)
                    o.Add((byte)Next(ref seed));
                produced += len;
            }
            else
            {
                var len = Math.Min(left, 4 + (int)(x >> 8) % 61);
                var off = 1 + (int)(Next(ref seed) % (uint)Math.Min(produced, 4096));
                o.Add((byte)(((len - 1) << 2) | 2));
                o.Add((byte)off);
                o.Add((byte)(off >> 8));
                produced += len;
            }
        }
        return [.. o];
    }

    private static uint Next(ref uint x)
    {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }
}
