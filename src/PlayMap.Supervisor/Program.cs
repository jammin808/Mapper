namespace PlayMap.Supervisor;

/// <summary>"PlayMap.Supervisor …": the supervisor's entry. It does nothing yet: it starts and watches no engine, reads no command,
/// writes nowhere and changes nothing on the machine; it exits <see cref="NothingDone"/>, so no caller reads its run as a supervised
/// engine, and <see cref="Words"/> say why. A service has no console: the built supervisor logs to its own file and the Event
/// Log.</summary>
public static class Program
{
    /// <summary>The exit code of a run that did nothing.</summary>
    public const int NothingDone = 1;

    /// <summary>What the supervisor answers every command line with until it is built.</summary>
    public const string Words = "PlayMap.Supervisor does nothing yet: it starts and watches no engine and changes nothing on this machine";

    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        // wp3-installer fills this.
        return NothingDone;
    }
}
