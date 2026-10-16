using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlayMap.Render.Platform.Windows;

// The structures PlayMap passes to DXGI, Direct3D 11, user32 and the display configuration API, laid out as the Windows SDK
// declares them for x64 (sizes and offsets checked against the mingw-w64 headers by DxgiLayoutTests). Only the fields PlayMap reads
// or writes are named; the rest are kept by the explicit size. Text fields (device names, adapter descriptions) are read through
// the structure's address.

/// <summary>DXGI_SWAP_CHAIN_DESC1.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential)]
internal record struct DxgiSwapChainDesc1(uint Width, uint Height, uint Format, int Stereo, uint SampleCount, uint SampleQuality,
    uint BufferUsage, uint BufferCount, uint Scaling, uint SwapEffect, uint AlphaMode, uint Flags);

/// <summary>DXGI_FRAME_STATISTICS: the last present the display showed, the refresh it first showed at, and the newest vblank's
/// refresh count with its QPC instant.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal record struct DxgiFrameStatistics(
    [field: FieldOffset(0)] uint PresentCount,
    [field: FieldOffset(4)] uint PresentRefreshCount,
    [field: FieldOffset(8)] uint SyncRefreshCount,
    [field: FieldOffset(16)] long SyncQpcTime,
    [field: FieldOffset(24)] long SyncGpuTime);

/// <summary>DXGI_PRESENT_PARAMETERS (all zero: the whole back buffer changed).</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal record struct DxgiPresentParameters(
    [field: FieldOffset(0)] uint DirtyRectsCount,
    [field: FieldOffset(8)] nint DirtyRects,
    [field: FieldOffset(16)] nint ScrollRect,
    [field: FieldOffset(24)] nint ScrollOffset);

/// <summary>DXGI_ADAPTER_DESC1: Description (WCHAR[128]) at 0.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 312)]
internal record struct DxgiAdapterDesc1(
    [field: FieldOffset(256)] uint VendorId,
    [field: FieldOffset(260)] uint DeviceId,
    [field: FieldOffset(272)] nuint DedicatedVideoMemory,
    [field: FieldOffset(296)] uint LuidLow,
    [field: FieldOffset(300)] int LuidHigh,
    [field: FieldOffset(304)] uint Flags)
{
    public const int DescriptionOffset = 0;
    public const int DescriptionChars = 128;
}

/// <summary>DXGI_OUTPUT_DESC: DeviceName (WCHAR[32], "\\.\DISPLAY2") at 0.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 96)]
internal record struct DxgiOutputDesc(
    [field: FieldOffset(64)] int Left,
    [field: FieldOffset(68)] int Top,
    [field: FieldOffset(72)] int Right,
    [field: FieldOffset(76)] int Bottom,
    [field: FieldOffset(80)] int AttachedToDesktop,
    [field: FieldOffset(88)] nint Monitor)
{
    public const int DeviceNameChars = 32;
}

/// <summary>D3D11_TEXTURE2D_DESC.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential)]
internal record struct D3D11Texture2DDesc(uint Width, uint Height, uint MipLevels, uint ArraySize, uint Format, uint SampleCount,
    uint SampleQuality, uint Usage, uint BindFlags, uint CpuAccessFlags, uint MiscFlags);

/// <summary>D3D11_MAPPED_SUBRESOURCE.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Sequential)]
internal record struct D3D11MappedSubresource(nint Data, uint RowPitch, uint DepthPitch);

/// <summary>DISPLAYCONFIG_PATH_INFO: the source's adapter LUID and id, the target's mode index and rational refresh.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 72)]
internal record struct DisplayConfigPathInfo(
    [field: FieldOffset(0)] uint SourceAdapterLow,
    [field: FieldOffset(4)] int SourceAdapterHigh,
    [field: FieldOffset(8)] uint SourceId,
    [field: FieldOffset(32)] uint TargetModeInfoIdx,
    [field: FieldOffset(48)] uint RefreshNumerator,
    [field: FieldOffset(52)] uint RefreshDenominator,
    [field: FieldOffset(60)] int TargetAvailable);

/// <summary>DISPLAYCONFIG_MODE_INFO: for a target mode, targetVideoSignalInfo.vSyncFreq at 16 + 16.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal record struct DisplayConfigModeInfo(
    [field: FieldOffset(0)] uint InfoType,
    [field: FieldOffset(32)] uint VSyncNumerator,
    [field: FieldOffset(36)] uint VSyncDenominator);

/// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME: a header (type, size, adapter LUID, id) and viewGdiDeviceName (WCHAR[32]) at 20.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 84)]
internal record struct DisplayConfigSourceDeviceName(
    [field: FieldOffset(0)] int Type,
    [field: FieldOffset(4)] uint Size,
    [field: FieldOffset(8)] uint AdapterLow,
    [field: FieldOffset(12)] int AdapterHigh,
    [field: FieldOffset(16)] uint Id)
{
    public const int GdiNameOffset = 20;
}

/// <summary>WNDCLASSEXW.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal record struct WndClassEx(
    [field: FieldOffset(0)] uint Size,
    [field: FieldOffset(4)] uint Style,
    [field: FieldOffset(8)] nint WndProc,
    [field: FieldOffset(24)] nint Instance,
    [field: FieldOffset(40)] nint Cursor,
    [field: FieldOffset(64)] nint ClassName);

/// <summary>MSG.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal record struct WinMsg(
    [field: FieldOffset(0)] nint Hwnd,
    [field: FieldOffset(8)] uint Message,
    [field: FieldOffset(16)] nint WParam,
    [field: FieldOffset(24)] nint LParam);

/// <summary>MONITORINFOEXW: szDevice (WCHAR[32]) at 40.</summary>
[SupportedOSPlatform("windows")]
[StructLayout(LayoutKind.Explicit, Size = 104)]
internal record struct MonitorInfoEx(
    [field: FieldOffset(0)] uint Size,
    [field: FieldOffset(4)] int Left,
    [field: FieldOffset(8)] int Top,
    [field: FieldOffset(12)] int Right,
    [field: FieldOffset(16)] int Bottom,
    [field: FieldOffset(36)] uint Flags)
{
    public const int DeviceOffset = 40;
}

/// <summary>The constants, interface ids and exports of DXGI, Direct3D 11, user32, kernel32 and DWM that the flip path uses.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class DxgiNative
{
    public const uint FormatB8G8R8A8 = 87;
    public const uint FormatR8G8B8A8 = 28;
    public const uint FormatR10G10B10A2 = 24;

    public const uint UsageRenderTargetOutput = 0x20;
    public const uint SwapEffectFlipDiscard = 4;
    public const uint ScalingNone = 1;
    public const uint AlphaModeIgnore = 3;
    public const uint FlagFrameLatencyWaitable = 0x40;
    public const uint PresentDoNotWait = 0x8;
    public const uint MwaNoWindowChanges = 1;
    public const uint MwaNoAltEnter = 2;

    public const int SOk = 0;
    public const int StatusOccluded = 0x087A0001;
    public const int ErrorNotFound = unchecked((int)0x887A0002);
    public const int ErrorDeviceRemoved = unchecked((int)0x887A0005);
    public const int ErrorDeviceReset = unchecked((int)0x887A0007);
    public const int ErrorWasStillDrawing = unchecked((int)0x887A000A);
    public const int ErrorFrameStatisticsDisjoint = unchecked((int)0x887A000B);

    public const int DriverTypeUnknown = 0;
    public const int DriverTypeWarp = 5;
    public const uint CreateBgraSupport = 0x20;
    public const uint CreateVideoSupport = 0x800;
    public const int FeatureLevel11_1 = 0xb100;
    public const int FeatureLevel11_0 = 0xb000;
    public const uint SdkVersion = 7;

    public const uint WsPopup = 0x80000000;
    public const uint WsVisible = 0x10000000;
    public const uint WsExToolWindow = 0x80;
    public const uint WsExTopMost = 0x8;
    public const uint WsExNoActivate = 0x08000000;
    public const uint CsOwnDc = 0x20;
    public const uint WmClose = 0x10;
    public const uint WmEraseBkgnd = 0x14;
    public const uint WmSetCursor = 0x20;
    public const uint PmRemove = 1;
    public const uint QsAllInput = 0x4FF;
    public const uint MwmoInputAvailable = 4;
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 258;
    public const int DwmwaTransitionsForceDisabled = 3;
    public const uint QdcOnlyActivePaths = 2;
    public const int DisplayConfigGetSourceName = 1;
    public const uint ModeInfoTypeTarget = 2;

    public static readonly Guid IidFactory2 = new(0x50c83a1c, 0xe072, 0x4c48, 0x87, 0xb0, 0x36, 0x30, 0xfa, 0x36, 0xa6, 0xd0);
    public static readonly Guid IidSwapChain2 = new(0xa8be2ac4, 0x199f, 0x4946, 0xb3, 0x31, 0x79, 0x59, 0x9f, 0xb9, 0x8d, 0xe7);
    public static readonly Guid IidTexture2D = new(0x6f15aaf2, 0xd208, 0x4e89, 0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c);
    public static readonly Guid IidMultithread = new(0x9b7e4e00, 0x342c, 0x4106, 0xa1, 0x9f, 0x4f, 0x27, 0x04, 0xf6, 0x89, 0xf0);

    [LibraryImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* levels, uint levelCount,
        uint sdkVersion, nint* device, int* level, nint* context);

    [LibraryImport("dxgi.dll", EntryPoint = "CreateDXGIFactory2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int CreateDXGIFactory2(uint flags, Guid* riid, nint* factory);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial ushort RegisterClassEx(WndClassEx* wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint DefWindowProc(nint hwnd, uint msg, nint w, nint l);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(WinMsg* msg, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(WinMsg* msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint DispatchMessage(WinMsg* msg);

    [LibraryImport("user32.dll", EntryPoint = "MsgWaitForMultipleObjectsEx")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint ms, uint wakeMask, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayMonitors")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, nint, nint, int> callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, MonitorInfoEx* info);

    [LibraryImport("user32.dll", EntryPoint = "SetCursor")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll", EntryPoint = "GetDisplayConfigBufferSizes")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int GetDisplayConfigBufferSizes(uint flags, uint* paths, uint* modes);

    [LibraryImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int QueryDisplayConfig(uint flags, uint* pathCount, DisplayConfigPathInfo* paths, uint* modeCount,
        DisplayConfigModeInfo* modes, nint topology);

    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int DisplayConfigGetDeviceInfo(DisplayConfigSourceDeviceName* request);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint GetModuleHandle(nint name);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObjectEx")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint WaitForSingleObjectEx(nint handle, uint ms, int alertable);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmFlush")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int DwmFlush();

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, int* value, int size);

    /// <summary>A NUL-terminated UTF-16 field inside a structure.</summary>
    public static string Text(void* structure, int offset, int chars) => new string((char*)((byte*)structure + offset), 0, chars).Split('\0')[0];
}
