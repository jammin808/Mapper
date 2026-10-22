using System.Text.Json;
using PlayMap.Core.Machine;
using PlayMap.Engine.Tests.Machine;

namespace PlayMap.Engine.Tests;

/// <summary>CONTEXT's recent rows as its JSON publishes them to MCP's get_context, Patterns, Stagewatch and the assistant: each row's
/// head follows its kind. A line the node refused says "refused: " before its words, a fault it met "fault: ", and a row of any other
/// kind, ok or not, is its own words: a claim Windows refused says so itself, and is no line the node refused.</summary>
public sealed class ContextRecentTests
{
    private const string Denied = "Windows refused the display-required request: error 5: Access is denied.";

    [Fact]
    public async Task ARefusalAFaultAndAMachineClaimsRefusalEachCarryTheHeadOfItsKind()
    {
        var writerBroken = false;
        await using var rig = new MachineRig(settings: MachineSettings.Default with { Awake = "off" }, save: _ =>
        {
            if (writerBroken)
                throw new InvalidOperationException("the settings writer broke");
        });

        // A line the node refuses.
        Assert.Equal("MACHINE SET QOS HIGH|AUTO", await rig.Refused("MACHINE SET QOS SIDEWAYS"));
        // A claim Windows refuses: the machine part journals it in its own words.
        rig.Platform.RefuseAwake = Denied;
        await rig.Ok("MACHINE SET AWAKE ON");
        // A line the node faults on: its settings writer throws what no handler takes.
        writerBroken = true;
        Assert.StartsWith("the node faulted on this line", (await rig.Run("MACHINE SET QOS AUTO")).Payload, StringComparison.Ordinal);

        var rows = rig.Engine.Journal.Recent(ContextBuilder.RecentRows);
        using var context = JsonDocument.Parse(rig.Engine.Context());
        var recent = context.RootElement.GetProperty("recent").EnumerateArray().Select(r => r.GetProperty("text").GetString()!).ToList();
        Assert.Equal(rows.Count, recent.Count);

        Assert.Contains("refused: MACHINE SET QOS HIGH|AUTO", recent);
        var fault = Assert.Single(rows, r => r.Kind == "fault");
        Assert.Contains("InvalidOperationException: the settings writer broke", fault.Text, StringComparison.Ordinal);
        Assert.Contains("fault: " + fault.Text, recent);
        var claim = Assert.Single(rows, r => r.Kind == "machine" && r.Text.StartsWith("process.awake", StringComparison.Ordinal));
        Assert.Equal(("process.awake on was refused: " + Denied, false), (claim.Text, claim.Ok));
        Assert.Contains("process.awake on was refused: " + Denied, recent);

        // No other row has a head: a row that is not ok says what it is in its own words.
        foreach (var (row, text) in rows.Zip(recent))
            Assert.Equal(row.Kind switch { "refused" => "refused: ", "fault" => "fault: ", _ => "" } + row.Text, text);
    }
}
