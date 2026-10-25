using System.Globalization;

namespace PlayMap.Net.Links;

/// <summary>What this node does with one of Stagewatch's screen commands: the journal row that says it, and for a redirect the
/// view its timer shows (<see cref="View"/>; null for the others). A plan carries no line for the engine: PlayMap sits on
/// Stagewatch's Screens page as a show app, not a screen, and nothing pressed there reaches its air.</summary>
public sealed record StagewatchScreenPlan(string Row, string? View);

/// <summary>Stagewatch's screen commands (its Screens page: redirect, identify, reload, disconnect) as this node takes them.
/// Identify is one journal row and never flashes an output: Stagewatch's "identify all" reaches every screen it holds, and a show
/// node's outputs are on the air. Redirect picks the view the timer shows; reload and disconnect are noted, the disconnect's row
/// naming the hold the link waits before it dials again (<see cref="StagewatchLinkOptions.DisconnectHold"/>).</summary>
public static class StagewatchScreenCommands
{
    /// <summary>Who each screen command's row names as the asker: a constant, never a name or id the peer sends.</summary>
    public const string Who = "Stagewatch";

    /// <summary>The row an identify writes: the ask, and that nothing flashed.</summary>
    public const string IdentifyRow = "Stagewatch's operator asked this screen to identify itself — PlayMap flashes no output for a screen "
        + "command (OUTPUT IDENTIFY on this node does)";

    /// <summary>The plan for <paramref name="command"/>, or null for a type that is not one of the four (nothing is done).
    /// <paramref name="disconnectHold"/> is the hold the link takes after a disconnect.</summary>
    public static StagewatchScreenPlan? Plan(StagewatchScreenCommand command, TimeSpan disconnectHold)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Type switch
        {
            "identify" => new StagewatchScreenPlan(IdentifyRow, null),
            "redirect" => new StagewatchScreenPlan(
                "Stagewatch's operator shows " + (command.To.Length > 0 ? command.To : "the default view") + " on this node's timer", command.To),
            "reload" => new StagewatchScreenPlan("Stagewatch's operator asked this screen to reload: its timer is read afresh from the next frame", null),
            "disconnect" => new StagewatchScreenPlan(
                string.Create(CultureInfo.InvariantCulture, $"Stagewatch's operator disconnected this screen; the link dials again in {disconnectHold.TotalSeconds:0.#} s"),
                null),
            _ => null,
        };
    }
}
