using System.Collections.Immutable;
using PlayMap.Core.Gpu;
using PlayMap.Core.Gpu.Programs;
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
            var repacked = ProgramLayout.Pack(p.Name, p.Constants.Select(c => (c.Name, c.Type, c.Count)), p.Textures.Select(t => (t.Name, t.Sampled)));
            Assert.Equal(repacked, p);
            Assert.Equal(0, p.ConstantBytes % 16);
        }
        var table = new ProgramTable();
        Assert.Equal(product.Select(p => p.Name), table.Names);
        Assert.Equal(product.Length, table.ProductCount);
    }
}
