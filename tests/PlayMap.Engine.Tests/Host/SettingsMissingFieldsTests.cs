using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMap.Core.Bus;
using PlayMap.Core.Storage;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Tests.Host;

/// <summary>A node settings file that lacks a field of the settings' own record reads that field by its rule at the reader, so no reader
/// of the settings sees a null string or a default list: a string, the list and a port read as a fresh node's; the pairing token, when
/// missing, never reads as "" (open) but is made afresh with the file kept beside it as a damaged one; a missing TrustLoopback, Announce
/// or RequireHttps reads as the value that widens no access; a missing node id keeps the damaged path; a missing name reads as the
/// load's default name. A load that filled a field writes nothing back; the node's start says once, in its log, which fields were
/// missing, by name only.</summary>
public sealed class SettingsMissingFieldsTests
{
    private const string FileToken = "Q7RX-MISSING-TOKEN-5TQ9";
    private const string FilePatternsToken = "PTOKEN-MISSING-8W2K";
    private const string FileStagewatchKey = "SWKEY-MISSING-3J6M";
    private const string DefaultName = "Loaded by default";

    /// <summary>Every parameter of the settings' primary constructor, taken from the record itself: a field added later is covered
    /// here without this test changing.</summary>
    public static TheoryData<string> PrimaryFields()
    {
        var data = new TheoryData<string>();
        foreach (var p in Primary())
            data.Add(p.Name!);
        return data;
    }

    private static ParameterInfo[] Primary() => typeof(NodeSettings).GetConstructors().Single().GetParameters();

    private static string Key(string field) => JsonNamingPolicy.CamelCase.ConvertName(field);

    /// <summary>A file whose every field differs from a fresh node's, so a field read from the defaults is told from one read from the
    /// file.</summary>
    private static NodeSettings Whole() => NodeSettings.CreateDefault("From the file") with
    {
        Bind = "127.0.0.1",
        HttpPort = 1101,
        WirePort = 1102,
        SyncPort = 1103,
        Token = FileToken,
        TrustLoopback = true,
        TrustedAddresses = ["10.0.0.9"],
        RequireHttps = false,
        Announce = true,
        PatternsHost = "patterns.local:9696",
        PatternsToken = FilePatternsToken,
        StagewatchHost = "stagewatch.local",
        StagewatchKey = FileStagewatchKey,
        StagewatchEnabled = true,
    };

    private static string Without(NodeSettings settings, params string[] fields)
    {
        var dir = Directory.CreateTempSubdirectory("playmap-missing-src-");
        try
        {
            var home = new NodeHome(dir.FullName);
            home.SaveSettings(settings);
            var json = JsonNode.Parse(DurableFile.ReadAllText(home.SettingsPath))!.AsObject();
            foreach (var f in fields)
                Assert.True(json.Remove(Key(f)), $"{Key(f)} is written");
            return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static object? Value(NodeSettings s, string field) => typeof(NodeSettings).GetProperty(field)!.GetValue(s);

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static void NoNullAndNoDefaultList(NodeSettings s)
    {
        foreach (var p in Primary())
        {
            var v = Value(s, p.Name!);
            if (p.ParameterType == typeof(string))
                Assert.True(v is not null, $"{p.Name} reads null");
            if (p.ParameterType == typeof(ImmutableArray<string>))
                Assert.False(((ImmutableArray<string>)v!).IsDefault, $"{p.Name} reads a default list");
        }
    }

    [Theory]
    [MemberData(nameof(PrimaryFields))]
    public void AFieldMissingFromTheFileReadsItsRule(string field)
    {
        var dir = Directory.CreateTempSubdirectory("playmap-missing-");
        try
        {
            var home = new NodeHome(dir.FullName);
            var whole = Whole();
            var text = Without(whole, field);
            File.WriteAllText(home.SettingsPath, text);

            var read = home.ReadSettings();
            Assert.Equal(text, File.ReadAllText(home.SettingsPath));
            var loaded = home.LoadSettings(DefaultName);
            NoNullAndNoDefaultList(loaded);
            var fresh = NodeSettings.CreateDefault(DefaultName);

            switch (field)
            {
                case nameof(NodeSettings.NodeId):
                    // The damaged path, as before: the file kept beside, a fresh valid id, the token and name kept.
                    Assert.Null(read);
                    Assert.True(PlayMap.Core.Model.Ids.IsValid(loaded.NodeId));
                    Assert.Equal((FileToken, "From the file"), (loaded.Token, loaded.Name));
                    Assert.True(File.Exists(home.SettingsPath + ".damaged"));
                    return;
                case nameof(NodeSettings.Token):
                    // Never "" (open): a read-only reader takes no settings, and the load makes a fresh token, keeps the file beside
                    // as a damaged one, and writes every other field as the file held it.
                    Assert.Null(read);
                    Assert.NotEqual("", loaded.Token);
                    Assert.NotEqual(FileToken, loaded.Token);
                    Assert.True(File.Exists(home.SettingsPath + ".damaged"));
                    Assert.Equal(text, File.ReadAllText(home.SettingsPath + ".damaged"));
                    Assert.Equal(loaded.Token, home.ReadSettings()!.Token);
                    Assert.Equal(loaded.Token, home.LoadSettings(DefaultName).Token);
                    Assert.Equal(Json(whole with { Token = loaded.Token }), Json(loaded));
                    return;
                case nameof(NodeSettings.Name):
                    Assert.Equal(DefaultName, loaded.Name);
                    Assert.Equal(Environment.MachineName, read!.Name);
                    break;
                case nameof(NodeSettings.TrustLoopback):
                    Assert.False(loaded.TrustLoopback);
                    break;
                case nameof(NodeSettings.RequireHttps):
                    Assert.True(loaded.RequireHttps);
                    break;
                case nameof(NodeSettings.Announce):
                    Assert.False(loaded.Announce);
                    break;
                case nameof(NodeSettings.StagewatchEnabled):
                    Assert.False(loaded.StagewatchEnabled);
                    break;
                default:
                    // A bool may widen access by its default (as TrustLoopback's and Announce's do): a new one is not filled from
                    // CreateDefault until its author names what a missing one reads as here and in NodeHome.FillMissing.
                    Assert.False(Primary().Single(p => p.Name == field).ParameterType == typeof(bool), $"decide what a missing {field} reads as");
                    Assert.Equal(Json(Value(fresh, field)), Json(Value(loaded, field)));
                    break;
            }

            // Every other field as the file held it; the read-only read the same as the load (but a name it has no default for);
            // nothing written back and nothing kept beside.
            var expected = JsonNode.Parse(Json(whole))!.AsObject();
            expected[field] = JsonNode.Parse(Json(Value(loaded, field)));
            Assert.Equal(expected.ToJsonString(), Json(loaded));
            Assert.NotNull(read);
            NoNullAndNoDefaultList(read!);
            Assert.Equal(Json(loaded with { Name = read!.Name }), Json(read));
            Assert.Equal(text, File.ReadAllText(home.SettingsPath));
            Assert.False(File.Exists(home.SettingsPath + ".damaged"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>The fault the owner named: a node home whose file lacks the link fields starts, its network host up, the links' fields
    /// read as none.</summary>
    [Fact]
    public async Task ANodeWhoseFileLacksTheLinkFieldsStarts()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-host-");
        File.WriteAllText(new NodeHome(dir.FullName).SettingsPath, Without(RigSettings("No links"),
            nameof(NodeSettings.PatternsHost), nameof(NodeSettings.PatternsToken), nameof(NodeSettings.StagewatchHost), nameof(NodeSettings.StagewatchKey)));

        await using var node = await HostRig.StartAsync(dir: dir);

        var s = node.Network.Settings;
        Assert.Equal(("", "", "", ""), (s.PatternsHost, s.PatternsToken, s.StagewatchHost, s.StagewatchKey));
        Assert.Equal("No links", s.Name);
        Assert.Equal(JsonValueKind.Object, node.State().ValueKind);
        await using var wire = await node.WireAsync();
    }

    /// <summary>The node's start says once which fields its file lacked, by name, and no line it writes carries the token or a key.</summary>
    [Fact]
    public async Task TheStartNamesTheMissingFieldsOnceAndNoValue()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-host-");
        var settings = RigSettings("Missing said") with
        {
            Token = FileToken,
            PatternsToken = FilePatternsToken,
            StagewatchKey = FileStagewatchKey,
        };
        string[] missing = [nameof(NodeSettings.PatternsHost), nameof(NodeSettings.StagewatchHost), nameof(NodeSettings.TrustedAddresses)];
        File.WriteAllText(new NodeHome(dir.FullName).SettingsPath, Without(settings, missing));

        var node = await HostRig.StartAsync(dir: dir);
        string journal;
        try
        {
            journal = node.Journal();
        }
        finally
        {
            await node.StopAsync();
        }
        try
        {
            var lines = Directory.GetFiles(dir.FullName, NodeLogProvider.FileName + "*")
                .SelectMany(f => File.ReadAllLines(f)).ToList();
            var said = lines.Where(l => missing.All(m => l.Contains(Key(m), StringComparison.Ordinal))).ToList();
            Assert.True(said.Count == 1, "one line names the missing fields:\n" + string.Join("\n", lines));
            Assert.DoesNotContain("bind", said[0], StringComparison.Ordinal);
            foreach (var secret in new[] { FileToken, FilePatternsToken, FileStagewatchKey })
            {
                Assert.DoesNotContain(lines, l => l.Contains(secret, StringComparison.Ordinal));
                Assert.DoesNotContain(secret, journal, StringComparison.Ordinal);
            }
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    /// <summary>The start on a file that lacks its pairing token: the node starts with a fresh token (never "", never the one a log line
    /// could have read), the file kept beside as .damaged, and the one line names the token among the missing fields with no value.</summary>
    [Fact]
    public async Task TheStartOnAFileWithoutItsTokenMakesOneAndSaysSoWithNoValue()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-host-");
        var settings = RigSettings("No token") with
        {
            PatternsToken = FilePatternsToken,
            StagewatchKey = FileStagewatchKey,
        };
        string[] missing = [nameof(NodeSettings.Token), nameof(NodeSettings.PatternsHost)];
        var settingsPath = new NodeHome(dir.FullName).SettingsPath;
        var text = Without(settings, missing);
        File.WriteAllText(settingsPath, text);

        var node = await HostRig.StartAsync(dir: dir);
        string journal;
        string token;
        try
        {
            token = node.Network.Settings.Token;
            journal = node.Journal();
        }
        finally
        {
            await node.StopAsync();
        }
        try
        {
            Assert.NotEqual("", token);
            Assert.NotEqual(HostRig.Token, token);
            Assert.Equal(text, File.ReadAllText(settingsPath + ".damaged"));
            Assert.Equal(token, new NodeHome(dir.FullName).ReadSettings()!.Token);
            var lines = Directory.GetFiles(dir.FullName, NodeLogProvider.FileName + "*")
                .SelectMany(f => File.ReadAllLines(f)).ToList();
            var said = lines.Where(l => l.Contains("lacked token, " + Key(nameof(NodeSettings.PatternsHost)), StringComparison.Ordinal)).ToList();
            Assert.True(said.Count == 1, "one line names the missing fields:\n" + string.Join("\n", lines));
            Assert.Contains(".damaged", said[0], StringComparison.Ordinal);
            foreach (var secret in new[] { token, FilePatternsToken, FileStagewatchKey })
            {
                Assert.DoesNotContain(lines, l => l.Contains(secret, StringComparison.Ordinal));
                Assert.DoesNotContain(secret, journal, StringComparison.Ordinal);
            }
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    /// <summary>A file with every field reads as it did: the load fills nothing and writes nothing, and a save of what it read writes the
    /// same bytes.</summary>
    [Fact]
    public void AWholeFileReadsAsItWasByteForByteThroughASave()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-missing-");
        try
        {
            var home = new NodeHome(dir.FullName);
            home.SaveSettings(Whole());
            var text = DurableFile.ReadAllText(home.SettingsPath);
            var written = File.GetLastWriteTimeUtc(home.SettingsPath);

            var loaded = home.LoadSettings(DefaultName);
            Assert.Equal(text, DurableFile.ReadAllText(home.SettingsPath));
            Assert.Equal(written, File.GetLastWriteTimeUtc(home.SettingsPath));
            Assert.Equal(Json(Whole() with { NodeId = loaded.NodeId }), Json(loaded));

            home.SaveSettings(loaded);
            Assert.Equal(text, DurableFile.ReadAllText(home.SettingsPath));
            Assert.Equal(["playmap.node.json"], Directory.GetFiles(dir.FullName).Select(f => Path.GetFileName(f)).ToArray());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>The desk's read-only read of a file that lacks fields writes nothing: the link fields read as none, and a file without
    /// its token reads as no settings, with nothing made beside it.</summary>
    [Fact]
    public void TheDesksReadOfAFileThatLacksFieldsWritesNothing()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-missing-");
        try
        {
            var home = new NodeHome(dir.FullName);
            var text = Without(Whole(), nameof(NodeSettings.PatternsHost), nameof(NodeSettings.PatternsToken), nameof(NodeSettings.StagewatchHost),
                nameof(NodeSettings.StagewatchKey));
            File.WriteAllText(home.SettingsPath, text);
            var written = File.GetLastWriteTimeUtc(home.SettingsPath);

            var read = home.ReadSettings()!;
            Assert.Equal(("", "", "", ""), (read.PatternsHost, read.PatternsToken, read.StagewatchHost, read.StagewatchKey));
            Assert.Equal(FileToken, read.Token);
            Assert.Equal(text, File.ReadAllText(home.SettingsPath));
            Assert.Equal(written, File.GetLastWriteTimeUtc(home.SettingsPath));

            var noToken = Without(Whole(), nameof(NodeSettings.Token));
            File.WriteAllText(home.SettingsPath, noToken);
            Assert.Null(home.ReadSettings());
            Assert.Equal(noToken, File.ReadAllText(home.SettingsPath));
            Assert.Equal(["playmap.node.json"], Directory.GetFiles(dir.FullName).Select(f => Path.GetFileName(f)).ToArray());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>What the host rig writes for a node of its own (loopback, ephemeral ports, quiet).</summary>
    private static NodeSettings RigSettings(string name) => NodeSettings.CreateDefault(name) with
    {
        Bind = "127.0.0.1",
        HttpPort = 0,
        WirePort = 0,
        SyncPort = 0,
        Token = HostRig.Token,
        TrustLoopback = true,
        Announce = false,
    };
}
