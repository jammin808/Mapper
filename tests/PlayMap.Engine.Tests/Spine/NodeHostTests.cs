using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlayMap.Core.Bus;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Host;
using PlayMap.Tests;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The node without the desk: "PlayMap.Node engine headless" runs the node through the same entry the desk's exe uses and
/// answers PING on its wire; a command line that names no entry exits 2 and says what it reads.</summary>
public sealed partial class NodeHostTests
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
            using var node = NodeExe.Start("engine", "--headless", "--home", dir.FullName, "--no-recover");
            var output = new NodeOutput(node);
            try
            {
                var log = Path.Combine(dir.FullName, NodeLogProvider.FileName);
                var port = 0;
                Assert.True(await Eventually.TrueAsync(() =>
                {
                    if (node.HasExited || !File.Exists(log))
                        return false;
                    var m = WireUp().Match(Tail(log, int.MaxValue));
                    port = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                    return port > 0;
                }, timeoutMs: 60_000), $"the node did not come up (exited: {node.HasExited}); its output ends:\n{output.Tail}");

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
                            + $"\nits journal ends:\n{Tail(home.JournalPath, 12)}\nits log ends:\n{Tail(log, 12)}\nits output ends:\n{output.Tail}", ex);
                    }
                }
            }
            finally
            {
                await ChildProcess.StopAsync(node);
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

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("")]
    public async Task AnUnknownEntryExitsTwoWithWords(string word)
    {
        using var node = word.Length == 0 ? NodeExe.Start() : NodeExe.Start(word);
        var errors = node.StandardError.ReadToEndAsync();
        var output = node.StandardOutput.ReadToEndAsync();
        Assert.True(await ChildProcess.EndAsync(node, ChildProcess.Deadline, errors, output), "the node did not exit and was stopped");
        Assert.True(errors.IsCompletedSuccessfully, "the node's words were held open by a process it started");
        var said = await errors;

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
