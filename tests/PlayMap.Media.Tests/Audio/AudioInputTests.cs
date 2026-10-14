using System.Runtime.Versioning;
using PlayMap.Core.Audio;
using PlayMap.Media.Audio;
using PlayMap.Media.Audio.Windows;

namespace PlayMap.Media.Tests.Audio;

/// <summary>The sound inputs this edge provides: a sound file as a looped input, FFmpeg's capture devices (none on this machine, said
/// in words), and WASAPI on Windows — enumerated on the CI runner, which has no endpoint, as an empty list with words and no throw.</summary>
public sealed class AudioInputTests
{
    /// <summary>Counts what it is given.</summary>
    private sealed class Counter : IAudioCaptureSink
    {
        private long _frames;

        public long Frames => Interlocked.Read(ref _frames);

        public int Channels;
        public int Rate;
        public int Faults;

        public void Samples(ReadOnlySpan<float> interleaved, int channels, int sampleRate, long firstSampleIndex, long readMonoNs)
        {
            Channels = channels;
            Rate = sampleRate;
            Interlocked.Add(ref _frames, interleaved.Length / channels);
        }

        public void Faulted(string words) => Interlocked.Increment(ref Faults);
    }

    [Fact]
    public async Task ASoundFileIsAnInputThatLoopsAtThePaceOfTheClock()
    {
        TestMedia.Ensure();
        var inputs = new FileAudioInputs();
        Assert.Empty(inputs.Inputs());
        var sink = new Counter();
        using (var capture = inputs.Open("file:" + TestMedia.Clip("tone48.wav"), 48000, sink))
        {
            Assert.Equal(48000, capture.SampleRate);
            Assert.Equal(2, capture.Channels);
            // The 2 s file runs past its end and starts again: 2.5 s of the clock deliver more frames than the file holds.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (sink.Frames < 110_000 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
        }
        Assert.True(sink.Frames >= 110_000, $"{sink.Frames} frames");
        Assert.Equal(2, sink.Channels);
        Assert.Equal(48000, sink.Rate);
        Assert.Equal(0, sink.Faults);
        Assert.Throws<AudioUnavailableException>(() => inputs.Open("file:/no/such/file.wav", 48000, sink));
        Assert.Throws<AudioUnavailableException>(() => inputs.Open("pulse:default", 48000, sink));
    }

    [Fact]
    public void FfmpegListsNoCaptureDeviceHereAndSaysWhy()
    {
        TestMedia.Ensure();
        var ffmpeg = new FfmpegAudioInputs();
        var list = ffmpeg.Inputs();
        if (list.Count == 0)
            Assert.False(string.IsNullOrWhiteSpace(ffmpeg.Words), "an empty list comes with words");
        else
            Assert.All(list, i => Assert.Equal("ffmpeg", i.Backend));
        var e = Assert.Throws<AudioUnavailableException>(() => ffmpeg.Open(FfmpegAudioInputs.PlatformFormats[0] + ":no-such-device-here", 48000, new Counter()));
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
    }

    /// <summary>WASAPI on Windows: enumeration answers a list (empty on the runner, which has no endpoint, with words) and never
    /// throws; a key that is not WASAPI's is refused with words. Elsewhere the type is Windows only and no backend makes it.</summary>
    [Fact]
    [Trait("Category", "Windows")]
    public void WasapiEnumeratesWithWordsAndNeverThrows()
    {
        if (!OperatingSystem.IsWindows())
        {
            var platform = typeof(WasapiAudioInputs).GetCustomAttributes(typeof(SupportedOSPlatformAttribute), false)
                .Cast<SupportedOSPlatformAttribute>().Select(a => a.PlatformName);
            Assert.Contains("windows", platform);
            return;
        }
        Enumerate();
    }

    [SupportedOSPlatform("windows")]
    private static void Enumerate()
    {
        var wasapi = new WasapiAudioInputs();
        var list = wasapi.Inputs();
        if (list.Count == 0)
            Assert.False(string.IsNullOrWhiteSpace(wasapi.Words), "an empty list comes with words");
        Assert.All(list, i => Assert.StartsWith(WasapiAudioInputs.Prefix, i.Key, StringComparison.Ordinal));
        var e = Assert.Throws<AudioUnavailableException>(() => wasapi.Open("wasapi:elsewhere", 48000, new Counter()));
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
    }
}
