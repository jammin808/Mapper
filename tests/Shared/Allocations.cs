using System.Runtime.CompilerServices;

namespace PlayMap.Tests;

/// <summary>
/// How every suite measures what code allocates, on the measuring thread or on another one (a render thread's frame path, a decoder's
/// threads). <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts what a thread has used of its allocation contexts; when a
/// background collection ends its mark it voids every thread's context, and the unused rest of it (up to 8 KB) then reads as allocated
/// by that thread, though nothing was. A gen0 collection first hands every thread's context back with its rest uncounted, so a window
/// begun after one reads 0 on any thread that allocates nothing in it, whenever a background collection ends.
/// </summary>
internal static class Allocations
{
    /// <summary>Begins a window on every thread (a gen0 collection: no thread holds an allocation context as it opens); a counter
    /// another thread keeps is read after it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Begin() => GC.Collect(0, GCCollectionMode.Forced, blocking: true);

    /// <summary>Begins a window and answers this thread's count at its start, for <see cref="Since"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Start()
    {
        Begin();
        return GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>The bytes this thread has allocated since <paramref name="start"/> (a count from <see cref="Start"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Since(long start) => GC.GetAllocatedBytesForCurrentThread() - start;
}
