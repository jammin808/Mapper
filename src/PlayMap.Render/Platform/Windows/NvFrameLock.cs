using System.Globalization;
using System.Runtime.Versioning;
using PlayMap.Core.Presentation;
using PlayMap.Core.Sync;
using PlayMap.Render.Sync;

namespace PlayMap.Render.Platform.Windows;

/// <summary>The NVIDIA tier: the sync board's facts through NVAPI's GSync calls (board id and firmware, synced, signal, house sync,
/// a cable into a switch, whether a display is its server or client). Swap groups act on a DXGI swap chain (NVAPI's D3D1x calls on
/// the device and swap chain an output presents through); a GL window has none, so a join on one is refused with words until the
/// DXGI path presents it. Windows only; the driver's own DLL, loaded from the user's install.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class NvFrameLock : IFrameLockDriver
{
    private readonly NvApi _api;
    private readonly Lock _gate = new();
    private bool _disposed;

    private NvFrameLock(NvApi api)
    {
        _api = api;
    }

    /// <summary>The tier this machine has: NVAPI with its board, or <see cref="NoFrameLock"/> with the words why not ("no NVIDIA
    /// driver on this machine", "NVAPI is Windows only").</summary>
    public static IFrameLockDriver Create()
    {
        var api = NvApi.TryLoad(out var words);
        return api is null ? new NoFrameLock(words) : new NvFrameLock(api);
    }

    public string Name => "nvapi";

    public string Words => "";

    /// <summary>D3D11 swap groups need the DXGI present path: none on a GL window.</summary>
    public int MaxSwapGroups => 0;

    public FrameLockBoard? Board()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var handles = stackalloc nint[NvApiIds.MaxGSyncDevices];
            uint count = 0;
            if (_api.EnumSyncDevices(handles, &count) != NvApiIds.Ok || count == 0)
                return null;
            var device = handles[0];
            var caps = new NvGSyncCapabilities { Version = NvGSyncCapabilities.Ver };
            var boardId = _api.QueryCapabilities(device, &caps) == NvApiIds.Ok ? (int)caps.BoardId : 0;
            var firmware = string.Create(CultureInfo.InvariantCulture, $"{caps.Revision}.{caps.ExtendedRevision}");
            var parameters = new NvGSyncStatusParams { Version = NvGSyncStatusParams.Ver };
            var hasParameters = _api.GetStatusParameters(device, &parameters) == NvApiIds.Ok;
            var (synced, signal, configured) = Topology(device);
            return new FrameLockBoard("nvidia", boardId, SyncBoards.Name(boardId), firmware, synced, signal,
                hasParameters && parameters.HouseSync != 0, hasParameters ? parameters.HouseSyncIncoming : 0,
                hasParameters && (parameters.Rj45Ethernet0 != 0 || parameters.Rj45Ethernet1 != 0), configured, "nvapi");
        }
    }

    /// <summary>Synced and signal from the first GPU on the board; configured when a display on it is the board's server or client.</summary>
    private (bool Synced, bool Signal, bool Configured) Topology(nint device)
    {
        if (_api.GetTopology is null)
            return (false, false, false);
        uint gpuCount = 0, displayCount = 0;
        if (_api.GetTopology(device, &gpuCount, null, &displayCount, null) != NvApiIds.Ok || gpuCount == 0)
            return (false, false, false);
        gpuCount = Math.Min(gpuCount, 8);
        displayCount = Math.Min(displayCount, 32);
        var gpus = stackalloc NvGSyncGpu[(int)gpuCount];
        var displays = stackalloc NvGSyncDisplay[(int)Math.Max(1, displayCount)];
        for (var i = 0; i < gpuCount; i++)
            gpus[i].Version = NvGSyncGpu.Ver;
        for (var i = 0; i < displayCount; i++)
            displays[i].Version = NvGSyncDisplay.Ver;
        if (_api.GetTopology(device, &gpuCount, gpus, &displayCount, displayCount > 0 ? displays : null) != NvApiIds.Ok)
            return (false, false, false);
        var status = new NvGSyncStatus { Version = NvGSyncStatus.Ver };
        var ok = _api.GetSyncStatus(device, gpus[0].PhysicalGpu, &status) == NvApiIds.Ok;
        var configured = false;
        for (var i = 0; i < displayCount; i++)
            configured |= displays[i].SyncState != NvApiIds.DisplayUnsynced;
        return (ok && status.IsSynced != 0, ok && status.IsSyncSignalAvailable != 0, configured);
    }

    public bool Join(string outputId, IOutputPresenter? presenter, uint group, uint barrier, out string words)
    {
        words = presenter?.Path == PresentPath.DxgiFlip
            ? "this build joins no DXGI swap chain to a swap group yet"
            : "NVAPI swap groups act on a DXGI swap chain; this output presents through GL — it runs free, measured";
        return false;
    }

    public void Leave(string outputId, IOutputPresenter? presenter)
    {
        // Nothing joined (see Join).
    }

    public bool BarrierBound(string outputId) => false;

    public bool TryFrameCount(string outputId, IOutputPresenter? presenter, out uint count24)
    {
        count24 = 0;
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _api.Dispose();
        }
    }
}
