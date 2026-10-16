using System.Collections.Immutable;
using System.Text.Json;
using PlayMap.Core.Model;
using PlayMap.Core.Show;
using PlayMap.Core.Wire;

namespace PlayMap.Engine;

/// <summary>LOCK, UNLOCK and LOCKS. A lock is kept in the project (it travels with the show) but is not an undo step: it is
/// published without a history entry and carried into every root the undo and redo stacks hold, so an undo never unlocks.
/// The guard (<see cref="LockGuard"/>) refuses any edit, undo or redo that changes what a lock holds.</summary>
public sealed partial class EngineCore
{
    private CommandReply Lock(VerbCall c)
    {
        var a = c.Args;
        if (a.Count == 0)
            return CommandReply.Fail("LOCK <id> [<words>] | LOCK AREA mapping|layout|content|playlists|media|devices|network [<words>]");
        var p = Store.Project;
        ShowLock l;
        if (a[0].Equals("AREA", StringComparison.OrdinalIgnoreCase))
        {
            if (a.Count < 2 || LockAreas.All.FirstOrDefault(x => x.Equals(a[1], StringComparison.OrdinalIgnoreCase)) is not { } area)
                return CommandReply.Fail($"LOCK AREA takes {string.Join(", ", LockAreas.All)}");
            l = new ShowLock(area, LockKind.Area, c.Session.ClientName, Services.Wall.UnixMs, string.Join(' ', a.Skip(2)));
        }
        else
        {
            if (ShowEntities.Find(p, a[0]) is not { } entity)
                return CommandReply.Fail($"no entity {a[0]} in the project — LOCK takes an id (GET shows it) or AREA <area>");
            l = new ShowLock(entity is IEntity e ? e.Id : a[0], LockKind.Entity, c.Session.ClientName, Services.Wall.UnixMs, string.Join(' ', a.Skip(1)));
        }
        if (p.Show.Locks.Any(x => x.Kind == l.Kind && x.Target == l.Target))
            return CommandReply.Fail($"{l.Target} is already locked");
        // A waiting blind commit is drawn on the air from its instant: a lock that would refuse it would show the audience a change
        // the document never takes, so it waits until the commit has landed.
        if (_blind?.Commit is { } waiting)
        {
            var locked = p with { Show = p.Show with { Locks = p.Show.Locks.Add(l) } };
            if (LockGuard.Check(locked, waiting.Patch.ApplyTo(locked), armed: false) is not null)
                return CommandReply.Fail($"a blind commit lands {l.Target} {When(waiting)} — LOCK it once the commit has landed, or BLIND DISCARD first");
        }
        var r = Store.ApplyUnrecorded($"lock {l.Target}", q => q with { Show = q.Show with { Locks = q.Show.Locks.Add(l) } }, KeepLocks);
        if (!r.Changed)
            return CommandReply.Fail(r.Effect);
        RaiseEvent("lock", LeaderNowNs, w =>
        {
            w.WriteString("target", l.Target);
            w.WriteString("kind", l.Kind == LockKind.Area ? "area" : "entity");
            w.WriteBoolean("on", true);
        });
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteString("target", l.Target);
            w.WriteString("kind", l.Kind == LockKind.Area ? "area" : "entity");
            w.WriteEndObject();
        }), $"locked {(l.Kind == LockKind.Area ? "area " : "")}{l.Target}");
    }

    private CommandReply Unlock(VerbCall c)
    {
        var a = c.Args;
        if (a.Count == 0)
            return CommandReply.Fail("UNLOCK <id> | UNLOCK AREA <area> | UNLOCK ALL");
        var locks = Store.Project.Show.Locks;
        ImmutableArray<ShowLock> lifted;
        if (a[0].Equals("ALL", StringComparison.OrdinalIgnoreCase) && a.Count == 1)
            lifted = locks;
        else if (a[0].Equals("AREA", StringComparison.OrdinalIgnoreCase) && a.Count == 2)
            lifted = [.. locks.Where(l => l.Kind == LockKind.Area && l.Target.Equals(a[1], StringComparison.OrdinalIgnoreCase))];
        else if (a.Count == 1)
            lifted = [.. locks.Where(l => l.Kind == LockKind.Entity && l.Target == a[0])];
        else
            return CommandReply.Fail("UNLOCK <id> | UNLOCK AREA <area> | UNLOCK ALL");
        if (lifted.IsEmpty)
            return CommandReply.Fail(locks.IsEmpty ? "nothing is locked" : $"{string.Join(' ', a)} is not locked");
        var r = Store.ApplyUnrecorded($"unlock {string.Join(", ", lifted.Select(l => l.Target))}",
            q => q with { Show = q.Show with { Locks = [.. q.Show.Locks.Where(l => !lifted.Contains(l))] } }, KeepLocks);
        if (!r.Changed)
            return CommandReply.Fail(r.Effect);
        RaiseEvent("lock", LeaderNowNs, w =>
        {
            w.WriteStartArray("targets");
            foreach (var l in lifted)
                w.WriteStringValue(l.Target);
            w.WriteEndArray();
            w.WriteBoolean("on", false);
        });
        return CommandReply.Success(JsonOut(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("unlocked");
            foreach (var l in lifted)
                w.WriteStringValue(l.Target);
            w.WriteEndArray();
            w.WriteEndObject();
        }), $"unlocked {string.Join(", ", lifted.Select(l => l.Target))}");
    }

    /// <summary>An older root (kept for undo or redo) with the locks the newest root holds, so moving through history never
    /// moves a lock.</summary>
    private static Project KeepLocks(Project older, Project now) =>
        older.Show.Locks == now.Show.Locks ? older : older with { Show = older.Show with { Locks = now.Show.Locks } };

    /// <summary>Every lock, then the areas an armed show adds (implicit), from published state.</summary>
    internal void WriteLocks(Utf8JsonWriter w)
    {
        var locks = Store.Project.Show.Locks;
        w.WriteStartArray();
        foreach (var l in locks)
            WriteLock(w, l.Target, l.Kind == LockKind.Area ? "area" : "entity", l.By, l.AtUnixMs, l.Words, false);
        var mode = _showMode;
        if (mode.Armed)
        {
            foreach (var area in LockGuard.ArmedAreas)
            {
                if (!locks.Any(l => l.Kind == LockKind.Area && l.Target == area))
                    WriteLock(w, area, "area", mode.By, mode.SinceUnixMs, "", true);
            }
        }
        w.WriteEndArray();
    }

    private static void WriteLock(Utf8JsonWriter w, string target, string kind, string by, long at, string words, bool @implicit)
    {
        w.WriteStartObject();
        w.WriteString("target", target);
        w.WriteString("kind", kind);
        w.WriteString("by", by);
        w.WriteNumber("at", at);
        w.WriteString("words", words);
        w.WriteBoolean("implicit", @implicit);
        w.WriteEndObject();
    }
}
