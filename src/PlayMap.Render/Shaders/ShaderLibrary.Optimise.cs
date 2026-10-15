namespace PlayMap.Render.Shaders;

/// <summary>wp2-optimise's programs, by name (sources carry no #version and no precision line).</summary>
public static partial class ShaderLibrary
{
    /// <summary>The HAP block encoders: one fragment per 4×4 block of the picture in <c>uSrc</c> (straight RGBA, <c>uSize</c> its
    /// size), each writing its block's bytes as four little-endian words into an RGBA32UI target of ⌈w/4⌉ × ⌈h/4⌉ texels (the
    /// 8-byte blocks use the first two words). Texels past the picture's edge repeat the edge. <c>uRefine</c> is the number of
    /// least-squares passes; at 2 and above each colour endpoint is also nudged one step either way per channel and kept when the
    /// block's error falls.
    /// <para>Colour (BC1, and the colour half of BC3): the block's principal axis by power iteration, the extremes along it as
    /// endpoints, then for each pass the 5:6:5 endpoints, the nearest of the four palette entries per texel, and the endpoints that
    /// minimise the squared error for those indices; the best is kept, endpoints ordered so the four-colour palette is used.
    /// Alpha (BC4, and the alpha half of BC3): the eight-entry palette from the extremes refined by least squares, against the
    /// six-entry palette with exact 0 and 255 over the inner values; the lower error wins. Scaled YCoCg (HAP Q): Y in the alpha
    /// block; Co and Cg scaled by 1, 2 or 4 (the largest that keeps the block's chroma in range) in red and green, the scale in
    /// blue as 0, 1 or 3 of 5 bits — the reading the hap pass's mode 1 inverts. The error weights chroma as RGB does: 2 for Co,
    /// 3 for Cg.</para></summary>
    internal const string HapEncodeFs = """
        uniform sampler2D uSrc;
        uniform ivec2 uSize;
        uniform int uRefine;
        out uvec4 oBlock;

        vec3 gC[16];
        float gA[16];

        vec3 expand565(uvec3 q) {
            return vec3(float((q.x << 3u) | (q.x >> 2u)), float((q.y << 2u) | (q.y >> 4u)), float((q.z << 3u) | (q.z >> 2u)));
        }

        uvec3 quant565(vec3 c) {
            vec3 m = vec3(31.0, 63.0, 31.0);
            return uvec3(clamp(floor(c * m / 255.0 + 0.5), vec3(0.0), m));
        }

        uint pack565(uvec3 q) { return (q.x << 11u) | (q.y << 5u) | q.z; }

        float indexColours(vec3 e0, vec3 e1, vec3 w, out uint bits) {
            vec3 p2 = (2.0 * e0 + e1) / 3.0;
            vec3 p3 = (e0 + 2.0 * e1) / 3.0;
            float err = 0.0;
            bits = 0u;
            for (int i = 0; i < 16; i++) {
                vec3 c = gC[i];
                vec3 d0 = c - e0; vec3 d1 = c - e1; vec3 d2 = c - p2; vec3 d3 = c - p3;
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
        bool fitColours(uint bits, out vec3 e0, out vec3 e1) {
            float aa = 0.0; float ab = 0.0; float bb = 0.0;
            vec3 ax = vec3(0.0); vec3 bx = vec3(0.0);
            for (int i = 0; i < 16; i++) {
                uint k = (bits >> uint(2 * i)) & 3u;
                float a = k == 0u ? 1.0 : k == 1u ? 0.0 : k == 2u ? 2.0 / 3.0 : 1.0 / 3.0;
                float b = 1.0 - a;
                aa += a * a; ab += a * b; bb += b * b;
                ax += a * gC[i]; bx += b * gC[i];
            }
            float det = aa * bb - ab * ab;
            if (abs(det) < 1e-6) { e0 = vec3(0.0); e1 = vec3(0.0); return false; }
            e0 = clamp((bb * ax - ab * bx) / det, 0.0, 255.0);
            e1 = clamp((aa * bx - ab * ax) / det, 0.0, 255.0);
            return true;
        }

        uvec2 encodeColour(vec3 w, int fixB) {
            vec3 mean = vec3(0.0);
            for (int i = 0; i < 16; i++) mean += gC[i];
            mean /= 16.0;
            float xx = 0.0; float xy = 0.0; float xz = 0.0; float yy = 0.0; float yz = 0.0; float zz = 0.0;
            vec3 lo = vec3(255.0); vec3 hi = vec3(0.0);
            for (int i = 0; i < 16; i++) {
                vec3 d = gC[i] - mean;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z; yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
                lo = min(lo, gC[i]); hi = max(hi, gC[i]);
            }
            mat3 cov = mat3(xx, xy, xz, xy, yy, yz, xz, yz, zz);
            vec3 axis = hi - lo;
            if (dot(axis, axis) < 1e-6) axis = vec3(1.0);
            for (int k = 0; k < 4; k++) {
                vec3 n = cov * axis;
                float l = length(n);
                if (l < 1e-6) break;
                axis = n / l;
            }
            axis = normalize(axis);
            float tmin = 1e9; float tmax = -1e9;
            for (int i = 0; i < 16; i++) {
                float t = dot(gC[i] - mean, axis);
                tmin = min(tmin, t); tmax = max(tmax, t);
            }
            vec3 e0 = clamp(mean + axis * tmax, 0.0, 255.0);
            vec3 e1 = clamp(mean + axis * tmin, 0.0, 255.0);
            uvec3 b0 = uvec3(0u); uvec3 b1 = uvec3(0u); uint bestBits = 0u; float bestErr = 1e30;
            for (int pass = 0; pass <= uRefine; pass++) {
                uvec3 q0 = quant565(e0); uvec3 q1 = quant565(e1);
                if (fixB >= 0) { q0.z = uint(fixB); q1.z = uint(fixB); }
                uint bits;
                float err = indexColours(expand565(q0), expand565(q1), w, bits);
                if (err < bestErr) { bestErr = err; b0 = q0; b1 = q1; bestBits = bits; }
                if (pass == uRefine || !fitColours(bits, e0, e1)) break;
            }
            if (uRefine >= 2) {
                uvec3 top = uvec3(31u, 63u, 31u);
                for (int c = 0; c < 6; c++) {
                    int ch = c % 3;
                    if (ch == 2 && fixB >= 0) continue;
                    for (int s = -1; s <= 1; s += 2) {
                        uvec3 q0 = b0; uvec3 q1 = b1;
                        int v = int(c < 3 ? q0[ch] : q1[ch]) + s;
                        if (v < 0 || v > int(top[ch])) continue;
                        if (c < 3) q0[ch] = uint(v); else q1[ch] = uint(v);
                        uint bits;
                        float err = indexColours(expand565(q0), expand565(q1), w, bits);
                        if (err < bestErr) { bestErr = err; b0 = q0; b1 = q1; bestBits = bits; }
                    }
                }
            }
            uint c0 = pack565(b0); uint c1 = pack565(b1);
            if (c0 < c1) { uint t = c0; c0 = c1; c1 = t; bestBits ^= 0x55555555u; }
            else if (c0 == c1) bestBits = 0u;
            return uvec2(c0 | (c1 << 16u), bestBits);
        }

        float alphaAt(float a0, float a1, uint k, bool eight) {
            if (k == 0u) return a0;
            if (k == 1u) return a1;
            if (eight) return (float(8u - k) * a0 + float(k - 1u) * a1) / 7.0;
            if (k == 6u) return 0.0;
            if (k == 7u) return 255.0;
            return (float(6u - k) * a0 + float(k - 1u) * a1) / 5.0;
        }

        float indexAlpha(float a0, float a1, bool eight, out uint lo, out uint hi) {
            float err = 0.0; lo = 0u; hi = 0u;
            for (int i = 0; i < 16; i++) {
                uint best = 0u; float be = 1e30;
                for (uint k = 0u; k < 8u; k++) {
                    float d = gA[i] - alphaAt(a0, a1, k, eight);
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

        uint alphaIndex(uint lo, uint hi, int i) {
            int pos = 3 * i;
            if (pos + 3 <= 32) return (lo >> uint(pos)) & 7u;
            if (pos >= 32) return (hi >> uint(pos - 32)) & 7u;
            return ((lo >> uint(pos)) | (hi << uint(32 - pos))) & 7u;
        }

        uvec2 packAlpha(float a0, float a1, uint lo, uint hi) {
            uint e = uint(a0) | (uint(a1) << 8u);
            return uvec2(e | (lo << 16u), (lo >> 16u) | (hi << 16u));
        }

        uvec2 encodeAlpha() {
            float lo = 255.0; float hi = 0.0;
            for (int i = 0; i < 16; i++) { lo = min(lo, gA[i]); hi = max(hi, gA[i]); }
            float a0 = floor(hi + 0.5); float a1 = floor(lo + 0.5);
            if (a0 <= a1) return uvec2(uint(a0) | (uint(a0) << 8u), 0u);
            float bestA0 = a0; float bestA1 = a1; uint bestLo; uint bestHi;
            float bestErr = indexAlpha(a0, a1, true, bestLo, bestHi);
            uint ilo = bestLo; uint ihi = bestHi;
            for (int pass = 0; pass < uRefine; pass++) {
                float aa = 0.0; float ab = 0.0; float bb = 0.0; float ax = 0.0; float bx = 0.0;
                for (int i = 0; i < 16; i++) {
                    uint k = alphaIndex(ilo, ihi, i);
                    float s = k == 0u ? 0.0 : k == 1u ? 1.0 : float(k - 1u) / 7.0;
                    float a = 1.0 - s;
                    aa += a * a; ab += a * s; bb += s * s; ax += a * gA[i]; bx += s * gA[i];
                }
                float det = aa * bb - ab * ab;
                if (abs(det) < 1e-6) break;
                float n0 = floor(clamp((bb * ax - ab * bx) / det, 0.0, 255.0) + 0.5);
                float n1 = floor(clamp((aa * bx - ab * ax) / det, 0.0, 255.0) + 0.5);
                if (n0 < n1) { float t = n0; n0 = n1; n1 = t; }
                if (n0 == n1) { if (n0 < 255.0) n0 += 1.0; else n1 -= 1.0; }
                float err = indexAlpha(n0, n1, true, ilo, ihi);
                if (err < bestErr) { bestErr = err; bestA0 = n0; bestA1 = n1; bestLo = ilo; bestHi = ihi; }
            }
            // The six-entry palette, with exact 0 and 255, over the values between them.
            float ilo6 = 255.0; float ihi6 = 0.0;
            for (int i = 0; i < 16; i++) {
                if (gA[i] > 4.0 && gA[i] < 251.0) { ilo6 = min(ilo6, gA[i]); ihi6 = max(ihi6, gA[i]); }
            }
            if (ilo6 > ihi6) { ilo6 = 0.0; ihi6 = 0.0; }
            float s0 = floor(ilo6 + 0.5); float s1 = floor(ihi6 + 0.5);
            uint l6; uint h6;
            float err6 = indexAlpha(s0, s1, false, l6, h6);
            if (err6 < bestErr) return packAlpha(s0, s1, l6, h6);
            return packAlpha(bestA0, bestA1, bestLo, bestHi);
        }

        void main() {
            ivec2 b = ivec2(gl_FragCoord.xy);
            float maxChroma = 0.0;
            for (int i = 0; i < 16; i++) {
                ivec2 q = min(b * 4 + ivec2(i & 3, i >> 2), uSize - ivec2(1));
                vec4 t = texelFetch(uSrc, q, 0);
                if (MODE == 2) {
                    float co = (t.r - t.b) * 0.5;
                    float cg = (2.0 * t.g - t.r - t.b) * 0.25;
                    gC[i] = vec3(co, cg, 0.0);
                    gA[i] = (t.r + 2.0 * t.g + t.b) * 0.25 * 255.0;
                    maxChroma = max(maxChroma, max(abs(co), abs(cg)));
                } else {
                    gC[i] = t.rgb * 255.0;
                    gA[i] = t.a * 255.0;
                }
            }
            if (MODE == 0) {
                oBlock = uvec4(encodeColour(vec3(1.0), -1), 0u, 0u);
            } else if (MODE == 1) {
                oBlock = uvec4(encodeAlpha(), encodeColour(vec3(1.0), -1));
            } else if (MODE == 2) {
                float s = maxChroma <= 0.1245 ? 4.0 : maxChroma <= 0.249 ? 2.0 : 1.0;
                int code = s == 4.0 ? 3 : s == 2.0 ? 1 : 0;
                for (int i = 0; i < 16; i++) gC[i] = vec3(clamp(gC[i].xy * s * 255.0 + 128.0, 0.0, 255.0), 0.0);
                oBlock = uvec4(encodeAlpha(), encodeColour(vec3(2.0, 3.0, 0.0), code));
            } else {
                oBlock = uvec4(encodeAlpha(), 0u, 0u);
            }
        }
        """;

    static partial void AddOptimise(Dictionary<string, (string Vertex, string Fragment)> programs)
    {
        programs["hapenc.bc1"] = (FullscreenVs, "const int MODE = 0;\n" + HapEncodeFs);
        programs["hapenc.bc3"] = (FullscreenVs, "const int MODE = 1;\n" + HapEncodeFs);
        programs["hapenc.ycocg"] = (FullscreenVs, "const int MODE = 2;\n" + HapEncodeFs);
        programs["hapenc.bc4"] = (FullscreenVs, "const int MODE = 3;\n" + HapEncodeFs);
    }
}
