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
/// by administrators (a folder another account made is taken back from it); the PlayMap folder's access its own — SYSTEM and
/// administrators full control, every account read — and every folder below it inheriting that alone but for the writer's Modify on
/// the folders the show user writes (the dumps and the engine's folder). Each folder is held open by its own handle while its access
/// is set, so it is neither renamed nor replaced under the call, and one that is a link (a junction or a symbolic link) is refused
/// with words: nothing is made or granted through it. In the supervisor's folder, what an earlier layout let another account place
/// there — a link, a file another account owns, the pid record's earlier place — is removed, so SYSTEM writes only its own files.</summary>
[SupportedOSPlatform("windows")]
public static class MachineFolders
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
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
    /// user's folders written by <see cref="WriterOf"/>; every explicit rule another install left is replaced. Says what it did.</summary>
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
                Directory.CreateDirectory(path);
                held.Add(Hold(path));
                var info = new DirectoryInfo(path);
                var security = info.GetAccessControl();
                if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !MachinePlan.OwnerTrusted(owner.Value))
                    security.SetOwner(Administrators);
                RemoveExplicit(security);
                if (plan.Under.Count == 0)
                {
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, Inherited, PropagationFlags.None, AccessControlType.Allow));
                    security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, Inherited, PropagationFlags.None, AccessControlType.Allow));
                    security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, Inherited, PropagationFlags.None, AccessControlType.Allow));
                }
                else
                {
                    security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    if (plan.ShowUserWrites)
                        security.AddAccessRule(new FileSystemAccessRule(writer, FileSystemRights.Modify, Inherited, PropagationFlags.None, AccessControlType.Allow));
                }
                info.SetAccessControl(security);
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

    /// <summary>Takes back the write access to the show user's folders under <paramref name="root"/> (every explicit rule on them),
    /// leaving the folders and what they hold, which then only SYSTEM and administrators may change. A folder not there is passed by.</summary>
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
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            RemoveExplicit(security);
            info.SetAccessControl(security);
            say("folder " + path + ": the show user's write access taken back");
        }
    }

    /// <summary>The folder at <paramref name="path"/>, held open by its own handle without leave to delete or rename it (and so
    /// without leave to replace it), never through a link: a folder that is one is refused with words.</summary>
    private static SafeFileHandle Hold(string path)
    {
        var handle = Native.CreateFile(path, Native.FileReadAttributes, Native.FileShareRead | Native.FileShareWrite, 0, Native.OpenExisting,
            Native.FileFlagBackupSemantics | Native.FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, path + " could not be held open: " + new Win32Exception(error).Message);
        }
        if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new InvalidOperationException(path + " is a link (a junction or a symbolic link): the install makes and grants nothing through it — remove it, then run the installer again");
        }
        return handle;
    }

    private static void RemoveExplicit(DirectorySecurity security)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
            security.RemoveAccessRuleSpecific(rule);
    }

    /// <summary>Removes from the supervisor's folder what SYSTEM would act on there but did not make: a link, a file or folder another
    /// account owns (the planned folders aside), and the pid record's earlier place beside the status file.</summary>
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
                Directory.Delete(entry, recursive: !link);
            else
                File.Delete(entry);
            say("removed " + entry + (link ? ", a link" : earlier ? ", the pid record's earlier place" : ", which " + (owner.Length > 0 ? owner : "an account Windows does not name") + " placed there"));
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
