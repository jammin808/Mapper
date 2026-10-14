using System.Text.Json;
using PlayMap.Core.Audio;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Core.Wire;

namespace PlayMap.Engine.Tests.Audio;

/// <summary>A device the test plays into: each open is counted, each capture is pushed by hand with the instant it was "read".</summary>
internal sealed class FakeAudioInputs(int channels = 8) : IAudioInputs
{
    public const string Key = "fake:desk";

    public int Opens { get; private set; }

    public List<FakeCapture> Captures { get; } = [];

    public IReadOnlyList<AudioInputInfo> Inputs() => [new AudioInputInfo(Key, "Desk mixer", channels, [48000], "fake") { IsDefault = true }];

    public IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink)
    {
        if (key != Key)
            throw new AudioUnavailableException($"no fake device {key}");
        Opens++;
        var c = new FakeCapture(sink, channels);
        Captures.Add(c);
        return c;
    }
}

internal sealed class FakeCapture(IAudioCaptureSink sink, int channels) : IAudioCapture
{
    private long _index;

    public bool Disposed { get; private set; }

    public int SampleRate => 48000;

    public int Channels => channels;

    /// <summary>Delivers mono samples on every channel (channel c scaled by <paramref name="gains"/>[c] when given), read at
    /// <paramref name="readMonoNs"/>.</summary>
    public void Push(ReadOnlySpan<float> mono, long readMonoNs, float[]? gains = null)
    {
        var block = new float[mono.Length * channels];
        for (var i = 0; i < mono.Length; i++)
            for (var c = 0; c < channels; c++)
                block[i * channels + c] = mono[i] * (gains is null ? 1 : gains[c]);
        sink.Samples(block, channels, 48000, _index, readMonoNs);
        _index += mono.Length;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>An engine on a hand-moved clock with the audio helpers the audio tests use.</summary>
internal sealed class AudioRig : IAsyncDisposable
{
    public static readonly SessionInfo Desk = new("t1", "desk", "127.0.0.1", true, "PlayMapDesk", true);
    public const long StartNs = 5_000_000_000;

    public readonly ManualClock Clock = new(StartNs);
    public readonly EngineCore Engine;
    public readonly FakeAudioInputs Fake = new();

    public AudioRig(Project? project = null)
    {
        Engine = new EngineCore(EngineServices.ForTests(Clock), project);
        Engine.AudioRt.AddBackend("fake:", Fake);
    }

    /// <summary>A show with a canvas holding one layer at opacity 0.2, a surface-less stage and a Julia generator.</summary>
    public static Project Show()
    {
        var layer = new Layer("ly_base", "Base", SourceRef.Solid("#ffffff"), new RectD(0, 0, 1920, 1080), 0.2, LayerBlend.Normal, true);
        var canvas = new Canvas("cv_main", "Main", 1920, 1080, "#000000", [layer]);
        var gen = new Generator("gn_julia", "Fractal", "fractal.julia", ParamSet.Empty);
        return Project.CreateDefault("Audio show") with { Canvases = [canvas], Generators = [gen] };
    }

    public async Task<CommandReply> Run(string line, SessionInfo? session = null)
    {
        Assert.True(WireGrammar.TryParse(line, out var parsed, out var error), error?.Reason);
        return await Engine.HandleAsync(parsed, session ?? Desk, CancellationToken.None);
    }

    public async Task<CommandReply> Ok(string line)
    {
        var r = await Run(line);
        Assert.True(r.Ok, $"{line} → {r.Payload}");
        return r;
    }

    public async Task<string> Refused(string line)
    {
        var r = await Run(line);
        Assert.False(r.Ok, $"{line} should be refused; it answered {r.Effect} {r.Payload}");
        return r.Payload ?? "";
    }

    public static JsonElement Json(CommandReply r) => JsonDocument.Parse(r.Payload!).RootElement;

    public JsonElement State() => JsonDocument.Parse(Engine.State().Json).RootElement;

    public void AdvanceMs(double ms) => Clock.AdvanceMs(ms);

    public ValueTask DisposeAsync()
    {
        Engine.AudioRt.Dispose();
        return Engine.DisposeAsync();
    }
}

/// <summary>Sound for the fake device: silence, then a 60 Hz burst from a given sample on.</summary>
internal static class Sounds
{
    public static float[] BassBurst(int samples, int burstFrom, float amplitude = 0.8f)
    {
        var s = new float[samples];
        for (var i = burstFrom; i < samples; i++)
            s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * 60 * (i - burstFrom) / 48000.0));
        return s;
    }
}
