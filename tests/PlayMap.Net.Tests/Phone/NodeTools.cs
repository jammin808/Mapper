using System.Diagnostics;

namespace PlayMap.Net.Tests.Phone;

/// <summary>Node.js for the camera page's checks (tests/e2e/phonescan): where node is, whether a test package resolves (that folder's
/// node_modules from <c>npm ci</c>, or the machine's global packages), and a script run to its end.</summary>
internal static class NodeTools
{
    private static readonly Lazy<string?> NodePath = new(() => Find("node"));
    private static readonly Lazy<string?> GlobalRoot = new(() => Output("npm", "root -g"));

    public static string? Node => NodePath.Value;

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root (PlayMap.sln) is not above the test");
    }

    public static string E2e => Path.Combine(RepoRoot(), "tests", "e2e", "phonescan");

    public static bool Has(string module) =>
        Directory.Exists(Path.Combine(E2e, "node_modules", module))
        || (GlobalRoot.Value is { Length: > 0 } root && Directory.Exists(Path.Combine(root, module)));

    private static string? Find(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string? Output(string tool, string args)
    {
        var path = Find(tool) ?? (OperatingSystem.IsWindows() ? Find(tool + ".cmd") : null);
        if (path is null)
            return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path, args) { RedirectStandardOutput = true, UseShellExecute = false });
            if (p is null)
                return null;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(30_000);
            return text;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Runs a script of tests/e2e/phonescan to its end; answers its exit code and what it wrote.</summary>
    public static async Task<(int Code, string Output)> RunAsync(string script, TimeSpan timeout, params string[] args)
    {
        var start = new ProcessStartInfo(Node ?? throw new InvalidOperationException("node is not installed"))
        {
            WorkingDirectory = E2e,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        foreach (var a in args)
            start.ArgumentList.Add(a);
        start.Environment["PLAYWRIGHT_BROWSERS_PATH"] = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") ?? "/opt/pw-browsers";
        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"{script} ran past {timeout.TotalSeconds} s: {await stderr}");
        }
        return (p.ExitCode, await stdout + await stderr);
    }
}

/// <summary>A fact that needs node (and, when named, a test package): on a machine without them it is skipped with the reason, as an
/// FFmpeg fact is without FFmpeg.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute(string? module = null)
    {
        if (NodeTools.Node is null)
            Skip = "node (22 or later) is not installed: the camera page's checks run under it";
        else if (module is not null && !NodeTools.Has(module))
            Skip = $"the test package {module} is not installed: run npm ci in tests/e2e/phonescan";
        else if (module == "playwright" && !Directory.Exists(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") ?? "/opt/pw-browsers"))
            Skip = "Playwright's Chromium is not installed (PLAYWRIGHT_BROWSERS_PATH)";
    }
}
