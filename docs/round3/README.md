# Round 3: the Windows-native node

The record behind PlayMap's third round, which will make PlayMap an installed, Windows-only show node on every edition of Windows
11, Home included. It will render on Direct3D 12 behind the rendering hardware interface, cut at `GlDevice`, that every frame draws
through today with OpenGL behind it, present each output through its own flip-model swap chain with the evidence for every claim,
lock outputs across machines where the hardware locks — the present barrier on Direct3D 12, the swap group on OpenGL — and report
the software lock's measured phase where it does not, claim what a real-time engine needs from Windows, prove what the audience saw
with an independent observer, rehearse losing the GPU, and install per machine, unsigned for now, with a version every installer
can order. Inno Setup will build this round's installer, with no paid WiX service, and an MSI is revisited next round; the
installer will carry the Direct3D 12 runtime under a licence page on Microsoft's terms for shipping it, and each build will be
named as unsigned, with a SHA-256 list. A supervising service will start the engine in the show user's session, never as
administrator or in session 0. OpenGL will stay the reference backend through the round and keep the Linux development loop.

PlayMap's desk will open on guided workflows — two projectors blended or stacked on a flat screen, a film mapped onto a wall so it
reads undistorted from the audience, an object mapped with one or two projectors — with every mapping step doable by hand and the
assistant in every step, and capture cards will pass through to the air. Advanced panes and a typed console will sit beside the
guides, and every command and feature will be in the menu bar and the right-click menus. Panes will dock, float and collapse, and
colour will mark status, identity and workflow stage, never as the only cue. The four-click quad will stay when a person clicks
away, and every corner, pin and handle will be dragged, typed and nudged by one pixel. Each screen will say what it shows, and one
pattern, media or generator will span several outputs as one canvas.

The round's spine is built: the catalogue's rows, Core's contracts, the engine's seams, the model's fields, the Direct3D 12,
Windows platform and supervisor projects as placeholders, and the Direct3D 12 CI leg on WARP (`docs/ROADMAP.md`, Round 3). Of the
packages that fill it, the rendering hardware interface (`wp3-rhi`) is built: every frame draws through Core's `IGpuDevice` with the
GL backend behind it, its pixel goldens unedited and holding within their tolerances, and `notes/rhi.md` holds its contracts as
built. So is `wp3-shaders`: PlayMap's programs in HLSL, compiled to signed DXIL on CI and held to the program table by
reflection, a user's ISF effect compiled to DXIL in the shader worker, and the node's quarantine with `FX RELEASE`; no device draws
that DXIL yet, and its papers note is the last section of `docs/wire/shaders.md`. The other packages are not built yet: today
PlayMap is the portable exe `docs/ARCHITECTURE.md` describes, drawing through that interface with OpenGL behind it.
`PLAN.md` §1.2 and §9 hold the owner's answer to each of the round's decisions, and none is open.

| What | Where |
|---|---|
| The plan of record: the goal and the decisions, the version rules, what each CI job proves, the questions measured first, done means, the ownership rules, the spine, the fifteen packages of the Windows move and the five for the owner's requirements with their slices, cut lines, hunks and acceptance tests, the shared files, the waves and the cloud split, the merge order and the gate, the risks, the lab checklist, the coverage of the direction and of each of the owner's sentences, and the decisions answered | `PLAN.md` |
| The designs that go beyond the plan, each with its interfaces, threading, facts in STATE, verbs and tests | `design/` — `rhi.md` (the RHI and the GL backend behind it), `d3d12.md` (the Direct3D 12 device), `graph.md` (the render graph, pipelines at arm, bindless records), `shaders.md` (HLSL through DXC, the shader worker for user ISF, quarantine, user effects on the device), `present.md` (flip presenters, the predicted display time, the present barrier, the GL swap group and the honest software lock), `platform.md` (the Windows platform seam, the node's identity and capabilities), `desk-gpu.md` (shared-texture previews), `stability.md` (the observer, the heartbeat, `LATENCY STEP`, the rebuild path), `installer.md` (this round's installer from Inno Setup and the MSI kept for the next, the supervisor, the version rules, unsigned builds, the editions), `desk-workflows.md` (the guides, every map by hand, the assistant in every step, the quad, the handles, what each screen shows, one canvas across outputs, menus, panes and colour), `capture.md` (capture cards passed through to the air) |
| A merged package's papers note: its contracts as built where they differ from its design, where the code proves a design wrong, its hunks outside its Owns and what waits for the Windows job and the lab | `notes/` — `rhi.md`; wp3-shaders' is its wire page's "Papers note" (`docs/wire/shaders.md`) |
| The research: the direction note that the plan follows, the six notes it synthesises (graphics API, framelock and presentation, vendor features, the Windows platform and packaging, gaming and medical architecture, the desk's UI and UX) and the map of what the move touches in the tree; for the owner's answers and requirements, Windows 11 Home, an installer without a paid service, capture cards, the workflows and the desk, and two readings of the tree at `6f24310` (the desk; the video input path) | `research/` — `windows-gpu-direction.md`, `codebase-map.md` and the six notes; `windows11_home.md`, `installer_tools.md`, `capture_cards.md`, `workflows_ux.md`, `desk-today.md`, `video-today.md` |

The round builds on `main`, which carries one version source (`Version.props`, 0.2.x): every build is stamped with its
version, build number and commit, named on the desk, in HEALTH and in the engine's log, and the CI artifact is named with it;
the plan adds the installer's version rules on top of it.

Lab results (the checklist of `PLAN.md` §7, each with its machine, driver and build) will be kept in `lab/` as they are measured. No
lab exists yet (the owner's answer), so every line is reported not run until one does.

This round is the third build round, after `docs/round2/`. It is numbered apart from the prioritised rounds of
`docs/ROADMAP.md`, whose own "Round 3" is "Plays with others".
