namespace PlayMap.Render.Shaders;

/// <summary>wp2-led's programs, by name (sources carry no #version and no precision line). Both are written as
/// <c>PlayMap.Core.Led.LedReference</c> is (the CPU reference a test holds the bytes against): the same order of operations in
/// single precision.</summary>
public static partial class ShaderLibrary
{
    /// <summary>Pass 1, one draw per group over the rows that hold its pixels (1024 a row): each fragment is a pixel. Its attribute
    /// texel holds the sample point (u, v; 0–1, v down) and the footprint's radii; point sampling reads the texel under it (black
    /// outside 0–1), area sampling averages nine bilinear taps over the footprint (a tap outside is black). Each tap is decoded to
    /// linear light with the group's content gamma before averaging.</summary>
    private const string LedSampleFs = """
        uniform sampler2D uAttr;
        uniform sampler2D uTex;
        uniform int uFirst;
        uniform int uCount;
        uniform int uTaps;
        uniform float uGamma;
        uniform float uSpread;
        uniform vec2 uTexSize;
        out vec4 oColor;
        vec3 decode(vec3 c) { return pow(max(c, vec3(0.0)), vec3(uGamma)); }
        void main() {
            ivec2 p = ivec2(gl_FragCoord.xy);
            int i = p.y * 1024 + p.x;
            if (i < uFirst || i >= uFirst + uCount)
                discard;
            vec4 a = texelFetch(uAttr, p, 0);
            vec3 acc = vec3(0.0);
            if (uTaps <= 1) {
                if (a.x >= 0.0 && a.x <= 1.0 && a.y >= 0.0 && a.y <= 1.0) {
                    ivec2 t = min(ivec2(floor(a.xy * uTexSize)), ivec2(uTexSize) - ivec2(1));
                    acc = decode(texelFetch(uTex, t, 0).rgb);
                }
            } else {
                for (int j = -1; j <= 1; j++) {
                    for (int k = -1; k <= 1; k++) {
                        vec2 s = a.xy + vec2(float(k), float(j)) * uSpread * a.zw;
                        if (s.x >= 0.0 && s.x <= 1.0 && s.y >= 0.0 && s.y <= 1.0)
                            acc += decode(texture(uTex, s).rgb);
                    }
                }
                acc = acc / 9.0;
            }
            oColor = vec4(acc, 1.0);
        }
        """;

    /// <summary>Pass 2, one fragment per four DMX slots of a row (written so a BGRA read-back gives them in address order). Per slot
    /// the patch texel says which pixel, channel and byte, and which fixture; blackout zeroes colour and dimmer slots and keeps
    /// constants; the locate blink and a test pattern are code values (no gamma), scaled by the level and the air; the show's
    /// colour is scaled in linear light, white is extracted for fixtures that have it, the gains apply, and the output gamma encodes
    /// it before it is quantised to 8 or 16 bits.</summary>
    private const string LedPatchFs = """
        uniform sampler2D uColors;
        uniform sampler2D uPatch;
        uniform sampler2D uFix;
        uniform sampler2D uAir;
        uniform float uLevel;
        uniform int uTest;
        uniform int uStep;
        uniform int uFixtures;
        out vec4 oColor;

        int imod(int a, int n) { return a - (a / n) * n; }

        vec3 palette(int k) {
            k = imod(k, 6);
            if (k == 0) return vec3(1.0, 0.0, 0.0);
            if (k == 1) return vec3(0.0, 1.0, 0.0);
            if (k == 2) return vec3(0.0, 0.0, 1.0);
            if (k == 3) return vec3(1.0, 1.0, 0.0);
            if (k == 4) return vec3(0.0, 1.0, 1.0);
            return vec3(1.0, 0.0, 1.0);
        }

        vec3 pattern(int kind, int pixel, int pixels, int fixture, int row) {
            if (kind == 1) return pixel == imod(uStep, max(pixels, 10)) ? vec3(1.0) : vec3(0.0);
            if (kind == 2) return pixel == 0 ? vec3(1.0, 0.0, 0.0) : pixel == 1 ? vec3(0.0, 1.0, 0.0) : pixel == 2 ? vec3(0.0, 0.0, 1.0) : vec3(0.0);
            if (kind == 3) return palette(row);
            if (kind == 4) return palette(fixture);
            if (kind == 5) {
                int level = pixel * 255 / max(pixels - 1, 1);
                return vec3(float(level) / 255.0);
            }
            if (kind == 6) return fixture == imod(uStep / 10, max(uFixtures, 1)) ? vec3(1.0) : vec3(0.0);
            if (kind == 7 || kind == 8) return vec3(1.0);
            return vec3(0.0);
        }

        uint quantise(float v, int sel) {
            v = clamp(v, 0.0, 1.0);
            if (sel == 0) return uint(floor(v * 255.0 + 0.5));
            uint q = uint(floor(v * 65535.0 + 0.5));
            return sel == 1 ? (q >> 8) : (q & 255u);
        }

        uint raw(float value, int sel) {
            uint q = uint(clamp(value, 0.0, 65535.0));
            if (sel == 0) return min(q, 255u);
            return sel == 1 ? ((q >> 8) & 255u) : (q & 255u);
        }

        float lum(vec3 c) { return c.r * 0.2126 + c.g * 0.7152 + c.b * 0.0722; }

        vec4 extractWhite(vec3 c) {
            float w = min(c.r, min(c.g, c.b));
            return vec4(c - vec3(w), w);
        }

        float component(vec4 c, float l, int k, bool full) {
            if (k == 0) return c.r;
            if (k == 1) return c.g;
            if (k == 2) return c.b;
            if (k == 3 || k == 4 || k == 5) return c.a;
            if (k == 8) return l;
            if (k == 6 || k == 7) return full ? 1.0 : 0.0;
            return 0.0;
        }

        float gainOf(vec4 g, int k) {
            if (k == 0) return g.r;
            if (k == 1) return g.g;
            if (k == 2) return g.b;
            if (k == 3 || k == 4 || k == 5) return g.a;
            return 1.0;
        }

        uint slot(ivec2 sp) {
            vec4 e = texelFetch(uPatch, sp, 0);
            if (e.y < 0.0) return 0u;
            int op = int(e.y);
            int k = op >> 2;
            int sel = op & 3;
            int fi = int(e.z);
            if (k == 11) return 0u;
            if (k == 10) return raw(e.w, sel);
            vec4 air = texelFetch(uAir, ivec2(0, fi), 0);
            bool black = air.z > 0.5;
            if (k == 9) return black ? 0u : raw(e.w, sel);
            if (black) return 0u;
            vec4 f0 = texelFetch(uFix, ivec2(0, fi), 0);
            vec4 f1 = texelFetch(uFix, ivec2(1, fi), 0);
            vec4 f2 = texelFetch(uFix, ivec2(2, fi), 0);
            vec4 f3 = texelFetch(uFix, ivec2(3, fi), 0);
            int pix = int(e.x);
            bool hasWhite = f3.x > 0.5;
            if (air.w > 0.5 || (uTest != 0 && air.y > 0.5)) {
                bool locate = air.w > 0.5;
                bool full = !locate && uTest == 8;
                vec3 code = locate ? (air.w > 1.5 ? f2.rgb : vec3(1.0)) : pattern(uTest, pix - int(f1.z), int(f1.w), fi, sp.y);
                vec4 c = full ? vec4(1.0) : hasWhite ? extractWhite(code) : vec4(code, 0.0);
                float l = full ? 1.0 : lum(code);
                float v = component(c, l, k, full) * (uLevel * air.x);
                return quantise(v, sel);
            }
            vec3 lin = texelFetch(uColors, ivec2(pix - (pix / 1024) * 1024, pix / 1024), 0).rgb;
            float scale = f1.x * uLevel * pow(air.x, f2.w);
            lin = lin * scale;
            vec4 c = hasWhite ? extractWhite(lin) : vec4(lin, 0.0);
            float v = component(c, lum(lin), k, false) * gainOf(f0, k);
            v = v <= 0.0 ? 0.0 : pow(min(v, 1.0), 1.0 / f1.y);
            return quantise(v, sel);
        }

        void main() {
            ivec2 p = ivec2(gl_FragCoord.xy);
            uint s0 = slot(ivec2(p.x * 4, p.y));
            uint s1 = slot(ivec2(p.x * 4 + 1, p.y));
            uint s2 = slot(ivec2(p.x * 4 + 2, p.y));
            uint s3 = slot(ivec2(p.x * 4 + 3, p.y));
            oColor = vec4(float(s2), float(s1), float(s0), float(s3)) / 255.0;
        }
        """;

    static partial void AddLed(Dictionary<string, (string Vertex, string Fragment)> programs)
    {
        programs.Add("led_sample", (FullscreenVs, LedSampleFs));
        programs.Add("led_patch", (FullscreenVs, LedPatchFs));
    }
}
