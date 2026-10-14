using System.Text.RegularExpressions;
using PlayMap.Core.Bus;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Host;

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
            using var node = NodeExe.Start("engine", "--headless", "--home", dir.FullName, "--no-recover");
            try
            {
                var log = Path.Combine(dir.FullName, NodeLogProvider.FileName);
                var port = 0;
                Assert.True(await Eventually.TrueAsync(() =>
                {
                    if (node.HasExited || !File.Exists(log))
                        return false;
                    using var read = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var text = new StreamReader(read);
                    var m = WireUp().Match(text.ReadToEnd());
                    port = m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                    return port > 0;
                }, timeoutMs: 60_000), $"the node did not come up (exited: {node.HasExited})");

                await using var c = await LineClient.ConnectAsync(port);
                Assert.StartsWith("OK @p PONG 1760101234567 ", await c.AskAsync("@p PING 1760101234567"), StringComparison.Ordinal);
                Assert.StartsWith("OK @s {\"v\":1,", await c.AskAsync("@s STATE"), StringComparison.Ordinal);
            }
            finally
            {
                node.Kill(entireProcessTree: true);
                await node.WaitForExitAsync();
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
