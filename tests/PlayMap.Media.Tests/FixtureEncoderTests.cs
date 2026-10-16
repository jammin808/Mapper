using System.Text;

namespace PlayMap.Media.Tests;

/// <summary>Licence hygiene (PLAN §5.2): the clips the tests make carry no GPL encoder's mark — no libx264 or libx265 in the
/// container's encoder tag and no x264 or x265 settings message in the stream — except hevc10.mp4, the one clip no LGPL-compatible
/// encoder here can make (10-bit HEVC), which carries libx265's.</summary>
public sealed class FixtureEncoderTests
{
    private static readonly string[] GplMarks = ["libx264", "libx265", "x264 - core", "x265 (build"];

    private static string[] MarksIn(string path)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        return [.. GplMarks.Where(m => text.Contains(m, StringComparison.Ordinal))];
    }

    [Theory]
    [InlineData("h264_25.mp4")]
    [InlineData("h264_2997.mp4")]
    [InlineData("h264_long.mp4")]
    [InlineData("h264_1080.mp4")]
    [InlineData("hevc.mp4")]
    [InlineData("av.mp4")]
    public void AClipCarriesNoGplEncodersMark(string clip) => Assert.Empty(MarksIn(TestMedia.Clip(clip)));

    [Fact]
    public void OnlyTheTenBitHevcClipIsMadeByAGplEncoder() => Assert.NotEmpty(MarksIn(TestMedia.Clip("hevc10.mp4")));
}
