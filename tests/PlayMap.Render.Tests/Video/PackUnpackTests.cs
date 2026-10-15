using System.Collections.Immutable;
using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Video;
using PlayMap.Render.Engine;
using PlayMap.Render.Gpu;
using PlayMap.Render.Passes;
using PlayMap.Render.Video;

namespace PlayMap.Render.Tests.Video;

/// <summary>The network layouts on the GPU against their CPU twin (<see cref="PackReference"/>): UYVY, UYVA, P216 and PA16 charts
/// unpack within 1 code at 8 bits and 4 codes at 16 bits, with alpha; UYVY, UYVA and BGRA pack and read back equal to the twin at
/// 1920×1080 and 1280×720; a read-back slot the sender holds stays mapped and unchanged until released, the ring never blocks, and
/// the send stage allocates nothing per frame once warm.</summary>
[Collection("gl")]
public sealed class PackUnpackTests(GlFixture gl)
{
    /// <summary>64 colours in 8×8 blocks of 8 pixels (8-bit codes), alpha a ramp across x.</summary>
    private static float[] Chart(int w, int h)
    {
        var rgba = new float[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = (y / 8 % 8) * 8 + x / 8 % 8;
                var o = (y * w + x) * 4;
                rgba[o] = (c * 37 % 256) / 255f;
                rgba[o + 1] = (c * 91 % 256) / 255f;
                rgba[o + 2] = (255 - c * 53 % 256) / 255f;
                rgba[o + 3] = (x * 255 / Math.Max(1, w - 1)) / 255f;
            }
        }
        return rgba;
    }

    private static ArrayLease LeaseOf(PixelLayout layout, byte[] frame, int w, int h)
    {
        var planes = new List<(byte[] Data, int Stride, int Width, int Height)>();
        var offset = 0;
        for (var i = 0; i < LivePlanes.PlaneCount(layout); i++)
        {
            var stride = LivePlanes.Stride(layout, i, w);
            planes.Add((frame[offset..(offset + stride * h)], stride, w, h));
            offset += stride * h;
        }
        return new ArrayLease(layout, w, h, 1, ColorMatrix.Bt709, ColorRange.Limited, [.. planes]);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GlDevice, ShaderProgram> CopyPrograms = [];

    /// <summary>A texture read back exactly as floats (RGBA, top row first), alpha included.</summary>
    private static float[] ReadFloats(GlDevice d, GpuTexture texture)
    {
        var copy = CopyPrograms.GetValue(d, dev => dev.CreateProgram("test.copy", """
            out vec2 vUv;
            void main() {
                vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
                vUv = p;
                gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
            }
            """, """
            uniform sampler2D uTex;
            out vec4 oColor;
            void main() { oColor = texelFetch(uTex, ivec2(gl_FragCoord.xy), 0); }
            """));
        using var target = d.CreateRenderTarget(texture.Width, texture.Height, TextureFormat.Rgba32F, filter: TextureFilter.Nearest);
        d.BindTarget(target);
        d.SetBlend(BlendMode.Opaque);
        d.BindSampler(copy, "uTex", 0, texture);
        d.DrawFullscreen(copy);
        var px = new float[texture.Width * texture.Height * 4];
        d.ReadPixelsFloat(target, px);
        return px;
    }

    [Theory]
    [InlineData(PixelLayout.Uyvy)]
    [InlineData(PixelLayout.Uyva)]
    [InlineData(PixelLayout.P216)]
    [InlineData(PixelLayout.Pa16)]
    public void AChartUnpacksAsItsCpuTwinDoesWithItsAlpha(PixelLayout layout)
    {
        const int w = 64, h = 64;
        var chart = Chart(w, h);
        var frame = PackReference.Pack(layout, chart, w, h);
        var expected = PackReference.Unpack(layout, frame, w, h);
        var deep = LivePlanes.IsDeep(layout);
        var got = gl.Run(c =>
        {
            using var set = new VideoTextureSet(c.Device);
            using var lease = LeaseOf(layout, frame, w, h);
            Assert.True(set.Update(lease));
            Assert.Equal(LivePlanes.HasAlpha(layout), set.HasAlpha);
            var px = ReadFloats(c.Device, set.Picture!);
            Assert.Empty(c.Device.TakeErrors());
            return px;
        });
        var scale = deep ? 65535f : 255f;
        var limit = deep ? 4f : 1f;
        var worst = 0f;
        for (var i = 0; i < got.Length; i++)
            worst = Math.Max(worst, Math.Abs(got[i] - expected[i]) * scale);
        Assert.True(worst <= limit, $"{layout}: worst {worst:0.00} codes");
        // The alpha plane reaches the picture's alpha (a ramp across x), or alpha is 1.
        var right = got[((h / 2) * w + w - 1) * 4 + 3];
        var left = got[((h / 2) * w) * 4 + 3];
        Assert.Equal(1f, right, 0.002f);
        Assert.Equal(LivePlanes.HasAlpha(layout) ? 0f : 1f, left, 0.002f);
        // Inside a block the picture is the chart within a code and a half (chroma is shared by a pair only at block edges).
        for (var y = 2; y < h; y += 8)
        {
            for (var x = 2; x < w; x += 8)
            {
                var o = (y * w + x) * 4;
                for (var ch = 0; ch < 3; ch++)
                    Assert.True(Math.Abs(got[o + ch] - chart[o + ch]) * 255 <= 1.5f, $"{layout} ({x},{y}) channel {ch}: {got[o + ch] * 255:0.0} vs {chart[o + ch] * 255:0.0}");
            }
        }
    }

    [Fact]
    public void TheLastPairsChromaIsClampedAtTheRightEdgeNotWrapped()
    {
        const int w = 8, h = 2;
        var rgba = new float[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                // Blue on the left half, red on the right: a wrap would pull blue into the last pixel.
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = x < w / 2 ? (0f, 0f, 1f, 1f) : (1f, 0f, 0f, 1f);
            }
        }
        var frame = PackReference.Pack(PixelLayout.Uyvy, rgba, w, h);
        var got = gl.Run(c =>
        {
            using var set = new VideoTextureSet(c.Device);
            using var lease = LeaseOf(PixelLayout.Uyvy, frame, w, h);
            set.Update(lease);
            return ReadFloats(c.Device, set.Picture!);
        });
        var last = (w - 1) * 4;
        Assert.True(got[last] > 0.9f && got[last + 2] < 0.1f, $"last pixel {got[last]:0.00},{got[last + 1]:0.00},{got[last + 2]:0.00}");
    }

    [Fact]
    public void AnOddWidthIsRefusedWithWords()
    {
        var why = gl.Run(c =>
        {
            using var set = new VideoTextureSet(c.Device);
            using var lease = new ArrayLease(PixelLayout.Uyvy, 63, 4, 1, ColorMatrix.Bt709, ColorRange.Limited, (new byte[126 * 4], 126, 63, 4));
            return Assert.Throws<ArgumentException>(() => set.Update(lease)).Message;
        });
        Assert.Contains("needs an even width (4:2:2 pairs), not 63", why, StringComparison.Ordinal);
    }

    /// <summary>A sender that copies what it is offered and releases it, or holds it until told.</summary>
    private sealed class TestSink(bool hold) : ISendSink
    {
        public List<byte[]> Frames { get; } = [];
        public List<SendFrame> Held { get; } = [];
        public string FullName => "TEST (sink)";
        public int Connections { get; set; } = 1;
        public (bool Program, bool Preview) Tally => default;
        public SendStats Stats => new(Frames.Count, 0, 0, 0, 0);
        public string Words => "";

        public void Offer(SendFrame frame)
        {
            var copy = new byte[frame.Bytes];
            Marshal.Copy(frame.Data, copy, 0, frame.Bytes);
            Frames.Add(copy);
            if (hold)
                Held.Add(frame);
            else
                frame.Release();
        }

        public void Update(SendSpec spec)
        {
            // The test's sender sends nothing anywhere.
        }

        public void Dispose()
        {
            foreach (var f in Held)
                f.Release();
        }
    }

    private static byte[] Packed(GlFixture gl, float[] chart, int w, int h, SendFormat format) => gl.Run(c =>
    {
        var d = c.Device;
        var bytes = new byte[w * h * 4];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)MathF.Round(chart[i] * 255);
        using var picture = d.CreateTexture(w, h, TextureFormat.Rgba8, TextureFilter.Nearest);
        d.Upload(picture, bytes, UploadFormat.Rgba8);
        using var pack = new PackPass(d);
        using var ring = new SendReadbackRing(d);
        var key = new object();
        var target = pack.Pack(key, picture, w, h, format, SendFit.Fit);
        var (frameBytes, stride) = PackPass.WireShape(format, w, h);
        Assert.True(ring.Request(target, format, w, h, frameBytes, stride, 0, 60, 1));
        d.Finish();
        var sink = new TestSink(hold: false);
        Assert.Equal(1, ring.Collect(sink));
        Assert.Empty(d.TakeErrors());
        return sink.Frames.Single();
    });

    [Theory]
    [InlineData(SendFormat.Uyvy, 1920, 1080)]
    [InlineData(SendFormat.Uyva, 1920, 1080)]
    [InlineData(SendFormat.Bgra8, 1920, 1080)]
    [InlineData(SendFormat.Uyvy, 1280, 720)]
    [InlineData(SendFormat.Uyva, 1280, 720)]
    [InlineData(SendFormat.Bgra8, 1280, 720)]
    public void AChartPacksAndReadsBackEqualToTheCpuTwin(SendFormat format, int w, int h)
    {
        var chart = Chart(w, h);
        var layout = format switch { SendFormat.Uyvy => PixelLayout.Uyvy, SendFormat.Uyva => PixelLayout.Uyva, _ => PixelLayout.Bgra8 };
        var expected = PackReference.Pack(layout, chart, w, h);
        var got = Packed(gl, chart, w, h, format);
        Assert.Equal(expected.Length, got.Length);
        var differ = 0;
        var first = -1;
        for (var i = 0; i < got.Length; i++)
        {
            if (got[i] != expected[i])
            {
                differ++;
                if (first < 0)
                    first = i;
            }
        }
        Assert.True(differ == 0, $"{format} {w}×{h}: {differ} bytes differ, first at {first}: {(first >= 0 ? got[first] : 0)} vs {(first >= 0 ? expected[first] : 0)}");
    }

    [Theory]
    [InlineData(SendFormat.P216, 1920, 1080)]
    [InlineData(SendFormat.Pa16, 1920, 1080)]
    [InlineData(SendFormat.P216, 1280, 720)]
    [InlineData(SendFormat.Pa16, 1280, 720)]
    public void ASixteenBitChartPacksWithinOneCodeOfTheCpuTwinAndUnpacksBack(SendFormat format, int w, int h)
    {
        var chart = Chart(w, h);
        var layout = format == SendFormat.P216 ? PixelLayout.P216 : PixelLayout.Pa16;
        var expected = PackReference.Pack(layout, chart, w, h);
        var got = Packed(gl, chart, w, h, format);
        Assert.Equal(LivePlanes.FrameBytes(layout, w, h), got.Length);
        Assert.Equal(expected.Length, got.Length);
        // Little-endian 16-bit samples: within one code of 65535 (the GPU's float order may round a half the other way).
        var worst = 0;
        var at = -1;
        for (var i = 0; i < got.Length; i += 2)
        {
            var d = Math.Abs((got[i] | (got[i + 1] << 8)) - (expected[i] | (expected[i + 1] << 8)));
            if (d > worst)
                (worst, at) = (d, i);
        }
        Assert.True(worst <= 1, $"{format} {w}×{h}: a sample is {worst} codes off at byte {at}");
        // What a receiver unpacks is the chart again inside each 8×8 block (its edges are where 4:2:2 chroma blends neighbours).
        var back = PackReference.Unpack(layout, got, w, h);
        for (var i = 0; i < back.Length; i++)
        {
            if ((i / 4 % w % 8) is < 2 or > 5 || (i % 4 == 3 && layout == PixelLayout.P216))
                continue;
            Assert.True(Math.Abs(back[i] - chart[i]) <= 0.5f / 255f, $"{format}: value {i} unpacks to {back[i]}, not {chart[i]}");
        }
    }

    [Fact]
    public void AHeldSlotStaysMappedAndUnchangedUntilReleasedAndAFullRingRefusesInsteadOfWaiting()
    {
        const int w = 64, h = 16;
        gl.Run(c =>
        {
            var d = c.Device;
            using var pack = new PackPass(d);
            using var ring = new SendReadbackRing(d, slots: 4);
            var sink = new TestSink(hold: true);
            using var picture = d.CreateTexture(w, h, TextureFormat.Rgba8, TextureFilter.Nearest);
            var key = new object();
            var (bytes, stride) = PackPass.WireShape(SendFormat.Uyvy, w, h);
            void Send(byte grey)
            {
                d.Upload(picture, Enumerable.Repeat(grey, w * h * 4).ToArray(), UploadFormat.Rgba8);
                var target = pack.Pack(key, picture, w, h, SendFormat.Uyvy, SendFit.Fit);
                Assert.True(ring.Request(target, SendFormat.Uyvy, w, h, bytes, stride, 0, 60, 1));
                d.Finish();
                ring.Collect(sink);
            }
            Send(10);
            var first = sink.Held[0];
            var before = new byte[first.Bytes];
            Marshal.Copy(first.Data, before, 0, before.Length);
            // Three more frames with other pictures while the sender holds the first: its bytes do not move.
            Send(200);
            Send(90);
            Send(160);
            var after = new byte[first.Bytes];
            Marshal.Copy(first.Data, after, 0, after.Length);
            Assert.Equal(before, after);
            Assert.Equal(4, ring.Handed);
            // Every slot held: the next request is refused, not waited for.
            var target = pack.Pack(key, picture, w, h, SendFormat.Uyvy, SendFit.Fit);
            Assert.False(ring.HasFree);
            Assert.False(ring.Request(target, SendFormat.Uyvy, w, h, bytes, stride, 0, 60, 1));
            // Two released: the next collect frees exactly those.
            sink.Held[0].Release();
            sink.Held[1].Release();
            ring.Collect(sink);
            Assert.Equal(2, ring.Handed);
            Assert.True(ring.HasFree);
            Assert.False(ring.TryDispose());
            sink.Dispose();
            Assert.True(ring.TryDispose());
            Assert.Empty(d.TakeErrors());
        });
    }

    /// <summary>The air's levels a test sets: one gain, one safe look and one hold for every output.</summary>
    private sealed class FixedLevels(float gain, SourceRef? safe = null, bool hold = false) : IAirLevels
    {
        public float Gain(string outputId, long leaderNs) => gain;

        public SourceRef? Override(string outputId, long leaderNs) => safe;

        public bool Hold(string outputId, long leaderNs) => hold;
    }

    [Fact]
    public void ASendThatFollowsTheAirTakesItsBlackoutGainSafeLookAndHoldAndADisabledOutputSendsBlack()
    {
        const int w = 64, h = 36;
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            engine.WarmUp();
            static Canvas Card(string colour) =>
                new("cv_1", "Card", w, h, "#000000", [new Layer("ly_1", "fill", SourceRef.Solid(colour), new RectD(0, 0, w, h), 1, LayerBlend.Normal, true)]);
            var shown = Scene.SourceOutput("op_1", SourceRef.Solid("#202020"), w, h);
            var off = Scene.SourceOutput("op_2", SourceRef.Solid("#FFFFFF"), w, h) with { Enabled = false };
            var project = Scene.Project([], [shown, off], [Card("#808080")]);
            var sink = new TestSink(hold: false);
            var offSink = new TestSink(hold: false);
            SendPlanEntry Entry(string id, SendTapKind kind, string target, ISendSink to) => new()
            {
                Id = id, Name = id, Kind = kind, Target = target, Width = w, Height = h, Format = SendFormat.Bgra8, FpsN = 60, FpsD = 1,
                Fit = SendFit.Fit, Sink = to, FollowAir = true,
            };
            var canvasSend = Entry("sd_1", SendTapKind.Canvas, "cv_1", sink);
            var offSend = Entry("sd_2", SendTapKind.Output, "op_2", offSink);
            using var stage = new SendStage(engine) { Plan = [canvasSend, offSend] };
            var passes = ImmutableArray.Create<IFramePass>(stage);
            var i = 0L;
            // One frame of the 60 Hz grid with the air as given; answers the canvas send's centre pixel (B, G, R, A).
            byte[] Frame(Project p, IAirLevels? levels = null, bool blackout = false)
            {
                i++;
                engine.Render(Scene.Inputs(p, (i + 0.5) / 60.0) with { Passes = passes, Levels = levels, Blackout = blackout });
                c.Device.Finish();
                i++;
                // The read-back of the frame packed above is collected and offered on the next frame.
                engine.Render(Scene.Inputs(p, (i + 0.5) / 60.0) with { Passes = passes, Levels = levels, Blackout = blackout });
                c.Device.Finish();
                var f = sink.Frames[^1];
                var o = ((h / 2) * w + w / 2) * 4;
                return f[o..(o + 4)];
            }
            Assert.Equal([0x80, 0x80, 0x80, 0xFF], Frame(project));
            // A disabled output's tap sends black, counted blank, though its last picture is still there.
            var o2 = ((h / 2) * w + w / 2) * 4;
            Assert.Equal([0, 0, 0, 0xFF], offSink.Frames[^1][o2..(o2 + 4)]);
            Assert.True(offSend.Blank > 0);
            // Half the gain on every output: the send's signal is halved, as an output's final stage halves its own.
            var half = Frame(project, new FixedLevels(0.5f));
            Assert.InRange(half[0], 0x3F, 0x41);
            Assert.Equal(0xFF, half[3]);
            // Every output dark (a panic fade, the master at 0): the send is dark.
            Assert.Equal([0, 0, 0, 0xFF], Frame(project, new FixedLevels(0f)));
            // The safe look on every output: the send shows it.
            Assert.Equal([0x00, 0x00, 0xFF, 0xFF], Frame(project, new FixedLevels(1f, SourceRef.Solid("#FF0000"))));
            // Blackout: black.
            Assert.Equal([0, 0, 0, 0xFF], Frame(project, blackout: true));
            // A hold on every output holds the send: the card changes, the send does not.
            Assert.Equal([0x80, 0x80, 0x80, 0xFF], Frame(project));
            var changed = project with { Canvases = [Card("#40C040")] };
            Assert.Equal([0x80, 0x80, 0x80, 0xFF], Frame(changed, new FixedLevels(1f, hold: true)));
            Assert.Equal([0x40, 0xC0, 0x40, 0xFF], Frame(changed));
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    [Fact]
    public void TheSendStageAllocatesNothingPerFrameOnceWarmAndSkipsWorkNobodyWatches()
    {
        const int w = 128, h = 72;
        gl.Run(c =>
        {
            using var engine = new RenderEngine(c, new RenderEngineOptions(GpuTiming: false));
            engine.WarmUp();
            var output = Scene.SourceOutput("op_1", SourceRef.Solid("#336699"), w, h);
            var project = Scene.Project([], [output]);
            var sink = new TestSink(hold: false);
            var entry = new SendPlanEntry
            {
                Id = "sd_1", Name = "Main", Kind = SendTapKind.Output, Target = "op_1", Width = w, Height = h, Format = SendFormat.Uyvy,
                FpsN = 60, FpsD = 1, Fit = SendFit.Fit, Sink = sink,
            };
            using var stage = new SendStage(engine) { Plan = [entry] };
            var passes = ImmutableArray.Create<IFramePass>(stage);
            // Mid-frame instants: frame i of the 60 Hz grid is due once.
            void Frame(long i) => engine.Render(Scene.Inputs(project, (i + 0.5) / 60.0) with { Passes = passes });
            for (var i = 1; i <= 30; i++)
            {
                Frame(i);
                c.Device.Finish();
            }
            Assert.True(entry.Packed >= 29, $"{entry.Packed} packed");
            Assert.NotEmpty(sink.Frames);
            sink.Frames.Clear();
            // The test's sink copies into new arrays; with a sender that releases at once, only the stage itself could allocate.
            entry.Sink = new ReleasingSink();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 31; i <= 90; i++)
                Frame(i);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            // Nobody watching: counted, no pack.
            var packed = entry.Packed;
            ((ReleasingSink)entry.Sink!).Connections = 0;
            for (var i = 91; i <= 100; i++)
                Frame(i);
            Assert.Equal(packed, entry.Packed);
            Assert.Equal(10, entry.SkippedUnwatched);
            Assert.Empty(c.Device.TakeErrors());
        });
    }

    /// <summary>A sender that releases every frame at once and keeps nothing (no allocation of its own).</summary>
    private sealed class ReleasingSink : ISendSink
    {
        public string FullName => "TEST (release)";
        public int Connections { get; set; } = 1;
        public (bool Program, bool Preview) Tally => default;
        public SendStats Stats => new(0, 0, 0, 0, 0);
        public string Words => "";
        public void Offer(SendFrame frame) => frame.Release();

        public void Update(SendSpec spec)
        {
            // Nothing to change.
        }

        public void Dispose()
        {
            // Nothing held.
        }
    }
}
