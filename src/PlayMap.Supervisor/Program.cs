namespace PlayMap.Supervisor;

/// <summary>"PlayMap.Supervisor …": the supervisor's entry. It does nothing yet: it starts and watches no engine, reads no command and
/// changes nothing on the machine; it says so on the standard error and exits <see cref="NothingDone"/>, so no caller reads its run as
/// a supervised engine.</summary>
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
        Console.Error.WriteLine(Words);
        return NothingDone;
    }
}
