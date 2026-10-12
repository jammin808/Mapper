using System.Text.Json;
using PlayMap.Core.Wire;

namespace PlayMap.Assistant.Tests;

/// <summary>The loop against a scripted model and a fake node: reads run freely, every change goes through
/// the gate — a low-risk verb runs through the node's endpoint and its reply goes back to the model, a
/// high-risk verb waits for a person and never runs on the model's word, a bad line is refused with its reason
/// — and the history stays append-only and whole whatever happens.</summary>
public class AssistantSessionTests
{
    private static readonly AssistantSettings On = AssistantSettings.Default with { Enabled = true };

    private static (AssistantSession Session, FakeEndpoint Node, ProposalQueue Queue) Make(ScriptedProvider provider, AssistantSettings? settings = null, AssistantSessionOptions? options = null)
    {
        var node = new FakeEndpoint();
        var queue = new ProposalQueue(node);
        var session = new AssistantSession(provider, node, queue, settings ?? On, options ?? new AssistantSessionOptions { SessionId = "t1" });
        return (session, node, queue);
    }

    private static ToolResultPart ResultFor(AssistantRequest request, string callId) =>
        request.Messages[^1].Parts.OfType<ToolResultPart>().Single(r => r.ToolCallId == callId);

    [Fact]
    public async Task TheLoopReadsStateThenRunsALowRiskVerbAndReportsTheNodesOkBackToTheModel()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Tool("c1", AssistantTools.GetState)))
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c2", "A snow generator for the windows", "GENERATOR NEW particles.field Snow2")))
            .Then(ScriptedProvider.Say("I made a snow generator (gn7e7e7e7e)."));
        var (session, node, _) = Make(provider);

        var result = await session.AskAsync("make me a snow generator");

        Assert.Equal(TurnOutcome.Answered, result.Outcome);
        Assert.Equal(3, provider.Requests.Count);
        // The state the model asked for came back as the tool's result.
        Assert.Contains("\"name\":\"Stage left\"", ResultFor(provider.Requests[1], "c1").Content, StringComparison.Ordinal);
        // The line ran once, through the node's endpoint, as the assistant.
        var (line, who) = Assert.Single(node.Handled);
        Assert.Equal("GENERATOR NEW particles.field Snow2", line);
        Assert.Equal("assistant", who.Transport);
        Assert.True(who.Paired);
        // What ran and what the node answered went back to the model as facts.
        var report = ResultFor(provider.Requests[2], "c2");
        Assert.False(report.IsError);
        using var doc = JsonDocument.Parse(report.Content);
        var row = doc.RootElement.GetProperty("results")[0];
        Assert.Equal("ran", row.GetProperty("status").GetString());
        Assert.Equal("low", row.GetProperty("risk").GetString());
        Assert.StartsWith("OK gn7e7e7e7e", row.GetProperty("reply").GetString(), StringComparison.Ordinal);
        var outcome = Assert.Single(result.Lines);
        Assert.Equal(LineStatus.Ran, outcome.Status);
        Assert.Equal("1 line ran.", result.Status);
        Assert.Equal("I made a snow generator (gn7e7e7e7e).", result.Text);
    }

    [Fact]
    public async Task AHighRiskVideoPlayIsQueuedAndNeverRunsUntilAPersonApprovesIt()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "Start the show", "VIDEO PLAY ON pl00000001")))
            .Then(ScriptedProvider.Say("The show will start once you approve it."));
        // Even with every setting that can be on turned on, high risk waits.
        var (session, node, queue) = Make(provider, On with { RunLowWithoutAsking = true, RunReviewWithoutAsking = true });

        var result = await session.AskAsync("start the show");

        Assert.Empty(node.Handled);
        var proposal = Assert.Single(queue.Pending);
        Assert.Equal("VIDEO PLAY ON pl00000001", proposal.Line);
        Assert.Equal(VerbRisk.High, proposal.Risk);
        Assert.False(proposal.PreTicked);
        Assert.Equal("Start the show", proposal.Reason);
        Assert.Equal([proposal.Id], result.Queued);
        using (var doc = JsonDocument.Parse(ResultFor(provider.Requests[1], "c1").Content))
        {
            var row = doc.RootElement.GetProperty("results")[0];
            Assert.Equal("queued", row.GetProperty("status").GetString());
            Assert.Equal(proposal.Id, row.GetProperty("proposal").GetString());
            Assert.Contains("has not run", row.GetProperty("reply").GetString(), StringComparison.Ordinal);
            Assert.Contains("not done yet", doc.RootElement.GetProperty("summary").GetString(), StringComparison.Ordinal);
        }

        var approved = await queue.ApproveAsync(proposal.Id, "Sam", CancellationToken.None);

        var (line, who) = Assert.Single(node.Handled);
        Assert.Equal("VIDEO PLAY ON pl00000001", line);
        Assert.Equal("approval", who.Transport);
        Assert.Equal("Sam", who.ClientName);
        Assert.Equal(ProposalState.Done, approved.State);
        Assert.Equal(true, approved.ResultOk);
        Assert.Equal("OK", approved.ResultWords);
        Assert.Empty(queue.Pending);
        // The next request tells the model what became of it.
        provider.Then(ScriptedProvider.Say("It is playing."));
        await session.AskAsync("is it playing?");
        var snapshot = provider.Requests[^1].Messages[^1].Parts.OfType<TextPart>().First().Text;
        Assert.Contains($"{proposal.Id} VIDEO PLAY ON pl00000001 (high) — approved by Sam, ran: OK", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownVerbIsRefusedAndTheReasonGoesBackToTheModelWithNothingRun()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "fly", "FLY AWAY", "PLAYLIST NEW Fine")))
            .Then(ScriptedProvider.Say("That is not something PlayMap does."));
        var (session, node, queue) = Make(provider);

        var result = await session.AskAsync("fly away");

        Assert.Empty(node.Handled);
        Assert.Empty(queue.All);
        var refusal = ResultFor(provider.Requests[1], "c1");
        Assert.True(refusal.IsError);
        Assert.Contains("'FLY AWAY' is not a PlayMap verb", refusal.Content, StringComparison.Ordinal);
        Assert.Contains("Nothing ran and nothing was queued", refusal.Content, StringComparison.Ordinal);
        Assert.Equal(TurnOutcome.Answered, result.Outcome);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public async Task AMalformedLineIsRefusedWithTheGrammarsOwnReason()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "a list", "PLAYLIST NEW \"Main show")))
            .Then(ScriptedProvider.Say("Sorry."));
        var (session, node, _) = Make(provider);

        await session.AskAsync("make a playlist");

        Assert.Empty(node.Handled);
        var refusal = ResultFor(provider.Requests[1], "c1");
        Assert.True(refusal.IsError);
        Assert.Contains("a quote that does not close", refusal.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALocalModelGetsOneRetryAfterARefusedCallAndThenTheSessionStops()
    {
        var provider = new ScriptedProvider(AssistantProviderKind.OpenAiCompatible, validationRetries: 1)
        {
            Forever = _ => ScriptedProvider.Call(ScriptedProvider.Propose("c" + Guid.NewGuid().ToString("N"), "?", "SURFACE SOURCE")),
        };
        var (session, node, _) = Make(provider);

        var result = await session.AskAsync("snow on the windows");

        Assert.Equal(TurnOutcome.InvalidCalls, result.Outcome);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Empty(node.Handled);
        Assert.Contains("SURFACE SOURCE needs <surface>", ResultFor(provider.Requests[1], provider.Requests[1].Messages[^2].ToolCalls.Single().Id).Content, StringComparison.Ordinal);
        Assert.Contains("larger local model", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinesAfterTheFirstLineThatNeedsAPersonQueueInOrder()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "Snow on the left window",
                "PLAYLIST NEW Extra", $"SURFACE SOURCE {SampleShow.WindowLeft} generator:{SampleShow.Snow}", "PLAYLIST NEW After")))
            .Then(ScriptedProvider.Say("Done what I could."));
        var (session, node, queue) = Make(provider);

        var result = await session.AskAsync("snow on the left window");

        Assert.Equal(["PLAYLIST NEW Extra"], node.Handled.Select(h => h.Line));
        Assert.Equal([LineStatus.Ran, LineStatus.Queued, LineStatus.Queued], result.Lines.Select(l => l.Status));
        var pending = queue.Pending;
        Assert.Equal(2, pending.Count);
        Assert.Equal(VerbRisk.High, pending[0].Risk);
        Assert.Equal(VerbRisk.Low, pending[1].Risk);
        Assert.True(pending[1].PreTicked);
        Assert.Equal(pending[0].Batch, pending[1].Batch);
        Assert.Equal([1, 2], pending.Select(p => p.Order));
    }

    [Fact]
    public async Task AQueryAfterALineThatWaitsStillRunsBecauseItChangesNothing()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "start and look", "VIDEO PLAY", "PLAYLIST LIST")))
            .Then(ScriptedProvider.Say("Waiting for you."));
        var (session, node, queue) = Make(provider);

        var result = await session.AskAsync("start");

        Assert.Equal(["PLAYLIST LIST"], node.Handled.Select(h => h.Line));
        Assert.Equal([LineStatus.Queued, LineStatus.Ran], result.Lines.Select(l => l.Status));
        Assert.Single(queue.Pending);
    }

    [Fact]
    public async Task ACallInAnAnswerThatRanOutOfRoomIsAnsweredButNeverRun()
    {
        var provider = new ScriptedProvider()
            .Then(new AssistantReply([new TextPart("Starting"), ScriptedProvider.Propose("c1", "go", "PLAYLIST NEW Cut")], StopKind.MaxTokens, default, "scripted-model"));
        var (session, node, _) = Make(provider);

        var result = await session.AskAsync("go");

        Assert.Equal(TurnOutcome.MaxTokens, result.Outcome);
        Assert.Empty(node.Handled);
        var answered = Assert.Single(session.History[^1].Parts.OfType<ToolResultPart>());
        Assert.Equal("c1", answered.ToolCallId);
        Assert.True(answered.IsError);
    }

    [Fact]
    public async Task ALineTheNodeRefusesStopsTheLinesAfterIt()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "two lists", "PLAYLIST NEW One", "PLAYLIST NEW Two")))
            .Then(ScriptedProvider.Say("The node refused."));
        var (session, node, queue) = Make(provider);
        node.Reply = _ => CommandReply.Fail("a playlist called One already exists");

        var result = await session.AskAsync("two playlists");

        Assert.Single(node.Handled);
        Assert.Empty(queue.All);
        Assert.Equal([LineStatus.Refused, LineStatus.NotRun], result.Lines.Select(l => l.Status));
        Assert.Equal("ERR a playlist called One already exists", result.Lines[0].Reply);
        Assert.Contains("refused by the node (ERR — it did not happen)", ResultFor(provider.Requests[1], "c1").Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALineNamingAnIdTheProjectDoesNotHoldIsRefusedBeforeAnythingRuns()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Propose("c1", "snow", $"SURFACE SOURCE sf99999999 generator:{SampleShow.Snow}")))
            .Then(ScriptedProvider.Say("I could not find that surface."));
        var (session, node, queue) = Make(provider);

        await session.AskAsync("snow on the chimney");

        Assert.Empty(node.Handled);
        Assert.Empty(queue.All);
        Assert.Contains("no entity 'sf99999999' in the project", ResultFor(provider.Requests[1], "c1").Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryToolCallOfATurnIsAnsweredInOneMessage()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(
                ScriptedProvider.Tool("c1", AssistantTools.GetContext),
                ScriptedProvider.Tool("c2", AssistantTools.GetEntity, $$"""{"id":"{{SampleShow.WindowRight}}"}"""),
                ScriptedProvider.Tool("c3", AssistantTools.ListVerbs, """{"category":"mapping"}""")))
            .Then(ScriptedProvider.Say("Read."));
        var (session, _, _) = Make(provider);

        await session.AskAsync("look around");

        var answer = provider.Requests[1].Messages[^1];
        Assert.Equal(AssistantRole.User, answer.Role);
        Assert.Equal(["c1", "c2", "c3"], answer.Parts.OfType<ToolResultPart>().Select(r => r.ToolCallId));
        Assert.Contains("\"path\":\"stage.surfaces[1]\"", ResultFor(provider.Requests[1], "c2").Content, StringComparison.Ordinal);
        Assert.Contains("SURFACE SOURCE", ResultFor(provider.Requests[1], "c3").Content, StringComparison.Ordinal);
        Assert.DoesNotContain("VIDEO PLAY", ResultFor(provider.Requests[1], "c3").Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHistoryIsAppendOnlyAndThinkingIsEchoedBackUnchanged()
    {
        var thinking = new ThinkingPart("", "sig-abc");
        var provider = new ScriptedProvider()
            .Then(new AssistantReply([thinking, ScriptedProvider.Tool("c1", AssistantTools.GetState)], StopKind.ToolUse, default, "scripted-model"))
            .Then(ScriptedProvider.Say("Fine."));
        var (session, _, _) = Make(provider);

        await session.AskAsync("how is it?");
        provider.Then(ScriptedProvider.Say("Still fine."));
        await session.AskAsync("and now?");

        var first = provider.Requests[1].Messages;
        var last = provider.Requests[^1].Messages;
        // Every message sent before is sent again, the same objects in the same order.
        Assert.True(last.Count > first.Count);
        for (var i = 0; i < first.Count; i++)
            Assert.Same(first[i], last[i]);
        Assert.Same(thinking, first[1].Parts[0]);
    }

    [Fact]
    public async Task CancellingWhileALineRunsAnswersEveryCallSoTheHistoryStaysWhole()
    {
        using var cts = new CancellationTokenSource();
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(
                ScriptedProvider.Propose("c1", "a list", "PLAYLIST NEW One"),
                ScriptedProvider.Tool("c2", AssistantTools.GetState)));
        var (session, node, _) = Make(provider);
        node.OnHandle = _ => cts.Cancel();

        var result = await session.AskAsync("make a list", cts.Token);

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        var answers = session.History[^1];
        Assert.Equal(AssistantRole.User, answers.Role);
        Assert.Equal(["c1", "c2"], answers.Parts.OfType<ToolResultPart>().Select(r => r.ToolCallId));
        Assert.Contains("whether it applied is not known", answers.Parts.OfType<ToolResultPart>().First().Content, StringComparison.Ordinal);
        Assert.Equal(LineStatus.Unknown, Assert.Single(result.Lines).Status);
        Assert.False(session.Busy);
    }

    [Fact]
    public async Task TheBudgetStopsALoopThatNeverEnds()
    {
        var provider = new ScriptedProvider { Forever = r => ScriptedProvider.Call(ScriptedProvider.Tool("c" + r.Messages.Count, AssistantTools.GetState)) };
        var (session, _, _) = Make(provider, On with { MaxToolIterations = 3 });

        var result = await session.AskAsync("loop");

        Assert.Equal(TurnOutcome.BudgetSpent, result.Outcome);
        Assert.Equal(3, result.ModelCalls);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Contains("go on", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalIsToldPlainlyWithItsCategory()
    {
        var provider = new ScriptedProvider()
            .Then(new AssistantReply([], StopKind.Refusal, default, "scripted-model", "cyber", "This looks like an attack."));
        var (session, _, _) = Make(provider);

        var result = await session.AskAsync("something it will not do");

        Assert.Equal(TurnOutcome.Refused, result.Outcome);
        Assert.Contains("declined this request (category: cyber). This looks like an attack.", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTranscriptKeepsWordsAndCallsButNoThinkingAndNoSecrets()
    {
        var provider = new ScriptedProvider()
            .Then(new AssistantReply([new ThinkingPart("private reasoning", "sig"), new TextPart("Looking."), ScriptedProvider.Tool("c1", AssistantTools.GetState)], StopKind.ToolUse, default, "scripted-model"))
            .Then(ScriptedProvider.Say("All green."));
        var (session, _, _) = Make(provider, options: new AssistantSessionOptions { SessionId = "t1", Secrets = ["0badc0de-pairing-token"] });
        var logged = new List<string>();
        session.Logged += logged.Add;

        await session.AskAsync("my key is sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUV and the token 0badc0de-pairing-token — how is it?");

        var json = session.TranscriptJson();
        Assert.Contains("Looking.", json, StringComparison.Ordinal);
        Assert.Contains("get_state", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private reasoning", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-api03", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0badc0de-pairing-token", json, StringComparison.Ordinal);
        // The key never left the node either: the model was sent the masked words.
        var sent = provider.Requests[0].Messages[0].Parts.OfType<TextPart>().Last().Text;
        Assert.Contains(SecretRedactor.Mask, sent, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-api03", sent, StringComparison.Ordinal);
        Assert.All(logged, l => Assert.DoesNotContain("0badc0de-pairing-token", l, StringComparison.Ordinal));
        Assert.Contains(logged, l => l.Contains("cache read 0, cache write 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStreamOfEventsCarriesTextAndEndsWithTheResult()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Tool("c1", AssistantTools.GetState)))
            .Then(ScriptedProvider.Say("All green."));
        var (session, _, _) = Make(provider);

        var events = new List<IAssistantEvent>();
        await foreach (var e in session.AskStreamAsync("how is it?"))
            events.Add(e);

        Assert.Contains(events, e => e is ToolCalled { Tool: AssistantTools.GetState });
        Assert.Contains(events, e => e is TextDelta { Text: "All green." });
        var done = Assert.IsType<TurnDone>(events[^1]);
        Assert.Equal(TurnOutcome.Answered, done.Result.Outcome);
    }

    [Fact]
    public async Task AnAssistantThatIsOffSendsNothing()
    {
        var provider = new ScriptedProvider();
        var (session, _, _) = Make(provider, AssistantSettings.Default);

        var result = await session.AskAsync("hello");

        Assert.Equal(TurnOutcome.NotSent, result.Outcome);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task TheSnapshotRidesInTheUserTurnAndTheSystemPromptStaysStatic()
    {
        var provider = new ScriptedProvider().Then(ScriptedProvider.Say("Hi."));
        var (session, _, _) = Make(provider);

        await session.AskAsync("hello");

        var request = provider.Requests[0];
        Assert.Same(AssistantPrompt.System, request.System);
        var user = request.Messages[0];
        Assert.Equal(AssistantRole.User, user.Role);
        Assert.StartsWith("=== THE NODE NOW", ((TextPart)user.Parts[0]).Text, StringComparison.Ordinal);
        Assert.Contains("\"seq\":412", ((TextPart)user.Parts[0]).Text, StringComparison.Ordinal);
        Assert.Equal("hello", ((TextPart)user.Parts[1]).Text);
        Assert.DoesNotContain("\"seq\":412", request.System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheModelCanPlanARecipeThroughItsTool()
    {
        var provider = new ScriptedProvider()
            .Then(ScriptedProvider.Call(ScriptedProvider.Tool("c1", AssistantTools.PlanRecipe, """{"recipe":"fill_tagged","tag":"windows","generator":"Snow"}""")))
            .Then(ScriptedProvider.Say("Here is the plan."));
        var node = new FakeEndpoint();
        var session = new AssistantSession(provider, node, new ProposalQueue(node), AssistantSettings.Default with { Enabled = true });

        await session.AskAsync("snow on all the windows");

        var result = provider.Requests[1].Messages[^1].Parts.OfType<ToolResultPart>().Single();
        Assert.False(result.IsError);
        Assert.Contains($"\"SURFACE SHOW {SampleShow.WindowRight} ON\"", result.Content, StringComparison.Ordinal);
        Assert.Empty(node.Handled);
    }
}
