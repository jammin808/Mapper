using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine;

namespace PlayMap.Engine.Tests;

/// <summary>The context's recent rows, the window of the journal MCP's get_context, Patterns, Stagewatch and the assistant read, say what
/// each row is: one that is not ok is prefixed by its kind, so a fault or a sync warning never reads there as a line that was refused,
/// and an ok row reads as written.</summary>
public sealed class ContextRecentTests
{
    [Fact]
    public async Task ARowThatIsNotOkSaysItsKindAndAnOkRowReadsAsWritten()
    {
        await using var engine = new EngineCore(EngineServices.ForTests(new ManualClock(5_000_000_000)), Project.CreateDefault("Show"));
        List<(string Kind, string Text, bool Ok)> rows =
        [
            ("fault", "output op_1 stopped presenting: the device was lost", false),
            ("sync", "FOH-A went quiet: free-wheeling on its last anchor", false),
            ("machine", "the high-resolution timer was refused: access denied", false),
            ("refused", "no output op_9", false),
            ("verb", "opened Show", true),
            ("sync", "following FOH-A for show", true),
        ];
        foreach (var (kind, text, ok) in rows)
            engine.Journal.Add(kind, text, ok: ok);

        var recent = ContextBuilder.Build(engine).Recent.Select(r => r.Text).TakeLast(rows.Count).ToList();

        Assert.Equal(
        [
            "fault: output op_1 stopped presenting: the device was lost",
            "sync: FOH-A went quiet: free-wheeling on its last anchor",
            "machine: the high-resolution timer was refused: access denied",
            "refused: no output op_9",
            "opened Show",
            "following FOH-A for show",
        ], recent);
        Assert.Equal(engine.Journal.Recent(rows.Count).Select(r => r.AtUnixMs), ContextBuilder.Build(engine).Recent.TakeLast(rows.Count).Select(r => r.AtUnixMs));
    }
}
