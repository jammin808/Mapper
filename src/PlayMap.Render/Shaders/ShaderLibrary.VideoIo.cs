namespace PlayMap.Render.Shaders;

/// <summary>wp2-videoio's programs, by name (sources carry no #version and no precision line).
///
/// <para><b>video.unpack</b> turns a packed network frame into RGB: UYVY (one RGBA8 texture of w/2 × h, texel = U Y0 V Y1) or P216
/// (an R16 luma texture of w × h and an RG16 chroma texture of w/2 × h), with an optional alpha texture (UYVA's R8, PA16's R16), read
/// BT.709 limited range (codes scaled to their 8-bit equivalents). An even pixel takes its pair's chroma, an odd one the mean of its
/// pair's and the next's, clamped at the right edge — <c>PackReference.Unpack</c> is its CPU twin.</para>
///
/// <para><b>video.pack</b> writes a picture (at the send's size, top row in row 0) into an RGBA8 target whose bytes, read back as RGBA,
/// are the wire format: mode 0 UYVY (target w/2 × h; chroma co-sited with the even pixel, filtered [1,2,1]/4, clamped at both edges);
/// mode 1 UYVA (rows below h carry the 8-bit alpha plane, four bytes a texel, two alpha rows a target row); mode 2 BGRA; mode 3 P216
/// and PA16 (target w/2 × 2h or 3h: rows [0, h) the Y plane, [h, 2h) the UV plane, [2h, 3h) PA16's alpha plane, each texel two
/// 16-bit samples as little-endian byte pairs, so a 16-bit sample is exact in an 8-bit target on every context). Every value is
/// rounded half up to its code before it is written, so the target stores the code exactly — <c>PackReference.Pack</c> is its CPU
/// twin.</para></summary>
public static partial class ShaderLibrary
{
    private const string VideoUnpackFs = """
        uniform sampler2D uLuma;
        uniform sampler2D uChroma;
        uniform sampler2D uAlpha;
        uniform int uMode;
        uniform int uHasAlpha;
        uniform int uPairs;
        uniform float uCode;
        uniform vec2 uMatrix;
        out vec4 oColor;
        void main() {
            ivec2 p = ivec2(gl_FragCoord.xy);
            int i = p.x / 2;
            bool odd = (p.x - 2 * i) == 1;
            int j = min(i + 1, uPairs - 1);
            float y;
            vec2 c;
            if (uMode == 0) {
                vec4 t = texelFetch(uLuma, ivec2(i, p.y), 0);
                y = odd ? t.a : t.g;
                c = t.rb;
                if (odd) {
                    c = (c + texelFetch(uLuma, ivec2(j, p.y), 0).rb) * 0.5;
                }
            } else {
                y = texelFetch(uLuma, p, 0).r;
                c = texelFetch(uChroma, ivec2(i, p.y), 0).rg;
                if (odd) {
                    c = (c + texelFetch(uChroma, ivec2(j, p.y), 0).rg) * 0.5;
                }
            }
            y *= uCode;
            c *= uCode;
            float Y = (y - 16.0) / 219.0;
            vec2 C = (c - 128.0) / 224.0;
            float kr = uMatrix.x;
            float kb = uMatrix.y;
            float kg = 1.0 - kr - kb;
            float r = Y + 2.0 * (1.0 - kr) * C.y;
            float b = Y + 2.0 * (1.0 - kb) * C.x;
            float g = (Y - kr * r - kb * b) / kg;
            float a = uHasAlpha == 1 ? texelFetch(uAlpha, p, 0).r : 1.0;
            oColor = vec4(clamp(vec3(r, g, b), 0.0, 1.0), a);
        }
        """;

    private const string VideoPackFs = """
        uniform sampler2D uPicture;
        uniform int uMode;
        uniform int uWidth;
        uniform int uHeight;
        out vec4 oColor;

        const float Kr = 0.2126;
        const float Kb = 0.0722;
        const float Kg = 1.0 - Kr - Kb;

        vec3 encode(vec3 rgb) {
            float y = Kr * rgb.r + Kg * rgb.g + Kb * rgb.b;
            float cb = (rgb.b - y) / (2.0 * (1.0 - Kb));
            float cr = (rgb.r - y) / (2.0 * (1.0 - Kr));
            return vec3(16.0 + 219.0 * y, 128.0 + 224.0 * cb, 128.0 + 224.0 * cr);
        }

        vec4 at(int x, int row) {
            return texelFetch(uPicture, ivec2(clamp(x, 0, uWidth - 1), row), 0);
        }

        float code(float c) {
            return clamp(floor(c + 0.5), 0.0, 255.0) / 255.0;
        }

        vec2 word(float c) {
            float w = clamp(floor(c + 0.5), 0.0, 65535.0);
            float hi = floor(w / 256.0);
            return vec2(w - hi * 256.0, hi) / 255.0;
        }

        void main() {
            ivec2 p = ivec2(gl_FragCoord.xy);
            if (uMode == 3) {
                int plane = p.y / uHeight;
                int row = p.y - plane * uHeight;
                int x = 2 * p.x;
                if (plane == 0) {
                    oColor = vec4(word(encode(at(x, row).rgb).x * 256.0), word(encode(at(x + 1, row).rgb).x * 256.0));
                } else if (plane == 1) {
                    vec3 e0 = encode(at(x, row).rgb);
                    vec3 e1 = encode(at(x + 1, row).rgb);
                    vec3 eL = encode(at(x - 1, row).rgb);
                    vec2 c = (eL.yz + 2.0 * e0.yz + e1.yz) * 0.25 * 256.0;
                    oColor = vec4(word(c.x), word(c.y));
                } else {
                    oColor = vec4(word(at(x, row).a * 65535.0), word(at(x + 1, row).a * 65535.0));
                }
                return;
            }
            if (uMode == 2) {
                vec4 c = at(p.x, p.y);
                oColor = vec4(code(c.b * 255.0), code(c.g * 255.0), code(c.r * 255.0), code(c.a * 255.0));
                return;
            }
            if (uMode == 1 && p.y >= uHeight) {
                int band = p.y - uHeight;
                vec4 a = vec4(0.0);
                for (int k = 0; k < 4; k++) {
                    int byteAt = 4 * p.x + k;
                    int row = 2 * band + byteAt / uWidth;
                    int x = byteAt - (byteAt / uWidth) * uWidth;
                    a[k] = row < uHeight ? code(at(x, row).a * 255.0) : 0.0;
                }
                oColor = a;
                return;
            }
            int i = p.x;
            vec3 e0 = encode(at(2 * i, p.y).rgb);
            vec3 e1 = encode(at(2 * i + 1, p.y).rgb);
            vec3 eL = encode(at(2 * i - 1, p.y).rgb);
            vec2 c = (eL.yz + 2.0 * e0.yz + e1.yz) * 0.25;
            oColor = vec4(code(c.x), code(e0.x), code(c.y), code(e1.x));
        }
        """;

    static partial void AddVideoIo(Dictionary<string, (string Vertex, string Fragment)> programs)
    {
        programs.Add("video.unpack", (FullscreenVs, VideoUnpackFs));
        programs.Add("video.pack", (FullscreenVs, VideoPackFs));
    }
}
