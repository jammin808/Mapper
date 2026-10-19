using System.Diagnostics;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Effects;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Tests.Effects;

/// <summary>Slots, chains and pictures for the effect tests, and a chain run until its programs are in (the lane compiles on its
/// own context; each try is one renderer frame).</summary>
internal static class FxRig
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static FxSlot Slot(string kind, ParamSet? ps = null, string id = "fx00000001", double mix = 1, bool bypass = false,
        FxBlend blend = FxBlend.Normal, double scale = 1, long originNs = 0) =>
        new(id, kind, "", ps ?? ParamSet.Empty, bypass, mix, blend, scale, originNs);

    public static FxChain Chain(params FxSlot[] slots) => new([.. slots]);

    /// <summary>A picture uploaded top row first from a function of (x, y) → (r, g, b).</summary>
    public static GpuTexture Picture(GlDevice d, int w, int h, Func<int, int, (int R, int G, int B)> px)
    {
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = px(x, y);
                var i = (y * w + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)r, (byte)g, (byte)b, 255);
            }
        var t = d.CreateTexture(w, h, TextureFormat.Rgba8, TextureFilter.Nearest);
        d.Upload(t, rgba, UploadFormat.Rgba8);
        return t;
    }

    /// <summary>Applies a chain frame after frame until no slot is compiling, and answers the last result read back (BGRA, top row
    /// first). <paramref name="frame"/> counts the frames drawn.</summary>
    public static byte[] Run(GlDevice d, FxCompileLane lane, FxChainRunner runner, Project project, FxChain chain, GpuTexture input,
        ref long frame, long leaderNs = 1_000_000_000, string owner = "op_test")
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var result = Step(d, lane, runner, project, chain, input, ref frame, leaderNs, owner);
            if (chain.Slots.All(s => !runner.Stats.TryGet(s.Id, out var live) || live.State != FxSlotState.Compiling))
                return Scene.ReadTexture(d, result);
            Assert.True(waited.Elapsed < Patience, "an effect never finished compiling");
            Thread.Sleep(2);
        }
    }

    /// <summary>One renderer frame of a chain: the lane collected, the runner begun, the chain applied.</summary>
    public static GpuTexture Step(GlDevice d, FxCompileLane lane, FxChainRunner runner, Project project, FxChain chain, GpuTexture input,
        ref long frame, long leaderNs, string owner = "op_test")
    {
        frame++;
        lane.Collect(frame);
        runner.BeginFrame(frame, project);
        var result = runner.Apply(chain, owner, input, new FxFrame(frame, leaderNs, 60, 1_760_000_000_000, 0));
        runner.EndFrame();
        return result;
    }

    public static FxSlotLive Live(PlayMap.Render.Engine.RenderEngine engine, FxPass fx, string slotId) =>
        fx.Stats.TryGet(slotId, out var live) ? live : throw new InvalidOperationException($"no stats for {slotId} after frame {engine.Frame}");

    public static FxSlotLive Live(FxChainRunner runner, string slotId) =>
        runner.Stats.TryGet(slotId, out var live) ? live : throw new InvalidOperationException($"no stats for {slotId}");
}
