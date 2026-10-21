// The content generators: checker, motion, colorcycle, syncmarks (ShaderLibrary.cs), the particle field's sprite and step, and the
// raymarched fractals (ShaderLibrary.Content.cs).

#include "Common.hlsli"

#if defined(PM_PROGRAM_checker)
float4 CheckerPS(VsUv i) : SV_Target
{
    int2 cell = int2(floor(i.pos.xy / uCell));
    bool even = ((cell.x + cell.y) & 1) == 0;
    if (uSwap == 1)
        even = !even;
    return even ? uColorA : uColorB;
}
#endif

#if defined(PM_PROGRAM_motion)
float Tri(float u, float span)
{
    float m = PmMod(u, 2.0 * span);
    return m < span ? m : 2.0 * span - m;
}

float4 MotionPS(VsUv i) : SV_Target
{
    float2 p = i.pos.xy;
    float3 c = (float3)0.0;
    if (uVariant == 0)
    {
        float span = uVertical == 1 ? uRes.y : uRes.x;
        float along = uVertical == 1 ? p.y : p.x;
        float across = uVertical == 1 ? p.x : p.y;
        float thick = min(64.0, max(2.0, floor(span / 8.0)));
        float pos = floor(uPxPerFrame > 0.0 ? PmMod(uFrame * uPxPerFrame, span) : PmMod(uTime * uSpeed, span));
        float d = PmMod(floor(along) - pos + span, span);
        if (d < thick)
            c = uAccent.rgb;
        float acrossSpan = uVertical == 1 ? uRes.x : uRes.y;
        for (int k = 1; k < 10; k++)
        {
            float mark = floor(span * float(k) / 10.0);
            if (floor(along) == mark && across < acrossSpan / 40.0)
                c = max(c, (float3)0.1882);
        }
    }
    else if (uVariant == 1)
    {
        float bw = min(uRes.x, uRes.y) * 0.18 * 1.7;
        float bh = bw * 0.55;
        float x = Tri(uTime * uSpeed, max(1.0, uRes.x - bw));
        float y = Tri(uTime * uSpeed * 0.731, max(1.0, uRes.y - bh));
        float2 q = p - float2(x, y);
        float2 h = float2(bw, bh) * 0.5;
        float r = bh * 0.16;
        float2 d = abs(q - h) - (h - r);
        float dist = length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r;
        c = lerp(uAccent.rgb, (float3)0.0, saturate(dist + 0.5));
    }
    else if (uVariant == 2)
    {
        bool even = PmMod(uFrame, 2.0) < 0.5;
        float bg = even ? 0.0 : uFlash;
        float fg = even ? uFlash : 0.0;
        float s = floor(min(uRes.x, uRes.y) / 8.0);
        bool tl = p.x < s && p.y < s;
        bool br = p.x >= uRes.x - s && p.y >= uRes.y - s;
        c = (float3)(tl || br ? fg : bg);
    }
    else if (uVariant == 3)
    {
        float2 d = p - uRes * 0.5;
        float k = 3.14159265 / (2.0 * min(uRes.x, uRes.y));
        c = (float3)(0.5 + 0.5 * sin(dot(d, d) * k - uTime * 6.0));
    }
    else
    {
        float cell = 96.0;
        float offset = PmMod(uTime * uSpeed, cell);
        float along = uVertical == 1 ? p.y : p.x;
        float across = uVertical == 1 ? p.x : p.y;
        if (PmMod(floor(along) - floor(offset) + cell, cell) < 1.0)
            c = (float3)1.0;
        else if (PmMod(floor(across), cell) < 1.0)
            c = (float3)0.3137;
    }
    return float4(c, 1.0);
}
#endif

#if defined(PM_PROGRAM_colorcycle)
float4 ColorCyclePS(VsUv i) : SV_Target
{
    float slot = max(uTime, 0.0) / uInterval;
    int n = max(uCount, 1);
    int k = int(PmMod(floor(slot), float(n)));
    float3 c = uColors[k];
    if (uFade == 1)
        c = lerp(c, uColors[int(PmMod(float(k + 1), float(n)))], frac(slot));
    return float4(c, 1.0);
}
#endif

#if defined(PM_PROGRAM_syncmarks)
float4 SyncMarksPS(VsUv i) : SV_Target
{
    float2 p = i.pos.xy;
    float3 c = (float3)uLevel;
    float barH = max(2.0, floor(uRes.y / 40.0));
    if (p.y >= uRes.y - barH)
    {
        if (p.x < uPhase * uRes.x)
            c = max(c, (float3)0.75);
        if (PmMod(floor(p.x), floor(uRes.x / 10.0)) < 1.0)
            c = (float3)0.4;
    }
    return float4(c, 1.0);
}
#endif

#if defined(PM_PROGRAM_content_psprite)
struct ParticleOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
    float4 tint : TEXCOORD1;
};

float3 Slot(float k)
{
    if (k < 0.5) return uP0;
    if (k < 1.5) return uP1;
    if (k < 2.5) return uP2;
    if (k < 3.5) return uP3;
    return uP4;
}

float3 Ramp(float v)
{
    float n = max(uNcol, 1.0);
    if (n < 1.5)
        return uP0;
    float pos = clamp(v, 0.0, 0.9999) * (n - 1.0);
    float k = floor(pos);
    return lerp(Slot(k), Slot(min(k + 1.0, n - 1.0)), smoothstep(0.0, 1.0, pos - k));
}

// One particle per instance from the state textures (position, age, life; velocity, colour key, size), a quad per particle.
ParticleOut ParticleSpriteVS(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    ParticleOut o;
    int id = int(iid);
    int2 t = int2(id % uSide, id / uSide);
    float4 s0 = PmLoad(SLOT_uS0, t);
    float4 s1 = PmLoad(SLOT_uS1, t);
    if (s0.w <= 0.0)
    {
        o.uv = (float2)0.0;
        o.tint = (float4)0.0;
        o.pos = PmClip(float4(4.0, 4.0, 4.0, 1.0));
        return o;
    }
    float u = saturate(s0.z / s0.w);
    float life = 1.0;
    if (uSizeOverLife == 1)
        life = 0.3 + 0.7 * u;
    else if (uSizeOverLife == 2)
        life = 1.0 - 0.7 * u;
    else if (uSizeOverLife == 3)
        life = 0.65 + 0.35 * sin(u * 18.8495559);
    float size = lerp(uSize.x, uSize.y, s1.w) * life * uGrow * uTarget.y;
    float key = uColorBy == 0 ? u : uColorBy == 1 ? saturate(length(s1.xy) / max(uSpeedRef, 1e-4)) : s1.z;
    float fade = smoothstep(0.0, 0.08, u) * (1.0 - smoothstep(0.85, 1.0, u));
    float3 rgb = Ramp(key);
    if (uColorBy == 3)
        rgb = float3(PmMod(s1.z, 256.0), PmMod(floor(s1.z / 256.0), 256.0), floor(s1.z / 65536.0)) / 255.0;
    o.tint = float4(rgb, uOpacity * fade);
    float2 c = float2(float(vid & 1), float((vid >> 1) & 1)) - 0.5;
    o.uv = float2(uAtlas.x + (c.x + 0.5) * uAtlas.y, c.y + 0.5);
    float2 px = float2(s0.x / uAspect * uTarget.x, s0.y * uTarget.y) + c * max(size, 0.5);
    o.pos = PmClip(float4(px / uTarget * 2.0 - 1.0, 0.0, 1.0));
    return o;
}

float4 ParticleSpritePS(ParticleOut i) : SV_Target
{
    float a = PmSample(SLOT_uTex, i.uv).a * i.tint.a;
    return float4(i.tint.rgb * a, a);
}
#endif

#if defined(PM_PROGRAM_content_pstep)
uint Pcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float U01(uint h) { return float(h >> 8u) * (1.0 / 16777216.0); }

static const uint KEY_LIFE = 0x9E3779B9u;
static const uint KEY_PHASE = 0x85EBCA6Bu;
static const uint KEY_ALIVE = 0xC2B2AE35u;
static const uint KEY_LIFE_INDEX = 0x27D4EB2Fu;

struct StepOut
{
    float4 o0 : SV_Target0;
    float4 o1 : SV_Target1;
};

float HashCell(float2 c)
{
    int2 i = int2(c);
    return U01(Pcg(uint(i.x) * 1597334677u ^ Pcg(uint(i.y) + uint(uSeedHash))));
}

// Value noise with its analytic gradient (quintic fade): (value, d/dx, d/dy).
float3 Noised(float2 x)
{
    float2 i = floor(x);
    float2 f = x - i;
    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float2 du = 30.0 * f * f * (f * (f - 2.0) + 1.0);
    float a = HashCell(i);
    float b = HashCell(i + float2(1.0, 0.0));
    float c = HashCell(i + float2(0.0, 1.0));
    float d = HashCell(i + float2(1.0, 1.0));
    float k1 = b - a;
    float k2 = c - a;
    float k4 = a - b - c + d;
    return float3(a + k1 * u.x + k2 * u.y + k4 * u.x * u.y, du * (float2(k1, k2) + k4 * u.yx));
}

// The curl of a noise potential: divergence-free, so the flow neither gathers nor thins the field.
float2 Curl(float2 p)
{
    float2 g = (float2)0.0;
    float amp = 1.0;
    float freq = uCurl.x;
    for (int o = 0; o < 3; o++)
    {
        if (o >= int(uCurl.z))
            break;
        float3 n = Noised(p * freq + float2(uCurl.y, -0.7 * uCurl.y) + float(o) * 17.31);
        g += amp * n.yz * freq;
        amp *= 0.5;
        freq *= 2.03;
    }
    return float2(g.y, -g.x) / max(uCurl.x, 0.001);
}

float Luma(float2 uv)
{
    float3 c = PmTex(SLOT_uPicture).SampleLevel(PmSmp(SLOT_uPicture), uv, 0).rgb;
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// The picture's brightness slope at a point, per picture height (central differences a 256th apart).
float2 Slope(float2 uv)
{
    float2 e = float2(1.0 / 256.0, 0.0);
    return float2(Luma(uv + e.xy) - Luma(uv - e.xy), Luma(uv + e.yx) - Luma(uv - e.yx)) * 128.0;
}

// What births weigh by: brightness, alpha or edges, inverted when asked.
float WeightAt(float2 uv)
{
    float w;
    if (uWeight == 1)
        w = PmTex(SLOT_uPicture).SampleLevel(PmSmp(SLOT_uPicture), uv, 0).a;
    else if (uWeight == 2)
        w = saturate(length(Slope(uv)) * 0.03);
    else
        w = Luma(uv);
    return uInvert == 1 ? 1.0 - w : w;
}

// A colour packed whole into one float: 8 bits a channel, exact below 2^24.
float PackRgb(float3 c)
{
    float3 b = floor(saturate(c) * 255.0 + 0.5);
    return b.r + b.g * 256.0 + b.b * 65536.0;
}

float2 EmitAt(uint h, float aspect, out bool ok)
{
    ok = true;
    float r1 = U01(Pcg(h ^ 0x68E31DA4u));
    float r2 = U01(Pcg(h ^ 0xB5297A4Du));
    float2 c = float2(uEmit.x * aspect, uEmit.y);
    float size = uEmit.z;
    if (uEmitter == 1) return c;
    if (uEmitter == 2) return c + float2((r1 - 0.5) * size, 0.0);
    if (uEmitter == 3) { float a = r1 * 6.28318530718; return c + float2(cos(a), sin(a)) * size * 0.5; }
    if (uEmitter == 4) { float a = r1 * 6.28318530718; return c + float2(cos(a), sin(a)) * size * 0.5 * sqrt(r2); }
    if (uEmitter == 5) return float2(r1 * aspect, 0.0);
    if (uEmitter == 6) return float2(r1 * aspect, 1.0);
    if (uEmitter == 7) return float2(0.0, r1);
    if (uEmitter == 8) return float2(aspect, r1);
    if (uEmitter == 9)
    {
        float e = r1 * (2.0 * aspect + 2.0);
        if (e < aspect) return float2(e, 0.0);
        if (e < 2.0 * aspect) return float2(e - aspect, 1.0);
        if (e < 2.0 * aspect + 1.0) return float2(0.0, e - 2.0 * aspect);
        return float2(aspect, e - 2.0 * aspect - 1.0);
    }
    if (uEmitter == 10)
    {
        // Where the source picture is bright: a candidate is kept with its brightness as the chance, eight tries.
        if (uHasPicture == 0) { ok = false; return (float2)0.0; }
        for (int t = 0; t < 8; t++)
        {
            uint ht = Pcg(h + uint(t) * 0x9E3779B9u);
            float2 q = float2(U01(Pcg(ht ^ 0x1B873593u)), U01(Pcg(ht ^ 0xCC9E2D51u)));
            float l = WeightAt(q);
            if (l > uThreshold && U01(Pcg(ht ^ 0xE6546B64u)) < l)
                return float2(q.x * aspect, q.y);
        }
        ok = false;
        return (float2)0.0;
    }
    return float2(r1 * aspect, r2);
}

// One step of the particle field, a particle a texel: born on its own schedule from its index and the step (never from the last
// frame's randomness), else moved by the forces, the flow and the edges.
StepOut ParticleStepPS(VsUv i)
{
    StepOut o;
    o.o0 = (float4)0.0;
    o.o1 = (float4)0.0;
    int2 texel = int2(i.pos.xy);
    int idx = texel.y * uSide + texel.x;
    if (idx >= uCount)
        return o;
    uint key = uint(idx) ^ uint(uSeedHash);
    uint lifeMin = uint(max(uLifeMin, 1));
    uint span = uint(max(uLifeMax, uLifeMin)) - lifeMin + 1u;
    uint life = lifeMin + Pcg(key ^ KEY_LIFE) % span;
    uint phase = Pcg(key ^ KEY_PHASE) % life;
    uint shifted = uint(uStep) - phase;
    uint since = shifted % life;
    if (since == 0u)
    {
        uint k = (shifted - since) / life;
        uint birth = Pcg(key * KEY_ALIVE ^ Pcg(k ^ KEY_LIFE_INDEX));
        bool ok = U01(birth) < uRate;
        float2 p = (float2)0.0;
        if (ok)
            p = EmitAt(Pcg(birth ^ 0x2545F491u), uAspect, ok);
        if (!ok)
            return o;
        float speed = lerp(uBirth.x, uBirth.y, U01(Pcg(birth ^ 0x7FEB352Du)));
        float ang = uBirth.z + (U01(Pcg(birth ^ 0x846CA68Bu)) - 0.5) * uBirth.w;
        float colourKey = U01(Pcg(birth ^ 0x3C6EF372u));
        if (uColorSource == 1 && uHasPicture == 1)
            colourKey = PackRgb(PmTex(SLOT_uPicture).SampleLevel(PmSmp(SLOT_uPicture), float2(p.x / uAspect, p.y), 0).rgb);
        o.o0 = float4(p, 0.0, float(life) * uDt);
        o.o1 = float4(cos(ang) * speed, sin(ang) * speed, colourKey, U01(Pcg(birth ^ 0xA54FF53Au)));
        return o;
    }
    float4 s0 = PmLoad(SLOT_uS0, texel);
    float4 s1 = PmLoad(SLOT_uS1, texel);
    if (s0.w <= 0.0)
        return o;
    float2 p = s0.xy;
    float2 v = s1.xy;
    float2 a = float2(uForces.y + uSurge.w, uForces.x * (1.0 - 2.0 * uSurge.y));
    float2 dv = p - float2(uVortex.x * uAspect, uVortex.y);
    a += uVortex.z * float2(-dv.y, dv.x) / (dot(dv, dv) + 0.01);
    float2 ds = p - float2(0.5 * uAspect, 0.5);
    a += uSurge.z * 4.0 * float2(-ds.y, ds.x) / (dot(ds, ds) + 0.02);
    float2 da = float2(uAttract.x * uAspect, uAttract.y) - p;
    float ra = length(da);
    a += uAttract.z * (da / max(ra, 0.001)) * (1.0 - smoothstep(0.0, uAttract.w, ra));
    v += a * uDt;
    v *= max(0.0, 1.0 - uForces.z * uDt);
    float2 flow = uForces.w * 0.1 * Curl(p);
    if (uHasPicture == 1 && uFlow.x != 0.0)
    {
        float2 g = Slope(float2(p.x / uAspect, p.y));
        flow += uFlow.x * 0.05 * (uFlow.y < 0.5 ? float2(-g.y, g.x) : g);
    }
    p += (v + flow) * uDt * uSurge.x;
    float age = s0.z + uDt;
    float alive = s0.w;
    if (uEdges == 1)
    {
        if (p.x < 0.0) { p.x = -p.x; v.x = -v.x * uRestitution; }
        if (p.x > uAspect) { p.x = 2.0 * uAspect - p.x; v.x = -v.x * uRestitution; }
        if (p.y < 0.0) { p.y = -p.y; v.y = -v.y * uRestitution; }
        if (p.y > 1.0) { p.y = 2.0 - p.y; v.y = -v.y * uRestitution; }
    }
    else if (uEdges == 2)
    {
        p.x = p.x - floor(p.x / uAspect) * uAspect;
        p.y = p.y - floor(p.y);
    }
    else if (uEdges == 3)
    {
        if (p.x < 0.0 || p.x > uAspect || p.y < 0.0 || p.y > 1.0)
            alive = 0.0;
    }
    bool bad = isnan(p.x) || isnan(p.y) || isnan(v.x) || isnan(v.y) || isinf(p.x) || isinf(p.y) || isinf(v.x) || isinf(v.y);
    if (bad || alive <= 0.0)
        return o;
    o.o0 = float4(p, age, alive);
    o.o1 = float4(v, s1.zw);
    return o;
}
#endif

#if defined(PM_PROGRAM_content_raymarch)
static const float FAR = 20.0;

float DeBulb(float3 p, out float trap)
{
    float3 w = p;
    float dr = 1.0;
    float r = length(w);
    trap = 1e10;
    for (int i = 0; i < 16; i++)
    {
        if (i >= uIters || r > 2.0 || r < 1e-6)
            break;
        trap = min(trap, r * r);
        float theta = acos(clamp(w.y / r, -1.0, 1.0)) * uPower;
        float phi = atan2(w.x, w.z) * uPower;
        dr = pow(r, uPower - 1.0) * uPower * dr + 1.0;
        float zr = pow(r, uPower);
        w = zr * float3(sin(theta) * sin(phi), cos(theta), sin(theta) * cos(phi)) + p;
        r = length(w);
    }
    return 0.5 * log(max(r, 1e-6)) * r / dr;
}

float DeBox(float3 p, out float trap)
{
    float3 w = p;
    float dr = 1.0;
    float minR2 = uMinR * uMinR;
    float fixedR2 = uFixedR * uFixedR;
    trap = 1e10;
    for (int i = 0; i < 20; i++)
    {
        if (i >= uIters)
            break;
        w = clamp(w, -uFoldLimit, uFoldLimit) * 2.0 - w;
        float r2 = dot(w, w);
        if (r2 < minR2) { float k = fixedR2 / minR2; w *= k; dr *= k; }
        else if (r2 < fixedR2) { float k = fixedR2 / r2; w *= k; dr *= k; }
        w = uScale * w + p;
        dr = dr * abs(uScale) + 1.0;
        trap = min(trap, dot(w, w));
    }
    return length(w) / abs(dr);
}

float DeKifs(float3 p, out float trap)
{
    float3 w = p;
    float c = cos(uTwist);
    float s = sin(uTwist);
    int n = 0;
    trap = 1e10;
    for (int i = 0; i < 20; i++)
    {
        if (i >= uIters)
            break;
        w = float3(c * w.x + s * w.z, w.y, -s * w.x + c * w.z);
        if (uFold == 0)
        {
            if (w.x + w.y < 0.0) w = float3(-w.y, -w.x, w.z);
            if (w.x + w.z < 0.0) w = float3(-w.z, w.y, -w.x);
            if (w.y + w.z < 0.0) w = float3(w.x, -w.z, -w.y);
        }
        else
        {
            w = abs(w);
            if (w.x < w.y) w = float3(w.y, w.x, w.z);
            if (w.x < w.z) w = float3(w.z, w.y, w.x);
            if (w.y < w.z) w = float3(w.x, w.z, w.y);
        }
        w = w * uScale - uOffset * (uScale - 1.0);
        if (uFold == 2 && w.z < -0.5 * uOffset.z * (uScale - 1.0))
            w = float3(w.x, w.y, w.z + uOffset.z * (uScale - 1.0));
        trap = min(trap, dot(w, w));
        n++;
    }
    return (length(w) - 1.0) * pow(uScale, -float(n));
}

float De(float3 p, out float trap)
{
    if (uKind == 1) return DeBox(p, trap);
    if (uKind == 2) return DeKifs(p, trap);
    return DeBulb(p, trap);
}

float De(float3 p)
{
    float t;
    return De(p, t);
}

float3 Slot(float k)
{
    if (k < 0.5) return uP0;
    if (k < 1.5) return uP1;
    if (k < 2.5) return uP2;
    if (k < 3.5) return uP3;
    return uP4;
}

float3 Cycle(float v)
{
    float n = max(uNcol, 1.0);
    float pos = frac(v) * n;
    float k = floor(pos);
    float j = PmMod(k + 1.0, n);
    return lerp(Slot(k), Slot(j), pos - k);
}

float4 RaymarchPS(VsUv i) : SV_Target
{
    float2 px = i.pos.xy;
    float x = (2.0 * px.x - uRes.x) / uRes.y * uTanHalf;
    float y = (uRes.y - 2.0 * px.y) / uRes.y * uTanHalf;
    float3 dir = normalize(uFwd + uRight * x + uUp * y);
    float t = 0.0;
    float hit = -1.0;
    int used = 0;
    float closest = 1e10;
    for (int k = 0; k < 512; k++)
    {
        if (k >= uSteps)
            break;
        float d = De(uEye + dir * t);
        closest = min(closest, d / max(t, 0.05));
        used = k;
        if (d < uCone * max(t, 0.05)) { hit = t; break; }
        t += d;
        if (t > FAR)
            break;
    }
    if (uMode == 1)
        return float4(hit, float(used), 0.0, 1.0);
    float3 col;
    if (hit >= 0.0)
    {
        float3 p = uEye + dir * hit;
        float e = max(uCone * hit * 0.5, 1e-4);
        float2 q = float2(1.0, -1.0);
        float3 n = normalize(q.xyy * De(p + q.xyy * e) + q.yyx * De(p + q.yyx * e) + q.yxy * De(p + q.yxy * e) + q.xxx * De(p + q.xxx * e));
        float trap;
        De(p, trap);
        float diffuse = saturate(dot(n, normalize(float3(0.6, 0.8, 0.4))));
        float ao = 1.0 - uAo * saturate(float(used) / float(max(uSteps, 1)));
        col = Cycle(sqrt(trap) * 0.6 + uPhase) * (0.25 + 0.75 * diffuse) * ao;
        col = lerp(col, uBg, 1.0 - exp(-hit * uFog * 0.3));
    }
    else
    {
        col = uBg + uGlow * 0.5 * Cycle(uPhase + 0.5) * exp(-closest * 40.0);
    }
    return float4(col * uBright, 1.0);
}
#endif
