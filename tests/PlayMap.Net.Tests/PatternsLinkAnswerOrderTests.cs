using PlayMap.Net.Links;

namespace PlayMap.Net.Tests;

/// <summary>The STATE a Patterns link asks once the desk pairs, against the STATEs the desk pushes: the answer settles on a pool
/// continuation while the link's reader goes on and takes the next push at once, so a push the desk said right after its answer can be
/// taken first. The answer is taken only if no push has been taken since the ask, so the link never ends on an answer older than a
/// push it followed; with no push since, the answer is the desk's state and is taken. The link's seam holds the answer between its
/// arrival and its take, so the order is the test's: nothing waits on the pool's timing.</summary>
public class PatternsLinkAnswerOrderTests
{
    [Fact]
    public async Task AnAnswerOvertakenByAPushTheLinkHasTakenIsSetAsideAndTheLinkFollowsThePush()
    {
        var asked = FakePatternsDesk.StateWith(1000, 3, playing: false, blackout: true, tag: "ASKED");
        var pushed = FakePatternsDesk.StateWith(5000, 4, playing: false, blackout: false, tag: "PUSHED");
        await using var desk = new FakePatternsDesk(TestSettings.Token) { State = asked };
        await using var link = NewLink(desk);
        var hold = new AnswerHold();
        link.BeforeStatusAnswerTaken = hold.WaitAsync;
        link.Start();

        // The desk has answered STATUS with its blackout and the ASKED clip, and the answer waits at the link's take.
        await hold.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, link.StatusAnswers);
        Assert.Null(link.StateJson);

        // The desk's next word lifts the blackout on another clip; the link takes the push while the answer waits.
        await desk.PushAsync(pushed);
        Assert.True(await Eventually.TrueAsync(() => link.Video is { Tag: "PUSHED" }), $"the push is taken: {link.StateJson}");
        Assert.False(link.Blackout);
        var said = desk.Said.ToList();
        var answer = said.FindIndex(l => l.StartsWith("OK @pm", StringComparison.Ordinal) && l.EndsWith(" " + asked, StringComparison.Ordinal));
        var push = said.IndexOf("STATE " + pushed);
        Assert.True(answer >= 0 && push > answer, "the desk said its answer, then the push: " + string.Join(" | ", said));

        hold.Release();
        Assert.True(await Eventually.TrueAsync(() => link.StatusAnswers == 1), "the answer reached the take");
        Assert.Equal(((bool?)false, "PUSHED", 4L, pushed), (link.Blackout, link.Video?.Tag, link.Video?.Seq, link.StateJson));
        Assert.Equal(1, link.StatusAnswersSetAside);
    }

    [Fact]
    public async Task AnAnswerWithNoPushSinceItsAskIsTheDesksStateAndIsTaken()
    {
        var asked = FakePatternsDesk.StateWith(1000, 3, playing: false, blackout: true, tag: "ASKED");
        await using var desk = new FakePatternsDesk(TestSettings.Token) { State = asked };
        await using var link = NewLink(desk);
        var hold = new AnswerHold();
        link.BeforeStatusAnswerTaken = hold.WaitAsync;
        link.Start();

        await hold.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        hold.Release();
        Assert.True(await Eventually.TrueAsync(() => link.StatusAnswers == 1), "the answer reached the take");
        Assert.Equal(((bool?)true, "ASKED", 3L, asked), (link.Blackout, link.Video?.Tag, link.Video?.Seq, link.StateJson));
        Assert.Equal(0, link.StatusAnswersSetAside);
        Assert.Equal(1, link.SetAsideBeforePairing);   // the greeting, said before the desk paired
    }

    private static PatternsLink NewLink(FakePatternsDesk desk) => new(new PatternsLinkOptions
    {
        Host = "127.0.0.1",
        Port = desk.Port,
        Token = TestSettings.Token,
        ContextEvery = TimeSpan.FromHours(1),
    }, TestSettings.Wall());

    /// <summary>The link's STATUS answer held between its arrival and its take: <see cref="Reached"/> once it waits, <see cref="Release"/>
    /// lets the take go on.</summary>
    private sealed class AnswerHold
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(CancellationToken ct)
        {
            Reached.TrySetResult();
            await _released.Task.WaitAsync(ct);
        }

        public void Release() => _released.TrySetResult();
    }
}
