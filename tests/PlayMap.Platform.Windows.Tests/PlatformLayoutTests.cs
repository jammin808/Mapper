using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PlayMap.Platform.Windows.Interop;

namespace PlayMap.Platform.Windows.Tests;

/// <summary>The platform's own probe structures have the Windows SDK's and NVAPI's sizes and field offsets for x64, and its vtable slots
/// the SDK's counts: DXGI_ADAPTER_DESC1 (dxgi.h), SYSTEM_CPU_SET_INFORMATION (winnt.h), the power throttling states (processthreadsapi.h),
/// OSVERSIONINFOEXW (winnt.h), REASON_CONTEXT (minwinbase.h), LUID_AND_ATTRIBUTES and a one-privilege TOKEN_PRIVILEGES (winnt.h), the
/// kernel thunks' D3DKMT_OPENADAPTERFROMLUID, D3DKMT_QUERYADAPTERINFO, D3DKMT_CLOSEADAPTER and D3DKMT_WDDM_2_7_CAPS with
/// KMTQAITYPE_WDDM_2_7_CAPS (d3dkmthk.h), SYSTEM_CODEINTEGRITY_INFORMATION (winternl.h), and NVAPI's NvAPI_ShortString (nvapi.h) with
/// the probe's interface ids (nvapi_interface.h). Runs everywhere: only layouts are read.</summary>
[SupportedOSPlatform("windows")]
public sealed class PlatformLayoutTests
{
    private static int Off<T>(string field) where T : struct => (int)Marshal.OffsetOf<T>("<" + field + ">k__BackingField");

    [Fact]
    public void DxgiAndNvapiProbeStructuresHaveTheHeadersSizesAndOffsets()
    {
        Assert.Equal(312, Unsafe.SizeOf<DxgiAdapterDesc1>());
        Assert.Equal(256, Off<DxgiAdapterDesc1>("VendorId"));
        Assert.Equal(260, Off<DxgiAdapterDesc1>("DeviceId"));
        Assert.Equal(272, Off<DxgiAdapterDesc1>("DedicatedVideoMemory"));
        Assert.Equal(296, Off<DxgiAdapterDesc1>("LuidLow"));
        Assert.Equal(300, Off<DxgiAdapterDesc1>("LuidHigh"));
        Assert.Equal(304, Off<DxgiAdapterDesc1>("Flags"));
        Assert.Equal(128 * sizeof(char), Off<DxgiAdapterDesc1>("VendorId"));

        Assert.Equal(64, Unsafe.SizeOf<NvShortString>());
        Assert.Equal(8, Unsafe.SizeOf<Luid>());
        Assert.Equal(0x2926aaadu, NvApiProbeIds.SysGetDriverAndBranchVersion);
        Assert.Equal(0xe5ac921fu, NvApiProbeIds.EnumPhysicalGpus);
        Assert.Equal(0x0ff07fdeu, NvApiProbeIds.GpuGetAdapterIdFromPhysicalGpu);
        Assert.Equal(0x80b1abb9u, NvApiProbeIds.GpuQueryWorkstationFeatureSupport);
        Assert.Equal(2, NvApiProbeIds.WorkstationFeatureProViz);
        Assert.Equal(64, NvApiProbeIds.MaxPhysicalGpus);
    }

    [Fact]
    public void TheClaimsStructuresHaveTheHeadersSizesAndOffsets()
    {
        Assert.Equal(12, Unsafe.SizeOf<PowerThrottlingState>());
        Assert.Equal(4, Off<PowerThrottlingState>("ControlMask"));
        Assert.Equal(8, Off<PowerThrottlingState>("StateMask"));

        Assert.Equal(32, Unsafe.SizeOf<SystemCpuSetInformation>());
        Assert.Equal(8, Off<SystemCpuSetInformation>("Id"));
        Assert.Equal(12, Off<SystemCpuSetInformation>("Group"));
        Assert.Equal(14, Off<SystemCpuSetInformation>("LogicalProcessorIndex"));
        Assert.Equal(18, Off<SystemCpuSetInformation>("EfficiencyClass"));
        Assert.Equal(20, Off<SystemCpuSetInformation>("SchedulingClass"));
        Assert.Equal(24, Off<SystemCpuSetInformation>("AllocationTag"));

        Assert.Equal(284, Unsafe.SizeOf<OsVersionInfoEx>());
        Assert.Equal(12, Off<OsVersionInfoEx>("Build"));
        Assert.Equal(20, OsVersionInfoEx.CsdVersionOffset);
        Assert.Equal(276, Off<OsVersionInfoEx>("ServicePackMajor"));
        Assert.Equal(282, Off<OsVersionInfoEx>("ProductType"));

        Assert.Equal(32, Unsafe.SizeOf<ReasonContext>());
        Assert.Equal(8, Off<ReasonContext>("SimpleReasonString"));

        Assert.Equal(12, Unsafe.SizeOf<LuidAndAttributes>());
        Assert.Equal(8, Off<LuidAndAttributes>("Attributes"));
        Assert.Equal(16, Unsafe.SizeOf<TokenPrivilege>());
        Assert.Equal(4, Off<TokenPrivilege>("Privilege"));
    }

    [Fact]
    public void TheKernelThunkAndCodeIntegrityStructuresHaveTheHeadersSizesAndOffsets()
    {
        Assert.Equal(12, Unsafe.SizeOf<KmtOpenAdapterFromLuid>());
        Assert.Equal(8, Off<KmtOpenAdapterFromLuid>("Adapter"));
        Assert.Equal(24, Unsafe.SizeOf<KmtQueryAdapterInfo>());
        Assert.Equal(4, Off<KmtQueryAdapterInfo>("Type"));
        Assert.Equal(8, Off<KmtQueryAdapterInfo>("PrivateDriverData"));
        Assert.Equal(16, Off<KmtQueryAdapterInfo>("PrivateDriverDataSize"));
        Assert.Equal(4, Unsafe.SizeOf<KmtCloseAdapter>());
        Assert.Equal(4, Unsafe.SizeOf<KmtWddm27Caps>());
        Assert.Equal(70, Native.KmtQaiTypeWddm27Caps);
        // HwSchSupported is bit 0 and HwSchEnabled bit 1 of the caps' one UINT.
        Assert.Equal((true, false), (new KmtWddm27Caps(0x1).HwSchSupported, new KmtWddm27Caps(0x1).HwSchEnabled));
        Assert.Equal((false, true), (new KmtWddm27Caps(0x2).HwSchSupported, new KmtWddm27Caps(0x2).HwSchEnabled));

        Assert.Equal(8, Unsafe.SizeOf<CodeIntegrityInformation>());
        Assert.Equal(4, Off<CodeIntegrityInformation>("CodeIntegrityOptions"));
        Assert.Equal((103, 0x400u), (Native.SystemCodeIntegrityInformation, Native.CodeIntegrityHvciKmciEnabled));
    }

    [Fact]
    public void TheVtableSlotsAreTheSdksCounts()
    {
        // IUnknown 0–2, IDXGIObject 3–6, IDXGIFactory 7–11, IDXGIFactory1 12–13, IDXGIFactory2 14–24 (eleven methods), IDXGIFactory3 25,
        // IDXGIFactory4 26–27, IDXGIFactory5 28: IDXGIFactory6's one method is the thirtieth.
        Assert.Equal(3 + 4 + 5 + 2 + 11 + 1 + 2 + 1, Slots.FactoryEnumAdapterByGpuPreference);
        Assert.Equal((7, 9, 10), (Slots.AdapterEnumOutputs, Slots.AdapterCheckInterfaceSupport, Slots.AdapterGetDesc1));
        // IDispatch 3–6; INetwork's GetCategory is its twelfth method.
        Assert.Equal(7 + 11, Slots.NetworkGetCategory);
        Assert.Equal((7, 8, 7), (Slots.NetworkListGetNetworks, Slots.EnumNetworksNext, Slots.NetworkGetName));
        // INetwork: GetName, SetName, GetDescription, SetDescription, GetNetworkId, GetDomainType, then GetNetworkConnections (its seventh);
        // IEnumNetworkConnections: _NewEnum, then Next; INetworkConnection: GetNetwork, IsConnectedToInternet, IsConnected,
        // GetConnectivity, GetConnectionId, then GetAdapterId (its sixth).
        Assert.Equal((7 + 6, 7 + 1, 7 + 5), (Slots.NetworkGetNetworkConnections, Slots.EnumNetworkConnectionsNext, Slots.NetworkConnectionGetAdapterId));
    }

    [Fact]
    public void TheDriversVersionReadsAsItsFourParts()
    {
        // 32.0.15.8216: the high word pair, then the low.
        var umd = ((long)((32 << 16) | 0) << 32) | (uint)((15 << 16) | 8216);
        Assert.Equal("32.0.15.8216", AdapterProbe.UmdText(umd));
    }
}
