using System.Collections.Immutable;
using PlayMap.Core.Model;
using PlayMap.Core.Time;
using PlayMap.Engine.Wall;
using PlayMap.Render.Engine;
using PlayMap.Render.Passes;

namespace PlayMap.Engine.Rendering;

/// <summary>wp2-wall's hooks into the render host: each frame takes the air's edit overlay and whether the show is armed (the
/// overlay pass then draws nothing), and adds the overlay pass, made with the context and let go with it (render thread). It also
/// measures how late each cursor move is drawn — from its line's arrival to the frame the overlay pass reports it drew — p95 over the
/// last moves into STATE.air.overlay.cursorP95Ms through the facts signal.</summary>
public sealed partial class RenderHost
{
#pragma warning disable CA2213 // Disposed by OnContextLostWall on the render thread that made it, never from DisposeAsync's thread.
    private OverlayPass? _overlay;
#pragma warning restore CA2213
    private ImmutableArray<IFramePass> _overlayGiven;
    private ImmutableArray<IFramePass> _overlayPasses;

    private const int FrameStarts = 64;
    private const int CursorSamples = 300;
    private const int MaxPending = 16;
    private readonly long[] _frameStarts = new long[FrameStarts];
    private long _frameStartCount;
    private CursorBoard.Board? _cursorBoardSeen;
    private long _cursorWatermarkNs;
    private readonly WallCursor?[] _pending = new WallCursor?[MaxPending];
    private readonly long[] _pendingFrame = new long[MaxPending];
    private readonly double[] _pendingHz = new double[MaxPending];
    private readonly double[] _cursorMs = new double[CursorSamples];
    private readonly double[] _cursorSorted = new double[CursorSamples];
    private int _cursorCount;
    private double _cursorShownMs = -1;

    /// <summary>How late cursor moves are drawn. A move joins the pending set when its board is first seen, with the index of the first
    /// frame that began after its line arrived; once the overlay pass reports it drew that very cursor on its output, its latency is
    /// the frames from that frame to the drawing one, inclusive, times its output's frame period. A move replaced before it was drawn
    /// is dropped unmeasured; one never drawn (its output's overlay off) stays pending until replaced. Fixed arrays only: nothing here
    /// allocates once warm.</summary>
    private void MeasureCursors(EngineCore engine, EngineSnapshot snap)
    {
        var now = MonoClock.Instance.NowNs;
        var thisFrame = _frameStartCount;
        var measured = false;

        // What the last frame (index thisFrame - 1) drew.
        if (_overlay is { } pass)
        {
            for (var i = 0; i < MaxPending; i++)
            {
                if (_pending[i] is not { } c || !pass.Drew(c))
                    continue;
                var frames = thisFrame - 1 - _pendingFrame[i] + 1;
                _cursorMs[_cursorCount % CursorSamples] = Math.Max(1, frames) * 1000.0 / _pendingHz[i];
                _cursorCount++;
                _pending[i] = null;
                measured = true;
            }
        }

        // New moves on the board: pending from the first frame that began after they arrived.
        var board = engine.WallCursorBoard;
        if (!ReferenceEquals(board, _cursorBoardSeen))
        {
            _cursorBoardSeen = board;
            for (var i = 0; i < MaxPending; i++)
            {
                if (_pending[i] is not { } c)
                    continue;
                var still = false;
                foreach (var b in board.Items)
                    still |= ReferenceEquals(b, c);
                if (!still)
                    _pending[i] = null;
            }
            var newest = _cursorWatermarkNs;
            for (var k = 0; k < board.Items.Length; k++)
            {
                var arrived = board.Arrived[k];
                if (arrived <= _cursorWatermarkNs)
                    continue;
                newest = Math.Max(newest, arrived);
                var after = 0;
                var known = (int)Math.Min(_frameStartCount, FrameStarts);
                for (var back = 1; back <= known && _frameStarts[(int)((_frameStartCount - back) % FrameStarts)] > arrived; back++)
                    after++;
                var hz = 60.0;
                foreach (var output in snap.Project.Project.Outputs)
                    if (string.Equals(output.Id, board.Items[k].OutputId, StringComparison.Ordinal) && output.RefreshHz > 0)
                        hz = output.RefreshHz;
                for (var i = 0; i < MaxPending; i++)
                {
                    if (_pending[i] is not null)
                        continue;
                    _pending[i] = board.Items[k];
                    _pendingFrame[i] = _frameStartCount - after;
                    _pendingHz[i] = hz;
                    break;
                }
            }
            _cursorWatermarkNs = newest;
        }

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
