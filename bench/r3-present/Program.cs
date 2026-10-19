using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using PlayMap.Render.Gpu;
using PlayMap.Render.Platform;
using PlayMap.Render.Platform.Windows;

namespace PlayMap.Bench.Present;

/// <summary>The bench for M1 and M3 (docs/round3/PLAN.md §1.5; README.md beside it): on one display, a quad at the top centre
/// flashes white for one frame in every <c>--flash</c> frames through each present path in turn, and every present's QPC instants
/// are written to a CSV for the photodiode and PresentMon to be lined up with.
///   PlayMap.Bench.Present [--path all|d3d12|gl|d3d11|composition] [--seconds 60] [--display 0] [--flash 30] [--hz 60] [--warp]
///                         [--out r3-present.csv]
/// It exits 0 when every path it was asked for ran or answered why not, 1 when one could not start, 2 on a wrong command line.</summary>
internal static class Program
{
    private static readonly string[] Paths = ["d3d12", "gl", "d3d11", "composition"];

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19043))
        {
            Console.Error.WriteLine("the bench runs on Windows 10 21H1 or later (the lab runs it on Windows 11 24H2 or later)");
            return 2;
        }
        var (paths, seconds, display, flash, hz, warp, output) = (Paths, 60, 0, 30, 60, false, "r3-present.csv");
        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--path" when next is "all":
                    i++;
                    break;
                case "--path" when next is not null && Paths.Contains(next):
                    paths = [next];
                    i++;
                    break;
                case "--seconds" when Number(next, 1, 3600) is { } s:
                    seconds = s;
                    i++;
                    break;
                case "--display" when Number(next, 0, 63) is { } d:
                    display = d;
                    i++;
                    break;
                case "--flash" when Number(next, 2, 600) is { } f:
                    flash = f;
                    i++;
                    break;
                case "--hz" when Number(next, 1, 1000) is { } r:
                    hz = r;
                    i++;
                    break;
                case "--warp":
                    warp = true;
                    break;
                case "--out" when next is not null:
                    output = next;
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"the bench does not know '{args[i]}'{(next is null ? "" : $" {next}")}: see README.md");
                    return 2;
            }
        }
        return Run(paths, seconds, display, flash, hz, warp, output);
    }

    private static int? Number(string? word, int low, int high) =>
        int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= low && n <= high ? n : null;

    [SupportedOSPlatform("windows10.0.19043")]
    private static int Run(string[] paths, int seconds, int index, int flash, int hz, bool warp, string output)
    {
        var display = BenchDisplay.Find(index);
        using var record = new PresentRecord(output, string.Create(CultureInfo.InvariantCulture,
            $"display {index} {display.Name} ({display.Width}x{display.Height} at {display.X},{display.Y}) on {display.AdapterName}; flash 1 in {flash}; {seconds} s per path{(warp ? "; WARP" : "")}"));
        var failed = 0;
        foreach (var path in paths)
        {
            try
            {
                Console.Out.WriteLine($"{path}: {Once(path, display, seconds, flash, hz, warp, record)}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or GlException or GpuPlatformStartException or DllNotFoundException
                or EntryPointNotFoundException or PlatformNotSupportedException)
            {
                failed++;
                record.Note($"{path}: did not start: {ex.Message}");
                Console.Out.WriteLine($"{path}: did not start: {ex.Message}");
            }
        }
        Console.Out.WriteLine($"{record.Rows} presents written to {Path.GetFullPath(output)}");
        return failed == 0 ? 0 : 1;
    }

    [SupportedOSPlatform("windows10.0.19043")]
    private static string Once(string path, BenchDisplay display, int seconds, int flash, int hz, bool warp, PresentRecord record)
    {
        switch (path)
        {
            case "d3d12":
            {
                using var flip = new D3D12FlipPath(display, warp, record);
                var frames = Loop(seconds, flash, () => flip.Closed, (f, w) => flip.Frame(f, w, record));
                return $"{frames} frames";
            }
            case "gl":
            {
                var options = new GlDeviceOptions(CheckErrors: false, DebugOutput: false);
                using var gl = new PlayMapPath("gl", GpuPlatforms.Create(new PlatformRequest("gl"), options), display, record);
                var frames = Loop(seconds, flash, () => gl.Closed, (f, w) => gl.Frame(f, w, record));
                return $"{frames} frames; {gl.Words()}";
            }
            case "d3d11":
            {
                var options = new GlDeviceOptions(CheckErrors: false, DebugOutput: false);
                using var dxgi = new PlayMapPath("d3d11", AnglePlatform.Create(options, warp), display, record);
                var frames = Loop(seconds, flash, () => dxgi.Closed, (f, w) => dxgi.Frame(f, w, record));
                return $"{frames} frames; {dxgi.Words()}";
            }
            default:
                return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                    ? Composition(display, seconds, flash, hz, warp, record)
                    : "the composition swapchain is Windows 11's: this Windows has none to ask";
        }
    }

    [SupportedOSPlatform("windows10.0.22000")]
    private static string Composition(BenchDisplay display, int seconds, int flash, int hz, bool warp, PresentRecord record)
    {
        using var composition = new CompositionPath(display, warp, hz, record);
        if (!composition.Ready)
            return "the composition swapchain did not take Direct3D 12 buffers here: the record says which ask it refused";
        var frames = Loop(seconds, flash, () => composition.Closed, (f, w) => composition.Frame(f, w, record));
        return $"{frames} frames presented at target times";
    }

    /// <summary>Frames until the time is up or the window is closed: one white frame, then <paramref name="flash"/> − 1 black ones.</summary>
    private static long Loop(int seconds, int flash, Func<bool> closed, Action<long, bool> frame)
    {
        var clock = Stopwatch.StartNew();
        long n = 0;
        while (clock.Elapsed.TotalSeconds < seconds && !closed())
        {
            frame(n, n % flash == 0);
            n++;
        }
        return n;
    }
}
