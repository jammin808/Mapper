namespace PlayMap.Devices.Commands;

/// <summary>
/// How far a command got — attempts are not facts. The stages are ordered: anything at or past
/// <see cref="Accepted"/> is a yes from the projector; only <see cref="Observed"/> is a fact about its state.
/// </summary>
public enum CommandStage
{
    /// <summary>Not sent: unsupported, unverified, missing credential or a bad argument. The words say which.</summary>
    Refused,

    /// <summary>Nothing reached the projector: no connection, or the handshake (authentication) failed.</summary>
    Unreached,

    /// <summary>The bytes left; no answer came that says yes or no (some protocols answer a set with silence).</summary>
    Sent,

    /// <summary>The projector answered no (PJLink ERR2/ERR3, an NTCONTROL ERR, a JSON-RPC error…).</summary>
    Rejected,

    /// <summary>The projector said yes (OK, an echo, an ACK).</summary>
    Accepted,

    /// <summary>A follow-up query read back the state the command asked for.</summary>
    Observed,
}

/// <summary>
/// The outcome of one command: what was asked, how far it got, the words for the desk and the projector's raw
/// reply (never a secret: drivers strip digests and passwords before a reply is kept).
/// </summary>
public sealed record CommandResult(string Command, CommandStage Stage, string Words, string Reply = "")
{
    /// <summary>The projector said yes, or a query confirmed it.</summary>
    public bool Succeeded => Stage >= CommandStage.Accepted;

    /// <summary>A follow-up query confirmed it: a fact, not an attempt.</summary>
    public bool IsFact => Stage == CommandStage.Observed;

    public static CommandResult Refused(string command, string why) => new(command, CommandStage.Refused, why);

    public static CommandResult Unreached(string command, string why) => new(command, CommandStage.Unreached, why);

    public static CommandResult Sent(string command, string words, string reply = "") => new(command, CommandStage.Sent, words, reply);

    public static CommandResult Rejected(string command, string why, string reply = "") => new(command, CommandStage.Rejected, why, reply);

    public static CommandResult Accepted(string command, string words, string reply = "") => new(command, CommandStage.Accepted, words, reply);

    public static CommandResult Observed(string command, string words, string reply = "") => new(command, CommandStage.Observed, words, reply);

    /// <summary>This result raised to Observed when a read-back confirms it; left as it was (with the read-back's
    /// words appended) when the read-back disagrees or could not be read.</summary>
    public CommandResult ConfirmedBy(bool? confirmed, string readBack)
    {
        if (Stage < CommandStage.Sent || Stage == CommandStage.Rejected)
            return this;
        return confirmed switch
        {
            true => this with { Stage = CommandStage.Observed, Words = Words + " — confirmed: " + readBack },
            false => this with { Words = Words + " — but the projector reads " + readBack },
            _ => this with { Words = Words + " — not confirmed (" + readBack + ")" },
        };
    }

    public static string StageWord(CommandStage s) => s switch
    {
        CommandStage.Refused => "refused",
        CommandStage.Unreached => "unreached",
        CommandStage.Sent => "sent",
        CommandStage.Rejected => "rejected",
        CommandStage.Accepted => "accepted",
        _ => "observed",
    };

    public override string ToString() => $"{Command}: {StageWord(Stage)} — {Words}";
}
