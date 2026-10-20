using System.Numerics;
using PlayMap.Core.Gpu;
using Silk.NET.OpenGL;

namespace PlayMap.Render.Gl;

/// <summary>What the command list and the backend need of the device beyond the frame code's calls: a pass's clear, draws of every
/// <see cref="Topology"/>, a texture bound or unbound by unit, a target with two colour attachments of any formats, a read of either
/// attachment as floats, and a texture copied into another. Each sets the device's cached state as the frame code's calls do.</summary>
public sealed unsafe partial class GlDevice
{
    // The framebuffers a copy reads from and draws into, made on first use and kept for the device's life.
    private uint _copyRead;
    private uint _copyDraw;

    /// <summary>Clears the bound target's colour, and its depth when <paramref name="depth"/> (depth writes are switched on to do it,
    /// as <see cref="ClearWithDepth"/> does; a clear depth other than the far plane is set for this clear only).</summary>
    internal void ClearTarget(Vector4 color, bool depth, float clearDepth)
    {
        if (!depth)
        {
            Clear(color.X, color.Y, color.Z, color.W);
            return;
        }
        if (clearDepth != 1f)
            _gl.ClearDepth(clearDepth);
        ClearWithDepth(color.X, color.Y, color.Z, color.W);
        if (clearDepth != 1f)
            _gl.ClearDepth(1f);
    }

    /// <summary>Binds a texture name (0: none) to a unit, through the device's cache.</summary>
    internal void BindUnit(int unit, uint texture) => BindTextureId(unit, texture);

    /// <summary>Vertices of a vertex array as any topology.</summary>
    internal void Draw(ShaderProgram program, VertexArray vao, Topology topology, int vertexCount, int first)
    {
        ArgumentNullException.ThrowIfNull(vao);
        UseProgram(program);
        BindVertexArrayId(vao.Id);
        _gl.DrawArrays(Primitive(topology), first, (uint)vertexCount);
        Check(program.Name);
        AfterDraw?.Invoke(program);
    }

    /// <summary>Indices of a vertex array as any topology; triangles asked for as a wireframe where the context draws none are not
    /// drawn (<see cref="SetWireframe"/>).</summary>
    internal void DrawIndexed(ShaderProgram program, VertexArray vao, Topology topology, int indexCount)
    {
        ArgumentNullException.ThrowIfNull(vao);
        if (_wireframe == true && !HasPolygonMode && topology == Topology.Triangles)
            return;
        UseProgram(program);
        BindVertexArrayId(vao.Id);
        _gl.DrawElements(Primitive(topology), (uint)indexCount, GLEnum.UnsignedInt, null);
        Check(program.Name);
        AfterDraw?.Invoke(program);
    }

    internal void DrawInstanced(ShaderProgram program, VertexArray vao, Topology topology, int vertexCount, int instances)
    {
        ArgumentNullException.ThrowIfNull(vao);
        UseProgram(program);
        BindVertexArrayId(vao.Id);
        _gl.DrawArraysInstanced(Primitive(topology), 0, (uint)vertexCount, (uint)instances);
        Check(program.Name);
        AfterDraw?.Invoke(program);
    }

    private static GLEnum Primitive(Topology topology) => topology switch
    {
        Topology.TriangleStrip => GLEnum.TriangleStrip,
        Topology.Lines => GLEnum.Lines,
        Topology.LineStrip => GLEnum.LineStrip,
        Topology.Points => GLEnum.Points,
        _ => GLEnum.Triangles,
    };

    /// <summary>A target of one or two colour attachments (the fragment shader's outputs 0 and 1) and an optional depth buffer, every
    /// attachment of the target's size; each format must be one this context renders to.</summary>
    internal GlRenderTarget CreateTarget(in TargetDesc desc)
    {
        if (desc.Color1 is not { } format1)
            return (GlRenderTarget)CreateRenderTarget(desc.Width, desc.Height, desc.Color0, desc.Depth, desc.Filter);
        AssertOwner();
        foreach (var f in (ReadOnlySpan<TextureFormat>)[desc.Color0, format1])
        {
            if (!Caps.SupportsRenderTarget(f))
                throw new ArgumentException($"This context cannot render to {f} ({Caps.Dialect}).", nameof(desc));
        }
        var a = CreateTexture(desc.Width, desc.Height, desc.Color0, desc.Filter, TextureWrap.Clamp, AttachmentUsage);
        GpuTexture b;
        try
        {
            b = CreateTexture(desc.Width, desc.Height, format1, desc.Filter, TextureWrap.Clamp, AttachmentUsage);
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
        uint rb = 0;
        if (desc.Depth)
        {
            rb = _gl.GenRenderbuffer();
            _liveRenderbuffers.Add(rb);
            _gl.BindRenderbuffer(GLEnum.Renderbuffer, rb);
            _gl.RenderbufferStorage(GLEnum.Renderbuffer, GLEnum.DepthComponent24, (uint)desc.Width, (uint)desc.Height);
            _gl.FramebufferRenderbuffer(GLEnum.Framebuffer, GLEnum.DepthAttachment, GLEnum.Renderbuffer, rb);
            _gl.BindRenderbuffer(GLEnum.Renderbuffer, 0);
        }
        var target = new GlRenderTarget(this, fbo, a, b, rb, desc.Name ?? "");
        var status = _gl.CheckFramebufferStatus(GLEnum.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            target.Dispose();
            throw new GlException($"A {desc.Width}×{desc.Height} {desc.Color0} + {format1} two-attachment target is incomplete: {status}.");
        }
        Check("target");
        return target;
    }

    /// <summary>Reads one colour attachment of a target as floats (RGBA, top row first): attachment 0 as
    /// <see cref="ReadPixelsFloat"/> reads it, attachment 1 of a two-attachment target as a float read of that buffer.</summary>
    internal void ReadAttachmentFloat(RenderTarget target, int attachment, Span<float> rgba)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (attachment == 0)
        {
            ReadPixelsFloat(target, rgba);
            return;
        }
        if (attachment != 1 || target.Color1 is null)
            throw new ArgumentOutOfRangeException(nameof(attachment), attachment, "the target has no such colour attachment");
        if (rgba.Length < target.Width * target.Height * 4)
            throw new ArgumentException("The buffer is smaller than the target.", nameof(rgba));
        BindTarget(target);
        _gl.ReadBuffer(GLEnum.ColorAttachment1);
        fixed (float* p = rgba)
            _gl.ReadPixels(0, 0, (uint)target.Width, (uint)target.Height, GLEnum.Rgba, GLEnum.Float, p);
        _gl.ReadBuffer(GLEnum.ColorAttachment0);
        Check("read attachment");
    }

    /// <summary>Copies the whole of one texture into another of the same size, texel for texel (a framebuffer blit, nearest). A block
    /// format is copied by neither: it is filled only through the compressed upload path.</summary>
    internal void CopyTexture(GpuTexture source, GpuTexture dest)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dest);
        AssertOwner();
        if (source.Width != dest.Width || source.Height != dest.Height)
            throw new ArgumentException($"A copy is between textures of one size: {source.Width}×{source.Height} into {dest.Width}×{dest.Height}.", nameof(dest));
        if (TextureFormats.IsBlockCompressed(source.Format) || TextureFormats.IsBlockCompressed(dest.Format))
            throw new ArgumentException("A block-compressed texture is filled only through the compressed upload path, never copied.", nameof(source));
        if (_copyRead == 0)
        {
            // The device's own, outside the census of targets; deleted with the device.
            _copyRead = _gl.GenFramebuffer();
            _copyDraw = _gl.GenFramebuffer();
        }
        _gl.BindFramebuffer(GLEnum.ReadFramebuffer, _copyRead);
        _gl.FramebufferTexture2D(GLEnum.ReadFramebuffer, GLEnum.ColorAttachment0, GLEnum.Texture2D, source.Id, 0);
        _gl.BindFramebuffer(GLEnum.DrawFramebuffer, _copyDraw);
        _gl.FramebufferTexture2D(GLEnum.DrawFramebuffer, GLEnum.ColorAttachment0, GLEnum.Texture2D, dest.Id, 0);
        _gl.BlitFramebuffer(0, 0, source.Width, source.Height, 0, 0, dest.Width, dest.Height, (uint)GLEnum.ColorBufferBit, GLEnum.Nearest);
        // Both attachments are let go, so a texture deleted later is never held by the copy's framebuffers.
        _gl.FramebufferTexture2D(GLEnum.ReadFramebuffer, GLEnum.ColorAttachment0, GLEnum.Texture2D, 0, 0);
        _gl.FramebufferTexture2D(GLEnum.DrawFramebuffer, GLEnum.ColorAttachment0, GLEnum.Texture2D, 0, 0);
        _gl.BindFramebuffer(GLEnum.Framebuffer, 0);
        _framebuffer = uint.MaxValue;
        dest.Version++;
        Check("copy");
    }

    /// <summary>A buffer's store made at <paramref name="bytes"/> before anything is set into it (the store still grows to fit a
    /// larger set).</summary>
    internal void ReserveBuffer(GlBuffer buffer, long bytes)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        AssertOwner();
        if (bytes <= 0)
            return;
        var target = Target(buffer.Kind);
        if (buffer.Kind == BufferKind.Index)
            BindVertexArrayId(0);
        _gl.BindBuffer(target, buffer.Id);
        _gl.BufferData(target, (nuint)bytes, null, (buffer.Usage & BufferUsage.Dynamic) != 0 ? GLEnum.StreamDraw : GLEnum.StaticDraw);
        if (buffer.Kind == BufferKind.Vertex)
            _gl.BindBuffer(target, 0);
        buffer.Capacity = (int)Math.Min(bytes, int.MaxValue);
        buffer.SetContent(buffer.Capacity);
        Check("buffer");
    }
}
