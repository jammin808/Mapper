using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// A view the joint solve leaves out (its camera saw none of the light) is left out again by the same solve: SCAN SOLVE of the same
/// views is refused as changing nothing — the scan, its record and the suggestions waiting on it stay as they are — and says which view
/// was left out and why.
/// </summary>
public sealed class LeftOutViewTests
{
    private const string Pj1 = MultiViewScanKit.Pj1;
    private const string Pj2 = MultiViewScanKit.Pj2;

    private static string Line(string? into = null) =>
        $"SCAN START {MultiViewScanKit.Camera} {Pj1} {Pj2}" + (into is null ? "" : " INTO " + into);

    [Fact]
    public async Task ASolveOfTheSameViewsWithOneLeftOutChangesNothingAndIsRefused()
    {
        var rig = new OpsRig(new AssistantSettings { Enabled = true }, project: MultiViewScanKit.Project(), scan: new ScanRunnerOptions
        {
            // The second view's camera stands far above the scene looking out over it: it sees none of the light.
            SimulatedNudge = (view, _, camera) => view == "v2"
                ? camera with { Pose = Pose.LookAt(new Vec3d(0, 50, 0), new Vec3d(0, 50, -10), Vec3d.UnitY) }
                : camera,
        });
        try
        {
            var id = await MultiViewScanKit.ScanAsync(rig, Line());
            Assert.Equal(id, await MultiViewScanKit.ScanAsync(rig, Line(id)));
            var two = rig.Scanner.Find(id)!;
            var v2 = two.Captured.Single(v => v.Id == "v2");
            Assert.Equal("left out", v2.State);
            Assert.NotEmpty(v2.Words);
            Assert.Equal(["v1"], two.Multi!.ViewIds.ToArray());
            Assert.Contains(two.Multi.Result.LeftOut, l => l.ViewId == "v2");

            var suggested = JsonDocument.Parse(await rig.Ok("SCAN SUGGEST " + id)).RootElement.GetProperty("suggestions").EnumerateArray()
                .SelectMany(s => s.GetProperty("proposals").EnumerateArray().Select(p => p.GetString()!)).ToList();
            Assert.NotEmpty(suggested);
            var record = rig.Engine.Store.Project.Stage.Scans.Single();

            // Solved again from the same views, v2 is left out again and v1 solves as it did: refused, nothing touched.
            var again = await rig.Run("SCAN SOLVE " + id);
            Assert.False(again.Ok);
            Assert.StartsWith("nothing would change", again.Payload, StringComparison.Ordinal);
            Assert.Contains("v2", again.Payload, StringComparison.Ordinal);
            Assert.Same(two, rig.Scanner.Find(id));
            Assert.Equal(record, rig.Engine.Store.Project.Stage.Scans.Single());
            Assert.All(suggested, p => Assert.Equal(ProposalState.Pending, rig.Assistant.Queue.Find(p)!.State));
        }
        finally
        {
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
