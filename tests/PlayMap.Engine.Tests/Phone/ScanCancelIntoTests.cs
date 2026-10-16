using System.Text.Json;
using PlayMap.Assistant;
using PlayMap.Engine.Scanning;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// A view INTO a scan, as STATE shows it and as SCAN CANCEL ends it: while the view is taken STATE.scan is the scan with the views and
/// regions it has (the view being taken named apart); cancelled mid-view, the scan is as it was — its views, its solve in memory and on
/// disk, its record and the suggestions waiting on it — and STATE still shows its views and regions.
/// </summary>
public sealed class ScanCancelIntoTests
{
    private const string Pj1 = MultiViewScanKit.Pj1;
    private const string Pj2 = MultiViewScanKit.Pj2;

    private static string Line(string? into = null) =>
        $"SCAN START {MultiViewScanKit.Camera} {Pj1} {Pj2}" + (into is null ? "" : " INTO " + into);

    private static string[] ViewIds(JsonElement scan) => [.. scan.GetProperty("views").EnumerateArray().Select(v => v.GetProperty("id").GetString()!)];

    private static string[] RegionIds(JsonElement scan) => [.. scan.GetProperty("regions").EnumerateArray().Select(r => r.GetProperty("id").GetString()!)];

    [Fact]
    public async Task WhileAViewIsTakenStateShowsTheScansViewsAndCancelledMidViewTheScanIsAsItWas()
    {
        using var hold = new ManualResetEventSlim(true);
        var inView = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new OpsRig(new AssistantSettings { Enabled = true }, project: MultiViewScanKit.Project(), scan: new ScanRunnerOptions
        {
            // The second view is held at its second projector (the first one's patterns taken) until the test lets it go.
            SimulatedNudge = (view, projector, camera) =>
            {
                if (view == "v2" && projector == Pj2 && inView.TrySetResult())
                    hold.Wait(TimeSpan.FromMinutes(2));
                return camera;
            },
        });
        try
        {
            var id = await MultiViewScanKit.ScanAsync(rig, Line());
            var before = rig.Scanner.Find(id)!;
            var regions = before.Regions.Select(r => r.Id).ToArray();
            Assert.NotEmpty(regions);
            var proposals = JsonDocument.Parse(await rig.Ok("SCAN SUGGEST " + id)).RootElement.GetProperty("suggestions").EnumerateArray()
                .SelectMany(s => s.GetProperty("proposals").EnumerateArray().Select(p => p.GetString()!)).ToList();
            Assert.NotEmpty(proposals);
            var record = rig.Engine.Store.Project.Stage.Scans.Single();
            var folder = Path.Combine(rig.Folder, "scans", id);

            hold.Reset();
            await rig.Ok(Line(id));
            await inView.Task.WaitAsync(TimeSpan.FromMinutes(2));

            // While v2 is taken STATE.scan is the scan with what it has: v1 and its regions; v2 is the view being taken.
            var running = rig.State().GetProperty("scan");
            Assert.Equal(id, running.GetProperty("id").GetString());
            Assert.Equal("running", running.GetProperty("state").GetString());
            Assert.Equal("v2", running.GetProperty("view").GetString());
            Assert.Equal(["v1"], ViewIds(running));
            Assert.Equal(regions, RegionIds(running));

            // Cancelled mid-view: the scan is as it was.
            await rig.Ok("SCAN CANCEL");
            hold.Set();
            Assert.True(await OpsRig.Eventually(() => rig.Scanner.View is { IsRunning: false } v && v.Id == id, 120_000), "the view did not end");
            var ended = rig.State().GetProperty("scan");
            Assert.Equal("cancelled", ended.GetProperty("state").GetString());
            Assert.Equal(["v1"], ViewIds(ended));
            Assert.Equal(regions, RegionIds(ended));
            Assert.Same(before, rig.Scanner.Find(id));
            Assert.Equal(record, rig.Engine.Store.Project.Stage.Scans.Single());
            Assert.All(proposals, p => Assert.Equal(ProposalState.Pending, rig.Assistant.Queue.Find(p)!.State));
            Assert.False(Directory.Exists(Path.Combine(folder, "views", "v2")));
            Assert.False(File.Exists(Path.Combine(folder, "multiview.json")));
            Assert.False(rig.Scanner.Busy);
        }
        finally
        {
            hold.Set();
            await rig.DisposeAsync();
            rig.DeleteFolder();
        }
    }
}
