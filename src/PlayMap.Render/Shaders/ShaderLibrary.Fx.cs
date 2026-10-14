namespace PlayMap.Render.Shaders;

/// <summary>wp2-fx's programs, by name (sources carry no #version and no precision line).</summary>
public static partial class ShaderLibrary
{
    /// <summary>An effect over the picture it was applied to: the effect's result (bottom row first when it comes from ISF,
    /// read the right way up), blended over the input (normal, add, multiply, screen, difference) and mixed by uMix. The
    /// result is top row first, like every PlayMap picture, at the input's size (a scaled effect is upsampled here).</summary>
    private const string FxMixFs = """
        in vec2 vUv;
        uniform sampler2D uInput;
        uniform sampler2D uFx;
        uniform int uFxBottomUp;
        uniform int uBlend;
        uniform float uMix;
        out vec4 oColor;
        void main() {
            vec4 a = texture(uInput, vUv);
            vec4 b = texture(uFx, uFxBottomUp != 0 ? vec2(vUv.x, 1.0 - vUv.y) : vUv);
            vec3 c = b.rgb;
            if (uBlend == 1)
                c = min(a.rgb + b.rgb, vec3(1.0));
            else if (uBlend == 2)
                c = a.rgb * b.rgb;
            else if (uBlend == 3)
                c = vec3(1.0) - (vec3(1.0) - a.rgb) * (vec3(1.0) - b.rgb);
            else if (uBlend == 4)
                c = abs(a.rgb - b.rgb);
            oColor = mix(a, vec4(c, b.a), clamp(uMix, 0.0, 1.0));
        }
        """;

    static partial void AddFx(Dictionary<string, (string Vertex, string Fragment)> programs)
    {
        programs.Add("fxmix", (FullscreenVs, FxMixFs));
    }
}
