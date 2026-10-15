namespace PlayMap.Core.Tc;

/// <summary>The fields of one LTC frame (SMPTE ST 12-1, linear timecode bit assignment): the label, the drop-frame and colour-frame
/// flags, the eight 4-bit user-bit groups packed (group 1 in the low nibble) and the binary group flags.</summary>
public readonly record struct LtcFields(int Hours, int Minutes, int Seconds, int Frames, bool DropFrame, bool ColourFrame, uint UserBits,
    bool Bgf0, bool Bgf1, bool Bgf2);

/// <summary>One 80-bit LTC codeword as bits in time order (bit 0 is sent first; bit i of <see cref="Bits"/> is bit i of the frame).
/// The layout (SMPTE ST 12-1, LTC bit assignment): frame units 0–3, user group 1 4–7, frame tens 8–9, drop frame 10, colour frame
/// 11, user group 2 12–15, seconds units 16–19, group 3 20–23, seconds tens 24–26, bit 27, group 4 28–31, minutes units 32–35,
/// group 5 36–39, minutes tens 40–42, bit 43, group 6 44–47, hours units 48–51, group 7 52–55, hours tens 56–57, BGF1 58, bit 59,
/// group 8 60–63, and the sync word 0011 1111 1111 1101 in 64–79. BCD digits are sent least significant bit first. At 25 fps bit
/// 27 is BGF0, 43 BGF2 and 59 the polarity correction bit; at the other rates 27 is the polarity bit, 43 BGF0 and 59 BGF2. The
/// polarity bit makes the number of zeros in the word even, so every frame starts with the same transition.</summary>
public readonly record struct LtcCodeword(UInt128 Bits)
{
    /// <summary>The sync word as sent: bit 64 first (0, 0, then twelve 1s, then 0, 1).</summary>
    public const int SyncWord = 0b1011_1111_1111_1100;

    public bool this[int bit] => ((Bits >> bit) & UInt128.One) != UInt128.Zero;

    public static LtcCodeword Build(LtcFields f, bool frames25)
    {
        UInt128 b = 0;
        void Put(int at, int width, int value)
        {
            for (var i = 0; i < width; i++)
                if (((value >> i) & 1) != 0)
                    b |= UInt128.One << (at + i);
        }
        Put(0, 4, f.Frames % 10);
        Put(8, 2, f.Frames / 10);
        Put(10, 1, f.DropFrame ? 1 : 0);
        Put(11, 1, f.ColourFrame ? 1 : 0);
        Put(16, 4, f.Seconds % 10);
        Put(24, 3, f.Seconds / 10);
        Put(32, 4, f.Minutes % 10);
        Put(40, 3, f.Minutes / 10);
        Put(48, 4, f.Hours % 10);
        Put(56, 2, f.Hours / 10);
        int[] groups = [4, 12, 20, 28, 36, 44, 52, 60];
        for (var g = 0; g < 8; g++)
            Put(groups[g], 4, (int)((f.UserBits >> (4 * g)) & 0xF));
        Put(58, 1, f.Bgf1 ? 1 : 0);
        Put(frames25 ? 27 : 43, 1, f.Bgf0 ? 1 : 0);
        Put(frames25 ? 43 : 59, 1, f.Bgf2 ? 1 : 0);
        // The sync word: bits 64–79 = 0011111111111101 in time order.
        for (var i = 0; i < 16; i++)
            if (((SyncWord >> i) & 1) != 0)
                b |= UInt128.One << (64 + i);
        var word = new LtcCodeword(b);
        var zeros = 80 - word.Ones;
        if (zeros % 2 != 0)
            word = new LtcCodeword(b | (UInt128.One << (frames25 ? 59 : 27)));
        return word;
    }

    /// <summary>How many bits are 1 in the 80-bit word.</summary>
    public int Ones => (int)UInt128.PopCount(Bits & ((UInt128.One << 80) - 1));

    /// <summary>Whether the polarity bit left an even number of zeros (some generators skip it; the word is still read).</summary>
    public bool ParityOk => (80 - Ones) % 2 == 0;

    public bool HasSync => (int)((Bits >> 64) & 0xFFFF) == SyncWord;

    private int Field(int at, int width) => (int)((Bits >> at) & (UInt128)((1 << width) - 1));

    /// <summary>The fields, or false when a BCD digit is out of range (frames ≤ 39 by its digits, seconds and minutes ≤ 59, hours ≤ 23).</summary>
    public bool TryRead(bool frames25, out LtcFields fields)
    {
        fields = default;
        int fu = Field(0, 4), ft = Field(8, 2), su = Field(16, 4), st = Field(24, 3), mu = Field(32, 4), mt = Field(40, 3), hu = Field(48, 4), ht = Field(56, 2);
        if (fu > 9 || su > 9 || mu > 9 || hu > 9 || st > 5 || mt > 5)
            return false;
        var hours = ht * 10 + hu;
        if (hours > 23)
            return false;
        uint user = 0;
        int[] groups = [4, 12, 20, 28, 36, 44, 52, 60];
        for (var g = 0; g < 8; g++)
            user |= (uint)Field(groups[g], 4) << (4 * g);
        fields = new LtcFields(hours, mt * 10 + mu, st * 10 + su, ft * 10 + fu, this[10], this[11], user,
            this[frames25 ? 27 : 43], this[58], this[frames25 ? 43 : 59]);
        return true;
    }

    /// <summary>The word as 80 characters '0'/'1' in time order (bit 0 first).</summary>
    public string ToBitString()
    {
        var c = new char[80];
        for (var i = 0; i < 80; i++)
            c[i] = this[i] ? '1' : '0';
        return new string(c);
    }

    public static LtcCodeword FromBitString(string bits)
    {
        ArgumentNullException.ThrowIfNull(bits);
        var clean = bits.Replace(" ", "", StringComparison.Ordinal);
        if (clean.Length != 80)
            throw new ArgumentException("80 bits", nameof(bits));
        UInt128 b = 0;
        for (var i = 0; i < 80; i++)
            if (clean[i] == '1')
                b |= UInt128.One << i;
        return new LtcCodeword(b);
    }
}
