using PlayMap.Core.Vision;

namespace PlayMap.Vision.Segmentation;

/// <summary>The one segmenter the engine holds: the installed models in order of preference, then the classical
/// segmenter, which is always there. A prompt goes to the first model that can take it (a phrase needs text, a
/// click points, a box a box); a model that throws or finds nothing hands over to the next, and the classical
/// path answers last — so a click always selects something a surface can be made from, model or not. A model's
/// masks are snapped to the scan's depth edges when the frame has points (its own logits kept, so the next click
/// still refines). The words say who answered and why the others did not.</summary>
public sealed class SegmenterRouter : ISegmenter
{
    private readonly IReadOnlyList<ISegmenter> _models;
    private readonly ClassicalSegmenter _classical;
    private readonly DepthSnapOptions? _snap;

    /// <param name="models">Model-backed segmenters, most preferred first (may be empty).</param>
    /// <param name="classical">The fallback.</param>
    /// <param name="snap">How model masks snap to the scan; null to leave them as the model drew them.</param>
    public SegmenterRouter(IReadOnlyList<ISegmenter> models, ClassicalSegmenter classical, DepthSnapOptions? snap)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(classical);
        _models = models;
        _classical = classical;
        _snap = snap;
    }

    public string Id => "router";

    public SegmenterCapabilities Capabilities
    {
        get
        {
            var c = _classical.Capabilities;
            foreach (var m in _models)
                c |= m.Capabilities;
            return c;
        }
    }

    /// <summary>Whether a segmenter with these capabilities can take the prompt.</summary>
    public static bool Fits(SegmenterCapabilities c, SegmentationPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.HasText && !prompt.HasPoints && !prompt.HasBox)
            return c.HasFlag(SegmenterCapabilities.Text);
        if (prompt.HasPoints && !c.HasFlag(SegmenterCapabilities.Points))
            return false;
        return !prompt.HasBox || c.HasFlag(SegmenterCapabilities.Box);
    }

    public async Task<SegmentationResult> SegmentAsync(VisionFrame frame, SegmentationPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(prompt);
        var notes = new List<string>();
        foreach (var model in _models)
        {
            if (!Fits(model.Capabilities, prompt))
                continue;
            SegmentationResult r;
            try
            {
                r = await model.SegmentAsync(frame, prompt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A model's failure is the next segmenter's turn, never the operator's dead end.
                notes.Add(model.Id + " failed: " + ex.Message);
                continue;
            }
            if (r.Best is null)
            {
                notes.Add(model.Id + ": " + (string.IsNullOrEmpty(r.Words) ? "nothing found" : r.Words));
                continue;
            }
            var masks = r.Masks;
            var snapped = false;
            if (_snap is not null && frame.Points is { } points)
            {
                masks = [.. masks.Select(m =>
                {
                    var s = DepthSnap.Snap(m, points, _snap);
                    return new SegmentationMask(s.Width, s.Height, s.Coverage, s.Score, s.Label, m.Logits);
                })];
                snapped = true;
            }
            return new SegmentationResult(masks, Join("by " + model.Id + (snapped ? ", snapped to the scan's edges" : ""), notes));
        }
        var classical = await _classical.SegmentAsync(frame, prompt, cancellationToken).ConfigureAwait(false);
        return classical with { Words = Join(classical.Best is null ? classical.Words : "by classical: " + classical.Words, notes) };
    }

    private static string Join(string words, List<string> notes) => notes.Count == 0 ? words : words + " (" + string.Join("; ", notes) + ")";
}
