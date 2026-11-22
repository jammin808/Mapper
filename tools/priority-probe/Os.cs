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
    /// Read from <see cref="CoreSets"/>; null with the words why where it is not read.</summary>
    public static (int Logical, int Cores, bool Smt)? Topology(out string words) =>
        CoreSets(out words) is { } layout ? (layout.Cores.Sum(c => c.Processors.Count), layout.Cores.Count, layout.Smt) : null;

    /// <summary>Each physical core with its logical processors, in the OS's own order, and whether any core runs two threads (SMT). On
    /// Windows from GetLogicalProcessorInformationEx's core records (each one's group and the bits of its group mask, the flag LTP_PC_SMT);
    /// on Linux from /sys/devices/system/cpu (each logical processor's package and core id, and smt/active). Read, never set; null with
    /// the words why where it is not read.</summary>
    public static (IReadOnlyList<CoreSet> Cores, bool Smt)? CoreSets(out string words)
    {
        words = "";
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsCores(out words);
            if (OperatingSystem.IsLinux())
                return LinuxCores(out words);
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

    private static (IReadOnlyList<CoreSet> Cores, bool Smt)? WindowsCores(out string words)
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
            // Reserved (20), GroupCount (2, at 30), and GroupCount GROUP_AFFINITY of 16 bytes from 32: a KAFFINITY mask (8), then the
            // group's number (2) and its reserved words.
            var cores = new List<CoreSet>();
            var smt = false;
            var offset = 0;
            while (offset + 32 <= length)
            {
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0 || offset + size > length)
                    break;
                if (Marshal.ReadInt32(buffer, offset) == RelationProcessorCore)
                {
                    smt |= (Marshal.ReadByte(buffer, offset + 8) & 1) != 0;
                    var groups = (ushort)Marshal.ReadInt16(buffer, offset + 30);
                    var processors = new List<int>();
                    var group = -1;
                    for (var g = 0; g < groups && offset + 32 + (g + 1) * 16 <= offset + size; g++)
                    {
                        var mask = (ulong)Marshal.ReadInt64(buffer, offset + 32 + g * 16);
                        var number = (ushort)Marshal.ReadInt16(buffer, offset + 32 + g * 16 + 8);
                        // A core spans one group; a record naming two is kept under the first, its numbers counted as they are.
                        group = group < 0 ? number : group;
                        for (var bit = 0; bit < 64; bit++)
                        {
                            if ((mask & (1UL << bit)) != 0)
                                processors.Add(bit);
                        }
                    }
                    cores.Add(new CoreSet(Math.Max(0, group), processors));
                }
                offset += size;
            }
            if (cores.Count == 0)
            {
                words = "topology not read: GetLogicalProcessorInformationEx returned no core";
                return null;
            }
            return (cores, smt);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (IReadOnlyList<CoreSet> Cores, bool Smt)? LinuxCores(out string words)
    {
        words = "";
        var cpus = Directory.GetDirectories("/sys/devices/system/cpu", "cpu*")
            .Where(d => Path.GetFileName(d).Length > 3 && Path.GetFileName(d)[3..].All(char.IsAsciiDigit))
            .Where(d => File.Exists(Path.Combine(d, "topology", "core_id")))
            .Select(d => (Id: int.Parse(Path.GetFileName(d)[3..], NumberStyles.Integer, CultureInfo.InvariantCulture),
                Core: File.ReadAllText(Path.Combine(d, "topology", "physical_package_id")).Trim() + "/" + File.ReadAllText(Path.Combine(d, "topology", "core_id")).Trim()))
            .OrderBy(c => c.Id)
            .ToList();
        if (cpus.Count == 0)
        {
            words = "topology not read: /sys/devices/system/cpu names no processor with a topology";
            return null;
        }
        // Cores in the order of their lowest logical processor, each with its logical processors in order.
        var cores = cpus.GroupBy(c => c.Core, StringComparer.Ordinal).Select(g => new CoreSet(0, g.Select(c => c.Id).ToList())).ToList();
        var smtFile = "/sys/devices/system/cpu/smt/active";
        var smt = File.Exists(smtFile) ? File.ReadAllText(smtFile).Trim() == "1" : cores.Any(c => c.Processors.Count > 1);
        return (cores, smt);
    }

    /// <summary>The plan of a whole-core-free leg (high-core1, normal-core1): the logical processors of every core but the last the OS
    /// lists (its order, read from <see cref="CoreSets"/>) are the burner's, one thread to each, and the last core's are left free. Made
    /// only on one processor group with two or more cores; otherwise not made, with the words why, and the leg runs the burner on the
    /// logical processors less one, unpinned. On a machine without SMT every core but one is the n1 legs' shape, pinned, and the words
    /// say so. A plan, never a pin: only the burner pins, on Windows (<see cref="PinCurrentThread"/>).</summary>
    public static CorePlan WholeCoreFree()
    {
        var layout = CoreSets(out var words);
        var lessOne = Math.Max(1, Environment.ProcessorCount - 1);
        if (layout is not { } l)
            return new CorePlan(false, lessOne, [], [], false, words + "; the burner runs unpinned on the logical processors less one");
        if (l.Cores.Count < 2)
            return new CorePlan(false, lessOne, [], [], l.Smt, "one core: no core can be left free; the burner runs unpinned on the logical processors less one");
        if (l.Cores.Any(c => c.Group != 0))
            return new CorePlan(false, lessOne, [], [], l.Smt, "more than one processor group: this probe pins within group 0 only; the burner runs unpinned on the logical processors less one");
        var free = l.Cores[^1].Processors.OrderBy(p => p).ToList();
        var pinned = l.Cores.Take(l.Cores.Count - 1).SelectMany(c => c.Processors).OrderBy(p => p).ToList();
        // Words without commas: a cell's comma would be written as a semicolon.
        var shape = l.Smt
            ? $"core {l.Cores.Count.ToString(CultureInfo.InvariantCulture)} of {l.Cores.Count.ToString(CultureInfo.InvariantCulture)} left free (its logical processors {Join(free)})"
            : "no SMT: every core but one is the n1 legs' shape (the logical processors less one) with one thread pinned to each where the burner pins";
        return new CorePlan(true, pinned.Count, pinned, free, l.Smt, shape);
    }

    /// <summary>Logical processors as one cell: "0;1".</summary>
    public static string Join(IEnumerable<int> processors) => string.Join(';', processors.Select(p => p.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Pins the calling thread to one logical processor of group 0 (SetThreadAffinityMask): "" when Windows took it, else the
    /// words why. Windows only; elsewhere nothing is pinned and the words say so.</summary>
    public static string PinCurrentThread(int processor)
    {
        if (!OperatingSystem.IsWindows())
            return "not pinned: pinning is Windows' (SetThreadAffinityMask); nothing is pinned on " + RuntimeInformation.OSDescription;
        if (processor is < 0 or > 63)
            return "not pinned: logical processor " + processor.ToString(CultureInfo.InvariantCulture) + " is outside group 0's mask";
        return SetThreadAffinityMask(GetCurrentThread(), (nuint)(1UL << processor)) != 0
            ? ""
            : "not pinned: SetThreadAffinityMask failed (error " + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>The plan of the pinned one-free leg (high-n1-pinned): the burner's threads pinned one to each of logical processors 0 to
    /// n-2, logical processor n-1 left free, so the same one logical processor stays free that the unpinned n1 legs leave free on
    /// average and only the pinning differs from them. Made on one processor group of two or more logical processors; the words say
    /// whether the free one's core has another logical processor (on SMT the free one is the sibling of a busy one, never a whole
    /// core), read from <see cref="CoreSets"/> where it reads. A plan, never a pin: only the burner pins, on Windows.</summary>
    public static CorePlan LessOnePinned()
    {
        var n = Environment.ProcessorCount;
        var lessOne = Math.Max(1, n - 1);
        if (n < 2)
            return new CorePlan(false, lessOne, [], [], false, "one logical processor: none can be left free; the burner runs unpinned");
        if (n > 64)
            return new CorePlan(false, lessOne, [], [], false, "more than 64 logical processors: this probe pins within group 0 only; the burner runs unpinned");
        var pinned = Enumerable.Range(0, n - 1).ToList();
        var free = n - 1;
        var layout = CoreSets(out var words);
        var shape = layout is { } l && l.Cores.FirstOrDefault(c => c.Processors.Contains(free)) is { } core
            ? core.Processors.Count > 1
                ? $"logical processor {free.ToString(CultureInfo.InvariantCulture)} left free: its core's other logical processors ({Join(core.Processors.Where(p => p != free))}) are pinned busy (SMT: no whole core is free)"
                : $"logical processor {free.ToString(CultureInfo.InvariantCulture)} left free: a core of its own (no SMT sibling)"
            : $"logical processor {free.ToString(CultureInfo.InvariantCulture)} left free; its core not read ({words})";
        return new CorePlan(true, pinned.Count, pinned, [free], layout?.Smt ?? false, shape.Replace(',', ';'));
    }

    /// <summary>Another process's processor time so far in milliseconds, or NaN where it is gone or cannot be read.</summary>
    public static double ProcessCpuMs(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.TotalProcessorTime.TotalMilliseconds;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return double.NaN;
        }
    }

    /// <summary>Another process's class and base priority as the OS reports them now, read through a fresh process object; words in place
    /// of a value where the read fails (on Linux the class is what the runtime maps the process's nice value to).</summary>
    public static (string Class, string Base) ReadClassOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string cls;
            try
            {
                cls = p.PriorityClass.ToString();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or NotSupportedException)
            {
                cls = "unread: " + e.Message;
            }
            return (cls, p.BasePriority.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return ("unread: " + e.Message, "unread");
        }
    }

    /// <summary>Every thread of a process with its name and its processor time in milliseconds, read now: on Windows each thread opened for
    /// limited query (its description, which the runtime sets from a managed thread's name, and its times, counted in clock ticks); on
    /// Linux /proc/PID/task (each thread's comm and its user and system time in clock ticks). A thread that ends between the list and
    /// its read, or cannot be opened, is left out; a thread with no name reads "(no name)". Empty where the process cannot be read.</summary>
    public static List<ThreadTime> Threads(int pid)
    {
        var list = new List<ThreadTime>();
        try
        {
            if (OperatingSystem.IsWindows())
                WindowsThreads(pid, list);
            else if (OperatingSystem.IsLinux())
                LinuxThreads(pid, list);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException
                                      or UnauthorizedAccessException or NotSupportedException)
        {
            // The process is gone or not readable: the threads read so far stand, the rest are not there to read.
            list.Add(new ThreadTime(-1, "(not read: " + e.GetType().Name + ")", double.NaN));
        }
        return list;
    }

    private const uint ThreadQueryLimitedInformation = 0x0800;

    private static void WindowsThreads(int pid, List<ThreadTime> list)
    {
        using var p = Process.GetProcessById(pid);
        foreach (ProcessThread t in p.Threads)
        {
            var handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)t.Id);
            if (handle == IntPtr.Zero)
                continue;
            try
            {
                if (!GetThreadTimes(handle, out _, out _, out var kernel, out var user))
                    continue;
                var name = "(no name)";
                if (GetThreadDescription(handle, out var text) >= 0 && text != IntPtr.Zero)
                {
                    var read = Marshal.PtrToStringUni(text);
                    _ = LocalFree(text);
                    if (!string.IsNullOrWhiteSpace(read))
                        name = read;
                }
                list.Add(new ThreadTime(t.Id, Clean(name), (kernel + user) / 1e4));
            }
            finally
            {
                _ = CloseHandle(handle);
            }
        }
    }

    private static void LinuxThreads(int pid, List<ThreadTime> list)
    {
        var tick = sysconf(2);
        var perTick = tick > 0 ? 1000.0 / tick : 10.0;
        foreach (var dir in Directory.GetDirectories($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/task"))
        {
            string raw;
            try
            {
                raw = File.ReadAllText(Path.Combine(dir, "stat"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The thread ended between the list and its read: it is not there to read.
                continue;
            }
            var open = raw.IndexOf('(', StringComparison.Ordinal);
            var close = raw.LastIndexOf(')');
            if (open < 0 || close < open)
                continue;
            var fields = raw[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 13 || !long.TryParse(fields[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var utime)
                || !long.TryParse(fields[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stime))
                continue;
            var name = raw[(open + 1)..close];
            list.Add(new ThreadTime(int.Parse(Path.GetFileName(dir), NumberStyles.Integer, CultureInfo.InvariantCulture),
                Clean(name.Length > 0 ? name : "(no name)"), (utime + stime) * perTick));
        }
    }

    /// <summary>A name as one cell: no comma, no line break, no control character.</summary>
    private static string Clean(string name) => new([.. name.Select(c => c is ',' ? ';' : char.IsControl(c) ? ' ' : c)]);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadDescription(IntPtr thread, out IntPtr description);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("libc")]
    private static partial long sysconf(int name);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint SetThreadAffinityMask(IntPtr thread, nuint mask);

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

/// <summary>One thread of a process as <see cref="Os.Threads"/> read it: its id, its name and its processor time in milliseconds.</summary>
internal readonly record struct ThreadTime(int Id, string Name, double CpuMs);

/// <summary>One physical core: its processor group and its logical processors' numbers within it.</summary>
internal sealed record CoreSet(int Group, IReadOnlyList<int> Processors);

/// <summary>A whole-core-free leg's plan (<see cref="Os.WholeCoreFree"/>): whether it was made, the burner's thread count, the logical
/// processors it pins one thread to each and those it leaves free, whether the machine has SMT, and the words.</summary>
internal sealed record CorePlan(bool Made, int Threads, IReadOnlyList<int> Pinned, IReadOnlyList<int> Free, bool Smt, string Words);

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
