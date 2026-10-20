using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlayMap.Core.Gpu;
using PlayMap.Core.Gpu.Programs;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>Each program's compiled HLSL is its <see cref="ProgramLayout"/> in the render hardware interface's program table, on both
/// jobs (DXC's Linux build signs): the table and the HLSL library hold the same programs, a program either lacks failing by name;
/// every entry that binds the block at b0 reflects exactly the table's constants — name, HLSL type, offset as HLSL packs them, array
/// length, a matrix row_major — and the block's size, and nothing is bound but that block and the binding block at b1; the program
/// reads its textures by the table's slots (the SLOT_ defines it is compiled with), sampling the ones the table says are sampled and
/// only loading the rest; and its vertex entry's input signature is the table's inputs as Direct3D 12's input layout reads them
/// (ATTR&lt;location&gt;, a float format of the input's float count, in an input layout whose buffer 0 steps per vertex and buffer 1
/// per instance), beside SV_VertexID and SV_InstanceID alone.</summary>
public sealed partial class ShaderLayoutTests
{
    /// <summary>D3D_NAME's system values a vertex entry may take: SV_VertexID and SV_InstanceID.</summary>
    private const int VertexId = 6;
    private const int InstanceId = 8;

    private static readonly IReadOnlyDictionary<string, HlslCompile.Compiled> Compiled =
        HlslCompile.Programs.ToDictionary(c => c.Program.Name, StringComparer.Ordinal);

    [Fact]
    public void TheTableAndTheHlslLibraryHoldTheSamePrograms()
    {
        var table = ProgramTable.Product.Select(l => l.Name).ToList();
        var hlsl = HlslLibrary.Programs().Select(p => p.Name).ToList();
        var problems = Missing(table, hlsl).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Equal(table, HlslCompile.Programs.Select(c => c.Program.Name));
    }

    [Fact]
    public void AProgramEitherSideLacksIsNamed()
    {
        Assert.Equal(["the table lists extra, which the HLSL library lacks", "the HLSL library has solo, which the table does not list"],
            Missing(["blit", "extra"], ["solo", "blit"]));
    }

    [Fact]
    public void EachProgramsBlockReflectsTheTablesConstantsAndSize()
    {
        var problems = new List<string>();
        var matrices = 0;
        foreach (var layout in ProgramTable.Product)
        {
            if (!Compiled.TryGetValue(layout.Name, out var c))
            {
                problems.Add($"{layout.Name}: the HLSL library has no such program");
                continue;
            }
            var want = layout.Constants.Select(k => (k.Name, ConstantTypes.Word(k.Type), k.Offset, k.Count)).ToList();
            var bound = 0;
            foreach (var (stage, s) in new[] { ("vs", c.Vs), ("ps", c.Ps) })
            {
                if (s.Reflection is not { } r)
                {
                    problems.Add($"{layout.Name} {stage}: {s.Words}");
                    continue;
                }
                foreach (var b in r.Bindings)
                {
                    var allowed = b.Kind == "cbuffer" && ((b.Name == "Program" && b.Register == 0) || (b.Name == "Bindings" && b.Register == 1));
                    if (!allowed || b.Space != 0)
                        problems.Add($"{layout.Name} {stage}: binds {b.Kind} {b.Name} at {b.Register} (space {b.Space})");
                }
                if (layout.Constants.IsEmpty)
                {
                    if (r.Buffers.Any(b => b.Name == "Program") || layout.ConstantBytes != 0)
                        problems.Add($"{layout.Name} {stage}: the table gives it no constants, yet a block of {layout.ConstantBytes} bytes is declared");
                    continue;
                }
                if (r.Bindings.All(b => b.Name != "Program"))
                    continue;
                bound++;
                var block = r.Constants.Where(k => k.Buffer == "Program").ToList();
                var reflected = block.Select(k => (k.Name, k.Type, k.Offset, k.Count)).ToList();
                if (!reflected.SequenceEqual(want))
                    problems.Add($"{layout.Name} {stage}: reflects [{string.Join(", ", reflected)}], the table says [{string.Join(", ", want)}]");
                var size = r.Buffers.Where(b => b.Name == "Program").Select(b => b.Size).ToList();
                if (!size.SequenceEqual([layout.ConstantBytes]))
                    problems.Add($"{layout.Name} {stage}: its block is [{string.Join(", ", size)}] bytes, the table says {layout.ConstantBytes}");
                foreach (var k in layout.Constants.Where(k => ConstantTypes.IsMatrix(k.Type)))
                {
                    if (block.FirstOrDefault(m => m.Name == k.Name) is { Name: not null } found && !found.RowMajor)
                        problems.Add($"{layout.Name} {stage}: {k.Name} is laid one column a register; the table's rows are one register each");
                    matrices++;
                }
            }
            if (bound == 0 && !layout.Constants.IsEmpty)
                problems.Add($"{layout.Name}: no entry reads its constants, so the table's cannot be checked");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(matrices > 0, "no matrix constant was checked for row_major");
    }

    [Fact]
    public void EachProgramReadsTheTablesTexturesBySlotSampledAsTheTableSays()
    {
        var problems = new List<string>();
        var common = Code(File.ReadAllText(Path.Combine(HlslLibrary.Folder, "Common.hlsli")));
        var entries = HlslLibrary.Programs().ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var layout in ProgramTable.Product)
        {
            if (!entries.TryGetValue(layout.Name, out var p))
            {
                problems.Add($"{layout.Name}: the HLSL library has no such program");
                continue;
            }
            // The slots the program is compiled with: its header's SLOT_ defines, the table's names and slots in the table's order.
            var header = Encoding.UTF8.GetString(HlslCompile.Resource($"shaders/gen/{layout.Name}/Layout.hlsli") ?? []);
            var defined = SlotDefine().Matches(header).Select(m => (m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
            var want = layout.Textures.Select(t => (t.Name, t.Slot)).ToList();
            if (!defined.SequenceEqual(want))
                problems.Add($"{layout.Name}: is compiled with slots [{string.Join(", ", defined)}], the table says [{string.Join(", ", want)}]");

            var block = Code(HlslLibrary.BlockOf(p));
            foreach (Match literal in LiteralSlot().Matches(block))
                problems.Add($"{layout.Name}: reads a texture by a number ({literal.Value.Trim()}), not by its SLOT_ define");
            var sampling = SamplingFunctions(common + "\n" + block);
            var used = SlotUse().Matches(block).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (var t in layout.Textures)
            {
                if (!used.Contains(t.Name))
                    problems.Add($"{layout.Name}: reads no SLOT_{t.Name}");
                var sampled = sampling.Any(f => Regex.IsMatch(block, $@"\b{f}\s*\(\s*SLOT_{Regex.Escape(t.Name)}\b"));
                if (sampled != t.Sampled)
                    problems.Add($"{layout.Name}: {t.Name} is {(sampled ? "sampled" : "only loaded")}, the table says {(t.Sampled ? "sampled" : "loaded")}");
            }
            foreach (var extra in used.Where(u => layout.Textures.All(t => t.Name != u)))
                problems.Add($"{layout.Name}: reads SLOT_{extra}, which is not its texture");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void EachVertexEntryTakesTheTablesInputsAsDirect3DsInputLayoutReadsThem()
    {
        var problems = new List<string>();
        var withInputs = 0;
        foreach (var layout in ProgramTable.Product)
        {
            if (!Compiled.TryGetValue(layout.Name, out var c))
            {
                problems.Add($"{layout.Name}: the HLSL library has no such program");
                continue;
            }
            if (c.Vs.Reflection is not { } r)
            {
                problems.Add($"{layout.Name} vs: {c.Vs.Words}");
                continue;
            }
            problems.AddRange(InputLayoutProblems(layout));
            var elements = layout.Inputs.Select(Element).ToList();
            var want = elements.Select(e => ("ATTR", e.Location, "float", e.Components)).ToList();
            var taken = r.Inputs.Where(i => i.SystemValue == 0).Select(i => (i.Semantic, i.Index, i.Type, i.Components)).ToList();
            if (!taken.SequenceEqual(want))
                problems.Add($"{layout.Name}: {c.Program.Vs} takes [{string.Join(", ", taken)}]; the table's input layout is [{string.Join(", ", elements)}]");
            foreach (var system in r.Inputs.Where(i => i.SystemValue != 0 && i.SystemValue is not (VertexId or InstanceId)))
                problems.Add($"{layout.Name}: {c.Program.Vs} takes {system.Semantic}, which no input layout or draw gives");
            if (want.Count > 0)
                withInputs++;
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Equal(6, withInputs);
    }

    [Fact]
    public void AnInputLayoutDirect3DWouldRefuseIsNamed()
    {
        static ProgramLayout With(params VertexAttribute[] inputs) => new("t", [], [], 0) { Inputs = [.. inputs] };
        static VertexAttribute A(int location, int components, int stride, int offset, int buffer = 0, int divisor = 0) =>
            new(location, components, stride, offset, buffer, divisor);
        Assert.Empty(InputLayoutProblems(With(A(0, 3, 32, 0), A(1, 3, 32, 12), A(2, 2, 32, 24))));
        Assert.Empty(InputLayoutProblems(With(A(0, 4, 32, 0, 1, 1), A(1, 4, 32, 16, 1, 1))));
        Assert.Equal(["t: ATTR0 has stride 32, another input of buffer 0 has 28", "t: ATTR1 has stride 28, another input of buffer 0 has 32"],
            InputLayoutProblems(With(A(0, 3, 32, 0), A(1, 4, 28, 12))));
        Assert.Equal(["t: ATTR1 (bytes 8 to 20) overlaps ATTR0 (bytes 0 to 12)"],
            InputLayoutProblems(With(A(0, 3, 32, 0), A(1, 3, 32, 8))));
        Assert.Equal(["t: ATTR0 sits at 2, not on a 4-byte boundary"], InputLayoutProblems(With(A(0, 2, 16, 2))));
        Assert.Equal(["t: ATTR1 ends at 36, past its stride of 32"], InputLayoutProblems(With(A(0, 3, 32, 0), A(1, 4, 32, 20))));
        Assert.Equal(["t: ATTR0 is in buffer 0 with divisor 1; buffer 0 steps per vertex and buffer 1 per instance"],
            InputLayoutProblems(With(A(0, 4, 16, 0, 0, 1))));
        Assert.Equal(["t: ATTR0 is in buffer 1 with divisor 0; buffer 0 steps per vertex and buffer 1 per instance"],
            InputLayoutProblems(With(A(0, 4, 16, 0, 1, 0))));
        Assert.Equal(["t: ATTR0 has 5 floats, which no float format holds"], InputLayoutProblems(With(A(0, 5, 32, 0))));
        Assert.Equal(["t: its inputs are not in location order, one each"], InputLayoutProblems(With(A(1, 2, 16, 8), A(0, 2, 16, 0))));
    }

    /// <summary>The names one side holds that the other lacks, the table's first.</summary>
    private static IEnumerable<string> Missing(IReadOnlyList<string> table, IReadOnlyList<string> hlsl) =>
        table.Where(n => !hlsl.Contains(n)).Select(n => $"the table lists {n}, which the HLSL library lacks")
            .Concat(hlsl.Where(n => !table.Contains(n)).Select(n => $"the HLSL library has {n}, which the table does not list"));

    /// <summary>A Direct3D 12 input element as a pipeline's input layout is made from one of the table's inputs: semantic ATTR with
    /// the location as its index, the float format of its float count, its buffer as the input slot at its offset, stepping per
    /// instance at its divisor or per vertex.</summary>
    private readonly record struct InputElement(int Location, int Components, string Format, int Slot, int Offset, bool PerInstance, int StepRate)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"ATTR{Location} {Format} slot {Slot} @{Offset}")
            + (PerInstance ? " per instance /" + StepRate.ToString(CultureInfo.InvariantCulture) : " per vertex");
    }

    private static InputElement Element(VertexAttribute a) => new(a.Location, a.Components, a.Components switch
    {
        1 => "R32_FLOAT",
        2 => "R32G32_FLOAT",
        3 => "R32G32B32_FLOAT",
        4 => "R32G32B32A32_FLOAT",
        _ => "?",
    }, a.Buffer, a.OffsetBytes, a.Divisor > 0, a.Divisor);

    /// <summary>What Direct3D 12 would refuse, or the backends would read apart, in the input layout made from a program's inputs:
    /// each in location order once, a float format for its count, one stride per buffer, 4-byte aligned inside that stride without
    /// overlap, buffer 0 stepping per vertex and buffer 1 per instance.</summary>
    private static List<string> InputLayoutProblems(ProgramLayout layout)
    {
        var problems = new List<string>();
        var inputs = layout.Inputs;
        if (!inputs.Select(a => a.Location).SequenceEqual(inputs.Select(a => a.Location).Distinct().Order()))
            problems.Add($"{layout.Name}: its inputs are not in location order, one each");
        foreach (var a in inputs)
        {
            var bytes = 4 * a.Components;
            if (Element(a).Format == "?")
                problems.Add($"{layout.Name}: ATTR{a.Location} has {a.Components} floats, which no float format holds");
            if (inputs.FirstOrDefault(b => b.Buffer == a.Buffer && b.StrideBytes != a.StrideBytes) is { Components: > 0 } other)
                problems.Add($"{layout.Name}: ATTR{a.Location} has stride {a.StrideBytes}, another input of buffer {a.Buffer} has {other.StrideBytes}");
            if (a.OffsetBytes % 4 != 0)
                problems.Add($"{layout.Name}: ATTR{a.Location} sits at {a.OffsetBytes}, not on a 4-byte boundary");
            else if (a.OffsetBytes + bytes > a.StrideBytes)
                problems.Add($"{layout.Name}: ATTR{a.Location} ends at {a.OffsetBytes + bytes}, past its stride of {a.StrideBytes}");
            foreach (var b in inputs.Where(b => b.Buffer == a.Buffer && b.Location < a.Location))
            {
                if (a.OffsetBytes < b.OffsetBytes + 4 * b.Components && b.OffsetBytes < a.OffsetBytes + bytes)
                    problems.Add($"{layout.Name}: ATTR{a.Location} (bytes {a.OffsetBytes} to {a.OffsetBytes + bytes}) overlaps ATTR{b.Location} (bytes {b.OffsetBytes} to {b.OffsetBytes + 4 * b.Components})");
            }
            if (a.Buffer is not (0 or 1) || (a.Buffer == 1) != (a.Divisor > 0))
                problems.Add($"{layout.Name}: ATTR{a.Location} is in buffer {a.Buffer} with divisor {a.Divisor}; buffer 0 steps per vertex and buffer 1 per instance");
        }
        return problems;
    }

    /// <summary>The functions whose first argument is a slot they sample through: PmSmp (a slot's sampler), and every function of the
    /// code that hands its first uint parameter to one of them as its first argument.</summary>
    private static HashSet<string> SamplingFunctions(string code)
    {
        var sampling = new HashSet<string>(StringComparer.Ordinal) { "PmSmp" };
        var functions = SlotFunction().Matches(code).Select(m => (Name: m.Groups["name"].Value, Slot: m.Groups["slot"].Value, At: m.Index + m.Length)).ToList();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (name, slot, at) in functions)
            {
                var body = code[at..Close(code, at - 1)];
                if (!sampling.Contains(name) && sampling.Any(f => Regex.IsMatch(body, $@"\b{f}\s*\(\s*{Regex.Escape(slot)}\b")))
                    grew = sampling.Add(name);
            }
        }
        return sampling;
    }

    /// <summary>The index of the brace that closes the one at <paramref name="open"/>.</summary>
    private static int Close(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return i;
        }
        throw new InvalidOperationException($"the brace at {open} never closes");
    }

    /// <summary>HLSL with its comments taken out.</summary>
    private static string Code(string hlsl) => Comments().Replace(hlsl, " ");

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"SLOT_(\w+)")]
    private static partial Regex SlotUse();

    [GeneratedRegex(@"^#define SLOT_(\w+) (\d+)$", RegexOptions.Multiline)]
    private static partial Regex SlotDefine();

    [GeneratedRegex(@"\bPm(Tex|TexU|Smp|Sample|Load|Size)\s*\(\s*\d")]
    private static partial Regex LiteralSlot();

    [GeneratedRegex(@"\b\w+(<\w+>)?\s+(?<name>\w+)\s*\(\s*uint\s+(?<slot>\w+)\s*(,[^)]*)?\)\s*\{")]
    private static partial Regex SlotFunction();
}
