// A pixel surface of a wall (ShaderLibrary.Wall.cs, "pixelsurface"): the source through projective texture coordinates (s, t, q),
// so a quad's picture is not bent along its diagonal, then the mask and the opacity.

#include "Common.hlsli"

#if defined(PM_PROGRAM_pixelsurface)
struct SurfaceIn
{
    float2 pos : ATTR0;
    float3 stq : ATTR1;
};

struct SurfaceOut
{
    float4 pos : SV_Position;
    float3 stq : TEXCOORD0;
};

SurfaceOut PixelSurfaceVS(SurfaceIn v)
{
    SurfaceOut o;
    o.stq = v.stq;
    o.pos = PmClip(float4(v.pos / uTarget * 2.0 - 1.0, 0.0, 1.0));
    return o;
}

float4 PixelSurfacePS(SurfaceOut i) : SV_Target
{
    float2 uv = i.stq.xy / i.stq.z;
    float2 tc = uRegion.xy + uv * uRegion.zw;
    float3 c = (any(tc < 0.0) || any(tc > 1.0)) ? (float3)0.0 : PmSample(SLOT_uSource, tc).rgb;
    float m = uHasMask == 1 ? PmSample(SLOT_uMask, uv).r : 1.0;
    return float4(c * m * uOpacity, 1.0);
}
#endif
