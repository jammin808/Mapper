# Outputs: presenting, measuring, locking

How a PlayMap node gets its outputs' pictures to the displays, how it knows what the displays really showed, how outputs with
different input lags light the same picture at the same instant, and how outputs on one or many machines share one vblank. The
wire's side is in `docs/wire/output.md` and `docs/wire/d3d11.md`.

## The platform and the presenters

The render host starts its **platform** (`IGpuPlatform`, `src/PlayMap.Render/Platform`) from the node's settings
(`NodeSettings.Present`: `path` auto|gl|dxgi, `dialect` auto|core330|es300, `frameLock` auto|off, `swapGroup`, `swapBarrier`):

- **GL** (what `auto` is on every platform this round): a hidden GL context through GLFW, and one **presenter** per output window
  (`IOutputPresenter`; the GL window, `OutputWindow`, is the first). The renderer draws every pass offscreen in the primary
  context; a presenter only shows the output's finished picture and swaps.
- **DXGI** (Windows, experimental, only on an explicit `PRESENT PATH DXGI`): see *The DXGI flip path* below. Off Windows `dxgi`
  is refused by the verb and, asked through the settings file, runs GL with `machine.present.why` saying "the DXGI path is
  Windows only".
- **Dialect.** `core330` is desktop GLSL 3.30 core (the default). `es300` is OpenGL ES 3.00: on Linux the context is made through
  GLFW on **EGL**, and when EGL refuses it is made on GLX and `machine.present.why` says so. A node with no window system can render
  on EGL's **surfaceless** platform (no presenters; every output offscreen).
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
- The platform hands decoders its **device share** (`IGpuShare`: OpenGL on the GL platform, Direct3D 11 with its LUID, its lock
  and its ANGLE display on the DXGI one) through `IMediaBackend.UseGpu`.
- **The GL context's window-system API on Linux** is `decode.glApi`: `auto` makes an ES context on EGL and a desktop one on GLX,
  `egl` makes both on EGL (the display then imports VAAPI's dmabufs, `docs/DECODE.md`), `glx` both on GLX. Output windows are made
  on the context's own API (a shared context comes from one API); an EGL window the platform refuses comes up on GLX with the words,
  and a word it does not know is the platform's choice with words. A surfaceless context is EGL whatever it says; other platforms
  have one API.

## The DXGI flip path

`AnglePlatform` (`src/PlayMap.Render/Platform/Windows`, `[SupportedOSPlatform("windows")]`) comes up in stages, and a start that
stops names its stage (`adapter`, `device`, `angle`, `extensions`, `context`, `programs`) in `machine.present.why` while the node
runs GL:

1. **Adapter and device.** `CreateDXGIFactory2`; the adapter whose outputs include the primary display (else the first; WARP on the
   CI runner); `D3D11CreateDevice` at feature level 11.1/11.0 with BGRA and video support (without video where the adapter refuses
   it, as WARP does); `ID3D10Multithread` protection on. Decoders go on the device only when it has both: otherwise
   `decode.platform.why` says which is missing and clips copy back.
2. **ANGLE** (BSD), loaded at run time by the single name `av_libglesv2` — the file Avalonia.Angle.Windows.Natives puts beside the
   exe; PlayMap adds no binary. Its EGL entry points are resolved from that handle by the names the library exports them under
   (`eglFoo` as `EGL_Foo`).
3. **Three extension lists**, the refusal naming the first missing one and its list: client (`EGL_EXT_platform_base`,
   `EGL_EXT_platform_device`, `EGL_EXT_device_query`, `EGL_ANGLE_device_creation`, `EGL_ANGLE_device_creation_d3d11`), device
   (`EGL_ANGLE_device_d3d11`), display (`EGL_ANGLE_d3d_texture_client_buffer`, `EGL_KHR_surfaceless_context`).
4. **The context**: `eglCreateDeviceANGLE` on our device, the display on it (verified: the display's device is ours, else "ANGLE made
   its own device"), an ES 3.0 context current with no surface, adopted as the renderer's (`RenderContext.Adopt`); every program
   is translated inside the start (a program ANGLE refuses is the `programs` stage's words and a GL start).
   **The device lock.** ANGLE records the renderer's work on the device's immediate context, and decoders share that context, so
   it has one recursive lock (`GpuDeviceLock`, `IGpuShare.Enter`/`Exit`): every thread issues immediate-context work only while it
   holds it. The render loop holds it around each frame and the work between frames, and lets go of every hold while a window
   waits on its swap chain (sharers submit during the vertical-blank wait); FFmpeg's D3D11VA decoders take it through their lock
   callbacks around each begin–submit–end bracket; Spout on this device takes it around each of its calls. A sharer waits as long as
   the frame takes; the frame waits at most `GpuDeviceLock.FrameBoundMs` (4 ms) for a sharer's bracket, and past it is drawn on the
   device's multithread protection (each call ordered) and counted (`TimedOut`). Nothing allocates in the lock once warm. One
   device and one immediate context are what let a decoded frame go as soon as the renderer's read of it is recorded.
5. **One window and swap chain per display output** (`DxgiOutputWindow`): a borderless top-most popup over the monitor (transitions
   off, the cursor hidden), a waitable `FLIP_DISCARD` swap chain (three B8G8R8A8 buffers, no scaling, no tearing, frame latency 1),
   Alt+Enter and window changes left to PlayMap; a swap chain that lands on another display than its monitor is refused with words.
   The nominal refresh is the active path's (`QueryDisplayConfig`: the target mode's `vSyncFreq`, else the path's `refreshRate`).
6. **Buffer 0 wrapped once per swap chain** as ANGLE's pbuffer (`eglCreatePbufferFromClientBuffer`, `EGL_D3D_TEXTURE_ANGLE`): the
   flip model renames buffer 0's storage on each present, so the same texture comes back; the cache holds exactly one wrapper and
   says so if a different texture comes back without a resize. The picture is drawn into it rows as they are (that surface runs top
   first, as PlayMap's targets do).
7. **Present**: `glFlush` then `Present1(1, DO_NOT_WAIT)` — a full queue holds the previous picture one refresh (counted, never a
   block, never a tear); occluded and device-removed come back as the result's problem. The pacing output waits on its waitable
   object after it presents (pumping window messages, at most three periods, the device lock let go of meanwhile), so the next
   frame starts just after the vblank as a GL swap returns; every other output presents only when its waitable is already signalled, and a count it took for a present that
   did not queue is kept for its next present rather than lost.
8. **Statistics**: after every output presented, `GetFrameStatistics` once per swap chain. The first statistics switch the output's
   clock to display timing — `SyncRefreshCount` and `SyncQPCTime` (QPC to ns as `(q / f)·10⁹ + (q % f)·10⁹ / f`, which never
   overflows) feed the fit, and the ledger is told which refresh first showed which present (32-bit counters unwrapped with unsigned
   differences); disjoint statistics restart the fit; statistics that stop moving for two periods send the clock back to swap
   returns and ask the compositor nudge, which calls `DwmFlush` on its own thread at most once a second — never on the render thread.

**The fuse.** Before a DXGI start the render host writes `<node home>/playmap.present.fuse` (`{"path","attempt","at"}`); the lane
deletes it once every open display output has presented 120 frames and the pacing one's clock has settled (with no display open,
once 120 frames were drawn), and an orderly stop deletes it too. A start that fell back to GL in-process leaves it blown. A fuse still there at the next start (a crash or a
hang before the outputs came up) makes that start GL, with words naming the attempts and when; `machine.present.fuse` says `clear`,
`armed` (this start waits for its outputs) or `blown`; the preflight's present-path row is amber with `PRESENT RESET`, which clears
it, as asking any path again with `PRESENT PATH` does. **`PRESENT PATH AUTO|DXGI|GL`** keeps the path on the node for the render host's next start;
`machine.present.asked` is what the next start asks, `path` what this one runs, `experimental` true on DXGI.

## Per-output latency

`Output.LatencyMs` is the display's input lag (0–500 ms). The node's **air latency** is the largest of its enabled outputs' (raised
to a sync group's through `IPresentBackend.SetGroupAirLatency`, never lowered; with none named, the configured display latency):
every frame is drawn for the instant its light leaves the slowest display. Each faster output presents the picture drawn
`d = round((L_air − L_o) / T_o)` frames ago, from a **delay ring** of `d + 1` pictures of its size (indices swapped; the pictures are
made only when the delay changes, so a warm frame allocates nothing). Two outputs of 17 and 50 ms at 60 Hz: the first waits 2
frames, 0.33 ms left over; the node's air is 50 ms. A scan's raw frame is never delayed (its present stamp is the frame's own).
Previews show what was presented (`RenderEngine.PresentedPicture`); the finished picture — what sends, LED maps and snapshots
read (`OutputPicture`) — is the frame's own. After a scan's raw frame, or frames the output skipped (switched off, not drawn), the
ring starts again: nothing from before the break is shown after it, and the delay builds again from the first picture written. STATE:
`outputs[].present.delayFrames`, `latencyResidualMs` (|residual| ≤ half a frame), `machine.present.airLatencyMs`, `latencyWords`.
At most six frames: an input lag that would need more is refused with the words.

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

- **Falls out** when the barrier's own wait timed out after three refreshes (the driver's fact; the never-stall watchdog: the
  output leaves the barrier and presents free from its next present; how long a present call took is never taken for a stalled
  barrier, since it holds the node's own work), a count that stood still for three periods, a board unsynced for three periods (locked
  only: shorter losses are debounced), a busy device, a changed display, or a driver that refused the join.
- **Never rejoins on its own while armed.** Disarmed, with `frameLock` auto, an output rejoins by itself once the board has been
  synced for the 10 s since the fall and the show is at a boundary (blacked out, or nothing playing). Otherwise `SYNC REJOIN`
  (armed: only while blacked out) or a new `SYNC APPLY`.
- **Refused with a reason word**: `no-board`, `not-configured` (the one-time administrator step), `cable-into-switch` (the board's
  RJ45 ports go board to board, never to Ethernet), `armed`. A node whose `frameLock` is off never joins: `SYNC APPLY` and
  `SYNC REJOIN` refuse with `off`.
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
  the output runs free, measured; on the DXGI path the join is refused too ("this build joins no DXGI swap chain to a swap group
  yet") and the output runs free, measured. `none` elsewhere ("NVAPI is Windows only"). `fake` scripts a board for tests and rehearsals.
  The board is polled off the render thread once a second.

## Preflight, INDEX, context

- **Preflight** (section `outputs`): the node's present-path row (amber with `PRESENT RESET` while a DXGI start's fuse is blown;
  amber with the words while the path is not the one asked; green, saying when it is the experimental DXGI path), then per enabled
  output a present row (green; amber past the late budget, with what to do; grey
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
- **Here, for the DXGI path**: the stage faults with a fake platform (each stage's words, the GL fallback), the fuse and its words in
  STATE and preflight, the extension lists, QPC conversion, the one-wrapper cache, display-timed clocks on synthetic statistics, the
  layout and vtable-slot goldens of every hand-declared structure and COM method (against the mingw-w64 headers), and the ANGLE
  loader's file and every entry point it resolves against the package's PE export table. The delay ring on the real renderer (an
  output back after a gap presents what it draws). The device lock's rules on a GL platform wearing a shared device.
- **On the Windows CI job (WARP, no GPU)**: the device on WARP, ANGLE's context with every program translated, a flip-model swap
  chain on a window, 120 presents with buffer 0 wrapped once, the frame statistics read, and the frame read back from buffer 0 top
  row first (`DxgiWarpPresenterTests`, Category=Windows: skipped with the words off Windows, never passed with nothing run).
- **Compile-only**: the NVAPI tier. Its structure sizes and version words are checked against NVIDIA's header here; on the Windows
  CI job it says why it has no board ("no NVIDIA driver on this machine"). The DXGI path on a real GPU and display (lab).
- **Next round**: DXGI as `auto` once proven in the lab; independent-flip evidence (the PresentMon proof, the media statistics hint);
  10-bit output, device-lost rebuild (the DXGI path's included), the DRS stop-gap, scanline sampling; `SYNC MARKS`,
  `SYNC CERTIFY`, `SYNC REPORT`, the frame code and certificate, `LATENCY MEASURE`; the IGCL and GLX swap-group tiers; the board
  helper; the D3D12 present barrier; HDR; macOS. The ES leg as a CI step (once the two spine tests read the fixture's dialect), and
  OML sync control on Linux.
