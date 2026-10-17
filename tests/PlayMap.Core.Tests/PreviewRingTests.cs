using PlayMap.Core.Media;
using PlayMap.Core.Wire;

namespace PlayMap.Core.Tests;

/// <summary>The preview ring between the engine and the desk, and the reply shapes.</summary>
public class PreviewRingTests
{
    [Fact]
    public void ADeskReadsTheNewestPictureTheEngineWrote()
    {
        var path = Path.Combine(PreviewRing.DefaultFolder(), $"playmap-test-{Guid.NewGuid():N}.preview");
        using var writer = new PreviewRingWriter(path, 64, 32);
        using var reader = new PreviewRingReader(path);
        var dest = new byte[64 * 32 * 4];
        Assert.False(reader.TryRead(dest, 0, out _));
        for (var frame = 1; frame <= 5; frame++)
        {
            var pic = new byte[16 * 8 * 4];
            Array.Fill(pic, (byte)frame);
            Assert.True(writer.Write(pic, 16, 8, 16 * 4, frame * 1000));
        }
        Assert.True(reader.TryRead(dest, 0, out var info));
        Assert.Equal(5, info.Sequence);
        Assert.Equal(16, info.Width);
        Assert.Equal(5, dest[0]);
        Assert.False(reader.TryRead(dest, info.Sequence, out _));
        Assert.False(writer.Write(new byte[100 * 100 * 4], 100, 100, 400, 0));
    }

    /// <summary>One ring per file: a writer makes its file new and never opens one that is there (an earlier ring a reader may still
    /// map), which stays as it was.</summary>
    [Fact]
    public void AWriterNeverOpensAFileThatIsThere()
    {
        var path = PreviewRing.PathFor("test", $"{Guid.NewGuid():N}", 1);
        Assert.EndsWith("~1.preview", path, StringComparison.Ordinal);
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            Assert.Throws<IOException>(() =>
            {
                using var writer = new PreviewRingWriter(path, 8, 8);
            });
            Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A ring that cannot be made (here larger than any disk or address space) leaves no file behind: a writer that
    /// failed never fills the folder with files no one maps.</summary>
    [Fact]
    public void AWriterThatCannotMakeItsRingLeavesNoFile()
    {
        var path = PreviewRing.PathFor("test", $"{Guid.NewGuid():N}", 1);
        Assert.ThrowsAny<Exception>(() =>
        {
            using var writer = new PreviewRingWriter(path, 1 << 29, 1 << 29);
        });
        Assert.False(File.Exists(path));
    }

    /// <summary>A file that is not a whole ring — empty, shorter than a header, or a ring's header claiming more than the file holds —
    /// is refused as not a ring, and the reader keeps nothing of it open: the file can be taken whole (no other handle shares it) at once.</summary>
    [Fact]
    public void AReaderRefusesAFileThatIsNotAWholeRingAndKeepsNothingOpen()
    {
        var header = new byte[PreviewRing.HeaderBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, PreviewRing.Magic);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 1920);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 1080);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), -1);
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 1, 2, 3 }, header })
        {
            var path = PreviewRing.PathFor("test", $"{Guid.NewGuid():N}", 1);
            File.WriteAllBytes(path, bytes);
            try
            {
                Assert.Throws<InvalidDataException>(() =>
                {
                    using var reader = new PreviewRingReader(path);
                });
                using (var alone = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Assert.Equal(bytes.Length, alone.Length);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>A ring a reader opens is one generation of the view's ring on that node, in this machine's preview folder, named as
    /// the node names it; any other words name no ring, whatever is on the disk.</summary>
    [Fact]
    public void OnlyAGenerationOfTheViewsRingOnTheNodeInThePreviewFolderIsARingOfIt()
    {
        var folder = PreviewRing.DefaultFolder();
        Assert.True(PreviewRing.IsGenerationOf(PreviewRing.PathFor("n1", "op-op_1", 42), "n1", "op-op_1"));
        Assert.True(PreviewRing.IsGenerationOf(PreviewRing.PathFor("n1", "editor", 1_760_000_000_000_000), "n1", "editor"));
        string?[] notRings =
        [
            null,
            "",
            PreviewRing.PathFor("n1", "op-op_1"),
            PreviewRing.PathFor("n2", "op-op_1", 42),
            PreviewRing.PathFor("n1", "op-op_2", 42),
            PreviewRing.PathFor("n1", "op-op_1~draft", 42),
            Path.Combine(folder, "playmap-n1-op-op_1~.preview"),
            Path.Combine(folder, "playmap-n1-op-op_1~4x2.preview"),
            Path.Combine(folder, "playmap-n1-op-op_1~42.preview.bak"),
            "playmap-n1-op-op_1~42.preview",
            Path.Combine(folder, "sub", "playmap-n1-op-op_1~42.preview"),
            Path.Combine(folder, "..", Path.GetFileName(folder), "playmap-n1-op-op_1~42.preview"),
            Path.Combine(Path.GetTempPath(), "playmap-n1-op-op_1~42.preview"),
            "/dev/zero",
            @"\\host\share\playmap-n1-op-op_1~42.preview",
        ];
        foreach (var path in notRings)
            Assert.False(PreviewRing.IsGenerationOf(path, "n1", "op-op_1"), path);
        Assert.False(PreviewRing.IsGenerationOf(PreviewRing.PathFor("n1", "op-op_1", 42), "", "op-op_1"));
        Assert.False(PreviewRing.IsGenerationOf(PreviewRing.PathFor("n1", "op-op_1", 42), "n1", ""));
    }

    [Fact]
    public void RepliesParseWithAndWithoutAnId()
    {
        Assert.Equal(new WireReply(true, "PONG 1 2 3"), WireReply.Parse("OK @p1 PONG 1 2 3"));
        Assert.Equal(new WireReply(false, "no clip"), WireReply.Parse("ERR no clip"));
        Assert.Equal(new WireReply(true, ""), WireReply.Parse("OK @a1"));
        Assert.Equal("OK @x {\"a\":1}", CommandReply.Success("{\"a\":1}").ToLine("x"));
        Assert.True(CommandReply.NoReply().Silent);
    }
}
