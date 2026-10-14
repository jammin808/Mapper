namespace PlayMap.Render.Shaders;

/// <summary>wp2-decode's programs, by name (sources carry no #version and no precision line).</summary>
public static partial class ShaderLibrary
{
    /// <summary>The `hap` pass: one draw per new HAP frame, from its block-compressed planes into an RGBA16F target at the
    /// picture's visible size. Each fragment reads its own texel (texelFetch at gl_FragCoord: no filtering across blocks, and
    /// the padding texels of a coded size past the visible one are never read). Modes: 0 the texel as it is (HAP, HAP Alpha,
    /// HAP R, and any plane read raw); 1 scaled YCoCg → RGB (HAP Q); 2 the same with the BC4 plane as alpha (HAP Q Alpha);
    /// 3 the BC4 plane as grey (HAP Alpha-Only); 4 HAP HDR (BC6H) by its reading — uHdr.x scales linear values to SDR
    /// reference white, uHdr.y is the transfer (0 linear, 1 PQ, 2 HLG, 3 display SDR) — tone-mapped and encoded with the yuv
    /// program's own curve. The YCoCg maths are the HAP specification's reference shader's.</summary>
    internal const string HapFs = """
        uniform sampler2D uTex0;
        uniform sampler2D uTex1;
        uniform int uMode;
        uniform vec2 uHdr;
        out vec4 oColor;

        vec3 hapPqToLinear(vec3 e) {
            const float m1 = 0.1593017578125;
            const float m2 = 78.84375;
            const float c1 = 0.8359375;
            const float c2 = 18.8515625;
            const float c3 = 18.6875;
            vec3 p = pow(max(e, 0.0), vec3(1.0 / m2));
            vec3 l = pow(max(p - c1, 0.0) / (c2 - c3 * p), vec3(1.0 / m1));
            return l * 10000.0;
        }

        vec3 hapHlgToLinear(vec3 e) {
            const float a = 0.17883277;
            const float b = 0.28466892;
            const float c = 0.55991073;
            vec3 lo = e * e / 3.0;
            vec3 hi = (exp((e - c) / a) + b) / 12.0;
            vec3 scene = mix(lo, hi, step(vec3(0.5), e));
            float y = dot(scene, vec3(0.2627, 0.6780, 0.0593));
            return scene * pow(max(y, 1e-6), 0.2) * 1000.0;
        }

        vec3 hapToneMap(vec3 nits) {
            vec3 x = nits / 203.0;
            const float white = 1000.0 / 203.0;
            return x * (1.0 + x / (white * white)) / (1.0 + x);
        }

        void main() {
            ivec2 p = ivec2(gl_FragCoord.xy);
            vec4 t = texelFetch(uTex0, p, 0);
            if (uMode == 1 || uMode == 2) {
                float s = t.b * (255.0 / 8.0) + 1.0;
                float co = (t.r - 0.50196078431373) / s;
                float cg = (t.g - 0.50196078431373) / s;
                float y = t.a;
                vec3 rgb = vec3(y + co - cg, y + cg, y - co - cg);
                float a = uMode == 2 ? texelFetch(uTex1, p, 0).r : 1.0;
                oColor = vec4(clamp(rgb, 0.0, 1.0), a);
            } else if (uMode == 3) {
                oColor = vec4(t.rrr, 1.0);
            } else if (uMode == 4) {
                vec3 e = max(t.rgb, 0.0);
                if (uHdr.y > 2.5) {
                    oColor = vec4(clamp(e, 0.0, 1.0), 1.0);
                } else {
                    vec3 nits = uHdr.y > 1.5 ? hapHlgToLinear(e) : uHdr.y > 0.5 ? hapPqToLinear(e) : e * uHdr.x * 203.0;
                    oColor = vec4(pow(clamp(hapToneMap(nits), 0.0, 1.0), vec3(1.0 / 2.4)), 1.0);
                }
            } else {
                oColor = t;
            }
        }
        """;

    static partial void AddDecode(Dictionary<string, (string Vertex, string Fragment)> programs)
    {
        programs["hap"] = (FullscreenVs, HapFs);
    }
}
