using PlayMap.Engine.Fleet;

namespace PlayMap.Engine.Tests.Fleet;

/// <summary>The machine's numbers: Linux's /proc and /sys read for real here (CPU, memory, disk and uptime in range; the GPU unknown
/// on this machine, with words), a tree of the test's own read exactly, and the first sample's rates unknown until a second gives a
/// delta.</summary>
public sealed class MachineSamplerTests
{
    private static string Tree(string stat, string meminfo, string netdev, string uptime, int? gpuBusy = null, int? milliC = null)
    {
        var root = Directory.CreateTempSubdirectory("playmap-proc-").FullName;
        var proc = Path.Combine(root, "proc");
        Directory.CreateDirectory(Path.Combine(proc, "net"));
        File.WriteAllText(Path.Combine(proc, "stat"), stat);
        File.WriteAllText(Path.Combine(proc, "meminfo"), meminfo);
        File.WriteAllText(Path.Combine(proc, "net", "dev"), netdev);
        File.WriteAllText(Path.Combine(proc, "uptime"), uptime);
        var device = Path.Combine(root, "sys", "class", "drm", "card0", "device");
        Directory.CreateDirectory(device);
        Directory.CreateDirectory(Path.Combine(root, "sys", "class", "drm", "card0-HDMI-A-1"));
        if (gpuBusy is { } busy)
            File.WriteAllText(Path.Combine(device, "gpu_busy_percent"), busy.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        if (milliC is { } temp)
        {
            var mon = Path.Combine(device, "hwmon", "hwmon3");
            Directory.CreateDirectory(mon);
            File.WriteAllText(Path.Combine(mon, "temp1_input"), temp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }
        return root;
    }

    private const string Meminfo = "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nMemAvailable:    8192000 kB\n";

    private static string NetDev(long rx, long tx) =>
        "Inter-|   Receive                                                |  Transmit\n"
        + " face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\n"
        + "    lo: 999999 10 0 0 0 0 0 0 999999 10 0 0 0 0 0 0\n"
        + string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  eth0: {rx} 10 0 0 0 0 0 0 {tx} 10 0 0 0 0 0 0\n");

    [Fact]
    public void ATreeOfItsOwnReadsExactly()
    {
        var root = Tree("cpu  100 0 100 700 100 0 0 0 0 0\ncpu0 1 2 3 4\n", Meminfo, NetDev(1000, 500), "86400.25 300000.00\n", gpuBusy: 37, milliC: 61500);
        try
        {
            var reading = new LinuxMachineReader(root, Path.Combine(root, "proc"), Path.Combine(root, "sys")).Read();
            Assert.Equal(1000, reading.CpuTotal);
            Assert.Equal(200, reading.CpuBusy);   // all but idle and iowait
            Assert.Equal(8000, reading.MemFreeMb);
            Assert.Equal(1500, reading.NetBytes); // loopback left out
            Assert.Equal(86400.25, reading.UptimeS);
            Assert.Equal(37, reading.GpuPct);
            Assert.Equal(61.5, reading.GpuTempC);
            Assert.Equal("", reading.GpuWhy);
            Assert.True(reading.DiskFreeGb > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CpuAndNetworkAreRatesBetweenTwoSamplesOnTheSamplersClock()
    {
        var time = new ManualTime();
        var reader = new StepReader();
        using var sampler = new MachineSampler(reader, time);
        var first = sampler.Sample();
        Assert.Equal(-1, first.CpuPct);
        Assert.Equal(-1, first.NetMbps);
        Assert.Equal(8000, first.MemFreeMb);

        reader.Next = reader.Next with { CpuBusy = 300, CpuTotal = 1400, NetBytes = 1_250_000 + 1000 };
        time.Advance(TimeSpan.FromSeconds(1));
        var second = sampler.Sample();
        Assert.Equal(25, second.CpuPct, 6);       // 100 busy of 400
        Assert.Equal(10, second.NetMbps, 6);      // 1.25 MB in a second
        Assert.Equal(-1, second.GpuPct);
        Assert.Equal("none here", second.GpuWhy);

        // Its own timer samples once a second.
        sampler.Start();
        reader.Next = reader.Next with { CpuBusy = 700, CpuTotal = 1800 };
        time.Advance(TimeSpan.Zero);
        Assert.Equal(100, sampler.Latest.CpuPct, 6);
    }

    private sealed class StepReader : IMachineReader
    {
        public MachineReading Next { get; set; } = new(200, 1000, 8000, 812, 1000, 3600, -1, -1, "none here");

        public MachineReading Read() => Next;
    }

    [Fact]
    public void ThisLinuxMachinesNumbersAreInRangeAndItsGpuIsUnknownWithWords()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var time = new ManualTime();
        using var sampler = new MachineSampler(MachineSampler.ForThisMachine(Path.GetTempPath()), time);
        Assert.IsType<LinuxMachineReader>(MachineSampler.ForThisMachine(Path.GetTempPath()));
        sampler.Sample();
        // Real counters move in real time: a short busy loop gives the second read something to differ by.
        var until = DateTime.UtcNow.AddMilliseconds(50);
        var spin = 0L;
        while (DateTime.UtcNow < until)
            spin++;
        Assert.True(spin > 0);
        time.Advance(TimeSpan.FromSeconds(1));
        var s = sampler.Sample();
        Assert.InRange(s.CpuPct, 0, 100);
        Assert.True(s.MemFreeMb > 0, $"{s.MemFreeMb} MB free");
        Assert.True(s.MemMb > 0);
        Assert.True(s.DiskFreeGb > 0, $"{s.DiskFreeGb} GB free");
        Assert.True(s.UptimeS > 0, $"{s.UptimeS} s up");
        Assert.True(s.NetMbps >= 0);
        if (!Directory.Exists("/sys/class/drm") || !Directory.EnumerateFiles("/sys/class/drm", "gpu_busy_percent", SearchOption.AllDirectories).Any())
        {
            Assert.Equal(-1, s.GpuPct);
            Assert.Equal(LinuxMachineReader.NoGpuWords, s.GpuWhy);
        }
    }
}

/// <summary>The Windows sampler (Category=Windows: the Windows CI job runs it): its PDH GPU counter opens or says why, and its system
/// counters read. Here (Linux) the machine's reader is Linux's.</summary>
public sealed class WindowsSamplerTests
{
    [Fact]
    [Trait("Category", "Windows")]
    public void PdhCountersOpenOrSayWhy()
    {
        if (OperatingSystem.IsWindows())
            OnWindows();
        else
            Assert.NotEqual("WindowsMachineReader", MachineSampler.ForThisMachine(Path.GetTempPath()).GetType().Name);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void OnWindows()
    {
        using var reader = new WindowsMachineReader(Path.GetTempPath());
        Assert.True(reader.GpuCounterOpen || reader.GpuWords.Length > 0, "the GPU counter neither opened nor said why");
        var r = reader.Read();
        Assert.True(r.CpuTotal > 0 && r.CpuBusy >= 0 && r.CpuBusy <= r.CpuTotal);
        Assert.True(r.MemFreeMb > 0);
        Assert.True(r.UptimeS > 0);
        Assert.True(r.DiskFreeGb > 0);
        if (r.GpuPct < 0)
            Assert.False(string.IsNullOrWhiteSpace(r.GpuWhy));
        else
            Assert.InRange(r.GpuPct, 0, 100);
    }
}
