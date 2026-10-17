using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.Versioning;

namespace PlayMap.Core.Storage;

/// <summary>A file written beside its place and renamed over it reaches the disk before the rename, and the rename reaches it before
/// the call returns: a power cut after the rename finds the new file whole, never a name pointing at blocks the disk never got (a
/// journalling file system may commit the rename before the data), and never the old file back once the call returned. On Unix the
/// folder that holds the name is flushed after the rename; on Windows the rename is written through. A reader of a file that holds the
/// node's state never makes its replace fail: every such reader opens it through <see cref="OpenRead"/> (shared for write and delete),
/// and on Windows the replace is a POSIX-semantics rename, which proceeds over a file such a reader holds open (the reader keeps the old
/// file's bytes; the name is the new file's at once), as a rename does on Unix. The media, versions, models and meshes moved in through
/// it are read shared for reading only: on Windows a reader holding one open can make its replace fail, which then says so. Every file
/// the node renames into
/// place whose loss costs more than doing it again goes through <see cref="MoveIntoPlace"/>, every folder through
/// <see cref="MoveFolderIntoPlace"/>. A rename that landed but whose folder could not be flushed after throws
/// <see cref="NotFlushedException"/>: the new file is in place, only its durability is in doubt.</summary>
public static partial class DurableFile
{
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileCopyAllowed = 0x2;
    private const uint MoveFileWriteThrough = 0x8;
    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const int FileRenameInfoEx = 22;
    private const uint RenameReplaceIfExists = 0x1;
    private const uint RenamePosixSemantics = 0x2;
    private const uint RenameIgnoreReadOnly = 0x40;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotSupported = 50;
    private const int ErrorNotSameDevice = 17;
    private const int Eintr = 4;
    private const int Einval = 22;

    // O_RDONLY | O_CLOEXEC: no process started meanwhile inherits the folder's descriptor.
    private static readonly int ReadFolder = OperatingSystem.IsLinux() ? 0x80000 : OperatingSystem.IsMacOS() ? 0x1000000 : 0;

    /// <summary>Raised after a file's (or a folder's files') bytes reached the disk and before it is renamed into place, with its final
    /// path (any thread).</summary>
    public static event Action<string>? Flushed;

    /// <summary>Raised before a folder's names are flushed, with the folder (any thread): an <see cref="IOException"/> from a handler
    /// stands for the disk refusing the flush (a test fails a folder's flush here).</summary>
    internal static event Action<string>? FlushingFolder;

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
            if (!overwrite || !ReplaceThrough(temp, path))
                MoveThrough(temp, path, MoveFileWriteThrough | MoveFileCopyAllowed | (overwrite ? MoveFileReplaceExisting : 0));
            return;
        }
        File.Move(temp, path, overwrite);
        FlushLanded(path);
    }

    /// <summary>Flushes the folder of a file or folder just renamed into place: a failure here leaves the new one in place, and says so
    /// (<see cref="NotFlushedException"/>).</summary>
    private static void FlushLanded(string path)
    {
        try
        {
            SyncFolder(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
        catch (IOException ex)
        {
            throw new NotFlushedException(path, ex);
        }
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
        FlushLanded(path);
    }

    /// <summary>Opens a durable file to read, shared for write and delete: a replace of it (here or in another process) proceeds while
    /// the stream is open, and the stream goes on reading the bytes it opened.</summary>
    public static FileStream OpenRead(string path, int bufferSize = 4096, FileOptions options = FileOptions.None) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, options);

    /// <summary>A durable file's bytes, read as <see cref="OpenRead"/> opens it.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        using var file = OpenRead(path, bufferSize: 1);
        var length = file.Length;
        if (length > Array.MaxLength)
            throw new IOException($"{path} is too long to read whole");
        var bytes = new byte[length];
        var read = 0;
        int n;
        while (read < bytes.Length && (n = file.Read(bytes, read, bytes.Length - read)) > 0)
            read += n;
        if (read == bytes.Length && file.ReadByte() < 0)
            return bytes;
        // The file changed length under the read (written in place, not replaced): read it again as a stream.
        file.Position = 0;
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>A durable file's text (UTF-8, a byte-order mark skipped), read as <see cref="OpenRead"/> opens it.</summary>
    public static string ReadAllText(string path)
    {
        using var reader = new StreamReader(OpenRead(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>A durable file's text (UTF-8, a byte-order mark skipped), read as <see cref="OpenRead"/> opens it, asynchronously.</summary>
    public static async Task<string> ReadAllTextAsync(string path, CancellationToken ct = default)
    {
        using var reader = new StreamReader(OpenRead(path, options: FileOptions.Asynchronous), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A durable file's bytes, read as <see cref="OpenRead"/> opens it, asynchronously.</summary>
    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        await using var file = OpenRead(path, bufferSize: 1, FileOptions.Asynchronous);
        using var copy = new MemoryStream(file.Length is var n and > 0 and < int.MaxValue ? (int)n : 0);
        await file.CopyToAsync(copy, ct).ConfigureAwait(false);
        return copy.ToArray();
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
        FlushingFolder?.Invoke(folder);
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

    /// <summary>Renames <paramref name="from"/> over <paramref name="to"/> with POSIX semantics (Windows 10 1709 and later, on a file
    /// system that keeps them): the name is replaced while other handles that share delete hold the old file open. The rename is flushed
    /// through the same handle before the call returns. False when the file system does not take a POSIX rename (FAT, an older system)
    /// or the target is on another volume: the caller replaces it the classic way, which copies across volumes.</summary>
    [SupportedOSPlatform("windows")]
    private static bool ReplaceThrough(string from, string to)
    {
        using var handle = CreateFile(LongPath(from), Delete | GenericWrite | Synchronize, ShareAll, 0, OpenExisting, FileAttributeNormal, 0);
        if (handle.IsInvalid)
            throw Refused(from, to, Marshal.GetLastPInvokeError());
        var name = LongPath(to);
        // FILE_RENAME_INFO: Flags (a DWORD in a union with BOOLEAN), RootDirectory (a HANDLE, pointer-aligned), FileNameLength (bytes),
        // FileName (UTF-16, terminated).
        var handleAt = IntPtr.Size;
        var lengthAt = handleAt + IntPtr.Size;
        var nameAt = lengthAt + sizeof(uint);
        var nameBytes = name.Length * sizeof(char);
        var info = new byte[Math.Max(nameAt + nameBytes + sizeof(char), lengthAt + 2 * IntPtr.Size)];
        BitConverter.TryWriteBytes(info.AsSpan(lengthAt), (uint)nameBytes);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(info.AsSpan(nameAt));
        // Read-only targets are replaced too where the system knows the flag (Windows 10 1809 and later); before it, the same rename
        // without it.
        var renamed = Rename(handle, info, RenameReplaceIfExists | RenamePosixSemantics | RenameIgnoreReadOnly)
            || (Marshal.GetLastPInvokeError() == ErrorInvalidParameter && Rename(handle, info, RenameReplaceIfExists | RenamePosixSemantics));
        if (!renamed)
        {
            var error = Marshal.GetLastPInvokeError();
            if (FallsBackToMove(error))
                return false;
            throw Refused(from, to, error);
        }
        if (!FlushFileBuffers(handle))
            throw new NotFlushedException(to, new IOException(Marshal.GetLastPInvokeErrorMessage()));
        return true;
    }

    /// <summary>A POSIX rename's error that moved nothing and that the classic move makes good: a file system or a system that takes no
    /// POSIX rename, or a target on another volume (a rename never crosses one; the classic move copies, flushed through).</summary>
    internal static bool FallsBackToMove(int error) =>
        error is ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction or ErrorNotSameDevice;

    [SupportedOSPlatform("windows")]
    private static bool Rename(Microsoft.Win32.SafeHandles.SafeFileHandle handle, byte[] info, uint flags)
    {
        BitConverter.TryWriteBytes(info.AsSpan(0), flags);
        return SetFileInformationByHandle(handle, FileRenameInfoEx, info, (uint)info.Length);
    }

    private static Exception Refused(string from, string to, int error)
    {
        var words = $"could not move {from} to {to}: {Marshal.GetPInvokeErrorMessage(error)}";
        return error switch
        {
            2 => new FileNotFoundException(words, from),
            3 => new DirectoryNotFoundException(words),
            5 => new UnauthorizedAccessException(words),
            _ => new IOException(words, unchecked((int)0x80070000) | error),
        };
    }

    [SupportedOSPlatform("windows")]
    private static void MoveThrough(string from, string to, uint flags)
    {
        if (MoveFileEx(LongPath(from), LongPath(to), flags))
            return;
        throw Refused(from, to, Marshal.GetLastPInvokeError());
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

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, byte[] info,
        uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle file);

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

/// <summary>A file (or folder) renamed into place whose folder could not be flushed after the rename: the new one is in place — every
/// reader sees it, and a later start reads it — but a power cut before the folder reaches the disk may still bring the old one back.
/// An <see cref="IOException"/>, so a caller that only knows a write failed still hears it fail.</summary>
public sealed class NotFlushedException(string path, Exception inner)
    : IOException($"{path} is in place, but its folder could not be flushed to the disk: {inner.Message}", inner)
{
    /// <summary>The file (or folder) that is in place.</summary>
    public string FilePath { get; } = path;
}
