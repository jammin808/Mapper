// The core programs: present, blit, solid, quad, mix, sprite and lines (ShaderLibrary.cs).

#include "Common.hlsli"

#if defined(PM_PROGRAM_present)
// A window's back buffer runs top row first, as every target here: the present draws the finished picture as it is (the GL
// present's flip answers GL's bottom-first default framebuffer, which Direct3D 12 does not have).
float4 PresentPS(VsUv i) : SV_Target
{
    return float4(PmSample(SLOT_uTex, i.uv).rgb, 1.0);
}
#endif

#if defined(PM_PROGRAM_blit)
float4 BlitPS(VsUv i) : SV_Target
{
    return float4(PmSample(SLOT_uTex, uSrc.xy + i.uv * uSrc.zw).rgb, 1.0);
}
#endif

#if defined(PM_PROGRAM_solid)
float4 SolidPS(VsUv i) : SV_Target
{
    return uColor;
}
#endif

#if defined(PM_PROGRAM_quad)
// A quad from the vertex id (a 4-vertex strip) at a rectangle of the target, in pixels.
VsUv QuadVS(uint id : SV_VertexID)
{
    float2 c = float2(float(id & 1), float((id >> 1) & 1));
    float2 p = uRect.xy + c * uRect.zw;
    VsUv o;
    o.uv = uUv.xy + c * uUv.zw;
    o.pos = PmClip(float4(p / uTarget * 2.0 - 1.0, 0.0, 1.0));
    return o;
}

// A layer or overlay shaped for the blend the pipeline is set to: normal and add use the source alpha as the factor; multiply
// writes lerp(1, s, k) for a DstColor × src blend; screen writes s·k for One + (1 − src) × dst; premultiplied scales the sample.
float4 QuadPS(VsUv i) : SV_Target
{
    float4 s = PmSample(SLOT_uTex, i.uv) * uTint;
    float k = uOpacity * s.a;
    if (uMode == 2)
        return float4(lerp(float3(1.0, 1.0, 1.0), s.rgb, k), 1.0);
    if (uMode == 3)
        return float4(s.rgb * k, 1.0);
    if (uMode == 4)
        return s * uOpacity;
    return float4(s.rgb, k);
}
#endif

#if defined(PM_PROGRAM_mix)
// Two pictures mixed in linear light: each decoded through the display gamma, weighted (from, to), summed and encoded again.
float4 MixPS(VsUv i) : SV_Target
{
    float3 a = pow(max(PmSample(SLOT_uFrom, i.uv).rgb, 0.0), uGamma);
    float3 b = pow(max(PmSample(SLOT_uTo, i.uv).rgb, 0.0), uGamma);
    float3 light = saturate(a * uWeights.x + b * uWeights.y);
    return float4(pow(light, 1.0 / uGamma), 1.0);
}
#endif

#if defined(PM_PROGRAM_sprite)
struct SpriteIn
{
    float4 sprite : ATTR0;
    float4 tint : ATTR1;
    uint id : SV_VertexID;
};

struct SpriteOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
    float4 tint : TEXCOORD1;
};

SpriteOut SpriteVS(SpriteIn v)
{
    float2 c = float2(float(v.id & 1), float((v.id >> 1) & 1)) - 0.5;
    float s = sin(v.sprite.w);
    float k = cos(v.sprite.w);
    float2 p = v.sprite.xy + uOffset + float2(c.x * k - c.y * s, c.x * s + c.y * k) * v.sprite.z;
    SpriteOut o;
    o.uv = float2(uAtlas.x + (c.x + 0.5) * uAtlas.y, c.y + 0.5);
    o.tint = v.tint;
    o.pos = PmClip(float4(p / uTarget * 2.0 - 1.0, 0.0, 1.0));
    return o;
}

float4 SpritePS(SpriteOut i) : SV_Target
{
    float a = PmSample(SLOT_uTex, i.uv).a * i.tint.a;
    return float4(i.tint.rgb * a, a);
}
#endif

#if defined(PM_PROGRAM_lines)
struct LinesIn
{
    float3 pos : ATTR0;
    float4 color : ATTR1;
};

struct LinesOut
{
    float4 pos : SV_Position;
    float4 color : TEXCOORD0;
};

LinesOut LinesVS(LinesIn v)
{
    LinesOut o;
    o.color = v.color;
    o.pos = PmClip(mul(float4(v.pos, 1.0), uViewProj));
    return o;
}

float4 LinesPS(LinesOut i) : SV_Target
{
    return i.color;
}
#endif
