using System.Buffers.Binary;

namespace PlayMap.Core.Media.Optimise;

/// <summary>A Snappy block compressor written from the published format description (Google's "Snappy compressed format
/// description"): a varint preamble with the uncompressed length, then literals (tag 00) and back-references — tag 01 for
/// lengths 4–11 at offsets below 2048, tag 10 for lengths 1–64 at offsets below 65 536. Matches are found greedily through a
/// hash of four bytes; a match is at least four bytes and never reaches back more than 65 535 bytes. The output decodes with
/// any Snappy decoder. One instance per thread: its hash table is reused, so a compression allocates nothing.</summary>
public sealed class SnappyCompressor
{
    private const int HashBits = 14;
    private const int MaxOffset = 65_535;
    private readonly int[] _table = new int[1 << HashBits];

    /// <summary>The most bytes a block of <paramref name="length"/> bytes compresses to (the format's own bound).</summary>
    public static int MaxCompressedLength(int length) => 32 + length + length / 6;

    /// <summary>Compresses <paramref name="input"/> into <paramref name="output"/> (at least <see cref="MaxCompressedLength"/>
    /// bytes) and answers the compressed length.</summary>
    public int Compress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < MaxCompressedLength(input.Length))
            throw new ArgumentException("The output holds fewer bytes than the bound.", nameof(output));
        var op = WriteVarint(output, (uint)input.Length);
        if (input.Length < 8)
            return input.IsEmpty ? op : op + Literal(output[op..], input);
        Array.Fill(_table, -1);
        var literalStart = 0;
        var ip = 0;
        var limit = input.Length - 4;
        while (ip <= limit)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(input[ip..]);
            var h = (int)((word * 0x1E35A7BDu) >> (32 - HashBits));
            var candidate = _table[h];
            _table[h] = ip;
            if (candidate < 0 || ip - candidate > MaxOffset || BinaryPrimitives.ReadUInt32LittleEndian(input[candidate..]) != word)
            {
                ip++;
                continue;
            }
            var length = 4;
            while (ip + length < input.Length && input[candidate + length] == input[ip + length])
                length++;
            if (ip > literalStart)
                op += Literal(output[op..], input[literalStart..ip]);
            op += Copies(output[op..], ip - candidate, length);
            ip += length;
            literalStart = ip;
        }
        if (literalStart < input.Length)
            op += Literal(output[op..], input[literalStart..]);
        return op;
    }

    private static int WriteVarint(Span<byte> output, uint value)
    {
        var i = 0;
        while (value >= 0x80)
        {
            output[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        output[i++] = (byte)value;
        return i;
    }

    private static int Literal(Span<byte> output, ReadOnlySpan<byte> bytes)
    {
        var n = bytes.Length - 1;
        int head;
        if (n < 60)
        {
            output[0] = (byte)(n << 2);
            head = 1;
        }
        else
        {
            var extra = n < 1 << 8 ? 1 : n < 1 << 16 ? 2 : n < 1 << 24 ? 3 : 4;
            output[0] = (byte)((59 + extra) << 2);
            for (var k = 0; k < extra; k++)
                output[1 + k] = (byte)(n >> (8 * k));
            head = 1 + extra;
        }
        bytes.CopyTo(output[head..]);
        return head + bytes.Length;
    }

    // A match of any length as back-references of at most 64 bytes each (never leaving one shorter than 4 when tag 01 is used).
    private static int Copies(Span<byte> output, int offset, int length)
    {
        var op = 0;
        while (length >= 68)
        {
            op += Copy2(output[op..], offset, 64);
            length -= 64;
        }
        if (length > 64)
        {
            op += Copy2(output[op..], offset, 60);
            length -= 60;
        }
        if (length is >= 4 and <= 11 && offset < 2048)
        {
            output[op] = (byte)(((offset >> 8) << 5) | ((length - 4) << 2) | 1);
            output[op + 1] = (byte)offset;
            return op + 2;
        }
        return op + Copy2(output[op..], offset, length);
    }

    private static int Copy2(Span<byte> output, int offset, int length)
    {
        output[0] = (byte)(((length - 1) << 2) | 2);
        BinaryPrimitives.WriteUInt16LittleEndian(output[1..], (ushort)offset);
        return 3;
    }
}
