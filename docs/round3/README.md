# Round 3: the Windows-native node

The record behind PlayMap's third round, which will make PlayMap an installed, Windows-only show node on every edition of Windows 11,
Home included. It will render on Direct3D 12 behind a rendering hardware interface cut at today's `GlDevice`, present each output
through its own flip-model swap chain with the evidence for every claim, lock outputs across machines where the hardware locks — the
present barrier on Direct3D 12, the swap group on OpenGL — and report the software lock's measured phase where it does not, claim what
a real-time engine needs from Windows, prove what the audience saw with an independent observer, rehearse losing the GPU, and install
per machine, unsigned this round, with a version every installer can order. Its desk will open on guided workflows — two projectors
blended or stacked on a flat screen, a film on a building seen from the audience, an object mapped with one or two projectors — with
every mapping step doable by hand and the assistant in every step, and capture cards will pass through to the air. OpenGL will stay the
reference backend through the round and keep the Linux development loop. None of this is built yet: today PlayMap is the portable exe
`docs/ARCHITECTURE.md` describes. The owner answered the round's decisions on 18 October 2026; `PLAN.md` §1.2 and §9 record each
answer, and none stays open.

| What | Where |
|---|---|
| The plan of record: the goal and the decisions, the version rules, what each CI job proves, the questions measured first, done means, the ownership rules, the spine, the fifteen packages of the Windows move and the five for the owner's requirements with their slices, cut lines, hunks and acceptance tests, the shared files, the waves and the cloud split, the merge order and the gate, the risks, the lab checklist, the coverage of the direction and of each of the owner's sentences, and the decisions answered | `PLAN.md` |
| The designs that go beyond the plan, each with its interfaces, threading, facts in STATE, verbs and tests | `design/` — `rhi.md` (the RHI and the GL backend behind it), `d3d12.md` (the Direct3D 12 device), `graph.md` (the render graph, pipelines at arm, bindless records), `shaders.md` (HLSL through DXC, the shader worker for user ISF, quarantine, user effects on the device), `present.md` (flip presenters, the predicted display time, the present barrier, the GL swap group and the honest software lock), `platform.md` (the Windows platform seam, the node's identity and capabilities), `desk-gpu.md` (shared-texture previews), `stability.md` (the observer, the heartbeat, `LATENCY STEP`, the rebuild path), `installer.md` (the MSI from the WiX toolset built from its source, the supervisor, the version rules, unsigned builds, the editions), `desk-workflows.md` (the guides, every map by hand, the assistant in every step, the quad, the handles, what each screen shows, one canvas across outputs, menus, panes and colour), `capture.md` (capture cards passed through to the air) |
| The research: the direction note that the plan follows, the six notes it synthesises (graphics API, framelock and presentation, vendor features, the Windows platform and packaging, gaming and medical architecture, the desk's UI and UX) and the map of what the move touches in the tree; for the owner's answers and requirements, Windows 11 Home, an installer without a paid service, capture cards, the workflows and the desk, and two readings of the tree at `6f24310` (the desk; the video input path) | `research/` — `windows-gpu-direction.md`, `codebase-map.md` and the six notes; `windows11_home.md`, `installer_tools.md`, `capture_cards.md`, `workflows_ux.md`, `desk-today.md`, `video-today.md` |

The round will build on `main`, which carries one version source (`Version.props`, 0.2.x): every build is stamped with its
version, build number and commit, named on the desk, in HEALTH and in the engine's log, and the CI artifact is named with it;
the plan adds the installer's version rules on top of it.

Lab results (the checklist of `PLAN.md` §7, each with its machine, driver and build) will be kept in `lab/` as they are measured. No
lab exists yet (the owner's answer), so every line is reported not run until one does.

This round is the third build round, after `docs/round2/`. It is numbered apart from the prioritised rounds of
`docs/ROADMAP.md`, whose own "Round 3" is "Plays with others".
