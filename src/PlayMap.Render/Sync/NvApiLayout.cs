using System.Runtime.InteropServices;

namespace PlayMap.Render.Sync;

/// <summary>The NVAPI structures the NVIDIA framelock tier reads, laid out as the driver's header declares them (x64; plain data, so
/// their layout is checked on every platform, while the calls that use them are Windows-only). Each version word is
/// <c>sizeof(T) | (version &lt;&lt; 16)</c>; bit fields are carried as the 32-bit word that holds them.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct NvGSyncStatus(uint Version, uint IsSynced, uint IsStereoSynced, uint IsSyncSignalAvailable)
{
    public const uint Ver = 16 | (1 << 16);
}

/// <summary>NV_GSYNC_STATUS_PARAMS_V2: the refresh, both RJ45 ports' direction and whether each runs into an Ethernet hub (the
/// board's "erroneously connected" warning), house sync and its rate.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct NvGSyncStatusParams(uint Version, uint RefreshRate, int Rj45Io0, int Rj45Io1, uint Rj45Ethernet0, uint Rj45Ethernet1,
    uint HouseSyncIncoming, uint HouseSync, uint InternalSlaveBits)
{
    public const uint Ver = 36 | (2 << 16);
}

/// <summary>NV_GSYNC_CAPABILITIES_V3: the board id (856, 8288, 8289 named; others unknown), FPGA major and minor revision.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct NvGSyncCapabilities(uint Version, uint BoardId, uint Revision, uint CapFlags, uint ExtendedRevision, uint MulDivBits,
    uint MaxMulDivValue)
{
    public const uint Ver = 28 | (3 << 16);
}

/// <summary>NV_GSYNC_GPU: a GPU on the board (handles are pointers; the synced flag is bit 0 of the last word).</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct NvGSyncGpu(uint Version, nint PhysicalGpu, int Connector, nint ProxyPhysicalGpu, uint SyncedBits)
{
    public const uint Ver = 40 | (1 << 16);
}

/// <summary>NV_GSYNC_DISPLAY_V2: a display on the board and its sync state (0 unsynced, 1 client, 2 server).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NvGSyncDisplay : IEquatable<NvGSyncDisplay>
{
    public const uint Ver = 96 | (2 << 16);

    public uint Version;
    public uint DisplayId;
    public uint MasterableBits;
    public int SyncState;
    public fixed uint Reserved[20];

    public readonly bool Equals(NvGSyncDisplay other) =>
        Version == other.Version && DisplayId == other.DisplayId && MasterableBits == other.MasterableBits && SyncState == other.SyncState;

    public override readonly bool Equals(object? obj) => obj is NvGSyncDisplay d && Equals(d);

    public override readonly int GetHashCode() => HashCode.Combine(Version, DisplayId, MasterableBits, SyncState);

    public static bool operator ==(NvGSyncDisplay left, NvGSyncDisplay right) => left.Equals(right);

    public static bool operator !=(NvGSyncDisplay left, NvGSyncDisplay right) => !left.Equals(right);
}

/// <summary>NVAPI's entry: <c>nvapi_QueryInterface(id)</c> answers each function by its interface id (from the driver's MIT header),
/// called with the platform's C convention. Loaded from the user's NVIDIA driver (nvapi64.dll), never shipped.</summary>
public static class NvApiIds
{
    public const uint Initialize = 0x0150e828;
    public const uint Unload = 0xd22bdd7e;
    public const uint GSyncEnumSyncDevices = 0xd9639601;
    public const uint GSyncQueryCapabilities = 0x44a3f1d1;
    public const uint GSyncGetTopology = 0x4562bc38;
    public const uint GSyncGetSyncStatus = 0xf1f5b434;
    public const uint GSyncGetStatusParameters = 0x70d404ec;
    public const uint D3D1xPresent = 0x03b845a1;
    public const uint D3D1xQueryFrameCount = 0x9152e055;
    public const uint D3D1xQueryMaxSwapGroup = 0x9bb9d68f;
    public const uint D3D1xJoinSwapGroup = 0x14610cd7;
    public const uint D3D1xBindSwapBarrier = 0x9de8c729;

    public const int Ok = 0;
    public const int NvidiaDeviceNotFound = -6;
    public const int DeviceBusy = -108;
    public const int MaxGSyncDevices = 4;
    public const int DisplayUnsynced = 0;
}

