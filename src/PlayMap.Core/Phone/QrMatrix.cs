using System.Text;

namespace PlayMap.Core.Phone;

/// <summary>
/// A QR code (ISO/IEC 18004 Model 2) for a short text: byte mode (UTF-8), error correction level M, the smallest version from 1 to
/// 10 that holds it, the mask with the lowest penalty of the standard's four rules. <see cref="Modules"/> is row-major, true for
/// dark, <see cref="Size"/> × <see cref="Size"/> without the quiet zone (four light modules round it are the reader's due).
/// </summary>
public sealed record QrMatrix(int Size, bool[] Modules)
{
    // Per version 1–10 at level M: error-correction codewords per block, then (blocks, data codewords) for each block group.
    private static readonly (int Ec, int Blocks1, int Data1, int Blocks2, int Data2)[] LevelM =
    [
        (10, 1, 16, 0, 0), (16, 1, 28, 0, 0), (26, 1, 44, 0, 0), (18, 2, 32, 0, 0), (24, 2, 43, 0, 0),
        (16, 4, 27, 0, 0), (18, 4, 31, 0, 0), (22, 2, 38, 2, 39), (22, 3, 36, 2, 37), (26, 4, 43, 1, 44),
    ];

    private static readonly int[][] Alignment =
    [
        [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    public const int MaxVersion = 10;

    /// <summary>The version: (Size − 17) / 4.</summary>
    public int Version => (Size - 17) / 4;

    public bool this[int row, int col] => Modules[row * Size + col];

    /// <summary>The modules as a bitset, row-major, most significant bit first, in base64 (the desk and the phone draw from it).</summary>
    public string ToBase64Bitset()
    {
        var bytes = new byte[(Modules.Length + 7) / 8];
        for (var i = 0; i < Modules.Length; i++)
        {
            if (Modules[i])
                bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The code for <paramref name="text"/>; throws when it does not fit version 10 at level M (213 bytes).</summary>
    public static QrMatrix Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var data = Encoding.UTF8.GetBytes(text);
        for (var version = 1; version <= MaxVersion; version++)
        {
            var spec = LevelM[version - 1];
            var dataCodewords = spec.Blocks1 * spec.Data1 + spec.Blocks2 * spec.Data2;
            var countBits = version <= 9 ? 8 : 16;
            if (4 + countBits + 8 * data.Length <= dataCodewords * 8)
                return Build(version, data, dataCodewords, countBits);
        }
        throw new ArgumentException($"{data.Length} bytes do not fit a version {MaxVersion} QR code at level M", nameof(text));
    }

    private static QrMatrix Build(int version, byte[] data, int dataCodewords, int countBits)
    {
        var bits = new List<bool>(dataCodewords * 8);
        Append(bits, 0b0100, 4);
        Append(bits, data.Length, countBits);
        foreach (var b in data)
            Append(bits, b, 8);
        Append(bits, 0, Math.Min(4, dataCodewords * 8 - bits.Count));
        while (bits.Count % 8 != 0)
            bits.Add(false);
        var codewords = new byte[dataCodewords];
        for (var i = 0; i < bits.Count / 8; i++)
        {
            var v = 0;
            for (var j = 0; j < 8; j++)
                v = (v << 1) | (bits[8 * i + j] ? 1 : 0);
            codewords[i] = (byte)v;
        }
        for (var i = bits.Count / 8; i < dataCodewords; i++)
            codewords[i] = (byte)((i - bits.Count / 8) % 2 == 0 ? 0xEC : 0x11);

        var stream = Interleave(version, codewords);
        var size = 17 + 4 * version;
        var modules = new bool[size * size];
        var function = new bool[size * size];
        DrawFunctionPatterns(version, size, modules, function);
        DrawCodewords(size, stream, modules, function);

        var best = -1;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(size, mask, modules, function);
            DrawFormat(size, mask, modules, function);
            var penalty = Penalty(size, modules);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                best = mask;
            }
            ApplyMask(size, mask, modules, function); // XOR again: back to unmasked
        }
        ApplyMask(size, best, modules, function);
        DrawFormat(size, best, modules, function);
        return new QrMatrix(size, modules);
    }

    private static void Append(List<bool> bits, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
            bits.Add(((value >> i) & 1) != 0);
    }

    /// <summary>The data split into blocks, each given its Reed–Solomon codewords, then interleaved: data column by column, then the
    /// error-correction codewords column by column.</summary>
    private static byte[] Interleave(int version, byte[] codewords)
    {
        var spec = LevelM[version - 1];
        var blocks = new List<byte[]>();
        var at = 0;
        for (var b = 0; b < spec.Blocks1 + spec.Blocks2; b++)
        {
            var len = b < spec.Blocks1 ? spec.Data1 : spec.Data2;
            blocks.Add(codewords.AsSpan(at, len).ToArray());
            at += len;
        }
        var generator = ReedSolomon.Generator(spec.Ec);
        var ec = blocks.Select(block => ReedSolomon.Remainder(block, generator)).ToList();
        var result = new List<byte>();
        var longest = blocks.Max(b => b.Length);
        for (var i = 0; i < longest; i++)
        {
            foreach (var block in blocks)
            {
                if (i < block.Length)
                    result.Add(block[i]);
            }
        }
        for (var i = 0; i < spec.Ec; i++)
        {
            foreach (var e in ec)
                result.Add(e[i]);
        }
        return [.. result];
    }

    private static void Set(int size, bool[] modules, bool[] function, int row, int col, bool dark)
    {
        modules[row * size + col] = dark;
        function[row * size + col] = true;
    }

    private static void DrawFunctionPatterns(int version, int size, bool[] modules, bool[] function)
    {
        for (var i = 0; i < size; i++)
        {
            Set(size, modules, function, 6, i, i % 2 == 0);
            Set(size, modules, function, i, 6, i % 2 == 0);
        }
        DrawFinder(size, modules, function, 3, 3);
        DrawFinder(size, modules, function, 3, size - 4);
        DrawFinder(size, modules, function, size - 4, 3);
        var centres = Alignment[version - 1];
        foreach (var r in centres)
        {
            foreach (var c in centres)
            {
                // Not over a finder.
                if ((r == 6 && c == 6) || (r == 6 && c == centres[^1]) || (r == centres[^1] && c == 6))
                    continue;
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                        Set(size, modules, function, r + dy, c + dx, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            }
        }
        // Reserve the format areas (drawn per mask) and the dark module.
        DrawFormat(size, 0, modules, function);
        if (version >= 7)
        {
            var rem = version;
            for (var i = 0; i < 12; i++)
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            var bits = (version << 12) | rem;
            for (var i = 0; i < 18; i++)
            {
                var dark = ((bits >> i) & 1) != 0;
                var a = size - 11 + i % 3;
                var b = i / 3;
                Set(size, modules, function, b, a, dark);
                Set(size, modules, function, a, b, dark);
            }
        }
    }

    /// <summary>A finder (7 × 7, dark ring, light ring, dark 3 × 3) with its light separator, centred at (row, col).</summary>
    private static void DrawFinder(int size, bool[] modules, bool[] function, int row, int col)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                int r = row + dy, c = col + dx;
                if (r < 0 || c < 0 || r >= size || c >= size)
                    continue;
                var d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                Set(size, modules, function, r, c, d != 2 && d != 4);
            }
        }
    }

    /// <summary>The 15 format bits (level M, the mask, BCH(15,5), XOR 0x5412) in both copies, and the dark module.</summary>
    private static void DrawFormat(int size, int mask, bool[] modules, bool[] function)
    {
        var data = (0b00 << 3) | mask; // level M is 00
        var rem = data;
        for (var i = 0; i < 10; i++)
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = ((data << 10) | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        // Around the top-left finder: (row, col).
        for (var i = 0; i <= 5; i++)
            Set(size, modules, function, i, 8, Bit(i));
        Set(size, modules, function, 7, 8, Bit(6));
        Set(size, modules, function, 8, 8, Bit(7));
        Set(size, modules, function, 8, 7, Bit(8));
        for (var i = 9; i < 15; i++)
            Set(size, modules, function, 8, 14 - i, Bit(i));
        // Split between the top-right and bottom-left finders.
        for (var i = 0; i < 8; i++)
            Set(size, modules, function, 8, size - 1 - i, Bit(i));
        for (var i = 8; i < 15; i++)
            Set(size, modules, function, size - 15 + i, 8, Bit(i));
        Set(size, modules, function, size - 8, 8, true);
    }

    /// <summary>The codewords' bits in the zigzag: two-column strips from the right, upward then downward, skipping the vertical
    /// timing column and every function module; what is left over stays light.</summary>
    private static void DrawCodewords(int size, byte[] stream, bool[] modules, bool[] function)
    {
        var i = 0;
        var total = stream.Length * 8;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
                right = 5;
            for (var vert = 0; vert < size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var col = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var row = upward ? size - 1 - vert : vert;
                    if (function[row * size + col])
                        continue;
                    if (i < total)
                    {
                        modules[row * size + col] = ((stream[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
            }
        }
    }

    private static void ApplyMask(int size, int mask, bool[] modules, bool[] function)
    {
        for (var r = 0; r < size; r++)
        {
            for (var c = 0; c < size; c++)
            {
                if (function[r * size + c])
                    continue;
                var invert = mask switch
                {
                    0 => (r + c) % 2 == 0,
                    1 => r % 2 == 0,
                    2 => c % 3 == 0,
                    3 => (r + c) % 3 == 0,
                    4 => (r / 2 + c / 3) % 2 == 0,
                    5 => r * c % 2 + r * c % 3 == 0,
                    6 => (r * c % 2 + r * c % 3) % 2 == 0,
                    _ => ((r + c) % 2 + r * c % 3) % 2 == 0,
                };
                if (invert)
                    modules[r * size + c] = !modules[r * size + c];
            }
        }
    }

    private static readonly bool[] FinderLike1 = [true, false, true, true, true, false, true, false, false, false, false];
    private static readonly bool[] FinderLike2 = [false, false, false, false, true, false, true, true, true, false, true];

    /// <summary>The standard's penalty: runs of five or more (3 + the excess), 2 × 2 blocks (3 each), finder-like runs with four
    /// light modules beside them (40 each), and 10 per 5 % the dark share strays from half.</summary>
    private static int Penalty(int size, bool[] m)
    {
        var penalty = 0;
        for (var line = 0; line < size; line++)
        {
            for (var horizontal = 0; horizontal < 2; horizontal++)
            {
                bool At(int k) => horizontal == 0 ? m[line * size + k] : m[k * size + line];
                var run = 1;
                for (var k = 1; k <= size; k++)
                {
                    if (k < size && At(k) == At(k - 1))
                    {
                        run++;
                        continue;
                    }
                    if (run >= 5)
                        penalty += 3 + run - 5;
                    run = 1;
                }
                for (var k = -4; k < size; k++)
                {
                    if (Matches(k, FinderLike1) || Matches(k, FinderLike2))
                        penalty += 40;
                }

                bool Matches(int start, bool[] pattern)
                {
                    for (var t = 0; t < pattern.Length; t++)
                    {
                        var p = start + t;
                        var dark = p >= 0 && p < size && At(p);
                        if (dark != pattern[t])
                            return false;
                    }
                    return true;
                }
            }
        }
        for (var r = 0; r < size - 1; r++)
        {
            for (var c = 0; c < size - 1; c++)
            {
                var v = m[r * size + c];
                if (m[r * size + c + 1] == v && m[(r + 1) * size + c] == v && m[(r + 1) * size + c + 1] == v)
                    penalty += 3;
            }
        }
        var dark = m.Count(x => x);
        var k5 = Math.Abs(dark * 20 - m.Length * 10) / m.Length;
        return penalty + 10 * k5;
    }
}

/// <summary>Reed–Solomon over GF(256) with the QR polynomial x⁸ + x⁴ + x³ + x² + 1 (0x11D) and generator roots α⁰…α^(n−1).</summary>
internal static class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = Tables(Exp);

    /// <summary>Fills the exponent table (doubled, so a sum of two logs needs no modulo) and answers the log table.</summary>
    private static byte[] Tables(byte[] exp)
    {
        var log = new byte[256];
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            exp[i] = (byte)x;
            log[x] = (byte)i;
            x <<= 1;
            if (x >= 256)
                x ^= 0x11D;
        }
        for (var i = 255; i < 512; i++)
            exp[i] = exp[i - 255];
        return log;
    }

    public static byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    /// <summary>The generator's coefficients after the leading 1, highest degree first.</summary>
    public static byte[] Generator(int degree)
    {
        var g = new byte[degree];
        g[degree - 1] = 1;
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                g[j] = Multiply(g[j], root);
                if (j + 1 < degree)
                    g[j] ^= g[j + 1];
            }
            root = Multiply(root, 2);
        }
        return g;
    }

    public static byte[] Remainder(byte[] data, byte[] generator)
    {
        var r = new byte[generator.Length];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ r[0]);
            Array.Copy(r, 1, r, 0, r.Length - 1);
            r[^1] = 0;
            for (var i = 0; i < r.Length; i++)
                r[i] ^= Multiply(generator[i], factor);
        }
        return r;
    }
}
