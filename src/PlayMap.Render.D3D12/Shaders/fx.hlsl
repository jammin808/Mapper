// An effect's result mixed over its input (ShaderLibrary.Fx.cs, "fxmix"): by its blend (normal, add, multiply, screen,
// difference) and its mix; an ISF result drawn bottom up is read the right way up.

#include "Common.hlsli"

#if defined(PM_PROGRAM_fxmix)
float4 FxMixPS(VsUv i) : SV_Target
{
    float4 a = PmSample(SLOT_uInput, i.uv);
    float4 b = PmSample(SLOT_uFx, uFxBottomUp != 0 ? float2(i.uv.x, 1.0 - i.uv.y) : i.uv);
    float3 c = b.rgb;
    if (uBlend == 1)
        c = min(a.rgb + b.rgb, (float3)1.0);
    else if (uBlend == 2)
        c = a.rgb * b.rgb;
    else if (uBlend == 3)
        c = 1.0 - (1.0 - a.rgb) * (1.0 - b.rgb);
    else if (uBlend == 4)
        c = abs(a.rgb - b.rgb);
    return lerp(a, float4(c, b.a), saturate(uMix));
}
#endif
