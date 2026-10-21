# L22 — a precise Windows thread time (wp3-platform)

**Status: not run** (no lab this round, D15). Until it passes, a Windows thread time is its ticks (`GetThreadTimes`), and a span's
budget there is proven by the stopwatch alone.

**Machine.** The RTX PRO node with Memory Integrity on (Windows as Hyper-V's root partition), then off.

**Pass mark.** A spinning thread's cycles (`QueryThreadCycleTime`) at the time-stamp counter's frequency from the platform within 1 %
of its running time from ETW's context-switch, interrupt and DPC events, idle and beside a network and disk interrupt load; its ticks
(`GetThreadTimes`) under the node's own timer claims (QoS HIGH with `IGNORE_TIMER_RESOLUTION`, the high-resolution timer) still in
the step `ThreadTime.ResolutionNs` states.

## Script

1. Start an ETW session with context switches, interrupts and DPCs (`xperf -on PROC_THREAD+CSWITCH+INTERRUPT+DPC`).
2. A test build's probe thread spins for 10 s, reading `QueryThreadCycleTime` and `GetThreadTimes` every 100 ms; the time-stamp
   counter's frequency as the platform reports it (with Memory Integrity on, the hypervisor's reference frequency), never a rate
   measured against the stopwatch.
3. Repeat beside a network load (iperf3 at line rate) and a disk load (diskspd random 4K reads), Memory Integrity on, then off.
4. From the trace, the probe thread's running time (context switches in and out, less interrupt and DPC time charged while it ran).
5. Compare: cycles ÷ frequency against the trace's running time; the ticks' step against `ThreadTime.ResolutionNs`.

## Record

Per run: cycles-derived time, ETW running time, their ratio, the ticks' step; Memory Integrity's state; the load.
