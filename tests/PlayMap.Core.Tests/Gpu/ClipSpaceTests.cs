using System.Numerics;
using PlayMap.Core.Gpu;

namespace PlayMap.Core.Tests.Gpu;

/// <summary>The render hardware interface's clip space: clip y = −w is the target's top row, z from −w to w is depth 0 to 1, and the
/// position a backend with Direct3D 12's rasteriser writes lands every vertex on the same row and depth.</summary>
public sealed class ClipSpaceTests
{
    [Theory]
    [InlineData(-1f, 1f, 0f)]
    [InlineData(1f, 1f, 1080f)]
    [InlineData(0f, 2f, 540f)]
    [InlineData(-3f, 3f, 0f)]
    public void ClipYMinusWIsTheTopRow(float y, float w, float row) => Assert.Equal(row, ClipSpace.Row(y, w, 1080), 3);

    [Theory]
    [InlineData(-1f, 1f, 0f)]
    [InlineData(1f, 1f, 1f)]
    [InlineData(0f, 4f, 0.5f)]
    public void ClipZFromMinusWToWIsDepthZeroToOne(float z, float w, float depth) => Assert.Equal(depth, ClipSpace.Depth(z, w), 5);

    [Fact]
    public void TheDirect3DPositionLandsOnTheSameRowAndDepth()
    {
        foreach (var clip in new[] { new Vector4(0.3f, -1f, -1f, 1f), new Vector4(-0.5f, 0.25f, 0.4f, 2f), new Vector4(1f, 3f, 3f, 3f) })
        {
            var d = ClipSpace.ToDirect3D(clip);
            // Direct3D 12: row = (1 − y/w)/2 · h from the top, depth = z/w.
            Assert.Equal(ClipSpace.Row(clip.Y, clip.W, 1080), (1 - d.Y / d.W) * 0.5f * 1080, 3);
            Assert.Equal(ClipSpace.Depth(clip.Z, clip.W), d.Z / d.W, 5);
            Assert.Equal((clip.X, clip.W), (d.X, d.W));
        }
    }
}
