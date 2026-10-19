using System.Globalization;
using System.Runtime.InteropServices;
using PlayMap.Core.Gpu;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Encode;

/// <summary>The HAP encoder on a GL context: the picture is uploaded as an RGBA8 texture, each plane's program (the
/// <c>hapenc.*</c> programs) draws one texel per 4×4 block into an RGBA32UI target in scissored tiles of at most
/// <see cref="TileBlocks"/> × <see cref="TileBlocks"/> blocks (each flushed, so no submission grows with the picture), and the
/// target is read back as the block bytes. HAP, HAP Alpha, HAP Q and HAP Q Alpha at Fast (one least-squares pass) and Show (two,
/// and an endpoint search); Archive is refused with words. Runs on any GL 3.3 core or ES 3.0 context, llvmpipe included.
///
/// Threading: made, used and disposed on the thread whose context it was made on. The device's cached state is invalidated after
/// each encode (the target and its framebuffer are bound by raw calls).</summary>
public sealed unsafe class GlHapEncoder : IHapBlockEncoder
{
    /// <summary>The largest tile, in blocks per side, one draw covers.</summary>
    public const int TileBlocks = 256;

    private readonly RenderContext _context;
    private readonly GL _gl;
    private readonly GlDevice _device;
    private GpuTexture? _source;
    private uint _target;
    private uint _framebuffer;
    private int _targetW;
    private int _targetH;
    private uint[] _words = [];
    private bool _disposed;

    public GlHapEncoder(RenderContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _gl = context.Gl;
        _device = context.Device;
        Id = string.Create(CultureInfo.InvariantCulture, $"gl-frag/1 · {_device.Renderer}");
    }

    public string Id { get; }

    public bool Supports(HapCodec codec, EncodeQuality quality, out string why)
    {
        if (codec is not (HapCodec.Hap or HapCodec.HapAlpha or HapCodec.HapQ or HapCodec.HapQAlpha))
        {
            why = $"{HapCodecs.Words(codec)} has no encoder on this node yet";
            return false;
        }
        if (quality == EncodeQuality.Archive)
        {
            why = "Archive quality (an exhaustive endpoint search) arrives next round: use QUALITY SHOW";
            return false;
        }
        why = "";
        return true;
    }

    public void Encode(ReadOnlySpan<byte> rgba, int width, int height, HapCodec codec, EncodeQuality quality, HapBlocks into)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(into);
        if (!Supports(codec, quality, out var why))
            throw new NotSupportedException(why);
        if (rgba.Length < width * height * 4)
            throw new ArgumentException($"A {width}×{height} RGBA picture is {width * height * 4} bytes; {rgba.Length} given.", nameof(rgba));
        if (width > _device.MaxTextureSize || height > _device.MaxTextureSize)
            throw new NotSupportedException($"a {width}×{height} picture is larger than this GPU's {_device.MaxTextureSize} pixels");
        into.Reset(codec, width, height);
        if (_source is null || _source.Width != width || _source.Height != height)
        {
            _source?.Dispose();
            _source = _device.CreateTexture(width, height, TextureFormat.Rgba8, TextureFilter.Nearest);
        }
        _device.Upload(_source, rgba, UploadFormat.Rgba8);
        EnsureTarget(into.BlocksWide, into.BlocksHigh);
        var refine = quality == EncodeQuality.Fast ? 1 : 2;
        try
        {
            switch (codec)
            {
                case HapCodec.Hap:
                    Run("hapenc.bc1", width, height, refine, into, 0);
                    break;
                case HapCodec.HapAlpha:
                    Run("hapenc.bc3", width, height, refine, into, 0);
                    break;
                case HapCodec.HapQ:
                    Run("hapenc.ycocg", width, height, refine, into, 0);
                    break;
                default:
                    Run("hapenc.ycocg", width, height, refine, into, 0);
                    Run("hapenc.bc4", width, height, refine, into, 1);
                    break;
            }
        }
        finally
        {
            _gl.Disable(EnableCap.ScissorTest);
            _device.InvalidateState();
        }
        var errors = _device.TakeErrors();
        if (errors.Count > 0)
            throw new GlException("the HAP encoder: " + string.Join("; ", errors));
    }

    private void EnsureTarget(int blocksW, int blocksH)
    {
        if (_target != 0 && _targetW == blocksW && _targetH == blocksH)
            return;
        DeleteTarget();
        _target = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _target);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32ui, (uint)blocksW, (uint)blocksH, 0, PixelFormat.RgbaInteger, PixelType.UnsignedInt, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _framebuffer = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _target, 0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _device.InvalidateState();
        if (status != GLEnum.FramebufferComplete)
        {
            DeleteTarget();
            throw new GlException($"the HAP encoder's RGBA32UI target is incomplete on this GL ({status})");
        }
        _targetW = blocksW;
        _targetH = blocksH;
        _words = new uint[blocksW * blocksH * 4];
    }

    private void Run(string name, int width, int height, int refine, HapBlocks into, int plane)
    {
        var program = _device.Program(name);
        _device.UseProgram(program);
        _device.BindSampler(program, "uSrc", 0, _source!);
        var loc = program.Location("uSize");
        _gl.Uniform2(loc, width, height);
        program.SetInt("uRefine", refine);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.Viewport(0, 0, (uint)_targetW, (uint)_targetH);
        _gl.Enable(EnableCap.ScissorTest);
        for (var y = 0; y < _targetH; y += TileBlocks)
        {
            for (var x = 0; x < _targetW; x += TileBlocks)
            {
                _gl.Scissor(x, y, (uint)Math.Min(TileBlocks, _targetW - x), (uint)Math.Min(TileBlocks, _targetH - y));
                _device.DrawFullscreen(program);
                _gl.Flush();
            }
        }
        _gl.Disable(EnableCap.ScissorTest);
        fixed (uint* w = _words)
            _gl.ReadPixels(0, 0, (uint)_targetW, (uint)_targetH, PixelFormat.RgbaInteger, PixelType.UnsignedInt, w);
        var bytes = MemoryMarshal.AsBytes(_words.AsSpan());
        var dst = into.Plane(plane);
        var blockBytes = into.BlockBytes(plane);
        if (blockBytes == 16)
        {
            bytes[..dst.Length].CopyTo(dst);
            return;
        }
        var blocks = _targetW * _targetH;
        for (var i = 0; i < blocks; i++)
            bytes.Slice(i * 16, 8).CopyTo(dst.Slice(i * 8, 8));
    }

    private void DeleteTarget()
    {
        if (_framebuffer != 0)
            _gl.DeleteFramebuffer(_framebuffer);
        if (_target != 0)
            _gl.DeleteTexture(_target);
        _framebuffer = 0;
        _target = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _context.MakeCurrent();
        DeleteTarget();
        _source?.Dispose();
        _device.InvalidateState();
    }
}
