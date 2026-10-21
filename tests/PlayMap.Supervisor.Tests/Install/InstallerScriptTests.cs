using System.Text.RegularExpressions;

namespace PlayMap.Supervisor.Tests.Install;

/// <summary>The installer's script makes nothing under %ProgramData%: the machine's folders are the supervisor's install's to make
/// (MachineFolders.Make), which holds each through its own handle and refuses a link before it makes anything inside it. Inno Setup's
/// [Dirs] would make them elevated during the file installation, before that check, and Windows' CreateDirectory follows a junction in
/// the path it is given — so a standard user who made %ProgramData%\PlayMap a junction before a first install would have folders made
/// by the installer wherever it points. The installer job proves the install itself (InstallerTests: nothing appears at the junction's
/// target).</summary>
public sealed partial class InstallerScriptTests
{
    [GeneratedRegex(@"^\s*\[Dirs\]", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirsSection();

    [Fact]
    public void TheScriptMakesNoFolderUnderProgramData()
    {
        var script = File.ReadAllText(Path.Combine(InstallerTermsTests.Root(), "installer", "PlayMap.iss"));
        Assert.False(DirsSection().IsMatch(script), "PlayMap.iss has a [Dirs] section");
        Assert.DoesNotContain("{commonappdata}", script, StringComparison.OrdinalIgnoreCase);
        foreach (var call in new[] { "CreateDir(", "ForceDirectories(" })
            Assert.DoesNotContain(call, script, StringComparison.OrdinalIgnoreCase);
    }
}
