using System.Collections.Concurrent;
using System.Diagnostics;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The node host (tools/PlayMap.Node) built beside these tests, and the repository it lives in.</summary>
internal static class NodeExe
{
    /// <summary>The repository's root: the folder that holds PlayMap.sln, above the tests' output.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root holds PlayMap.sln");
    }

    /// <summary>PlayMap.Node.dll in the same configuration and runtime as these tests (built with them).</summary>
    public static string Locate()
    {
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = tfm.Parent?.Name ?? "Debug";
        var dll = Path.Combine(RepoRoot(), "tools", "PlayMap.Node", "bin", configuration, tfm.Name, "PlayMap.Node.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("the node host is built with the engine's tests", dll);
    }

    /// <summary>Starts the node host with these words, its output and errors kept.</summary>
    public static Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Locate());
        foreach (var a in args)
            info.ArgumentList.Add(a);
        return Process.Start(info) ?? throw new InvalidOperationException("the node host did not start");
    }
}

/// <summary>A node's output and errors read as they come, the last lines of both kept for a failing test's words. A redirected pipe
/// nobody reads fills (on Windows its buffer is a few KiB), and a node thread that writes to it then waits for good.</summary>
internal sealed class NodeOutput
{
    private const int Kept = 40;
    private readonly ConcurrentQueue<string> _lines = new();

    public NodeOutput(Process node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.OutputDataReceived += (_, e) => Keep("out", e.Data);
        node.ErrorDataReceived += (_, e) => Keep("err", e.Data);
        node.BeginOutputReadLine();
        node.BeginErrorReadLine();
    }

    /// <summary>The last lines read, each marked with its stream.</summary>
    public string Tail => string.Join("\n", _lines);

    private void Keep(string stream, string? line)
    {
        if (line is null)
            return;
        _lines.Enqueue(stream + ": " + line);
        while (_lines.Count > Kept)
            _lines.TryDequeue(out _);
    }
}
