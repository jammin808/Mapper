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
