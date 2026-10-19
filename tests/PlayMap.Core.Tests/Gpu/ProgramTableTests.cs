using System.Collections.Immutable;
using PlayMap.Core.Gpu;
using PlayMap.Core.Gpu.Programs;
using PlayMap.Core.Storage;
using PlayMap.Core.Tests.Model;
using PlayMap.Tests;

namespace PlayMap.Core.Tests.Gpu;

/// <summary>Program layouts pack as an HLSL cbuffer does (16-byte registers, matrices and array elements each starting one, what
/// follows packing into the space after), look up by name without allocating, and the program table reads and writes its file byte
/// for byte, holds the embedded product programs packed by those rules under distinct names, and numbers user programs past them,
/// never reusing an id.</summary>
public sealed class ProgramTableTests
{
    private static ProgramLayout Pack(params (string, ConstantType, int)[] constants) =>
        ProgramLayout.Pack("p", constants, []);

    private static int Offset(ProgramLayout layout, string name) =>
        layout.TryConstant(name, out var slot) ? slot.Offset : throw new KeyNotFoundException(name);

    [Fact]
    public void ScalarsAndVectorsPackIntoRegistersWithoutCrossingOne()
    {
        var l = Pack(("a", ConstantType.Float, 1), ("b", ConstantType.Float2, 1), ("c", ConstantType.Float3, 1),
            ("d", ConstantType.Float, 1), ("e", ConstantType.Float4, 1), ("f", ConstantType.Int, 1), ("g", ConstantType.Int2, 1),
            ("h", ConstantType.Int4, 1));
        Assert.Equal(0, Offset(l, "a"));
        Assert.Equal(4, Offset(l, "b"));   // 4 + 8 stays inside register 0
        Assert.Equal(16, Offset(l, "c"));  // 12 + 12 would cross it
        Assert.Equal(28, Offset(l, "d"));  // the w after c
        Assert.Equal(32, Offset(l, "e"));
        Assert.Equal(48, Offset(l, "f"));
        Assert.Equal(52, Offset(l, "g"));
        Assert.Equal(64, Offset(l, "h"));
        Assert.Equal(80, l.ConstantBytes);
    }

    [Fact]
    public void MatricesAndArrayElementsStartRegistersAndWhatFollowsPacksAfterTheLastRow()
    {
        var l = Pack(("a", ConstantType.Float, 1), ("m", ConstantType.Float4x4, 1), ("b", ConstantType.Float, 1),
            ("n", ConstantType.Float3x3, 1), ("x", ConstantType.Float, 1), ("u", ConstantType.Float3, 16), ("y", ConstantType.Float, 1));
        Assert.Equal(0, Offset(l, "a"));
        Assert.Equal(16, Offset(l, "m"));
        Assert.Equal(80, Offset(l, "b"));
        Assert.Equal(96, Offset(l, "n"));
        Assert.Equal(140, Offset(l, "x"));            // 96 + 44: the w of the matrix's third row
        Assert.Equal(144, Offset(l, "u"));
        Assert.Equal(144 + 15 * 16 + 12, Offset(l, "y")); // the w of the array's last element
        Assert.Equal(400, l.ConstantBytes);
        Assert.Equal(44, ConstantTypes.Bytes(ConstantType.Float3x3));
        Assert.Equal(252, ConstantTypes.Bytes(ConstantType.Float3, 16));
    }

    [Fact]
    public void AnArrayAnswersByItsNameAndByEachElementsName()
    {
        var l = Pack(("t", ConstantType.Float, 1), ("u", ConstantType.Float3, 16));
        Assert.True(l.TryConstant("u", out var whole));
        Assert.Equal(new ConstantSlot("u", ConstantType.Float3, 16, 16), whole);
        Assert.True(l.TryConstant("u[3]", out var third));
        Assert.Equal(new ConstantSlot("u", ConstantType.Float3, 16 + 3 * 16), third);
        Assert.True(l.TryConstant("u[15]", out var last));
        Assert.Equal(16 + 15 * 16, last.Offset);
        Assert.False(l.TryConstant("u[16]", out _));
        Assert.False(l.TryConstant("v", out _));
    }

    [Fact]
    public void TexturesTakeSlotsInTheOrderGivenAndANameTwiceOrASeventeenthTextureIsRefused()
    {
        var l = ProgramLayout.Pack("p", [("uMix", ConstantType.Float, 1)], [("uFrom", true), ("uTo", true), ("uLut", false)]);
        Assert.Equal(new[] { new TextureSlot("uFrom", 0, true), new TextureSlot("uTo", 1, true), new TextureSlot("uLut", 2, false) }, l.Textures);
        Assert.True(l.TryTexture("uLut", out var lut));
        Assert.Equal(2, lut.Slot);
        Assert.False(l.TryTexture("uMix", out _));
        Assert.Throws<ArgumentException>(() => ProgramLayout.Pack("p", [("a", ConstantType.Float, 1), ("a", ConstantType.Int, 1)], []));
        Assert.Throws<ArgumentException>(() => ProgramLayout.Pack("p", [("a", ConstantType.Float, 1)], [("a", true)]));
        var seventeen = Enumerable.Range(0, 17).Select(i => ("t" + i, true)).ToList();
        Assert.Throws<ArgumentException>(() => ProgramLayout.Pack("p", [], seventeen));
    }

    [Fact]
    public void LayoutsAreEqualByWhatTheyHold()
    {
        var a = ProgramLayout.Pack("p", [("x", ConstantType.Float4, 1)], [("t", true)]);
        var b = ProgramLayout.Pack("p", [("x", ConstantType.Float4, 1)], [("t", true)]);
        var c = ProgramLayout.Pack("p", [("x", ConstantType.Float4, 1)], [("t", false)]);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        // The vertex inputs are part of what a layout holds.
        var meshed = a with { Inputs = [new VertexAttribute(0, 3, 32, 0), new VertexAttribute(1, 2, 32, 12)] };
        Assert.NotEqual(a, meshed);
        Assert.Equal(meshed, b with { Inputs = [new VertexAttribute(0, 3, 32, 0), new VertexAttribute(1, 2, 32, 12)] });
        Assert.NotEqual(meshed, b with { Inputs = [new VertexAttribute(0, 3, 32, 0), new VertexAttribute(1, 2, 32, 12, Buffer: 1, Divisor: 1)] });
        Assert.Empty(a.Inputs);
    }

    [Fact]
    public void ALookUpAllocatesNothing()
    {
        var l = Pack(("uAir", ConstantType.Float, 1), ("uColors", ConstantType.Float3, 16));
        var table = new ProgramTable([l]);
        _ = l.TryConstant("uColors[7]", out _);
        _ = table.Layout(new ProgramId(0));
        var start = Allocations.Start();
        var found = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (l.TryConstant("uAir", out _) && l.TryConstant("uColors[7]", out _) && !l.TryTexture("uAir", out _))
                found++;
            if (table.Layout(new ProgramId(0)).ConstantBytes > 0)
                found++;
        }
        Assert.Equal(0, Allocations.Since(start));
        Assert.Equal(2000, found);
    }

    [Fact]
    public void TheTablesFileReadsBackToTheSameLayoutsAndWritesTheSameBytes()
    {
        ImmutableArray<ProgramLayout> layouts =
        [
            ProgramLayout.Pack("quad", [("uRect", ConstantType.Float4, 1), ("uMode", ConstantType.Int, 1), ("uM", ConstantType.Float4x4, 1)],
                [("uTex", true)]),
            ProgramLayout.Pack("sprite", [("uSize", ConstantType.Float2, 1)], [("uTex", true)]) with
            {
                Inputs = [new VertexAttribute(0, 4, 32, 0, Buffer: 1, Divisor: 1), new VertexAttribute(1, 4, 32, 16, Buffer: 1, Divisor: 1)],
            },
            ProgramLayout.Pack("solid", [("uColor", ConstantType.Float4, 1)], []),
            ProgramLayout.Pack("present", [], [("uTex", true)]),
        ];
        var text = ProgramTable.Write(layouts);
        var read = ProgramTable.Read(text);
        Assert.Equal<ProgramLayout>(layouts, read);
        Assert.Equal(text, ProgramTable.Write(read));
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void AFileThatNamesAProgramTwiceOrATypeWithNoWordIsRefused()
    {
        var twice = ProgramTable.Write([ProgramLayout.Pack("a", [], []), ProgramLayout.Pack("a", [], [])]);
        Assert.Throws<FormatException>(() => ProgramTable.Read(twice));
        var unknown = ProgramTable.Write([ProgramLayout.Pack("a", [("x", ConstantType.Float, 1)], [])]).Replace("\"float\"", "\"half\"", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => ProgramTable.Read(unknown));
        Assert.Throws<FormatException>(() => ProgramTable.Read("{}"));
        Assert.Throws<ArgumentException>(() => new ProgramTable([ProgramLayout.Pack("a", [], []), ProgramLayout.Pack("a", [], [])]));
    }

    [Fact]
    public void ProductProgramsAnswerByNameAndUserProgramsTakeIdsPastThemNeverReused()
    {
        var table = new ProgramTable([ProgramLayout.Pack("blit", [], [("uTex", true)]), ProgramLayout.Pack("solid", [("uColor", ConstantType.Float4, 1)], [])]);
        Assert.Equal(new[] { "blit", "solid" }, table.Names);
        Assert.Equal(new ProgramId(1), table.Id("solid"));
        Assert.Equal("solid", table.Layout(table.Id("solid")).Name);
        var unknown = Assert.Throws<KeyNotFoundException>(() => table.Id("nothing"));
        Assert.Contains("nothing", unknown.Message, StringComparison.Ordinal);
        Assert.False(table.TryId("nothing", out _));

        var user = ProgramLayout.Pack("fx.blur.0", [("uAmount", ConstantType.Float, 1)], [("inputImage", true)]);
        var first = table.AddUser(user);
        Assert.Equal(new ProgramId(2), first);
        Assert.True(table.IsUser(first));
        Assert.False(table.IsUser(new ProgramId(1)));
        Assert.Same(user, table.Layout(first));
        Assert.True(table.ReleaseUser(first));
        Assert.False(table.ReleaseUser(first));
        Assert.False(table.ReleaseUser(new ProgramId(0)));
        Assert.Throws<ArgumentException>(() => table.Layout(first));
        var second = table.AddUser(user);
        Assert.Equal(new ProgramId(3), second);
        Assert.Throws<ArgumentException>(() => table.Layout(new ProgramId(9)));
        Assert.Throws<ArgumentException>(() => table.Layout(new ProgramId(-1)));
    }

    [Fact]
    public void TheEmbeddedTableHoldsDistinctProgramsEachPackedByTheBlocksRules()
    {
        var product = ProgramTable.Product;
        Assert.NotEmpty(product);
        Assert.Equal(product.Length, product.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var p in product)
        {
            var repacked = ProgramLayout.Pack(p.Name, p.Constants.Select(c => (c.Name, c.Type, c.Count)), p.Textures.Select(t => (t.Name, t.Sampled)))
                with { Inputs = p.Inputs };
            Assert.Equal(repacked, p);
            // Vertex inputs in location order, each in one of the two buffers, the instance buffer's stepping per instance.
            Assert.Equal(p.Inputs.OrderBy(a => a.Location), p.Inputs);
            Assert.All(p.Inputs, a => Assert.True(a.Buffer is 0 or 1 && a.Components is >= 1 and <= 4 && a.OffsetBytes + 4 * a.Components <= a.StrideBytes
                && a.Divisor == a.Buffer, $"{p.Name}: {a}"));
            Assert.Equal(0, p.ConstantBytes % 16);
        }
        var table = new ProgramTable();
        Assert.Equal(product.Select(p => p.Name), table.Names);
        Assert.Equal(product.Length, table.ProductCount);
    }

    /// <summary>The generated ids (<c>src/PlayMap.Core/Gpu/Programs/ProgramIds.g.cs</c>) are the table's own: one class per product
    /// program, its id the program's place in the table, each constant's id its place in the layout, each texture's its slot.
    /// PLAYMAP_WRITE_FIXTURES=1 writes the file from the table; it changes only with it.</summary>
    [Fact]
    public void TheGeneratedIdsAreTheTablesOwnByteForByte()
    {
        var path = Path.Combine(RoundTwoSample.RepoRoot(), "src", "PlayMap.Core", "Gpu", "Programs", "ProgramIds.g.cs");
        var expected = IdsSource(ProgramTable.Product);
        if (Environment.GetEnvironmentVariable("PLAYMAP_WRITE_FIXTURES") == "1")
            DurableFile.WriteAllText(path, expected);
        Assert.True(File.Exists(path), "the ids are generated: run this test once with PLAYMAP_WRITE_FIXTURES=1");
        Assert.Equal(expected, DurableFile.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void EveryIdNamesItsProgramsSlotOfItsName()
    {
        var table = new ProgramTable();
        var classes = typeof(ProgramIds).GetNestedTypes();
        Assert.Equal(table.ProductCount, classes.Length);
        foreach (var c in classes)
        {
            var program = (ProgramId)c.GetField("Program")!.GetValue(null)!;
            var layout = table.Layout(program);
            Assert.Equal(ClassName(layout.Name), c.Name);
            Assert.Equal(program, table.Id(layout.Name));
            var constants = c.GetFields().Where(f => f.FieldType == typeof(ConstantId)).Select(f => (ConstantId)f.GetValue(null)!).ToList();
            var textures = c.GetFields().Where(f => f.FieldType == typeof(TextureId)).Select(f => (TextureId)f.GetValue(null)!).ToList();
            Assert.Equal(layout.Constants.Select((k, i) => new ConstantId(program, i, k.Name)), constants);
            Assert.Equal(layout.Textures.Select(t => new TextureId(program, t.Slot, t.Name)), textures);
        }
    }

    /// <summary>A program's class: its name's parts, each capitalised ("content.pstep" → ContentPstep, "led_patch" → LedPatch).</summary>
    private static string ClassName(string program) =>
        string.Concat(program.Split('.', '_').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static string IdsSource(IEnumerable<ProgramLayout> programs)
    {
        var o = new System.Text.StringBuilder();
        o.Append("""
            // The product programs' constants and textures by their place in the program table (programs.layout.json), so a draw sets
            // them with no look-up by name. ProgramTableTests writes this file from the table (PLAYMAP_WRITE_FIXTURES=1); it changes
            // only with the table.

            namespace PlayMap.Core.Gpu.Programs;

            /// <summary>Each product program's id, its constants' ids and its textures' ids, as the program table numbers them.</summary>
            public static class ProgramIds
            {

            """);
        var first = true;
        var index = 0;
        foreach (var p in programs)
        {
            if (!first)
                o.Append('\n');
            first = false;
            var id = index++.ToString(System.Globalization.CultureInfo.InvariantCulture);
            o.Append("    /// <summary>The <c>").Append(p.Name).Append("</c> program.</summary>\n");
            o.Append("    public static class ").Append(ClassName(p.Name)).Append("\n    {\n");
            o.Append("        public static readonly ProgramId Program = new(").Append(id).Append(");\n");
            for (var i = 0; i < p.Constants.Length; i++)
            {
                var n = p.Constants[i].Name;
                o.Append("        public static readonly ConstantId ").Append(n).Append(" = new(Program, ")
                    .Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(", \"").Append(n).Append("\");\n");
            }
            foreach (var t in p.Textures)
            {
                o.Append("        public static readonly TextureId ").Append(t.Name).Append(" = new(Program, ")
                    .Append(t.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(", \"").Append(t.Name).Append("\");\n");
            }
            o.Append("    }\n");
        }
        o.Append("}\n");
        return o.ToString();
    }
}
