using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Wall;
using PlayMap.Render.Engine;
using PlayMap.Render.Passes;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-wall's hooks into the render host: each frame takes the air's edit overlay and whether the show is armed (the
/// overlay pass then draws nothing), and adds the overlay pass, made with the context and let go with it (render thread). It also
/// measures how late each cursor move is drawn: the frames from its line's arrival to the frame that first draws it (this one counts),
/// times the output's frame period, p95 over the last moves into STATE.air.overlay.cursorP95Ms. Nothing here allocates once warm.</summary>
public sealed partial class RenderHost
{
#pragma warning disable CA2213 // Disposed by OnContextLostWall on the render thread that made it, never from DisposeAsync's thread.
    private OverlayPass? _overlay;
#pragma warning restore CA2213
    private ImmutableArray<IFramePass> _overlayGiven;
    private ImmutableArray<IFramePass> _overlayPasses;

    private const int FrameStarts = 32;
    private const int CursorSamples = 300;
    private readonly long[] _frameStarts = new long[FrameStarts];
    private long _frameStartCount;
    private CursorBoard.Board? _cursorBoardSeen;
    private long _cursorWatermarkNs;
    private readonly double[] _cursorMs = new double[CursorSamples];
    private readonly double[] _cursorSorted = new double[CursorSamples];
    private int _cursorCount;
    private double _cursorShownMs = -1;

    /// <summary>Each cursor move not yet measured that this frame draws: its frames late (1 when it arrived after the last frame
    /// began) times its output's frame period.</summary>
    private void MeasureCursors(EngineCore engine, EngineSnapshot snap)
    {
        var now = MonoClock.Instance.NowNs;
        var board = engine.WallCursorBoard;
        var o = snap.Air.Overlay;
        if (!ReferenceEquals(board, _cursorBoardSeen) && o.On && (o.Layers & OverlayLayers.Cursor) != 0 && !engine.Services.ShowGate.Armed)
        {
            _cursorBoardSeen = board;
            var measured = false;
            var newest = _cursorWatermarkNs;
            for (var k = 0; k < board.Items.Length; k++)
            {
                var arrived = board.Arrived[k];
                if (arrived <= _cursorWatermarkNs)
                    continue;
                newest = Math.Max(newest, arrived);
                var frames = 1;
                var known = (int)Math.Min(_frameStartCount, FrameStarts);
                for (var back = 1; back <= known; back++)
                {
                    if (_frameStarts[(int)((_frameStartCount - back) % FrameStarts)] <= arrived)
                        break;
                    frames++;
                }
                var hz = 60.0;
                foreach (var output in snap.Project.Project.Outputs)
                    if (string.Equals(output.Id, board.Items[k].OutputId, StringComparison.Ordinal))
                        hz = output.RefreshHz;
                _cursorMs[_cursorCount % CursorSamples] = frames * 1000.0 / (hz > 0 ? hz : 60);
                _cursorCount++;
                measured = true;
            }
            _cursorWatermarkNs = newest;
            if (measured)
            {
                var n = Math.Min(_cursorCount, CursorSamples);
                Array.Copy(_cursorMs, _cursorSorted, n);
                Array.Sort(_cursorSorted, 0, n);
                var p95 = _cursorSorted[Math.Clamp((int)Math.Ceiling(0.95 * n) - 1, 0, n - 1)];
                if (Math.Abs(p95 - _cursorShownMs) >= 0.05)
                {
                    _cursorShownMs = p95;
                    engine.CursorP95Ms = p95;
                    Volatile.Read(ref _facts)?.Raise();
                }
            }
        }
        else if (!ReferenceEquals(board, _cursorBoardSeen))
        {
            // Not drawn this frame (the overlay is off, its cursor layer is, or the show is armed): no move is measured late for it.
            _cursorBoardSeen = board;
            for (var k = 0; k < board.Arrived.Length; k++)
                _cursorWatermarkNs = Math.Max(_cursorWatermarkNs, board.Arrived[k]);
        }
        _frameStarts[(int)(_frameStartCount % FrameStarts)] = now;
        _frameStartCount++;
    }

    private bool _witnessing;

    partial void ComposeWall(EngineCore engine, EngineSnapshot snap, ref FrameInputs inputs)
    {
        MeasureCursors(engine, snap);
        if (!_witnessing)
        {
            _witnessing = true;
            engine.RenderWitnessesContent();
        }
        // The first content on a quad: a frame of its source is there to draw this frame (once; nothing is held).
        if (engine.AwaitingFirstContent && EngineCore.QuadContent(snap.Project.Project) is { } content
            && inputs.Sources.Acquire(content, inputs.LeaderNs) is { } lease)
        {
            lease.Dispose();
            engine.FirstContentDrawn();
            Volatile.Read(ref _facts)?.Raise();
        }
        inputs = inputs with { Overlay = snap.Air.Overlay, Cursors = engine.WallCursors, Armed = inputs.Armed || engine.Services.ShowGate.Armed };
        if (_overlay is not { } pass)
            return;
        if (_overlayPasses.IsDefault || inputs.Passes != _overlayGiven)
        {
            _overlayGiven = inputs.Passes;
            _overlayPasses = inputs.Passes.IsDefaultOrEmpty ? [pass] : inputs.Passes.Add(pass);
        }
        inputs = inputs with { Passes = _overlayPasses };
        pass.Prepare(in inputs);
    }

    partial void OnContextWall(RenderEngine renderer)
    {
        _overlay = new OverlayPass(renderer.Device);
    }

    partial void OnContextLostWall()
    {
        _overlay?.Dispose();
        _overlay = null;
        _overlayGiven = default;
        _overlayPasses = default;
    }
}
