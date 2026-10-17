using System.Collections.Concurrent;
using System.Globalization;
using PlayMap.Core.Media;
using PlayMap.Core.Media.Decode;
using PlayMap.Core.Media.Hap;
using PlayMap.Core.Model;
using PlayMap.Media.Hap;

namespace PlayMap.Engine.Decode;

/// <summary>This node's 4K60 HAP layer count as measured: how many 4K60 HAP Q layers its cores carry through the second stage
/// (upload excluded), the term that binds, when it was measured, and the arithmetic in words.</summary>
public sealed record HapLayersMeasured(int Value, string Limit, long MeasuredAtUnixMs, double DecodeMs, int Cores, string Words);

/// <summary>The node-local facts of decoding: what the renderer samples (set by the render host when its context exists, cleared
/// when it goes), the measured 4K60 HAP layer count (measured once, on demand, on a worker), and MEDIA INSPECT's results by
/// content hash. Read by STATE, the context and the preflight check from any thread; written by the render thread and the
/// measuring worker.</summary>
public sealed class DecodeFacts
{
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, HapInspection> _inspections = new(StringComparer.Ordinal);
    private volatile int _formats = -1;
    private string _binding = "";
    private volatile HapLayersMeasured? _layers;
    private Task<HapLayersMeasured>? _measuring;
    private volatile ZeroCopyFacts _zeroCopy = ZeroCopyFacts.None;
    private volatile PolicyState _policy = new(DecodePolicy.Auto, false);

    /// <summary>Called when a fact here moves (the engine's FactsChanged).</summary>
    public event Action? Changed;

    /// <summary>The renderer's block families, or null while no render context exists.</summary>
    public BlockFormats? Formats => _formats < 0 ? null : (BlockFormats)_formats;

    public HapLayersMeasured? Layers => _layers;

    /// <summary>The window-system binding of the render context ("egl", "glx", "wgl", "cgl"; "" when none answered or no
    /// context exists).</summary>
    public string Binding => Volatile.Read(ref _binding);

    /// <summary>The render context came (its families and the binding it came through) or went (null).</summary>
    public void Renderer(BlockFormats? formats, string binding = "")
    {
        var value = formats is { } f ? (int)f : -1;
        var bound = formats is null ? "" : binding;
        var moved = !string.Equals(Interlocked.Exchange(ref _binding, bound), bound, StringComparison.Ordinal);
        if (Interlocked.Exchange(ref _formats, value) != value || moved)
            Changed?.Invoke();
    }

    /// <summary>What the render context offers decoders: zero-copy ready or not and why, what its share is, and its upload arena
    /// (None while no context exists).</summary>
    public ZeroCopyFacts ZeroCopy => _zeroCopy;

    /// <summary>The render context's zero-copy facts and arena came (or went: <see cref="ZeroCopyFacts.None"/>).</summary>
    public void UseZeroCopy(ZeroCopyFacts facts)
    {
        _zeroCopy = facts ?? ZeroCopyFacts.None;
        Changed?.Invoke();
    }

    /// <summary>The policy decoders opened from now on follow, and whether DIRECT was forced without a proof.</summary>
    public (DecodePolicy Policy, bool Forced) Policy
    {
        get
        {
            var p = _policy;
            return (p.Policy, p.Forced);
        }
    }

    /// <summary>Sets the policy (the node's settings at start, DECODE POLICY after).</summary>
    public void SetPolicy(DecodePolicy policy, bool forced)
    {
        _policy = new PolicyState(policy, forced && policy == DecodePolicy.Direct);
        Changed?.Invoke();
    }

    private sealed record PolicyState(DecodePolicy Policy, bool Forced);

    /// <summary>Writes the policy into the node's settings file (the decode part sets it; null in tests: the policy lives in memory).
    /// Answers the words when the file could not be written, else null.</summary>
    public Func<DecodePolicy, string?>? Persist { get; set; }

    /// <summary>Records a measurement (the measuring worker; a test that sets what this node sustains).</summary>
    public void Record(HapLayersMeasured measured)
    {
        _layers = measured;
        Changed?.Invoke();
    }

    /// <summary>The measured layer count, measuring it once on a worker when it has not been (a second caller shares the same
    /// measurement).</summary>
    public Task<HapLayersMeasured> EnsureMeasuredAsync(CancellationToken ct)
    {
        if (_layers is { } known)
            return Task.FromResult(known);
        lock (_gate)
        {
            _measuring ??= Core.Threading.Compute.RunAsync(() =>
            {
                var measured = Measure(Environment.ProcessorCount, HapThroughput.DecodeMsPer4KFrame(9, CancellationToken.None),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Record(measured);
                return measured;
            }, CancellationToken.None);
            return _measuring.WaitAsync(ct);
        }
    }

    /// <summary>The measurement's arithmetic: 4K60 HAP Q layers from one core's decode time per frame and the core count (disk
    /// and upload are not part of it).</summary>
    public static HapLayersMeasured Measure(int cores, double decodeMs, long atUnixMs)
    {
        // The count is worked from the decode time the record keeps (to 0.01 ms), so the published time gives the published count.
        var ms = decodeMs > 0 ? Math.Max(Math.Round(decodeMs, 2), 0.01) : decodeMs;
        var budget = HapBudget.Layers(new HapNodeFacts(0, cores, 0), HapBudget.Layer4K60(HapCodec.HapQ, ms));
        return new HapLayersMeasured(budget.Layers, budget.Limit, atUnixMs, ms, cores,
            string.Create(CultureInfo.InvariantCulture, $"4K HAP Q second stage {ms:0.0} ms a frame on one core; ") + budget.Words);
    }

    /// <summary>A kept MEDIA INSPECT answer by its key (the content hash and the colour override that read it; "" is never kept).</summary>
    public bool TryInspection(string key, out HapInspection inspection) => _inspections.TryGetValue(key, out inspection!);

    public void Remember(string key, HapInspection inspection)
    {
        if (key.Length > 0)
            _inspections[key] = inspection;
    }
}

/// <summary>How much HAP the show plays at once, in 4K60 HAP Q layers (what the second stage moves: texture bytes × rate), the
/// item that weighs most, and every HAP item the show plays (<see cref="ItemIds"/>: what the BPTC rule of STATE, the context and
/// preflight looks at — a library item no output, surface, canvas or playlist shows is not the show's). Every output and surface
/// showing a clip directly is one decoder; a playlist is one decoder at its heaviest item; a canvas counts each of its layers.</summary>
public readonly record struct HapDemand(double Layers4K60, int Items, string Heaviest, double HeaviestLayers, IReadOnlySet<string> ItemIds)
{
    private static readonly double OneLayer = HapCodecs.FrameBytes(HapCodec.HapQ, 3840, 2160) * 60.0;

    public int Needed => (int)Math.Ceiling(Layers4K60 - 1e-9);

    public static HapDemand Of(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        double total = 0, heaviest = 0;
        var heaviestId = "";
        var items = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<SourceRef>();
        void Add(SourceRef source, int depth)
        {
            if (depth > 8 || !seen.Add(source))
                return;
            switch (source.Kind)
            {
                case SourceKind.Media:
                    var load = Load(project.FindMedia(source.Id), out var id);
                    if (load > 0)
                    {
                        items.Add(id);
                        total += load;
                        if (load > heaviest)
                            (heaviest, heaviestId) = (load, id);
                    }
                    break;
                case SourceKind.Playlist when project.FindPlaylist(source.Id) is { } list:
                    double most = 0;
                    var mostId = "";
                    foreach (var item in list.Items)
                    {
                        if (item.Source.Kind != SourceKind.Media)
                            continue;
                        var l = Load(project.FindMedia(item.Source.Id), out var mid);
                        if (l > 0)
                            items.Add(mid);
                        if (l > most)
                            (most, mostId) = (l, mid);
                    }
                    total += most;
                    if (most > heaviest)
                        (heaviest, heaviestId) = (most, mostId);
                    break;
                case SourceKind.Canvas when project.Canvases.FirstOrDefault(c => c.Id == source.Id) is { } canvas:
                    foreach (var layer in canvas.Layers)
                        if (layer.Visible)
                            Add(layer.Source, depth + 1);
                    break;
            }
        }
        foreach (var o in project.Outputs)
            Add(o.Source, 0);
        foreach (var s in project.Stage.Surfaces)
            Add(s.Content.Source, 0);
        return new HapDemand(total, items.Count, heaviestId, heaviest, items);
    }

    private static double Load(MediaItem? item, out string id)
    {
        id = item?.Id ?? "";
        if (item is null || !HapCodecs.TryFromName(item.Info.VideoCodec, out var codec) || item.Info.Width < 1 || item.Info.Height < 1)
            return 0;
        var fps = Core.Time.FrameRate.TryParse(item.Info.FrameRate, out var rate) && rate.IsValid ? rate.Numerator / (double)rate.Denominator : 60;
        return HapCodecs.FrameBytes(codec, item.Info.Width, item.Info.Height) * fps / OneLayer;
    }
}

/// <summary>What one render context offers decoding, as STATE says it: the share's API word and description, the binding, whether
/// zero-copy is ready (and why not), whether direct sampling is proven, and the upload arena (or why there is none).</summary>
public sealed record ZeroCopyFacts(string Api, string Describe, bool Ready, bool DirectProven, string Words, IUploadArena? Arena, string ArenaWords)
{
    public static readonly ZeroCopyFacts None = new("none", "", false, false, "no render context", null, "no render context");

    /// <summary>"dmabuf", "d3d11", "opengl", "none".</summary>
    public static string ApiWord(GpuApi api) => api switch
    {
        GpuApi.DmaBuf => "dmabuf",
        GpuApi.D3D11 => "d3d11",
        GpuApi.OpenGl => "opengl",
        GpuApi.IoSurface => "iosurface",
        GpuApi.Cuda => "cuda",
        _ => "none",
    };
}
