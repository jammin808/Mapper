using PlayMap.Core.Storage;

namespace PlayMap.Core.Tests.Storage;

/// <summary>A file renamed into place reaches the disk first: the flush is reported with the file's final path while the bytes are
/// whole beside it and the old file is still in place, then the rename lands them; the temporary name is gone after.</summary>
public sealed class DurableFileTests
{
    [Fact]
    public void AFileIsFlushedWholeBeforeItIsRenamedOverTheOldOne()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-durable-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "old");
            var seen = new List<(string Path, string Beside, string InPlace)>();
            void Flushed(string p)
            {
                if (p == path)
                    seen.Add((p, File.ReadAllText(p + ".saving"), File.ReadAllText(p)));
            }
            DurableFile.Flushed += Flushed;
            try
            {
                DurableFile.WriteAllText(path, "new");
            }
            finally
            {
                DurableFile.Flushed -= Flushed;
            }
            Assert.Equal([(path, "new", "old")], seen);
            Assert.Equal("new", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".saving"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BytesAndAFileWrittenElsewhereMoveIntoPlaceTheSameWay()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-durable-").FullName;
        try
        {
            var flushed = new List<string>();
            void Flushed(string p)
            {
                if (p.StartsWith(dir, StringComparison.Ordinal))
                    flushed.Add(Path.GetFileName(p));
            }
            DurableFile.Flushed += Flushed;
            try
            {
                DurableFile.WriteAllBytes(Path.Combine(dir, "a.bin"), [1, 2, 3]);
                File.WriteAllText(Path.Combine(dir, "b.part"), "b");
                DurableFile.MoveIntoPlace(Path.Combine(dir, "b.part"), Path.Combine(dir, "b.txt"));
                // Not over a file that is there when asked not to: nothing moves.
                File.WriteAllText(Path.Combine(dir, "c.part"), "c");
                Assert.Throws<IOException>(() => DurableFile.MoveIntoPlace(Path.Combine(dir, "c.part"), Path.Combine(dir, "b.txt"), overwrite: false));
            }
            finally
            {
                DurableFile.Flushed -= Flushed;
            }
            Assert.Equal(["a.bin", "b.txt", "b.txt"], flushed);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(dir, "a.bin")));
            Assert.Equal("b", File.ReadAllText(Path.Combine(dir, "b.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "c.part")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
