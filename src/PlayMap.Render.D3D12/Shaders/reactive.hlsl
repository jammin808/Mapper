// The sound-reactive generators: plasma, tunnel, kaleidoscope, pulse, vortex and star warp (ShaderLibrary.cs). Every program of the
// family has the same constants, so the helpers below serve all six.

#include "Common.hlsli"

static const float TAU = 6.28318530718;

float2 Place(float2 frag)
{
    float2 p = (frag - uRes * 0.5) / uRes.y;
    return float2(p.x * uRot.x - p.y * uRot.y, p.x * uRot.y + p.y * uRot.x);
}

float3 Slot(float k)
{
    if (k < 0.5) return uP0;
    if (k < 1.5) return uP1;
    if (k < 2.5) return uP2;
    if (k < 3.5) return uP3;
    return uP4;
}

float4 Paint(float v)
{
    float n = max(uNcol, 1.0);
    float pos = frac(v) * n;
    float k = floor(pos);
    float t = pos - k;
    t = t * t * (3.0 - 2.0 * t);
    float3 a = Slot(PmMod(k, n));
    float3 b = Slot(PmMod(k + 1.0, n));
    return float4(saturate(lerp(a, b, t) * uBright), 1.0);
}

float Plasma(float x, float y, float warp)
{
    float s = sin(x + uT)
            + sin(y * 0.9 - uT * 0.7)
            + sin((x + y) * 0.6 * warp + uT * 0.4)
            + sin(sqrt(x * x + y * y) * 1.3 * warp - uT * 1.1);
    return frac(s * 0.125 + 0.5);
}

#if defined(PM_PROGRAM_reactive_plasma)
float4 PlasmaPS(VsUv i) : SV_Target
{
    float2 p = Place(i.pos.xy);
    return Paint(Plasma(p.x * 6.0, p.y * 6.0, 0.5 + uDepth));
}
#endif

#if defined(PM_PROGRAM_reactive_tunnel)
float4 TunnelPS(VsUv i) : SV_Target
{
    float2 p = Place(i.pos.xy);
    float r = length(p);
    float a = atan2(p.y, p.x);
    float d = 0.15 + uDepth * 0.35;
    float spokes = sin(a * uSym + uT * 0.4) * 0.06 * uDepth;
    return Paint(frac(d / max(r, 0.02) - uT * 0.35 + spokes));
}
#endif

#if defined(PM_PROGRAM_reactive_kaleidoscope)
float4 KaleidoscopePS(VsUv i) : SV_Target
{
    float2 p = Place(i.pos.xy);
    float r = length(p);
    float a = atan2(p.y, p.x);
    float wedge = TAU / max(uSym, 2.0);
    float folded = abs(frac(a / wedge + 0.5) - 0.5) * wedge;
    return Paint(Plasma(cos(folded) * r * 5.0, sin(folded) * r * 5.0, 0.5 + uDepth));
}
#endif

#if defined(PM_PROGRAM_reactive_pulse)
float4 PulsePS(VsUv i) : SV_Target
{
    float2 p = Place(i.pos.xy);
    float r = length(p);
    float rings = 4.0 + uLow * uAmount * 5.0;
    float edge = sin((r * rings - uT * 0.5) * TAU) * 0.5 + 0.5;
    return Paint(saturate(pow(edge, 1.5 + uDepth * 2.0)));
}
#endif

#if defined(PM_PROGRAM_reactive_vortex)
float4 VortexPS(VsUv i) : SV_Target
{
    float2 p = Place(i.pos.xy);
    float r = length(p);
    float a = atan2(p.y, p.x);
    float twist = a + r * (2.0 + uDepth * 8.0) + uT * 0.25;
    return Paint(frac(twist / TAU * uSym * 0.5 + sin(r * 6.0 - uT * 0.4) * 0.15));
}
#endif

#if defined(PM_PROGRAM_reactive_starwarp)
float Hash1(float n) { return frac(sin(n * 12.9898) * 43758.5453); }

float4 StarWarpPS(VsUv i) : SV_Target
{
    float2 p = Place(i.pos.xy);
    float r = length(p);
    float a = atan2(p.y, p.x);
    float spokes = max(uSym, 2.0) * 6.0;
    float lane = frac(a / TAU * spokes);
    float seed = floor(a / TAU * spokes);
    float along = frac(log(max(r, 0.015)) * 0.7 + uT * 0.5 + Hash1(seed));
    float streak = along * along * along;
    float across = 1.0 - min(1.0, abs(lane - 0.5) * 6.0);
    return Paint(saturate(streak * max(across, 0.0) * (0.55 + r * 1.2)));
}
#endif
