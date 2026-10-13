namespace PlayMap.Core.Modulation;

/// <summary>How a source's value is made: a pure function of show time, shared through the group, or live on this node.</summary>
public enum SourceDeterminism { Pure, Shared, Live }

/// <summary>Modulation sources read on the render thread: never block, never allocate. A source the node lacks answers false
/// (its binding holds the base value and STATE names it).</summary>
public interface IModulationBus
{
    bool TryRead(string source, long leaderNs, out double value01);
    bool IsLive(string source);
    /// <summary>A hot-path slot for a source name (−1 = unknown); TryRead(slot) then reads without a lookup.</summary>
    int Resolve(string source) => -1;
    bool TryRead(int slot, long leaderNs, out double value01) { value01 = 0.5; return false; }
    /// <summary>A raw value (bpm, dB, counts).</summary>
    bool TryReadRaw(string source, long leaderNs, out double value) { value = 0; return false; }
    SourceDeterminism Determinism(string source) => IsLive(source) ? SourceDeterminism.Live : SourceDeterminism.Pure;
    /// <summary>Called once per frame before any read, with the frame's leader instant.</summary>
    void BeginFrame(long leaderNs) { }
}

/// <summary>Sound for ISF audio textures and FFGL buffers.</summary>
public interface IAudioFrames
{
    int Channels { get; }
    bool TryWave(long leaderNs, int samples, Span<float> interleaved);
    bool TrySpectrum(long leaderNs, int bins, Span<float> magnitudes01);
}

/// <summary>No sources at all (a node without the audio package; effects' built-ins are wp2-fx's BuiltinModulation).</summary>
public sealed class NoModulation : IModulationBus
{
    public static readonly NoModulation Instance = new();
    public bool TryRead(string source, long leaderNs, out double value01) { value01 = 0.5; return false; }
    public bool IsLive(string source) => false;
}
