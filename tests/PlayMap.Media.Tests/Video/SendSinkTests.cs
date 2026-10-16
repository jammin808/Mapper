using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Media.Video;

namespace PlayMap.Media.Tests.Video;

/// <summary>A sender never strands a frame across its dispose: one offered before it ever started is released at dispose, one
/// offered after dispose is released at once, and both are counted dropped.</summary>
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
