namespace PlayMap.Render.Shaders;

/// <summary>wp2-content's programs, by name (sources carry no #version and no precision line): the GPU particle field's step and
/// sprites, and the raymarcher the 3D fractals share. Integer hashes are on uint (uniforms travel as int bit patterns), every loop is
/// bounded by a constant with an early break on a uniform, and nothing needs more than GLSL 3.30 or ES 3.00 with float targets.</summary>
public static partial class ShaderLibrary
{
    static partial void AddContent(Dictionary<string, (string Vertex, string Fragment)> programs)
    {
        programs["content.pstep"] = (FullscreenVs, ParticleStepFs);
        programs["content.psprite"] = (ParticleSpriteVs, ParticleSpriteFs);
        programs["content.raymarch"] = (FullscreenVs, RaymarchFs);
    }

    /// <summary>The integer hash and the C# twin's constants (Core/Content/ParticleSchedule.cs says the same words).</summary>
    private const string ContentHash = """
        uint pcg(uint v) {
            uint state = v * 747796405u + 2891336453u;
            uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
            return (word >> 22u) ^ word;
        }
        float u01(uint h) { return float(h >> 8u) * (1.0 / 16777216.0); }
        const uint KEY_LIFE = 0x9E3779B9u;
        const uint KEY_PHASE = 0x85EBCA6Bu;
        const uint KEY_ALIVE = 0xC2B2AE35u;
        const uint KEY_LIFE_INDEX = 0x27D4EB2Fu;
        """;

    /// <summary>One step of the field: slot i is reborn when its schedule says so (everything drawn from hashes of the slot and its
    /// life), else integrated from its last state under the forces. Attachment 0 = (position, age, life s; life 0 = dormant),
    /// attachment 1 = (velocity, colour key, size key).</summary>
    private const string ParticleStepFs = ContentHash + """
        uniform sampler2D uS0;
        uniform sampler2D uS1;
        uniform sampler2D uPicture;
        uniform int uSide;
        uniform int uCount;
        uniform int uStep;
        uniform int uSeedHash;
        uniform int uLifeMin;
        uniform int uLifeMax;
        uniform float uRate;
        uniform float uDt;
        uniform float uAspect;
        uniform int uEmitter;
        uniform vec4 uEmit;
        uniform vec4 uBirth;
        uniform float uThreshold;
        uniform int uHasPicture;
        uniform vec4 uForces;
        uniform vec4 uCurl;
        uniform vec4 uVortex;
        uniform vec4 uAttract;
        uniform vec4 uSurge;
        uniform int uEdges;
        uniform float uRestitution;
        uniform int uWeight;
        uniform int uInvert;
        uniform vec2 uFlow;
        uniform int uColorSource;
        layout(location = 0) out vec4 o0;
        layout(location = 1) out vec4 o1;

        float hashCell(vec2 c) {
            ivec2 i = ivec2(c);
            return u01(pcg(uint(i.x) * 1597334677u ^ pcg(uint(i.y) + uint(uSeedHash))));
        }

        // Value noise with its analytic gradient (quintic fade): (value, d/dx, d/dy).
        vec3 noised(vec2 x) {
            vec2 i = floor(x);
            vec2 f = x - i;
            vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
            vec2 du = 30.0 * f * f * (f * (f - 2.0) + 1.0);
            float a = hashCell(i);
            float b = hashCell(i + vec2(1.0, 0.0));
            float c = hashCell(i + vec2(0.0, 1.0));
            float d = hashCell(i + vec2(1.0, 1.0));
            float k1 = b - a;
            float k2 = c - a;
            float k4 = a - b - c + d;
            return vec3(a + k1 * u.x + k2 * u.y + k4 * u.x * u.y, du * (vec2(k1, k2) + k4 * u.yx));
        }

        // The curl of a noise potential: divergence-free, so the flow neither gathers nor thins the field.
        vec2 curl(vec2 p) {
            vec2 g = vec2(0.0);
            float amp = 1.0;
            float freq = uCurl.x;
            for (int o = 0; o < 3; o++) {
                if (o >= int(uCurl.z)) break;
                vec3 n = noised(p * freq + vec2(uCurl.y, -0.7 * uCurl.y) + float(o) * 17.31);
                g += amp * n.yz * freq;
                amp *= 0.5;
                freq *= 2.03;
            }
            return vec2(g.y, -g.x) / max(uCurl.x, 0.001);
        }

        float luma(vec2 uv) {
            vec3 c = texture(uPicture, uv).rgb;
            return dot(c, vec3(0.2126, 0.7152, 0.0722));
        }

        // The picture's brightness slope at a point, per picture height (central differences a 256th apart).
        vec2 slope(vec2 uv) {
            vec2 e = vec2(1.0 / 256.0, 0.0);
            return vec2(luma(uv + e.xy) - luma(uv - e.xy), luma(uv + e.yx) - luma(uv - e.yx)) * 128.0;
        }

        // What births weigh by: brightness, alpha or edges, inverted when asked.
        float weightAt(vec2 uv) {
            float w;
            if (uWeight == 1) w = texture(uPicture, uv).a;
            else if (uWeight == 2) w = clamp(length(slope(uv)) * 0.03, 0.0, 1.0);
            else w = luma(uv);
            return uInvert == 1 ? 1.0 - w : w;
        }

        // A colour packed whole into one float: 8 bits a channel, exact below 2^24.
        float packRgb(vec3 c) {
            vec3 b = floor(clamp(c, 0.0, 1.0) * 255.0 + 0.5);
            return b.r + b.g * 256.0 + b.b * 65536.0;
        }

        vec2 emitAt(uint h, float aspect, out bool ok) {
            ok = true;
            float r1 = u01(pcg(h ^ 0x68E31DA4u));
            float r2 = u01(pcg(h ^ 0xB5297A4Du));
            vec2 c = vec2(uEmit.x * aspect, uEmit.y);
            float size = uEmit.z;
            if (uEmitter == 1) return c;
            if (uEmitter == 2) return c + vec2((r1 - 0.5) * size, 0.0);
            if (uEmitter == 3) { float a = r1 * 6.28318530718; return c + vec2(cos(a), sin(a)) * size * 0.5; }
            if (uEmitter == 4) { float a = r1 * 6.28318530718; return c + vec2(cos(a), sin(a)) * size * 0.5 * sqrt(r2); }
            if (uEmitter == 5) return vec2(r1 * aspect, 0.0);
            if (uEmitter == 6) return vec2(r1 * aspect, 1.0);
            if (uEmitter == 7) return vec2(0.0, r1);
            if (uEmitter == 8) return vec2(aspect, r1);
            if (uEmitter == 9) {
                float e = r1 * (2.0 * aspect + 2.0);
                if (e < aspect) return vec2(e, 0.0);
                if (e < 2.0 * aspect) return vec2(e - aspect, 1.0);
                if (e < 2.0 * aspect + 1.0) return vec2(0.0, e - 2.0 * aspect);
                return vec2(aspect, e - 2.0 * aspect - 1.0);
            }
            if (uEmitter == 10) {
                // Where the source picture is bright: a candidate is kept with its brightness as the chance, eight tries.
                if (uHasPicture == 0) { ok = false; return vec2(0.0); }
                for (int t = 0; t < 8; t++) {
                    uint ht = pcg(h + uint(t) * 0x9E3779B9u);
                    vec2 q = vec2(u01(pcg(ht ^ 0x1B873593u)), u01(pcg(ht ^ 0xCC9E2D51u)));
                    float l = weightAt(q);
                    if (l > uThreshold && u01(pcg(ht ^ 0xE6546B64u)) < l) return vec2(q.x * aspect, q.y);
                }
                ok = false;
                return vec2(0.0);
            }
            return vec2(r1 * aspect, r2);
        }

        void main() {
            ivec2 texel = ivec2(gl_FragCoord.xy);
            int idx = texel.y * uSide + texel.x;
            if (idx >= uCount) { o0 = vec4(0.0); o1 = vec4(0.0); return; }
            uint key = uint(idx) ^ uint(uSeedHash);
            uint lifeMin = uint(max(uLifeMin, 1));
            uint span = uint(max(uLifeMax, uLifeMin)) - lifeMin + 1u;
            uint life = lifeMin + pcg(key ^ KEY_LIFE) % span;
            uint phase = pcg(key ^ KEY_PHASE) % life;
            uint shifted = uint(uStep) - phase;
            uint since = shifted % life;
            if (since == 0u) {
                uint k = (shifted - since) / life;
                uint birth = pcg(key * KEY_ALIVE ^ pcg(k ^ KEY_LIFE_INDEX));
                bool ok = u01(birth) < uRate;
                vec2 p = vec2(0.0);
                if (ok) p = emitAt(pcg(birth ^ 0x2545F491u), uAspect, ok);
                if (!ok) { o0 = vec4(0.0); o1 = vec4(0.0); return; }
                float speed = mix(uBirth.x, uBirth.y, u01(pcg(birth ^ 0x7FEB352Du)));
                float ang = uBirth.z + (u01(pcg(birth ^ 0x846CA68Bu)) - 0.5) * uBirth.w;
                float key = u01(pcg(birth ^ 0x3C6EF372u));
                if (uColorSource == 1 && uHasPicture == 1) key = packRgb(texture(uPicture, vec2(p.x / uAspect, p.y)).rgb);
                o0 = vec4(p, 0.0, float(life) * uDt);
                o1 = vec4(cos(ang) * speed, sin(ang) * speed, key, u01(pcg(birth ^ 0xA54FF53Au)));
                return;
            }
            vec4 s0 = texelFetch(uS0, texel, 0);
            vec4 s1 = texelFetch(uS1, texel, 0);
            if (s0.w <= 0.0) { o0 = vec4(0.0); o1 = vec4(0.0); return; }
            vec2 p = s0.xy;
            vec2 v = s1.xy;
            vec2 a = vec2(uForces.y + uSurge.w, uForces.x * (1.0 - 2.0 * uSurge.y));
            vec2 dv = p - vec2(uVortex.x * uAspect, uVortex.y);
            a += uVortex.z * vec2(-dv.y, dv.x) / (dot(dv, dv) + 0.01);
            vec2 ds = p - vec2(0.5 * uAspect, 0.5);
            a += uSurge.z * 4.0 * vec2(-ds.y, ds.x) / (dot(ds, ds) + 0.02);
            vec2 da = vec2(uAttract.x * uAspect, uAttract.y) - p;
            float ra = length(da);
            a += uAttract.z * (da / max(ra, 0.001)) * (1.0 - smoothstep(0.0, uAttract.w, ra));
            v += a * uDt;
            v *= max(0.0, 1.0 - uForces.z * uDt);
            vec2 flow = uForces.w * 0.1 * curl(p);
            if (uHasPicture == 1 && uFlow.x != 0.0) {
                vec2 g = slope(vec2(p.x / uAspect, p.y));
                flow += uFlow.x * 0.05 * (uFlow.y < 0.5 ? vec2(-g.y, g.x) : g);
            }
            p += (v + flow) * uDt * uSurge.x;
            float age = s0.z + uDt;
            float alive = s0.w;
            if (uEdges == 1) {
                if (p.x < 0.0) { p.x = -p.x; v.x = -v.x * uRestitution; }
                if (p.x > uAspect) { p.x = 2.0 * uAspect - p.x; v.x = -v.x * uRestitution; }
                if (p.y < 0.0) { p.y = -p.y; v.y = -v.y * uRestitution; }
                if (p.y > 1.0) { p.y = 2.0 - p.y; v.y = -v.y * uRestitution; }
            } else if (uEdges == 2) {
                p.x = p.x - floor(p.x / uAspect) * uAspect;
                p.y = p.y - floor(p.y);
            } else if (uEdges == 3) {
                if (p.x < 0.0 || p.x > uAspect || p.y < 0.0 || p.y > 1.0) alive = 0.0;
            }
            bool bad = isnan(p.x) || isnan(p.y) || isnan(v.x) || isnan(v.y) || isinf(p.x) || isinf(p.y) || isinf(v.x) || isinf(v.y);
            if (bad || alive <= 0.0) { o0 = vec4(0.0); o1 = vec4(0.0); return; }
            o0 = vec4(p, age, alive);
            o1 = vec4(v, s1.zw);
        }
        """;

    /// <summary>A slot's sprite, made from gl_VertexID (a 4-vertex strip) for instance gl_InstanceID; a dormant slot lands off
    /// the target. Size and colour over its life; positions in picture heights.</summary>
    private const string ParticleSpriteVs = """
        uniform sampler2D uS0;
        uniform sampler2D uS1;
        uniform int uSide;
        uniform float uAspect;
        uniform vec2 uTarget;
        uniform vec2 uSize;
        uniform int uSizeOverLife;
        uniform int uColorBy;
        uniform float uSpeedRef;
        uniform float uOpacity;
        uniform vec2 uAtlas;
        uniform float uGrow;
        uniform float uNcol;
        uniform vec3 uP0;
        uniform vec3 uP1;
        uniform vec3 uP2;
        uniform vec3 uP3;
        uniform vec3 uP4;
        out vec2 vUv;
        out vec4 vTint;
        vec3 slot(float i) {
            if (i < 0.5) return uP0;
            if (i < 1.5) return uP1;
            if (i < 2.5) return uP2;
            if (i < 3.5) return uP3;
            return uP4;
        }
        vec3 ramp(float v) {
            float n = max(uNcol, 1.0);
            if (n < 1.5) return uP0;
            float pos = clamp(v, 0.0, 0.9999) * (n - 1.0);
            float i = floor(pos);
            return mix(slot(i), slot(min(i + 1.0, n - 1.0)), smoothstep(0.0, 1.0, pos - i));
        }
        void main() {
            int id = gl_InstanceID;
            ivec2 t = ivec2(id % uSide, id / uSide);
            vec4 s0 = texelFetch(uS0, t, 0);
            vec4 s1 = texelFetch(uS1, t, 0);
            if (s0.w <= 0.0) { vUv = vec2(0.0); vTint = vec4(0.0); gl_Position = vec4(4.0, 4.0, 4.0, 1.0); return; }
            float u = clamp(s0.z / s0.w, 0.0, 1.0);
            float life = 1.0;
            if (uSizeOverLife == 1) life = 0.3 + 0.7 * u;
            else if (uSizeOverLife == 2) life = 1.0 - 0.7 * u;
            else if (uSizeOverLife == 3) life = 0.65 + 0.35 * sin(u * 18.8495559);
            float size = mix(uSize.x, uSize.y, s1.w) * life * uGrow * uTarget.y;
            float key = uColorBy == 0 ? u : uColorBy == 1 ? clamp(length(s1.xy) / max(uSpeedRef, 1e-4), 0.0, 1.0) : s1.z;
            float fade = smoothstep(0.0, 0.08, u) * (1.0 - smoothstep(0.85, 1.0, u));
            vec3 rgb = ramp(key);
            if (uColorBy == 3) rgb = vec3(mod(s1.z, 256.0), mod(floor(s1.z / 256.0), 256.0), floor(s1.z / 65536.0)) / 255.0;
            vTint = vec4(rgb, uOpacity * fade);
            vec2 c = vec2(float(gl_VertexID & 1), float((gl_VertexID >> 1) & 1)) - 0.5;
            vUv = vec2(uAtlas.x + (c.x + 0.5) * uAtlas.y, c.y + 0.5);
            vec2 px = vec2(s0.x / uAspect * uTarget.x, s0.y * uTarget.y) + c * max(size, 0.5);
            gl_Position = vec4(px / uTarget * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string ParticleSpriteFs = """
        in vec2 vUv;
        in vec4 vTint;
        uniform sampler2D uTex;
        out vec4 oColor;
        void main() {
            float a = texture(uTex, vUv).a * vTint.a;
            oColor = vec4(vTint.rgb * a, a);
        }
        """;

    /// <summary>The 3D fractals' one marcher: sphere tracing through the family's distance estimator (Core/Content/Fractal3D.cs is
    /// its twin, line for line), shaded by a tetrahedral normal, the step count as occlusion and the orbit trap as colour; mode 1
    /// writes the distance travelled (−1 for a miss) for the twin to be held against.</summary>
    private const string RaymarchFs = """
        uniform vec2 uRes;
        uniform vec3 uEye;
        uniform vec3 uRight;
        uniform vec3 uUp;
        uniform vec3 uFwd;
        uniform float uTanHalf;
        uniform int uKind;
        uniform int uIters;
        uniform float uPower;
        uniform float uScale;
        uniform float uFoldLimit;
        uniform float uMinR;
        uniform float uFixedR;
        uniform int uFold;
        uniform vec3 uOffset;
        uniform float uTwist;
        uniform int uSteps;
        uniform float uCone;
        uniform float uGlow;
        uniform float uAo;
        uniform float uFog;
        uniform float uBright;
        uniform float uPhase;
        uniform vec3 uBg;
        uniform int uMode;
        uniform float uNcol;
        uniform vec3 uP0;
        uniform vec3 uP1;
        uniform vec3 uP2;
        uniform vec3 uP3;
        uniform vec3 uP4;
        out vec4 oColor;
        const float FAR = 20.0;

        float deBulb(vec3 p, out float trap) {
            vec3 w = p;
            float dr = 1.0;
            float r = length(w);
            trap = 1e10;
            for (int i = 0; i < 16; i++) {
                if (i >= uIters || r > 2.0 || r < 1e-6) break;
                trap = min(trap, r * r);
                float theta = acos(clamp(w.y / r, -1.0, 1.0)) * uPower;
                float phi = atan(w.x, w.z) * uPower;
                dr = pow(r, uPower - 1.0) * uPower * dr + 1.0;
                float zr = pow(r, uPower);
                w = zr * vec3(sin(theta) * sin(phi), cos(theta), sin(theta) * cos(phi)) + p;
                r = length(w);
            }
            return 0.5 * log(max(r, 1e-6)) * r / dr;
        }

        float deBox(vec3 p, out float trap) {
            vec3 w = p;
            float dr = 1.0;
            float minR2 = uMinR * uMinR;
            float fixedR2 = uFixedR * uFixedR;
            trap = 1e10;
            for (int i = 0; i < 20; i++) {
                if (i >= uIters) break;
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

        float deKifs(vec3 p, out float trap) {
            vec3 w = p;
            float c = cos(uTwist);
            float s = sin(uTwist);
            int n = 0;
            trap = 1e10;
            for (int i = 0; i < 20; i++) {
                if (i >= uIters) break;
                w = vec3(c * w.x + s * w.z, w.y, -s * w.x + c * w.z);
                if (uFold == 0) {
                    if (w.x + w.y < 0.0) w = vec3(-w.y, -w.x, w.z);
                    if (w.x + w.z < 0.0) w = vec3(-w.z, w.y, -w.x);
                    if (w.y + w.z < 0.0) w = vec3(w.x, -w.z, -w.y);
                } else {
                    w = abs(w);
                    if (w.x < w.y) w = vec3(w.y, w.x, w.z);
                    if (w.x < w.z) w = vec3(w.z, w.y, w.x);
                    if (w.y < w.z) w = vec3(w.x, w.z, w.y);
                }
                w = w * uScale - uOffset * (uScale - 1.0);
                if (uFold == 2 && w.z < -0.5 * uOffset.z * (uScale - 1.0)) w = vec3(w.x, w.y, w.z + uOffset.z * (uScale - 1.0));
                trap = min(trap, dot(w, w));
                n++;
            }
            return (length(w) - 1.0) * pow(uScale, -float(n));
        }

        float de(vec3 p, out float trap) {
            if (uKind == 1) return deBox(p, trap);
            if (uKind == 2) return deKifs(p, trap);
            return deBulb(p, trap);
        }

        float de(vec3 p) { float t; return de(p, t); }

        vec3 slot(float i) {
            if (i < 0.5) return uP0;
            if (i < 1.5) return uP1;
            if (i < 2.5) return uP2;
            if (i < 3.5) return uP3;
            return uP4;
        }
        vec3 cycle(float v) {
            float n = max(uNcol, 1.0);
            float pos = fract(v) * n;
            float i = floor(pos);
            float j = mod(i + 1.0, n);
            return mix(slot(i), slot(j), pos - i);
        }

        void main() {
            vec2 px = gl_FragCoord.xy;
            float x = (2.0 * px.x - uRes.x) / uRes.y * uTanHalf;
            float y = (uRes.y - 2.0 * px.y) / uRes.y * uTanHalf;
            vec3 dir = normalize(uFwd + uRight * x + uUp * y);
            float t = 0.0;
            float hit = -1.0;
            int used = 0;
            float closest = 1e10;
            for (int i = 0; i < 512; i++) {
                if (i >= uSteps) break;
                float d = de(uEye + dir * t);
                closest = min(closest, d / max(t, 0.05));
                used = i;
                if (d < uCone * max(t, 0.05)) { hit = t; break; }
                t += d;
                if (t > FAR) break;
            }
            if (uMode == 1) { oColor = vec4(hit, float(used), 0.0, 1.0); return; }
            vec3 col;
            if (hit >= 0.0) {
                vec3 p = uEye + dir * hit;
                float e = max(uCone * hit * 0.5, 1e-4);
                vec2 k = vec2(1.0, -1.0);
                vec3 n = normalize(k.xyy * de(p + k.xyy * e) + k.yyx * de(p + k.yyx * e) + k.yxy * de(p + k.yxy * e) + k.xxx * de(p + k.xxx * e));
                float trap;
                de(p, trap);
                float diffuse = clamp(dot(n, normalize(vec3(0.6, 0.8, 0.4))), 0.0, 1.0);
                float ao = 1.0 - uAo * clamp(float(used) / float(max(uSteps, 1)), 0.0, 1.0);
                col = cycle(sqrt(trap) * 0.6 + uPhase) * (0.25 + 0.75 * diffuse) * ao;
                col = mix(col, uBg, 1.0 - exp(-hit * uFog * 0.3));
            } else {
                col = uBg + uGlow * 0.5 * cycle(uPhase + 0.5) * exp(-closest * 40.0);
            }
            oColor = vec4(col * uBright, 1.0);
        }
        """;
}
