using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Sync;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;
using PlayMap.Engine.Sync;
using PlayMap.Engine.Tests.Host;

namespace PlayMap.Engine.Tests.Sync;

/// <summary>A group of real nodes in one process for the sync tests: each on one shared manual clock (the leader's moved ahead by a
/// fixed offset: another machine's), with the sync part paced fast and synthetic vblanks, finding each other over loopback.</summary>
internal static class SyncRig
{
    public const long Second = 1_000_000_000;
    public const string LeaderId = "0badc0de";
    public const string FollowerId = "9f8e7d6c";

    /// <summary>op_1 shows pl_show (in group "show": items it_a, it_b, it_c of ten seconds, generators); pl_local is in no group (one
    /// ten-minute item).</summary>
    public static Project Show()
    {
        var p = Project.CreateDefault("Sync test");
        var gen = p.Generators[0];
        PlaylistItem Item(string id, string label, long ns) => PlaylistItem.For(id, SourceRef.Generator(gen.Id), label) with { DurationNs = ns };
        return p with
        {
            Playlists =
            [
                new Playlist("pl_show", "Show", [Item("it_a", "A", 10 * Second), Item("it_b", "B", 10 * Second), Item("it_c", "C", 10 * Second)], false, "show"),
                new Playlist("pl_local", "Local", [Item("it_l", "L", 600 * Second)], false, null),
            ],
            Outputs = [p.Outputs[0] with { Mode = OutputMode.Source, Source = SourceRef.Playlist("pl_show") }],
        };
    }

    public static SyncNodeOptions Fast(IVblankSource? vblanks = null) => new()
    {
        WatchEvery = TimeSpan.FromMilliseconds(20),
        ReportEvery = TimeSpan.FromMilliseconds(100),
        Vblanks = vblanks is null ? null : _ => vblanks,
    };

    public static Task<HostRig> StartAsync(string name, string id, IMonoClock mono, SyncNodeOptions? options = null, DirectoryInfo? dir = null,
        Func<RenderHostRequest, IRenderHost?>? render = null) =>
        HostRig.StartAsync(name, network: true, mono: mono,
            parts: new EngineParts { Extensions = [SyncExtension.With(options ?? Fast())], RenderHost = render },
            settings: s => s with { NodeId = id }, dir: dir);

    /// <summary>Opens the test show on a node (its own copy, in its folder).</summary>
    public static async Task OpenShowAsync(HostRig rig, Project? project = null)
    {
        var path = Path.Combine(rig.Dir.FullName, "show.playmap");
        await File.WriteAllTextAsync(path, ProjectJson.Serialize(project ?? Show()));
        await rig.OkAsync("PROJECT OPEN " + WireGrammar.Quote(path));
    }

    public static JsonElement Sync(HostRig rig) => rig.State().GetProperty("sync");

    /// <summary>The leader leads "show"; the follower, once it hears it, follows it and has its clock, its feed, and is a member.</summary>
    public static async Task GroupAsync(HostRig leader, HostRig follower)
    {
        HostRig.Introduce(leader, follower);
        await leader.OkAsync("SYNC LEAD show");
        Assert.True(await Eventually.TrueAsync(() => follower.Network.Hello!.Peers.Find(leader.Id)?.Hello.Port("sync") == leader.Network.SyncPort));
        await follower.OkAsync("SYNC FOLLOW show " + leader.Id);
        await CaughtUpAsync(leader, follower);
    }

    public static async Task CaughtUpAsync(HostRig leader, HostRig follower)
    {
        // The manual clock stands still: once the follower's estimate is in, both read the same leader instant exactly.
        Assert.True(await Eventually.TrueAsync(() => follower.Engine.LeaderNowNs == leader.Engine.LeaderNowNs),
            $"the follower reads {follower.Engine.LeaderNowNs}, the leader {leader.Engine.LeaderNowNs}");
        // The clock reads the new line as its step begins on the lane; STATE counts the step once it is taken in full.
        Assert.True(await Eventually.TrueAsync(() => Sync(follower).GetProperty("steps").GetInt64() == follower.Engine.Services.Clock.Steps),
            follower.Journal());
        Assert.True(await Eventually.TrueAsync(() => Sync(follower).GetProperty("feed").GetString() == "live"), follower.Journal());
        Assert.True(await Eventually.TrueAsync(() => leader.Engine.Services.Fanout!.Members.Contains(follower.Id)));
    }

    /// <summary>The node's sync:&lt;group&gt; item in its context (its light is the group's as this node tells it).</summary>
    public static JsonElement GroupItem(HostRig rig, string group = "show") =>
        JsonDocument.Parse(rig.Engine.Context()).RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "sync:" + group).Clone();
}

/// <summary>A monotonic clock moved by an offset a test may change (another machine's clock, stepped or drifting).</summary>
internal sealed class MovableClock(IMonoClock inner, long offsetNs) : IMonoClock
{
    private long _offset = offsetNs;

    public long OffsetNs
    {
        get => Interlocked.Read(ref _offset);
        set => Interlocked.Exchange(ref _offset, value);
    }

    public long NowNs => inner.NowNs + OffsetNs;
}

/// <summary>Synthetic vblanks: each output at its period with a vblank at a given local instant (a test may change them, or empty
/// them to stop the reports).</summary>
internal sealed class FakeVblanks(params OutputVblank[] outputs) : IVblankSource
{
    private volatile OutputVblank[] _outputs = outputs;

    public IReadOnlyList<OutputVblank> Read() => _outputs;

    public void Set(params OutputVblank[] now) => _outputs = now;

    public static OutputVblank At(string outputId, double hz, long vblankLocalNs, LockState state = LockState.FreeMeasured) =>
        new(outputId, state, 1, (long)Math.Round(1e12 / hz), vblankLocalNs, 600, 0, true, 16_000, 0);
}
