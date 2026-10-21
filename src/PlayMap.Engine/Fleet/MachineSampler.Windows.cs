using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlayMap.Engine.Fleet;

/// <summary>Windows' counters: <c>GetSystemTimes</c> (kernel time includes idle), <c>GlobalMemoryStatusEx</c>, <c>GetTickCount64</c>, the
/// free space of the home's volume, the network's bytes from the interfaces' statistics, and the GPU's 3D engines from the PDH
/// counter <c>\GPU Engine(*engtype_3D)\Utilization Percentage</c> (summed over the engines of the adapter that drives the outputs, as
/// the machine platform's identity names it, else over every engine; at most 100). A PDH query that does not
/// open leaves the GPU unknown with the status in its words. GPU temperature is not read here (NVML, next round).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineReader : IMachineReader, IDisposable
{
    public const string GpuCounter = @"\GPU Engine(*engtype_3D)\Utilization Percentage";

    private const uint PdhFmtDouble = 0x00000200;
    private const int PdhMoreData = unchecked((int)0x800007D2);

    private readonly string _diskPath;
    private readonly Lock _gate = new();
    private nint _query;
    private nint _counter;
    private bool _collected;

    public WindowsMachineReader(string diskPath)
    {
        _diskPath = diskPath;
        var status = PdhOpenQueryW(null, 0, out _query);
        if (status != 0)
        {
            GpuWords = string.Create(CultureInfo.InvariantCulture, $"the PDH query did not open (status 0x{status:X8}): GPU load unknown");
            _query = 0;
            return;
        }
        status = PdhAddEnglishCounterW(_query, GpuCounter, 0, out _counter);
        if (status != 0)
        {
            GpuWords = string.Create(CultureInfo.InvariantCulture,
                $"this machine has no PDH counter {GpuCounter} (status 0x{status:X8}): GPU load unknown — a driver with WDDM 2.4 or later gives it");
            _ = PdhCloseQuery(_query);
            _query = 0;
            return;
        }
        GpuWords = "";
    }

    /// <summary>Why the GPU's load is unknown ("" when its counter opened).</summary>
    public string GpuWords { get; }

    /// <summary>True when the GPU counter opened.</summary>
    public bool GpuCounterOpen => _query != 0;

    public MachineReading Read()
    {
        long busy = -1, total = -1;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            total = (long)(kernel + user);
            busy = total - (long)idle;
        }
        double memFree = -1;
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status))
            memFree = status.AvailPhys / (1024.0 * 1024.0);
        var uptime = GetTickCount64() / 1000.0;
        var gpu = GpuPct(out var gpuWhy);
        return new MachineReading(busy, total, memFree, DiskFreeGb(), NetBytes(), uptime, gpu, -1, gpu < 0 ? gpuWhy : "");
    }

    private double DiskFreeGb()
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(_diskPath)).AvailableFreeSpace / (1024.0 * 1024 * 1024);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static long NetBytes()
    {
        try
        {
            long sum = 0;
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;
                var s = nic.GetIPStatistics();
                sum += s.BytesReceived + s.BytesSent;
            }
            return sum;
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return -1;
        }
    }

    private unsafe double GpuPct(out string why)
    {
        lock (_gate)
        {
            if (_query == 0)
            {
                why = GpuWords;
                return -1;
            }
            var status = PdhCollectQueryData(_query);
            if (status != 0)
            {
                why = string.Create(CultureInfo.InvariantCulture, $"the GPU counter did not collect (status 0x{status:X8})");
                return -1;
            }
            if (!_collected)
            {
                // A rate counter needs two collections: the first sample is a baseline.
                _collected = true;
                why = "the GPU counter takes its baseline";
                return -1;
            }
            uint size = 0, count = 0;
            status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, ref count, null);
            if (status != PdhMoreData || size == 0)
            {
                why = string.Create(CultureInfo.InvariantCulture, $"the GPU counter gave no engines (status 0x{status:X8})");
                return -1;
            }
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, ref count, p);
                if (status != 0)
                {
                    why = string.Create(CultureInfo.InvariantCulture, $"the GPU counter's engines did not read (status 0x{status:X8})");
                    return -1;
                }
                var items = (CounterValueItem*)p;
                // The engines of the adapter that drives the outputs, by its LUID in the instance's name, where the identity names one.
                var show = Core.Machine.ThreadClaims.Platform?.Identity().Adapters.FirstOrDefault(a => a.DrivesOutputs) is { } a
                    ? string.Create(CultureInfo.InvariantCulture, $"luid_0x{(uint)(a.Luid >> 32):X8}_0x{(uint)a.Luid:X8}") : null;
                double sum = 0;
                for (var i = 0; i < count; i++)
                {
                    if (items[i].CStatus == 0 && (show is null || (Marshal.PtrToStringUni(items[i].Name)?.Contains(show, StringComparison.OrdinalIgnoreCase) ?? false)))
                        sum += items[i].DoubleValue;
                }
                why = "";
                return Math.Clamp(sum, 0, 100);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != 0)
                _ = PdhCloseQuery(_query);
            _query = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W with a double: the instance's name, the value's status, the value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValueItem
    {
        public nint Name;
        public uint CStatus;
        public uint Padding;
        public double DoubleValue;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ulong GetTickCount64();

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern unsafe int PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, ref uint itemCount, byte* items);

    [DllImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int PdhCloseQuery(nint query);
}
