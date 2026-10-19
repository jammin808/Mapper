using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PlayMap.Render.D3D12.Tests;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D_FEATURE_LEVEL;
using static TerraFX.Interop.DirectX.D3D12_COMMAND_LIST_TYPE;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.DirectX.DXGI;
using static TerraFX.Interop.DirectX.DXGI_COLOR_SPACE_TYPE;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.DirectX.DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG;
using static TerraFX.Interop.DirectX.DXGI_SWAP_EFFECT;
using static TerraFX.Interop.Windows.CREATE;
using static TerraFX.Interop.Windows.PROCESS_INFORMATION_CLASS;
using static TerraFX.Interop.Windows.Windows;

namespace PlayMap.Bench.Probe;

/// <summary>One question's answer: its number in docs/round3/PLAN.md §1.5, the question, the verdict ("yes", "no", "refused" —
/// Windows answered with an error — or "could not ask") and what Windows said, call by call.</summary>
internal sealed record Answer(string Number, string Question, string Verdict, string Said);

/// <summary>The questions M7 and M14 put to the runner. Each asks Windows and keeps what it answered; nothing here decides what a
/// package does with the answer.</summary>
[SupportedOSPlatform("windows10.0.19043")]
internal static unsafe class Questions
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Asks one question; an exception is its answer ("could not ask", with the exception's words), never the probe's end.</summary>
    public static Answer Ask(string number, string question, Func<(string Verdict, string Said)> ask)
    {
        try
        {
            var (verdict, said) = ask();
            return new Answer(number, question, verdict, said);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or DllNotFoundException or EntryPointNotFoundException
            or UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            return new Answer(number, question, "could not ask", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Hex(int value) => string.Create(Invariant, $"0x{value:X8}");

    /// <summary>M7: the Agility SDK's runtime in <paramref name="folder"/>, loaded by this exe — a .NET apphost, which exports no
    /// D3D12SDKVersion — through ID3D12SDKConfiguration1::CreateDeviceFactory, as the Direct3D 12 test asks it.</summary>
    public static (string, string) Agility(uint sdk, string folder)
    {
        var exe = Path.GetFileName(Environment.ProcessPath ?? "");
        var exports = NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "D3D12SDKVersion", out _);
        if (sdk == 0)
            return ("could not ask", "no Agility SDK version was given (PLAYMAP_AGILITY_SDK)");
        if (!File.Exists(Path.Combine(folder, "D3D12Core.dll")))
            return ("could not ask", $"no D3D12Core.dll in {folder}");
        var answer = WarpDevice.Make(sdk, folder);
        var root = Path.GetFullPath(folder);
        var ours = answer.CoreModules.Any(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        var said = $"{exe} {(exports ? "exports" : "exports no")} D3D12SDKVersion; {string.Join("; ", answer.Steps)}; D3D12Core.dll loaded from " +
            $"{string.Join(", ", answer.CoreModules)}; d3d10warp.dll from {string.Join(", ", answer.WarpModules)}; the device ({answer.Runtime} runtime) " +
            $"at feature level {answer.FeatureLevelWord}, its debug layer {(answer.DebugLayer ? "on" : "off")}";
        return (answer.Runtime == "agility" && ours ? "yes" : "no", said);
    }

    /// <summary>M14: MMCSS for this thread, as the platform claims it for render and present ("Games") and for audio and timecode
    /// ("Pro Audio"), raised to high and reverted.</summary>
    public static (string, string) Mmcss()
    {
        var said = new List<string>();
        var all = true;
        foreach (var task in new[] { "Games", "Pro Audio" })
        {
            uint index = 0;
            var handle = Native.AvSetMmThreadCharacteristics(task, ref index);
            if (handle == 0)
            {
                all = false;
                said.Add(string.Create(Invariant, $"AvSetMmThreadCharacteristicsW(\"{task}\") error {Marshal.GetLastPInvokeError()}"));
                continue;
            }
            var raised = Native.AvSetMmThreadPriority(handle, Native.AvrtPriorityHigh);
            var raiseWords = raised ? "ok" : string.Create(Invariant, $"error {Marshal.GetLastPInvokeError()}");
            var reverted = Native.AvRevertMmThreadCharacteristics(handle);
            var revertWords = reverted ? "ok" : string.Create(Invariant, $"error {Marshal.GetLastPInvokeError()}");
            said.Add(string.Create(Invariant, $"\"{task}\": task index {index}, AvSetMmThreadPriority(HIGH) {raiseWords}, AvRevertMmThreadCharacteristics {revertWords}"));
            all = all && raised && reverted;
        }
        return (all ? "yes" : "no", string.Join("; ", said));
    }

    /// <summary>M14: HighQoS with every timer honoured (EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION in the control mask, a zero state
    /// mask), set and read back.</summary>
    public static (string, string) Throttling()
    {
        // PROCESS_POWER_THROTTLING_CURRENT_VERSION 1; EXECUTION_SPEED 0x1, IGNORE_TIMER_RESOLUTION 0x4 (processthreadsapi.h).
        var asked = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
        var size = (uint)sizeof(PROCESS_POWER_THROTTLING_STATE);
        bool set = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &asked, size);
        var setWords = set ? "ok" : string.Create(Invariant, $"error {Marshal.GetLastSystemError()}");
        var read = new PROCESS_POWER_THROTTLING_STATE { Version = 1 };
        bool got = GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &read, size);
        var getWords = got ? string.Create(Invariant, $"control 0x{read.ControlMask:X}, state 0x{read.StateMask:X}")
            : string.Create(Invariant, $"error {Marshal.GetLastSystemError()}");
        var said = string.Create(Invariant,
            $"SetProcessInformation(ProcessPowerThrottling, control 0x{asked.ControlMask:X}, state 0x0) {setWords}; GetProcessInformation(ProcessPowerThrottling) {getWords}");
        var verdict = !got ? "refused" : set && read.ControlMask == asked.ControlMask && read.StateMask == asked.StateMask ? "yes" : "no";
        return (verdict, said);
    }

    /// <summary>M14: the process's GPU scheduling class on the runner's adapter, raised to high and read back.</summary>
    public static (string, string) SchedulingClass()
    {
        using var self = Process.GetCurrentProcess();
        var handle = self.Handle;
        var before = Native.D3DKMTGetProcessSchedulingPriorityClass(handle, out var was);
        var set = Native.D3DKMTSetProcessSchedulingPriorityClass(handle, Native.SchedulingHigh);
        var after = Native.D3DKMTGetProcessSchedulingPriorityClass(handle, out var now);
        var said = $"D3DKMTGetProcessSchedulingPriorityClass {Hex(before)}: {Class(before, was)}; D3DKMTSetProcessSchedulingPriorityClass(high) {Hex(set)}; " +
            $"read back {Hex(after)}: {Class(after, now)}";
        var verdict = after != 0 ? "refused" : set == 0 && now == Native.SchedulingHigh ? "yes" : "no";
        return (verdict, said);
    }

    private static string Class(int status, int value) => status != 0 ? "no answer"
        : value >= 0 && value < Native.SchedulingClasses.Length ? Native.SchedulingClasses[value] : string.Create(Invariant, $"class {value}");

    /// <summary>M14: the runner's adapters and the displays on each, with each display's colour space as Windows reports it now.</summary>
    public static (string, string) Displays()
    {
        IDXGIFactory6* factory = null;
        var parts = new List<string>();
        var hdr = false;
        try
        {
            var hr = CreateDXGIFactory2(0, __uuidof<IDXGIFactory6>(), (void**)&factory);
            if (FAILED(hr))
                return ("refused", $"CreateDXGIFactory2 {Hex(hr.Value)}");
            for (uint i = 0; ; i++)
            {
                IDXGIAdapter1* adapter = null;
                if (FAILED(factory->EnumAdapters1(i, &adapter)))
                    break;
                try
                {
                    DXGI_ADAPTER_DESC1 desc;
                    _ = adapter->GetDesc1(&desc);
                    var name = new string((char*)&desc.Description).TrimEnd('\0');
                    var software = (desc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                    var outputs = Outputs(adapter, ref hdr);
                    parts.Add(string.Create(Invariant,
                        $"adapter {i} \"{name}\" (vendor 0x{desc.VendorId:X4}, device 0x{desc.DeviceId:X4}{(software ? ", software" : "")}): {(outputs.Count == 0 ? "no display" : string.Join(", ", outputs))}"));
                }
                finally
                {
                    _ = adapter->Release();
                }
            }
        }
        finally
        {
            if (factory is not null)
                _ = factory->Release();
        }
        return (hdr ? "yes" : "no", parts.Count == 0 ? "no adapter" : string.Join("; ", parts));
    }

    private static List<string> Outputs(IDXGIAdapter1* adapter, ref bool hdr)
    {
        var outputs = new List<string>();
        for (uint o = 0; ; o++)
        {
            IDXGIOutput* output = null;
            if (FAILED(adapter->EnumOutputs(o, &output)))
                break;
            IDXGIOutput6* six = null;
            try
            {
                if (FAILED(output->QueryInterface(__uuidof<IDXGIOutput6>(), (void**)&six)))
                {
                    outputs.Add(string.Create(Invariant, $"display {o} (no IDXGIOutput6)"));
                    continue;
                }
                DXGI_OUTPUT_DESC1 desc;
                _ = six->GetDesc1(&desc);
                var device = new string((char*)&desc.DeviceName).TrimEnd('\0');
                var space = desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ? "HDR10 (G2084, P2020)"
                    : desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 ? "SDR (G22, P709)" : string.Create(Invariant, $"colour space {(int)desc.ColorSpace}");
                hdr |= desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                outputs.Add(string.Create(Invariant,
                    $"{device} {desc.DesktopCoordinates.right - desc.DesktopCoordinates.left}×{desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top}, {space}, {desc.BitsPerColor} bits, {desc.MinLuminance}–{desc.MaxLuminance} nits"));
            }
            finally
            {
                if (six is not null)
                    _ = six->Release();
                _ = output->Release();
            }
        }
        return outputs;
    }

    /// <summary>M14: whether a flip swap chain on WARP, on a window of the runner's display, may present in HDR10 (and, beside it, in
    /// SDR), as CheckColorSpaceSupport answers.</summary>
    public static (string, string) Hdr10SwapChain()
    {
        IDXGIFactory4* factory = null;
        IDXGIAdapter1* adapter = null;
        ID3D12Device* device = null;
        ID3D12CommandQueue* queue = null;
        IDXGISwapChain1* chain = null;
        IDXGISwapChain3* three = null;
        try
        {
            Need(CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory), "CreateDXGIFactory2");
            Need(factory->EnumWarpAdapter(__uuidof<IDXGIAdapter1>(), (void**)&adapter), "EnumWarpAdapter");
            Need(D3D12CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device), "D3D12CreateDevice");
            var queueDesc = new D3D12_COMMAND_QUEUE_DESC { Type = D3D12_COMMAND_LIST_TYPE_DIRECT };
            Need(device->CreateCommandQueue(&queueDesc, __uuidof<ID3D12CommandQueue>(), (void**)&queue), "CreateCommandQueue");
            using var window = new BenchWindow("PlayMap probe", 0, 0, 64, 64, visible: false, borderless: true);
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = 64,
                Height = 64,
                Format = DXGI_FORMAT_R10G10B10A2_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD,
            };
            var hr = factory->CreateSwapChainForHwnd((IUnknown*)queue, window.Handle, &desc, null, null, &chain);
            if (FAILED(hr))
                return ("refused", $"CreateSwapChainForHwnd (R10G10B10A2, flip discard) {Hex(hr.Value)}");
            Need(chain->QueryInterface(__uuidof<IDXGISwapChain3>(), (void**)&three), "QueryInterface(IDXGISwapChain3)");
            uint hdr10 = 0, sdr = 0;
            var hrHdr = three->CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, &hdr10);
            var hrSdr = three->CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, &sdr);
            var present = (uint)DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT;
            var said = string.Create(Invariant,
                $"CheckColorSpaceSupport(HDR10) {Hex(hrHdr.Value)}: flags 0x{hdr10:X}; CheckColorSpaceSupport(SDR) {Hex(hrSdr.Value)}: flags 0x{sdr:X}");
            return (SUCCEEDED(hrHdr) && (hdr10 & present) != 0 ? "yes" : "no", said);
        }
        finally
        {
            if (three is not null)
                _ = three->Release();
            if (chain is not null)
                _ = chain->Release();
            if (queue is not null)
                _ = queue->Release();
            if (device is not null)
                _ = device->Release();
            if (adapter is not null)
                _ = adapter->Release();
            if (factory is not null)
                _ = factory->Release();
        }
    }

    private static void Need(HRESULT hr, string call)
    {
        if (FAILED(hr))
            throw new InvalidOperationException($"{call} answered {Hex(hr.Value)}");
    }

    /// <summary>M14 (M9's calls): who this process is and in which session; the console session; whether that session's user token
    /// comes (WTSQueryUserToken needs the TCB privilege, which LocalSystem holds); and, with the token, whether a process starts in that
    /// session (CreateProcessAsUser, the supervisor's start of the engine).</summary>
    public static (string, string) Sessions()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var admin = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        uint mine = 0;
        _ = ProcessIdToSessionId((uint)Environment.ProcessId, &mine);
        var console = WTSGetActiveConsoleSessionId();
        var said = new List<string>
        {
            string.Create(Invariant, $"{identity.Name}{(identity.IsSystem ? " (LocalSystem)" : admin ? " (an administrator)" : "")} in session {mine}"),
            console == 0xFFFFFFFF ? "no console session" : string.Create(Invariant, $"the console session is {console}"),
        };
        if (!Native.WTSQueryUserToken(console, out var token))
        {
            said.Add(string.Create(Invariant, $"WTSQueryUserToken({console}) error {Marshal.GetLastPInvokeError()}"));
            return ("no", string.Join("; ", said));
        }
        try
        {
            said.Add(string.Create(Invariant, $"WTSQueryUserToken({console}) gave a token"));
            var (started, words) = StartIn((HANDLE)token, console);
            said.Add(words);
            return (started ? "yes" : "no", string.Join("; ", said));
        }
        finally
        {
            _ = CloseHandle((HANDLE)token);
        }
    }

    /// <summary>Starts cmd.exe (which exits 7) with the session's token on its default desktop, waits for it and says where it ran.</summary>
    private static (bool, string) StartIn(HANDLE token, uint session)
    {
        var line = $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /c exit 7\0".ToCharArray();
        var startup = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
        PROCESS_INFORMATION process;
        bool made;
        fixed (char* command = line)
        fixed (char* desktop = "winsta0\\default")
        {
            startup.lpDesktop = desktop;
            made = CreateProcessAsUserW(token, null, command, null, null, false, CREATE_NO_WINDOW, null, null, &startup, &process);
        }
        if (!made)
            return (false, string.Create(Invariant, $"CreateProcessAsUserW error {Marshal.GetLastSystemError()}"));
        try
        {
            uint code = 0, ran = 0;
            _ = ProcessIdToSessionId(process.dwProcessId, &ran);
            var waited = WaitForSingleObject(process.hProcess, 10_000);
            _ = GetExitCodeProcess(process.hProcess, &code);
            var inSession = ran == session && code == 7;
            return (inSession, string.Create(Invariant,
                $"CreateProcessAsUserW started cmd.exe (pid {process.dwProcessId}) in session {ran}; wait {(waited == 0 ? "ended" : "timed out")}, exit code {code}"));
        }
        finally
        {
            _ = CloseHandle(process.hThread);
            _ = CloseHandle(process.hProcess);
        }
    }
}
