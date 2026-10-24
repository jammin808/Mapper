using System.Reflection;
using System.Runtime.Versioning;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>Windows-only code says so: every type under a Windows namespace carries [SupportedOSPlatform("windows")], every source
/// file named for Windows declares it, and a call to such code from code that is not is a build error (CA1416 with warnings as
/// errors, never lowered).</summary>
public sealed class WindowsPlatformTests
{
    private static readonly string[] Assemblies =
        ["PlayMap.Core", "PlayMap.Render", "PlayMap.Media", "PlayMap.Net", "PlayMap.Engine", "PlayMap.Devices", "PlayMap.Assistant", "PlayMap.Vision"];

    private static bool WindowsOnly(Type t)
    {
        for (var x = t; x is not null; x = x.DeclaringType)
            if (x.GetCustomAttributes<SupportedOSPlatformAttribute>().Any(a => a.PlatformName.StartsWith("windows", StringComparison.OrdinalIgnoreCase)))
                return true;
        return t.Assembly.GetCustomAttributes<SupportedOSPlatformAttribute>().Any(a => a.PlatformName.StartsWith("windows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryTypeUnderAWindowsNamespaceIsMarkedWindowsOnly()
    {
        var seen = 0;
        foreach (var name in Assemblies)
        {
            var assembly = Assembly.Load(name);
            foreach (var t in assembly.GetTypes())
            {
                seen++;
                if (t.Namespace?.Split('.').Contains("Windows", StringComparer.Ordinal) == true)
                    Assert.True(WindowsOnly(t), $"{t.FullName} is under a Windows namespace without [SupportedOSPlatform(\"windows\")]");
            }
        }
        Assert.True(seen > 100, $"{seen} types");
    }

    /// <summary>Round 3's new projects: every type of the Direct3D 12 backend and of the Windows machine platform is Windows only; the
    /// supervisor's are where their namespace says Windows (its policy is pure and runs on every OS, its service calls carry their
    /// platform).</summary>
    [Fact]
    public void TheNewProjectsTypesCarryTheirPlatform()
    {
        foreach (var name in new[] { "PlayMap.Render.D3D12", "PlayMap.Platform.Windows" })
        {
            var types = Assembly.Load(name).GetTypes().Where(t => t.Namespace?.StartsWith(name, StringComparison.Ordinal) == true).ToList();
            Assert.NotEmpty(types);
            foreach (var t in types)
                Assert.True(WindowsOnly(t), $"{t.FullName} is in {name} without [SupportedOSPlatform(\"windows\")]");
        }
        var supervisor = Assembly.Load("PlayMap.Supervisor").GetTypes().Where(t => t.Namespace?.StartsWith("PlayMap.Supervisor", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(supervisor);
        foreach (var t in supervisor.Where(t => t.Namespace!.Split('.').Contains("Windows", StringComparer.Ordinal)))
            Assert.True(WindowsOnly(t), $"{t.FullName} is under a Windows namespace without [SupportedOSPlatform(\"windows\")]");
    }

    [Fact]
    public void EverySourceFileNamedForWindowsDeclaresIt()
    {
        var src = Path.Combine(NodeExe.RepoRoot(), "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}ThirdParty{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);
        foreach (var f in files.Where(f => Path.GetRelativePath(src, f).Split(Path.DirectorySeparatorChar, '.').Contains("Windows", StringComparer.Ordinal)))
            Assert.Contains("SupportedOSPlatform(\"windows\")", File.ReadAllText(f), StringComparison.Ordinal);
    }

    [Fact]
    public void CallingWindowsOnlyCodeIsABuildError()
    {
        var root = NodeExe.RepoRoot();
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", File.ReadAllText(Path.Combine(root, "Directory.Build.props")), StringComparison.Ordinal);
        var lowered = File.ReadAllLines(Path.Combine(root, ".editorconfig"))
            .Where(l => l.Replace(" ", "", StringComparison.Ordinal).StartsWith("dotnet_diagnostic.CA1416.severity=", StringComparison.Ordinal))
            .Where(l => !l.EndsWith("error", StringComparison.Ordinal) && !l.EndsWith("warning", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(lowered);
    }
}
