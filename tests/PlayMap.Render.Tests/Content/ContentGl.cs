using PlayMap.Core.Model;
using PlayMap.Core.Modulation;
using PlayMap.Render.Generators;
using PlayMap.Render.Gpu;

namespace PlayMap.Render.Tests.Content;

/// <summary>What the content tests share: a generator drawn on a host at a leader instant with a content frame, drawn again at the
/// same instant until it has settled, and a bus answering fixed values.</summary>
internal static class ContentGl
{
    public static readonly GeneratorContext Context = new("Output 1", 1, "#FF8800", true);

    /// <summary>One frame of a generator on a host (frame number <paramref name="frame"/>), its picture read back as BGRA.</summary>
    public static GpuTexture Draw(GeneratorHost host, Generator g, double leaderSeconds, long frame, int w, int h,
        IModulationBus? bus = null, Core.Content.EffectImpulse? impulse = null)
    {
        host.BeginFrame(frame);
        var f = GeneratorFrame.At(leaderSeconds) with { Modulation = bus, Impulse = impulse ?? Core.Content.EffectImpulse.None };
        var tex = host.Render(g, w, h, Context, f);
        host.EndFrame();
        return tex;
    }

    /// <summary>Draws a generator at one instant until its renderer has settled (a particle field catching up a life), at most 40
    /// frames; answers the frame number reached.</summary>
    public static long Settle(GeneratorHost host, Generator g, double leaderSeconds, long frame, int w, int h, IModulationBus? bus = null,
        Core.Content.EffectImpulse? impulse = null)
    {
        for (var i = 0; i < 40; i++)
        {
            Draw(host, g, leaderSeconds, frame++, w, h, bus, impulse);
            if (host.RendererOf(g.Id, w, h) is not { } r || r.Progress >= 1)
                return frame;
        }
        throw new InvalidOperationException($"{g.Kind} did not settle in 40 frames");
    }

    /// <summary>A float state attachment's bits, bottom row first.</summary>
    public static float[] ReadState(GlDevice d, StateTarget state, int attachment)
    {
        var data = new float[state.Width * state.Height * 4];
        d.ReadStateFloat(state, attachment, data);
        return data;
    }
}

/// <summary>A bus that answers fixed values for named sources (and a slot per name), never allocating once made.</summary>
internal sealed class FixedBus : IModulationBus
{
    private readonly string[] _names;
    private readonly double[] _values;

    public FixedBus(params (string Name, double Value)[] values)
    {
        _names = [.. values.Select(v => v.Name)];
        _values = [.. values.Select(v => v.Value)];
    }

    public void Set(string name, double value) => _values[Array.IndexOf(_names, name)] = value;

    public bool TryRead(string source, long leaderNs, out double value01) => TryRead(Resolve(source), leaderNs, out value01);

    public bool IsLive(string source) => source.StartsWith("audio:", StringComparison.Ordinal);

    public int Resolve(string source) => Array.IndexOf(_names, source);

    public bool TryRead(int slot, long leaderNs, out double value01)
    {
        if (slot < 0 || slot >= _values.Length)
        {
            value01 = 0.5;
            return false;
        }
        value01 = _values[slot];
        return true;
    }
}
