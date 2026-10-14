using System.Globalization;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Gpu;

/// <summary>The GLSL dialect a context compiles: desktop GLSL 3.30 core, or OpenGL ES 3.00 (ANGLE on Windows, Mesa's ES
/// profile on Linux). One program source compiles on both: the device writes the dialect's header.</summary>
public enum GlDialect
{
    Core330,
    Es300,
}

/// <summary>What each dialect puts in front of every program source; the sources themselves carry no header.</summary>
public static class GlDialects
{
    private const string EsHeader =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "precision highp int;\n" +
        "precision highp sampler2D;\n" +
        "precision highp sampler3D;\n" +
        "precision highp sampler2DArray;\n" +
        "precision highp isampler2D;\n" +
        "precision highp usampler2D;\n";

    /// <summary>The first lines of every program for the dialect: "#version 330 core", or "#version 300 es" and the precision
    /// lines ES needs (float, int and every sampler kind a program may use: ES gives most of them no default).</summary>
    public static string Header(GlDialect dialect) => dialect switch
    {
        GlDialect.Core330 => "#version 330 core\n",
        GlDialect.Es300 => EsHeader,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "no header for this dialect"),
    };

    /// <summary>The dialect's name as <see cref="GlCaps.Dialect"/> and the node's settings write it.</summary>
    public static string Word(GlDialect dialect) => dialect switch
    {
        GlDialect.Core330 => "core330",
        GlDialect.Es300 => "es300",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "no word for this dialect"),
    };

    /// <summary>A settings word ("core330", "es300"); "auto", empty or anything else is null (the platform's own choice).</summary>
    public static GlDialect? Parse(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "core330" or "core" or "gl" => GlDialect.Core330,
        "es300" or "es" or "gles" => GlDialect.Es300,
        _ => null,
    };

    /// <summary>The dialect a context speaks, from its GL_VERSION string: "OpenGL ES 3.2 Mesa …" is ES; anything else desktop.</summary>
    public static GlDialect FromVersion(string? version) =>
        version is not null && version.StartsWith("OpenGL ES", StringComparison.Ordinal) ? GlDialect.Es300 : GlDialect.Core330;

    /// <summary>Major × 10 + minor from a GL_VERSION string ("4.5 (Core Profile) Mesa …" → 45, "OpenGL ES 3.0 (ANGLE …)" → 30);
    /// 0 when it names none.</summary>
    public static int VersionNumber(string? version)
    {
        if (string.IsNullOrEmpty(version))
            return 0;
        var s = version.StartsWith("OpenGL ES", StringComparison.Ordinal) ? version["OpenGL ES".Length..].TrimStart() : version;
        // "OpenGL ES-CM 1.1" and profile words before the number are skipped to the first digit.
        var i = 0;
        while (i < s.Length && !char.IsAsciiDigit(s[i]))
            i++;
        var dot = s.IndexOf('.', i);
        if (dot <= i || dot + 1 >= s.Length || !char.IsAsciiDigit(s[dot + 1]))
            return 0;
        return int.TryParse(s.AsSpan(i, dot - i), NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            ? major * 10 + (s[dot + 1] - '0')
            : 0;
    }
}

/// <summary>GL entry points by name as a context's loader answers them, with the suffixes extensions add: <c>glFoo</c>, then
/// <c>glFooEXT</c>, <c>glFooKHR</c>, <c>glFooOES</c>, <c>glFooANGLE</c>. An ES context reaches the debug, timer and polygon-mode
/// calls the renderer uses through these names (glDebugMessageCallbackKHR, glGetQueryObjecti64vEXT, glPolygonModeANGLE).</summary>
public static class AliasLoader
{
    /// <summary>The suffixes tried after the plain name, in order.</summary>
    public static readonly IReadOnlyList<string> Suffixes = ["EXT", "KHR", "OES", "ANGLE"];

    /// <summary>A loader that answers the first of the name and its suffixed aliases the inner loader resolves; 0 when none does.
    /// Loaders that throw for an unknown name (some do) count as "not resolved".</summary>
    public static Func<string, nint> Wrap(Func<string, nint> getProcAddress)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);
        return name => Resolve(getProcAddress, name, out _);
    }

    /// <summary>Resolves one name; <paramref name="resolvedAs"/> is the alias that answered ("" when none did).</summary>
    public static nint Resolve(Func<string, nint> getProcAddress, string name, out string resolvedAs)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var p = TryOne(getProcAddress, name);
        if (p != 0)
        {
            resolvedAs = name;
            return p;
        }
        foreach (var suffix in Suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var alias = name + suffix;
            p = TryOne(getProcAddress, alias);
            if (p != 0)
            {
                resolvedAs = alias;
                return p;
            }
        }
        resolvedAs = "";
        return 0;
    }

    private static nint TryOne(Func<string, nint> getProcAddress, string name)
    {
        try
        {
            return getProcAddress(name);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or InvalidOperationException or ArgumentException)
        {
            // A loader that throws for a name it does not know: the name is not resolved, the next alias is tried.
            return 0;
        }
    }
}

/// <summary>The per-dialect format table: each texture format's internal format and upload layout on the dialect, the upload
/// layout of each CPU format, and the fallbacks a context's <see cref="GlCaps"/> force (16-bit unsigned without
/// <c>EXT_texture_norm16</c> is half float; a float target without <c>EXT_color_buffer_float</c> is 8-bit). Pure.</summary>
public static class GlFormatTable
{
    /// <summary>A texture format's internal format and upload layout. The enum values are the same on both dialects (the ES
    /// extension names reuse the desktop numbers: R16_EXT = R16, RGBA16_EXT = RGBA16); what differs is whether the context
    /// may use them, which <see cref="Sampled"/> and <see cref="Target"/> answer.</summary>
    public static (GLEnum Internal, GLEnum Format, GLEnum Type) Describe(GlDialect dialect, TextureFormat format) => format switch
    {
        TextureFormat.Rgba8 => (GLEnum.Rgba8, GLEnum.Rgba, GLEnum.UnsignedByte),
        TextureFormat.R8 => (GLEnum.R8, GLEnum.Red, GLEnum.UnsignedByte),
        TextureFormat.Rg8 => (GLEnum.RG8, GLEnum.RG, GLEnum.UnsignedByte),
        TextureFormat.R16 => (GLEnum.R16, GLEnum.Red, GLEnum.UnsignedShort),
        TextureFormat.Rg16 => (GLEnum.RG16, GLEnum.RG, GLEnum.UnsignedShort),
        // ES 3.00 takes half-float data for a 16F texture as HALF_FLOAT, as desktop does.
        TextureFormat.R16F => (GLEnum.R16f, GLEnum.Red, GLEnum.HalfFloat),
        TextureFormat.Rg16F => (GLEnum.RG16f, GLEnum.RG, GLEnum.HalfFloat),
        TextureFormat.Rgba16F => (GLEnum.Rgba16f, GLEnum.Rgba, GLEnum.HalfFloat),
        TextureFormat.R32F => (GLEnum.R32f, GLEnum.Red, GLEnum.Float),
        TextureFormat.Rg32F => (GLEnum.RG32f, GLEnum.RG, GLEnum.Float),
        TextureFormat.Rgba32F => (GLEnum.Rgba32f, GLEnum.Rgba, GLEnum.Float),
        TextureFormat.Rgba16 => (GLEnum.Rgba16, GLEnum.Rgba, GLEnum.UnsignedShort),
        // RGB10_A2 is colour-renderable in ES 3.00 core; its upload type is the reversed 2-10-10-10 on both.
        TextureFormat.Rgb10A2 => (GLEnum.Rgb10A2, GLEnum.Rgba, GLEnum.UnsignedInt2101010Rev),
        TextureFormat.Bc1 => ((GLEnum)0x83F0, GLEnum.Rgba, GLEnum.UnsignedByte),        // COMPRESSED_RGB_S3TC_DXT1_EXT
        TextureFormat.Bc1Alpha => ((GLEnum)0x83F1, GLEnum.Rgba, GLEnum.UnsignedByte),   // COMPRESSED_RGBA_S3TC_DXT1_EXT
        TextureFormat.Bc3 => ((GLEnum)0x83F3, GLEnum.Rgba, GLEnum.UnsignedByte),        // COMPRESSED_RGBA_S3TC_DXT5_EXT
        TextureFormat.Bc4 => ((GLEnum)0x8DBB, GLEnum.Red, GLEnum.UnsignedByte),         // COMPRESSED_RED_RGTC1
        TextureFormat.Bc5 => ((GLEnum)0x8DBD, GLEnum.RG, GLEnum.UnsignedByte),          // COMPRESSED_RG_RGTC2
        TextureFormat.Bc7 => ((GLEnum)0x8E8C, GLEnum.Rgba, GLEnum.UnsignedByte),        // COMPRESSED_RGBA_BPTC_UNORM
        TextureFormat.Bc6hSfloat => ((GLEnum)0x8E8E, GLEnum.Rgb, GLEnum.Float),         // COMPRESSED_RGB_BPTC_SIGNED_FLOAT
        TextureFormat.Bc6hUfloat => ((GLEnum)0x8E8F, GLEnum.Rgb, GLEnum.Float),         // COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, $"a texture format with no description on {GlDialects.Word(dialect)}"),
    };

    /// <summary>A CPU layout's upload format and type, and whether the texture samples it with red and blue swapped. Desktop
    /// uploads BGRA as GL_BGRA; ES 3.00 has no BGRA source format for an RGBA8 texture, so it uploads the bytes as RGBA and the
    /// texture's swizzle (core in ES 3.00) swaps red and blue when sampled — no copy, no extension.</summary>
    public static (GLEnum Format, GLEnum Type, bool SwapRedBlue) Upload(GlDialect dialect, UploadFormat format) => format switch
    {
        UploadFormat.Bgra8 => dialect == GlDialect.Es300 ? (GLEnum.Rgba, GLEnum.UnsignedByte, true) : (GLEnum.Bgra, GLEnum.UnsignedByte, false),
        UploadFormat.R8 => (GLEnum.Red, GLEnum.UnsignedByte, false),
        UploadFormat.Rg8 => (GLEnum.RG, GLEnum.UnsignedByte, false),
        UploadFormat.R16 => (GLEnum.Red, GLEnum.UnsignedShort, false),
        UploadFormat.Rg16 => (GLEnum.RG, GLEnum.UnsignedShort, false),
        UploadFormat.RgbaHalf => (GLEnum.Rgba, GLEnum.HalfFloat, false),
        UploadFormat.R32F => (GLEnum.Red, GLEnum.Float, false),
        UploadFormat.Rg32F => (GLEnum.RG, GLEnum.Float, false),
        UploadFormat.Rgba32F => (GLEnum.Rgba, GLEnum.Float, false),
        UploadFormat.Rgba16 => (GLEnum.Rgba, GLEnum.UnsignedShort, false),
        _ => (GLEnum.Rgba, GLEnum.UnsignedByte, false),
    };

    /// <summary>The format a texture to be sampled is made in on this context: the asked one when the context samples it, else
    /// its fallback (16-bit unsigned → 16-bit float, which holds every 16-bit code within half a code of 10 bits), with the
    /// words that say so; block formats never fall back (their upload path refuses with words instead).</summary>
    public static TextureFormat Sampled(GlCaps caps, TextureFormat format, out string? words)
    {
        ArgumentNullException.ThrowIfNull(caps);
        words = null;
        if (caps.Supports(format) || TextureFormats.IsBlockCompressed(format))
            return format;
        var fallback = format switch
        {
            TextureFormat.R16 => TextureFormat.R16F,
            TextureFormat.Rg16 => TextureFormat.Rg16F,
            TextureFormat.Rgba16 => TextureFormat.Rgba16F,
            _ => format,
        };
        if (fallback != format)
            words = $"{format} is {fallback} here: this {caps.Dialect} context has no EXT_texture_norm16";
        return fallback;
    }

    /// <summary>The format a render target is made in on this context: the asked one when the context renders to it, else its
    /// fallback — 16-bit unsigned → 16-bit float when float targets are renderable, any target the context cannot render → RGBA8 —
    /// with the words that say so.</summary>
    public static TextureFormat Target(GlCaps caps, TextureFormat format, out string? words)
    {
        ArgumentNullException.ThrowIfNull(caps);
        words = null;
        if (caps.SupportsRenderTarget(format))
            return format;
        if (TextureFormats.IsBlockCompressed(format))
            throw new ArgumentException($"{format} is block-compressed and is never a render target.", nameof(format));
        var fallback = format switch
        {
            TextureFormat.R16 when caps.ColorBufferFloat => TextureFormat.R16F,
            TextureFormat.Rg16 when caps.ColorBufferFloat => TextureFormat.Rg16F,
            TextureFormat.Rgba16 when caps.ColorBufferFloat => TextureFormat.Rgba16F,
            TextureFormat.R16F or TextureFormat.R32F or TextureFormat.R16 => TextureFormat.R8,
            TextureFormat.Rg16F or TextureFormat.Rg32F or TextureFormat.Rg16 => TextureFormat.Rg8,
            _ => TextureFormat.Rgba8,
        };
        words = fallback is TextureFormat.Rgba16F or TextureFormat.Rg16F or TextureFormat.R16F
            ? $"{format} targets are {fallback} here: this {caps.Dialect} context has no EXT_texture_norm16"
            : $"{format} targets are {fallback} here: this {caps.Dialect} context renders no float colour (EXT_color_buffer_float is absent)";
        return fallback;
    }
}
