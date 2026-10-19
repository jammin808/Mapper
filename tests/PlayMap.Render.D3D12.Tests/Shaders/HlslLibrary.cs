using System.Text.Json;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>The HLSL library's files in the tree: its folder, its program table (programs.json: each program's family file, its
/// vertex and pixel entries and how its pixels are held to the GL backend's) and the layout table its slot headers are made from.</summary>
internal static class HlslLibrary
{
    public sealed record Entry(string Name, string File, string Vs, string Ps, string Parity);

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root holds PlayMap.sln");
    }

    public static string Folder => Path.Combine(RepoRoot(), "src", "PlayMap.Render.D3D12", "Shaders");

    public static string LayoutPath => Path.Combine(Folder, "programs.glsl.layout.json");

    public static IReadOnlyList<Entry> Programs()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "programs.json")));
        return [.. doc.RootElement.GetProperty("programs").EnumerateArray().Select(p => new Entry(p.GetProperty("name").GetString()!,
            p.GetProperty("file").GetString()!, p.GetProperty("vs").GetString()!, p.GetProperty("ps").GetString()!,
            p.GetProperty("parity").GetString()!))];
    }

    /// <summary>The define a program's generated header sets, so its family file compiles that program's code.</summary>
    public static string ProgramDefine(string name) => "PM_PROGRAM_" + name.Replace('.', '_');

    /// <summary>The text of one program's block in its family file: the shared code before the first guard, and its own guard's.</summary>
    public static string BlockOf(Entry e)
    {
        var text = File.ReadAllText(Path.Combine(Folder, e.File));
        var guard = $"#if defined({ProgramDefine(e.Name)})";
        var first = text.IndexOf("#if defined(PM_PROGRAM_", StringComparison.Ordinal);
        var shared = first < 0 ? text : text[..first];
        var at = text.IndexOf(guard, StringComparison.Ordinal);
        if (at < 0)
            return shared;
        var end = text.IndexOf("\n#endif", at, StringComparison.Ordinal);
        return shared + text[at..(end < 0 ? text.Length : end)];
    }
}
