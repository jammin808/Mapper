namespace PlayMap.Core.Numerics;

/// <summary>PCG32 (O'Neill, XSH-RR): a small, fast, seeded generator. A value type, so a simulation's
/// state copies with it — two nodes seeded alike draw the same particles, and a late joiner copies the
/// leader's generator. Not for secrets (those come from RandomNumberGenerator).</summary>
public struct Pcg32 : IEquatable<Pcg32>
{
    private const ulong Multiplier = 6364136223846793005UL;
    private ulong _state;
    private readonly ulong _increment;

    public Pcg32(ulong seed, ulong stream = 0xda3e39cb94b95bdbUL)
    {
        _state = 0;
        _increment = (stream << 1) | 1;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    public uint NextUInt()
    {
        var old = _state;
        _state = unchecked(old * Multiplier + _increment);
        var xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        var rot = (int)(old >> 59);
        return (xorShifted >> rot) | (xorShifted << (-rot & 31));
    }

    /// <summary>Uniform in [0, 1) with 24 bits — exactly representable in a float.</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1.0f / 16777216f);

    /// <summary>Uniform in [0, 1) with 53 bits.</summary>
    public double NextDouble()
    {
        var hi = (ulong)NextUInt() >> 5;
        var lo = (ulong)NextUInt() >> 6;
        return (hi * 67108864.0 + lo) * (1.0 / 9007199254740992.0);
    }

    public float Range(float min, float max) => min + (max - min) * NextFloat();

    public double Range(double min, double max) => min + (max - min) * NextDouble();

    /// <summary>Uniform integer in [0, bound) without modulo bias (Lemire's method).</summary>
    public int NextInt(int bound)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bound);
        var b = (uint)bound;
        var threshold = (uint)(-(int)b) % b;
        while (true)
        {
            var r = NextUInt();
            var mul = (ulong)r * b;
            if ((uint)mul >= threshold)
                return (int)(mul >> 32);
        }
    }

    /// <summary>Standard normal by Box–Muller (one of the pair; the generator stays a plain value).</summary>
    public double NextGaussian()
    {
        double u1;
        do
            u1 = NextDouble();
        while (u1 <= double.Epsilon);
        var u2 = NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public readonly bool Equals(Pcg32 other) => _state == other._state && _increment == other._increment;
    public override readonly bool Equals(object? obj) => obj is Pcg32 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(_state, _increment);
    public static bool operator ==(Pcg32 left, Pcg32 right) => left.Equals(right);
    public static bool operator !=(Pcg32 left, Pcg32 right) => !left.Equals(right);
}
