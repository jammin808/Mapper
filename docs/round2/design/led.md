# WP "led" — LED pixel mapping

Fixtures on canvases, outputs, sources, surfaces and in 3D; MVR, GDTF and CSV import; GPU sampling in linear light;
Art-Net 4, sACN and DDP output latched in step with the render loop; auto-patch; test patterns; monitoring; camera
auto-locate. Research: `r2/research/led.md` (cited below as **[R:§n]**). Code read on `main` (c12b186).

---

## 1. Goal and the bar

PlayMap today has no LED output (docs/COMPETITIVE.md: "no … LED pixel mapping"; ROADMAP M4/M5). The bar, from what
was verified:

| Competitor point (verified) | PlayMap this round | Matches / beats |
|---|---|---|
| Resolume samples the **centre pixel** of each fixture cell; Lumiverses; detected Art-Net nodes fill IP/universe on right-click; Auto Span + **Align Output**; overlaps marked red; ArtSync with a framerate and a fixed **40 ms delay** for mixed light+projection [R:§6] | Point **and footprint (area) sampling in linear light**; ArtPoll/mDNS discovery that fills node + universes; auto-patch with align; conflicts in STATE and red in the desk; **ArtSync/sACN sync/DDP push latched at the light instant of the projector frame**, computed from the measured display latency, not a fixed delay | Beats: area sampling, latch time derived from the projector's own latency per frame |
| MadMapper: Art-Net 4 to 16384 universes, sACN multicast only, universe sync, jumbo frames, up to 1000 fps, cross-universe fixtures, CSV and SVG fixture import, **Led Scanner**, DMX monitor; *cannot map fixtures onto a media directly* [R:§6] | 32768 Art-Net universes (15-bit Port-Address), sACN multicast **and unicast**, DDP; per-node rate up to the render rate; cross-universe fixtures; CSV, **MVR and GDTF** import; DMX monitor; fixtures sample *any* source, canvas, output or surface directly; **camera auto-locate that lands pixels on the 3D stage** | Beats on import breadth, direct source sampling and 3D locate; render-rate cap (not 1000 fps) is a known gap |
| Pixera: Art-Net/sACN/**KiNET v1**; GDTF import; CSV streams (25.1); destination starts at **1×1 so a 4K mapping cannot flood the network**; cannot span interleaved control channels across pixels [R:§6] | GDTF import with interleaved per-pixel control channels handled (per-slot patch table); a **bandwidth guard** that refuses and explains above a Mb/s limit; KiNET later (protocol unverified) | Beats on fixture generality and the guard's explanation; KiNET parity later |
| MADRIX 5: 2D pixel / 3D voxel matrix, Patch Editor with DMX Map, Auto Address, CSV import/export (Capture) [R:§6] | Real 3D positions in metres (not a voxel grid), DMX map view, auto-patch, CSV import in MADRIX/Capture column names | Matches patching; beats on true 3D geometry. Voxel *effects* are later (volume generators) |
| disguise: Art-Net driver, strict IP filtering [R:§6]; 3D LED sampling unverified | World-space fixtures bound to surfaces or sampled from a viewpoint | Parity or better (unverified competitor side) |
| WLED (the hobby/install default): DDP, E1.31, Art-Net; mDNS `_wled._tcp`; `/json/info` [R:§3–4] | WLED found by mDNS, its LED count and port read from `/json/info`, driven by DDP with push | Parity, zero configuration |

Where PlayMap is structurally ahead, and keeps the doctrine:

1. **One content, projection and LEDs continuous.** A pixel bound to a surface samples the surface's content with the
   same `uMode/uRegion/uProjector` maths the projector pass uses (`StageRenderer.SetContent`), so a strip in a mapped
   facade shows exactly what the projection paints around it.
2. **Content is a pure function of show time**, so LED frames are too: every LED frame is stamped with the leader
   instant of the light it belongs to, nodes decimate on the leader-time grid, and two PlayMap nodes driving halves of
   one rig emit byte-identical frames for the same instants.
3. **Attempts are not facts.** STATE says per node: sending at N fps, late by X ms, unreachable (and why), refused
   (and why); discovery says what the node itself reported (ArtPollReply, `/json/info`).
4. **Fail closed.** A project carries the rig; the *machine* decides whether to transmit (`NodeSettings.LedOutput`,
   off on a fresh node). Opening a venue's project on a laptop never blasts the venue's pixels.

## 2. Scope this round

### Must (with acceptance criteria — each is a test unless marked manual)

| # | Item | Acceptance |
|---|---|---|
| M1 | Model `Project.Leds` (nodes, fixture types, fixtures), ids, generic GET/SET/ADD/REMOVE/MOVE, JSON round-trip, older files read empty | A project without `leds` loads with an empty rig; `ADD lednode`/`fixture`/`fixturetype` answer ids; SET on `patch.universe` works; UNDO restores |
| M2 | Codecs: Art-Net ArtDmx/ArtSync/ArtPoll writers, ArtPollReply reader; sACN data/sync/termination; DDP data/push | Byte-exact against hand-built vectors from the offsets in §3.6; readers reject short/garbled packets with reasons |
| M3 | Sampling spaces Canvas, Output, Source, Surface (UV), World (nearest surface or viewpoint) | GL golden on llvmpipe: payload bytes equal the CPU reference exactly for Point sampling of flat and gradient sources, ±1 LSB for Area |
| M4 | Two-pass GPU pipeline (sample → patch), linear-light area sampling, colour orders, RGBW extraction, 16-bit coarse/fine, white balance, content/output gamma, brightness, constant and dimmer channels | Patch-pass unit goldens per feature; GRB/RGBW/RGB16 reference vectors |
| M5 | Sender: per-node rate on the leader-time grid, emission at light instant − node latency, latch (ArtSync directed broadcast / sACN sync universe / DDP push), keep-alive 800–1000 ms, sACN termination ×3, sequence numbers | Loopback: ArtSync follows the frame's last ArtDmx; emission p95 within ±2 ms of target (±4 ms tolerance in CI); keep-alive at 900 ± 100 ms with a frozen show; three terminated packets per universe on stop |
| M6 | Discovery: ArtPoll every 3 s while output runs and on `LED DISCOVER`; WLED by mDNS `_wled._tcp` + `GET /json/info` | Fake Art-Net node on 127.0.0.2:6454 is listed with its short name, SwOut universes and RefreshRate; fake WLED (loopback HTTP + DDP) is probed |
| M7 | Auto-patch (align, each-universe, from, node capacity), manual patch, conflicts, bandwidth guard | Deterministic patches for a fixture set (golden); overlap reported with both fixture ids; a 4K grid is refused with the Mb/s estimate and nothing is sent |
| M8 | Test patterns (chase, order, universe, fixture, gradient, walk, white, full, black), `LED LOCATE`, `LED MONITOR` | Patterns evaluated identically by shader and CPU reference; FlashGuard holds every pattern ≤ 3 flashes/s |
| M9 | Import: CSV (MADRIX/Capture-style columns), GDTF (pixel geometry + channel layout), MVR (fixtures, matrices, GDTF modes, addresses, `Mappings`) | Synthetic MVR (3 × 16-px bars) → 48 pixels within 1e-6 m of expected, patches from `Addresses`, one undo step; CSV errors name the line |
| M10 | Air: blackout before content, freeze holds, machine master, `LED OUTPUT OFF` = one black frame then silence, `LED LEVEL` | BLACKOUT ON → next frame all colour and dimmer slots 0, constants kept |
| M11 | STATE `led`, CONTEXT items, Show Bus cap `led`, MCP tools (generated), journal | STATE shape test; context red for an unreachable node while output is on |
| M12 | Desk: LED tool (line/grid/ring) on canvas, surface and 3D views; outliner group; inspector; Outputs drawer LED tab; DMX monitor; live previs dots | App tests for view-models and verb mapping (the verb-first CI rule); screenshots manual |
| M13 | Fold-ins from CARRYOVER in this area: the render host's headless present stamp ("stamp present time after GPU finish consistently") is used for LED emission timing — LED uses the frame's predicted light instant, not the readback time, so it is immune; per-output display latency is read when the present WP lands (see §6) | Documented and asserted in the timing test |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | Power estimate per fixture type (W/pixel), rig total in STATE, **power cap** (`leds.settings.maxWatts`) scaling all pixels in the same frame | Full white on a 1000 W rig with a 500 W cap → payload at half linear level; STATE `capped:true` |
| S2 | **Camera auto-locate** (`LED SCAN`): Gray-coded blink frames over the normal LED path, decoded per blob, ray-cast onto the stage mesh → `Points` layouts; `LED SCAN APPLY` as one undo step | In the scan simulator: 2,000 pixels on a stepped facade located with mean error < 1 cm at 8 m, 0 mis-decoded indices at noise σ = 2 % |
| S3 | Redundancy: sACN priority per node; machine role Primary/Backup (Art-Net/DDP backup silent until the primary's hello is lost > 1 s, back after 3 s) | Two in-process engines: kill primary's sender → backup's first frame within 1.5 s; sACN backup always sends at its lower priority |
| S4 | sACN universe discovery announcements (universe 64214, every 10 s) | Codec + loopback test |
| S5 | Ordered temporal dither for 8-bit linear-light fixtures (off by default) | Mean over 16 frames equals the 16-bit value ±0.5 LSB |

### Later (named, not built)

KiNET v1/v2 (Pixera documents KiNET v1 on UDP 6038 [R:§6]; the protocol itself was not verified); MVR-xchange live
(`_mvrxchange._tcp`) [R:§5]; volume (voxel) generators sampled at XYZ; sampling surface-aware *effect* generators
(drawn from geometry, so no texture to sample — those surfaces read black for LEDs this round, with a warning); GDTF
Share sign-in; ArtRdm / RDM discovery; USB DMX widgets; per-LED radiometric calibration by camera; LED processors and
genlock in mixed rooms (M26).

## 3. Architecture

### 3.1 Components and threads

```
 control lane (one writer)        plan builder (pool, off-lane)          render thread (GL)                  led-send thread
 ─────────────────────────        ─────────────────────────────          ──────────────────                  ───────────────
 ProjectStore.Apply ──root──▶ LedService.OnProject: when leds or a      RenderHost.Frame:                   LedSender.Run:
                              referenced surface/mesh/canvas branch       … outputs drawn (RenderEngine)       wait for frame or due
                              changed (reference equality), debounce      LedPass.Draw(plan, inputs)            keep-alive / ArtPoll
                              50 ms → LedPlanBuilder.Build(project,        1 sample pass (points → RGBA16F)      hold until T_emit
                              meshes) → LedPlan (immutable arrays)         2 patch pass (fullscreen → R8)        copy rows → packets
                              → RenderHost.OfferLedPlan(plan)              PBO read + fence                      send (unicast/mcast)
                              → LedSender.Apply(sendProgram)             … presents (swap)                     latch (ArtSync / sync /
                                                                          LedPass.Collect → LedFrame ─────────▶  DDP push)
 verbs: LED … ──────────────▶ AirState.Led (test, level), NodeSettings   (fence not done → collect at the      ArtPollReply / WLED probe
                                                                          start of the next frame, counted)     → discovery table
```

- **Plan builder** (pure Core, `LedPlanBuilder`): layouts → per-pixel sample specs; world pixels bound to the nearest
  surface (`MeshProximity` over the stage meshes, off-lane); patch table; universe rows; conflicts; bandwidth estimate;
  warnings. Published as one immutable `LedPlan` with the project revision it was built from. The newest plan wins;
  an older one finishing late is dropped.
- **Render thread** owns all GL. `LedPass` runs **after every output is drawn and before the presents** (a hook in
  `RenderEngine.Render`), so Output-space fixtures read this frame's final pictures, and its read-back fence is
  checked right after the swap (by then the GPU has finished the frame on every driver that blocks on swap).
- **led-send thread** (one per node, `ThreadPriority.AboveNormal`): the only writer of LED sockets. Waits on an
  `AutoResetEvent` with a timeout to 1 ms before the next due instant, then `SpinWait`s to it (no `Thread.Sleep`;
  `MonoClock` only).
- **Discovery** runs on the shared Art-Net endpoint's receive loop (Art-Net) and a pool task (mDNS + HTTP probe).

### 3.2 Coordinates and layouts

| Space | `spaceId` | Units of `placement` and `layout.size` | Sampled from |
|---|---|---|---|
| `canvas` | canvas id | canvas pixels, x right, y down | the composed canvas (`SourceResolver.Canvas`) |
| `output` | output id | output raster pixels | the output's final picture this frame (`RenderEngine.OutputPicture`) |
| `source` | a source word (`media:md…`, `generator:gn…`, `playlist:pl…`, `color:#…`) | normalised 0–1, y down | `SourceResolver.Resolve(source, raster)` |
| `surface` | surface id | surface UV 0–1 (v down, the picture's rows) | the surface's content, mapped exactly as `StageRenderer.SetContent` (UV region / canvas region / viewpoint projection) |
| `world` | "" | metres, stage frame (right-handed, +Y up) | the nearest surface within `bindDistance` (→ surface UV), else the viewpoint `viewpointId` projecting a source, else black + warning |

`LedLayout` makes local points; `placement` (`Transform3`: 2D spaces use `position.xy`, `rotation.z` in radians,
`scale.xy`) places them; pixel order is address order (`reverse` flips):

- `line`: `count` points from (0,0,0) to (`size.x`,0,0), first and last LED centres on the ends.
- `grid`: `count` × `rows` over `size.x` × `size.y`, starting at `start` (`topLeft|topRight|bottomLeft|bottomRight`),
  rows first unless `columnsFirst`, alternate direction when `snake`.
- `ring`: `count` points on a circle of diameter `size.x`, from angle 0 clockwise (y down).
- `points`: inline `points` (≤ 4096; more goes to a file).
- `file`: a pixel list beside the project (`.pmleds`: little-endian header `PMLD`, u32 count, then `count` × 3 ×
  float32), loaded and cached by `contentHash` like mesh files. MVR/CSV imports above 4096 pixels per fixture write one.

Area footprint default = half the distance to the nearest neighbour in the layout (pitch / 2) in space units,
converted to source UV per group.

### 3.3 GPU pipeline (PlayMap.Render/Led)

**Pass 1 — sample** (`led_sample` program). Target `LedColors`: `Rgba16F`, 1024 × ⌈N/1024⌉. Draw `GL_POINTS`, one
per LED pixel, grouped by source binding (one draw per group; groups = canvases, outputs, sources, surfaces in use).
Per-vertex attributes (one interleaved VBO uploaded once per plan, 8 floats/pixel): `aIndex` (texel index),
`aSpace` (0 uv / 1 world), `aUv` (vec2), `aWorld` (vec3), `aFootprint` (radius in source UV). Vertex shader puts the
point at the exact texel centre (`gl_PointSize = 1`). Fragment shader:

```glsl
// uMode: 0 = through UV with uRegion (x, y, w, h); 1 = project uWorld through uProjector (a viewpoint)
vec2 st = uMode == 0 ? uRegion.xy + vUv * uRegion.zw : projectToUv(uProjector, vWorld, uRegion);
vec3 acc = vec3(0); float n = 0.0;
for (int i = 0; i < uTaps; i++) {              // 1 (Point) or 9 (Area: centre + 8 on a rotated grid at vFootprint)
    vec2 p = st + uTapOffsets[i] * vFootprint;
    vec3 c = texture(uTex, p).rgb;
    acc += pow(max(c, 0.0), vec3(uContentGamma)); n += 1.0;   // decode to linear before averaging
}
fragColor = vec4(acc / n, wattsWeight(acc / n));             // alpha carries the pixel's share of the power model (S1)
```

Out-of-range `st` samples black (clamp-to-border semantics done in the shader). Canvases are already `Rgba16F`
composites; videos are converted by `YuvConversion`; generators are resolved at the group's raster (canvas size,
video size, or 512 × 512 for a generator only LEDs use — the generator host caches per size).

**Pass 2 — patch** (`led_patch` program). Target `LedSlots`: `R8`, 512 × R (one row per (node, universe) in use; a DDP
device's buffer spans ⌈bytes/512⌉ consecutive rows). Fullscreen draw; each fragment is one DMX slot:

```glsl
vec4 e = texelFetch(uPatch, ivec2(gl_FragCoord.xy), 0);      // RGBA32F: pixel, op, fixture, constant (0..65535)
if (e.y < 0.0) { out8 = 0; return; }                          // unused slot
int comp = int(e.y) >> 2, sel = int(e.y) & 3;                  // comp: R G B W WW CW A UV L DIMMER CONST; sel: 8-bit, 16-hi, 16-lo
FixtureParams f = fetchFixture(int(e.z));                      // 3 texels: gains, white LED rgb + output gamma, flags/brightness/whiteMix
vec3 lin = uBlackout ? vec3(0) : uTest != 0 ? testPattern(...) : texelFetch(uColors, idx2d(int(e.x)), 0).rgb;
lin *= f.brightness * uLevel * uPowerScale;                    // uPowerScale from the 1×1 mip of LedColors.a (S1)
vec4 rgbw = f.hasWhite ? extractWhite(lin, f.white, f.whiteMix) : vec4(lin, 0);
float v = component(rgbw, comp, e.w, uBlackout) * gain(f, comp);
v = pow(clamp(v, 0.0, 1.0), 1.0 / f.outputGamma);
uint q = sel == 0 ? uint(floor(v * 255.0 + 0.5)) : (uint(floor(v * 65535.0 + 0.5)) >> (sel == 1 ? 8u : 0u)) & 255u;
fragColor = vec4(float(q) / 255.0);                           // UNORM8 stores the byte exactly
```

- White extraction: `w = min(r/wr, g/wg, b/wb) · whiteMix`, then `r −= w·wr` etc. (`white` is the white LED's colour in
  the fixture's linear RGB, default (1,1,1)).
- `L` = Rec.709 luminance of `lin`. `DIMMER` = constant × (blackout ? 0 : 1). `CONST` = constant always (a strobe or
  mode channel never changes on blackout).
- Defaults keep code values unchanged: `contentGamma = outputGamma = 2.2` → `out = in` (Resolume behaviour). A 16-bit
  linear-PWM fixture sets `outputGamma = 1` and gets true linear light at 16-bit resolution.
- Test patterns are evaluated in address space here (`chase`, `order`, `universe`, `fixture`, `walk`, `white`,
  `full`, `black`) or in position space in pass 1 (`gradient`); the same functions exist in `LedTestPatterns` (Core)
  for the CPU reference.
- **Freeze**: pass 1 is skipped (colours held), pass 2 runs (so blackout and tests still apply).
- **Previs**: `LedPreview` draws instanced discs at the pixels' world (or space) positions in the editor view, coloured
  by `texelFetch(LedColors, index)` re-encoded for display — no read-back, so selection and view changes stay instant.

**Read-back.** `PixelPackRing` gains a read format (`ReadFormat.R8`: `GL_RED`/`GL_UNSIGNED_BYTE`); the LED ring has 3
slots of 512 × R bytes. `Collect` after the presents copies the mapped bytes into a pooled `LedFrame` and hands it to
the sink. If the fence has not passed, the frame is collected at the start of the next frame and
`led.lateCollects` counts it.

GL 3.3 core suffices: `texelFetch`, `RGBA32F` sampling (filter nearest), `R8` render targets, points. `GpuTypes`
gains `TextureFormat.Rgba32F`; `GlDevice` gains `GenerateMipmaps(RenderTarget)` (S1) and the R8 pack read.

### 3.4 Timing: in step with the render loop

For render frame *k*: `L_k` = the leader instant of its light (`FrameInputs.LeaderNs` = leader now + time to the
predicted vblank + display latency); `M_k` = the monotonic instant of that light, recorded alongside
(`monoNow + lead + displayLatency`). The LED frame carries both.

Per node *n* with rate `R_n` (Hz) and latency `D_n` (node + network + driver, ms):

1. **Decimation on the leader grid**: send frame *k* iff `floor(L_k · R_n) ≠ floor(L_{k−1} · R_n)` (leader seconds).
   Deterministic across machines; no node integrates a delta.
2. **Emission instant** `T = M_k − D_n`. If the frame is collected after `T`, it is sent at once and the lateness
   (`now − T`) goes into the node's p95 `lateMs`. A node is `late` when its p95 over 2 s exceeds one render frame.
3. **Burst + latch**: all universes of the frame are sent back to back (pre-built packets, the 512 slots copied in),
   then the latch: one ArtSync per directed-broadcast address (Art-Net nodes with `sync`), one sACN sync packet to the
   sync universe (multicast group or unicast), or the DDP push flag on the device's last packet (or one data-less push
   to `255` broadcast when several DDP devices share a sync group).
4. **Keep-alive**: any universe not sent for 900 ms is re-sent with its last payload (Art-Net rec. 800–1000 ms; sACN
   library default 800 ms) [R:§1–2]. ArtSync continues with it so nodes stay in sync mode (4 s timeout).
5. **Rates**: defaults Art-Net 44 Hz (or the node's ArtPollReply RefreshRate when higher), sACN 44 Hz, DDP 60 Hz;
   always ≤ the render rate. Per-destination-IP Art-Net rate never exceeds what the node advertises.

Budgets (estimates to be measured in `r2/bench`): plan build ≤ 200 ms for 100k pixels (world binding ≤ 1 s on a
200k-triangle facade, 4 cores); GPU ≤ 0.5 ms for 100k pixels × 9 taps + 600 universes on a mid-range discrete GPU
(llvmpipe: measured and reported, target ≤ 10 ms); read-back 300 KB/frame for 600 universes; sender ≤ 1 ms CPU per
frame for 600 universes; **zero allocation** on the render and send paths once warm; emission p95 error ≤ 1 ms on a
quiet machine (±2 ms in tests). Network: 600 Art-Net universes at 44 Hz ≈ 26,400 packets/s × 572 B ≈ 121 Mb/s — the
bandwidth guard (`NodeSettings.LedMaxMbps`, default 200) refuses beyond its limit with that arithmetic in words.

### 3.5 Auto-patch (Core `LedPatcher`, pure, deterministic)

Input: fixtures in scope in rig order (the outliner order, which `MOVE` changes), the target node(s), `from`
(universe.address), `align` (default on), `eachUniverse` (default off), the node's `universeChannels` (default 510:
170 RGB pixels) and its discovered universe range when known. For each fixture: footprint = head + pixels × stride +
tail. Place the head at the cursor; place pixels so that, with `align`, no pixel's channels straddle a universe
boundary; spill to the next universe at address 1; `eachUniverse` starts every fixture on a fresh universe; overflow
past the node's known ports moves to the next node listed or refuses with the reason. Result: one edit
(`patch.*` fields) and a summary (`universes used 0–25, 4320 pixels, 0 conflicts`); a result equal to the current
patch is refused ("already patched so").

### 3.6 Packet layouts (offsets in bytes)

**Art-Net** (UDP 6454; OpCode little-endian; ProtVer 0/14) [R:§1]

| Packet | Layout |
|---|---|
| ArtDmx `0x5000` | 0–7 `"Art-Net\0"` · 8–9 `00 50` · 10 `00` · 11 `0E` · 12 Sequence (1–255 wrapping, never 0 while sending) · 13 Physical (0) · 14 SubUni (Port-Address & 0xFF) · 15 Net (Port-Address >> 8, 7 bits) · 16–17 Length BE (even, 2–512; we send the used slots rounded up to even) · 18… data |
| ArtSync `0x5200` | 0–7 ID · 8–9 `00 52` · 10–11 ProtVer · 12 Aux1 0 · 13 Aux2 0 (14 bytes, directed broadcast only) |
| ArtPoll `0x2000` | 0–7 ID · 8–9 `00 20` · 10–11 ProtVer · 12 Flags (bit 1: send ArtPollReply on change) · 13 DiagPriority · 14–15 TargetPortAddressTop Hi/Lo · 16–17 TargetPortAddressBottom Hi/Lo · 18–19 EstaMan Hi/Lo · 20–21 Oem Hi/Lo — we send the 22-byte form with EstaMan and Oem filled (receivers accept ≥ 14 bytes, so no short-form fallback) [Art-Net 4, `ext/artnet.txt` l.700–735] |
| ArtPollReply `0x2100` | read leniently: IP (10–13), port (14–15), NetSwitch (18), SubSwitch (19), ShortName (26–43), LongName (44–107), NumPorts (172–173), PortTypes (174–177), SwOut (190–193) forming Port-Addresses, MAC (201–206), User Hi/Lo (224–225), **RefreshRate Hi/Lo (226–227, big-endian; 0–44 means 44 Hz)** [Art-Net 4, `ext/artnet.txt` l.1182–1185] — every field length-checked, unknown bytes ignored |

**sACN E1.31** (UDP 5568; multicast 239.255.hi.lo) [R:§2]

| Packet | Layout |
|---|---|
| Data (126 + n) | 0–1 preamble `00 10` · 2–3 postamble `00 00` · 4–15 `"ASC-E1.17\0\0\0"` · 16–17 flags+len (`0x7000 \| (110+n)`) · 18–21 `00 00 00 04` · 22–37 CID (per machine, stable, from NodeId) · 38–39 flags+len (`0x7000 \| (88+n)`) · 40–43 `00 00 00 02` · 44–107 source name (UTF-8, NUL-padded) · 108 priority (0–200) · 109–110 sync universe · 111 sequence · 112 options (0x40 terminated) · 113–114 universe · 115–116 flags+len (`0x7000 \| (11+n)`) · 117 `02` · 118 `A1` · 119–120 `00 00` · 121–122 `00 01` · 123–124 n+1 · 125 start code 0 · 126… data |
| Sync (49) | root vector `00 00 00 08` · framing vector `00 00 00 01` · sequence · sync universe (u16) · 2 reserved |
| Termination | three data packets with option 0x40 per universe on stop |
| Discovery (S4) | universe 64214, every 10 s, our universe list (pages of 512) |

**DDP** (UDP 4048) [R:§3]: 0 flags (`0x40` v1, `|0x01` push on the device's last packet) · 1 sequence (1–15 cycling) ·
2 data type (`0x0B` RGB 8-bit, `0x1B` RGBW 8-bit, `0x0C`/`0x1C` 16-bit) · 3 destination id (1) · 4–7 offset BE (bytes) ·
8–9 length BE (≤ 1440) · 10… data. **WLED's DDP receiver assumes 8 bits per channel** (wled00/e131.cpp `handleDDPPacket`): a node
with `Vendor == "wled"` gets `0x0B`/`0x1B` only, and a 16-bit fixture type patched to it is refused with words. 16-bit types go
only to a DDP device whose status reply advertises them, or by an explicit node setting.

**mDNS** (S/M6): one PTR query for `_wled._tcp.local.` to 224.0.0.251:5353 (QU bit set so answers may come unicast),
answers parsed for PTR→SRV→A/AAAA and TXT `mac=`; then `GET http://<ip>/json/info` → `leds.count`, `leds.lc`,
`udpport`, `ver`, `name`, `arch` [R:§4].

### 3.7 Import

- **CSV** (`LedCsvReader`): header row; comma, semicolon or tab; invariant culture; recognised columns (case-
  insensitive, MADRIX/Capture names): `Fixture`/`Display Name`, `Pixel`/`Index`, `X`/`Position X`, `Y`, `Z`,
  `Universe`, `Channel`/`Address`, `DMX Address` (`U.A` or absolute), `Type`/`Mode`, `Colour order`. Rows with the
  same fixture name become one `points` fixture in row order. Units scaled by `SCALE`. Errors name the line and column.
- **GDTF** (`GdtfReader`): open the zip (`System.IO.Compression`), stream `description.xml` (`XmlReader`), pick the
  DMX mode (by name, or the first with colour channels), walk the geometry tree from the mode's top geometry
  composing `Position` matrices (GDTF metres, Z-up), emit a pixel per `GeometryReference` (or per geometry owning
  `ColorAdd_*`/`ColorRGB_*` channels), map `LogicalChannel Attribute` → `LedChannel` (`ColorAdd_R|ColorRGB_Red` → R …,
  `ColorAdd_W` → W, `ColorAdd_WW`/`CW`, `ColorAdd_A` → A, `ColorAdd_UV` → UV, `Dimmer` → DIMMER, anything else →
  CONST at its `Default`, else `Highlight`, else 0), `Offset="a,b"` → 16-bit, channel offsets + `Break DMXOffset − 1`
  for `DMXBreak="Overwrite"` [R:§5]. Result: one `LedFixtureType` with `origin = "gdtf:<sha256>"`, pixels as
  `points` in fixture-local metres.
- **MVR** (`MvrReader`): open the zip, `GeneralSceneDescription.xml`; for each `Fixture` (recursively in
  `ChildList`s and groups): `Matrix` (4×3, mm, Z-up → metres, Y-up: (x, y, z) ↦ (x, z, −y), as `MeshIO.ZUpToYUp`),
  `GDTFSpec` + `GDTFMode` → the type (each `.gdtf` in the archive read once), `Addresses/Address` (`"U.A"` or
  absolute; MVR universes start at 1 → Art-Net Port-Address U−1 / sACN U, configurable), `Protocols/Protocol type`
  → node protocol hint, `Mappings/Mapping` (`ux uy ox oy rz` in a `MappingDefinition` of `SizeX×SizeY`) → a
  `canvas`-space placement when the operator picks a canvas for that definition. Fixtures without pixel geometry
  (moving heads) are listed in the warnings and skipped. One undoable edit for the whole import.
- Files come from this machine's paths (like `MEDIA IMPORT`); GDTF Share files are never bundled (its terms) [R:§5].

### 3.8 Camera auto-locate (S2)

Code: pixel *i* (global index over the plan) carries `c = gray(i + 1)` (0 is "not a pixel"); frames: all-off,
all-on, then for each bit b of ⌈log2(N + 2)⌉ bits the bit frame and its inverse — 2 + 2·17 = 36 frames for 100k
pixels, every node at once. The frames go out through the normal LED path (`uTest = LOCATE_BIT`, bit and phase as
uniforms), so they carry the same latch and timing. Capture per frame uses the scan runner's `CaptureTiming` and
settle detector, with the LED latency measured first by an all-on flash (`LATENCY MEASURE` extended to LED nodes).
Decode: blobs from (all-on − all-off) above a threshold; per blob per bit, compare frame vs inverse (intensity-weighted
over the blob); code → index; sub-pixel centroid; confidence = min contrast × bit agreement. Placement: a calibrated
camera's ray through the centroid cast into the stage mesh (`MeshRaycaster.Cast`) → world point → the fixture's
`points` (world space) or its surface UV; pixels not seen are interpolated along the strip and flagged `estimated`.
`LED SCAN APPLY` writes the new layouts as one edit and keeps the old ones in the scan record (a history to go back
to). The simulator renders LEDs as Gaussian blobs at their true positions through the sim camera.

### 3.9 Redundancy (S3)

`NodeSettings.LedRole` (`primary|backup`) and `LedPrimaryNode` (the primary's Show Bus id) are machine-local; the
project is the same on both. sACN: both send; each `LedNode.sacnPriority` + `NodeSettings.LedPriorityOffset`
(backup default −50) lets receivers pick (receiver-side 2.5 s source loss) [R:§2]. Art-Net/DDP: the backup is
`standby` (sends nothing) until the primary's hello has been missing > 1 s or its STATE says LED output failed; it
returns to standby after the primary is back for 3 s. Because frames are a pure function of show time, the backup's
first frame is the one the primary would have sent.

## 4. Spine needs

### 4.1 Model (Core, `PlayMap.Core.Model`, new file `LedModel.cs`)

Backwards compatible: `Project` gains an init property, so older files read an empty rig (the `Stage.Devices`
precedent); all new records are camel-case JSON with enums as words.

```csharp
// in Project.cs (one line in the record body; the positional record keeps its shape):
public LedRig Leds { get; init; } = LedRig.Empty;   // JSON "leds"

public enum LedProtocol { ArtNet, Sacn, Ddp }
public enum LedSync { None, Latch }                       // Latch = ArtSync / sACN sync universe / DDP push
public enum LedSpace { Canvas, Output, Source, Surface, World }
public enum LedShape { Line, Grid, Ring, Points, File }
public enum LedStart { TopLeft, TopRight, BottomLeft, BottomRight }
public enum LedSampling { Point, Area }
public enum LedChannel { R, G, B, W, WW, CW, A, UV, L, Dimmer, Constant, Gap }

/// <summary>A network receiver of pixel data: an Art-Net node, an sACN receiver, a DDP/WLED controller.</summary>
public sealed record LedNode(
    string Id, string Name, LedProtocol Protocol,
    string Host,               // address or host name; "" = sACN multicast
    int Port,                  // 0 = protocol default (6454 / 5568 / 4048)
    bool Enabled,
    double RateHz,             // 0 = protocol default (Art-Net: 44 or the node's RefreshRate; sACN 44; DDP 60)
    double LatencyMs,          // node + network + driver, to land with the projection (default 0)
    LedSync Sync,              // default Latch
    int UniverseChannels,      // slots used per universe when auto-patching (default 510)
    string Color) : IEntity
{
    public int SacnPriority { get; init; } = 100;          // 0..200
    public int SacnSyncUniverse { get; init; }              // 0 = none
    public bool SacnMulticast { get; init; } = true;
    public bool ArtNetBroadcast { get; init; }              // legacy; off (Art-Net 4: unicast only)
    public bool FollowBlackout { get; init; } = true;
    public string Vendor { get; init; } = "";               // "wled", "advatek"… from discovery
}

public sealed record LedChannelSlot(LedChannel Channel, int Bits /* 8|16 */, int Value /* 0..65535 for Dimmer/Constant */);

public sealed record LedFixtureType(
    string Id, string Name,
    ImmutableArray<LedChannelSlot> Head,       // before the pixels (a master dimmer, a mode channel)
    ImmutableArray<LedChannelSlot> PerPixel,   // e.g. G, R, B for a WS2812-class strip
    ImmutableArray<LedChannelSlot> Tail,
    double WattsPerPixel,                      // at full white (S1); 0 = unknown
    string Origin,                             // "builtin:grb", "gdtf:<sha256>", "csv"
    string Manufacturer) : IEntity;

public sealed record LedLayout(
    LedShape Shape, int Count, int Rows, Vec3d Size, LedStart Start, bool ColumnsFirst, bool Snake,
    ImmutableArray<Vec3d> Points, string File, string ContentHash);

public sealed record LedPatch(string NodeId, int Universe /* Art-Net Port-Address 0..32767; sACN 1..63999; DDP 0 */,
    int Address /* 1..512, or the 1-based byte in a DDP device buffer */);

public sealed record LedColor(double Brightness, double GainR, double GainG, double GainB, double GainW,
    double ContentGamma, double OutputGamma, Vec3d White, double WhiteMix, bool Dither)
{
    public static readonly LedColor Neutral = new(1, 1, 1, 1, 1, 2.2, 2.2, new Vec3d(1, 1, 1), 1, false);
}

public sealed record LedFixture(
    string Id, string Name, string TypeId,
    LedSpace Space, string SpaceId, Transform3 Placement, LedLayout Layout,
    LedPatch? Patch, LedSampling Sampling, double Footprint /* space units; 0 = pitch / 2 */,
    LedColor Color, bool Enabled, bool Locked, bool Reverse,
    string EditorColor, ImmutableArray<string> Tags) : IEntity
{
    public string? ViewpointId { get; init; }               // world fixtures sampled through a viewpoint
    public double BindDistance { get; init; } = 0.25;       // metres: nearest-surface binding radius
}

public sealed record LedRigSettings(double MaxWatts /* 0 = no cap */)
{
    public static readonly LedRigSettings Default = new(0);
}

public sealed record LedRig(ImmutableArray<LedNode> Nodes, ImmutableArray<LedFixtureType> Types,
    ImmutableArray<LedFixture> Fixtures, LedRigSettings Settings)
{
    public static readonly LedRig Empty = new([], [], [], LedRigSettings.Default);
}
```

`Ids`: `LedNode = "ln"`, `LedFixture = "lf"`, `LedType = "lt"`. `Project.AllIds()` yields them. Built-in types are
not stored: `LedBuiltinTypes` answers `builtin:rgb|grb|brg|rgbw|grbw|rgb16|rgbw16|l|dimmer+rgb` by id, and a fixture
may name one directly as its `typeId`.

**Runtime (not in the document):** `AirState` gains `public LedAir Led { get; init; } = LedAir.Initial;` with
`LedAir(string? Test, ImmutableArray<string> TestScope, double Level /* 0..1 */, string? LocateFixture, long LocateUntilLeaderNs)`
— live actions go to the journal and the recovery record, never to undo (UX §7.2).

### 4.2 Verbs (`VerbCategory.Led`, new; all in `VerbCatalog.All`)

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| `LED LIST` | `LED LIST` | Low | no | `{"output":true,"nodes":[…],"fixtures":12,"pixels":4320,"universes":26,"conflicts":[…],"mbps":38.4}` |
| `LED NEW` | `LED NEW LINE\|GRID\|RING <count>[x<rows>] [ON canvas:<id>\|output:<id>\|surface:<id>\|source:<word>\|world] [TYPE <type>] [<name>]` | Low | yes | the new fixture id; effect `added fixture lf… (170 px, unpatched)` |
| `LED IMPORT` | `LED IMPORT <path> [AS MVR\|GDTF\|CSV] [ON <space>] [SCALE <x>] [NODE <node>]` | Review | yes | `{"fixtures":n,"types":n,"nodes":n,"pixels":n,"warnings":[…]}`; one undo step |
| `LED PATCH` | `LED PATCH <fixture> <node> <universe>[.<address>]` | Review | yes | effect `patched lf… to ln… 3.1–3.48`; refused when it would overlap (says with whom) unless `FORCE` |
| `LED AUTOPATCH` | `LED AUTOPATCH [<fixture>\|#<tag>\|ALL] [NODE <node>…] [FROM <u>[.<a>]] [ALIGN ON\|OFF] [EACH ON\|OFF]` | Review | yes | `{"universes":"0–25","pixels":4320,"fixtures":12,"conflicts":0}`; refused "already patched so" |
| `LED DISCOVER` | `LED DISCOVER [ARTNET\|WLED\|ALL] [<seconds 1–10>]` | Low | yes (sends polls) | `[{"protocol":"artnet","host":"10.0.0.21","name":"PixLite16","universes":[0,1,…],"refreshHz":44,"known":"ln…"\|null}]` |
| `LED OUTPUT` | `LED OUTPUT ON\|OFF [<node>\|ALL] [HOLD]` | High | yes | no node: the machine master (`NodeSettings.LedOutput`); with a node: that node's `enabled`. OFF sends one black frame (latched) then silence, sACN adds 3 terminations; `HOLD` stops without the black frame |
| `LED TEST` | `LED TEST <chase\|order\|universe\|fixture\|gradient\|walk\|white\|full\|black>\|OFF [ON <node>\|<fixture>\|#<tag>\|ALL]` | High | yes | effect; refused while armed (show-safety WP) |
| `LED LOCATE` | `LED LOCATE <fixture> [<seconds 1–60>]` | Review | yes | blinks the fixture at 2 Hz (white ↔ its editor colour) |
| `LED LEVEL` | `LED LEVEL <0–100> [FADE <ms>]` | High | yes | the master level (live, journalled, not undo) |
| `LED MONITOR` | `LED MONITOR <node> <universe>` | Low | no | `{"node":"ln…","universe":3,"leaderMs":…,"slots":"<1024 hex>","fixtures":[{"id":"lf…","from":1,"to":48}]}` — the bytes last *sent* |
| `LED SCAN` (S2) | `LED SCAN <camera> [<fixture>…\|ALL] [MODE FAST\|ROBUST]` | High | yes | accepted → progress in `STATE.led.scan` |
| `LED SCAN APPLY` (S2) | `LED SCAN APPLY <scan>` | Review | yes | `{"moved":n,"estimated":n,"meanErrorMm":…}`; one undo step |

`ADD` learns `lednode`, `fixture`, `fixturetype` (syntax words updated); `GET/SET/REMOVE/MOVE` find them through a
`LocateLed` hook. Generated for free: HELP, VERBS, `/api/verbs`, MCP tools, the context's command list, Companion
actions (companion WP).

### 4.3 STATE and context

New key at the **end** of STATE (readers matching raw substrings stay safe):

```json
"led": {
  "output": true, "level": 1.0, "fixtures": 12, "pixels": 4320, "universes": 26, "mbps": 38.4,
  "test": null, "locate": null,
  "lagMs": 0.4, "lateCollects": 0, "powerW": 312.5, "capped": false, "role": "primary",
  "nodes": [ { "id": "ln…", "name": "PixLite A", "protocol": "artnet", "host": "10.0.0.21",
               "state": "sending", "fps": 44.0, "universes": 8, "lateMs": 0.2, "lastLeaderMs": 1760101234567,
               "seen": { "name": "PixLite16 MkII", "refreshHz": 44, "ageS": 1.2 }, "why": "" } ],
  "conflicts": [ { "node": "ln…", "universe": 3, "from": 1, "to": 48, "fixtures": ["lf…", "lf…"] } ],
  "warnings": [ "lf… samples an effect surface — black for LEDs this round" ],
  "scan": null
}
```

Node `state` words: `off` (machine master off), `disabled`, `idle` (nothing patched), `sending`, `late`,
`unreachable` (ICMP/socket error, ArtPoll silent ≥ 10 s after it had answered, WLED probe failing), `refused` (the
bandwidth guard, with `why`), `standby` (backup). CONTEXT: one item per node — green sending/standby, amber
late/test/locate/capped, red unreachable or refused while the master is on, grey off. Hello caps: add `led` (15 of the
16 kept).

### 4.4 Seams (C#)

```csharp
// PlayMap.Engine/Seams.Led.cs
public sealed record LedNodeSeen(string Protocol, string Host, string Name, string LongName,
    IReadOnlyList<int> Universes, int RefreshHz, string Mac, string Vendor, int Pixels, long SeenMonoNs);
public sealed record LedNodeView(string Id, string Name, string Protocol, string Host, string State,
    double Fps, int Universes, double LateMs, long LastLeaderMs, LedNodeSeen? Seen, string Why);
public sealed record LedOutputView(bool Output, string Role, IReadOnlyList<LedNodeView> Nodes,
    double LagMs, long LateCollects, double PowerW, bool Capped);

/// <summary>The LED network edge as the engine sees it (PlayMap.Net implements it; tests use a fake).</summary>
public interface ILedOutput : IAsyncDisposable
{
    LedOutputView View();
    void Apply(LedSendProgram program);                 // nodes, rows → packets; swapped atomically by the sender
    void SetMaster(bool on, bool blackFirst);
    Task<IReadOnlyList<LedNodeSeen>> DiscoverAsync(string scope, TimeSpan window, CancellationToken ct);
    bool TryMonitor(string nodeId, int universe, Span<byte> slots, out long leaderMs);
}

// PlayMap.Core/Led/LedFrames.cs — render thread → sender, never blocks
public sealed class LedFrame { public long LeaderNs, LightMonoNs, PlanRevision; public int Rows; public byte[] Slots = []; }
public interface ILedFrameSink { LedFrame? Rent(int rows); void Submit(LedFrame frame); }   // a pool of 4; oldest unsent dropped and counted

// PlayMap.Engine/Rendering — capability the render host also offers
public interface ILedRenderer { void Offer(LedPlan? plan, ILedFrameSink? sink); }

// PlayMap.Net/Dmx/ArtNetEndpoint.cs — one socket on <LedInterface>:6454 shared with the control WP's Art-Net input
public sealed class ArtNetEndpoint : IAsyncDisposable
{
    public event Action<ReadOnlyMemory<byte>, IPEndPoint>? Received;   // demuxed by OpCode by its subscribers
    public bool Send(ReadOnlySpan<byte> packet, SocketAddress to);    // allocation-free
    public string? Problem { get; }                                     // "UDP 6454 is in use by another program — sending from an ephemeral port, discovery off"
}

// PlayMap.Net/Discovery/MdnsBrowser.cs
public interface IMdnsBrowser { Task<IReadOnlyList<MdnsService>> BrowseAsync(string service, TimeSpan window, CancellationToken ct); }
public sealed record MdnsService(string Instance, string Host, IPAddress Address, int Port, IReadOnlyDictionary<string, string> Txt);
```

Composition: `EngineParts` gains one partial hook `static partial void AddLedParts(List<EngineExtensionFactory>)`
(the existing `AddExtensions` already has its one implementation in `EngineParts.Ops.cs`). `EngineParts.Led.cs`
implements it with `LedExtension`, which builds `LedService` (plan builder + `ILedOutput` from Net), finds the render
host as `setup.Outputs is ILedRenderer`, and adds `EngineServices.Led` (an init property in `EngineServices.Led.cs`).
No render host (headless without GL) → LED verbs answer "no renderer on this node — LED output needs the GPU".

### 4.5 NodeSettings additions (init properties, defaults keep old files valid)

| Property | Default | Meaning |
|---|---|---|
| `LedOutput` | `false` | the machine master: this machine transmits LED data (fail closed on a fresh node) |
| `LedInterface` | `""` | local address to send from and bind 6454 on ("" = the OS's route) |
| `LedMulticastTtl` | `1` | sACN multicast TTL |
| `LedMaxMbps` | `200` | the bandwidth guard |
| `LedRole` (S3) | `"primary"` | `primary` or `backup` |
| `LedPrimaryNode` (S3) | `""` | the primary's Show Bus id |
| `LedPriorityOffset` (S3) | `0` | added to each sACN priority on this machine (backup: −50) |

## 5. Files

**New**

| Module | File | What |
|---|---|---|
| Core | `Model/LedModel.cs` | records and enums of §4.1 |
| Core | `Led/LedBuiltinTypes.cs`, `Led/LedLayouts.cs` | built-in types; layout → points |
| Core | `Led/LedPlan.cs`, `Led/LedPlanBuilder.cs` | the compiled plan (sample groups, pixel attribs, patch texels, fixture texels, rows, conflicts, warnings, Mb/s) |
| Core | `Led/LedPatcher.cs` | auto-patch |
| Core | `Led/LedReference.cs`, `Led/LedTestPatterns.cs` | the CPU reference of both passes; test pattern functions |
| Core | `Led/LedFrames.cs`, `Led/LedSendProgram.cs` | frame pool contract; per-node packet program |
| Core | `Led/Import/LedCsvReader.cs`, `GdtfReader.cs`, `MvrReader.cs`, `LedImport.cs` | importers and their result |
| Core | `Led/LedLocate.cs` (S2) | blink-code plan and decoder |
| Core | `Dmx/ArtNet.cs`, `Dmx/Sacn.cs`, `Dmx/Ddp.cs` | pure span writers/readers (shared with the control WP) |
| Core | `Geometry/MeshProximity.cs` | closest point on a mesh (BVH), for world binding |
| Render | `Led/LedPass.cs`, `Led/LedPreview.cs`, `Shaders/LedShaders.cs` | the two passes, the previs, GLSL sources registered in `ShaderLibrary` |
| Net | `Dmx/ArtNetEndpoint.cs`, `Led/LedSender.cs`, `Led/LedDiscovery.cs`, `Led/WledProbe.cs`, `Discovery/MdnsBrowser.cs`, `Discovery/MdnsCodec.cs` | sockets, scheduler, discovery |
| Engine | `Led/LedService.cs`, `Led/LedExtension.cs`, `EngineParts.Led.cs`, `EngineServices.Led.cs`, `Seams.Led.cs` | composition and service |
| Engine | `EngineCore.Led.cs`, `EngineCore.Led.Edits.cs`, `EngineCore.Led.State.cs` | verbs, locate/add kinds, STATE writer and context items |
| Engine | `Rendering/RenderHost.Led.cs` | `ILedRenderer` on the render host; frame hook; sink |
| App | `ViewModels/LedViewModel.cs`, `Views/Led/LedTab.axaml(.cs)`, `Views/Led/DmxMonitor.axaml(.cs)`, `Scene/LedTool.cs` | desk |
| Tests | `PlayMap.Core.Tests/Led/*` (model, layouts, patcher, reference, codecs, importers, locate), `PlayMap.Net.Tests/Led/*` (loopback sender, discovery fakes, mDNS codec), `PlayMap.Render.Tests/Led/LedPassTests.cs` (GL goldens), `PlayMap.Engine.Tests/Led/*` (verbs, STATE, air, timing, allocation), `PlayMap.App.Tests/Led/*` | |
| Docs | `docs/LED.md` | the paper |

**Touched (small hunks)**

| File | Hunk |
|---|---|
| `Core/Model/Project.cs` | `Leds` init property; `AllIds` yields LED ids |
| `Core/Model/Ids.cs` | three prefixes |
| `Core/Wire/VerbCatalog.cs` | `VerbCategory.Led`; 13 rows; `ADD` syntax words |
| `Core/Bus/Pairing.cs` | `NodeSettings` init properties |
| `Core/Bus/ShowBus.cs` | cap `led` |
| `Engine/EngineCore.cs` | `AirState.Led` init property |
| `Engine/EngineCore.Router.cs` | one line: `_ when spec.Category == VerbCategory.Led => LedVerb(spec, args, line, session)` (the on-lane LED verbs: LIST, NEW, PATCH, AUTOPATCH, OUTPUT, TEST, LOCATE, LEVEL, MONITOR) |
| `Engine/EngineCore.cs` | one line beside `TryOps`: `if (TryLedAsync(line, session, ct, out var led)) return new ValueTask<CommandReply>(led);` — the verbs that wait on the network or a file (`LED DISCOVER`, `LED IMPORT` parse, `LED SCAN`) run off the lane like the operations verbs; what they change still goes through the lane and the store |
| `Engine/EngineCore.Edits.cs` | `?? LocateLed(p, id)`; three `ListOf` rows; `ADD` kinds delegate to `AddLed` |
| `Engine/StateWriter.cs` | `e.WriteLedState(w);` before the closing brace |
| `Engine/ContextBuilder.cs` | `items.AddRange(LedContext.Items(engine));` |
| `Engine/Host/EngineParts.cs` | `AddLedParts` partial declaration and call |
| `Render/Engine/Contracts.cs` | `FrameInputs.Led` (`LedFrameInputs?`: plan, sink, level, test, blackout-follow) |
| `Render/Engine/RenderEngine.cs` | call `_led.Draw(...)` after the outputs loop, `_led.Collect()` after presents |
| `Render/Gpu/GpuTypes.cs`, `GlDevice.Resources.cs`, `PixelBuffers.cs` | `Rgba32F`; R8 pack read; `GenerateMipmaps` |
| `Render/Projection/StageRenderer.cs` | `EditorCamera.ShowLeds = true`; call `LedPreview.Draw` in the editor pass |
| `docs/WIRE.md`, `ARCHITECTURE.md`, `ROADMAP.md` (M4 done, M5 should), `UX.md` (§4 LED tool), `COMPETITIVE.md` | papers |

## 6. Dependencies

| On | Why | If it is not there |
|---|---|---|
| **control** WP (OSC/MIDI/Art-Net input, timecode) | shares `Core/Dmx` codecs and `ArtNetEndpoint` (one socket on 6454) | this WP creates both; the control WP subscribes to `Received` |
| **present/framelock** WP | per-output display latency (CARRYOVER: "Display latency is one option, not per output"); vblank stamps; a high-resolution waitable timer on Windows | LED uses the global `DisplayLatencyNs` and the render host's predicted vblank; Windows timer precision is a risk (§9) |
| **showsafety** WP | `SHOW ARM` locks LED edits and refuses `LED TEST`; PANIC outcome includes LED black; per-argument risk (`LED OUTPUT OFF` lower than `ON`) | verbs carry their risk; armed checks added when that WP lands |
| **fleet** WP | peer health for the backup role (S3), a shared mDNS browser | S3 waits; `MdnsBrowser` lives here and fleet can reuse it |
| **companion** WP | module actions/feedbacks/presets generated from verbs and STATE (§8) | the verbs and STATE are enough for the generic module |
| **phonescan / scan** | `LED SCAN` reuses `CaptureTiming`, settle detection, camera sources | S2 waits for a camera path; the simulator works today |
| **content/effects/audio** WPs | ISF effects, audio reactivity, particles feed LEDs automatically through sources | nothing to do |

Third-party: **none required.** `System.IO.Compression`, `System.Xml`, `System.Net.Sockets`, `System.Net.Http` are the
runtime's. `Haukcode.ArtNet` 4.0.115 / `Haukcode.sACN` 2.0.112 (MIT) were considered and not taken: our packets are
fixed and allocation-free over pooled buffers; the libraries bring their own threading [R:§7]. WLED is EUPL since
Oct 2024 — nothing is copied from it; only the documented protocols are implemented [R:§4].

## 7. Verification here

| What | How | Real / simulated / compile-only |
|---|---|---|
| Codecs | byte-exact vectors built from §3.6 offsets; round trips; malformed-input refusals | **Real** |
| Sender over UDP | loopback unicast: Art-Net to a fake node on 127.0.0.2:6454 (sender bound to 127.0.0.1:6454), sACN unicast to 127.0.0.3:5568, DDP to 127.0.0.4:4048; test receivers decode, check order (all ArtDmx then ArtSync), sequence, keep-alive, termination ×3, rate decimation, emission time vs `LightMonoNs − latency` | **Real** (unicast). Multicast group membership: the address maths is tested; live multicast on the container's loopback is not relied on |
| Discovery | fake Art-Net node answering ArtPoll with an ArtPollReply; fake WLED (`HttpListener` on loopback serving `/json/info`, DDP receiver); mDNS codec with test-built answers | **Real** for Art-Net and WLED probe; **compile + codec** for live mDNS multicast |
| GPU passes | Xvfb :99 + Mesa llvmpipe: canvases/sources/surfaces/world fixtures sampled; read-back bytes vs `LedReference` (exact for Point, ±1 LSB for Area); freeze/blackout/test/power-cap goldens; 100k-pixel timing reported (not asserted on llvmpipe) | **Real** on llvmpipe; discrete-GPU budget is an estimate |
| Engine | verbs, STATE, context, journal, undo; two in-process engines on one leader clock emit byte-identical frames (deterministic sources); allocation flat on render and send paths after warm-up | **Real** |
| Import | synthetic MVR/GDTF zips and CSVs written by the tests (pixel bars with GeometryReferences and Break offsets; Overwrite channels; 16-bit offsets; Z-up mm matrices) | **Real**; real-world vendor files are manual (GDTF Share needs an account) |
| Camera locate (S2) | scan simulator renders LED blobs through the sim camera with noise and latency; decode and placement asserted | **Simulated**; webcams and phones are hardware |
| Redundancy (S3) | two in-process engines with fake hello loss | **Simulated** |
| Real controllers (PixLite/Falcon/ENTTEC, WLED on an ESP32), ArtSync on hardware, sACN through IGMP switches, jumbo frames, Windows timer precision | not available | **Manual check list** in `docs/LED.md` |
| Desk | view-model tests, verb mapping CI rule; screenshots under Xvfb | **Real** (headless Avalonia) / manual look |

## 8. Desk and Companion surface

**Desk**

- **MAP → LED tool (L)**: drag to draw a strip (line), a grid (rect) or a ring on whatever the view shows — a canvas
  view makes canvas fixtures, a selected surface makes surface-UV fixtures, the 3D view draws *on* the clicked mesh
  (snapping to edges and scan creases) and makes world fixtures bound to that surface. The last-action panel edits
  the pixel count, type and direction live. "Draw a quad and edit while looking at the wall" (MadMapper/HeavyM's
  first five minutes) applies to LEDs: the previs dots and the real pixels change together.
- **Outliner**: an "LED" group — nodes, with their fixtures under them (unpatched fixtures in their own folder); red
  badge for conflicts, amber for late, grey for disabled.
- **Inspector** (fixture): Layout, Patch (node, `universe.address`, a footprint bar across universes), Colour (type,
  gamma pair, white LED, white mix, brightness, gains), Sampling (point/area, footprint, bind distance, viewpoint).
- **Outputs drawer → LED tab**: a tile per node — state light, fps, universes, lateness, what the node said about
  itself (short name, RefreshRate, WLED version and LED count); buttons Test ▾, Locate, Monitor, On/Off; the machine
  master switch with "this machine transmits" wording; the Mb/s estimate against the guard.
- **DMX monitor**: 32 × 16 cells per universe with live values, fixture colour bands, overlaps in red, hover shows
  fixture/pixel/channel; patch view (MADRIX "DMX Map", Resolume "DMX Output") with drag-to-readdress (issues
  `LED PATCH`).
- **Previs**: LED pixels drawn as glowing discs in the 3D editor and canvas views with live colours, so projection
  and LEDs are judged together.
- **Right-click**: on a fixture — Auto-patch from here, Locate, Reverse, Snake, Set node ▸, Test ▸, Bind to surface,
  Sample through viewpoint ▸, Convert to points, Locate pixels with camera…; on a node — Discover, Test ▸, Monitor
  universe ▸, Output on/off, Latency…; on a surface — "Add LED strip along this edge".
- **Import**: drop `.mvr`, `.gdtf` or `.csv` onto the desk → a preview (fixtures, pixels, universes, Mb/s, warnings)
  → Import (one undo step).
- **On-wall-now chip**: "LED: show · test chase · black · off".

**Companion** (generated through the companion WP from the verbs and STATE)

- Actions: LED master on/off; node on/off; `LED LEVEL` on a rotary (`$(this:delta)`, Companion 5.1) and as a fade
  button; test pattern per node (dropdown); locate fixture (dropdown from `LED LIST`); discover.
- Feedbacks: node state colour (green sending/standby, amber late, red unreachable/refused, grey off); test active
  (button flashes, ≤ 3 Hz); power capped; LED level as a surface gauge LED.
- Variables: `led_pixels`, `led_universes`, `led_mbps`, `led_power_w`, `led_lag_ms`, per-node `led_<name>_fps`.
- Live auto-created presets: an "LED" page with one button per node (state feedback; press toggles chase test,
  long-press locate), the master, the level rotary — regenerated when `LED LIST` changes.

**AI**: the assistant reads `STATE.led` and the plan warnings; it may run Low verbs (`LED LIST`, `LED DISCOVER`,
`LED NEW`) and proposes Review/High ones (`LED IMPORT`, `LED AUTOPATCH`, `LED OUTPUT ON`, `LED TEST`) to a person;
a "LED quick start" recipe in `MappingRecipes` chains discover → import → autopatch → test chase.

## 9. Risks and open questions

1. **UDP 6454 sharing.** Art-Net replies come to 6454; the control WP also listens there; another program on the
   machine may hold it. Mitigation: one shared `ArtNetEndpoint` with `ReuseAddress`; degraded mode sends from an
   ephemeral port with discovery off, said in STATE.
2. **Art-Net 4 forbids broadcast ArtDmx** but many cheap controllers never answer ArtPoll. Mitigation: a manual host
   works without discovery; the legacy broadcast option exists, off by default, and says so.
3. **sACN sync is spotty** (ETC's own source library leaves it TODO) [R:§2]. Default `Latch` for sACN uses the sync
   universe only when set; otherwise frames are sent in one tight burst.
4. **Timer precision on Windows** (15.6 ms default) for emission scheduling; spin for the last 1 ms costs CPU.
   Depends on the present WP's high-resolution waitable timer; measured on Windows later.
5. **Read-back on discrete GPUs**: the fence may not have passed right after a swap that does not block (virtual
   displays); the frame is then collected next frame and counted (`lateCollects`).
6. **Generator sizes**: an LED-only generator rendered at 512 × 512 alongside an output's size costs a second
   render. Open: share the output's size when the same generator is on an output.
7. **Effect surfaces** (drawn from geometry) cannot be sampled as textures this round: black + warning.
8. **ArtPollReply offsets** were recalled, not re-read; the parser is lenient and tested against our own fake. Verify
   against a capture from a real node (manual).
9. **GDTF variety**: geometry trees from real manufacturers differ (pixels as `Beam` geometries, nested references).
   The importer reports what it skipped; real files are checked manually.
10. **Bandwidth** of large rigs on 100 Mb/s networks; the guard refuses, and the desk explains the arithmetic.
11. Open: should `LED LEVEL` be per node as well as master? (Companion users will ask; trivial to add as an argument.)
12. Open: MVR universe numbering (MVR starts at 1) vs Art-Net Port-Address (starts at 0) — default maps MVR U to
    Port-Address U−1; confirm with a real lighting-desk export.

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| Model, ids, JSON, edits hooks, built-in types, layouts | 5 |
| Codecs (Art-Net, sACN, DDP), mDNS codec | 5 |
| Plan builder, world binding (`MeshProximity`), conflicts, bandwidth, auto-patch | 8 |
| GL passes, shaders, read-back, previs | 8 |
| Sender (scheduler, decimation, latch, keep-alive, termination), discovery, WLED probe | 8 |
| Engine service, verbs, STATE, context, air, journal | 5 |
| Importers (CSV, GDTF, MVR) | 7 |
| Tests (codec, loopback, GL goldens, import, timing, allocation, two-engine determinism) | 8 |
| Desk (tool, outliner, inspector, LED tab, monitor, previs toggle) | 10 |
| Papers | 2 |
| **Must subtotal** | **66** |
| S1 power cap · S2 camera locate (sim) · S3 redundancy · S4 sACN discovery · S5 dither | 3 · 7 · 4 · 1 · 1 = **16** |
| **Total** | **82** |
