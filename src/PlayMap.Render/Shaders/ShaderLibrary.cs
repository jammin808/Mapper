using System.Collections.Frozen;

namespace PlayMap.Render.Shaders;

/// <summary>Every shader the renderer runs, by name, in GLSL 3.30 core (desktop GL 3.3 and up, Mesa included). A source carries
/// no #version and no precision line: the device prepends its dialect's header (<see cref="Gpu.GlDialects.Header"/>).
/// A Direct3D backend supplies the same names in HLSL with the same uniforms.
///
/// The conventions every source keeps:
/// <list type="bullet">
/// <item>Offscreen targets are drawn top row first: NDC y = −1 is the picture's top row, so
/// <c>gl_FragCoord.xy</c> is the pixel position with y down and a texture's v = 0 is its top row — the same
/// as a picture uploaded from the CPU. Only the present into a window flips.</item>
/// <item>Matrices arrive in System.Numerics' row-vector layout; GLSL's <c>m * v</c> then computes <c>v * M</c>.</item>
/// <item>Surface UVs have (0, 0) at the picture's top-left, v down, like every texture here.</item>
/// </list></summary>
public static partial class ShaderLibrary
{
    /// <summary>Programs the packages add, by name: (vertex, fragment), from each package's ShaderLibrary.&lt;Word&gt;.cs.</summary>
    private static readonly FrozenDictionary<string, (string Vertex, string Fragment)> Added = BuildAdded();

    /// <summary>A triangle covering the target, made from gl_VertexID; vUv runs 0..1 across it, v down.</summary>
    private const string FullscreenVs = """
        out vec2 vUv;
        void main() {
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            vUv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>A quad made from gl_VertexID (a 4-vertex strip) at a rectangle of the target, in pixels.</summary>
    private const string QuadVs = """
        uniform vec4 uRect;
        uniform vec2 uTarget;
        uniform vec4 uUv;
        out vec2 vUv;
        void main() {
            vec2 c = vec2(float(gl_VertexID & 1), float((gl_VertexID >> 1) & 1));
            vec2 p = uRect.xy + c * uRect.zw;
            vUv = uUv.xy + c * uUv.zw;
            gl_Position = vec4(p / uTarget * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string PresentFs = """
        in vec2 vUv;
        uniform sampler2D uTex;
        out vec4 oColor;
        void main() {
            oColor = vec4(texture(uTex, vec2(vUv.x, 1.0 - vUv.y)).rgb, 1.0);
        }
        """;

    private const string BlitFs = """
        in vec2 vUv;
        uniform sampler2D uTex;
        uniform vec4 uSrc;
        out vec4 oColor;
        void main() {
            oColor = vec4(texture(uTex, uSrc.xy + vUv * uSrc.zw).rgb, 1.0);
        }
        """;

    /// <summary>Two pictures over the whole target, mixed in linear light: each decoded through the display
    /// gamma, weighted (uWeights = from, to), summed and encoded again — a fade hands over without the dark
    /// middle a mix of encoded values has, and a dip's weights reach black.</summary>
    private const string MixFs = """
        in vec2 vUv;
        uniform sampler2D uFrom;
        uniform sampler2D uTo;
        uniform vec2 uWeights;
        uniform float uGamma;
        out vec4 oColor;
        void main() {
            vec3 a = pow(max(texture(uFrom, vUv).rgb, 0.0), vec3(uGamma));
            vec3 b = pow(max(texture(uTo, vUv).rgb, 0.0), vec3(uGamma));
            vec3 light = clamp(a * uWeights.x + b * uWeights.y, 0.0, 1.0);
            oColor = vec4(pow(light, vec3(1.0 / uGamma)), 1.0);
        }
        """;

    private const string SolidFs = """
        uniform vec4 uColor;
        out vec4 oColor;
        void main() { oColor = uColor; }
        """;

    /// <summary>A layer or overlay: the source shaped for the blend the device is set to. Normal and add use
    /// the source alpha as the factor; multiply writes mix(1, s, k) for a DstColor × src blend; screen writes
    /// s·k for One + (1 − src) × dst; premultiplied scales the whole sample.</summary>
    private const string QuadFs = """
        in vec2 vUv;
        uniform sampler2D uTex;
        uniform float uOpacity;
        uniform int uMode;
        uniform vec4 uTint;
        out vec4 oColor;
        void main() {
            vec4 s = texture(uTex, vUv) * uTint;
            float k = uOpacity * s.a;
            if (uMode == 2) oColor = vec4(mix(vec3(1.0), s.rgb, k), 1.0);
            else if (uMode == 3) oColor = vec4(s.rgb * k, 1.0);
            else if (uMode == 4) oColor = s * uOpacity;
            else oColor = vec4(s.rgb, k);
        }
        """;

    /// <summary>YUV planes to RGB. Samples are scaled to code values (uCodeScale), offset and scaled by the
    /// range (uRange = y offset, y scale, c offset, c scale), converted with the matrix's Kr and Kb, then the
    /// transfer is brought to SDR: PQ and HLG are tone-mapped (BT.2020 primaries to BT.709 in linear light),
    /// linear is gamma-encoded.</summary>
    private const string YuvFs = """
        in vec2 vUv;
        uniform sampler2D uY;
        uniform sampler2D uU;
        uniform sampler2D uV;
        uniform int uPlanes;
        uniform float uCodeScale;
        uniform vec4 uRange;
        uniform vec2 uMatrix;
        uniform int uTransfer;
        uniform int uWide;
        out vec4 oColor;

        vec3 pqToLinear(vec3 e) {
            const float m1 = 0.1593017578125;
            const float m2 = 78.84375;
            const float c1 = 0.8359375;
            const float c2 = 18.8515625;
            const float c3 = 18.6875;
            vec3 p = pow(max(e, 0.0), vec3(1.0 / m2));
            vec3 l = pow(max(p - c1, 0.0) / (c2 - c3 * p), vec3(1.0 / m1));
            return l * 10000.0;
        }

        vec3 hlgToLinear(vec3 e) {
            const float a = 0.17883277;
            const float b = 0.28466892;
            const float c = 0.55991073;
            vec3 lo = e * e / 3.0;
            vec3 hi = (exp((e - c) / a) + b) / 12.0;
            vec3 scene = mix(lo, hi, step(vec3(0.5), e));
            float y = dot(scene, vec3(0.2627, 0.6780, 0.0593));
            return scene * pow(max(y, 1e-6), 0.2) * 1000.0;
        }

        vec3 toneMap(vec3 nits) {
            vec3 x = nits / 203.0;
            const float white = 1000.0 / 203.0;
            return x * (1.0 + x / (white * white)) / (1.0 + x);
        }

        void main() {
            float y = texture(uY, vUv).r * uCodeScale;
            vec2 c = uPlanes == 2 ? texture(uU, vUv).rg * uCodeScale
                                  : vec2(texture(uU, vUv).r, texture(uV, vUv).r) * uCodeScale;
            float Y = (y - uRange.x) * uRange.y;
            vec2 C = (c - uRange.z) * uRange.w;
            float kr = uMatrix.x;
            float kb = uMatrix.y;
            float kg = 1.0 - kr - kb;
            float r = Y + 2.0 * (1.0 - kr) * C.y;
            float b = Y + 2.0 * (1.0 - kb) * C.x;
            float g = (Y - kr * r - kb * b) / kg;
            vec3 rgb = clamp(vec3(r, g, b), 0.0, 1.0);
            if (uTransfer == 1 || uTransfer == 2) {
                vec3 lin = toneMap(uTransfer == 1 ? pqToLinear(rgb) : hlgToLinear(rgb));
                if (uWide == 1) {
                    lin = mat3(1.6605, -0.1246, -0.0182,
                               -0.5876, 1.1329, -0.1006,
                               -0.0728, -0.0083, 1.1187) * lin;
                }
                rgb = pow(clamp(lin, 0.0, 1.0), vec3(1.0 / 2.4));
            } else if (uTransfer == 3) {
                rgb = pow(rgb, vec3(1.0 / 2.2));
            }
            oColor = vec4(rgb, 1.0);
        }
        """;

    /// <summary>The stage: a surface's mesh from a camera (a projector, a viewpoint, the editor's eye).</summary>
    private const string StageVs = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        uniform mat4 uModel;
        uniform mat4 uViewProj;
        uniform mat3 uNormalMat;
        uniform mat4 uProjector;
        out vec3 vWorld;
        out vec3 vNormal;
        out vec2 vUv;
        out vec4 vProj;
        void main() {
            vec4 w = uModel * vec4(aPos, 1.0);
            vWorld = w.xyz;
            vNormal = uNormalMat * aNormal;
            vUv = aUv;
            vProj = uProjector * w;
            gl_Position = uViewProj * w;
        }
        """;

    /// <summary>A surface's light: its content by mode — 0 a texture through the UVs (uRegion = offset, scale),
    /// 1 projected from a viewpoint, 2 the sweep, 3 the outline, 4 the relight, 5 the editor's tint, 6 a flat
    /// colour — then the surface mask (UV space) and the opacity.</summary>
    private const string StageFs = """
        in vec3 vWorld;
        in vec3 vNormal;
        in vec2 vUv;
        in vec4 vProj;
        uniform int uMode;
        uniform sampler2D uSource;
        uniform vec4 uRegion;
        uniform sampler2D uMask;
        uniform int uHasMask;
        uniform float uOpacity;
        uniform vec4 uColorA;
        uniform vec4 uColorB;
        uniform vec4 uParams;
        uniform float uTime;
        uniform vec3 uBoundsMin;
        uniform vec3 uBoundsMax;
        uniform vec3 uEye;
        out vec4 oColor;

        vec3 sampleRegion(vec2 uv) {
            vec2 tc = uRegion.xy + uv * uRegion.zw;
            if (any(lessThan(tc, vec2(0.0))) || any(greaterThan(tc, vec2(1.0)))) return vec3(0.0);
            return texture(uSource, tc).rgb;
        }

        vec3 sweep() {
            int axis = int(uParams.x + 0.5);
            vec3 centre = (uBoundsMin + uBoundsMax) * 0.5;
            float p;
            float lo;
            float hi;
            if (axis == 3) {
                p = length(vWorld - centre);
                lo = 0.0;
                hi = length(uBoundsMax - centre);
            } else {
                vec3 d = axis == 0 ? vec3(1.0, 0.0, 0.0) : axis == 1 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);
                p = dot(vWorld, d);
                lo = dot(uBoundsMin, d);
                hi = dot(uBoundsMax, d);
            }
            float width = max(uParams.z, 1e-4);
            float travel = (hi - lo) + 2.0 * width;
            float s = lo - width + mod(uTime * uParams.y, max(travel, 1e-4));
            float band = 1.0 - smoothstep(0.0, width * 0.5, abs(p - s));
            return mix(uColorB.rgb, uColorA.rgb, band);
        }

        vec3 outline() {
            float width = max(uParams.x, 1e-4);
            float edgeDist = min(min(vUv.x, 1.0 - vUv.x), min(vUv.y, 1.0 - vUv.y));
            float border = 1.0 - smoothstep(0.0, width, edgeDist);
            vec3 n = normalize(vNormal);
            float fold = smoothstep(0.15, 0.6, length(fwidth(n)) * 4.0);
            float e = max(border, fold);
            float pulse = uParams.y > 0.0 ? 0.65 + 0.35 * sin(6.2831853 * uParams.y * uTime) : 1.0;
            return uColorA.rgb * e * pulse;
        }

        vec3 relight() {
            float a = uTime * uParams.x / 60.0 * 6.2831853;
            float el = clamp(uParams.y, -1.0, 1.0) * 1.5707963;
            vec3 l = normalize(vec3(cos(a) * cos(el), sin(el), sin(a) * cos(el)));
            float lambert = max(dot(normalize(vNormal), l), 0.0);
            return uColorA.rgb * (uParams.z + (1.0 - uParams.z) * lambert);
        }

        vec3 tint() {
            vec3 n = normalize(vNormal);
            vec3 v = normalize(uEye - vWorld);
            float shade = 0.35 + 0.65 * abs(dot(n, v));
            return mix(uColorA.rgb * shade, uColorB.rgb, uColorB.a);
        }

        void main() {
            vec3 c;
            if (uMode == 0) {
                c = sampleRegion(vUv);
            } else if (uMode == 1) {
                if (vProj.w <= 0.0) {
                    c = vec3(0.0);
                } else {
                    vec2 ndc = vProj.xy / vProj.w;
                    vec2 tc = vec2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
                    c = sampleRegion(tc);
                }
            } else if (uMode == 2) {
                c = sweep();
            } else if (uMode == 3) {
                c = outline();
            } else if (uMode == 4) {
                c = relight();
            } else if (uMode == 5) {
                c = tint();
            } else {
                c = uColorA.rgb;
            }
            float m = uHasMask == 1 ? texture(uMask, vUv).r : 1.0;
            oColor = vec4(c * m * uOpacity, 1.0);
        }
        """;

    /// <summary>The output stage: the pre-warp picture through the warp mesh (positions in normalised output
    /// units, y down; texture coordinates into the picture), through the lens lookup when there is one, times
    /// the output mask and the blend weight, plus the black pedestal, then the trim — gamma (an exponent, as
    /// Patterns' trims), gains × brightness, contrast about mid-grey, lift — and a dither under half a code
    /// (an exact code stays exact; a value between codes is spread). The rotation turns the result onto the
    /// display's raster.</summary>
    private const string OutputVs = """
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aTex;
        uniform int uRotation;
        out vec2 vTex;
        out vec2 vOut;
        void main() {
            vTex = aTex;
            vOut = aPos;
            vec2 p = aPos;
            if (uRotation == 1) p = vec2(1.0 - aPos.y, aPos.x);
            else if (uRotation == 2) p = vec2(1.0 - aPos.x, 1.0 - aPos.y);
            else if (uRotation == 3) p = vec2(aPos.y, 1.0 - aPos.x);
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string OutputFs = """
        in vec2 vTex;
        in vec2 vOut;
        uniform sampler2D uPicture;
        uniform vec4 uSrc;
        uniform sampler2D uLens;
        uniform int uHasLens;
        uniform sampler2D uMask;
        uniform int uHasMask;
        uniform sampler2D uBlend;
        uniform int uHasBlend;
        uniform vec4 uGain;
        uniform vec4 uTone;
        uniform float uFrame;
        uniform float uAir;
        out vec4 oColor;

        float hash(vec2 p) {
            vec3 p3 = fract(vec3(p.xyx) * 0.1031);
            p3 += dot(p3, p3.yzx + 33.33);
            return fract((p3.x + p3.y) * p3.z);
        }

        void main() {
            vec2 tc = vTex;
            bool inside = true;
            if (uHasLens == 1) {
                tc = texture(uLens, tc).xy;
                inside = tc.x >= 0.0 && tc.y >= 0.0;
            }
            inside = inside && tc.x >= 0.0 && tc.y >= 0.0 && tc.x <= 1.0 && tc.y <= 1.0;
            vec3 c = vec3(0.0);
            if (inside) c = texture(uPicture, uSrc.xy + tc * uSrc.zw).rgb;
            float m = 1.0;
            if (uHasMask == 1) m = texture(uMask, vOut).r;
            vec2 bw = vec2(1.0, 0.0);
            if (uHasBlend == 1) bw = texture(uBlend, vOut).rg;
            c = c * (m * bw.x) + vec3(bw.y * m);
            if (uTone.y != 1.0) c = pow(max(c, 0.0), vec3(uTone.y));
            c *= uGain.rgb;
            if (uTone.x != 1.0) c = (c - 0.5) * uTone.x + 0.5;
            if (uTone.z != 0.0) c = c + uTone.z * (1.0 - c);
            c = clamp(c, 0.0, 1.0);
            if (uTone.w > 0.0) c += (hash(gl_FragCoord.xy + vec2(uFrame * 7.31, uFrame * 3.17)) - 0.5) * (uTone.w / 255.0);
            c *= uAir;
            oColor = vec4(c, 1.0);
        }
        """;

    /// <summary>Particles: one instanced quad per sprite (centre, size, rotation; tint and alpha), the shape
    /// from a white atlas, written premultiplied for an over or an additive blend.</summary>
    private const string SpriteVs = """
        layout(location = 0) in vec4 aSprite;
        layout(location = 1) in vec4 aTint;
        uniform vec2 uTarget;
        uniform vec2 uAtlas;
        uniform vec2 uOffset;
        out vec2 vUv;
        out vec4 vTint;
        void main() {
            vec2 c = vec2(float(gl_VertexID & 1), float((gl_VertexID >> 1) & 1)) - 0.5;
            float s = sin(aSprite.w);
            float k = cos(aSprite.w);
            vec2 p = aSprite.xy + uOffset + vec2(c.x * k - c.y * s, c.x * s + c.y * k) * aSprite.z;
            vUv = vec2(uAtlas.x + (c.x + 0.5) * uAtlas.y, c.y + 0.5);
            vTint = aTint;
            gl_Position = vec4(p / uTarget * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string SpriteFs = """
        in vec2 vUv;
        in vec4 vTint;
        uniform sampler2D uTex;
        out vec4 oColor;
        void main() {
            float a = texture(uTex, vUv).a * vTint.a;
            oColor = vec4(vTint.rgb * a, a);
        }
        """;

    private const string LinesVs = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec4 aColor;
        uniform mat4 uViewProj;
        out vec4 vColor;
        void main() {
            vColor = aColor;
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    private const string LinesFs = """
        in vec4 vColor;
        out vec4 oColor;
        void main() { oColor = vColor; }
        """;

    // ---- generators ----

    private const string CheckerFs = """
        uniform float uCell;
        uniform vec4 uColorA;
        uniform vec4 uColorB;
        uniform int uSwap;
        out vec4 oColor;
        void main() {
            ivec2 cell = ivec2(floor(gl_FragCoord.xy / uCell));
            bool even = ((cell.x + cell.y) & 1) == 0;
            if (uSwap == 1) even = !even;
            oColor = even ? uColorA : uColorB;
        }
        """;

    /// <summary>The motion tests (variant 0 bar, 1 box, 2 frame flash, 3 zone plate, 4 scrolling grid), all a
    /// pure function of the leader time and the show's frame index.</summary>
    private const string MotionFs = """
        uniform vec2 uRes;
        uniform int uVariant;
        uniform float uTime;
        uniform float uFrame;
        uniform float uSpeed;
        uniform float uPxPerFrame;
        uniform int uVertical;
        uniform vec4 uAccent;
        uniform float uFlash;
        out vec4 oColor;

        float tri(float u, float span) {
            float m = mod(u, 2.0 * span);
            return m < span ? m : 2.0 * span - m;
        }

        void main() {
            vec2 p = gl_FragCoord.xy;
            vec3 c = vec3(0.0);
            if (uVariant == 0) {
                float span = uVertical == 1 ? uRes.y : uRes.x;
                float along = uVertical == 1 ? p.y : p.x;
                float across = uVertical == 1 ? p.x : p.y;
                float thick = min(64.0, max(2.0, floor(span / 8.0)));
                float pos = floor(uPxPerFrame > 0.0 ? mod(uFrame * uPxPerFrame, span) : mod(uTime * uSpeed, span));
                float d = mod(floor(along) - pos + span, span);
                if (d < thick) c = uAccent.rgb;
                float acrossSpan = uVertical == 1 ? uRes.x : uRes.y;
                for (int i = 1; i < 10; i++) {
                    float mark = floor(span * float(i) / 10.0);
                    if (floor(along) == mark && across < acrossSpan / 40.0) c = max(c, vec3(0.1882));
                }
            } else if (uVariant == 1) {
                float bw = min(uRes.x, uRes.y) * 0.18 * 1.7;
                float bh = bw * 0.55;
                float x = tri(uTime * uSpeed, max(1.0, uRes.x - bw));
                float y = tri(uTime * uSpeed * 0.731, max(1.0, uRes.y - bh));
                vec2 q = p - vec2(x, y);
                vec2 h = vec2(bw, bh) * 0.5;
                float r = bh * 0.16;
                vec2 d = abs(q - h) - (h - r);
                float dist = length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r;
                c = mix(uAccent.rgb, vec3(0.0), clamp(dist + 0.5, 0.0, 1.0));
            } else if (uVariant == 2) {
                bool even = mod(uFrame, 2.0) < 0.5;
                float bg = even ? 0.0 : uFlash;
                float fg = even ? uFlash : 0.0;
                float s = floor(min(uRes.x, uRes.y) / 8.0);
                bool tl = p.x < s && p.y < s;
                bool br = p.x >= uRes.x - s && p.y >= uRes.y - s;
                bool tr = p.x >= uRes.x - s && p.y < s;
                bool bl = p.x < s && p.y >= uRes.y - s;
                c = vec3(tl || br ? fg : (tr || bl ? bg : bg));
            } else if (uVariant == 3) {
                vec2 d = p - uRes * 0.5;
                float k = 3.14159265 / (2.0 * min(uRes.x, uRes.y));
                c = vec3(0.5 + 0.5 * sin(dot(d, d) * k - uTime * 6.0));
            } else {
                float cell = 96.0;
                float offset = mod(uTime * uSpeed, cell);
                float along = uVertical == 1 ? p.y : p.x;
                float across = uVertical == 1 ? p.x : p.y;
                if (mod(floor(along) - floor(offset) + cell, cell) < 1.0) c = vec3(1.0);
                else if (mod(floor(across), cell) < 1.0) c = vec3(0.3137);
            }
            oColor = vec4(c, 1.0);
        }
        """;

    private const string ColorCycleFs = """
        uniform vec3 uColors[16];
        uniform int uCount;
        uniform float uTime;
        uniform float uInterval;
        uniform int uFade;
        out vec4 oColor;
        void main() {
            float slot = max(uTime, 0.0) / uInterval;
            int n = max(uCount, 1);
            int i = int(mod(floor(slot), float(n)));
            vec3 c = uColors[i];
            if (uFade == 1) c = mix(c, uColors[int(mod(float(i + 1), float(n)))], fract(slot));
            oColor = vec4(c, 1.0);
        }
        """;

    /// <summary>The sync marks: black, a white flash on the leader's grid (already through the flash guard:
    /// uLevel), and along the bottom a bar that grows from each mark to the next — a clock a camera can read
    /// to a frame.</summary>
    private const string SyncMarksFs = """
        uniform vec2 uRes;
        uniform float uLevel;
        uniform float uPhase;
        out vec4 oColor;
        void main() {
            vec2 p = gl_FragCoord.xy;
            vec3 c = vec3(uLevel);
            float barH = max(2.0, floor(uRes.y / 40.0));
            if (p.y >= uRes.y - barH) {
                if (p.x < uPhase * uRes.x) c = max(c, vec3(0.75));
                if (mod(floor(p.x), floor(uRes.x / 10.0)) < 1.0) c = vec3(0.4);
            }
            oColor = vec4(c, 1.0);
        }
        """;

    private const string FractalCommon = """
        uniform vec2 uRes;
        uniform vec2 uCenter;
        uniform float uUpp;
        uniform vec2 uC;
        uniform float uIters;
        uniform float uOffset;
        uniform float uBright;
        uniform float uNcol;
        uniform vec3 uP0;
        uniform vec3 uP1;
        uniform vec3 uP2;
        uniform vec3 uP3;
        uniform vec3 uP4;
        uniform float uT;
        uniform vec2 uRot;
        uniform float uWarp;
        out vec4 oColor;
        vec2 plane(vec2 px) {
            vec2 d = px - uRes * 0.5;
            d = vec2(d.x * uRot.x - d.y * uRot.y, d.x * uRot.y + d.y * uRot.x);
            return uCenter + d * uUpp;
        }
        vec3 pick(float i) {
            if (i < 0.5) return uP0;
            if (i < 1.5) return uP1;
            if (i < 2.5) return uP2;
            if (i < 3.5) return uP3;
            return uP4;
        }
        vec3 pal(float u) {
            float f = fract(u) * uNcol;
            float i = floor(f);
            float k = f - i;
            float j = i + 1.0;
            if (j >= uNcol) j = 0.0;
            return mix(pick(i), pick(j), k);
        }
        vec4 finish(float v, float inside) {
            if (inside > 0.5) return vec4(0.0, 0.0, 0.0, 1.0);
            return vec4(clamp(pal(v * 3.0 + uOffset) * uBright, 0.0, 1.0), 1.0);
        }

        """;

    private const string MandelbrotFs = FractalCommon + """
        void main() {
            vec2 c0 = plane(gl_FragCoord.xy);
            vec2 z = vec2(0.0);
            float n = 0.0;
            float inside = 1.0;
            for (int i = 0; i < 256; ++i) {
                if (float(i) >= uIters) break;
                z = vec2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c0;
                float m = dot(z, z);
                if (m > 16.0) { n = float(i) + 1.0 - log2(log2(m) * 0.5); inside = 0.0; break; }
            }
            oColor = finish(clamp(n / uIters, 0.0, 1.0), inside);
        }
        """;

    private const string JuliaFs = FractalCommon + """
        void main() {
            vec2 z = plane(gl_FragCoord.xy);
            float n = 0.0;
            float inside = 1.0;
            for (int i = 0; i < 256; ++i) {
                if (float(i) >= uIters) break;
                z = vec2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + uC;
                float m = dot(z, z);
                if (m > 16.0) { n = float(i) + 1.0 - log2(log2(m) * 0.5); inside = 0.0; break; }
            }
            oColor = finish(clamp(n / uIters, 0.0, 1.0), inside);
        }
        """;

    private const string BurningShipFs = FractalCommon + """
        void main() {
            vec2 c0 = plane(gl_FragCoord.xy);
            vec2 z = vec2(0.0);
            float n = 0.0;
            float inside = 1.0;
            for (int i = 0; i < 256; ++i) {
                if (float(i) >= uIters) break;
                z = abs(z);
                z = vec2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c0;
                float m = dot(z, z);
                if (m > 16.0) { n = float(i) + 1.0 - log2(log2(m) * 0.5); inside = 0.0; break; }
            }
            oColor = finish(clamp(n / uIters, 0.0, 1.0), inside);
        }
        """;

    private const string NewtonFs = FractalCommon + """
        void main() {
            vec2 z = plane(gl_FragCoord.xy);
            float n = 0.0;
            for (int i = 0; i < 256; ++i) {
                if (float(i) >= uIters) break;
                vec2 z2 = vec2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y);
                vec2 z3 = vec2(z2.x * z.x - z2.y * z.y, z2.x * z.y + z2.y * z.x);
                vec2 f = z3 - vec2(1.0, 0.0);
                if (dot(f, f) < 0.000001) break;
                vec2 d = 3.0 * z2;
                float dd = dot(d, d) + 0.000001;
                vec2 q = vec2(f.x * d.x + f.y * d.y, f.y * d.x - f.x * d.y) / dd;
                z = z - q;
                n = float(i) + 1.0;
            }
            float ang = atan(z.y, z.x);
            float root = floor(mod(ang / 6.2831853 + 1.0 + 1.0 / 6.0, 1.0) * 3.0);
            float shade = 1.0 - clamp(n / uIters, 0.0, 1.0) * 0.8;
            float index = mod(root + floor(uOffset * uNcol), uNcol);
            oColor = vec4(clamp(pick(index) * shade * uBright, 0.0, 1.0), 1.0);
        }
        """;

    private const string DomainWarpFs = FractalCommon + """
        float hash(vec2 p) {
            p = fract(p * vec2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return fract(p.x * p.y);
        }
        float noise(vec2 p) {
            vec2 i = floor(p);
            vec2 f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            float a = hash(i);
            float b = hash(i + vec2(1.0, 0.0));
            float c2 = hash(i + vec2(0.0, 1.0));
            float d = hash(i + vec2(1.0, 1.0));
            return mix(mix(a, b, u.x), mix(c2, d, u.x), u.y);
        }
        float fbm(vec2 p) {
            float v = 0.0;
            float amp = 0.5;
            for (int i = 0; i < 4; ++i) {
                v += amp * noise(p);
                p = p * 2.03 + vec2(17.1, 9.3);
                amp *= 0.5;
            }
            return v;
        }
        void main() {
            vec2 p = plane(gl_FragCoord.xy) * 1.5;
            float q = fbm(p + vec2(uT * 0.11, uT * 0.07));
            float r = fbm(p + (3.0 + 3.0 * uWarp) * q + vec2(1.7, 9.2) - uT * 0.05);
            float v = fbm(p + 3.0 * r);
            oColor = vec4(clamp(pal(v * 1.5 + uOffset) * uBright, 0.0, 1.0), 1.0);
        }
        """;

    private const string ReactiveCommon = """
        uniform vec2 uRes;
        uniform float uT;
        uniform vec2 uRot;
        uniform float uDepth;
        uniform float uSym;
        uniform float uLow;
        uniform float uAmount;
        uniform float uBright;
        uniform float uNcol;
        uniform vec3 uP0;
        uniform vec3 uP1;
        uniform vec3 uP2;
        uniform vec3 uP3;
        uniform vec3 uP4;
        out vec4 oColor;
        const float TAU = 6.28318530718;
        vec2 place(vec2 frag) {
            vec2 p = (frag - uRes * 0.5) / uRes.y;
            return vec2(p.x * uRot.x - p.y * uRot.y, p.x * uRot.y + p.y * uRot.x);
        }
        vec3 slot(float i) {
            if (i < 0.5) return uP0;
            if (i < 1.5) return uP1;
            if (i < 2.5) return uP2;
            if (i < 3.5) return uP3;
            return uP4;
        }
        vec4 paint(float v) {
            float n = max(uNcol, 1.0);
            float pos = fract(v) * n;
            float i = floor(pos);
            float k = pos - i;
            k = k * k * (3.0 - 2.0 * k);
            vec3 a = slot(mod(i, n));
            vec3 b = slot(mod(i + 1.0, n));
            return vec4(clamp(mix(a, b, k) * uBright, 0.0, 1.0), 1.0);
        }
        float plasma(float x, float y, float warp) {
            float s = sin(x + uT)
                    + sin(y * 0.9 - uT * 0.7)
                    + sin((x + y) * 0.6 * warp + uT * 0.4)
                    + sin(sqrt(x * x + y * y) * 1.3 * warp - uT * 1.1);
            return fract(s * 0.125 + 0.5);
        }

        """;

    private const string PlasmaFs = ReactiveCommon + """
        void main() {
            vec2 p = place(gl_FragCoord.xy);
            oColor = paint(plasma(p.x * 6.0, p.y * 6.0, 0.5 + uDepth));
        }
        """;

    private const string TunnelFs = ReactiveCommon + """
        void main() {
            vec2 p = place(gl_FragCoord.xy);
            float r = length(p);
            float a = atan(p.y, p.x);
            float d = 0.15 + uDepth * 0.35;
            float spokes = sin(a * uSym + uT * 0.4) * 0.06 * uDepth;
            oColor = paint(fract(d / max(r, 0.02) - uT * 0.35 + spokes));
        }
        """;

    private const string KaleidoscopeFs = ReactiveCommon + """
        void main() {
            vec2 p = place(gl_FragCoord.xy);
            float r = length(p);
            float a = atan(p.y, p.x);
            float wedge = TAU / max(uSym, 2.0);
            float folded = abs(fract(a / wedge + 0.5) - 0.5) * wedge;
            oColor = paint(plasma(cos(folded) * r * 5.0, sin(folded) * r * 5.0, 0.5 + uDepth));
        }
        """;

    private const string PulseFs = ReactiveCommon + """
        void main() {
            vec2 p = place(gl_FragCoord.xy);
            float r = length(p);
            float rings = 4.0 + uLow * uAmount * 5.0;
            float edge = sin((r * rings - uT * 0.5) * TAU) * 0.5 + 0.5;
            oColor = paint(clamp(pow(edge, 1.5 + uDepth * 2.0), 0.0, 1.0));
        }
        """;

    private const string VortexFs = ReactiveCommon + """
        void main() {
            vec2 p = place(gl_FragCoord.xy);
            float r = length(p);
            float a = atan(p.y, p.x);
            float twist = a + r * (2.0 + uDepth * 8.0) + uT * 0.25;
            oColor = paint(fract(twist / TAU * uSym * 0.5 + sin(r * 6.0 - uT * 0.4) * 0.15));
        }
        """;

    private const string StarWarpFs = ReactiveCommon + """
        float hash1(float n) { return fract(sin(n * 12.9898) * 43758.5453); }
        void main() {
            vec2 p = place(gl_FragCoord.xy);
            float r = length(p);
            float a = atan(p.y, p.x);
            float spokes = max(uSym, 2.0) * 6.0;
            float lane = fract(a / TAU * spokes);
            float seed = floor(a / TAU * spokes);
            float along = fract(log(max(r, 0.015)) * 0.7 + uT * 0.5 + hash1(seed));
            float streak = along * along * along;
            float across = 1.0 - min(1.0, abs(lane - 0.5) * 6.0);
            oColor = paint(clamp(streak * max(across, 0.0) * (0.55 + r * 1.2), 0.0, 1.0));
        }
        """;

    /// <summary>The vertex and fragment source of a program by name.</summary>
    public static (string Vertex, string Fragment) Source(string name) => name switch
    {
        "present" => (FullscreenVs, PresentFs),
        "blit" => (FullscreenVs, BlitFs),
        "solid" => (FullscreenVs, SolidFs),
        "quad" => (QuadVs, QuadFs),
        "mix" => (FullscreenVs, MixFs),
        "yuv" => (FullscreenVs, YuvFs),
        "stage" => (StageVs, StageFs),
        "output" => (OutputVs, OutputFs),
        "sprite" => (SpriteVs, SpriteFs),
        "lines" => (LinesVs, LinesFs),
        "checker" => (FullscreenVs, CheckerFs),
        "motion" => (FullscreenVs, MotionFs),
        "colorcycle" => (FullscreenVs, ColorCycleFs),
        "syncmarks" => (FullscreenVs, SyncMarksFs),
        "fractal.mandelbrot" => (FullscreenVs, MandelbrotFs),
        "fractal.julia" => (FullscreenVs, JuliaFs),
        "fractal.burningship" => (FullscreenVs, BurningShipFs),
        "fractal.newton" => (FullscreenVs, NewtonFs),
        "fractal.warp" => (FullscreenVs, DomainWarpFs),
        "reactive.plasma" => (FullscreenVs, PlasmaFs),
        "reactive.tunnel" => (FullscreenVs, TunnelFs),
        "reactive.kaleidoscope" => (FullscreenVs, KaleidoscopeFs),
        "reactive.pulse" => (FullscreenVs, PulseFs),
        "reactive.vortex" => (FullscreenVs, VortexFs),
        "reactive.starwarp" => (FullscreenVs, StarWarpFs),
        _ => Added.TryGetValue(name, out var added) ? added : throw new ArgumentException($"No shader named '{name}'.", nameof(name)),
    };

    /// <summary>Every program name — the library's own, then the packages' — for a warm-up that compiles them all before the show.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "present", "blit", "solid", "quad", "mix", "yuv", "stage", "output", "sprite", "lines", "checker", "motion", "colorcycle", "syncmarks",
        "fractal.mandelbrot", "fractal.julia", "fractal.burningship", "fractal.newton", "fractal.warp",
        "reactive.plasma", "reactive.tunnel", "reactive.kaleidoscope", "reactive.pulse", "reactive.vortex", "reactive.starwarp",
        .. Added.Keys.Order(StringComparer.Ordinal),
    ];

    private static FrozenDictionary<string, (string Vertex, string Fragment)> BuildAdded()
    {
        var d = new Dictionary<string, (string Vertex, string Fragment)>(StringComparer.Ordinal);
        AddShow(d); AddOutput(d); AddDecode(d); AddOptimise(d); AddVideoIo(d); AddFx(d); AddContent(d); AddLed(d); AddAlign(d); AddWall(d);
        return d.ToFrozenDictionary(StringComparer.Ordinal);
    }

    // Each package adds its programs in its own file (ShaderLibrary.<Word>.cs); a name the library already has is refused there.
    static partial void AddShow(Dictionary<string, (string Vertex, string Fragment)> programs);
    static partial void AddOutput(Dictionary<string, (string Vertex, string Fragment)> programs);
    static partial void AddDecode(Dictionary<string, (string Vertex, string Fragment)> programs);
    static partial void AddOptimise(Dictionary<string, (string Vertex, string Fragment)> programs);
    static partial void AddVideoIo(Dictionary<string, (string Vertex, string Fragment)> programs);
    static partial void AddFx(Dictionary<string, (string Vertex, string Fragment)> programs);
    static partial void AddContent(Dictionary<string, (string Vertex, string Fragment)> programs);
    static partial void AddLed(Dictionary<string, (string Vertex, string Fragment)> programs);
    static partial void AddAlign(Dictionary<string, (string Vertex, string Fragment)> programs);
    static partial void AddWall(Dictionary<string, (string Vertex, string Fragment)> programs);
}
