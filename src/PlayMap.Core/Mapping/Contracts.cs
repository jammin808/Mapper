using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Mapping;

/// <summary>The tessellated 2D warp of an output, ready for the GPU: positions are where each vertex lands
/// on the output in normalised units (0..1, y down), texture coordinates where it samples the pre-warp
/// picture (0..1). Drawing the pre-warp picture through this mesh is the whole 2D warp.</summary>
public sealed record WarpMeshGeometry(float[] Positions, float[] TexCoords, uint[] Indices, int GridColumns, int GridRows)
{
    public int VertexCount => Positions.Length / 2;
}

/// <summary>A blend map for one output: per-pixel weights (0..1, already through the inverse gamma so they
/// are what the signal is multiplied by) and the black-level pedestal (added light, also through the
/// inverse gamma), at a resolution the GPU upsamples bilinearly.</summary>
public sealed record BlendMap(string OutputId, int Width, int Height, float[] Weights, float[] Pedestal)
{
    public float WeightAt(int x, int y) => Weights[y * Width + x];
}

/// <summary>The result of solving a projector from correspondences: the optics, the pose, and how well
/// they explain each pin (the per-pin residual names a bad pick).</summary>
public sealed record PoseSolution(
    Intrinsics Intrinsics,
    LensDistortion Lens,
    Pose Pose,
    double RmsPx,
    IReadOnlyList<double> ResidualsPx,
    IReadOnlyList<bool> Inliers,
    string Words)
{
    public bool IsGood => RmsPx < 2.0;
}

/// <summary>Correspondence between a world point and a pixel, for a pose solve.</summary>
public readonly record struct Correspondence(Vec3d World, Vec2d Pixel);
