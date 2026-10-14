namespace PlayMap.Core.Media.Hap;

/// <summary>The texture nibble of a HAP image section type (the low four bits). Values are the HAP specification's.</summary>
public enum HapTexture : byte
{
    Bc4Alpha = 0x01,
    Bc6hUnsigned = 0x02,
    Bc6hSigned = 0x03,
    Bc1Rgb = 0x0B,
    Bc7Rgba = 0x0C,
    Bc3Rgba = 0x0E,
    Bc3YCoCg = 0x0F,
}

/// <summary>A chunk's second-stage compressor (the HAP specification's byte values).</summary>
public enum HapCompressor : byte
{
    None = 0x0A,
    Snappy = 0x0B,
}

/// <summary>The seven HAP codecs, one per fourcc.</summary>
public enum HapCodec
{
    Hap,
    HapAlpha,
    HapQ,
    HapQAlpha,
    HapAlphaOnly,
    HapR,
    HapHdr,
}

/// <summary>A section type the HAP specification names, and its meaning in its words.</summary>
public readonly record struct HapSectionType(byte Type, string Meaning);

/// <summary>The HAP family as data: fourcc ↔ codec ↔ texture ↔ pixel layout, the section-type table and which textures a
/// codec's frames may hold. Every table here is the published HAP specification's; nothing is inferred from a file.</summary>
public static class HapCodecs
{
    /// <summary>The second-stage nibble (high four bits) of an image section: one raw chunk, one Snappy chunk, or chunks
    /// described by a decode-instructions container.</summary>
    public const byte StageNone = 0xA, StageSnappy = 0xB, StageComplex = 0xC;

    /// <summary>Section types that are not images.</summary>
    public const byte DecodeInstructions = 0x01, CompressorTable = 0x02, SizeTable = 0x03, OffsetTable = 0x04, MultipleImages = 0x0D;

    /// <summary>Every section type the specification defines, in its order: the four decode-instruction sections, the
    /// multiple-images container, then each texture format under each second stage.</summary>
    public static readonly IReadOnlyList<HapSectionType> SectionTypes = BuildSectionTypes();

    private static readonly HapCodec[] All = Enum.GetValues<HapCodec>();

    public static IReadOnlyList<HapCodec> Codecs => All;

    /// <summary>The codec a fourcc names ('Hap1', 'Hap5', 'HapY', 'HapM', 'HapA', 'Hap7', 'HapH'; the tag read as a
    /// little-endian uint, the way containers store it).</summary>
    public static bool TryFromTag(uint fourcc, out HapCodec codec)
    {
        foreach (var c in All)
        {
            if (Tag(c) == fourcc)
            {
                codec = c;
                return true;
            }
        }
        codec = default;
        return false;
    }

    public static uint Tag(HapCodec codec) => Four(TagText(codec));

    public static string TagText(HapCodec codec) => codec switch
    {
        HapCodec.Hap => "Hap1",
        HapCodec.HapAlpha => "Hap5",
        HapCodec.HapQ => "HapY",
        HapCodec.HapQAlpha => "HapM",
        HapCodec.HapAlphaOnly => "HapA",
        HapCodec.HapR => "Hap7",
        HapCodec.HapHdr => "HapH",
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "not a HAP codec"),
    };

    /// <summary>The codec's name in <c>MediaInfo.VideoCodec</c> and STATE.</summary>
    public static string Name(HapCodec codec) => codec switch
    {
        HapCodec.Hap => "hap",
        HapCodec.HapAlpha => "hap_alpha",
        HapCodec.HapQ => "hap_q",
        HapCodec.HapQAlpha => "hap_q_alpha",
        HapCodec.HapAlphaOnly => "hap_alpha_only",
        HapCodec.HapR => "hap_r",
        HapCodec.HapHdr => "hap_hdr",
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "not a HAP codec"),
    };

    /// <summary>The codec a <see cref="Name"/> names.</summary>
    public static bool TryFromName(string? name, out HapCodec codec)
    {
        foreach (var c in All)
        {
            if (string.Equals(Name(c), name, StringComparison.Ordinal))
            {
                codec = c;
                return true;
            }
        }
        codec = default;
        return false;
    }

    /// <summary>The operator's words for the codec.</summary>
    public static string Words(HapCodec codec) => codec switch
    {
        HapCodec.Hap => "HAP",
        HapCodec.HapAlpha => "HAP Alpha",
        HapCodec.HapQ => "HAP Q",
        HapCodec.HapQAlpha => "HAP Q Alpha",
        HapCodec.HapAlphaOnly => "HAP Alpha-Only",
        HapCodec.HapR => "HAP R",
        HapCodec.HapHdr => "HAP HDR",
        _ => codec.ToString(),
    };

    /// <summary>True for the five codecs FFmpeg's own decoder reads on the CPU (HAP R and HAP HDR it does not).</summary>
    public static bool FfmpegDecodes(HapCodec codec) => codec is not (HapCodec.HapR or HapCodec.HapHdr);

    /// <summary>Bytes per 4×4 block: 8 for BC1 and BC4, 16 for BC3, BC7 and BC6H.</summary>
    public static int BlockBytes(HapTexture texture) => texture switch
    {
        HapTexture.Bc1Rgb or HapTexture.Bc4Alpha => 8,
        HapTexture.Bc3Rgba or HapTexture.Bc3YCoCg or HapTexture.Bc7Rgba or HapTexture.Bc6hUnsigned or HapTexture.Bc6hSigned => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(texture), texture, "not a HAP texture"),
    };

    public static bool IsTexture(int nibble) => nibble is 0x01 or 0x02 or 0x03 or 0x0B or 0x0C or 0x0E or 0x0F;

    /// <summary>Whether a frame of the codec may hold the texture as its image <paramref name="index"/> (0, or 1 for the
    /// alpha image of HAP Q Alpha).</summary>
    public static bool Allowed(HapCodec codec, HapTexture texture, int index) => (codec, index) switch
    {
        (HapCodec.Hap, 0) => texture == HapTexture.Bc1Rgb,
        (HapCodec.HapAlpha, 0) => texture == HapTexture.Bc3Rgba,
        (HapCodec.HapQ, 0) => texture == HapTexture.Bc3YCoCg,
        (HapCodec.HapQAlpha, 0) => texture == HapTexture.Bc3YCoCg,
        (HapCodec.HapQAlpha, 1) => texture == HapTexture.Bc4Alpha,
        (HapCodec.HapAlphaOnly, 0) => texture == HapTexture.Bc4Alpha,
        (HapCodec.HapR, 0) => texture == HapTexture.Bc7Rgba,
        (HapCodec.HapHdr, 0) => texture is HapTexture.Bc6hUnsigned or HapTexture.Bc6hSigned,
        _ => false,
    };

    /// <summary>How many images a frame of the codec holds.</summary>
    public static int Images(HapCodec codec) => codec == HapCodec.HapQAlpha ? 2 : 1;

    /// <summary>The pixel layout a frame of the codec decodes to (HAP HDR by its first texture: unsigned or signed BC6H).</summary>
    public static PixelLayout Layout(HapCodec codec, HapTexture first) => codec switch
    {
        HapCodec.Hap => PixelLayout.Bc1,
        HapCodec.HapAlpha => PixelLayout.Bc3,
        HapCodec.HapQ => PixelLayout.Bc3YCoCg,
        HapCodec.HapQAlpha => PixelLayout.Bc3YCoCgBc4,
        HapCodec.HapAlphaOnly => PixelLayout.Bc4,
        HapCodec.HapR => PixelLayout.Bc7,
        HapCodec.HapHdr => first == HapTexture.Bc6hSigned ? PixelLayout.Bc6hSfloat : PixelLayout.Bc6hUfloat,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "not a HAP codec"),
    };

    /// <summary>The first (or only) texture a codec's frames hold, for sizing before a frame is read (HAP HDR: unsigned).</summary>
    public static HapTexture FirstTexture(HapCodec codec) => codec switch
    {
        HapCodec.Hap => HapTexture.Bc1Rgb,
        HapCodec.HapAlpha => HapTexture.Bc3Rgba,
        HapCodec.HapQ or HapCodec.HapQAlpha => HapTexture.Bc3YCoCg,
        HapCodec.HapAlphaOnly => HapTexture.Bc4Alpha,
        HapCodec.HapR => HapTexture.Bc7Rgba,
        HapCodec.HapHdr => HapTexture.Bc6hUnsigned,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "not a HAP codec"),
    };

    /// <summary>True for the block-compressed pixel layouts (HAP's).</summary>
    public static bool IsBlockLayout(PixelLayout layout) => layout is PixelLayout.Bc1 or PixelLayout.Bc3 or PixelLayout.Bc3YCoCg
        or PixelLayout.Bc3YCoCgBc4 or PixelLayout.Bc4 or PixelLayout.Bc7 or PixelLayout.Bc6hUfloat or PixelLayout.Bc6hSfloat;

    /// <summary>The planes of a block layout: (block bytes of plane 0, block bytes of plane 1 or 0).</summary>
    public static (int First, int Second) PlaneBlockBytes(PixelLayout layout) => layout switch
    {
        PixelLayout.Bc1 or PixelLayout.Bc4 => (8, 0),
        PixelLayout.Bc3 or PixelLayout.Bc3YCoCg or PixelLayout.Bc7 or PixelLayout.Bc6hUfloat or PixelLayout.Bc6hSfloat => (16, 0),
        PixelLayout.Bc3YCoCgBc4 => (16, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "not a block layout"),
    };

    /// <summary>The texture's word in STATE and MEDIA INSPECT.</summary>
    public static string TextureWord(PixelLayout layout) => layout switch
    {
        PixelLayout.Bc1 => "bc1",
        PixelLayout.Bc3 => "bc3",
        PixelLayout.Bc3YCoCg => "ycocg-bc3",
        PixelLayout.Bc3YCoCgBc4 => "ycocg-bc3+bc4",
        PixelLayout.Bc4 => "bc4",
        PixelLayout.Bc7 => "bc7",
        PixelLayout.Bc6hUfloat => "bc6h-uf16",
        PixelLayout.Bc6hSfloat => "bc6h-sf16",
        _ => "",
    };

    /// <summary>The block families the renderer must sample for a codec's frames.</summary>
    public static BlockFormats Needs(HapCodec codec) => codec switch
    {
        HapCodec.Hap => BlockFormats.Bc1,
        HapCodec.HapAlpha or HapCodec.HapQ => BlockFormats.Bc3,
        HapCodec.HapQAlpha => BlockFormats.Bc3 | BlockFormats.Bc4,
        HapCodec.HapAlphaOnly => BlockFormats.Bc4,
        HapCodec.HapR => BlockFormats.Bc7,
        HapCodec.HapHdr => BlockFormats.Bc6h,
        _ => BlockFormats.None,
    };

    /// <summary>The GL feature words for the block families a codec needs and the renderer lacks ("" when none).</summary>
    public static string Missing(HapCodec codec, BlockFormats have)
    {
        var lack = Needs(codec) & ~have;
        if (lack == BlockFormats.None)
            return "";
        if ((lack & (BlockFormats.Bc7 | BlockFormats.Bc6h)) != 0)
            return "BPTC";
        if ((lack & (BlockFormats.Bc1 | BlockFormats.Bc3)) != 0)
            return (lack & BlockFormats.Bc4) != 0 ? "S3TC and RGTC" : "S3TC";
        return "RGTC";
    }

    /// <summary>Bytes of one plane of 4×4 blocks for a picture (sizes round up to whole blocks).</summary>
    public static long PlaneBytes(int width, int height, int blockBytes) => (long)((width + 3) / 4) * ((height + 3) / 4) * blockBytes;

    /// <summary>Bytes of a whole frame's textures for a codec at a size.</summary>
    public static long FrameBytes(HapCodec codec, int width, int height)
    {
        var (a, b) = PlaneBlockBytes(Layout(codec, FirstTexture(codec)));
        return PlaneBytes(width, height, a) + (b > 0 ? PlaneBytes(width, height, b) : 0);
    }

    private static uint Four(string s) => s[0] | ((uint)s[1] << 8) | ((uint)s[2] << 16) | ((uint)s[3] << 24);

    private static HapSectionType[] BuildSectionTypes()
    {
        var list = new List<HapSectionType>
        {
            new(DecodeInstructions, "Decode Instructions Container"),
            new(CompressorTable, "Chunk Second-Stage Compressor Table"),
            new(SizeTable, "Chunk Size Table"),
            new(OffsetTable, "Chunk Offset Table"),
            new(MultipleImages, "Multiple Images"),
        };
        (byte Nibble, string Name)[] stages = [(StageNone, "None"), (StageSnappy, "Snappy"), (StageComplex, "Complex")];
        (byte Nibble, string Name)[] textures =
        [
            (0x0B, "RGB DXT1"), (0x0E, "RGBA DXT5"), (0x0F, "Scaled YCoCg DXT5"), (0x01, "Alpha RGTC1"),
            (0x0C, "RGBA BPTC Unorm"), (0x02, "RGB BPTC Unsigned Float"), (0x03, "RGB BPTC Signed Float"),
        ];
        foreach (var (t, tn) in textures)
            foreach (var (s, sn) in stages)
                list.Add(new HapSectionType((byte)((s << 4) | t), tn + ", " + sn));
        return [.. list];
    }
}
