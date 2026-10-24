namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>The ISF files the Vulkan target is held to beyond the built-ins, one per feature it translates: multi-pass with sizes,
/// persistent float buffers, every input type, bias sampling, v1's names and PERSISTENT_BUFFERS, IMG_SIZE, a #version line and a
/// precision line in the body, sound inputs, an import, a vertex file that passes a varying to its fragment file, and a generator.</summary>
internal static class IsfCorpus
{
    public static readonly (string Name, string Fs, string? Vs)[] Samples =
    [
        ("passes", """
            /*{ "INPUTS": [ { "NAME": "inputImage", "TYPE": "image" }, { "NAME": "amount", "TYPE": "float", "DEFAULT": 0.5 } ],
                "PASSES": [ { "TARGET": "small", "WIDTH": "floor($WIDTH/4)", "HEIGHT": "$HEIGHT/4" }, { "TARGET": "acc", "PERSISTENT": true, "FLOAT": true }, {} ] }*/
            void main() {
                if (PASSINDEX == 0) gl_FragColor = IMG_THIS_NORM_PIXEL(inputImage);
                else if (PASSINDEX == 1) gl_FragColor = mix(IMG_NORM_PIXEL(acc, isf_FragNormCoord), IMG_NORM_THIS_PIXEL(small), amount);
                else gl_FragColor = IMG_PIXEL(acc, gl_FragCoord.xy) * vec4(IMG_SIZE(small) / RENDERSIZE, 1.0, 1.0);
            }
            """, null),
        ("inputs", """
            /*{ "INPUTS": [ { "NAME": "inputImage", "TYPE": "image" }, { "NAME": "on", "TYPE": "bool", "DEFAULT": true },
                { "NAME": "kick", "TYPE": "event" }, { "NAME": "mode", "TYPE": "long", "VALUES": [0, 1], "LABELS": ["A", "B"] },
                { "NAME": "pos", "TYPE": "point2D", "DEFAULT": [0.5, 0.5] }, { "NAME": "tint", "TYPE": "color", "DEFAULT": [1, 0, 0, 1] },
                { "NAME": "matte", "TYPE": "image" }, { "NAME": "wave", "TYPE": "audio", "MAX": 32 }, { "NAME": "fft", "TYPE": "audioFFT" } ],
                "IMPORTED": { "grain": { "PATH": "grain.png" } } }*/
            #version 120
            precision mediump float;
            void main() {
                vec4 c = IMG_NORM_PIXEL(inputImage, isf_FragNormCoord, 0.5) + IMG_PIXEL(matte, gl_FragCoord.xy, 1.0);
                float s = IMG_NORM_PIXEL(wave, vec2(pos.x, 0.0)).r + IMG_NORM_PIXEL(fft, pos).r + IMG_THIS_PIXEL(grain).g;
                gl_FragColor = (on && !kick && mode == 1) ? tint * s : c * vec4(TIME, TIMEDELTA, float(FRAMEINDEX), DATE.w);
            }
            """, null),
        ("v1", """
            /*{ "INPUTS": [ { "NAME": "inputImage", "TYPE": "image" } ], "PERSISTENT_BUFFERS": [ "trail" ],
                "PASSES": [ { "TARGET": "trail" }, {} ] }*/
            varying vec2 shifted;
            void main() {
                vec4 now = texture2D(inputImage, vv_FragNormCoord);
                gl_FragColor = PASSINDEX == 0 ? max(now, IMG_NORM_PIXEL(trail, shifted) * 0.9) : IMG_THIS_NORM_PIXEL(trail);
            }
            """, """
            /*{ }*/
            varying vec2 shifted;
            void main() {
                vv_vertShaderInit();
                shifted = isf_FragNormCoord + vec2(0.01, 0.0);
            }
            """),
        ("generator", """
            /*{ "INPUTS": [ { "NAME": "speed", "TYPE": "float", "DEFAULT": 1.0, "MIN": 0.0, "MAX": 4.0 } ] }*/
            void main() {
                vec2 p = gl_FragCoord.xy / RENDERSIZE;
                gl_FragColor = vec4(p, 0.5 + 0.5 * sin(TIME * speed), 1.0);
            }
            """, null),
    ];
}
