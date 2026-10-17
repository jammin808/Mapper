using PlayMap.Core.Storage;

namespace PlayMap.Engine.Tests.Host;

/// <summary>A reader of the node's settings file — the desk's pairing-token read, another process's — holding the file open across
/// the one writer's write never loses the write: the host's edit returns no words, the file holds the change, and the reader reads the
/// settings it opened whole. Windows is where a rename over an open file can be refused (the Windows job runs this); elsewhere the same
/// contract holds.</summary>
public sealed class SettingsReaderTests
{
    [Fact]
    [Trait("Category", "Windows")]
    public async Task AReaderHoldingTheSettingsOpenNeverLosesTheWritersChange()
    {
        await using var node = await HostRig.StartAsync("Reader node");
        for (var i = 0; i < 3; i++)
        {
            var before = node.Home.ReadSettings()!;
            // Opened as the desk's read opens it (NodeHome.ReadSettings), and held across the write.
            using var held = DurableFile.OpenRead(node.Home.SettingsPath);
            Assert.Equal('{', held.ReadByte());

            Assert.Equal("", node.Network.PersistSettings(s => s with { Name = $"Reader node {i}" }));

            Assert.Equal($"Reader node {i}", node.Home.ReadSettings()!.Name);
            Assert.Equal(before.NodeId, node.Home.ReadSettings()!.NodeId);
            using var rest = new StreamReader(held);
            using var old = System.Text.Json.JsonDocument.Parse("{" + rest.ReadToEnd());
            Assert.Equal(before.Name, old.RootElement.GetProperty("name").GetString());
        }
    }
}
