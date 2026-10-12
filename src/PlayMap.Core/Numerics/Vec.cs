using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PlayMap.Core.Numerics;

/// <summary>A 2D vector in double precision — calibration, scanning and warp maths run in doubles; the
/// GPU gets floats (<see cref="Vector2"/>) at the edge.</summary>
public readonly record struct Vec2d(double X, double Y)
{
    public static readonly Vec2d Zero = new(0, 0);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;

    public Vec2d Normalized()
    {
        var l = Length;
        return l > 0 ? new Vec2d(X / l, Y / l) : Zero;
    }

    public static Vec2d operator +(Vec2d a, Vec2d b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2d operator -(Vec2d a, Vec2d b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2d operator -(Vec2d a) => new(-a.X, -a.Y);
    public static Vec2d operator *(Vec2d a, double s) => new(a.X * s, a.Y * s);
    public static Vec2d operator *(double s, Vec2d a) => new(a.X * s, a.Y * s);
    public static Vec2d operator /(Vec2d a, double s) => new(a.X / s, a.Y / s);

    public static double Dot(Vec2d a, Vec2d b) => a.X * b.X + a.Y * b.Y;

    /// <summary>The z of the 3D cross product: positive when b turns left of a.</summary>
    public static double Cross(Vec2d a, Vec2d b) => a.X * b.Y - a.Y * b.X;

    public static double Distance(Vec2d a, Vec2d b) => (a - b).Length;

    public static Vec2d Lerp(Vec2d a, Vec2d b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public Vector2 ToVector2() => new((float)X, (float)Y);
    public static Vec2d From(Vector2 v) => new(v.X, v.Y);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:0.###}, {Y:0.###})");
}

/// <summary>A 3D vector in double precision.</summary>
public readonly record struct Vec3d(double X, double Y, double Z)
{
    public static readonly Vec3d Zero = new(0, 0, 0);
    public static readonly Vec3d UnitX = new(1, 0, 0);
    public static readonly Vec3d UnitY = new(0, 1, 0);
    public static readonly Vec3d UnitZ = new(0, 0, 1);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;

    public Vec3d Normalized()
    {
        var l = Length;
        return l > 0 ? new Vec3d(X / l, Y / l, Z / l) : Zero;
    }

    public double this[int i] => i switch { 0 => X, 1 => Y, 2 => Z, _ => throw new ArgumentOutOfRangeException(nameof(i)) };

    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3d operator -(Vec3d a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3d operator *(Vec3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3d operator *(double s, Vec3d a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3d operator /(Vec3d a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(Vec3d a, Vec3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3d Cross(Vec3d a, Vec3d b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static double Distance(Vec3d a, Vec3d b) => (a - b).Length;

    public static Vec3d Lerp(Vec3d a, Vec3d b, double t) => a + (b - a) * t;

    public static Vec3d Min(Vec3d a, Vec3d b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Vec3d Max(Vec3d a, Vec3d b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
    public static Vec3d From(Vector3 v) => new(v.X, v.Y, v.Z);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:0.####}, {Y:0.####}, {Z:0.####})");
}
