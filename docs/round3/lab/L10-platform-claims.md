# L10 — the platform's claims (wp3-platform)

**Status: not run** (no lab this round, D15). Until it runs, every default whose cause is not documented stays off: the GPU
scheduling class is NORMAL, CPU sets are AUTO (never ONE-CACHE), and nothing is claimed of the GC.

**Machines.** An NVIDIA RTX PRO node, a Radeon RDNA 3 or 4 node, an Intel Arc node, a hybrid Intel CPU (performance and efficiency
cores), a dual-CCD X3D CPU. Each on Windows 11 24H2 or later, the build under test installed by its installer, outputs on real
displays.

**Pass mark.** With each claim on, p99.9 frame time at or below the same show with that claim off; each default confirmed or changed
in one commit that cites this record; the high-resolution timer's lateness at or below today's wait.

## Script

1. Load the reference show (two outputs at their native refresh, a HAP clip and an H.264 clip, one ISF effect). `MACHINE` and
   `PREFLIGHT QUICK`: keep both answers with the record (every claim's answer, the identity, the machine rows).
2. Baseline: every claim at its default. Run 20 minutes; record `OUTPUT STATS` for each output (p50, p99, p99.9 frame time, missed
   vblanks) every minute, and STATE `machine.platform.timer` at the end.
3. For each MACHINE SET word, one at a time, from the default to its other value, restart the engine where the word applies at the
   next start (TIMER, MMCSS, CPUSETS), run 20 minutes, record as in 2:
   - `QOS AUTO`, then with the desk's window covering every output and minimised (the case HighQoS is for);
   - `PROCESS NORMAL`; `MMCSS OFF`; `CPUSETS OFF`; `TIMER DEFAULT`;
   - `GPU-PRIORITY HIGH`; on the RTX PRO node with the privilege granted by the installer, `GPU-PRIORITY REALTIME`.
4. Background load during every run: a 4K H.264 export in another process at normal priority, and a file copy to the media volume.
5. On the hybrid Intel CPU: the critical threads' processors from ETW (`Microsoft-Windows-Kernel-Process` context switches) — the
   render, present and decode threads on the performance cores with `CPUSETS AUTO`, anywhere with `OFF`.
6. On the dual-CCD X3D CPU: the same with `MACHINE SET CPUSETS ONE-CACHE` (the critical pool confined to the highest efficiency
   class's first last-level cache; confirm from `MACHINE` that it is the cache CCD), recorded beside AUTO.
7. The timer: `machine.platform.timer.lateP99Us` with `TIMER HIGH` and `DEFAULT`, beside CI's numbers (PLATFORM.md, Time).

## Record

One table per machine: claim, value, p50 / p99 / p99.9 frame time (ms), missed vblanks per hour, timer p99 (µs), the claim's answer
from `MACHINE`. The commit that changes a default links this record.
