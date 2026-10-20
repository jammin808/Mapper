// YUV planes to RGB (ShaderLibrary.cs, "yuv").

#include "Common.hlsli"

#if defined(PM_PROGRAM_yuv)
float3 PqToLinear(float3 e)
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

float3 HlgToLinear(float3 e)
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

float3 ToneMap(float3 nits)
{
    float3 x = nits / 203.0;
    const float white = 1000.0 / 203.0;
    return x * (1.0 + x / (white * white)) / (1.0 + x);
}

// Samples scaled to code values, offset and scaled by the range, converted with the matrix's Kr and Kb, then brought to SDR: PQ
// and HLG tone-mapped (BT.2020 primaries to BT.709 in linear light), linear gamma-encoded.
float4 YuvPS(VsUv i) : SV_Target
{
    float2 uvY = i.uv * uCrop;
    float2 uvC = min(i.uv * uCrop, uMaxUv);
    float y = PmSample(SLOT_uY, uvY).r * uCodeScale;
    float2 c = uPlanes == 2 ? PmSample(SLOT_uU, uvC).rg * uCodeScale
                            : float2(PmSample(SLOT_uU, uvC).r, PmSample(SLOT_uV, uvC).r) * uCodeScale;
    float Y = (y - uRange.x) * uRange.y;
    float2 C = (c - uRange.z) * uRange.w;
    float kr = uMatrix.x;
    float kb = uMatrix.y;
    float kg = 1.0 - kr - kb;
    float r = Y + 2.0 * (1.0 - kr) * C.y;
    float b = Y + 2.0 * (1.0 - kb) * C.x;
    float g = (Y - kr * r - kb * b) / kg;
    float3 rgb = saturate(float3(r, g, b));
    if (uTransfer == 1 || uTransfer == 2)
    {
        float3 lin = ToneMap(uTransfer == 1 ? PqToLinear(rgb) : HlgToLinear(rgb));
        if (uWide == 1)
        {
            lin = mul(lin, float3x3(1.6605, -0.1246, -0.0182,
                                    -0.5876, 1.1329, -0.1006,
                                    -0.0728, -0.0083, 1.1187));
        }
        rgb = pow(saturate(lin), (float3)(1.0 / 2.4));
    }
    else if (uTransfer == 3)
    {
        rgb = pow(rgb, (float3)(1.0 / 2.2));
    }
    return float4(rgb, 1.0);
}
#endif
