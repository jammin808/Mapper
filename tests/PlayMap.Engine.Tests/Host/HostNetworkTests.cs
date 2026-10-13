using System.Security.Cryptography;
using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;

namespace PlayMap.Engine.Tests.Host;

/// <summary>Two real nodes in one process finding each other on the hello: one leads a sync group and the other takes
/// its clock; one holds a file and the other fetches it by its hash. And a node following a Patterns desk: its blackout
/// reaches the air through the engine's own fact path, the follow switch decides, and the link is kept in the node's
/// settings with its token — which never reaches STATE or the journal.</summary>
public sealed class HostNetworkTests
{
    /// <summary>The follower's leader time agrees with the leader's own within this (loopback: a fraction of it).</summary>
    private const double ToleranceMs = 5;

    [Fact]
    public async Task AFollowerTakesItsLeadersClockWithinFiveMilliseconds()
    {
        // The leader's monotonic clock is five seconds ahead of the follower's: another machine's.
        const long ahead = 5_000_000_000;
        await using var leader = await HostRig.StartAsync("Stage left", network: true, mono: new OffsetClock(MonoClock.Instance, ahead));
        await using var follower = await HostRig.StartAsync("Stage right", network: true);
        HostRig.Introduce(leader, follower);
        Assert.True(await Eventually.TrueAsync(() => follower.Network.Hello!.Peers.Find(leader.Id) is not null));

        await leader.OkAsync("SYNC LEAD show");
        Assert.True(await Eventually.TrueAsync(() => leader.State().GetProperty("sync").GetProperty("role").GetString() == "leader"));
        // The leader's hello names its sync port once it answers on it.
        Assert.True(await Eventually.TrueAsync(() => follower.Network.Hello!.Peers.Find(leader.Id)?.Hello.Port("sync") == leader.Network.SyncPort));

        await follower.OkAsync("SYNC FOLLOW show " + leader.Id);
        Assert.True(await Eventually.TrueAsync(() => follower.State().GetProperty("sync").GetProperty("role").GetString() == "follower"));
        Assert.Equal(leader.Id, follower.State().GetProperty("sync").GetProperty("leader").GetString());

        // Bracketed by two reads of the leader's clock, so a pause between the reads widens the window, never the error.
        double error = double.NaN;
        Assert.True(await Eventually.TrueAsync(() =>
        {
            var before = leader.Engine.LeaderNowNs;
            var read = follower.Engine.LeaderNowNs;
            var after = leader.Engine.LeaderNowNs;
            error = read < before ? (before - read) / 1e6 : read > after ? (read - after) / 1e6 : 0;
            return error <= ToleranceMs;
        }), $"the follower's leader time is {error:0.###} ms outside the leader's");
        // STATE's offset is the reading's uncertainty, half the best round trip — not the clocks' five-second gap.
        var sync = follower.State().GetProperty("sync");
        Assert.Equal(sync.GetProperty("rttMs").GetDouble() / 2, sync.GetProperty("offsetMs").GetDouble(), 0.01);
        Assert.InRange(sync.GetProperty("offsetMs").GetDouble(), 0, 1000);
        Assert.True(await Eventually.TrueAsync(() => follower.Journal().Contains("following Stage left", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FetchPullsAFileByItsHashFromAPeerAndVerifiesIt()
    {
        // The holder has the file in its media store before it starts: the index finds it by its bytes.
        var holderDir = Directory.CreateTempSubdirectory("playmap-host-");
        var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        var hex = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Directory.CreateDirectory(Path.Combine(holderDir.FullName, "media"));
        await File.WriteAllBytesAsync(Path.Combine(holderDir.FullName, "media", "clip.bin"), bytes);
        await using var holder = await HostRig.StartAsync("Stage left", network: true, dir: holderDir);
        await using var fetcher = await HostRig.StartAsync("Stage right", network: true);
        Assert.True(await Eventually.TrueAsync(() => holder.Host.Media.Lookup(hex) is not null));

        var id = await fetcher.OkAsync("ADD media {\"name\":\"clip\",\"kind\":\"video\",\"location\":\"/elsewhere/clip.bin\",\"contentHash\":\"sha256:" + hex + "\",\"sizeBytes\":" + bytes.Length + "}");
        Assert.Equal("no", await fetcher.OkAsync("MEDIA HAS sha256:" + hex));
        Assert.True(await Eventually.TrueAsync(() => fetcher.State().GetProperty("media").GetProperty("missing").GetInt32() == 1));

        HostRig.Introduce(holder, fetcher);
        Assert.True(await Eventually.TrueAsync(() => fetcher.Network.Hello!.Peers.Find(holder.Id) is not null));
        var reply = await fetcher.RunAsync($"MEDIA FETCH {id} FROM {holder.Id}");
        Assert.True(reply.Ok, reply.Payload);
        Assert.False(reply.Applied);   // accepted: the pull runs beside the engine

        Assert.True(await Eventually.TrueAsync(() => fetcher.Host.Media.PathForHash("sha256:" + hex) is not null, 20000), fetcher.Journal());
        var landed = fetcher.Host.Media.PathForHash("sha256:" + hex)!;
        Assert.StartsWith(fetcher.Home.MediaStore, landed, StringComparison.Ordinal);
        Assert.Equal(hex, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(landed))));
        Assert.Equal("yes", await fetcher.OkAsync("MEDIA HAS sha256:" + hex));
        Assert.True(await Eventually.TrueAsync(() => fetcher.State().GetProperty("media").GetProperty("missing").GetInt32() == 0));
        // The file is a fact first; the journal's row and the transfer's leaving follow it.
        Assert.True(await Eventually.TrueAsync(() => fetcher.Journal().Contains("pulled from Stage left and verified", StringComparison.Ordinal)));
        Assert.True(await Eventually.TrueAsync(() => fetcher.State().GetProperty("media").GetProperty("transfers").GetArrayLength() == 0));
    }

    [Fact]
    public async Task ThePatternsLinkDrivesTheBlackoutAndKeepsItsTokenOffEveryLine()
    {
        const string deskToken = "AAAA-BBBB-CCCC";
        await using var desk = new FakePatternsDesk(deskToken);
        await using var rig = await HostRig.StartAsync();
        await using var c = await rig.WireAsync();
        var link = await c.AskAsync($"@l LINK PATTERNS 127.0.0.1:{desk.Port} {deskToken}");
        Assert.StartsWith("OK @l {\"stage\":\"accepted\"", link, StringComparison.Ordinal);

        JsonElement Patterns() => rig.State().GetProperty("links").GetProperty("patterns");
        JsonElement Air() => rig.State().GetProperty("air");
        Assert.True(await Eventually.TrueAsync(() => Patterns().GetProperty("state").GetString() == "live" && Patterns().GetProperty("paired").GetBoolean()));
        Assert.True(await Eventually.TrueAsync(() => Patterns().GetProperty("clip").GetString()!.StartsWith("VT ", StringComparison.Ordinal)));
        Assert.Contains(desk.Heard, l => l.Contains(" AUTH-PROOF ", StringComparison.Ordinal));
        Assert.False(Air().GetProperty("blackout").GetBoolean());

        await desk.PushAsync(FakePatternsDesk.StateWith(blackout: true));
        Assert.True(await Eventually.TrueAsync(() => Air().GetProperty("blackout").GetBoolean()));
        Assert.True(Air().GetProperty("patternsBlackout").GetBoolean());

        // The follow switch decides whether the desk's blackout is this node's.
        Assert.StartsWith("OK @f", await c.AskAsync("@f LINK PATTERNS FOLLOW-BLACKOUT OFF"), StringComparison.Ordinal);
        Assert.False(Air().GetProperty("blackout").GetBoolean());
        Assert.False(Air().GetProperty("followPatternsBlackout").GetBoolean());
        Assert.StartsWith("ERR @g already ignoring", await c.AskAsync("@g LINK PATTERNS FOLLOW-BLACKOUT OFF"), StringComparison.Ordinal);
        Assert.StartsWith("OK @h", await c.AskAsync("@h LINK PATTERNS FOLLOW-BLACKOUT ON"), StringComparison.Ordinal);
        Assert.True(Air().GetProperty("blackout").GetBoolean());

        await desk.PushAsync(FakePatternsDesk.StateWith(blackout: false));
        Assert.True(await Eventually.TrueAsync(() => !Air().GetProperty("blackout").GetBoolean()));

        // Kept for the next start, token and all — in the node's settings, and nowhere a peer reads.
        var kept = rig.Home.LoadSettings("unused");
        Assert.Equal($"127.0.0.1:{desk.Port}", kept.PatternsHost);
        Assert.Equal(deskToken, kept.PatternsToken);
        Assert.DoesNotContain(deskToken, rig.Engine.State().Json, StringComparison.Ordinal);
        Assert.DoesNotContain(deskToken, rig.Engine.Context(), StringComparison.Ordinal);
        Assert.DoesNotContain(deskToken, rig.Journal(), StringComparison.Ordinal);
        Assert.True(await Eventually.TrueAsync(() => rig.Journal().Contains("following Patterns at 127.0.0.1", StringComparison.Ordinal)));

        Assert.StartsWith("OK @o", await c.AskAsync("@o LINK PATTERNS OFF"), StringComparison.Ordinal);
        Assert.True(await Eventually.TrueAsync(() => Patterns().GetProperty("state").GetString() == "off"));
        Assert.True(await Eventually.TrueAsync(() => rig.Home.LoadSettings("unused") is { PatternsHost: "", PatternsToken: "" }));
    }
}
