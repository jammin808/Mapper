using System.Buffers;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;

namespace PlayMap.Net.Http;

/// <summary>A file a node holds by its SHA-256, answered with one byte range (RFC 9110 §14) and written from the disk to the socket in
/// blocks of <see cref="BlockBytes"/>: a pull's cost in thread-pool turns follows its blocks, so a megabyte costs a few turns on the
/// holder, never one per 64 KiB. The strong ETag is the hash. One satisfiable <c>bytes</c> range answers 206 with its
/// <c>Content-Range</c>; one that starts past the end answers 416 with the file's length; anything else (no range, several, another
/// unit, an <c>If-Range</c> that is not the ETag) answers the whole file with 200, as a server may always ignore a range. HEAD answers
/// the headers alone. A file shorter on the disk than when it was opened ends the connection, so a reader never takes a short body
/// for the file.</summary>
public static class MediaFileResponse
{
    /// <summary>The block read from the disk and written to the response at a time.</summary>
    public const int BlockBytes = 1024 * 1024;

    public static async Task WriteAsync(HttpContext context, string path, string hash)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(hash);
        var response = context.Response;
        // Shared like the other readers of a durable file: a landing that replaces it (a rename over it) is never refused by a reader.
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var size = RandomAccess.GetLength(file);
        var etag = "\"" + hash + "\"";
        response.Headers.AcceptRanges = "bytes";
        response.Headers.ETag = etag;
        response.Headers.LastModified = File.GetLastWriteTimeUtc(path).ToString("R", CultureInfo.InvariantCulture);
        response.ContentType = "application/octet-stream";
        var (from, length, status) = Choose(context.Request.GetTypedHeaders(), size, etag);
        response.StatusCode = status;
        if (status == StatusCodes.Status416RangeNotSatisfiable)
        {
            response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes */{size}");
            response.ContentLength = 0;
            return;
        }
        if (status == StatusCodes.Status206PartialContent)
            response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {from}-{from + length - 1}/{size}");
        response.ContentLength = length;
        if (HttpMethods.IsHead(context.Request.Method) || length == 0)
            return;
        var ct = context.RequestAborted;
        var block = ArrayPool<byte>.Shared.Rent((int)Math.Min(BlockBytes, length));
        try
        {
            for (long sent = 0; sent < length;)
            {
                var n = await RandomAccess.ReadAsync(file, block.AsMemory(0, (int)Math.Min(block.Length, length - sent)), from + sent, ct);
                if (n == 0)
                {
                    // The file is shorter than its length said when it was opened: the body cannot be whole, so it is not ended as if it were.
                    context.Abort();
                    return;
                }
                await response.Body.WriteAsync(block.AsMemory(0, n), ct);
                sent += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block);
        }
    }

    /// <summary>The bytes to answer and the status: the one range asked for, or the whole file, or none (416).</summary>
    internal static (long From, long Length, int Status) Choose(RequestHeaders headers, long size, string etag)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var whole = (0L, size, StatusCodes.Status200OK);
        if (headers.Range is not { } range || !string.Equals(range.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase) || range.Ranges.Count != 1)
            return whole;
        // A range is honoured only for the copy its If-Range names; a date or another tag gets the whole file.
        if (headers.IfRange is { } condition && (condition.EntityTag is not { IsWeak: false } tag || !string.Equals(tag.Tag.Value, etag, StringComparison.Ordinal)))
            return whole;
        var item = range.Ranges.First();
        long from, to;
        if (item.From is { } start)
        {
            if (start >= size)
                return (0, 0, StatusCodes.Status416RangeNotSatisfiable);
            from = start;
            to = item.To is { } end ? Math.Min(end, size - 1) : size - 1;
        }
        else if (item.To is { } suffix)
        {
            if (suffix == 0 || size == 0)
                return (0, 0, StatusCodes.Status416RangeNotSatisfiable);
            from = Math.Max(0, size - suffix);
            to = size - 1;
        }
        else
        {
            return whole;
        }
        return to < from ? whole : (from, to - from + 1, StatusCodes.Status206PartialContent);
    }
}
