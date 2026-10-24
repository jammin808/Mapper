using System.Text.Json;
using PlayMap.Core.Bus;
using PlayMap.Core.Effects;
using PlayMap.Core.Model;
using PlayMap.Render.Effects;

namespace PlayMap.Engine.Fx;

/// <summary>An effect chain and the entity that owns it: its id, kind ("output", "layer", "canvas", "surface", "item"), name, and
/// whether the renderer may draw it (an enabled output's chain, any layer's, surface's or item's; never a canvas's own).</summary>
internal sealed record FxOwner(string Id, string Kind, string Name, FxChain Chain, bool Drawn);

/// <summary>One slot as STATE, FX LIST, INDEX, preflight and the context tell it: the record, its owner and place, its kind (null
/// when this node has none), where the renderer has it ("ok", "compiling", "faulted", "missing", "bypassed", "idle") with words,
/// its measured GPU time, whether it is a pure function of show time, and its light.</summary>
internal sealed record FxSlotView(FxSlot Slot, FxOwner Owner, int Index, FxKind? Kind, string State, string Words, double GpuMs,
    bool Deterministic, bool History, Light Light)
{
    public string Label => Slot.Name.Length > 0 ? Slot.Name : Kind?.Name ?? Slot.Kind;
}

/// <summary>The engine's side of effects: the renderer's pass when the render host has one (its lane and its observed slot
/// states), and the views every reader builds from the published project and those observations. Any thread.</summary>
internal sealed class FxBoard
{
    /// <summary>A slot the renderer has not drawn for this many frames is not on its picture now.</summary>
    private const long RecentFrames = 120;

    internal const string HistoryWords = "integrates frame to frame: not frame-exact across nodes";

    /// <summary>Why a canvas's own chain shows nowhere: its layers' chains, and the chains of the outputs and surfaces that show
    /// it, are drawn; the canvas's is kept.</summary>
    internal const string CanvasWords = "not drawn: this build draws a canvas's layers' chains, not the canvas's own";

    private volatile FxPass? _pass;

    /// <summary>The render host's pass (null: no renderer on this node, or its context went).</summary>
    public FxPass? Pass => _pass;

    public void Attach(FxPass? pass) => _pass = pass;

    /// <summary>Where programs compile: "shared-context", "render-thread", or "none" with no renderer.</summary>
    public string LaneMode => _pass?.Lane.Mode ?? "none";

    /// <summary>Every chain in the show with its owner.</summary>
    public static IEnumerable<FxOwner> Owners(Project p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return OwnersOf(p);
    }

    private static IEnumerable<FxOwner> OwnersOf(Project p)
    {
        foreach (var o in p.Outputs)
            yield return new FxOwner(o.Id, "output", o.Name, o.Fx, o.Enabled);
        foreach (var c in p.Canvases)
        {
            yield return new FxOwner(c.Id, "canvas", c.Name, c.Fx, false);
            foreach (var ly in c.Layers)
                yield return new FxOwner(ly.Id, "layer", ly.Name, ly.Fx, true);
        }
        foreach (var sf in p.Stage.Surfaces)
            yield return new FxOwner(sf.Id, "surface", sf.Name, sf.Fx, true);
        foreach (var list in p.Playlists)
            foreach (var it in list.Items)
                yield return new FxOwner(it.Id, "item", it.Label, it.Fx, true);
    }

    /// <summary>Every slot of every chain, as the renderer last observed it.</summary>
    public List<FxSlotView> Views(Project p)
    {
        var catalog = FxCatalog.For(p.FxAssets);
        var list = new List<FxSlotView>();
        foreach (var owner in Owners(p))
            for (var i = 0; i < owner.Chain.Slots.Length; i++)
                list.Add(View(owner.Chain.Slots[i], owner, i, catalog));
        return list;
    }

    public FxSlotView View(FxSlot slot, FxOwner owner, int index, FxCatalog catalog)
    {
        var kind = catalog.Find(slot.Kind);
        var history = kind?.Traits.HistoryDependent ?? false;
        var deterministic = kind is not null && !history && !kind.Traits.ReadsAudio;
        var (state, words, gpu) = Observe(slot, kind, owner);
        if (!deterministic && kind is not null && words.Length == 0)
            words = kind.Traits.ReadsAudio ? "reads live sound: not frame-exact across nodes" : HistoryWords;
        var light = state switch
        {
            "ok" => Light.Green,
            "faulted" or "missing" => Light.Red,
            "compiling" => Light.Amber,
            _ => Light.Grey,
        };
        return new FxSlotView(slot, owner, index, kind, state, words, gpu, deterministic, history, light);
    }

    private (string State, string Words, double GpuMs) Observe(FxSlot slot, FxKind? kind, FxOwner owner)
    {
        if (slot.Bypass)
            return ("bypassed", "bypassed", 0);
        if (slot.Mix <= 0)
            return ("bypassed", "mix 0", 0);
        if (kind is null)
            return ("missing", $"this node has no effect {slot.Kind} — FX KINDS lists what it has", 0);
        if (_pass?.Stats is not { } stats)
            return ("idle", "no renderer on this node", 0);
        if (stats.TryGet(slot.Id, out var live) && stats.Frame - live.LastFrame <= RecentFrames && live.State != FxSlotState.Bypassed)
            return (Word(live.State), live.Words, live.GpuMs);
        if (owner.Kind == "canvas")
            return ("idle", CanvasWords, 0);
        if (!owner.Drawn)
            return ("idle", "not drawn: its output is off", 0);
        return ("compiling", "waiting for the renderer", 0);
    }

    public static string Word(FxSlotState state) => state switch
    {
        FxSlotState.Ok => "ok",
        FxSlotState.Compiling => "compiling",
        FxSlotState.Faulted => "faulted",
        // A slot the node's quarantine holds reads bypassed as the renderer drew it; the show's own bypass is read from the project.
        FxSlotState.Bypassed or FxSlotState.Quarantined => "bypassed",
        FxSlotState.Missing => "missing",
        _ => "idle",
    };

    /// <summary>An output chain's measured cost and its light against the budget: red past twice the budget, amber past it or
    /// with a slot faulted or missing, green drawn within it, grey before anything is measured.</summary>
    public static (double CostMs, Light Light) Cost(IEnumerable<FxSlotView> slots, double budgetMs)
    {
        var cost = 0.0;
        var any = false;
        var wrong = false;
        foreach (var v in slots)
        {
            cost += v.GpuMs;
            any |= v.State == "ok";
            wrong |= v.State is "faulted" or "missing";
        }
        var light = cost > 2 * budgetMs ? Light.Red : wrong || cost > budgetMs ? Light.Amber : any ? Light.Green : Light.Grey;
        return (cost, light);
    }

    /// <summary>A slot as JSON, the shape of STATE.fx.slots[] and FX LIST.</summary>
    public static void WriteSlot(Utf8JsonWriter w, FxSlotView v)
    {
        w.WriteStartObject();
        w.WriteString("id", v.Slot.Id);
        w.WriteString("owner", v.Owner.Id);
        w.WriteString("ownerKind", v.Owner.Kind);
        w.WriteNumber("index", v.Index);
        w.WriteString("kind", v.Slot.Kind);
        w.WriteString("name", v.Label);
        w.WriteString("state", v.State);
        w.WriteNumber("gpuMs", Math.Round(v.GpuMs, 3));
        w.WriteBoolean("bypass", v.Slot.Bypass);
        w.WriteNumber("mix", v.Slot.Mix);
        w.WriteString("blend", v.Slot.Blend.ToString().ToLowerInvariant());
        w.WriteNumber("scale", v.Slot.Scale);
        w.WriteBoolean("deterministic", v.Deterministic);
        w.WriteBoolean("history", v.History);
        w.WriteStartObject("live");
        w.WriteEndObject();
        w.WriteString("words", v.Words);
        w.WriteEndObject();
    }
}
