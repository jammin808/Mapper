using PlayMap.Core.Content;
using PlayMap.Core.Model;
using PlayMap.Core.Playback;
using PlayMap.Render.Engine;
using PlayMap.Render.Generators;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-content's hooks into the render host. Each frame: every generator a playlist routes reads its item's clip time (the
/// incoming side of a transition too), the frame's modulation becomes the content frame over the node's bus (it forwards the bus
/// whole), and the sting the engine fired is the frame's impulse. The content frame is made with the context and let go with it;
/// the engine reads what the renderer observed of each generator from it, and is told when that moves (through the facts signal,
/// never the thread pool). Nothing here allocates once warm.</summary>
public sealed partial class RenderHost
{
    private ContentFrame? _content;
    private EngineCore? _contentEngine;
    private long _contentVersion;

    /// <summary>The frame's resolver as the pictures a generator reads (a particle field's emitter).</summary>
    private sealed class ContentPictures(RenderEngine renderer) : IContentPictures
    {
        public GpuTexture? Picture(SourceRef source, int width, int height) => renderer.SourcePicture(source, width, height);
    }

    partial void ComposeContent(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        var sting = engine.Content.Stings.Current;
        if (!sting.IsNone)
            inputs = inputs with { Impulse = sting };
        if (_content is not { } content)
            return;
        if (!ReferenceEquals(_contentEngine, engine))
        {
            _contentEngine = engine;
            engine.Content.Attach(content);
        }
        content.BeginItems();
        var leaderNs = inputs.LeaderNs;
        var lanes = snap.Playback.Playlists;
        foreach (var list in snap.Project.Project.Playlists)
        {
            if (!lanes.TryGetValue(list.Id, out var lane) || lane.IsIdle)
                continue;
            var (now, clip, _) = lane.At(leaderNs);
            if (now.Kind == SourceKind.Generator)
                content.SetItemTime(now.Id, PlaylistCursor.ItemTimeNs(clip, leaderNs));
            if (lane.MixAt(leaderNs) is not null && lane.NextSource.Kind == SourceKind.Generator)
                content.SetItemTime(lane.NextSource.Id, PlaylistCursor.ItemTimeNs(lane.NextClip, leaderNs));
        }
        inputs = inputs with { Modulation = content.Over(inputs.Modulation) };
        var version = content.Stats.Version;
        if (version != _contentVersion)
        {
            _contentVersion = version;
            Volatile.Read(ref _facts)?.Raise();
        }
    }

    partial void OnContextContent(RenderEngine renderer)
    {
        _content = new ContentFrame { Pictures = new ContentPictures(renderer) };
    }

    partial void OnContextLostContent()
    {
        _contentEngine?.Content.Attach(null);
        _content = null;
        // A context made again attaches its own frame.
        _contentEngine = null;
    }
}
