// A send's picture packed into a video frame's bytes, and a received frame unpacked (ShaderLibrary.VideoIo.cs, "video.pack" and
// "video.unpack"): every read is exact (Load), and every code is rounded once to its 8- or 16-bit value, so the bytes are the same
// on every device (the byte-exact parity programs).

#include "Common.hlsli"

#if defined(PM_PROGRAM_video_pack)
static const float Kr = 0.2126;
static const float Kb = 0.0722;
static const float Kg = 1.0 - Kr - Kb;

float3 Encode(float3 rgb)
{
    float y = Kr * rgb.r + Kg * rgb.g + Kb * rgb.b;
    float cb = (rgb.b - y) / (2.0 * (1.0 - Kb));
    float cr = (rgb.r - y) / (2.0 * (1.0 - Kr));
    return float3(16.0 + 219.0 * y, 128.0 + 224.0 * cb, 128.0 + 224.0 * cr);
}

float4 At(int x, int row)
{
    float4 t = PmLoad(SLOT_uPicture, int2(clamp(x, 0, uWidth - 1), row));
    return float4(saturate(t.rgb) * uGain, t.a);
}

float Code(float c) { return clamp(floor(c + 0.5), 0.0, 255.0) / 255.0; }

float2 Word(float c)
{
    float w = clamp(floor(c + 0.5), 0.0, 65535.0);
    float hi = floor(w / 256.0);
    return float2(w - hi * 256.0, hi) / 255.0;
}

float4 VideoPackPS(VsUv i) : SV_Target
{
    int2 p = int2(i.pos.xy);
    if (uMode == 3)
    {
        int plane = p.y / uHeight;
        int row = p.y - plane * uHeight;
        int x = 2 * p.x;
        if (plane == 0)
            return float4(Word(Encode(At(x, row).rgb).x * 256.0), Word(Encode(At(x + 1, row).rgb).x * 256.0));
        if (plane == 1)
        {
            float3 e0 = Encode(At(x, row).rgb);
            float3 e1 = Encode(At(x + 1, row).rgb);
            float3 eL = Encode(At(x - 1, row).rgb);
            float2 c = (eL.yz + 2.0 * e0.yz + e1.yz) * 0.25 * 256.0;
            return float4(Word(c.x), Word(c.y));
        }
        return float4(Word(At(x, row).a * 65535.0), Word(At(x + 1, row).a * 65535.0));
    }
    if (uMode == 2)
    {
        float4 c = At(p.x, p.y);
        return float4(Code(c.b * 255.0), Code(c.g * 255.0), Code(c.r * 255.0), Code(c.a * 255.0));
    }
    if (uMode == 1 && p.y >= uHeight)
    {
        int band = p.y - uHeight;
        float4 a = (float4)0.0;
        for (int k = 0; k < 4; k++)
        {
            int byteAt = 4 * p.x + k;
            int row = 2 * band + byteAt / uWidth;
            int x = byteAt - (byteAt / uWidth) * uWidth;
            a[k] = row < uHeight ? Code(At(x, row).a * 255.0) : 0.0;
        }
        return a;
    }
    int n = p.x;
    float3 e0 = Encode(At(2 * n, p.y).rgb);
    float3 e1 = Encode(At(2 * n + 1, p.y).rgb);
    float3 eL = Encode(At(2 * n - 1, p.y).rgb);
    float2 c = (eL.yz + 2.0 * e0.yz + e1.yz) * 0.25;
    return float4(Code(c.x), Code(e0.x), Code(c.y), Code(e1.x));
}
#endif

#if defined(PM_PROGRAM_video_unpack)
float4 VideoUnpackPS(VsUv i) : SV_Target
{
    int2 p = int2(i.pos.xy);
    int n = p.x / 2;
    bool odd = (p.x - 2 * n) == 1;
    int j = min(n + 1, uPairs - 1);
    float y;
    float2 c;
    if (uMode == 0)
    {
        float4 t = PmLoad(SLOT_uLuma, int2(n, p.y));
        y = odd ? t.a : t.g;
        c = t.rb;
        if (odd)
            c = (c + PmLoad(SLOT_uLuma, int2(j, p.y)).rb) * 0.5;
    }
    else
    {
        y = PmLoad(SLOT_uLuma, p).r;
        c = PmLoad(SLOT_uChroma, int2(n, p.y)).rg;
        if (odd)
            c = (c + PmLoad(SLOT_uChroma, int2(j, p.y)).rg) * 0.5;
    }
    y *= uCode;
    c *= uCode;
    float Y = (y - 16.0) / 219.0;
    float2 C = (c - 128.0) / 224.0;
    float kr = uMatrix.x;
    float kb = uMatrix.y;
    float kg = 1.0 - kr - kb;
    float r = Y + 2.0 * (1.0 - kr) * C.y;
    float b = Y + 2.0 * (1.0 - kb) * C.x;
    float g = (Y - kr * r - kb * b) / kg;
    float a = uHasAlpha == 1 ? PmLoad(SLOT_uAlpha, p).r : 1.0;
    return float4(saturate(float3(r, g, b)), a);
}
#endif
