using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D_FEATURE_LEVEL;
using static TerraFX.Interop.DirectX.D3D12_DRED_ENABLEMENT;
using static TerraFX.Interop.DirectX.D3D12_FEATURE;
using static TerraFX.Interop.DirectX.D3D12_MESSAGE_SEVERITY;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace PlayMap.Render.D3D12.Tests;

/// <summary>One message the debug layer stored in the device's info queue.</summary>
internal readonly record struct InfoQueueMessage(string Severity, int Id, string Text)
{
    /// <summary>Warning, error and corruption: a Direct3D 12 test fails on any of them it does not name.</summary>
    public bool Counts => Severity is "corruption" or "error" or "warning";

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Severity} #{Id}: {Text}");
}

/// <summary>What making a WARP device answered, every call's result kept: the runtime that made it ("agility" — the Agility SDK's
/// runtime from the folder beside the host, through <c>ID3D12SDKConfiguration1::CreateDeviceFactory</c> — or "os", Windows' own),
/// each HRESULT on the way, the highest feature level the device reports, whether the debug layer was on, every WARP and Direct3D 12
/// core module the process holds afterwards (by path), the info queue's messages and DRED's state.</summary>
[SupportedOSPlatform("windows10.0.19043")]
internal sealed record WarpAnswer(
    string Runtime,
    IReadOnlyList<string> Steps,
    int FeatureLevel,
    string Adapter,
    bool DebugLayer,
    bool Dred,
    string RemovedReason,
    IReadOnlyList<string> WarpModules,
    IReadOnlyList<string> CoreModules,
    IReadOnlyList<InfoQueueMessage> Messages)
{
    /// <summary>The feature level as Direct3D writes it: "12_2".</summary>
    public string FeatureLevelWord => WarpDevice.LevelWord(FeatureLevel);
}

/// <summary>Makes one Direct3D 12 device on the WARP adapter through TerraFX's raw bindings, with the debug layer and DRED armed
/// before it, and reads back what it got. The Agility runtime is asked for first when an SDK version is given and its folder holds
/// D3D12Core.dll — the host is a .NET apphost, which exports no <c>D3D12SDKVersion</c>, so the device factory is the only way it
/// can load (M7) — and Windows' own runtime makes the device when that answers no, its debug layer present only where Windows'
/// Graphics Tools are. Every interface is released before it answers.</summary>
[SupportedOSPlatform("windows10.0.19043")]
internal static unsafe class WarpDevice
{
    private static readonly D3D_FEATURE_LEVEL[] Levels =
        [D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_12_0, D3D_FEATURE_LEVEL_12_1, D3D_FEATURE_LEVEL_12_2];

    /// <summary>"11_0" … "12_2" from a D3D_FEATURE_LEVEL value (0xc200 is 12_2).</summary>
    public static string LevelWord(int level) => string.Create(CultureInfo.InvariantCulture, $"{level >> 12}_{(level >> 8) & 0xF}");

    public static string Hex(HRESULT hr) => string.Create(CultureInfo.InvariantCulture, $"0x{hr.Value:X8}");

    public static WarpAnswer Make(uint sdkVersion, string agilityFolder)
    {
        IDXGIFactory4* dxgi = null;
        IDXGIAdapter1* adapter = null;
        ID3D12SDKConfiguration1* configuration = null;
        ID3D12DeviceFactory* factory = null;
        ID3D12Device* device = null;
        var steps = new List<string>();
        try
        {
            Need(CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&dxgi), "CreateDXGIFactory2", steps);
            Need(dxgi->EnumWarpAdapter(__uuidof<IDXGIAdapter1>(), (void**)&adapter), "IDXGIFactory4::EnumWarpAdapter", steps);
            DXGI_ADAPTER_DESC1 desc;
            Need(adapter->GetDesc1(&desc), "IDXGIAdapter1::GetDesc1", steps);
            var name = new string((char*)&desc.Description).TrimEnd('\0');

            var runtime = "os";
            bool debug = false, dred = false;
            if (sdkVersion != 0 && File.Exists(Path.Combine(agilityFolder, "D3D12Core.dll")))
            {
                var clsid = CLSID.CLSID_D3D12SDKConfiguration;
                var hr = D3D12GetInterface(&clsid, __uuidof<ID3D12SDKConfiguration1>(), (void**)&configuration);
                steps.Add($"D3D12GetInterface(SDKConfiguration1) {Hex(hr)}");
                if (SUCCEEDED(hr))
                    factory = Factory(configuration, sdkVersion, agilityFolder, steps);
                if (factory is not null)
                {
                    debug = FactoryDebug(factory, steps);
                    dred = FactoryDred(factory, steps);
                    hr = factory->CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device);
                    steps.Add($"ID3D12DeviceFactory::CreateDevice {Hex(hr)}");
                    if (SUCCEEDED(hr))
                        runtime = "agility";
                    else
                        device = null;
                }
            }
            if (device is null)
            {
                debug = OsDebug(steps);
                dred = OsDred(steps);
                Need(D3D12CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device), "D3D12CreateDevice", steps);
            }

            var level = MaxLevel(device, steps);
            var messages = Drain(device, steps);
            var removed = device->GetDeviceRemovedReason();
            var (warp, core) = Modules();
            return new WarpAnswer(runtime, steps, level, name, debug, dred, Hex(removed), warp, core, messages);
        }
        finally
        {
            if (device is not null)
                _ = device->Release();
            if (factory is not null)
                _ = factory->Release();
            if (configuration is not null)
                _ = configuration->Release();
            if (adapter is not null)
                _ = adapter->Release();
            if (dxgi is not null)
                _ = dxgi->Release();
        }
    }

    private static void Need(HRESULT hr, string call, List<string> steps)
    {
        steps.Add($"{call} {Hex(hr)}");
        if (FAILED(hr))
            throw new InvalidOperationException($"{call} answered {Hex(hr)}: {string.Join("; ", steps)}");
    }

    /// <summary>The device factory of the Agility runtime in <paramref name="folder"/>: asked first with the folder relative to the
    /// process's exe (the form the Agility SDK documents), then with its absolute path; each answer kept. Null when neither loads.</summary>
    private static ID3D12DeviceFactory* Factory(ID3D12SDKConfiguration1* configuration, uint sdkVersion, string folder, List<string> steps)
    {
        var exe = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var relative = Path.GetRelativePath(exe, folder);
        foreach (var path in new[] { $".\\{relative.TrimEnd('\\')}\\", Path.GetFullPath(folder).TrimEnd('\\') + "\\" })
        {
            ID3D12DeviceFactory* factory = null;
            var bytes = System.Text.Encoding.ASCII.GetBytes(path + "\0");
            HRESULT hr;
            fixed (byte* p = bytes)
                hr = configuration->CreateDeviceFactory(sdkVersion, (sbyte*)p, __uuidof<ID3D12DeviceFactory>(), (void**)&factory);
            steps.Add(string.Create(CultureInfo.InvariantCulture, $"ID3D12SDKConfiguration1::CreateDeviceFactory({sdkVersion}, \"{path}\") {Hex(hr)}"));
            if (SUCCEEDED(hr))
                return factory;
        }
        return null;
    }

    private static bool FactoryDebug(ID3D12DeviceFactory* factory, List<string> steps)
    {
        ID3D12Debug* debug = null;
        var clsid = CLSID.CLSID_D3D12Debug;
        var hr = factory->GetConfigurationInterface(&clsid, __uuidof<ID3D12Debug>(), (void**)&debug);
        steps.Add($"ID3D12DeviceFactory::GetConfigurationInterface(Debug) {Hex(hr)}");
        if (FAILED(hr))
            return false;
        debug->EnableDebugLayer();
        _ = debug->Release();
        return true;
    }

    private static bool FactoryDred(ID3D12DeviceFactory* factory, List<string> steps)
    {
        ID3D12DeviceRemovedExtendedDataSettings* settings = null;
        var clsid = CLSID.CLSID_D3D12DeviceRemovedExtendedData;
        var hr = factory->GetConfigurationInterface(&clsid, __uuidof<ID3D12DeviceRemovedExtendedDataSettings>(), (void**)&settings);
        steps.Add($"ID3D12DeviceFactory::GetConfigurationInterface(DRED) {Hex(hr)}");
        if (FAILED(hr))
            return false;
        Arm(settings);
        return true;
    }

    private static bool OsDebug(List<string> steps)
    {
        ID3D12Debug* debug = null;
        var hr = D3D12GetDebugInterface(__uuidof<ID3D12Debug>(), (void**)&debug);
        steps.Add($"D3D12GetDebugInterface(Debug) {Hex(hr)}");
        if (FAILED(hr))
            return false;
        debug->EnableDebugLayer();
        _ = debug->Release();
        return true;
    }

    private static bool OsDred(List<string> steps)
    {
        ID3D12DeviceRemovedExtendedDataSettings* settings = null;
        var hr = D3D12GetDebugInterface(__uuidof<ID3D12DeviceRemovedExtendedDataSettings>(), (void**)&settings);
        steps.Add($"D3D12GetDebugInterface(DRED) {Hex(hr)}");
        if (FAILED(hr))
            return false;
        Arm(settings);
        return true;
    }

    /// <summary>Auto-breadcrumbs and page-fault reporting forced on, before the device: what DRED reports on a removal.</summary>
    private static void Arm(ID3D12DeviceRemovedExtendedDataSettings* settings)
    {
        settings->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
        settings->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
        _ = settings->Release();
    }

    private static int MaxLevel(ID3D12Device* device, List<string> steps)
    {
        fixed (D3D_FEATURE_LEVEL* requested = Levels)
        {
            var data = new D3D12_FEATURE_DATA_FEATURE_LEVELS { NumFeatureLevels = (uint)Levels.Length, pFeatureLevelsRequested = requested };
            Need(device->CheckFeatureSupport(D3D12_FEATURE_FEATURE_LEVELS, &data, (uint)sizeof(D3D12_FEATURE_DATA_FEATURE_LEVELS)),
                "ID3D12Device::CheckFeatureSupport(FEATURE_LEVELS)", steps);
            return (int)data.MaxSupportedFeatureLevel;
        }
    }

    /// <summary>Every message the info queue stored (the queue exists only while the debug layer is on).</summary>
    private static List<InfoQueueMessage> Drain(ID3D12Device* device, List<string> steps)
    {
        var messages = new List<InfoQueueMessage>();
        ID3D12InfoQueue* queue = null;
        var hr = device->QueryInterface(__uuidof<ID3D12InfoQueue>(), (void**)&queue);
        steps.Add($"QueryInterface(ID3D12InfoQueue) {Hex(hr)}");
        if (FAILED(hr))
            return messages;
        try
        {
            var count = queue->GetNumStoredMessages();
            for (ulong i = 0; i < count; i++)
            {
                nuint length = 0;
                if (FAILED(queue->GetMessage(i, null, &length)) || length == 0)
                    continue;
                var buffer = NativeMemory.Alloc(length);
                try
                {
                    var message = (D3D12_MESSAGE*)buffer;
                    if (FAILED(queue->GetMessage(i, message, &length)))
                        continue;
                    var text = Marshal.PtrToStringAnsi((nint)message->pDescription) ?? "";
                    messages.Add(new InfoQueueMessage(Word(message->Severity), (int)message->ID, text));
                }
                finally
                {
                    NativeMemory.Free(buffer);
                }
            }
        }
        finally
        {
            _ = queue->Release();
        }
        return messages;
    }

    private static string Word(D3D12_MESSAGE_SEVERITY severity) => severity switch
    {
        D3D12_MESSAGE_SEVERITY_CORRUPTION => "corruption",
        D3D12_MESSAGE_SEVERITY_ERROR => "error",
        D3D12_MESSAGE_SEVERITY_WARNING => "warning",
        D3D12_MESSAGE_SEVERITY_INFO => "info",
        _ => "message",
    };

    /// <summary>Every WARP (d3d10warp.dll) and Direct3D 12 core (D3D12Core.dll) module the process holds, by full path.</summary>
    private static (List<string> Warp, List<string> Core) Modules()
    {
        var warp = new List<string>();
        var core = new List<string>();
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            var path = module.FileName;
            var file = Path.GetFileName(path);
            if (file.Equals("d3d10warp.dll", StringComparison.OrdinalIgnoreCase))
                warp.Add(path);
            else if (file.Equals("D3D12Core.dll", StringComparison.OrdinalIgnoreCase))
                core.Add(path);
            module.Dispose();
        }
        return (warp, core);
    }
}
