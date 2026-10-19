using System.Reflection;
using System.Runtime.Versioning;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>The Windows machine platform is an edge: its assembly references no PlayMap project but Core, and every type in it is
/// Windows only (its namespace says Windows), so the solution builds and its pure policy runs on Linux from Core/Machine. Read by
/// reflection, so nothing Windows-only runs here.</summary>
public sealed class PlatformProjectTests
{
    private static Assembly Platform => Assembly.Load("PlayMap.Platform.Windows");

    [Fact]
    public void ThePlatformReferencesNoPlayMapProjectButCore()
    {
        var playMap = Platform.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("PlayMap.", StringComparison.Ordinal)).ToList();
        // The compiler keeps a reference only where code uses one, so Core may be absent; no other PlayMap assembly is ever listed.
        Assert.All(playMap, n => Assert.Equal("PlayMap.Core", n));
    }

    [Fact]
    public void EveryTypeOfThePlatformIsWindowsOnly()
    {
        var types = Platform.GetTypes().Where(t => t.Namespace?.StartsWith("PlayMap.Platform.Windows", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(types);
        foreach (var t in types)
        {
            var marked = false;
            for (var x = t; x is not null && !marked; x = x.DeclaringType)
                marked = x.GetCustomAttributes<SupportedOSPlatformAttribute>().Any(a => a.PlatformName.StartsWith("windows", StringComparison.OrdinalIgnoreCase));
            Assert.True(marked, $"{t.FullName} is not marked [SupportedOSPlatform(\"windows\")]");
        }
    }
}
