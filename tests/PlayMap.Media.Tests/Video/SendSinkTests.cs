using System.Runtime.InteropServices;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Video;
using PlayMap.Media.Video;

namespace PlayMap.Media.Tests.Video;

/// <summary>A sender never strands a frame across its dispose: one offered before it ever started is released at dispose, one
/// offered after dispose is released at once, and both are counted dropped. A sender that stops after sending sends one black frame
/// of its last shape first — exactly what the pack pass packs from black, one period on — unless its owner cleared that.</summary>
public sealed class SendSinkTests
{
    private sealed class QuietSink : SendSink
    {
        public QuietSink(bool start) : base(new SendSpec("sd_q", "Quiet", 64, 16, 60, 1, SendFormat.Uyvy, "default", "", "", false))
        {
            if (start)
                Start();
        }

        public override string FullName => "TEST (Quiet)";

        protected override bool Transmit(SendFrame frame) => false;

        protected override void Refresh()
        {
            // Nothing to ask a sender that sends nowhere.
        }

        protected override void Close()
        {
            // Nothing to close.
        }
    }

    private static ArrayFrame Frame() => new(new byte[64 * 16 * 2], 64, 16, SendFormat.Uyvy, 16_666_600, 128);

    /// <summary>A sender that keeps a copy of every frame it sends with its stamp, and says when it sent one.</summary>
    private sealed class CopyingSink : SendSink
    {
        public CopyingSink(SendFormat format) : base(new SendSpec("sd_c", "Copy", 64, 16, 60, 1, format, "default", "", "", false)) => Start();

        public override string FullName => "TEST (Copy)";

        public List<(byte[] Bytes, long LeaderNs, SendFormat Format, int Stride)> Sent { get; } = [];

        public SemaphoreSlim Each { get; } = new(0);

        protected override bool Transmit(SendFrame frame)
        {
            var copy = new byte[frame.Bytes];
            Marshal.Copy(frame.Data, copy, 0, copy.Length);
            lock (Sent)
                Sent.Add((copy, frame.LeaderNs, frame.Format, frame.Stride));
            Each.Release();
            return false;
        }

        protected override void Refresh()
        {
            // Nothing to ask.
        }

        protected override void Close()
        {
            // Nothing to close.
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                Each.Dispose();
        }
    }

    [Theory]
    [InlineData(SendFormat.Uyvy)]
    [InlineData(SendFormat.Uyva)]
    [InlineData(SendFormat.P216)]
    [InlineData(SendFormat.Pa16)]
    [InlineData(SendFormat.Bgra8)]
    public async Task ASenderThatStopsAfterSendingSendsOneBlackFrameOfItsLastShapeFirst(SendFormat format)
    {
        const int w = 64, h = 16;
        var layout = format switch
        {
            SendFormat.Uyvy => PixelLayout.Uyvy,
            SendFormat.Uyva => PixelLayout.Uyva,
            SendFormat.P216 => PixelLayout.P216,
            SendFormat.Pa16 => PixelLayout.Pa16,
            _ => PixelLayout.Bgra8,
        };
        // What the pack pass packs from its black picture (opaque black), through the CPU twin.
        var black = new float[w * h * 4];
        for (var i = 3; i < black.Length; i += 4)
            black[i] = 1f;
        var expected = PackReference.Pack(layout, black, w, h);
        var grey = PackReference.Pack(layout, Enumerable.Repeat(0.5f, w * h * 4).ToArray(), w, h);
        var stride = format == SendFormat.Bgra8 ? 4 * w : 2 * w;
        var sink = new CopyingSink(format);
        var frame = new ArrayFrame(grey, w, h, format, 16_666_600, stride);
        sink.Offer(frame);
        Assert.True(await sink.Each.WaitAsync(TimeSpan.FromSeconds(10)), "the frame was sent");
        sink.Dispose();
        Assert.Equal(2, sink.Sent.Count);
        var (bytes, leaderNs, sentFormat, sentStride) = sink.Sent[1];
        Assert.Equal(expected, bytes);
        Assert.Equal(format, sentFormat);
        Assert.Equal(stride, sentStride);
        // One period of 60 fps after the last frame, on the leader's grid.
        Assert.Equal(16_666_600 + 16_666_666, leaderNs);
        Assert.Equal(2, sink.Stats.Sent);
    }

    [Fact]
    public async Task ASenderWhoseOwnerClearedTheBlackFrameOrThatNeverSentStopsWithoutOne()
    {
        var cleared = new CopyingSink(SendFormat.Uyvy) { BlackOnStop = false };
        cleared.Offer(Frame());
        Assert.True(await cleared.Each.WaitAsync(TimeSpan.FromSeconds(10)), "the frame was sent");
        cleared.Dispose();
        Assert.Single(cleared.Sent);
        var quiet = new CopyingSink(SendFormat.Uyvy);
        quiet.Dispose();
        Assert.Empty(quiet.Sent);
    }

    [Fact]
    public void AFrameOfferedToASenderThatNeverStartedIsReleasedAtItsDispose()
    {
        var sink = new QuietSink(start: false);
        var frame = Frame();
        sink.Offer(frame);
        Assert.False(frame.IsReleased);
        sink.Dispose();
        Assert.True(frame.IsReleased);
        Assert.Equal(1, sink.Stats.Dropped);
    }

    [Fact]
    public void AFrameOfferedAfterDisposeIsReleasedAtOnceAndCounted()
    {
        var sink = new QuietSink(start: true);
        sink.Dispose();
        var frame = Frame();
        sink.Offer(frame);
        Assert.True(frame.IsReleased);
        Assert.Equal(1, sink.Stats.Dropped);
    }
}
