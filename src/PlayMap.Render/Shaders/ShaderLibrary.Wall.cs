namespace PlayMap.Render.Shaders;

/// <summary>wp2-wall's programs, by name (sources carry no #version and no precision line).</summary>
public static partial class ShaderLibrary
{
    /// <summary>A pixel quad in a projector's raster: positions in raster pixels (y down, top row first), content coordinates as
    /// (u·q, v·q, q), interpolated linearly across the raster and divided per fragment, so the picture is projectively exact
    /// across the quad's diagonal.</summary>
    private const string PixelSurfaceVs = """
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec3 aStq;
        uniform vec2 uTarget;
        out vec3 vStq;
        void main() {
            vStq = aStq;
            gl_Position = vec4(aPos / uTarget * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>A pixel quad's light: its content through (u, v) with the region's offset and scale (black outside it), times the
    /// surface mask (UV space) and the opacity — the stage program's mode 0, for a quad.</summary>
    private const string PixelSurfaceFs = """
        in vec3 vStq;
        uniform sampler2D uSource;
        uniform vec4 uRegion;
        uniform sampler2D uMask;
        uniform int uHasMask;
        uniform float uOpacity;
        out vec4 oColor;
        void main() {
            vec2 uv = vStq.xy / vStq.z;
            vec2 tc = uRegion.xy + uv * uRegion.zw;
            vec3 c = (any(lessThan(tc, vec2(0.0))) || any(greaterThan(tc, vec2(1.0)))) ? vec3(0.0) : texture(uSource, tc).rgb;
            float m = uHasMask == 1 ? texture(uMask, uv).r : 1.0;
            oColor = vec4(c * m * uOpacity, 1.0);
        }
        """;

    /// <summary>The edit overlay's shapes: triangles in the output's final raster (pixels, y down) with a colour each, blended over
    /// the picture.</summary>
    private const string OverlayShapesVs = """
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec4 aColor;
        uniform vec2 uTarget;
        out vec4 vColor;
        void main() {
            vColor = aColor;
            gl_Position = vec4(aPos / uTarget * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string OverlayShapesFs = """
        in vec4 vColor;
        out vec4 oColor;
        void main() {
            oColor = vColor;
        }
        """;

    static partial void AddWall(Dictionary<string, (string Vertex, string Fragment)> programs)
    {
        programs.Add("pixelsurface", (PixelSurfaceVs, PixelSurfaceFs));
        programs.Add("overlay.shapes", (OverlayShapesVs, OverlayShapesFs));
    }
}
