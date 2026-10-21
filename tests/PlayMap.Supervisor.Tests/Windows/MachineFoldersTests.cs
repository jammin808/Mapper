using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PlayMap.Supervisor.Install;
using PlayMap.Supervisor.Windows;

namespace PlayMap.Supervisor.Tests.Windows;

/// <summary>The machine's folders as the elevated install makes them, under a root of the test's own: each owned by SYSTEM or
/// administrators; the PlayMap folder's access its own (SYSTEM and administrators full control, every account read, nothing
/// inherited); the supervisor's folder with no rule of its own, so only SYSTEM and administrators may change what it holds; the dumps
/// and the engine's folder each with the show user's Modify and nothing else of their own; an earlier show user's rule replaced; and
/// the uninstall's take-back leaving no rule of the show user's. A folder that is a link is refused with words and nothing is granted
/// through it. A caller that is not elevated is refused: the install runs elevated.</summary>
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
                var own = Assert.Single(Explicit(shown));
                Assert.Equal(user, own.IdentityReference);
                Assert.Equal(FileSystemRights.Modify, own.FileSystemRights & FileSystemRights.Modify);
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
}
