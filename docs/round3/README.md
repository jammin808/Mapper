# Round 3: the Windows-native node

The record behind PlayMap's third round: an installed, Windows-only show node that renders on Direct3D 12 behind a rendering
hardware interface cut at today's `GlDevice`, presents each output through its own flip-model swap chain with the evidence for
every claim, locks outputs across machines where RTX PRO Sync exists and reports measured phase where it does not, claims what a
real-time engine needs from Windows, proves what the audience saw with an independent observer, rehearses losing the GPU, and
installs per machine with a version every installer can order. OpenGL stays the reference backend through the round and keeps the
Linux development loop.

| What | Where |
|---|---|
| The plan of record: the goal and the decisions, the version rules, what each CI job proves, the questions measured first, done means, the ownership rules, the fifteen packages with their slices, cut lines, hunks and acceptance tests, the shared files, the waves and the cloud split, the merge order and the gate, the risks, the lab checklist, the coverage of the direction and the open decisions | `PLAN.md` |
| The designs that go beyond the plan, each with its interfaces, threading, facts in STATE, verbs and tests | `design/` — `rhi.md` (the RHI and the GL backend behind it), `d3d12.md` (the Direct3D 12 device), `graph.md` (the render graph, pipelines at arm, bindless records), `shaders.md` (HLSL through DXC, the shader worker for user ISF, quarantine), `present.md` (flip presenters, the predicted display time, the present barrier and the honest software lock), `platform.md` (the Windows platform seam, the node's identity and capabilities), `desk-gpu.md` (shared-texture previews), `stability.md` (the observer, the heartbeat, `LATENCY STEP`, the rebuild path), `installer.md` (the MSI, the supervisor, the version rules) |
| The research: the direction note that the plan follows, the six notes it synthesises (graphics API, framelock and presentation, vendor features, the Windows platform and packaging, gaming and medical architecture, the desk's UI and UX) and the map of what the move touches in the tree | `research/` — `windows-gpu-direction.md`, `codebase-map.md` and the six notes |

The base the round builds on is `main` with the `versioning` branch merged (one version source, 0.2.x, stamped into the exe,
shown by the desk and STATE, the CI artifact named with it); the plan adds the installer's version rules on top of it.

Lab results (the checklist of `PLAN.md` §7, each with its machine, driver and build) will be kept in `lab/` as they are measured.

This round is the third build round, after `docs/round2/`. It is numbered apart from the prioritised rounds of
`docs/ROADMAP.md`, whose own "Round 3" is "Plays with others".
