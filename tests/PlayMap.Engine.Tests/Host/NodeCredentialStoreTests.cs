using System.Globalization;
using PlayMap.Core.Bus;
using PlayMap.Devices.Model;
using PlayMap.Engine.Devices;

namespace PlayMap.Engine.Tests.Host;

/// <summary>The keys this node holds for its links: the Stagewatch key in a file of its own in the keys folder, protected at rest as
/// the node's other secrets are and written over by nothing else; an older settings file's key moved into it once at start and taken
/// out of the settings file, the move journalled only once the settings file is written without it; a key typed with LINK STAGEWATCH
/// kept there and never in the settings file. A file that cannot be written (a folder where its next copy goes, which refuses every
/// user, an administrator too) leaves the key where the node says it is.</summary>
public sealed class NodeCredentialStoreTests
{
    private const string MovedKey = "swk_moved0123456789";

    [Fact]
    public void TheLinkKeysLiveInAFileOfTheirOwnThatNothingElseWritesOver()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-keys-");
        try
        {
            var links = new NodeCredentialStore(dir.FullName, NodeCredentialStore.LinkKeysFileName);
            links.Set(NodeCredentialStore.StagewatchKey, new Credential("", "swk_link0123456789"));
            // The devices' and the assistant's store writes its whole file beside it, and names none of the links' keys.
            var devices = new NodeCredentialStore(dir.FullName);
            devices.Set("pj1", new Credential("admin", "pw"));
            Assert.True(devices.Remove("pj1"));
            Assert.Null(devices.Find(NodeCredentialStore.StagewatchKey));

            var again = new NodeCredentialStore(dir.FullName, NodeCredentialStore.LinkKeysFileName);
            Assert.Equal(("", "swk_link0123456789"), (again.Problem, again.Find(NodeCredentialStore.StagewatchKey)?.Secret));
            Assert.Empty(new NodeCredentialStore(dir.FullName).References);
            var file = Path.Combine(dir.FullName, NodeCredentialStore.LinkKeysFileName);
            if (OperatingSystem.IsWindows())
                Assert.DoesNotContain("swk_link", File.ReadAllText(file), StringComparison.Ordinal);
            else
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal("credential store", again.ToString());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AnOlderSettingsFilesStagewatchKeyMovesIntoTheStoreOnceAndTheLinkStillCarriesIt()
    {
        await using var sw = await ScreensStagewatch.StartAsync();
        var address = "127.0.0.1:" + sw.Port.ToString(CultureInfo.InvariantCulture);
        var dir = Directory.CreateTempSubdirectory("playmap-host-");
        var first = await HostRig.StartAsync(dir: dir, settings: s => s with { StagewatchEnabled = true, StagewatchHost = address, StagewatchKey = MovedKey });
        try
        {
            // This machine, and a Stagewatch with no TLS: plain ws, and the key rides it.
            Assert.True(await Eventually.TrueAsync(() => first.Network.Stagewatch()?.State == "live" && sw.Screens == 1), first.LogTail());
            Assert.Equal("Bearer " + MovedKey, Assert.Single(sw.Authorizations));
            Assert.DoesNotContain(MovedKey, await File.ReadAllTextAsync(first.Home.SettingsPath), StringComparison.Ordinal);
            Assert.Equal(("", true, address), (first.Home.ReadSettings()!.StagewatchKey, first.Home.ReadSettings()!.StagewatchEnabled, first.Home.ReadSettings()!.StagewatchHost));
            Assert.Equal(MovedKey, Stored(first));
            var moved = Assert.Single(first.Engine.Journal.Recent(200), r => r.Text.StartsWith("the Stagewatch key moved", StringComparison.Ordinal));
            Assert.Equal(("network", true), (moved.Kind, moved.Ok));
            Assert.DoesNotContain(first.Engine.Journal.Recent(200), r => r.Text.Contains(MovedKey, StringComparison.Ordinal));
            Assert.DoesNotContain(first.Network.Faults(), f => f.Part == "stagewatch-key");
        }
        finally
        {
            await first.StopAsync();
        }

        // A restart reads the key from the store: nothing more to move, and the link carries it again.
        await using var second = await HostRig.StartAsync(dir: dir);
        Assert.True(await Eventually.TrueAsync(() => second.Network.Stagewatch()?.State == "live" && sw.Screens == 2), second.LogTail());
        Assert.Equal(["Bearer " + MovedKey, "Bearer " + MovedKey], sw.Authorizations);
        Assert.DoesNotContain(second.Engine.Journal.Recent(200), r => r.Text.StartsWith("the Stagewatch key moved", StringComparison.Ordinal));
        Assert.DoesNotContain(MovedKey, await File.ReadAllTextAsync(second.Home.SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeyTypedWithLinkStagewatchIsKeptInTheStoreAndNeverInTheSettingsFile()
    {
        await using var sw = await ScreensStagewatch.StartAsync();
        var address = "127.0.0.1:" + sw.Port.ToString(CultureInfo.InvariantCulture);
        await using var rig = await HostRig.StartAsync();
        const string typed = "swk_typed0123456789";
        // A LINK verb's change runs beside the control lane and journals how it ended.
        var ended = new System.Collections.Concurrent.ConcurrentQueue<string>();
        rig.Engine.Journal.Written += r =>
        {
            if (r.Kind == "network" && r.Text.Contains("Stagewatch", StringComparison.Ordinal) && r.Line is null)
                ended.Enqueue(r.Text);
        };

        await rig.OkAsync($"LINK STAGEWATCH {address} {typed}");
        Assert.True(await Eventually.TrueAsync(() => ended.Count == 1 && rig.Network.Stagewatch()?.State == "live" && sw.Screens == 1), rig.LogTail());
        Assert.Equal($"following Stagewatch at {address} on a node link", ended.Single());
        Assert.Equal("Bearer " + typed, Assert.Single(sw.Authorizations));
        Assert.Equal(typed, Stored(rig));
        var kept = rig.Home.ReadSettings()!;
        Assert.Equal(("", true, address), (kept.StagewatchKey, kept.StagewatchEnabled, kept.StagewatchHost));
        Assert.DoesNotContain(typed, await File.ReadAllTextAsync(rig.Home.SettingsPath), StringComparison.Ordinal);

        // The same address again with no key keeps the key it holds; OFF forgets it.
        await rig.OkAsync($"LINK STAGEWATCH {address}");
        Assert.True(await Eventually.TrueAsync(() => ended.Count == 2 && sw.Screens == 2), rig.LogTail());
        Assert.Equal("Bearer " + typed, sw.Authorizations.Last());
        Assert.Equal(typed, Stored(rig));
        await rig.OkAsync("LINK STAGEWATCH OFF");
        Assert.True(await Eventually.TrueAsync(() => ended.Count == 3), rig.Journal());
        Assert.Equal("stopped following Stagewatch", ended.Last());
        Assert.Null(Stored(rig));
        Assert.Equal(("", false, ""), (rig.Home.ReadSettings()!.StagewatchKey, rig.Home.ReadSettings()!.StagewatchEnabled, rig.Home.ReadSettings()!.StagewatchHost));
        Assert.DoesNotContain(typed, await File.ReadAllTextAsync(rig.Home.SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALinkKeysFileThatCannotBeWrittenLeavesTheKeyInTheSettingsFileAndSaysSo()
    {
        await using var sw = await ScreensStagewatch.StartAsync();
        var address = "127.0.0.1:" + sw.Port.ToString(CultureInfo.InvariantCulture);
        var dir = Directory.CreateTempSubdirectory("playmap-host-");
        // The store writes its next copy beside its file and moves it over: a folder in that place refuses the write.
        Directory.CreateDirectory(Path.Combine(dir.FullName, "keys", NodeCredentialStore.LinkKeysFileName + ".new"));
        await using var rig = await HostRig.StartAsync(dir: dir, settings: s => s with { StagewatchEnabled = true, StagewatchHost = address, StagewatchKey = MovedKey });

        var fault = Assert.Single(rig.Network.Faults(), f => f.Part == "stagewatch-key");
        Assert.StartsWith("the Stagewatch key is still in the node's settings file in the clear — it could not be moved into the credential store",
            fault.Words, StringComparison.Ordinal);
        Assert.DoesNotContain(MovedKey, fault.Words, StringComparison.Ordinal);
        // Nothing moved: the settings file keeps the key, the store's file holds none, and no row says it moved.
        Assert.Equal(MovedKey, rig.Home.ReadSettings()!.StagewatchKey);
        Assert.Null(Stored(rig));
        Assert.DoesNotContain(rig.Engine.Journal.Recent(200), r => r.Text.StartsWith("the Stagewatch key moved", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Engine.Journal.Recent(200), r => r.Text.Contains(MovedKey, StringComparison.Ordinal));
        // The link still carries the key it holds for this run.
        Assert.True(await Eventually.TrueAsync(() => rig.Network.Stagewatch()?.State == "live" && sw.Screens == 1), rig.LogTail());
        Assert.Equal("Bearer " + MovedKey, Assert.Single(sw.Authorizations));
    }

    [Fact]
    public async Task ASettingsFileThatCannotBeWrittenKeepsTheKeyBesideTheStoreUntilTheNextStartTakesItOut()
    {
        await using var sw = await ScreensStagewatch.StartAsync();
        var address = "127.0.0.1:" + sw.Port.ToString(CultureInfo.InvariantCulture);
        var dir = Directory.CreateTempSubdirectory("playmap-host-");
        var home = new PlayMap.Engine.NodeHome(dir.FullName);
        home.SaveSettings(NodeSettings.CreateDefault("Stage left") with
        {
            Bind = "127.0.0.1",
            HttpPort = 0,
            WirePort = 0,
            SyncPort = 0,
            Token = HostRig.Token,
            TrustLoopback = true,
            Announce = false,
            StagewatchEnabled = true,
            StagewatchHost = address,
            StagewatchKey = MovedKey,
        });
        // The settings file is written beside itself and moved over: a folder in that place refuses the write.
        var blocker = Directory.CreateDirectory(home.SettingsPath + ".saving");
        var first = await HostRig.StartAsync(dir: dir);
        try
        {
            var fault = Assert.Single(first.Network.Faults(), f => f.Part == "stagewatch-key");
            Assert.StartsWith("the Stagewatch key is in the credential store and still in the node's settings file in the clear",
                fault.Words, StringComparison.Ordinal);
            Assert.DoesNotContain(MovedKey, fault.Words, StringComparison.Ordinal);
            // The store holds the key and the settings file still does: the move is not journalled, since it has not happened.
            Assert.Equal(MovedKey, Stored(first));
            Assert.Equal(MovedKey, first.Home.ReadSettings()!.StagewatchKey);
            Assert.DoesNotContain(first.Engine.Journal.Recent(200), r => r.Text.StartsWith("the Stagewatch key moved", StringComparison.Ordinal));
            Assert.True(await Eventually.TrueAsync(() => first.Network.Stagewatch()?.State == "live" && sw.Screens == 1), first.LogTail());
            Assert.Equal("Bearer " + MovedKey, Assert.Single(sw.Authorizations));
        }
        finally
        {
            await first.StopAsync();
        }

        // The next start, with the settings file writable again, takes the key out and journals the move once; no fault stands.
        blocker.Delete();
        await using var second = await HostRig.StartAsync(dir: dir);
        Assert.DoesNotContain(MovedKey, await File.ReadAllTextAsync(second.Home.SettingsPath), StringComparison.Ordinal);
        Assert.Single(second.Engine.Journal.Recent(200), r => r.Text.StartsWith("the Stagewatch key moved", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Network.Faults(), f => f.Part == "stagewatch-key");
        Assert.Equal(MovedKey, Stored(second));
    }

    /// <summary>The Stagewatch key the node's link keys' file holds now, read afresh.</summary>
    private static string? Stored(HostRig rig) =>
        new NodeCredentialStore(rig.Home.KeysFolder, NodeCredentialStore.LinkKeysFileName).Find(NodeCredentialStore.StagewatchKey)?.Secret;
}
