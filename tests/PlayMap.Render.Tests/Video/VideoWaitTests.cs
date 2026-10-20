using PlayMap.Core.Gpu;
using PlayMap.Core.Media;
using PlayMap.Render.Tests.Rhi;
using PlayMap.Render.Video;

namespace PlayMap.Render.Tests.Video;

/// <summary>A picture's planes copied by the device's upload ring are waited for by the frame's list before the pass that samples them:
/// streamed YUV planes before their conversion, block planes before the HAP pass, a network frame's planes before its unpack. On GL
/// the waits are nothing (one stream in order); on a device whose copies ride their own queue they are what orders the draw after the
/// copy. The pictures are those the list would draw anyway.</summary>
[Collection("gpu")]
public sealed class VideoWaitTests(BackendFixture backends)
{
    [Fact]
    public void StreamedYuvPlanesAreWaitedForBeforeTheirConversion()
    {
        const int w = 64, h = 32;
        using var lease = new ArrayLease(PixelLayout.Nv12, w, h, 1, ColorMatrix.Bt709, ColorRange.Limited,
            (new byte[w * h], w, w, h), (new byte[w * h / 2], w, w / 2, h / 2));
        backends.Run(leg =>
        {
            var d = new RecordingDevice(leg.Device);
            using var set = new VideoTextureSet(d, streaming: true);
            Assert.True(set.Update(lease));
            Assert.Equal(2, d.Log.Count(e => e.Kind == "upload"));
            Assert.Contains(d.Log, e => e.Kind == "draw");
            Assert.True(d.EverySampledValueWaitedFor(out var words), words);
            Assert.Empty(leg.TakeErrors());
        });
    }

    [Fact]
    public void BlockPlanesAreWaitedForBeforeTheHapPass()
    {
        const int w = 16, h = 16;
        // BC1: 4×4 blocks of 8 bytes, tight rows.
        using var lease = new ArrayLease(PixelLayout.Bc1, w, h, 1, ColorMatrix.Bt709, ColorRange.Full, (new byte[w / 4 * 8 * (h / 4)], w / 4 * 8, w / 4, h / 4));
        backends.Run(leg =>
        {
            var d = new RecordingDevice(leg.Device);
            using var set = new VideoTextureSet(d, streaming: true);
            if (d.Features.RefusalOf(TextureFormat.Bc1) is { } refused)
            {
                // A device that samples no BC1 refuses the picture with its words, before any copy.
                Assert.Throws<NotSupportedException>(() => set.Update(lease));
                Assert.Equal(refused, set.Refusal);
                return;
            }
            Assert.True(set.Update(lease));
            Assert.Equal(1, d.Log.Count(e => e.Kind == "upload"));
            Assert.True(d.EverySampledValueWaitedFor(out var words), words);
            Assert.Empty(leg.TakeErrors());
        });
    }

    [Fact]
    public void ANetworkFramesPlanesAreWaitedForBeforeItsUnpack()
    {
        const int w = 64, h = 4;
        using var lease = new ArrayLease(PixelLayout.Uyva, w, h, 1, ColorMatrix.Bt709, ColorRange.Limited,
            (new byte[2 * w * h], 2 * w, w, h), (new byte[w * h], w, w, h));
        backends.Run(leg =>
        {
            var d = new RecordingDevice(leg.Device);
            using var set = new VideoTextureSet(d, streaming: true);
            Assert.True(set.Update(lease));
            Assert.Equal(2, d.Log.Count(e => e.Kind == "upload"));
            Assert.True(d.EverySampledValueWaitedFor(out var words), words);
            Assert.Empty(leg.TakeErrors());
        });
    }
}
