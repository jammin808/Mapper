using Microsoft.Extensions.Logging;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Tests.Host;

/// <summary>The engine's log names the build at the head of every file it starts: the first file in a fresh folder and the one
/// after each roll, so a file read on its own says which build wrote it. A file that already holds lines is appended to as it
/// is.</summary>
public sealed class NodeLogTests
{
    private static string[] Lines(string path)
    {
        using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var text = new StreamReader(read);
        return text.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsHeading(string line) => line.EndsWith(" info PlayMap.Engine.Log: " + NodeLogProvider.Heading, StringComparison.Ordinal);

    [Fact]
    public void TheHeadingNamesTheBuildAndTheProcess()
    {
        Assert.StartsWith(Core.BuildInfo.Current.Display + " begins this file of the engine's log", NodeLogProvider.Heading, StringComparison.Ordinal);
        Assert.Contains(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"(process {Environment.ProcessId})"), NodeLogProvider.Heading,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryFileTheLogStartsBeginsWithTheBuild()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-log-");
        try
        {
            var path = Path.Combine(dir.FullName, NodeLogProvider.FileName);
            var rolled = path + ".1";
            // A roll past one byte: each line ends its file, so each is in a file the log started — the first in a fresh folder,
            // each after it once a roll moved the one before aside.
            using var log = new NodeLogProvider(path, LogLevel.Information, rollBytes: 1);
            var logger = log.CreateLogger("PlayMap.Test");
            var before = -1L;
            for (var i = 0; i < 4; i++)
            {
                // Each line is longer than the one before, so the moved file's size says the writer is done with this one; the
                // file is read only then, while the writer waits for the next.
                var pad = new string('x', i + 1);
                logger.LogInformation("line {N} {Pad}", i, pad);
                var seen = before;
                Assert.True(await Eventually.TrueAsync(() => File.Exists(rolled) && new FileInfo(rolled).Length != seen), "line " + i + " was not moved aside");
                before = new FileInfo(rolled).Length;
                var lines = Lines(rolled);
                Assert.Equal(2, lines.Length);
                Assert.True(IsHeading(lines[0]), "the file begins: " + lines[0]);
                Assert.EndsWith(string.Create(System.Globalization.CultureInfo.InvariantCulture, $" info PlayMap.Test: line {i} {pad}"), lines[1],
                    StringComparison.Ordinal);
                Assert.False(File.Exists(path));
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AFileThatHoldsLinesIsAppendedToWithoutAHeading()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-log-");
        try
        {
            var path = Path.Combine(dir.FullName, NodeLogProvider.FileName);
            File.WriteAllText(path, "2026-10-17T00:00:00.000Z info PlayMap.Engine: an earlier process's line\n");
            using (var log = new NodeLogProvider(path))
                log.CreateLogger("PlayMap.Test").LogInformation("this process's line");
            var lines = Lines(path);
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("an earlier process's line", lines[0], StringComparison.Ordinal);
            Assert.EndsWith(" info PlayMap.Test: this process's line", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
