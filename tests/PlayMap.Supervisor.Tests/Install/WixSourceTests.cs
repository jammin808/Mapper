using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PlayMap.Supervisor.Tests.Install;

/// <summary>The WiX toolset is PlayMap's only from its source (docs/round3/PLAN.md §3.5 item 2): nuget.config maps <c>WixToolset.*</c>
/// to installer/wix/feed and to no other source, and no licence gesture — an <c>AcceptEula</c> property, an <c>-acceptEula</c> argument
/// or a <c>wix eula accept</c> step — appears in the installer's files, the <c>Directory.Build.*</c> files, nuget.config or the
/// installer's CI block. The Windows job's wix-toolset probe proves the other half on the runner: every WixToolset.* package restored
/// from the feed, its bytes the build's.</summary>
public sealed partial class WixSourceTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no PlayMap.sln above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"AcceptEula|-acceptEula|eula\s+accept", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Gesture();

    [GeneratedRegex(@"^  # wp3-installer:", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NamedBlock();

    [GeneratedRegex(@"^  # wp3-", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AnyBlock();

    [Fact]
    public void OnlyTheLocalFeedServesWixToolsetPackages()
    {
        var config = XDocument.Load(Path.Combine(Root(), "nuget.config"));
        var sources = config.Root!.Element("packageSources")!;
        Assert.NotNull(sources.Element("clear"));
        var named = sources.Elements("add").ToDictionary(e => (string)e.Attribute("key")!, e => (string)e.Attribute("value")!, StringComparer.Ordinal);
        Assert.Equal("installer/wix/feed", named["wix-from-source"]);
        var mapping = config.Root.Element("packageSourceMapping")!.Elements("packageSource")
            .ToDictionary(e => (string)e.Attribute("key")!, e => e.Elements("package").Select(p => (string)p.Attribute("pattern")!).ToList(), StringComparer.Ordinal);
        Assert.Equal(["WixToolset.*"], mapping["wix-from-source"]);
        // Every other source maps patterns less specific than WixToolset.*, so NuGet takes WixToolset.* from the feed alone.
        foreach (var (key, patterns) in mapping.Where(m => m.Key != "wix-from-source"))
            Assert.All(patterns, p => Assert.False(p.StartsWith("WixToolset", StringComparison.OrdinalIgnoreCase), key + " maps " + p));
        Assert.Equal(named.Keys.Order(StringComparer.Ordinal), mapping.Keys.Order(StringComparer.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(Root(), "installer", "wix", "feed")));
    }

    [Fact]
    public void NoLicenceGestureAppearsInTheInstallersFiles()
    {
        var root = Root();
        var files = Directory.GetFiles(Path.Combine(root, "installer"), "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.EndsWith(".nupkg", StringComparison.Ordinal))
            .Concat(Directory.GetFiles(root, "Directory.Build.*", SearchOption.TopDirectoryOnly))
            .Append(Path.Combine(root, "nuget.config"))
            .ToList();
        Assert.Contains(files, f => f.EndsWith("Probe.wixproj", StringComparison.Ordinal));
        foreach (var file in files)
            Assert.False(Gesture().IsMatch(File.ReadAllText(file)), file + " carries a licence gesture");
    }

    [Fact]
    public void NoLicenceGestureAppearsInTheInstallersCiBlock()
    {
        var workflow = File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "build.yml"));
        var start = NamedBlock().Match(workflow) is { Success: true } m ? m.Index : -1;
        Assert.True(start >= 0, "the installer's CI block is named '# wp3-installer:' at the jobs' indent");
        // The block runs to the next package's named block, or to the file's end.
        var next = AnyBlock().Match(workflow, start + 1);
        var block = next.Success ? workflow[start..next.Index] : workflow[start..];
        Assert.Contains("wix-toolset:", block, StringComparison.Ordinal);
        Assert.False(Gesture().IsMatch(block), "the installer's CI block carries a licence gesture");
    }
}
