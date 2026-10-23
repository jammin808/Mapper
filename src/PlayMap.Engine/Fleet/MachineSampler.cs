namespace PlayMap.Engine.Fleet;

/// <summary>One read of the machine's counters. The CPU and network counters are cumulative (the sampler takes their deltas);
/// anything the platform does not give is −1, and the GPU's absence carries its words.</summary>
public readonly record struct MachineReading(
    long CpuBusy,
    long CpuTotal,
    double MemFreeMb,
    double DiskFreeGb,
    long NetBytes,
    double UptimeS,
    double GpuPct,
    double GpuTempC,
    string GpuWhy)
{
    public static MachineReading Unknown(string why) => new(-1, -1, -1, -1, -1, -1, -1, -1, why);
}

/// <summary>Reads the platform's counters (Linux /proc and /sys; Windows system times, memory status and PDH).</summary>
public interface IMachineReader
{
    MachineReading Read();
}

/// <summary>The machine's numbers as HEALTH reports them: CPU and network over the last interval, memory and disk now, this process's
/// memory, the uptime, the GPU where the platform reads it (−1 with words where it does not).</summary>
public readonly record struct MachineSample(double CpuPct, double MemMb, double MemFreeMb, double DiskFreeGb, double NetMbps, double UptimeS,
    double GpuPct, double GpuTempC, string GpuWhy)
{
    public static readonly MachineSample Unknown = new(-1, -1, -1, -1, -1, -1, -1, -1, "");
}

/// <summary>The machine's latest numbers, for HEALTH (any thread).</summary>
public interface IMachineSampler
{
    MachineSample Latest { get; }
}

/// <summary>Samples the machine once a second on its own timer (never on the control lane or the render thread) and keeps the latest
/// numbers; the first sample's CPU and network read −1 until a second read gives a delta. A tick that finds the last reading still
/// being read takes none, so a reading that stalls (a counter's collect that does not return) holds one pool thread, not one more
/// each second.</summary>
public sealed class MachineSampler : IMachineSampler, IDisposable
{
    private readonly IMachineReader _reader;
    private readonly TimeProvider _time;
    private readonly TimeSpan _every;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private MachineReading? _last;
    private long _lastTs;
    private MachineSample _latest = MachineSample.Unknown;
    private int _sampling;

    public MachineSampler(IMachineReader reader, TimeProvider time, TimeSpan? every = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _every = every ?? TimeSpan.FromSeconds(1);
    }

    public MachineSample Latest
    {
        get
        {
            lock (_gate)
                return _latest;
        }
    }

    /// <summary>The reader for this platform: Linux and Windows read their counters; anything else reads unknown with words.</summary>
    public static IMachineReader ForThisMachine(string diskPath)
    {
        if (OperatingSystem.IsLinux())
            return new LinuxMachineReader(diskPath);
        if (OperatingSystem.IsWindows())
            return new WindowsMachineReader(diskPath);
        return new UnknownMachineReader("this platform's counters are not read (Linux and Windows are)");
    }

    public void Start()
    {
        lock (_gate)
            _timer ??= _time.CreateTimer(_ => Tick(), null, TimeSpan.Zero, _every);
    }

    /// <summary>The timer's sample, none while the last is still being read.</summary>
    private void Tick()
    {
        if (Interlocked.Exchange(ref _sampling, 1) != 0)
            return;
        try
        {
            Sample();
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    /// <summary>Takes one sample now (the timer calls this; a test calls it to step).</summary>
    public MachineSample Sample()
    {
        var reading = _reader.Read();
        var ts = _time.GetTimestamp();
        lock (_gate)
        {
            var cpu = -1.0;
            var net = -1.0;
            if (_last is { } last)
            {
                var total = reading.CpuTotal - last.CpuTotal;
                var busy = reading.CpuBusy - last.CpuBusy;
                if (reading.CpuTotal >= 0 && last.CpuTotal >= 0 && total > 0 && busy >= 0)
                    cpu = Math.Clamp(100.0 * busy / total, 0, 100);
                var seconds = _time.GetElapsedTime(_lastTs, ts).TotalSeconds;
                var bytes = reading.NetBytes - last.NetBytes;
                if (reading.NetBytes >= 0 && last.NetBytes >= 0 && seconds > 0 && bytes >= 0)
                    net = bytes * 8 / 1e6 / seconds;
            }
            _last = reading;
            _lastTs = ts;
            _latest = new MachineSample(cpu, Environment.WorkingSet / (1024.0 * 1024.0), reading.MemFreeMb, reading.DiskFreeGb, net, reading.UptimeS,
                reading.GpuPct, reading.GpuTempC, reading.GpuWhy);
            return _latest;
        }
    }

    /// <summary>Stops the timer and lets the reader go (the sampler owns it).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
        (_reader as IDisposable)?.Dispose();
    }
}

/// <summary>A platform whose counters are not read: every number unknown, with the reason.</summary>
public sealed class UnknownMachineReader(string why) : IMachineReader
{
    public MachineReading Read() => MachineReading.Unknown(why);
}
