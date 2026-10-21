// The LED pixel pipeline's two passes (ShaderLibrary.Led.cs), written as PlayMap.Core.Led.LedReference is: the same order of
// operations in single precision. led_sample samples each pixel's point or footprint into linear light; led_patch turns pixels into
// DMX slots (four a texel), through each fixture's gains, curve, white extraction, the air's level and the test patterns.

#include "Common.hlsli"

#if defined(PM_PROGRAM_led_sample)
float3 Decode(float3 c) { return pow(max(c, 0.0), (float3)uGamma); }

float4 LedSamplePS(VsUv i) : SV_Target
{
    int2 p = int2(i.pos.xy);
    int n = p.y * 1024 + p.x;
    if (n < uFirst || n >= uFirst + uCount)
        discard;
    float4 a = PmLoad(SLOT_uAttr, p);
    float3 acc = (float3)0.0;
    if (uTaps <= 1)
    {
        if (a.x >= 0.0 && a.x <= 1.0 && a.y >= 0.0 && a.y <= 1.0)
        {
            int2 t = min(int2(floor(a.xy * uTexSize)), int2(uTexSize) - 1);
            acc = Decode(PmLoad(SLOT_uTex, t).rgb);
        }
    }
    else
    {
        for (int j = -1; j <= 1; j++)
        {
            for (int k = -1; k <= 1; k++)
            {
                float2 s = a.xy + float2(float(k), float(j)) * uSpread * a.zw;
                if (s.x >= 0.0 && s.x <= 1.0 && s.y >= 0.0 && s.y <= 1.0)
                    acc += Decode(PmTex(SLOT_uTex).SampleLevel(PmSmp(SLOT_uTex), s, 0).rgb);
            }
        }
        acc = acc / 9.0;
    }
    return float4(acc, 1.0);
}
#endif

#if defined(PM_PROGRAM_led_patch)
int Imod(int a, int n) { return a - (a / n) * n; }

float3 Palette(int k)
{
    k = Imod(k, 6);
    if (k == 0) return float3(1.0, 0.0, 0.0);
    if (k == 1) return float3(0.0, 1.0, 0.0);
    if (k == 2) return float3(0.0, 0.0, 1.0);
    if (k == 3) return float3(1.0, 1.0, 0.0);
    if (k == 4) return float3(0.0, 1.0, 1.0);
    return float3(1.0, 0.0, 1.0);
}

float3 Pattern(int kind, int pixel, int pixels, int fixture, int row)
{
    if (kind == 1) return pixel == Imod(uStep, max(pixels, 10)) ? (float3)1.0 : (float3)0.0;
    if (kind == 2) return pixel == 0 ? float3(1.0, 0.0, 0.0) : pixel == 1 ? float3(0.0, 1.0, 0.0) : pixel == 2 ? float3(0.0, 0.0, 1.0) : (float3)0.0;
    if (kind == 3) return Palette(row);
    if (kind == 4) return Palette(fixture);
    if (kind == 5)
    {
        int level = pixel * 255 / max(pixels - 1, 1);
        return (float3)(float(level) / 255.0);
    }
    if (kind == 6) return fixture == Imod(uStep / 10, max(uFixtures, 1)) ? (float3)1.0 : (float3)0.0;
    if (kind == 7 || kind == 8) return (float3)1.0;
    return (float3)0.0;
}

uint Quantise(float v, int sel)
{
    v = saturate(v);
    if (sel == 0)
        return uint(floor(v * 255.0 + 0.5));
    uint q = uint(floor(v * 65535.0 + 0.5));
    return sel == 1 ? (q >> 8) : (q & 255u);
}

uint Raw(float value, int sel)
{
    uint q = uint(clamp(value, 0.0, 65535.0));
    if (sel == 0)
        return min(q, 255u);
    return sel == 1 ? ((q >> 8) & 255u) : (q & 255u);
}

float Lum(float3 c) { return c.r * 0.2126 + c.g * 0.7152 + c.b * 0.0722; }

float4 ExtractWhite(float3 c)
{
    float w = min(c.r, min(c.g, c.b));
    return float4(c - w, w);
}

float Component(float4 c, float l, int k, bool full)
{
    if (k == 0) return c.r;
    if (k == 1) return c.g;
    if (k == 2) return c.b;
    if (k == 3 || k == 4 || k == 5) return c.a;
    if (k == 8) return l;
    if (k == 6 || k == 7) return full ? 1.0 : 0.0;
    return 0.0;
}

float GainOf(float4 g, int k)
{
    if (k == 0) return g.r;
    if (k == 1) return g.g;
    if (k == 2) return g.b;
    if (k == 3 || k == 4 || k == 5) return g.a;
    return 1.0;
}

uint SlotAt(int2 sp)
{
    float4 e = PmLoad(SLOT_uPatch, sp);
    if (e.y < 0.0)
        return 0u;
    int op = int(e.y);
    int k = op >> 2;
    int sel = op & 3;
    int fi = int(e.z);
    if (k == 11)
        return 0u;
    if (k == 10)
        return Raw(e.w, sel);
    float4 air = PmLoad(SLOT_uAir, int2(0, fi));
    bool black = air.z > 0.5;
    if (k == 9)
        return black ? 0u : Raw(e.w, sel);
    if (black)
        return 0u;
    float4 f0 = PmLoad(SLOT_uFix, int2(0, fi));
    float4 f1 = PmLoad(SLOT_uFix, int2(1, fi));
    float4 f2 = PmLoad(SLOT_uFix, int2(2, fi));
    float4 f3 = PmLoad(SLOT_uFix, int2(3, fi));
    int pix = int(e.x);
    bool hasWhite = f3.x > 0.5;
    if (air.w > 0.5 || (uTest != 0 && air.y > 0.5))
    {
        bool locate = air.w > 0.5;
        bool full = !locate && uTest == 8;
        float3 code = locate ? (air.w > 1.5 ? f2.rgb : (float3)1.0) : Pattern(uTest, pix - int(f1.z), int(f1.w), fi, sp.y);
        float4 c = full ? (float4)1.0 : hasWhite ? ExtractWhite(code) : float4(code, 0.0);
        float l = full ? 1.0 : Lum(code);
        float v = Component(c, l, k, full) * (uLevel * air.x);
        return Quantise(v, sel);
    }
    float3 lin = PmLoad(SLOT_uColors, int2(pix - (pix / 1024) * 1024, pix / 1024)).rgb;
    float scale = f1.x * uLevel * pow(air.x, f2.w);
    lin = lin * scale;
    float4 c2 = hasWhite ? ExtractWhite(lin) : float4(lin, 0.0);
    float v2 = Component(c2, Lum(lin), k, false) * GainOf(f0, k);
    v2 = v2 <= 0.0 ? 0.0 : pow(min(v2, 1.0), 1.0 / f1.y);
    return Quantise(v2, sel);
}

float4 LedPatchPS(VsUv i) : SV_Target
{
    int2 p = int2(i.pos.xy);
    uint s0 = SlotAt(int2(p.x * 4, p.y));
    uint s1 = SlotAt(int2(p.x * 4 + 1, p.y));
    uint s2 = SlotAt(int2(p.x * 4 + 2, p.y));
    uint s3 = SlotAt(int2(p.x * 4 + 3, p.y));
    return float4(float(s2), float(s1), float(s0), float(s3)) / 255.0;
}
#endif
