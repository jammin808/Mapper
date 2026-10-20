using System.Globalization;
using System.Runtime.Versioning;
using PlayMap.Render.Platform;

namespace PlayMap.Render.Gl.Windows;

/// <summary>The Direct3D 11 device the flip path renders on: the DXGI factory, the adapter that drives the first display output (or
/// the first adapter, or WARP — the software rasteriser CI runs on), the device with BGRA and video support (without video support
/// where the adapter refuses it, which <see cref="VideoSupport"/> says), its immediate context and multithread protection switched on
/// (decoders share the device). Owns every COM pointer it made and releases them in reverse order. Render thread.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class D3D11Adapter : IDisposable
{
    private nint _factory;
    private nint _adapter;
    private nint _device;
    private nint _context;

    private D3D11Adapter()
    {
    }

    public nint Factory => _factory;

    public nint Device => _device;

    public nint Context => _context;

    /// <summary>"NVIDIA RTX A4000", "Microsoft Basic Render Driver" (WARP).</summary>
    public string Name { get; private set; } = "";

    public long Luid { get; private set; }

    public bool Warp { get; private set; }

    /// <summary>The device was made with D3D11_CREATE_DEVICE_VIDEO_SUPPORT (WARP and some drivers refuse it: decoders cannot use it).</summary>
    public bool VideoSupport { get; private set; }

    /// <summary>ID3D10Multithread::SetMultithreadProtected took: every immediate-context call is ordered under the device's own
    /// lock, so a sharer's call on another thread never interleaves inside one of the renderer's.</summary>
    public bool MultithreadProtected { get; private set; }

    /// <summary>Makes the device on the adapter whose outputs include <paramref name="displayName"/> ("\\.\DISPLAY2"; null or no
    /// match: the first adapter), or on WARP. Throws <see cref="GpuPlatformStartException"/> with the stage and words.</summary>
    public static D3D11Adapter Create(string? displayName, bool warp)
    {
        var a = new D3D11Adapter();
        try
        {
            a.Open(displayName, warp);
            return a;
        }
        catch
        {
            a.Dispose();
            throw;
        }
    }

    private void Open(string? displayName, bool warp)
    {
        nint factory = 0;
        var iid = DxgiNative.IidFactory2;
        var hr = DxgiNative.CreateDXGIFactory2(0, &iid, &factory);
        if (hr < 0)
            throw new GpuPlatformStartException(DxgiStartStage.Adapter, $"DXGI made no factory ({Com.Hr(hr)})");
        _factory = factory;
        if (!warp)
            _adapter = ChooseAdapter(displayName);
        int* levels = stackalloc int[2] { DxgiNative.FeatureLevel11_1, DxgiNative.FeatureLevel11_0 };
        nint device = 0, context = 0;
        int level = 0;
        var flags = DxgiNative.CreateBgraSupport | DxgiNative.CreateVideoSupport;
        hr = DxgiNative.D3D11CreateDevice(_adapter, _adapter != 0 ? DxgiNative.DriverTypeUnknown : DxgiNative.DriverTypeWarp, 0, flags,
            levels, 2, DxgiNative.SdkVersion, &device, &level, &context);
        VideoSupport = hr >= 0;
        if (hr < 0 && (flags & DxgiNative.CreateVideoSupport) != 0)
        {
            // WARP and some drivers have no video support: the renderer does not need it, decoders say so on their own.
            hr = DxgiNative.D3D11CreateDevice(_adapter, _adapter != 0 ? DxgiNative.DriverTypeUnknown : DxgiNative.DriverTypeWarp, 0,
                DxgiNative.CreateBgraSupport, levels, 2, DxgiNative.SdkVersion, &device, &level, &context);
        }
        if (hr < 0)
            throw new GpuPlatformStartException(DxgiStartStage.Device, $"Direct3D 11 made no device at feature level 11 ({Com.Hr(hr)})");
        _device = device;
        _context = context;
        Warp = _adapter == 0;
        if (Warp)
            Name = "Microsoft Basic Render Driver (WARP)";
        var mt = Com.Query(_device, DxgiNative.IidMultithread);
        if (mt != 0)
        {
            _ = ((delegate* unmanaged<nint, int, int>)Com.Slot(mt, ComSlots.MultithreadSetMultithreadProtected))(mt, 1);
            Com.Release(ref mt);
            MultithreadProtected = true;
        }
    }

    /// <summary>The adapter whose output list holds the display, else the first adapter; 0 when the factory lists none.</summary>
    private nint ChooseAdapter(string? displayName)
    {
        nint first = 0;
        for (uint i = 0; ; i++)
        {
            nint adapter = 0;
            var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Com.Slot(_factory, ComSlots.FactoryEnumAdapters1))(_factory, i, &adapter);
            if (hr < 0)
                break;
            var holds = displayName is not null && HoldsDisplay(adapter, displayName);
            if (first == 0 || holds)
            {
                if (first != 0 && first != adapter)
                    Com.Release(ref first);
                first = adapter;
                Describe(adapter);
                if (holds)
                    return adapter;
            }
            else
            {
                Com.Release(ref adapter);
            }
        }
        return first;
    }

    private void Describe(nint adapter)
    {
        DxgiAdapterDesc1 desc = default;
        if (((delegate* unmanaged<nint, DxgiAdapterDesc1*, int>)Com.Slot(adapter, ComSlots.AdapterGetDesc1))(adapter, &desc) < 0)
            return;
        Name = DxgiNative.Text(&desc, DxgiAdapterDesc1.DescriptionOffset, DxgiAdapterDesc1.DescriptionChars);
        Luid = ((long)desc.LuidHigh << 32) | desc.LuidLow;
    }

    private static bool HoldsDisplay(nint adapter, string displayName)
    {
        for (uint j = 0; ; j++)
        {
            nint output = 0;
            if (((delegate* unmanaged<nint, uint, nint*, int>)Com.Slot(adapter, ComSlots.AdapterEnumOutputs))(adapter, j, &output) < 0)
                return false;
            DxgiOutputDesc desc = default;
            var ok = ((delegate* unmanaged<nint, DxgiOutputDesc*, int>)Com.Slot(output, ComSlots.OutputGetDesc))(output, &desc) >= 0;
            Com.Release(ref output);
            if (ok && string.Equals(DxgiNative.Text(&desc, 0, DxgiOutputDesc.DeviceNameChars), displayName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
    }

    /// <summary>Why the device was removed, in words ("DXGI_ERROR_DEVICE_HUNG 0x887A0006"), or "" while it is alive.</summary>
    public string RemovedReason()
    {
        if (_device == 0)
            return "";
        var hr = ((delegate* unmanaged<nint, int>)Com.Slot(_device, ComSlots.DeviceGetDeviceRemovedReason))(_device);
        return hr == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"device removed {Com.Hr(hr)}");
    }

    public void Dispose()
    {
        Com.Release(ref _context);
        Com.Release(ref _device);
        Com.Release(ref _adapter);
        Com.Release(ref _factory);
    }
}
