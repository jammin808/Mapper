namespace PlayMap.Render.D3D12.Tests;

/// <summary>Where a Direct3D 12 test leaves what a reader on another machine needs to see why it failed: under the folder
/// PLAYMAP_D3D12_EVIDENCE names (the Windows job's Direct3D 12 leg sets it and uploads it), one folder per test holding its diff
/// images (<c>*.diff.png</c>), the info queue's messages (<c>infoqueue.log</c>) and DRED's output (<c>dred.txt</c>). With the variable
/// unset nothing is written.</summary>
internal static class D3D12Evidence
{
    /// <summary>The variable that names the evidence folder.</summary>
    public const string Variable = "PLAYMAP_D3D12_EVIDENCE";

    /// <summary>The info queue's messages, one per line, the severity first.</summary>
    public const string InfoQueueLog = "infoqueue.log";

    /// <summary>What DRED was set to record and what it holds.</summary>
    public const string DredOutput = "dred.txt";

    /// <summary>The test's own evidence folder, made on first use; null when the variable is unset.</summary>
    public static string? Folder(string test)
    {
        var root = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(root))
            return null;
        var folder = Path.Combine(Path.GetFullPath(root), test);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Writes one evidence file of a test (nothing when the variable is unset); answers where it went, or null.</summary>
    public static string? Write(string test, string file, IEnumerable<string> lines)
    {
        var folder = Folder(test);
        if (folder is null)
            return null;
        var path = Path.Combine(folder, file);
        File.WriteAllLines(path, lines);
        return path;
    }
}
