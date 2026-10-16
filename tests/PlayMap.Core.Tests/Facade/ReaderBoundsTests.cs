using System.Text;
using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Tests.Facade;

/// <summary>The readers hold a header to what its file can carry: a PLY header declaring more vertices or faces than its bytes can hold
/// is refused with words before anything is allocated for them (binary and ASCII), an honest file still reads; and the whole-file LAS
/// reader leaves out points flagged withheld, as the chunked reader does.</summary>
public sealed class ReaderBoundsTests
{
    private static byte[] BinaryPly(int declared, int written)
    {
        var head = $"ply\nformat binary_little_endian 1.0\nelement vertex {declared}\nproperty float x\nproperty float y\nproperty float z\nend_header\n";
        var body = new byte[written * 12];
        for (var i = 0; i < written; i++)
        {
            BitConverter.GetBytes((float)i).CopyTo(body, i * 12);
            BitConverter.GetBytes(1f).CopyTo(body, i * 12 + 4);
            BitConverter.GetBytes(2f).CopyTo(body, i * 12 + 8);
        }
        return [.. Encoding.ASCII.GetBytes(head), .. body];
    }

    [Fact]
    public void APlyHeaderThatPromisesMoreThanItsFileHoldsIsRefusedBeforeItIsRead()
    {
        var honest = MeshIO.ReadPly(new MemoryStream(BinaryPly(3, 3)));
        Assert.Equal(3, honest.Cloud!.Count);

        var lying = Assert.Throws<FormatException>(() => MeshIO.ReadPly(new MemoryStream(BinaryPly(100_000_000, 3))));
        Assert.Contains("100000000 vertex", lying.Message, StringComparison.Ordinal);
        Assert.Contains("damaged or cut short", lying.Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => MeshIO.ReadPly(new MemoryStream(BinaryPly(4, 3))));
        Assert.Contains("are not read", Assert.Throws<FormatException>(() => MeshIO.ReadPly(new MemoryStream(BinaryPly(2_000_000_000, 3)))).Message,
            StringComparison.Ordinal);

        var ascii = "ply\nformat ascii 1.0\nelement vertex 3\nproperty float x\nproperty float y\nproperty float z\nelement face 900000\nproperty list uchar int vertex_indices\nend_header\n0 0 0\n1 0 0\n0 1 0\n3 0 1 2\n";
        var faces = Assert.Throws<FormatException>(() => MeshIO.ReadPly(new MemoryStream(Encoding.ASCII.GetBytes(ascii))));
        Assert.Contains("900000 face", faces.Message, StringComparison.Ordinal);
        Assert.Equal(1, MeshIO.ReadPly(new MemoryStream(Encoding.ASCII.GetBytes(ascii.Replace("face 900000", "face 1", StringComparison.Ordinal)))).Mesh!.TriangleCount);
    }

    [Fact]
    public void TheWholeFileLasReaderLeavesOutWithheldPoints()
    {
        var rng = new Pcg32(4, 2);
        var pts = Enumerable.Range(0, 3000).Select(_ => new Vec3d(512_000 + rng.Range(0.0, 10.0), 5_401_000 + rng.Range(0.0, 10.0), 100)).ToList();
        var withheld = pts.Select((_, i) => i % 4 == 1).ToList();
        var colours = pts.Select((_, i) => i & 0xFF).ToList();
        var bytes = LasTestWriter.Write(pts, colours, new Vec3d(0.001, 0.001, 0.001), new Vec3d(512_000, 5_401_000, 0), withheld: withheld);
        var cloud = MeshIO.ReadLas(new MemoryStream(bytes));
        Assert.Equal(2250, cloud.Count);
        Assert.Equal(2250, cloud.Colors!.Length);
        var kept = pts.Where((_, i) => i % 4 != 1).ToList();
        Assert.True(cloud.Points.Zip(kept).All(p => (p.First - p.Second).Length < 0.001));
        Assert.Equal(colours.Where((_, i) => i % 4 != 1), cloud.Colors);

        using var chunked = new LasChunkSource(new MemoryStream(bytes));
        var buffer = new Vec3d[4096];
        var n = chunked.Read(buffer, new int[4096], CancellationToken.None);
        Assert.Equal(cloud.Points, buffer.AsSpan(0, n).ToArray());
    }
}
