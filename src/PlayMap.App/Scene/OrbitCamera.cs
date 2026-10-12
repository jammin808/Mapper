using System.Globalization;
using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.App.Scene;

/// <summary>The MAP editor's orbit camera: a target, a yaw and pitch around it, a distance and a field of
/// view. Its pose and intrinsics are what the desk sends the engine for the editor view (VIEW editor CAMERA)
/// and what it projects its overlays with, so the overlays sit exactly on the engine's picture.</summary>
public sealed class OrbitCamera
{
    public Vec3d Target { get; set; } = new(0, 1.0, 0.4);

    /// <summary>Around +Y, radians; 0 looks along −Z (from the audience towards the back wall).</summary>
    public double Yaw { get; set; } = -0.42;

    /// <summary>Up from the horizon, radians (clamped short of the poles).</summary>
    public double Pitch { get; set; } = 0.32;

    public double Distance { get; set; } = 8.5;

    public double FovDegrees { get; set; } = 50;

    public Vec3d Eye
    {
        get
        {
            var cp = Math.Cos(Pitch);
            return Target + new Vec3d(Math.Sin(Yaw) * cp, Math.Sin(Pitch), Math.Cos(Yaw) * cp) * Distance;
        }
    }

    public Pose Pose => Pose.LookAt(Eye, Target, Vec3d.UnitY);

    public Intrinsics IntrinsicsFor(double width, double height) =>
        Intrinsics.FromFieldOfView(Math.Max(1, (int)Math.Round(width)), Math.Max(1, (int)Math.Round(height)), FovDegrees);

    /// <summary>Turns the view by a drag in pixels.</summary>
    public void Orbit(double dxPixels, double dyPixels)
    {
        Yaw -= dxPixels * 0.006;
        Pitch = Math.Clamp(Pitch + dyPixels * 0.006, -1.45, 1.45);
    }

    /// <summary>Slides the target in the view plane by a drag in pixels.</summary>
    public void Pan(double dxPixels, double dyPixels, double viewportHeight)
    {
        var pose = Pose;
        var r = pose.CameraToWorld;
        var right = new Vec3d(r.M00, r.M10, r.M20);
        var down = new Vec3d(r.M01, r.M11, r.M21);
        var metresPerPixel = 2 * Distance * Math.Tan(FovDegrees * Math.PI / 360) / Math.Max(1, viewportHeight);
        Target = Target - right * (dxPixels * metresPerPixel) - down * (dyPixels * metresPerPixel);
    }

    /// <summary>Wheel notches in (positive) or out.</summary>
    public void Zoom(double notches) => Distance = Math.Clamp(Distance * Math.Pow(0.88, notches), 0.3, 500);

    /// <summary>The camera as the VIEW CAMERA verb carries it: intrinsics and pose in the project's own JSON.</summary>
    public string ToJson(double width, double height)
    {
        var k = IntrinsicsFor(width, height);
        return "{\"intrinsics\":" + ProjectJson.SerializeEntity(k) + ",\"pose\":" + ProjectJson.SerializeEntity(Pose)
            + string.Create(CultureInfo.InvariantCulture, $",\"near\":0.05,\"far\":500}}");
    }
}
