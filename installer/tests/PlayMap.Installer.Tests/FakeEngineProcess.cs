using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using PlayMap.Tests;

namespace PlayMap.Installer.Tests;

/// <summary>The fake engine (installer/tests/PlayMap.FakeEngine) run as a process named PlayMap.exe from a folder of its own, as
/// "PlayMap.exe engine …": the supervisor's identity rule takes it for an engine, so the armed check asks it. Its port is read from the
/// file it writes once its listener and its record (where asked) are in place; the lines it was asked are read from its asked file.
/// It is ended when the test is done.</summary>
internal sealed class FakeEngineProcess : IDisposable
{
    private readonly Process _process;
    private readonly string _folder;
    private readonly string _asked;

    private FakeEngineProcess(Process process, string folder, string asked, int port)
    {
        _process = process;
        _folder = folder;
        _asked = asked;
        Port = port;
    }

    public int Pid => _process.Id;

    public int Port { get; }

    public bool Running => !_process.HasExited;

    /// <summary>The lines the fake was asked so far.</summary>
    public IReadOnlyList<string> Asked => File.Exists(_asked) ? File.ReadAllLines(_asked) : [];

    /// <summary>Starts the fake with these options (<c>--answer &lt;payload&gt;</c>, <c>--silent</c>, <c>--refuse &lt;reason&gt;</c>,
    /// <c>--record &lt;folder&gt;</c>, <c>--stop-event &lt;name&gt;</c>) and waits until it listens.</summary>
    public static FakeEngineProcess Start(params string[] options)
    {
        var folder = Directory.CreateTempSubdirectory("playmap-fake-engine-").FullName;
        foreach (var file in Directory.GetFiles(Built()))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        // The apphost finds its assembly by the name built into it, so it runs under the engine's name.
        File.Move(Path.Combine(folder, "PlayMap.FakeEngine.exe"), Path.Combine(folder, "PlayMap.exe"));
        var portFile = Path.Combine(folder, "port.txt");
        var asked = Path.Combine(folder, "asked.txt");
        var start = new ProcessStartInfo(Path.Combine(folder, "PlayMap.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var a in (string[])["engine", .. options, "--port-file", portFile, "--asked-file", asked])
            start.ArgumentList.Add(a);
        var process = Process.Start(start)!;
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(portFile) && !process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(60))
            process.WaitForExit(100);
        Assert.False(process.HasExited, "the fake engine ended at start (exit " + (process.HasExited ? process.ExitCode : 0) + ")");
        Assert.True(File.Exists(portFile), "the fake engine wrote no port within 60 s");
        return new FakeEngineProcess(process, folder, asked, int.Parse(File.ReadAllText(portFile), CultureInfo.InvariantCulture));
    }

    /// <summary>Waits up to <paramref name="within"/> for the fake to end; its exit code, or null when it still runs.</summary>
    public int? WaitForExit(TimeSpan within) => _process.WaitForExit(within) ? _process.ExitCode : null;

    public void Dispose()
    {
        ChildProcess.Stop(_process);
        _process.Dispose();
        Directory.Delete(_folder, true);
    }

    /// <summary>The fake's build output beside these tests' (the same configuration).</summary>
    private static string Built()
    {
        var configuration = typeof(FakeEngineProcess).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlayMap.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var built = Path.Combine(dir.FullName, "installer", "tests", "PlayMap.FakeEngine", "bin", configuration, "net10.0");
        Assert.True(File.Exists(Path.Combine(built, "PlayMap.FakeEngine.exe")), "the fake engine is not built in " + built);
        return built;
    }
}
