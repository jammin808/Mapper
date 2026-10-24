// The HAP encoder's four block programs (ShaderLibrary.Optimise.cs): BC1 colour, BC3 colour and alpha, YCoCg in BC3 (HAP Q) and
// BC4 alpha, one 4×4 block a texel, written as the block's words. Every program of the family has the same constants and differs
// only in its mode, so one encoder serves all four. Integer maths stays in uint; nothing round-trips a code through a float that
// cannot hold it.

#include "Common.hlsli"

static float3 gC[16];
static float gA[16];

float3 Expand565(uint3 q)
{
    return float3(float((q.x << 3u) | (q.x >> 2u)), float((q.y << 2u) | (q.y >> 4u)), float((q.z << 3u) | (q.z >> 2u)));
}

uint3 Quant565(float3 c)
{
    float3 m = float3(31.0, 63.0, 31.0);
    return uint3(clamp(floor(c * m / 255.0 + 0.5), (float3)0.0, m));
}

uint Pack565(uint3 q) { return (q.x << 11u) | (q.y << 5u) | q.z; }

float IndexColours(float3 e0, float3 e1, float3 w, out uint bits)
{
    float3 p2 = (2.0 * e0 + e1) / 3.0;
    float3 p3 = (e0 + 2.0 * e1) / 3.0;
    float err = 0.0;
    bits = 0u;
    for (int i = 0; i < 16; i++)
    {
        float3 c = gC[i];
        float3 d0 = c - e0; float3 d1 = c - e1; float3 d2 = c - p2; float3 d3 = c - p3;
        float s0 = dot(d0 * d0, w); float s1 = dot(d1 * d1, w); float s2 = dot(d2 * d2, w); float s3 = dot(d3 * d3, w);
        uint k = 0u; float best = s0;
        if (s1 < best) { best = s1; k = 1u; }
        if (s2 < best) { best = s2; k = 2u; }
        if (s3 < best) { best = s3; k = 3u; }
        err += best;
        bits |= k << uint(2 * i);
    }
    return err;
}

// Endpoints that minimise the squared error for the indices: each texel is a·e0 + (1 − a)·e1.
bool FitColours(uint bits, out float3 e0, out float3 e1)
{
    float aa = 0.0; float ab = 0.0; float bb = 0.0;
    float3 ax = (float3)0.0; float3 bx = (float3)0.0;
    for (int i = 0; i < 16; i++)
    {
        uint k = (bits >> uint(2 * i)) & 3u;
        float a = k == 0u ? 1.0 : k == 1u ? 0.0 : k == 2u ? 2.0 / 3.0 : 1.0 / 3.0;
        float b = 1.0 - a;
        aa += a * a; ab += a * b; bb += b * b;
        ax += a * gC[i]; bx += b * gC[i];
    }
    float det = aa * bb - ab * ab;
    if (abs(det) < 1e-6) { e0 = (float3)0.0; e1 = (float3)0.0; return false; }
    e0 = clamp((bb * ax - ab * bx) / det, 0.0, 255.0);
    e1 = clamp((aa * bx - ab * ax) / det, 0.0, 255.0);
    return true;
}

uint2 EncodeColour(float3 w, int fixB)
{
    float3 mean = (float3)0.0;
    for (int i = 0; i < 16; i++)
        mean += gC[i];
    mean /= 16.0;
    float xx = 0.0; float xy = 0.0; float xz = 0.0; float yy = 0.0; float yz = 0.0; float zz = 0.0;
    float3 lo = (float3)255.0; float3 hi = (float3)0.0;
    for (int j = 0; j < 16; j++)
    {
        float3 d = gC[j] - mean;
        xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z; yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
        lo = min(lo, gC[j]); hi = max(hi, gC[j]);
    }
    // The covariance is symmetric: GLSL's column-major literal and HLSL's row-major one are the same matrix.
    float3x3 cov = float3x3(xx, xy, xz, xy, yy, yz, xz, yz, zz);
    float3 axis = hi - lo;
    if (dot(axis, axis) < 1e-6)
        axis = (float3)1.0;
    for (int k = 0; k < 4; k++)
    {
        float3 n = mul(axis, cov);
        float l = length(n);
        if (l < 1e-6)
            break;
        axis = n / l;
    }
    axis = normalize(axis);
    float tmin = 1e9; float tmax = -1e9;
    for (int m = 0; m < 16; m++)
    {
        float t = dot(gC[m] - mean, axis);
        tmin = min(tmin, t); tmax = max(tmax, t);
    }
    float3 e0 = clamp(mean + axis * tmax, 0.0, 255.0);
    float3 e1 = clamp(mean + axis * tmin, 0.0, 255.0);
    uint3 b0 = (uint3)0u; uint3 b1 = (uint3)0u; uint bestBits = 0u; float bestErr = 1e30;
    for (int pass = 0; pass <= uRefine; pass++)
    {
        uint3 q0 = Quant565(e0); uint3 q1 = Quant565(e1);
        if (fixB >= 0) { q0.z = uint(fixB); q1.z = uint(fixB); }
        uint bits;
        float err = IndexColours(Expand565(q0), Expand565(q1), w, bits);
        if (err < bestErr) { bestErr = err; b0 = q0; b1 = q1; bestBits = bits; }
        if (pass == uRefine || !FitColours(bits, e0, e1))
            break;
    }
    if (uRefine >= 2)
    {
        uint3 top = uint3(31u, 63u, 31u);
        for (int c = 0; c < 6; c++)
        {
            int ch = c % 3;
            if (ch == 2 && fixB >= 0)
                continue;
            for (int s = -1; s <= 1; s += 2)
            {
                uint3 q0 = b0; uint3 q1 = b1;
                int v = int(c < 3 ? q0[ch] : q1[ch]) + s;
                if (v < 0 || v > int(top[ch]))
                    continue;
                if (c < 3) q0[ch] = uint(v); else q1[ch] = uint(v);
                uint bits;
                float err = IndexColours(Expand565(q0), Expand565(q1), w, bits);
                if (err < bestErr) { bestErr = err; b0 = q0; b1 = q1; bestBits = bits; }
            }
        }
    }
    uint c0 = Pack565(b0); uint c1 = Pack565(b1);
    if (c0 < c1) { uint t = c0; c0 = c1; c1 = t; bestBits ^= 0x55555555u; }
    else if (c0 == c1) bestBits = 0u;
    return uint2(c0 | (c1 << 16u), bestBits);
}

float AlphaAt(float a0, float a1, uint k, bool eight)
{
    if (k == 0u) return a0;
    if (k == 1u) return a1;
    if (eight) return (float(8u - k) * a0 + float(k - 1u) * a1) / 7.0;
    if (k == 6u) return 0.0;
    if (k == 7u) return 255.0;
    return (float(6u - k) * a0 + float(k - 1u) * a1) / 5.0;
}

float IndexAlpha(float a0, float a1, bool eight, out uint lo, out uint hi)
{
    float err = 0.0; lo = 0u; hi = 0u;
    for (int i = 0; i < 16; i++)
    {
        uint best = 0u; float be = 1e30;
        for (uint k = 0u; k < 8u; k++)
        {
            float d = gA[i] - AlphaAt(a0, a1, k, eight);
            if (d * d < be) { be = d * d; best = k; }
        }
        err += be;
        int pos = 3 * i;
        if (pos + 3 <= 32) lo |= best << uint(pos);
        else if (pos >= 32) hi |= best << uint(pos - 32);
        else { lo |= best << uint(pos); hi |= best >> uint(32 - pos); }
    }
    return err;
}

uint AlphaIndex(uint lo, uint hi, int i)
{
    int pos = 3 * i;
    if (pos + 3 <= 32) return (lo >> uint(pos)) & 7u;
    if (pos >= 32) return (hi >> uint(pos - 32)) & 7u;
    return ((lo >> uint(pos)) | (hi << uint(32 - pos))) & 7u;
}

uint2 PackAlpha(float a0, float a1, uint lo, uint hi)
{
    uint e = uint(a0) | (uint(a1) << 8u);
    return uint2(e | (lo << 16u), (lo >> 16u) | (hi << 16u));
}

uint2 EncodeAlpha()
{
    float lo = 255.0; float hi = 0.0;
    for (int i = 0; i < 16; i++) { lo = min(lo, gA[i]); hi = max(hi, gA[i]); }
    float a0 = floor(hi + 0.5); float a1 = floor(lo + 0.5);
    if (a0 <= a1)
        return uint2(uint(a0) | (uint(a0) << 8u), 0u);
    float bestA0 = a0; float bestA1 = a1; uint bestLo; uint bestHi;
    float bestErr = IndexAlpha(a0, a1, true, bestLo, bestHi);
    uint ilo = bestLo; uint ihi = bestHi;
    for (int pass = 0; pass < uRefine; pass++)
    {
        float aa = 0.0; float ab = 0.0; float bb = 0.0; float ax = 0.0; float bx = 0.0;
        for (int j = 0; j < 16; j++)
        {
            uint k = AlphaIndex(ilo, ihi, j);
            float s = k == 0u ? 0.0 : k == 1u ? 1.0 : float(k - 1u) / 7.0;
            float a = 1.0 - s;
            aa += a * a; ab += a * s; bb += s * s; ax += a * gA[j]; bx += s * gA[j];
        }
        float det = aa * bb - ab * ab;
        if (abs(det) < 1e-6)
            break;
        float n0 = floor(clamp((bb * ax - ab * bx) / det, 0.0, 255.0) + 0.5);
        float n1 = floor(clamp((aa * bx - ab * ax) / det, 0.0, 255.0) + 0.5);
        if (n0 < n1) { float t = n0; n0 = n1; n1 = t; }
        if (n0 == n1) { if (n0 < 255.0) n0 += 1.0; else n1 -= 1.0; }
        float err = IndexAlpha(n0, n1, true, ilo, ihi);
        if (err < bestErr) { bestErr = err; bestA0 = n0; bestA1 = n1; bestLo = ilo; bestHi = ihi; }
    }
    // The six-entry palette, with exact 0 and 255, over the values between them.
    float ilo6 = 255.0; float ihi6 = 0.0;
    for (int m = 0; m < 16; m++)
    {
        if (gA[m] > 4.0 && gA[m] < 251.0) { ilo6 = min(ilo6, gA[m]); ihi6 = max(ihi6, gA[m]); }
    }
    if (ilo6 > ihi6) { ilo6 = 0.0; ihi6 = 0.0; }
    float s0 = floor(ilo6 + 0.5); float s1 = floor(ihi6 + 0.5);
    uint l6; uint h6;
    float err6 = IndexAlpha(s0, s1, false, l6, h6);
    if (err6 < bestErr)
        return PackAlpha(s0, s1, l6, h6);
    return PackAlpha(bestA0, bestA1, bestLo, bestHi);
}

// mode 0: BC1; 1: BC3; 2: YCoCg in BC3 (HAP Q); 3: BC4 alpha.
uint4 Encode(int mode, float2 frag)
{
    int2 b = int2(frag);
    float maxChroma = 0.0;
    for (int i = 0; i < 16; i++)
    {
        int2 q = min(b * 4 + int2(i & 3, i >> 2), uSize - 1);
        float4 t = PmLoad(SLOT_uSrc, q);
        if (mode == 2)
        {
            float co = (t.r - t.b) * 0.5;
            float cg = (2.0 * t.g - t.r - t.b) * 0.25;
            gC[i] = float3(co, cg, 0.0);
            gA[i] = (t.r + 2.0 * t.g + t.b) * 0.25 * 255.0;
            maxChroma = max(maxChroma, max(abs(co), abs(cg)));
        }
        else
        {
            gC[i] = t.rgb * 255.0;
            gA[i] = t.a * 255.0;
        }
    }
    if (mode == 0)
        return uint4(EncodeColour((float3)1.0, -1), 0u, 0u);
    if (mode == 1)
        return uint4(EncodeAlpha(), EncodeColour((float3)1.0, -1));
    if (mode == 2)
    {
        float s = maxChroma <= 0.1245 ? 4.0 : maxChroma <= 0.249 ? 2.0 : 1.0;
        int code = s == 4.0 ? 3 : s == 2.0 ? 1 : 0;
        for (int j = 0; j < 16; j++)
            gC[j] = float3(clamp(gC[j].xy * s * 255.0 + 128.0, 0.0, 255.0), 0.0);
        return uint4(EncodeAlpha(), EncodeColour(float3(2.0, 3.0, 0.0), code));
    }
    return uint4(EncodeAlpha(), 0u, 0u);
}

#if defined(PM_PROGRAM_hapenc_bc1)
uint4 HapEncBc1PS(VsUv i) : SV_Target { return Encode(0, i.pos.xy); }
#endif

#if defined(PM_PROGRAM_hapenc_bc3)
uint4 HapEncBc3PS(VsUv i) : SV_Target { return Encode(1, i.pos.xy); }
#endif

#if defined(PM_PROGRAM_hapenc_ycocg)
uint4 HapEncYcocgPS(VsUv i) : SV_Target { return Encode(2, i.pos.xy); }
#endif

#if defined(PM_PROGRAM_hapenc_bc4)
uint4 HapEncBc4PS(VsUv i) : SV_Target { return Encode(3, i.pos.xy); }
#endif
