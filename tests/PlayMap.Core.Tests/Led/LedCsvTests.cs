using PlayMap.Core.Led;

namespace PlayMap.Core.Tests.Led;

/// <summary>Pixel lists in MADRIX/Capture-style columns: rows of one fixture become one points fixture in pixel order, scaled; the
/// first pixel's address is the patch; every error names its line and column.</summary>
public sealed class LedCsvTests
{
    [Fact]
    public void RowsOfAFixtureBecomeOneFixtureInPixelOrder()
    {
        const string csv = """
            Fixture;Pixel;X;Y;Z;DMX Address;Type
            Bar A;2;10;0;0;3.4;grb
            Bar A;1;0;0;0;3.1;grb
            Bar B;1;0;5;0;1030;
            Bar A;3;20,5;0;0;3.7;grb
            """;
        // Semicolons, so a decimal comma is a cell, not a separator: invariant culture refuses it.
        Assert.False(LedCsvReader.TryRead(csv, 1, out _, out var why));
        Assert.Equal("line 5, column 3 (x): '20,5' is not a number", why);
        Assert.True(LedCsvReader.TryRead(csv.Replace("20,5", "20.5", StringComparison.Ordinal), 0.5, out var fixtures, out why), why);
        Assert.Equal(["Bar A", "Bar B"], fixtures.Select(f => f.Name));
        Assert.Equal([0.0, 5, 10.25], fixtures[0].Points.Select(p => p.X));
        Assert.Equal(("grb", 3, 1), (fixtures[0].Type, fixtures[0].Universe, fixtures[0].Address));
        // Absolute slot 1030 is universe 2, slot 6.
        Assert.Equal((2, 6, 2.5), (fixtures[1].Universe, fixtures[1].Address, fixtures[1].Points[0].Y));
    }

    [Theory]
    [InlineData("A,B\n1,2", "line 1: the header has no X and Y columns (it has a, b)")]
    [InlineData("X,Y\n", "line 1: a header and no pixels")]
    [InlineData("X,Y,Universe,Channel\n1,2,0,600", "line 2, column 4 (channel): '600' is not a channel from 1 to 512")]
    [InlineData("X,Y,DMX Address\n1,2,x.1", "line 2, column 3 (dmx address): 'x.1' is not a DMX address (U.A, or a slot from 1)")]
    [InlineData("Display Name\tX\tY\tIndex\nA\t1\t2\tone", "line 2, column 4 (index): 'one' is not a whole number")]
    [InlineData("", "the file is empty")]
    public void AnErrorNamesItsLineAndColumn(string csv, string words)
    {
        Assert.False(LedCsvReader.TryRead(csv, 1, out _, out var why));
        Assert.Equal(words, why);
    }
}
