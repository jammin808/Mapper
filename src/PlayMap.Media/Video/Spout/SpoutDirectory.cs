namespace PlayMap.Media.Video.Spout;

/// <summary>A sender the list names, with its info (null when its info map is gone: a sender that left without unregistering).</summary>
public sealed record SpoutSenderSeen(string Name, SpoutInfo? Info, bool Active);

/// <summary>Spout's discovery, as receivers see it: the sender list read under its mutex (Spout's 67 ms wait — on the discovery thread,
/// never the frame path), each sender's info map read, and the active sender. Pure over the kernel; the provider's thread publishes
/// what it reads as an immutable snapshot.</summary>
public static class SpoutDirectory
{
    /// <summary>The senders now, in the list's order; empty when there is no list (no Spout app has run) or it is held too long.</summary>
    public static IReadOnlyList<SpoutSenderSeen> Read(ISpoutKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        using var list = kernel.OpenMap(SpoutNames.List);
        if (list is null)
            return [];
        List<string> names;
        using (var listLock = kernel.OpenMutex(SpoutNames.MutexOf(SpoutNames.List)))
        {
            var taken = listLock?.TryEnter(SpoutNames.ListWaitMs) ?? LockTry.Acquired;
            if (taken == LockTry.Busy)
                return [];
            try
            {
                names = SpoutNames.Read(list.Bytes);
            }
            finally
            {
                if (listLock is not null)
                    listLock.Exit();
            }
        }
        string active = "";
        using (var a = kernel.OpenMap(SpoutNames.Active))
        {
            if (a is not null && SpoutNames.Read(a.Bytes) is [var first])
                active = first;
        }
        var seen = new List<SpoutSenderSeen>(names.Count);
        foreach (var name in names)
        {
            using var map = kernel.OpenMap(name);
            SpoutInfo? info = map is null ? null : SpoutInfo.Read(map.Bytes);
            seen.Add(new SpoutSenderSeen(name, info is { ShareHandle: not 0 } ? info : null, name == active));
        }
        return seen;
    }
}
