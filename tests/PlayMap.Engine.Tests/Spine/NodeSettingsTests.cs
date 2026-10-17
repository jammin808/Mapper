using System.Text.Json.Nodes;
using PlayMap.Core.Bus;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The node's settings file keeps each package's settings beside the first round's: a file from before round 2 reads every
/// package's defaults, and a changed value comes back as it was saved.</summary>
public sealed class NodeSettingsTests
{
    private static readonly string[] PackageKeys =
        ["show", "fleet", "sync", "present", "decode", "optimise", "videoIo", "control", "companion", "fx", "audio", "content", "led", "align", "phone", "wall"];

    /// <summary>A read-only read never writes the file: no file reads as none and none is made; a file that does not read reads as none
    /// and stays byte for byte as it was, with no damaged copy beside it; a file that reads gives what a load gives.</summary>
    [Fact]
    public void AReadOnlyReadNeverWritesTheFile()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-settings-");
        try
        {
            var home = new NodeHome(dir.FullName);
            Assert.Null(home.ReadSettings());
            Assert.False(File.Exists(home.SettingsPath));

            var damaged = "{ \"nodeId\": \"a1b2c3d4\", \"name\": \"Typo\", \"token\": \"t0k3n\", ";
            File.WriteAllText(home.SettingsPath, damaged);
            var written = File.GetLastWriteTimeUtc(home.SettingsPath);
            Assert.Null(home.ReadSettings());
            Assert.Equal(damaged, File.ReadAllText(home.SettingsPath));
            Assert.Equal(written, File.GetLastWriteTimeUtc(home.SettingsPath));
            Assert.False(File.Exists(home.SettingsPath + ".damaged"));
            Assert.Equal(["playmap.node.json"], Directory.GetFiles(dir.FullName).Select(f => Path.GetFileName(f)).ToArray());

            home.SaveSettings(NodeSettings.CreateDefault("Reads") with { Token = "kept" });
            var read = home.ReadSettings()!;
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(home.LoadSettings("unused")), System.Text.Json.JsonSerializer.Serialize(read));
            Assert.Equal(("Reads", "kept"), (read.Name, read.Token));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AnOlderFileReadsEveryPackagesDefaults()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-settings-");
        try
        {
            var home = new NodeHome(dir.FullName);
            home.SaveSettings(NodeSettings.CreateDefault("Old node") with { Token = "" });
            var json = JsonNode.Parse(File.ReadAllText(home.SettingsPath))!.AsObject();
            foreach (var key in PackageKeys)
                Assert.True(json.Remove(key), $"{key} is written");
            File.WriteAllText(home.SettingsPath, json.ToJsonString());

            var read = home.LoadSettings("unused");

            Assert.Equal("Old node", read.Name);
            Assert.Same(ShowSafetySettings.Default, read.Show);
            Assert.Same(ControlSettings.Default, read.Control);
            Assert.Same(SyncSettings.Default, read.Sync);
            Assert.Same(PresentSettings.Default, read.Present);
            Assert.Same(LedSettings.Default, read.Led);
            Assert.Equal(ShowBusConstants.PlayMapOscPort, read.Control.OscPort);
            Assert.False(read.Led.Output);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void APackageKeyWrittenAsNullReadsItsDefaults()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-settings-");
        try
        {
            var home = new NodeHome(dir.FullName);
            home.SaveSettings(NodeSettings.CreateDefault("Hand edited") with { Control = ControlSettings.Default with { OscPort = 9000 } });
            var json = JsonNode.Parse(File.ReadAllText(home.SettingsPath))!.AsObject();
            json["show"] = null;
            json["led"] = null;
            json["control"]!["surfacePorts"] = null;
            File.WriteAllText(home.SettingsPath, json.ToJsonString());

            var read = home.LoadSettings("unused");

            Assert.Equal("Hand edited", read.Name);
            Assert.Same(ShowSafetySettings.Default, read.Show);
            Assert.Same(LedSettings.Default, read.Led);
            Assert.False(read.Led.Output);
            Assert.Equal(9000, read.Control.OscPort);
            Assert.Empty(read.Control.SurfacePorts);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AChangedValueComesBackAsSaved()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-settings-");
        try
        {
            var home = new NodeHome(dir.FullName);
            var saved = NodeSettings.CreateDefault("Node") with
            {
                Show = new ShowSafetySettings { Operators = ["FOH deck"] },
                Control = ControlSettings.Default with { OscPort = 9000, SurfacePorts = ControlSettings.Default.SurfacePorts.Add("APC40", "APC40*") },
                Sync = SyncSettings.Default with { Role = "follow", Group = "wall", Leader = "0badc0de" },
                Led = new LedSettings { Output = true },
            };
            home.SaveSettings(saved);

            var read = home.LoadSettings("unused");

            Assert.Equal("FOH deck", Assert.Single(read.Show.Operators));
            Assert.Equal(9000, read.Control.OscPort);
            Assert.Equal("APC40*", read.Control.SurfacePorts["APC40"]);
            Assert.Equal(("follow", "wall", "0badc0de"), (read.Sync.Role, read.Sync.Group, read.Sync.Leader));
            Assert.True(read.Led.Output);
            Assert.Equal(saved.NodeId, read.NodeId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AListWrittenAsNullReadsEmptyAndTheNodeStaysWhoItIs()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-settings-");
        try
        {
            var home = new NodeHome(dir.FullName);
            var saved = NodeSettings.CreateDefault("Stage left") with { TrustedAddresses = ["10.0.0.5"] };
            home.SaveSettings(saved);
            var json = JsonNode.Parse(File.ReadAllText(home.SettingsPath))!.AsObject();
            json["trustedAddresses"] = null;
            File.WriteAllText(home.SettingsPath, json.ToJsonString());

            var read = home.LoadSettings("unused");

            Assert.Equal(saved.NodeId, read.NodeId);
            Assert.Equal(saved.Token, read.Token);
            Assert.Empty(read.TrustedAddresses);
            Assert.False(File.Exists(home.SettingsPath + ".damaged"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AFileThatNoLongerReadsIsKeptAndTheNodeKeepsItsIdNameAndToken()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-settings-");
        try
        {
            var home = new NodeHome(dir.FullName);
            var saved = NodeSettings.CreateDefault("Stage left");
            home.SaveSettings(saved);
            var text = File.ReadAllText(home.SettingsPath);
            // Cut short, as a full disk or a stray edit leaves a file.
            File.WriteAllText(home.SettingsPath, text[..(text.Length / 2)]);

            var read = home.LoadSettings("unused");

            Assert.Equal(saved.NodeId, read.NodeId);
            Assert.Equal("Stage left", read.Name);
            Assert.Equal(saved.Token, read.Token);
            Assert.True(File.Exists(home.SettingsPath + ".damaged"));
            Assert.Equal(read.NodeId, home.LoadSettings("unused").NodeId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
