using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using PlayMap.Engine.Host;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Host;

/// <summary>The engine's log names the build at the head of every file it starts: the first file in a fresh folder and the one
/// after each roll, so a file read on its own says which build wrote it. A file that already holds lines is appended to as it
/// is. A roll that fails loses no line and is counted on its own, never as a dropped line, and is tried again on its own wait, with
/// no further line, until it lands.</summary>
public sealed class NodeLogTests(ITestOutputHelper output)
{
    private static string[] Lines(string path)
    {
        using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var text = new StreamReader(read);
        return text.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>The file's lines, or none when it is not there (a roll may move it between a look and a read).</summary>
    private static string[] LinesIfThere(string path)
    {
        try
        {
            return Lines(path);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }

    /// <summary>What a roll that did not happen left, read when the wait for it gives up: the log's dropped lines, its failed rolls
    /// (a move that threw leaves the line in the live file and the roll owed; a move not yet made counts nothing), and each file's
    /// length.</summary>
    private static string Found(NodeLogProvider log, string path, string rolled) =>
        string.Create(CultureInfo.InvariantCulture,
            $"the log has dropped {log.Dropped}; {log.RollsFailed} rolls failed; the live file {Length(path)}; '.1' {Length(rolled)}");

    private static string Length(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"holds {info.Length} bytes") : "is not there";
        }
        catch (IOException ex)
        {
            return "could not be read: " + ex.Message;
        }
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
                var moved = await Eventually.TrueAsync(() => File.Exists(rolled) && new FileInfo(rolled).Length != seen);
                Assert.True(moved, moved ? "" : $"line {i} was not moved aside: {Found(log, path, rolled)}");
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

    /// <summary>A roll refused three times (the seam stands for a handle on either file that shares no delete) is tried again with no
    /// further line, each wait twice the one before from <see cref="NodeLogProvider.FirstRetry"/>, and lands: the three failures are
    /// counted in <see cref="NodeLogProvider.RollsFailed"/> and none in <see cref="NodeLogProvider.Dropped"/>, ".1" holds the heading and
    /// the line, and the next line starts a new live file with its own heading.</summary>
    [Fact]
    public async Task AFailedRollIsTriedAgainWithoutAnotherLineAndIsNeverCountedAsADroppedLine()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-log-");
        try
        {
            var path = Path.Combine(dir.FullName, NodeLogProvider.FileName);
            var rolled = path + ".1";
            var clock = Stopwatch.StartNew();
            var tries = new List<TimeSpan>();
            var named = new List<string>();
            // Runs on the log's writer thread: it throws nothing but the refusal it stands for (an assertion thrown here would end the
            // writer, not the test), and what it saw is asserted after.
            void Refuse(string live)
            {
                lock (tries)
                {
                    named.Add(live);
                    tries.Add(clock.Elapsed);
                    if (tries.Count <= 3)
                        throw new IOException("the move aside was refused");
                }
            }
            // Past the heading's length and 200 bytes: the first line (300 bytes of padding after the heading) rolls, the short line
            // after it does not.
            var rollBytes = System.Text.Encoding.UTF8.GetByteCount(NodeLogProvider.Heading) + 200;
            using var log = new NodeLogProvider(path, LogLevel.Information, rollBytes, rolling: Refuse);
            var logger = log.CreateLogger("PlayMap.Test");
            var pad = new string('x', 300);
            logger.LogInformation("the first line {Pad}", pad);

            // No further line: the roll lands on its own after 100, 200 and 400 ms (the waits doubling), well inside 10 s.
            var landed = await Eventually.TrueAsync(() => !log.RollOwed && File.Exists(rolled) && !File.Exists(path));
            Assert.True(landed, landed ? "" : $"the roll did not land: tried {tries.Count} times; owed {log.RollOwed}; {Found(log, path, rolled)}");
            TimeSpan[] at;
            lock (tries)
            {
                at = [.. tries];
                Assert.All(named, live => Assert.Equal(path, live));
            }
            // Three refusals by the seam; on Windows the real move after them can be refused too, by a handle on the file just closed
            // that shares no delete (a scanner's), and is then counted and tried again the same way.
            var failed = log.RollsFailed;
            Assert.Equal(at.Length - 1, failed);
            if (OperatingSystem.IsWindows())
                Assert.True(failed >= 3, string.Create(CultureInfo.InvariantCulture, $"{failed} rolls failed"));
            else
                Assert.Equal(3, failed);
            Assert.Equal(0, log.Dropped);
            // Each wait is at least its share of the doubling (up to the longest), less a tick of the coarsest clock the writer may read
            // (16 ms on Windows) on either end.
            for (var i = 1; i < at.Length; i++)
            {
                var doubled = NodeLogProvider.FirstRetry * Math.Pow(2, i - 1);
                var wanted = (doubled < NodeLogProvider.LongestRetry ? doubled : NodeLogProvider.LongestRetry) - TimeSpan.FromMilliseconds(32);
                Assert.True(at[i] - at[i - 1] >= wanted, string.Create(CultureInfo.InvariantCulture,
                    $"try {i + 1} came {(at[i] - at[i - 1]).TotalMilliseconds:0} ms after the one before, not {wanted.TotalMilliseconds:0} ms or more"));
            }
            var lines = Lines(rolled);
            Assert.Equal(2, lines.Length);
            Assert.True(IsHeading(lines[0]), "the file begins: " + lines[0]);
            Assert.EndsWith(" info PlayMap.Test: the first line " + pad, lines[1], StringComparison.Ordinal);

            logger.LogInformation("after the roll");
            var started = await Eventually.TrueAsync(() => LinesIfThere(path).Length == 2);
            Assert.True(started, $"the next line did not start a live file: {Found(log, path, rolled)}");
            var live = Lines(path);
            Assert.True(IsHeading(live[0]), "the new file begins: " + live[0]);
            Assert.EndsWith(" info PlayMap.Test: after the roll", live[1], StringComparison.Ordinal);
            Assert.Equal(lines, Lines(rolled));
            Assert.Equal((failed, 0L, false), (log.RollsFailed, log.Dropped, log.RollOwed));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>On Windows, the ".1" file held open by a handle that shares no delete (as a viewer or a scanner may hold it) when the
    /// live file rolls. The roll is a POSIX-semantics rename (<c>DurableFile.Replace</c>); a target held without FILE_SHARE_DELETE makes
    /// it fail, as <c>DurableFileTests.AReaderThatSharesNoDeleteMakesTheReplaceFailAloudOnWindows</c> holds on the Windows job: the
    /// roll is counted failed and owed, no line is dropped, the line stays in the live file and ".1" keeps its own bytes. Should the
    /// rename land over the held handle instead (Microsoft's words for FILE_RENAME_POSIX_SEMANTICS allow a replace "even if there are
    /// existing handles to it" and do not name the share mode), the test says so and holds that: ".1" by name holds the heading and the
    /// line, and the held handle still reads the bytes it opened ("existing handles to the replaced file continue to be valid"). Then
    /// the handle is let go and no further line is logged: the roll lands within its longest wait.</summary>
    [WindowsFact]
    public async Task ARollRefusedByAHeldFileLandsOnceTheHandleIsLetGoWithoutAnotherLine()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-log-");
        try
        {
            var path = Path.Combine(dir.FullName, NodeLogProvider.FileName);
            var rolled = path + ".1";
            var held = new FileStream(rolled, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            try
            {
                held.Write("held\n"u8);
                held.Flush();
                using var log = new NodeLogProvider(path, LogLevel.Information, rollBytes: 1);
                log.CreateLogger("PlayMap.Test").LogInformation("the held line");

                // The writer has taken the line and tried the roll: it failed, or it landed over the held handle.
                var tried = await Eventually.TrueAsync(() => log.RollsFailed >= 1 || Lines(rolled) is not ["held"]);
                Assert.True(tried, $"the roll was not tried: {Found(log, path, rolled)}");
                // Read in this order: the roll owed and the live file before ".1". If ".1" by name still holds the test's bytes after,
                // the roll had not landed when the first two were read.
                var owed = log.RollOwed;
                var live = LinesIfThere(path);
                var dropped = log.Dropped;
                var byName = Lines(rolled);
                if (byName is ["held"])
                {
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"the held '.1' refused the roll: {log.RollsFailed} failed, owed {owed}"));
                    Assert.True(owed, "a failed roll is owed");
                    Assert.Equal(0, dropped);
                    Assert.Equal(2, live.Length);
                    Assert.True(IsHeading(live[0]), "the live file begins: " + live[0]);
                    Assert.EndsWith(" info PlayMap.Test: the held line", live[1], StringComparison.Ordinal);
                }
                else
                {
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"the POSIX rename replaced the held '.1': {log.RollsFailed} failed before it landed"));
                    Assert.Equal(2, byName.Length);
                    Assert.True(IsHeading(byName[0]), "'.1' begins: " + byName[0]);
                    Assert.EndsWith(" info PlayMap.Test: the held line", byName[1], StringComparison.Ordinal);
                    held.Position = 0;
                    using var old = new StreamReader(held, leaveOpen: true);
                    Assert.Equal("held\n", old.ReadToEnd());
                }
                await held.DisposeAsync();

                // No further line: the owed roll lands on its own wait, at most its longest after the handle is let go.
                var landed = await Eventually.TrueAsync(() => !log.RollOwed && !File.Exists(path) && Lines(rolled).Length == 2,
                    timeoutMs: (int)(NodeLogProvider.LongestRetry * 3).TotalMilliseconds);
                Assert.True(landed, landed ? "" : $"the roll did not land once the handle was let go: owed {log.RollOwed}; {Found(log, path, rolled)}");
                var lines = Lines(rolled);
                Assert.True(IsHeading(lines[0]), "'.1' begins: " + lines[0]);
                Assert.EndsWith(" info PlayMap.Test: the held line", lines[1], StringComparison.Ordinal);
                Assert.Equal(0, log.Dropped);
            }
            finally
            {
                await held.DisposeAsync();
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>A log whose folder cannot be made (a file stands at its name) loses each line it is given and counts each once in
    /// <see cref="NodeLogProvider.Dropped"/>; no roll is owed or counted failed.</summary>
    [Fact]
    public void ALineTheFolderCannotTakeIsCountedDroppedOnce()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-log-");
        try
        {
            var blocker = Path.Combine(dir.FullName, "not-a-folder");
            File.WriteAllText(blocker, "a file where the log's folder would be");
            var log = new NodeLogProvider(Path.Combine(blocker, NodeLogProvider.FileName));
            using (log)
            {
                var logger = log.CreateLogger("PlayMap.Test");
                logger.LogInformation("the first line");
                logger.LogInformation("the second line");
            }
            // Disposing let the writer finish every line it was given before it returned.
            Assert.Equal((2L, 0L, false), (log.Dropped, log.RollsFailed, log.RollOwed));
            Assert.False(Directory.Exists(blocker));
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
