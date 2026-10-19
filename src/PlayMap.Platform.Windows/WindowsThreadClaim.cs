using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using PlayMap.Core.Machine;
using PlayMap.Platform.Windows.Interop;

namespace PlayMap.Platform.Windows;

/// <summary>One thread's claims by role, made on the thread itself: its priority, MMCSS's task (render and present "Games", audio and
/// timecode "Pro Audio", capture "Capture"), its CPU sets and, for background pools, EcoQoS. Each claim answers what it asked, what
/// Windows said and what reading it back shows. The thread is held by a real handle, so disposing reverts every claim on that thread
/// whichever thread disposes it: the MMCSS task is left, the CPU sets cleared, the throttling handed back to Windows and the priority
/// put back as it was.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsThreadClaim : IThreadClaim
{
    private readonly nint _thread;
    private readonly int _priorityBefore;
    private readonly bool _cpuSets;
    private readonly bool _throttled;
    private nint _avrt;
    private int _disposed;

    private WindowsThreadClaim(nint thread, int priorityBefore, nint avrt, bool cpuSets, bool throttled, ImmutableArray<Claim> claims)
    {
        _thread = thread;
        _priorityBefore = priorityBefore;
        _avrt = avrt;
        _cpuSets = cpuSets;
        _throttled = throttled;
        Claims = claims;
    }

    public ImmutableArray<Claim> Claims { get; }

    /// <summary>Claims the calling thread's role as <paramref name="claims"/> says, placed as <paramref name="placement"/> says.</summary>
    public static IThreadClaim Claim(RoleClaims claims, CpuPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(placement);
        var role = ThreadClaims.Word(claims.Role);
        var process = Native.GetCurrentProcess();
        if (!Native.DuplicateHandle(process, Native.GetCurrentThread(), process, out var thread, 0, false, Native.DuplicateSameAccess))
            return new UnclaimedThread(claims.Role, "the thread's handle could not be taken (" + Native.LastErrorWords() + "): nothing was claimed");
        var list = ImmutableArray.CreateBuilder<Claim>();
        var before = Native.GetThreadPriority(thread);
        var asked = PlatformPolicy.Word(claims.Priority);
        list.Add(Native.SetThreadPriority(thread, (int)claims.Priority)
            ? new Claim($"thread.{role}.priority", asked, true, PriorityWord(Native.GetThreadPriority(thread)), "")
            : new Claim($"thread.{role}.priority", asked, false, PriorityWord(Native.GetThreadPriority(thread)), "Windows refused the priority: " + Native.LastErrorWords()));

        nint avrt = 0;
        if (claims.MmcssTask is { } task)
        {
            uint index = 0;
            avrt = Native.AvSetMmThreadCharacteristicsW(task, ref index);
            if (avrt == 0)
            {
                list.Add(new Claim($"thread.{role}.mmcss", task, false, "",
                    "MMCSS did not take the thread (" + Native.LastErrorWords() + "): the Multimedia Class Scheduler service may be stopped"));
            }
            else
            {
                var high = Native.AvSetMmThreadPriority(avrt, Native.AvrtPriorityHigh);
                list.Add(new Claim($"thread.{role}.mmcss", task, true, string.Create(CultureInfo.InvariantCulture, $"task index {index}"),
                    high ? "" : "the task's priority stayed its own: " + Native.LastErrorWords()));
            }
        }

        var ids = placement.Of(claims.Pool);
        var cpuSets = false;
        if (claims.Pool != CpuPool.None)
        {
            if (ids.IsEmpty)
            {
                list.Add(new Claim($"thread.{role}.cpusets", PlatformPolicy.Word(claims.Pool), true, "none", placement.Words));
            }
            else
            {
                var array = ids.ToArray();
                fixed (uint* p = array)
                    cpuSets = Native.SetThreadSelectedCpuSets(thread, p, (uint)array.Length);
                list.Add(cpuSets
                    ? new Claim($"thread.{role}.cpusets", PlatformPolicy.Word(claims.Pool), true, ReadCpuSets(thread), "")
                    : new Claim($"thread.{role}.cpusets", PlatformPolicy.Word(claims.Pool), false, "", "Windows refused the CPU sets: " + Native.LastErrorWords()));
            }
        }

        var throttled = false;
        if (claims.EcoQos)
        {
            var state = new PowerThrottlingState(Native.PowerThrottlingCurrentVersion, Native.ExecutionSpeed, Native.ExecutionSpeed);
            throttled = Native.SetThreadInformation(thread, Native.ThreadPowerThrottling, &state, (uint)sizeof(PowerThrottlingState));
            list.Add(throttled
                ? new Claim($"thread.{role}.qos", "eco", true, ReadThrottling(thread), "")
                : new Claim($"thread.{role}.qos", "eco", false, "", "Windows refused EcoQoS: " + Native.LastErrorWords()));
        }
        return new WindowsThreadClaim(thread, before, avrt, cpuSets, throttled, list.ToImmutable());
    }

    private static string PriorityWord(int priority) => priority switch
    {
        0x7FFFFFFF => "unread",
        -1 => "belowNormal",
        0 => "normal",
        1 => "aboveNormal",
        2 => "highest",
        15 => "timeCritical",
        -2 => "lowest",
        -15 => "idle",
        _ => priority.ToString(CultureInfo.InvariantCulture),
    };

    private static string ReadCpuSets(nint thread)
    {
        var ids = stackalloc uint[256];
        return Native.GetThreadSelectedCpuSets(thread, ids, 256, out var count)
            ? string.Create(CultureInfo.InvariantCulture, $"{count} CPU sets")
            : "unread (" + Native.LastErrorWords() + ")";
    }

    private static string ReadThrottling(nint thread)
    {
        PowerThrottlingState state = new(Native.PowerThrottlingCurrentVersion, 0, 0);
        if (!Native.GetThreadInformation(thread, Native.ThreadPowerThrottling, &state, (uint)sizeof(PowerThrottlingState)))
            return "unread (" + Native.LastErrorWords() + ")";
        return (state.ControlMask & Native.ExecutionSpeed) == 0 ? "auto" : (state.StateMask & Native.ExecutionSpeed) != 0 ? "eco" : "high";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_avrt != 0)
            _ = Native.AvRevertMmThreadCharacteristics(_avrt);
        _avrt = 0;
        if (_cpuSets)
            _ = Native.SetThreadSelectedCpuSets(_thread, null, 0);
        if (_throttled)
        {
            var state = new PowerThrottlingState(Native.PowerThrottlingCurrentVersion, 0, 0);
            _ = Native.SetThreadInformation(_thread, Native.ThreadPowerThrottling, &state, (uint)sizeof(PowerThrottlingState));
        }
        if (_priorityBefore != 0x7FFFFFFF)
            _ = Native.SetThreadPriority(_thread, _priorityBefore);
        _ = Native.CloseHandle(_thread);
    }
}
