using PlayMap.Core.Effects;
using PlayMap.Core.Gpu;
using PlayMap.Core.Model;
using PlayMap.Render.Engine;

namespace PlayMap.Render.Effects;

/// <summary>A frame as a chain sees it: the renderer's frame number, the leader instant, the show's frame rate, the instant as wall
/// time (for DATE), and the instant the owner's time counts from (an item's start; 0 for the leader's epoch).</summary>
public readonly record struct FxFrame(long Frame, long LeaderNs, double FrameRate, long UnixMs, long OwnerOriginNs);

/// <summary>Runs effect chains on the render thread, into the frame's list: for each slot in order, skips it when bypassed or at mix 0
/// (the input passes untouched — bit-exact), when its kind is missing, while its program compiles or once it faulted (it shows its
/// input); otherwise draws the slot's effect at its scale and mixes it over its input into one of the owner's two targets (blend and
/// mix), so every slot's result is a top-row-first picture at the input's size. One renderer per slot, made on first use, configured
/// again when the slot's record changes, dropped after <see cref="IdleFrames"/> frames unused; each slot's GPU time is measured
/// between two timestamps. It allocates nothing per frame once warm, and nothing it draws throws through a frame.</summary>
public sealed class FxChainRunner : IDisposable
{
    public const int IdleFrames = 120;

    private sealed class Owner : IDisposable
    {
        public RenderTarget? A;
        public RenderTarget? B;
        public long Frame = -1;
        public GpuTexture? Input;
        public GpuTexture? Result;
        public long LastUsed;

        public RenderTarget Next(IGpuDevice device, GpuTexture current, int w, int h, TextureFormat format)
        {
            if (A is null || A.Width != w || A.Height != h || A.Format != format)
            {
                Dispose();
                A = device.CreateTarget(new TargetDesc(w, h, format));
                B = device.CreateTarget(new TargetDesc(w, h, format));
            }
            return ReferenceEquals(current, A.Color) ? B! : A;
        }

        public void Dispose()
        {
            A?.Dispose();
            B?.Dispose();
            A = B = null;
        }
    }

    private sealed class SlotState(IsfRenderer renderer, GpuSpanTimer timer, FxSlotLive live) : IDisposable
    {
        public IsfRenderer Renderer { get; } = renderer;
        public GpuSpanTimer Timer { get; } = timer;
        public FxSlotLive Live { get; } = live;
        public FxSlot? Slot;
        public FxCatalog? Catalog;
        public long LastUsed;

        public void Dispose() => Renderer.Dispose();
    }

    /// <summary>The mix of a slot's result over its input writes the owner's target whole, opaque.</summary>
    private static readonly DrawState Mixed = DrawState.Opaque;

    private readonly IGpuDevice _device;
    private readonly FxLane _lane;
    private readonly ProgramId _mix;
    private readonly Dictionary<string, Owner> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SlotState> _slots = new(StringComparer.Ordinal);
    private readonly List<string> _stale = [];
    private readonly GpuTexture _black;
    private readonly GpuTexture _grey;
    private readonly IsfDialect _dialect;
    private FxCatalog _catalog = FxCatalog.Builtin;
    private long _frame;

    public FxChainRunner(IGpuDevice device, FxLane lane)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(lane);
        _device = device;
        _lane = lane;
        _dialect = lane.Dialect;
        _mix = device.Programs.Id("fxmix");
        _black = device.CreateTexture(new TextureDesc(1, 1, TextureFormat.Rgba8, FrameLists.Uploaded));
        _grey = device.CreateTexture(new TextureDesc(1, 1, TextureFormat.Rgba8, FrameLists.Uploaded));
    }

    private bool _filled;

    public FxStats Stats { get; } = new();

    /// <summary>Slot renderers alive now.</summary>
    public int Renderers => _slots.Count;

    /// <summary>The kinds this frame's show can use.</summary>
    public FxCatalog Catalog => _catalog;

    public void BeginFrame(long frame, Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _frame = frame;
        _catalog = FxCatalog.For(project.FxAssets);
        Stats.SetFrame(frame);
    }

    /// <summary>A chain over a picture, drawn into the frame's <paramref name="list"/>: the last slot's result, or the input itself
    /// when no slot changed it. Render thread.</summary>
    public GpuTexture Apply(ICommandList list, FxChain chain, string ownerId, GpuTexture input, in FxFrame frame)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(input);
        if (chain.IsEmpty)
            return input;
        if (!_filled)
        {
            _filled = true;
            list.Upload(_black, [0, 0, 0, 0], UploadFormat.Rgba8);
            list.Upload(_grey, [128, 128, 128, 255], UploadFormat.Rgba8);
        }
        if (!_owners.TryGetValue(ownerId, out var owner))
            _owners[ownerId] = owner = new Owner();
        owner.LastUsed = _frame;
        if (owner.Frame == _frame && ReferenceEquals(owner.Input, input) && owner.Result is not null)
            return owner.Result;
        var current = input;
        var w = input.Width;
        var h = input.Height;
        var features = _device.Features;
        var format = input.Format is TextureFormat.Rgba8 or TextureFormat.Rgba16F or TextureFormat.Rgba32F && IsfRenderer.Renders(features, input.Format)
            ? input.Format
            : IsfRenderer.Renders(features, TextureFormat.Rgba16F) ? TextureFormat.Rgba16F : TextureFormat.Rgba8;
        foreach (var slot in chain.Slots)
            current = ApplySlot(list, slot, owner, current, w, h, format, in frame);
        owner.Frame = _frame;
        owner.Input = input;
        owner.Result = current;
        return current;
    }

    private GpuTexture ApplySlot(ICommandList list, FxSlot slot, Owner owner, GpuTexture current, int w, int h, TextureFormat format,
        in FxFrame frame)
    {
        if (slot.Bypass || slot.Mix <= 0)
        {
            Stats.For(slot.Id).Set(FxSlotState.Bypassed, slot.Bypass ? "bypassed" : "mix 0", _frame);
            return current;
        }
        if (_catalog.Find(slot.Kind) is not { } kind || _catalog.Translation(kind.Id, _dialect) is not { } source
            || _catalog.Source(kind.Id) is not { } files)
        {
            var missing = Stats.For(slot.Id);
            if (missing.State == FxSlotState.Missing)
                missing.Touch(_frame);
            else
                missing.Set(FxSlotState.Missing, "this node has no effect " + slot.Kind, _frame);
            return current;
        }
        if (!_slots.TryGetValue(slot.Id, out var state))
        {
            state = new SlotState(new IsfRenderer(_device, _lane, _black, _grey), new GpuSpanTimer(_device), Stats.For(slot.Id));
            _slots[slot.Id] = state;
        }
        state.LastUsed = _frame;
        try
        {
            if (!ReferenceEquals(state.Slot, slot) || !ReferenceEquals(state.Catalog, _catalog))
            {
                state.Renderer.Configure(slot, kind, source, files);
                state.Slot = slot;
                state.Catalog = _catalog;
                state.Live.Kind(kind.Traits.HistoryDependent);
            }
            var scale = Math.Clamp(slot.Scale, 0.25, 1);
            var ew = Math.Max(1, (int)Math.Round(w * scale));
            var eh = Math.Max(1, (int)Math.Round(h * scale));
            var origin = slot.OriginNs != 0 ? slot.OriginNs : frame.OwnerOriginNs;
            var clock = FxTime.At(frame.LeaderNs, origin, frame.FrameRate, frame.UnixMs);
            state.Timer.Begin(list, _frame);
            var fx = state.Renderer.Apply(list, _frame, in clock, current, ew, eh);
            if (fx is null)
            {
                state.Timer.Cancel();
                state.Live.Set(state.Renderer.State, state.Renderer.Words, _frame);
                return current;
            }
            var target = owner.Next(_device, current, w, h, format);
            using (list.Pass(target, PassDesc.Keep))
                list.Draw(_mix, Mixed)
                    .Texture("uInput", current)
                    .Texture("uFx", fx)
                    .SetInt("uFxBottomUp", 1)
                    .SetInt("uBlend", (int)slot.Blend)
                    .Set("uMix", (float)Math.Clamp(slot.Mix, 0, 1))
                    .Fullscreen();
            state.Timer.End(list);
            state.Live.Gpu(state.Timer.LastNs);
            state.Live.Set(FxSlotState.Ok, clock.Reanchor ? "TIME is past float precision: FX RETIME re-anchors it" : "", _frame);
            return target.Color;
        }
        // A slot that cannot draw shows its input; the next slot and the output go on.
        catch (Exception ex) when (ex is GpuException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            state.Timer.Cancel();
            state.Renderer.Fail(ex.Message);
            state.Live.Set(FxSlotState.Faulted, state.Renderer.Words, _frame);
            return current;
        }
    }

    /// <summary>True when the owner's chain was applied or skipped this frame. Render thread.</summary>
    public bool Met(string ownerId) => _owners.TryGetValue(ownerId, out var owner) && owner.LastUsed == _frame;

    /// <summary>True when the owner's chain was applied or skipped the frame before this one (it was on the air, or kept while the
    /// air kept it off). Render thread.</summary>
    public bool MetLast(string ownerId) => _owners.TryGetValue(ownerId, out var owner) && owner.LastUsed == _frame - 1;

    /// <summary>A chain the frame did not run because its owner showed something else (blackout, a test pattern, a safe look, a
    /// scan frame): each slot that is not bypassed says why in <paramref name="words"/> (it is idle, not drawing), or, with null
    /// words (a held picture, which still shows what the chain made), keeps its last state. Either way its renderer, buffers
    /// and program are kept, so the chain draws again on the first frame its owner is drawn. Render thread; allocates nothing
    /// once the slots are known.</summary>
    public void Skip(FxChain chain, string ownerId, string? words)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.IsEmpty)
            return;
        if (!_owners.TryGetValue(ownerId, out var owner))
            _owners[ownerId] = owner = new Owner();
        owner.LastUsed = _frame;
        foreach (var slot in chain.Slots)
        {
            var live = Stats.For(slot.Id);
            if (_slots.TryGetValue(slot.Id, out var state))
            {
                state.LastUsed = _frame;
                state.Renderer.Keep(_frame);
            }
            if (slot.Bypass || slot.Mix <= 0)
                live.Set(FxSlotState.Bypassed, slot.Bypass ? "bypassed" : "mix 0", _frame);
            else if (words is null)
                live.Touch(_frame);
            else
                live.Set(FxSlotState.Idle, words, _frame);
        }
    }

    /// <summary>A chain the frame did not run because its owner is out of the picture (a layer or surface that is not visible, a
    /// canvas nothing shows, an item its playlist is not showing): each slot that is not bypassed says why in
    /// <paramref name="words"/>, and nothing of the chain is kept for it — its renderers, buffers and targets go once unused for
    /// <see cref="IdleFrames"/> frames, as any unused slot's do. Render thread; allocates nothing once the slots are known.</summary>
    public void Report(FxChain chain, string words)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(words);
        if (chain.IsEmpty)
            return;
        foreach (var slot in chain.Slots)
        {
            var live = Stats.For(slot.Id);
            if (slot.Bypass || slot.Mix <= 0)
                live.Set(FxSlotState.Bypassed, slot.Bypass ? "bypassed" : "mix 0", _frame);
            else
                live.Set(FxSlotState.Idle, words, _frame);
        }
    }

    /// <summary>Lets go of slots and owners not drawn for <see cref="IdleFrames"/> frames. Render thread, once a frame.</summary>
    public void EndFrame()
    {
        if (_frame % 30 != 0)
            return;
        foreach (var (id, s) in _slots)
            if (_frame - s.LastUsed > IdleFrames)
                _stale.Add(id);
        foreach (var id in _stale)
        {
            _slots[id].Dispose();
            _slots.Remove(id);
            Stats.Forget(id);
        }
        _stale.Clear();
        foreach (var (id, o) in _owners)
            if (_frame - o.LastUsed > IdleFrames)
                _stale.Add(id);
        foreach (var id in _stale)
        {
            _owners[id].Dispose();
            _owners.Remove(id);
        }
        _stale.Clear();
    }

    public void Dispose()
    {
        foreach (var s in _slots.Values)
            s.Dispose();
        _slots.Clear();
        foreach (var o in _owners.Values)
            o.Dispose();
        _owners.Clear();
        _black.Dispose();
        _grey.Dispose();
    }
}
