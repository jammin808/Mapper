using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PlayMap.Core.Effects;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>What the road made of a source: each stage in order (a stage after a failed one not run, with the words why), the
/// stage that failed ("done" when none did) with its words and ISF line, and — when every stage passed — each entry's validated
/// DXIL and reflection (vertex, then pixel) and the effect's layout.</summary>
public sealed record RoadResult(ImmutableArray<FxStage> Stages, string Stage, string Words, int IsfLine, ImmutableArray<byte[]> Dxil,
    ImmutableArray<byte[]> Reflection, ShaderLayout Layout)
{
    public bool Ok => Stage == ShaderRoad.Done;
}

/// <summary>The natives the road's stages call. Each is null when it could not be loaded; its stage then fails with the load's
/// words, so a node missing a native still answers every stage before it.</summary>
public sealed record RoadNatives(Glslang? Glslang, SpirvValidator? Validator, SpirvCross? Cross, Dxc? Dxc, string GlslangWords,
    string ValidatorWords, string CrossWords, string DxcWords) : IDisposable
{
    /// <summary>Every native from its pinned build.</summary>
    public static RoadNatives Load(string? baseDirectory = null)
    {
        var glslang = Glslang.TryLoad(out var g, baseDirectory);
        var validator = SpirvValidator.TryLoad(out var v, baseDirectory);
        var cross = SpirvCross.TryLoad(out var c, baseDirectory);
        var dxc = Dxc.TryLoad(out var d, baseDirectory);
        return new RoadNatives(glslang, validator, cross, dxc, g, v, c, d);
    }

    /// <summary>Each native's name and whether it loaded, with the words when not.</summary>
    public IEnumerable<(string Name, bool Ok, string Words)> Loaded =>
    [
        ("glslang", Glslang is not null, GlslangWords),
        ("spirv-val", Validator is not null, ValidatorWords),
        ("spirv-cross", Cross is not null, CrossWords),
        ("dxc", Dxc is not null, DxcWords),
    ];

    public void Dispose()
    {
        Glslang?.Dispose();
        Validator?.Dispose();
        Dxc?.Dispose();
    }
}

/// <summary>A user's ISF effect to DXIL, one stage after another, each failure in its stage's words with the line mapped back to the
/// ISF file: translate (Core's Vulkan target), glslang (relaxed rules), validate (SPIRV-Tools), cross (SPIRV-Cross to HLSL, Shader
/// Model 6.0, the registers remapped) and dxc (validated, signed DXIL and its reflection). A multi-pass file is one program (its
/// passes differ by PASSINDEX), so the road makes one vertex and one pixel entry.</summary>
public sealed partial class ShaderRoad(RoadNatives natives)
{
    public const string Translate = "translate";
    public const string GlslangStage = "glslang";
    public const string Validate = "validate";
    public const string Cross = "cross";
    public const string DxcStage = "dxc";
    public const string Done = "done";

    /// <summary>The stages in the road's order.</summary>
    public static readonly ImmutableArray<string> StageNames = [Translate, GlslangStage, Validate, Cross, DxcStage];

    /// <summary>DXC's arguments for a user effect's entries: HLSL 2021, a PDB, reflection kept apart from the DXIL.</summary>
    public static readonly ImmutableArray<string> DxcArguments = ["-HV", "2021", "-Zi", "-Qstrip_debug", "-Qstrip_reflect"];

    /// <summary>The name a stage's source carries in its compiler's messages.</summary>
    private const string SourceName = "effect";

    public RoadResult Run(string fragment, string? vertex)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var stages = ImmutableArray.CreateBuilder<FxStage>();
        RoadResult Fail(string stage, string words, int line)
        {
            stages.Add(new FxStage(stage, false, words, line));
            foreach (var later in StageNames.SkipWhile(s => s != stage).Skip(1))
                stages.Add(new FxStage(later, null, $"not run: the {stage} stage failed", 0));
            return new RoadResult(stages.ToImmutable(), stage, words, line, [], [], ShaderLayout.Empty);
        }

        // 1. translate
        var doc = IsfParser.Parse(fragment);
        if (!doc.Valid)
            return Fail(Translate, doc.Issues[0].Words, doc.Issues[0].Line);
        var source = IsfTranslator.TranslateVulkan(doc, vertex);
        if (!source.Issues.IsEmpty)
            return Fail(Translate, source.Issues[0].Words, source.Issues[0].Line);
        stages.Add(new FxStage(Translate, true, $"GLSL 4.50 for Vulkan, {doc.Images.Length} image{(doc.Images.Length == 1 ? "" : "s")}, {Math.Max(1, doc.Passes.Length)} pass{(doc.Passes.Length > 1 ? "es" : "")}", 0));

        // 2. glslang
        if (natives.Glslang is not { } glslang)
            return Fail(GlslangStage, natives.GlslangWords, 0);
        var vs = glslang.Compile(source.Vertex, vertex: true, SourceName + ".vs");
        if (!vs.Ok)
            return Fail(GlslangStage, Issue(source.LineMap.ReadGlslang(vs.Log, vertex: true), out var vsLine), vsLine);
        var fs = glslang.Compile(source.Fragment, vertex: false, SourceName + ".fs");
        if (!fs.Ok)
            return Fail(GlslangStage, Issue(source.LineMap.ReadGlslang(fs.Log, vertex: false), out var fsLine), fsLine);
        stages.Add(new FxStage(GlslangStage, true, $"SPIR-V, {vs.Words.Length + fs.Words.Length} words", 0));

        // 3. validate
        if (natives.Validator is not { } validator)
            return Fail(Validate, natives.ValidatorWords, 0);
        if (validator.Validate(vs.Words) is { } vsInvalid)
            return Fail(Validate, "vertex shader: " + vsInvalid, 0);
        if (validator.Validate(fs.Words) is { } fsInvalid)
            return Fail(Validate, fsInvalid, 0);
        stages.Add(new FxStage(Validate, true, "valid SPIR-V for Vulkan 1.0", 0));

        // 4. cross
        if (natives.Cross is not { } cross)
            return Fail(Cross, natives.CrossWords, 0);
        var vsHlsl = cross.ToHlsl(vs.Words);
        if (!vsHlsl.Ok)
            return Fail(Cross, "vertex shader: " + vsHlsl.Words, 0);
        var fsHlsl = cross.ToHlsl(fs.Words);
        if (!fsHlsl.Ok)
            return Fail(Cross, fsHlsl.Words, 0);
        var layout = Merge(fsHlsl.Layout, vsHlsl.Layout, out var mismatch);
        if (mismatch is not null)
            return Fail(Cross, mismatch, 0);
        stages.Add(new FxStage(Cross, true, $"HLSL for Shader Model 6.0, {layout.Constants.Length} constants, {layout.Textures.Length} images", 0));

        // 5. dxc
        if (natives.Dxc is not { } dxc)
            return Fail(DxcStage, natives.DxcWords, 0);
        var vsDxil = dxc.Compile(vsHlsl.Hlsl, "main", "vs_6_0", DxcArguments, SourceName + ".vs.hlsl");
        if (!vsDxil.Ok)
            return Fail(DxcStage, Issue(DxcIssues(vsDxil.Words, source.LineMap, vertex: true), out var vLine), vLine);
        var fsDxil = dxc.Compile(fsHlsl.Hlsl, "main", "ps_6_0", DxcArguments, SourceName + ".fs.hlsl");
        if (!fsDxil.Ok)
            return Fail(DxcStage, Issue(DxcIssues(fsDxil.Words, source.LineMap, vertex: false), out var fLine), fLine);
        if (!vsDxil.Signed || !fsDxil.Signed)
            return Fail(DxcStage, "the DXIL was validated but not signed", 0);
        stages.Add(new FxStage(DxcStage, true, $"validated, signed DXIL ({vsDxil.Dxil.Length + fsDxil.Dxil.Length} bytes)", 0));
        return new RoadResult(stages.ToImmutable(), Done, "", 0, [vsDxil.Dxil, fsDxil.Dxil], [vsDxil.Reflection, fsDxil.Reflection], layout);
    }

    private static string Issue(ImmutableArray<IsfIssue> issues, out int line)
    {
        var first = issues.IsDefaultOrEmpty ? new IsfIssue(0, 0, "the compiler stopped without words") : issues[0];
        line = first.Line;
        return first.Line > 0 ? $"line {first.Line}: {first.Words}" : first.Words;
    }

    /// <summary>The effect's layout: the pixel entry's, with every constant the vertex entry reads at the same offset (a stage lays
    /// its uniforms out from the same declarations, so they agree unless the file declares different uniforms per stage).</summary>
    private static ShaderLayout Merge(ShaderLayout pixel, ShaderLayout vertex, out string? mismatch)
    {
        mismatch = null;
        var constants = pixel.Constants.ToList();
        foreach (var c in vertex.Constants)
        {
            var same = constants.FirstOrDefault(p => p.Name == c.Name);
            if (same.Name is null)
                constants.Add(c);
            else if (same != c)
                mismatch = $"the vertex and fragment shaders lay {c.Name} out differently";
        }
        var textures = pixel.Textures.ToList();
        foreach (var t in vertex.Textures.Where(t => textures.All(p => p.Slot != t.Slot)))
            textures.Add(t);
        constants.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        textures.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        return new ShaderLayout([.. constants], [.. textures], Math.Max(pixel.ConstantBytes, vertex.ConstantBytes));
    }

    /// <summary>DXC's errors as issues: a line SPIRV-Cross carried from the ISF body (its <c>#line</c> names source string 1) maps
    /// back to the file; any other is the generated HLSL's.</summary>
    private static ImmutableArray<IsfIssue> DxcIssues(string log, IsfLineMap map, bool vertex)
    {
        var list = ImmutableArray.CreateBuilder<IsfIssue>();
        var first = vertex ? map.VertexBodyLine : map.FragmentBodyLine;
        var stage = vertex ? "vertex shader: " : "";
        foreach (var raw in log.Split('\n'))
        {
            if (DxcLine().Match(raw.Trim()) is not { Success: true } m)
                continue;
            var n = int.Parse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            list.Add(m.Groups[1].Value.Trim('"') == "1"
                ? new IsfIssue(first + n - 1, 0, stage + m.Groups[3].Value.Trim())
                : new IsfIssue(0, 0, stage + "in the HLSL SPIRV-Cross wrote: " + m.Groups[3].Value.Trim()));
        }
        if (list.Count == 0)
            list.Add(new IsfIssue(0, 0, stage + log.Trim()));
        return list.ToImmutable();
    }

    [GeneratedRegex(@"^(.+?):(\d+):\d+:\s*error:\s*(.*)$")]
    private static partial Regex DxcLine();
}
