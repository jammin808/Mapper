using System.Text.Json;
using PlayMap.Core.Wire;
using PlayMap.Net.Commands;

namespace PlayMap.Net.Tests;

/// <summary>VERBS, /api/verbs and MCP's list_verbs: one table with each row's show class, risk words, flags, forms and whether
/// this node answers it now; node-to-node verbs are not in it.</summary>
public sealed class VerbJsonTests
{
    [Fact]
    public void EveryRowCarriesItsShowClassFormsAndAvailability()
    {
        using var doc = JsonDocument.Parse(VerbJson.All);
        var rows = doc.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!, StringComparer.Ordinal);

        Assert.Equal(VerbCatalog.All.Count(v => !v.Internal), rows.Count);
        Assert.False(rows.ContainsKey("MIRROR"));
        Assert.False(rows.ContainsKey("FLEET FENCE"));

        var blackout = rows["BLACKOUT"];
        Assert.Equal("air", blackout.GetProperty("show").GetString());
        Assert.True(blackout.GetProperty("available").GetBoolean());
        Assert.Equal(["BLACKOUT ON|OFF|TOGGLE"], blackout.GetProperty("forms").EnumerateArray().Select(f => f.GetString()));

        var policy = rows["DECODE POLICY"];
        Assert.False(policy.GetProperty("available").GetBoolean());
        Assert.Equal("setup", policy.GetProperty("show").GetString());
        var when = Assert.Single(policy.GetProperty("riskWhen").EnumerateArray());
        Assert.Equal((-1, "FORCE", "high"), (when.GetProperty("arg").GetInt32(), when.GetProperty("word").GetString(), when.GetProperty("risk").GetString()));

        Assert.True(rows["CURSOR"].GetProperty("ephemeral").GetBoolean());
        Assert.True(rows["NUDGE"].GetProperty("gesture").GetBoolean());
        Assert.True(rows["THUMB"].GetProperty("pairedOnly").GetBoolean());
        Assert.Equal("devicePower", rows["DEVICE POWER"].GetProperty("show").GetString());
        Assert.Equal(3, rows["UNLOCK"].GetProperty("forms").GetArrayLength());
        Assert.All(rows.Values, r => Assert.Equal(VerbCatalog.Find(r.GetProperty("name").GetString()!)!.Round == 1, r.GetProperty("available").GetBoolean()));
    }
}
