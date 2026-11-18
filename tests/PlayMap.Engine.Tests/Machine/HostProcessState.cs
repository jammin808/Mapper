using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlayMap.Engine.Tests.Machine;

/// <summary>What the machine part's process claims change in the process that makes them, read from the outside: the priority class
/// (read on every OS), and on Windows the power throttling state the QoS claim sets and the GPU scheduling class (each null where
/// Windows does not answer its read). A test that composes the real platform on the shared test host reads this before its claim and
/// puts it back after (<see cref="PutBack"/>). The product has no release of a process claim (<c>IMachinePlatform.ClaimProcess</c>
/// makes them; <c>WindowsMachinePlatform.Dispose</c> lets go of keep-awake only), so each part is set back here through the call the
/// platform claims it with. Off Windows the null platform claims nothing, so nothing moves and nothing is set back.</summary>
internal sealed partial record HostProcessState(ProcessPriorityClass Class, uint? QosControl, uint? QosState, int? GpuClass)
{
    /// <summary>This process's state as it reads now; the class through a fresh process object, since one object keeps the class it
    /// read first.</summary>
    public static HostProcessState Read()
    {
        var priority = ReadClass();
        if (!OperatingSystem.IsWindows())
            return new HostProcessState(priority, null, null, null);
        var qos = Calls.ReadThrottling();
        return new HostProcessState(priority, qos?.ControlMask, qos?.StateMask, Calls.ReadGpuClass());
    }

    /// <summary>Sets back each part that reads otherwise now, the priority class last, and returns what reads after it. A part this
    /// state could not read is neither set nor compared (it is null in what this returns too), so the two are equal exactly when every
    /// part read before reads the same again. A set Windows refuses is not thrown: the read after it says so.</summary>
    public HostProcessState PutBack()
    {
        if (OperatingSystem.IsWindows())
        {
            var now = Read();
            if (QosControl is { } control && QosState is { } state && (now.QosControl, now.QosState) != (control, state))
                _ = Calls.SetThrottling(control, state);
            if (GpuClass is { } gpu && now.GpuClass != gpu)
                _ = Calls.SetGpuClass(gpu);
        }
        if (ReadClass() != Class)
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = Class;
        }
        var after = Read();
        return after with
        {
            QosControl = QosControl is null ? null : after.QosControl,
            QosState = QosState is null ? null : after.QosState,
            GpuClass = GpuClass is null ? null : after.GpuClass,
        };
    }

    /// <summary>The state in words for a failure's message.</summary>
    public string Words => string.Create(CultureInfo.InvariantCulture,
        $"class {Class}, QoS {(QosControl is { } c && QosState is { } s ? $"control 0x{c:X} state 0x{s:X}" : "unread")}, GPU class {(GpuClass is { } g ? g.ToString(CultureInfo.InvariantCulture) : "unread")}");

    private static ProcessPriorityClass ReadClass()
    {
        using var self = Process.GetCurrentProcess();
        return self.PriorityClass;
    }

    /// <summary>The Win32 calls <c>WindowsMachinePlatform.ClaimProcess</c> makes its QoS and GPU claims with, on this process.</summary>
    [SupportedOSPlatform("windows")]
    private static partial class Calls
    {
        /// <summary>GetCurrentProcess's pseudo-handle: the calling process.</summary>
        private const nint Self = -1;

        // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling and PROCESS_POWER_THROTTLING_CURRENT_VERSION.
        private const int ProcessPowerThrottling = 4;
        private const uint ThrottlingVersion = 1;

        public static (uint ControlMask, uint StateMask)? ReadThrottling()
        {
            var state = new Throttling(ThrottlingVersion, 0, 0);
            return GetProcessInformation(Self, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<Throttling>())
                ? (state.ControlMask, state.StateMask)
                : null;
        }

        public static bool SetThrottling(uint controlMask, uint stateMask)
        {
            var state = new Throttling(ThrottlingVersion, controlMask, stateMask);
            return SetProcessInformation(Self, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<Throttling>());
        }

        public static int? ReadGpuClass() => D3DKMTGetProcessSchedulingPriorityClass(Self, out var read) < 0 ? null : read;

        public static bool SetGpuClass(int priority) => D3DKMTSetProcessSchedulingPriorityClass(Self, priority) >= 0;

        /// <summary>PROCESS_POWER_THROTTLING_STATE.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private record struct Throttling(uint Version, uint ControlMask, uint StateMask);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetProcessInformation(nint process, int informationClass, ref Throttling information, uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetProcessInformation(nint process, int informationClass, ref Throttling information, uint size);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int D3DKMTGetProcessSchedulingPriorityClass(nint process, out int priority);

        [LibraryImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priority);
    }
}
