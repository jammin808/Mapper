using System.Collections.Immutable;
using System.Security.Cryptography;

namespace PlayMap.Core.Model;

/// <summary>Anything in the project with a stable id. Ids are short lowercase strings with a kind prefix
/// ("pl" playlist, "sf" surface, "pj" projector…) so a wire line or an AI reads what it names.</summary>
public interface IEntity
{
    string Id { get; }
}

public static class Ids
{
    public const string Media = "md";
    public const string Playlist = "pl";
    public const string PlaylistItem = "it";
    public const string Canvas = "cv";
    public const string Layer = "ly";
    public const string Generator = "gn";
    public const string Surface = "sf";
    public const string Mesh = "ms";
    public const string Projector = "pj";
    public const string Output = "op";
    public const string Camera = "cm";
    public const string Scan = "sc";
    public const string Mask = "mk";
    public const string Project = "pr";

    /// <summary>A projector's control link (<see cref="DeviceLink"/>).</summary>
    public const string Device = "dv";

    /// <summary>A new id: the prefix and eight random hex digits (32 bits — unique within a project by
    /// a margin no show reaches; collisions are still checked by <see cref="NewUnique"/>).</summary>
    public static string New(string prefix)
    {
        Span<byte> b = stackalloc byte[4];
        RandomNumberGenerator.Fill(b);
        return prefix + Convert.ToHexStringLower(b);
    }

    public static string NewUnique<T>(string prefix, IEnumerable<T> existing) where T : IEntity
    {
        var taken = existing.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        string id;
        do
            id = New(prefix);
        while (taken.Contains(id));
        return id;
    }

    /// <summary>An id is 1–40 characters of [A-Za-z0-9_-]: safe in a wire line, a URL and a file name.</summary>
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 40)
            return false;
        foreach (var c in id)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
                return false;
        return true;
    }
}

/// <summary>Helpers for the immutable collections the model is made of.</summary>
public static class EntityList
{
    public static T? Find<T>(this ImmutableArray<T> items, string? id) where T : class, IEntity
    {
        if (id is null || items.IsDefault)
            return null;
        foreach (var it in items)
            if (string.Equals(it.Id, id, StringComparison.Ordinal))
                return it;
        return null;
    }

    public static int IndexOfId<T>(this ImmutableArray<T> items, string? id) where T : IEntity
    {
        if (id is null || items.IsDefault)
            return -1;
        for (var i = 0; i < items.Length; i++)
            if (string.Equals(items[i].Id, id, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>The list with the item of that id replaced by the edit; unchanged (same instance) when the
    /// id is absent or the edit returns the same item.</summary>
    public static ImmutableArray<T> Update<T>(this ImmutableArray<T> items, string id, Func<T, T> edit) where T : IEntity
    {
        var i = items.IndexOfId(id);
        if (i < 0)
            return items;
        var old = items[i];
        var next = edit(old);
        return ReferenceEquals(old, next) ? items : items.SetItem(i, next);
    }

    public static ImmutableArray<T> RemoveId<T>(this ImmutableArray<T> items, string id) where T : IEntity
    {
        var i = items.IndexOfId(id);
        return i < 0 ? items : items.RemoveAt(i);
    }

    public static ImmutableArray<T> Upsert<T>(this ImmutableArray<T> items, T item) where T : IEntity
    {
        var i = items.IndexOfId(item.Id);
        return i < 0 ? items.Add(item) : items.SetItem(i, item);
    }

    /// <summary>Moves the item to a new index (clamped).</summary>
    public static ImmutableArray<T> Move<T>(this ImmutableArray<T> items, string id, int newIndex) where T : IEntity
    {
        var i = items.IndexOfId(id);
        if (i < 0)
            return items;
        var item = items[i];
        var rest = items.RemoveAt(i);
        return rest.Insert(Math.Clamp(newIndex, 0, rest.Length), item);
    }
}
