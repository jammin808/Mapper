using System.Text.RegularExpressions;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>Each program's DXC reflection equals its layout in the program table (until wp3-rhi lands the table, the one the GLSL
/// declarations give, in the same shape): every entry that binds the block at b0 reflects exactly the table's constants — name, type,
/// offset as HLSL packs them, array length — and the program reads its textures by the table's slots, sampling the ones the table
/// says are sampled and only loading the rest; the binding block is at b1 and nothing else is bound.</summary>
public sealed partial class ShaderLayoutTests
{
    [Fact]
    public void EachProgramsReflectionIsItsTablesConstants()
    {
        var problems = new List<string>();
        foreach (var c in HlslCompile.Programs)
        {
            var table = GlslLayout.Of(c.Program.Name).Layout;
            var bound = 0;
            foreach (var (stage, s) in new[] { ("vs", c.Vs), ("ps", c.Ps) })
            {
                Assert.True(s.Reflection is not null, $"{c.Program.Name} {stage}: {s.Words}");
                var r = s.Reflection!;
                foreach (var b in r.Bindings)
                {
                    var allowed = b.Kind == "cbuffer" && ((b.Name == "Program" && b.Register == 0) || (b.Name == "Bindings" && b.Register == 1));
                    if (!allowed || b.Space != 0)
                        problems.Add($"{c.Program.Name} {stage}: binds {b.Kind} {b.Name} at {b.Register} (space {b.Space})");
                }
                if (r.Bindings.All(b => b.Name != "Program"))
                    continue;
                bound++;
                var reflected = r.Constants.Where(k => k.Buffer == "Program").Select(k => (k.Name, k.Type, k.Offset, k.Count)).ToList();
                var want = table.Constants.Select(k => (k.Name, k.Type, k.Offset, k.Count)).ToList();
                if (!reflected.SequenceEqual(want))
                    problems.Add($"{c.Program.Name} {stage}: reflects [{string.Join(", ", reflected)}], the table says [{string.Join(", ", want)}]");
            }
            if (bound == 0 && !table.Constants.IsEmpty)
                problems.Add($"{c.Program.Name}: no entry reads its constants, so the table's cannot be checked");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void EachProgramReadsItsTexturesByTheTablesSlotsSampledAsTheTableSays()
    {
        var problems = new List<string>();
        foreach (var p in HlslLibrary.Programs())
        {
            var block = HlslLibrary.BlockOf(p);
            var table = GlslLayout.Of(p.Name).Layout;
            var used = SlotUse().Matches(block).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (var t in table.Textures)
            {
                if (!used.Contains(t.Name))
                    problems.Add($"{p.Name}: reads no SLOT_{t.Name}");
                var sampled = Regex.IsMatch(block, @"(PmSample\(SLOT_|PmSmp\(SLOT_)" + Regex.Escape(t.Name) + @"\b");
                if (sampled != t.Sampled)
                    problems.Add($"{p.Name}: {t.Name} is {(sampled ? "sampled" : "only loaded")}, the table says {(t.Sampled ? "sampled" : "loaded")}");
            }
            foreach (var extra in used.Where(u => table.Textures.All(t => t.Name != u)))
                problems.Add($"{p.Name}: reads SLOT_{extra}, which is not its texture");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [GeneratedRegex(@"SLOT_(\w+)")]
    private static partial Regex SlotUse();
}
