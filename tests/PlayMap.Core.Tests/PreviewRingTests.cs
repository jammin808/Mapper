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

    [Fact]
    public void ARingMadeAgainWhileTheDeskStillMapsTheOldOneIsANewFileAndTheOldReaderIsToldItsWriterWent()
    {
        var path = Path.Combine(PreviewRing.DefaultFolder(), $"playmap-test-{Guid.NewGuid():N}.preview");
        var dest = new byte[64 * 32 * 4];
        var first = new PreviewRingWriter(path, 64, 32);
        using var stale = new PreviewRingReader(path);
        using (first)
        {
            Assert.False(stale.WriterGone);
            Assert.True(first.Write(new byte[16 * 8 * 4], 16, 8, 16 * 4, 1));
        }
        Assert.True(stale.WriterGone);

        // The view comes back at another size while the desk still maps the old ring.
        using var second = new PreviewRingWriter(path, 32, 16);
        using var fresh = new PreviewRingReader(path);
        Assert.False(fresh.WriterGone);
        Assert.Equal((32, 16), (fresh.MaxWidth, fresh.MaxHeight));
        var pic = new byte[8 * 4 * 4];
        Array.Fill(pic, (byte)7);
        Assert.True(second.Write(pic, 8, 4, 8 * 4, 2));
        Assert.True(fresh.TryRead(dest, 0, out var info));
        Assert.Equal((8, 4, 1L, 7), (info.Width, info.Height, info.Sequence, (int)dest[0]));

        // The old pages are the old ring's: its last picture, never the new ring's, and still marked gone.
        Assert.True(stale.TryRead(dest, 0, out var old));
        Assert.Equal((16, 1L), (old.Width, old.Sequence));
        Assert.True(stale.WriterGone);
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
