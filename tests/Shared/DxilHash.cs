using System.Buffers.Binary;
using System.Numerics;

namespace PlayMap.Tests;

/// <summary>A DXIL container's header and its validator hash, written here from the published algorithm (hlsl-specs INF-0004,
/// "Validator Hashing", Appendix 1), independent of DXC: the retail hash is MD5's compression over the container from its version
/// (byte 20) to its end, with the message length in bits as the last block's first word and <c>1 | length &lt;&lt; 1</c> as its
/// last word, where MD5 writes the length in bits at the end. A container's digest is bytes 4 to 19: zeros when no validator ran,
/// sixteen 0x01 bytes for BYPASS and sixteen 0x02 bytes for PREVIEW_BYPASS. Shared by the Engine suite (the shader worker's DXIL)
/// and the Direct3D 12 suite (the HLSL library's).</summary>
internal static class DxilHash
{
    /// <summary>What a container's digest holds.</summary>
    internal enum Kind
    {
        Zero,
        Bypass,
        PreviewBypass,
        Hash,
    }

    private static readonly int[] Shifts = [7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21];

    private static readonly uint[] Sines =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];

    /// <summary>The container's digest (bytes 4 to 19).</summary>
    internal static byte[] Digest(byte[] container) => container.AsSpan(4, 16).ToArray();

    /// <summary>What the container's digest holds.</summary>
    internal static Kind KindOf(byte[] container)
    {
        var digest = container.AsSpan(4, 16);
        if (!digest.ContainsAnyExcept((byte)0))
            return Kind.Zero;
        if (!digest.ContainsAnyExcept((byte)1))
            return Kind.Bypass;
        return digest.ContainsAnyExcept((byte)2) ? Kind.Hash : Kind.PreviewBypass;
    }

    /// <summary>The retail hash of a container, over its bytes from 20 to the size its header gives (bytes 24 to 27).</summary>
    internal static byte[] RetailOf(byte[] container)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (container.Length < 32 || !container.AsSpan(0, 4).SequenceEqual("DXBC"u8))
            throw new ArgumentException("not a DXIL container (no DXBC header)", nameof(container));
        var size = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(24));
        if (size < 20 || size > container.Length)
            throw new ArgumentException($"the header's size {size} is not within the container's {container.Length} bytes", nameof(container));
        return Retail(container.AsSpan(20, size - 20));
    }

    /// <summary>The retail hash of a message (INF-0004 Appendix 1's ComputeHashRetail).</summary>
    internal static byte[] Retail(ReadOnlySpan<byte> data)
    {
        var length = (uint)data.Length;
        var left = data.Length & 63;
        var full = data.Length >> 6;
        Span<uint> state = [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476];
        Span<uint> words = stackalloc uint[16];
        Span<byte> block = stackalloc byte[64];
        for (var i = 0; i < full; i++)
        {
            Words(data.Slice(i * 64, 64), words);
            Compress(state, words);
        }
        var tail = data[(full * 64)..];
        block.Clear();
        if (left < 56)
        {
            // One last block: the length in bits, the tail, 0x80, zeros, and 1 | length << 1 in the last word.
            BinaryPrimitives.WriteUInt32LittleEndian(block, length << 3);
            tail.CopyTo(block[4..]);
            block[4 + left] = 0x80;
        }
        else
        {
            // The tail does not leave room for the length: the tail, 0x80 and zeros, then a block of the length in bits and zeros.
            tail.CopyTo(block);
            block[left] = 0x80;
            Words(block, words);
            Compress(state, words);
            block.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(block, length << 3);
        }
        Words(block, words);
        words[15] = 1 | (length << 1);
        Compress(state, words);
        var hash = new byte[16];
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(hash.AsSpan(i * 4), state[i]);
        return hash;
    }

    private static void Words(ReadOnlySpan<byte> block, Span<uint> words)
    {
        for (var i = 0; i < 16; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]);
    }

    /// <summary>MD5's compression of one 16-word block into the state.</summary>
    private static void Compress(Span<uint> state, ReadOnlySpan<uint> x)
    {
        uint a = state[0], b = state[1], c = state[2], d = state[3];
        for (var i = 0; i < 64; i++)
        {
            uint f;
            int g;
            switch (i >> 4)
            {
                case 0:
                    f = (b & c) | (~b & d);
                    g = i;
                    break;
                case 1:
                    f = (b & d) | (c & ~d);
                    g = (1 + 5 * i) & 15;
                    break;
                case 2:
                    f = b ^ c ^ d;
                    g = (5 + 3 * i) & 15;
                    break;
                default:
                    f = c ^ (b | ~d);
                    g = 7 * i & 15;
                    break;
            }
            var rotated = BitOperations.RotateLeft(a + f + x[g] + Sines[i], Shifts[((i >> 4) << 2) + (i & 3)]);
            a = d;
            d = c;
            c = b;
            b += rotated;
        }
        state[0] += a;
        state[1] += b;
        state[2] += c;
        state[3] += d;
    }
}
