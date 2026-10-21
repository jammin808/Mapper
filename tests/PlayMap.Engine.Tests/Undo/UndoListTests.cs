using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Show;
using PlayMap.Engine.Tests.Show;

namespace PlayMap.Engine.Tests.Undo;

/// <summary>The Oops history: UNDO LIST names the undo steps newest first in field words with who and when; UNDO TO takes a step and
/// every newer one back as one action (one revision, one journal row), refused with the store's words and nothing undone across a
/// lock, for an unknown step and on an empty list; armed it is the operator's as UNDO is; for the blind owner both are the draft's.</summary>
public sealed class UndoListTests
{
    private static readonly string[] Names = ["One", "Two", "Three", "Four", "Five"];

    private static Project WithWall() => ShowRig.OneScreen().WithStage(s => s with
    {
        Meshes = [new MeshAsset("ms_wall", "Wall", MeshOrigin.Primitive, "", "", "plane", ParamSet.Empty.With("width", 6.0).With("height", 3.0), 0, 0)],
        Surfaces = [new Surface("sf00000001", "Wall", "ms_wall", Transform3.Identity, [], SurfaceContent.Empty, 1, [], true, false, "#ffffff", [])],
    });

    /// <summary>Five edits a second apart, each its own undo step: op_1 named One … Five.</summary>
    private static async Task FiveEdits(ShowRig rig)
    {
        foreach (var name in Names)
        {
            await rig.Ok($"SET op_1 name \"{name}\"");
            rig.Advance(1000);
        }
    }

    private static JsonElement List(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static long[] Numbers(JsonElement list) => [.. list.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("step").GetInt64())];

    [Fact]
    public async Task FiveEditsAreFiveStepsNewestFirstInFieldWordsWithWhoAndWhen()
    {
        await using var rig = new ShowRig();
        await FiveEdits(rig);

        var list = List(await rig.Ok("UNDO LIST"));
        Assert.False(list.GetProperty("draft").GetBoolean());
        Assert.Equal(5, list.GetProperty("count").GetInt32());
        var steps = list.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(5, steps.Count);
        var numbers = Numbers(list);
        Assert.Equal(numbers.OrderDescending(), numbers);
        Assert.Equal(rig.Engine.Store.Steps.Select(s => s.Number), numbers);
        Assert.True(steps[0].GetProperty("next").GetBoolean());
        Assert.All(steps.Skip(1), s => Assert.False(s.GetProperty("next").GetBoolean()));
        foreach (var s in steps)
        {
            var label = s.GetProperty("label").GetString()!;
            Assert.StartsWith("set output ", label, StringComparison.Ordinal);
            Assert.EndsWith(" name", label, StringComparison.Ordinal);
            Assert.Equal("Desk", s.GetProperty("who").GetString());
            Assert.True(s.GetProperty("at").GetInt64() > 0);
            Assert.StartsWith(s.GetProperty("label").GetString() + " — Desk at ", s.GetProperty("words").GetString(), StringComparison.Ordinal);
        }

        // The count bounds the list, newest first; outside 1–100 is refused with the syntax.
        Assert.Equal(numbers[..2], Numbers(List(await rig.Ok("UNDO LIST 2"))));
        Assert.Equal("UNDO LIST [<n 1-100>]", await rig.Refused("UNDO LIST 0"));
        Assert.Equal("UNDO LIST [<n 1-100>]", await rig.Refused("UNDO LIST 101"));
    }

    [Fact]
    public async Task UndoToTheThirdLeavesTheRootAfterTheSecondInOneRevisionAndOneJournalRow()
    {
        await using var rig = new ShowRig();
        await FiveEdits(rig);
        var numbers = rig.Engine.Store.Steps.Select(s => s.Number).Reverse().ToArray();
        var revision = rig.Engine.Store.Current.Revision;
        var rows = rig.Engine.Journal.Recent(200).Count;

        await rig.Ok($"UNDO TO {numbers[2]}");

        Assert.Equal("Two", rig.Engine.Store.Project.Outputs[0].Name);
        Assert.Equal(revision + 1, rig.Engine.Store.Current.Revision);
        Assert.Equal(numbers[..2].Reverse(), rig.Engine.Store.Steps.Select(s => s.Number));
        var added = rig.Engine.Journal.Recent(200).Skip(rows).ToList();
        var row = Assert.Single(added);
        Assert.Equal(("verb", $"UNDO TO {numbers[2]}", true), (row.Kind, row.Line, row.Ok));

        // REDO brings them back one at a time, oldest first.
        await rig.Ok("REDO");
        Assert.Equal("Three", rig.Engine.Store.Project.Outputs[0].Name);
    }

    [Fact]
    public async Task UndoToTheNewestIsOneUndo()
    {
        await using var rig = new ShowRig();
        await FiveEdits(rig);
        var newest = rig.Engine.Store.Steps[0].Number;
        await rig.Ok($"UNDO TO {newest}");
        Assert.Equal("Four", rig.Engine.Store.Project.Outputs[0].Name);
        Assert.Equal(4, rig.Engine.Store.Steps.Length);
    }

    [Fact]
    public async Task AcrossALockIsRefusedWithTheLocksWordsAndNothingIsUndone()
    {
        await using var rig = new ShowRig(WithWall());
        await rig.Ok("SET sf00000001 opacity 0.5");
        rig.Advance(1000);
        var first = rig.Engine.Store.Steps[0].Number;
        await rig.Ok("SET op_1 name \"Left\"");
        await rig.Ok("LOCK sf00000001");
        var revision = rig.Engine.Store.Current.Revision;

        var words = await rig.Refused($"UNDO TO {first}");
        Assert.StartsWith("Wall (surface) is locked by Desk (", words, StringComparison.Ordinal);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Equal(0.5, rig.Engine.Store.Project.FindSurface("sf00000001")!.Opacity);
        Assert.Equal("Left", rig.Engine.Store.Project.Outputs[0].Name);
        Assert.Equal(2, rig.Engine.Store.Steps.Length);
    }

    [Fact]
    public async Task AnUnknownStepIsRefusedWithTheRangeAndAnEmptyListWithNothingToUndo()
    {
        await using var rig = new ShowRig();
        Assert.Equal("nothing to undo", await rig.Refused("UNDO TO 1"));
        Assert.Equal(0, List(await rig.Ok("UNDO LIST")).GetProperty("count").GetInt32());

        await FiveEdits(rig);
        var numbers = rig.Engine.Store.Steps.Select(s => s.Number).ToArray();
        var revision = rig.Engine.Store.Current.Revision;
        Assert.Equal($"no undo step 999 — they are {numbers[^1]} to {numbers[0]} (UNDO LIST lists them)", await rig.Refused("UNDO TO 999"));
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Equal("UNDO TO <step> — UNDO LIST names the steps", await rig.Refused("UNDO TO third"));
        Assert.Equal(5, rig.Engine.Store.Steps.Length);
    }

    [Fact]
    public async Task ArmedTheOperatorsUndoToIsAllowedAsUndoIs()
    {
        await using var rig = new ShowRig();
        await FiveEdits(rig);
        var numbers = rig.Engine.Store.Steps.Select(s => s.Number).Reverse().ToArray();
        await rig.Ok("SHOW ARM");
        Assert.Equal(ShowRole.Operator, rig.Engine.ShowRoleOf(ShowRig.Desk));

        await rig.Ok($"UNDO TO {numbers[3]}");
        Assert.Equal("Three", rig.Engine.Store.Project.Outputs[0].Name);
        // UNDO and UNDO TO are one class: a remote is refused both while armed.
        var undo = await rig.Refused("UNDO", ShowRig.Remote);
        var undoTo = await rig.Refused($"UNDO TO {numbers[2]}", ShowRig.Remote);
        Assert.StartsWith("Not while armed: UNDO", undo, StringComparison.Ordinal);
        Assert.StartsWith("Not while armed: UNDO", undoTo, StringComparison.Ordinal);
        Assert.Equal("Three", rig.Engine.Store.Project.Outputs[0].Name);
    }

    [Fact]
    public async Task TheBlindOwnersListAndUndoToAreTheDraftsAndTheLiveRevisionStands()
    {
        await using var rig = new ShowRig();
        await rig.Ok("SET op_1 name \"Live\"");
        rig.Advance(1000);
        await rig.Ok("BLIND ON");
        var liveRevision = rig.Engine.Store.Current.Revision;
        await FiveEdits(rig);

        var list = List(await rig.Ok("UNDO LIST"));
        Assert.True(list.GetProperty("draft").GetBoolean());
        Assert.Equal(5, list.GetProperty("count").GetInt32());
        var numbers = Numbers(list);
        // Another session reads the live project's own history.
        var other = List(await rig.Ok("UNDO LIST", ShowRig.Remote));
        Assert.False(other.GetProperty("draft").GetBoolean());
        Assert.Equal(1, other.GetProperty("count").GetInt32());

        await rig.Ok($"UNDO TO {numbers[2]}");
        Assert.Equal("Two", rig.Engine.Snapshot.Draft!.Project.Outputs[0].Name);
        Assert.Equal("Live", rig.Engine.Store.Project.Outputs[0].Name);
        Assert.Equal(liveRevision, rig.Engine.Store.Current.Revision);
        Assert.Equal(2, List(await rig.Ok("UNDO LIST")).GetProperty("count").GetInt32());
    }
}
