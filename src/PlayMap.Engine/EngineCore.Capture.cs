using System.Text.Json;
using PlayMap.Core.Media;
using PlayMap.Core.Model;
using PlayMap.Engine.VideoIo;

namespace PlayMap.Engine;

/// <summary>wp3-capture's hooks into the engine (EngineCore.Modules.cs): its verbs, the outputs' passThrough key, the live inputs'
/// capture and latency keys and its checks. The package fills each body; until it does, its check refuses an output's pass-through
/// and every capture: source an edit would store (a media item's location or a live input's backup), with the grammar's words when
/// the grammar refuses it, so no capture URI is stored as anything else and nothing of a refused one is journalled.</summary>
public sealed partial class EngineCore
{
    /// <summary>What a node without capture providers answers for a well-formed capture: source.</summary>
    internal const string CaptureSourcesNotYetWords = "capture: sources are not available on this node yet";
    partial void RouteCapture(VerbRoutes routes)
    {
        // wp3-capture fills this.
    }

    partial void OutputCapture(Utf8JsonWriter w, Output o)
    {
        // wp3-capture fills this.
    }

    partial void InputCapture(Utf8JsonWriter w, LiveInput input)
    {
        // wp3-capture fills this.
    }

    partial void ValidateCapture(Project p, object entity, ref string? why)
    {
        // wp3-capture fills this. Another check's words stand: this one only adds its own. Until then an output's pass-through is
        // refused (nothing runs one yet), and so is every capture: source.
        if (why is not null)
            return;
        if (entity is Output o)
        {
            why = NotDrawnYet("passThrough", o.PassThrough, p.FindOutput(o.Id)?.PassThrough ?? false, false, p.FindOutput(o.Id) is null);
            return;
        }
        if (entity is not MediaItem item)
            return;
        var claimed = CaptureUri.Claims(item.Location) ? item.Location
            : item.Live is { Backup: { } backup } && CaptureUri.Claims(backup) ? backup
            : null;
        if (claimed is not null)
            why = CaptureUri.TryParse(claimed, out _, out var refused) ? CaptureSourcesNotYetWords : refused;
    }
}
