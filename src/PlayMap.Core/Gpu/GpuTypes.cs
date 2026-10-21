namespace PlayMap.Core.Gpu;

/// <summary>How a texture stores its texels on the GPU. The 16-bit float formats are the pipeline's
/// intermediates (canvases, the projector pass, converted video): 8-bit in, float through, 8-bit out.</summary>
public enum TextureFormat
{
    Rgba8,
    R8,
    Rg8,
    /// <summary>16-bit unsigned normalised (10-bit video in a 16-bit container).</summary>
    R16,
    Rg16,
    R16F,
    Rg16F,
    Rgba16F,
    R32F,
    Rg32F,

    /// <summary>Four 32-bit float channels (particle state, accumulation).</summary>
    Rgba32F,

    /// <summary>Four 16-bit unsigned normalised channels (P216/PA16 unpack, deep sends).</summary>
    Rgba16,

    /// <summary>10-bit colour, 2-bit alpha (the 10-bit output picture).</summary>
    Rgb10A2,

    // Block-compressed (4×4 blocks; sampled only, never a render target). Present only where the device says so (its features);
    // an absent format refuses with words, it never falls back silently.
    Bc1,
    Bc1Alpha,
    Bc3,
    Bc4,
    Bc5,
    Bc7,
    Bc6hUfloat,
    Bc6hSfloat,
}

/// <summary>The layout of CPU pixels handed to an upload.</summary>
public enum UploadFormat
{
    Rgba8,
    Bgra8,
    R8,
    Rg8,
    /// <summary>16-bit unsigned, one channel.</summary>
    R16,
    /// <summary>16-bit unsigned, two channels.</summary>
    Rg16,
    /// <summary>Half floats, four channels.</summary>
    RgbaHalf,
    /// <summary>32-bit floats, one channel.</summary>
    R32F,
    /// <summary>32-bit floats, two channels.</summary>
    Rg32F,
    /// <summary>32-bit floats, four channels.</summary>
    Rgba32F,

    /// <summary>16-bit unsigned, four channels.</summary>
    Rgba16,
}

public enum TextureFilter
{
    Linear,
    Nearest,
}

public enum TextureWrap
{
    Clamp,
    Repeat,
}

/// <summary>How a draw combines with what is already in the target. Premultiplied sources use
/// <see cref="Premultiplied"/>; the canvas compositor's modes are <see cref="Normal"/> (straight alpha),
/// <see cref="Add"/>, <see cref="Multiply"/> and <see cref="Screen"/> (the shader shapes its output for each).
/// Every blended mode leaves the target's alpha as it was; <see cref="Opaque"/> writes all four channels (a converted picture's
/// alpha included).</summary>
public enum BlendMode
{
    Opaque,
    Normal,
    Premultiplied,
    Add,
    /// <summary>Premultiplied additive (glow).</summary>
    AddPremultiplied,
    Multiply,
    Screen,
}

public enum CullMode
{
    None,
    /// <summary>Cull triangles that are clockwise in the target's raster seen top row first (<see cref="ClipSpace"/>).</summary>
    Back,
}

/// <summary>The render hardware interface's clip space, which every vertex stage writes and every backend rasterises alike. Clip
/// y = −w lands on the target's row 0 (the image's top row, as a texture's v = 0 is its top row), y = +w on its last; clip x = −w on
/// column 0; clip z from −w to +w maps to depth 0 to 1 (a depth test passes less or equal, against a target cleared to 1);
/// <see cref="CullMode.Back"/> culls the triangles that are clockwise in the raster seen top row first. These are OpenGL's own rules
/// with its framebuffer's row 0 the image's top row: the GLSL library and the GL backend need no conversion. A backend whose
/// rasteriser differs converts as the last step of every vertex stage: Direct3D 12 (clip y = +w its top row, depth z/w from 0) writes
/// <see cref="ToDirect3D"/>'s position, and its pipelines cull with <c>FrontCounterClockwise = TRUE</c> and <c>CULL_MODE_BACK</c>, so
/// the same pixels, depths and culled triangles come out.</summary>
public static class ClipSpace
{
    /// <summary>A clip position as Direct3D 12 rasterises it to the same pixel and depth: y negated, z from [−w, w] to [0, w].</summary>
    public static System.Numerics.Vector4 ToDirect3D(System.Numerics.Vector4 clip) =>
        new(clip.X, -clip.Y, (clip.Z + clip.W) * 0.5f, clip.W);

    /// <summary>The target row (0 = the top row, at its upper edge) a clip y lands on, for a target <paramref name="height"/> rows high.</summary>
    public static float Row(float clipY, float w, int height) => (clipY / w + 1) * 0.5f * height;

    /// <summary>The depth (0 to 1) a clip z writes.</summary>
    public static float Depth(float clipZ, float w) => (clipZ / w + 1) * 0.5f;
}

/// <summary>A draw's depth: off, tested and written (less or equal, the far plane cleared to 1), or tested only.</summary>
public enum DepthMode
{
    Off,
    TestWrite,
    TestOnly,
}

/// <summary>How a draw's vertices make primitives.</summary>
public enum Topology
{
    Triangles,
    TriangleStrip,
    Lines,
    LineStrip,
    Points,
}

/// <summary>One vertex attribute: the shader location, how many floats, where it sits in its buffer, which
/// buffer (0 = the vertex buffer, 1 = the instance buffer) and the instancing divisor.</summary>
public readonly record struct VertexAttribute(int Location, int Components, int StrideBytes, int OffsetBytes, int Buffer = 0, int Divisor = 0);

/// <summary>A 3×3 matrix for normals, row-major (row r, column c at index 3r + c). Set into a constant it is a column-vector transform:
/// the shader computes M·v (<see cref="ConstantType.Float3x3"/>).</summary>
public readonly record struct Matrix3x3(float M00, float M01, float M02, float M10, float M11, float M12, float M20, float M21, float M22)
{
    public static readonly Matrix3x3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);
}

/// <summary>What a texture format is, named per format (never by enum order: formats are appended, and a later uncompressed one must
/// not read as a block format).</summary>
public static class TextureFormats
{
    public static bool IsBlockCompressed(TextureFormat f) => f is TextureFormat.Bc1 or TextureFormat.Bc1Alpha or TextureFormat.Bc3
        or TextureFormat.Bc4 or TextureFormat.Bc5 or TextureFormat.Bc7 or TextureFormat.Bc6hUfloat or TextureFormat.Bc6hSfloat;

    /// <summary>Bytes per 4×4 block (8 for BC1 and BC4, 16 for BC3, BC5, BC7 and BC6H); 0 for an uncompressed format.</summary>
    public static int BlockBytes(TextureFormat f) => f switch
    {
        TextureFormat.Bc1 or TextureFormat.Bc1Alpha or TextureFormat.Bc4 => 8,
        TextureFormat.Bc3 or TextureFormat.Bc5 or TextureFormat.Bc7 or TextureFormat.Bc6hUfloat or TextureFormat.Bc6hSfloat => 16,
        _ => 0,
    };

    /// <summary>Bytes per texel of an uncompressed format (1 for R8 up to 16 for RGBA32F); 0 for a block-compressed one.</summary>
    public static int TexelBytes(TextureFormat f) => f switch
    {
        TextureFormat.R8 => 1,
        TextureFormat.Rg8 or TextureFormat.R16 or TextureFormat.R16F => 2,
        TextureFormat.Rgba8 or TextureFormat.Rg16 or TextureFormat.Rg16F or TextureFormat.R32F or TextureFormat.Rgb10A2 => 4,
        TextureFormat.Rgba16F or TextureFormat.Rg32F or TextureFormat.Rgba16 => 8,
        TextureFormat.Rgba32F => 16,
        _ => 0,
    };

    /// <summary>Bytes of one level of a texture: texels times their bytes, or a block format's whole blocks.</summary>
    public static long LevelBytes(TextureFormat f, int width, int height) =>
        IsBlockCompressed(f) ? CompressedSize(f, width, height) : (long)width * height * TexelBytes(f);

    /// <summary>Bytes of one level of a block-compressed texture (sizes round up to whole blocks).</summary>
    public static long CompressedSize(TextureFormat f, int width, int height) =>
        (long)((width + 3) / 4) * ((height + 3) / 4) * BlockBytes(f);
}

/// <summary>What a CPU pixel layout is.</summary>
public static class UploadFormats
{
    /// <summary>Bytes one pixel of the layout takes (1 for R8 up to 16 for four 32-bit floats).</summary>
    public static int BytesPerPixel(UploadFormat format) => format switch
    {
        UploadFormat.R8 => 1,
        UploadFormat.Rg8 => 2,
        UploadFormat.R16 => 2,
        UploadFormat.Rg16 => 4,
        UploadFormat.RgbaHalf => 8,
        UploadFormat.R32F => 4,
        UploadFormat.Rg32F => 8,
        UploadFormat.Rgba32F => 16,
        UploadFormat.Rgba16 => 8,
        _ => 4,
    };
}
