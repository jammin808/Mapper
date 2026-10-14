# PlayMap round 2: the plan

*How the round is built: a spine in two sessions, then twenty-eight packages in three waves. Each package is one implementation
session followed by one review-and-fix session. `SPINE.md` is the contract every package builds against (model, catalogue rows,
seams, hooks, settings, STATE, context, Show Bus and Eye). This file says who builds what, in which order, how each is accepted,
and what waits for the next round. Read against `main` at `7a01783` (13 Oct 2026) and the 22 designs in `r2/design/`.*

*This is the second cut. A review against the request and against this machine found three blockers in what round 2 would
ship (curved multi-projector phone scans, framelock, the Companion module's depth) and three in how it would be built (risk
rewrite, vendored OMT under the fences, the air path's ownership), plus majors and minors. §10 lists every finding this cut
does not take as written, and why.*

---

## 0. The numbers, and the honest consequence

The 22 designs ask for about **1 730 agent-hours** of "Must" work (their own §10 figures; the desk's alone is 103). The first cut
gave round 2 eighteen sessions, about 470 design-hours. The review showed what that left out: a phone scan that sees one view of
one projector; framelock that was all stretch; a Companion module of 9 hours against a 100-hour design; panic, preflight, backup
and blind edit each short of the competitor they were measured against; and nothing above a cut line for zero-copy decode or
Spout.

This cut has **30 implementation sessions**: the spine in two (W0a, W0b) and 28 packages. Nine packages are **continuations**:
a second slice of an earlier package, built one wave later on its merged code, owning the same files (§2.2). Above their cut
lines the packages sum to about **700 design-hours**, about 40 % of what was designed. Each package still ships a **vertical
slice**: the thinnest piece that works end to end (Core → engine → wire → STATE → context → MCP → INDEX → a card on the desk), is
tested here, and leaves the next round a clean place to continue. Everything else is named under "Next round" in its package,
so nothing is dropped silently. The coverage table (§8) says, for every line of the request, the bar, what round 2 reaches, and
**which test proves it**; a row with no test says "Below bar this round".

Where round 2 cannot verify something on this machine (no Windows, no GPU, no audio device, no camera, no projector, no phone),
the code is **compile-only**, written to the API facts the research verified, behind a seam with a tested fake. Compile-only code
is never the only path: each has a fallback that runs here and says why it is in use. Windows code also runs on the repository's
existing `windows-latest` CI job (WARP needs no GPU, §5.2): "compile-only here" no longer means "never executed".

---

## 1. The capacity rule

| Rule | What it means |
|---|---|
| **One session, one slice** | A package is built by one implementation session, then one review-and-fix session (fixes only, no new scope). |
| **The budget** | Above its cut line (⟂), a slice is about 24–26 of its spec's own §10 hours (seven sit at 27–29 and are named in risk R1), after removing the spec's papers lines and what the spine already lands (model, ids, catalogue rows, STATE plumbing, stubs: 3–8 h per spec). Below the line, stretch items total ≤ ~8 h. Continuations may be lighter. |
| **Order is the contract** | Slice items are built in the listed order. The must above ⟂ merges on its own. A stretch item starts only when the must is green; an unfinished stretch item is not merged half-built — it moves to "Next round" and its catalogue rows are trimmed. |
| **Done means** | Merged to `main` green with analyzers on; its acceptance tests pass here (or compile-only as stated, with a Windows-category test on the CI job for Windows code); its fixture `tests/fixtures/state/<pkg>.json` is written by one of its own tests, **with a card descriptor `<pkg>.cards.json` for each new STATE block** (the desk renders it, §3.23); its catalogue block holds only rows it routes (§2.2); **it registers at least one `IPreflightCheck` with a fix line, tested by injecting a fault** (named in its slice; Core-only and desk packages excepted); **it contributes its INDEX section and one preset section** through `IIndexContributor` when it adds an entity a controller can act on (named in its slice); `docs/<AREA>.md` and `docs/wire/<pkg>.md` describe what shipped, with a short "papers note" for the integration docs pass. |
| **Doctrine** | Attempts are not facts (receipts: sent → accepted → applied → observed; no-ops refused with the reason; tests assert fields and published STATE). Content is a pure function of show time. One writer. The desk has no private API. Fail closed on the wire. Measured, then governed. |
| **Licences** | No GPL or LGPL code is copied or opened while writing the matching code (§5.2 "Licence hygiene"). Restrictive native runtimes load from the user's install. Test fixtures are made with LGPL-compatible encoders. |

---

## 2. The package list

### 2.1 Overview

"Cont." marks a continuation (§2.2). Hours are the slice's must above ⟂.

| # | Branch | From specs | Hook word | Wave | Depends on | h | Round-2 headline |
|---|---|---|---|---|---|---|---|
| 0a | `wp2-spine-model` | all | — | W0a | `main` | — | Model, ids, catalogue rows, `RiskOf` (raise-only), syntax alternatives, live-video contracts |
| 0b | `wp2-spine-engine` | all | — | W0b | 0a | — | Hooks, gate, `Edits`, seams, air path, shader dialect convention, `GlCaps`, node host, third-party project, stubs |
| 1 | `wp2-show` | showsafety | Show | W1 | spine | 26.5 | Show Mode with no password, locks, panic with REWIND and SAFE and the air's sound gain, master, MinGo/EXPECT, preflight core |
| 2 | `wp2-output` | present, framelock (display) | Output | W1 | spine | 26.5 | Presenter seam, GLES dialect, vblank truth, the frame-lock machine with its watchdog |
| 3 | `wp2-decode` | hap | Decode | W1 | spine | 26 | The whole HAP family on the GPU, HAP R and HAP HDR included; measured HAP layers per node |
| 4 | `wp2-control` | control, timecode | Control | W1 | spine | 27.5 | OSC, MIDI, Art-Net in, bindings and a DMX personality; LTC/MTC/Art-Net timecode chase with independent goldens |
| 5 | `wp2-companion` | companion (node) | Companion | W1 | spine | 27 | Line WebSocket, INDEX with every package's sections, EVENTS, WHOAMI, typed verbs, THUMB, mDNS |
| 6 | `wp2-fx` | effects | Fx | W1 | spine | 27.5 | ISF v2 effects in chains at surface, layer and output; a built-in library; history effects named |
| 7 | `wp2-audio` | audio | Audio | W1 | spine | 26.5 | Analysis, onsets, beat clock, the modulation bus, the capture hub (WASAPI loopback on Windows) |
| 8 | `wp2-fleet` | fleet (health, Eye) | Fleet | W1 | spine | 24 | Health of every machine, the Eye board with focus and lenses, the context shaped for Patterns, the Patterns-side patch prepared |
| 9 | `wp2-wall` | firstfive, calibops | Wall | W1 | spine | 26.5 | Draw a quad on the wall; the score vocabulary for pins, scans and align; fallback, history, re-solve from pins |
| 10 | `wp2-multiview` | phonescan (Wave B core) | — (Core) | W1 | spine | 26 | Multi-view, multi-projector, curved: projector-anchored bundle adjustment and dense per-projector depth |
| 11 | `wp2-blind` | showsafety | Show (cont.) | W2 | show | 22 | Blind edit with a draft view and boundary commits; preflight breadth; ARM/PANIC fan-out; sound on air; manual transitions |
| 12 | `wp2-d3d11` | present, texshare | Output (cont.) | W2 | output, videoio (merge order) | 24 | The DXGI flip path run on CI's WARP; Spout send and receive; per-output latency |
| 13 | `wp2-zerocopy` | zerocopy, hap | Decode (cont.) | W2 | decode, output | 19 | The decode-path ladder reporting the path used; VAAPI dmabuf and D3D11VA imports; the upload arena |
| 14 | `wp2-companion-module` | companion (module) | — (TS) | W2 | companion | 26 | The Companion 5.1 module: linked templates by id, layered keys, value overrides, delta encoders, receipts, the Live page |
| 15 | `wp2-sync` | framelock (network) | Sync | W2 | output, show | 26.5 | Anchors to followers, clock slew without jumps, PMVB vblank reports and the per-pair verdict, the group's air fan-out |
| 16 | `wp2-content` | content | Content | W2 | control (hunk) | 24.5 | Content time, stings, GPU particles, raymarched fractals, reactive that listens |
| 17 | `wp2-optimise` | optimise (+ hap encoder) | Optimise | W2 | decode | 28.5 | Optimise on import: planner (HDR kept), HAP encoder, worker process, audio kept, verified versions |
| 18 | `wp2-videoio` | netvideo | VideoIo | W2 | decode (hunks) | 27.5 | OMT and NDI 6.3 in and out (discovery server, groups, advertiser); the video I/O spine |
| 19 | `wp2-led` | led | Led | W2 | control | 26 | LED pixel mapping from canvas, output or surface to Art-Net, sACN and DDP |
| 20 | `wp2-phonescan` | phonescan (Wave A) | Phone | W2 | wall, multiview | 26 | The phone as a local scan camera: pairing, lock and steady (iPhone) profiles, tagged lock-step capture |
| 21 | `wp2-align` | autoalign | Align | W2 | wall, show | 25 | Camera auto-align of flat screens: warp, blend with pedestal, uniformity and γ, staged and accepted through the ledger |
| 22 | `wp2-facade` | facade | Site | W2 | wall, phonescan (hunk order) | 27 | Site frame, survey import (PLY, OBJ, LAS, E57), control points, long-throw priors |
| 23 | `wp2-desk` | desk (D0, show) | — | W2 | show fixtures | 20.5 | The desk spine, generic STATE cards and generated right-click entries, the show strip and panic |
| 24 | `wp2-resolve` | calibops, firstfive, autoalign | Wall + Align (cont.) | W3 | wall, align | 26 | Re-solve only the projector that moved, from the camera; re-seat; the wall cursor; align parity with the truck |
| 25 | `wp2-multiscan` | phonescan (Wave B) | Phone (cont.) | W3 | phonescan, multiview | 27 | Many views into one scan, per-projector regions, persisted scans, safe AI suggestions, the closed browser loop, the Folder camera |
| 26 | `wp2-stacks` | facade | Site (cont.) | W3 | facade | 18.5 | Stacks aligned and colour-matched, drawn as a warp layer; geodesy; the facade guide and its end-to-end test |
| 27 | `wp2-backup` | fleet (backup) | Fleet (cont.) | W3 | fleet, sync | 19 | A hot backup that mirrors the air, switches the projector's input on FAILOVER, pulls with proof; FLEET PROMOTE |
| 28 | `wp2-desk-2` | desk (D1/D3 lean) | — (cont.) | W3 | desk, every fixture | 18 | Preflight board, fleet and backup cards, the quad tool, the score card, the blind chip, every package's cards |

Twenty-eight packages is well over the ~16 the first cut aimed at. The review's three blockers and its majors each need hours the
first cut did not have; continuations keep the file ownership of the first cut, so the extra sessions add no new merge
conflicts. If the round must be smaller, §9 decision 1 gives the order in which whole packages are deferred, and §8 then marks
their rows "Below bar this round".

### 2.2 What every package owns, by convention

Each package owns, beyond the files named in its section:

- its **stub files** created by the spine (`SPINE.md` §5.1): `src/PlayMap.Engine/EngineCore.<Word>.cs`, `EngineServices.<Word>.cs`,
  `Host/EngineParts.<Word>.cs`, `Rendering/RenderHost.<Word>.cs`, `src/PlayMap.Render/Shaders/ShaderLibrary.<Word>.cs`,
  `src/PlayMap.Core/Bus/NodeSettings.<Word>.cs` (where the hook exists for its word);
- its **model file** from `SPINE.md` §2.3 (the owner in brackets in §11 there);
- its **catalogue block** in `VerbCatalog.cs` (between its comment line and the next blank line): it trims syntax to what it
  implements, removes rows it does not implement, may add rows inside its own block for a stretch item it reaches, and edits no
  row outside its block;
- new folders `src/PlayMap.<Proj>/<Area>/**` and `tests/PlayMap.<Proj>.Tests/<Area>/**` for its area;
- `tests/fixtures/state/<pkg>.json` and `<pkg>.cards.json`, `docs/<AREA>.md`, `docs/wire/<pkg>.md`, and its section of
  `THIRD-PARTY-NOTICES.md`;
- at most one `<ItemGroup Label="wp2-<pkg>">` at the end of any `.csproj` it needs a reference in (one hunk per project).

**Continuations.** A continuation (blind, d3d11, zerocopy, companion-module, resolve, multiscan, stacks, backup, desk-2) owns,
for its wave, every file its parent package owned (the parent is merged and finished), plus the files named in its own section.
It keeps the parent's hook word, stubs, catalogue block and fixture (it adds to them). Its review session checks the parent's
tests still pass unchanged. No two packages in one wave own the same file.

Files that stay the **spine's** after W0 (changed only through the *spine amendment lane*, §6.3): `Core/Model/{Project, Ids,
Content, MediaModel, OutputModel, PlaylistModel, StageModel, DeviceModel, ProjectJson, ProjectStore}.cs`, `Core/Wire/{VerbCatalog
(outside blocks), VerbSyntax, Endpoints, WireLine, ShowClass}.cs`, `Core/Bus/{Pairing, ShowBus, ShowContext}.cs`,
`Core/Media/VideoFrames.cs`, `Engine/{EngineCore, EngineCore.Router, EngineCore.Ops, EngineCore.Edits, EngineCore.Modules,
StateWriter, ContextBuilder, NodeHome, Seams, Seams.Round2}.cs`, `Engine/Host/{EngineParts, EngineHost, NetworkHost,
EngineExtension, EngineEntry}.cs`, `Render/Engine/FramePasses.cs`, `Render/Shaders/ShaderLibrary.cs` (with the named program
hunks of §4), `tools/PlayMap.Node/**`, `src/ThirdParty/Directory.Build.props`, `.github/workflows/build.yml`, `.editorconfig`
(only through `tools/gen_editorconfig.py`), `BannedSymbols.txt`, `Directory.Build.props`, `PlayMap.sln`.

---

## 3. The packages

Each section: the slice in order (⟂ is the cut line; items after it are stretch), the files it owns, the hunks it may add to
others' files, its dependencies, its acceptance tests (all run in this container unless marked), what is verified here and what
is compile-only, and its next round. "Preflight" and "INDEX" lines name what the package adds under the Done-means rule.

### 3.0a `wp2-spine-model` (W0a)

**Slice.** `SPINE.md` §0–§4: the model records, ids and the `ProjectJson` rule; Core's shared types (§3, including the live-video
contracts of §3.8 so Spout and NDI/OMT build in parallel); every catalogue row of §4 with `RiskOf(line)`, **raise-only** per §4.1;
`VerbSyntax` (the one syntax reader, which splits a row at top-level ` | ` into alternatives, re-prefixing the verb's name) and
`CommandGate.RequiredTokens` rebuilt on it, accepting a line that matches any alternative; `VerbSpec.Round` and `VERBS`
`"available"`; the show classes; the context's link verbs; the `VideoFrames` hunks. Two commits: (1) model, ids, JSON, Core
types; (2) catalogue, risk, syntax, gate.

**Acceptance.** `ModelCompatTests`; `CatalogueTests` (every row: ≤ 2 words, syntax parses into words HELP prints, risk set,
`SecretFrom` null on new rows, show class set, aliases unique, `TC`; **for every row and every alternative, a line built from that
alternative passes `CommandGate.Check`**; **no `RiskWhen` takes a line of class Air, Transport, Panic or DevicePower below High,
and every new row of class Air or Panic is High**); `RiskOfTests` (BLACKOUT ON high, FREEZE ON high, DEVICE SHUTTER dv CLOSE high,
MASTER 0 high, PANIC high, BIND ADD … TO BLACKOUT OFF high, DECODE POLICY DIRECT FORCE high, MEDIA OPTIMISE CANCEL low, BIND
LEARN OFF low, TIMECODE SOURCE RESET tc low, SEND ENABLE sd ON review, an unknown verb high); `ContextLinkTests`;
`LiveContractTests` (`LiveUri` parse table); **every existing test passes unchanged** — named because the first cut would have
broken them: `OpsHostTests` (MCP stages BLACKOUT ON as queued), `HostLifecycleTests` (the queue holds BLACKOUT ON),
`StagewatchSinkTests` (offered risk "high"), `AssistantEngineTests` (an asked high-risk line waits), `GateTests`.

**Verified here.** All. **Compile-only.** None.

### 3.0b `wp2-spine-engine` (W0b)

**Slice.** `SPINE.md` §5–§11, in three commits so review can follow it:
1. Engine hooks (`EngineCore.Modules.cs`, `VerbRoutes` with topics and INDEX contributors), `GateLine`/`Edits`/`EditAsync` with
   every edit site converted, `Seams.Round2.cs` (`IShowGate`, `IOutputGains`, `ICalibrationLedger`, `IPreflightCheck`,
   `IPosterStore`, `IFleetFanout`, `IIndexContributor`), host parts (audio's before control's), `NodeSettings` partial, network
   hooks (`IPushTopics`, `IWebModule`), `IVerbAvailability` (the assistant's prompt and MCP's `list_verbs` list only available
   rows).
2. Render: frame inputs and the three pass call points; **the air path end to end** (`RenderEngine` sets
   `OutputStageInputs.Gain` from `IAirLevels.Gain`, applies `Hold` per output and `Override` as the output's picture;
   `OutputStage` sets `uAir` (1 when no levels) and the `output` program multiplies by it last; `Measured` bound the same way);
   **the shader dialect convention** (program sources carry no `#version` and no precision line; `GlDevice.CreateProgram`
   prepends the dialect's header and refuses a source that has one; `GlDialect` lands with `Core330` only; every existing program
   converted); `GlCaps` read by the `glGetStringi` loop with split flags (§6.4); the texture formats with explicit classification.
3. The process and the tree: `tools/PlayMap.Node` (a small non-Avalonia exe referencing only Engine; `engine` and every
   `EngineEntry` dispatch, so tests and the Companion module's e2e never need App's 832 MB build) with `EngineEntry.TryDispatch`
   used by App's `Program.cs` too; the `src/ThirdParty` tree (a nested `Directory.Build.props` with `RunAnalyzers=false`,
   `TreatWarningsAsErrors=false`, no `BannedSymbols.txt`; an empty `PlayMap.ThirdParty.Omt.csproj` in `PlayMap.sln`); the CI
   Windows job runs `Render.Tests` and `Media.Tests` with `--filter Category=Windows`; every per-package stub (generated by a
   script; every empty body holds a comment, `// wp2-<pkg> fills this.`, so Sonar S1186 passes); empty fixtures; `docs/wire/<pkg>.md`
   generated from the catalogue; the "Round 2" section of `docs/WIRE.md`; `THIRD-PARTY-NOTICES.md` with one empty heading per
   package; `tests/fixtures/state/main.json` (today's STATE, for the desk's cards of `solve`, `cameras` and `ai`).

**Acceptance.** `RoutesTests`; `EditsRedirectTests`; `StateOrderTests`; `PushTopicTests`; `IndexContributorTests` (a fake
contributor's section is listed once; a second with the same section throws at construction); `TextureFormatTests` (every value
of `Enum.GetValues<TextureFormat>()` has a `Describe` arm and the expected block class; `CompressedSize` of 1920×1080 BC1 =
1 036 800 and BC3 = 2 073 600); `GlCapsTests` (fake extension lists: core 3.3 Mesa, ES 3.0 Mesa, an ANGLE-like list with
`EXT_texture_compression_dxt1` + `ANGLE_texture_compression_dxt5` and no S3TC; on llvmpipe all five block families read true in
core 3.3); `ShaderConventionTests` (no program source holds `#version`; `CreateProgram` refuses one that does, with words; every
program compiles and links on core 3.3); `AirPlumbingTests` (llvmpipe: with no `IAirLevels` every existing render read-back is
byte-identical; a fake gain of 0.5 halves one output's codes ± 1 and leaves the other; `Hold` freezes one output only; an
`Override` shows its source); `NodeHostTests` (`PlayMap.Node engine --headless` answers `PING` on its wire; an unknown verb exits
non-zero with words); `AvailabilityTests` (the prompt and `list_verbs` hold every routed row and no unrouted round-2 row);
`WindowsPlatformTests` (every type under a `Windows` namespace or file carries `[SupportedOSPlatform("windows")]`; CA1416 is an
error); **every existing test passes unchanged**; the desk builds.

**Verified here.** All. **Compile-only.** None.

### 3.1 `wp2-show` — Show Mode, locks, panic, master, MinGo, preflight core (W1)

**Slice.**
1. `ShowPolicy` (Core, pure): roles (desk, operator, paired, remote, companion, mcp, assistant, approved) × `ShowClass` ×
   armed/rehearsal, with the refusal words and the way out; `GateLine` and the ops gate; `IShowGate`. (6 h)
2. `SHOW`, `SHOW ARM`, `SHOW DISARM`, `SHOW SET`; armed survives an engine restart (recovery); `STATE.show`; context item `show`;
   events. `ALL` is parsed and refused "no fleet fan-out on this node" until `wp2-sync` sets `IFleetFanout`. (3 h)
3. Locks: `LOCK`, `UNLOCK`, `LOCKS`; entity and area locks; one guard through `ProjectStore.Guard` (Apply, Undo, Redo); armed adds
   `mapping` and `layout`. (4 h)
4. Panic and master: `PANIC [BLACK|FREEZE|NEXT|REWIND|SAFE|SHUTTER|FX-OFF|HARD]`, `PANIC CLEAR`, `PANIC SET` (a plan per output,
   with its safe look), `MASTER`; fades are pure functions of leader time (`AirFade`); a second PANIC inside a fade is HARD;
   `AirLevels` = master × panic × degrade into the spine's `uAir`; **REWIND** cues the item's in-point at the fade's end; **SAFE**
   dips to black and shows the plan's safe look through `IAirLevels.Override` (the pattern-override path); **the air's sound gain**
   `IAirLevels.AudioGain` comes from the same `AirFade` (master × panic), so sound fades on the same frames as picture (wp2-blind
   puts it on `AudioOut`); SHUTTER runs `DEVICE SHUTTER <dv> CLOSE` on linked devices and FX-OFF runs `FX BYPASS ALL ON`, both as
   `system` lines through the gate; PANIC is never refused for an operator. (9 h)
5. MinGo double-GO protection (`MinGoMs` per rule set) and `EXPECT <item>` on `PLAYLIST GO|NEXT|PREV` and `VIDEO PLAY|RESTART`. (1.5 h)
6. Preflight core: `PREFLIGHT [QUICK]`, `PREFLIGHT REPORT [<id>]`; the `IPreflightCheck` registry with per-check budgets; four
   built-in checks (outputs on present displays, media present and readable, linked devices reachable, free disk on the media
   volume); fix lines that parse; `STATE.show.preflight`; the show-ready light. (3 h)

⟂ *(≈ 26.5 h)*

7. *(stretch)* Panic outcomes `RUN` and `AT`.

**INDEX.** Section `show`: ARM, DISARM, PANIC per planned outcome, PANIC CLEAR, MASTER on an encoder. **Preflight.** The four
built-ins above.

**Owns.** `src/PlayMap.Core/Show/**`, `src/PlayMap.Core/Preflight/**`, `src/PlayMap.Engine/Show/**`,
`src/PlayMap.Engine/EngineCore.Transport.cs`, `src/PlayMap.Engine/Host/ApprovalHook.cs`,
`src/PlayMap.Engine/Assistant/StagewatchProposalSink.cs`, `src/PlayMap.Assistant/CommandGate.cs` (after W0a), `docs/SHOW.md`.

**Hunks.** None: the spine lands `uAir` and the per-output gain, hold and override in `RenderEngine`/`OutputStage` (W0b).

**Depends on.** The spine.

**Acceptance.**
- `ShowPolicyTests` (Core): every `ShowClass` × role × {armed, rehearsal} cell; refusal words name the rule and the way out.
- `ShowModeTests` (Engine): ARM, restart through the recovery record → `STATE.show.armed == true`; edits refused from desk, wire,
  companion, mcp and assistant sessions; transport, BLACKOUT and PANIC still run; DISARM from an unpaired session refused.
- `LockTests`: `LOCK sf…` → `SET sf…` refused with the lock's words; an UNDO across a locked entity refused; `LOCK AREA mapping`
  blocks SURFACE verbs only.
- `PanicTests` (ManualClock): `PANIC FADE 1000` → level 0.5 ± 1e-6 and `AudioGain` 0.5 ± 1e-6 at +500 ms; a second PANIC at
  +300 ms → 0 at once and `hard:true`; `PANIC CLEAR FADE 500` → 1.0 at +500 ms; REWIND → the playlist's item cued at its in-point
  at the fade's end; NEXT → the next item cued; SHUTTER → the simulator device reports the shutter closed (observed, not just
  sent); FX-OFF → a journalled `FX BYPASS ALL ON` by `system` (observed in STATE at the merge after fx, §6.1).
- `PanicRenderTests` (llvmpipe): a white output read back at t, +250, +500, +1000 ms of `PANIC FADE 1000` gives 255, ≈191, ≈128,
  0 (± 2); SAFE shows the safe source after the dip.
- `MinGoTests` (ManualClock): two GOs 200 ms apart armed → the second refused with the double-GO words; `PLAYLIST GO show EXPECT
  it_b` while the next is `it_c` → refused naming both.
- `PreflightTests`: each built-in check fails on an injected fault and passes once fixed; every fix line parses with
  `VerbCatalog.Match`; budgets hold on a ManualClock; REPORT round-trips.
- `show.json` and `show.cards.json` written by `ShowStateFixtureTest`.

**Verified here.** Everything. **Compile-only.** None.

**Next round.** Parked edits (waits for DISARM), soft edit leases, `FULL` and `SOAK` preflight, the approval desk
(`StagewatchProposalSink` switched on with `CollectApproved=false`, §9), the OS show lock on Windows, named users, multiple drafts,
lowering the risk of safe-direction lines (§10, F-RISK).

### 3.2 `wp2-output` — presenter seams, the GLES dialect, vblank truth, the lock machine (W1)

**Slice.**
1. Presenter and platform seams: `IOutputPresenter` replaces `OutputWindow` in `RenderEngine` and `OutputWindows`; `IGpuPlatform`
   replaces `RenderContext.CreateHidden`; the GL window is the first presenter, with unchanged behaviour. (6 h)
2. `GlDialect.Es300` on the spine's convention: the ES header and precision line, the per-dialect format table (with the
   `GlCaps` fallbacks: RGBA16 → RGBA16F where `EXT_texture_norm16` is absent, float targets only with `EXT_color_buffer_float`),
   the alias proc loader (`glFoo` → `EXT` → `KHR` → `OES` → `ANGLE`); EGL contexts on Linux (`NodeSettings.Present.Dialect`, GLX
   fallback with words); an ES test fixture through EGL surfaceless (Mesa offers OpenGL ES 3.2 here). (4 h)
3. `VblankClock` and `PresentLedger` (`Render/Timing`, pure) fed by GL swap-return instants; `outputs[].present{…}`,
   `machine.present{…}`; `OUTPUT STATS`, `PRESENT RESET`; the output light from display-side counts measured against the output's
   own refresh. (6 h)
4. The lock machine (framelock F4): `FrameLockMachine` (Core, pure), `LockCounter` (24-bit board counter, wrap and the 60 s
   re-anchor), `IFrameLockDriver` with `FakeFrameLockDriver`, the never-stall watchdog, frame-count content selection, no
   auto-rejoin while armed; `SYNC APPLY`, `SYNC LEAVE`, `SYNC REJOIN` with refusals by reason field (armed, follower, no board,
   mixed rates); the NVAPI tier (`NvFrameLock`) compile-only with struct-size goldens taken from the MIT `nvapi.h`; board names
   by id (856 Quadro G-Sync P358, 8288 Quadro Sync P2060, 8289 Quadro Sync II P2061; any other id "sync board 0x<id>", capabilities
   unknown). (10 h)

⟂ *(≈ 26 h; + preflight 0.5 h)*

5. *(stretch)* The whole Render suite as a second leg on Mesa GLES 3.0 (the dialect tests of item 2 already compile every program
   on ES).
6. *(stretch)* OML sync control on Linux.

**Preflight.** Present path and lock state per output (a free-running output in a group that applied the lock is amber with its
fix). **INDEX.** Section `outputs` gains present and lock lights (feedback hints only).

**Owns.** `src/PlayMap.Render/Engine/{RenderEngine, Contracts}.cs`, `src/PlayMap.Render/Platform/**`,
`src/PlayMap.Render/Timing/**`, `src/PlayMap.Render/Gpu/{GlDevice, GlDevice.Resources, ShaderProgram, GlDialect}.cs`,
`src/PlayMap.Engine/Rendering/{RenderHost, OutputWindows, HeadlessPacing, PreviewViews}.cs`, `src/PlayMap.Core/Sync/**` (lock
machine), `docs/OUTPUT.md`.

**Hunks.** None (the dialect convention is the spine's).

**Depends on.** The spine.

**Acceptance.**
- `PresenterSeamTests`: the whole existing Render and Engine suites pass with the GL presenter, with no test edited.
- `GlDialectTests`: every program in `ShaderLibrary.Names` compiles and links on core 3.3 and on Mesa GLES 3.0; the alias loader
  resolves through the suffix list against a fake proc table; the format table's fallbacks follow `GlCaps`.
- `VblankClockTests` (synthetic): 600 samples at 60 Hz with ± 0.5 ms jitter → period within 1 µs of 16 666.7 µs, phase within
  0.2 ms; a missed vblank counted once; `PresentLedger` counts late and dropped against the output's own refresh.
- `PresentStateTests`: `outputs[].present` keys and types; the light goes amber at > 0.5 % late over 10 s (scripted ledger).
- `FrameLockMachineTests`: ≥ 25 scripted transitions with `FakeFrameLockDriver` — join to lock after 120 good refreshes; a stall at
  3T leaves the barrier and the output keeps presenting; a board-sync loss debounced over 3T; no rejoin while armed; auto-rejoin
  after 10 s at an idle boundary while disarmed; a cable into a switch refused with words.
- `LockCounterTests`: counter maths over 4 simulated days at 120 Hz; an anchor switch at a named count gives identical content time
  on 3 simulated nodes for every vblank across 2 wraps.
- `SyncVerbTests`: every refusal asserted by its reason field. `NvapiLayoutTests`: struct sizes and version words equal the
  header's.
- `output.json` and `output.cards.json` written by a test.

**Verified here.** 1–4 (Mesa EGL and GLX on Xvfb; vblank arithmetic synthetic, as Xvfb's swaps are not a real display; the lock
machine against its fake). **Compile-only.** NVAPI (struct goldens here; a Windows-category load test on CI says "no NVIDIA
driver" with words).

**Next round.** 10-bit output, device-lost rebuild, the PresentMon proof, the NVIDIA DRS stop-gap, scanline sampling;
`SYNC MARKS/CERTIFY/REPORT`, the frame code and certificate, `LATENCY MEASURE`; IGCL and GLX swap-group tiers; the board helper;
D3D12 present barrier; HDR; macOS.

### 3.3 `wp2-decode` — the HAP family on the GPU (W1)

**Slice.**
1. Core, parse only: `HapFrame.TryParse` (section headers, chunk tables), `HapFrameWriter`, `HapChunking`, `HapCodecs` (fourcc ↔
   texture format ↔ layout) for HAP, HAP Alpha, HAP Q, HAP Q Alpha, HAP Alpha-only, **HAP R (BC7)** and **HAP HDR (BC6H)**; the
   section-type table pinned to the published HAP specification as a golden; refusal words for every malformed case. (6 h)
2. `HapVideoSource : IVideoSource` (Media): a read thread with time- and byte-bounded read-ahead (`HapReadAheadMs` 250 binds first
   at 4K60; `HapReadAheadMb` 192 binds only above 12.8 MB per frame), a decode thread with chunked parallel Snappy through
   **Snappier 1.3.1** (BSD-3, `Snappy.Decompress(ReadOnlySpan<byte>, Span<byte>)`, the library hap.md benchmarked), `FrameLayout`
   compressed, the clip clock (frame = f(time)). (8 h)
3. Probe and routing: `MediaInfoReader` names every HAP codec (including the two FFmpeg cannot decode); `MediaBackend.Open` routes
   HAP to `HapVideoSource`; readiness words. (2 h)
4. Render: the compressed upload path (`GlDevice.Decode.cs`), capability from the spine's `GlCaps` (Dxt1, Dxt5, Rgtc, Bptc per
   dialect), the `VideoTextureSet` compressed branch, the `hap` pass (YCoCg → RGB for HAP Q, the BC4 plane for Q Alpha, BC6H into
   `Rgba16F`). (6 h)
5. HAP HDR interpretation: linear with 1.0 = 203 nits by default, `colr` nclx read when present. (1 h)
6. Facts and the budget: `SourceHealth.Hap`, `STATE.decode.sources[].hap`, `STATE.decode.platform.blocks`, `MEDIA INSPECT <md>`
   (layer budget = min(disk ÷ peak MB/s per layer, cores ÷ decode ms per layer, upload) with the limit word that binds);
   `decode.platform.hapLayers4k60` measured by `HapThroughputTests` (upload excluded, recorded in STATE). (3 h)

⟂ *(≈ 26 h)*

7. *(stretch)* The persistently mapped upload arena (else wp2-zerocopy's item 4).

**Preflight.** "The show needs N HAP layers at 4K60; this node sustains M" (amber when N > M, with the line that shows why).

**Owns.** `src/PlayMap.Core/Media/Hap/**`, `src/PlayMap.Core/Media/{GpuSurfaces, UploadArena}.cs`, `src/PlayMap.Media/Hap/**`,
`src/PlayMap.Media/MediaProbe.cs`, `src/PlayMap.Media/Ffmpeg/MediaInfoReader.cs`, `src/PlayMap.Media/Frames/**`,
`src/PlayMap.Media/Sources/**`, `src/PlayMap.Render/Video/{VideoTextureSet, YuvConversion}.cs`,
`src/PlayMap.Render/Gpu/GlDevice.Decode.cs`, `src/PlayMap.Engine/Host/MediaBackend.cs`, `docs/DECODE.md`.

**Hunks.** None.

**Depends on.** The spine.

**Acceptance.** Fixtures are **made in test setup**, never read from the scratchpad: lavfi `testsrc2` frames (synthetic, no
copyright) encoded by FFmpeg 9's own `hap` encoder (`hap`, `hap_alpha`, `hap_q`) with fixed parameters; HAP Q Alpha and HAP
Alpha-only assembled from `hap_q` blocks plus a small test BC4 writer; HAP R and HAP HDR frames assembled by `HapFrameWriter` from
blocks written by BCnEncoder.NET (test-only package, MIT OR Unlicense), which is also the CPU reference decoder.
- `HapParserTests`: every variant parses; the section-type golden; truncation, bad chunk tables and Snappy overruns are refused
  with words; writer → parser round trips byte-exact.
- `HapSourceTests` (Media): generated clips play 120 frames with frame n = f(time); read-ahead stays within its bounds; nothing
  allocates per frame once warm.
- `HapRenderTests` (llvmpipe; Mesa exposes S3TC, RGTC and BPTC): (a) raw planes, `texelFetch` into RGBA8/RGBA16F targets with the
  pass in plane mode, against BCnEncoder.NET's decode: ≤ 1 code for BC1, BC3, BC4 and BC7, ≤ 1 binary16 ULP (or relative 1e-3) for
  BC6H; (b) the YCoCg → RGB maths against a CPU twin of the `hap` program; (c) whole frames against FFmpeg's CPU decode with
  hap.md M4's per-family bounds (HAP and HAP Alpha max diff ≤ 2, HAP Q ≤ 4, and their PSNR floors); a missing extension refuses
  with words, never a silent black frame.
- `HapHdrTests`: the 203-nit mapping, nclx parsing.
- `HapBudgetTests`: the budget arithmetic and the read-ahead caps (the 250 ms cap binds at 15 frames of 4K60 HAP Q).
- `HapThroughputTests` (`Category=Perf`, run serially at integration; measured and recorded, never a pass/fail ceiling).
- `HapEndToEndTests` (Engine, headless): `MEDIA IMPORT` of a HAP clip, `SURFACE SOURCE`, the output read-back shows the clip's frame
  for leader time t.
- `decode.json` and `decode.cards.json` written by a test.

**Verified here.** HAP, HAP Alpha and HAP Q against FFmpeg's encoder and decoder; HAP Q Alpha and Alpha-only against our assembly;
**HAP R and HAP HDR against our writer and Mesa's BPTC — interop with third-party files is lab work**. **Compile-only.** None.

**Next round.** Interop fixtures of HAP R/HDR from other encoders (lab), direct sampling of BC textures with no `hap` pass, raw HAP
straight from disk into the arena, sRGB block formats, the per-item colour override, the desk's codec chips.

### 3.4 `wp2-control` — OSC, MIDI, Art-Net, bindings, timecode chase (W1)

Clean room: the LTC, MTC, OSC and MSC code is written from the standards (SMPTE ST 12-1, MIDI 1.0 MTC and MSC, OSC 1.0, Art-Net 4);
`ext/libltc` (LGPL-3) is not opened while writing it, and the spec sections used are noted in the tests.

**Slice.**
1. Bindings: `ControlSet` (surfaces `ct…`, bindings `bd…`); input words → a wire line; `BindingMath` (ranges, invert, curves,
   steps, pick-up takeover); `CONTROL SURFACE`, `BIND ADD/LIST/LEARN/ENABLE/FORGET`; one journal row per gesture; a binding's line
   goes through the gate like any line; **a built-in DMX personality** (`ControlSet` preset: per surface intensity, source index,
   speed and opacity; per output the master) that `BIND ADD <json>` installs in one line. (6 h)
2. OSC server on UDP 9943: OSC 1.0 codec (bundles, timetags parsed, arrays), pattern matching, the address map
   (`/playmap/<verb words>` and bound addresses), replies and errors. (4 h)
3. MIDI in: the parser (running status, velocity-0 as release, 14-bit CC pairing, NRPN/RPN, bend, SysEx, MSC/MMC decode, MTC full
   frames `F0 7F <dev> 01 01 …` and user bits for any device id, 7F always accepted); the ALSA sequencer backend (runs here and
   says there are no ports), a fake backend for tests; WinMM compile-only. (4 h)
4. DMX in: Art-Net 4 (OpDmx with sequence check, OpSync, OpPoll → unicast OpPollReply after a random delay ≤ 1 s from `Pcg32`,
   OpTimeCode to the timecode sink) on the node's one Art-Net socket (`IArtNetEndpoint`, `EngineServices.ArtNet`): bound to the
   address in `NodeSettings.Control.ArtNetInterface`, **opened when any consumer needs it** (DMX input, an ARTNET timecode
   source, LED output; `ArtNetEnabled` gates only DMX → binding lines), sending through cached `SocketAddress`es without
   allocating. (3.5 h)
5. Timecode chase: the LTC decoder (pure C#) and an encoder for tests at 24, 25, 29.97 DF/NDF and 30 fps and 44.1/48 kHz;
   ArtTimeCode and MTC quarter-frame in; `TimecodeFit` and `ChaseLogic` (pure); the engine chase (a playlist's item time is a
   function of the fitted code at vblank; dropout free-wheels then stops by rule); `TIMECODE STATUS/SOURCE/CHASE/ENGAGE/SET/
   INTERNAL`; `STATE.timecode`; manual transport on a chased playlist refused with words. LTC arrives through `IAudioInputs`,
   resolved at start from the final services (audio's hub, §5.5). (8 h)

⟂ *(≈ 25.5 h; + preflight 0.5 h and the independent goldens ≈ 1.5 h)*

6. *(stretch)* The feedback engine: value and lamp feedback over OSC to each client's reply address, generic profile (first).
7. *(stretch)* sACN E1.31 receive with priority merge.
8. *(stretch)* Timetagged OSC bundles scheduled at their instant; OSCQuery on TCP 9944.

**Preflight.** A chased playlist's timecode source present and rolling; every bound control port open. **INDEX.** Section
`timecode`: ENGAGE per chased playlist.

**Owns.** `src/PlayMap.Core/Control/**`, `src/PlayMap.Core/Timecode/**`, `src/PlayMap.Core/Dmx/**`,
`src/PlayMap.Core/Time/Timecode.cs`, `src/PlayMap.Core/Playback/**`, `src/PlayMap.Engine/Playback/PlaybackLanes.cs`,
`src/PlayMap.Net/Control/**` (OSC, Art-Net, sACN sockets), `src/PlayMap.Devices/Midi/**`, `src/PlayMap.Engine/Control/**`,
`src/PlayMap.Engine/Timecode/**`, `docs/CONTROL.md`, `docs/TIMECODE.md`.

**Hunks.** `EngineCore.Transport.cs` (show's): one guard call refusing manual transport on a chased playlist (after show in the W1
merge order).

**Depends on.** The spine.

**Acceptance.**
- `BindingMathTests` (a table row per curve and takeover); `BindingLearnTests`; `DmxPersonalityTests` (a 16-channel personality
  installed by one line drives `SURFACE` opacity through the gate).
- `OscServerTests` (UDP on the slot's loopback address, §5.2): `/playmap/playlist/go show` → PLAYLIST GO observed; a refused line
  → an error reply with the words; `OscInteropTests`: messages and bundles written by Node's `osc` package (MIT; the generator
  script is committed under `tests/interop/osc/`) parse identically, and ours parse in it.
- `MidiParserTests`: running status, 14-bit pairs, NRPN, MSC from the specification's example messages; `MtcTests` with device ids
  0x00, 0x10 and 0x7F.
- `ArtNetInTests`: sequence handling; the poll reply is unicast after ≤ 1 s (ManualClock); ArtTimeCode reaches the timecode sink;
  10 000 sends allocate 0 bytes (`GC.GetAllocatedBytesForCurrentThread`); a consumer other than DMX input opens the socket and
  fires no binding.
- `LtcDecoderTests`: three 80-bit frames derived by hand from the standard's field table (25 fps, 29.97 DF, 30 fps; bit order in
  the BCD fields, the sync word 0011 1111 1111 1101, the polarity bit, the DF and colour-frame flags) with the citation; one
  external LTC WAV fixture (public domain or made by a non-LGPL generator, provenance in the test); then the matrix of rates ×
  sample rates from our encoder; strict DF; speed ± 10 %; 20 dB SNR noise.
- `ChaseTests` (seeded `Pcg32` TC runs): the frame chosen at every simulated vblank equals the reference f(TC); dropout and jump
  rules. `TimecodeEngineTests`: chase a playlist from a synthetic LTC input; `STATE.timecode` fields; refusals by reason.
- `control.json` and `control.cards.json` written by a test.

**Verified here.** All (UDP and multicast on loopback, synthetic audio). **Compile-only.** WinMM (a Windows-category test on CI
lists no MIDI ports with words), CoreMIDI. **Manual (lab).** Real controllers, consoles and LTC sources.

**Next round.** Controller profiles (Mackie, Launchpad), Windows MIDI Services, MIDI out, MSC/MMC maps, sACN discovery, house
bindings, input forwarding; timecode outputs (LTC, MTC, Art-Net), TC cues, triggered mode, backup sources, jump preload, the
timecode test card, `SUBSCRIBE TIMECODE`, `TIMECODE CALIBRATE`, MIDI clock.

### 3.5 `wp2-companion` — the node's side of two-way control (W1)

**Slice.**
1. The line channel over WebSocket: `/ws` with subprotocol `playmap.lines.v1` runs the line-wire session (NONCE, HELLO,
   AUTH-PROOF, PING, SUBSCRIBE, pushes); `HELLO Companion:<label>` → transport `companion` (a claim only narrows rights). (6 h)
2. `INDEX` and `SUBSCRIBE INDEX` (latest-wins): playlists with items, surfaces, projectors, outputs, devices, generators, **and
   every section a package contributes through `IIndexContributor`** (rows plus a preset-section hint, as data); `sig` and
   `labels`. (3 h)
3. `EVENTS`: the ring of the last 256 events raised through `RaiseEvent`, `SUBSCRIBE EVENTS`, `EVENTS SINCE <seq>`; `WHOAMI`,
   `PRESENCE`, `STATE.clients`, a context item per Companion client. (4 h)
4. `VerbParams` (`Core/Wire/VerbParams.cs`): a typed parameter list for every catalogue row and every alternative (read through
   the spine's `VerbSyntax`), with overrides by row name; **the press shape** per row (instant for the Panic, Air and Transport
   show classes — PANIC, BLACKOUT, GO, NEXT, PREV — otherwise from the risk); `VERBS` and `/api/verbs` gain `params` and
   `press`. (4 h)
5. `THUMB`, `SUBSCRIBE THUMBS`, `GET /api/thumb`: media posters from `IPosterStore` or one decoded frame, solid and colour
   generators drawn, cached by content and ETag. (3 h)
6. mDNS: one responder for `_playmap._tcp` in `PlayMap.Net/Discovery` (TXT id, name, proto, ws, wire, tls, fp; PTR, SRV, TXT and A
   including legacy unicast; announce and goodbye; conflict rename; a bind failure is a fault, never a crash), so the module's
   `bonjour-device` field lists the node. (7 h)

⟂ *(≈ 27 h)*

7. *(stretch)* Live output and projector thumbnails, ≤ 1 fps, from preview views with internal leases.
8. *(stretch)* Streamed `ASK` replies over the WebSocket (carry-over: ASK waits for the whole model turn); `AskStreamTests` (a
   scripted model's tokens reach a WS client as `ASK-PART` lines before the turn ends; the final reply is unchanged).
9. *(stretch)* The node → Companion link: `COMPANION PRESS` (HTTP `/api/location/p/r/c/press`), `COMPANION VAR`
   (`/api/custom-variable/<n>/value`) and `COMPANION BRIGHTNESS` (REST v2 `PATCH /api/v2/surfaces/v1/{id}`), tested against a fake
   built from `openapi-stable.json`; the REST key in the credential store (`CREDENTIAL SET companion`, this machine only).

**INDEX.** The core sections above. **Preflight.** Companion clients: paired and live, or amber with the pairing fix.

**Owns.** `src/PlayMap.Net/Wire/WireSession.cs`, `src/PlayMap.Net/Http/{HttpHost, HttpParts, WebSocketSession}.cs`,
`src/PlayMap.Net/Mcp/McpTools.cs`, `src/PlayMap.Net/Companion/**`, `src/PlayMap.Net/Discovery/**`,
`src/PlayMap.Core/Wire/VerbParams.cs`, `src/PlayMap.Engine/Companion/**`, `docs/COMPANION.md`.

**Hunks.** None. Packages that remove a catalogue row also remove its override line in `VerbParams.cs` if it has one (named in
their PR; the `VerbParamTests` orphan check finds any left).

**Depends on.** The spine.

**Acceptance.**
- `LineWebSocketTests`: a WS client with the subprotocol pairs and runs `PLAYLIST GO`; a Companion HELLO cannot widen rights;
  unpaired edits are refused; a binary frame closes 1003; over 64 KiB closes 1009.
- `IndexTests`: INDEX fields; a fake contributor's section and hint appear; `SUBSCRIBE INDEX` pushes on a playlist add (event
  driven, asserted on the push, not on a wall-clock window); latest-wins; a rename moves only `labels`.
- `EventsTests` (ManualClock): order; `EVENTS SINCE` replays; an overflowing subscriber is closed with "overflow".
- `VerbParamTests`: every row and every alternative has params; types agree with the syntax HELP prints; no override names a
  missing row; every Panic/Air/Transport row's press is instant.
- `ThumbTests` (FFmpeg 9): a 2 s test clip → `THUMB media:md… 96x96` returns a decodable JPEG ≤ 12 KB; `IF <etag>` answers `same`;
  an uncached poster while armed is refused with words.
- `MdnsTests`: codec round trip of captured Bonjour/Avahi packets; a unicast query on the slot's loopback address is answered with
  PTR, SRV, TXT and A; a conflict renames to "<name> (2)"; a bind failure becomes a `faults[]` entry.
- `companion.json` and `companion.cards.json` written by a test.

**Verified here.** All. **Manual (lab).** A real Companion install and a Stream Deck (with wp2-companion-module).

**Next round.** Key pages authored on the desk (`KEYS …`), `COMPANION PAGE` and `COMPANION ADD` (REST v2 provisioning), `SELECT`
from the desk, Show Bus hello discovery in the module, the desk's Decks drawer, browsing (OSCQuery and WLED) on the same
responder.

### 3.6 `wp2-fx` — ISF effects in chains (W1)

**Slice.**
1. ISF v2 parser and translator (Core): every key of ISF v2 and v1's `PERSISTENT_BUFFERS`, both `IMG_THIS_NORM_PIXEL` spellings,
   `IMG_*`, WIDTH/HEIGHT expressions (our own evaluator); output for the spine's dialects with a line map for errors; **the ES
   built-in renames** (`IsfEsFixups`, effects.md M2) in this item; the vertex prelude per dialect (`#define varying out`,
   `#define attribute in`, `#define texture2D texture`, `out vec2 isf_FragNormCoord;` and an `isf_vertShaderInit()` that writes
   `gl_Position` from `gl_VertexID` and the coordinate; no fragment output). (9 h)
2. The ISF renderer (Render): passes, temporary and persistent float targets, imported images, built-ins from show time (`TIME`,
   `TIMEDELTA`, `FRAMEINDEX` as functions of the leader instant and frame), a compile lane on a shared context that never stalls a
   frame (a slot shows its input until compiled). Effects with persistent buffers are marked **history** (they integrate frame to
   frame) in `FX KINDS` and in their slot's STATE, with words saying they are not frame-exact across nodes. (8 h)
3. The chain runner through `IFramePass` at three attach points — surface, layer, output — with bypass, mix, blend (normal, add,
   multiply, screen, difference) and scale; patterns, raw scan frames, blackout and identify never pass through; allocation-free
   once warm. (5 h)
4. `FX KINDS/LIST/ADD/REMOVE/MOVE/SET/BYPASS/IMPORT/CHECK`, `STATE.fx`, context; a built-in library v1 of 10 effects written for
   PlayMap as ISF, each with a pixel test. (5 h)

⟂ *(≈ 27 h; + preflight 0.5 h)*

5. *(stretch)* Slot parameters bound through `IModulationBus` (with fx's built-in `lfo:*` and `ramp:*` sources until audio lands).
6. *(stretch)* Attach points on playlist items and canvases; 14 more built-ins including the geometry-aware ones.

**Preflight.** Every slot of a chain on an output in the show compiled, none faulted. **INDEX.** Section `fx`: per slot BYPASS
and mix on an encoder.

**Owns.** `src/PlayMap.Core/Effects/**`, `src/PlayMap.Render/Effects/**` (the built-in `.fs` files embedded there),
`src/PlayMap.Render/Gpu/GlDevice.Fx.cs`, `src/PlayMap.Engine/Fx/**`, `docs/EFFECTS.md`.

**Hunks.** None.

**Depends on.** The spine. Its ES leg runs at the merge after output (§6.1).

**Acceptance.**
- `IsfParserTests`, `IsfTranslatorTests` (every translated sample compiles on core 3.3; a `.vs` that passes a varying compiles);
  the opt-in corpus test (`PLAYMAP_ISF_CORPUS` → a checkout of the ISF files: ≥ 324/327 at 330 core). The ES figure (≥ 305/327) is
  measured on the real GLES context at the merge after output; the research's 305 came from `ARB_ES3_compatibility` in a 4.5 core
  context, so the GLES leg is the binding figure.
- `FxChainTests` (llvmpipe): bypass and mix 0 are bit-exact pass-through; PATTERN over an output with a chain shows exact pattern
  codes; a history effect is identical on two renderers at the same leader instants from the same start, and a renderer that
  starts 3 s late reports the effect as history (`deterministic:false` with words) rather than claiming equality; a compile held
  open by a test hook never delays a frame (frames are counted while the compile is held — no wall-clock bound).
- `FxVerbTests` (Engine): fields and refusals for every verb (no-ops refused with the reason); slot state `compiling` → `ok` or
  `faulted` in STATE.
- Ten built-in pixel tests at 64×36; `fx.json` and `fx.cards.json` written by a test.

**Verified here.** All (the ES leg after output's merge). **Compile-only.** None.

**Next round.** FFGL 2.x (the in-process host, the probe child and quarantine, the isolated host, the Windows GL island); the ES
fix-up tokenizer (hoisting, int → float); history pre-roll; ISF generators and transitions; presets and linked looks; audition;
the shader editor; the remaining 22 built-ins.

### 3.7 `wp2-audio` — analysis, beat clock, modulation bus, capture (W1)

**Slice.**
1. The analyser (Core, pure): real FFT 1024 Hann at hop 240 (48 kHz → 200 frames/s); Level/Bass/Mid/High by Patterns' formula; a
   16-band filterbank; auto-gain. (6 h)
2. Onsets (log spectral flux, causal peak picking, ≥ 30 ms gap), tap tempo, the beat clock as anchors (`TempoAnchor`, changes
   scheduled 40 ms ahead, phase a pure function of leader time); `TEMPO STATUS/TAP/SET/SOURCE`. (6 h)
3. The modulation bus implementing `IModulationBus` and `IAudioFrames`: sources `audio:{level,bass,mid,high,band/n,onset}`,
   `beat`, `bar`, `phrase`, `lfo:*`; `ParamBinding` evaluation on generators, layers, surfaces and fx slots (ADD, SCALE, REPLACE;
   attack/release smoothing); `MOD BIND/UNBIND/SOURCES/PEEK/BYPASS` with the `FX BIND` and `GENERATOR BIND` aliases; modulated
   values never write the project. (8 h)
4. Capture: the input hub over `IAudioInputs` (a device opened once, fanned out to the analyser and control's LTC decoder); the
   FFmpeg libavdevice backend — **pulse, oss and openal on this Linux build** (no device here → words), **dshow on Windows through
   the same code path**; on Windows, **NAudio.Wasapi 3.1.0** (MIT; it targets `net9.0`, so it sits inside the one `net10.0` TFM,
   every call guarded by `OperatingSystem.IsWindows()`) for input, endpoint loopback ("System sound") and process loopback
   ("Everything but PlayMap", Windows 11+), compile-only here; synthetic and file inputs; `AUDIO INPUTS`, `AUDIO LISTEN`,
   `AUDIO STATUS`. (6 h)

⟂ *(≈ 26 h; + preflight 0.5 h)*

5. *(stretch)* Shared live frames to followers (PMMD on UDP 9945, HMAC keyed from pairing), so every node reacts to the same sound.
6. *(stretch)* Tempo detection (ACF comb and beat tracker); analysis tracks at import (`.pmaa`).

**Preflight.** A listening input is present and not silent when bindings depend on it. **INDEX.** Section `tempo`: TAP, RESYNC,
HALF, DOUBLE, BPM on an encoder.

**Owns.** `src/PlayMap.Core/Audio/**`, `src/PlayMap.Core/Modulation/**`, `src/PlayMap.Media/Audio/**`,
`src/PlayMap.Engine/Audio/**`, `docs/AUDIO.md`.

**Hunks.** None.

**Depends on.** The spine.

**Acceptance.**
- `AnalyserTests`: Patterns' reference vectors → Level/Bass/Mid/High within 1e-4; filterbank responses at the band centres.
- `OnsetTests`: a synthetic kick at 128 BPM → onsets within 10 ms, F1 ≥ 0.95; tap tempo median and outlier rules.
- `BeatClockTests`: anchors 40 ms ahead; beat phase identical on two engines at the same leader instants.
- `ModulationTests`: an lfo-bound parameter gives identical values on two engines; an `audio:bass`-bound opacity follows a bass
  burst within one hop; the project revision does not move.
- `ModVerbTests`: fields and refusals; the aliases route to MOD BIND.
- `CaptureHubTests`: one fake device, two sinks with different channel picks, one open; the libavdevice backend here lists no
  capture device with words. `WasapiInputsTests` (`[WindowsFact]`, Category=Windows, on the CI job): enumeration on the runner
  (which has no audio endpoint) answers an empty list with words and never throws.
- `audio.json` and `audio.cards.json` written by a test.

**Verified here.** All with synthetic and file inputs. **Compile-only.** Capture from a real device; WASAPI (enumeration runs on
CI).

**Next round.** Ableton Link only through a Carabiner process the user installs (GPLv2+; never bundled), MIDI clock in, named
modulators (LFO, envelope), `MOD AUTO` presets, launch quantisation (`AT BEAT|BAR|PHRASE`), discrete inputs for bindings,
`AUDIO CALIBRATE`.

### 3.8 `wp2-fleet` — health, the Eye board, the context for Patterns (W1)

**Slice.**
1. `HealthRecord` (Core), `MachineSampler` (Linux `/proc` and `/sys`, real; Windows PDH compile-only), `HEALTH`, `GET /api/health`
   (an `IWebModule`), `SUBSCRIBE HEALTH` (1 Hz and on a light change; unknown is −1 and grey). (5 h)
2. The fleet monitor: subscribes to every live PlayMap peer's HEALTH (TLS pinned when offered), ≤ 32 peers; stale at 3 s and gone at
   6 s **on an injected `TimeProvider`**; `FLEET` (alias `EYE`) answers every machine as things and links in Patterns' `EyeJson`
   shape with lights and a headline; `GET /api/fleet`; `STATE.fleet`; peer context items with real lights. (5 h)
3. The context for Patterns' Eye: `ShapeContext` orders and budgets items for the Eye's 12-thing window (`show` and `machine` first,
   greens aggregated, devices folded into projectors), links only in the Eye's eleven verbs; hello caps `eye` and `show`. (3 h)
4. `FleetGraph` (Core, pure; fleet.md M3): things, links, deterministic grid, headline, problems queue, lenses; `FLEET
   FOCUS/LENS/NEXT/PREV/RESET` (view verbs); `STATE.eye` in Patterns' shape. (6 h)
5. The Patterns side, prepared, not applied: the P1–P3 change (`KindWords("playmap")`, reading every live peer's context with the
   `context` cap, PlayMap's items on the Video plane) written as a patch under `scratchpad/ext/patterns-eye-p2/` with tests against
   Patterns' `ShowBusView`/`EyeGraph`, built and run in a scratch copy (never in `patterns-svc`); in PlayMap, `PatternsEyeContractTests`
   feed the integrated context through a copy of Patterns' `ShowContext.Parse` and `EyeGraph.Build` (as data, its commit noted)
   and show PlayMap's things lit. (4 h)

⟂ *(≈ 23 h; + preflight 1 h)*

6. *(stretch)* The metrics ring (1-minute aggregates for 24 h) and the `/fleet` web page.

**Preflight.** Every expected peer heard; the monitor's TLS pins valid. **INDEX.** Section `eye`: NEXT/PREV PROBLEM, LENS, and one
key per machine with its light.

**Owns.** `src/PlayMap.Core/Fleet/**`, `src/PlayMap.Net/Bus/{HelloService, PeerTable}.cs`,
`src/PlayMap.Net/Links/{PatternsLink, StagewatchLink, StagewatchTimer}.cs`, `src/PlayMap.Net/Fleet/**`,
`src/PlayMap.Engine/Fleet/**`, `docs/FLEET.md`. (`Core/Time/Clocks.cs`, `Net/Sync/**` and `EngineCore.Sync.cs` are wp2-sync's.)

**Hunks.** None.

**Depends on.** The spine. Reads other packages' context items by kind only.

**Acceptance.**
- `HealthRecordTests`: every category present; unknown → −1 and grey; the light table.
- `MachineSamplerTests` (Linux): CPU, memory, disk and uptime in range; GPU unknown here → grey with words.
  `WindowsSamplerTests` (Category=Windows, CI): PDH counters open or say why.
- `FleetMonitorTests` (two in-process engines over loopback; staleness on a fake `TimeProvider`): B appears in A's FLEET once
  B's first HEALTH push arrives (event-driven); B stopped → stale at 3 s and gone at 6 s of fake time; the peer light is red with
  words.
- `EyeContextTests`: a node with 30 items fits the 12-thing window with show and machine first; every link verb is one of the Eye's
  eleven; `FLEET`/`EYE` equals a golden in Patterns' shape (copied from `patterns-svc` with its commit noted, as data).
- `FleetGraphTests`: golden JSON for a 3-node fixture; the same facts give the same ids and places; the PROBLEMS lens holds reds and
  ambers and their neighbours.
- `PatternsEyeContractTests` (above), and the patch's own Patterns tests green in the scratch copy (their output kept with the patch).
- `fleet.json` and `fleet.cards.json` written by a test.

**Verified here.** All (loopback). **Compile-only.** The Windows sampler (CI runs it). **Outside PlayMap.** Patterns takes P2 (§9).

**Next round.** The metrics ring and `FLEET AT` replay, the `/fleet` page, webhooks and syslog, GPU temperatures through NVML.

### 3.9 `wp2-wall` — the quad on the wall; the score vocabulary, the ledger, re-solve from pins (W1)

**Slice.**
1. `PixelQuad`: `Surface.Pixel` (projector, four corners in raster-normalised units, optional lattice) and `PixelQuadMath`; the
   `pixelsurface` program in the projector pass (homogeneous s,t,q UVs). (6 h)
2. `SURFACE QUAD`, `SURFACE CORNER`, `HANDLE SELECT/NEXT/PREV/NUDGE`, coordinates in wall pixels (`PX`) by default; `LIGHT WALL`
   (binds an output to a display, fullscreen, projector mode with a nominal projector, overlay on); **a new quad shows
   `pattern.uvchecker`** until it gets a source (MadMapper's test card). (4 h)
3. The edit overlay pass (outlines and handles) composited through the warp and not the blend; on while editing; never on air
   while armed. (4 h)
4. `CalibrationScoring` → `QualityScore` (grade A/B/C/F) with **a source** (pins, scan, align, stack) and **the term vocabulary**
   (`ScoreTermCodes`: pin RMS and p95, coverage, conditioning, lens plausibility; for scans dense reprojection RMS, decoded coverage
   and views; for align coverage, seam misregistration and stack convergence), each code with its words template and a fix line
   that parses. Wall fills the pin terms; phonescan and align fill theirs. `SolveUncertaintyAnalysis` (Core) is created here for
   the conditioning term (facade extends it by a hunk). `STATE.calibration`; a `calibration:<pj>` context item. (5 h)
5. The ledger: a `CalibrationSnapshot` on every `PROJECTOR SOLVE` and `SCAN APPLY` (with the scan's score); the first is the
   fallback; `CALIBRATION STATUS/SAVE/ACCEPT/RECALL/FALLBACK/HISTORY/FORGET` (history with Δmm, Δdeg and Δwall px); retention
   (`CalibrationKeep`); `ICalibrationLedger` for align and facade. (5 h)
6. `CALIBRATION RESOLVE <projector>… [STAGE]` from pins: re-solves only the named projectors against the stage, scores the result,
   applies it (or stages it when it fails `CalibrationGate`) with the previous result kept as fallback; nothing else moves. (2 h)

⟂ *(≈ 26 h; + preflight 0.5 h)*

7. *(stretch)* Overlay names and the alignment grid (`OVERLAY GRID`).

**Preflight.** Each projector's grade and a fallback present (a projector with no fallback is amber with `CALIBRATION ACCEPT`).
**INDEX.** Section `calibration`: per projector a score ring, RE-SOLVE, FALLBACK, HISTORY.

**Owns.** `src/PlayMap.Core/Mapping/{PixelQuadMath, QuadHomography}.cs`, `src/PlayMap.Core/Calibration/Scoring/**`,
`src/PlayMap.Core/Calibration/SolveUncertaintyAnalysis.cs`, `src/PlayMap.Render/Projection/StageRenderer.cs`,
`src/PlayMap.Render/Passes/OverlayPass.cs`, `src/PlayMap.Engine/EngineCore.Mapping.cs`, `src/PlayMap.Engine/EngineCore.BlendSolve.cs`,
`src/PlayMap.Engine/Rendering/{StageResources, StageMeshes}.cs`, `src/PlayMap.Engine/Wall/**`, `docs/CALIBRATION.md`.

**Hunks.** `Engine/Scanning/ScanOutcome.cs` (phonescan's; wall lands it in W1, before phonescan starts): SCAN APPLY records a
snapshot. `Core/Scan/ScanSimulator.cs` (phonescan's): `Bump(projector, Δpose)`.

**Depends on.** The spine (the gate refuses CALIBRATION verbs while armed through `ShowClass`).

**Acceptance.**
- `PixelQuadMathTests`: corners ↔ homography, lattice offsets, inversion.
- `PixelSurfaceRenderTests` (llvmpipe): a quad's corners land on the raster within 0.5 px; a checker is projectively correct (no
  affine seam on the diagonal); a new quad reads back the UV checker.
- `QuadVerbTests`: `SURFACE QUAD` with four PX points; `SURFACE CORNER … BY 0.1 0 PX` moves only that corner; armed → refused with
  words.
- `OverlayTests`: drawn through a warped output's warp; off on air while armed (byte-identical to overlay-off).
- `ScoreTests`: each injected fault raises its term with the right words; every fix line parses; a scan-sourced and an
  align-sourced score built from fake term values grade and word correctly.
- `LedgerTests`: snapshots on solve and apply; fallback = first; RECALL restores exactly; HISTORY deltas; retention.
- `ResolveTests` (simulator): bump one of three projectors 2 cm and move its pins to where the wall shows them; `CALIBRATION RESOLVE`
  on it brings it back within 0.5 px RMS; the other two snapshots and poses are untouched; a result under the gate is staged.
- `wall.json` and `wall.cards.json` written by a test.

**Verified here.** All. **Compile-only.** None.

**Next round** (beyond wp2-resolve). The full re-solve ladder (LENS, CODED, DENSE), the dot check on every projector, the drift
monitor, lens read-back triggers, before/after on the wall, the seam term, the report, `HIGHLIGHT`, `PICK PIXEL`, snapping, quads
on the 3D view, warp handles on the wall.

### 3.10 `wp2-multiview` — multi-view, multi-projector, curved (W1, Core only)

The phone scan's Wave B core (phonescan.md M6), built without a browser so the curved, many-projector claim does not wait for the
phone page.

**Slice.**
1. Inverse correspondence and tracks: each view's decode inverted to camera positions on a 16-px projector grid; tracks keyed by
   projector pixel across views. (5 h)
2. Multi-view initialisation and the projector-anchored bundle adjustment: every projector, every view and the camera's intrinsics
   (phone focal prior, projector throw priors) in one robust solve, with `HOLD` keeping chosen projectors fixed. (14 h)
3. Dense per-projector depth from all views, the merged mesh, coverage per projector, and `DropView` (a view's removal and re-solve).
   (7 h)

⟂ *(≈ 26 h)*

4. *(stretch)* Per-projector regions in Core (merged across projectors by 3D overlap) for wp2-multiscan.

**Owns.** `src/PlayMap.Core/Scan/MultiView/**` (with `MultiViewSim`, scenes over the existing `ScanSimulator`),
`tests/PlayMap.Core.Tests/Scan/MultiView/**`, `docs/SCAN-MULTIVIEW.md`.

**Hunks.** `Core/Scan/ScanSimulator.cs` (phonescan's): render a view from a given camera pose, if the public surface lacks it (after
wall's `Bump` in the W1 merge order).

**Depends on.** The spine (Core only; no engine hooks, no STATE).

**Acceptance** (all Core, simulator, the thresholds of phonescan.md M6):
- `MultiViewSolveTests`: *wall-and-cylinder*, 2 projectors, 3 views, phone focal prior 8 % off, projector throw priors 5 % off →
  every projector within **5 mm, 0.05°, throw 0.2 %**; the phone's focal within 0.5 %.
- `DenseDepthTests`: dense surface RMS to the truth mesh recorded (≤ 3 mm expected, tightened after the first run); coverage of the
  lit raster **≥ 95 % with 3 views against ≤ 80 % for the best single view**.
- `DropViewTests`: a view shifted 2 px synthetically raises RMS; dropping it brings RMS under 0.5 px.
- `HoldTests`: a held projector is bit-identical after the solve.

**Verified here.** All (simulator). **Manual (lab).** Real phones and projectors on a real curved surface.

**Next round** (beyond wp2-multiscan). Metric scale (tape, phone depth), TSDF or Poisson meshing, two phones in one view.

### 3.11 `wp2-blind` — blind edit you can see, preflight breadth, fan-out, sound on air (W2, continues show)

**Slice.**
1. Blind edit: `BLIND ON`, `BLIND [STATUS]`, `BLIND OFF`, `BLIND DIFF`, `BLIND COMMIT [NOW|NEXT [<pl>]|ON GO [<pl>]|AT …] [FORCE]`,
   `BLIND DISCARD`; a draft store per session behind `Edits`; `ProjectDiff` (Core, pure) with field-level words; **the draft is
   visible**: `EngineSnapshot.Draft` and `VIEW <v> SHOW editor DRAFT`, where the render picks the draft root for that view only;
   **commits at a boundary**: the pending root picked per frame by `RenderHost.Show`'s compose at the item boundary's leader
   instant (NEXT, ON GO); a commit is one undo step through the lock guard; a live change to an entity the draft also changed
   refuses the commit naming it (no silent merge). (11 h)
2. Preflight breadth (showsafety's rows): mapping (each projector's grade and fallback through `ICalibrationLedger`), sync and
   clock uncertainty, safety (panic plans and their safe looks present; armed expectation), links (Patterns and Stagewatch heard).
   (2 h)
3. Fan-out, show side: `SHOW ARM ALL`, `SHOW DISARM ALL`, `PREFLIGHT ALL` through `IFleetFanout` (wp2-sync implements it) with each
   node's receipt; the report lists one section per node. (2 h)
4. Sound on air: main has `AudioOut` (OpenAL, slaved to a timeline) but no caller in the engine, so clips play silent today. The
   on-air item's audio plays through `AudioOut` slaved to its anchor (`AnchorTimeline`), its gain per buffer = the air's
   `AudioGain` (master × panic), so PANIC fades sound with picture. (4 h)
5. Transitions on a manual GO, NEXT and PREV (carry-over: main draws fades and dips only between scheduled items): the lane keeps
   the outgoing item through the transport's transition window and the resolver mixes both sides in linear light. (3 h)

⟂ *(≈ 22 h)*

6. *(stretch)* Parked edits (`PARK`, `PARKED`, land in order on DISARM).

**Preflight.** The rows of item 2. **INDEX.** Section `show` gains BLIND COMMIT NEXT and DISCARD.

**Owns.** Show's files for this wave, plus `src/PlayMap.Engine/Show/AirAudio.cs`.

**Hunks.** `Engine/Rendering/PreviewViews.cs` (output's): the editor view draws `EngineSnapshot.Draft`.
`Engine/Playback/PlaybackLanes.cs` (control's): the outgoing item kept through a manual transition's window.

**Depends on.** show; wall (the ledger, W1); wp2-sync for the fan-out test (added at the merge, §6.1).

**Acceptance.**
- `BlindTests`: BLIND ON then SET → `PROJECT GET` and live STATE unchanged and `BLIND DIFF` lists `{id, field, from, to}` in words;
  COMMIT → one undo step; `COMMIT NEXT pl…` while an item plays with 4 s left lands at the switch instant, not before (ManualClock);
  `COMMIT ON GO` lands with the next `PLAYLIST GO … AT +200` on the same leader instant; a conflicting live edit → refused naming
  the entity, `FORCE` commits; DISCARD → draft gone.
- `BlindRenderTests` (llvmpipe): the editor view shows the draft colour while the output, read at the same leader instant, shows
  the live colour; at a NEXT commit the output reads the old colour one frame before the instant and the new colour at it.
- `PreflightBreadthTests`: each new row fails on an injected fault and passes when fixed; every fix line parses.
- `AirAudioTests` (OpenAL Soft loopback device): a test-tone item; `PANIC FADE 1000` → the loopback RMS at +500 ms is 0.5 ± 1 % of
  the RMS at +0 ms; after REWIND the item is cued at its top; `PANIC CLEAR` brings the RMS back.
- `ManualTransitionTests` (llvmpipe): `PLAYLIST GO` into an item with a 1 s fade → the read-back at the window's midpoint is a 50/50
  mix ± 1 code.
- `show.json` regenerated (blind and preflight rows) and its cards.

**Verified here.** All. **Compile-only.** None.

**Next round.** Multiple drafts, soft edit leases, panic outcomes on followers' audio beyond the group's air.

### 3.12 `wp2-d3d11` — the D3D11 path run on CI, Spout, per-output latency (W2, continues output)

**Slice.**
1. The DXGI flip path, written to the verified APIs: a D3D11 device on the first Display output's adapter; ANGLE (BSD) loaded
   at run time **by the single name `av_libglesv2`** — the file `Avalonia.Angle.Windows.Natives` puts beside the exe — through
   `NativeLibrary.TryLoad(name, assembly, searchPath)`, with the EGL entry points resolved from that handle; ANGLE's extensions
   checked in **three lists** (client: `EGL_EXT_platform_base`, `EGL_EXT_platform_device`, `EGL_EXT_device_query`,
   `EGL_ANGLE_device_creation`, `EGL_ANGLE_device_creation_d3d11`; device: `EGL_ANGLE_device_d3d11`; display:
   `EGL_ANGLE_d3d_texture_client_buffer`, `EGL_KHR_surfaceless_context`), the refusal naming the missing one; one waitable
   `FLIP_DISCARD` swap chain per Display output; **buffer 0 wrapped once per swap chain** (again after `ResizeBuffers` or device
   loss; the cache holds exactly one entry); frame statistics into `VblankClock`; the refresh from `QueryDisplayConfig`
   (`DISPLAYCONFIG_PATH_TARGET_INFO.refreshRate`, `targetVideoSignalInfo.vSyncFreq`); QPC → ns as
   `(qpc / f) * 1e9 + (qpc % f) * 1e9 / f`; `DwmFlush` never on the render thread (a helper thread nudges the compositor, or the
   clock takes swap-return samples with evidence "none"); the fuse. **`PRESENT PATH AUTO` is GL on every platform this round; DXGI
   runs only on an explicit `PRESENT PATH DXGI`, shown as experimental in STATE**, until the WARP job and a lab run pass. The
   device is exposed as `IGpuShare` (`GpuApi.D3D11`). (9 h)
2. The Windows CI leg: `DxgiWarpPresenterTests` (`[WindowsFact]`, Category=Windows): the device on WARP; a `FLIP_DISCARD` swap chain
   on a hidden window; 120 presents; `GetFrameStatistics` read (or the fuse falling back with words when WARP gives none); the frame
   read back through ANGLE on WARP. Layout and slot goldens: `Unsafe.SizeOf` and field offsets of every hand-declared struct and the
   vtable index of every hand-declared COM method, against the Windows SDK headers' values (mingw-w64 headers when apt has them,
   else copied once with the citation); `AngleAssetTests` (the loader's file names equal the package's asset list read from the
   NuGet cache); a fake `IGpuPlatform` failing at each start stage (fuse words, GL fallback). (4 h)
3. Spout send and receive on `IGpuShare` (compile-only, texshare.md): D3D11 shared handle, `OpenSharedResource1`, the keyed mutex
   where present, the sender-name memory map; **receive in Spout's order** (access mutex or keyed mutex at 0 ms → read the frame
   semaphore → `CopyResource` → release → commit `lastCount` only after a successful copy); a `SpoutProvider` registered as videoio's
   live input and GPU send provider; the protocol layer with golden tests against a fake kernel (including the count advancing
   while the mutex is busy: the next render copies frame N); on CI a WARP in-process sender → receiver round trip of a 64×36 chart
   when WARP allows shared resources, otherwise the goldens only, said in the test output. (8 h)
4. Per-output latency (carry-over): `Output.LatencyMs`, a whole-frame delay ring, the node's air latency = the largest output's. (3 h)

⟂ *(≈ 24 h; + preflight 0.5 h)*

5. *(stretch)* 10-bit output (`Rgb10A2`) on the GL path.
6. *(stretch)* Device-lost rebuild on the DXGI path.

**Preflight.** The present path, its fuse and why (a blown fuse is amber with `PRESENT RESET`).

**Owns.** Output's files for this wave, plus `src/PlayMap.Media/Video/Spout/**` and `src/PlayMap.Render/Video/Spout*.cs`.

**Hunks.** None (Spout registers through videoio's provider lists in `EngineServices.VideoIo.cs`, after videoio in the W2 merge order).

**Depends on.** output; videoio (merge order, providers); decode (`IGpuShare` type, W1).

**Acceptance.** `DxgiWarpPresenterTests`, `DxgiLayoutTests`, `AngleAssetTests` and `GpuPlatformFaultTests` (item 2);
`SpoutProtocolTests` (fake kernel goldens, including the busy-mutex case) and `SpoutWarpTests` (Category=Windows on CI: the WARP
round trip where WARP shares resources, else the goldens path, printing which ran — never a skip); `PresentPathTests` (Linux: `AUTO` → GL with words; `DXGI` refused
"Windows only"); `PresentFuseTests` (the fuse's transitions and words in STATE, the reset); `LatencyRingTests` (two outputs of 17
and 50 ms → a 2-frame ring at 60 Hz; the node's air latency 50 ms); `output.json` regenerated.

**Verified here.** The fuse, the goldens, the Spout protocol layer and latency. **On CI (Windows, WARP).** The DXGI presenter, its
statistics or the fuse's fallback, the ANGLE read-back, Spout's round trip where WARP shares. **Manual (lab).** A real GPU and
display, Spout with Resolume and TouchDesigner.

**Next round.** DXGI as `AUTO` once lab-proven, PresentMon, the NVIDIA DRS stop-gap, HDR, Spout's WGL bridge, Syphon.

### 3.13 `wp2-zerocopy` — the decode-path ladder and hardware imports (W2, continues decode)

**Slice.**
1. The decode-path ladder (zerocopy.md M5–M6, M8): `DecodePath` choice and demotion (pure, with fakes), the starvation watch,
   padded surfaces (`uCrop`/`uMaxUv` in `yuv`), `DECODE STATUS` and `DECODE POLICY` reporting **the path actually used** per source
   (so "software decode" is said honestly). (7 h)
2. Linux VAAPI → GL by dmabuf, compile-only on output's EGL context: `av_hwframe_map` to DRM PRIME, `eglCreateImageKHR` per plane,
   the import fence; **the GL import half tested on llvmpipe** by importing a dmabuf exported from a GL texture through
   `EGL_MESA_image_dma_buf_export` where Mesa offers it, otherwise said compile-only. (5 h)
3. Windows D3D11VA, compile-only: the hardware device made on `IGpuShare`'s `ID3D11Device` (wp2-d3d11), frames imported through
   ANGLE's `EGL_ANGLE_d3d_texture_client_buffer`; layout goldens; a Windows-category test that the hw device opens on WARP or says
   why. (3 h)
4. The upload arena (persistently mapped unpack buffer per context; `NodeSettings.Decode.UploadArenaMb`), if not reached by
   decode. (3 h)

⟂ *(≈ 18 h; + preflight 1 h)*

5. *(stretch)* Raw HAP straight from disk into the arena.
6. *(stretch)* CUDA → GL one-copy path with a fake libcuda.

**Preflight.** "Show item decodes in software at ≥ 4K" (amber, fix `MEDIA OPTIMISE md…`).

**Owns.** Decode's files for this wave, plus `src/PlayMap.Media/Hw/**`, `src/PlayMap.Render/Gpu/{DmaBufImporter, AngleImporter}.cs`.

**Hunks.** `ShaderLibrary.cs`, the `yuv` program: `uCrop`/`uMaxUv`.

**Depends on.** decode; output (EGL contexts); wp2-d3d11 for the Windows device (compile-only against the interface in parallel).

**Acceptance.** `DecodeLadderTests` (fakes: every demotion with its words; the path in STATE equals the path used);
`PaddedSurfaceTests` (llvmpipe: a 1920×1088 coded frame shows no padding rows); `DmaBufImportTests` (llvmpipe import of an exported
dmabuf equals the texture ± 0 codes, or the test says the extension is absent); `D3d11vaWarpTests` (Category=Windows, CI);
`UploadArenaTests` (allocation-free, epochs); `decode.json` regenerated.

**Verified here.** The ladder, padded surfaces, the arena, the dmabuf import half where Mesa exports. **Compile-only.** VAAPI decode
(no VA driver here), D3D11VA (opens on CI's WARP or says why). **Manual (lab).** Intel/AMD VAAPI and NVIDIA/Intel D3D11VA.

**Next round.** The EGLImage cache, direct sampling, the proof harness per driver, the per-item decode override.

### 3.14 `wp2-companion-module` — the Companion 5.1 module (W2, continues companion)

The module lives in `integrations/companion-module-playmap/` (TypeScript, Node 22, `@companion-module/base` ~2.2.1, manifest
`jammin808-playmap`, API pinned by the research). A module cannot create pages or place buttons: everything here is preset
sections, template groups and variables, rebuilt through `setPresetDefinitions`; the operator installs the Live slot block once.

**Slice** (companion.md M10 and M11).
1. Connection: host and port, the token as **`secret-text`**, `bonjour-device` over `_playmap._tcp` (wp2-companion's responder);
   the WS line link with reconnect and jittered backoff, the 2 s `PING` watchdog and clock exchange; **the stale guard** (no answer
   for 4 s → every key dims with a mark; 10 s → reconnect; state emptied and feedbacks rechecked). (5 h)
2. State: diffed STATE and INDEX → variables; **10 Hz clock interpolation** between pushes from `positionMs`, `rate` and `at`;
   **feedback-based local variables**. (4 h)
3. Actions generated from `VERBS` and `params`; smart actions (GO with `EXPECT`); **press shapes from `press`**: PANIC, BLACKOUT, GO,
   NEXT and PREV instant on generated keys, others from risk; **`@c<n>` receipts** (amber while accepted, cleared when applied, red
   on `ERR` with the words in `last_refusal`); **action results with `storeResult` and `notes`**; **`$(this:delta)` encoders with
   the shadow value** (sending `NUDGE`/`… BY <delta>`); learn from STATE. (6 h)
4. Feedbacks: boolean and value (`label`, `light`, `state_word`, `progress`, `remaining`, `thumb`, `score`); **value feedbacks as
   `$(this:value)` style overrides**; **`event_pulse` feedbacks** for triggers from `EVENT`s; Show-Mode locks from `WHOAMI`. (4 h)
5. Presets: **template preset groups keyed by entity id** (`pl.<id>`, so linked presets survive renames and reorders), **layered
   presets and composites**, **`alternatives` fallbacks**; sections built from INDEX — playlists, the show group (ARM, PANIC per
   outcome), surfaces on encoders, and every package's contributed section with its hint; **the Live page**: the installed block of
   32 slots re-maps from INDEX through `$(page:pm_live)`, frozen while armed. (5 h)
6. `HELP.md` with recipes (oscillate, colorMix, page variables, button references) using only 5.1 functions; packaging. (2 h)

⟂ *(≈ 26 h)*

7. *(stretch)* The typed recorder: `RECORD ON` → `ACTION <line>` → typed actions through a TypeScript port of `VerbCatalog.Match`.

**Owns.** `integrations/companion-module-playmap/**`, `tests/PlayMap.Net.Tests/Companion/CompanionContractTests.cs`.

**Hunks.** None.

**Depends on.** companion (W1). Fixtures of W1 packages are real; later packages' sections are generic (from hints) and are
checked at the integration pass.

**Acceptance** (`npm test` here, Node 22):
- every preset passes `@companion-module/host`'s `sanitisePresetDefinitions` unchanged (no action, feedback, option or element
  dropped); `companion-module-check` passes and `companion-module-build` makes `pkg.tgz`;
- `actions.test.ts` (from a VERBS fixture regenerated at merge): PANIC presets have a `down` action and no duration group; every
  action's line parses (the C# contract test reads `test/lines.txt`);
- `presets.test.ts`: INDEX fixture → preset structure, template values and the slot → entity map; **a rename and a reorder leave a
  linked template preset's id and checksum unchanged**;
- `encoder.test.ts`: `$(this:delta)` sends the step and the shadow value never passes the range;
- `override.test.ts`: a value feedback's `$(this:value)` override sets the key's text and colour;
- `receipts.test.ts`: `@c<n>` amber → cleared → red with words; `stale.test.ts`: silence → dimmed at 4 s, reconnect at 10 s (fake
  timers);
- `recipes.test.ts`: every HELP.md recipe uses only Companion 5.1 functions (the list pinned with the API);
- `e2e-engine.test.ts` against `tools/PlayMap.Node engine --headless` (paired over `ws://` with a token; actions make a playlist; a
  template group appears after the INDEX push; the GO key's feedback flips when STATE moves; killing the engine dims every key;
  restarting re-pairs);
- `CompanionContractTests` (C#): `verbs.json` equals `VerbJson.All`; every STATE path the module reads exists in a fixture; module
  versions equal in `package.json`, `manifest.json` and `MODULE_VERSION`; the palette is Patterns', byte-equal.

**Verified here.** The module, including the real-engine e2e. **Manual (lab).** A real Companion 5.1 and a Stream Deck.

**Next round.** Key pages authored on the desk, `COMPANION PAGE`/`ADD` provisioning, Show Bus hello discovery, the back-port offer
to Patterns' module.

### 3.15 `wp2-sync` — anchors, clock, vblank reports, the group's air (W2)

**Slice.**
1. Anchors reach the followers (framelock F1): `SUBSCRIBE ANCHORS`, `ANCHOR <group> <playlist> <json>` pushes in order; a follower
   adopts each through its own `PLAYLIST ANCHOR`; the sync role, group, leader and follow-blackout persisted
   (`NodeSettings.Sync`) and re-entered at start; `NetworkClock.Hold` free-wheels on leader loss and followers keep switching their
   own items. (8 h)
2. Following never makes a playlist jump (F2): `NetworkClock` slews corrections under 1 ms at ≤ 200 ppm, steps larger ones and
   raises `Stepped(Δ)`; the lane rebases every non-group anchor and every scheduled `AT` by +Δ; `NetworkClock.Promote()` continues
   the leader's time line (used by wp2-backup). (6 h)
3. Vblank reports (F3): PMVB on UDP 9942 — **today's clock-sync socket, which this package owns**, demuxed by the 4-byte magic,
   fail closed; the reporter fed by output's `VblankClock`; the leader's aggregation (per-output ppm and phase, per-pair phase,
   uncertainty and next slip, the group verdict, mixed-rate detection); `STATE.sync.outputs/pairs/verdict`; `SYNC STATUS`
   extended; `sync:<group>` context items. (10 h)
4. The group's air (showsafety S2, sync side): `IFleetFanout`. On a group leader, `PANIC`, `PANIC CLEAR`, `BLACKOUT` and `MASTER`
   go to every follower and backup as anchored air lines (`ANCHOR <group> @air {"line":…,"leaderNs":…}`), which each member runs
   at the same leader instant; `SHOW ARM|DISARM ALL` and `PREFLIGHT ALL` use the same path with receipts; `HERE` keeps a line
   local. (2 h)

⟂ *(≈ 26 h; + preflight 0.5 h)*

5. *(stretch)* The group's air latency (`SYNC LATENCY`, `ANCHOR <group> @latency`).

**Preflight.** Clock uncertainty under the group's bound, the leader heard, no mixed rates. **INDEX.** Section `sync`: the
group's verdict key.

**Owns.** `src/PlayMap.Core/Time/Clocks.cs`, `src/PlayMap.Net/Sync/**`, `src/PlayMap.Engine/EngineCore.Sync.cs` (the Sync hooks
live there), `src/PlayMap.Engine/Sync/**`, `src/PlayMap.Core/Bus/NodeSettings.Sync.cs`, `docs/SYNC.md`.

**Hunks.** None.

**Depends on.** output (`VblankClock`, the lock machine); show (PANIC and MASTER run on followers).

**Acceptance.**
- `AnchorFeedTests` (two engines, ManualClock, offset `NetworkClock`): a leader GO → the follower's anchor at the same leader instant;
  leader killed → the follower free-wheels and switches its next item at the anchored instant; a follower restart re-enters follow;
  **`PANIC FADE 1000` on the leader → both nodes' level 0.5 ± 1e-6 at +500 ms**; BLACKOUT on the leader blacks both on one frame.
- `ClockSlewTests`: a node plays a non-group playlist, then follows a leader 3 h 12 min ahead: the media position is continuous within
  1 ms across the step; leader time read in a tight loop never runs backwards and never faster than 1 + 200 ppm + drift while
  slewing.
- `PmvbCodecTests`: round trip; every malformed byte refused; a 100 k-packet `Pcg32` fuzz accepts none.
- `SyncBoardTests`: aggregation against closed form (phase within 1 µs, slip time within 0.1 %); 59.94 + 60 → `mixedRate:true` and
  amber; two in-process nodes over loopback list both nodes' outputs in the leader's `sync.outputs` (lock-step on report arrival,
  not wall time).
- `sync.json` and `sync.cards.json` (the `SYNC STATUS` fixture) written by a test.

**Verified here.** All (loopback, synthetic vblanks). **Manual (lab).** Real displays and boards.

**Next round.** `SYNC LATENCY` if not reached, succession lists, the frame code and certificate.

### 3.16 `wp2-content` — particles, fractals, reactive (W2, merged after audio)

**Slice.**
1. Content time: `Generator.OriginNs` stamped by `GENERATOR NEW` and `RETIME`; item time for a generator a playlist routes; every
   generator a pure function of (show time − origin). (4 h)
2. `STING`, `STING CLEAR`, `STING LIST` (carry-over: the render host's impulses on the wire), `STATE.content.sting`. (2 h)
3. The GPU particle engine and `particles.flow`: state in `Rgba32F` textures, positions a function of time (per-lifetime re-seed,
   bounded history), 256 k by default and up to 1 M; curl-noise flow; emission from the canvas's luminance. (9 h)
4. Raymarched fractals `fractal.mandelbulb`, `fractal.mandelbox`, `fractal.kifs` on one raymarcher, with CPU distance-estimator
   twins for tests; render scale. (6 h)
5. Reactive: `reactive.scene` and the fractals gain `sound` (0–1; 0 for old projects) reading `audio:level`/`audio:bass` through
   `IModulationBus`, capped by `FlashGuard`. (3 h)

⟂ *(≈ 24 h; + preflight 0.5 h)*

6. *(stretch)* `particles.image`; `GENERATOR PRESETS/RECALL/SAVE` with 30 curated presets.
7. *(stretch)* Deep zoom for the Mandelbrot family.

**Preflight.** Every on-air generator within its GPU budget. **INDEX.** Section `content`: STING per preset, RETIME per generator.

**Owns.** `src/PlayMap.Core/Content/**`, `src/PlayMap.Render/Generators/**`, `src/PlayMap.Render/Gpu/GlDevice.Content.cs`,
`src/PlayMap.Engine/EngineCore.Content.cs`, `src/PlayMap.Engine/Content/**`, `docs/CONTENT.md`.

**Hunks.** `Core/Playback/PlaylistCursor.cs` (control's): item time for routed generators.

**Depends on.** control (hunk order); audio (W1) for the reactive end-to-end test.

**Acceptance.** `ContentTimeTests` (a generator's frame at leader t is pixel-identical on two engines started 3 s apart; RETIME shifts
it); `ParticleDeterminismTests` (`particles.flow` at t = 10 s after a seek equals playing from 0, byte-equal at 64 k particles);
`FractalTests` (GPU depth equals the CPU twin at 16 probes; a 128×72 golden per kind); `ReactiveTests` (against audio's real bus: a
bass burst raises brightness within the FlashGuard cap; `sound` 0 equals the old render); `StingTests`; `content.json` and its cards.

**Verified here.** All (particle counts lowered to 64 k on llvmpipe). **Compile-only.** None.

**Next round.** Stage spaces and stage particles, crease/grow/shadowlight, `julia4d` and `apollonian`, FSR upscaling, fractal flames,
morphs and the curated presets, costs and the governor, camera reactive, fluid, trails, preroll, `GENERATOR NEW … WITH`.

### 3.17 `wp2-optimise` — optimise on import (W2, after decode)

**Slice.**
1. `OptimisePlanner` (Core, pure): Keep / Lossless / Encode / Ask / Never, with the recipe and the reason; default rules, in order:
   **HDR (PQ, HLG or ≥ 10-bit wide gamut) → Keep, with words "a HAP HDR encoder arrives next round"**; long-GOP above N px → HAP Q;
   alpha → HAP Q Alpha; stills kept; `VersionChoice` per node. (5 h)
2. The HAP encoder: `IHapBlockEncoder` (Core seam) and `GlHapEncoder` (Render) — fragment programs for BC1, BC3, YCoCg-BC3, BC4 on a
   hidden context — with the packer through decode's `HapFrameWriter`. (8 h)
3. The job service (engine extension `optimise`): a queue persisted in `NodeHome/optimise/jobs.json`; `queued → planning → encoding →
   verifying → done | failed | cancelled`; `MEDIA OPTIMISE … [TO hap|hap-alpha|hap-q|hap-q-alpha]`, `MEDIA OPTIMISE CANCEL`,
   `MEDIA JOBS`; the worker is its own process, `PlayMap.Node optimise-worker` (`Host/EngineEntry.Optimise.cs`; one job at a time,
   JSON lines over stdin/stdout, restarted with backoff). (7 h)
4. Verify and record: first, middle and last frames of the result against the source, PSNR over the recipe's floor or failed with
   words; **the source's audio stream-copied into the HAP `.mov` with its original timestamps** (the mov muxer takes AAC and PCM);
   the `MediaVersion` recorded as one edit. (4 h)
5. Playing versions: `MediaBackend` opens the version `VersionChoice` names for this node (`use: auto`), `MEDIA USE` and `MEDIA
   VERSIONS`; a change takes effect at the next open, never mid-air. (2 h)
6. The import hook and the governor: `MEDIA IMPORT` runs the planner under `MEDIA RULE MODE AUTO|NEVER`; the governor is pure:
   armed → waiting, playing → trickle (one worker at below-normal priority), idle → run. (2 h)

⟂ *(≈ 28 h; + preflight 0.5 h)*

7. *(stretch)* `MEDIA RULE MODE ASK`: a suggestion with its accept line (`STATE.media.optimise`).
8. *(stretch)* The lossless rewriter; `MEDIA ESTIMATE`; `MEDIA VERSION REMOVE`.

**Preflight.** Show items that still decode on the CPU at ≥ 4K and could be optimised. **INDEX.** None.

**Owns.** `src/PlayMap.Core/Media/Optimise/**`, `src/PlayMap.Render/Encode/**`, `src/PlayMap.Engine/Optimise/**`,
`src/PlayMap.Engine/Host/EngineEntry.Optimise.cs`, `src/PlayMap.Media/MediaLibrary.cs`, `docs/OPTIMISE.md`.

**Hunks.** `Engine/Host/MediaBackend.cs` (decode's, after zerocopy's changes): one `VersionChoice` call at open.

**Depends on.** decode; output (the encoder's hidden context in the worker comes through output's `IGpuPlatform`, W1).

**Acceptance.**
- `OptimisePlannerTests`: a table row per rule → decision and reason words, including the HDR row ahead of the long-GOP row.
- `HapEncoderTests` (llvmpipe): `testsrc2` frames generated in the test with lavfi; **PSNR ≥ FFmpeg's own `hap`/`hap_q` encoder's on
  the same frame − 0.5 dB**, and on the noisy variant HAP ≥ 30 dB and HAP Q ≥ 36 dB; two runs bit-identical.
- `OptimiseJobTests` (Engine, the real worker through `tools/PlayMap.Node`): a 2 s 1080p clip made in the test with LGPL-compatible
  encoders (`libopenh264` or `mpeg4`, never `libx264`) and AAC or PCM audio, rules `auto` → a verified version with its PSNR →
  playback opens it (`STATE.decode.sources[].codec == "hap_q"`); **ffprobe: audio start and duration equal the source's within 1 ms**;
  killing the worker mid-job restarts it and the job resumes or fails with words; ARM → the governor waits and nothing starts;
  CANCEL removes partial files.
- `optimise.json` and its cards written by a test.

**Verified here.** All. **Compile-only.** None.

**Next round.** Keyframe-aligned segments, `MEDIA FETCH` of each node's own pick with dedupe (wp2-backup fetches a version by hash
this round), rule editing, the farm over idle peers, stale versions, the scrub strip, watch folders, hardware long-GOP targets,
VMAF, show-quality BC7/BC6H encoders (HAP R and HAP HDR from our own encoder).

### 3.18 `wp2-videoio` — OMT and NDI in and out (W2)

**Slice.**
1. The engine side of the video I/O spine over the spine's contracts (`SPINE.md` §3.8, §2.3 `VideoIoModel`): `LiveUri` (`ndi:`,
   `omt:`, `spout:`), a warm input pool, provider lists in `EngineServices.VideoIo` (wp2-d3d11 adds Spout's), live URIs routed in
   `MediaBackend.Open`, `INPUT ADD`, `INPUT LIST` (removal through the generic `REMOVE`), `SEND ADD NDI|OMT|SPOUT <name> <tap> …`,
   `SEND REMOVE`, `SEND LIST`, `STATE.videoio.{ndi,omt,spout,inputs,sends,discovered}`. (5 h)
2. GPU unpack of UYVY, UYVA, P216 and PA16 (each layout's planes as `SPINE.md` §3.1 documents them) and the GPU pack of UYVY, UYVA and
   BGRA with a send read-back ring, from any tap (output, canvas, layer). (6 h)
3. OMT in and out: libomtnet vendored as its own project, `src/ThirdParty/libomtnet/PlayMap.ThirdParty.Omt.csproj` (the spine's
   nested props: analyzers off, warnings not errors, no banned list), referenced by Media; patches P1–P4 from the bench, P5's
   in-memory configuration and **P6: `Thread.Abort` in `OMTDiscoveryAvahi` (obsolete, and `PlatformNotSupportedException` on
   .NET 10) replaced by a stop flag and a join**, all listed in `PATCHES.md`; libvmx loaded at run time (built per platform; here from source with clang in test setup);
   a real loopback. (7 h)
4. NDI 6.3 through the user's runtime: the loader (`PLAYMAP_NDI_DIR`, the runtime's own variable), find, receive (capture thread),
   send (async ring), tally both ways, colour info; **the Discovery Server address and groups** from `NodeSettings.VideoIo`
   (`NdiDiscovery`, `NdiGroups`) passed to `NDIlib_find_create_v2` and `NDIlib_send_create`, and **the 6.3 send and receive
   advertisers** created when the runtime reports 6.3 or later; tests against a fake runtime compiled from the MIT headers in test
   setup; the licence obligations (no NDI binary in our output, the notice and the trademark line). (9 h)

⟂ *(≈ 27 h; + preflight 0.5 h)*

5. *(stretch)* The loss policy (stale at 3 periods, lost at `LostMs`; hold, black or fallback) and backup inputs.
6. *(stretch)* P216 and PA16 packing (RGBA16 targets where `EXT_texture_norm16` exists, RGBA16F otherwise, per `GlCaps`).

**Preflight.** Each runtime the show uses is present; every input on air delivering. **INDEX.** Section `video`: per input SWITCH
and RECONNECT, per send ENABLE.

**Owns.** `src/PlayMap.Media/Video/**` except `Spout/**`, `src/ThirdParty/libomtnet/**`, `src/PlayMap.Core/Media/LiveVideo.cs`
(after the spine), `src/PlayMap.Render/Video/Packed*.cs`, `src/PlayMap.Render/Passes/SendStage.cs`,
`src/PlayMap.Render/Engine/SourceResolver.cs`, `src/PlayMap.Engine/Rendering/{EngineSources, SourceLeases}.cs`,
`src/PlayMap.Engine/VideoIo/**`, `docs/VIDEO.md`.

**Hunks.** `Engine/Host/MediaBackend.cs` (decode's, after optimise's): live URI routing. `Render/Video/VideoTextureSet.cs`
(decode's, after zerocopy's): the packed-layout branch.

**Depends on.** decode (hunk order).

**Acceptance.**
- `PackUnpackTests` (llvmpipe): UYVY, UYVA, P216, PA16 and BGRA charts unpack within 1 code (8-bit) or 4 codes (16-bit); UYVY, UYVA and
  BGRA pack and read back equal to the CPU reference; `LayoutSizeTests`: 1920×1080 frame sizes per layout equal NDI's (UYVY
  4 147 200 bytes, P216 8 294 400).
- `OmtLoopbackTests`: an output tap sent at 1080p UYVY and received as an input shows on a surface — **lock-step**: frames counted
  as delivered with no real-time pacing, in order, luma error ≤ 1 code; INPUT and SEND STATE fields.
- `OmtDiscoveryTests`: with no avahi-daemon (as here) discovery starts, reports `mdns:"none"` and disposes cleanly (no abort, no
  `PlatformNotSupportedException`).
- `NdiFakeRuntimeTests`: the loader finds the fake; find lists two sources and receives the discovery server and groups; receive
  delivers timestamped frames; the send ring publishes; tally both ways; the advertisers are created on a 6.3 runtime and not on
  6.2; a missing runtime refuses with words naming `PLAYMAP_NDI_DIR` and where to get it. `NdiRealRuntimeTests` (opt-in on
  `PLAYMAP_NDI_DIR` holding a real runtime): a loopback send → receive of 120 frames.
- `videoio.json` and its cards written by a test.

**Verified here.** OMT for real (loopback), NDI against the fake runtime, GPU pack/unpack. **Opt-in.** NDI against a real runtime
the user installs. **Manual (lab).** NDI and OMT across machines.

**Next round.** Syphon, multiview sends, `LiveCamera` for scans over NDI/OMT, smooth and locked pacing, PTZ, discovery thumbnails,
failover sends, hosting an OMT discovery server, naming sources after Show Bus peers, capture cards.

### 3.19 `wp2-led` — LED pixel mapping (W2, after control)

**Slice.**
1. Codecs: Art-Net ArtDmx, ArtSync, ArtPoll (the 22-byte form with EstaMan and Oem; receivers accept ≥ 14) and ArtPollReply
   (RefreshRate at 226–227, length-checked) at the Art-Net 4 offsets (led.md §3.6); sACN data, sync and termination; DDP data and push
   — byte-exact against hand-built vectors. (4 h)
2. Sampling spaces Canvas, Output and Surface (UV); the two-pass GPU pipeline sample → patch (linear-light area sampling, colour
   orders, RGBW extraction, 16-bit coarse/fine, gamma, white balance, brightness). (8 h)
3. The sender: a per-node rate on the leader-time grid, emission at light instant − node latency, the latch (ArtSync, sACN sync, DDP
   push), keep-alive and termination; Art-Net through control's `EngineServices.ArtNet`; a WLED node (`Vendor == "wled"`) gets 8-bit
   DDP only (0x0B/0x1B) and a 16-bit fixture type on it is refused with words. (5 h)
4. Patching: `LED AUTOPATCH`, `LED PATCH`, overlap and bandwidth conflicts refused unless `FORCE`. (4 h)
5. `LED NEW` (line, grid, ring fixtures), `LED LIST`, `LED TEST`, `LED LOCATE`; the air rules (blackout before content, freeze holds,
   `LED LEVEL`, `LED OUTPUT OFF` = one black frame then silence); `STATE.led`, context, hello cap `led`. (4 h)

⟂ *(≈ 25 h; + preflight 0.5 h)*

6. *(stretch)* `LED IMPORT … AS CSV`. 7. *(stretch)* `LED DISCOVER`: ArtPoll discovery and a WLED probe by address.

**Preflight.** Every patched node reachable (ArtPollReply or DDP status heard). **INDEX.** Section `led`: OUTPUT, LEVEL on an encoder,
TEST.

**Owns.** `src/PlayMap.Core/Led/**`, `src/PlayMap.Net/Led/**`, `src/PlayMap.Render/Led/**`, `src/PlayMap.Engine/Led/**`, `docs/LED.md`.

**Hunks.** None.

**Depends on.** control (the Art-Net endpoint).

**Acceptance.** `LedCodecTests` (byte-exact vectors at the spec offsets); `LedSamplingTests` (llvmpipe: payload bytes equal the CPU
reference in every space and colour order; the RGBW table); `LedSenderTests` (fake nodes on the slot's loopback addresses at the
spec ports, ManualClock: packets on the rate grid; the latch; 1 Hz keep-alive; termination; BLACKOUT → the next frame all zero;
WLED forced to 8-bit); `LedPatchTests`; two engines at the same leader instants send identical payloads; `led.json` and its cards.

**Verified here.** All (loopback UDP, llvmpipe). **Manual (lab).** Real Art-Net/sACN nodes and WLED controllers.

**Next round.** GDTF and MVR import, the World sampling space, the power cap, camera auto-locate, redundancy, sACN discovery,
temporal dither, WLED by mDNS (on wp2-companion's responder), the desk's LED tool and patch editor.

### 3.20 `wp2-phonescan` — the phone as a local scan camera (W2)

**Slice.**
1. The `/scan` web module (`IWebModule`; embedded page, strict CSP); `PHONE INVITE` (a one-time ticket in the URL fragment, a QR
   code from Core's own encoder, the URL and fingerprint words); `PHONE LIST`; tickets expire; the phone's session is a capability,
   never the node's token. (5 h)
2. The camera page (plain JS) with both exposure profiles (phonescan.md M3): the secure-context check with words, the back camera;
   **the lock profile** (Chrome Android: settle on a probe, apply exposure explicitly, lock focus and white balance, verify) and
   **the steady profile** (iOS Safari or any camera without the controls: no full white or black in the coded run, synthesised
   references, per-frame gain normalisation, drift measured), with Core's reference synthesis, gain normaliser and the simulator's
   AE option. (9 h)
3. The upload protocol `playmap.camera.v1` with observed pairing (M4 core): JSON control and binary `PMF1` frames over `/scan/ws`;
   every pattern carries its **tag** (4×4 cells, 7 differential pairs: 6 bits and parity) at 2–3 sites found by a cell-locate run;
   **`seen` is a tag match at every valid site in two consecutive agreeing frames**; rolling-shutter mixes (top site *i*, bottom
   *i−1*) are rejected; timing is the cross-check. (8 h)
4. `PhoneCamera : IScanCamera` feeding the existing `ScanRunner` (one view): `SCAN START phone:<id>`. (4 h)

⟂ *(≈ 26 h)*

5. *(stretch)* `PHONE FORGET`; the wake lock.

**Preflight.** A scan planned on a phone that is not connected (amber, with `PHONE INVITE`). **INDEX.** Section `scan`: SCAN NEXT
and CANCEL per phone.

**Owns.** `src/PlayMap.Net/Phone/**` (the hub and `www/`), `src/PlayMap.Core/Phone/**` (protocol, PMF1, QR, tags),
`src/PlayMap.Core/Scan/**` except `MultiView/**`, `src/PlayMap.Core/Vision/**`, `src/PlayMap.Vision/**`,
`src/PlayMap.Engine/Scanning/**`, `src/PlayMap.Engine/Phone/**`, `tests/e2e/phonescan/**`, `docs/PHONESCAN.md`.

**Hunks.** None (wall's `ScanOutcome` and `ScanSimulator` hunks and multiview's landed in W1; facade's land after).

**Depends on.** wall and multiview (W1, merged).

**Acceptance.**
- `PhoneInviteTests`: a ticket works once and expires (fake `TimeProvider`); the QR decodes to the URL; the CSP header is present;
  `/scan/ws` without a ticket is refused; no request URL, header or body carries the ticket.
- `Pmf1Tests`: codec round trip, CRC checked.
- `TagCodecTests`: encode/decode round trip; every single-bit flip caught by parity; a frame whose top site reads *i* and bottom
  *i−1* is rejected; the page's JS decoder (Node 22 test) agrees with Core's encoder on rendered frames.
- `SteadyProfileTests` (simulator): a camera whose gain random-walks 1 %/frame (AE hunting) decodes ≥ 90 % of the pixels the locked
  profile decodes, and the pose stays within 2× the locked thresholds.
- `PhoneCameraTests` (fake WS client): lock-step capture paced by `seen`; late frames, a dropped frame and a half-and-half frame give
  0 mispaired patterns; a phone that stops sending puts words in STATE and stops the scan cleanly.
- `phonescan.json` and its cards written by a test.

**Verified here.** All (the browser loop arrives in wp2-multiscan). **Manual (lab).** Real phones (Android Chrome, iOS Safari).

**Next round** (beyond wp2-multiscan). The node-local CA, USB mode, averaging and ROI, colour frames for align, a native iOS
client with LiDAR, WebTransport, two phones in one view, the desk's phone path.

### 3.21 `wp2-align` — camera auto-align of flat screens (W2)

**Slice.**
1. Identify: per-output white and black flashes captured **in sequence** (grouping into shared runs is next round) → each output's
   camera footprint; its own `AlignRunner` over `IScanDisplay` and **any `IScanCamera`** (the simulator here; the phone through
   wp2-phonescan's `PhoneCamera`, so "any brand, your phone" answers the truck), off the lane with progress. (3 h)
2. Flat screens: a joint fit of the camera's radial distortion (k1, k2) and one homography per projector, with the planarity test (a
   curved screen is refused with words this round); metric rectification. (6 h)
3. The canvas target (`FIT INSIDE` by default, `FIT CORNERS` with `ALIGN CORNERS`) and a measured warp grid per output (33×19 by
   default) fitted from dense correspondences → `MeasuredCorrection.Warp`. (5 h)
4. The measured blend, complete (autoalign.md M11): per-output maps at ¼ raster from the distances to the raster edge and the
   coverage edge, normalised in light; **the measured black-level pedestal**; **the uniformity gain**; **per-output γ from the grey
   ramp** written to `Blend.Gamma`; stacks converge because every member fits the same target. (6 h)
5. Render: `MeasuredStage` draws the measured warp as the first warp layer, the measured blend and pedestal, γ and the gains slot.
   (3 h)
6. Runs: `ALIGN NEW`, `ALIGN RUN … STEPS SCAN,SOLVE` stages a result (files under `align/<run>/`, never overwritten), `ALIGN STATUS`,
   `ALIGN ACCEPT` applies every output's measured correction through `ICalibrationLedger` with the previous one kept as fallback,
   `ALIGN REVERT`, `ALIGN CLEAR`; one edit. (2 h)

⟂ *(≈ 25 h; + preflight 0.5 h)*

7. *(stretch)* `ALIGN PLAN`, the dry run.

**Preflight.** Every accepted run's files present and loaded. **INDEX.** Section `align`: per group RUN, ACCEPT, REVERT, and the
grade.

**Owns.** `src/PlayMap.Core/Align/**` (including its `MultiProjectorSim`), `src/PlayMap.Render/Passes/{OutputStage,
MeasuredStage}.cs`, `src/PlayMap.Engine/Align/**`, `docs/ALIGN.md`. Nothing in `PlayMap.Devices`.

**Hunks.** `ShaderLibrary.cs`, the `output` program: the measured grid, blend and pedestal, gains and γ (after zerocopy's `yuv` hunk).

**Depends on.** wall (the ledger, W1), show (the gate, W1).

**Acceptance.**
- `FootprintTests` (simulator): footprints within 2 px.
- `FlatFitTests`: the simulator uses **a richer model than the fit** (k3 and tangential terms, a screen non-planar within the
  planarity tolerance): the residual after the fit stays under its bound and canvas edges are straight within 0.5 projector px; a
  curved screen is refused with words. No assertion of exact parameter recovery.
- `MeasuredWarpTests`: a 3×1 flat simulator → after ACCEPT, canvas grid points land within 0.5 px p95; a stacked pair converges within
  0.5 px.
- `MeasuredBlendTests` (simulator 2×1 with projector γ 2.2 and 2.6): blended white in light within ±2 % across the overlap; the black
  floor after the pedestal within ±5 % of the deepest overlap; with 30 % corner fall-off, uniformity 1.0 gives white p5/p95 ≥ 0.95.
- `AlignRenderTests` (llvmpipe): `MeasuredStage` read-back equals the CPU reference warp within 1 code; identity γ 2.2 is bit-exact.
- `AlignLedgerTests`: ACCEPT records snapshots with fallbacks; REVERT restores; armed → refused with the gate's words.
- `align.json` and its cards written by a test.

**Verified here.** All (simulator). **Manual (lab).** A real camera and projectors.

**Next round** (beyond wp2-resolve). Grouped capture, curved screens (cylinder, then dome), FULL colour match, the projector's own
processing (`INativeProcessing`: Panasonic, then Epson and Christie) with snapshot, neutral and restore, the shutter knock, the
free-form hand-over to the scan solver, auditing a vendor's built-in result, the warm-up gate, two cameras, camera control, the
desk's Align tab.

### 3.22 `wp2-facade` — sites, surveys, control points, long throws (W2)

**Slice.**
1. The site frame: `Stage.Site`; mesh and point readers take `GeometryReadOptions` and subtract the site origin in double; `SITE`,
   `SITE ORIGIN`, `SITE MAINS`; the carry-over fix "a scan bridges into the stage as placed". (4 h)
2. `SURVEY IMPORT` off the lane with progress (PLY, OBJ, LAS 1.4 uncompressed), QEM decimation to a triangle budget with a deviation
   bound, applied as one edit (a mesh asset and a `SurveySource` with provenance). (7 h)
3. **E57** (facade.md M4): the managed reader (1024-byte pages with CRC-32C, XML without DTD, Data3D with Float and ScaledInteger
   fields bit-packed, per-scan pose, row and column indices; a structured scan meshes per station) and a test writer. (7 h)
4. `SITE POINTS` (PENZD, PNEZD, ENH, NEH, header sniffing) and `SITE CHECK`; pins bound to control points (`CalibrationPin.ControlId`).
   (3 h)
5. Long-throw solves: focal and position priors as residual rows (`Projector.Priors`), an automatic focal lock when the data cannot
   carry focal, `SolveUncertainty` (extending wall's `SolveUncertaintyAnalysis`); `PROJECTOR PRIOR`. (6 h)

⟂ *(≈ 27 h; + preflight 0.5 h)*

6. *(stretch)* `PLAN LIGHT` (predicted lux, nits and contrast per surface).

**Preflight.** `SITE CHECK` residuals within the site's tolerance. **INDEX.** None this slice (stacks add theirs).

**Owns.** `src/PlayMap.Core/Site/**`, `src/PlayMap.Core/Geometry/**`, `src/PlayMap.Core/Calibration/PoseSolver.cs`,
`src/PlayMap.Core/Mapping/{BlendSolver3D, WarpField, WarpMeshBuilder}.cs`, `src/PlayMap.Engine/Site/**`, `docs/FACADE.md`.

**Hunks.** `Engine/EngineCore.Mapping.cs` (wall's): `PROJECTOR SOLVE` passes the projector's priors (one call).
`Engine/Scanning/ScanOutcome.cs` (phonescan's): the bridged-as-placed fix (after phonescan in the W2 merge order).
`Core/Calibration/SolveUncertaintyAnalysis.cs` (wall's): the priors' rows.

**Depends on.** wall (W1); phonescan (hunk order).

**Acceptance.**
- `SiteFrameTests`: an OBJ at E 512 345.123 / N 5 401 234.567 lands in stage coordinates exact to 0.1 mm and round-trips.
- `SurveyImportTests`: a 2 M-triangle PLY → ≤ 300 k triangles with Hausdorff deviation ≤ the reported bound; one undo step; the
  lane is never held by the import (the lane's own busy time per slice ≤ 10 ms, from its instrumentation). The time is recorded; the
  15 s budget and `PING` ≤ 50 ms are `Category=Perf` tests run serially at integration, with a 4× ceiling here.
- `E57Tests`: files from the test writer (two stations, ScaledInteger and Float, with colour) read back point for point within the
  scale; one flipped byte fails the page CRC with the page number in the words.
- `ControlPointTests`: a 14-point PNEZD file → 14 points; `SITE CHECK` residuals; a bound pin follows its point.
- `LongThrowTests` (seeded Monte Carlo, the bench's long-throw table): a 95 m throw with 8 pins → focal and pose within the table's
  bounds with priors; near-collinear pins trigger the focal lock with words; the uncertainty is reported.
- `facade.json` and its cards written by a test.

**Verified here.** All (simulators). **Compile-only.** None.

**Next round** (beyond wp2-stacks). LAZ through LASzip at run time, the night profile and `SCAN AMBIENT`, measured photometry,
`MASK GLASS`, `STACK FAILOVER`, `CAMERA SOLVE` from photos, the light-plan heat map, glass from model materials, the desk's facade
drawer.

### 3.23 `wp2-desk` — the desk spine, generic cards, the show strip (W2)

**Slice.**
1. The desk spine D0 from `r2/bench/desk/desk-spine-proto.patch` (module hooks, drawer host, Esc ladder, banners, badges, the param
   templates, `DeskVerbRuleTests`, `StateFixtures`). (8 h)
2. **Generic STATE cards**: one card template that renders any `<pkg>.cards.json` descriptor (title, STATE path, light, words, up to
   six fields, actions as verb lines) in the inspector and under Health, so no package's STATE stays invisible; the cards for
   `main.json`'s `solve`, `cameras` and `ai` (carry-over: the desk had no screens for them; `FakeEngineLink` answers the ASSISTANT
   verbs). (3 h)
3. **Right-click entries generated** from the descriptors' actions and `DeskAction`s: Add effect ▸, Modulate ▸, Bind/Learn ▸,
   Optimise ▸, Use input ▸, on existing menus, in step with the toolbar (`DeskVerbRuleTests` polices them). (2 h)
4. The show strip: ARM and DISARM (no password), armed desk-wide (mapping actions disabled with the gate's words), PANIC (F12; Esc in
   PLAY with the preference), the panic banner and Clear, Lock/Unlock in the right-click menus. (7 h)
5. Carry-over: `StateSnapshot` reads `air.pattern` as an object. (0.5 h)

⟂ *(≈ 20.5 h)*

**Owns.** `src/PlayMap.App/**`, `tests/PlayMap.App.Tests/**`.

**Hunks.** None.

**Depends on.** show's fixture (W1); every fixture present at its start for the generic cards (later ones are checked by wp2-desk-2).

**Acceptance** (App tests, headless, fake engine with the packages' fixtures): ARM sends `SHOW ARM`; with `show.json` armed, mapping
actions are disabled with the gate's words; F12 sends `PANIC`; the panic fixture raises the red banner whose Clear sends `PANIC CLEAR
FADE 1000`; Esc within 600 ms of closing a drawer does not send PANIC; every card descriptor present renders with its light and
words, and each action sends a catalogue verb; every generated right-click entry equals a toolbar `DeskAction`; a real STATE line
with `air.pattern` as an object parses; the top bar fits at 1100, 1280, 1366, 1600 and 1920 px.

**Verified here.** All (Avalonia headless). **Manual.** The five-second gestures and the dark-room look.

### 3.24 `wp2-resolve` — re-solve the projector that moved; align parity (W3, continues wall and align)

**Slice.** Wall's side:
1. `CALIBRATION RESOLVE <pj>… CAMERA <camera>` (calibops.md M5–M7, minimal): a fresh capture of only the named projectors through the
   scan runner's display and capture pacing (the simulator here), dots detected and matched, **attribution per projector (unmoved /
   moved)**, and the pose re-solved for the movers only; scored, applied or staged through the ledger. (5 h)
2. Re-seat (M8, minimal): pins follow the pose (pixel := projection of their world point); pixel quads re-seated by the measured
   displacement field; masks and lattices untouched; a re-solved projector in an auto-blend re-solves its blend (main's off-lane
   `BLEND SOLVE`). (3 h)
3. The wall cursor and the first map: one session's cursor (leased for 3 s, drawn in the post-warp overlay) with its latency measured
   in frames into `STATE.air.overlay.cursorP95Ms`; firstfive M9's scripted first map; `STATE.onboarding`. (3 h)
4. Carry-over: the projector `VIEW` applies the lens lookup and draws black around the picture. (1 h)

Align's side:
5. WHITE colour and level match (M12, the diagonal half): photometric frames through `IScanDisplay.ShowRgbAsync` with the camera
   stability probe; per-projector K, R, G, B, W and greys; diagonal gains to a common white and level applied in linear light. (5 h)
6. `ONLY <output>` (M17): decoded maps persisted per projector; rescan one output, keep the others' maps, re-fit its warp, recompute
   its neighbours' blends. (3 h)
7. `ALIGN SCORE` as a `QualityScore` (coverage, seam misregistration, stack convergence) with a verify pass (a dot grid's
   misregistration). (3 h)
8. The double-correction guard (M4, minimal): Panasonic's `GMMI0` (geometry) and `EDBI0` (edge blend) read through the existing
   driver (`Devices/Native/**`, `DEVICE NATIVE <dv>` read-only); an output with a measured correction on a projector whose own
   geometry or blend is on lights `device:<id>` amber with the words and `DEVICE RAW` fix. (2 h)
9. A timed acceptance of the whole flat path. (1 h)

⟂ *(≈ 26 h)*

10. *(stretch)* Overlay names and grid if wall did not reach them; `ALIGN PLAN`.

**Owns.** Wall's and align's files for this wave, plus `src/PlayMap.Devices/Native/**`.

**Hunks.** `Engine/Scanning/ScanRunner.cs` (phonescan's; resolve merges before multiscan in W3): one entry that captures named
projectors only.

**Depends on.** wall, align (W2), phonescan (merged).

**Acceptance.**
- `ResolveCameraTests` (simulator, 3 projectors, P2 bumped 0.8° / 2 cm, calibops.md M7's numbers): only P2 re-solved; wall error vs
  truth ≤ **0.3 px p95**; P1's snapshot id unchanged; **pattern time (frames × frame time) ≤ 2 s**; attribution P2 `moved`, others
  `unmoved`.
- `ReseatTests` (M8): a pixel quad re-seated after a 16 px bump puts its corners within **0.5 px** of the wall spots they lit before;
  pins follow the pose; masks equal.
- `CursorTests`: a cursor not renewed for 3 s disappears from the next frame; the latency p95 ≤ **2 frame periods** over 300 moves,
  counted in frames of the headless render loop (not wall time); 0 bytes allocated per frame with two cursors moving.
- `FirstMapTests` (firstfive M9): `LIGHT WALL` → `SURFACE QUAD` → 8 nudges → `MEDIA IMPORT` of a 2 s clip → `SURFACE SOURCE` on the
  real headless engine: `STATE.onboarding.stage == "content"` with its four timestamps; the read-back shows the clip inside the
  quad and black outside; the machine time is recorded (under 5 s expected; a 4× ceiling asserted, the 5 s bar a Perf test).
- `ProjectorViewTests`: the projector VIEW equals the output's pre-trim picture within 1 code, black around it.
- `ColourMatchTests` (simulator: P1 6 500 K level 1.0, P2 7 500 K level 0.8): after WHITE, white Δu′v′ ≤ 0.003 and levels within 3 %; a
  camera that adjusts its own exposure refuses colour with words while geometry completes.
- `AlignOnlyTests`: after moving P2 by 3 px, `ALIGN RUN al… ONLY op2` shows patterns on op2 only (counting display), op1's grid is
  unchanged bit for bit, seams back ≤ 0.3 px.
- `AlignScoreTests`: each term triggered by a simulator condition; every fix line parses.
- `DoubleCorrectionTests`: the simulator's `GMMI0=+00001` after ACCEPT → `device:<id>` amber with the words.
- `AlignTimingTests`: four flat projectors (2×2) through the whole run in **≤ 90 s of pattern time** (frames × frame time).
- `wall.json` and `align.json` regenerated.

**Verified here.** All (simulator). **Manual (lab).** A real camera, projectors and a Panasonic projector's words.

**Next round.** The ladder's LENS/CODED/DENSE rungs, the drift monitor, lens read-back triggers, FULL colour, curved screens,
`INativeProcessing` with snapshot/neutral/restore.

### 3.25 `wp2-multiscan` — many views, every projector, safe suggestions (W3, continues phonescan)

**Slice.**
1. `SCAN START phone:<id> … INTO <scan>` adds a view; `SCAN SOLVE`, `SCAN VIEWS`, `SCAN DROP` through wp2-multiview's solver. (3 h)
2. Per-projector smart scans and regions (M7, carry-over): regions per projector on its own raster, merged across projectors by 3D
   overlap; `SCAN SURFACE … [ON <projector>]`; **scans persisted** under `scans/<id>/` (views, tracks, the white picture, depth maps)
   and usable after a restart (M8, carry-over). (6 h)
3. Safe AI suggestions: `SCAN SUGGEST <scan>` ranks measured regions with reasons and queues each as an approval proposal
   (`SCAN SURFACE …`, and for each planar region a `SURFACE QUAD` pixel quad — Ghost Arcade's planes as four-corner layers);
   nothing is applied without a person's `APPROVE`. (4 h)
4. The scan's score terms (dense reprojection RMS, decoded coverage, views) filled into wall's vocabulary for `SCAN APPLY`. (1 h)
5. The closed-loop browser test: Chromium (Playwright, `/opt/pw-browsers`) with a synthesised camera track fed by the render host
   lighting the simulator's scene with what the output shows; an **Android profile** (locks honoured), an **iOS profile** (no
   capabilities, AE hunting ±3 %), and **two views INTO one scan**. (7 h)
6. The Folder camera (tether: each new image in a folder is the picture for the current pattern; manual: `SCAN NEXT`) — facade's night
   scans need it. (6 h)

⟂ *(≈ 27 h — if time runs short, the E2E runs the Android profile only; the suggestions stay)*

7. *(stretch)* `PHONE FORGET`, the wake lock (if phonescan did not reach them); coverage on the wall (`SCAN COVERAGE … SHOW`).

**Preflight.** A scan an applied projector depends on is on disk. **INDEX.** Section `scan` gains the suggestions waiting.

**Owns.** Phonescan's files for this wave.

**Hunks.** None (resolve's `ScanRunner` entry lands first).

**Depends on.** phonescan (W2), multiview (W1), resolve (merge order).

**Acceptance.**
- `MultiViewScanTests` (engine, simulator): three views INTO one scan on wall-and-cylinder with 2 projectors meet multiview's M6
  thresholds through the engine; `SCAN DROP` of a bad view brings RMS under 0.5 px.
- `RegionTests`: a box face lit by both projectors appears once with `projectors:[pj1,pj2]`; `SCAN SURFACE sc… REGION rg… ON pj2`
  makes a surface of what P2 sees. `ScanPersistenceTests`: scan → node stop → start → `SCAN START … INTO sc…` adds a view and
  re-solves; `SCAN SURFACE` works.
- `ScanSuggestTests`: with `RunReviewWithoutAsking=true`, every suggestion stays `Pending` until `APPROVE`; the assistant fake
  receives text descriptors only; planar regions propose `SURFACE QUAD` lines that parse; `APPROVE` applies through the gate.
- `PhoneScanE2E` (gated on `PLAYMAP_E2E=1` and Chromium present): Android profile single projector — pose within 10 mm / 0.25° /
  0.6 % throw; iOS profile with AE hunting ±3 % completes; two views INTO one scan — both solve, RMS ≤ 0.5 px; **≤ 15 s per projector
  on the simulator path** (phonescan.md M4: 30 fps camera, 120 ms injected latency, 42 + 12 + 4 frames), counted in frames.
- `FolderCameraTests`: a fake tether copying simulator frames completes the scan with the simulator's pose; a photo of the wrong
  pattern is refused "photo 17 shows pattern 16: re-shoot"; 16-bit TIFF arrives as gray16.
- `ScanScoreTests`: SCAN APPLY records a snapshot whose score has source `scan` and the dense-RMS, decoded and views terms with
  their words; a scan with a 2 px shifted view raises `dense-rms` and its fix names `SCAN DROP`.
- `phonescan.json` regenerated.

**Verified here.** All, including the browser loop. **Manual (lab).** Real phones, real projectors, a real curved surface.

### 3.26 `wp2-stacks` — stacks, geodesy, the facade guide (W3, continues facade)

**Slice.**
1. Stacks: `StackGroup`, `STACK NEW`, `STACK LIST` (removal through the generic `REMOVE`), the blend collapse (members share the
   group's blend), `STACK ALIGN` (a residual lattice into `MeasuredCorrection`) and **`STACK MATCH`** (RGB gains from the same
   decoded frames). (8 h)
2. **The measured lattice drawn on the air**: the stack's lattice as the first layer of `WarpField`/`WarpMeshBuilder` (Core), so the
   existing warp mesh draws it with no shader change (facade.md's hunk, restored). (2 h)
3. Geodesy (facade.md M2): geodetic ↔ ECEF ↔ local, UTM by Krüger series, EPSG UTM codes, CRS sniffing from WKT and LAS GeoKeys. (4 h)
4. `SITE GUIDE` (alias `FACADE GUIDE`) and `STATE.stage.site.next`: the facade workflow as a step list (survey, control points, pins,
   priors and solve, stacks, blend, score), each step with its state and the line that does it, so the desk and Companion can
   drive it later without new verbs. (2 h)
5. A scripted facade end to end on the simulator. (2 h)

⟂ *(≈ 18 h; + preflight 0.5 h)*

6. *(stretch)* `PLAN LIGHT`; `STACK FAILOVER` with a degrade gain through `IOutputGains`.

**Preflight.** Each stack's residual within 0.5 px and no degraded member. **INDEX.** Section `site`: per stack ALIGN and MATCH,
and the guide's next step.

**Owns.** Facade's files for this wave.

**Hunks.** None.

**Depends on.** facade (W2).

**Acceptance.**
- `StackTests`: two simulated projectors offset 0.8 px with a 0.3 % zoom difference → `STACK ALIGN` brings misregistration p95 to ≤
  0.2 px; the blend collapse gives each member the group's share; **`STACK MATCH` on the colour simulator: gains ≤ 1 and white Δuv ≤
  0.003**.
- `StackRenderTests` (llvmpipe): a member's lattice moves its read-back by the lattice's offset within 0.5 px (no shader change).
- `GeodesyTests`: UTM round trip ≤ 1 mm over the zone; three published sample points ≤ 1 mm; a LAS 1.4 file with a WKT VLR reports
  `EPSG:25832`.
- `SiteGuideTests`: each step's state follows the project; every step's line parses.
- `FacadeEndToEndTests`: `SURVEY IMPORT` → `SITE POINTS` → pins bound to control points → `PROJECTOR SOLVE` with priors → `STACK NEW`
  / `STACK ALIGN` / `STACK MATCH` → `BLEND SOLVE` → the score, all through the wire on one engine, each step's STATE asserted.
- `facade.json` regenerated.

**Verified here.** All (simulators).

### 3.27 `wp2-backup` — a hot backup that keeps the air (W3, continues fleet)

**Slice.**
1. The hot backup: `FLEET BACKUP <primary> [HOT]` (manual takeover), `FLEET SOLO`, `FAILOVER`, `FAILBACK`; the backup follows the
   primary's group (wp2-sync's anchors) and renders the same frames on its own outputs, re-fetching the project when the primary's
   revision moves; **`MIRROR AIR`** carries blackout, freeze, panic and master with their leader instants, so a backup in panic
   black stays black after FAILOVER; `FAILOVER` makes it the leader through `NetworkClock.Promote()` and fences the old primary by
   epoch; role and epoch persisted. (8 h)
2. Pulls with proof: the project and media are pulled with the paired wire session's proof over TLS-pinned HTTPS (carry-over:
   `FETCH` sent no token over plain HTTP; `Net/Media/MediaTransfer.cs`); **`MEDIA FETCH <md> VERSION <mv>`** by hash, so the backup
   plays the same optimised version as the primary. (4 h)
3. Routes (fleet.md M8, thin): `FLEET ROUTE <output> DEVICE <dv> <input> <backup input> | NONE`, run by FAILOVER, the input switch
   observed through the projector's own answer (PJLink `INPT ?`); `FLEET ROUTE TEST` reads reachability and the current input. (3 h)
4. `FLEET PROMOTE`: a follower leads its group now, continuing the old leader's time line; the controller-death test of fleet.md M9.
   (3 h)

⟂ *(≈ 18 h; + preflight 1 h)*

5. *(stretch)* Automatic takeover: the pure `FailoverMachine` on silence (`TIMEOUT <ms>`); `FLEET SUCCESSION`.

**Preflight.** The backup identical (same revision and versions); every route reachable. **INDEX.** Section `fleet`: FAILOVER,
FAILBACK, PROMOTE with the backup's state.

**Owns.** Fleet's files for this wave, plus `src/PlayMap.Net/Media/MediaTransfer.cs`.

**Hunks.** `Engine/EngineCore.Content.cs` (content's): the `VERSION` arm of `MEDIA FETCH`.

**Depends on.** fleet (W1), sync (W2), optimise (W2, versions).

**Acceptance.**
- `BackupTests` (two engines, **the primary token-protected**): BACKUP → the backup's output read-back equals the primary's for the
  same leader instant and its revision tracks the primary's (event-driven); across `BLACKOUT ON` and `PANIC FADE 1000` the two
  read-backs are byte-equal at chosen leader instants; the backup plays the same codec and version (`decode.sources[].codec`);
  FAILOVER → the backup leads, show time continuous within 1 frame, the old primary fenced by epoch; FAILBACK returns.
- `RouteTests` (fake PJLink driver): TEST reads `INPT ?` and reports "P1 on 31 (primary) · reachable"; FAILOVER sends `INPT 32` and
  reads back observed; an unreachable route makes FAILOVER proceed with words.
- `PromoteTests` (three engines in a group): the leader disposed mid-item; both followers switch at the instant they computed;
  `FLEET PROMOTE` on follower 1 → it leads, follower 2 follows; leader time on follower 2 continuous within 1 ms and never backwards;
  with no promotion the context says "leader gone — FLEET PROMOTE on the node that should lead".
- `fleet.json` regenerated.

**Verified here.** All (loopback). **Manual (lab).** A real switcher input and a real failover drill.

**Next round.** Automatic takeover and succession if not reached, `ProjectDiff` deltas in the mirror, the picture fingerprint,
projector auto-switch and Videohub routes, N+1 cold standby, drills, webhooks.

### 3.28 `wp2-desk-2` — the desk for round 2 (W3, continues desk)

**Slice.**
1. The preflight board (rows with Fix buttons that send the row's fix line), the readiness light (the worst of show, fleet and sync),
   fleet and backup cards (stale and gone colours, the backup role, FAILOVER). (5 h)
2. The quad tool and on-wall mode: Q (four clicks with a rubber band, Enter sends one `SURFACE QUAD`), arrow nudges (`SURFACE CORNER
   … BY … PX`), Tab cycles, O toggles the overlay; the first-light guide (LIGHT WALL → quad → source). (6 h)
3. The calibration score card (terms with Fix) and the history strip (Recall), Health ▸ Calibration. (3 h)
4. The blind chip: Diff, Commit (NOW, NEXT), Discard, and the draft view toggle. (2 h)
5. Every package's cards and generated menus render from the final fixtures (a test over every `*.cards.json`); the list of features
   that are wire-only in round 2 written to the papers note for `UX.md` (§8). (2 h)

⟂ *(≈ 18 h)*

**Owns.** The desk's files for this wave.

**Depends on.** desk (W2) and every fixture (W1–W3; desk-2 merges last).

**Acceptance.** Every preflight Fix sends its row's fix line; the fleet fixture renders cards with stale and gone colours; FAILOVER
sends `FAILOVER`; Q, four clicks, Enter → exactly one `SURFACE QUAD`; Alt+Right sends `SURFACE CORNER <sf> <c> BY 0.1 0 PX`; O sends
`OVERLAY ON ON <op>`; armed → O disabled with words; the score card's Fix sends the term's line; the blind chip's Commit NEXT sends
`BLIND COMMIT NEXT`; every package's card renders and every action sends a catalogue verb; `DeskVerbRuleTests`.

**Verified here.** All (Avalonia headless).

**Next round.** The rest of D1 (Sync tab, present chips, timecode chip and tab, bindings drawer and learn, Decks), all of D2 (effects
browser, modulation sockets and drag-to-bind, tempo chip, content library, optimise jobs, live inputs and sends as dedicated views),
the rest of D3 (Align tab, facade drawer, phone path, LED tool and patch editor), and `desk.md` §7's list. Until then these features
have generic cards and generated menus (§8).

---

## 4. Shared files and their hunks

Every file has one owner (§2.2 and §3). A package touching another's file does so only through a hunk named here, landing in the
merge order of §6. A continuation editing its parent's file is the owner, not a hunk.

| File | Owner | Hunks, in merge order |
|---|---|---|
| `Render/Shaders/ShaderLibrary.cs` | spine (W0b lands the dialect convention and `uAir`) | zerocopy (`yuv`: `uCrop`/`uMaxUv`) → align (`output`: measured grid, blend and pedestal, gains, γ) |
| `Render/Engine/RenderEngine.cs`, `Render/Passes/OutputStage.cs` | output, align | none: the spine plumbs gain, hold, override and `Measured` in W0b |
| `Core/Wire/VerbCatalog.cs` | spine | each package inside its own block only (trim, remove, add for a reached stretch item) |
| `Core/Wire/VerbParams.cs` | companion | any package removing a row with an override deletes that override line |
| `Engine/EngineCore.Transport.cs` | show | control (chased playlist guard, W1) |
| `Core/Playback/PlaylistCursor.cs` | control | content (item time for routed generators, W2) |
| `Engine/Playback/PlaybackLanes.cs` | control | blind (the outgoing item kept through a manual transition, W2) |
| `Engine/Rendering/PreviewViews.cs` | output | blind (the editor view draws the draft, W2) |
| `Engine/Host/MediaBackend.cs` | decode (zerocopy in W2) | optimise (`VersionChoice` at open) → videoio (live URI routing) |
| `Render/Video/VideoTextureSet.cs` | decode (zerocopy in W2) | videoio (packed-layout branch) |
| `Engine/Scanning/ScanOutcome.cs` | phonescan | wall (snapshot on SCAN APPLY, W1, before phonescan starts) → facade (bridged as placed, W2) |
| `Engine/Scanning/ScanRunner.cs` | phonescan (multiscan in W3) | resolve (capture of named projectors, W3, before multiscan) |
| `Core/Scan/ScanSimulator.cs` | phonescan | wall (`Bump`) → multiview (a view from a given pose), both W1 |
| `Engine/EngineCore.Mapping.cs` | wall | facade (priors into `PROJECTOR SOLVE`, W2) |
| `Core/Calibration/SolveUncertaintyAnalysis.cs` | wall | facade (the priors' rows, W2) |
| `Engine/EngineCore.Content.cs` | content | backup (`MEDIA FETCH … VERSION`, W3) |
| `Engine/Host/EngineEntry.cs` | spine (`TryDispatch`) | none: optimise owns its stub `EngineEntry.Optimise.cs` |
| `App/Program.cs` | desk (after W0b's `TryDispatch` hunk) | none |
| `THIRD-PARTY-NOTICES.md` | spine (headings) | each package fills its own heading: d3d11 (ANGLE, BSD), videoio (libomtnet and libvmx, MIT; NDI notice and trademark), decode (Snappier, BSD-3), audio (NAudio, MIT), led and control (Art-Net trademark line), companion-module (module dependencies) |
| `*.csproj` | the project's owner package | one `<ItemGroup Label="wp2-<pkg>">` block per package at the end |
| `docs/WIRE.md`, `ARCHITECTURE.md`, `ROADMAP.md`, `UX.md`, `COMPETITIVE.md`, `MAPPING.md` | integration docs pass (§6.2) | none during the waves; packages write `docs/<AREA>.md` and `docs/wire/<pkg>.md` with a papers note |

---

## 5. Waves and the machine

### 5.1 Waves

| Wave | Packages (implementation sessions) | Why this grouping |
|---|---|---|
| **W0a** | `wp2-spine-model` | Model, catalogue, risk and syntax first: every later test reads them. |
| **W0b** | `wp2-spine-engine` | Hooks, gate, seams, the air path, the dialect convention, the node host. W1 starts only on a green W0b. |
| **W1** | show, output, decode, control, companion, fx, audio, fleet, wall, multiview (10) | Each depends on the spine only; together they own the files W2 and W3 hunk (renderer and dialect, gate and transport, `MediaBackend` and textures, the playback cursor and the Art-Net socket, the wire, the ledger and the scan files). |
| **W2** | blind, d3d11, zerocopy, companion-module, sync, content, optimise, videoio, led, phonescan, align, facade, desk (13) | Continuations of W1 packages, and the packages that need a W1 package merged (sync needs output's vblank clock; content and led need control; optimise and videoio need decode; phonescan, align and facade need wall; phonescan needs multiview). |
| **W3** | resolve, multiscan, stacks, backup, desk-2 (5) | Continuations of W2 packages (resolve needs align; multiscan needs phonescan; stacks needs facade; backup needs sync; desk-2 reads every fixture). |

**Scheduling.** Waves name dependency depth, not a barrier: a package starts when every package it depends on is merged and a builder
slot is free. **At most five sessions run at once** (four implementations and one review, §5.2); a review takes the next free slot
when its implementation ends; merges follow §6.1's order within each wave. With five slots the round runs in about eleven rounds of
sessions: W0a, W0b, three for W1, four for W2, two for W3.

### 5.2 The machine (4 cores, 15 GB RAM and no swap, ~13 GB free disk, no GPU)

| Constraint | Rule |
|---|---|
| **Disk** | A fully built tree is 3.2 GB, of which `App` + `App.Tests` are 1.7 GB, `Engine.Tests` 0.7, `Render.Tests` 0.45. A package builds **only the projects it needs** (`dotnet build tests/PlayMap.Engine.Tests`, …, plus `tools/PlayMap.Node` when its tests launch the node, ≈ 150 MB): about 1.4 GB per worktree. Only the desk packages and the integration tree build App. With four implementations: 4 × 1.4 + one App tree 2.6 ≈ 8 GB. A merged package's worktree is removed (bin/obj first). No new build starts when free space is under 3 GB. |
| **Memory** | One agent is ~480 MB; the compiler server idles at ~250 MB and passes 1 GB under load; an Engine test host with Kestrel, GL and FFmpeg passes 1 GB. Hence: **at most 5 concurrent sessions** (4 implementations + 1 review); a machine-wide **build semaphore of 3** (`flock` on one of three lock files, `-m:2` each) and a **test semaphore of 2** for `Engine.Tests`, `Render.Tests` and `Media.Tests` runs; test hosts run with `DOTNET_GCHeapHardLimit=0x60000000` (1.5 GB); `dotnet build-server shutdown` after each build; **no build or test run starts while `MemAvailable` is under 2 GB**; `LP_NUM_THREADS=1` so llvmpipe does not oversubscribe the cores. One `dotnet build` at a time per tree (CLAUDE.md). |
| **Displays** | One Xvfb per session (`:101`–`:105`), so GL and window tests never share a display. |
| **Network isolation** | Sessions are isolated **by loopback address, not by port**: each builder slot owns `127.<slot>.0.0/24` (all of 127/8 routes to `lo`), given to tests as `PLAYMAP_TEST_LOOPBACK`. Protocols with spec ports (Art-Net 6454, sACN 5568, DDP 4048, mDNS 5353, PJLink 4352) bind that address on the spec port; `IArtNetEndpoint` and the sACN receiver take their bind address from `NodeSettings`. PlayMap's own protocols (wire, web, OSC, clock, PMVB) bind port 0. Multicast tests use a per-session group. |
| **Time in tests** | Rules about time (staleness, budgets, leases, fades) run on `ManualClock` or an injected `TimeProvider`. Budgets on real work (survey import 15 s, `PING` ≤ 50 ms under load, first map < 5 s, HAP layers) record the measurement in the test output, assert only a 4× ceiling, and have a `Category=Perf` twin the integration gate runs serially. Media loopbacks (OMT, NDI fake, Spout) count frames lock-step with no real-time pacing. |
| **FFmpeg and Chromium** | `PLAYMAP_FFMPEG_DIR=/opt/ffmpeg9/lib`; Playwright with `/opt/pw-browsers` (multiscan, and the module's tests under Node 22). |
| **Windows** | The repository's `windows-latest` CI job already builds everything and runs the Core, Engine, Net, Devices, Assistant and Vision suites on every push. W0b adds `Render.Tests` and `Media.Tests` with `--filter Category=Windows`. Every package with Windows code adds `[WindowsFact]` tests in that category (output NVAPI, d3d11 DXGI/ANGLE/Spout on WARP, zerocopy D3D11VA, control WinMM, audio WASAPI, fleet PDH) and reads the result with `gh run view` on its pushed branch. `WindowsPlatformTests` (W0b) keeps every Windows-only type under `[SupportedOSPlatform("windows")]` with CA1416 at error. There is no `-r win-x64` gate (it compiles the same C# as the Linux build and rewrites every project's assets file under a `--no-build` test run). |
| **Licence hygiene** | Clean room: `ext/libltc` (LGPL-3) and `ext/ffsrc` (FFmpeg's LGPL `d3d11va.c`, `dxva2.c`, `hwcontext_*`) are not opened while writing the matching code (LTC, D3D11VA/VAAPI imports); the code follows the standards and published API documentation, and its tests cite the sections used. Test fixtures are made with LGPL-compatible encoders (`mpeg4`, `libopenh264`, `hap`, `pcm`, `aac`), never `libx264`/`libx265` (this `/opt/ffmpeg9` is a GPLv3 build). Ableton Link only through a Carabiner process the user installs (GPLv2+, never bundled). NDI, libvmx, ANGLE and FFmpeg load at run time; OpenAL Soft (LGPL, from `Silk.NET.OpenAL.Soft.Native`) stays a separate, replaceable file. |

---

## 6. Integration

### 6.1 Merge order and the gate for every merge

| Wave | Order | Why |
|---|---|---|
| W1 | **output → show → decode → fx → control → companion → audio → wall → multiview → fleet** | The renderer refactor and the ES dialect first; the gate next (behaviour every later test sees); `MediaBackend` and textures; passes; control's transport hunk after show; companion's `VerbParams` after the W1 blocks are trimmed; audio before content's reactive test; wall's scan hunks before multiview's simulator hunk. |
| W2 | **zerocopy → optimise → videoio → d3d11 → sync → blind → content → led → phonescan → facade → align → companion-module → desk** | Decode's own changes before the `MediaBackend` and `VideoTextureSet` hunks; videoio's provider lists before Spout registers; sync before blind's fan-out; phonescan before facade's `ScanOutcome` hunk; zerocopy's `yuv` hunk before align's `output` hunk; the module regenerates its fixtures against every W2 block; the desk last. |
| W3 | **resolve → multiscan → stacks → backup → desk-2** | Resolve's `ScanRunner` entry before multiscan; the desk last, against the final catalogue and fixtures. |

**Every merge** (on one integration worktree, one build at a time): rebase on `main`; build the whole solution including App and
`tools/PlayMap.Node`; run every test project (GL on Xvfb, FFmpeg with the 9.0 libraries, the module's `npm test`); the package's
fixtures regenerated and equal to the committed ones; `RoutesTests`, `StateOrderTests`, `ContextLinkTests`, `CatalogueTests`,
`VerbParamTests`; analyzers on; no skipped, disabled or quarantined test; the pushed branch's Windows CI job green. A red merge is
fixed by that package's review session, never by editing another package's test.

**Tests added at merge** (they need two packages; written by the second package's review session):

| After | Test |
|---|---|
| output, fx | `FxEsLegTests`: every translated sample compiles on Mesa GLES 3.0; the opt-in corpus ≥ 305/327 on the real ES context |
| show, fx | `PanicFxOffTests`: PANIC FX-OFF bypasses every slot, observed in `STATE.fx` |
| show, control | `OscArmedTests`: an armed node refuses edits over OSC with the gate's words |
| show, companion | `CompanionArmedTests`: `WHOAMI` gives the armed `may` list; an armed refusal reaches a WS client with words |
| control, audio | `LtcThroughCaptureHubTests`: control's LTC decoder fed by audio's hub (synthetic LTC input) chases a playlist |
| sync, blind | `ArmAllTests`: `SHOW ARM ALL` arms both nodes; `PREFLIGHT ALL` lists one section per node |
| sync, control | `TimecodeMultiNodeTests`: a leader chases LTC, the follower adopts its anchors |
| audio, content | `ReactiveRealBusTests` |
| control, led | `ArtNetSharedSocketTests`: DMX in and LED out on one socket |
| wall, align | `AlignLedgerRoundTripTests`: ALIGN ACCEPT → fallback → RECALL |
| videoio, d3d11 | `SpoutProviderTests`: `SEND ADD SPOUT` and `INPUT ADD spout:` reach d3d11's provider; on Linux refused "Spout is Windows only" |
| companion, companion-module | the module's fixtures regenerated from the integrated catalogue and W1/W2 STATE |

### 6.2 The round-2 integration pass (after W3)

1. `RoutesTests` flips to **every catalogue row is routed** (true by construction: packages removed the rows they did not build).
2. Cross-package tests: ARM refuses every package's air-moving verbs (generated from the catalogue by `ShowClass`); PANIC BLACK blacks
   LED output on the next frame; an optimised HAP version plays through decode on an output and is sent over OMT and received back;
   **the integrated INDEX holds every package's kinds, and the module builds a preset section for each**; **PREFLIGHT on the
   integrated engine lists one section per package** (every Done-means check); Companion's e2e against the integrated engine
   presses GO and PANIC and its feedbacks follow; the shaped context with every package's items fits the Eye's window and passes
   `PatternsEyeContractTests`; the desk renders every package's cards.
3. `LicenceTests` on the publish output: no NDI binary; any FFmpeg in the folder reports no `--enable-gpl`; OpenAL Soft is a separate
   file; every notice present.
4. The docs pass: the "Round 2" sections of `WIRE.md`, `ARCHITECTURE.md`, `ROADMAP.md` (statuses), `UX.md` (with the wire-only list),
   `MAPPING.md`, `COMPETITIVE.md`, from each package's papers note; `THIRD-PARTY-NOTICES.md` checked against the publish output.
5. Budgets measured and recorded: the assistant prompt's tokens (only available rows) against a budget, the `VERBS` size, the
   frame-path allocation assertion with every pass registered, the `Category=Perf` tests run serially, the build size.

### 6.3 The spine amendment lane

A seam found missing mid-wave is added by a short spine commit on `main` between merges (never by a package editing a spine-owned
file); until it lands, the package works behind its own interface in its own files.

### 6.4 Carry-overs: who owns each

`main` at `cb3cfc0` (the polish merge, under the spine) already fixed several items of `CARRYOVER.md`; they need only main's own
tests. The rest have owners:

| Carry-over | Status | Owner | Test |
|---|---|---|---|
| Anchors not carried to followers; role not persisted | open | sync | `AnchorFeedTests` |
| FOLLOW steps the clock (playlists jump) | open | sync | `ClockSlewTests` |
| FETCH sends no token over plain HTTP | open | backup | `BackupTests` (token-protected primary) |
| `sync.offsetMs` meaning in the papers | open | sync | papers note |
| Desk reads `air.pattern` as a string | open | desk | `StateSnapshot` test |
| Fade and dip play as cuts | **fixed on main** (03f4979) for scheduled items; manual GO/NEXT/PREV open | blind | `ManualTransitionTests` |
| A playlist nested in a playlist shows black | **fixed on main** (7071e3d) | — | main's tests |
| BLEND SOLVE blocks the lane | **fixed on main** (eab17db); blends following a re-solve open | resolve | `ReseatTests` |
| Shown VIEWs never expire | **fixed on main** (f466600) | — | main's tests |
| No stings on the wire | open | content | `StingTests` |
| Display latency not per output | open | d3d11 | `LatencyRingTests` |
| Headless "dropped" against project fps | **fixed on main** (1a6711f) | — | main's tests |
| Projector VIEW: no lens, grey background | open | resolve | `ProjectorViewTests` |
| Phone and Folder cameras refused | open | phonescan, multiscan | `PhoneCameraTests`, `FolderCameraTests` |
| A camera that fails to open leaves its view blank | **fixed on main** (545c48b) | — | main's tests |
| Scans held in memory only | open | multiscan | `ScanPersistenceTests` |
| Regions and the smart scan for the first projector only | open | multiscan | `RegionTests` |
| A real scan without a known distance is not metric | open | next round (scale) | — |
| `ASSISTANT SET` | **fixed on main** (5b587af) | — | main's tests |
| ASK waits for the whole model turn | open | companion (stretch 8, WS) | `AskStreamTests` |
| `StagewatchProposalSink` not switched on | decision | §9, decision 7 | — |
| Risk per verb | open | spine (`RiskOf(line)`, raise-only) | `RiskOfTests` |
| Camera read-back stamped ~8 ms before present | **fixed on main** (e01bdca) | — | main's tests |
| MCP approval shows `line.Raw` | **fixed on main** (66110df) | — | main's tests |
| Desk has no screens for `solve`, `cameras`, `ai.assistant` | open | desk (generic cards from `main.json`) | card tests |
| MCP client count approximate; render host on a pool thread (macOS); credentials from this machine only; Devices UDP 4352 | by design or next round | — | — |

---

## 7. Risk register

| # | Risk | Likelihood | Impact | Mitigation | Owner |
|---|---|---|---|---|---|
| R1 | Slices over capacity (companion, control, fx, optimise, videoio, facade, multiscan sit at 27–29 h) | High | Medium | Ordered slices with cut lines; must merges alone; unfinished stretch moves to next round with its rows trimmed. If the round must be smaller, whole packages are deferred in §9 decision 1's order — never align, phonescan, multiview or the Companion module. | Each package |
| R2 | The spine is the largest single piece (≈ 200 rows, ~90 stubs, 32 edit sites, the air path, the dialect convention) | High | High (blocks everything) | Two sessions (W0a model and catalogue; W0b engine, render and tree), each with its own review; stubs generated by a script; W1 starts only on a green W0b. | spine |
| R3 | Windows code that cannot run here (DXGI/ANGLE, NVAPI, WinMM, WASAPI, PDH, Spout, D3D11VA) rots | High | High for the D3D11 path | Written to the verified API facts; layout and slot goldens; **run on the existing Windows CI job with WARP this round**; DXGI only on an explicit `PRESENT PATH DXGI` (experimental) until a lab run passes; the fuse falls back to GL with words. | d3d11 |
| R4 | Hunk conflicts in hot files | Medium | Medium | One owner per file; named hunks only (§4); merge order fixed (§6.1); each hunk ≤ ~10 lines; continuations own their parent's files. | integrator |
| R5 | Disk (~13 GB free) | Medium | High | Build only needed projects; App only in the desk packages and integration; remove merged worktrees; 3 GB guard. | orchestrator |
| R6 | Memory (15 GB, no swap): the OOM killer stops a build or test host mid-run, a red that is not a defect | High | Medium | Five sessions at most; build semaphore 3, test semaphore 2; `DOTNET_GCHeapHardLimit`; build servers shut down; 2 GB guard. | orchestrator |
| R7 | Socket, multicast and display clashes between concurrent test runs | Medium | Medium | Loopback address per slot on the spec ports; port 0 for PlayMap's own; per-session multicast groups; one Xvfb per session. | each package |
| R8 | The catalogue grows by ~200 rows; unrouted rows answer "not available yet" for weeks; a small local model's context may not hold the prompt | Medium | Medium | The prompt and `list_verbs` hold only **available** rows (`IVerbAvailability`); unbuilt rows trimmed; `Internal` rows hidden; the prompt's tokens measured at integration against a budget. | spine, companion |
| R9 | Licences: NDI (proprietary runtime), OMT/libvmx (MIT, vendored and patched), ANGLE (BSD), Art-Net (trademark), FFGL (BSD, next round), the ISF corpus (opt-in), **LGPL sources at hand (libltc, FFmpeg's hwcontext), a GPLv3 FFmpeg here, Ableton Link (GPLv2+), OpenAL Soft (LGPL)** | Medium | High | Runtimes loaded from the user's install; the clean-room rule and fixture encoders of §5.2; `LicenceTests` on the publish output; notices per package. | videoio, d3d11, control, integrator |
| R10 | The show gate refuses something the desk or a controller needs while armed | Medium | High | The policy table test covers every class; transport, blackout and PANIC are live armed; the cross-package ARM test runs every verb class. | show |
| R11 | GPU determinism differs between llvmpipe and real GPUs (particles, ISF floats, BC decode) | Medium | Medium | Same-engine pair tests and CPU twins with tolerances; history effects named as such; goldens only on llvmpipe; a lab check next round. | content, fx, decode |
| R12 | Patterns' Eye shape or Companion's API moves | Low | Medium | Goldens copied with their source commit; contract tests on both sides; the module pins its API version. | fleet, companion-module |
| R13 | `main` moves during the round | Medium | Low | The spine rebases at start; every package rebases before merge; the merge gate. | integrator |
| R14 | The desk reads stale fixtures | Low | Medium | Fixtures regenerated and compared at every merge; desk-2 merges last. | desk-2 |
| R15 | The phone loop is flaky in Chromium | Medium | Medium | Tagged lock-step (no timing assumptions); gated test; no retries hide failures. | multiscan |
| R16 | Users read "round 2" as "done" for items that are foundations | Medium | Medium | §8's "Proved by" column, "Below bar this round" where nothing proves the bar, the wire-only list, and `ROADMAP.md`. | integrator |
| R17 | Thirty sessions take about eleven rounds; the user's time or budget runs out first | Medium | High | Waves start packages as soon as dependencies merge; §9 decision 1 names the deferral order; every merged package is independently useful. | orchestrator |
| R18 | Patterns never takes P2, so PlayMap stays a grey ShowApp in Patterns' Eye | Medium | Medium | The patch, its tests and the contract test are ready; PlayMap's own `FLEET`/`EYE` board works without it; §9 decision 10. | fleet |

---

## 8. Coverage: every line of the request

"Comparative" = matches what the named competitor ships; "superior" = exceeds it in a stated, measurable way. Each row gives the
bar the feature must finally clear, what round 2 reaches, and the acceptance test that proves it. A part with no test that proves
the bar says **Below bar this round**.

| Request line | Packages | Bar (target) | Round 2 reaches | Proved by |
|---|---|---|---|---|
| Smooth output: a Windows D3D11 output path | output, d3d11 | Superior: DXGI flip model with measured vblank truth per output and a fuse that never strands a show | The DXGI flip path runs on CI's WARP (presents, statistics or the fuse's fallback, the ANGLE read-back); vblank truth measured on GL; DXGI opt-in (experimental) this round. **Below bar this round** on a real display (lab). | `DxgiWarpPresenterTests`, `VblankClockTests`, `PresentFuseTests` |
| Smooth output: framelock | output, sync | Superior: hardware lock where present plus a measured per-pair verdict and certificate everywhere | The lock machine with its never-stall watchdog and 24-bit counter maths (fake driver; NVAPI compile-only); PMVB vblank reports with the measured per-pair phase, uncertainty, next slip and the group verdict. (Anchors align content time, not vblank phase; they are not framelock.) **Below bar this round**: hardware lock on a real board and the certificate (lab, next round). | `FrameLockMachineTests`, `LockCounterTests`, `SyncBoardTests`, `PmvbCodecTests` |
| No HAP decoding on the GPU | decode | Comparative (Resolume, Watchout, disguise decode HAP on the GPU) | Met: HAP, Alpha, Q, Q Alpha and Alpha-only on the GPU, frame = f(time); the layers a node sustains measured. | `HapRenderTests`, `HapSourceTests`, `HapThroughputTests` |
| HAP R and HAP HDR need our own decoder | decode | Superior (few servers play HAP R/HDR; FFmpeg cannot) | Met against our writer and Mesa's BPTC, HDR at 203 nits with nclx; third-party file interop is lab. | `HapRenderTests` (BC7, BC6H), `HapHdrTests` |
| No zero-copy hardware decode | zerocopy | Comparative | The ladder reports the path actually used; the dmabuf GL import half tested where Mesa exports; VAAPI and D3D11VA written to the APIs (D3D11VA opens on CI's WARP or says why). **Below bar this round**: measured zero-copy on real VAAPI/D3D11VA hardware (lab). | `DecodeLadderTests`, `DmaBufImportTests`, `D3d11vaWarpTests` |
| No "optimise on import" (Watchout) | optimise, backup | Superior: a planner that explains itself, verified versions per node, governed by show state | Met for HAP/HAP Q: PSNR-verified versions that keep their audio, HDR kept with words, an armed-aware governor; the backup fetches the same version. HAP R/HDR encoding next round. | `OptimisePlannerTests`, `HapEncoderTests`, `OptimiseJobTests`, `BackupTests` |
| No Spout | d3d11 | Comparative | Send and receive on output's D3D11 device, protocol goldens here, a WARP round trip on CI where WARP shares resources. **Below bar this round**: a live exchange with Resolume or TouchDesigner (lab). | `SpoutProtocolTests`, `SpoutWarpTests`, `SpoutProviderTests` |
| No OMT | videoio | Superior (open, low-latency, few servers have it) | Met: real OMT in and out on loopback. | `OmtLoopbackTests`, `OmtDiscoveryTests` |
| No NDI 6.3 | videoio | Comparative | Met in code with 6.3's discovery server, groups and advertisers against a fake runtime; an opt-in real-runtime loopback. | `NdiFakeRuntimeTests`, `NdiRealRuntimeTests` (opt-in) |
| No OSC | control | Comparative (Millumin, Resolume send feedback), superior with OSCQuery | OSC in met and cross-checked with another implementation. **Below bar this round** for two-way OSC (feedback out is control's first stretch) and OSCQuery. | `OscServerTests`, `OscInteropTests` |
| No MIDI | control | Comparative | MIDI in met (ALSA here, WinMM listed on CI); MIDI out and profiles next round. | `MidiParserTests`, `MtcTests` |
| No Art-Net | control, led | Comparative | DMX in with a built-in personality, LED out, one socket. | `ArtNetInTests`, `DmxPersonalityTests`, `ArtNetSharedSocketTests` |
| No timecode chase | control, sync | Superior: frame-exact chase as a pure function of fitted code, identical on every node | LTC (independent goldens and an external WAV), MTC and Art-Net TC chase; multi-node at the sync merge; TC outputs next round. | `LtcDecoderTests`, `ChaseTests`, `TimecodeMultiNodeTests` |
| No Companion module; two-way, feedbacks, automations, auto-created buttons, creative | companion, companion-module | Superior: show-built presets that survive renames, generated actions for every verb, value overrides, receipts, safe presses, the Live page | Met: line WS, INDEX with every package's sections, EVENTS, typed verbs, THUMB, mDNS; the 5.1 module with template groups by id, layered keys and composites, value overrides, delta encoders, receipts, event pulses, the Live page. The node → Companion link (press, var, brightness) is stretch; REST provisioning next round. | `presets.test.ts` (rename), `encoder.test.ts`, `override.test.ts`, `receipts.test.ts`, sanitise/check/build, `e2e-engine.test.ts`, `IndexTests` |
| No Show Mode | show, desk | Comparative to QLab/Millumin/Mitti (no password) | Met: ARM/DISARM, the gate on every transport, the desk strip. | `ShowModeTests`, `ShowPolicyTests` |
| No preflight check | show, blind, every package | Superior: checks with fix lines that run (Watchout's going online, Patterns' Super Check, disguise's health) | Met: one section per package with fix lines; fleet-wide with `PREFLIGHT ALL`. | `PreflightTests`, `PreflightBreadthTests`, the integration's one-section-per-package test, `ArmAllTests` |
| No redundancy | sync, backup | Comparative (Watchout, Pixera, disguise) | A hot backup that mirrors the air, switches the projector's input on FAILOVER, pulls with proof and plays the same version; `FLEET PROMOTE` for followers. **Below bar this round** for automatic takeover on a timeout (backup's stretch). | `BackupTests`, `RouteTests`, `PromoteTests` |
| No health view across machines | fleet, desk-2 | Comparative to Pixera, superior through the Eye link | HEALTH of every machine, `FLEET` with focus and lenses, fleet cards on the desk. | `FleetMonitorTests`, `FleetGraphTests` |
| No effects (ISF or FFGL) | fx | Comparative (ISF like MadMapper/VDMX; FFGL like Resolume) | ISF met with chains at three attach points. **Below bar this round** for FFGL (next round). | `FxChainTests`, built-in pixel tests |
| No audio reactivity | audio, content | Superior: one modulation bus, identical on every node | Analysis, onsets, beat clock and bindings; WASAPI loopback on Windows; shared frames to followers stretch. | `ModulationTests`, `BeatClockTests`, `ReactiveRealBusTests` |
| No LED pixel mapping | led | Comparative (MadMapper, Resolume) | Met for Art-Net, sACN and DDP from canvas, output and surface. **Below bar this round** for visual fixture placement on the desk (wire and generic cards only). | `LedSamplingTests`, `LedSenderTests` |
| Expand on Particles, Fractals and Reactive | content | Superior: GPU particles and raymarched fractals that are pure functions of show time | GPU flow particles, three raymarched fractals, reactive that listens; deep zoom and flames next round. | `ParticleDeterminismTests`, `FractalTests`, `ReactiveRealBusTests` |
| No workflow for facades | facade, stacks | Superior: survey-grade import, control points, long-throw priors, a guided workflow | Site frame, PLY/OBJ/LAS/**E57** import, control points, long-throw priors, geodesy, the guide's steps with their lines, one scripted end to end. Night scans next round. | `E57Tests`, `LongThrowTests`, `SiteGuideTests`, `FacadeEndToEndTests` |
| No workflow for stacked projectors | stacks, align | Comparative to disguise/Pixera stacking | Stacks aligned and colour-matched, drawn on the air as a warp layer; flat stacks converge through align. | `StackTests`, `StackRenderTests`, `MeasuredWarpTests` |
| Panasonic, Epson and Christie have built-in camera alignment | align, resolve | Superior: any brand (your phone), measured and explained, driving the projector's own processing where it has one | Flat screens: warp, blend with pedestal, uniformity and γ, WHITE colour and level match, `ONLY`, an explained score, the double-correction guard, four projectors in ≤ 90 s of patterns, any `IScanCamera`. **Below parity this round** on curved screens, FULL colour matching and driving the projector's own processing (next round). | `MeasuredBlendTests`, `ColourMatchTests`, `AlignOnlyTests`, `AlignScoreTests`, `DoubleCorrectionTests`, `AlignTimingTests` |
| Phone-scan auto-mapping, fully local | phonescan, multiscan | Superior: nothing leaves the LAN | Met: the page and the solve run on the node; closed-loop tested with Android and iPhone profiles. | `PhoneInviteTests`, `PhoneScanE2E` |
| … on multi-projector curved surfaces | multiview, multiscan | Superior (Ghost Arcade finds flat surfaces only) | **Met (simulator); lab confirms**: wall-and-cylinder, 2 projectors, 3 views within 5 mm and 0.05°; coverage ≥ 95 % against ≤ 80 % for one view; per-projector regions; persisted scans; ≤ 15 s per projector. | `MultiViewSolveTests`, `DenseDepthTests`, `MultiViewScanTests`, `RegionTests`, `PhoneScanE2E` |
| … and on safe AI | multiscan, show | Superior: suggestions are proposals a person approves | Met: `SCAN SUGGEST` → proposals (surfaces and pixel quads); nothing applies without `APPROVE`; the assistant sees text only. | `ScanSuggestTests` |
| MadMapper and HeavyM: the first five minutes trivial; draw a quad and edit while looking at the wall | wall, resolve, desk-2 | Comparative, superior with wall-pixel nudges, the overlay through the warp and a cursor on the wall | Met: LIGHT WALL, pixel quads with the UV checker, the on-wall overlay and cursor (p95 ≤ 2 frames), the desk's Q tool; the first map in under 5 s of machine time. | `FirstMapTests`, `CursorTests`, `PixelSurfaceRenderTests` |
| disguise: a calibration score that explains itself | wall, multiscan, resolve | Superior: every term has words and a fix line that runs | Met for pins, scans and align. | `ScoreTests`, `ScanScoreTests`, `AlignScoreTests` |
| disguise: a saved fallback result | wall, align | Comparative | Met: the ledger with fallback, accept and recall. | `LedgerTests`, `AlignLedgerRoundTripTests` |
| Watchout: optimise media on import | optimise | See above | Met (HAP targets). | as above |
| Watchout: keep playback machines running if the controller dies | sync, backup | Comparative, superior with free-wheel on the leader's time line | Met: no desk needed; followers free-wheel, keep switching, and one can be promoted. | `AnchorFeedTests`, `PromoteTests` |
| Pixera: locks | show | Comparative | Met: entity and area locks enforced in the store, including undo. | `LockTests` |
| Pixera: a health view of every machine | fleet, desk-2 | Comparative | Met (FLEET and the desk's cards); the `/fleet` page is stretch. | `FleetMonitorTests` |
| Link with the Patterns ecosystem and Patterns "Eye" | fleet | Superior: the node's context shaped for the Eye's window, in its own verbs | **Met on PlayMap's side; lights in Patterns' Eye after Patterns takes P2** (the patch and its tests are prepared, §9 decision 10). | `EyeContextTests`, `PatternsEyeContractTests` |
| QLab, Millumin, Mitti: Show Mode with no password | show | Comparative | Met. | `ShowModeTests` |
| … blind editing | blind, desk-2 | Comparative (Millumin's Silent Edition, Pandoras Box's blind mode) | Met: the draft seen off air in the editor view, its diff, commit now or at the next item or GO, discard. | `BlindTests`, `BlindRenderTests` |
| … a panic button with defined outcomes | show, blind, sync | Superior: planned outcomes per output (Mitti's fade & pause, rewind, next), fades as functions of show time, sound with picture, every node of the group | Met. | `PanicTests`, `PanicRenderTests`, `AirAudioTests`, `AnchorFeedTests` (both nodes 0.5 ± 1e-6) |
| Scalable and VYV: re-solve only the projector that moved | wall, resolve | Comparative, superior once the camera names the mover | Met: a camera re-solve of only the mover (attribution), quads and pins re-seated, blends following; the LENS/CODED/DENSE ladder next round. | `ResolveCameraTests` (≤ 0.3 px p95, ≤ 2 s of patterns), `ReseatTests` |
| … keep a calibration history | wall | Comparative | Met: history with Δmm, Δdeg and Δwall px. | `LedgerTests` |
| Research the latest Companion release; latest developments for creative programming and operation | companion-module, fx, content, audio | Superior | The module on base ~2.2.1 with Companion 5.1's features; ISF, modulation and GPU content as above. | as above |

**Wire-only in round 2** (reachable by the wire, MCP and Companion; on the desk only as a generic STATE card and generated right-click
entries, no dedicated view): effects browser and inspector, modulation sockets, tempo, bindings and learn, timecode, LED fixture
placement and patching, optimise jobs, live inputs and sends, the phone scan path, align, the facade guide, the sync and framelock
tab, Companion decks, the content library. desk-2 writes this list into the papers note for `UX.md`.

---

## 9. Open decisions

1. **Twenty-eight packages, or fewer.** As planned, 28. If the round must be smaller, defer whole packages in this order, marking
   their §8 rows "Below bar this round": wp2-stacks (stacks then converge only through align), wp2-desk-2 (generic cards remain),
   wp2-zerocopy, wp2-content. Never align, phonescan, multiview, multiscan, companion-module or sync: they carry the request's
   explicit "win" lines.
2. **Windows verification.** This round on the repository's Windows CI job with WARP; a lab Windows machine with a real GPU confirms
   DXGI before it becomes `AUTO`.
3. **DXGI as the default.** Experimental and explicit this round; `AUTO` stays GL until the lab run.
4. **FFGL.** Deferred to next round (ISF covers "effects" now); confirm that ordering.
5. **libvmx and ANGLE distribution.** Shipped per platform beside the exe (MIT/BSD), or loaded from a user install like NDI. The
   plan assumes shipped.
6. **The Companion module's publication.** The manifest id (`jammin808-playmap`) and submission to the module registry.
7. **Approvals through Stagewatch.** Switching `StagewatchProposalSink` on changes who can approve AI lines; confirm before it ships.
8. **Ports.** 9943 (OSC), 9944 (OSCQuery) and 9945 (PMMD) are new node defaults; 9942 is today's clock-sync port, which the PMVB
   reports share by their magic. Confirm they do not collide with Patterns' or Stagewatch's defaults on show networks.
9. **Main has moved** (`d7d7862`, `7a01783`) since the spine was read; the spine rebases at start (no conflict expected).
10. **Patterns P2.** wp2-fleet prepares P1–P3 as a patch with Patterns' tests; Patterns' maintainers decide whether to take it. Until
    they do, PlayMap is a grey ShowApp with word lines in Patterns' Eye.
11. **Lowering the risk of safe-direction lines** (BLACKOUT ON, FREEZE ON, MASTER 0, DEVICE SHUTTER CLOSE, PANIC → Review). Not this
    round: it would let any MCP client black out the air or close every shutter with no person approving. If wanted, it is a
    separately approved change that edits the four existing tests with the reason.
12. **Clip sound on air.** wp2-blind plays the on-air item's audio on the node's default output, faded with the air. Confirm the
    default (on) and that a node setting turns it off for shows that run sound elsewhere.

---

## 10. Decisions on the review's findings

Every blocker, major and cheap minor of the review is taken, in this file, in `SPINE.md` or in the designs, except where a note below
says how this cut differs and why.

| Finding | Decision |
|---|---|
| Framelock above the cut | Taken, split: F4 (the lock machine) is output's item 4; F3 (PMVB and the verdict) is in the new **wp2-sync** with F1, F2 and the fan-out, because sync now owns `ClockSync.cs` (so PMVB needs no demux hook in another package's file). Output's GLES second leg moved after its cut as asked; the DXGI path moved to wp2-d3d11 to make room. |
| Companion depth | Taken: node package (W1) and module package (W2). mDNS sits in the node package; S2's press/var/brightness are node stretch. The correctness minor (no `bonjourQueries` without a responder) is resolved the other way: the responder ships this round. |
| Panic audio | `AudioOut` exists on `main` but has no caller: clips play silent today, so there is no hard cut to hear yet. Show defines the air's sound gain from the same `AirFade` (tested to 1e-6); wp2-blind puts the on-air item's audio on `AudioOut` under that gain and holds the RMS test (§9 decision 12). |
| Unassigned carry-overs | Most were fixed by the polish merge `cb3cfc0`, the spine's base (§6.4 lists them with their commits); the open ones have owners and tests. Manual-GO transitions go to wp2-blind (show's transport file), not output. |
| Preflight breadth | Taken for every engine package; the Core-only multiview and the two desk packages are excepted (they own no show resource). |
| Zero-copy ladder | Above a cut, but in **wp2-zerocopy** (decode's continuation) rather than decode's own slice, which the HAP family fills; decode's per-item colour override is dropped as asked. The upload arena (HAP throughput minor) is zerocopy's item 4; the throughput benchmark and its preflight row are decode's. |
| Spout | In **wp2-d3d11** beside the D3D11 device it shares and its WARP leg, not in videoio (videoio is at 27 h and would need output's device anyway). Videoio's loss policy stays stretch. |
| Audio capture on Windows | The two findings disagree. NAudio.Wasapi 3.1.0 targets `net9.0`, not a Windows TFM, so it sits inside the single `net10.0` exe: taken, every call guarded by `OperatingSystem.IsWindows()`. The Linux text is corrected to pulse/oss/openal (no ALSA in this FFmpeg). |
| Two-way control (minor) | The DMX personality is in control's must; OSC feedback out stays control's first stretch (control is at 27.5 h), and §8 says "Below bar" for two-way OSC unless it lands. |
| Score for automatic paths | Taken: wall owns the vocabulary (W1); phonescan's terms are filled in wp2-multiscan and align's `ALIGN SCORE` in wp2-resolve (W3), not in align's W2 slice, which spends its hours on the blend's parity items. |
| Align parity | Taken across align (M11 complete, sequential capture, any `IScanCamera`) and wp2-resolve (WHITE match, `ONLY`, score, minimal M4, the 90 s test). `DEVICE NATIVE` stays as a read-only query for M4's guard; the minor's removal of `DEVICE SNAPSHOT/NEUTRAL/RESTORE` is taken. |
| Facade workflow | E57 is facade's must; `STACK MATCH`, the measured lattice on the air, geodesy, the guide and the end-to-end are wp2-stacks'. The Folder camera goes to wp2-multiscan, which owns the scan runner's files, rather than to facade. |
| Phone scan (iPhone, tags, AI order) | Taken; the suggestions move to wp2-multiscan (W3) so phonescan (W2) fits its steady profile and tags; they stay above a cut line, and the E2E is what shrinks if time runs short. |
| Desk coverage | Taken through data: each package writes card descriptors (`<pkg>.cards.json`) and the desk renders them with one generic template; packages do not build App themselves (the disk rule). |
| Splitting W0 | Taken (W0a, W0b). Landing only above-cut rows was not taken: `VerbParams`, MCP and the desk's verb rule test generate from the rows, and the availability bit keeps unrouted rows out of prompts. |
| Shader dialect in W0 | Taken for the convention and `GlDialect.Core330`. The EGL surfaceless ES fixture stays with output, since it is output's EGL work; fx's ES leg runs at the merge after output. |
| Pixel quads re-seated, grid and names | Taken: the overlay's grid and names are wall's stretch; M8's minimal re-seat is wp2-resolve's. |
| First five minutes on the wall | The UV checker on a new quad is wall's must (W1). The single-session cursor, its latency check and firstfive M9's first-map test sit above the cut of **wp2-resolve** (W3, wall's continuation), not wall's W1 slice, which the score vocabulary and the ledger fill; §8 cites their tests. |
| Fleet's additions | Taken across three packages (fleet W1: board, Eye, Patterns patch; sync W2: anchors, slew, fan-out; backup W3: mirrored air, routes, proven pulls, promote). Automatic takeover moved to backup's stretch, not to next round, because backup has room. |
