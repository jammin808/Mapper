using PlayMap.Assistant;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// Suggestions name regions and corners of the solve they are made from, and a solve withdraws the ones waiting: so while a scan's
/// suggestions are made (an assistant naming them) no solve or view lands on that scan, and while the scan takes a view or is solved no
/// suggestion of it is made. No suggestion made from a solve that is gone ever waits for a person.
/// </summary>
public sealed class ScanSuggestRaceTests
{
    private const string Pj1 = MultiViewScanKit.Pj1;
    private const string Pj2 = MultiViewScanKit.Pj2;

    private static string Line(string? into = null) =>
        $"SCAN START {MultiViewScanKit.Camera} {Pj1} {Pj2}" + (into is null ? "" : " INTO " + into);

    /// <summary>A model that answers once it is let go, naming nothing (the measured names stand).</summary>
    private sealed class GatedModel : IAssistantProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AssistantProviderKind Kind => AssistantProviderKind.OpenAiCompatible;

        public string Model => "gated-model";

        public int ValidationRetries => 0;

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task<AssistantReply> CompleteAsync(AssistantRequest request, Action<string>? onText, CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return new AssistantReply([new ToolCallPart("t1", ScanSuggestions.ToolName, """{"items":[]}""")], StopKind.ToolUse, default, Model);
        }

        public void Dispose()
        {
            // Nothing is held.
        }
    }

    [Fact]
    public async Task WhileSuggestionsAreMadeTheScanIsNotSolvedAgainAndWhileItTakesAViewNoSuggestionIsMade()
    {
        var model = new GatedModel();
        using var hold = new ManualResetEventSlim(true);
        var inView = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new OpsRig(new AssistantSettings { Enabled = true }, project: MultiViewScanKit.Project(), scan: new ScanRunnerOptions
        {
            SuggestModel = (_, _) => model,
            // The second view is held at its first projector while the test looks.
            SimulatedNudge = (view, _, camera) =>
            {
                if (view == "v2" && inView.TrySetResult())
                    hold.Wait(TimeSpan.FromMinutes(2));
                return camera;
            },
        });
        try
        {
            var id = await MultiViewScanKit.ScanAsync(rig, Line());
            var before = rig.Scanner.Find(id)!;
            var record = rig.Engine.Store.Project.Stage.Scans.Single();
            var asking = rig.Run($"SCAN SUGGEST {id} ASSISTANT");
            await model.Entered.WaitAsync(TimeSpan.FromSeconds(60));

            // The suggestions are being made from the solve the scan has now: a solve or a view INTO it is refused until they are queued.
            foreach (var line in new[] { $"SCAN SOLVE {id} HOLD {Pj1}", Line(id) })
            {
                var refused = await rig.Run(line);
                Assert.False(refused.Ok, line);
                Assert.Contains("suggestions are being made", refused.Payload, StringComparison.Ordinal);
            }
            Assert.Same(before, rig.Scanner.Find(id));
            Assert.Equal(record, rig.Engine.Store.Project.Stage.Scans.Single());
            Assert.False(rig.Scanner.Busy);
            Assert.False(inView.Task.IsCompleted);

            model.Release();
            var asked = await asking;
            Assert.True(asked.Ok, asked.Payload);
            var proposals = rig.Scanner.SuggestionsFor(id).SelectMany(s => s.Proposals).ToList();
            Assert.NotEmpty(proposals);
            Assert.All(proposals, p => Assert.Equal(ProposalState.Pending, rig.Assistant.Queue.Find(p)!.State));

            // A view INTO the scan, held while it is taken: no suggestion of the scan is made meanwhile.
            hold.Reset();
            await rig.Ok(Line(id));
            await inView.Task.WaitAsync(TimeSpan.FromMinutes(2));
            var during = await rig.Run($"SCAN SUGGEST {id}");
            hold.Set();
            Assert.False(during.Ok);
            Assert.Contains("is taking a view", during.Payload, StringComparison.Ordinal);
            Assert.True(await OpsRig.Eventually(() => rig.Scanner.View is { IsRunning: false } v && v.Id == id, 300_000), "the view did not end");
            Assert.True(rig.Scanner.View!.State == "done", rig.Scanner.View.Message);

            // The view landed: the suggestions made from the solve before it are withdrawn, and none made from that solve waits.
            Assert.All(proposals, p =>
            {
                var proposal = rig.Assistant.Queue.Find(p)!;
                Assert.Equal(ProposalState.Rejected, proposal.State);
                Assert.Equal("the scan was solved again — SCAN SUGGEST again", proposal.ResultWords);
            });
            Assert.DoesNotContain(rig.Assistant.Queue.All, p => p.State == ProposalState.Pending);
            Assert.Empty(rig.Scanner.SuggestionsFor(id));
        }
        finally
        {
            model.Release();
            hold.Set();
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
