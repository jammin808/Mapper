using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PlayMap.Render.Sync;

namespace PlayMap.Render.Platform.Windows;

/// <summary>The loaded library and the functions the board tier calls (Windows; the driver's own DLL).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class NvApi : IDisposable
{
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<int> _unload;

    private NvApi(nint library, delegate* unmanaged[Cdecl]<uint, nint> query)
    {
        _library = library;
        _unload = (delegate* unmanaged[Cdecl]<int>)query(NvApiIds.Unload);
        EnumSyncDevices = (delegate* unmanaged[Cdecl]<nint*, uint*, int>)query(NvApiIds.GSyncEnumSyncDevices);
        QueryCapabilities = (delegate* unmanaged[Cdecl]<nint, NvGSyncCapabilities*, int>)query(NvApiIds.GSyncQueryCapabilities);
        GetTopology = (delegate* unmanaged[Cdecl]<nint, uint*, NvGSyncGpu*, uint*, NvGSyncDisplay*, int>)query(NvApiIds.GSyncGetTopology);
        GetSyncStatus = (delegate* unmanaged[Cdecl]<nint, nint, NvGSyncStatus*, int>)query(NvApiIds.GSyncGetSyncStatus);
        GetStatusParameters = (delegate* unmanaged[Cdecl]<nint, NvGSyncStatusParams*, int>)query(NvApiIds.GSyncGetStatusParameters);
    }

    public delegate* unmanaged[Cdecl]<nint*, uint*, int> EnumSyncDevices { get; }

    public delegate* unmanaged[Cdecl]<nint, NvGSyncCapabilities*, int> QueryCapabilities { get; }

    public delegate* unmanaged[Cdecl]<nint, uint*, NvGSyncGpu*, uint*, NvGSyncDisplay*, int> GetTopology { get; }

    public delegate* unmanaged[Cdecl]<nint, nint, NvGSyncStatus*, int> GetSyncStatus { get; }

    public delegate* unmanaged[Cdecl]<nint, NvGSyncStatusParams*, int> GetStatusParameters { get; }

    /// <summary>Loads the driver's NVAPI and initialises it; null with words when there is no NVIDIA driver, or it refuses.</summary>
    public static NvApi? TryLoad(out string words)
    {
        if (!NativeLibrary.TryLoad("nvapi64.dll", typeof(NvApi).Assembly, DllImportSearchPath.System32, out var library))
        {
            words = "no NVIDIA driver on this machine (nvapi64.dll is not there): outputs present free, measured";
            return null;
        }
        if (!NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out var queryExport))
        {
            NativeLibrary.Free(library);
            words = "the NVIDIA driver's nvapi64.dll has no nvapi_QueryInterface";
            return null;
        }
        var query = (delegate* unmanaged[Cdecl]<uint, nint>)queryExport;
        var initialize = (delegate* unmanaged[Cdecl]<int>)query(NvApiIds.Initialize);
        var status = initialize is null ? NvApiIds.NvidiaDeviceNotFound : initialize();
        if (status != NvApiIds.Ok)
        {
            NativeLibrary.Free(library);
            words = $"the NVIDIA driver did not start NVAPI (status {status})";
            return null;
        }
        var api = new NvApi(library, query);
        if (api.EnumSyncDevices is null || api.QueryCapabilities is null || api.GetSyncStatus is null || api.GetStatusParameters is null)
        {
            api.Dispose();
            words = "this NVIDIA driver offers no sync board calls";
            return null;
        }
        words = "";
        return api;
    }

    public void Dispose()
    {
        if (_unload is not null)
            _ = _unload();
        NativeLibrary.Free(_library);
    }
}
