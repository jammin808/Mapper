using System.Globalization;
using System.Text;
using PlayMap.Devices.Model;

namespace PlayMap.Devices.Display;

/// <summary>What a display's EDID base block says: who made it, its name and serial, its native (preferred) timing.</summary>
/// <param name="Manufacturer">The three-letter PNP id ("PAN", "SNY", "NEC", "EPS", "CDT"…).</param>
/// <param name="SerialNumber">The 32-bit serial field (0 = not given; many projectors put it in <paramref name="SerialText"/>).</param>
/// <param name="RefreshHz">The preferred timing's refresh rate (pixel clock ÷ total pixels per frame).</param>
public sealed record EdidInfo(
    string Manufacturer,
    int ProductCode,
    uint SerialNumber,
    string Name,
    string SerialText,
    int Year,
    int NativeWidth,
    int NativeHeight,
    double RefreshHz,
    string Version)
{
    /// <summary>Whether a networked projector's identity is this display: the serial text equals the projector's
    /// serial, or — with no serial to compare — the EDID name is the model.</summary>
    public bool Matches(DeviceState projector)
    {
        if (SerialText.Length > 0 && projector.Serial is { Length: > 0 } serial)
            return serial.Trim().Equals(SerialText, StringComparison.OrdinalIgnoreCase);
        return Name.Length > 0 && projector.Model is { Length: > 0 } model
            && model.Trim().Equals(Name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The EDID base block (VESA E-EDID 1.3/1.4): the fixed 8-byte header, the packed manufacturer id, product code,
/// serial, year, then four 18-byte descriptors — a detailed timing (the first is the preferred, i.e. native,
/// mode) or a display descriptor (FF serial text, FC name). The block's 128 bytes sum to 0 mod 256. This is what
/// a GPU output reads from a projector over DDC; the platform reads the bytes (the registry on Windows,
/// /sys/class/drm/*/edid on Linux) and hands them here. DDC/CI control is rarely offered by projectors and is not used.
/// </summary>
public static class Edid
{
    private static ReadOnlySpan<byte> Header => [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <summary>The base block's facts, or null with the reason when the bytes are not a valid EDID block.</summary>
    public static EdidInfo? Parse(ReadOnlySpan<byte> edid, out string problem)
    {
        problem = "";
        if (edid.Length < 128)
        {
            problem = "an EDID base block is 128 bytes";
            return null;
        }
        var block = edid[..128];
        if (!block[..8].SequenceEqual(Header))
        {
            problem = "the EDID header is missing";
            return null;
        }
        var sum = 0;
        foreach (var b in block)
            sum += b;
        if ((sum & 0xFF) != 0)
        {
            problem = "the EDID checksum is wrong";
            return null;
        }
        var packed = (block[8] << 8) | block[9];
        var manufacturer = new string([Letter(packed >> 10), Letter(packed >> 5), Letter(packed)]);
        var product = block[10] | (block[11] << 8);
        var serial = (uint)(block[12] | (block[13] << 8) | (block[14] << 16) | (block[15] << 24));
        var year = block[17] > 0 ? 1990 + block[17] : 0;
        var version = block[18].ToString(CultureInfo.InvariantCulture) + "." + block[19].ToString(CultureInfo.InvariantCulture);
        var name = "";
        var serialText = "";
        var width = 0;
        var height = 0;
        var refresh = 0.0;
        for (var d = 0; d < 4; d++)
        {
            var desc = block.Slice(54 + d * 18, 18);
            var clock = desc[0] | (desc[1] << 8);
            if (clock != 0)
            {
                if (width > 0)
                    continue;
                var hActive = desc[2] | ((desc[4] & 0xF0) << 4);
                var hBlank = desc[3] | ((desc[4] & 0x0F) << 8);
                var vActive = desc[5] | ((desc[7] & 0xF0) << 4);
                var vBlank = desc[6] | ((desc[7] & 0x0F) << 8);
                width = hActive;
                height = vActive;
                var total = (double)(hActive + hBlank) * (vActive + vBlank);
                refresh = total > 0 ? Math.Round(clock * 10_000.0 / total, 3) : 0;
                continue;
            }
            switch (desc[3])
            {
                case 0xFC:
                    name = Text(desc[5..18]);
                    break;
                case 0xFF:
                    serialText = Text(desc[5..18]);
                    break;
            }
        }
        return new EdidInfo(manufacturer, product, serial, name, serialText, year, width, height, refresh, version);
    }

    private static char Letter(int fiveBits) => (char)('A' - 1 + (fiveBits & 0x1F));

    /// <summary>A descriptor's 13 text bytes: ASCII, ended by a line feed, padded with spaces.</summary>
    private static string Text(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0x0A);
        return Encoding.ASCII.GetString(end >= 0 ? bytes[..end] : bytes).Trim();
    }
}
