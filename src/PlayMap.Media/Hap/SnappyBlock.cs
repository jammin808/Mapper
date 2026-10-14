using PlayMap.Core.Media.Hap;

namespace PlayMap.Media.Hap;

/// <summary>A Snappy block decoder that allocates nothing, written from the published format description (Google's
/// "Snappy compressed format description"): a varint preamble with the uncompressed length, then elements — literals (tag
/// 00, the length in the tag or in one to four bytes after it) and back-references (tag 01: length 4–11 and an 11-bit offset;
/// tag 10: length 1–64 and a 16-bit offset; tag 11: length 1–64 and a 32-bit offset). A reference reaching before the start
/// of the output, a zero offset, an element running past either buffer, or a stream that ends short of its preamble's
/// length refuses the block.</summary>
internal static class SnappyBlock
{
    /// <summary>Decodes a whole block into <paramref name="output"/>; true when it decoded to exactly its preamble's length
    /// (<paramref name="written"/>).</summary>
    public static bool TryDecompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
    {
        written = 0;
        if (!SnappyLength.TryRead(input, out var length, out var ip) || length > (uint)output.Length)
            return false;
        var end = (int)length;
        var op = 0;
        while (ip < input.Length)
        {
            int tag = input[ip++];
            int len;
            switch (tag & 3)
            {
                case 0:
                    len = tag >> 2;
                    if (len >= 60)
                    {
                        var extra = len - 59;
                        if (extra > input.Length - ip)
                            return false;
                        long l = 0;
                        for (var i = 0; i < extra; i++)
                            l |= (long)input[ip + i] << (8 * i);
                        ip += extra;
                        if (l >= int.MaxValue)
                            return false;
                        len = (int)l;
                    }
                    len++;
                    if (len > input.Length - ip || len > end - op)
                        return false;
                    input.Slice(ip, len).CopyTo(output.Slice(op, len));
                    ip += len;
                    op += len;
                    break;
                case 1:
                    if (ip >= input.Length)
                        return false;
                    len = 4 + ((tag >> 2) & 7);
                    if (!Copy(output, ref op, ((tag >> 5) << 8) | input[ip++], len, end))
                        return false;
                    break;
                case 2:
                    if (input.Length - ip < 2)
                        return false;
                    len = 1 + (tag >> 2);
                    var off2 = input[ip] | (input[ip + 1] << 8);
                    ip += 2;
                    if (!Copy(output, ref op, off2, len, end))
                        return false;
                    break;
                default:
                    if (input.Length - ip < 4)
                        return false;
                    len = 1 + (tag >> 2);
                    var off4 = (uint)(input[ip] | (input[ip + 1] << 8) | (input[ip + 2] << 16) | (input[ip + 3] << 24));
                    ip += 4;
                    if (off4 > int.MaxValue || !Copy(output, ref op, (int)off4, len, end))
                        return false;
                    break;
            }
        }
        written = op;
        return op == end;
    }

    // A back-reference: len bytes from off bytes behind the write position. Overlapping references repeat a pattern of period
    // off; it is copied in runs that double, each run's source wholly behind its destination, so the period's phase holds.
    private static bool Copy(Span<byte> output, ref int op, int off, int len, int end)
    {
        if (off == 0 || off > op || len > end - op)
            return false;
        var src = op - off;
        var p = op;
        var left = len;
        var run = off;
        while (left > 0)
        {
            var n = Math.Min(run, left);
            output.Slice(src, n).CopyTo(output.Slice(p, n));
            p += n;
            left -= n;
            run = p - src;
        }
        op = p;
        return true;
    }
}
