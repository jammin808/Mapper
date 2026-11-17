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
/// output, pass or fail (<see cref="NodeUpTimes"/>).</summary>
public sealed partial class NodeHostTests(ITestOutputHelper output)
{
    [GeneratedRegex(@"is up: wire (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex WireUp();

    [Fact]
    public async Task TheNodeHostRunsAnEngineThatAnswersPing()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-node-");
        try
        {
            var home = new NodeHome(dir.FullName);
            home.SaveSettings(NodeSettings.CreateDefault("Node host") with
            {
                Bind = "127.0.0.1", HttpPort = 0, WirePort = 0, SyncPort = 0, Token = HostRig.Token, TrustLoopback = true, Announce = false,
            });
            var id = home.LoadSettings("Node host").NodeId;
            var startedChild = DateTime.UtcNow;
            using var node = NodeExe.Start("engine", "--headless", "--home", dir.FullName, "--no-recover");
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
                        times.SawUp(text, m.Index, log);
                    return port > 0;
                }, timeoutMs: 60_000), $"the node did not come up (exited: {node.HasExited}); {times.Words()}; its output ends:\n{said.Tail}");

                await using var c = await LineClient.ConnectAsync(port);
                Assert.StartsWith("OK @p PONG 1760101234567 ", await AskAsync(c, "@p PING 1760101234567"), StringComparison.Ordinal);
                var state = await AskAsync(c, "@s STATE");
                Assert.StartsWith("OK @s {\"v\":1,", state, StringComparison.Ordinal);
                // The answer is this node's: the id and the name its fresh folder's settings hold.
                using var doc = JsonDocument.Parse(state!["OK @s ".Length..]);
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
                node.Kill(entireProcessTree: true);
                await node.WaitForExitAsync();
                times.Keep(output);
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
