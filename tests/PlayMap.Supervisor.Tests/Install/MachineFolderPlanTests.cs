using PlayMap.Core.Machine;
using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Tests.Install;

/// <summary>The machine's folders as the install makes them (<see cref="MachinePlan.Folders"/>): the supervisor's folder, where SYSTEM
/// writes its status file and log by path, is not the show user's to change, nor is the PlayMap folder above it; the engine's folder
/// inside it, which holds the pid record alone and which the supervisor only reads, is, as are the dumps; each folder comes after the
/// folder it lies in, so the install holds a folder before it makes or grants anything inside it; and no folder the show user does
/// not write lies inside one it does.</summary>
public sealed class MachineFolderPlanTests
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PlayMap");

    [Fact]
    public void TheSupervisorsFolderIsNotTheShowUsersToChangeAndTheEnginesFolderInsideItIs()
    {
        var byPath = MachinePlan.Folders.ToDictionary(f => f.In(Root), f => f.ShowUserWrites, StringComparer.Ordinal);
        Assert.False(byPath[Root]);
        Assert.False(byPath[SupervisorStatus.MachineFolder()]);
        Assert.True(byPath[EnginePidRecord.MachineFolder()]);
        Assert.True(byPath[Path.Combine(Root, "dumps")]);
        Assert.Equal(4, byPath.Count);
        Assert.Equal(@"%ProgramData%\PlayMap\dumps", MachinePlan.DumpFolder);
    }

    [Fact]
    public void EachFolderComesAfterTheFolderItLiesInAndNoneTheShowUserDoesNotWriteLiesInOneItDoes()
    {
        var seen = new List<MachineFolderPlan>();
        foreach (var folder in MachinePlan.Folders)
        {
            if (folder.Under.Count > 0)
            {
                var parent = Assert.Single(seen, s => s.Under.SequenceEqual(folder.Under.Take(folder.Under.Count - 1)));
                Assert.False(parent.ShowUserWrites && !folder.ShowUserWrites, string.Join('/', folder.Under) + " lies in a folder the show user writes");
            }
            seen.Add(folder);
        }
        Assert.Empty(MachinePlan.Folders[0].Under);
    }

    [Fact]
    public void WithNoShowUserNamedTheInteractiveUsersWriteItsFolders() =>
        Assert.Equal("S-1-5-4", MachinePlan.AnyInteractiveUserSid);
}
