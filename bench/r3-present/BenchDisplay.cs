using System.Runtime.Versioning;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace PlayMap.Bench.Present;

/// <summary>The display every path of a run draws on: the n-th display DXGI lists (adapters in order, then each adapter's
/// displays), its desktop rectangle and the adapter that drives it.</summary>
[SupportedOSPlatform("windows10.0.19043")]
internal sealed record BenchDisplay(int Index, string Name, uint Adapter, string AdapterName, int X, int Y, int Width, int Height)
{
    public static unsafe BenchDisplay Find(int index)
    {
        IDXGIFactory6* factory = null;
        try
        {
            if (FAILED(CreateDXGIFactory2(0, __uuidof<IDXGIFactory6>(), (void**)&factory)))
                throw new InvalidOperationException("CreateDXGIFactory2 failed: no DXGI on this machine");
            var seen = 0;
            for (uint a = 0; ; a++)
            {
                IDXGIAdapter1* adapter = null;
                if (FAILED(factory->EnumAdapters1(a, &adapter)))
                    break;
                try
                {
                    DXGI_ADAPTER_DESC1 adapterDesc;
                    _ = adapter->GetDesc1(&adapterDesc);
                    for (uint o = 0; ; o++)
                    {
                        IDXGIOutput* output = null;
                        if (FAILED(adapter->EnumOutputs(o, &output)))
                            break;
                        DXGI_OUTPUT_DESC desc;
                        _ = output->GetDesc(&desc);
                        _ = output->Release();
                        if (seen++ != index)
                            continue;
                        var r = desc.DesktopCoordinates;
                        return new BenchDisplay(index, new string((char*)&desc.DeviceName).TrimEnd('\0'), a,
                            new string((char*)&adapterDesc.Description).TrimEnd('\0'), r.left, r.top, r.right - r.left, r.bottom - r.top);
                    }
                }
                finally
                {
                    _ = adapter->Release();
                }
            }
            throw new InvalidOperationException($"no display {index}: this machine lists {seen}");
        }
        finally
        {
            if (factory is not null)
                _ = factory->Release();
        }
    }

    /// <summary>The flashing quad: a square an eighth of the display's height, centred at the top, a twentieth of the height down —
    /// where the lab's photodiode sits (L1: top-centre).</summary>
    public RECT Quad
    {
        get
        {
            var side = Math.Max(8, Height / 8);
            var left = (Width - side) / 2;
            var top = Height / 20;
            return new RECT { left = left, top = top, right = left + side, bottom = top + side };
        }
    }
}
