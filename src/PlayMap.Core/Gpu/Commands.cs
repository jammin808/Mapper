using System.Numerics;
using System.Runtime.InteropServices;

namespace PlayMap.Core.Gpu;

/// <summary>What a pass does with the target's old content: keeps it, clears it, or leaves it undefined.</summary>
public enum LoadOp
{
    Load,
    Clear,
    DontCare,
}

/// <summary>A pass over a target: its load, the clear colour and depth, and the rectangle drawn (a zero size is the whole target).</summary>
public readonly record struct PassDesc(LoadOp Load, Vector4 Clear, float ClearDepth = 1, int X = 0, int Y = 0, int Width = 0,
    int Height = 0)
{
    public static readonly PassDesc ClearBlack = new(LoadOp.Clear, new Vector4(0, 0, 0, 1));
    public static readonly PassDesc Keep = new(LoadOp.Load, default);
}

/// <summary>How a resource is about to be used: the state a barrier moves it to. Both backends take the call; GL ignores it.</summary>
public enum ResourceUse
{
    Sampled,
    RenderTarget,
    DepthWrite,
    CopySource,
    CopyDest,
    Present,
    Storage,
    ShaderRead,
}

/// <summary>One queue's recording for one frame. Recorded by one thread (the render thread for the direct queue; an upload lane
/// for its copy list); submitted once. Every method is allocation-free.</summary>
public interface ICommandList
{
    QueueKind Queue { get; }

    void BeginPass(RenderTarget target, in PassDesc pass);

    void EndPass();

    /// <summary>Starts a draw of a program with a state into the current pass's target.</summary>
    DrawBuilder Draw(ProgramId program, in DrawState state);

    void Copy(GpuTexture source, GpuTexture dest);

    /// <summary>Pixels into a texture through the frame's upload ring (GL: straight into the texture). <paramref name="rowPixels"/>
    /// is the source row length in pixels when rows are padded (0 = tightly packed).</summary>
    void Upload(GpuTexture dest, ReadOnlySpan<byte> pixels, UploadFormat format, int rowPixels = 0);

    void UploadCompressed(GpuTexture dest, nint blocks, int bytes);

    /// <summary>An explicit transition. A backend that tracks states itself takes it as a hint; the render graph's plans issue every
    /// one they need.</summary>
    void Barrier(GpuResource resource, ResourceUse use);

    /// <summary>The GPU waits for another queue's value before what follows (GL: nothing to wait for).</summary>
    void Wait(in FenceValue value);

    /// <summary>The memory under <paramref name="after"/> held <paramref name="before"/> until now (two transients aliased by the
    /// render graph): an aliasing barrier on Direct3D 12; nothing on GL, whose transients are pooled, never aliased.</summary>
    void Alias(GpuResource? before, GpuResource after);

    /// <summary>The resource's old content is not needed (a transient's first use in a frame): <c>DiscardResource</c> on Direct3D 12,
    /// nothing on GL.</summary>
    void Discard(GpuResource resource);

    /// <summary>A marker for PIX and DRED's breadcrumbs: a pass's name, a literal (never built per frame).</summary>
    void Marker(string pass);

    void Timestamp(int slot);
}

/// <summary>What a draw's terminal call draws.</summary>
public enum DrawShape
{
    /// <summary>One triangle over the target, made in the vertex shader from the vertex index.</summary>
    Fullscreen,

    /// <summary>A four-vertex strip made in the vertex shader, <see cref="DrawCall.Instances"/> times.</summary>
    GeneratedQuads,

    /// <summary><see cref="DrawCall.Count"/> vertices of the mesh from <see cref="DrawCall.First"/>.</summary>
    Vertices,

    /// <summary><see cref="DrawCall.Count"/> indices of the mesh.</summary>
    Indexed,

    /// <summary><see cref="DrawCall.Count"/> vertices of the mesh, <see cref="DrawCall.Instances"/> times.</summary>
    Instanced,
}

/// <summary>A finished draw as a list records it: the program and state, what is drawn, and from which mesh.</summary>
public readonly record struct DrawCall(ProgramId Program, DrawState State, DrawShape Shape, GpuMesh? Mesh, int Count, int First,
    int Instances);

/// <summary>The list's side of a <see cref="DrawBuilder"/>: the finished draw with its constant block (laid out by the program's
/// layout; every constant the draw did not set is zero) and its textures by slot (null where none was bound).</summary>
public interface IDrawSink
{
    void Record(in DrawCall call, ReadOnlySpan<byte> constants, ReadOnlySpan<GpuTexture?> textures);
}

/// <summary>One draw being built: constants by name through the program's layout, textures by slot name, then the draw itself.
/// A ref struct over the list's per-frame constant ring: nothing escapes the frame, nothing allocates.
///
/// The list hands it a zeroed block of the layout's <see cref="ProgramLayout.ConstantBytes"/> and cleared texture slots. A name
/// the layout does not hold writes nothing (a programming error the layout tests catch, never a look-up failure mid-frame), as does
/// a value of the wrong kind for its constant (a float into an int, a vector into a matrix); a vector of fewer components than its
/// constant writes those it has. A matrix's rows each start a 16-byte register, row r at offset + 16·r, in System.Numerics' row
/// order.</summary>
public readonly ref struct DrawBuilder
{
    private readonly IDrawSink? _sink;
    private readonly ProgramLayout? _layout;
    private readonly Span<byte> _constants;
    private readonly Span<GpuTexture?> _textures;
    private readonly ProgramId _program;
    private readonly DrawState _state;

    /// <summary>A draw of <paramref name="program"/> with <paramref name="state"/>, writing into <paramref name="constants"/> (at
    /// least the layout's block, zeroed) and <paramref name="textures"/> (one per layout slot, cleared), finished into
    /// <paramref name="sink"/>.</summary>
    public DrawBuilder(IDrawSink sink, ProgramId program, in DrawState state, ProgramLayout layout, Span<byte> constants,
        Span<GpuTexture?> textures)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(layout);
        if (constants.Length < layout.ConstantBytes)
            throw new ArgumentException($"Program '{layout.Name}' takes {layout.ConstantBytes} bytes of constants; {constants.Length} given.", nameof(constants));
        if (textures.Length < layout.Textures.Length)
            throw new ArgumentException($"Program '{layout.Name}' reads {layout.Textures.Length} textures; {textures.Length} slots given.", nameof(textures));
        _sink = sink;
        _layout = layout;
        _constants = constants[..layout.ConstantBytes];
        _textures = textures[..layout.Textures.Length];
        _program = program;
        _state = state;
    }

    public DrawBuilder Set(string name, float x) => SetFloats(name, new Vector4(x, 0, 0, 0), 1);

    public DrawBuilder Set(string name, float x, float y) => SetFloats(name, new Vector4(x, y, 0, 0), 2);

    public DrawBuilder Set(string name, float x, float y, float z) => SetFloats(name, new Vector4(x, y, z, 0), 3);

    public DrawBuilder Set(string name, float x, float y, float z, float w) => SetFloats(name, new Vector4(x, y, z, w), 4);

    public DrawBuilder Set(string name, Vector4 v) => SetFloats(name, v, 4);

    /// <summary>A 4×4 matrix in System.Numerics' row-vector layout, its rows written in order.</summary>
    public DrawBuilder Set(string name, in Matrix4x4 m)
    {
        if (TrySlot(name, out var slot) && slot.Type == ConstantType.Float4x4)
        {
            var block = MemoryMarshal.Cast<byte, float>(_constants.Slice(slot.Offset, 64));
            block[0] = m.M11; block[1] = m.M12; block[2] = m.M13; block[3] = m.M14;
            block[4] = m.M21; block[5] = m.M22; block[6] = m.M23; block[7] = m.M24;
            block[8] = m.M31; block[9] = m.M32; block[10] = m.M33; block[11] = m.M34;
            block[12] = m.M41; block[13] = m.M42; block[14] = m.M43; block[15] = m.M44;
        }
        return this;
    }

    /// <summary>A 3×3 matrix, row-major: its three rows each start a register.</summary>
    public DrawBuilder Set(string name, in Matrix3x3 m)
    {
        if (TrySlot(name, out var slot) && slot.Type == ConstantType.Float3x3)
        {
            var block = MemoryMarshal.Cast<byte, float>(_constants.Slice(slot.Offset, 44));
            block[0] = m.M00; block[1] = m.M01; block[2] = m.M02;
            block[4] = m.M10; block[5] = m.M11; block[6] = m.M12;
            block[8] = m.M20; block[9] = m.M21; block[10] = m.M22;
        }
        return this;
    }

    public DrawBuilder SetInt(string name, int value)
    {
        if (TrySlot(name, out var slot) && ConstantTypes.IsInt(slot.Type))
            MemoryMarshal.Write(_constants[slot.Offset..], in value);
        return this;
    }

    /// <summary>Binds a texture to the layout's slot of that name. The texture's own filter and wrap choose its sampler.</summary>
    public DrawBuilder Texture(string name, GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (_layout is not null && _layout.TryTexture(name, out var slot))
            _textures[slot.Slot] = texture;
        return this;
    }

    /// <summary>One triangle that covers the target.</summary>
    public void Fullscreen() => Finish(new DrawCall(_program, _state, DrawShape.Fullscreen, null, 3, 0, 1));

    /// <summary>Four vertices as a strip with no buffers, <paramref name="instances"/> times.</summary>
    public void GeneratedQuads(int instances = 1) =>
        Finish(new DrawCall(_program, _state, DrawShape.GeneratedQuads, null, 4, 0, instances));

    public void Vertices(GpuMesh mesh, int vertexCount, int first = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        Finish(new DrawCall(_program, _state, DrawShape.Vertices, mesh, vertexCount, first, 1));
    }

    public void Indexed(GpuMesh mesh, int indexCount)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        Finish(new DrawCall(_program, _state, DrawShape.Indexed, mesh, indexCount, 0, 1));
    }

    public void Instanced(GpuMesh mesh, int vertexCount, int instances)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        Finish(new DrawCall(_program, _state, DrawShape.Instanced, mesh, vertexCount, 0, instances));
    }

    private DrawBuilder SetFloats(string name, Vector4 v, int components)
    {
        if (!TrySlot(name, out var slot) || ConstantTypes.IsInt(slot.Type) || ConstantTypes.IsMatrix(slot.Type))
            return this;
        var block = MemoryMarshal.Cast<byte, float>(_constants.Slice(slot.Offset, ConstantTypes.Bytes(slot.Type)));
        var n = Math.Min(components, block.Length);
        for (var i = 0; i < n; i++)
            block[i] = v[i];
        return this;
    }

    private bool TrySlot(string name, out ConstantSlot slot)
    {
        if (_layout is not null)
            return _layout.TryConstant(name, out slot);
        slot = default;
        return false;
    }

    private void Finish(in DrawCall call)
    {
        if (_sink is null)
            throw new InvalidOperationException("This draw was not started by a command list (ICommandList.Draw).");
        _sink.Record(call, _constants, _textures);
    }
}

/// <summary>One block of memory a plan places transient textures and targets in at the offsets its aliasing chose. GL rents each
/// placement from the resource pool and ignores the offset; Direct3D 12 places resources in one heap. A placement lives until the
/// memory is disposed, which hands every placement back (GL: to the pool). A placement that does not fit the block at its offset is
/// refused.</summary>
public interface ITransientMemory : IDisposable
{
    long Bytes { get; }

    /// <summary>The bytes and alignment a texture of this shape takes here, so the plan can lay transients out.</summary>
    (long Bytes, long Align) SizeOf(in TextureDesc desc);

    /// <summary>The bytes and alignment a target of this shape takes here (its colour attachments and depth together).</summary>
    (long Bytes, long Align) SizeOf(in TargetDesc desc);

    GpuTexture Place(in TextureDesc desc, long offset);

    /// <summary>A target placed at the offset: what a pass draws into (a placed texture is only sampled or copied).</summary>
    RenderTarget PlaceTarget(in TargetDesc desc, long offset);
}
