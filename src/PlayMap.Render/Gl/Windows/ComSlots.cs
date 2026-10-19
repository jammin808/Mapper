using System.Runtime.Versioning;

namespace PlayMap.Render.Gl.Windows;

/// <summary>The vtable slot of every COM method PlayMap calls by hand: the method's index in its interface's vtable, counted from
/// IUnknown's QueryInterface at 0 through every base interface in declaration order (the Windows SDK's C vtables; the values are
/// checked against the mingw-w64 headers by DxgiLayoutTests).</summary>
[SupportedOSPlatform("windows")]
internal static class ComSlots
{
    public const int QueryInterface = 0;
    public const int Release = 2;

    // IDXGIObject 3–6, IDXGIFactory 7–11, IDXGIFactory1 12–13, IDXGIFactory2 14–.
    public const int FactoryMakeWindowAssociation = 8;
    public const int FactoryEnumAdapters1 = 12;
    public const int FactoryCreateSwapChainForHwnd = 15;

    // IDXGIAdapter 7–9, IDXGIAdapter1 10.
    public const int AdapterEnumOutputs = 7;
    public const int AdapterGetDesc1 = 10;

    // IDXGIOutput 7–.
    public const int OutputGetDesc = 7;

    // IDXGIDeviceSubObject 7, IDXGISwapChain 8–17, IDXGISwapChain1 18–28, IDXGISwapChain2 29–35.
    public const int SwapChainPresent = 8;
    public const int SwapChainGetBuffer = 9;
    public const int SwapChainResizeBuffers = 13;
    public const int SwapChainGetContainingOutput = 15;
    public const int SwapChainGetFrameStatistics = 16;
    public const int SwapChainGetLastPresentCount = 17;
    public const int SwapChainPresent1 = 22;
    public const int SwapChainSetMaximumFrameLatency = 31;
    public const int SwapChainGetFrameLatencyWaitableObject = 33;

    // IDXGIResource 8–11 (after IDXGIDeviceSubObject's GetDevice at 7).
    public const int ResourceGetSharedHandle = 8;

    // IDXGIKeyedMutex 8–9.
    public const int KeyedMutexAcquireSync = 8;
    public const int KeyedMutexReleaseSync = 9;

    // ID3D11Device 3–.
    public const int DeviceCreateTexture2D = 5;
    public const int DeviceOpenSharedResource = 28;
    public const int DeviceGetDeviceRemovedReason = 39;
    public const int DeviceGetImmediateContext = 40;

    // ID3D11DeviceChild 3–6, ID3D11DeviceContext 7–.
    public const int ContextMap = 14;
    public const int ContextUnmap = 15;
    public const int ContextCopyResource = 47;
    public const int ContextUpdateSubresource = 48;
    public const int ContextFlush = 111;

    // ID3D11DeviceChild 3–6, ID3D11Resource 7–9, ID3D11Texture2D 10.
    public const int Texture2DGetDesc = 10;

    // ID3D10Multithread 3–6.
    public const int MultithreadSetMultithreadProtected = 5;
}

/// <summary>Calls through a COM object's vtable: the slot's function pointer read from the object's first word.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class Com
{
    public static void* Slot(nint obj, int slot) => (*(void***)obj)[slot];

    /// <summary>Releases a reference and clears the pointer (a null pointer is left alone).</summary>
    public static void Release(ref nint obj)
    {
        if (obj == 0)
            return;
        _ = ((delegate* unmanaged<nint, uint>)Slot(obj, ComSlots.Release))(obj);
        obj = 0;
    }

    /// <summary>The interface <paramref name="iid"/> of an object, or 0 when it does not have it.</summary>
    public static nint Query(nint obj, in Guid iid)
    {
        nint result = 0;
        fixed (Guid* g = &iid)
        {
            var hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(obj, ComSlots.QueryInterface))(obj, g, &result);
            return hr < 0 ? 0 : result;
        }
    }

    public static string Hr(int hr) => "0x" + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}
