using System.Collections.Immutable;
using PlayMap.Core.Model;

namespace PlayMap.Engine.Wall;

/// <summary>Every session's cursor on the wall: written on the control lane only (CURSOR, the tick's lease), read by the render
/// thread through one volatile swap of an immutable set, so a reader never sees a half-made board. A session has at most one cursor;
/// past the node's maximum the oldest goes. Cursor traffic never moves the project or the journal; only a change in how many are
/// up moves STATE.</summary>
internal sealed class CursorBoard
{
    private sealed record Board(ImmutableArray<WallCursor> Items);

    private volatile Board _board = new([]);

    /// <summary>The cursors up now (any thread).</summary>
    public ImmutableArray<WallCursor> Cursors => _board.Items;

    /// <summary>Puts a session's cursor (replacing its last), dropping the oldest past <paramref name="max"/>; true when the count
    /// changed.</summary>
    public bool Put(WallCursor cursor, int max)
    {
        var items = _board.Items;
        var before = items.Length;
        var i = -1;
        for (var k = 0; k < items.Length && i < 0; k++)
            if (items[k].Session == cursor.Session)
                i = k;
        items = i >= 0 ? items.SetItem(i, cursor) : items.Add(cursor);
        while (items.Length > Math.Max(1, max))
        {
            var oldest = 0;
            for (var k = 1; k < items.Length; k++)
                if (items[k].ReceivedMonoNs < items[oldest].ReceivedMonoNs)
                    oldest = k;
            items = items.RemoveAt(oldest);
        }
        _board = new Board(items);
        return items.Length != before;
    }

    /// <summary>Takes a session's cursor down; false when it had none.</summary>
    public bool Remove(string session)
    {
        var items = _board.Items;
        var left = items.RemoveAll(c => c.Session == session);
        if (left.Length == items.Length)
            return false;
        _board = new Board(left);
        return true;
    }

    /// <summary>Takes down every cursor whose lease ended by <paramref name="monoNs"/>; true when any went.</summary>
    public bool Prune(long monoNs)
    {
        var items = _board.Items;
        if (!items.Any(c => c.ExpiresMonoNs <= monoNs))
            return false;
        _board = new Board(items.RemoveAll(c => c.ExpiresMonoNs <= monoNs));
        return true;
    }
}
