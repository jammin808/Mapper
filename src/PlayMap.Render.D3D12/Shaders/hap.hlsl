// HAP's decoded blocks to RGB (ShaderLibrary.Decode.cs, "hap"): YCoCg (with the alpha plane for HAP Q Alpha), the alpha-only
// variant, and HAP R's HDR mode brought to SDR as the YUV program does.

#include "Common.hlsli"

#if defined(PM_PROGRAM_hap)
float3 HapPqToLinear(float3 e)
{
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;
    float3 p = pow(max(e, 0.0), (float3)(1.0 / m2));
    float3 l = pow(max(p - c1, 0.0) / (c2 - c3 * p), (float3)(1.0 / m1));
    return l * 10000.0;
}

float3 HapHlgToLinear(float3 e)
{
    const float a = 0.17883277;
    const float b = 0.28466892;
    const float c = 0.55991073;
    float3 lo = e * e / 3.0;
    float3 hi = (exp((e - c) / a) + b) / 12.0;
    float3 scene = lerp(lo, hi, step((float3)0.5, e));
    float y = dot(scene, float3(0.2627, 0.6780, 0.0593));
    return scene * pow(max(y, 1e-6), 0.2) * 1000.0;
}

float3 HapToneMap(float3 nits)
{
    float3 x = nits / 203.0;
    const float white = 1000.0 / 203.0;
    return x * (1.0 + x / (white * white)) / (1.0 + x);
}

float4 HapPS(VsUv i) : SV_Target
{
    int2 p = int2(i.pos.xy);
    float4 t = PmLoad(SLOT_uTex0, p);
    if (uMode == 1 || uMode == 2)
    {
        float s = t.b * (255.0 / 8.0) + 1.0;
        float co = (t.r - 0.50196078431373) / s;
        float cg = (t.g - 0.50196078431373) / s;
        float y = t.a;
        float3 rgb = float3(y + co - cg, y + cg, y - co - cg);
        float a = uMode == 2 ? PmLoad(SLOT_uTex1, p).r : 1.0;
        return float4(saturate(rgb), a);
    }
    if (uMode == 3)
        return float4(t.rrr, 1.0);
    if (uMode == 4)
    {
        float3 e = max(t.rgb, 0.0);
        if (uHdr.y > 2.5)
            return float4(saturate(e), 1.0);
        float3 nits = uHdr.y > 1.5 ? HapHlgToLinear(e) : uHdr.y > 0.5 ? HapPqToLinear(e) : e * uHdr.x * 203.0;
        return float4(pow(saturate(HapToneMap(nits)), (float3)(1.0 / 2.4)), 1.0);
    }
    return t;
}
#endif
