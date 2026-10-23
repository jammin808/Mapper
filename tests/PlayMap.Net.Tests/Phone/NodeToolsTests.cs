using PlayMap.Tests;

namespace PlayMap.Net.Tests.Phone;

/// <summary>A camera page script's run says how it ended (<see cref="NodeTools.Ended"/>): one that could not start says so with the
/// reason, never that it ran past its deadline; one stopped at its deadline says that; one that finished gives its code and words.</summary>
public sealed class NodeToolsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public void AScriptThatCouldNotStartSaysSoWithTheReason()
    {
        var ran = new ChildRun("/no/such/node", Started: false, Finished: false, ExitCode: -1, Output: [], Errors: "", WordsIn: false, Deadline,
            Why: "No such file or directory");
        var thrown = Assert.Throws<InvalidOperationException>(() => NodeTools.Ended(ran, "camera.test.mjs", Deadline));
        Assert.Equal("camera.test.mjs: /no/such/node could not be started: No such file or directory", thrown.Message);
    }

    [Fact]
    public void AScriptStoppedAtItsDeadlineSaysSoAndAFinishedOneGivesItsCodeAndWords()
    {
        var stopped = new ChildRun("node", Started: true, Finished: false, ExitCode: -1, Output: [], Errors: "waiting", WordsIn: true, Deadline, Why: "");
        var thrown = Assert.Throws<TimeoutException>(() => NodeTools.Ended(stopped, "camera.test.mjs", Deadline));
        Assert.Equal("camera.test.mjs ran past 30 s and was stopped: waiting", thrown.Message);
        var finished = new ChildRun("node", Started: true, Finished: true, ExitCode: 1, Output: "ok 1\n"u8.ToArray(), Errors: "not ok 2\n", WordsIn: true,
            Deadline, Why: "");
        Assert.Equal((1, "ok 1\nnot ok 2\n"), NodeTools.Ended(finished, "camera.test.mjs", Deadline));
    }
}
