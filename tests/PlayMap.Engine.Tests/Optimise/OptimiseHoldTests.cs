using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PlayMap.Core.Model;
using PlayMap.Engine.Optimise;

namespace PlayMap.Engine.Tests.Optimise;

/// <summary>The running job's hold as its worker's held lines make it a fact, with a worker that reads nothing and says nothing (the
/// system's sleeper): the test says the worker's lines itself, each where it would land while knobs are on their way, and orders the
/// knobs by its own steps (the service steps by itself only when a verb kicks it). A pause is applied at once only while the worker holds
/// the job and the knobs last sent hold it too, and is journalled at the frame the worker held at; a pause that comes while knobs that
/// let the job go are on their way is pending until the worker holds again; a resume that comes while knobs that hold are on their way
/// is pending until the worker goes on; and STATE says held only once the journal says paused.</summary>
public sealed class OptimiseHoldTests
{
    private const string JobId = "ojfirst1";

    private static WorkerEvent Held(bool on, long frames) => WorkerProtocol.Parse(WorkerProtocol.Held(JobId, on, frames))!;

    /// <summary>A node whose one job runs on a sleeper, its knobs ("run") sent with the job.</summary>
    private static async Task<OptimiseRig> RunningAsync()
    {
        var dir = Directory.CreateTempSubdirectory("playmap-optimise-");
        var home = new NodeHome(Path.Combine(dir.FullName, "node"));
        new JobStore(Path.Combine(home.Folder, "optimise"), NullLogger.Instance).Save([OptimiseWorkerEndTests.Job(JobId, 1, paused: false)]);
        var options = new OptimiseOptions
        {
            Worker = OptimiseWorkerEndTests.Sleeper(),
            Step = TimeSpan.FromHours(1),
            StopGrace = TimeSpan.FromMilliseconds(500),
        };
        var rig = OptimiseRig.Start(Project.CreateDefault("Optimise") with { Media = [OptimiseWorkerEndTests.Clip(dir)] }, options: options, dir: dir);
        await rig.Until(() => rig.Job(JobId).State == JobStates.Encoding && rig.Service.View.Pid is not null, "the job on its worker");
        return rig;
    }

    private static (bool Paused, bool Held) Row(OptimiseRig rig)
    {
        var row = rig.State().GetProperty("media").GetProperty("optimise").GetProperty("jobs").EnumerateArray()
            .First(j => j.GetProperty("id").GetString() == JobId);
        return (row.GetProperty("paused").GetBoolean(), row.GetProperty("held").GetBoolean());
    }

    private static string[] Pending(Core.Wire.CommandReply reply)
    {
        using var doc = JsonDocument.Parse(reply.Payload!);
        return [.. doc.RootElement.GetProperty("pending").EnumerateArray().Select(e => e.GetString()!)];
    }

    private static string[] Rows(OptimiseRig rig, string words) =>
        [.. rig.Engine.Journal.Recent(100).Where(r => r.Kind == "optimise" && r.Text.Contains(words, StringComparison.Ordinal)).Select(r => r.Text)];

    /// <summary>The governor holds the job (the show armed: knobs that hold), the worker says it holds at frame 7 while its last
    /// progress line said frame 5: a pause now is applied at once, journalled at frame 7.</summary>
    [Fact]
    public async Task APauseOfAJobTheGovernorHoldsIsAppliedAtOnceAtTheFrameItHeldAt()
    {
        await using var rig = await RunningAsync();
        rig.Gate.IsArmed = true;
        rig.Service.StepOnce();
        rig.Service.OnEvent(WorkerProtocol.Parse(WorkerProtocol.Progress(JobId, 5, 100, 25))!);
        rig.Service.OnEvent(Held(true, 7));

        var reply = await rig.Run($"MEDIA OPTIMISE PAUSE {JobId}");
        Assert.Equal((true, true), (reply.Ok, reply.Applied));
        Assert.Empty(Pending(reply));
        Assert.Equal([$"Clip ({JobId}) paused at frame 7"], Rows(rig, "paused at frame"));
        Assert.Equal((true, true), Row(rig));
    }

    /// <summary>The governor held the job and has let it go (the show disarmed: "run" sent), and the worker's line that it goes on is
    /// still on its way: a pause now is pending, not applied, and journalled once, when the worker holds again — at that frame.</summary>
    [Fact]
    public async Task APauseWhileKnobsThatLetTheJobGoAreOnTheirWayIsPendingUntilTheWorkerHoldsAgain()
    {
        await using var rig = await RunningAsync();
        rig.Gate.IsArmed = true;
        rig.Service.StepOnce();
        rig.Service.OnEvent(Held(true, 7));
        rig.Gate.IsArmed = false;
        rig.Service.StepOnce();

        var reply = await rig.Run($"MEDIA OPTIMISE PAUSE {JobId}");
        Assert.Equal((true, false), (reply.Ok, reply.Applied));
        Assert.Equal([JobId], Pending(reply));
        Assert.Empty(Rows(rig, "paused at frame"));

        // The line on its way: the worker went on at frame 7. Then the pause's knobs reach it, and it holds at frame 9.
        rig.Service.OnEvent(Held(false, 7));
        Assert.Equal((true, false), Row(rig));
        rig.Service.OnEvent(Held(true, 9));
        Assert.Equal([$"Clip ({JobId}) paused at frame 9"], Rows(rig, "paused at frame"));
        Assert.Equal((true, true), Row(rig));
    }

    /// <summary>The pause's knobs are sent and the worker's line that it holds is still on its way: a resume now is pending, not applied,
    /// and journalled when the worker goes on.</summary>
    [Fact]
    public async Task AResumeWhileKnobsThatHoldAreOnTheirWayIsPendingUntilTheWorkerGoesOn()
    {
        await using var rig = await RunningAsync();
        var paused = await rig.Run($"MEDIA OPTIMISE PAUSE {JobId}");
        Assert.Equal([JobId], Pending(paused));
        rig.Service.StepOnce();

        var reply = await rig.Run($"MEDIA OPTIMISE RESUME {JobId}");
        Assert.Equal((true, false), (reply.Ok, reply.Applied));
        Assert.Equal([JobId], Pending(reply));

        // The line on its way: the worker held at frame 4. Then the resume's knobs reach it, and it goes on.
        rig.Service.OnEvent(Held(true, 4));
        Assert.Equal((false, true), Row(rig));
        Assert.Empty(Rows(rig, "resumed at frame"));
        rig.Service.OnEvent(Held(false, 4));
        Assert.Equal([$"Clip ({JobId}) resumed at frame 4"], Rows(rig, "resumed at frame"));
        Assert.Equal((false, false), Row(rig));
    }

    /// <summary>The worker's line that it holds the paused job: the journal says paused before STATE says held, so a reader that sees
    /// held finds the row. Held while the row is being said, the job is not yet held to anyone who looks.</summary>
    [Fact]
    public async Task StateSaysHeldOnlyOnceTheJournalSaysPaused()
    {
        await using var rig = await RunningAsync();
        Assert.Equal([JobId], Pending(await rig.Run($"MEDIA OPTIMISE PAUSE {JobId}")));
        rig.Service.StepOnce();
        bool? heldWhileSaid = null;
        rig.Service.Said += (_, words, _) =>
        {
            if (words.Contains("paused at frame", StringComparison.Ordinal))
                heldWhileSaid = rig.Job(JobId).Held;
        };

        rig.Service.OnEvent(Held(true, 3));
        Assert.False(heldWhileSaid);
        Assert.Equal([$"Clip ({JobId}) paused at frame 3"], Rows(rig, "paused at frame"));
        Assert.True(rig.Job(JobId).Held);
    }
}
