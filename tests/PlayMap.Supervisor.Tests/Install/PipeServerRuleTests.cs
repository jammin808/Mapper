using PlayMap.Supervisor.Install;

namespace PlayMap.Supervisor.Tests.Install;

/// <summary>Who the pipe's client takes an answer from: SYSTEM, or the installed supervisor's exe; a server it may not read at all
/// only in session 0, where services run. A program of the show user's own that took the pipe's name is refused before a word of its
/// answer is read.</summary>
public sealed class PipeServerRuleTests
{
    private const string Supervisor = @"C:\Program Files\PlayMap\Supervisor\PlayMap.Supervisor.exe";

    private static ProcessFacts Server(string sid = "", string image = "") => new(true, "", image, -1, "", sid, -1, 0);

    [Fact]
    public void TheServiceIsTakenAsItReads()
    {
        Assert.Null(PipeServerRule.Refusal(812, Server(sid: PipeServerRule.LocalSystemSid), 0, Supervisor));
        Assert.Null(PipeServerRule.Refusal(812, Server(image: Supervisor.ToUpperInvariant()), 0, Supervisor));
        // As a standard account reads a SYSTEM service: nothing at all, in session 0.
        Assert.Null(PipeServerRule.Refusal(812, Server(), 0, Supervisor));
    }

    [Fact]
    public void AnyOtherServerIsRefusedWithWhy()
    {
        Assert.Equal("pid 4400, which runs as another account than SYSTEM",
            PipeServerRule.Refusal(4400, Server(sid: "S-1-5-21-1-2-3-1001", image: Supervisor), 1, Supervisor));
        Assert.Equal(@"pid 4400 (C:\Users\show\squat.exe), which is not the installed " + Supervisor,
            PipeServerRule.Refusal(4400, Server(image: @"C:\Users\show\squat.exe"), 0, Supervisor));
        Assert.Equal("pid 4400, a process this account may not read, in session 1 rather than the services' session 0",
            PipeServerRule.Refusal(4400, Server(), 1, Supervisor));
    }
}
