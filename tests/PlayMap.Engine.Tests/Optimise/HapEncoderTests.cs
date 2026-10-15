using PlayMap.Core.Media.Hap;
using PlayMap.Core.Media.Optimise;
using PlayMap.Core.Model;
using PlayMap.Engine.Tests.Host;
using PlayMap.Media.Optimise;
using PlayMap.Render.Encode;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Optimise;

/// <summary>The GL HAP encoder on llvmpipe, measured: FFmpeg's testsrc2 made in the test, encoded by our programs and by FFmpeg's
/// own hap encoder, and both files decoded by the same decoder (FFmpeg's) — our PSNR over RGB is within 0.5 dB of FFmpeg's on
/// the clean pattern for HAP and HAP Q; on the noisy pattern HAP reaches 30 dB and HAP Q 36 dB; HAP Alpha and HAP Q Alpha decode
/// in FFmpeg with their alpha; and two runs write the same bytes.</summary>
[Collection("render host")]
public sealed class HapEncoderTests(ITestOutputHelper log) : IDisposable
{
    private const int W = 960;
    private const int H = 540;
    private const int Frames = 6;
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("playmap-hapenc-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Ours(GlThread gl, string source, HapCodec codec, string name, EncodeQuality quality = EncodeQuality.Show)
    {
        var output = Path.Combine(_dir.FullName, name);
        var result = gl.Run(() =>
        {
            using var encoder = new GlHapEncoder(gl.Context);
            using var blocks = new HapBlocks();
            var packer = new HapPacker();
            var recipe = Recipe.For(OptimiseTargets.Word(codec), quality);
            return HapTranscode.Run(new HapTranscodeRequest(source, output, codec) { CopyAudio = false }, (ReadOnlySpan<byte> rgba, int w, int h, ref byte[] packet) =>
            {
                encoder.Encode(rgba, w, h, codec, quality, blocks);
                return packer.Pack(blocks, recipe.ChunksFor(w), recipe.SecondStage, recipe.RawBelowGain, ref packet);
            }, null, null, CancellationToken.None);
        });
        Assert.Equal((result.Frames, W, H), (result.Parsed, result.Width, result.Height));
        log.WriteLine($"{name}: sampled {result.Quality.Sampled}, avg {result.Quality.PsnrAvg} dB, min {result.Quality.PsnrMin} dB");
        return output;
    }

    [FfmpegFact]
    public void OurHapAndHapQAreWithinHalfADecibelOfFfmpegsOwnEncoderOnTheCleanPattern()
    {
        var source = OptimiseClips.TestSource(_dir.FullName, W, H, Frames, noisy: false);
        var reference = OptimiseClips.Rgba(source);
        using var gl = new GlThread();
        foreach (var (codec, format) in new[] { (HapCodec.Hap, "hap"), (HapCodec.HapQ, "hap_q") })
        {
            var ours = OptimiseClips.AveragePsnr(OptimiseClips.Rgba(Ours(gl, source, codec, format + "-ours.mov")), reference, W, H);
            var theirs = OptimiseClips.AveragePsnr(OptimiseClips.Rgba(OptimiseClips.FfmpegHap(source, format, Path.Combine(_dir.FullName, format + "-ffmpeg.mov"))), reference, W, H);
            log.WriteLine($"{format}: ours {ours:0.00} dB, FFmpeg's {theirs:0.00} dB");
            Assert.True(ours >= theirs - 0.5, $"{format}: ours {ours:0.00} dB, FFmpeg's {theirs:0.00} dB");
        }
    }

    [FfmpegFact]
    public void OnTheNoisyPatternHapReachesThirtyAndHapQThirtySixDecibels()
    {
        var source = OptimiseClips.TestSource(_dir.FullName, W, H, Frames, noisy: true);
        var reference = OptimiseClips.Rgba(source);
        using var gl = new GlThread();
        var hap = OptimiseClips.AveragePsnr(OptimiseClips.Rgba(Ours(gl, source, HapCodec.Hap, "noisy-hap.mov")), reference, W, H);
        var hapQ = OptimiseClips.AveragePsnr(OptimiseClips.Rgba(Ours(gl, source, HapCodec.HapQ, "noisy-hapq.mov")), reference, W, H);
        var theirs = OptimiseClips.AveragePsnr(OptimiseClips.Rgba(OptimiseClips.FfmpegHap(source, "hap", Path.Combine(_dir.FullName, "noisy-ffmpeg.mov"))), reference, W, H);
        var theirsQ = OptimiseClips.AveragePsnr(OptimiseClips.Rgba(OptimiseClips.FfmpegHap(source, "hap_q", Path.Combine(_dir.FullName, "noisy-ffmpeg-q.mov"))), reference, W, H);
        log.WriteLine($"noisy: HAP {hap:0.00} dB (FFmpeg's {theirs:0.00}), HAP Q {hapQ:0.00} dB (FFmpeg's {theirsQ:0.00})");
        Assert.True(hap >= 30, $"HAP {hap:0.00} dB");
        Assert.True(hapQ >= 36, $"HAP Q {hapQ:0.00} dB");
    }

    [FfmpegFact]
    public void TheAlphaCodecsDecodeInFfmpegWithTheirAlphaAndTwoRunsAreBitIdentical()
    {
        // A pattern whose alpha is a ramp across the picture.
        var source = Path.Combine(_dir.FullName, "alpha.nut");
        OptimiseClips.Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=s={W}x{H}:r=25", "-frames:v", "3",
            "-vf", "format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='X*255/W'", "-c:v", "rawvideo", source);
        var reference = OptimiseClips.Rgba(source);
        using var gl = new GlThread();
        foreach (var codec in new[] { HapCodec.HapAlpha, HapCodec.HapQAlpha })
        {
            var file = Ours(gl, source, codec, codec + ".mov");
            var tag = OptimiseClips.Streams(file)[0].Tag;
            Assert.Equal(HapCodecs.TagText(codec), tag);
            var back = OptimiseClips.Rgba(file);
            Assert.True(OptimiseClips.AveragePsnr(back, reference, W, H) >= 30);
            // Alpha within two steps of the ramp, everywhere.
            var worst = 0;
            for (var i = 3; i < back.Length; i += 4)
                worst = Math.Max(worst, Math.Abs(back[i] - reference[i]));
            Assert.True(worst <= 2, $"{codec}: alpha off by {worst}");
        }
        var a = File.ReadAllBytes(Ours(gl, source, HapCodec.HapQ, "run-a.mov", EncodeQuality.Fast));
        var b = File.ReadAllBytes(Ours(gl, source, HapCodec.HapQ, "run-b.mov", EncodeQuality.Fast));
        Assert.Equal(a, b);
    }
}
