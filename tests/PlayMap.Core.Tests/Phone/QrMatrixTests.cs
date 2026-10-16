using System.Text;
using PlayMap.Core.Phone;

namespace PlayMap.Core.Tests.Phone;

/// <summary>The invite's QR code: read back by a reader written apart from the encoder (its own module map, its own zigzag, the
/// Reed–Solomon syndromes rather than a re-encode), at every version the encoder makes; the format bits name level M and the mask;
/// the base64 bitset is the matrix. (The page-side check with an outside decoder is tests/e2e/phonescan/qr.test.mjs.)</summary>
public sealed class QrMatrixTests
{
    public static TheoryData<string> Texts() =>
    [
        "https://192.168.1.44:8080/scan#t=ABCDEFGHIJKLMNOPQRSTUVWXYZ234567",
        "a",
        "https://[fd00::1234:5678:9abc:def0]:49152/scan#t=QZ7KX2M4N6P8R3S5T7V9W2Y4Z6B8C3D5",
        new string('x', 100),
        string.Concat(Enumerable.Repeat("Stage left ✓ ", 12)),
        new string('q', 213),
    ];

    [Theory]
    [MemberData(nameof(Texts))]
    public void AnIndependentReaderReadsTheTextBack(string text)
    {
        var qr = QrMatrix.Encode(text);
        Assert.Equal(17 + 4 * qr.Version, qr.Size);
        Assert.InRange(qr.Version, 1, 10);
        Assert.Equal(text, QrReader.Read(qr.Size, qr.Modules));
    }

    [Fact]
    public void TheSmallestVersionThatHoldsTheTextIsChosen()
    {
        Assert.Equal(1, QrMatrix.Encode(new string('a', 14)).Version);
        Assert.Equal(2, QrMatrix.Encode(new string('a', 15)).Version);
        Assert.Equal(10, QrMatrix.Encode(new string('a', 213)).Version);
        Assert.Throws<ArgumentException>(() => QrMatrix.Encode(new string('a', 214)));
    }

    [Fact]
    public void TheBitsetIsTheMatrixRowMajorMostSignificantBitFirst()
    {
        var qr = QrMatrix.Encode("https://10.0.0.2:8443/scan#t=AAAA");
        var bytes = Convert.FromBase64String(qr.ToBase64Bitset());
        Assert.Equal((qr.Size * qr.Size + 7) / 8, bytes.Length);
        for (var i = 0; i < qr.Modules.Length; i++)
            Assert.Equal(qr.Modules[i], (bytes[i / 8] & (0x80 >> (i % 8))) != 0);
        // The top-left finder: a dark ring round a light ring round a dark 3 × 3.
        Assert.True(qr[0, 0] && qr[0, 6] && qr[6, 0] && qr[2, 2] && qr[4, 4]);
        Assert.False(qr[1, 1] || qr[5, 5] || qr[7, 7]);
    }
}

/// <summary>A QR reader for a clean module matrix (no image): format bits, unmask, the zigzag, the blocks, the syndromes, byte mode.</summary>
internal static class QrReader
{
    private static readonly int[][] Centres =
    [
        [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    // Level M per version: (ec per block, [data codewords of each block]).
    private static readonly (int Ec, int[] Blocks)[] Spec =
    [
        (10, [16]), (16, [28]), (26, [44]), (18, [32, 32]), (24, [43, 43]), (16, [27, 27, 27, 27]), (18, [31, 31, 31, 31]),
        (22, [38, 38, 39, 39]), (22, [36, 36, 36, 37, 37]), (26, [43, 43, 43, 43, 44]),
    ];

    public static string Read(int size, bool[] m)
    {
        var version = (size - 17) / 4;
        bool Dark(int r, int c) => m[r * size + c];

        // Format: bits 0–5 down column 8, then rows 7 and 8 at column 8, then column 7 and leftwards along row 8.
        (int R, int C)[] formatAt = [(0, 8), (1, 8), (2, 8), (3, 8), (4, 8), (5, 8), (7, 8), (8, 8), (8, 7), (8, 5), (8, 4), (8, 3), (8, 2), (8, 1), (8, 0)];
        var format = 0;
        for (var i = 0; i < 15; i++)
            format |= (Dark(formatAt[i].R, formatAt[i].C) ? 1 : 0) << i;
        format ^= 0x5412;
        var level = format >> 13;
        var mask = (format >> 10) & 7;
        Assert.Equal(0, level); // M
        var check = format >> 10;
        for (var i = 0; i < 10; i++)
            check = (check << 1) ^ ((check >> 9) * 0x537);
        Assert.Equal(format & 0x3FF, check);

        var reserved = new bool[size, size];
        void Block(int r0, int c0, int h, int w)
        {
            for (var r = Math.Max(0, r0); r < Math.Min(size, r0 + h); r++)
            {
                for (var c = Math.Max(0, c0); c < Math.Min(size, c0 + w); c++)
                    reserved[r, c] = true;
            }
        }
        Block(0, 0, 9, 9);
        Block(0, size - 8, 9, 8);
        Block(size - 8, 0, 8, 9);
        Block(6, 0, 1, size);
        Block(0, 6, size, 1);
        foreach (var r in Centres[version - 1])
        {
            foreach (var c in Centres[version - 1])
            {
                // Every centre pair but the three under a finder.
                var underFinder = (r < 9 && c < 9) || (r < 9 && c > size - 9) || (r > size - 9 && c < 9);
                if (!underFinder)
                    Block(r - 2, c - 2, 5, 5);
            }
        }
        if (version >= 7)
        {
            Block(0, size - 11, 6, 3);
            Block(size - 11, 0, 3, 6);
        }

        var bits = new List<bool>();
        var up = true;
        for (var col = size - 1; col > 0; col -= 2)
        {
            if (col == 6)
                col--;
            for (var step = 0; step < size; step++)
            {
                var row = up ? size - 1 - step : step;
                for (var dc = 0; dc < 2; dc++)
                {
                    var c = col - dc;
                    if (reserved[row, c])
                        continue;
                    var flip = mask switch
                    {
                        0 => (row + c) % 2 == 0,
                        1 => row % 2 == 0,
                        2 => c % 3 == 0,
                        3 => (row + c) % 3 == 0,
                        4 => (row / 2 + c / 3) % 2 == 0,
                        5 => row * c % 2 + row * c % 3 == 0,
                        6 => (row * c % 2 + row * c % 3) % 2 == 0,
                        _ => ((row + c) % 2 + row * c % 3) % 2 == 0,
                    };
                    bits.Add(Dark(row, c) ^ flip);
                }
            }
            up = !up;
        }

        var (ec, blocks) = Spec[version - 1];
        var total = blocks.Sum() + ec * blocks.Length;
        var stream = new byte[total];
        for (var i = 0; i < total; i++)
        {
            for (var b = 0; b < 8; b++)
                stream[i] = (byte)((stream[i] << 1) | (bits[8 * i + b] ? 1 : 0));
        }
        var data = new List<byte>[blocks.Length];
        var all = new List<byte>[blocks.Length];
        for (var b = 0; b < blocks.Length; b++)
        {
            data[b] = [];
            all[b] = [];
        }
        var at = 0;
        for (var i = 0; i < blocks.Max(); i++)
        {
            for (var b = 0; b < blocks.Length; b++)
            {
                if (i < blocks[b])
                    data[b].Add(stream[at++]);
            }
        }
        for (var b = 0; b < blocks.Length; b++)
            all[b].AddRange(data[b]);
        for (var i = 0; i < ec; i++)
        {
            for (var b = 0; b < blocks.Length; b++)
                all[b].Add(stream[at++]);
        }
        foreach (var block in all)
        {
            for (var i = 0; i < ec; i++)
                Assert.Equal(0, Syndrome(block, i));
        }

        var payload = data.SelectMany(d => d).ToArray();
        var reader = new BitReader(payload);
        Assert.Equal(0b0100, reader.Take(4));
        var count = reader.Take(version <= 9 ? 8 : 16);
        var text = new byte[count];
        for (var i = 0; i < count; i++)
            text[i] = (byte)reader.Take(8);
        return Encoding.UTF8.GetString(text);
    }

    /// <summary>The codeword polynomial evaluated at αⁱ (0 for every i below the error-correction count when nothing is wrong).</summary>
    private static int Syndrome(List<byte> block, int i)
    {
        var s = 0;
        foreach (var c in block)
            s = Mul(s, Pow(i)) ^ c;
        return s;
    }

    private static int Pow(int e)
    {
        var x = 1;
        for (var k = 0; k < e; k++)
            x = Mul(x, 2);
        return x;
    }

    private static int Mul(int a, int b)
    {
        var r = 0;
        while (b > 0)
        {
            if ((b & 1) != 0)
                r ^= a;
            a <<= 1;
            if ((a & 0x100) != 0)
                a ^= 0x11D;
            b >>= 1;
        }
        return r;
    }

    private sealed class BitReader(byte[] bytes)
    {
        private int _bit;

        public int Take(int n)
        {
            var v = 0;
            for (var i = 0; i < n; i++, _bit++)
                v = (v << 1) | ((bytes[_bit / 8] >> (7 - _bit % 8)) & 1);
            return v;
        }
    }
}
