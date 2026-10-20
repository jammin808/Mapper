using PlayMap.Core.Gpu;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Gl;

/// <summary>What content needs of the device beyond one-colour targets: a target with two colour attachments drawn in one pass
/// (a particle field's position and velocity textures, written together by one step).</summary>
public sealed unsafe partial class GlDevice
{
    /// <summary>A target whose fragment shader writes two textures of one format (layout location 0 and 1). The format must be one
    /// this context renders to (RGBA32F needs EXT_color_buffer_float on ES).</summary>
    public StateTarget CreateStateTarget(int width, int height, TextureFormat format)
    {
        AssertOwner();
        if (!Caps.SupportsRenderTarget(format))
            throw new ArgumentException($"This context cannot render to {format} ({Caps.Dialect}).", nameof(format));
        var a = CreateTexture(width, height, format, TextureFilter.Nearest, TextureWrap.Clamp, AttachmentUsage);
        GpuTexture b;
        try
        {
            b = CreateTexture(width, height, format, TextureFilter.Nearest, TextureWrap.Clamp, AttachmentUsage);
        }
        catch
        {
            a.Dispose();
            throw;
        }
        var fbo = _gl.GenFramebuffer();
        _liveFramebuffers.Add(fbo);
        _gl.BindFramebuffer(GLEnum.Framebuffer, fbo);
        _framebuffer = fbo;
        _gl.FramebufferTexture2D(GLEnum.Framebuffer, GLEnum.ColorAttachment0, GLEnum.Texture2D, a.Id, 0);
        _gl.FramebufferTexture2D(GLEnum.Framebuffer, GLEnum.ColorAttachment1, GLEnum.Texture2D, b.Id, 0);
        GLEnum* buffers = stackalloc GLEnum[2] { GLEnum.ColorAttachment0, GLEnum.ColorAttachment1 };
        _gl.DrawBuffers(2, buffers);
        var status = _gl.CheckFramebufferStatus(GLEnum.Framebuffer);
        var target = new StateTarget(this, fbo, a, b);
        if (status != GLEnum.FramebufferComplete)
        {
            target.Dispose();
            throw new GlException($"A {width}×{height} {format} two-attachment target is incomplete: {status}.");
        }
        Check("state target");
        return target;
    }

    /// <summary>Draw into both attachments of a state target over its whole raster.</summary>
    public void BindStateTarget(StateTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        AssertOwner();
        if (_framebuffer != target.Framebuffer)
        {
            _gl.BindFramebuffer(GLEnum.Framebuffer, target.Framebuffer);
            _framebuffer = target.Framebuffer;
        }
        SetViewport(0, 0, target.Width, target.Height);
    }

    /// <summary>Reads one attachment of a state target back as floats (RGBA, bottom row first) — the tests' bit-exact comparisons.</summary>
    public void ReadStateFloat(StateTarget target, int attachment, Span<float> rgba)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (rgba.Length < target.Width * target.Height * 4)
            throw new ArgumentException("The buffer is smaller than the target.", nameof(rgba));
        BindStateTarget(target);
        _gl.ReadBuffer(attachment == 0 ? GLEnum.ColorAttachment0 : GLEnum.ColorAttachment1);
        fixed (float* p = rgba)
            _gl.ReadPixels(0, 0, (uint)target.Width, (uint)target.Height, GLEnum.Rgba, GLEnum.Float, p);
        _gl.ReadBuffer(GLEnum.ColorAttachment0);
        Check("read state");
    }

    internal void DeleteStateTarget(uint framebuffer)
    {
        if (_disposed)
            return;
        AssertOwner();
        if (_liveFramebuffers.Remove(framebuffer))
        {
            if (_framebuffer == framebuffer)
                _framebuffer = uint.MaxValue;
            _gl.DeleteFramebuffer(framebuffer);
        }
    }
}

/// <summary>Two textures of one size and format behind one framebuffer, written together by one draw.</summary>
public sealed class StateTarget : IDisposable
{
    private readonly GlDevice _device;

    internal StateTarget(GlDevice device, uint framebuffer, GpuTexture a, GpuTexture b)
    {
        _device = device;
        Framebuffer = framebuffer;
        A = a;
        B = b;
    }

    internal uint Framebuffer { get; private set; }

    /// <summary>Attachment 0.</summary>
    public GpuTexture A { get; }

    /// <summary>Attachment 1.</summary>
    public GpuTexture B { get; }

    public int Width => A.Width;
    public int Height => A.Height;

    public void Dispose()
    {
        if (Framebuffer == 0)
            return;
        _device.DeleteStateTarget(Framebuffer);
        Framebuffer = 0;
        A.Dispose();
        B.Dispose();
    }
}
