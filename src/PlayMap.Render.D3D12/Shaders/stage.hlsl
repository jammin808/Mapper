// The stage: a surface's mesh from a camera (a projector, a viewpoint, the editor's eye) and its light (ShaderLibrary.cs, "stage").

#include "Common.hlsli"

#if defined(PM_PROGRAM_stage)
struct StageIn
{
    float3 pos : ATTR0;
    float3 normal : ATTR1;
    float2 uv : ATTR2;
};

struct StageOut
{
    float4 pos : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float2 uv : TEXCOORD2;
    float4 proj : TEXCOORD3;
};

StageOut StageVS(StageIn v)
{
    float4 w = mul(float4(v.pos, 1.0), uModel);
    StageOut o;
    o.world = w.xyz;
    o.normal = mul(v.normal, uNormalMat);
    o.uv = v.uv;
    o.proj = mul(w, uProjector);
    o.pos = PmClip(mul(w, uViewProj));
    return o;
}

float3 SampleRegion(float2 uv)
{
    float2 tc = uRegion.xy + uv * uRegion.zw;
    if (any(tc < 0.0) || any(tc > 1.0))
        return (float3)0.0;
    return PmSample(SLOT_uSource, tc).rgb;
}

float3 Sweep(StageOut i)
{
    int axis = int(uParams.x + 0.5);
    float3 centre = (uBoundsMin + uBoundsMax) * 0.5;
    float p;
    float lo;
    float hi;
    if (axis == 3)
    {
        p = length(i.world - centre);
        lo = 0.0;
        hi = length(uBoundsMax - centre);
    }
    else
    {
        float3 d = axis == 0 ? float3(1.0, 0.0, 0.0) : axis == 1 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0);
        p = dot(i.world, d);
        lo = dot(uBoundsMin, d);
        hi = dot(uBoundsMax, d);
    }
    float width = max(uParams.z, 1e-4);
    float travel = (hi - lo) + 2.0 * width;
    float s = lo - width + PmMod(uTime * uParams.y, max(travel, 1e-4));
    float band = 1.0 - smoothstep(0.0, width * 0.5, abs(p - s));
    return lerp(uColorB.rgb, uColorA.rgb, band);
}

float3 Outline(StageOut i)
{
    float width = max(uParams.x, 1e-4);
    float edgeDist = min(min(i.uv.x, 1.0 - i.uv.x), min(i.uv.y, 1.0 - i.uv.y));
    float border = 1.0 - smoothstep(0.0, width, edgeDist);
    float3 n = normalize(i.normal);
    float fold = smoothstep(0.15, 0.6, length(fwidth(n)) * 4.0);
    float e = max(border, fold);
    float pulse = uParams.y > 0.0 ? 0.65 + 0.35 * sin(6.2831853 * uParams.y * uTime) : 1.0;
    return uColorA.rgb * e * pulse;
}

float3 Relight(StageOut i)
{
    float a = uTime * uParams.x / 60.0 * 6.2831853;
    float el = clamp(uParams.y, -1.0, 1.0) * 1.5707963;
    float3 l = normalize(float3(cos(a) * cos(el), sin(el), sin(a) * cos(el)));
    float lambert = max(dot(normalize(i.normal), l), 0.0);
    return uColorA.rgb * (uParams.z + (1.0 - uParams.z) * lambert);
}

float3 Tint(StageOut i)
{
    float3 n = normalize(i.normal);
    float3 v = normalize(uEye - i.world);
    float shade = 0.35 + 0.65 * abs(dot(n, v));
    return lerp(uColorA.rgb * shade, uColorB.rgb, uColorB.a);
}

// A surface's light by mode — 0 a texture through the UVs (uRegion = offset, scale), 1 projected from a viewpoint, 2 the sweep, 3
// the outline, 4 the relight, 5 the editor's tint, 6 a flat colour — then the surface mask (UV space) and the opacity.
float4 StagePS(StageOut i) : SV_Target
{
    float3 c;
    if (uMode == 0)
    {
        c = SampleRegion(i.uv);
    }
    else if (uMode == 1)
    {
        if (i.proj.w <= 0.0)
        {
            c = (float3)0.0;
        }
        else
        {
            float2 ndc = i.proj.xy / i.proj.w;
            c = SampleRegion(float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5));
        }
    }
    else if (uMode == 2)
    {
        c = Sweep(i);
    }
    else if (uMode == 3)
    {
        c = Outline(i);
    }
    else if (uMode == 4)
    {
        c = Relight(i);
    }
    else if (uMode == 5)
    {
        c = Tint(i);
    }
    else
    {
        c = uColorA.rgb;
    }
    float m = uHasMask == 1 ? PmSample(SLOT_uMask, i.uv).r : 1.0;
    return float4(c * m * uOpacity, 1.0);
}
#endif
