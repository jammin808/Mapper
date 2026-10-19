using Microsoft.Extensions.Logging;
using PlayMap.Core.Bus;
using PlayMap.Core.Time;

namespace PlayMap.Engine.Host;

/// <summary>What a render host is made from, handed to <see cref="EngineParts.RenderHost"/> after the media backend
/// and the network exist and before the engine does. The render host is the engine's output backend and the scan's
/// display; it is started with the engine once that exists.</summary>
public sealed record RenderHostRequest(
    EngineOptions Options,
    NodeHome Home,
    NodeSettings Settings,
    IMonoClock Mono,
    ShowWallClock Wall,
    IMediaBackend Media,
    ILoggerFactory Logs);

/// <summary>What a scan backend is made from: the display its structured-light frames go to (the render host, or —
/// on a node without one — a display that refuses with the reason) and the outputs it may ask for.</summary>
public sealed record ScanBackendRequest(
    IScanDisplay Display,
    IOutputBackend Outputs,
    NodeHome Home,
    NodeSettings Settings,
    IMonoClock Mono,
    ILoggerFactory Logs);

/// <summary>The pieces a node is composed of beyond the host's own (media, network, journal, recovery):
/// <list type="bullet">
/// <item><see cref="RenderHost"/> — answers the render host, or null when this machine has no GL or GL failed; the
/// node then runs with no outputs and every verb still answers. A factory that throws counts as null, and the words
/// become a fault in STATE.</item>
/// <item><see cref="ScanBackend"/> — the scan runner over the display; none means "no scanner on this node".</item>
/// <item><see cref="Extensions"/> — parts attached beside the engine (projector devices, the assistant), in order;
/// see <see cref="IEngineExtension"/>.</item>
/// </list>
/// The exe runs <see cref="Standard"/>: each side plugs its parts in through its own partial method in its own file
/// (<c>UseRenderHost</c>, <c>UseScanBackend</c>, <c>AddExtensions</c>), so no side edits another's line.</summary>
public sealed partial record EngineParts
{
    public Func<RenderHostRequest, IRenderHost?>? RenderHost { get; init; }

    public Func<ScanBackendRequest, IScanBackend>? ScanBackend { get; init; }

    public IReadOnlyList<EngineExtensionFactory> Extensions { get; init; } = [];

    /// <summary>No render host, no scanner, no extensions: the bare node (tests, a machine with nothing else).</summary>
    public static EngineParts Bare { get; } = new();

    /// <summary>The composition the exe runs: whatever the sides have plugged in, the bare node where none has.</summary>
    public static EngineParts Standard()
    {
        Func<RenderHostRequest, IRenderHost?>? render = null;
        Func<ScanBackendRequest, IScanBackend>? scan = null;
        var extensions = new List<EngineExtensionFactory>();
#pragma warning disable S3251 // The seam: a side implements its method in its own file; one not implemented is a part this exe does not have.
        UseRenderHost(ref render);
        UseScanBackend(ref scan);
        AddExtensions(extensions);
        // The round-2 packages' parts, in this order: an extension's Configure sees every earlier one's services (audio's capture
        // hub before control's timecode reader).
        AddShowParts(extensions); AddFleetParts(extensions); AddSyncParts(extensions); AddOutputParts(extensions); AddDecodeParts(extensions);
        AddAudioParts(extensions); AddControlParts(extensions); AddOptimiseParts(extensions); AddVideoIoParts(extensions);
        AddCompanionParts(extensions); AddFxParts(extensions); AddContentParts(extensions); AddLedParts(extensions);
        AddSiteParts(extensions); AddWallParts(extensions); AddAlignParts(extensions); AddPhoneParts(extensions);
        // Round 3's, by hook word (docs/round3/PLAN.md §2.3).
        AddMachineParts(extensions); AddPlanParts(extensions); AddObserveParts(extensions); AddPreviewParts(extensions);
        AddCaptureParts(extensions);
        return new EngineParts { RenderHost = render, ScanBackend = scan, Extensions = extensions };
    }

    /// <summary>The render side sets the factory of the exe's render host here (one implementation, in its own file).</summary>
    static partial void UseRenderHost(ref Func<RenderHostRequest, IRenderHost?>? factory);

    /// <summary>The scan side sets the factory of the exe's scan backend here (one implementation, in its own file).</summary>
    static partial void UseScanBackend(ref Func<ScanBackendRequest, IScanBackend>? factory);

    /// <summary>The side that owns the devices and the assistant adds their extensions here (one implementation, in its own
    /// file); they attach in the order added.</summary>
    static partial void AddExtensions(List<EngineExtensionFactory> extensions);
#pragma warning restore S3251

    // Each round-2 package adds its extensions in its own file (Host/EngineParts.<Word>.cs).
    static partial void AddShowParts(List<EngineExtensionFactory> extensions);
    static partial void AddFleetParts(List<EngineExtensionFactory> extensions);
    static partial void AddSyncParts(List<EngineExtensionFactory> extensions);
    static partial void AddOutputParts(List<EngineExtensionFactory> extensions);
    static partial void AddDecodeParts(List<EngineExtensionFactory> extensions);
    static partial void AddOptimiseParts(List<EngineExtensionFactory> extensions);
    static partial void AddVideoIoParts(List<EngineExtensionFactory> extensions);
    static partial void AddControlParts(List<EngineExtensionFactory> extensions);
    static partial void AddCompanionParts(List<EngineExtensionFactory> extensions);
    static partial void AddFxParts(List<EngineExtensionFactory> extensions);
    static partial void AddAudioParts(List<EngineExtensionFactory> extensions);
    static partial void AddContentParts(List<EngineExtensionFactory> extensions);
    static partial void AddLedParts(List<EngineExtensionFactory> extensions);
    static partial void AddSiteParts(List<EngineExtensionFactory> extensions);
    static partial void AddAlignParts(List<EngineExtensionFactory> extensions);
    static partial void AddPhoneParts(List<EngineExtensionFactory> extensions);
    static partial void AddWallParts(List<EngineExtensionFactory> extensions);

    // Each round-3 package with parts adds its extensions in its own file (Host/EngineParts.<Word>.cs).
    static partial void AddMachineParts(List<EngineExtensionFactory> extensions);
    static partial void AddPlanParts(List<EngineExtensionFactory> extensions);
    static partial void AddObserveParts(List<EngineExtensionFactory> extensions);
    static partial void AddPreviewParts(List<EngineExtensionFactory> extensions);
    static partial void AddCaptureParts(List<EngineExtensionFactory> extensions);
}
