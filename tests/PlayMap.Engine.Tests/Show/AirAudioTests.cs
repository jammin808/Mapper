using System.Buffers.Binary;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Media.Audio;
using Xunit.Abstractions;

namespace PlayMap.Engine.Tests.Show;

/// <summary>Sound on the air through OpenAL Soft's loopback device, pulled block by block against the show's hand-moved clock: the
/// on-air item's tone plays slaved to its anchor; PANIC RUN FADE 1000 halves its level half way through the fade, on the same show
/// clock as the picture, and silences it at the end; PANIC CLEAR brings it back; REWIND cues the item at its top and the sound
/// stops with the transport.</summary>
[Collection("render host")]
public sealed class AirAudioTests(ITestOutputHelper log)
{
    private const int Block = AudioOut.BlockFrames;
    private const double BlockMs = Block * 1000.0 / FfmpegAudioDecoder.SampleRate;
    private static readonly Lazy<string> Tone = new(() => WriteTone(4));

    /// <summary>A 48 kHz stereo 16-bit WAV of a 1 kHz sine at half scale (48 samples a period: a block holds whole periods).</summary>
    private static string WriteTone(int seconds)
    {
        var frames = FfmpegAudioDecoder.SampleRate * seconds;
        var data = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var v = (short)Math.Round(Math.Sin(2 * Math.PI * i / 48.0) * 0.5 * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 4), v);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 4 + 2), v);
        }
        var path = Path.Combine(Directory.CreateTempSubdirectory("playmap-air-audio-").FullName, "tone.wav");
        using var f = File.Create(path);
        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], 36 + data.Length);
        "WAVEfmt "u8.CopyTo(h[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], FfmpegAudioDecoder.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], FfmpegAudioDecoder.SampleRate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], 4);
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], data.Length);
        f.Write(h);
        f.Write(data);
        return path;
    }

    private static Project ToneShow()
    {
        var p = ShowRig.OneScreen();
        var media = new MediaItem("md_tone", "Tone", MediaKind.Audio, Tone.Value, "", 0,
            MediaInfo.Unknown with { DurationNs = 4_000_000_000, AudioCodec = "pcm_s16le", AudioChannels = 2, AudioRate = 48_000 }, []);
        var item = PlaylistItem.For("it_tone", SourceRef.Media("md_tone"), "Tone");
        return p with { Media = [media], Playlists = [new Playlist("pl_show", "Show", [item, item with { Id = "it_after", Label = "After" }], false, null)] };
    }

    /// <summary>Pulls one block with the show clock moved on by the block's length; answers its RMS.</summary>
    private static double Pull(ShowRig rig, AudioOut output, float[] buffer)
    {
        rig.Advance(BlockMs);
        output.Render(buffer);
        var sum = 0.0;
        foreach (var x in buffer)
            sum += x * (double)x;
        return Math.Sqrt(sum / buffer.Length);
    }

    /// <summary>Pulls blocks until the show clock reaches <paramref name="atNs"/>; answers the RMS of the block after it, the first
    /// whose gain was read at that instant.</summary>
    private static double At(ShowRig rig, AudioOut output, float[] buffer, long atNs)
    {
        while (rig.Now + (long)(BlockMs * 1e6) <= atNs)
            Pull(rig, output, buffer);
        rig.Clock.NowNs = atNs - (long)(BlockMs * 1e6);
        Pull(rig, output, buffer);
        return Pull(rig, output, buffer);
    }

    [Fact]
    public async Task PanicFadesTheSoundOnTheShowClockAndClearBringsItBack()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Media.Ffmpeg.FfmpegLoader.EnvironmentVariable)))
            Environment.SetEnvironmentVariable(Media.Ffmpeg.FfmpegLoader.EnvironmentVariable, "/opt/ffmpeg9/lib");
        Media.Ffmpeg.FfmpegLoader.EnsureLoaded();
        await using var rig = new ShowRig(ToneShow());
        await using var sound = rig.Engine.StartAirAudio(AudioOut.OpenLoopback());
        var output = sound.Output;
        var buffer = new float[Block * FfmpegAudioDecoder.Channels];
        await rig.Ok("PLAYLIST GO pl_show");
        Assert.True(await ShowRig.Eventually(() => sound.ItemId == "it_tone"), sound.Problem);
        for (var i = 0; i < 20; i++)
            Pull(rig, output, buffer);
        var full = Pull(rig, output, buffer);
        log.WriteLine($"full {full:0.0000}");
        Assert.InRange(full, 0.35, 0.36);
        Assert.InRange(Math.Abs(output.LastErrorMs), 0, 10);
        Assert.Equal("it_tone", rig.Show().GetProperty("sound").GetProperty("item").GetString());

        var t = rig.Now;
        await rig.Ok("PANIC RUN FADE 1000");
        var half = At(rig, output, buffer, t + 500_000_000);
        log.WriteLine($"+500 ms {half:0.0000} = {half / full:0.0000} of full");
        Assert.InRange(half / full, 0.495, 0.505);
        // STATE says the air's gain at the clock's instant now (one block past half way).
        Assert.Equal(1 - (rig.Now - t) / 1e9, rig.Show().GetProperty("sound").GetProperty("gain").GetDouble(), 3);
        var end = At(rig, output, buffer, t + 1_000_000_000);
        Assert.True(end < full * 0.01, $"the fade's end read {end}");
        Assert.Equal(0, output.Resyncs);

        var c = rig.Now;
        await rig.Ok("PANIC CLEAR FADE 500");
        var back = At(rig, output, buffer, c + 500_000_000);
        log.WriteLine($"cleared {back:0.0000}");
        Assert.InRange(back / full, 0.99, 1.01);

        // REWIND: at the fade's end the item is cued at its top; its sound stops with its transport.
        var r = rig.Now;
        await rig.Ok("PANIC REWIND FADE 200");
        At(rig, output, buffer, r + 200_000_000);
        await rig.Ticked();
        var lane = rig.Engine.Playback.Lane("pl_show")!;
        Assert.Equal(("it_tone", TransportState.Cued), (lane.ItemId, lane.Clip.State));
        Assert.Equal(lane.Clip.InNs, lane.Clip.Anchor.MediaAt(rig.Now));
        Assert.True(Pull(rig, output, buffer) < 1e-4);
        Assert.True(Pull(rig, output, buffer) < 1e-4);

        // With the air's gain back at full the cued item stays silent: its transport, not the gain, holds the sound.
        await rig.Ok("PANIC CLEAR FADE 0");
        await rig.Ticked();
        Assert.Equal(1, rig.Show().GetProperty("sound").GetProperty("gain").GetDouble(), 3);
        Assert.Equal(TransportState.Cued, rig.Engine.Playback.Lane("pl_show")!.Clip.State);
        for (var i = 0; i < 4; i++)
            Assert.True(Pull(rig, output, buffer) < 1e-4, "a cued item sounded");
    }

    [Fact]
    public async Task TheItemsVolumeScalesTheSoundAndAFileWithNoSoundSaysWhy()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Media.Ffmpeg.FfmpegLoader.EnvironmentVariable)))
            Environment.SetEnvironmentVariable(Media.Ffmpeg.FfmpegLoader.EnvironmentVariable, "/opt/ffmpeg9/lib");
        Media.Ffmpeg.FfmpegLoader.EnsureLoaded();
        var broken = Path.Combine(Directory.CreateTempSubdirectory("playmap-air-audio-").FullName, "broken.wav");
        await File.WriteAllBytesAsync(broken, "not a sound file at all"u8.ToArray());
        var p = ToneShow();
        var list = p.Playlists[0];
        var bad = new MediaItem("md_bad", "Broken", MediaKind.Audio, broken, "", 0,
            MediaInfo.Unknown with { DurationNs = 4_000_000_000, AudioCodec = "pcm_s16le", AudioChannels = 2, AudioRate = 48_000 }, []);
        p = p with
        {
            Media = [.. p.Media, bad],
            Playlists = [list with { Items = [list.Items[0] with { Volume = 0.5 }, PlaylistItem.For("it_bad", SourceRef.Media("md_bad"), "Broken")] }],
        };
        await using var rig = new ShowRig(p);
        await using var sound = rig.Engine.StartAirAudio(AudioOut.OpenLoopback());
        var output = sound.Output;
        var buffer = new float[Block * FfmpegAudioDecoder.Channels];

        // A half-scale sine at the item's volume 0.5: RMS 0.5 × 0.5 / √2.
        await rig.Ok("PLAYLIST GO pl_show");
        Assert.True(await ShowRig.Eventually(() => sound.ItemId == "it_tone"), sound.Problem);
        for (var i = 0; i < 20; i++)
            Pull(rig, output, buffer);
        var level = Pull(rig, output, buffer);
        log.WriteLine($"volume 0.5 {level:0.0000}");
        Assert.InRange(level, 0.175, 0.179);

        // An item whose file holds no sound this node can play: silence, and STATE says why.
        await rig.Ok("PLAYLIST NEXT pl_show");
        Assert.True(await ShowRig.Eventually(() => sound.Problem.Length > 0), rig.Show().GetProperty("sound").ToString());
        // The problem and the silence land on the same tick: STATE never names the problem with the item before it still sounding.
        var said = rig.Show().GetProperty("sound");
        Assert.Contains("broken.wav", said.GetProperty("problem").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, said.GetProperty("item").ValueKind);
        Assert.Null(sound.ItemId);
        for (var i = 0; i < 4; i++)
            Assert.True(Pull(rig, output, buffer) < 1e-4, "the broken item sounded");
    }

    [Fact]
    public async Task WhenTheSourceDoesNotResolveTheSoundIsThePlayedVersions()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Media.Ffmpeg.FfmpegLoader.EnvironmentVariable)))
            Environment.SetEnvironmentVariable(Media.Ffmpeg.FfmpegLoader.EnvironmentVariable, "/opt/ffmpeg9/lib");
        Media.Ffmpeg.FfmpegLoader.EnsureLoaded();
        // The source is on a drive that is gone; its optimised version (with the same sound) is in this node's store.
        var sourceHash = "sha256:" + new string('a', 64);
        var versionHash = "sha256:" + new string('b', 64);
        var recipe = Core.Media.Optimise.Recipe.For("hap", EncodeQuality.Show);
        var info = MediaInfo.Unknown with { DurationNs = 4_000_000_000, AudioCodec = "pcm_s16le", AudioChannels = 2, AudioRate = 48_000 };
        var version = new MediaVersion("mv_tone", "hap", recipe.Canonical(), recipe.Key(sourceHash), versionHash, 1, info with { VideoCodec = "hap" },
            new VersionQuality(40, 39, 3, 0), "hapenc/1", "0badc0de", 1);
        var p = ToneShow();
        var source = p.Media[0] with { Location = "/nowhere/unmounted/tone.mov", ContentHash = sourceHash, Versions = [version] };
        await using var rig = new ShowRig(p with { Media = [source] }, s => s with { Media = new StoreOnly(versionHash, Tone.Value) });
        await using var sound = rig.Engine.StartAirAudio(AudioOut.OpenLoopback());
        var buffer = new float[Block * FfmpegAudioDecoder.Channels];
        await rig.Ok("PLAYLIST GO pl_show");
        Assert.True(await ShowRig.Eventually(() => sound.ItemId == "it_tone"), sound.Problem);
        for (var i = 0; i < 20; i++)
            Pull(rig, sound.Output, buffer);
        Assert.InRange(Pull(rig, sound.Output, buffer), 0.35, 0.36);
    }

    /// <summary>A media edge whose store holds one file, by its hash; nothing else resolves.</summary>
    private sealed class StoreOnly(string hash, string path) : IMediaBackend
    {
        public Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException("this test imports nothing");

        public Core.Media.IVideoSource? Open(MediaItem item) => null;

        public MediaReadiness Readiness(MediaItem item) => new(true, true, true, true, "ready");

        public string? PathForHash(string contentHash) => string.Equals(contentHash, hash, StringComparison.Ordinal) ? path : null;
    }
}
