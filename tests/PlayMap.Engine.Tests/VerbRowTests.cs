using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Host;

namespace PlayMap.Engine.Tests;

/// <summary>A verb's own journal row and the rows of the work it began and left running: the verb's row comes first, in the journal
/// and in the context's recent rows, however soon that work ends (sent → accepted → applied → observed).</summary>
public sealed class VerbRowTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("test");

    private const string Refused = "Dead list's media list could not be read: Response status code does not indicate success: 503 (Refused).";

    [Fact]
    public async Task AFetchsRowComesBeforeEveryRowOfItsPullsWhenTheyAllEndBeforeTheFetchIsAnswered()
    {
        // The network edge ends the whole fetch inside its own call: each file refused, then the row that counts the rest, all
        // journalled before it answers. That is the soonest a pull can end (a peer whose list is refused at once), and the verb's
        // row still comes first: its pulls begin only once the dispatcher has written it.
        const int Files = 6;
        var pulls = new EndedPulls();
        var project = Project.CreateDefault("Fetch") with { Media = [.. Enumerable.Range(0, Files).Select(Item)] };
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(1_000_000_000)) with { NetworkControl = pulls }, project);
        pulls.Journal = engine.Journal;
        var before = engine.Journal.Recent(200).Count;

        var reply = await Run(engine, "MEDIA FETCH ALL");

        Assert.True(reply.Ok, reply.Payload);
        Assert.False(reply.Applied);
        Assert.Equal(Files, pulls.Asked);
        var rows = engine.Journal.Recent(200).Skip(before).ToList();
        var ended = string.Create(CultureInfo.InvariantCulture,
            $"the fetch of {Files} files from Dead list ended: 0 pulled and verified, {Files} could not be fetched; {Files - FetchJournal.Most} of its rows are in the node's log only");
        List<(string Kind, string Text, bool Ok)> work =
        [
            .. Enumerable.Range(0, FetchJournal.Most).Select(k => ("refused", $"clip{k} could not be fetched: {Refused}", false)),
            ("refused", ended, false),
            ("media", $"fetching {Files} files from Dead list", true),
        ];
        Assert.Equal([("verb", $"fetching {Files} files", true), .. work], rows.Select(r => (r.Kind, r.Text, r.Ok)));
        Assert.Equal(("Desk", "MEDIA FETCH ALL"), (rows[0].Who, rows[0].Line));
        // The context's recent rows, the window MCP's get_context, Patterns and the assistant read, say them in the same order.
        var context = ContextBuilder.Build(engine).Recent.Select(r => r.Text).ToList();
        Assert.Equal([$"fetching {Files} files", .. work.Select(w => w.Ok ? w.Text : "refused: " + w.Text)], context.TakeLast(work.Count + 1));
    }

    [Fact]
    public async Task ALinksRowComesBeforeTheRowThatSaysHowTheChangeEndedWhenItEndsAtOnce()
    {
        // The edge answers the link change the moment it is asked (a change that ends at once): its row still follows the verb's.
        var links = new LinkSpy();
        await using var engine = new EngineCore(EngineServices.ForTests() with { NetworkControl = links });
        var before = engine.Journal.Recent(200).Count;

        var reply = await Run(engine, "LINK PATTERNS 10.0.0.5 s3cret-token");

        Assert.True(reply.Ok, reply.Payload);
        Assert.Equal(("10.0.0.5", "s3cret-token"), Assert.Single(links.Patterns));
        var rows = engine.Journal.Recent(200).Skip(before).ToList();
        Assert.Equal([("verb", "LINK PATTERNS started", true), ("network", "following Patterns at 10.0.0.5", true)], rows.Select(r => (r.Kind, r.Text, r.Ok)));
        Assert.DoesNotContain("s3cret-token", rows[0].Line ?? "", StringComparison.Ordinal);
        var context = ContextBuilder.Build(engine).Recent.Select(r => r.Text).ToList();
        Assert.Equal(["LINK PATTERNS started", "following Patterns at 10.0.0.5"], context.TakeLast(2));
    }

    [Fact]
    public async Task APreflightsRunIsJournalledAfterTheRowOfTheVerbThatStartedIt()
    {
        // Answered off the lane with the report's id at once: the run's row, written when it ends, follows the verb's.
        await using var engine = new EngineCore(EngineServices.ForTests());
        var done = new TaskCompletionSource<PreflightReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PreflightDone += r => done.TrySetResult(r);
        var before = engine.Journal.Recent(200).Count;

        var reply = await Run(engine, "PREFLIGHT");

        Assert.True(reply.Ok, reply.Payload);
        Assert.False(reply.Applied);
        var report = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var rows = engine.Journal.Recent(200).Skip(before).ToList();
        var verb = rows.FindIndex(r => r.Kind == "verb" && r.Text == $"preflight {report.Id} started by Desk");
        var run = rows.FindIndex(r => r.Kind == "preflight" && r.Text.StartsWith("preflight QUICK by Desk:", StringComparison.Ordinal));
        Assert.True(verb >= 0 && run >= 0 && verb < run, string.Join(" | ", rows.Select(r => $"{r.Kind}: {r.Text}")));
    }

    [Fact]
    public void WorkHandedToALineIsHeldUntilItsDispatcherTakesItAndBegunAtOnceAfter()
    {
        var call = new VerbCall(VerbCatalog.Find("PREFLIGHT")!, [], Line("PREFLIGHT"), Desk, CancellationToken.None);
        var begun = new List<string>();
        call.AfterRow(() => begun.Add("first"));
        call.AfterRow(() => begun.Add("second"));
        Assert.Empty(begun);

        var held = call.TakeAfterRow();

        Assert.NotNull(held);
        Assert.Empty(begun);
        held();
        Assert.Equal(["first", "second"], begun);
        // Taken once: what is handed over after begins at once, and nothing is held for a second take.
        call.AfterRow(() => begun.Add("late"));
        Assert.Equal(["first", "second", "late"], begun);
        Assert.Null(call.TakeAfterRow());
    }

    private static MediaItem Item(int k) =>
        new(string.Create(CultureInfo.InvariantCulture, $"md{k}"), string.Create(CultureInfo.InvariantCulture, $"clip{k}"), MediaKind.Video,
            string.Create(CultureInfo.InvariantCulture, $"/elsewhere/clip{k}.bin"), "sha256:" + Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(k))),
            1000, MediaInfo.Unknown, []);

    private static WireLine Line(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var line, out var error), error?.Reason);
        return line;
    }

    private static async Task<CommandReply> Run(EngineCore engine, string text) => await engine.HandleAsync(Line(text), Desk, CancellationToken.None);

    /// <summary>A network edge whose fetch has ended by the time it answers: every file refused through the fetch's own bounded rows.</summary>
    private sealed class EndedPulls : INetworkControl
    {
        public Journal? Journal { get; set; }

        public int Asked { get; private set; }

        public Task<string> FetchAsync(IReadOnlyList<MediaItem> items, string? fromNode, CancellationToken ct)
        {
            Asked = items.Count;
            var what = string.Create(CultureInfo.InvariantCulture, $"{items.Count} files from Dead list");
            var rows = new FetchJournal(Journal!, NullLogger.Instance, "the fetch of " + what);
            foreach (var item in items)
                rows.Failed($"{item.Name} could not be fetched: {Refused}");
            rows.End();
            return Task.FromResult("fetching " + what);
        }

        public Task<string> LinkPatternsAsync(string? host, string? token, CancellationToken ct) => Task.FromResult("");

        public Task<string> LinkStagewatchAsync(string? host, string? key, CancellationToken ct) => Task.FromResult("");

        public Task<string> LeadAsync(string group, CancellationToken ct) => Task.FromResult("");

        public Task<string> FollowAsync(string group, string nodeId, CancellationToken ct) => Task.FromResult("");
    }
}
