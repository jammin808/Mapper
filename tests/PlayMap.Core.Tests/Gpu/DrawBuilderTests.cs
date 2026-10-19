using System.Numerics;
using System.Runtime.InteropServices;
using PlayMap.Core.Gpu;
using PlayMap.Tests;

namespace PlayMap.Core.Tests.Gpu;

/// <summary>A draw built through the builder: each constant lands at its layout's offset in the block the list handed it (a matrix's
/// rows each starting a register), a name the layout lacks or a value of the wrong kind writes nothing, textures land in their slots,
/// the terminal call hands the list the draw with its block and textures, and nothing of it allocates.</summary>
public sealed class DrawBuilderTests
{
    private sealed class FakeTexture(int width, int height) : GpuTexture(new TextureDesc(width, height, TextureFormat.Rgba8, TextureUsage.Sampled), 1)
    {
        protected override void Dispose(bool disposing) => IsDisposed = true;
    }

    private sealed class FakeMesh() : GpuMesh([], null, null, null, "mesh", 1)
    {
        protected override void Dispose(bool disposing) => IsDisposed = true;
    }

    /// <summary>A list's draw side: the last draw, a copy of its block and its textures.</summary>
    private sealed class Sink : IDrawSink
    {
        public DrawCall Call { get; private set; }
        public byte[] Constants { get; private set; } = [];
        public GpuTexture?[] Textures { get; private set; } = [];
        public int Draws { get; private set; }

        public void Record(in DrawCall call, ReadOnlySpan<byte> constants, ReadOnlySpan<GpuTexture?> textures)
        {
            Call = call;
            Constants = constants.ToArray();
            Textures = textures.ToArray();
            Draws++;
        }
    }

    private static readonly ProgramLayout Layout = ProgramLayout.Pack("output",
        [("uGain", ConstantType.Float, 1), ("uSize", ConstantType.Float2, 1), ("uRotation", ConstantType.Int, 1),
         ("uTint", ConstantType.Float4, 1), ("uView", ConstantType.Float4x4, 1), ("uNormal", ConstantType.Float3x3, 1),
         ("uColors", ConstantType.Float3, 4)],
        [("uPicture", true), ("uMask", false)]);

    private static float F(byte[] block, int offset) => MemoryMarshal.Read<float>(block.AsSpan(offset));

    [Fact]
    public void EachConstantLandsAtItsOffsetAndEachTextureInItsSlot()
    {
        var sink = new Sink();
        var block = new byte[Layout.ConstantBytes];
        var textures = new GpuTexture?[Layout.Textures.Length];
        var picture = new FakeTexture(4, 4);
        var view = new Matrix4x4(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16);
        new DrawBuilder(sink, new ProgramId(7), DrawState.Opaque, Layout, block, textures)
            .Set("uGain", 0.5f)
            .Set("uSize", 64, 36)
            .SetInt("uRotation", 3)
            .Set("uTint", new Vector4(0.1f, 0.2f, 0.3f, 0.4f))
            .Set("uView", view)
            .Set("uNormal", new Matrix3x3(1, 2, 3, 4, 5, 6, 7, 8, 9))
            .Set("uColors[2]", 0.25f, 0.5f, 0.75f)
            .Texture("uPicture", picture)
            .Fullscreen();

        Assert.Equal(1, sink.Draws);
        Assert.Equal(new DrawCall(new ProgramId(7), DrawState.Opaque, DrawShape.Fullscreen, null, 3, 0, 1), sink.Call);
        var c = sink.Constants;
        Assert.Equal(Layout.ConstantBytes, c.Length);
        Layout.TryConstant("uGain", out var gain);
        Assert.Equal(0.5f, F(c, gain.Offset));
        Layout.TryConstant("uSize", out var size);
        Assert.Equal((64f, 36f), (F(c, size.Offset), F(c, size.Offset + 4)));
        Layout.TryConstant("uRotation", out var rotation);
        Assert.Equal(3, MemoryMarshal.Read<int>(c.AsSpan(rotation.Offset)));
        Layout.TryConstant("uTint", out var tint);
        Assert.Equal(0.3f, F(c, tint.Offset + 8));
        Layout.TryConstant("uView", out var v);
        for (var i = 0; i < 16; i++)
            Assert.Equal(i + 1f, F(c, v.Offset + 4 * i));
        Layout.TryConstant("uNormal", out var n);
        Assert.Equal((1f, 2f, 3f), (F(c, n.Offset), F(c, n.Offset + 4), F(c, n.Offset + 8)));
        Assert.Equal((4f, 5f, 6f), (F(c, n.Offset + 16), F(c, n.Offset + 20), F(c, n.Offset + 24)));
        Assert.Equal((7f, 8f, 9f), (F(c, n.Offset + 32), F(c, n.Offset + 36), F(c, n.Offset + 40)));
        Layout.TryConstant("uColors", out var colors);
        Assert.Equal((0.25f, 0.5f, 0.75f), (F(c, colors.Offset + 32), F(c, colors.Offset + 36), F(c, colors.Offset + 40)));
        Assert.Equal(0f, F(c, colors.Offset));
        Assert.Same(picture, sink.Textures[0]);
        Assert.Null(sink.Textures[1]);
    }

    [Fact]
    public void ANameTheLayoutLacksOrAValueOfTheWrongKindWritesNothing()
    {
        var sink = new Sink();
        var block = new byte[Layout.ConstantBytes];
        new DrawBuilder(sink, new ProgramId(0), DrawState.Opaque, Layout, block, new GpuTexture?[2])
            .Set("uMissing", 1f)
            .Set("uRotation", 9f)
            .SetInt("uGain", 9)
            .Set("uView", 9f)
            .Set("uGain", Matrix4x4.Identity)
            .Texture("uGain", new FakeTexture(1, 1))
            .GeneratedQuads(5);

        Assert.All(sink.Constants, b => Assert.Equal(0, b));
        Assert.All(sink.Textures, Assert.Null);
        Assert.Equal(DrawShape.GeneratedQuads, sink.Call.Shape);
        Assert.Equal(5, sink.Call.Instances);
    }

    [Fact]
    public void EachTerminalCallHandsTheListItsShapeMeshAndCounts()
    {
        var sink = new Sink();
        var mesh = new FakeMesh();
        var state = new DrawState(BlendMode.Premultiplied, DepthMode.TestWrite, CullMode.Back, Topology.Lines);
        var block = new byte[Layout.ConstantBytes];
        var textures = new GpuTexture?[2];
        new DrawBuilder(sink, new ProgramId(1), state, Layout, block, textures).Vertices(mesh, 12, 3);
        Assert.Equal(new DrawCall(new ProgramId(1), state, DrawShape.Vertices, mesh, 12, 3, 1), sink.Call);
        new DrawBuilder(sink, new ProgramId(1), state, Layout, block, textures).Indexed(mesh, 36);
        Assert.Equal(new DrawCall(new ProgramId(1), state, DrawShape.Indexed, mesh, 36, 0, 1), sink.Call);
        new DrawBuilder(sink, new ProgramId(1), state, Layout, block, textures).Instanced(mesh, 4, 100);
        Assert.Equal(new DrawCall(new ProgramId(1), state, DrawShape.Instanced, mesh, 4, 0, 100), sink.Call);
        Assert.Throws<InvalidOperationException>(() => default(DrawBuilder).Set("uGain", 1f).Fullscreen());
    }

    [Fact]
    public void ABlockOrSlotsSmallerThanTheLayoutAreRefused()
    {
        var sink = new Sink();
        Assert.Throws<ArgumentException>(() => new DrawBuilder(sink, new ProgramId(0), DrawState.Opaque, Layout, new byte[16], new GpuTexture?[2]));
        Assert.Throws<ArgumentException>(() => new DrawBuilder(sink, new ProgramId(0), DrawState.Opaque, Layout, new byte[Layout.ConstantBytes], new GpuTexture?[1]));
    }

    [Fact]
    public void ADrawAllocatesNothing()
    {
        var sink = new NoCopySink();
        var block = new byte[Layout.ConstantBytes];
        var textures = new GpuTexture?[2];
        var picture = new FakeTexture(4, 4);
        var view = Matrix4x4.CreateTranslation(1, 2, 3);
        void Draw(int i) => new DrawBuilder(sink, new ProgramId(0), DrawState.Opaque, Layout, block, textures)
            .Set("uGain", i)
            .SetInt("uRotation", i & 3)
            .Set("uView", view)
            .Set("uColors[1]", 1, 2, 3)
            .Texture("uPicture", picture)
            .Fullscreen();
        Draw(0);
        var start = Allocations.Start();
        for (var i = 0; i < 1000; i++)
            Draw(i);
        Assert.Equal(0, Allocations.Since(start));
        Assert.Equal(1001, sink.Draws);
    }

    private sealed class NoCopySink : IDrawSink
    {
        public int Draws { get; private set; }

        public void Record(in DrawCall call, ReadOnlySpan<byte> constants, ReadOnlySpan<GpuTexture?> textures) => Draws++;
    }
}
