using PlayMap.Core.Media;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>A capture URI the grammar refuses, sent on a line the node takes (INPUT ADD) or one it does not route yet (OBSERVE): the
/// answer is the grammar's words, nothing moves, and no journal row — its text or its line — holds anything of the URI.</summary>
public sealed class CaptureUriJournalTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private static async Task<CommandReply> Run(EngineCore engine, string line)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await engine.HandleAsync(parsed, Desk, CancellationToken.None);
    }

    [Theory]
    [InlineData("capture:dshow/cam?token=zz9secret", "carries no query")]
    [InlineData("capture://zz9user:zz9secret@dshow/cam", "carries no user information")]
    [InlineData("capture:dshow/zz9user:zz9secret@cam", "carries no user information")]
    [InlineData("capture:zz9provider/cam", "names one of the providers")]
    public async Task ARefusedCaptureUriIsAnsweredWithTheGrammarsWordsAndNothingOfItIsJournalled(string uri, string words)
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var output = engine.Store.Project.Outputs[0].Id;
        var revision = engine.Store.Current.Revision;
        var media = engine.Store.Project.Media.Length;

        var add = await Run(engine, $"INPUT ADD \"{uri}\" Front");
        Assert.False(add.Ok);
        Assert.Contains(words, add.Payload, StringComparison.Ordinal);
        Assert.Equal(CaptureUri.TryParse(uri, out _, out var grammar) ? "" : grammar, add.Payload);

        var observe = await Run(engine, $"OBSERVE {output} FROM \"{uri}\"");
        Assert.False(observe.Ok);
        Assert.Equal("OBSERVE is not available on this node yet", observe.Payload);

        Assert.Equal(revision, engine.Store.Current.Revision);
        Assert.Equal(media, engine.Store.Project.Media.Length);
        var rows = engine.Journal.Recent(engine.Journal.RecentCapacity);
        Assert.Contains(rows, r => r.Line?.StartsWith("INPUT ADD " + CaptureUri.RefusedMarker, StringComparison.Ordinal) == true);
        Assert.Contains(rows, r => r.Line?.StartsWith($"OBSERVE {output} FROM {CaptureUri.RefusedMarker}", StringComparison.Ordinal) == true);
        Assert.All(rows, r =>
        {
            Assert.DoesNotContain("zz9", r.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("zz9", r.Line ?? "", StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AWellFormedCaptureUriIsJournalledAsItCameAndAnsweredNotAvailableYet()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var reply = await Run(engine, "INPUT ADD \"capture:decklink/DeckLink Duo (1)/sdi\" Front");
        Assert.False(reply.Ok);
        Assert.StartsWith("INPUT ADD capture: is not available on this node yet — ", reply.Payload, StringComparison.Ordinal);
        Assert.Contains(engine.Journal.Recent(engine.Journal.RecentCapacity),
            r => r.Line?.Contains("capture:decklink/DeckLink Duo (1)/sdi", StringComparison.Ordinal) == true);
    }
}
