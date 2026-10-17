using PlayMap.Core.Geometry;
using PlayMap.Core.Numerics;
using PlayMap.Core.Threading;

namespace PlayMap.Core.Scan;

/// <summary>
/// A camera looking at a stage several projectors light: the stage's world mesh traced once from the camera (each pixel's world
/// point), and for each projector where on its raster that point lies, how squarely it faces it and whether something shadows it.
/// <see cref="Capture"/> answers the camera's gray picture of any frames the projectors show at once (null: black), with the room's
/// light and seeded read noise. The re-solve's tests and the simulated camera of CALIBRATION RESOLVE … CAMERA use it: the projectors
/// are where the simulator says they are (bumped or not), not where the project thinks.
/// </summary>
public sealed class StageSimulator
{
    private readonly SimDevice[] _projectors;
    private readonly SimOptions _options;
    private readonly float[] _ambient;
    private readonly float[][] _u;
    private readonly float[][] _v;
    private readonly float[][] _gain;
    private readonly float[] _projectorLut = new float[256];
    private int _shots;

    public StageSimulator(MeshRaycaster world, SimDevice camera, IReadOnlyList<SimDevice> projectors, SimOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(projectors);
        _projectors = [.. projectors];
        _options = options ?? new SimOptions { NoiseSigma = 0.8 };
        Camera = camera;
        var k = camera.Intrinsics;
        CameraWidth = k.Width;
        CameraHeight = k.Height;
        var n = CameraWidth * CameraHeight;
        _ambient = new float[n];
        _u = new float[_projectors.Length][];
        _v = new float[_projectors.Length][];
        _gain = new float[_projectors.Length][];
        for (var j = 0; j < _projectors.Length; j++)
        {
            _u[j] = new float[n];
            _v[j] = new float[n];
            _gain[j] = new float[n];
        }
        for (var i = 0; i < 256; i++)
            _projectorLut[i] = (float)Math.Pow(i / 255.0, _options.ProjectorGamma);
        Parallel.For(0, CameraHeight, Compute.Options, y =>
        {
            for (var x = 0; x < CameraWidth; x++)
            {
                var at = y * CameraWidth + x;
                var ray = CameraModel.PixelRay(new Vec2d(x + 0.5, y + 0.5), k, camera.Lens, camera.Pose);
                if (world.Cast(ray) is not { } hit)
                    continue;
                var (a, b, c) = world.Mesh.Triangle(hit.Triangle);
                var normal = Vec3d.Cross(world.Mesh.Position(b) - world.Mesh.Position(a), world.Mesh.Position(c) - world.Mesh.Position(a)).Normalized();
                if (Vec3d.Dot(normal, ray.Direction) > 0)
                    normal = normal * -1;
                _ambient[at] = (float)(_options.Ambient * _options.AlbedoScale);
                for (var j = 0; j < _projectors.Length; j++)
                {
                    var p = _projectors[j];
                    if (CameraModel.Project(hit.Point, p.Intrinsics, p.Lens, p.Pose) is not { } px || !CameraModel.InRaster(px, p.Intrinsics))
                        continue;
                    var toProjector = p.Pose.Position - hit.Point;
                    var distance = toProjector.Length;
                    var cos = Vec3d.Dot(normal, toProjector / distance);
                    if (cos <= 0)
                        continue;
                    if (_options.Shadows && world.Occluded(new Ray(hit.Point + normal * 1e-4, toProjector / distance), distance - 2e-4))
                        continue;
                    _u[j][at] = (float)px.X;
                    _v[j][at] = (float)px.Y;
                    _gain[j][at] = (float)(_options.ProjectorGain * cos * _options.AlbedoScale);
                }
            }
        });
    }

    public SimDevice Camera { get; }

    public int CameraWidth { get; }

    public int CameraHeight { get; }

    public IReadOnlyList<SimDevice> Projectors => _projectors;

    /// <summary>How many pictures were taken.</summary>
    public int Shots => _shots;

    /// <summary>The camera's gray8 picture of each projector's frame at once (null shows black); a frame is a projector's own raster.</summary>
    public byte[] Capture(IReadOnlyList<byte[]?> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var shot = Interlocked.Increment(ref _shots);
        var image = new byte[CameraWidth * CameraHeight];
        var seed = _options.Seed * 0x9E3779B97F4A7C15UL + (ulong)shot;
        var sigma = _options.NoiseSigma;
        var exposure = _options.Exposure;
        Parallel.For(0, CameraHeight, Compute.Options, y =>
        {
            var rng = new Pcg32(seed, (ulong)y);
            for (var x = 0; x < CameraWidth; x++)
            {
                var at = y * CameraWidth + x;
                double light = _ambient[at];
                for (var j = 0; j < _projectors.Length && j < frames.Count; j++)
                {
                    if (frames[j] is not { } f || _gain[j][at] <= 0)
                        continue;
                    var k = _projectors[j].Intrinsics;
                    light += _gain[j][at] * Sample(f, k.Width, k.Height, _u[j][at] - 0.5, _v[j][at] - 0.5);
                }
                var level = 255 * Math.Clamp(light * exposure, 0, 1);
                if (sigma > 0)
                    level += sigma * rng.NextGaussian();
                image[at] = (byte)Math.Clamp(Math.Round(level), 0, 255);
            }
        });
        return image;
    }

    private double Sample(byte[] frame, int w, int h, double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        double At(int xx, int yy) => xx < 0 || yy < 0 || xx >= w || yy >= h ? 0 : _projectorLut[frame[yy * w + xx]];
        return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy) + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
    }
}
