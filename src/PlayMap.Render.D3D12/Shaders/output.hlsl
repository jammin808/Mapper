// The output stage (ShaderLibrary.Output.cs, "output"): the pre-warp picture through the warp mesh, the camera-measured warp, the
// lens lookup, the mask, the blend (measured, in light, or by weights and pedestal), the trim and the dither, then the air's level.

#include "Common.hlsli"

#if defined(PM_PROGRAM_output)
struct OutputIn
{
    float2 pos : ATTR0;
    float2 tex : ATTR1;
};

struct OutputOut
{
    float4 pos : SV_Position;
    float2 tex : TEXCOORD0;
    float2 out_ : TEXCOORD1;
};

// The warp mesh's positions are in normalised output units, y down; the rotation turns the result onto the display's raster.
OutputOut OutputVS(OutputIn v)
{
    OutputOut o;
    o.tex = v.tex;
    o.out_ = v.pos;
    float2 p = v.pos;
    if (uRotation == 1)
        p = float2(1.0 - v.pos.y, v.pos.x);
    else if (uRotation == 2)
        p = float2(1.0 - v.pos.x, 1.0 - v.pos.y);
    else if (uRotation == 3)
        p = float2(v.pos.y, 1.0 - v.pos.x);
    o.pos = PmClip(float4(p * 2.0 - 1.0, 0.0, 1.0));
    return o;
}

float Hash(float2 p)
{
    float3 p3 = frac(p.xyx * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// A grid read bilinearly in float with exact reads, so it is exact on every device.
float4 Texels(uint slot, float2 p)
{
    int2 size = PmSize(slot);
    float2 c = clamp(p, (float2)0.0, float2(size - 1));
    int2 i0 = int2(floor(c));
    int2 i1 = min(i0 + 1, size - 1);
    float2 f = c - float2(i0);
    float4 a = PmLoad(slot, i0);
    float4 b = PmLoad(slot, int2(i1.x, i0.y));
    float4 d = PmLoad(slot, int2(i0.x, i1.y));
    float4 e = PmLoad(slot, i1);
    return lerp(lerp(a, b, f.x), lerp(d, e, f.x), f.y);
}

float4 OutputPS(OutputOut i) : SV_Target
{
    float2 tc = i.tex;
    bool inside = true;
    if (uHasMeasuredGrid == 1)
    {
        float4 g = Texels(SLOT_uMeasuredGrid, i.tex * float2(PmSize(SLOT_uMeasuredGrid) - 1));
        tc = g.xy;
        inside = g.z >= 0.5;
    }
    if (uHasLens == 1)
    {
        tc = PmSample(SLOT_uLens, tc).xy;
        inside = inside && tc.x >= 0.0 && tc.y >= 0.0;
    }
    inside = inside && tc.x >= 0.0 && tc.y >= 0.0 && tc.x <= 1.0 && tc.y <= 1.0;
    float3 c = (float3)0.0;
    if (inside)
        c = PmSample(SLOT_uPicture, uSrc.xy + tc * uSrc.zw).rgb;
    float m = 1.0;
    if (uHasMask == 1)
        m = PmSample(SLOT_uMask, i.out_).r;
    if (uHasMeasuredBlend == 1)
    {
        float3 b = Texels(SLOT_uMeasuredBlend, i.out_ * float2(PmSize(SLOT_uMeasuredBlend)) - 0.5).xyz;
        float3 lin = pow(max(c, 0.0), (float3)2.2) * uMeasuredGains.rgb * uMeasuredGains.w;
        float ped = b.y * uMeasuredPedestal;
        lin = ped + (1.0 - ped) * b.x * lin;
        c = pow(max(lin, 0.0), (float3)(1.0 / uMeasuredGamma)) * m;
    }
    else
    {
        if (uMatch == 1)
            c = pow(pow(max(c, 0.0), (float3)2.2) * uMeasuredGains.rgb * uMeasuredGains.w, (float3)(1.0 / uMeasuredGamma));
        float2 bw = float2(1.0, 0.0);
        if (uHasBlend == 1)
            bw = PmSample(SLOT_uBlend, i.out_).rg;
        c = c * (m * bw.x) + bw.y * m;
    }
    if (uTone.y != 1.0)
        c = pow(max(c, 0.0), (float3)uTone.y);
    c *= uGain.rgb;
    if (uTone.x != 1.0)
        c = (c - 0.5) * uTone.x + 0.5;
    if (uTone.z != 0.0)
        c = c + uTone.z * (1.0 - c);
    c = saturate(c);
    if (uTone.w > 0.0)
        c += (Hash(i.pos.xy + float2(uFrame * 7.31, uFrame * 3.17)) - 0.5) * (uTone.w / 255.0);
    c *= uAir;
    return float4(c, 1.0);
}
#endif
