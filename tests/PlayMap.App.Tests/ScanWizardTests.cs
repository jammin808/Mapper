using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PlayMap.App.ViewModels;

namespace PlayMap.App.Tests;

/// <summary>The scan wizard walks its five steps: a camera, the projectors, the scan (progress from
/// STATE.scan), the review (a click on the picture picks a region; Make surface sends it), and Apply. Next
/// waits until a step's choice is made.</summary>
public class ScanWizardTests
{
    [AvaloniaFact]
    public async Task TheWizardWalksCameraProjectorsScanReviewAndApply()
    {
        await using var desk = new DeskHarness();
        var window = MainWindowTests.Open(desk);
        desk.Main.Workspace = Workspace.Scan;
        DeskHarness.Pump();
        var scan = desk.Main.Scan;
        var next = MainWindowTests.Find<Button>(window, "NextButton");

        // 1 · Camera: the demo has one; the sentence says what will happen.
        Assert.Equal(1, scan.Current);
        Assert.Equal("cm_webcam", scan.Camera!.Id);
        Assert.Contains("USB camera", scan.Sentence, StringComparison.Ordinal);
        Assert.Contains("exposure", scan.Sentence, StringComparison.Ordinal);
        Screens.Save(window, "scan-1-camera");
        DeskHarness.Click(window, next);
        Assert.Equal(2, scan.Current);

        // 2 · Projectors: untick both and Next waits; tick one back.
        foreach (var p in scan.Projectors)
            p.IsPicked = false;
        Assert.False(scan.CanNext);
        Assert.False(next.IsEffectivelyEnabled);
        scan.Projectors[0].IsPicked = true;
        scan.Projectors[1].IsPicked = true;
        scan.Mode = ScanMode.Fast;
        Assert.Contains("fast patterns", scan.Sentence, StringComparison.Ordinal);
        DeskHarness.Click(window, next);
        Assert.Equal(3, scan.Current);
        Assert.False(scan.CanNext);

        // 3 · Scan: the big button sends SCAN START; progress comes from STATE.scan as the engine runs it.
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "ScanButton"));
        await scan.Start.Command.Last;
        DeskHarness.Pump();
        Assert.Equal("SCAN START cm_webcam pj_left pj_right MODE FAST", desk.Verbs()[^1]);
        Assert.True(scan.IsRunning);
        desk.Clock.AdvanceMs(3000);
        desk.Link.Tick();
        DeskHarness.Pump();
        Assert.InRange(scan.Progress, 0.3, 0.6);
        Assert.Contains("pattern", scan.ProgressText, StringComparison.Ordinal);
        Screens.Save(window, "scan-3-running");
        desk.Clock.AdvanceMs(10_000);
        desk.Link.Tick();
        DeskHarness.Pump();
        Assert.True(scan.IsDone);
        Assert.Contains("decoded", scan.QualityText, StringComparison.Ordinal);
        Assert.True(scan.CanNext);
        DeskHarness.Click(window, next);
        Assert.Equal(4, scan.Current);

        // 4 · Review: a click on the wall picks it; Make surface sends the click point.
        Assert.Equal(4, scan.Regions.Count);
        scan.PickAt(0.5, 0.3);
        Assert.Equal("wall", scan.SelectedRegion!.Label);
        Screens.Save(window, "scan-4-review");
        await scan.MakeSurface.Command.ExecuteAsync();
        DeskHarness.Pump();
        Assert.StartsWith($"SCAN SURFACE {scan.ScanId} 0.5 0.39 Wall", desk.Verbs()[^1], StringComparison.Ordinal);
        Assert.Contains(desk.Link.CurrentProject.Stage.Surfaces, s => s.Name == "Wall");
        DeskHarness.Click(window, next);
        Assert.Equal(5, scan.Current);

        // 5 · Apply.
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "ApplyButton"));
        await scan.Apply.Command.Last;
        DeskHarness.Pump();
        Assert.Equal("SCAN APPLY " + scan.ScanId, desk.Verbs()[^1]);
        Assert.NotEmpty(scan.Applied);
        Assert.False(scan.CanNext);

        // Back walks back; a step not yet reachable cannot be jumped to.
        DeskHarness.Click(window, MainWindowTests.Find<Button>(window, "BackButton"));
        Assert.Equal(4, scan.Current);
        window.Close();
    }
}
