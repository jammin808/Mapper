using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PlayMap.Core.Machine;

namespace PlayMap.Platform.Windows.Interop;

/// <summary>One adapter as DXGI answered it, before the published policy places it: its LUID, ids, name, the driver's user-mode version
/// ("32.0.15.8216", "" when unread), whether an output is attached, its dedicated memory and whether DXGI calls it software.</summary>
[SupportedOSPlatform("windows")]
internal sealed record DxgiAdapter(long Luid, int VendorId, int DeviceId, string Name, string UmdVersion, bool HasOutputs, long DedicatedMb,
    bool Software);

/// <summary>Every adapter, read-only, through DXGI 1.6's <c>EnumAdapterByGpuPreference</c> (the three vtable slots of
/// <see cref="Slots"/>), and on NVIDIA the driver's version and branch and the RTX PRO probe through NVAPI. Nothing is created on any
/// adapter; every interface taken is released before the call returns.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class AdapterProbe
{
    private static readonly Guid FactoryIid = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    private static readonly Guid Adapter1Iid = new("29038f61-3839-4626-91fd-086879011a05");
    private static readonly Guid DxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private const int NotFound = unchecked((int)0x887A0002);

    /// <summary>The adapters in DXGI's order (the system's preference), or none with words when the factory does not open.</summary>
    public static ImmutableArray<DxgiAdapter> Read(out string words)
    {
        var status = Native.CreateDXGIFactory2(0, FactoryIid, out var factory);
        if (status < 0 || factory == 0)
        {
            words = "DXGI 1.6 did not open (" + Native.StatusWords(status) + "): no adapter is read";
            return [];
        }
        var list = ImmutableArray.CreateBuilder<DxgiAdapter>();
        try
        {
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, int, Guid*, nint*, int>)Slot(factory, Slots.FactoryEnumAdapterByGpuPreference);
            var iid = Adapter1Iid;
            for (uint i = 0; i < 64; i++)
            {
                nint adapter = 0;
                status = enumerate(factory, i, 0, &iid, &adapter);
                if (status == NotFound)
                    break;
                if (status < 0 || adapter == 0)
                {
                    words = "DXGI did not list adapter " + i.ToString(CultureInfo.InvariantCulture) + " (" + Native.StatusWords(status) + ")";
                    return list.ToImmutable();
                }
                try
                {
                    list.Add(Describe(adapter));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }
        words = "";
        return list.ToImmutable();
    }

    private static DxgiAdapter Describe(nint adapter)
    {
        DxgiAdapterDesc1 desc = default;
        var getDesc = (delegate* unmanaged[Stdcall]<nint, DxgiAdapterDesc1*, int>)Slot(adapter, Slots.AdapterGetDesc1);
        _ = getDesc(adapter, &desc);
        var name = new string((char*)&desc, 0, DxgiAdapterDesc1.DescriptionChars).TrimEnd('\0');
        var cut = name.IndexOf('\0', StringComparison.Ordinal);
        if (cut >= 0)
            name = name[..cut];

        var check = (delegate* unmanaged[Stdcall]<nint, Guid*, long*, int>)Slot(adapter, Slots.AdapterCheckInterfaceSupport);
        var deviceIid = DxgiDeviceIid;
        long umd = 0;
        var version = check(adapter, &deviceIid, &umd) >= 0 ? UmdText(umd) : "";

        var outputs = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(adapter, Slots.AdapterEnumOutputs);
        nint output = 0;
        var hasOutputs = outputs(adapter, 0, &output) >= 0 && output != 0;
        if (output != 0)
            Release(output);

        var luid = ((long)desc.LuidHigh << 32) | desc.LuidLow;
        return new DxgiAdapter(luid, (int)desc.VendorId, (int)desc.DeviceId, name, version, hasOutputs,
            (long)(desc.DedicatedVideoMemory / (1024 * 1024)), (desc.Flags & DxgiAdapterDesc1.SoftwareFlag) != 0);
    }

    /// <summary>The user-mode driver's version as its four 16-bit parts: "32.0.15.8216".</summary>
    internal static string UmdText(long umd)
    {
        var hi = (ulong)umd >> 32;
        var lo = (ulong)umd & 0xFFFFFFFF;
        return string.Create(CultureInfo.InvariantCulture, $"{hi >> 16}.{hi & 0xFFFF}.{lo >> 16}.{lo & 0xFFFF}");
    }

    private static nint Slot(nint com, int slot) => (*(nint**)com)[slot];

    private static void Release(nint com) => _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, Slots.Release))(com);
}

/// <summary>NVIDIA's facts through the driver's own NVAPI (nvapi64.dll, never shipped): the driver's version and branch, and, per
/// physical GPU by its LUID, whether it answers the professional visualisation feature (RTX PRO). The library is loaded for the probe
/// and unloaded after it.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class NvApiProbe
{
    /// <summary>The driver ("582.16"), its branch ("r580_00") and the LUIDs of the GPUs that answer PROVIZ; false with words when there is
    /// no NVIDIA driver or it refuses.</summary>
    public static bool TryRead(out string driver, out string branch, out ImmutableHashSet<long> workstation, out string words)
    {
        driver = "";
        branch = "";
        workstation = [];
        if (!NativeLibrary.TryLoad("nvapi64.dll", typeof(NvApiProbe).Assembly, DllImportSearchPath.System32, out var library))
        {
            words = "no NVIDIA driver on this machine (nvapi64.dll is not there)";
            return false;
        }
        try
        {
            if (!NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out var export))
            {
                words = "the NVIDIA driver's nvapi64.dll has no nvapi_QueryInterface";
                return false;
            }
            var query = (delegate* unmanaged[Cdecl]<uint, nint>)export;
            var initialize = (delegate* unmanaged[Cdecl]<int>)query(NvApiProbeIds.Initialize);
            if (initialize is null || initialize() != NvApiProbeIds.Ok)
            {
                words = "the NVIDIA driver did not start NVAPI";
                return false;
            }
            var unload = (delegate* unmanaged[Cdecl]<int>)query(NvApiProbeIds.Unload);
            try
            {
                var version = (delegate* unmanaged[Cdecl]<uint*, NvShortString*, int>)query(NvApiProbeIds.SysGetDriverAndBranchVersion);
                if (version is not null)
                {
                    uint number = 0;
                    NvShortString text = default;
                    if (version(&number, &text) == NvApiProbeIds.Ok)
                    {
                        driver = string.Create(CultureInfo.InvariantCulture, $"{number / 100}.{number % 100:00}");
                        branch = Marshal.PtrToStringAnsi((nint)(&text)) ?? "";
                    }
                }
                workstation = Workstation(query);
            }
            finally
            {
                if (unload is not null)
                    _ = unload();
            }
            words = "";
            return true;
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static ImmutableHashSet<long> Workstation(delegate* unmanaged[Cdecl]<uint, nint> query)
    {
        var enumerate = (delegate* unmanaged[Cdecl]<nint*, uint*, int>)query(NvApiProbeIds.EnumPhysicalGpus);
        var adapterId = (delegate* unmanaged[Cdecl]<nint, Luid*, int>)query(NvApiProbeIds.GpuGetAdapterIdFromPhysicalGpu);
        var feature = (delegate* unmanaged[Cdecl]<nint, int, int>)query(NvApiProbeIds.GpuQueryWorkstationFeatureSupport);
        if (enumerate is null || adapterId is null || feature is null)
            return [];
        var handles = stackalloc nint[NvApiProbeIds.MaxPhysicalGpus];
        uint count = 0;
        if (enumerate(handles, &count) != NvApiProbeIds.Ok)
            return [];
        var set = ImmutableHashSet.CreateBuilder<long>();
        for (var i = 0; i < Math.Min(count, NvApiProbeIds.MaxPhysicalGpus); i++)
        {
            Luid luid = default;
            if (adapterId(handles[i], &luid) == NvApiProbeIds.Ok && feature(handles[i], NvApiProbeIds.WorkstationFeatureProViz) == NvApiProbeIds.Ok)
                set.Add(luid.Value);
        }
        return set.ToImmutable();
    }
}
