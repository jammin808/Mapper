namespace PlayMap.Core.Audio;

/// <summary>What an audio input is: a device, the system's own sound, one process's sound, a file, or a test signal.</summary>
public enum AudioInputKind { Capture, Loopback, ProcessLoopback, File, Synthetic }

/// <summary>A sound input of this machine as its backend names it.</summary>
public sealed record AudioInputInfo(string Key, string Name, int Channels, IReadOnlyList<int> Rates, string Backend)
{
    public AudioInputKind Kind { get; init; } = AudioInputKind.Capture;
    public bool IsDefault { get; init; }
}

/// <summary>Receives capture blocks on the capture thread: never block (copy into a ring and return).</summary>
public interface IAudioCaptureSink
{
    void Samples(ReadOnlySpan<float> interleaved, int channels, int sampleRate, long firstSampleIndex, long readMonoNs);
    void Faulted(string words);
}

/// <summary>An open capture; disposing it stops the samples.</summary>
public interface IAudioCapture : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
}

/// <summary>The sound inputs of this machine; Open throws AudioUnavailableException with words when the input cannot open.</summary>
public interface IAudioInputs
{
    IReadOnlyList<AudioInputInfo> Inputs();
    IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink);
}

/// <summary>An audio input that cannot open, with the words why.</summary>
public sealed class AudioUnavailableException(string message) : Exception(message);
