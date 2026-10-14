using PlayMap.Core.Media;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Gpu;

/// <summary>The compressed upload path (HAP): block-compressed textures are made at a whole number of 4×4 blocks and filled
/// with tight rows of blocks, from client memory or from a pixel-unpack buffer. What can be sampled comes from <see cref="Caps"/>
/// (the context's version and extensions), never from a driver's compressed-format list; a format the context lacks is
/// refused with words, never uploaded into a texture that would sample black.</summary>
public sealed unsafe partial class GlDevice
{
    /// <summary>The block families this context samples (from <see cref="Caps"/>): BC1 with DXT1, BC3 with DXT5, BC4 with RGTC,
    /// BC7 and BC6H with BPTC. No persistent staging: frames are copied into the driver as they are uploaded.</summary>
    public BlockFormats BlockFormats =>
        (Caps.Dxt1 ? BlockFormats.Bc1 : 0) | (Caps.Dxt5 ? BlockFormats.Bc3 : 0) | (Caps.Rgtc ? BlockFormats.Bc4 : 0)
        | (Caps.Bptc ? BlockFormats.Bc7 | BlockFormats.Bc6h : 0);

    /// <summary>The words that refuse a block format this context cannot sample (null when it can).</summary>
    public string? RefuseBlockFormat(TextureFormat format) => RefuseBlockFormat(Caps, format);

    /// <summary>The words that refuse a block format a context with <paramref name="caps"/> cannot sample (null when it can).</summary>
    public static string? RefuseBlockFormat(GlCaps caps, TextureFormat format)
    {
        ArgumentNullException.ThrowIfNull(caps);
        if (!TextureFormats.IsBlockCompressed(format))
            return $"{format} is not a block-compressed format";
        if (caps.Supports(format))
            return null;
        var family = format switch
        {
            TextureFormat.Bc1 or TextureFormat.Bc1Alpha => "S3TC (DXT1)",
            TextureFormat.Bc3 => "S3TC (DXT5)",
            TextureFormat.Bc4 or TextureFormat.Bc5 => "RGTC",
            _ => "BPTC",
        };
        return $"this GPU's GL ({caps.Dialect}) does not offer {family} textures, which {format} needs";
    }

    /// <summary>A block-compressed texture for a picture of <paramref name="width"/>×<paramref name="height"/> pixels; its size
    /// rounds up to whole blocks (the coded size). Sampled with nearest filtering (the `hap` pass reads exact texels).</summary>
    public GpuTexture CreateCompressedTexture(int width, int height, TextureFormat format)
    {
        AssertOwner();
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (RefuseBlockFormat(format) is { } why)
            throw new NotSupportedException(why);
        var w = (width + 3) / 4 * 4;
        var h = (height + 3) / 4 * 4;
        if (w > MaxTextureSize || h > MaxTextureSize)
            throw new GlException($"A {w}×{h} texture is larger than this GPU's {MaxTextureSize}.");
        var id = _gl.GenTexture();
        _liveTextures.Add(id);
        BindTextureId(0, id);
        var (internalFormat, _, _) = Describe(format);
        var bytes = (uint)TextureFormats.CompressedSize(format, w, h);
        _gl.CompressedTexImage2D(GLEnum.Texture2D, 0, internalFormat, (uint)w, (uint)h, 0, bytes, null);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMaxLevel, 0);
        Check("compressed texture");
        return new GpuTexture(this, id, w, h, format, TextureFilter.Nearest, TextureWrap.Clamp);
    }

    /// <summary>Fills a whole compressed texture from client memory: <paramref name="bytes"/> of tight rows of blocks, top row
    /// first.</summary>
    public void UploadCompressed(GpuTexture texture, nint blocks, int bytes)
    {
        ArgumentNullException.ThrowIfNull(texture);
        AssertOwner();
        CheckCompressedSize(texture, bytes);
        BindTextureId(0, texture.Id);
        var (internalFormat, _, _) = Describe(texture.Format);
        _gl.CompressedTexSubImage2D(GLEnum.Texture2D, 0, 0, 0, (uint)texture.Width, (uint)texture.Height, internalFormat, (uint)bytes, (void*)blocks);
        texture.Version++;
        Check("compressed upload");
    }

    /// <summary>Fills a whole compressed texture from a pixel-unpack buffer at a byte offset (the streaming path).</summary>
    internal void UploadCompressedFromUnpackBuffer(GpuTexture texture, uint buffer, nint offset, int bytes)
    {
        AssertOwner();
        CheckCompressedSize(texture, bytes);
        BindTextureId(0, texture.Id);
        var (internalFormat, _, _) = Describe(texture.Format);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, buffer);
        _gl.CompressedTexSubImage2D(GLEnum.Texture2D, 0, 0, 0, (uint)texture.Width, (uint)texture.Height, internalFormat, (uint)bytes, (void*)offset);
        _gl.BindBuffer(GLEnum.PixelUnpackBuffer, 0);
        texture.Version++;
        Check("compressed upload pbo");
    }

    private static void CheckCompressedSize(GpuTexture texture, int bytes)
    {
        var need = TextureFormats.CompressedSize(texture.Format, texture.Width, texture.Height);
        if (need == 0)
            throw new ArgumentException($"{texture.Format} is not block-compressed: it uploads through the plain path.", nameof(texture));
        if (bytes != need)
            throw new ArgumentException($"A {texture.Width}×{texture.Height} {texture.Format} texture takes {need} bytes; {bytes} given.", nameof(bytes));
    }
}
