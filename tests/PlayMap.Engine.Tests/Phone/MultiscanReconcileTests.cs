using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Core.Geometry;
using PlayMap.Core.Model;
using PlayMap.Core.Site;
using PlayMap.Core.Wire;
using PlayMap.Engine.Scanning;
using PlayMap.Engine.Tests.Wall;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// Multi-view scans in the node's other rules: from the session that holds the blind draft a scan, a view INTO one, a joint solve, a drop
/// and suggestions are refused (never staged) while a surface made from the scan is staged and lands with the commit; armed, every scan
/// that lights the outputs or solves is refused, and a person's approval of a suggestion runs as an approval (refused while armed, through
/// the lane once disarmed) — never the assistant's; a lock on the scan or the stage refuses the work before any starts; once the runner
/// stops no view, solve or suggestion starts and a suggestion under way ends first with nothing queued; a scan taken before SITE ORIGIN
/// moved the stage is carried into the new frame by its next view, solve or drop; a camera re-solve and a scan never run together, and
/// a folder camera is not one a re-solve looks through.
/// </summary>
public sealed class MultiscanReconcileTests(ITestOutputHelper output)
{
    private const string Pj1 = MultiViewScanKit.Pj1;
    private const string Pj2 = MultiViewScanKit.Pj2;

    private static string Line(string? into = null) =>
        $"SCAN START {MultiViewScanKit.Camera} {Pj1} {Pj2}" + (into is null ? "" : " INTO " + into);

    private static JsonElement Json(string payload) => JsonDocument.Parse(payload).RootElement.Clone();

    private static void SamePlace(Pose expected, Pose actual, double metres, double degrees)
    {
        Assert.True((expected.Position - actual.Position).Length < metres, $"{(expected.Position - actual.Position).Length} m");
        Assert.True(MultiViewScanKit.AngleDeg(expected, actual) < degrees, $"{MultiViewScanKit.AngleDeg(expected, actual)}°");
    }

    [Fact]
    public async Task FromTheDraftsSessionScanWorkIsRefusedAndASurfaceFromTheScanIsStagedUntilTheCommitLandsIt()
    {
        var rig = new OpsRig(project: MultiViewScanKit.Project());
        try
        {
            var id = await MultiViewScanKit.ScanAsync(rig, Line());
            var before = rig.Scanner.Find(id)!;
            var record = rig.Engine.Store.Project.Stage.Scans.Single();
            await rig.Ok("BLIND ON");

            // Heavy work lands on the live stage: refused from the draft's session, with the words why, and nothing starts.
            foreach (var line in new[] { Line(id), Line(), $"SCAN SOLVE {id} HOLD {Pj1}", $"SCAN DROP {id} v1" })
            {
                var r = await rig.Run(line);
                Assert.False(r.Ok, line);
                Assert.Contains("is heavy work and is not staged in a blind draft", r.Payload, StringComparison.Ordinal);
                Assert.EndsWith("BLIND COMMIT or BLIND DISCARD first", r.Payload, StringComparison.Ordinal);
            }
            var suggest = await rig.Run($"SCAN SUGGEST {id}");
            Assert.False(suggest.Ok);
            Assert.StartsWith("SCAN SUGGEST's lines are approved onto the live stage, never into a blind draft", suggest.Payload, StringComparison.Ordinal);
            Assert.Same(before, rig.Scanner.Find(id));
            Assert.Equal(record, rig.Engine.Store.Project.Stage.Scans.Single());
            Assert.Empty(rig.Assistant.Queue.All);
            Assert.False(rig.Scanner.Busy);

            // A surface made from the scan is an edit: staged in the draft, off the live stage, and named by BLIND DIFF.
            var region = before.Regions.First(r => r.Projectors.Contains(Pj2));
            var made = await rig.Run($"SCAN SURFACE {id} REGION {region.Id} ON {Pj2} Wall");
            Assert.True(made.Ok, made.Payload);
            output.WriteLine(made.Effect);
            Assert.StartsWith("staged in the blind draft: made surface Wall", made.Effect, StringComparison.Ordinal);
            Assert.EndsWith("on the stage when the blind draft lands", made.Effect, StringComparison.Ordinal);
            Assert.Empty(rig.Engine.Store.Project.Stage.Surfaces);
            var changes = Json(await rig.Ok("BLIND DIFF")).GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
            Assert.Contains(made.Payload, changes);

            // The commit lands it on the live stage.
            await rig.Ok("BLIND COMMIT");
            Assert.True(await OpsRig.Eventually(() => rig.Engine.Store.Project.Stage.Surfaces.Any(s => s.Id == made.Payload)), "the commit did not land");
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task ALockRefusesScanWorkBeforeItStartsArmedScansAreRefusedAndASuggestionIsApprovedByAPersonAlone()
    {
        var rig = new OpsRig(new AssistantSettings { Enabled = true, RunReviewWithoutAsking = true }, project: MultiViewScanKit.Project());
        try
        {
            var id = await MultiViewScanKit.ScanAsync(rig, Line());
            var one = rig.Scanner.Find(id)!;

            // A lock on the scan refuses a view INTO it, a solve and a drop with the lock's words, before any pattern or solve.
            await rig.Ok($"LOCK {id} survey night");
            foreach (var line in new[] { Line(id), $"SCAN SOLVE {id} HOLD {Pj1}", $"SCAN DROP {id} v1" })
            {
                var r = await rig.Run(line);
                Assert.False(r.Ok, line);
                Assert.Contains("is locked by Desk", r.Payload, StringComparison.Ordinal);
                Assert.Contains($"UNLOCK {id} first", r.Payload, StringComparison.Ordinal);
            }
            Assert.Same(one, rig.Scanner.Find(id));
            Assert.Null(rig.Scanner.RunningId);
            await rig.Ok($"UNLOCK {id}");
            // The mapping area locked refuses a new scan the same way.
            await rig.Ok("LOCK AREA mapping");
            var area = await rig.Run(Line());
            Assert.False(area.Ok);
            Assert.StartsWith("mapping is locked by Desk", area.Payload, StringComparison.Ordinal);
            Assert.Single(rig.Engine.Store.Project.Stage.Scans);
            await rig.Ok("UNLOCK AREA mapping");

            // Suggestions wait for a person: the assistant's own line never approves one.
            var suggested = Json(await rig.Ok($"SCAN SUGGEST {id}")).GetProperty("suggestions").EnumerateArray().ToList();
            var queue = rig.Assistant.Queue;
            var proposals = suggested.SelectMany(s => s.GetProperty("proposals").EnumerateArray().Select(p => p.GetString()!)).ToList();
            var surfaceLine = proposals.First(p => queue.Find(p)!.Line.StartsWith("SCAN SURFACE ", StringComparison.Ordinal));
            var quadLine = proposals.First(p => queue.Find(p)!.Line.StartsWith("SURFACE QUAD ", StringComparison.Ordinal));
            var asAssistant = new SessionInfo("a1", "assistant", "127.0.0.1", true, "Assistant", true);
            var byAssistant = await rig.Run("APPROVE " + surfaceLine, asAssistant);
            Assert.False(byAssistant.Ok);
            Assert.StartsWith("a person approves", byAssistant.Payload, StringComparison.Ordinal);
            Assert.Equal(ProposalState.Pending, queue.Find(surfaceLine)!.State);

            // Armed: every scan that lights the outputs or solves is refused with the gate's words; reading the scan still runs.
            await rig.Ok("SHOW ARM");
            foreach (var line in new[] { Line(id), Line(), $"SCAN SOLVE {id} HOLD {Pj1}", $"SCAN DROP {id} v1", "SCAN NEXT" })
            {
                var r = await rig.Run(line);
                Assert.False(r.Ok, line);
                Assert.StartsWith("Not while armed: SCAN ", r.Payload, StringComparison.Ordinal);
            }
            Assert.True((await rig.Run("SCAN VIEWS " + id)).Ok);
            // A person's approval runs the line as an approval: an armed show refuses it, and nothing is made.
            var armed = await rig.Run("APPROVE " + surfaceLine);
            Assert.True(armed.Ok, armed.Payload);
            Assert.Equal(ProposalState.Failed, queue.Find(surfaceLine)!.State);
            Assert.Contains("Not while armed: SCAN SURFACE", queue.Find(surfaceLine)!.ResultWords, StringComparison.Ordinal);
            Assert.Empty(rig.Engine.Store.Project.Stage.Surfaces);
            await rig.Ok("SHOW DISARM");

            // Disarmed, the plane's four corners run through the lane as the person approved them: the surface is on the live stage.
            var approved = await rig.Run("APPROVE " + quadLine);
            Assert.True(approved.Ok, approved.Payload);
            Assert.Equal(ProposalState.Done, queue.Find(quadLine)!.State);
            Assert.Equal("Desk", queue.Find(quadLine)!.DecidedBy);
            Assert.NotNull(Assert.Single(rig.Engine.Store.Project.Stage.Surfaces).Pixel);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>A model that waits, answering nothing, until its call is cancelled.</summary>
    private sealed class WaitingModel : IAssistantProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AssistantProviderKind Kind => AssistantProviderKind.OpenAiCompatible;

        public string Model => "waiting-model";

        public int ValidationRetries => 0;

        public Task Entered => _entered.Task;

        public bool Cancelled { get; private set; }

        public async Task<AssistantReply> CompleteAsync(AssistantRequest request, Action<string>? onText, CancellationToken ct)
        {
            _entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
            throw new InvalidOperationException("a call that never ends ended");
        }

        public void Dispose()
        {
            // Nothing is held.
        }
    }

    [Fact]
    public async Task OnceTheRunnerStopsNoViewSolveOrSuggestionStartsAndASuggestionUnderWayEndsFirstWithNothingQueued()
    {
        var model = new WaitingModel();
        var rig = new OpsRig(new AssistantSettings { Enabled = true }, project: MultiViewScanKit.Project(),
            scan: new ScanRunnerOptions { SuggestModel = (_, _) => model });
        try
        {
            var id = await MultiViewScanKit.ScanAsync(rig, Line());
            var asking = rig.Run($"SCAN SUGGEST {id} ASSISTANT");
            await model.Entered.WaitAsync(TimeSpan.FromSeconds(60));

            // The stop cancels the suggestion and waits for it: its reply says so, and no proposal waits on the node's list.
            await rig.Scanner.StopAsync();
            Assert.True(model.Cancelled);
            var asked = await asking;
            Assert.False(asked.Ok);
            Assert.StartsWith(ScanRunner.StoppingWords, asked.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(rig.Assistant.Queue.All, p => p.State == ProposalState.Pending);
            Assert.Empty(rig.Scanner.SuggestionsFor(id));

            // From then on nothing starts: a view INTO the scan, a new scan, a solve, a drop, suggestions.
            foreach (var line in new[] { Line(id), Line(), $"SCAN SOLVE {id} HOLD {Pj1}", $"SCAN DROP {id} v1", $"SCAN SUGGEST {id}" })
            {
                var r = await rig.Run(line);
                Assert.False(r.Ok, line);
                Assert.Equal(ScanRunner.StoppingWords, r.Payload);
            }
            Assert.False(rig.Scanner.Busy);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    [Fact]
    public async Task AScanTakenBeforeTheSiteMovedIsCarriedIntoTheNewFrameByItsNextViewSolveAndDrop()
    {
        var rig = new OpsRig(project: MultiViewScanKit.Project());
        try
        {
            // The site's first origin moves nothing; the scan's first view is placed in it.
            await rig.Ok("SITE ORIGIN 512300 5401200 100 CRS EPSG:25832");
            var site1 = rig.Engine.Store.Project.Stage.Site;
            var id = await MultiViewScanKit.ScanAsync(rig, Line());
            var one = rig.Scanner.Find(id)!;
            Assert.Equal(site1, Assert.Single(one.Captured).Site);

            // The site moves and the stage with it: the scan keeps its old points, and the words say what carries it.
            var moved = Json(await rig.Ok("SITE ORIGIN 512310 5401195 101 ROTATE 30"));
            output.WriteLine(moved.GetProperty("words").GetString());
            Assert.Contains("a view INTO a scan, or SCAN SOLVE of one with many views, carries its views into the new frame",
                moved.GetProperty("words").GetString(), StringComparison.Ordinal);
            var site2 = rig.Engine.Store.Project.Stage.Site;
            var move = StageMove.Between(site1, site2);

            // A second view: the anchor view stands where the move puts its old place, every view is in the new frame, and the
            // projectors land where the move puts the first view's solve of them.
            Assert.Equal(id, await MultiViewScanKit.ScanAsync(rig, Line(id)));
            var two = rig.Scanner.Find(id)!;
            SamePlace(move.Apply(one.Captured[0].Pose!), two.Multi!.Result.View("v1").Pose, 1e-6, 1e-4);
            Assert.All(two.Captured, v => Assert.Equal(site2, v.Site));
            Assert.Contains(MultiViewScan.CarriedWords, rig.Engine.Store.Project.Stage.Scans.Single().Notes, StringComparison.Ordinal);
            foreach (var p in one.Solution.Projectors.Where(p => p.Solved))
            {
                var now = two.Solution.Projectors.Single(x => x.ProjectorId == p.ProjectorId);
                output.WriteLine($"{p.ProjectorId}: {(move.Apply(p.Pose).Position - now.Pose.Position).Length * 1000:0.0} mm");
                SamePlace(move.Apply(p.Pose), now.Pose, 0.05, 1);
            }

            // Moved again: SCAN SOLVE of the two views is no longer "nothing would change" — the fresh solve is carried.
            await rig.Ok("SITE ORIGIN 512305 5401190 99 ROTATE 10");
            var site3 = rig.Engine.Store.Project.Stage.Site;
            var solved = Json(await rig.Ok($"SCAN SOLVE {id}"));
            Assert.Contains(MultiViewScan.CarriedWords, solved.GetProperty("words").GetString() + rig.Engine.Store.Project.Stage.Scans.Single().Notes,
                StringComparison.Ordinal);
            var three = rig.Scanner.Find(id)!;
            SamePlace(StageMove.Between(site2, site3).Apply(two.Multi.Result.View("v1").Pose), three.Multi!.Result.View("v1").Pose, 1e-6, 1e-4);

            // Moved again: the warm drop keeps the frame it had and carries it by the move.
            await rig.Ok("SITE ORIGIN 512300 5401200 100 ROTATE 0");
            var site4 = rig.Engine.Store.Project.Stage.Site;
            await rig.Ok($"SCAN DROP {id} v2");
            var four = rig.Scanner.Find(id)!;
            SamePlace(StageMove.Between(site3, site4).Apply(three.Multi.Result.View("v1").Pose), four.Multi!.Result.View("v1").Pose, 1e-6, 1e-4);
            Assert.All(four.Captured.Where(v => v.State != "dropped"), v => Assert.Equal(site4, v.Site));

            // Not moved since: a solve of the same views would change nothing.
            var same = await rig.Run($"SCAN SOLVE {id}");
            Assert.False(same.Ok);
            Assert.StartsWith("nothing would change", same.Payload, StringComparison.Ordinal);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }

    /// <summary>A camera whose opening waits until its look is cancelled.</summary>
    private sealed class HeldCamera : IScanCamera
    {
        private readonly TaskCompletionSource _opening = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Opening => _opening.Task;

        public string Problem => "held";

        public async Task<bool> OpenAsync(CancellationToken ct)
        {
            _opening.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        }

        public Task<CameraFrame?> NextAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct) => Task.FromResult<CameraFrame?>(null);

        public void Dispose()
        {
            // Nothing is held.
        }
    }

    private sealed class DarkGlass : IScanDisplay
    {
        public Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct) =>
            Task.FromResult(Core.Time.MonoClock.Instance.NowNs);

        public void Release()
        {
            // Nothing is shown.
        }
    }

    private static Output ProjectorOutput(string id, string projectorId) =>
        new(id, id, OutputKind.Virtual, "", MultiViewScanKit.ProjectorWidth, MultiViewScanKit.ProjectorHeight, 60, OutputMode.Projector, projectorId,
            SourceRef.None, new RectD(0, 0, MultiViewScanKit.ProjectorWidth, MultiViewScanKit.ProjectorHeight), OutputWarp.None,
            BlendSettings.Default, ColorTrim.Neutral, [], new RectD(0, 0, 1, 1), 0, true);

    [Fact]
    public async Task AScanWaitsForACameraLookALookWaitsForAScanAndAFolderCameraIsNotOneALookTakes()
    {
        var camera = new HeldCamera();
        var p = CameraStage.With(MultiViewScanKit.Project());
        p = p with { Outputs = [ProjectorOutput("op00000001", Pj1), ProjectorOutput("op00000002", Pj2)] };
        p = p.WithStage(s => s with
        {
            Cameras = s.Cameras.Add(new CameraRig("cm_room", "Room camera", CameraSourceKind.Device, "room", CameraStage.CameraOptics, LensDistortion.None,
                CameraStage.CameraPose, true)),
        });
        var rig = new OpsRig(project: p, services: s => s with { WallCamera = _ => camera, WallDisplay = new DarkGlass() });
        try
        {
            // A camera re-solve claims its look: a scan is refused while it looks, and claims nothing.
            var look = rig.Run($"CALIBRATION RESOLVE {Pj1} CAMERA cm_room");
            await camera.Opening.WaitAsync(TimeSpan.FromSeconds(60));
            var refused = await rig.Run(Line());
            Assert.False(refused.Ok);
            Assert.Equal("a camera re-solve is looking through Room camera — the scan waits for it (CALIBRATION CANCEL stops it)", refused.Payload);
            Assert.False(rig.Scanner.Busy);
            Assert.Null(rig.Scanner.View);
            await rig.Ok("CALIBRATION CANCEL");
            Assert.False((await look).Ok);

            // The look over, the scan runs; while it runs a camera look is refused before it claims anything.
            var started = Json(await rig.Ok(Line()));
            var id = started.GetProperty("scan").GetString()!;
            var waits = await rig.Run($"CALIBRATION RESOLVE {Pj1} CAMERA cm_sim");
            Assert.False(waits.Ok);
            Assert.Equal("a scan is running — a re-solve waits for it (SCAN CANCEL)", waits.Payload);
            Assert.True(await OpsRig.Eventually(() => rig.Scanner.View is { IsRunning: false } v && v.Id == id, 300_000), "the scan did not end");
            Assert.Equal("done", rig.Scanner.View!.State);

            // A folder camera's pictures come one per pattern: a camera re-solve does not look through one.
            var edit = await rig.Engine.EditAsync(OpsRig.Desk, "folder camera", q => q.WithStage(s => s with
            {
                Cameras = s.Cameras.Add(new CameraRig("cm_folder", "Tether", CameraSourceKind.Folder, rig.Folder, CameraStage.CameraOptics, LensDistortion.None,
                    CameraStage.CameraPose, true)),
            }));
            Assert.True(edit.Changed);
            var folder = await rig.Run($"CALIBRATION RESOLVE {Pj1} CAMERA cm_folder");
            Assert.False(folder.Ok);
            Assert.StartsWith("Tether is a folder camera: its pictures come one per pattern", folder.Payload, StringComparison.Ordinal);
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
