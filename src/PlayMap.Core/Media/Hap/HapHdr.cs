using System.Buffers.Binary;
using System.Globalization;
using PlayMap.Core.Model;

namespace PlayMap.Core.Media.Hap;

/// <summary>A QuickTime/ISO 'colr' box's nclx payload: ITU-T H.273 code points for primaries, transfer and matrix, and the
/// full-range flag.</summary>
public readonly record struct Nclx(int Primaries, int Transfer, int Matrix, bool FullRange);

/// <summary>How a HAP HDR picture's values are read: the transfer they are encoded in, the nits a linear 1.0 stands for,
/// and which rule decided ("item", "colr", "tag", "default").</summary>
public readonly record struct HapHdrReading(TransferFunction Transfer, double WhiteNits, string Rule)
{
    /// <summary>The factor a linear value is multiplied by so that 1.0 is SDR reference white (203 nits).</summary>
    public double Scale => Transfer == TransferFunction.Linear ? WhiteNits / HapHdr.ReferenceWhiteNits : 1;

    /// <summary>The `hap` program's transfer code: 0 linear, 1 PQ, 2 HLG, 3 display SDR.</summary>
    public int ShaderTransfer => Transfer switch
    {
        TransferFunction.Pq => 1,
        TransferFunction.Hlg => 2,
        TransferFunction.Sdr => 3,
        _ => 0,
    };

    /// <summary>STATE's words: "linear · white 203 nits (default)", "pq (colr)", "linear · white 100 nits (item)".</summary>
    public string Words => Transfer == TransferFunction.Linear
        ? string.Create(CultureInfo.InvariantCulture, $"linear · white {WhiteNits:0.##} nits ({Rule})")
        : $"{HapHdr.TransferWord(Transfer)} ({Rule})";
}

/// <summary>HAP HDR carries no colour metadata, so its reading is an explicit rule chain: the item's own override, then the
/// container's colr nclx transfer, then PlayMap's stream tag for the white level, then the default — linear light with 1.0 =
/// 203 nits (BT.2408 reference white). Every value here is pure; STATE says which rule applied.</summary>
public static class HapHdr
{
    public const double ReferenceWhiteNits = 203;

    /// <summary>The stream metadata key PlayMap writes the white level of a HAP HDR file it makes into.</summary>
    public const string WhiteTag = "playmap_white_nits";

    /// <summary>The reading of a HAP HDR stream. <paramref name="colrTransfer"/> is the container's H.273 transfer code (0 =
    /// none); <paramref name="tagWhiteNits"/> is the stream tag's white level (0 = none).</summary>
    public static HapHdrReading Read(ColorOverride? item, int colrTransfer, double tagWhiteNits)
    {
        item ??= ColorOverride.Auto;
        var itemTransfer = FromWord(item.Transfer);
        var colr = TransferOfCode(colrTransfer);
        var itemWhite = item.WhiteNits > 0 && double.IsFinite(item.WhiteNits) ? item.WhiteNits : 0;
        var tagWhite = tagWhiteNits > 0 && double.IsFinite(tagWhiteNits) ? tagWhiteNits : 0;

        var transfer = itemTransfer ?? colr ?? TransferFunction.Linear;
        var white = itemWhite > 0 ? itemWhite : tagWhite > 0 ? tagWhite : ReferenceWhiteNits;
        string rule;
        if (itemTransfer is not null || itemWhite > 0)
            rule = "item";
        else if (colr is not null)
            rule = "colr";
        else if (tagWhite > 0)
            rule = "tag";
        else
            rule = "default";
        return new HapHdrReading(transfer, white, rule);
    }

    /// <summary>The transfer an H.273 code names, for the codes a HAP HDR file can mean: 8 linear, 16 PQ (SMPTE ST 2084),
    /// 18 HLG (ARIB STD-B67); null for any other (unspecified, or an SDR curve that says nothing about float values).</summary>
    public static TransferFunction? TransferOfCode(int code) => code switch
    {
        8 => TransferFunction.Linear,
        16 => TransferFunction.Pq,
        18 => TransferFunction.Hlg,
        _ => null,
    };

    /// <summary>Reads a 'colr' box payload (after the box header): the four-character colour type 'nclx', then primaries,
    /// transfer and matrix as big-endian u16, then a byte whose top bit is the full-range flag. 'nclc' (QuickTime's form,
    /// no range byte) reads as limited range. Anything else (an ICC profile) is not an nclx.</summary>
    public static bool TryParseNclx(ReadOnlySpan<byte> colr, out Nclx nclx)
    {
        nclx = default;
        if (colr.Length < 10)
            return false;
        var nclxType = colr[0] == (byte)'n' && colr[1] == (byte)'c' && colr[2] == (byte)'l' && colr[3] == (byte)'x';
        var nclcType = colr[0] == (byte)'n' && colr[1] == (byte)'c' && colr[2] == (byte)'l' && colr[3] == (byte)'c';
        if (!nclxType && !nclcType)
            return false;
        if (nclxType && colr.Length < 11)
            return false;
        nclx = new Nclx(
            BinaryPrimitives.ReadUInt16BigEndian(colr[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(colr[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(colr[8..]),
            nclxType && (colr[10] & 0x80) != 0);
        return true;
    }

    public static string TransferWord(TransferFunction transfer) => transfer switch
    {
        TransferFunction.Pq => "pq",
        TransferFunction.Hlg => "hlg",
        TransferFunction.Linear => "linear",
        _ => "sdr",
    };

    private static TransferFunction? FromWord(string? word) => (word ?? "").Trim().ToLowerInvariant() switch
    {
        "linear" => TransferFunction.Linear,
        "pq" => TransferFunction.Pq,
        "hlg" => TransferFunction.Hlg,
        "sdr" => TransferFunction.Sdr,
        _ => null,
    };
}
