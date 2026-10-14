# Round 2: research, designs and the plan

The record behind PlayMap's second round of professional features: smooth Windows output and framelock, the GPU codecs
(HAP, HAP R, HAP HDR, zero-copy decode, optimise on import), video and control connections (Spout, OMT, NDI, OSC, MIDI,
Art-Net, timecode, a Companion 5.1 module), show safety (Show Mode, blind edit, panic, preflight, redundancy, fleet health
and the Patterns Eye), creative tools (ISF effects, audio reactivity, LED pixel mapping, particles, fractals, reactive
scenes), the real jobs (facades, stacks), auto-align against the projectors' own cameras, local phone-scan mapping, the
first five minutes, and calibration you can explain, keep and repair.

| What | Where |
|---|---|
| The build plan: packages, waves, cut lines, acceptance tests, the machine rules, the coverage of every requested line | `PLAN.md` |
| The shared code every package builds on (model, verb catalogue, gate, seams, STATE, the Eye contract) | `SPINE.md` |
| One design per feature: the bar each competitor sets and how PlayMap meets or beats it, scope, architecture, verification | `design/` |
| The research behind the designs, every fact with its source and whether it was opened | `research/` |
| Gaps carried over from the first integration, each owned by a package | `CARRYOVER.md` |

Decisions taken for this round: the full plan; ISF effects now and the FFGL host next round; the Patterns-side Eye patch is
prepared by the fleet package for Patterns' maintainers and not applied; AI proposals wait on PlayMap's own approval list
(Stagewatch forwarding stays off). PlayMap's own code names no licence; the Companion module's licence (its manifest requires
one) is the owner's decision before that package is built. Third-party notices are kept in `THIRD-PARTY-NOTICES.md`.

Prototype measurements the designs mention (benches under `bench/`) were run while designing and are not kept here.
