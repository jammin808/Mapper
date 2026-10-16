using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using PlayMap.Render.Platform.Windows;

namespace PlayMap.Render.Tests.Presentation;

/// <summary>Every hand-declared structure of the flip path has the Windows SDK's size and field offsets, and every hand-declared COM
/// method its vtable slot. The goldens were taken from the mingw-w64 11.0.1 headers (d3d11.h, dxgi.h, dxgi1_2.h, dxgi1_3.h, wingdi.h,
/// winuser.h) for x86_64 with <c>offsetof</c> and <c>sizeof</c>; where those headers are installed (Linux: the mingw-w64-common
/// package), the vtable slots are also counted from the headers' C vtables at test time. Runs everywhere (only layouts are read).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class DxgiLayoutTests
{
    private const string Headers = "/usr/share/mingw-w64/include";

    private static int Off<T>(string field) where T : struct => (int)Marshal.OffsetOf<T>("<" + field + ">k__BackingField");

    [Fact]
    [Trait("Category", "Windows")]
    public void StructuresHaveTheSdksSizesAndOffsets()
    {
        Assert.Equal(48, Unsafe.SizeOf<DxgiSwapChainDesc1>());
        Assert.Equal(16, Off<DxgiSwapChainDesc1>("SampleCount"));
        Assert.Equal(24, Off<DxgiSwapChainDesc1>("BufferUsage"));
        Assert.Equal(36, Off<DxgiSwapChainDesc1>("SwapEffect"));
        Assert.Equal(44, Off<DxgiSwapChainDesc1>("Flags"));

        Assert.Equal(32, Unsafe.SizeOf<DxgiFrameStatistics>());
        Assert.Equal(4, Off<DxgiFrameStatistics>("PresentRefreshCount"));
        Assert.Equal(8, Off<DxgiFrameStatistics>("SyncRefreshCount"));
        Assert.Equal(16, Off<DxgiFrameStatistics>("SyncQpcTime"));
        Assert.Equal(24, Off<DxgiFrameStatistics>("SyncGpuTime"));

        Assert.Equal(32, Unsafe.SizeOf<DxgiPresentParameters>());
        Assert.Equal(8, Off<DxgiPresentParameters>("DirtyRects"));
        Assert.Equal(16, Off<DxgiPresentParameters>("ScrollRect"));
        Assert.Equal(24, Off<DxgiPresentParameters>("ScrollOffset"));

        Assert.Equal(312, Unsafe.SizeOf<DxgiAdapterDesc1>());
        Assert.Equal(256, Off<DxgiAdapterDesc1>("VendorId"));
        Assert.Equal(272, Off<DxgiAdapterDesc1>("DedicatedVideoMemory"));
        Assert.Equal(296, Off<DxgiAdapterDesc1>("LuidLow"));
        Assert.Equal(304, Off<DxgiAdapterDesc1>("Flags"));

        Assert.Equal(96, Unsafe.SizeOf<DxgiOutputDesc>());
        Assert.Equal(64, Off<DxgiOutputDesc>("Left"));
        Assert.Equal(80, Off<DxgiOutputDesc>("AttachedToDesktop"));
        Assert.Equal(88, Off<DxgiOutputDesc>("Monitor"));

        Assert.Equal(44, Unsafe.SizeOf<D3D11Texture2DDesc>());
        Assert.Equal(16, Off<D3D11Texture2DDesc>("Format"));
        Assert.Equal(28, Off<D3D11Texture2DDesc>("Usage"));
        Assert.Equal(40, Off<D3D11Texture2DDesc>("MiscFlags"));

        Assert.Equal(16, Unsafe.SizeOf<D3D11MappedSubresource>());
        Assert.Equal(8, Off<D3D11MappedSubresource>("RowPitch"));

        // DISPLAYCONFIG_PATH_INFO: targetInfo at 20 (modeInfoIdx at +12, refreshRate at +28, targetAvailable at +40).
        Assert.Equal(72, Unsafe.SizeOf<DisplayConfigPathInfo>());
        Assert.Equal(20 + 12, Off<DisplayConfigPathInfo>("TargetModeInfoIdx"));
        Assert.Equal(20 + 28, Off<DisplayConfigPathInfo>("RefreshNumerator"));
        Assert.Equal(20 + 40, Off<DisplayConfigPathInfo>("TargetAvailable"));
        // DISPLAYCONFIG_MODE_INFO: targetMode at 16, its vSyncFreq at +16.
        Assert.Equal(64, Unsafe.SizeOf<DisplayConfigModeInfo>());
        Assert.Equal(16 + 16, Off<DisplayConfigModeInfo>("VSyncNumerator"));
        Assert.Equal(84, Unsafe.SizeOf<DisplayConfigSourceDeviceName>());
        Assert.Equal(20, DisplayConfigSourceDeviceName.GdiNameOffset);

        Assert.Equal(80, Unsafe.SizeOf<WndClassEx>());
        Assert.Equal(8, Off<WndClassEx>("WndProc"));
        Assert.Equal(24, Off<WndClassEx>("Instance"));
        Assert.Equal(64, Off<WndClassEx>("ClassName"));
        Assert.Equal(48, Unsafe.SizeOf<WinMsg>());
        Assert.Equal(16, Off<WinMsg>("WParam"));
        Assert.Equal(104, Unsafe.SizeOf<MonitorInfoEx>());
        Assert.Equal(40, MonitorInfoEx.DeviceOffset);
    }

    /// <summary>The golden slots: (interface, method, slot) as the SDK's C vtables count them.</summary>
    public static readonly TheoryData<string, string, int, int> Slots = new()
    {
        { "IUnknown", "QueryInterface", 0, ComSlots.QueryInterface },
        { "IUnknown", "Release", 2, ComSlots.Release },
        { "IDXGIFactory2", "MakeWindowAssociation", 8, ComSlots.FactoryMakeWindowAssociation },
        { "IDXGIFactory2", "EnumAdapters1", 12, ComSlots.FactoryEnumAdapters1 },
        { "IDXGIFactory2", "CreateSwapChainForHwnd", 15, ComSlots.FactoryCreateSwapChainForHwnd },
        { "IDXGIAdapter1", "EnumOutputs", 7, ComSlots.AdapterEnumOutputs },
        { "IDXGIAdapter1", "GetDesc1", 10, ComSlots.AdapterGetDesc1 },
        { "IDXGIOutput", "GetDesc", 7, ComSlots.OutputGetDesc },
        { "IDXGISwapChain2", "Present", 8, ComSlots.SwapChainPresent },
        { "IDXGISwapChain2", "GetBuffer", 9, ComSlots.SwapChainGetBuffer },
        { "IDXGISwapChain2", "ResizeBuffers", 13, ComSlots.SwapChainResizeBuffers },
        { "IDXGISwapChain2", "GetContainingOutput", 15, ComSlots.SwapChainGetContainingOutput },
        { "IDXGISwapChain2", "GetFrameStatistics", 16, ComSlots.SwapChainGetFrameStatistics },
        { "IDXGISwapChain2", "GetLastPresentCount", 17, ComSlots.SwapChainGetLastPresentCount },
        { "IDXGISwapChain2", "Present1", 22, ComSlots.SwapChainPresent1 },
        { "IDXGISwapChain2", "SetMaximumFrameLatency", 31, ComSlots.SwapChainSetMaximumFrameLatency },
        { "IDXGISwapChain2", "GetFrameLatencyWaitableObject", 33, ComSlots.SwapChainGetFrameLatencyWaitableObject },
        { "IDXGIResource", "GetSharedHandle", 8, ComSlots.ResourceGetSharedHandle },
        { "IDXGIKeyedMutex", "AcquireSync", 8, ComSlots.KeyedMutexAcquireSync },
        { "IDXGIKeyedMutex", "ReleaseSync", 9, ComSlots.KeyedMutexReleaseSync },
        { "ID3D11Device", "CreateTexture2D", 5, ComSlots.DeviceCreateTexture2D },
        { "ID3D11Device", "OpenSharedResource", 28, ComSlots.DeviceOpenSharedResource },
        { "ID3D11Device", "GetDeviceRemovedReason", 39, ComSlots.DeviceGetDeviceRemovedReason },
        { "ID3D11Device", "GetImmediateContext", 40, ComSlots.DeviceGetImmediateContext },
        { "ID3D11DeviceContext", "Map", 14, ComSlots.ContextMap },
        { "ID3D11DeviceContext", "Unmap", 15, ComSlots.ContextUnmap },
        { "ID3D11DeviceContext", "CopyResource", 47, ComSlots.ContextCopyResource },
        { "ID3D11DeviceContext", "UpdateSubresource", 48, ComSlots.ContextUpdateSubresource },
        { "ID3D11DeviceContext", "Flush", 111, ComSlots.ContextFlush },
        { "ID3D11Texture2D", "GetDesc", 10, ComSlots.Texture2DGetDesc },
        { "ID3D10Multithread", "SetMultithreadProtected", 5, ComSlots.MultithreadSetMultithreadProtected },
    };

    [Theory]
    [MemberData(nameof(Slots))]
    [Trait("Category", "Windows")]
    public void EveryComMethodSitsInItsSdkSlot(string iface, string method, int golden, int declared)
    {
        Assert.Equal(golden, declared);
        if (HeaderSlot(iface, method) is { } counted)
            Assert.Equal(counted, declared);
    }

    [Fact]
    public void TheHeadersAreReadWhereTheyAreInstalled()
    {
        // Where the mingw-w64 headers are installed the slots are counted from them; elsewhere the goldens stand (said, not skipped).
        if (!Directory.Exists(Headers))
        {
            Assert.Null(HeaderSlot("IDXGISwapChain2", "Present1"));
            return;
        }
        Assert.Equal(22, HeaderSlot("IDXGISwapChain2", "Present1"));
        Assert.Equal(111, HeaderSlot("ID3D11DeviceContext", "Flush"));
    }

    /// <summary>The method's index in the interface's C vtable (<c>typedef struct IVtbl { … } IVtbl;</c>) in the installed headers,
    /// or null where they are not installed.</summary>
    private static int? HeaderSlot(string iface, string method)
    {
        if (!Directory.Exists(Headers))
            return null;
        foreach (var file in Directory.EnumerateFiles(Headers, "*.h"))
        {
            var text = File.ReadAllText(file);
            var start = text.IndexOf($"typedef struct {iface}Vtbl {{", StringComparison.Ordinal);
            if (start < 0)
                continue;
            var end = text.IndexOf($"}} {iface}Vtbl;", start, StringComparison.Ordinal);
            var methods = MethodPattern().Matches(text[start..end]).Select(m => m.Groups[1].Value).ToList();
            var i = methods.IndexOf(method);
            return i < 0 ? throw new InvalidOperationException($"{iface}Vtbl in {file} has no {method}") : i;
        }
        return null;
    }

    [GeneratedRegex(@"\(STDMETHODCALLTYPE \*(\w+)\)")]
    private static partial Regex MethodPattern();
}
