using PlayMap.Core.Media;
using PlayMap.Core.Model;

namespace PlayMap.Engine;

/// <summary>What importing a file or URL found.</summary>
public sealed record MediaImport(MediaKind Kind, MediaInfo Info, string ContentHash, long SizeBytes, string Name);

/// <summary>Whether a library item can play on this node now.</summary>
public sealed record MediaReadiness(bool Present, bool Verified, bool Decodable, bool FirstFrameReady, string Words);

/// <summary>The media edge as the engine sees it: import (hash + probe), open sources, check readiness.
/// PlayMap.Media implements it; tests use a fake.</summary>
public interface IMediaBackend
{
    Task<MediaImport> ImportAsync(string location, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Opens a picture source for a library item (the caller owns and disposes it).</summary>
    IVideoSource? Open(MediaItem item);

    MediaReadiness Readiness(MediaItem item);

    /// <summary>The local file holding content with that hash, or null.</summary>
    string? PathForHash(string contentHash);

    /// <summary>The renderer's device for decoders opened for it (wp2-output makes it; wp2-decode's backend uses it); null when the
    /// render context goes.</summary>
    void UseGpu(IGpuShare? share)
    {
        // A backend that decodes on the CPU only takes no device.
    }

    /// <summary>What the renderer samples and its upload arena (wp2-decode).</summary>
    void UseRenderer(BlockFormats formats, IUploadArena? arena)
    {
        // A backend that decodes on the CPU only uploads through the renderer's own copy path.
    }
}

/// <summary>A display the platform reports.</summary>
public sealed record DisplayInfo(string Key, string Name, int X, int Y, int Width, int Height, double RefreshHz, bool Primary);

/// <summary>Per-output health for STATE: what the render loop measured.</summary>
public sealed record OutputHealth(string OutputId, bool Open, double Fps, long Dropped, double FrameMsP95, string Display);

/// <summary>The output edge: the displays, which outputs are open, their health. PlayMap.Render's output
/// windows implement it; headless nodes and tests use a fake.</summary>
public interface IOutputBackend
{
    IReadOnlyList<DisplayInfo> Displays();

    IReadOnlyList<OutputHealth> Health();

    string RendererName { get; }
}

/// <summary>What a scan asks of the outputs: one structured-light frame on one output, everything else the
/// show keeps, and the instant the frame was really presented so the camera knows which capture to keep.</summary>
public interface IScanDisplay
{
    /// <summary>Shows an 8-bit grey frame at the output's raster size and completes once it has been presented,
    /// with the monotonic instant of that present (ns). Replaces the previous scan frame on that output.</summary>
    Task<long> ShowAsync(string outputId, ReadOnlyMemory<byte> gray8, int width, int height, CancellationToken ct);

    /// <summary>An 8-bit RGB frame (interleaved) at the output's raster; completes at its present (ns) (wp2-align). A display that
    /// shows grey only refuses with words.</summary>
    Task<long> ShowRgbAsync(string outputId, ReadOnlyMemory<byte> rgb24, int width, int height, CancellationToken ct) =>
        Task.FromException<long>(new NotSupportedException("this display shows no colour frames"));

    /// <summary>Hands every output back to the show.</summary>
    void Release();
}

/// <summary>The render side as the host owns it: built before the engine (it is the engine's output backend
/// and the scan's display), started once the engine exists, disposed on the render thread it made.</summary>
public interface IRenderHost : IOutputBackend, IScanDisplay, IAsyncDisposable
{
    void Start(EngineCore engine);
}

/// <summary>A scan's progress for STATE.scan: its state, the projector being scanned, patterns shown of all, the decode quality in
/// percent (0–100) and its words.</summary>
public sealed record ScanProgress(string State, string? ProjectorId, int Pattern, int Of, double Quality, string Words);

/// <summary>The scan runner as the engine's verbs see it.</summary>
public interface IScanBackend
{
    ScanProgress? Current { get; }

    Task<string> StartAsync(string cameraId, IReadOnlyList<string> projectorIds, string mode, CancellationToken ct);

    bool Cancel();
}

/// <summary>A Show Bus peer as STATE and the context show it.</summary>
public sealed record PeerView(string Kind, string Id, string Name, string Light, string Headline, bool Live);

/// <summary>A link to Patterns or Stagewatch as STATE shows it: <c>State</c> live, stale, connecting or down, or held (the far end
/// refused this node's key, its hold runs and no connection is up); <c>Paired</c> when the far end took this node's proof on the
/// connection up now, or asks none; <c>Detail</c> the link's words, the far end's own while its refusal stands.</summary>
public sealed record LinkView(string State, string Host, bool Paired, string Detail);

/// <summary>The clock sync as STATE shows it.</summary>
public sealed record SyncView(string Role, string? Leader, double OffsetMs, double RttMs, double DriftPpm);

/// <summary>What the network edge tells the engine (read-only facts; the engine never reaches into sockets).</summary>
public interface INetworkView
{
    IReadOnlyList<PeerView> Peers();

    LinkView? Patterns();

    LinkView? Stagewatch();

    /// <summary>The Stagewatch timer as a compact JSON object (for STATE.links.stagewatch.timer), or null.</summary>
    string? StagewatchTimerJson();

    SyncView Sync();
}

/// <summary>Nothing connected: a headless node with no outputs, no scanner and no network yet.</summary>
public sealed class NullBackends : IOutputBackend, IScanBackend, INetworkView
{
    public static readonly NullBackends Instance = new();

    public string RendererName => "none";
    public IReadOnlyList<DisplayInfo> Displays() => [];
    public IReadOnlyList<OutputHealth> Health() => [];
    public ScanProgress? Current => null;

    public Task<string> StartAsync(string cameraId, IReadOnlyList<string> projectorIds, string mode, CancellationToken ct) =>
        Task.FromException<string>(new InvalidOperationException("no scanner on this node"));

    public bool Cancel() => false;
    public IReadOnlyList<PeerView> Peers() => [];
    public LinkView? Patterns() => null;
    public LinkView? Stagewatch() => null;
    public string? StagewatchTimerJson() => null;
    public SyncView Sync() => new("solo", null, 0, 0, 0);
}
