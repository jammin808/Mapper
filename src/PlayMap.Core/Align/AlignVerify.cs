using System.Globalization;
using PlayMap.Core.Numerics;
using PlayMap.Core.Scan;

namespace PlayMap.Core.Align;

/// <summary>What the verify pass measured: where two outputs show the same canvas dot, how far apart the camera saw them (p95, output
/// px) — the seams', or the stack's when the outputs share one picture; how far each dot landed from where the solve says its canvas
/// point is (p95, output px); how many dots were seen; and the words.</summary>
public sealed record AlignVerifyResult(double SeamP95Px, bool Stack, double OffsetP95Px, int Dots, string Words);

/// <summary>
/// The verify pass (autoalign M15's seams term): a grid of canvas points drawn as dots through each output's new warp, each output alone;
/// the camera finds each dot; where two outputs drew the same canvas point, the distance between the two dots the camera saw is their
/// misregistration on the screen, in output pixels. Measured, not predicted: a projector that drifted after the scan shows here.
/// </summary>
public static class AlignVerify
{
    /// <summary>The verify pass's pictures: one per output it looks at (the black one is the scan's). A run that scanned only some outputs
    /// looks at only those: the others show nothing, so their seams are the solve's own measure.</summary>
    public static int Shots(int outputs) => outputs;

    public static async Task<AlignVerifyResult> RunAsync(IAlignShooter shooter, IReadOnlyList<AlignOutputSpec> outputs, AlignSolveResult solve,
        CameraShot black, IReadOnlyList<AlignCapture> captures, Action<AlignStepProgress>? progress, CancellationToken ct, IReadOnlySet<int>? only = null)
    {
        ArgumentNullException.ThrowIfNull(shooter);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(solve);
        ArgumentNullException.ThrowIfNull(black);
        ArgumentNullException.ThrowIfNull(captures);
        if (!solve.Ok || solve.Planar is not { } planar || solve.Rectification is not { } rect || solve.Target is not { } target)
            return new AlignVerifyResult(-1, false, -1, 0, "nothing to verify");
        var lens = planar.Lens;
        Vec2d CameraAt(Vec2d uv) => lens.Distort(rect.Inverse(target.FromUv(uv)));

        // Canvas points about 24 output pixels apart.
        var first = solve.Outputs[0].Warp;
        var across = Math.Clamp((int)Math.Round(1 / Math.Max(first.UvPerPx, 1e-6) / 24), 8, 96);
        var down = Math.Clamp((int)Math.Round(across * target.Height / target.Width), 4, 96);
        var uvs = new List<Vec2d>(across * down);
        for (var y = 0; y < down; y++)
            for (var x = 0; x < across; x++)
                uvs.Add(new Vec2d((x + 0.5) / across, (y + 0.5) / down));

        // Output px per camera px for each output: how far apart its neighbouring dots are in its raster over how far apart the camera
        // should see them.
        var scales = new double[outputs.Count];
        var seen = new Vec2d?[outputs.Count, uvs.Count];
        var offsets = new List<double>();
        var stack = outputs.Count >= 2 && captures.Count == outputs.Count
            && Enumerable.Range(1, outputs.Count - 1).All(j => captures[0].Footprint.Overlap(captures[j].Footprint) > 0.8);
        for (var j = 0; j < outputs.Count; j++)
        {
            ct.ThrowIfCancellationRequested();
            if (only is not null && !only.Contains(j))
                continue;
            var o = outputs[j];
            var warp = solve.Outputs[j].Warp;
            var inverse = CanvasInverse.Build(warp.Grid, 512, Math.Max(16, (int)Math.Round(512 * target.Height / target.Width)));
            var dots = new List<(int Uv, Vec2d Q)>();
            for (var i = 0; i < uvs.Count; i++)
            {
                if (inverse.At(uvs[i]) is not { } start || warp.Grid.Invert(uvs[i], start) is not { } q)
                    continue;
                if (q.X >= 3 && q.Y >= 3 && q.X <= o.Width - 3 && q.Y <= o.Height - 3)
                    dots.Add((i, q));
            }
            var frame = DotCheck.Render([.. dots.Select(d => d.Q)], o.Width, o.Height, 1.2);
            var shot = await shooter.ShootAsync(j, frame, ct).ConfigureAwait(false);
            progress?.Invoke(new AlignStepProgress("verify", j, (j + 1.0) / outputs.Count, $"{o.Name}: the canvas's dots"));
            var blobs = DotCheck.Detect(shot.Gray, black.Gray, shot.Width, shot.Height, minLevel: 8, window: 2, minPixels: 1);
            var expected = dots.Select(d => (Vec2d?)CameraAt(uvs[d.Uv])).ToList();
            var spacing = DotCheck.CameraSpacing(expected);
            scales[j] = spacing > 0 ? DotCheck.CameraSpacing([.. dots.Select(d => (Vec2d?)d.Q)]) / spacing : 0;
            var match = DotCheck.Match(expected, blobs, Math.Max(2, spacing / 2));
            var pxPerCamera = scales[j];
            for (var k = 0; k < dots.Count; k++)
            {
                if (match.Observed[k] < 0)
                    continue;
                var at = blobs[match.Observed[k]].At;
                seen[j, dots[k].Uv] = at;
                offsets.Add(Vec2d.Distance(at, expected[k]!.Value) * pxPerCamera);
            }
        }

        // Where two outputs drew the same canvas point: the distance between their dots, in output px.
        var gaps = new List<double>();
        for (var i = 0; i < uvs.Count; i++)
        {
            for (var a = 0; a < outputs.Count; a++)
            {
                for (var b = a + 1; b < outputs.Count; b++)
                {
                    if (seen[a, i] is not { } pa || seen[b, i] is not { } pb)
                        continue;
                    gaps.Add(Vec2d.Distance(pa, pb) * 0.5 * (scales[a] + scales[b]));
                }
            }
        }
        var seam = gaps.Count == 0 ? -1 : Quantile(gaps, 0.95);
        var offset = offsets.Count == 0 ? -1 : Quantile(offsets, 0.95);
        var words = seam < 0
            ? string.Create(CultureInfo.InvariantCulture, $"verified: {offsets.Count} dots seen, no two outputs share one")
            : string.Create(CultureInfo.InvariantCulture, $"verified: {(stack ? "the stack's members" : "the seams")} agree to {seam:0.00} px on the screen (95 %, {gaps.Count} dots)");
        return new AlignVerifyResult(seam, stack, offset, offsets.Count, words);
    }

    private static double Quantile(List<double> values, double q)
    {
        values.Sort();
        return values[Math.Min(values.Count - 1, (int)(q * values.Count))];
    }
}
