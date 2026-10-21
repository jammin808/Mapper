// The fractal generators: Mandelbrot, Julia, burning ship, Newton and the domain warp (ShaderLibrary.cs). Every program of the family
// has the same constants, so the helpers below serve all five.

#include "Common.hlsli"

float2 Plane(float2 px)
{
    float2 d = px - uRes * 0.5;
    d = float2(d.x * uRot.x - d.y * uRot.y, d.x * uRot.y + d.y * uRot.x);
    return uCenter + d * uUpp;
}

float3 Pick(float k)
{
    if (k < 0.5) return uP0;
    if (k < 1.5) return uP1;
    if (k < 2.5) return uP2;
    if (k < 3.5) return uP3;
    return uP4;
}

float3 Pal(float u)
{
    float f = frac(u) * uNcol;
    float k = floor(f);
    float t = f - k;
    float j = k + 1.0;
    if (j >= uNcol)
        j = 0.0;
    return lerp(Pick(k), Pick(j), t);
}

float4 Finish(float v, float inside)
{
    if (inside > 0.5)
        return float4(0.0, 0.0, 0.0, 1.0);
    return float4(saturate(Pal(v * 3.0 + uOffset) * uBright), 1.0);
}

#if defined(PM_PROGRAM_fractal_mandelbrot)
float4 MandelbrotPS(VsUv i) : SV_Target
{
    float2 c0 = Plane(i.pos.xy);
    float2 z = (float2)0.0;
    float n = 0.0;
    float inside = 1.0;
    for (int k = 0; k < 256; ++k)
    {
        if (float(k) >= uIters)
            break;
        z = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c0;
        float m = dot(z, z);
        if (m > 16.0) { n = float(k) + 1.0 - log2(log2(m) * 0.5); inside = 0.0; break; }
    }
    return Finish(saturate(n / uIters), inside);
}
#endif

#if defined(PM_PROGRAM_fractal_julia)
float4 JuliaPS(VsUv i) : SV_Target
{
    float2 z = Plane(i.pos.xy);
    float n = 0.0;
    float inside = 1.0;
    for (int k = 0; k < 256; ++k)
    {
        if (float(k) >= uIters)
            break;
        z = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + uC;
        float m = dot(z, z);
        if (m > 16.0) { n = float(k) + 1.0 - log2(log2(m) * 0.5); inside = 0.0; break; }
    }
    return Finish(saturate(n / uIters), inside);
}
#endif

#if defined(PM_PROGRAM_fractal_burningship)
float4 BurningShipPS(VsUv i) : SV_Target
{
    float2 c0 = Plane(i.pos.xy);
    float2 z = (float2)0.0;
    float n = 0.0;
    float inside = 1.0;
    for (int k = 0; k < 256; ++k)
    {
        if (float(k) >= uIters)
            break;
        z = abs(z);
        z = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c0;
        float m = dot(z, z);
        if (m > 16.0) { n = float(k) + 1.0 - log2(log2(m) * 0.5); inside = 0.0; break; }
    }
    return Finish(saturate(n / uIters), inside);
}
#endif

#if defined(PM_PROGRAM_fractal_newton)
float4 NewtonPS(VsUv i) : SV_Target
{
    float2 z = Plane(i.pos.xy);
    float n = 0.0;
    for (int k = 0; k < 256; ++k)
    {
        if (float(k) >= uIters)
            break;
        float2 z2 = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y);
        float2 z3 = float2(z2.x * z.x - z2.y * z.y, z2.x * z.y + z2.y * z.x);
        float2 f = z3 - float2(1.0, 0.0);
        if (dot(f, f) < 0.000001)
            break;
        float2 d = 3.0 * z2;
        float dd = dot(d, d) + 0.000001;
        float2 q = float2(f.x * d.x + f.y * d.y, f.y * d.x - f.x * d.y) / dd;
        z = z - q;
        n = float(k) + 1.0;
    }
    float ang = atan2(z.y, z.x);
    float root = floor(PmMod(ang / 6.2831853 + 1.0 + 1.0 / 6.0, 1.0) * 3.0);
    float shade = 1.0 - saturate(n / uIters) * 0.8;
    float index = PmMod(root + floor(uOffset * uNcol), uNcol);
    return float4(saturate(Pick(index) * shade * uBright), 1.0);
}
#endif

#if defined(PM_PROGRAM_fractal_warp)
float Hash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float Noise(float2 p)
{
    float2 k = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = Hash(k);
    float b = Hash(k + float2(1.0, 0.0));
    float c2 = Hash(k + float2(0.0, 1.0));
    float d = Hash(k + float2(1.0, 1.0));
    return lerp(lerp(a, b, u.x), lerp(c2, d, u.x), u.y);
}

float Fbm(float2 p)
{
    float v = 0.0;
    float amp = 0.5;
    for (int k = 0; k < 4; ++k)
    {
        v += amp * Noise(p);
        p = p * 2.03 + float2(17.1, 9.3);
        amp *= 0.5;
    }
    return v;
}

float4 DomainWarpPS(VsUv i) : SV_Target
{
    float2 p = Plane(i.pos.xy) * 1.5;
    float q = Fbm(p + float2(uT * 0.11, uT * 0.07));
    float r = Fbm(p + (3.0 + 3.0 * uWarp) * q + float2(1.7, 9.2) - uT * 0.05);
    float v = Fbm(p + 3.0 * r);
    return float4(saturate(Pal(v * 1.5 + uOffset) * uBright), 1.0);
}
#endif
