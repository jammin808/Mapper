using System.Collections.Concurrent;
using System.Globalization;
using FFmpeg.AutoGen;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Hap;

namespace PlayMap.Media.Tests.Hap;

/// <summary>HAP clips the tests play, made at test time: FFmpeg 9's own <c>hap</c> encoder (<c>hap</c>, <c>hap_alpha</c>,
/// <c>hap_q</c>) over the frame-index painter, and — for the codecs FFmpeg cannot encode (HAP Q Alpha, Alpha-Only, R, HDR) —
/// frames assembled by <see cref="HapFrameWriter"/> from seeded block planes, muxed into a QuickTime file through
/// libavformat with the codec's fourcc. Every assembled frame's planes are known, so a test compares bytes.</summary>
internal static unsafe class HapClips
{
    public const int Width = 160, Height = 96, Fps = 30, Frames = 120;

    private static readonly ConcurrentDictionary<string, Lazy<string>> Made = new(StringComparer.Ordinal);

    /// <summary>A clip FFmpeg encodes: <paramref name="format"/> is hap, hap_alpha or hap_q; <paramref name="compressor"/>
    /// snappy or none.</summary>
    public static string Ffmpeg(string format, int chunks, string compressor)
    {
        TestMedia.Ensure();
        var name = $"{format}_{chunks}_{compressor}.mov";
        return Made.GetOrAdd(name, n => new Lazy<string>(() =>
        {
            var path = Path.Combine(TestMedia.Dir, n);
            var src = string.Create(CultureInfo.InvariantCulture,
                $"nullsrc=s={Width}x{Height}:r={Fps}:d=5,format=yuv420p,{TestMedia.IndexPainter},format=rgba");
            TestMedia.Run("-f", "lavfi", "-i", src, "-frames:v", Frames.ToString(CultureInfo.InvariantCulture), "-c:v", "hap",
                "-format", format, "-chunks", chunks.ToString(CultureInfo.InvariantCulture), "-compressor", compressor, path);
            return path;
        })).Value;
    }

    /// <summary>The planes of assembled frame <paramref name="n"/> of a codec (one, or two for HAP Q Alpha).</summary>
    public static byte[][] Planes(HapCodec codec, int n, int width = Width, int height = Height)
    {
        var (a, b) = HapCodecs.PlaneBlockBytes(HapCodecs.Layout(codec, HapCodecs.FirstTexture(codec)));
        var planes = b > 0 ? new byte[2][] : new byte[1][];
        planes[0] = Fill(HapCodecs.PlaneBytes(width, height, a), n * 2 + 1);
        if (b > 0)
            planes[1] = Fill(HapCodecs.PlaneBytes(width, height, b), n * 2 + 2);
        return planes;
    }

    /// <summary>A frame of the codec holding <see cref="Planes"/>(n), in <paramref name="chunks"/> literal-Snappy chunks.</summary>
    public static byte[] Frame(HapCodec codec, int n, int chunks, int width = Width, int height = Height)
    {
        var planes = Planes(codec, n, width, height);
        var kinds = codec == HapCodec.HapQAlpha ? new[] { HapTexture.Bc3YCoCg, HapTexture.Bc4Alpha } : [HapCodecs.FirstTexture(codec)];
        var outs = new HapChunkOut[planes.Length][];
        var datas = new byte[planes.Length][];
        for (var t = 0; t < planes.Length; t++)
        {
            var bb = HapCodecs.BlockBytes(kinds[t]);
            var plan = new HapChunkOut[HapFrame.MaxChunks];
            var count = HapChunking.Plan(planes[t].Length / bb, bb, chunks, plan);
            var data = new List<byte>();
            outs[t] = new HapChunkOut[count];
            for (var i = 0; i < count; i++)
            {
                var z = SnappyLiteral(planes[t].AsSpan(plan[i].Offset, plan[i].Length));
                outs[t][i] = new HapChunkOut(data.Count, z.Length, HapCompressor.Snappy);
                data.AddRange(z);
            }
            datas[t] = [.. data];
        }
        var frame = new byte[HapFrameWriter.Measure(outs[0], planes.Length > 1 ? outs[1] : default)];
        if (planes.Length > 1)
            HapFrameWriter.Write(frame, kinds[0], outs[0], datas[0], kinds[1], outs[1], datas[1]);
        else
            HapFrameWriter.Write(frame, kinds[0], outs[0], datas[0]);
        return frame;
    }

    /// <summary>A clip of assembled frames. <paramref name="corrupt"/> names frames whose Snappy data is broken (their table
    /// still parses); <paramref name="refused"/> frames whose header lies about their length.</summary>
    public static string Assembled(HapCodec codec, int chunks = 4, int[]? corrupt = null, int[]? refused = null, int colorTrc = 0,
        int width = Width, int height = Height, int count = Frames)
    {
        TestMedia.Ensure();
        var name = string.Create(CultureInfo.InvariantCulture,
            $"{HapCodecs.Name(codec)}_{chunks}_{string.Join('-', corrupt ?? [])}_{string.Join('-', refused ?? [])}_{colorTrc}_{width}x{height}x{count}.mov");
        return Made.GetOrAdd(name, n => new Lazy<string>(() =>
        {
            var frames = new byte[count][];
            for (var i = 0; i < count; i++)
            {
                frames[i] = Frame(codec, i, chunks, width, height);
                if (corrupt?.Contains(i) == true)
                {
                    // The first element of chunk 0 becomes a copy from 16 bytes back at output position 0: the table still
                    // parses, and a Snappy decoder refuses the data.
                    var parsed = new HapFrame();
                    Assert.True(parsed.TryParse(frames[i], width, height, codec, out _));
                    var c = parsed.Chunks(0)[0];
                    Assert.True(SnappyLength.TryRead(frames[i].AsSpan(c.SourceOffset), out _, out var pre));
                    frames[i][c.SourceOffset + pre] = 0x01;
                    frames[i][c.SourceOffset + pre + 1] = 0x10;
                }
                if (refused?.Contains(i) == true)
                    frames[i] = frames[i][..^5];
            }
            var path = Path.Combine(TestMedia.Dir, n);
            Mux(path, codec, width, height, Fps, frames, colorTrc);
            return path;
        })).Value;
    }

    /// <summary>Writes frames as a QuickTime file of one HAP video track with the codec's fourcc, through libavformat (the
    /// fourccs FFmpeg does not map are written as given under unofficial compliance). A non-zero <paramref name="colorTrc"/>
    /// writes a colr box with BT.2020 primaries and matrix and that transfer.</summary>
    public static void Mux(string path, HapCodec codec, int width, int height, int fps, IReadOnlyList<byte[]> frames, int colorTrc = 0)
    {
        AVFormatContext* oc = null;
        Check(ffmpeg.avformat_alloc_output_context2(&oc, null, "mov", path), "output");
        try
        {
            var st = ffmpeg.avformat_new_stream(oc, null);
            var p = st->codecpar;
            p->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            p->codec_id = AVCodecID.AV_CODEC_ID_HAP;
            p->codec_tag = HapCodecs.Tag(codec);
            p->width = width;
            p->height = height;
            if (colorTrc != 0)
            {
                p->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
                p->color_trc = (AVColorTransferCharacteristic)colorTrc;
                p->color_space = AVColorSpace.AVCOL_SPC_BT2020_NCL;
            }
            st->time_base = new AVRational { num = 1, den = fps };
            st->avg_frame_rate = new AVRational { num = fps, den = 1 };
            oc->strict_std_compliance = ffmpeg.FF_COMPLIANCE_UNOFFICIAL;
            Check(ffmpeg.avio_open(&oc->pb, path, ffmpeg.AVIO_FLAG_WRITE), "open");
            Check(ffmpeg.avformat_write_header(oc, null), "header");
            var packet = ffmpeg.av_packet_alloc();
            try
            {
                for (var i = 0; i < frames.Count; i++)
                {
                    Check(ffmpeg.av_new_packet(packet, frames[i].Length), "packet");
                    frames[i].AsSpan().CopyTo(new Span<byte>(packet->data, frames[i].Length));
                    packet->pts = packet->dts = i;
                    packet->duration = 1;
                    packet->stream_index = 0;
                    packet->flags = ffmpeg.AV_PKT_FLAG_KEY;
                    ffmpeg.av_packet_rescale_ts(packet, new AVRational { num = 1, den = fps }, st->time_base);
                    Check(ffmpeg.av_interleaved_write_frame(oc, packet), "frame");
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
            }
            Check(ffmpeg.av_write_trailer(oc), "trailer");
        }
        finally
        {
            ffmpeg.avio_closep(&oc->pb);
            ffmpeg.avformat_free_context(oc);
        }
    }

    /// <summary>Reads the frame index the painter put in an FFmpeg-made clip's top rows, from the blocks themselves: a 4×4
    /// block inside a 20-pixel bit cell is uniform, so its first endpoint is its colour (BC1: colour 0 as RGB565; BC3: colour 0
    /// of the colour half; YCoCg BC3: alpha 0, which holds luma).</summary>
    public static int ReadIndex(VideoFrameLease lease)
    {
        var plane = lease.Planes[0];
        var value = 0;
        for (var bit = 0; bit < 16; bit++)
        {
            var x = bit * 20 + 10;
            if (x >= lease.Width)
                break;
            var bx = x / 4;
            const int by = 20 / 4;
            var on = lease.Layout switch
            {
                PixelLayout.Bc1 => Green565(Block(plane, bx, by, 8)) > 128,
                PixelLayout.Bc3 => Green565(Block(plane, bx, by, 16) + 8) > 128,
                PixelLayout.Bc3YCoCg => *Block(plane, bx, by, 16) > 128,
                _ => throw new InvalidOperationException("not an FFmpeg HAP layout: " + lease.Layout),
            };
            if (on)
                value |= 1 << bit;
        }
        return value;
    }

    /// <summary>The bytes of plane <paramref name="index"/> of a lease, as a managed copy.</summary>
    public static byte[] PlaneBytes(VideoFrameLease lease, int index)
    {
        var p = lease.Planes[index];
        return new ReadOnlySpan<byte>((void*)p.Data, p.Stride * p.Height).ToArray();
    }

    private static byte* Block(FramePlane plane, int bx, int by, int blockBytes) => (byte*)plane.Data + (nint)by * plane.Stride + bx * blockBytes;

    private static int Green565(byte* p) => (((p[0] | (p[1] << 8)) >> 5) & 0x3F) * 255 / 63;

    private static byte[] Fill(long bytes, int seed)
    {
        var p = new byte[bytes];
        var x = (uint)seed * 2654435761u + 12345u;
        for (var i = 0; i < p.Length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            p[i] = (byte)x;
        }
        return p;
    }

    /// <summary>A literal-only Snappy stream (the format's length preamble, then literal elements): input any Snappy decoder
    /// reads, made without an encoder.</summary>
    public static byte[] SnappyLiteral(ReadOnlySpan<byte> data)
    {
        var o = new List<byte>();
        var n = (uint)data.Length;
        while (n >= 0x80)
        {
            o.Add((byte)(n | 0x80));
            n >>= 7;
        }
        o.Add((byte)n);
        for (var at = 0; at < data.Length; at += 65536)
        {
            var len = Math.Min(65536, data.Length - at);
            if (len <= 60)
            {
                o.Add((byte)((len - 1) << 2));
            }
            else if (len <= 256)
            {
                o.Add(60 << 2);
                o.Add((byte)(len - 1));
            }
            else
            {
                o.Add(61 << 2);
                o.Add((byte)(len - 1));
                o.Add((byte)((len - 1) >> 8));
            }
            o.AddRange(data.Slice(at, len));
        }
        return [.. o];
    }

    private static void Check(int r, string what)
    {
        if (r < 0)
            throw new InvalidOperationException($"muxing a HAP clip failed at {what}: {r}");
    }
}
