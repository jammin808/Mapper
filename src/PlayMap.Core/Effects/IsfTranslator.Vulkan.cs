using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayMap.Core.Effects;

/// <summary>The bindings the Vulkan target gives an ISF file's resources (set 0): the block of ISF's built-ins, then the images in
/// the order the file declares them; the default uniform block that glslang's relaxed rules gather the inputs and each image's
/// size, rect and flag into takes a binding of glslang's choosing. The shader worker remaps them to user effects' registers (the
/// default block b0, the built-ins b1, image i at t i, every image sampled through the one sampler at s0).</summary>
public static class IsfVulkanBindings
{
    /// <summary>The block of ISF's built-ins: PASSINDEX, RENDERSIZE, TIME, TIMEDELTA, DATE, FRAMEINDEX.</summary>
    public const int Builtins = 1;

    /// <summary>The first image's binding; image i is at <see cref="FirstImage"/> + i.</summary>
    public const int FirstImage = 2;

    /// <summary>The most images a user effect binds (its texture table holds sixteen).</summary>
    public const int MaxImages = 16;

    /// <summary>The name of the built-ins' block.</summary>
    public const string BuiltinsBlock = "IsfBuiltins";
}

public static partial class IsfTranslator
{
    private const string VulkanFragmentPrelude =
        "#version 450\nlayout(location = 0) out vec4 isf_FragColor;\n#define gl_FragColor isf_FragColor\n#define varying in\n#define texture2D texture\n#define texture2DRect texture\n";

    private const string VulkanVertexPrelude =
        "#version 450\n#define varying out\n#define attribute in\n#define texture2D texture\n#define texture2DRect texture\n";

    /// <summary>ISF's built-ins as one block (std140: the order keeps every member at its natural offset).</summary>
    private const string VulkanBuiltins =
        "layout(std140, set = 0, binding = 1) uniform IsfBuiltins { int PASSINDEX; vec2 RENDERSIZE; float TIME; float TIMEDELTA; vec4 DATE; int FRAMEINDEX; };\n";

    /// <summary>An ISF file as GLSL 4.50 for Vulkan's rules relaxed (glslang <c>-R</c>, the shader worker's first native stage): a
    /// <c>#version 450</c> line, an explicit location on the colour it writes (each stage's varyings take glslang's locations, and the
    /// shader worker's cross stage matches them by name), images at set 0 and fixed bindings
    /// (<see cref="IsfVulkanBindings"/>), the inputs and each image's size, rect and flag as loose uniforms (the relaxed rules gather
    /// them into the default uniform block), the built-ins as a block. The bodies are rewritten as for the GL dialects and follow
    /// <c>#line 1 1</c>, so a compiler's line in source string 1 is the body's line and <see cref="IsfLineMap.ReadGlslang"/> maps it
    /// back to the file. A file with more images than a user effect binds carries the issue.</summary>
    public static IsfProgramSource TranslateVulkan(IsfDocument doc, string? vertexSource)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var issues = ImmutableArray.CreateBuilder<IsfIssue>();
        issues.AddRange(doc.Issues);
        if (doc.Images.Length > IsfVulkanBindings.MaxImages)
            issues.Add(new IsfIssue(0, 0, $"the file reads {doc.Images.Length} images; a user effect binds at most {IsfVulkanBindings.MaxImages}"));
        var images = doc.Images.ToHashSet(StringComparer.Ordinal);
        var body = Rewrite(doc.Body, doc.BodyLine, images, vertex: false, issues);
        var (vsBody, vsLine) = vertexSource is null ? ((string?)null, 1) : StripHeader(vertexSource);
        var vsRewritten = vsBody is null ? null : Rewrite(vsBody, vsLine, images, vertex: true, issues);

        var fs = new StringBuilder(VulkanFragmentPrelude.Length + body.Length + 1024);
        fs.Append(VulkanFragmentPrelude);
        VulkanDeclarations(fs, doc, vertex: false);
        fs.Append("#line 1 1\n").Append(body);
        if (!body.EndsWith('\n'))
            fs.Append('\n');

        var vs = new StringBuilder(2048);
        vs.Append(VulkanVertexPrelude);
        VulkanDeclarations(vs, doc, vertex: true);
        vs.Append(VertexInit);
        if (vsRewritten is null)
        {
            vs.Append("void main(void) { isf_vertShaderInit(); }\n");
        }
        else
        {
            vs.Append("#line 1 1\n").Append(vsRewritten);
            if (!vsRewritten.EndsWith('\n'))
                vs.Append('\n');
        }
        return new IsfProgramSource(vs.ToString(), fs.ToString(), issues.ToImmutable(), new IsfLineMap(doc.BodyLine, vsLine));
    }

    private static void VulkanDeclarations(StringBuilder sb, IsfDocument doc, bool vertex)
    {
        sb.Append(VulkanBuiltins);
        // Each stage's varyings take glslang's locations in that stage's own declaration order; the shader worker's cross stage moves
        // each fragment input to the location of the vertex output of its name, as GL links varyings by name.
        sb.Append(vertex ? "out vec2 isf_FragNormCoord;\n" : "in vec2 isf_FragNormCoord;\n");
        foreach (var input in doc.Inputs)
            if (GlslType(input.Type) is { } t)
                sb.Append("uniform ").Append(t).Append(' ').Append(input.Name).Append(";\n");
        for (var i = 0; i < doc.Images.Length; i++)
        {
            var img = doc.Images[i];
            sb.Append("layout(set = 0, binding = ").Append((IsfVulkanBindings.FirstImage + i).ToString(CultureInfo.InvariantCulture))
                .Append(") uniform sampler2D ").Append(img).Append(";\nuniform vec2 _").Append(img).Append("_imgSize;\nuniform vec4 _")
                .Append(img).Append("_imgRect;\nuniform bool _").Append(img).Append("_flip;\n");
        }
        // As for the GL dialects: every picture is a normalised 2D texture, and the flag says it is stored top row first.
        sb.Append("vec4 isf_sample(sampler2D s, bool flip, vec2 uv) { return texture(s, flip ? vec2(uv.x, 1.0 - uv.y) : uv); }\n");
        if (!vertex)
            sb.Append("vec4 isf_sample(sampler2D s, bool flip, vec2 uv, float bias) { return texture(s, flip ? vec2(uv.x, 1.0 - uv.y) : uv, bias); }\n");
    }
}

public sealed partial record IsfLineMap
{
    /// <summary>glslang's log (as shaderc words it: "name:line: error: words", or glslang's own "ERROR: string:line: words") as
    /// issues at the file's lines, errors only. A line in source string 1 is the body's; anything else is PlayMap's prelude.</summary>
    public ImmutableArray<IsfIssue> ReadGlslang(string? log, bool vertex)
    {
        if (string.IsNullOrWhiteSpace(log))
            return [];
        var list = ImmutableArray.CreateBuilder<IsfIssue>();
        var first = vertex ? VertexBodyLine : FragmentBodyLine;
        var stage = vertex ? "vertex shader: " : "";
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.Trim();
            string source, words;
            int n;
            if (ShadercLine().Match(line) is { Success: true } s)
            {
                if (!s.Groups[3].Value.Equals("error", StringComparison.OrdinalIgnoreCase))
                    continue;
                (source, n, words) = (s.Groups[1].Value, Number(s.Groups[2].Value), s.Groups[4].Value);
            }
            else if (GlslangLine().Match(line) is { Success: true } g)
            {
                (source, n, words) = (g.Groups[1].Value, Number(g.Groups[2].Value), g.Groups[3].Value);
            }
            else
            {
                continue;
            }
            words = stage + words.Trim();
            list.Add(source == "1" ? new IsfIssue(first + n - 1, 0, words) : new IsfIssue(0, 0, "in PlayMap's prelude: " + words));
        }
        if (list.Count == 0)
            list.Add(new IsfIssue(0, 0, stage + FirstLine(log)));
        return list.ToImmutable();
    }

    private static int Number(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    [GeneratedRegex(@"^(\S+?):(\d+):\s*(error|warning)\s*:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShadercLine();

    [GeneratedRegex(@"^ERROR:\s*(\S+?):(\d+):\s*(.*)$")]
    private static partial Regex GlslangLine();
}
