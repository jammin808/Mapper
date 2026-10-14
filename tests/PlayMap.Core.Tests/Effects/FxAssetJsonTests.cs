using System.Collections.Immutable;
using PlayMap.Core.Model;

namespace PlayMap.Core.Tests.Effects;

/// <summary>An ISF file the show carries survives the show's file: its fragment and vertex sources and its imported pictures
/// come back from the saved project as they went in, so a show opened on another node draws the same effects.</summary>
public sealed class FxAssetJsonTests
{
    [Fact]
    public void AnIsfAssetsSourcesAndPicturesRoundTripThroughTheShowsFile()
    {
        const string fs = "/*{ \"ISFVSN\": \"2\", \"IMPORTED\": { \"card\": { \"PATH\": \"card.png\" } } }*/\nvarying vec2 left;\nvoid main() { gl_FragColor = IMG_NORM_PIXEL(card, left); }\n";
        const string vs = "varying vec2 left;\nvoid main() { isf_vertShaderInit(); left = isf_FragNormCoord; }\n";
        var asset = new FxAsset("fa0a0b0c0d", "Shift", FxFormat.Isf, "/shows/fx/Shift.fs", "sha")
        {
            Source = fs,
            VertexSource = vs,
            Images = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create("card", "iVBORw0KGgo=")]),
        };
        var slot = new FxSlot("fx0a0b0c0d", "isf:fa0a0b0c0d", "", ParamSet.Empty, false, 1, FxBlend.Normal, 1, 0);
        var p = Project.CreateDefault("Carried");
        p = p with { FxAssets = [asset], Outputs = p.Outputs.Update(p.Outputs[0].Id, o => o with { Fx = new FxChain([slot]) }) };

        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));

        var a = Assert.Single(back.FxAssets);
        Assert.Equal((asset.Id, asset.Name, asset.Format, asset.Path, asset.ContentHash, fs, vs),
            (a.Id, a.Name, a.Format, a.Path, a.ContentHash, a.Source, a.VertexSource));
        Assert.Equal(["card"], a.Images.Keys.ToArray());
        Assert.Equal("iVBORw0KGgo=", a.Images["card"]);
        Assert.Equal("isf:fa0a0b0c0d", back.Outputs[0].Fx.Slots.Single().Kind);
        Assert.Equal(ProjectJson.Serialize(p), ProjectJson.Serialize(back));
    }
}
