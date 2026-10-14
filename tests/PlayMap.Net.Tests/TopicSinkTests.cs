using System.Text;

namespace PlayMap.Net.Tests;

/// <summary>A push topic's lines on a wire connection: in order and bounded below the connection's own limit, so a reader too slow for
/// a topic loses the topic (with one overflow line) and never the connection; latest-wins lines keep only the newest per key; what a
/// topic says while it opens waits for the session's reply.</summary>
public sealed class TopicSinkTests
{
    /// <summary>A connection whose writes wait until the test lets them through, keeping what was written.</summary>
    private sealed class GatedStream : Stream
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream _written = new();
        private readonly Lock _gate = new();

        public void Open() => _open.TrySetResult();

        /// <summary>Completes once the writer is inside its first write (and holds no queued line).</summary>
        public Task Writing => _writing.Task;

        public string Text
        {
            get
            {
                lock (_gate)
                    return Encoding.UTF8.GetString(_written.ToArray());
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _writing.TrySetResult();
            await _open.Task.WaitAsync(cancellationToken);
            lock (_gate)
                _written.Write(buffer.Span);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
            // Nothing is buffered here: a write is kept as it lands.
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _written.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task ASlowReaderLosesTheTopicWithOneOverflowLineAndKeepsTheConnection()
    {
        await using var stream = new GatedStream();
        var cut = 0;
        await using var writer = new LineWriter(stream, maxQueued: 256, TimeSpan.FromSeconds(30), () => Interlocked.Increment(ref cut));
        var sink = new WireTopicSink(writer, "FLOOD");

        // The first line goes to the (held) write; then the queue fills to the topic's bound, half the connection's.
        Assert.True(sink.InOrder("FLOOD first"));
        await stream.Writing.WaitAsync(TimeSpan.FromSeconds(10));
        var taken = 0;
        while (taken < 1000 && sink.InOrder("FLOOD " + taken))
            taken++;
        Assert.Equal(128, taken);
        Assert.False(sink.InOrder("FLOOD later"));
        writer.Say("OK @p PONG");

        stream.Open();
        await writer.FlushAsync(TimeSpan.FromSeconds(10));

        var lines = stream.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(0, Volatile.Read(ref cut));
        Assert.False(writer.Closed);
        Assert.Equal("FLOOD first", lines[0]);
        Assert.Equal("FLOOD overflow — this session read too slowly; SUBSCRIBE FLOOD again", lines[^2]);
        Assert.Equal("OK @p PONG", lines[^1]);
        Assert.DoesNotContain("FLOOD later", lines);
    }

    [Fact]
    public async Task LatestWinsKeepsOnlyTheNewestLinePerKeyAndHeldLinesFollowTheReply()
    {
        await using var stream = new GatedStream();
        await using var writer = new LineWriter(stream, maxQueued: 256, TimeSpan.FromSeconds(30), () => { });
        var held = new HeldSink(new WireTopicSink(writer, "INDEX"));

        held.InOrder("INDEX opened");
        held.Latest("rows", "INDEX rows 1");
        held.Latest("rows", "INDEX rows 2");
        writer.Say("OK @s subscribed index");
        held.Release();
        held.Latest("rows", "INDEX rows 3");

        stream.Open();
        await writer.FlushAsync(TimeSpan.FromSeconds(10));
        var lines = stream.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("OK @s subscribed index", lines[0]);
        Assert.Equal("INDEX opened", lines[1]);
        Assert.DoesNotContain("INDEX rows 1", lines);
        Assert.Equal("INDEX rows 3", lines[^1]);
    }

    [Fact]
    public async Task AScreensQueueTakesItsTopicsInOrderAndDropsALateLineOnceTheScreenIsGone()
    {
        var queue = new PushQueue();
        var index = queue.SinkFor("INDEX");
        var events = queue.SinkFor("EVENTS");
        try
        {
            Assert.True(events.InOrder("EVENT 1"));
            index.Latest("rows", "INDEX rows 1");
            index.Latest("rows", "INDEX rows 2");
            Assert.True(events.InOrder("EVENT 2"));
            await queue.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            var frames = queue.Drain().Select(System.Text.Encoding.UTF8.GetString).ToList();

            Assert.Equal(
                ["""{"type":"push","topic":"EVENTS","line":"EVENT 1"}""", """{"type":"push","topic":"EVENTS","line":"EVENT 2"}""",
                 """{"type":"push","topic":"INDEX","line":"INDEX rows 2"}"""],
                frames);
        }
        finally
        {
            queue.Dispose();
        }

        // A package pushing after the screen closed (its own thread, or the lane's) is told no, and nothing is thrown at it.
        Assert.False(events.InOrder("EVENT 3"));
        index.Latest("rows", "INDEX rows 3");
        Assert.Empty(queue.Drain());
    }
}
