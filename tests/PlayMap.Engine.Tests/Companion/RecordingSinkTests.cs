namespace PlayMap.Engine.Tests.Companion;

/// <summary>The tests' recording sink does what its waits promise: a wait is met at once by a line already said and otherwise by the
/// next line that matches, and a wait whose test throws on a line fails with that error while the line's speaker and the other waits
/// go on.</summary>
public sealed class RecordingSinkTests
{
    [Fact]
    public async Task AWaitIsMetAtOnceByALineAlreadySaidAndOtherwiseByTheNextThatMatches()
    {
        var sink = new RecordingSink();
        sink.Latest("index", "INDEX {\"light\":\"amber\"}");

        Assert.True(sink.Heard("\"amber\"").IsCompletedSuccessfully);
        Assert.True(sink.Heard(l => l.StartsWith("INDEX ", StringComparison.Ordinal)).IsCompletedSuccessfully);

        var green = sink.Heard("\"green\"");
        Assert.False(green.IsCompleted);
        sink.Latest("index", "INDEX {\"light\":\"green\"}");
        await green;
        Assert.Equal("INDEX {\"light\":\"green\"}", sink.Latests["index"]);
    }

    [Fact]
    public async Task AWaitWhoseTestThrowsFailsWithItsOwnErrorAndTheSpeakerGoesOn()
    {
        var sink = new RecordingSink();
        var unreadable = sink.Heard(_ => throw new InvalidOperationException("this line has no such row"));
        var heard = sink.Heard("pushed");

        // The speaker is the engine's push loop in the rig: a test's error must not reach it.
        sink.Latest("index", "INDEX pushed");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => unreadable);
        Assert.Equal("this line has no such row", error.Message);
        await heard;
        Assert.Equal("INDEX pushed", Assert.Single(sink.Lines));
        Assert.Equal("INDEX pushed", sink.Latests["index"]);
    }
}
