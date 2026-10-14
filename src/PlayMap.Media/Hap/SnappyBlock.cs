using System.Runtime.CompilerServices;
using PlayMap.Core.Media.Hap;

namespace PlayMap.Media.Hap;

/// <summary>A Snappy block decoder that allocates nothing, written from the published format description (Google's
/// "Snappy compressed format description"): a varint preamble with the uncompressed length, then elements — literals (tag
/// 00, the length in the tag or in one to four bytes after it) and back-references (tag 01: length 4–11 and an 11-bit offset;
/// tag 10: length 1–64 and a 16-bit offset; tag 11: length 1–64 and a 32-bit offset). A reference reaching before the start
/// of the output, a zero offset, an element running past either buffer, or a stream that ends short of its preamble's
/// length refuses the block.
/// <para>Short literals and references move eight or sixteen bytes at a time where both buffers have the slack; the bytes
/// written past an element's end are inside the output and are overwritten by the elements that follow.</para></summary>
internal static unsafe class SnappyBlock
{
    /// <summary>Decodes a whole block into <paramref name="output"/>; true when it decoded to exactly its preamble's length
    /// (<paramref name="written"/>).</summary>
    public static bool TryDecompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
    {
        written = 0;
        if (!SnappyLength.TryRead(input, out var length, out var pre) || length > (uint)output.Length)
            return false;
        fixed (byte* src = input)
        fixed (byte* dst = output)
        {
            var ip = src + pre;
            var ipEnd = src + input.Length;
            var op = dst;
            var opEnd = dst + length;
            while (ip < ipEnd)
            {
                uint tag = *ip++;
                nint len;
                if ((tag & 3) == 0)
                {
                    len = (nint)(tag >> 2);
                    if (len >= 60)
                    {
                        var extra = (int)len - 59;
                        if (extra > ipEnd - ip)
                            return false;
                        ulong l = 0;
                        for (var i = 0; i < extra; i++)
                            l |= (ulong)ip[i] << (8 * i);
                        ip += extra;
                        if (l >= int.MaxValue)
                            return false;
                        len = (nint)l;
                    }
                    len++;
                    if (len > ipEnd - ip || len > opEnd - op)
                        return false;
                    if (len <= 16 && ipEnd - ip >= 16 && opEnd - op >= 16)
                    {
                        Unsafe.WriteUnaligned(op, Unsafe.ReadUnaligned<ulong>(ip));
                        Unsafe.WriteUnaligned(op + 8, Unsafe.ReadUnaligned<ulong>(ip + 8));
                    }
                    else
                    {
                        Buffer.MemoryCopy(ip, op, opEnd - op, len);
                    }
                    ip += len;
                    op += len;
                    continue;
                }
                nint off;
                switch (tag & 3)
                {
                    case 1:
                        if (ip >= ipEnd)
                            return false;
                        len = 4 + (nint)((tag >> 2) & 7);
                        off = (nint)(((tag >> 5) << 8) | *ip++);
                        break;
                    case 2:
                        if (ipEnd - ip < 2)
                            return false;
                        len = 1 + (nint)(tag >> 2);
                        off = ip[0] | (ip[1] << 8);
                        ip += 2;
                        break;
                    default:
                        if (ipEnd - ip < 4)
                            return false;
                        len = 1 + (nint)(tag >> 2);
                        var off4 = Unsafe.ReadUnaligned<uint>(ip);
                        ip += 4;
                        if (off4 > int.MaxValue)
                            return false;
                        off = (nint)off4;
                        break;
                }
                if (off == 0 || off > op - dst || len > opEnd - op)
                    return false;
                Copy(op, off, len, opEnd);
                op += len;
            }
            written = (int)(op - dst);
            return op == opEnd;
        }
    }

    // A back-reference: len bytes from off bytes behind op. With an offset of eight or more each eight-byte read lies wholly
    // in bytes already written; a shorter offset repeats a pattern of that period, copied in runs that double so each run's
    // source is wholly behind its destination and the period's phase holds.
    private static void Copy(byte* op, nint off, nint len, byte* opEnd)
    {
        var from = op - off;
        if (off >= 8 && opEnd - op >= len + 8)
        {
            for (nint i = 0; i < len; i += 8)
                Unsafe.WriteUnaligned(op + i, Unsafe.ReadUnaligned<ulong>(from + i));
            return;
        }
        var p = op;
        var left = len;
        var run = off;
        while (left > 0)
        {
            var n = Math.Min(run, left);
            Buffer.MemoryCopy(from, p, n, n);
            p += n;
            left -= n;
            run = (nint)(p - from);
        }
    }
}
