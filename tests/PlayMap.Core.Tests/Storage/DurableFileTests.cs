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

    /// <summary>A POSIX rename Windows cannot make moves nothing, and the replace falls back to the classic move, which copies across
    /// volumes: a file system or a system that takes no POSIX rename (an invalid function, not supported, an invalid parameter) and a
    /// target on another volume (not the same device — a media folder that is a junction to another drive) fall back; a refusal (access
    /// denied, a sharing violation, a missing file) is the caller's to hear.</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(87, true)]
    [InlineData(17, true)]
    [InlineData(2, false)]
    [InlineData(5, false)]
    [InlineData(32, false)]
    public void ARenameWindowsCannotMakeFallsBackToTheMoveThatCopies(int error, bool fallsBack) =>
        Assert.Equal(fallsBack, DurableFile.FallsBackToMove(error));

    /// <summary>A reader that opened a durable file as every reader of the node's state does (<see cref="DurableFile.OpenRead"/>) and holds it open
    /// across a replace never makes the replace fail: the new bytes are in place when the write returns, nothing is left beside them,
    /// and the reader goes on reading the old bytes whole. On Windows this is the POSIX-semantics rename; elsewhere a rename always is.</summary>
    [Fact]
    [Trait("Category", "Windows")]
    public void AReaderHeldOpenAcrossAReplaceNeitherFailsItNorSeesItHalfDone()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-durable-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            DurableFile.WriteAllText(path, "old settings");
            for (var i = 0; i < 3; i++)
            {
                var before = DurableFile.ReadAllText(path);
                using var held = DurableFile.OpenRead(path);
                Assert.Equal((byte)before[0], held.ReadByte());
                DurableFile.WriteAllText(path, $"new settings {i}");
                File.WriteAllText(Path.Combine(dir, "b.part"), $"moved {i}");
                using var heldMoved = File.Exists(Path.Combine(dir, "b.txt")) ? DurableFile.OpenRead(Path.Combine(dir, "b.txt")) : null;
                DurableFile.MoveIntoPlace(Path.Combine(dir, "b.part"), Path.Combine(dir, "b.txt"));

                Assert.Equal($"new settings {i}", DurableFile.ReadAllText(path));
                Assert.Equal($"moved {i}", DurableFile.ReadAllText(Path.Combine(dir, "b.txt")));
                Assert.False(File.Exists(path + ".saving"));
                using var rest = new StreamReader(held);
                Assert.Equal(before[1..], rest.ReadToEnd());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A reader in another program that opened the file without sharing delete (Windows only: Unix never refuses a rename
    /// over an open file) makes the replace fail, and the writer hears it fail: the call throws, the old bytes are still in place.</summary>
    [Fact]
    [Trait("Category", "Windows")]
    public void AReaderThatSharesNoDeleteMakesTheReplaceFailAloudOnWindows()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-durable-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            DurableFile.WriteAllText(path, "old");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (OperatingSystem.IsWindows())
                {
                    var refused = Record.Exception(() => DurableFile.WriteAllText(path, "new"));
                    Assert.True(refused is IOException or UnauthorizedAccessException, $"{refused}");
                    Assert.Equal("old", DurableFile.ReadAllText(path));
                }
                else
                {
                    DurableFile.WriteAllText(path, "new");
                    Assert.Equal("new", DurableFile.ReadAllText(path));
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
