using PlayMap.Core.Storage;

namespace PlayMap.Core.Tests.Storage;

/// <summary>A file renamed into place reaches the disk first: the flush is reported with the file's final path while the bytes are
/// whole beside it and the old file is still in place, then the rename lands them; the temporary name is gone after. A folder moves
/// in the same way, whole, and never over one that is there.</summary>
public sealed class DurableFileTests
{
    /// <summary>A rename that landed but whose folder the disk then refused to flush is in place all the same, and says so: the call
    /// throws <see cref="NotFlushedException"/> (an IOException) naming the file, and the file holds the new bytes with nothing left
    /// beside it. Where the rename is written through (Windows) there is no folder to flush after it.</summary>
    [Fact]
    public void ARenameThatLandedButWhoseFolderWasNotFlushedSaysItIsInPlace()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-durable-").FullName;
        var path = Path.Combine(dir, "settings.json");
        var asked = new List<string>();
        void Refuse(string folder)
        {
            if (folder == dir)
            {
                asked.Add(folder);
                throw new IOException("the disk refused the folder's flush");
            }
        }
        DurableFile.FlushingFolder += Refuse;
        try
        {
            File.WriteAllText(path, "old");
            if (OperatingSystem.IsWindows())
            {
                DurableFile.WriteAllText(path, "new");
                Assert.Empty(asked);
            }
            else
            {
                var notFlushed = Assert.Throws<NotFlushedException>(() => DurableFile.WriteAllText(path, "new"));
                Assert.Equal(path, notFlushed.FilePath);
                Assert.Equal([dir], asked);
            }
            Assert.Equal("new", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".saving"));
        }
        finally
        {
            DurableFile.FlushingFolder -= Refuse;
            Directory.Delete(dir, recursive: true);
        }
    }

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

    [Fact]
    public void AFolderIsFlushedWholeBesideItsPlaceThenMovedInAndNeverOverOneThatIsThere()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-durable-").FullName;
        try
        {
            var temp = Path.Combine(dir, ".run.partial");
            var final = Path.Combine(dir, "run");
            Directory.CreateDirectory(Path.Combine(temp, "inner"));
            File.WriteAllText(Path.Combine(temp, "a.txt"), "a");
            File.WriteAllText(Path.Combine(temp, "inner", "b.txt"), "b");
            var seen = new List<(string Path, bool InPlace, string Beside)>();
            void Flushed(string p)
            {
                if (p.StartsWith(dir, StringComparison.Ordinal))
                    seen.Add((p, Directory.Exists(final), File.ReadAllText(Path.Combine(temp, "inner", "b.txt"))));
            }
            DurableFile.Flushed += Flushed;
            try
            {
                DurableFile.MoveFolderIntoPlace(temp, final);
            }
            finally
            {
                DurableFile.Flushed -= Flushed;
            }
            Assert.Equal([(final, false, "b")], seen);
            Assert.False(Directory.Exists(temp));
            Assert.Equal(("a", "b"), (File.ReadAllText(Path.Combine(final, "a.txt")), File.ReadAllText(Path.Combine(final, "inner", "b.txt"))));

            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(temp, "c.txt"), "c");
            Assert.Throws<IOException>(() => DurableFile.MoveFolderIntoPlace(temp, final));
            Assert.True(File.Exists(Path.Combine(temp, "c.txt")));
            Assert.False(File.Exists(Path.Combine(final, "c.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
