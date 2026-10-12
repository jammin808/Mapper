using PlayMap.Core.Scan;

namespace PlayMap.Core.Tests.Scan;

/// <summary>Simulated scans shared by the scan tests: a scene, a plan, the camera's frames and their decode,
/// and the measures the tests assert on.</summary>
internal static class ScanTestKit
{
    public const int ProjectorWidth = 256;
    public const int ProjectorHeight = 160;
    public const int CameraWidth = 320;
    public const int CameraHeight = 240;

    private static readonly Lazy<SimulatedScan> FastFloor = new(() => Run(SimScenes.FloorAndBox(), new ScanPlanOptions { Mode = ScanMode.Fast }));
    private static readonly Lazy<SimulatedScan> RobustFloor = new(() => Run(SimScenes.FloorAndBox(), new ScanPlanOptions { Mode = ScanMode.Robust }));

    /// <summary>The floor-and-box scene scanned in Fast mode with the default camera.</summary>
    public static SimulatedScan Fast => FastFloor.Value;

    /// <summary>The floor-and-box scene scanned in Robust mode with the default camera.</summary>
    public static SimulatedScan Robust => RobustFloor.Value;

    public static SimulatedScan Run(SimScene scene, ScanPlanOptions planOptions, SimOptions? sim = null, DecodeOptions? decode = null,
        Func<byte[][], byte[][]>? alter = null)
    {
        var plan = ScanPlan.Create(scene.Projector.Intrinsics.Width, scene.Projector.Intrinsics.Height, planOptions);
        var simulator = new ScanSimulator(scene, plan, sim);
        var frames = simulator.RenderAll();
        if (alter is not null)
            frames = alter(frames);
        var map = new StructuredLightDecoder(plan, decode).Decode(simulator.CameraWidth, simulator.CameraHeight, frames, simulator.RenderWhiteColour());
        return new SimulatedScan(plan, simulator, frames, map);
    }
}

/// <summary>One simulated scan and its decode.</summary>
internal sealed record SimulatedScan(ScanPlan Plan, ScanSimulator Simulator, byte[][] Frames, CorrespondenceMap Map)
{
    public SimTruth Truth => Simulator.Truth;

    /// <summary>The truly lit camera pixels the camera can tell from black (expected contrast at least
    /// <paramref name="minContrast"/>) and that are not on a depth or projector edge (all 8 neighbours lit too).</summary>
    public List<int> LitPixels(float minContrast = 24)
    {
        var t = Truth;
        var list = new List<int>();
        for (var y = 1; y < t.Height - 1; y++)
        {
            for (var x = 1; x < t.Width - 1; x++)
            {
                var k = y * t.Width + x;
                if (!t.Lit[k] || t.ExpectedContrast[k] < minContrast)
                    continue;
                var interior = true;
                for (var dy = -1; dy <= 1 && interior; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var n = k + dy * t.Width + dx;
                        if (!t.Lit[n] || Math.Abs(t.ProjectorX[n] - t.ProjectorX[k]) > 3 || Math.Abs(t.ProjectorY[n] - t.ProjectorY[k]) > 3)
                        {
                            interior = false;
                            break;
                        }
                    }
                }
                if (interior)
                    list.Add(k);
            }
        }
        return list;
    }

    /// <summary>The share of <paramref name="pixels"/> that decoded within <paramref name="tolerance"/> projector
    /// pixels of the truth on both axes.</summary>
    public double ShareWithin(IReadOnlyList<int> pixels, double tolerance)
    {
        var good = 0;
        foreach (var k in pixels)
        {
            if ((Map.Flags[k] & DecodeStatus.Valid) == 0)
                continue;
            if (Math.Abs(Map.ProjectorX[k] - Truth.ProjectorX[k]) <= tolerance && Math.Abs(Map.ProjectorY[k] - Truth.ProjectorY[k]) <= tolerance)
                good++;
        }
        return pixels.Count > 0 ? (double)good / pixels.Count : 0;
    }

    /// <summary>The share of the decoded pixels among <paramref name="pixels"/> that are off by more than
    /// <paramref name="threshold"/> projector pixels on either axis: jumps, not noise.</summary>
    public double GrossShare(IReadOnlyList<int> pixels, double threshold = 4)
    {
        var decoded = 0;
        var gross = 0;
        foreach (var k in pixels)
        {
            if ((Map.Flags[k] & DecodeStatus.Valid) == 0)
                continue;
            decoded++;
            if (Math.Abs(Map.ProjectorX[k] - Truth.ProjectorX[k]) > threshold || Math.Abs(Map.ProjectorY[k] - Truth.ProjectorY[k]) > threshold)
                gross++;
        }
        return decoded > 0 ? (double)gross / decoded : 0;
    }

    /// <summary>The RMS error (projector pixels, both axes pooled) over the decoded pixels among <paramref name="pixels"/>
    /// whose error is at most <paramref name="excludeAbove"/> on both axes (jumps are counted by <see cref="GrossShare"/>),
    /// and how many of them decoded.</summary>
    public (double Rms, int Decoded) Rms(IReadOnlyList<int> pixels, double excludeAbove = double.PositiveInfinity)
    {
        double sum = 0;
        var n = 0;
        foreach (var k in pixels)
        {
            if ((Map.Flags[k] & DecodeStatus.Valid) == 0)
                continue;
            var dx = Map.ProjectorX[k] - Truth.ProjectorX[k];
            var dy = Map.ProjectorY[k] - Truth.ProjectorY[k];
            if (Math.Abs(dx) > excludeAbove || Math.Abs(dy) > excludeAbove)
                continue;
            sum += dx * dx + dy * dy;
            n++;
        }
        return (n > 0 ? Math.Sqrt(sum / (2 * n)) : double.NaN, n);
    }
}
