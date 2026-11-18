using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Host;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The node without the desk: "PlayMap.Node engine headless" runs the node through the same entry the desk's exe uses and
/// answers PING on its wire; a command line that names no entry exits 2 and says what it reads. The node's start is said in the test's
/// output, pass or fail (<see cref="NodeUpTimes"/>). The child takes the home's default machine claims and runs and paces its render
/// loop on the default project with its one output not enabled: a 1920 × 1080 output drawn through a software rasteriser in a HIGH
/// class child holds every processor of a small machine and starves a test host below that class, so that draw is measured only by
/// the dispatched job that asks for it (<see cref="NodeHostLeg"/>, <c>on</c>).</summary>
public sealed partial class NodeHostTests(ITestOutputHelper output)
{
    [GeneratedRegex(@"is up: wire (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex WireUp();

    [Fact]
    public async Task TheNodeHostRunsAnEngineThatAnswersPing()
    {
        // The dispatched measurement's leg (NodeHostLeg): with its three variables unset, as everywhere but that job, no hold, the
        // home's defaults and the default project with its output not enabled.
        var leg = NodeHostLeg.Read(Environment.GetEnvironmentVariable);
        var dir = Directory.CreateTempSubdirectory("playmap-node-");
        try
        {
            var home = new NodeHome(dir.FullName);
            var saved = leg.Home(NodeSettings.CreateDefault("Node host") with
            {
                Bind = "127.0.0.1", HttpPort = 0, WirePort = 0, SyncPort = 0, Token = HostRig.Token, TrustLoopback = true, Announce = false,
            });
            home.SaveSettings(saved);
            var id = home.LoadSettings("Node host").NodeId;
            string[] words = ["engine", "--headless", "--home", dir.FullName, "--no-recover"];
            if (leg.WriteProject(dir.FullName, "Node host") is { } project)
                words = [.. words, "--project", project];
            var child = new NodeChildReadings(leg);
            child.Starting();
            var startedChild = DateTime.UtcNow;
            using var node = NodeExe.Start(words);
            var times = new NodeUpTimes(startedChild, node);
            var said = new NodeOutput(node);
            try
            {
                var log = Path.Combine(dir.FullName, NodeLogProvider.FileName);
                var port = 0;
                Assert.True(await Eventually.TrueAsync(() =>
                {
                    times.Poll();
                    if (node.HasExited || !File.Exists(log))
                        return false;
                    var read = Stopwatch.StartNew();
                    var text = Tail(log, int.MaxValue);
                    times.Read(text, read.Elapsed);
                    var m = WireUp().Match(text);
                    port = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                    if (port > 0)
                    {
                        times.SawUp(text, m.Index, log);
                        child.SawUp(text, m.Index);
                    }
                    return port > 0;
                }, timeoutMs: 60_000), $"the node did not come up (exited: {node.HasExited}); {times.Words()}; its output ends:\n{said.Tail}");
                child.Up(node);
                if (OperatingSystem.IsWindows())
                {
                    // The default claim stays a field: once seen up, the child has made its claims, and its class is the one its home
                    // asks (HIGH unless a leg names normal). An empty read of a child that has already exited is that exit, the first
                    // fault, said as such; an empty read of a child that runs is a failure of its own, never taken as a class.
                    var claimed = NodeChildReadings.ClaimedClass(saved.Machine);
                    if (child.PriorityClass is null && node.HasExited)
                    {
                        Assert.Fail(string.Create(CultureInfo.InvariantCulture,
                            $"the child exited (exit {node.ExitCode}) after it was seen up and before its priority class was read; {times.Words()}; its output ends:\n{said.Tail}"));
                    }
                    Assert.True(child.PriorityClass == claimed,
                        $"the child runs and its priority class read {child.PriorityClass ?? "nothing (the system would not say)"}, not {claimed} as its home's process class '{saved.Machine.Process}' asks");
                }
                if (leg.HoldMs > 0)
                {
                    // The leg's hold: the child runs unasked for a set time, so every leg measures the same exposure.
                    await Task.Delay(leg.HoldMs);
                    child.Held(node);
                }

                await using var c = await LineClient.ConnectAsync(port);
                child.Connect();
                Assert.StartsWith("OK @p PONG 1760101234567 ", await AskAsync(c, "@p PING 1760101234567"), StringComparison.Ordinal);
                child.Ping();
                var state = await AskAsync(c, "@s STATE");
                Assert.StartsWith("OK @s {\"v\":1,", state, StringComparison.Ordinal);
                // The answer is this node's: the id and the name its fresh folder's settings hold.
                using var doc = JsonDocument.Parse(state!["OK @s ".Length..]);
                child.State(doc.RootElement);
                // The child ran on the project it was started on: drawing nothing (the suite's), every output STATE lists is not enabled
                // (a project that would not open leaves the engine on its default, whose output is); asked to draw, the first is.
                var listed = doc.RootElement.GetProperty("outputs");
                var outputs = listed.EnumerateArray().ToList();
                if (leg.DrawsOutput)
                {
                    Assert.True(outputs.Count > 0 && outputs[0].GetProperty("enabled").GetBoolean(),
                        $"the leg {leg} started its child on the default project, and STATE's first output is not enabled: {listed.GetRawText()}");
                }
                else
                {
                    Assert.True(outputs.TrueForAll(o => !o.GetProperty("enabled").GetBoolean()),
                        $"the leg {leg} started its child on a project whose output is not enabled, and STATE's outputs say one is: {listed.GetRawText()}");
                }
                Assert.Equal(id, doc.RootElement.GetProperty("id").GetString());
                Assert.Equal("Node host", doc.RootElement.GetProperty("name").GetString());

                // A line not answered says what the node did meanwhile, which names the stage it stood at: whether that line was
                // answered later and when, whether a second connection's PING (with the node's read and write stamps) and CONTEXT
                // were answered then, whether the process lives, and the ends of its journal, its log and its output.
                async Task<string?> AskAsync(LineClient client, string line)
                {
                    var asked = Stopwatch.StartNew();
                    try
                    {
                        return await client.AskAsync(line);
                    }
                    catch (TimeoutException ex)
                    {
                        string later;
                        try
                        {
                            var reply = await client.ReplyAsync(50_000);
                            later = string.Create(CultureInfo.InvariantCulture, $"answered {asked.Elapsed.TotalSeconds:0.0} s after it was asked: {Clip(reply)}");
                        }
                        catch (TimeoutException)
                        {
                            later = string.Create(CultureInfo.InvariantCulture, $"not answered within {asked.Elapsed.TotalSeconds:0} s");
                        }
                        var other = await SecondConnectionAsync(port);
                        throw new TimeoutException($"{line}: {ex.Message}; {later}; {other}; the node {(node.HasExited ? "has exited" : "runs")}"
                            + $"\nits journal ends:\n{Tail(home.JournalPath, 12)}\nits log ends:\n{Tail(log, 12)}\nits output ends:\n{said.Tail}", ex);
                    }
                }
            }
            finally
            {
                child.BeforeKill(node);
                node.Kill(entireProcessTree: true);
                await node.WaitForExitAsync();
                child.Ended(node);
                times.Keep(output, child);
            }
        }
        finally
        {
            try
            {
                dir.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A file the killed node still held stays in the private temp folder.
            }
        }
    }

    /// <summary>A PING and a CONTEXT on a connection of their own, in words: how long each took, and the PING's stamps as the node read
    /// and wrote it against when it was sent (both clocks this machine's).</summary>
    private static async Task<string> SecondConnectionAsync(int port)
    {
        try
        {
            await using var c = await LineClient.ConnectAsync(port);
            var sent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var took = Stopwatch.StartNew();
            string ping;
            try
            {
                var pong = await c.AskAsync(string.Create(CultureInfo.InvariantCulture, $"@p2 PING {sent}"));
                var words = pong?.Split(' ') ?? [];
                ping = words.Length == 6 && long.TryParse(words[4], NumberStyles.None, CultureInfo.InvariantCulture, out var read)
                    && long.TryParse(words[5], NumberStyles.None, CultureInfo.InvariantCulture, out var wrote)
                    ? string.Create(CultureInfo.InvariantCulture, $"a second connection's PING answered in {took.ElapsedMilliseconds} ms (read {read - sent} ms after it was sent, written {wrote - read} ms after that)")
                    : $"a second connection's PING answered: {Clip(pong)}";
            }
            catch (TimeoutException)
            {
                return "a second connection's PING not answered within 10 s";
            }
            took.Restart();
            try
            {
                var context = await c.AskAsync("@c2 CONTEXT");
                return ping + string.Create(CultureInfo.InvariantCulture, $", its CONTEXT in {took.ElapsedMilliseconds} ms: {Clip(context)}");
            }
            catch (TimeoutException)
            {
                return ping + ", its CONTEXT not answered within 10 s";
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return $"a second connection failed: {ex.Message}";
        }
    }

    private static string Clip(string? line) => line is null ? "the connection ended" : line.Length > 160 ? line[..160] + "…" : line;

    /// <summary>The last lines of a file the node may still be writing ("" when it is not there).</summary>
    private static string Tail(string path, int lines)
    {
        if (!File.Exists(path))
            return "";
        using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var text = new StreamReader(read);
        return string.Join("\n", text.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines));
    }

    /// <summary>The node host test's times read the stamps the node's own log writes: a heading and an "is up" line written through
    /// <see cref="NodeLogProvider"/> are read back as times between the moments before and after they were logged, so a change to the
    /// log's stamp is seen here rather than as empty cells in CI's rows.</summary>
    [Fact]
    public void TheNodesUpTimesReadTheStampsItsLogWrites()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-node-times-");
        try
        {
            var path = Path.Combine(dir.FullName, NodeLogProvider.FileName);
            var before = DateTime.UtcNow;
            using (var log = new NodeLogProvider(path))
            {
                log.CreateLogger("PlayMap.Test").LogInformation("{Name} ({Id}) is up: wire {Wire}, web {Web}", "Node host", "n1", 41234, 41235);
            }
            var after = DateTime.UtcNow;
            using var self = Process.GetCurrentProcess();
            var times = new NodeUpTimes(before, self);
            var text = Tail(path, int.MaxValue);
            times.Read(text, TimeSpan.Zero);
            var m = WireUp().Match(text);
            Assert.True(m.Success, text);
            times.SawUp(text, m.Index, path);

            // The log stamps to the millisecond, cut, so a stamp may read up to 1 ms before the moment it was made.
            foreach (var (name, stamp) in new[] { ("heading", times.LogFirst), ("is up", times.Up) })
            {
                Assert.True(stamp is { } at && at >= before.AddMilliseconds(-1) && at <= after,
                    string.Create(CultureInfo.InvariantCulture, $"the {name} stamp {stamp:O} is not between {before:O} and {after:O}: {text}"));
            }
            Assert.NotNull(times.LogWritten);
            Assert.NotNull(times.Seen);
            Assert.DoesNotContain(",,", times.Row(), StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>The measurement's leg is the suite's own test with its variables unset or empty; a value outside a variable's words is
    /// refused with the variable and the words it takes, never defaulted; a leg that names no process class saves the home it was given;
    /// only <c>on</c> starts the child on the default project with its output drawn, and the class the child claims follows the home.</summary>
    [Fact]
    public void TheLegIsTodaysUnsetAndRefusesAWordItDoesNotTake()
    {
        Assert.Equal(NodeHostLeg.Today, NodeHostLeg.Read(_ => null));
        Assert.Equal(NodeHostLeg.Today, NodeHostLeg.Read(_ => ""));
        var asked = new Dictionary<string, string>
        {
            [NodeHostLeg.HoldVariable] = "5000", [NodeHostLeg.ProcessVariable] = "normal", [NodeHostLeg.OutputVariable] = "off",
        };
        Assert.Equal(new NodeHostLeg(5000, "normal", "off"), NodeHostLeg.Read(n => asked.GetValueOrDefault(n)));
        Assert.Equal("high", NodeHostLeg.Read(n => n == NodeHostLeg.ProcessVariable ? "high" : null).ProcessClass);
        var on = NodeHostLeg.Read(n => n == NodeHostLeg.OutputVariable ? "on" : null);
        Assert.Equal(new NodeHostLeg(0, null, NodeHostLeg.OutputOn), on);
        Assert.True(on.DrawsOutput);
        Assert.False(NodeHostLeg.Today.DrawsOutput);
        Assert.False(new NodeHostLeg(0, null, NodeHostLeg.OutputOff).DrawsOutput);
        foreach (var (name, word) in new[]
                 {
                     (NodeHostLeg.HoldVariable, "5s"), (NodeHostLeg.HoldVariable, "-1"), (NodeHostLeg.HoldVariable, "60001"),
                     (NodeHostLeg.HoldVariable, " 5000"), (NodeHostLeg.ProcessVariable, "realtime"), (NodeHostLeg.ProcessVariable, "Normal"),
                     (NodeHostLeg.OutputVariable, "ON"), (NodeHostLeg.OutputVariable, "OFF"), (NodeHostLeg.OutputVariable, "none"),
                 })
        {
            var refused = Assert.Throws<ArgumentException>(() => NodeHostLeg.Read(n => n == name ? word : null));
            Assert.Contains($"{name} is '{word}'", refused.Message, StringComparison.Ordinal);
        }

        var settings = NodeSettings.CreateDefault("Node host");
        Assert.Same(settings, NodeHostLeg.Today.Home(settings));
        Assert.Equal("normal", new NodeHostLeg(0, "normal", null).Home(settings).Machine.Process);
        Assert.Equal(settings.Machine with { Process = "normal" }, new NodeHostLeg(0, "normal", null).Home(settings).Machine);
        var untouched = Directory.CreateTempSubdirectory("playmap-node-leg-on-");
        try
        {
            Assert.Null(on.WriteProject(untouched.FullName, "Node host"));
            Assert.Empty(untouched.EnumerateFileSystemInfos());
        }
        finally
        {
            untouched.Delete(recursive: true);
        }

        Assert.Equal("High", NodeChildReadings.ClaimedClass(settings.Machine));
        Assert.Equal("Normal", NodeChildReadings.ClaimedClass(new NodeHostLeg(0, "normal", null).Home(settings).Machine));
    }

    /// <summary>The project the suite's child (and a leg's set <c>off</c>) starts on is the default project with its one output not
    /// enabled, written by the product's serializer and read back by it.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(NodeHostLeg.OutputOff)]
    public void ALegWithoutAnOutputWritesTheDefaultProjectWithItsOutputNotEnabled(string? output)
    {
        var dir = Directory.CreateTempSubdirectory("playmap-node-leg-");
        try
        {
            var path = new NodeHostLeg(0, null, output).WriteProject(dir.FullName, "Node host");
            Assert.NotNull(path);
            var read = PlayMap.Core.Model.ProjectJson.Read(File.ReadAllText(path)).Project;
            var made = PlayMap.Core.Model.Project.CreateDefault("Node host");
            Assert.NotEmpty(made.Outputs);
            Assert.Contains(made.Outputs, o => o.Enabled);
            Assert.Equal(made.Outputs.Select(o => o.Id), read.Outputs.Select(o => o.Id));
            Assert.All(read.Outputs, o => Assert.False(o.Enabled));
            Assert.Equal(made.Canvases.Select(c => c.Id), read.Canvases.Select(c => c.Id));
            Assert.Equal(made.Generators.Select(g => g.Id), read.Generators.Select(g => g.Id));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>The renderer is copied from the "is up" line's name alone, kept to letters, digits, dots, spaces and brackets, so a cell
    /// never carries a comma or a line of its own.</summary>
    [Theory]
    [InlineData("2026-11-17T13:18:57.944Z info Node host (n1) is up: wire 41234, web 41235, renderer llvmpipe (LLVM 21.1.4, 256 bits); facts.\nnext",
        "llvmpipe (LLVM 21.1.4 256 bits)")]
    [InlineData("x is up: wire 1, web 2, renderer none; facts.", "none")]
    [InlineData("x is up: wire 1, web 2, renderer Mesa \"evil\",=1,\r; facts.", "Mesa evil1")]
    [InlineData("x is up: wire 1, web 2; facts.\nrenderer later", "")]
    public void TheRendererIsTheUpLinesNameAndNothingElse(string text, string renderer)
    {
        var at = text.IndexOf("is up", StringComparison.Ordinal);
        Assert.Equal(renderer, NodeChildReadings.RendererOf(text, at));
    }

    /// <summary>The child's readings are reads, never a throw into the test: a process that has exited reads as empty or as the system
    /// says, the row has a cell for every column, and STATE's first output is read where it is and left empty where it is not.</summary>
    [Fact]
    public async Task TheChildsReadingsOfAProcessThatHasGoneAreCellsNeverAThrow()
    {
        using var gone = NodeExe.Start();
        _ = await gone.StandardError.ReadToEndAsync();
        await gone.WaitForExitAsync();
        var readings = new NodeChildReadings(new NodeHostLeg(5000, "normal", "off"));
        readings.Starting();
        readings.Up(gone);
        readings.Held(gone);
        readings.BeforeKill(gone);
        readings.Ended(gone);
        Assert.Equal(NodeChildReadings.Header.Split(',').Length, readings.Row().Split(',').Length);
        Assert.Contains("the leg: process normal, output off", readings.Words(), StringComparison.Ordinal);
        // The test host's own class is read as it starts the child and before the kill, whatever became of the child. Each cell is a
        // class's name, not compared with a second read: another test on this host may claim a class between the two reads.
        Assert.NotNull(readings.HostClassStart);
        Assert.NotNull(readings.HostClassKill);
        Assert.True(Enum.TryParse<ProcessPriorityClass>(readings.HostClassStart, out _), readings.HostClassStart);
        Assert.True(Enum.TryParse<ProcessPriorityClass>(readings.HostClassKill, out _), readings.HostClassKill);
        Assert.EndsWith($",normal,off,{readings.HostClassStart},{readings.HostClassKill}", readings.Row(), StringComparison.Ordinal);
        // The kept file's two lines: the up times' columns then the child's, and a cell under each.
        var (header, row) = new NodeUpTimes(DateTime.UtcNow, gone).Lines(readings);
        Assert.Equal(NodeUpTimes.Header + "," + NodeChildReadings.Header, header);
        Assert.Equal(header.Split(',').Length, row.Split(',').Length);

        using var self = Process.GetCurrentProcess();
        var mine = new NodeChildReadings(NodeHostLeg.Today);
        mine.Up(self);
        Assert.NotNull(mine.PriorityClass);
        Assert.NotNull(mine.CpuUpMs);
        Assert.True(mine.ThreadsUp > 0);
        using (var state = JsonDocument.Parse("{\"outputs\":[{\"id\":\"op_1\",\"enabled\":false,\"fps\":59.9,\"frameMsP95\":9.17}]}"))
            mine.State(state.RootElement);
        Assert.Equal(("op_1", false, 59.9, 9.17), (mine.OutputId, mine.OutputEnabled, mine.OutputFps, mine.OutputFrameMsP95));
        Assert.Equal(NodeChildReadings.Header.Split(',').Length, mine.Row().Split(',').Length);
        Assert.EndsWith(",op_1,false,59.9,9.17,,,,,,,,", mine.Row(), StringComparison.Ordinal);

        var odd = new NodeChildReadings(NodeHostLeg.Today);
        using (var state = JsonDocument.Parse("{\"outputs\":[{\"id\":7,\"enabled\":\"yes\",\"fps\":\"60\"}]}"))
            odd.State(state.RootElement);
        Assert.Equal((null, null, null, null), (odd.OutputId, odd.OutputEnabled, odd.OutputFps, odd.OutputFrameMsP95));
        using (var state = JsonDocument.Parse("[]"))
            odd.State(state.RootElement);
        Assert.NotNull(odd.Stated);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("")]
    public async Task AnUnknownEntryExitsTwoWithWords(string word)
    {
        using var node = word.Length == 0 ? NodeExe.Start() : NodeExe.Start(word);
        var said = await node.StandardError.ReadToEndAsync();
        await node.WaitForExitAsync();

        Assert.Equal(EngineEntry.BadArguments, node.ExitCode);
        Assert.Contains("engine [headless]", said, StringComparison.Ordinal);
        if (word.Length > 0)
            Assert.Contains($"'{word}' is not an entry of PlayMap.Node", said, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEngineEntryDispatchesOnlyItsOwnWords()
    {
        Assert.False(EngineEntry.TryDispatch(["--desk"], out var exit));
        Assert.Equal(EngineEntry.BadArguments, exit);
        Assert.False(EngineEntry.TryDispatch([], out _));
    }
}
