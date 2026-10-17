using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.Versioning;

namespace PlayMap.Core.Storage;

/// <summary>A file written beside its place and renamed over it reaches the disk before the rename, and the rename reaches it before
/// the call returns: a power cut after the rename finds the new file whole, never a name pointing at blocks the disk never got (a
/// journalling file system may commit the rename before the data), and never the old file back once the call returned. On Unix the
/// folder that holds the name is flushed after the rename; on Windows the rename is written through. Every file the node renames into
/// place whose loss costs more than doing it again goes through <see cref="MoveIntoPlace"/>, every folder through
/// <see cref="MoveFolderIntoPlace"/>.</summary>
public static partial class DurableFile
{
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileCopyAllowed = 0x2;
    private const uint MoveFileWriteThrough = 0x8;
    private const int Eintr = 4;
    private const int Einval = 22;

    // O_RDONLY | O_CLOEXEC: no process started meanwhile inherits the folder's descriptor.
    private static readonly int ReadFolder = OperatingSystem.IsLinux() ? 0x80000 : OperatingSystem.IsMacOS() ? 0x1000000 : 0;

    /// <summary>Raised after a file's (or a folder's files') bytes reached the disk and before it is renamed into place, with its final
    /// path (any thread).</summary>
    public static event Action<string>? Flushed;

    /// <summary>Flushes <paramref name="temp"/> to the disk, then renames it to <paramref name="path"/> and flushes the rename.</summary>
    public static void MoveIntoPlace(string temp, string path, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(temp);
        ArgumentException.ThrowIfNullOrEmpty(path);
        FlushFile(temp);
        Land(temp, path, overwrite);
    }

    private static void Land(string temp, string path, bool overwrite)
    {
        Flushed?.Invoke(path);
        if (OperatingSystem.IsWindows())
        {
            MoveThrough(temp, path, MoveFileWriteThrough | MoveFileCopyAllowed | (overwrite ? MoveFileReplaceExisting : 0));
            return;
        }
        File.Move(temp, path, overwrite);
        SyncFolder(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    /// <summary>Flushes every file in the folder <paramref name="temp"/> (written whole beside its place) and the folder's own entries,
    /// then moves it to <paramref name="path"/> and flushes the move. Throws when <paramref name="path"/> exists; nothing moves.</summary>
    public static void MoveFolderIntoPlace(string temp, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(temp);
        ArgumentException.ThrowIfNullOrEmpty(path);
        foreach (var file in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
            FlushFile(file);
        if (!OperatingSystem.IsWindows())
        {
            foreach (var folder in Directory.EnumerateDirectories(temp, "*", SearchOption.AllDirectories))
                SyncFolder(folder);
            SyncFolder(temp);
        }
        Flushed?.Invoke(path);
        if (OperatingSystem.IsWindows())
        {
            MoveThrough(temp, path, MoveFileWriteThrough);
            return;
        }
        Directory.Move(temp, path);
        SyncFolder(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    /// <summary>Writes <paramref name="text"/> (UTF-8) beside <paramref name="path"/> and moves it into place, flushed.</summary>
    public static void WriteAllText(string path, string text, string suffix = ".saving")
    {
        ArgumentNullException.ThrowIfNull(text);
        WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), suffix);
    }

    /// <summary>Writes <paramref name="bytes"/> beside <paramref name="path"/> and moves it into place, flushed.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes, string suffix = ".saving")
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var temp = path + suffix;
        // Flushed through the handle that wrote it: no second open for another process's handle to refuse.
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        Land(temp, path, overwrite: true);
    }

    private static void FlushFile(string path)
    {
        // Write access: Windows flushes a file's buffers only through a handle that may write it. Opening it does not change it.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        file.Flush(flushToDisk: true);
    }

    /// <summary>Flushes a folder's entries (the names in it) to the disk. A file system that keeps no folder to flush (EINVAL) has
    /// nothing to flush.</summary>
    private static void SyncFolder(string folder)
    {
        var fd = Open(folder, ReadFolder);
        if (fd < 0)
            throw new IOException($"the folder {folder} could not be opened to flush its names: {Marshal.GetLastPInvokeErrorMessage()}");
        try
        {
            int done;
            while ((done = Fsync(fd)) != 0 && Marshal.GetLastPInvokeError() == Eintr)
            {
                // Interrupted by a signal before it finished: asked again.
            }
            if (done != 0 && Marshal.GetLastPInvokeError() != Einval)
                throw new IOException($"the folder {folder} could not flush its names to the disk: {Marshal.GetLastPInvokeErrorMessage()}");
        }
        finally
        {
            _ = Close(fd);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void MoveThrough(string from, string to, uint flags)
    {
        if (MoveFileEx(LongPath(from), LongPath(to), flags))
            return;
        var error = Marshal.GetLastPInvokeError();
        var words = $"could not move {from} to {to}: {Marshal.GetPInvokeErrorMessage(error)}";
        throw error switch
        {
            2 => new FileNotFoundException(words, from),
            3 => new DirectoryNotFoundException(words),
            5 => new UnauthorizedAccessException(words),
            _ => new IOException(words, unchecked((int)0x80070000) | error),
        };
    }

    /// <summary>A full path Windows' own calls take at any length (the \\?\ form past MAX_PATH).</summary>
    private static string LongPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < 260 || full.StartsWith(@"\\?\", StringComparison.Ordinal))
            return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existing, string replacement, uint flags);

    // open(2) is variadic; without O_CREAT its mode is never read, so the two fixed arguments are the whole call on every ABI.
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Close(int fd);
}
