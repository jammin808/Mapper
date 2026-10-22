using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Assistant;
using PlayMap.Core.Control;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Core.Preflight;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;
using PlayMap.Engine.Fx;
using PlayMap.Engine.Host;
using PlayMap.Engine.Tests.Control;
using PlayMap.Engine.Tests.Facade;
using PlayMap.Engine.Tests.Fleet;
using PlayMap.Engine.Tests.Host;
using PlayMap.Engine.Tests.Optimise;
using PlayMap.Engine.Tests.Rendering;
using PlayMap.Engine.Tests.Show;

namespace PlayMap.Engine.Tests;

/// <summary>A verb's own journal row and the rows of the work it began and left running: the verb's row comes first, in the journal
/// and in the context's recent rows, however soon that work ends (sent → accepted → applied → observed). Each test makes that work
/// end as early as it can: inside the edge's own call, or with the verb's row held back as late as a busy machine can make it
/// (<see cref="EngineCore.RowHeldForTests"/>), until the work's first row is in unless the line holds its work behind that row.</summary>
public sealed class VerbRowTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("test");

    private const string Refused = "Dead list's media list could not be read: Response status code does not indicate success: 503 (Refused).";

    [Fact]
    public async Task AFetchsRowComesBeforeEveryRowOfItsPullsWhenTheyAllEndBeforeTheFetchIsAnswered()
    {
        // The network edge ends the whole fetch inside its own call: what it queued, each file refused, then the row that counts the
        // rest, all journalled before it answers. That is the soonest a pull can end (a peer whose list is refused at once), and the
        // verb's row still comes first: its pulls begin only once the dispatcher has written it.
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
            ("media", $"fetching {Files} files from Dead list", true),
            .. Enumerable.Range(0, FetchJournal.Most).Select(k => ("refused", $"clip{k} could not be fetched: {Refused}", false)),
            ("refused", ended, false),
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
    public async Task AnImportsOwnRowsComeAfterTheRowOfTheVerbThatBeganIt()
    {
        // The media edge says what it found the moment it is asked (a file that will not play here, as FFmpeg answers a file it cannot
        // open): that row follows the verb's, and the import's end lands behind both.
        var file = Path.Combine(Path.GetTempPath(), "playmap-verbrow-" + Guid.NewGuid().ToString("N")[..10] + ".mp4");
        await File.WriteAllBytesAsync(file, new byte[4096]);
        try
        {
            var media = new SaysAtOnce();
            await using var engine = new EngineCore(EngineServices.ForTests() with { Media = media });
            media.Journal = engine.Journal;
            var before = engine.Journal.Recent(200).Count;

            var reply = await Run(engine, $"MEDIA IMPORT \"{file}\" Garbage");

            Assert.True(reply.Ok, reply.Payload);
            Assert.False(reply.Applied);
            Assert.Equal(1, media.Asked);
            var rows = engine.Journal.Recent(200).Skip(before).ToList();
            Assert.Equal([("verb", "importing Garbage", true), ("media", SaysAtOnce.Words, false)], rows.Select(r => (r.Kind, r.Text, r.Ok)));
            var context = ContextBuilder.Build(engine).Recent.Select(r => r.Text).ToList();
            Assert.Equal(["importing Garbage", "refused: " + SaysAtOnce.Words], context.TakeLast(2));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ASurveyThatCannotBeReadIsRefusedAfterTheRowOfTheImportThatBeganIt()
    {
        // A file that is no PLY ends its import at the first read, on the worker's own thread; the verb's row, held back as late as a
        // busy machine makes it, still comes first.
        await using var rig = new SiteRig();
        var file = rig.File("broken.ply");
        await File.WriteAllTextAsync(file, "this is not a ply file");
        var refusal = HoldRowUntil(rig.Engine, "SURVEY IMPORT", r => r.Kind == "refused" && r.Text.StartsWith("broken was not imported", StringComparison.Ordinal));
        var before = rig.Engine.Journal.Recent(200).Count;

        var accepted = SiteRig.Json(await rig.Ok($"SURVEY IMPORT {file} CRS LOCAL"));
        await rig.Engine.WhenSurveyed.WaitAsync(TimeSpan.FromMinutes(1));
        await refusal.WaitAsync(TimeSpan.FromSeconds(30));

        var id = accepted.GetProperty("survey").GetString();
        Assert.Equal("failed", rig.Engine.SurveyJob!.State);
        var rows = rig.Engine.Journal.Recent(200).Skip(before).Where(r => r.Text.Contains("broken", StringComparison.Ordinal)).ToList();
        Assert.Equal([("verb", $"importing broken (PLY) as {id}"), ("refused", "broken was not imported")],
            rows.Select(r => (r.Kind, r.Kind == "refused" ? r.Text[..r.Text.IndexOf(':', StringComparison.Ordinal)] : r.Text)));
        var context = ContextBuilder.Build(rig.Engine).Recent.Select(r => r.Text).Where(t => t.Contains("broken", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, context.Count);
        Assert.StartsWith("importing broken (PLY)", context[0], StringComparison.Ordinal);
        Assert.StartsWith("refused: broken was not imported", context[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task APreflightsRunIsJournalledAfterTheRowOfTheVerbThatStartedIt()
    {
        // Answered off the lane with the report's id at once: the run's row, written when it ends, follows the verb's, however late the
        // verb's row is written.
        await using var engine = new EngineCore(EngineServices.ForTests());
        var done = new TaskCompletionSource<PreflightReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PreflightDone += r => done.TrySetResult(r);
        var ran = HoldRowUntil(engine, "PREFLIGHT", r => r.Kind == "preflight");
        var before = engine.Journal.Recent(200).Count;

        var reply = await Run(engine, "PREFLIGHT");

        Assert.True(reply.Ok, reply.Payload);
        Assert.False(reply.Applied);
        var report = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await ran.WaitAsync(TimeSpan.FromSeconds(30));
        var rows = engine.Journal.Recent(200).Skip(before).Where(r => r.Kind is "verb" or "preflight").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("verb", $"preflight {report.Id} started by Desk"), (rows[0].Kind, rows[0].Text));
        Assert.Equal("preflight", rows[1].Kind);
        Assert.StartsWith("preflight QUICK by Desk:", rows[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlendSolveIsJournalledAfterTheRowOfTheLineThatAskedForIt()
    {
        // The solve runs on a worker and journals how it ended once the lane applies it; the verb's row, held back, comes first.
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), BlendSolveTests.TwoProjectorsOnAWall());
        var solved = HoldRowUntil(engine, "BLEND SOLVE", r => r.Kind == "blend");
        var before = engine.Journal.Recent(200).Count;

        var reply = await Run(engine, "BLEND SOLVE");

        Assert.True(reply.Ok, reply.Payload);
        Assert.Equal("done", (await engine.WhenSolved.WaitAsync(TimeSpan.FromSeconds(30)))!.State);
        await solved.WaitAsync(TimeSpan.FromSeconds(30));
        var rows = engine.Journal.Recent(200).Skip(before).Where(r => r.Kind is "verb" or "blend").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("verb", "solving the blend for 2 outputs"), (rows[0].Kind, rows[0].Text));
        Assert.Equal("blend", rows[1].Kind);
        Assert.StartsWith("blend solved for 2 outputs", rows[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHookLeftRunningPastItsDeadlineFaultsAfterTheRowOfTheArmThatLeftIt()
    {
        // A hook that ignores its token runs on past its deadline; it faults just as the verb's row is about to be written. Its fault row
        // follows that row.
        var time = new ManualTime();
        var late = new TaskCompletionSource<string>();
        var stubborn = new LateHook("stubborn", TimeSpan.FromSeconds(1), late.Task);
        await using var engine = new EngineCore(EngineServices.ForTests() with { ArmHooks = [stubborn], ArmTimers = time });
        var fault = HoldRowUntil(engine, "SHOW ARM", r => r.Kind == "fault" && r.Text.Contains("stubborn", StringComparison.Ordinal));
        var hold = engine.RowHeldForTests!;
        engine.RowHeldForTests = (line, held) =>
        {
            if (line.Raw == "SHOW ARM")
                late.TrySetException(new InvalidOperationException("the card went away"));
            return hold(line, held);
        };
        var before = engine.Journal.Recent(200).Count;

        var arming = Run(engine, "SHOW ARM");
        await stubborn.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(1));
        var reply = await arming.WaitAsync(TimeSpan.FromSeconds(30));
        await fault.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(reply.Ok, reply.Payload);
        Assert.True(engine.ShowNow.Armed);
        var rows = engine.Journal.Recent(200).Skip(before).Where(r => r.Kind is "verb" or "fault").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("verb", "SHOW ARM"), (rows[0].Kind, rows[0].Line));
        Assert.Contains("stubborn: missed its 1000 ms deadline", rows[0].Text, StringComparison.Ordinal);
        Assert.Equal("fault", rows[1].Kind);
        Assert.StartsWith("the arm hook stubborn failed after its deadline: ", rows[1].Text, StringComparison.Ordinal);
        Assert.Contains("the card went away", rows[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSyncLeadersArmRunsItsHooksBehindItsRow()
    {
        // The leader's line is answered once the mode has changed and its hooks run behind the answer: a hook that answers at once is
        // journalled after the line's row, however late that row is written.
        var plans = new LateHook("plans", TimeSpan.FromSeconds(5), Task.FromResult("12 plans compiled"));
        await using var engine = new EngineCore(EngineServices.ForTests() with { ArmHooks = [plans], ArmTimers = new ManualTime() });
        var hooked = HoldRowUntil(engine, "SHOW ARM", r => r.Kind == "show" && r.Text.Contains("arm hook plans", StringComparison.Ordinal));
        var leader = new SessionInfo("sync:nd_lead", EngineCore.LeaderTransport, "10.0.0.1", false, "FOH-A (sync leader)", true);
        var before = engine.Journal.Recent(200).Count;

        var reply = await engine.HandleAsync(Line("SHOW ARM"), leader, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await hooked.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(reply.Ok, reply.Payload);
        var rows = engine.Journal.Recent(200).Skip(before).Where(r => r.Kind is "verb" or "show").ToList();
        Assert.Equal([("verb", "SHOW ARM"), ("show", null)], rows.Select(r => (r.Kind, r.Line)));
        Assert.Equal("SHOW ARM's arm hook plans: 12 plans compiled", rows[1].Text);
    }

    [Fact]
    public async Task APanicsOwnLinesAreJournalledAfterThePanicsRow()
    {
        // FX-OFF on the panic's output: the panic sends FX BYPASS ALL ON as a line of the node's own, with its own row, behind the
        // panic's row however late that row is written.
        var p = ShowRig.OneScreen();
        var project = p with { Show = p.Show with { Panic = [new OutputPanicPlan(p.Outputs[0].Id, PanicOutcome.FxOff, 0, null)] } };
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), project);
        Assert.True((await Run(engine, $"FX ADD \"{p.Outputs[0].Name}\" fx.invert")).Ok);
        var bypassed = HoldRowUntil(engine, "PANIC", r => r.Line == "FX BYPASS ALL ON");
        var before = engine.Journal.Recent(200).Count;

        var reply = await Run(engine, "PANIC");
        await bypassed.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(reply.Ok, reply.Payload);
        var rows = engine.Journal.Recent(200).Skip(before).Where(r => r.Line is "PANIC" or "FX BYPASS ALL ON").ToList();
        Assert.Equal([("PANIC", "Desk"), ("FX BYPASS ALL ON", EngineCore.SystemSession.ClientName)], rows.Select(r => (r.Line, r.Who)));
        Assert.All(rows, r => Assert.True(r.Ok, r.Text));
        Assert.All(FxBoard.Owners(engine.Store.Project).SelectMany(o => o.Chain.Slots), s => Assert.True(s.Bypass));
    }

    [Fact]
    public async Task AProposalADeskDoesNotTakeIsSaidAfterTheRefusalThatNamesIt()
    {
        // An assistant's armed line waits on this node's list for a person, and the approval desk it is offered to refuses it at once:
        // that row names the proposal, and follows the refusal that gave the proposal its id.
        var rig = new OpsRig(project: ShowRig.OneScreen());
        try
        {
            rig.Assistant.Queue.Sink = new RefusingDesk();
            await rig.Ok("SHOW ARM");
            var before = rig.Engine.Journal.Recent(200).Count;

            var refused = await rig.Run("SET op_1 name \"Stage\"", OpsRig.Mcp);

            Assert.False(refused.Ok);
            var proposal = Assert.Single(rig.Assistant.Queue.Pending);
            Assert.EndsWith("queued for a person as proposal " + proposal.Id, refused.Payload, StringComparison.Ordinal);
            var rows = rig.Engine.Journal.Recent(200).Skip(before).Where(r => r.Text.Contains(proposal.Id, StringComparison.Ordinal)).ToList();
            Assert.Equal(["refused", "assistant"], rows.Select(r => r.Kind));
            Assert.Equal(refused.Payload, rows[0].Text);
            Assert.Equal($"assistant: the approval desk {RefusingDesk.DeskName} did not take {proposal.Id} (the desk is not answering); it stays on this node's list",
                rows[1].Text);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task AScansEndIsJournalledAfterTheRowOfTheStartThatBeganIt()
    {
        // A scan backend whose scan has ended by the time it answers: the row that says the scan finished follows the verb's, however
        // late the verb's row is written.
        var scans = new EndedScan();
        var project = BlendSolveTests.TwoProjectorsOnAWall();
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)) with { Scan = scans }, project);
        var finished = HoldRowUntil(engine, "SCAN START", r => r.Kind == "scan");
        var before = engine.Journal.Recent(200).Count;

        var reply = await Run(engine, "SCAN START sim MODE FAST");
        await finished.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(reply.Ok, reply.Payload);
        Assert.Equal(1, scans.Asked);
        var rows = engine.Journal.Recent(200).Skip(before).Where(r => r.Kind is "verb" or "scan").ToList();
        Assert.Equal([
            ("verb", string.Create(CultureInfo.InvariantCulture, $"scanning {project.Stage.Projectors.Length} projectors through the simulated camera (fast)")),
            ("scan", $"scan {EndedScan.Id} finished"),
        ], rows.Select(r => (r.Kind, r.Text)));
        Assert.Equal("SCAN START sim MODE FAST", rows[0].Line);
    }

    [FfmpegFact]
    public async Task AnOptimiseJobThatCannotRunIsJournalledAfterTheRowOfTheVerbThatQueuedIt()
    {
        // The node's margin of free disk is more than any disk holds: the job is planned and queued, and its first step ends it failed
        // at once, before any worker starts. That row follows the verb's, however late the verb's row is written.
        await using var rig = OptimiseRig.Start(settings: s => s with { MinFreeGb = 1_000_000 });
        var id = await rig.ImportAsync(OptimiseClips.Mpeg4WithAudio(rig.Dir.FullName, 320, 180, 2), "Clip");
        var failed = HoldRowUntil(rig.Engine, "MEDIA OPTIMISE", r => r.Kind == "optimise" && r.Text.Contains(" failed: needs ", StringComparison.Ordinal));
        var before = rig.Engine.Journal.Recent(200).Count;

        using var reply = System.Text.Json.JsonDocument.Parse(await rig.Ok($"MEDIA OPTIMISE {id} TO hap QUALITY FAST"));
        await failed.WaitAsync(TimeSpan.FromSeconds(30));

        var job = reply.RootElement.GetProperty("jobs")[0].GetProperty("id").GetString()!;
        Assert.Equal("failed", rig.Job(job).State);
        Assert.Null(rig.Service.View.Pid);
        var rows = rig.Engine.Journal.Recent(200).Skip(before)
            .Where(r => (r.Kind == "verb" && r.Line?.StartsWith("MEDIA OPTIMISE", StringComparison.Ordinal) == true) || r.Text.StartsWith(job + " ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal([("verb", "optimising 1 clip in the background"), ("optimise", job + " failed")],
            rows.Select(r => (r.Kind, r.Kind == "optimise" ? r.Text[..r.Text.IndexOf(':', StringComparison.Ordinal)] : r.Text)));
        Assert.Contains(" GB margin; ", rows[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALearnsRowIsJournalledAfterTheRowOfTheBindLearnThatArmedIt()
    {
        // The control moves as the verb's row is about to be written, once the learn is armed: the binding is made then, and the row
        // that says what was learnt follows the verb's.
        await using var rig = await ControlRig.StartAsync(BindingEngineTests.Show());
        var cc = InputAddress.Midi(MidiFamily.Cc, 1, 7);
        var learnt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Engine.Journal.Written += r =>
        {
            if (r.Text.StartsWith("learnt: ", StringComparison.Ordinal))
                learnt.TrySetResult();
        };
        rig.Engine.RowHeldForTests = async (line, _) =>
        {
            if (!line.Raw.StartsWith("BIND LEARN", StringComparison.Ordinal))
                return;
            // A first reading sets where the fader starts; the second move binds it.
            await rig.Engine.ControlInput.InjectAsync(cc, 0.2, CancellationToken.None);
            await rig.Engine.ControlInput.InjectAsync(cc, 0.5, CancellationToken.None);
            Assert.Single(rig.Engine.Store.Project.Control.Bindings);
        };
        var before = rig.Engine.Journal.Recent(200).Count;

        var reply = await rig.Ok("BIND LEARN FROM MIDI SET sf_lobby opacity *");
        await learnt.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(reply.Applied);
        var made = Assert.Single(rig.Engine.Store.Project.Control.Bindings);
        Assert.Equal(("MIDI CC 1 7", "SET sf_lobby opacity *"), (made.Input, made.Target));
        var rows = rig.Engine.Journal.Recent(200).Skip(before)
            .Where(r => r.Line == "BIND LEARN FROM MIDI SET sf_lobby opacity *" || r.Text.StartsWith("learnt: ", StringComparison.Ordinal)).ToList();
        Assert.Equal([("verb", "Desk"), ("control", "control")], rows.Select(r => (r.Kind, r.Who)));
        Assert.Equal("learnt: MIDI CC 1 7 → Lobby opacity", rows[1].Text);
    }

    [Fact]
    public void WorkHandedToALineIsHeldUntilItsDispatcherTakesItAndBegunAtOnceAfter()
    {
        var call = new VerbCall(VerbCatalog.Find("PREFLIGHT")!, [], Line("PREFLIGHT"), Desk, CancellationToken.None);
        var begun = new List<string>();
        call.AfterRow(() => begun.Add("first"));
        call.AfterRow(() => begun.Add("second"));
        Assert.Empty(begun);
        Assert.True(call.HoldsAfterRow);

        var held = call.TakeAfterRow();

        Assert.NotNull(held);
        Assert.False(call.HoldsAfterRow);
        Assert.Empty(begun);
        held();
        Assert.Equal(["first", "second"], begun);
        // Taken once: what is handed over after begins at once, and nothing is held for a second take.
        call.AfterRow(() => begun.Add("late"));
        Assert.Equal(["first", "second", "late"], begun);
        Assert.False(call.HoldsAfterRow);
        Assert.Null(call.TakeAfterRow());
    }

    /// <summary>Holds the row of each <paramref name="verb"/> line, once its handler has answered, until a row <paramref name="first"/>
    /// matches is journalled, unless the line holds its work behind its row: work its handler began itself runs ahead of the row then,
    /// as on a machine whose thread pool is busy. Completes when such a row is journalled.</summary>
    private static Task HoldRowUntil(EngineCore engine, string verb, Func<JournalRow, bool> first)
    {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.Written += r =>
        {
            if (first(r))
                written.TrySetResult();
        };
        engine.RowHeldForTests = (line, held) => !held && line.Raw.StartsWith(verb, StringComparison.Ordinal)
            ? written.Task.WaitAsync(TimeSpan.FromSeconds(30))
            : Task.CompletedTask;
        return written.Task;
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

    /// <summary>A network edge whose fetch has ended by the time it answers: what it queued, then every file refused through the fetch's
    /// own bounded rows.</summary>
    private sealed class EndedPulls : INetworkControl
    {
        public Journal? Journal { get; set; }

        public int Asked { get; private set; }

        public Task<string> FetchAsync(IReadOnlyList<MediaItem> items, string? fromNode, CancellationToken ct)
        {
            Asked = items.Count;
            var what = string.Create(CultureInfo.InvariantCulture, $"{items.Count} files from Dead list");
            Journal!.Add("media", "fetching " + what);
            var rows = new FetchJournal(Journal, NullLogger.Instance, "the fetch of " + what);
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

    /// <summary>A media edge that says what it found inside its own call, as the node's own says a file that will not play, and
    /// answers the import at once.</summary>
    private sealed class SaysAtOnce : IMediaBackend
    {
        public const string Words = "Garbage imported, but it will not play here: Invalid data found when processing input";

        public Journal? Journal { get; set; }

        public int Asked { get; private set; }

        public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct)
        {
            Asked++;
            Journal!.Add("media", Words, ok: false);
            return Task.FromResult(new MediaImport(MediaKind.Video, MediaInfo.Unknown, "", 4096, "Garbage"));
        }

        public IVideoSource? Open(MediaItem item) => null;

        public MediaReadiness Readiness(MediaItem item) => new(true, false, false, false, "not decodable");

        public string? PathForHash(string contentHash) => null;
    }

    /// <summary>An arm hook whose arming is the task it is handed (one that never heeds its token runs on past its deadline).</summary>
    private sealed class LateHook(string name, TimeSpan deadline, Task<string> arming) : IShowArmHook
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => name;

        public TimeSpan Deadline => deadline;

        public Task<string> ArmingAsync(CancellationToken ct)
        {
            Started.TrySetResult();
            return arming;
        }

        public Task<string> DisarmingAsync(CancellationToken ct) => Task.FromResult("");
    }

    /// <summary>A scan backend whose scan has ended by the time it answers.</summary>
    private sealed class EndedScan : IScanBackend
    {
        public const string Id = "sc_ended";

        public ScanProgress? Current { get; private set; }

        public int Asked { get; private set; }

        public Task<string> StartAsync(string cameraId, IReadOnlyList<string> projectorIds, string mode, CancellationToken ct)
        {
            Asked++;
            Current = new ScanProgress("done", null, 0, 0, 1, "scanned");
            return Task.FromResult(Id);
        }

        public bool Cancel() => false;
    }

    /// <summary>An approval desk that refuses every offer at once.</summary>
    private sealed class RefusingDesk : IProposalSink
    {
        public const string DeskName = "Stagewatch at 10.0.0.7";

        public string Name => DeskName;

        public Task<string?> OfferAsync(Proposal proposal, CancellationToken ct) => throw new InvalidOperationException("the desk is not answering");
    }
}
