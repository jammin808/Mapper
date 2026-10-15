using PlayMap.Core.Model;
using PlayMap.Engine.Tests.Rendering;

namespace PlayMap.Engine.Tests.Audio;

/// <summary>A bound number reaches the drawn picture (on a hidden context; Mesa is enough): a white layer at opacity 0.2 over black,
/// its opacity on a one-second square LFO, reads white in the LFO's high half-second and a fifth of white in its low one — at the
/// show clock's instants, while the show keeps 0.2 and its revision.</summary>
[Collection("render host")]
public sealed class ModulationRenderTests
{
    private static Project Show()
    {
        var layer = new Layer("ly_white", "White", SourceRef.Solid("#FFFFFF"), new RectD(0, 0, 64, 48), 0.2, LayerBlend.Normal, true);
        var canvas = new Canvas("cv_main", "Main", 64, 48, "#000000", [layer]);
        return HostRig.WithOutputs(HostRig.Screen("op_1", SourceRef.Canvas("cv_main"), 64, 48)) with { Canvases = [canvas] };
    }

    [Fact]
    public async Task AnLfoBoundOpacityIsDrawnAtTheShowClocksInstants()
    {
        await using var rig = HostRig.Start(Show());
        static int Red(byte[] px) => HostRig.At(px, 64, 32, 24).R;
        rig.Clock.NowNs = 5_000_000_000;
        Assert.InRange(Red(await rig.ReadUntil("op_1", px => Math.Abs(Red(px) - 51) <= 3)), 48, 54);
        await rig.Ok("MOD BIND ly_white opacity lfo:square/1 AMOUNT 1");
        var revision = rig.Engine.Store.Current.Revision;
        // 5.2 s: the square is high, the opacity 0.2 + 1 (held at 1): white.
        rig.Clock.NowNs = 5_200_000_000;
        Assert.Equal(255, Red(await rig.ReadUntil("op_1", px => Red(px) == 255)));
        // 5.7 s: low, the base: a fifth of white.
        rig.Clock.NowNs = 5_700_000_000;
        Assert.InRange(Red(await rig.ReadUntil("op_1", px => Math.Abs(Red(px) - 51) <= 3)), 48, 54);
        // Held: the base whatever the LFO.
        await rig.Ok("MOD BYPASS ON");
        rig.Clock.NowNs = 6_200_000_000;
        Assert.InRange(Red(await rig.ReadUntil("op_1", px => Math.Abs(Red(px) - 51) <= 3)), 48, 54);
        Assert.Equal(revision, rig.Engine.Store.Current.Revision);
        Assert.Equal(0.2, rig.Engine.Store.Project.Canvases[0].Layers[0].Opacity);
    }
}
