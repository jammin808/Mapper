using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;

namespace PlayMap.Core.Tests.Media;

/// <summary>The decode-path ladder as pure rules (zerocopy.md §3.4): the rung a clip opens on from the policy, the renderer's share
/// and the proof; every run-time demotion with its words; the starvation watch's count over its window.</summary>
public sealed class DecodeLadderTests
{
    private static readonly ZeroCopyPlatform Linux = new(GpuApi.DmaBuf, true, true, "");
    private static readonly ZeroCopyPlatform Windows = new(GpuApi.D3D11, true, false, "");
    private static readonly ZeroCopyPlatform WindowsProven = new(GpuApi.D3D11, true, true, "");
    private static readonly ZeroCopyPlatform Software = new(GpuApi.DmaBuf, false, true, "the GL is software (llvmpipe): zero-copy needs a GPU");

    [Theory]
    [InlineData("auto", DecodePolicy.Auto)]
    [InlineData("DIRECT", DecodePolicy.Direct)]
    [InlineData("gpu-copy", DecodePolicy.GpuCopy)]
    [InlineData("Copy-Back", DecodePolicy.CopyBack)]
    [InlineData(" software ", DecodePolicy.Software)]
    public void PolicyWordsReadEitherCaseAndWriteBackTheirWord(string word, DecodePolicy expected)
    {
        Assert.True(DecodeLadder.TryParse(word, out var policy));
        Assert.Equal(expected, policy);
        Assert.Equal(word.Trim().ToLowerInvariant(), DecodeLadder.Word(policy));
    }

    [Theory]
    [InlineData("")]
    [InlineData("fast")]
    [InlineData("gpu_copy")]
    public void AnyOtherPolicyWordIsRefused(string word) => Assert.False(DecodeLadder.TryParse(word, out _));

    [Fact]
    public void PathWordsAndRanksFollowTheLadder()
    {
        Assert.Equal(["hw-direct", "hw-gpu-copy", "hap-gpu", "gpu-share", "hw-copy-back", "software"],
            Enum.GetValues<DecodePath>().Select(DecodeLadder.Word));
        Assert.Equal([1, 2, 0, 0, 3, 4], Enum.GetValues<DecodePath>().Select(DecodeLadder.Rank));
    }

    [Fact]
    public void LinuxOpensDirectOnVaapiAndHasNoOneCopyRung()
    {
        Assert.Equal(new LadderChoice(DecodePath.HwDirect, "vaapi", ""), DecodeLadder.Choose(DecodePolicy.Auto, Linux));
        Assert.Equal(new LadderChoice(DecodePath.HwDirect, "vaapi", ""), DecodeLadder.Choose(DecodePolicy.Direct, Linux));
        var gpuCopy = DecodeLadder.Choose(DecodePolicy.GpuCopy, Linux);
        Assert.Equal(DecodePath.HwCopyBack, gpuCopy.Ceiling);
        Assert.Contains("VAAPI has no one-copy path", gpuCopy.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsTakesOneGpuCopyUntilProvenOrForced()
    {
        var unproven = DecodeLadder.Choose(DecodePolicy.Auto, Windows);
        Assert.Equal((DecodePath.HwGpuCopy, "d3d11va"), (unproven.Ceiling, unproven.Device));
        Assert.Contains("not proven", unproven.Why, StringComparison.Ordinal);
        Assert.Equal(DecodePath.HwGpuCopy, DecodeLadder.Choose(DecodePolicy.Direct, Windows).Ceiling);
        Assert.Equal(DecodePath.HwDirect, DecodeLadder.Choose(DecodePolicy.Direct, Windows, forced: true).Ceiling);
        Assert.Equal(DecodePath.HwDirect, DecodeLadder.Choose(DecodePolicy.Auto, WindowsProven).Ceiling);
        Assert.Equal(DecodePath.HwGpuCopy, DecodeLadder.Choose(DecodePolicy.GpuCopy, WindowsProven).Ceiling);
    }

    [Fact]
    public void APlatformThatIsNotReadyCopiesBackWithItsOwnWords()
    {
        var c = DecodeLadder.Choose(DecodePolicy.Auto, Software);
        Assert.Equal(DecodePath.HwCopyBack, c.Ceiling);
        Assert.Equal(Software.Words, c.Why);
        var none = DecodeLadder.Choose(DecodePolicy.Direct, ZeroCopyPlatform.None);
        Assert.Equal((DecodePath.HwCopyBack, ZeroCopyPlatform.None.Words), (none.Ceiling, none.Why));
        var gl = DecodeLadder.Choose(DecodePolicy.Auto, new ZeroCopyPlatform(GpuApi.OpenGl, true, false, ""));
        Assert.Equal(DecodePath.HwCopyBack, gl.Ceiling);
        Assert.Contains("dmabuf (EGL) or D3D11", gl.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePolicyCapsTheRungWhateverThePlatform()
    {
        foreach (var platform in new[] { Linux, Windows, WindowsProven, Software })
        {
            Assert.Equal(new LadderChoice(DecodePath.Software, "", "decode.policy is software"), DecodeLadder.Choose(DecodePolicy.Software, platform));
            Assert.Equal(DecodePath.HwCopyBack, DecodeLadder.Choose(DecodePolicy.CopyBack, platform).Ceiling);
        }
    }

    [Theory]
    [InlineData(DecodePath.HwDirect, DemotionTrigger.ImportFailed, DecodePath.HwCopyBack, "could not import a frame: EGL_BAD_MATCH")]
    [InlineData(DecodePath.HwGpuCopy, DemotionTrigger.ImportFailed, DecodePath.HwCopyBack, "could not import a frame: EGL_BAD_MATCH")]
    [InlineData(DecodePath.HwDirect, DemotionTrigger.NotImportable, DecodePath.HwCopyBack, "first frame's layout does not import here: EGL_BAD_MATCH")]
    [InlineData(DecodePath.HwDirect, DemotionTrigger.Starved, DecodePath.HwCopyBack, "waited over 3 frames for a free surface 3 times in 10 s")]
    [InlineData(DecodePath.HwDirect, DemotionTrigger.ProofFailed, DecodePath.HwGpuCopy, "proof failed: EGL_BAD_MATCH; one GPU copy")]
    [InlineData(DecodePath.HwCopyBack, DemotionTrigger.HandedBack, DecodePath.Software, "handed this stream back to software: EGL_BAD_MATCH")]
    [InlineData(DecodePath.HwDirect, DemotionTrigger.DeviceLost, DecodePath.HwCopyBack, "the renderer's device changed: EGL_BAD_MATCH; frames come back")]
    public void EveryDemotionStepsDownWithItsWords(DecodePath from, DemotionTrigger trigger, DecodePath to, string words)
    {
        var (next, why) = DecodeLadder.Demote(from, trigger, "EGL_BAD_MATCH");
        Assert.Equal(to, next);
        Assert.Contains(words, why, StringComparison.Ordinal);
        Assert.True(DecodeLadder.Rank(next) >= DecodeLadder.Rank(from), "the ladder never steps up");
    }

    [Theory]
    [InlineData(DecodePath.HwCopyBack, DemotionTrigger.ImportFailed)]
    [InlineData(DecodePath.Software, DemotionTrigger.Starved)]
    [InlineData(DecodePath.HwGpuCopy, DemotionTrigger.ProofFailed)]
    [InlineData(DecodePath.Software, DemotionTrigger.HandedBack)]
    [InlineData(DecodePath.HapGpu, DemotionTrigger.NotImportable)]
    [InlineData(DecodePath.HwCopyBack, DemotionTrigger.DeviceLost)]
    public void ATriggerThatDoesNotApplyToTheRungChangesNothing(DecodePath from, DemotionTrigger trigger) =>
        Assert.Equal((from, ""), DecodeLadder.Demote(from, trigger, "x"));

    [Fact]
    public void ThreeStarvedWaitsInsideTenSecondsTripTheWatch()
    {
        const long frame = 16_666_667;
        var watch = new StarvationWatch();
        Assert.False(watch.Waited(3 * frame, frame, 0), "three frame periods is not over three");
        Assert.Equal(0, watch.Recent);
        Assert.False(watch.Waited(3 * frame + 1, frame, 0));
        Assert.False(watch.Waited(4 * frame, frame, 4_000_000_000));
        Assert.Equal(2, watch.Recent);
        Assert.True(watch.Waited(5 * frame, frame, 9_000_000_000));
        Assert.Equal(3, watch.Recent);
    }

    [Fact]
    public void WaitsOlderThanTheWindowAreForgotten()
    {
        const long frame = 40_000_000;
        var watch = new StarvationWatch();
        Assert.False(watch.Waited(10 * frame, frame, 0));
        Assert.False(watch.Waited(10 * frame, frame, 6_000_000_000));
        Assert.False(watch.Waited(10 * frame, frame, 11_000_000_000), "the first wait is 11 s old");
        Assert.Equal(2, watch.Recent);
        Assert.True(watch.Waited(10 * frame, frame, 12_000_000_000));
        watch.Reset();
        Assert.Equal(0, watch.Recent);
        Assert.False(watch.Waited(10 * frame, frame, 12_000_000_001));
    }

    [Fact]
    public void AWatchWithoutAFramePeriodNeverTrips()
    {
        var watch = new StarvationWatch();
        for (var i = 0; i < 10; i++)
            Assert.False(watch.Waited(long.MaxValue / 2, 0, i));
    }
}
