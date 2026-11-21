using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PlayMap.Core.Machine;
using PlayMap.Platform.Windows;

namespace PlayMap.PriorityProbe;

/// <summary>What the probe reads from and asks of the operating system: the calling thread's processor time, the process's class as
/// the OS reports it, and (on Windows only) the class the product's own platform claims. Every read is guarded: a read that fails is
/// a word in a cell, never a throw.</summary>
internal static partial class Os
{
    private const int ClockThreadCpuTime = 3;

    /// <summary>The calling thread's processor time in nanoseconds, or -1 where it is not read. On Windows it is counted in clock ticks
    /// (15.625 ms at the default resolution), so one 10 ms piece of work reads 0 or a tick: it is meaningful summed over a window only.
    /// On Linux it is the thread's own CPU clock, exact. Allocates nothing.</summary>
    public static long ThreadCpuNs()
    {
        if (OperatingSystem.IsWindows())
            return GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? (kernel + user) * 100 : -1;
        if (OperatingSystem.IsLinux())
            return clock_gettime(ClockThreadCpuTime, out var ts) == 0 ? ts.Seconds * 1_000_000_000L + ts.Nanoseconds : -1;
        return -1;
    }

    /// <summary>The process's class as the OS reports it now, read through a fresh process object (one object caches the class it
    /// read first), with its base priority; words in place of a value when the read fails.</summary>
    public static (string Class, string Base) ReadClass()
    {
        string cls;
        string bas;
        try
        {
            using var self = Process.GetCurrentProcess();
            cls = self.PriorityClass.ToString();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or NotSupportedException)
        {
            cls = "unread: " + e.Message;
        }
        try
        {
            using var self = Process.GetCurrentProcess();
            bas = self.BasePriority.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or NotSupportedException)
        {
            bas = "unread";
        }
        return (cls, bas);
    }

    /// <summary>The process's processor time so far, in milliseconds.</summary>
    public static double ProcessCpuMs()
    {
        using var self = Process.GetCurrentProcess();
        return self.TotalProcessorTime.TotalMilliseconds;
    }

    /// <summary>The class a desk is started at by its own hand: Normal, set on the process and read back through a fresh object.
    /// Windows only: elsewhere nothing is set and the words say so.</summary>
    public static (bool Set, string Words) SetNormal()
    {
        if (!OperatingSystem.IsWindows())
            return (false, "not set: a priority class is Windows' (here the runtime maps a class to a nice value); nothing is set on " + RuntimeInformation.OSDescription);
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.Normal;
            return (true, "");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (false, "Windows refused Normal: " + e.Message);
        }
    }

    /// <summary>The platform whose timer the probe waits on: Windows' own (whose timer is the high-resolution waitable timer under the
    /// default TIMER HIGH), or Core's null platform elsewhere (the runtime's own wait).</summary>
    public static IMachinePlatform Platform() =>
        OperatingSystem.IsWindows() ? WindowsPlatform.Make() : new NullMachinePlatform("not Windows: the probe's frame loop waits on the runtime's own wait");

    /// <summary>The system timer's resolution as NtQueryTimerResolution reports it to this process, in 100 ns units under that call's own
    /// names (minimum, maximum, current), or null with the words why: a reading, never set. Windows only.</summary>
    public static (uint Minimum, uint Maximum, uint Current)? TimerResolution(out string words)
    {
        words = "";
        if (!OperatingSystem.IsWindows())
        {
            words = "timer resolution not read: NtQueryTimerResolution is Windows'";
            return null;
        }
        try
        {
            var status = NtQueryTimerResolution(out var minimum, out var maximum, out var current);
            if (status == 0)
                return (minimum, maximum, current);
            words = "timer resolution not read: NtQueryTimerResolution answered 0x" + status.ToString("X8", CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            words = "timer resolution not read: " + e.Message;
        }
        return null;
    }

    /// <summary>The current timer resolution in 100 ns units as text, or "" when it is not read: one cell, allocation aside.</summary>
    public static string TimerCurrent() => TimerResolution(out _) is { } t ? t.Current.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>The quantum's setting Windows schedules by (HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl, value
    /// Win32PrioritySeparation), read, never set; null with the words why where it is not read.</summary>
    public static int? PrioritySeparation(out string words)
    {
        words = "";
        if (!OperatingSystem.IsWindows())
        {
            words = "Win32PrioritySeparation not read: a Windows registry value";
            return null;
        }
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl", writable: false);
            if (key?.GetValue("Win32PrioritySeparation") is int value)
                return value;
            words = "Win32PrioritySeparation not read: " + (key is null ? "PriorityControl is not there" : "the value is not there or not a DWORD");
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            words = "Win32PrioritySeparation not read: " + e.Message;
        }
        return null;
    }

    /// <summary>The processors as the OS lays them out: logical processors, physical cores, and whether any core runs two threads (SMT).
    /// On Windows from GetLogicalProcessorInformationEx's core records (their count, each one's flag LTP_PC_SMT, the bits of their group
    /// masks); on Linux from /sys/devices/system/cpu (each logical processor's package and core id, and smt/active). Read, never set;
    /// null with the words why where it is not read.</summary>
    public static (int Logical, int Cores, bool Smt)? Topology(out string words)
    {
        words = "";
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsTopology(out words);
            if (OperatingSystem.IsLinux())
                return LinuxTopology(out words);
            words = "topology not read on " + RuntimeInformation.OSDescription;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OutOfMemoryException)
        {
            words = "topology not read: " + e.Message;
        }
        return null;
    }

    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    private static (int Logical, int Cores, bool Smt)? WindowsTopology(out string words)
    {
        words = "";
        uint length = 0;
        if (GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length) || Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || length == 0)
        {
            words = "topology not read: GetLogicalProcessorInformationEx gave no size (error " + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + ")";
            return null;
        }
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
            {
                words = "topology not read: GetLogicalProcessorInformationEx failed (error " + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + ")";
                return null;
            }
            // Each record: Relationship (4 bytes), Size (4), then PROCESSOR_RELATIONSHIP: Flags (1; LTP_PC_SMT is 1), EfficiencyClass (1),
            // Reserved (20), GroupCount (2, at 30), and GroupCount GROUP_AFFINITY of 16 bytes from 32, each a KAFFINITY mask first.
            var cores = 0;
            var logical = 0;
            var smt = false;
            var offset = 0;
            while (offset + 32 <= length)
            {
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0 || offset + size > length)
                    break;
                if (Marshal.ReadInt32(buffer, offset) == RelationProcessorCore)
                {
                    cores++;
                    smt |= (Marshal.ReadByte(buffer, offset + 8) & 1) != 0;
                    var groups = (ushort)Marshal.ReadInt16(buffer, offset + 30);
                    for (var g = 0; g < groups && offset + 32 + (g + 1) * 16 <= offset + size; g++)
                        logical += System.Numerics.BitOperations.PopCount((ulong)Marshal.ReadInt64(buffer, offset + 32 + g * 16));
                }
                offset += size;
            }
            if (cores == 0)
            {
                words = "topology not read: GetLogicalProcessorInformationEx returned no core";
                return null;
            }
            return (logical, cores, smt);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (int Logical, int Cores, bool Smt)? LinuxTopology(out string words)
    {
        words = "";
        var cpus = Directory.GetDirectories("/sys/devices/system/cpu", "cpu*")
            .Where(d => Path.GetFileName(d).Length > 3 && Path.GetFileName(d)[3..].All(char.IsAsciiDigit))
            .Where(d => File.Exists(Path.Combine(d, "topology", "core_id")))
            .ToList();
        if (cpus.Count == 0)
        {
            words = "topology not read: /sys/devices/system/cpu names no processor with a topology";
            return null;
        }
        var cores = cpus.Select(d => File.ReadAllText(Path.Combine(d, "topology", "physical_package_id")).Trim() + "/" +
                                     File.ReadAllText(Path.Combine(d, "topology", "core_id")).Trim()).Distinct(StringComparer.Ordinal).Count();
        var smtFile = "/sys/devices/system/cpu/smt/active";
        var smt = File.Exists(smtFile) ? File.ReadAllText(smtFile).Trim() == "1" : cpus.Count > cores;
        return (cpus.Count, cores, smt);
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("libc")]
    private static partial int clock_gettime(int clock, out Timespec time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }
}

/// <summary>The product's own Windows platform: the class the burner claims is claimed by <see cref="WindowsMachinePlatform.ClaimProcess"/>
/// with <see cref="MachineSettings"/> whose Process is the leg's word and every other setting its default, so what is measured is the
/// product's claim, never a hand-set class.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPlatform
{
    public static IMachinePlatform Make() => new WindowsMachinePlatform();

    /// <summary>The product's process claims under <paramref name="process"/> ("high" or "normal"), each as "what asked→read back
    /// (applied|refused) words".</summary>
    public static (Claim? Priority, string Words) Claim(IMachinePlatform platform, string process)
    {
        var claims = platform.ClaimProcess(MachineSettings.Default with { Process = process });
        Claim? priority = null;
        var words = new List<string>();
        foreach (var c in claims)
        {
            if (c.What == "process.priority")
                priority = c;
            words.Add($"{c.What} asked {c.Asked} read {c.ReadBack} {(c.Applied ? "applied" : "refused")}{(c.Words.Length > 0 ? " (" + c.Words + ")" : "")}");
        }
        return (priority, string.Join("; ", words));
    }
}

/// <summary>The runner's scheduler, one row of readings (none is ever set): the quantum's setting, the timer's resolution and the
/// processors' layout, each a value or, where it is not read, an empty cell with the words why at the row's end. The calibration writes
/// it once for the job (before any leg) and each victim once more into its leg's folder at its end, after its own rows. A reading is
/// never a pass mark, so <see cref="Write"/> never throws: a reading that fails in a way its reader does not name leaves the row's
/// cells empty with the failure in its words cell, and a row that cannot be written is said on the output, never an exit code.</summary>
internal static class Scheduler
{
    public const string File = "scheduler.csv";

    public static void Write(string path, string when)
    {
        string[] row;
        try
        {
            row = Read(when);
        }
        catch (Exception e)
        {
            row = Rows.SchedulerHeader.Select(_ => "").ToArray();
            row[0] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            row[1] = when;
            row[^1] = $"the scheduler's reading failed: {e.GetType().Name}: {e.Message}";
        }
        try
        {
            Files.WriteCsv(path, Rows.SchedulerHeader, [row]);
        }
        catch (Exception e)
        {
            Program.Say($"{path} not written ({when}): {e.GetType().Name}: {e.Message}; the scheduler's reading is never a pass mark");
        }
    }

    private static string[] Read(string when)
    {
        var words = new List<string>();
        var separation = Os.PrioritySeparation(out var w1);
        var timer = Os.TimerResolution(out var w2);
        var topology = Os.Topology(out var w3);
        words.AddRange(new[] { w1, w2, w3 }.Where(w => w.Length > 0));
        string N(long? v) => v is { } x ? x.ToString(CultureInfo.InvariantCulture) : "";
        return
        [
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), when, RuntimeInformation.OSDescription,
            Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture), N(separation),
            N(timer?.Minimum), N(timer?.Maximum), N(timer?.Current),
            N(topology?.Logical), N(topology?.Cores), topology is { } t ? (t.Smt ? "yes" : "no") : "",
            string.Join("; ", words),
        ];
    }
}
