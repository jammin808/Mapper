using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>The packages' routes: an engine builds with every hook in place; a row is routed once, by its own name; a round-2 row
/// no package routed answers "not available on this node yet"; a routed one reaches its handler with its words; pairing, the
/// node-to-node rule and the journal follow the router's rules for a package's verb too. These run at every merge (PLAN §6.1), so
/// they hold whatever the merged packages route: a test that routes a row of its own takes one no package routes yet.</summary>
public sealed class RoutesTests
{
    private static readonly SessionInfo Node = EngineCore.SystemSession;
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private static async Task<CommandReply> Run(EngineCore engine, string line, SessionInfo session)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await engine.HandleAsync(parsed, session, CancellationToken.None);
    }

    /// <summary>A round-2 row no merged package routes on this engine, for a test that routes it itself. Once every row is routed the
    /// integration pass replaces the tests that ask for one.</summary>
    internal static VerbSpec Unrouted(EngineCore engine, Func<VerbSpec, bool> also) =>
        VerbCatalog.All.FirstOrDefault(v => v.Round == 2 && !v.Internal && !engine.Routes.IsRouted(v.Name) && also(v))
        ?? throw new InvalidOperationException("every such round-2 row is routed — the integration pass replaces this test");

    [Fact]
    public async Task AnEngineBuildsAndARowIsAvailableExactlyWhenItIsTodaysOrRouted()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        foreach (var v in VerbCatalog.All)
        {
            Assert.False(engine.Routes.IsRouted(v.Name) && v.Round == 1, $"{v.Name} is a first-round row, answered by the router itself");
            Assert.Equal(v.Round == 1 || engine.Routes.IsRouted(v.Name), engine.IsAvailable(v));
        }
        Assert.All(engine.Routes.Topics, t => Assert.DoesNotContain(t.ToUpperInvariant(), new[] { "STATE", "CONTEXT", "PROJECT", "OFF" }));
        var sections = engine.Routes.IndexSections.Select(s => s.Section).ToList();
        Assert.Equal(sections.Count, sections.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task EveryRoundTwoRowAnswersNotAvailableUntilItsPackageRoutesIt()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var revision = engine.Store.Current.Revision;
        foreach (var v in VerbCatalog.All.Where(v => v.Round == 2 && !engine.Routes.IsRouted(v.Name)))
        {
            var reply = await Run(engine, v.Name, Node);
            Assert.False(reply.Ok, v.Name);
            Assert.Equal($"{v.Name} is not available on this node yet", reply.Payload);
        }
        Assert.Equal(revision, engine.Store.Current.Revision);
    }

    [Fact]
    public void ARowIsRoutedOnceAndOnlyByItsOwnName()
    {
        var routes = new VerbRoutes();
        var spec = VerbCatalog.All.First(v => v.Round == 2 && v.Aliases.Count > 0);
        routes.Lane(spec.Name, _ => CommandReply.Success());
        Assert.True(routes.IsRouted(spec.Name));
        Assert.True(routes.IsAvailable(spec));
        Assert.Throws<InvalidOperationException>(() => routes.Lane(spec.Name, _ => CommandReply.Success()));
        Assert.Throws<InvalidOperationException>(() => routes.OffLane(spec.Name, _ => Task.FromResult(CommandReply.Success())));
        Assert.Throws<ArgumentException>(() => routes.Lane(spec.Aliases[0], _ => CommandReply.Success()));
        Assert.Throws<ArgumentException>(() => routes.Lane("LAUNCH ROCKETS", _ => CommandReply.Success()));
        Assert.Throws<ArgumentException>(() => routes.Topic("state", (_, _, _) => PushOpen.Refuse("no")));
    }

    [Fact]
    public async Task ARoutedLaneVerbGetsItsWordsAndIsJournalledAsTheRouterJournals()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var spec = Unrouted(engine, v => v.Mutates && !v.Ephemeral && !v.Gesture);
        VerbCall? seen = null;
        engine.Routes.Lane(spec.Name, call =>
        {
            seen = call;
            return CommandReply.Success(effect: "done for the test");
        });

        var reply = await Run(engine, spec.Name + " x1 y2", Desk);

        Assert.True(reply.Ok, reply.Payload);
        Assert.NotNull(seen);
        Assert.Equal(spec, seen.Spec);
        Assert.Equal(["x1", "y2"], seen.Args);
        Assert.Equal("desk", seen.Session.Transport);
        Assert.Contains(engine.Journal.Recent(10), r => r.Text == "done for the test");
    }

    [Fact]
    public async Task ARoutedOffLaneVerbRunsOffTheLaneAndEditsThroughIt()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var onLane = true;
        var spec = Unrouted(engine, v => v.Mutates && !v.Ephemeral && !v.Gesture);
        engine.Routes.OffLane(spec.Name, async call =>
        {
            // The lane is free while an off-lane verb waits: a lane task posted now completes before the verb answers.
            onLane = !await engine.Lane.RunAsync(() => true).WaitAsync(TimeSpan.FromSeconds(10));
            var r = await engine.EditAsync(call.Session, "named by an off-lane verb", p => p with { Name = "Checked" });
            return r.Changed ? CommandReply.Success(effect: r.Effect) : CommandReply.Fail(r.Effect);
        });

        var reply = await Run(engine, spec.Name + " x1", Desk);

        Assert.True(reply.Ok, reply.Payload);
        Assert.False(onLane);
        Assert.Equal("Checked", engine.Store.Project.Name);
        Assert.Contains(engine.Journal.Recent(10), r => r.Kind == "verb");
    }

    [Fact]
    public async Task APairedOnlyQueryNeedsPairingAndAnInternalVerbOnlyComesFromAPeer()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var record = VerbCatalog.All.First(v => v.PairedOnly && !v.Mutates);
        var mirror = VerbCatalog.All.First(v => v.Internal);
        var unpaired = new SessionInfo("u", "wire", "10.0.0.9", false, "Someone", false);

        var r1 = await Run(engine, record.Name, unpaired);
        Assert.False(r1.Ok);
        Assert.StartsWith("not paired", r1.Payload, StringComparison.Ordinal);

        var r2 = await Run(engine, mirror.Name, Desk);
        Assert.False(r2.Ok);
        Assert.Equal($"{mirror.Name}: this line is node-to-node only", r2.Payload);

        // From a fleet peer the line passes the door (its own package answers it, or says it is not available yet).
        var r3 = await Run(engine, mirror.Name, Node with { Transport = "fleet" });
        Assert.NotEqual($"{mirror.Name}: this line is node-to-node only", r3.Payload);
        Assert.False(r3.Payload?.StartsWith("not paired", StringComparison.Ordinal) ?? false);
    }

    [Fact]
    public async Task AnEphemeralVerbIsNeverJournalled()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var spec = Unrouted(engine, v => v.Ephemeral && v.Mutates);
        engine.Routes.Lane(spec.Name, _ => CommandReply.Success(effect: "a cursor moved"));
        var before = engine.Journal.Recent(100).Count;

        Assert.True((await Run(engine, spec.Name, Desk)).Ok);

        Assert.Equal(before, engine.Journal.Recent(100).Count);
        Assert.DoesNotContain(engine.Journal.Recent(100), r => r.Text == "a cursor moved");
    }

    [Fact]
    public async Task AnItemStartAndTheBlackoutRaiseShowEvents()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var events = new List<ShowEvent>();
        engine.EventRaised += e =>
        {
            lock (events)
                events.Add(e);
        };
        var list = (await Run(engine, "PLAYLIST NEW Show", Desk)).Payload!;
        var gen = (await Run(engine, "GENERATOR NEW testcard Card", Desk)).Payload!;
        var item = (await Run(engine, $"PLAYLIST ADD {list} generator:{gen} Opening", Desk)).Payload!;
        Assert.True((await Run(engine, $"PLAYLIST GO {list}", Desk)).Ok);
        Assert.True((await Run(engine, "BLACKOUT ON", Desk)).Ok);

        ShowEvent[] seen;
        lock (events)
            seen = [.. events];
        var start = Assert.Single(seen, e => e.Kind == "item.start");
        Assert.Contains($"\"item\":\"{item}\"", start.FieldsJson, StringComparison.Ordinal);
        Assert.Contains($"\"playlist\":\"{list}\"", start.FieldsJson, StringComparison.Ordinal);
        var blackout = Assert.Single(seen, e => e.Kind == "air.blackout");
        Assert.Equal("{\"on\":true}", blackout.FieldsJson);
        Assert.True(blackout.Seq > start.Seq);
        Assert.Equal(1_760_000_000_000, blackout.AtUnixMs);
    }

    [Fact]
    public async Task APackagesValidateCheckRefusesAnEditWithItsWords()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var output = engine.Store.Project.Outputs[0].Id;
        // With every stub empty, a valid edit passes untouched.
        Assert.Null(engine.ValidateModules(engine.Store.Project, engine.Store.Project.Outputs[0]));
        Assert.True((await Run(engine, $"SET {output} name \"Left\"", Desk)).Ok);

        // A package's check runs at the end of the generic one, inside the edit: its words refuse SET and ADD, and nothing moves.
        engine.ValidateForTests = (_, e) => e is PlayMap.Core.Model.Output { Name: "Forbidden" } ? "no output is called Forbidden on this stage" : null;
        var revision = engine.Store.Current.Revision;
        var set = await Run(engine, $"SET {output} name \"Forbidden\"", Desk);
        var add = await Run(engine, "ADD output {\"name\":\"Forbidden\"}", Desk);

        Assert.False(set.Ok);
        Assert.Equal("no output is called Forbidden on this stage", set.Payload);
        Assert.False(add.Ok);
        Assert.Equal("no output is called Forbidden on this stage", add.Payload);
        Assert.Equal(revision, engine.Store.Current.Revision);
        Assert.Equal("Left", engine.Store.Project.FindOutput(output)!.Name);
        Assert.True((await Run(engine, $"SET {output} name \"Right\"", Desk)).Ok);
    }

    [Fact]
    public async Task ARecoveryRecordCarriesEachPackagesFactsAndPutsThemBack()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var record = await engine.Lane.RunAsync(() => RecoveryRecord.Capture(engine));
        Assert.DoesNotContain("spine-test", record.Modules.Keys);

        var path = Path.Combine(Path.GetTempPath(), "playmap-recovery-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            (record with { Modules = record.Modules.Add("spine-test", "{\"armed\":false}") }).Save(path);
            var back = RecoveryRecord.Load(path);
            Assert.NotNull(back);
            Assert.Equal("{\"armed\":false}", back.Modules["spine-test"]);
            Assert.Equal("recovered " + engine.Store.Project.Name, await engine.RestoreAsync(back));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
