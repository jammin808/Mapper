using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Model;

/// <summary>The store's undo steps and UndoTo: each step is numbered, newest first, with who made it and when; UndoTo(n) takes step n and
/// every newer one back as one action (one revision, one change) to the root before step n, each step going to the redo list as UNDO
/// would put it there; UndoTo of the newest is UNDO; an empty list, an unknown step and a guarded run are refused with nothing changed.</summary>
public sealed class UndoToStoreTests
{
    private sealed class Clock
    {
        public long Mono;
        public long Wall = 1_760_000_000_000;
        public string Who = "Desk";
    }

    private static (ProjectStore Store, Clock Clock) Store()
    {
        var clock = new Clock();
        var store = new ProjectStore(Project.CreateDefault("Show"), () => clock.Mono) { Who = () => clock.Who, WallMs = () => clock.Wall };
        return (store, clock);
    }

    /// <summary>Five renames, a second apart, the third and fourth by another client; the roots after each.</summary>
    private static List<Project> FiveEdits(ProjectStore store, Clock clock)
    {
        var roots = new List<Project> { store.Project };
        for (var i = 1; i <= 5; i++)
        {
            clock.Who = i is 3 or 4 ? "Companion" : "Desk";
            clock.Mono += 1_000;
            clock.Wall += 1_000;
            Assert.True(store.Apply($"rename {i}", p => p with { Name = $"Show {i}" }).Changed);
            roots.Add(store.Project);
        }
        return roots;
    }

    [Fact]
    public void UndoToThreeOnFiveEditsIsTheRootAfterTheSecondInOneStep()
    {
        var (store, clock) = Store();
        var roots = FiveEdits(store, clock);
        var revision = store.Current.Revision;
        var changes = 0;
        store.Changed += _ => changes++;

        var r = store.UndoTo(3);

        Assert.True(r.Changed);
        Assert.Same(roots[2], store.Project);
        Assert.Equal(revision + 1, store.Current.Revision);
        Assert.Equal(revision + 1, r.Revision);
        Assert.Equal(1, changes);
        Assert.Equal("undo rename 5; rename 4; rename 3", r.Effect);
        Assert.Equal(new long[] { 2, 1 }, store.Steps.Select(s => s.Number));
        // Each step taken back is on the redo list as UNDO puts it: REDO brings them back oldest first.
        Assert.Equal("rename 3", store.RedoLabel);
        for (var i = 3; i <= 5; i++)
        {
            Assert.True(store.Redo().Changed);
            Assert.Same(roots[i], store.Project);
        }
        Assert.False(store.CanRedo);
        Assert.Equal(new long[] { 5, 4, 3, 2, 1 }, store.Steps.Select(s => s.Number));
    }

    [Fact]
    public void TheStepsAreNewestFirstWithWhoAndWhen()
    {
        var (store, clock) = Store();
        FiveEdits(store, clock);

        var steps = store.Steps;

        Assert.Equal(new long[] { 5, 4, 3, 2, 1 }, steps.Select(s => s.Number));
        Assert.Equal(new[] { "rename 5", "rename 4", "rename 3", "rename 2", "rename 1" }, steps.Select(s => s.Label));
        Assert.Equal(new[] { "Desk", "Companion", "Companion", "Desk", "Desk" }, steps.Select(s => s.Who));
        Assert.Equal(new[] { 1_760_000_005_000L, 1_760_000_004_000, 1_760_000_003_000, 1_760_000_002_000, 1_760_000_001_000 },
            steps.Select(s => s.AtUnixMs));

        // A coalesced burst is one step, recorded as it began; a new step after an undo takes the next number, never an old one's.
        clock.Mono += 100;
        store.Apply("drag", p => p with { Name = "a" }, coalesceKey: "drag");
        clock.Mono += 100;
        store.Apply("drag", p => p with { Name = "b" }, coalesceKey: "drag");
        Assert.Equal(new long[] { 6, 5 }, store.Steps.Take(2).Select(s => s.Number));
        Assert.True(store.Undo().Changed);
        clock.Mono += 10_000;
        store.Apply("rename again", p => p with { Name = "c" });
        Assert.Equal(new long[] { 7, 5 }, store.Steps.Take(2).Select(s => s.Number));
        // A store with no clock or author given records "" and 0.
        var bare = new ProjectStore(Project.CreateDefault());
        bare.Apply("rename", p => p with { Name = "x" });
        Assert.Equal(new UndoStep(1, "rename", "", 0), Assert.Single(bare.Steps));
    }

    [Fact]
    public void UndoToTheNewestIsOneUndo()
    {
        var (a, ca) = Store();
        var (b, cb) = Store();
        FiveEdits(a, ca);
        FiveEdits(b, cb);

        var to = a.UndoTo(5);
        var undo = b.Undo();

        Assert.Equal((undo.Changed, undo.Effect, undo.Revision), (to.Changed, to.Effect, to.Revision));
        Assert.Equal(b.Project.Name, a.Project.Name);
        Assert.Equal(b.Steps.AsEnumerable(), a.Steps);
        Assert.Equal(b.RedoLabel, a.RedoLabel);
    }

    [Fact]
    public void AnEmptyListAnUnknownStepAndAGuardedRunAreRefusedWithNothingChanged()
    {
        var (store, clock) = Store();
        var empty = store.UndoTo(1);
        Assert.False(empty.Changed);
        Assert.Equal("nothing to undo", empty.Effect);

        var roots = FiveEdits(store, clock);
        var revision = store.Current.Revision;
        var unknown = store.UndoTo(9);
        Assert.False(unknown.Changed);
        Assert.Equal("no undo step 9 — they are 1 to 5 (UNDO LIST lists them)", unknown.Effect);
        Assert.Equal(revision, store.Current.Revision);

        store.Guard = (_, after) => after.Name == "Show 1" ? "the name is locked" : null;
        var guarded = store.UndoTo(2);
        Assert.False(guarded.Changed);
        Assert.Equal("the name is locked", guarded.Effect);
        Assert.Same(roots[5], store.Project);
        Assert.Equal(revision, store.Current.Revision);
        Assert.Equal(5, store.Steps.Length);
        Assert.False(store.CanRedo);

        store.Guard = null;
        Assert.True(store.UndoTo(2).Changed);
        var one = store.UndoTo(5);
        Assert.Equal("no undo step 5 — the only one is 1 (UNDO LIST lists them)", one.Effect);
    }
}
