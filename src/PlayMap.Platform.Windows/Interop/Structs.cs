using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlayMap.Platform.Windows.Interop;

/// <summary>PROCESS_POWER_THROTTLING_STATE and THREAD_POWER_THROTTLING_STATE (the same three ULONGs): the version, the bits the caller
/// controls, and their state (a bit in the control mask and clear in the state mask asks for HighQoS; set in both, EcoQoS).</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential)]
internal record struct PowerThrottlingState(uint Version, uint ControlMask, uint StateMask);

/// <summary>A LUID: its low and high parts.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential)]
internal record struct Luid(uint LowPart, int HighPart)
{
    public readonly long Value => ((long)HighPart << 32) | LowPart;
}

/// <summary>LUID_AND_ATTRIBUTES (12 bytes, 4-aligned).</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct LuidAndAttributes(Luid Luid, uint Attributes);

/// <summary>TOKEN_PRIVILEGES with one privilege: its count and the privilege.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TokenPrivilege(uint Count, LuidAndAttributes Privilege);

/// <summary>OSVERSIONINFOEXW: the size, major, minor, build, platform, the service pack's string (WCHAR[128] at 20), its numbers, the
/// suite mask and the product type.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 284)]
internal record struct OsVersionInfoEx(
    [field: FieldOffset(0)] uint Size,
    [field: FieldOffset(4)] uint Major,
    [field: FieldOffset(8)] uint Minor,
    [field: FieldOffset(12)] uint Build,
    [field: FieldOffset(16)] uint PlatformId,
    [field: FieldOffset(276)] ushort ServicePackMajor,
    [field: FieldOffset(278)] ushort ServicePackMinor,
    [field: FieldOffset(280)] ushort SuiteMask,
    [field: FieldOffset(282)] byte ProductType)
{
    public const int CsdVersionOffset = 20;
}

/// <summary>SYSTEM_CPU_SET_INFORMATION's CPU set: the record's size and type, the set's id, group, logical processor, core, cache and
/// NUMA indices, efficiency class, flags, scheduling class and allocation tag.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal record struct SystemCpuSetInformation(
    [field: FieldOffset(0)] uint Size,
    [field: FieldOffset(4)] uint Type,
    [field: FieldOffset(8)] uint Id,
    [field: FieldOffset(12)] ushort Group,
    [field: FieldOffset(14)] byte LogicalProcessorIndex,
    [field: FieldOffset(15)] byte CoreIndex,
    [field: FieldOffset(16)] byte LastLevelCacheIndex,
    [field: FieldOffset(17)] byte NumaNodeIndex,
    [field: FieldOffset(18)] byte EfficiencyClass,
    [field: FieldOffset(19)] byte AllFlags,
    [field: FieldOffset(20)] uint SchedulingClass,
    [field: FieldOffset(24)] ulong AllocationTag);

/// <summary>REASON_CONTEXT with a simple reason string: the version, the flags and the string.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal record struct ReasonContext(
    [field: FieldOffset(0)] uint Version,
    [field: FieldOffset(4)] uint Flags,
    [field: FieldOffset(8)] nint SimpleReasonString);

/// <summary>DXGI_ADAPTER_DESC1: Description (WCHAR[128]) at 0, the vendor and device ids, the memory sizes, the LUID and the flags.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 312)]
internal record struct DxgiAdapterDesc1(
    [field: FieldOffset(256)] uint VendorId,
    [field: FieldOffset(260)] uint DeviceId,
    [field: FieldOffset(264)] uint SubSysId,
    [field: FieldOffset(268)] uint Revision,
    [field: FieldOffset(272)] nuint DedicatedVideoMemory,
    [field: FieldOffset(280)] nuint DedicatedSystemMemory,
    [field: FieldOffset(288)] nuint SharedSystemMemory,
    [field: FieldOffset(296)] uint LuidLow,
    [field: FieldOffset(300)] int LuidHigh,
    [field: FieldOffset(304)] uint Flags)
{
    public const int DescriptionChars = 128;

    /// <summary>DXGI_ADAPTER_FLAG_SOFTWARE.</summary>
    public const uint SoftwareFlag = 0x2;
}

/// <summary>D3DKMT_OPENADAPTERFROMLUID (d3dkmthk.h): the adapter's LUID in, its kernel handle out.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential)]
internal record struct KmtOpenAdapterFromLuid(Luid AdapterLuid, uint Adapter);

/// <summary>D3DKMT_QUERYADAPTERINFO (d3dkmthk.h): the handle, the query's type, and the caller's buffer and its size.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal record struct KmtQueryAdapterInfo(
    [field: FieldOffset(0)] uint Adapter,
    [field: FieldOffset(4)] int Type,
    [field: FieldOffset(8)] nint PrivateDriverData,
    [field: FieldOffset(16)] uint PrivateDriverDataSize);

/// <summary>D3DKMT_CLOSEADAPTER (d3dkmthk.h): the handle.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential)]
internal record struct KmtCloseAdapter(uint Adapter);

/// <summary>D3DKMT_WDDM_2_7_CAPS (d3dkmthk.h): one UINT of flags — HwSchSupported (bit 0), HwSchEnabled (bit 1),
/// HwSchEnabledByDefault (bit 2), IndependentVidPnVSyncControl (bit 3).</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential)]
internal record struct KmtWddm27Caps(uint Value)
{
    public readonly bool HwSchSupported => (Value & 0x1) != 0;

    public readonly bool HwSchEnabled => (Value & 0x2) != 0;
}

/// <summary>SYSTEM_CODEINTEGRITY_INFORMATION (winternl.h): its length (set by the caller) and the code integrity options.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential)]
internal record struct CodeIntegrityInformation(uint Length, uint CodeIntegrityOptions);

/// <summary>NVAPI's NvAPI_ShortString: 64 bytes of a NUL-terminated string.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential, Size = Length)]
internal record struct NvShortString(byte First)
{
    public const int Length = 64;
}

/// <summary>NVAPI's identifiers and limits used by the probe (nvapi_interface.h and nvapi.h, NVIDIA's NVAPI SDK headers).</summary>
[SupportedOSPlatform("windows")]
internal static class NvApiProbeIds
{
    public const uint Initialize = 0x0150e828;
    public const uint Unload = 0xd22bdd7e;
    public const uint SysGetDriverAndBranchVersion = 0x2926aaad;
    public const uint EnumPhysicalGpus = 0xe5ac921f;
    public const uint GpuGetAdapterIdFromPhysicalGpu = 0x0ff07fde;
    public const uint GpuQueryWorkstationFeatureSupport = 0x80b1abb9;

    /// <summary>NV_GPU_WORKSTATION_FEATURE_TYPE_PROVIZ: the professional visualisation features RTX PRO boards carry.</summary>
    public const int WorkstationFeatureProViz = 2;

    /// <summary>NVAPI_MAX_PHYSICAL_GPUS.</summary>
    public const int MaxPhysicalGpus = 64;

    public const int Ok = 0;
}

/// <summary>The COM vtable slots the probes call, counted from IUnknown's QueryInterface at 0 through every base interface in the
/// Windows SDK's declaration order.</summary>
[SupportedOSPlatform("windows")]
internal static class Slots
{
    public const int Release = 2;

    // IDXGIObject 3–6, IDXGIFactory 7–11, IDXGIFactory1 12–13, IDXGIFactory2 14–24, IDXGIFactory3 25, IDXGIFactory4 26–27,
    // IDXGIFactory5 28, IDXGIFactory6 29.
    public const int FactoryEnumAdapterByGpuPreference = 29;

    // IDXGIAdapter 7–9, IDXGIAdapter1 10.
    public const int AdapterEnumOutputs = 7;
    public const int AdapterCheckInterfaceSupport = 9;
    public const int AdapterGetDesc1 = 10;

    // IDispatch 3–6; INetworkListManager 7– (GetNetworks), IEnumNetworks 7– (Next at 8), INetwork 7– (GetName 7,
    // GetNetworkConnections 13, GetCategory 18), IEnumNetworkConnections 7– (Next at 8), INetworkConnection 7– (GetAdapterId 12).
    public const int NetworkListGetNetworks = 7;
    public const int EnumNetworksNext = 8;
    public const int NetworkGetName = 7;
    public const int NetworkGetNetworkConnections = 13;
    public const int NetworkGetCategory = 18;
    public const int EnumNetworkConnectionsNext = 8;
    public const int NetworkConnectionGetAdapterId = 12;
}
