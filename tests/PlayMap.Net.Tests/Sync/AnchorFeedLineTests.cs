using PlayMap.Net.Sync;

namespace PlayMap.Net.Tests.Sync;

/// <summary>The anchor feed's own rules, without a network: its lines read back and anything else is refused; an air line is
/// numbered and pushed in the order it was sent, to members and readers, and kept as its kind's latest even with no member; a
/// member that comes back from its own address replaces its old subscription, one from elsewhere does not; a receipt for a line
/// whose send stopped waiting still reaches the leader as a late one.</summary>
public sealed class AnchorFeedLineTests
{
    private sealed class Sink : IPushSink
    {
        public List<string> Lines { get; } = [];

        public bool Open { get; set; } = true;

        public void Latest(string key, string line) => Lines.Add(line);

        public bool InOrder(string line)
        {
            if (!Open)
                return false;
            lock (Lines)
                Lines.Add(line);
            return true;
        }
    }

    private static AnchorMember Member(string session, string node, string address = "127.0.0.1") => new(session, node, "Stage", address, ["op_1"]);

    /// <summary>A pushed air line's feed, sequence and line.</summary>
    private static (string Feed, long Seq, string Line) Air(string pushed)
    {
        Assert.True(AnchorFeed.TryParse(pushed, out _, out var key, out var json), pushed);
        Assert.Equal(AnchorFeed.Air, key);
        Assert.True(AnchorFeed.TryReadAir(json, out var feed, out var seq, out var line, out _), json);
        return (feed, seq, line);
    }

    [Fact]
    public void LinesReadBackAndAnythingElseIsRefused()
    {
        Assert.True(AnchorFeed.TryParse("ANCHOR show pl_show {\"a\":1}", out var g, out var k, out var j));
        Assert.Equal(("show", "pl_show", "{\"a\":1}"), (g, k, j));
        foreach (var bad in new[] { "", "ANCHOR", "ANCHOR show", "ANCHOR show pl_show", "ANCHOR show pl_show nojson", "ANCHORS overflow — read slowly",
                     "STATE {}", "ANCHOR  pl {}" })
            Assert.False(AnchorFeed.TryParse(bad, out _, out _, out _), bad);

        Assert.True(AnchorFeed.TryReadAir("{\"feed\":\"ab12cd34\",\"seq\":3,\"line\":\"PANIC\",\"leaderNs\":7}", out var feed, out var seq, out var line, out var at));
        Assert.Equal(("ab12cd34", 3L, "PANIC", 7L), (feed, seq, line, at));
        foreach (var bad in new[] { "[]", "{}", "{\"seq\":3,\"line\":\"PANIC\",\"leaderNs\":7}", "{\"feed\":\"f\",\"seq\":\"3\",\"line\":\"PANIC\",\"leaderNs\":7}",
                     "{\"feed\":\"f\",\"seq\":3,\"line\":\"PANIC\\nBLACKOUT ON\",\"leaderNs\":7}", "{\"feed\":\"f\",\"seq\":3,\"line\":\"\",\"leaderNs\":7}", "not json" })
            Assert.False(AnchorFeed.TryReadAir(bad, out _, out _, out _, out _), bad);
    }

    [Fact]
    public async Task AirLinesArePushedInOrderKeptWithNoMemberAndAnswersComeLateWhenTheyMust()
    {
        var feed = new AnchorFeed();
        // No member yet: sending returns at once and the line is kept as the latest of its kind.
        Assert.Empty(await feed.SendAirAsync("show", "blackout", "BLACKOUT ON", 5, TimeSpan.FromSeconds(1), CancellationToken.None));
        var reader = new Sink();
        using var read = feed.Subscribe(new AnchorMember("r1", "", "Desk", "127.0.0.1", []), reader);
        Assert.Single(reader.Lines, l => l.Contains("\"line\":\"BLACKOUT ON\"", StringComparison.Ordinal));
        Assert.Empty(feed.Members);

        var stage = new Sink();
        using var sub = feed.Subscribe(Member("s1", "9f8e7d6c"), stage);
        Assert.Equal(["9f8e7d6c"], feed.Members.Select(m => m.Node));
        stage.Lines.Clear();
        reader.Lines.Clear();

        // Two lines sent one after the other reach the member in that order, numbered in that order.
        var first = feed.SendAirAsync("show", "panic", "PANIC FADE 0", 10, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        var second = feed.SendAirAsync("show", "panic", "PANIC CLEAR FADE 0", 11, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Equal(2, stage.Lines.Count);
        var (f0, s0, l0) = Air(stage.Lines[0]);
        var (f1, s1, l1) = Air(stage.Lines[1]);
        Assert.Equal(("PANIC FADE 0", "PANIC CLEAR FADE 0"), (l0, l1));
        Assert.Equal(feed.Instance, f0);
        Assert.Equal(f0, f1);
        Assert.Equal(s0 + 1, s1);
        Assert.Equal(stage.Lines, reader.Lines);   // a reader gets the lines too, and is never waited for

        Assert.True(feed.Receipt("s1", s0, "applied", 10, ""));
        Assert.False(feed.Receipt("r1", s1, "applied", 11, ""));     // not a member's own line
        Assert.Equal("applied", Assert.Single(await first).Stage);
        Assert.Equal(("silent", "9f8e7d6c"), (Assert.Single(await second).Stage, (await second)[0].Node));

        // A line for a later instant: accepted within the wait, applied after it — the late receipt still reaches the leader.
        var late = new List<(string Line, AirReceipt Receipt)>();
        feed.LateReceipt += (l, r) => late.Add((l, r));
        stage.Lines.Clear();
        var later = feed.SendAirAsync("show", null, "BLACKOUT OFF", 99, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var (_, s2, _) = Air(stage.Lines.Single());
        Assert.True(feed.Receipt("s1", s2, "accepted", 99, ""));
        Assert.Equal("accepted", Assert.Single(await later).Stage);
        Assert.True(feed.Receipt("s1", s2, "applied", 99, "blackout off"));
        Assert.Equal(("BLACKOUT OFF", "applied", 99L), (late.Single().Line, late.Single().Receipt.Stage, late.Single().Receipt.AppliedLeaderNs));
        Assert.False(feed.Receipt("s1", s2, "applied", 99, ""));    // once
    }

    [Fact]
    public async Task EveryMemberHasTheSameOneDeadlineHoweverManyThereAre()
    {
        var feed = new AnchorFeed();
        var subs = Enumerable.Range(0, 20).Select(i => feed.Subscribe(
            Member("s" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), (0x10000000 + i).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)), new Sink())).ToList();
        try
        {
            var took = System.Diagnostics.Stopwatch.StartNew();
            var sent = feed.SendAirAsync("show", null, "BLACKOUT ON", 5, TimeSpan.FromMilliseconds(100), CancellationToken.None);
            Assert.True(feed.Receipt("s7", 1, "applied", 5, ""));
            var receipts = await sent;
            // Twenty silent members waited for one after another would take two seconds; together they take the one deadline.
            Assert.True(took.ElapsedMilliseconds < 1_500, $"{took.ElapsedMilliseconds} ms");
            Assert.Equal(20, receipts.Count);
            Assert.Equal(19, receipts.Count(r => r.Stage == "silent"));
            Assert.Equal("applied", receipts[7].Stage);
        }
        finally
        {
            foreach (var s in subs)
                s.Dispose();
        }
    }

    [Fact]
    public void AMemberThatComesBackReplacesItselfOnlyFromItsOwnAddressAndAnOverflowedOneLeaves()
    {
        var feed = new AnchorFeed();
        var old = new Sink();
        using var a = feed.Subscribe(Member("s1", "9f8e7d6c"), old);
        using var b = feed.Subscribe(Member("s2", "9f8e7d6c"), new Sink());
        Assert.Equal(["s2"], feed.Members.Select(m => m.SessionId));
        using var c = feed.Subscribe(Member("s3", "9f8e7d6c", "10.0.0.66"), new Sink());
        Assert.Equal(["s2", "s3"], feed.Members.Select(m => m.SessionId));

        var slow = new Sink { Open = false };
        using var d = feed.Subscribe(Member("s4", "1a2b3c4d"), slow);
        feed.Publish("show", "pl_show", "{}");
        Assert.DoesNotContain(feed.Members, m => m.SessionId == "s4");   // its topic closed on overflow: it rejoins with a new SUBSCRIBE
    }
}
