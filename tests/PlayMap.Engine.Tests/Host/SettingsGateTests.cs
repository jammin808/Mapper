using System.Text.RegularExpressions;
using PlayMap.Core.Bus;
using PlayMap.Core.Media.Decode;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Spine;

namespace PlayMap.Engine.Tests.Host;

/// <summary>The node's settings file has one writer, the network host, and every read-modify-write of it runs inside the writer's
/// lock: two edits of different keys landing together — the present path (PRESENT PATH's save), decode.policy (DECODE POLICY's) and
/// any other part's (the host's own <see cref="NetworkHost.PersistSettings"/>) — both reach the file, whichever reads it first; and no
/// other code writes the file.</summary>
public sealed class SettingsGateTests
{
    public enum Edit
    {
        Present,
        Decode,
        Host,
    }

    /// <summary>The first edit is held between its read of the file and its write; the second lands meanwhile (written beside the
    /// held read, or waiting at the writer's lock the held edit holds), then the first is let go. Both keys are in the file, and the node
    /// runs with both.</summary>
    [Theory]
    [InlineData(Edit.Present, Edit.Decode)]
    [InlineData(Edit.Decode, Edit.Present)]
    [InlineData(Edit.Present, Edit.Host)]
    [InlineData(Edit.Decode, Edit.Host)]
    public async Task TwoEditsLandingTogetherAreBothKept(Edit first, Edit second)
    {
        await using var node = await HostRig.StartAsync("Settings gate", parts: new EngineParts { Extensions = [OutputParts.Factory, EngineParts.DecodeFactory] });
        var holder = 0;
        var racer = 0;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        void OnRead()
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref holder) && held.TrySetResult() && !release.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("the held edit was never let go");
        }
        void OnWaits()
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref racer))
                waits.TrySetResult();
        }
        node.Home.SettingsRead += OnRead;
        node.Network.SettingsEditWaits += OnWaits;
        try
        {
            var a = OnItsOwnThread(() =>
            {
                Volatile.Write(ref holder, Environment.CurrentManagedThreadId);
                Apply(node, first);
            });
            await held.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var b = OnItsOwnThread(() =>
            {
                Volatile.Write(ref racer, Environment.CurrentManagedThreadId);
                Apply(node, second);
            });
            // The second edit has landed: written beside the held one's read, or waiting at the writer's lock the held one holds.
            await Task.WhenAny(b, waits.Task).WaitAsync(TimeSpan.FromSeconds(30));
            release.Set();
            await a.WaitAsync(TimeSpan.FromSeconds(30));
            await b.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            release.Set();
            node.Home.SettingsRead -= OnRead;
            node.Network.SettingsEditWaits -= OnWaits;
        }

        var kept = node.Home.LoadSettings("unused");
        Assert.Equal([true, true], new[] { Holds(kept, first), Holds(kept, second) });
        Assert.Equal([true, true], new[] { Holds(node.Network.Settings, first), Holds(node.Network.Settings, second) });
    }

    /// <summary>No code but the network host writes the settings file: its one write is the only call of
    /// <see cref="NodeHome.SaveSettings"/> in the sources (the node home itself makes the file on a first run, or beside a damaged one),
    /// and no other source names the file with a write.</summary>
    [Fact]
    public void NoCodeButTheNetworkHostWritesTheSettingsFile()
    {
        var root = NodeExe.RepoRoot();
        var sep = Path.DirectorySeparatorChar;
        var sources = new[] { "src", "tools" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
        Assert.True(sources.Count > 100, $"{sources.Count} sources");

        var saves = sources.SelectMany(kv => Regex.Matches(kv.Value, @"\.SaveSettings\(", RegexOptions.None, TimeSpan.FromSeconds(5)).Select(_ => kv.Key)).ToList();
        Assert.Equal(["src/PlayMap.Engine/Host/NetworkHost.cs"], saves);

        var write = new Regex(@"Write|DurableFile|File\.(Move|Copy|Replace|Delete|Create|Open)|FileMode\.(Create|Truncate|OpenOrCreate|Append)|FileAccess\.(Write|ReadWrite)|AppendAll",
            RegexOptions.None, TimeSpan.FromSeconds(5));
        foreach (var (file, text) in sources.Where(kv => kv.Key != "src/PlayMap.Engine/NodeHome.cs"))
        {
            Assert.False(text.Contains("playmap.node.json", StringComparison.Ordinal), $"{file} names the settings file");
            foreach (var line in text.Split('\n').Where(l => l.Contains("SettingsPath", StringComparison.Ordinal)))
                Assert.False(write.IsMatch(line), $"{file} writes the settings file: {line.Trim()}");
        }
    }

    private static Task OnItsOwnThread(Action action) =>
        Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Apply(HostRig node, Edit edit)
    {
        switch (edit)
        {
            case Edit.Present:
            {
                var present = node.Engine.Services.PresentNode!;
                present.Save(present.Saved with { Path = "gl" });
                break;
            }
            case Edit.Decode:
                Assert.Null(node.Engine.DecodeFacts.Persist!(DecodePolicy.Software));
                break;
            default:
                Assert.Equal("", node.Network.PersistSettings(s => s with { Name = "Settings gate, renamed" }));
                break;
        }
    }

    private static bool Holds(NodeSettings settings, Edit edit) => edit switch
    {
        Edit.Present => settings.Present.Path == "gl",
        Edit.Decode => settings.Decode.Policy == "software",
        _ => settings.Name == "Settings gate, renamed",
    };
}
