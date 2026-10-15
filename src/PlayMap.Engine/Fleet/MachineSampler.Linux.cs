using System.Globalization;

namespace PlayMap.Engine.Fleet;

/// <summary>Linux's counters: <c>/proc/stat</c> (every CPU's jiffies; busy is all but idle and iowait), <c>/proc/meminfo</c>
/// (MemAvailable), <c>/proc/net/dev</c> (bytes in and out of every interface but loopback), <c>/proc/uptime</c>, the free space of the
/// volume the node's home is on, and the GPU from <c>/sys/class/drm/card*/device</c> (<c>gpu_busy_percent</c>, and a hwmon
/// <c>temp1_input</c> in millidegrees) where the driver gives it. A file that cannot be read reads −1. The roots are parameters so a
/// test reads a tree of its own.</summary>
public sealed class LinuxMachineReader(string diskPath, string procRoot = "/proc", string sysRoot = "/sys") : IMachineReader
{
    /// <summary>Why the GPU reads unknown when no card under /sys/class/drm gives a load or a temperature.</summary>
    public const string NoGpuWords =
        "no GPU counter under /sys/class/drm on this machine (gpu_busy_percent, hwmon temp1_input) — NVIDIA's numbers need NVML, next round";

    public MachineReading Read()
    {
        var (busy, total) = Cpu();
        var (gpuPct, gpuTemp) = Gpu();
        return new MachineReading(busy, total, MemAvailableMb(), DiskFreeGb(), NetBytes(), Uptime(), gpuPct, gpuTemp,
            gpuPct < 0 && gpuTemp < 0 ? NoGpuWords : "");
    }

    private (long Busy, long Total) Cpu()
    {
        var line = FirstLine(Path.Combine(procRoot, "stat"));
        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
            return (-1, -1);
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long total = 0, idle = 0;
        // user nice system idle iowait irq softirq steal (guest time is already inside user and nice).
        for (var i = 1; i < parts.Length && i <= 8; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                return (-1, -1);
            total += v;
            if (i is 4 or 5)
                idle += v;
        }
        return (total - idle, total);
    }

    private double MemAvailableMb()
    {
        foreach (var line in Lines(Path.Combine(procRoot, "meminfo")))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb) ? kb / 1024.0 : -1;
        }
        return -1;
    }

    private long NetBytes()
    {
        var lines = Lines(Path.Combine(procRoot, "net", "dev"));
        if (lines.Count == 0)
            return -1;
        long sum = 0;
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || line[..colon].Trim() == "lo")
                continue;
            var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // receive bytes is the first column, transmit bytes the ninth.
            if (parts.Length >= 9
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rx)
                && long.TryParse(parts[8], NumberStyles.None, CultureInfo.InvariantCulture, out var tx))
                sum += rx + tx;
        }
        return sum;
    }

    private double Uptime()
    {
        var line = FirstLine(Path.Combine(procRoot, "uptime"));
        var first = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : -1;
    }

    private double DiskFreeGb()
    {
        try
        {
            var full = Path.GetFullPath(diskPath);
            return new DriveInfo(full).AvailableFreeSpace / (1024.0 * 1024 * 1024);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private (double Pct, double TempC) Gpu()
    {
        var drm = Path.Combine(sysRoot, "class", "drm");
        if (!Directory.Exists(drm))
            return (-1, -1);
        double pct = -1, temp = -1;
        try
        {
            foreach (var card in Directory.EnumerateDirectories(drm, "card*").Order(StringComparer.Ordinal))
            {
                if (Path.GetFileName(card).Contains('-', StringComparison.Ordinal))
                    continue;   // a connector (card0-HDMI-A-1), not a card
                var device = Path.Combine(card, "device");
                if (pct < 0 && FirstLine(Path.Combine(device, "gpu_busy_percent")) is { } busy
                    && double.TryParse(busy, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                    pct = b;
                var hwmon = Path.Combine(device, "hwmon");
                if (temp < 0 && Directory.Exists(hwmon))
                {
                    foreach (var mon in Directory.EnumerateDirectories(hwmon).Order(StringComparer.Ordinal))
                    {
                        if (FirstLine(Path.Combine(mon, "temp1_input")) is { } milli
                            && double.TryParse(milli, NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
                        {
                            temp = m / 1000.0;
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (pct, temp);
        }
        return (pct, temp);
    }

    private static string? FirstLine(string path)
    {
        var lines = Lines(path);
        return lines.Count > 0 ? lines[0].Trim() : null;
    }

    private static List<string> Lines(string path)
    {
        try
        {
            return File.Exists(path) ? [.. File.ReadLines(path)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
