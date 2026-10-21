using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;
using PlayMap.Supervisor.Windows;

namespace PlayMap.Supervisor.Tests.Windows;

/// <summary>The machine's folders as the elevated install makes them, under a root of the test's own: each owned by SYSTEM or
/// administrators; the PlayMap folder's access its own (SYSTEM and administrators full control, every account read, nothing
/// inherited); the supervisor's folder with no rule of its own, so only SYSTEM and administrators may change what it holds; the dumps
/// and the engine's folder each letting the show user make and replace what they hold (Modify on their contents) but neither move nor
/// delete the folder itself (no Delete right on the folder); an earlier show user's rule replaced; and the uninstall's take-back
/// leaving no rule of the show user's. A folder that is a link is refused with words and nothing is granted through it; the owner and
/// the access are set through the one handle the reparse point was checked on, so a conversion to a link while the install runs lands
/// nothing on another folder, and what another layout placed in the supervisor's folder is removed — a folder another account owns only
/// when it is empty, nothing inside one ever deleted. A caller that is not elevated is refused: the install runs elevated.</summary>
[SupportedOSPlatform("windows")]
public sealed class MachineFoldersTests
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static List<FileSystemAccessRule> Explicit(string path) => new DirectoryInfo(path).GetAccessControl()
        .GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    private static List<FileSystemAccessRule> All(string path) => new DirectoryInfo(path).GetAccessControl()
        .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    private static bool Writes(FileSystemAccessRule r) => r.AccessControlType == AccessControlType.Allow
        && (r.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0;

    // A rule that applies to the folder object itself (not inherit-only), so it governs the folder, not only its children.
    private static bool OnTheFolder(FileSystemAccessRule r) => (r.PropagationFlags & PropagationFlags.InheritOnly) == 0;

    [WindowsFact]
    public void TheFoldersAreAdministratorsAndOnlyTheDumpsAndTheEnginesFolderAreTheShowUsersToWrite()
    {
        var root = Path.Combine(Directory.CreateTempSubdirectory("playmap-machine-").FullName, "PlayMap");
        var user = WindowsIdentity.GetCurrent().User!;
        var said = new List<string>();
        try
        {
            if (!Elevated)
            {
                // The PlayMap folder this account made is this account's: only an elevated caller may give it to administrators.
                Assert.ThrowsAny<Exception>(() => MachineFolders.Make(root, EngineIdentityWindowsTests.Place.ThisUser, said.Add));
                return;
            }
            // An earlier install's rule for another account (here, every user) on the engine's folder is replaced.
            Directory.CreateDirectory(Path.Combine(root, "supervisor", "engine"));
            var engineFolder = new DirectoryInfo(Path.Combine(root, "supervisor", "engine"));
            var earlier = engineFolder.GetAccessControl();
            earlier.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            engineFolder.SetAccessControl(earlier);

            MachineFolders.Make(root, EngineIdentityWindowsTests.Place.ThisUser, said.Add);

            foreach (var folder in MachinePlan.Folders)
            {
                var path = folder.In(root);
                var owner = (SecurityIdentifier)new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;
                Assert.True(MachinePlan.OwnerTrusted(owner.Value), path + " is owned by " + owner.Value);
                // Every rule that lets an account change what the folder holds is SYSTEM's, administrators' or, on the show user's
                // folders alone, the show user's.
                foreach (var rule in All(path).Where(Writes))
                {
                    var who = (SecurityIdentifier)rule.IdentityReference;
                    Assert.True(who.Equals(System) || who.Equals(Administrators) || (folder.ShowUserWrites && who.Equals(user)),
                        $"{path}: {who.Value} may {rule.FileSystemRights}");
                }
                Assert.Equal(folder.ShowUserWrites, All(path).Any(r => Writes(r) && r.IdentityReference.Equals(user)));
            }

            var top = new DirectoryInfo(root).GetAccessControl();
            Assert.True(top.AreAccessRulesProtected);
            var rules = Explicit(root);
            Assert.Equal(3, rules.Count);
            Assert.Contains(rules, r => r.IdentityReference.Equals(System) && r.FileSystemRights == FileSystemRights.FullControl);
            Assert.Contains(rules, r => r.IdentityReference.Equals(Administrators) && r.FileSystemRights == FileSystemRights.FullControl);
            Assert.Contains(rules, r => r.IdentityReference.Equals(Users) && (r.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute && !Writes(r));

            Assert.Empty(Explicit(Path.Combine(root, "supervisor")));
            Assert.False(new DirectoryInfo(Path.Combine(root, "supervisor")).GetAccessControl().AreAccessRulesProtected);
            foreach (var shown in new[] { Path.Combine(root, "dumps"), Path.Combine(root, "supervisor", "engine") })
            {
                // Every explicit rule on these folders is the show user's, and together they let it make and replace what the folder
                // holds (Modify reaches the contents) …
                var own = Explicit(shown);
                Assert.NotEmpty(own);
                Assert.All(own, r => Assert.Equal(user, r.IdentityReference));
                Assert.Contains(own, r => (r.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) != 0
                    && (r.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
                // … but no rule that governs the folder itself gives it Delete, so it may neither move nor delete the folder out of the
                // supervisor's own. (Delete on the folder, or FILE_DELETE_CHILD from its parent, would let it do either.)
                Assert.DoesNotContain(own, r => OnTheFolder(r) && (r.FileSystemRights & FileSystemRights.Delete) != 0);
            }
            Assert.Contains(said, s => s.Contains("only SYSTEM and administrators may write it", StringComparison.Ordinal));

            MachineFolders.TakeBack(root, said.Add);
            foreach (var shown in new[] { Path.Combine(root, "dumps"), Path.Combine(root, "supervisor", "engine") })
            {
                Assert.True(Directory.Exists(shown));
                Assert.Empty(Explicit(shown));
                Assert.DoesNotContain(All(shown), r => Writes(r) && r.IdentityReference.Equals(user));
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }
    }

    [WindowsFact]
    public void AFolderThatIsALinkIsRefusedAndNothingIsGrantedThroughIt()
    {
        var parent = Directory.CreateTempSubdirectory("playmap-machine-").FullName;
        var root = Path.Combine(parent, "PlayMap");
        var target = Directory.CreateDirectory(Path.Combine(parent, "elsewhere")).FullName;
        try
        {
            if (!Elevated)
            {
                Assert.ThrowsAny<Exception>(() => MachineFolders.Make(root, EngineIdentityWindowsTests.Place.ThisUser, _ => { }));
                return;
            }
            Directory.CreateDirectory(root);
            Directory.CreateSymbolicLink(Path.Combine(root, "supervisor"), target);
            var refused = Assert.Throws<InvalidOperationException>(() => MachineFolders.Make(root, EngineIdentityWindowsTests.Place.ThisUser, _ => { }));
            Assert.Contains("is a link (a junction or a symbolic link)", refused.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            Assert.DoesNotContain(Explicit(target), r => r.IdentityReference.Equals(WindowsIdentity.GetCurrent().User!));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [WindowsFact]
    public void AMachineFolderThatIsALinkIsRefusedBeforeAnythingIsMadeInsideIt()
    {
        // The PlayMap folder itself a link, as a standard user may make it before a first install: refused first, so none of the
        // folders below it is made through it (the installer's script makes none of them either: InstallerScriptTests).
        var parent = Directory.CreateTempSubdirectory("playmap-machine-").FullName;
        var root = Path.Combine(parent, "PlayMap");
        var target = Directory.CreateDirectory(Path.Combine(parent, "elsewhere")).FullName;
        try
        {
            if (!Elevated)
                return;
            Directory.CreateSymbolicLink(root, target);
            var refused = Assert.Throws<InvalidOperationException>(() => MachineFolders.Make(root, EngineIdentityWindowsTests.Place.ThisUser, _ => { }));
            Assert.Contains(root + " is a link (a junction or a symbolic link)", refused.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [WindowsFact]
    public void AShowUserFolderThatIsALinkIsRefusedAndNoWriteGrantReachesItsTarget()
    {
        // A leaf the show user writes (dumps) that is a link: the grant (its Modify) is set through the handle Hold checks, never by a
        // name that follows the link, so the link is refused before any grant and the target keeps its own access.
        var parent = Directory.CreateTempSubdirectory("playmap-machine-").FullName;
        var root = Path.Combine(parent, "PlayMap");
        var target = Directory.CreateDirectory(Path.Combine(parent, "elsewhere")).FullName;
        var user = WindowsIdentity.GetCurrent().User!;
        try
        {
            if (!Elevated)
            {
                Assert.ThrowsAny<Exception>(() => MachineFolders.Make(root, EngineIdentityWindowsTests.Place.ThisUser, _ => { }));
                return;
            }
            Directory.CreateDirectory(root);
            Directory.CreateSymbolicLink(Path.Combine(root, "dumps"), target);
            var refused = Assert.Throws<InvalidOperationException>(() => MachineFolders.Make(root, EngineIdentityWindowsTests.Place.ThisUser, _ => { }));
            Assert.Contains("is a link (a junction or a symbolic link)", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Explicit(target), r => r.IdentityReference.Equals(user) && r.AccessControlType == AccessControlType.Allow);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [WindowsFact]
    public void TheSupervisorsFolderIsSweptOfALinkAForeignFileAndThePidRecordsEarlierPlace()
    {
        if (!Elevated)
            return;
        var root = Path.Combine(Directory.CreateTempSubdirectory("playmap-machine-").FullName, "PlayMap");
        var elsewhere = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(root)!, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "a file the link's target holds");
        var said = new List<string>();
        try
        {
            var supervisor = Path.Combine(root, "supervisor");
            Directory.CreateDirectory(supervisor);
            // A link, the pid record's earlier place beside the status file, and a file whose owner is not SYSTEM or administrators.
            var link = Path.Combine(supervisor, "stale-link");
            Directory.CreateSymbolicLink(link, elsewhere);
            var pid = Path.Combine(supervisor, EnginePidRecord.FileName);
            File.WriteAllText(pid, "an earlier layout's pid record");
            var foreign = Path.Combine(supervisor, "foreign.txt");
            File.WriteAllText(foreign, "a file another account owns");
            var owned = new FileInfo(foreign).GetAccessControl();
            owned.SetOwner(WindowsIdentity.GetCurrent().User!);
            new FileInfo(foreign).SetAccessControl(owned);
            // The status file SYSTEM would write, owned by administrators, stays.
            var status = Path.Combine(supervisor, "status.json");
            File.WriteAllText(status, "{}");
            var kept = new FileInfo(status).GetAccessControl();
            kept.SetOwner(Administrators);
            new FileInfo(status).SetAccessControl(kept);

            MachineFolders.Make(root, EngineIdentityWindowsTests.Place.ThisUser, said.Add);

            Assert.False(Directory.Exists(link), "the link stayed");
            Assert.False(File.Exists(pid), "the pid record's earlier place stayed");
            Assert.False(File.Exists(foreign), "the foreign-owned file stayed");
            Assert.True(File.Exists(status), "the status file an administrator owns was removed");
            // The link was removed, its target and the target's file left alone.
            Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
            Assert.Contains(said, s => s.Contains("stale-link", StringComparison.Ordinal) && s.Contains("a link", StringComparison.Ordinal));
            Assert.Contains(said, s => s.Contains(EnginePidRecord.FileName, StringComparison.Ordinal) && s.Contains("the pid record's earlier place", StringComparison.Ordinal));
            Assert.Contains(said, s => s.Contains("foreign.txt", StringComparison.Ordinal) && s.Contains("placed there", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }
    }

    [WindowsFact]
    public void AFolderAnotherAccountPlacedInTheSupervisorsFolderIsRemovedOnlyEmptyAndNothingInsideOneIsDeleted()
    {
        // A folder whose owner is not SYSTEM or administrators stays its owner's to change while the install runs, so the sweep never
        // walks it: an empty one is removed, and one that holds anything — here a file, a folder and a link to a folder elsewhere —
        // refuses the install with words, every entry inside it and the link's target left as they were.
        if (!Elevated)
            return;
        var parent = Directory.CreateTempSubdirectory("playmap-machine-").FullName;
        var root = Path.Combine(parent, "PlayMap");
        var supervisor = Path.Combine(root, "supervisor");
        var elsewhere = Directory.CreateDirectory(Path.Combine(parent, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "a file the link's target holds");
        var user = WindowsIdentity.GetCurrent().User!;
        void OwnedByThisUser(string folder)
        {
            var security = new DirectoryInfo(folder).GetAccessControl();
            security.SetOwner(user);
            new DirectoryInfo(folder).SetAccessControl(security);
            Assert.False(MachinePlan.OwnerTrusted(((SecurityIdentifier)new DirectoryInfo(folder).GetAccessControl().GetOwner(typeof(SecurityIdentifier))!).Value));
        }
        var said = new List<string>();
        try
        {
            var empty = Directory.CreateDirectory(Path.Combine(supervisor, "placed-empty")).FullName;
            OwnedByThisUser(empty);
            MachineFolders.Make(root, EngineIdentityWindowsTests.Place.ThisUser, said.Add);
            Assert.False(Directory.Exists(empty), "the empty folder another account placed stayed");
            Assert.Contains(said, s => s.Contains("placed-empty", StringComparison.Ordinal) && s.Contains("placed there", StringComparison.Ordinal));

            var held = Directory.CreateDirectory(Path.Combine(supervisor, "placed-holding")).FullName;
            File.WriteAllText(Path.Combine(held, "note.txt"), "a file inside it");
            Directory.CreateDirectory(Path.Combine(held, "sub"));
            File.WriteAllText(Path.Combine(held, "sub", "deep.txt"), "a file a level down");
            Directory.CreateSymbolicLink(Path.Combine(held, "link"), elsewhere);
            OwnedByThisUser(held);
            var refused = Assert.Throws<InvalidOperationException>(() => MachineFolders.Make(root, EngineIdentityWindowsTests.Place.ThisUser, said.Add));
            Assert.Contains("placed-holding", refused.Message, StringComparison.Ordinal);
            Assert.Contains("the install deletes nothing inside a folder another account controls", refused.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(held, "note.txt")));
            Assert.True(File.Exists(Path.Combine(held, "sub", "deep.txt")));
            Assert.True((File.GetAttributes(Path.Combine(held, "link")) & FileAttributes.ReparsePoint) != 0, "the link inside it was removed");
            Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }
}
