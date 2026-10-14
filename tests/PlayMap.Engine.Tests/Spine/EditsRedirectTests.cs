using PlayMap.Core.Model;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Spine;

/// <summary>Every document edit handler writes through <see cref="EngineCore.Edits"/>: with a line's edits sent to another store (what
/// the show's gate does for a blind session), every edit verb changes that store and the live project's revision never moves.</summary>
public sealed class EditsRedirectTests
{
    private static readonly SessionInfo Desk = SessionInfo.Desk("t");

    private static WireLine Line(string text)
    {
        Assert.True(WireGrammar.TryParse(text, out var parsed, out var error), error?.Reason);
        return parsed;
    }

    private static async Task<string> Live(EngineCore engine, string text)
    {
        var r = await engine.HandleAsync(Line(text), Desk, CancellationToken.None);
        Assert.True(r.Ok, $"{text} → {r.Payload}");
        return r.Payload ?? "";
    }

    [Fact]
    public async Task EveryEditVerbGoesToTheLinesStoreAndTheLiveRevisionNeverMoves()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        // What the lines below refer to, made live first; the draft starts as the live project.
        var list = await Live(engine, "PLAYLIST NEW Show");
        var spare = await Live(engine, "PLAYLIST NEW Spare");
        var gen = await Live(engine, "GENERATOR NEW grid Grid");
        var card = await Live(engine, "GENERATOR NEW testcard Card");
        var unused = await Live(engine, "GENERATOR NEW checker Unused");
        var item = await Live(engine, $"PLAYLIST ADD {list} generator:{gen} Opening");
        await Live(engine, $"PLAYLIST ADD {list} generator:{card} Main");
        var output = engine.Store.Project.Outputs[0].Id;
        var live = engine.Store.Current.Revision;
        var draft = new ProjectStore(engine.Store.Project);

        string[] lines =
        [
            "GENERATOR NEW julia Fractal",
            $"GENERATOR SET {gen} cell 77",
            "PLAYLIST NEW Draft",
            $"PLAYLIST ADD {list} generator:{card} Encore",
            $"PLAYLIST MOVE {list} {item} 1",
            $"PLAYLIST REMOVE {list} {item}",
            $"PLAYLIST LOOP {list} ON",
            $"PLAYLIST SYNC {list} wall",
            $"PLAYLIST DELETE {spare}",
            $"OUTPUT ENABLE {output} OFF",
            $"OUTPUT SOURCE {output} generator:{gen}",
            $"WARP NODE {output} 1 1 0.05 0",
            $"WARP RESET {output}",
            $"SET {output} name \"Draft name\"",
            "ADD canvas {\"name\":\"Draft canvas\"}",
            $"MOVE {unused} 0",
            $"REMOVE {unused}",
            "UNDO",
            "REDO",
        ];
        foreach (var text in lines)
        {
            var before = draft.Current.Revision;
            var reply = await engine.RouteIntoAsync(draft, Line(text), Desk);
            Assert.True(reply.Ok, $"{text} → {reply.Payload}");
            Assert.NotEqual(before, draft.Current.Revision);
            Assert.Equal(live, engine.Store.Current.Revision);
        }

        Assert.Equal("Draft name", draft.Project.FindOutput(output)!.Name);
        Assert.Equal("Output 1", engine.Store.Project.FindOutput(output)!.Name);
        Assert.Null(draft.Project.FindPlaylist(spare));
        Assert.NotNull(engine.Store.Project.FindPlaylist(spare));
        Assert.Null(draft.Project.FindGenerator(unused));
        Assert.NotNull(engine.Store.Project.FindGenerator(unused));
    }

    [Fact]
    public async Task AnOffLaneEditGoesToTheLiveStoreWhenTheSessionHasNoDraft()
    {
        await using var engine = new EngineCore(EngineServices.ForTests());
        var before = engine.Store.Current.Revision;

        var r = await engine.EditAsync(Desk, "renamed", p => p with { Name = "Renamed" });

        Assert.True(r.Changed, r.Effect);
        Assert.Equal(before + 1, engine.Store.Current.Revision);
        Assert.Equal("Renamed", engine.Store.Project.Name);
        var again = await engine.EditAsync(Desk, "renamed", p => p with { Name = "Renamed" });
        Assert.False(again.Changed);
        Assert.Contains("nothing to change", again.Effect, StringComparison.Ordinal);
    }
}
