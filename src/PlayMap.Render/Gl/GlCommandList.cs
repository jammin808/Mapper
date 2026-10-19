using PlayMap.Core.Gpu;

namespace PlayMap.Render.Gl;

/// <summary>The GL backend's frame list: immediate. Every call runs on the device's context as it is made, in order, so what a list
/// "records" has been issued by the time <see cref="GlBackend.Submit"/> signals its fence. A pass binds its target and sets its
/// viewport (the whole target, or the pass's rectangle); its load op clears the whole target, depth included where it has one
/// (GL's clear ignores the viewport). A draw applies its state through the device's cache (blend, depth, cull, polygon mode only
/// where they differ), uses its program, sets every constant of the program's layout from the draw's block (an unset one is zero)
/// and binds each texture to the unit of its slot. Barriers, waits, aliasing and discards are nothing on GL: one context orders its
/// own work, and transients are pooled, never aliased.
///
/// Threading: the render thread, between <see cref="GlBackend.BeginFrame"/> and <see cref="GlBackend.Submit"/>. Allocation-free
/// once every pipeline it draws is made.</summary>
internal sealed class GlCommandList : ICommandList, IDrawSink
{
    private const int TextureSlots = 16;

    private readonly GlBackend _backend;
    private readonly GlDevice _device;
    private readonly GpuTexture?[] _textures = new GpuTexture?[TextureSlots];
    private byte[] _constants;
    private RenderTarget? _target;

    public GlCommandList(GlBackend backend, GlDevice device, int constantBytes)
    {
        _backend = backend;
        _device = device;
        _constants = new byte[Math.Max(256, constantBytes)];
    }

    public QueueKind Queue => QueueKind.Direct;

    /// <summary>Between the frame's begin and its submit.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The frame the list records (from <see cref="GlBackend.BeginFrame"/>).</summary>
    public long Frame { get; private set; }

    /// <summary>Inside a pass.</summary>
    public bool InPass => _target is not null;

    internal void Open(long frame)
    {
        Frame = frame;
        IsOpen = true;
    }

    internal void Close()
    {
        if (!IsOpen)
            throw new InvalidOperationException("This list is submitted once, after its frame began.");
        if (_target is not null)
            throw new InvalidOperationException("A pass is still open: every BeginPass ends with EndPass before the list is submitted.");
        IsOpen = false;
    }

    /// <summary>Grows the constant block for a user program's layout (render thread, between frames).</summary>
    internal void EnsureConstants(int bytes)
    {
        if (bytes > _constants.Length)
            Array.Resize(ref _constants, bytes);
    }

    public void BeginPass(RenderTarget target, in PassDesc pass)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireOpen();
        if (_target is not null)
            throw new InvalidOperationException("A pass is open: EndPass comes before the next BeginPass.");
        _device.BindTarget(target);
        if (pass.Width > 0 && pass.Height > 0)
            _device.SetViewport(pass.X, pass.Y, pass.Width, pass.Height);
        if (pass.Load == LoadOp.Clear)
            _device.ClearTarget(pass.Clear, target.HasDepth, pass.ClearDepth);
        _target = target;
    }

    public void EndPass()
    {
        if (_target is null)
            throw new InvalidOperationException("No pass is open.");
        _target = null;
    }

    public DrawBuilder Draw(ProgramId program, in DrawState state)
    {
        RequireOpen();
        if (_target is null)
            throw new InvalidOperationException("A draw is inside a pass: BeginPass names the target it draws into.");
        var layout = _backend.ProgramTable.Layout(program);
        var constants = _constants.AsSpan(0, layout.ConstantBytes);
        constants.Clear();
        var textures = _textures.AsSpan(0, layout.Textures.Length);
        textures.Clear();
        return new DrawBuilder(this, program, state, layout, constants, textures);
    }

    public void Record(in DrawCall call, ReadOnlySpan<byte> constants, ReadOnlySpan<GpuTexture?> textures)
    {
        RequireOpen();
        var target = _target ?? throw new InvalidOperationException("A draw is inside a pass: BeginPass names the target it draws into.");
        var pipeline = _backend.PipelineCache.Resolve(PipelineKey.For(call.Program, call.State, target));
        var program = pipeline.Program;
        var state = call.State;
        _device.SetBlend(state.Blend);
        _device.SetDepth(state.Depth != DepthMode.Off, state.Depth == DepthMode.TestWrite);
        _device.SetCull(state.Cull);
        _device.SetWireframe(state.Wireframe);
        _device.UseProgram(program.Program);
        program.Apply(_device, constants, textures);
        program.LastUse = _backend.FrameValue;
        switch (call.Shape)
        {
            case DrawShape.Fullscreen:
                _device.DrawFullscreen(program.Program);
                break;
            case DrawShape.GeneratedQuads:
                _device.DrawGeneratedQuads(program.Program, call.Instances);
                break;
            case DrawShape.Vertices:
                _device.Draw(program.Program, Mesh(call), state.Topology, call.Count, call.First);
                break;
            case DrawShape.Indexed:
                _device.DrawIndexed(program.Program, Mesh(call), state.Topology, call.Count);
                break;
            default:
                _device.DrawInstanced(program.Program, Mesh(call), state.Topology, call.Count, call.Instances);
                break;
        }
    }

    private static VertexArray Mesh(in DrawCall call) =>
        call.Mesh as VertexArray ?? throw new ArgumentException("A GL draw reads a mesh the GL device made.", nameof(call));

    public void Copy(GpuTexture source, GpuTexture dest)
    {
        RequireOutsidePass("A copy");
        _device.CopyTexture(source, dest);
    }

    public void Upload(GpuTexture dest, ReadOnlySpan<byte> pixels, UploadFormat format, int rowPixels = 0)
    {
        RequireOpen();
        _device.Upload(dest, pixels, format, rowPixels);
    }

    public void UploadCompressed(GpuTexture dest, nint blocks, int bytes)
    {
        RequireOpen();
        _device.UploadCompressed(dest, blocks, bytes);
    }

    /// <summary>Nothing on GL: the context orders reads after the writes before them.</summary>
    public void Barrier(GpuResource resource, ResourceUse use) => ArgumentNullException.ThrowIfNull(resource);

    /// <summary>Nothing on GL: one context, one queue in order.</summary>
    public void Wait(in FenceValue value)
    {
        // Every value of this device is on the one context's stream, issued before this call.
    }

    /// <summary>Nothing on GL: its transients are pooled, never aliased.</summary>
    public void Alias(GpuResource? before, GpuResource after) => ArgumentNullException.ThrowIfNull(after);

    /// <summary>Nothing on GL: a pooled transient keeps its old content, which its first use overwrites.</summary>
    public void Discard(GpuResource resource) => ArgumentNullException.ThrowIfNull(resource);

    /// <summary>Nothing on GL: no capture tool reads GL markers here.</summary>
    public void Marker(string pass) => ArgumentNullException.ThrowIfNull(pass);

    public void Timestamp(int slot)
    {
        RequireOpen();
        _backend.GlTimers.Stamp(Frame, slot);
    }

    private void RequireOpen()
    {
        if (!IsOpen)
            throw new InvalidOperationException("The frame's list records between BeginFrame and Submit.");
    }

    private void RequireOutsidePass(string what)
    {
        RequireOpen();
        if (_target is not null)
            throw new InvalidOperationException($"{what} is outside a pass: EndPass first.");
    }
}
