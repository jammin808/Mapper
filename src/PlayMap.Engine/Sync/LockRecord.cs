using System.Text.Json;
using PlayMap.Core.Sync;
using PlayMap.Net;

namespace PlayMap.Engine.Sync;

/// <summary>The group's frame lock as its leader carries it to the members on the anchor feed
/// (<c>ANCHOR &lt;group&gt; @lock {…}</c>): whether the lock is applied, its swap group and barrier, and the leader's counter anchor
/// (the board count ↔ show time pair every member maps counts with), when it has one.</summary>
public readonly record struct LockRecord(bool Apply, uint SwapGroup, uint Barrier, LockAnchor? Counter)
{
    /// <summary>The feed's key for the record, in place of a playlist id.</summary>
    public const string Key = "@lock";

    public string ToJson()
    {
        var (apply, group, barrier, counter) = (Apply, SwapGroup, Barrier, Counter);
        return JsonText.Write(w => Write(w, apply, group, barrier, counter));
    }

    private static void Write(Utf8JsonWriter w, bool apply, uint group, uint barrier, LockAnchor? counter)
    {
        w.WriteStartObject();
        w.WriteBoolean("apply", apply);
        w.WriteNumber("swapGroup", group);
        w.WriteNumber("barrier", barrier);
        if (counter is { } c)
        {
            w.WriteStartObject("counter");
            w.WriteNumber("count24", c.Count24);
            w.WriteNumber("leaderNs", c.LeaderNs);
            w.WriteNumber("periodPs", c.PeriodPs);
            w.WriteNumber("effectiveAt24", c.EffectiveAt24);
            w.WriteNumber("seq", c.Seq);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("counter");
        }
        w.WriteEndObject();
    }

    /// <summary>Reads a record; false for anything that is not one whole (fail closed: a member never guesses at a lock).</summary>
    public static bool TryParse(string json, out LockRecord record)
    {
        record = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("apply", out var apply) || apply.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !r.TryGetProperty("swapGroup", out var g) || !g.TryGetUInt32(out var group)
                || !r.TryGetProperty("barrier", out var b) || !b.TryGetUInt32(out var barrier)
                || !r.TryGetProperty("counter", out var c))
                return false;
            LockAnchor? counter = null;
            if (c.ValueKind == JsonValueKind.Object)
            {
                if (!c.TryGetProperty("count24", out var n) || !n.TryGetUInt32(out var count) || count > 0xFFFFFF
                    || !c.TryGetProperty("leaderNs", out var l) || !l.TryGetInt64(out var leaderNs)
                    || !c.TryGetProperty("periodPs", out var p) || !p.TryGetInt64(out var period) || period <= 0
                    || !c.TryGetProperty("effectiveAt24", out var e) || !e.TryGetUInt32(out var effective) || effective > 0xFFFFFF
                    || !c.TryGetProperty("seq", out var s) || !s.TryGetInt64(out var seq))
                    return false;
                counter = new LockAnchor(count, leaderNs, period, effective, seq);
            }
            else if (c.ValueKind != JsonValueKind.Null)
            {
                return false;
            }
            record = new LockRecord(apply.GetBoolean(), group, barrier, counter);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not JSON, or a field of another kind (a string where a number belongs): not a record.
            return false;
        }
    }
}
