using System.Globalization;
using System.Text;

namespace PlayMap.Supervisor.Status;

/// <summary>The supervisor's log (<c>supervisor.log</c> beside its status file): one line per act, stamped in UTC; at 1 MB the file
/// is moved to <c>supervisor.log.1</c> (the one before it dropped), so the log never fills the disk.</summary>
public static class SupervisorLog
{
    public const string FileName = "supervisor.log";
    public const long MaxBytes = 1024 * 1024;

    public static void Append(string folder, string line)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " " + line + "\n",
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is the supervisor's memory of its acts, never a reason to stop acting.
            System.Diagnostics.Trace.WriteLine("PlayMap.Supervisor: " + line + " (the log could not be written: " + ex.Message + ")");
        }
    }
}
