# Outputs: presenting, measuring, locking

How a PlayMap node gets its outputs' pictures to the displays, how it knows what the displays really showed, and how outputs on
one or many machines share one vblank. The wire's side is in `docs/wire/output.md`.

## The platform and the presenters

The render host starts its **platform** (`IGpuPlatform`, `src/PlayMap.Render/Platform`) from the node's settings
(`NodeSettings.Present`: `path` auto|gl|dxgi, `dialect` auto|core330|es300, `frameLock` auto|off, `swapGroup`, `swapBarrier`):

- **GL** (every platform this round): a hidden GL context through GLFW, and one **presenter** per output window
  (`IOutputPresenter`; the GL window, `OutputWindow`, is the first). The renderer draws every pass offscreen in the primary
  context; a presenter only shows the output's finished picture and swaps.
- **Dialect.** `core330` is desktop GLSL 3.30 core (the default). `es300` is OpenGL ES 3.00: on Linux the context is made through
  GLFW on **EGL**, and when EGL refuses it is made on GLX and `machine.present.why` says so. A node with no window system can render
  on EGL's **surfaceless** platform (no presenters; every output offscreen). `"dxgi"` is not a path this build presents on: it runs
  GL and says so.
- **Program sources carry no `#version`**: the device writes the dialect's header (`#version 300 es` plus precision lines for
  float, int and every sampler kind). Every program in `ShaderLibrary` compiles and links on both dialects (tested on Mesa's ES 3.2,
  which compiles `#version 300 es`).
- **The per-dialect format table** (`GlFormatTable`): BGRA uploads on ES go up as RGBA with the texture's red/blue swizzle (core
  ES 3.00, no copy); 16-bit unsigned textures fall back to 16-bit float without `EXT_texture_norm16`; float render targets need
  `EXT_color_buffer_float` (else 8-bit, with words); GPU timers need `EXT_disjoint_timer_query` (else GPU times read 0); wireframe
  needs `ANGLE_polygon_mode` (else the editor's outline is not drawn); BGRA read-backs need `EXT_read_format_bgra` (else a
  synchronous read swaps on the CPU and the streamed read-back refuses with words).
- **The alias loader** resolves `glFoo` through `glFooEXT`, `KHR`, `OES` and `ANGLE`, so an ES context reaches the debug, timer and
  polygon-mode calls by their extension names.
- The platform hands decoders its **device share** (`IGpuShare`: OpenGL, nothing imported directly this round) through
  `IMediaBackend.UseGpu`.

## Vblank truth and the present ledger

Every output keeps a **present clock** (`src/PlayMap.Render/Timing`):

- **`VblankClock`** fits `t = a + b·k` over its samples: the period from a long window (3 600 samples, 60 s at 60 Hz), the phase
  from a short one (240) with the period fixed; two-pass, centred, with samples more than a quarter period off refitted out.
  Settled after 30 samples. 600 samples at 60 Hz with ±0.5 ms of jitter give the period within 1 µs and the phase within 0.2 ms.
- **The pacing window's** swap returns are its samples (the refresh index counted from the gaps; a gap of n periods is n − 1
  missed vblanks, counted once). Its phase is approximate (a swap returns just after the vblank, not at it). The other windows
  present without waiting and are not measured. **Offscreen** outputs are counted against their own configured refresh.
- **`PresentLedger`** records, per present, the refresh it was meant for, and the refresh that showed it: shown later is a glitch
  and that many missed vblanks; refreshes with no new picture are repeats; presents replaced before they showed are discarded; a
  present the queue could not take is held. Unsigned arithmetic throughout (wrap-safe). Per-second buckets give `missed60s` and
  the late share of the last 10 s.
- A frame starting in the last quarter period before a vblank is meant for the one after it (a swap's return jitters around the
  fitted instant).
- **The output's light** (STATE `outputs[].light`, and `present.light`) is the display side's count against the output's own
  refresh: amber only when more than 0.5 % of the last 10 s of refreshes came late; green again once the last 10 s are clean.
  `outputs[].dropped` is those late presents (0 within the budget). Totals stay in `present.missedVblanks`.
- `PRESENT RESET` zeroes the counters and restarts the fit; the old totals go to the journal.

## The frame lock

The lock is **per output** (`FrameLockMachine`, Core, pure): facts in once a frame, a state, its words and the driver's actions out.

| State | Meaning |
|---|---|
| `free-unknown` / `free-measured` | Free-running on its own vblank, its clock not settled / settled |
| `joining` | Asked to join; 120 refreshes in a row with the board's count advancing by exactly one, the board synced and the barrier bound confirm it (5 s at most) |
| `framelocked` / `house-locked` | Locked (on house sync) |
| `fell-out` | Was locked or joining; the lock broke; presenting free. The reason is kept |

- **Falls out** on a present that waited more than three periods (the never-stall watchdog: the output leaves the barrier and
  presents free from its next present), a count that stood still for three periods, a board unsynced for three periods (locked
  only: shorter losses are debounced), a busy device, a changed display, or a driver that refused the join.
- **Never rejoins on its own while armed.** Disarmed, with `frameLock` auto, an output rejoins by itself once the board has been
  synced for the 10 s since the fall and the show is at a boundary (blacked out, or nothing playing). Otherwise `SYNC REJOIN`
  (armed: only while blacked out) or a new `SYNC APPLY`.
- **Refused with a reason word**: `no-board`, `not-configured` (the one-time administrator step), `cable-into-switch` (the board's
  RJ45 ports go board to board, never to Ethernet), `armed`.
- **Content time under the lock** (`LockCounter`): the board's 24-bit frame count is unwrapped (a jump over 2²³ is a fault, a value
  over 2²⁴ switches to 32 bits); the anchor `{count24, leaderNs, periodPs, effectiveAt24, seq}` maps any count to show time as
  `leaderNs + ((c − count24) mod 2²⁴) × periodPs / 1000` — the same integers on every node. A leader re-anchors every 60 s from its
  pacer's fitted vblank, taking effect 120 refreshes later, so every node switches on the same vblank; a follower adopts the
  leader's anchor (`IFrameLockBackend.OfferAnchor`, carried by the group's anchor feed). While the pacing window is locked with a
  valid count, the frame's show time is the anchor's time of the next count (`sync.lock.time` = `count`); otherwise the leader's
  clock plus the time to the pacer's next vblank (`clock`).
- **Drivers** (`IFrameLockDriver`, `src/PlayMap.Render/Sync`): `nvapi` on Windows reads the sync board through NVAPI's GSync calls
  (board names by id: 856 Quadro G-Sync (P358), 8288 Quadro Sync (P2060), 8289 Quadro Sync II (P2061); any other "sync board
  0x…", capabilities unknown). NVAPI's swap groups act on a DXGI swap chain, so on a GL window the join is refused with words and
  the output runs free, measured. `none` elsewhere ("NVAPI is Windows only"). `fake` scripts a board for tests and rehearsals.
  The board is polled off the render thread once a second.

## Preflight, INDEX, context

- **Preflight** (section `outputs`): per enabled output a present row (green; amber past the late budget, with what to do; grey
  before anything was presented) and, while the lock is applied, a lock row: green locked, amber joining, amber held out of a
  group that applied the lock (fix `SYNC REJOIN <output>`), amber free after a refused join (fix `SYNC APPLY`, once what refused
  it is put right), red fell out (fix `SYNC REJOIN <output>`).
- **INDEX** section `present`: per enabled output its present light (red when it fell out of the lock) and lock state; keys
  `OUTPUT STATS $(id)` lit by `outputs.$(id).present.light`, `SYNC REJOIN $(id)` lit by `outputs.$(id).lock.state`.
- **Context**: while the lock is applied (or an output fell out), an item `sync:lock` (kind `sync`) lit by the group's lock, linked
  from each output with the Eye's verbs (`follows` while locked, `hears` while free); a `machine:present` item (amber) when the
  platform is not what was asked.

## Verified here, compile-only, next

- **Here** (Mesa llvmpipe on Xvfb): every program on core 3.3 and on ES 3.00 (EGL surfaceless and GLFW); BGRA swizzle, float
  targets, an output through the whole frame on ES; the presenter seam with the GL window and a scripted presenter; vblank and
  ledger arithmetic on synthetic sequences (Xvfb's swaps are no real display); the lock machine, the counter and the verbs against
  the scripted board on the real render host (headless and windowed).
- **The ES leg**: `PLAYMAP_GL_DIALECT=es300` runs the whole Render suite on an OpenGL ES 3.00 context (GLFW, on EGL here). Every
  test passes on it except the two spine tests that assert a desktop core context by name
  (`GlCapsTests.OnTheTestsCoreContextEveryBlockFamilyReadsTrue`, `ShaderConventionTests.EveryProgramCompilesAndLinksOnCore33`);
  the leg runs with those two filtered out until they read the fixture's dialect, and it is not yet a step of the CI workflow.
  On ES without a polygon-mode extension the editor's selection outline is not drawn (its filled pass would cover the surface).
- **Compile-only**: the NVAPI tier. Its structure sizes and version words are checked against NVIDIA's header here; on the Windows
  CI job it says why it has no board ("no NVIDIA driver on this machine").
- **Next round**: 10-bit output, device-lost rebuild, the PresentMon proof, the DRS stop-gap, scanline sampling; `SYNC MARKS`,
  `SYNC CERTIFY`, `SYNC REPORT`, the frame code and certificate, `LATENCY MEASURE`; the IGCL and GLX swap-group tiers; the board
  helper; the D3D12 present barrier; HDR; macOS. The whole Render suite as a second leg on GLES, and OML sync control on Linux.
