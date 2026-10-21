using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Windows;

/// <summary>The machine's folders (<see cref="MachinePlan.Folders"/>), made and given their access by the elevated install: each owned
/// by administrators — one the install makes is given to them, and one that was there before it is used only when SYSTEM or
/// administrators own it: one another account made is refused with words, never taken over, for its maker may hold it open with leave
/// to change its access, which no later change of its owner or access takes back; the PlayMap folder's access its own — SYSTEM and
/// administrators full control, every account read — and every folder below it inheriting that alone but for the writer's Modify on
/// what the folders the show user writes (the dumps and the engine's folder) hold, and read-and-write but not delete on those folders
/// themselves (so the writer may make and replace files in them but neither move nor delete the folder). Each folder's owner and
/// access are set through the one handle its reparse point was checked on, and that handle is held open until the install is done, so
/// the owner and the access are made and granted through the checked folder itself and never through a link that replaced it: a folder
/// that is a link (a junction or a symbolic link) is refused with words, and the check and the write are the same handle. In the
/// supervisor's folder, what an earlier layout let another account place there — a link, a file or an empty folder another account
/// owns, the pid record's earlier place — is removed, so SYSTEM writes only its own files; nothing inside a folder another account owns
/// is ever deleted, and such a folder that holds anything refuses the install with words.</summary>
[SupportedOSPlatform("windows")]
public static class MachineFolders
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private const int ErrorDirNotEmpty = 145;
    private const int ErrorAlreadyExists = 183;
    private const InheritanceFlags Inherited = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>The account that writes the show user's folders: the show user, or, when the install names none, INTERACTIVE
    /// (<see cref="MachinePlan.AnyInteractiveUserSid"/>). An account Windows cannot find throws IdentityNotMappedException.</summary>
    public static SecurityIdentifier WriterOf(string showUser)
    {
        ArgumentNullException.ThrowIfNull(showUser);
        return showUser.Length > 0
            ? (SecurityIdentifier)new NTAccount(showUser).Translate(typeof(SecurityIdentifier))
            : new SecurityIdentifier(MachinePlan.AnyInteractiveUserSid);
    }

    /// <summary>Makes the machine's folders under <paramref name="root"/> and sets their owner and access as the plan says, the show
    /// user's folders written by <see cref="WriterOf"/>; every explicit rule another install left is replaced. A folder that was there
    /// before this call and that neither SYSTEM nor administrators own is refused with words before anything is made inside it or set
    /// on it. Says what it did.</summary>
    public static void Make(string root, string showUser, Action<string> say)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(say);
        var writer = WriterOf(showUser);
        var held = new List<SafeFileHandle>();
        try
        {
            foreach (var plan in MachinePlan.Folders)
            {
                var path = plan.In(root);
                var made = MakeFolder(path);
                var handle = Hold(path);
                held.Add(handle);
                var owner = OwnerThrough(handle);
                var trusted = owner is not null && MachinePlan.OwnerTrusted(owner.Value);
                // A folder this call made, no other account held open with leave to change its access before: what is made in the
                // folder it lies in inherits that leave for no other account (%ProgramData% gives every account read and make, the
                // PlayMap folder SYSTEM and administrators alone), and its owner, the maker's token's default, is given to
                // administrators. One that was there already and that neither owns is refused: its owner may hold it open with leave
                // to change its access, which no owner or access set here takes back.
                if (!made && !trusted)
                    throw new InvalidOperationException(path + " was made by " + (owner?.Value ?? "an account Windows does not name")
                        + ", neither SYSTEM nor administrators: the install takes over no folder another account made, for that account may "
                        + "keep it open to change its access after the install — remove it, then run the installer again");
                var security = new DirectorySecurity();
                bool isProtected;
                if (plan.Under.Count == 0)
                {
                    isProtected = true;
                    security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, Inherited, PropagationFlags.None, AccessControlType.Allow));
                    security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, Inherited, PropagationFlags.None, AccessControlType.Allow));
                    security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, Inherited, PropagationFlags.None, AccessControlType.Allow));
                }
                else
                {
                    isProtected = false;
                    if (plan.ShowUserWrites)
                    {
                        // What the folder holds: Modify (so the writer makes, replaces and deletes the dumps and the pid record).
                        security.AddAccessRule(new FileSystemAccessRule(writer, FileSystemRights.Modify, Inherited, PropagationFlags.InheritOnly, AccessControlType.Allow));
                        // The folder itself: read and write but not delete, so the writer may make and replace files in it and neither
                        // move nor delete the folder out of the supervisor's own (Delete would let it do either).
                        security.AddAccessRule(new FileSystemAccessRule(writer, FileSystemRights.ReadAndExecute | FileSystemRights.Write, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                    }
                }
                // The owner and the DACL are set through the same handle the reparse point was checked on (Hold), so neither is set
                // through a link that may have replaced the folder by name after the check.
                ApplySecurity(handle, path, trusted ? null : BinaryForm(Administrators), DaclBytes(security), isProtected);
                say("folder " + path + " (" + plan.Holds + "): " + (plan.ShowUserWrites
                    ? (showUser.Length > 0 ? showUser : "any signed-in user") + " may write it"
                    : "only SYSTEM and administrators may write it"));
            }
            RemovePlaced(root, say);
        }
        finally
        {
            foreach (var handle in held)
                handle.Dispose();
        }
    }

    /// <summary>Takes back the write access to the show user's folders under <paramref name="root"/> (every explicit rule on them is
    /// replaced by an empty DACL, so only the inherited read access the PlayMap folder grants is left), leaving the folders, which then
    /// only SYSTEM and administrators may change — though the show user keeps whatever it already owns among what they hold. The empty
    /// DACL is set through the same handle the folder's reparse point was checked on, so it is never set through a link. A folder not
    /// there is passed by.</summary>
    public static void TakeBack(string root, Action<string> say)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(say);
        foreach (var plan in MachinePlan.Folders.Where(f => f.ShowUserWrites))
        {
            var path = plan.In(root);
            if (!Directory.Exists(path))
                continue;
            using var held = Hold(path);
            ApplySecurity(held, path, owner: null, EmptyDacl(), isProtected: false);
            say("folder " + path + ": the show user's write access taken back");
        }
    }

    /// <summary>Makes the folder at <paramref name="path"/> in its parent, which is there: true when this call made it, false when a
    /// name was there already (a folder, a link or a file, which <see cref="Hold"/> then takes or refuses). Windows answers which in
    /// the one call, so no account can make the folder between a look and the make.</summary>
    private static bool MakeFolder(string path)
    {
        if (Native.CreateDirectory(path, 0))
            return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorAlreadyExists)
            return false;
        throw new Win32Exception(error, path + " could not be made: " + new Win32Exception(error).Message);
    }

    /// <summary>The folder at <paramref name="path"/>, held open by its own handle with the access to read and set its owner and DACL
    /// and without leave to delete or rename it (and so without leave to replace it), never through a link: a folder that is one, or a
    /// name that is no folder, is refused with words. The owner and the access are then set through this same handle, so the reparse
    /// point this checks and the write are one handle on one folder.</summary>
    private static SafeFileHandle Hold(string path)
    {
        var handle = Native.CreateFile(path, Native.FileReadAttributes | Native.ReadControl | Native.WriteDac | Native.WriteOwner,
            Native.FileShareRead | Native.FileShareWrite, 0, Native.OpenExisting,
            Native.FileFlagBackupSemantics | Native.FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, path + " could not be held open: " + new Win32Exception(error).Message);
        }
        var attributes = File.GetAttributes(handle);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new InvalidOperationException(path + " is a link (a junction or a symbolic link): the install makes and grants nothing through it — remove it, then run the installer again");
        }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            handle.Dispose();
            throw new InvalidOperationException(path + " is a file, not a folder: the install makes the machine's folders there — remove it, then run the installer again");
        }
        return handle;
    }

    /// <summary>Sets the owner (when <paramref name="owner"/> is given) and the DACL of the folder <paramref name="handle"/> holds,
    /// through that handle (so neither is set by a name that a link may now resolve elsewhere than the checked folder). The reparse
    /// point is checked once more on the very handle the write goes through. A protected DACL inherits nothing; an unprotected one adds
    /// the inheritable access the folder above grants.</summary>
    private static void ApplySecurity(SafeFileHandle handle, string path, byte[]? owner, byte[] dacl, bool isProtected)
    {
        if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(path + " became a link while it was held: the install makes and grants nothing through it — remove it, then run the installer again");
        var info = Native.DaclSecurityInformation | (isProtected ? Native.ProtectedDaclSecurityInformation : Native.UnprotectedDaclSecurityInformation);
        if (owner is not null)
            info |= Native.OwnerSecurityInformation;
        var error = Native.SetSecurityInfo(handle, Native.SeFileObject, info, owner, null, dacl, null);
        if (error != 0)
            throw new Win32Exception((int)error, path + " could not be given its access: " + new Win32Exception((int)error).Message);
    }

    /// <summary>The owner of the folder <paramref name="handle"/> holds, read through that handle; null when it may not be read (which
    /// no trusted owner is).</summary>
    private static SecurityIdentifier? OwnerThrough(SafeFileHandle handle)
    {
        var error = Native.GetSecurityInfo(handle, Native.SeFileObject, Native.OwnerSecurityInformation,
            out var owner, out _, out _, out _, out var descriptor);
        try
        {
            return error == 0 && owner != 0 ? new SecurityIdentifier(owner) : null;
        }
        finally
        {
            if (descriptor != 0)
                Native.LocalFree(descriptor);
        }
    }

    /// <summary>The self-relative binary SID of <paramref name="sid"/>, for SetSecurityInfo's owner.</summary>
    private static byte[] BinaryForm(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    /// <summary>The DACL of <paramref name="security"/> in binary form, for SetSecurityInfo: the explicit rules it holds, present even
    /// when there are none (a present empty DACL, which inherits but grants nothing of its own — never a null DACL, which would grant
    /// everyone everything).</summary>
    private static byte[] DaclBytes(DirectorySecurity security)
    {
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var dacl = raw.DiscretionaryAcl ?? new RawAcl(GenericAcl.AclRevision, 0);
        var bytes = new byte[dacl.BinaryLength];
        dacl.GetBinaryForm(bytes, 0);
        return bytes;
    }

    /// <summary>A present empty DACL (not a null one), for TakeBack: no explicit access, the folder's inherited read access left.</summary>
    private static byte[] EmptyDacl()
    {
        var dacl = new RawAcl(GenericAcl.AclRevision, 0);
        var bytes = new byte[dacl.BinaryLength];
        dacl.GetBinaryForm(bytes, 0);
        return bytes;
    }

    /// <summary>Removes from the supervisor's folder what SYSTEM would act on there but did not make: a link, a file or folder another
    /// account owns (the planned folders aside), and the pid record's earlier place beside the status file. A folder another account
    /// owns stays that account's to change while the install runs — its owner may swap anything inside it for a junction between a
    /// delete's look at a name and its open of it, turning a delete that walks the folder by path onto the junction's target — so no
    /// folder is walked: each is removed by RemoveDirectory alone (<see cref="RemoveFolder"/>).</summary>
    private static void RemovePlaced(string root, Action<string> say)
    {
        var supervisor = MachinePlan.Folders.Single(f => f.Under is ["supervisor"]).In(root);
        var planned = MachinePlan.Folders.Select(f => Path.GetFullPath(f.In(root))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(supervisor))
        {
            if (planned.Contains(Path.GetFullPath(entry)))
                continue;
            var attributes = File.GetAttributes(entry);
            var link = (attributes & FileAttributes.ReparsePoint) != 0;
            var directory = (attributes & FileAttributes.Directory) != 0;
            var earlier = !directory && string.Equals(Path.GetFileName(entry), EnginePidRecord.FileName, StringComparison.OrdinalIgnoreCase);
            var owner = link ? "" : OwnerOf(entry, directory);
            if (!link && !earlier && MachinePlan.OwnerTrusted(owner))
                continue;
            if (directory)
                RemoveFolder(entry, owner);
            else
                File.Delete(entry);
            say("removed " + entry + (link ? ", a link" : earlier ? ", the pid record's earlier place" : ", which " + (owner.Length > 0 ? owner : "an account Windows does not name") + " placed there"));
        }
    }

    /// <summary>Removes a link (a junction or a directory symbolic link) or an empty folder by RemoveDirectory, which removes a link
    /// itself whatever its target holds and deletes nothing inside a folder: a folder that holds anything is refused with words
    /// (ERROR_DIR_NOT_EMPTY), and the install deletes nothing of what it holds.</summary>
    private static void RemoveFolder(string path, string owner)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == ErrorDirNotEmpty)
        {
            throw new InvalidOperationException(path + " is a folder " + (owner.Length > 0 ? owner : "an account Windows does not name")
                + " placed in the supervisor's folder, and it holds what that account may change: the install deletes nothing inside a "
                + "folder another account controls — remove it, then run the installer again", ex);
        }
    }

    /// <summary>The SID of what owns a file or folder ("" when its owner may not be read, which no trusted owner is).</summary>
    private static string OwnerOf(string path, bool directory)
    {
        try
        {
            var security = directory
                ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            return (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ?? "";
        }
        catch (UnauthorizedAccessException)
        {
            // An owner administrators may not read is not one of theirs.
            return "";
        }
    }
}
