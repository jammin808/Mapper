using System.Collections.Immutable;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Wall;

/// <summary>Every session's cursor on the wall: written on the control lane only (CURSOR, the tick's lease), read by the render
/// thread through one volatile swap of an immutable set, so a reader never sees a half-made board. A session has at most one cursor;
/// past the node's maximum the oldest goes. Cursor traffic never moves the project or the journal; only a change in how many are
/// up moves STATE. Each cursor carries the instant its line arrived on the node's monotonic clock (the render thread's), so the frame
/// that first draws it can say how late it is.</summary>
internal sealed class CursorBoard
{
    /// <summary>The cursors up and, item for item, when each one's line arrived (monotonic ns).</summary>
    public sealed record Board(ImmutableArray<WallCursor> Items, ImmutableArray<long> Arrived);

    private volatile Board _board = new([], []);

    /// <summary>The cursors up now (any thread).</summary>
    public ImmutableArray<WallCursor> Cursors => _board.Items;

    /// <summary>The board as one immutable value (the render thread reads it once a frame).</summary>
    public Board Current => _board;

    /// <summary>Puts a session's cursor (replacing its last), dropping the oldest past <paramref name="max"/>; true when the count
    /// changed.</summary>
    public bool Put(WallCursor cursor, int max, long arrivedNs)
    {
        var items = _board.Items;
        var arrived = _board.Arrived;
        var before = items.Length;
        var i = -1;
        for (var k = 0; k < items.Length && i < 0; k++)
            if (items[k].Session == cursor.Session)
                i = k;
        (items, arrived) = i >= 0 ? (items.SetItem(i, cursor), arrived.SetItem(i, arrivedNs)) : (items.Add(cursor), arrived.Add(arrivedNs));
        while (items.Length > Math.Max(1, max))
        {
            var oldest = 0;
            for (var k = 1; k < items.Length; k++)
                if (items[k].ReceivedMonoNs < items[oldest].ReceivedMonoNs)
                    oldest = k;
            items = items.RemoveAt(oldest);
            arrived = arrived.RemoveAt(oldest);
        }
        _board = new Board(items, arrived);
        return items.Length != before;
    }

    /// <summary>Takes a session's cursor down; false when it had none.</summary>
    public bool Remove(string session)
    {
        return Keep(c => c.Session != session);
    }

    /// <summary>Takes down every cursor whose lease ended by <paramref name="monoNs"/>; true when any went.</summary>
    public bool Prune(long monoNs)
    {
        return Keep(c => c.ExpiresMonoNs > monoNs);
    }

    private bool Keep(Func<WallCursor, bool> keep)
    {
        var board = _board;
        if (board.Items.All(keep))
            return false;
        var items = ImmutableArray.CreateBuilder<WallCursor>();
        var arrived = ImmutableArray.CreateBuilder<long>();
        for (var k = 0; k < board.Items.Length; k++)
            if (keep(board.Items[k]))
            {
                items.Add(board.Items[k]);
                arrived.Add(board.Arrived[k]);
            }
        _board = new Board(items.ToImmutable(), arrived.ToImmutable());
        return true;
    }
}
