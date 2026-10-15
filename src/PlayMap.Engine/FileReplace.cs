using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PlayMap.Engine;

/// <summary>Moves a file written aside over the one it replaces, whole and at once. On Windows the move is a POSIX-semantics rename
/// (Windows 10 1709 and later, on NTFS), so — as on Linux — a reader holding the old file and sharing delete (the node's own read,
/// the desk reading the token) never refuses it; an ordinary replace is refused while any reader holds the file. Where the file
/// system has no POSIX rename (FAT, some shares) the move is the ordinary replace, and its refusal says why.</summary>
internal static partial class FileReplace
{
    private const uint DeleteAccess = 0x0001_0000;
    private const uint SynchronizeAccess = 0x0010_0000;
    private const uint ShareReadWriteDelete = 0x7;
    private const uint OpenExisting = 3;
    private const int FileRenameInfoEx = 22;
    private const uint ReplaceIfExists = 0x1;
    private const uint PosixSemantics = 0x2;

    public static void Over(string source, string destination)
    {
        if (OperatingSystem.IsWindows() && PosixRename(Path.GetFullPath(source), Path.GetFullPath(destination)))
            return;
        File.Move(source, destination, overwrite: true);
    }

    [SupportedOSPlatform("windows")]
    private static unsafe bool PosixRename(string source, string destination)
    {
        using var file = CreateFile(source, DeleteAccess | SynchronizeAccess, ShareReadWriteDelete, 0, OpenExisting, 0, 0);
        if (file.IsInvalid)
            return false;
        // FILE_RENAME_INFO: Flags (the union's DWORD), RootDirectory (a handle, none here), FileNameLength (bytes), FileName.
        var lengthAt = 2 * IntPtr.Size;
        var nameAt = lengthAt + sizeof(uint);
        var nameBytes = destination.Length * sizeof(char);
        var info = new byte[nameAt + nameBytes + sizeof(char) + IntPtr.Size];
        fixed (byte* b = info)
        fixed (char* name = destination)
        {
            *(uint*)b = ReplaceIfExists | PosixSemantics;
            *(uint*)(b + lengthAt) = (uint)nameBytes;
            Buffer.MemoryCopy(name, b + nameAt, nameBytes, nameBytes);
            return SetFileInformationByHandle(file, FileRenameInfoEx, b, (uint)info.Length);
        }
    }

    [SupportedOSPlatform("windows")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [SupportedOSPlatform("windows")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* info, uint size);
}
