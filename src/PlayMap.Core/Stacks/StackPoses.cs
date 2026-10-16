using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Stacks;

/// <summary>
/// What a stack member's measured lattice was fitted against (<see cref="MeasuredCorrection.FittedTo"/>): the member's pose in its
/// master's frame and both projectors' optics as they stood. The lattice is the measured light less the modelled pictures, so it holds
/// only while they stand so: once either projector is solved again to another pose or other optics (a re-solve, a scan applied, a
/// result recalled), the member is no longer aligned until STACK ALIGN fits it again. A move of the whole site moves both alike, and a
/// result recorded as it stands moves neither: both leave it aligned.
/// </summary>
public static class StackPoses
{
    /// <summary>How far a fitted pose may sit from the projectors' now and still be theirs (metres; radians): rounding, never a move.</summary>
    public const double PositionToleranceM = 1e-6, RotationToleranceRad = 1e-7;

    /// <summary>The member against its master now, or null when either is gone.</summary>
    public static FittedPoses? Of(Project project, string masterId, string memberId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.FindProjector(masterId) is not { } master || project.FindProjector(memberId) is not { } member)
            return null;
        var back = master.Pose.CameraToWorld.Transposed();
        var relative = new Pose(back * (member.Pose.Position - master.Pose.Position), (back * member.Pose.CameraToWorld).ToRotationVector());
        return new FittedPoses(relative, master.Intrinsics, master.Lens, member.Intrinsics, member.Lens);
    }

    /// <summary>True when the member's lattice, fitted to <paramref name="measured"/>'s poses, no longer stands: the member or its
    /// master was solved to another pose or other optics since. False when nothing was kept to compare (a lattice fitted before poses
    /// were kept is taken as it is).</summary>
    public static bool MovedSince(Project project, StackGroup stack, string memberId, MeasuredCorrection measured)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(measured);
        if (measured.Lattice is null || measured.FittedTo is not { } then || Of(project, stack.MasterId, memberId) is not { } now)
            return false;
        return (now.Relative.Position - then.Relative.Position).Length > PositionToleranceM
            || (now.Relative.Rotation - then.Relative.Rotation).Length > RotationToleranceRad
            || !Same(now.Master, then.Master) || !Same(now.Member, then.Member) || now.MasterLens != then.MasterLens || now.MemberLens != then.MemberLens;
    }

    private static bool Same(Intrinsics a, Intrinsics b) =>
        a.Width == b.Width && a.Height == b.Height && Near(a.Fx, b.Fx) && Near(a.Fy, b.Fy) && Near(a.Cx, b.Cx) && Near(a.Cy, b.Cy);

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
}
