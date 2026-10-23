using System.Reflection;

namespace PlayMap.Tests;

/// <summary>What a lock's first wait makes on the thread that waits. The calling thread holds the lock while a thread whose own wait
/// record is already made waits a moment on it, inside an allocation window of that thread's: a lock whose wait event is made reads
/// 0, one whose event the wait has to make reads its bytes (56, or more after a collection dropped the runtime's cache of the event's
/// handle type).</summary>
internal static class LockWaitProbe
{
    /// <summary>The bytes the first wait on <paramref name="gate"/> made on the waiting thread.</summary>
    public static long FirstWait(Lock gate)
    {
        long allocated = -1;
        gate.Enter();
        try
        {
            var waiter = new Thread(() =>
            {
                // The thread's own wait record, made before the window: only the lock's part is counted.
                using (var once = new AutoResetEvent(false))
                    once.WaitOne(0);
                var before = Allocations.Start();
                if (gate.TryEnter(1))
                    gate.Exit();
                allocated = Allocations.Since(before);
            })
            { IsBackground = true, Name = "lock probe" };
            waiter.Start();
            waiter.Join();
        }
        finally
        {
            gate.Exit();
        }
        return allocated;
    }

    /// <summary>Every <see cref="Lock"/> field of <paramref name="owner"/>'s type and its bases (and the type's static ones when
    /// <paramref name="statics"/>), each named with its type and what its first wait made; at least one lock, or the call throws.</summary>
    public static IReadOnlyList<string> FirstWaits(object owner, bool statics = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var seen = new List<string>();
        for (var type = owner.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            if (statics)
                flags |= BindingFlags.Static;
            foreach (var field in type.GetFields(flags))
            {
                if (field.FieldType != typeof(Lock))
                    continue;
                var gate = (Lock)field.GetValue(field.IsStatic ? null : owner)!;
                seen.Add($"{type.Name}.{field.Name} {FirstWait(gate)} B");
            }
        }
        if (seen.Count == 0)
            throw new ArgumentException($"{owner.GetType().Name} holds no lock", nameof(owner));
        return seen;
    }

    /// <summary>The probes of <see cref="FirstWaits"/> that made anything (empty when every lock's first wait made nothing).</summary>
    public static IReadOnlyList<string> Unprepared(object owner, bool statics = false) =>
        [.. FirstWaits(owner, statics).Where(p => !p.EndsWith(" 0 B", StringComparison.Ordinal))];
}
