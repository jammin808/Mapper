using System.Reflection;
using System.Runtime.Versioning;

namespace PlayMap.Render.D3D12.Tests;

/// <summary>The Direct3D 12 backend's tree: its folders are the ones docs/round3/design/d3d12.md §0 lists, each holding its
/// placeholder in the folder's namespace; every type of the backend is Windows only; and the assembly references no PlayMap project
/// but Core, so the GL backend and this one meet only in Core's contracts. Read by reflection, so nothing Windows-only runs here.</summary>
public sealed class BackendTreeTests
{
    /// <summary>docs/round3/design/d3d12.md §0's folders, in its order (the root holds the project and D3D12Backend.cs).</summary>
    private static readonly string[] Folders =
        ["Device", "Queues", "Memory", "Descriptors", "Commands", "Pipelines", "Shaders", "Share", "Platform", "Interop", "Present", "Video",
         "Effects", "Bindless", "Sync", "Rebuild"];

    private static Assembly Backend => Assembly.Load("PlayMap.Render.D3D12");

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir is null
            ? throw new DirectoryNotFoundException("the repository root holds PlayMap.sln")
            : Path.Combine(dir.FullName, "src", "PlayMap.Render.D3D12");
    }

    [Fact]
    public void TheFoldersAreTheDesignsAndEachHoldsItsPlaceholder()
    {
        var onDisk = Directory.EnumerateDirectories(Source()).Select(Path.GetFileName)
            .Where(n => n is not ("bin" or "obj")).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Folders.Order(StringComparer.Ordinal), onDisk);
        Assert.True(File.Exists(Path.Combine(Source(), "D3D12Backend.cs")));
        Assert.NotNull(Backend.GetType("PlayMap.Render.D3D12.D3D12Backend"));
        foreach (var folder in Folders)
        {
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(Source(), folder), "*.cs"));
            var placeholder = Backend.GetType($"PlayMap.Render.D3D12.{folder}.Folder");
            Assert.True(placeholder is not null, $"{folder}/ holds no placeholder in its namespace");
            var holds = placeholder.GetField("Holds", BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue() as string;
            Assert.False(string.IsNullOrWhiteSpace(holds), $"{folder}/'s placeholder says nothing of what the folder holds");
        }
    }

    [Fact]
    public void EveryTypeOfTheBackendIsWindowsOnly()
    {
        var types = Backend.GetTypes().Where(t => t.Namespace?.StartsWith("PlayMap.Render.D3D12", StringComparison.Ordinal) == true).ToList();
        Assert.True(types.Count > Folders.Length, $"{types.Count} types");
        foreach (var t in types)
        {
            var marked = false;
            for (var x = t; x is not null && !marked; x = x.DeclaringType)
                marked = x.GetCustomAttributes<SupportedOSPlatformAttribute>().Any(a => a.PlatformName.StartsWith("windows", StringComparison.OrdinalIgnoreCase));
            Assert.True(marked, $"{t.FullName} is not marked [SupportedOSPlatform(\"windows\")]");
        }
    }

    [Fact]
    public void TheBackendReferencesNoPlayMapProjectButCore()
    {
        var playMap = Backend.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("PlayMap.", StringComparison.Ordinal)).ToList();
        // The compiler keeps a reference only where code uses one, so Core may be absent; no other PlayMap assembly is ever listed.
        Assert.All(playMap, n => Assert.Equal("PlayMap.Core", n));
    }
}
