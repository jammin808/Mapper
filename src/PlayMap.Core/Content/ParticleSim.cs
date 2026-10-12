using PlayMap.Core.Model;
using PlayMap.Core.Numerics;

namespace PlayMap.Core.Content;

public enum ParticleEmitter
{
    TopEdge,
    BottomEdge,
    Center,
    FullArea,
}

public enum ParticleShape
{
    Circle,
    Square,
    Star,
    Streak,
    Bokeh,
}

/// <summary>Everything that makes a particle field. Value equality: two equal settings at one size are the
/// same field, so a sim re-seeds only when one of these changes.</summary>
public sealed record ParticleSettings(
    int Count,
    ParticleEmitter Emitter,
    ParticleShape Shape,
    double SizeMin,
    double SizeMax,
    double SpeedMin,
    double SpeedMax,
    /// <summary>Degrees; 0 = right, 90 = down.</summary>
    double DirectionDeg,
    double SpreadDeg,
    /// <summary>Downward acceleration, px/s² (negative rises).</summary>
    double GravityY,
    double WindX,
    double Wobble,
    /// <summary>Spin in turns a second.</summary>
    double SpinTurns,
    bool Glow,
    /// <summary>Normalised "#RRGGBB,…" list.</summary>
    string Colors,
    string Background,
    int Seed)
{
    public const int MaxCount = 20000;

    public static ParticleEmitter EmitterOf(string choice) => choice.ToLowerInvariant() switch
    {
        "bottom" => ParticleEmitter.BottomEdge,
        "center" => ParticleEmitter.Center,
        "area" => ParticleEmitter.FullArea,
        _ => ParticleEmitter.TopEdge,
    };

    public static ParticleShape ShapeOf(string choice) => choice.ToLowerInvariant() switch
    {
        "square" => ParticleShape.Square,
        "star" => ParticleShape.Star,
        "streak" => ParticleShape.Streak,
        "bokeh" => ParticleShape.Bokeh,
        _ => ParticleShape.Circle,
    };

    /// <summary>A generator's settings: a parameter the operator set wins; the rest come from the named preset
    /// (its values fill everything but the background and the seed), then the catalogue's defaults.</summary>
    public static ParticleSettings Resolve(ParamSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var kind = GeneratorCatalog.Find("particles.field")!;
        var preset = ParticlePresets.Find(set.Get("preset") ?? kind.Value(ParamSet.Empty, "preset"));
        double Num(string name, double? fromPreset) =>
            ParamValues.TryNumber(set, kind, name, out var d) ? d : fromPreset ?? ParamValues.DefaultNumber(kind, name);
        var emitter = set.Get("emitter") is not null || preset is null
            ? EmitterOf(ParamValues.Choice(set, kind, "emitter")) : preset.Emitter;
        var shape = set.Get("shape") is not null || preset is null
            ? ShapeOf(ParamValues.Choice(set, kind, "shape")) : preset.Shape;
        var glow = set.Get("glow") is not null || preset is null ? ParamValues.Flag(set, kind, "glow") : preset.Glow;
        var spinDeg = Num("rotation", preset is null ? null : preset.SpinTurns * 360);
        var colors = set.Get("colors") ?? preset?.Colors ?? kind.Value(ParamSet.Empty, "colors");
        return new ParticleSettings(
            Math.Clamp((int)Math.Round(Num("count", preset?.Count)), 1, MaxCount),
            emitter,
            shape,
            Num("sizeMin", preset?.SizeMin),
            Num("sizeMax", preset?.SizeMax),
            Num("speedMin", preset?.SpeedMin),
            Num("speedMax", preset?.SpeedMax),
            Num("direction", preset?.DirectionDeg),
            Num("spread", preset?.SpreadDeg),
            Num("gravity", preset?.GravityY),
            Num("wind", preset?.WindX),
            Num("wobble", preset?.Wobble),
            spinDeg / 360.0,
            glow,
            colors,
            ParamValues.Text(set, kind, "background"),
            ParamValues.Integer(set, kind, "seed"));
    }

    /// <summary>The settings a preset alone makes (background black, the default seed).</summary>
    public static ParticleSettings FromPreset(ParticlePreset p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new ParticleSettings(p.Count, p.Emitter, p.Shape, p.SizeMin, p.SizeMax, p.SpeedMin, p.SpeedMax, p.DirectionDeg, p.SpreadDeg,
            p.GravityY, p.WindX, p.Wobble, p.SpinTurns, p.Glow, p.Colors, "#000000", 20260829);
    }
}

/// <summary>One particle of a field, in pixels of the field's picture.</summary>
public record struct Particle
{
    public float X;
    public float Y;
    public float Vx;
    public float Vy;
    /// <summary>Radius-ish, pixels.</summary>
    public float Size;
    public float Rot;
    public float RotV;
    public float Age;
    public float WobPhase;
    public float WobFreq;
    public byte ColorIdx;
}

/// <summary>A deterministic particle field on the CPU: a fixed step of 1/120 s on the leader clock's grid,
/// its own seeded generator (a value, so a late joiner copies it with the field), and pooled arrays so a
/// frame allocates nothing. Two sims with the same settings, size and step sequence hold bit-identical
/// fields; the renderer only uploads what <see cref="WriteInstances"/> writes. Owned by one thread (the
/// render thread); <see cref="JoinTimeline"/> copies between sims on that thread.</summary>
public sealed class ParticleSim
{
    /// <summary>The fixed integration step. Every node quantises the leader clock to this grid and runs the
    /// identical step sequence, so identically-seeded sims stay bit-exact whatever their frame timing.</summary>
    public const float StepSeconds = 1f / 120f;

    /// <summary>Baselines snap to this many steps (~4.3 s), so sims configured within the same window start
    /// from the same absolute step index.</summary>
    public const long BaselineQuantum = 512;

    /// <summary>A sim further behind the clock than this (~17 s) re-anchors on the quantised grid instead of
    /// catching up.</summary>
    public const long MaxBehindSteps = 2048;

    /// <summary>The catch-up one frame may do in particle-updates (steps × particles): a sim a few seconds
    /// behind catches up over a few frames rather than in one frame that stalls the output.</summary>
    public const long CatchUpBudget = 3_000_000;

    /// <summary>Never fewer steps than a second of sim per frame, however large the field.</summary>
    public const int MinStepsPerFrame = 120;

    /// <summary>Floats per instance written by <see cref="WriteInstances"/>: x, y, size px, rotation, r, g, b, a.</summary>
    public const int InstanceFloats = 8;

    private Particle[] _pool = [];
    private ColorRgb[] _colors = [ColorRgb.White];
    private ColorRgb[] _turned = [];
    private float _turnedHue = float.NaN;
    private Pcg32 _rng = new(1);
    private int _count;
    private long _doneSteps = -1;
    private float _w = 1920;
    private float _h = 1080;
    private ParticleSettings? _settings;
    private EdgeFlux _flux = EdgeFlux.None;
    private EffectSurge _surge = EffectSurge.Zero;

    /// <summary>The settings of the field this sim holds (null before the first configure).</summary>
    public ParticleSettings? Settings => _settings;

    public int Count => _count;

    public int Width => (int)_w;

    public int Height => (int)_h;

    /// <summary>The step index the field has reached (−1 before the first advance).</summary>
    public long StepsDone => _doneSteps;

    /// <summary>The surge as of the last advance (the draw reads it).</summary>
    public EffectSurge Surge => _surge;

    /// <summary>The particles, read-only (tests and the instance writer).</summary>
    public ReadOnlySpan<Particle> Particles => _pool.AsSpan(0, _count);

    /// <summary>The steps one frame may catch up for this field's size.</summary>
    public int StepsPerFrame => (int)Math.Clamp(CatchUpBudget / Math.Max(1, _count), MinStepsPerFrame, MaxBehindSteps);

    /// <summary>Re-seeds and settles the field when the settings or the size changed; a no-op otherwise.
    /// Returns true when it re-seeded.</summary>
    public bool Configure(ParticleSettings settings, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(settings);
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (settings == _settings && width == (int)_w && height == (int)_h)
            return false;
        _settings = settings;
        _w = width;
        _h = height;
        _flux = EdgeFlux.Estimate(settings, _w, _h);
        _colors = ColorRgb.ParseList(settings.Colors, ColorRgb.White);
        _turned = [];
        _turnedHue = float.NaN;
        _count = Math.Clamp(settings.Count, 1, ParticleSettings.MaxCount);
        if (_pool.Length != _count)
            _pool = new Particle[_count];

        _rng = new Pcg32((ulong)(uint)settings.Seed);
        for (var i = 0; i < _count; i++)
            Spawn(ref _pool[i], preWarm: true);
        _doneSteps = -1;
        _surge = EffectSurge.Zero;

        // Settle the field so it never starts empty on screen.
        for (var i = 0; i < 90; i++)
            StepFixed(1f / 30f, EffectSurge.Zero);
        return true;
    }

    /// <summary>Steps the field to a leader time. A fresh sim anchors on the quantised grid; one behind by a
    /// few seconds catches up over frames (<see cref="StepsPerFrame"/>); one hopelessly behind, or one whose
    /// clock went backwards (a seek), re-anchors and keeps moving. The sting is read at the quantised step
    /// clock, so every node steps through the same surge on the same steps.</summary>
    public void Advance(double time, in EffectImpulse impulse)
    {
        if (_settings is null)
            return;
        var target = (long)(time / StepSeconds);
        if (_doneSteps < 0 || target < _doneSteps || target - _doneSteps > MaxBehindSteps)
            _doneSteps = Math.Max(0, target / BaselineQuantum * BaselineQuantum);
        var stop = Math.Min(target, _doneSteps + StepsPerFrame);
        for (; _doneSteps < stop; _doneSteps++)
            StepFixed(StepSeconds, EffectEnvelope.At(impulse, (_doneSteps + 1) * StepSeconds));
        _surge = EffectEnvelope.At(impulse, time);
    }

    /// <summary>Takes a running leader's field as this sim's own — every particle, the generator, the step
    /// index — so a picture opened late shows the same particles as the ones already drawing the field from
    /// its first frame. Both sims must hold the same settings at the same size.</summary>
    public bool JoinTimeline(ParticleSim leader)
    {
        ArgumentNullException.ThrowIfNull(leader);
        if (ReferenceEquals(leader, this) || leader._settings is null || leader._settings != _settings
            || leader._count != _count || leader._w != _w || leader._h != _h || _count == 0 || leader._doneSteps < 0)
            return false;
        Array.Copy(leader._pool, _pool, _count);
        _rng = leader._rng;
        _doneSteps = leader._doneSteps;
        _surge = leader._surge;
        return true;
    }

    /// <summary>One deterministic step under a sting's surge: everything moves faster by its Speed, and its
    /// Burst re-births a share of the field at the emitter with a kick that the envelope then settles.</summary>
    public void StepFixed(float dt, in EffectSurge surge)
    {
        if (_settings is null)
            return;
        var o = _settings;
        var speedMul = (1 + 3f * surge.Speed) * (1 - 0.95f * surge.Slow);
        var gravity = (float)o.GravityY * (1 - 2f * surge.Lift);
        var windAccel = (float)o.WindX * 0.35f + surge.Gust * 900f;
        var wobble = (float)o.Wobble;
        var starfield = o.Emitter == ParticleEmitter.Center;
        float cx = _w / 2, cy = _h / 2;
        var maxDist = MathF.Sqrt(cx * cx + cy * cy) + 1;
        var swirl = surge.Swirl;
        var swirlAngle = swirl * 2.2f * dt;
        var swirlPull = MathF.Abs(swirl) * 110f * dt;
        var swirlCos = MathF.Cos(swirlAngle);
        var swirlSin = MathF.Sin(swirlAngle);

        for (var i = 0; i < _count; i++)
        {
            ref var p = ref _pool[i];
            p.Age += dt;

            if (starfield)
            {
                // Radial acceleration: faster with distance — the classic warp. Vx carries the base speed.
                var dx = p.X - cx;
                var dy = p.Y - cy;
                var dist = MathF.Sqrt(dx * dx + dy * dy) + 0.01f;
                var speed = (0.15f + dist / maxDist * 2.6f) * p.Vx;
                p.X += dx / dist * speed * dt * speedMul;
                p.Y += dy / dist * speed * dt * speedMul;
            }
            else
            {
                p.Vx += windAccel * dt;
                p.Vy += gravity * dt;
                p.X += p.Vx * dt * speedMul + MathF.Sin(p.Age * p.WobFreq + p.WobPhase) * wobble * 42f * dt;
                p.Y += p.Vy * dt * speedMul;
            }

            if (swirl != 0)
            {
                var dx = p.X - cx;
                var dy = p.Y - cy;
                var dist = MathF.Sqrt(dx * dx + dy * dy) + 0.01f;
                var rx = dx * swirlCos - dy * swirlSin;
                var ry = dx * swirlSin + dy * swirlCos;
                var inward = MathF.Max(0f, 1f - swirlPull / dist);
                p.X = cx + rx * inward;
                p.Y = cy + ry * inward;
            }

            p.Rot += p.RotV * dt;

            // Off the picture, or not a number any more (a poisoned particle would never leave by the bounds
            // test and would hand the GPU a NaN transform): born again.
            var m = p.Size * 3 + 8;
            if (p.X < -m || p.X > _w + m || p.Y < -m || p.Y > _h + m || !float.IsFinite(p.X + p.Y + p.Vx + p.Vy))
                Spawn(ref p, preWarm: false);
        }

        if (surge.Burst > 0.001f)
        {
            var births = (int)Math.Ceiling(_count * surge.Burst * 0.015f);
            var kick = 1 + 2.5f * surge.Burst;
            for (var k = 0; k < births; k++)
            {
                var i = _rng.NextInt(_count);
                Spawn(ref _pool[i], preWarm: false);
                _pool[i].Vx *= kick;
                _pool[i].Vy *= kick;
            }
        }
    }

    private void Spawn(ref Particle p, bool preWarm)
    {
        var o = _settings!;
        p.Size = (float)Lerp(o.SizeMin, o.SizeMax, _rng.NextDouble());
        p.ColorIdx = (byte)_rng.NextInt(Math.Min(255, _colors.Length));
        p.Age = 0;
        p.WobPhase = (float)(_rng.NextDouble() * Math.PI * 2);
        p.WobFreq = (float)(0.6 + _rng.NextDouble() * 2.2);
        p.RotV = (float)(o.SpinTurns * Math.PI * 2 * (0.4 + _rng.NextDouble() * 1.2) * (_rng.NextInt(2) == 0 ? 1 : -1));
        p.Rot = (float)(_rng.NextDouble() * Math.PI * 2);

        var speed = (float)Lerp(o.SpeedMin, o.SpeedMax, _rng.NextDouble());
        var dir = (float)((o.DirectionDeg + (_rng.NextDouble() - 0.5) * o.SpreadDeg) * Math.PI / 180.0);
        p.Vx = speed * MathF.Cos(dir);
        p.Vy = speed * MathF.Sin(dir);

        var margin = p.Size * 2 + 4;
        switch (o.Emitter)
        {
            case ParticleEmitter.TopEdge:
            case ParticleEmitter.BottomEdge:
            {
                var top = o.Emitter == ParticleEmitter.TopEdge;
                if (preWarm)
                {
                    p.X = (float)(_rng.NextDouble() * _w);
                    p.Y = (float)(_rng.NextDouble() * _h);
                    break;
                }
                // The upwind side edge takes its share of births (EdgeFlux) so a drifting field keeps the
                // whole picture covered. The draw is taken on every birth, so the sequence — and every node's
                // field — never depends on the estimate's value.
                var side = _rng.NextDouble() < _flux.SideFraction;
                if (!side)
                {
                    p.X = (float)(_rng.NextDouble() * _w);
                    p.Y = top ? -margin : _h + margin;
                    break;
                }
                var u = _rng.NextDouble();
                var depth = (float)(_h * (u + (Math.Sqrt(u) - u) * _flux.AccelShare));
                var t = MathF.Min(EdgeFlux.TimeToTravel(_flux.V0, _flux.A, depth), _flux.Cross);
                p.X = _flux.FromLeft ? -margin : _w + margin;
                p.Y = top ? depth : _h - depth;
                p.Vx += _flux.Ax * t;
                p.Vy += _flux.Ay * t;
                break;
            }
            case ParticleEmitter.Center:
            {
                // Starfield: Vx carries the base speed; the position starts near the centre.
                p.Vx = Math.Max(20, speed);
                var a = _rng.NextDouble() * Math.PI * 2;
                var r = preWarm ? _rng.NextDouble() : _rng.NextDouble() * 0.06 + 0.01;
                var maxR = Math.Min(_w, _h) / 2.0;
                p.X = (float)(_w / 2 + Math.Cos(a) * r * maxR * (preWarm ? 2.2 : 1));
                p.Y = (float)(_h / 2 + Math.Sin(a) * r * maxR * (preWarm ? 2.2 : 1));
                break;
            }
            default:
                p.X = (float)(_rng.NextDouble() * _w);
                p.Y = (float)(_rng.NextDouble() * _h);
                break;
        }
    }

    /// <summary>How many particles draw at a quality share (the field always steps in full; the share decides
    /// only the draw, so two pictures at different shares show the same particles).</summary>
    public int ActiveCount(double quality = 1) =>
        _count == 0 ? 0 : Math.Max(1, Math.Min(_count, (int)Math.Round(_count * Math.Clamp(quality, 0.05, 1))));

    /// <summary>Writes the sprites to draw — <see cref="InstanceFloats"/> floats each: centre x, y in pixels,
    /// sprite size in pixels, rotation in radians, then the tint r, g, b and alpha (a quick fade-in avoids
    /// births popping). Under a sting the sprites grow with its glow and scale, a ripple rolls a ring of
    /// enlarged sprites out from the centre, and the hue turns. Returns the instance count.</summary>
    public int WriteInstances(Span<float> destination, double quality = 1)
    {
        if (_settings is null || _count == 0)
            return 0;
        var active = ActiveCount(quality);
        if (destination.Length < active * InstanceFloats)
            active = destination.Length / InstanceFloats;
        var o = _settings;
        var starfield = o.Emitter == ParticleEmitter.Center;
        var streaky = o.Shape == ParticleShape.Streak;
        float cx = _w / 2, cy = _h / 2;
        var maxDist = MathF.Sqrt(cx * cx + cy * cy) + 1;
        var surge = _surge;
        var sizeMul = 2 * (1 + 0.6f * surge.Glow) * (1 + 1.5f * surge.Scale);
        var ripple = surge.Ripple;
        var ringRadius = surge.Progress * maxDist * 1.2f;
        var ringWidth = MathF.Max(1f, maxDist * 0.12f);
        var colors = surge.Hue != 0 && ColorRgb.HueStrength(surge.Hue) > 0.002f ? Turned(surge.Hue) : _colors;

        for (var i = 0; i < active; i++)
        {
            ref readonly var p = ref _pool[i];
            var scalePx = p.Size * sizeMul;
            if (starfield || ripple > 0.001f)
            {
                var dx = p.X - cx;
                var dy = p.Y - cy;
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                if (starfield)
                    scalePx *= 0.25f + dist / maxDist * 1.6f;
                if (ripple > 0.001f)
                {
                    var d = (dist - ringRadius) / ringWidth;
                    scalePx *= 1 + 2.5f * ripple * MathF.Exp(-d * d);
                }
            }
            var rot = p.Rot;
            if (streaky || (starfield && o.Shape != ParticleShape.Bokeh))
                rot = starfield ? MathF.Atan2(p.Y - cy, p.X - cx) : MathF.Atan2(p.Vy, p.Vx);
            var col = colors[p.ColorIdx % colors.Length];
            var o8 = i * InstanceFloats;
            destination[o8] = p.X;
            destination[o8 + 1] = p.Y;
            destination[o8 + 2] = scalePx;
            destination[o8 + 3] = rot;
            destination[o8 + 4] = col.R;
            destination[o8 + 5] = col.G;
            destination[o8 + 6] = col.B;
            destination[o8 + 7] = Math.Min(1f, p.Age * 2.5f);
        }
        return active;
    }

    /// <summary>True when the field draws additively: its own glow, or a sting's strong glow.</summary>
    public bool Additive => _settings is { } s && (s.Glow || _surge.Glow > 0.25f);

    private ColorRgb[] Turned(float hue)
    {
        if (_turned.Length == _colors.Length && _turnedHue == hue)
            return _turned;
        if (_turned.Length != _colors.Length)
            _turned = new ColorRgb[_colors.Length];
        for (var i = 0; i < _colors.Length; i++)
            _turned[i] = _colors[i].TurnHue(hue);
        _turnedHue = hue;
        return _turned;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

/// <summary>Where an edge emitter's particles should be born so the whole picture stays covered. Pure. A
/// field that drifts sideways sweeps the upwind side bare when every particle is born on the top edge: in
/// the steady state the flux through the upwind side edge is as real as the flux through the top, so that
/// share of births belongs there, entering at the depth and with the sideways speed a particle born on the
/// top would have by then.</summary>
public readonly record struct EdgeFlux(float SideFraction, bool FromLeft, float AccelShare, float V0, float A, float Ax, float Ay, float Cross)
{
    public static readonly EdgeFlux None = new(0, true, 0, 0, 0, 0, 0, 0);

    /// <summary>Never more than this share leaves the side: the edge the operator chose stays the main source.</summary>
    public const float MaxSideFraction = 0.75f;

    /// <summary>Below this the side share is noise, and the estimate says so with a plain zero.</summary>
    public const float MinSideFraction = 0.02f;

    public static EdgeFlux Estimate(ParticleSettings o, float w, float h)
    {
        ArgumentNullException.ThrowIfNull(o);
        var sign = o.Emitter switch
        {
            ParticleEmitter.TopEdge => 1f,
            ParticleEmitter.BottomEdge => -1f,
            _ => 0f,
        };
        if (sign == 0 || w <= 0 || h <= 0)
            return None;

        var speed = (float)((o.SpeedMin + o.SpeedMax) / 2);
        var dir = (float)(o.DirectionDeg * Math.PI / 180);
        var vx0 = speed * MathF.Cos(dir);
        var vy0 = speed * MathF.Sin(dir);
        var ax = (float)(o.WindX * 0.35);
        var ay = (float)o.GravityY;
        var v0 = sign * vy0;
        var a = sign * ay;

        var cross = TimeToTravel(v0, a, h);
        if (float.IsInfinity(cross))
            return None;
        cross = MathF.Min(cross, 60);
        var vxMean = vx0 + 0.5f * ax * cross;
        var vyMean = MathF.Max(v0 + 0.5f * a * cross, 0.01f);
        var side = h * MathF.Abs(vxMean);
        var top = w * vyMean;
        var fraction = side / (side + top);
        if (fraction < MinSideFraction)
            return None;

        var built = MathF.Abs(ax * cross);
        var atBirth = MathF.Abs(vx0);
        var share = built + atBirth > 0 ? built / (built + atBirth) : 0;
        return new EdgeFlux(MathF.Min(fraction, MaxSideFraction), vxMean > 0, share, v0, a, ax, ay, cross);
    }

    /// <summary>Seconds for something moving at <paramref name="v0"/> with acceleration <paramref name="a"/>
    /// to first travel a distance; infinity when it never does.</summary>
    public static float TimeToTravel(float v0, float a, float distance)
    {
        if (distance <= 0)
            return 0;
        if (MathF.Abs(a) < 1e-4f)
            return v0 > 1e-4f ? distance / v0 : float.PositiveInfinity;
        var disc = v0 * v0 + 2 * a * distance;
        if (disc < 0)
            return float.PositiveInfinity;
        var root = MathF.Sqrt(disc);
        var t1 = (-v0 - root) / a;
        var t2 = (-v0 + root) / a;
        var t = float.PositiveInfinity;
        if (t1 > 0)
            t = t1;
        if (t2 > 0 && t2 < t)
            t = t2;
        return t;
    }
}
