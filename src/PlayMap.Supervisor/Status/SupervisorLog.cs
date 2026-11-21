using System.Globalization;
using System.Text;
using PlayMap.Core.Storage;

namespace PlayMap.Supervisor.Status;

/// <summary>The supervisor's log (<c>supervisor.log</c> beside its status file): one line per act, stamped in UTC; past 1 MB the file
/// is moved to <c>supervisor.log.1</c> (the one before it dropped; replaced through <see cref="DurableFile.Replace"/>), so the log never
/// fills the disk. A move that fails (on Windows, a handle on either file that shares no delete) costs no line: the line is appended to
/// the file where it is, and the next line's append tries the move again. The appends are the log's only writer, a static with no
/// thread of its own, so a move is never tried between two lines.</summary>
public static class SupervisorLog
{
    public const string FileName = "supervisor.log";
    public const long MaxBytes = 1024 * 1024;

    /// <summary>Raised with the log's path before each move aside (any thread): an <see cref="IOException"/> from a handler stands for
    /// the move refused (a test fails a roll here).</summary>
    internal static event Action<string>? Rolling;

    public static void Append(string folder, string line)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            Roll(path);
            File.AppendAllText(path, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " " + line + "\n",
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is the supervisor's memory of its acts, never a reason to stop acting.
            System.Diagnostics.Trace.WriteLine("PlayMap.Supervisor: " + line + " (the log could not be written: " + ex.Message + ")");
        }
    }

    /// <summary>Moves a log past <see cref="MaxBytes"/> aside. A move that fails leaves the file where it is, for the line being
    /// appended and the next append's try.</summary>
    private static void Roll(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= MaxBytes)
                return;
            Rolling?.Invoke(path);
            DurableFile.Replace(path, path + ".1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("PlayMap.Supervisor: the log could not be moved aside, the next line tries again: " + ex.Message);
        }
    }
}
