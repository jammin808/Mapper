using System.Text;

namespace PlayMap.Engine.Tests.Rendering;

/// <summary>Licence hygiene (PLAN §5.2): the render host's test clips are made with LGPL-compatible encoders — no libx264 or
/// libx265 mark in their containers or streams.</summary>
public sealed class ClipEncoderTests
{
    [Host.FfmpegFact]
    public void TheCountedAndSolidClipsCarryNoGplEncodersMark()
    {
        foreach (var clip in new[] { Clips.Counted(1), Clips.Solid("0x336699", 1) })
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(clip));
            Assert.All(new[] { "libx264", "libx265", "x264 - core", "x265 (build" }, m => Assert.DoesNotContain(m, text, StringComparison.Ordinal));
        }
    }
}
