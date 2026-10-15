using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Phone;
using PlayMap.Engine.Scanning;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Phone;

/// <summary>
/// The phone as a scan camera, end to end over its real socket: SCAN START through the phone's camera rig drives the lock-step
/// capture — each pattern goes up only when the phone has said it saw the one before — and late pictures, a rolling-shutter mix,
/// a dropped upload and a phone that lies about a picture all end with every pattern paired to its own picture (the projector is
/// found within the real-camera tolerance of the truth; STATE.scan.pairing counts what was caught). A phone that hangs up or goes
/// silent puts its words in STATE and the scan stops cleanly with the outputs given back; one whose motion sensor fires, or that is
/// nudged two pixels, fails the view with words.
/// </summary>
public sealed class PhoneCameraTests(ITestOutputHelper output)
{
    /// <summary>OpsRenderScanTests' tolerance for one projector against one calibrated camera: 1 cm, 0.25°, 0.6 % of throw.</summary>
    private const double PositionToleranceM = 0.01;

    private const double RotationToleranceDeg = 0.25;

    private const double ThrowTolerance = 0.006;

    private void AssertFound(PhoneScanRig rig, ScanView view)
    {
        var record = Assert.Single(rig.Engine.Store.Project.Stage.Scans);
        Assert.Equal(view.Id, record.Id);
        var solved = Assert.Single(rig.Runner.Find(record.Id)!.Solution.Projectors);
        Assert.True(solved.Solved, solved.Words);
        var truth = rig.Truth.Projector(0);
        var (position, degrees) = ScanRunner.PoseError(solved.Pose, truth.Pose);
        var throwError = Math.Abs(solved.Intrinsics.ThrowRatio / truth.Intrinsics.ThrowRatio - 1);
        output.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{position * 1000:0.0} mm, {degrees:0.000}°, throw {throwError * 100:0.00} %, {solved.RmsPx:0.000} px; {record.Notes}"));
        Assert.True(position < PositionToleranceM, $"{position * 1000:0.0} mm");
        Assert.True(degrees < RotationToleranceDeg, $"{degrees:0.000}°");
        Assert.True(throwError < ThrowTolerance, $"throw {throwError * 100:0.00} %");
    }

    [Fact]
    public async Task ALockStepScanPacedBySeenPairsEveryPatternDespiteLateMixedDroppedAndFalsePictures()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        await using var phone = await rig.JoinAsync();
        // Pattern indices: late pictures on three, a rolling-shutter mix on one, a dropped upload on one, a lie on one.
        phone.Late.UnionWith([3, 10, 20]);
        phone.Half.Add(12);
        phone.DropUpload.Add(15);
        phone.Lie.Add(25);

        var started = await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE ROBUST");
        Assert.True(started.Ok, started.Payload);
        var view = await rig.EndedAsync();
        Assert.True(view.State == "done", view.Message);
        AssertFound(rig, view);

        var pairing = rig.State().GetProperty("scan").GetProperty("pairing");
        output.WriteLine(pairing.ToString());
        Assert.Equal("tag", pairing.GetProperty("mode").GetString());
        Assert.InRange(pairing.GetProperty("sites").GetInt32(), 2, 3);
        Assert.True(pairing.GetProperty("rollingRejects").GetInt32() >= 1);
        Assert.True(pairing.GetProperty("retakes").GetInt32() >= 2, "the dropped upload and the false picture were shot again");
        Assert.Equal(1, pairing.GetProperty("caught").GetInt32());
        Assert.Equal("locked", pairing.GetProperty("exposure").GetString());
        Assert.Equal(0, pairing.GetProperty("timingDisagrees").GetInt32());
        Assert.True(phone.RollingSeen >= 1);
        // Lock-step: every picture after the probe went up only once the phone had answered the cue before it (show s, counted from
        // 1 with the probe first, goes with cue s − 1).
        var answered = rig.Display.AnsweredAtShow;
        for (var i = 1; i < answered.Count; i++)
            Assert.True(answered[i] >= i - 1, $"show {i + 1} went up with {answered[i]} cues answered");
        Assert.True(rig.Display.Released >= 1, "the outputs were given back");
        var record = rig.Engine.Store.Project.Stage.Scans[0];
        Assert.Equal(phone.Rig, record.CameraId);
        Assert.Contains("paired by tag", rig.Runner.View!.Pairing!.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APhoneWhoseCaptureTimesComeBeforeTheirPatternsIsCountedAndTheTagsStillPair()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        await using var phone = await rig.JoinAsync();
        // Its clock exchange was true; then it says every picture was taken half a second before it was.
        phone.CaptureSkewMs = -500;
        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE FAST")).Ok);
        var view = await rig.EndedAsync();
        Assert.True(view.State == "done", view.Message);
        AssertFound(rig, view);
        var pairing = rig.State().GetProperty("scan").GetProperty("pairing");
        Assert.Equal("tag", pairing.GetProperty("mode").GetString());
        // Every pattern's picture claims a capture before its present: each is counted, none is trusted over its tag.
        Assert.True(pairing.GetProperty("timingDisagrees").GetInt32() > 3, pairing.ToString());
        Assert.Contains("the phone's clock or latency is off — pairing by tags only", rig.Engine.Store.Project.Stage.Scans[0].Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASteadyPhoneWhoseBrightnessWandersIsScannedWithoutFullWhiteOrBlack()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        await using var phone = await rig.JoinAsync(locks: false, gainWalk: 0.01);
        Assert.Equal("steady", rig.Engine.Store.Project.Stage.Cameras.Find(phone.Rig)!.Phone!.Exposure);
        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE ROBUST")).Ok);
        var view = await rig.EndedAsync();
        Assert.True(view.State == "done", view.Message);
        AssertFound(rig, view);
        var pairing = rig.State().GetProperty("scan").GetProperty("pairing");
        Assert.Equal("steady", pairing.GetProperty("exposure").GetString());
        Assert.True(pairing.GetProperty("aeDriftPct").GetDouble() > 0);
        // No full white or full black ever went up (tagged or not): the camera had nothing to ramp on.
        Assert.True(rig.Display.Fullest < 0.9, $"a picture {rig.Display.Fullest:P0} at one extreme went up");
    }

    [Fact]
    public async Task APhoneThatHangsUpPutsItsWordsInStateAndTheScanStopsCleanly()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        await using var phone = await rig.JoinAsync();
        phone.HangUpAfter = 20;
        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE ROBUST")).Ok);
        var view = await rig.EndedAsync();
        Assert.Equal("failed", view.State);
        Assert.Contains("Pixel 8", view.Message, StringComparison.Ordinal);
        Assert.Contains("said goodbye", view.Message, StringComparison.Ordinal);
        Assert.True(rig.Display.Released >= 1, "the outputs were given back");
        Assert.Empty(rig.Engine.Store.Project.Stage.Scans);

        var state = rig.State();
        Assert.Equal("failed", state.GetProperty("scan").GetProperty("state").GetString());
        var p = Assert.Single(state.GetProperty("phones").EnumerateArray());
        Assert.False(p.GetProperty("connected").GetBoolean());
        Assert.Contains("said goodbye", p.GetProperty("words").GetString()!, StringComparison.Ordinal);
        using var context = JsonDocument.Parse(rig.Engine.Context());
        var item = context.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "phone");
        Assert.Equal("grey", item.GetProperty("light").GetString());
    }

    [Fact]
    public async Task APhoneThatGoesSilentHasItsPatternShownAgainThenTheScanFailsWithWords()
    {
        await using var rig = await PhoneScanRig.StartAsync(new ScanRunnerOptions { PatternTimeout = TimeSpan.FromSeconds(2), MaxRetakes = 1 });
        await using var phone = await rig.JoinAsync();
        phone.SilentAfter = 18;
        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE ROBUST")).Ok);
        var view = await rig.EndedAsync();
        Assert.Equal("failed", view.State);
        Assert.Contains("said nothing for 2 s", view.Message, StringComparison.Ordinal);
        // The pattern it fell silent on was cued once more before the view gave up.
        Assert.Equal(20, phone.Cues);
        Assert.True(rig.Display.Released >= 1);
        Assert.True(Assert.Single(rig.State().GetProperty("phones").EnumerateArray()).GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task ScanNextShowsTheWaitedPatternAgainAndIsRefusedWhenNothingWaits()
    {
        await using var rig = await PhoneScanRig.StartAsync(new ScanRunnerOptions { PatternTimeout = TimeSpan.FromSeconds(30) });
        var refused = await rig.Run("SCAN NEXT");
        Assert.False(refused.Ok);
        Assert.Equal("no scan is waiting for a picture", refused.Payload);
        await using var phone = await rig.JoinAsync();
        phone.SilentAfter = 3;
        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE ROBUST")).Ok);
        Assert.True(await OpsRig.Eventually(() => phone.Cues == 4 && rig.Runner.WaitingForPicture, 20_000));
        var shown = rig.Display.Shown;
        // The phone answers again; SCAN NEXT puts the waited pattern up again at once — long before its 30 s would have — and the scan
        // goes on with no pattern shown again for a timeout.
        phone.SilentAfter = int.MaxValue;
        await rig.Ok("SCAN NEXT");
        Assert.True(await OpsRig.Eventually(() => phone.Cues >= 5, 10_000), "SCAN NEXT asked for the pattern again");
        Assert.True(rig.Display.Shown > shown);
        var view = await rig.EndedAsync();
        Assert.True(view.State == "done", view.Message);
        Assert.Equal(0, rig.State().GetProperty("scan").GetProperty("pairing").GetProperty("retakes").GetInt32());
        Assert.Contains(rig.Engine.Journal.Recent(200), r => r.Text == "asked: the waited pattern goes up again for the camera");
    }

    [Fact]
    public async Task APhoneWhoseMotionSensorFiresFailsTheViewWithWords()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        await using var phone = await rig.JoinAsync();
        phone.MovedAtCue = 20;
        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE ROBUST")).Ok);
        var view = await rig.EndedAsync();
        Assert.Equal("failed", view.State);
        Assert.Contains("Pixel 8 moved during the view (its motion sensor said so)", view.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Engine.Store.Project.Stage.Scans);
        Assert.Equal("moved", Assert.Single(rig.State().GetProperty("phones").EnumerateArray()).GetProperty("motion").GetString());
    }

    [Fact]
    public async Task AMovementStartingBetweenCuesFailsTheViewToo()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        await using var phone = await rig.JoinAsync();
        // The sensor fires after a seen, while no cue is in hand: the page reports the movement starting, and that counts.
        phone.BumpAfterCue = 20;
        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE ROBUST")).Ok);
        var view = await rig.EndedAsync();
        Assert.Equal("failed", view.State);
        Assert.Contains("Pixel 8 moved during the view (its motion sensor said so)", view.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMovementBetweenTwoProjectorsRunsFailsTheScan()
    {
        await using var rig = await PhoneScanRig.StartAsync(projectors: 2);
        await using var phone = await rig.JoinAsync();
        // The first projector's view ends whole; the phone is bumped while the second projector's probe is up.
        phone.BumpAtPrepare = 2;
        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 pj00000002 MODE FAST")).Ok);
        var view = await rig.EndedAsync();
        Assert.Equal("failed", view.State);
        Assert.Contains("Pixel 8 moved during the view (its motion sensor said so)", view.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Engine.Store.Project.Stage.Scans);
    }

    [Fact]
    public async Task ALostLocateOrClosingPictureIsTakenAgainAsARetakeWithoutAStall()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        await using var phone = await rig.JoinAsync();
        phone.DropLocateUpload.Add(5);
        phone.DropClosingUpload = true;
        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE FAST")).Ok);
        var view = await rig.EndedAsync();
        Assert.True(view.State == "done", view.Message);
        // Each frame asked for again was seen at once against the picture before it: no stuck, one retake each.
        Assert.Equal(0, phone.Stucks);
        Assert.Equal(2, rig.State().GetProperty("scan").GetProperty("pairing").GetProperty("retakes").GetInt32());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APhoneNudgedTwoPixelsDuringTheViewIsCaughtByTheClosingCheck(bool locks)
    {
        await using var rig = await PhoneScanRig.StartAsync();
        await using var phone = await rig.JoinAsync(locks: locks);
        // After the locate run and some patterns the camera stands two pixels over: every tag still reads, so only the end check sees it.
        phone.ShiftAfter = CellLocate.Frames + 10;
        phone.ShiftPx = 2;
        Assert.True((await rig.Run($"SCAN START {phone.Rig} pj00000001 MODE ROBUST")).Ok);
        var view = await rig.EndedAsync();
        Assert.Equal("failed", view.State);
        output.WriteLine(view.Message);
        Assert.Contains("Pixel 8 moved during the view: its tag places stand 2.", view.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Engine.Store.Project.Stage.Scans);
    }

    [Fact]
    public async Task APhoneNotConnectedDoesNotOpenAndSaysHowToConnectIt()
    {
        await using var rig = await PhoneScanRig.StartAsync();
        var phone = await rig.JoinAsync();
        var rigId = phone.Rig;
        await phone.DisposeAsync();
        Assert.True(await OpsRig.Eventually(() => !rig.Hub.Phones()[0].Connected));
        Assert.True((await rig.Run($"SCAN START {rigId} pj00000001")).Ok);
        var view = await rig.EndedAsync();
        Assert.Equal("failed", view.State);
        Assert.Contains("Pixel 8 did not open: Pixel 8 is not connected — open the invite on the phone", view.Message, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(rig.Engine.State().Json);
        Assert.Equal("failed", doc.RootElement.GetProperty("scan").GetProperty("state").GetString());
    }
}
