// The overlay's shapes (ShaderLibrary.Show.cs, "overlay.shapes"): coloured triangles in the target's pixels.

#include "Common.hlsli"

#if defined(PM_PROGRAM_overlay_shapes)
struct ShapeIn
{
    float2 pos : ATTR0;
    float4 color : ATTR1;
};

struct ShapeOut
{
    float4 pos : SV_Position;
    float4 color : TEXCOORD0;
};

ShapeOut ShapesVS(ShapeIn v)
{
    ShapeOut o;
    o.color = v.color;
    o.pos = PmClip(float4(v.pos / uTarget * 2.0 - 1.0, 0.0, 1.0));
    return o;
}

float4 ShapesPS(ShapeOut i) : SV_Target
{
    return i.color;
}
#endif
