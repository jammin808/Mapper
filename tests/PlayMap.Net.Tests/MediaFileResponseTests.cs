using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using PlayMap.Net.Http;

namespace PlayMap.Net.Tests;

/// <summary>A held file answered by its hash: written to the response in blocks of 1 MiB (what a pull's cost in thread-pool turns
/// follows on the holder), the one range asked for or the whole file, and the headers a pull and a HEAD read.</summary>
public sealed class MediaFileResponseTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("playmap-media-answer-");

    public void Dispose() => _dir.Delete(recursive: true);

    /// <summary>A response body that keeps what was written to it, write by write.</summary>
    private sealed class Writes : Stream
    {
        private readonly MemoryStream _kept = new();

        public List<int> Sizes { get; } = [];

        public byte[] ToArray() => _kept.ToArray();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Sizes.Add(buffer.Length);
            _kept.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _kept.Length;

        public override long Position { get => _kept.Position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
            // Kept in memory: nothing to flush.
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _kept.Dispose();
            base.Dispose(disposing);
        }
    }

    private async Task<(DefaultHttpContext Context, Writes Body, byte[] Bytes)> AnswerAsync(int size, string method, string? range)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        var path = Path.Combine(_dir.FullName, "clip.bin");
        await File.WriteAllBytesAsync(path, bytes);
        var body = new Writes();
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (range is not null)
            context.Request.Headers.Range = range;
        context.Response.Body = body;
        await MediaFileResponse.WriteAsync(context, path, "ab12");
        return (context, body, bytes);
    }

    [Fact]
    public async Task TheWholeFileIsWrittenInBlocksOfAMegabyte()
    {
        const int MiB = MediaFileResponse.BlockBytes;
        var (context, body, bytes) = await AnswerAsync(3 * MiB + 17, "GET", null);
        using (body)
        {
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal(3L * MiB + 17, context.Response.ContentLength);
            Assert.Equal([MiB, MiB, MiB, 17], body.Sizes);
            Assert.Equal(bytes, body.ToArray());
            Assert.Equal("\"ab12\"", context.Response.Headers.ETag.ToString());
            Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        }
    }

    [Fact]
    public async Task ARangeIsWrittenInBlocksOfAMegabyteFromItsFirstByte()
    {
        const int MiB = MediaFileResponse.BlockBytes;
        var (context, body, bytes) = await AnswerAsync(3 * MiB, "GET", "bytes=1000-2099151");
        using (body)
        {
            Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
            Assert.Equal("bytes 1000-2099151/3145728", context.Response.Headers.ContentRange.ToString());
            Assert.Equal([MiB, MiB, 1000], body.Sizes);
            Assert.Equal(bytes[1000..2_099_152], body.ToArray());
        }
    }

    [Fact]
    public async Task AHeadAnswersTheLengthAndWritesNothing()
    {
        var (context, body, _) = await AnswerAsync(5000, "HEAD", null);
        using (body)
        {
            Assert.Equal((StatusCodes.Status200OK, (long?)5000), (context.Response.StatusCode, context.Response.ContentLength));
            Assert.Empty(body.Sizes);
        }
    }
}
