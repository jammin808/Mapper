using PlayMap.Supervisor.Status;

namespace PlayMap.Supervisor.Tests.Status;

/// <summary>The supervisor's log past its size moves aside to ".1" before the next line; a move that is refused costs no line (the line
/// is appended to the file where it is) and the next line's append moves it.</summary>
public sealed class SupervisorLogTests
{
    private static string Oversize(string folder)
    {
        var path = Path.Combine(folder, SupervisorLog.FileName);
        File.WriteAllText(path, new string('x', (int)SupervisorLog.MaxBytes) + "\nthe last old line\n");
        return path;
    }

    [Fact]
    public void ALogPastItsSizeMovesAsideBeforeTheNextLine()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-supervisor-log-").FullName;
        try
        {
            var path = Oversize(dir);
            File.WriteAllText(path + ".1", "the generation before");
            SupervisorLog.Append(dir, "the new line");
            Assert.EndsWith("the last old line\n", File.ReadAllText(path + ".1"), StringComparison.Ordinal);
            var live = File.ReadAllLines(path);
            Assert.EndsWith(" the new line", Assert.Single(live), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ARefusedMoveCostsNoLineAndTheNextAppendMovesIt()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-supervisor-log-").FullName;
        var path = Path.Combine(dir, SupervisorLog.FileName);
        var refused = 0;
        void Refuse(string log)
        {
            // Other tests in this process append to their own folders: only this one's first move is refused.
            if (log == path && Interlocked.Increment(ref refused) == 1)
                throw new IOException("the move aside was refused");
        }
        SupervisorLog.Rolling += Refuse;
        try
        {
            Oversize(dir);
            SupervisorLog.Append(dir, "the line while the move is refused");
            Assert.Equal(1, refused);
            Assert.False(File.Exists(path + ".1"));
            var kept = File.ReadAllLines(path);
            Assert.Equal("the last old line", kept[^2]);
            Assert.EndsWith(" the line while the move is refused", kept[^1], StringComparison.Ordinal);

            SupervisorLog.Append(dir, "the next line");
            Assert.Equal(2, refused);
            var rolled = File.ReadAllLines(path + ".1");
            Assert.EndsWith(" the line while the move is refused", rolled[^1], StringComparison.Ordinal);
            var live = File.ReadAllLines(path);
            Assert.EndsWith(" the next line", Assert.Single(live), StringComparison.Ordinal);
        }
        finally
        {
            SupervisorLog.Rolling -= Refuse;
            Directory.Delete(dir, recursive: true);
        }
    }
}
