# WP "timecode": timecode chase as a fitted anchor

*Design spec for PlayMap round 2, 13 October 2026, against `main` at `c12b186`. Research:
`r2/research/timecode.md` (tags `[Tn]` refer to its §9). This package covers ROADMAP R3.7 and the LTC-out part of
R4.4.*

---

## 1. Goal and the bar

**Goal.** PlayMap follows LTC, MTC and Art-Net timecode. Every frame on every node is the frame the timecode says,
corrected for the node's own display latency. TC-locked playlists replace a timeline engine. Timecode loss, jumps,
stops and reverse each have a defined outcome, stated in words in STATE. PlayMap can also generate and convert timecode
for the rest of the rig.

**The principle.** Timecode is not a playhead PlayMap is pushed around by. It is a stream of observations
`(this machine's monotonic time, timecode position)`. A robust linear fit turns them into `tc(t) = TC₀ + r·(t − t₀)`.
Mapped through `NetworkClock` to leader time, that line *is* a `TimelineAnchor` (`MediaNs`, `LeaderNs`, `Rate = r`,
`Running`). Everything PlayMap already does with anchors then applies to timecode unchanged: frame selection at the
predicted vblank plus display latency, the exact-frame item switch, sync-group anchors to followers, and audio slaved
to the anchor.

| What the best do | Source | How PlayMap matches or beats it |
|---|---|---|
| WATCHOUT 7: a 1 Hz sync message with a median "time dilation"; pause after ~100 ms of silence; per-timeline offset in ms; six decode error counters; level thresholds (≥ −14 dBFS green, −26…−14 orange); detected rate, measured rate and speed deviation | [T1] | **Beats on timing:** a fit on every LTC frame (four points per MTC frame), with the residual in µs shown, instead of a once-a-second message. **Matches on diagnostics:** the same six counters plus parity, the same level bands, detected/measured rate and speed in ppm. **Beats on loss:** a configurable freewheel and outcome instead of a fixed pause at 100 ms. |
| Pixera 26.3: timeout frames, no-input behaviour, "ensure device time at pause", drift correction none/standard/adaptive, a jump threshold with fade and **preload delay**, hot regions, "pause until TC reaches offset" | [T5] | **Matches all of them except jump fade, which comes later** once fades are drawn (CARRYOVER). Freewheel ms and OnLoss (`hold`/`stop`/`continue`) with `return` (= device time at pause). Correction is the fit itself: a short window for pitch-ridden code, a long one by default. The jump threshold is automatic or set. Preload uses PlayMap's exact-frame switch, so the new position appears on a decoded frame. Regions come from items' TC in-points. Pre-roll holds the first region's first frame, or black, until its TC. |
| disguise: engage/disengage, persistent; per-track TC tags and TC Adjust; "timecode can change track"; on clock lost stop or keep playing; Locked (jam sync) vs Normal (triggered) clips; advice to leave pre-roll | [T2][T3][T4] | **Matches:** `TIMECODE ENGAGE`, persisted through recovery; item `tcIn` labels; region lookup across the playlist; OnLoss `continue` is "the show must go on"; modes `locked` and `triggered`. **Beats:** one item's TC in lays every following item end to end, so a whole set list becomes TC-locked by typing one label. |
| QLab 5: five on-start lookback rules; on stop pause/stop/keep running; freewheel 0–2 s; Capture button; timecode cues generating LTC/MTC, any number at once; LTC paced by the audio device clock | [T8][T9][T10] | **Matches:** `lookback` for triggered items (a running item always starts at its due offset); OnLoss; freewheel 0–10 s; `TIMECODE IN … NOW` (capture); TC outputs fed by a source, a playlist or the clock, LTC paced by the device clock. **Beats:** conversion (LTC in → MTC/Art-Net out) and a rehearsal generator in the same node. |
| Resolume Arena: two SMPTE inputs; per-clip start offset; delay in frames; not available on clips with audio | [T7] | **Beats:** any number of sources and chases; delay in ms, signed, with the sign in words; audio items chase too (the existing `AudioOut` slews ±0.5 % to the anchor). |
| Millumin: freewheel in seconds; a **per-machine delay** tuned by eye with a test card (blinking circle and beep); NTC network timecode | [T11] | **Beats:** followers get the *anchor*, not the timecode, so no per-machine delay is needed. Display latency per output is measured, not tuned by eye. A `pattern.timecode` test card is still provided, for proving it on the wall. |
| Stagewatch 2.7: LTC in at 24/25/30 with the DF bit (cannot tell 29.97 NDF from 30); chase a timer; TC triggers; LTC out of time of day or the countdown. The Show Bus carries only whole-second text | local, §6 | **Interop:** a Stagewatch source kind for display, arming and triggered cues (±1 s, refused for locked chase with the reason); LTC from Stagewatch's audio out is chased frame-accurately; a Show Bus 1.3 `tc` record proposed (§4.9). |
| Nobody documents: a measured residual, backup timecode sources, reverse handling, or behaviour across a redundant pair | §5 of the research | **Beats:** ranked backup sources with a seamless switch when they agree within a frame; reverse detected and held with words; one chase writer per playlist plus node relay of timecode fits; the fit's σ in STATE. |

---

## 2. Scope this round

### Must (acceptance criteria are tests that run in this container unless marked)

| # | What | Acceptance |
|---|---|---|
| M1 | **LTC decoder** (Core, pure C#, our own code) | Synthetic LTC from our encoder at 24, 23.976, 25, 29.97DF, 29.97NDF, 30 and frame-pair 50/59.94/60, at 44.1/48/96 kHz: **100 % of frames decoded** across levels −40…0 dBFS (clean), down to −46 dBFS at SNR ≥ 26 dB, white noise SNR ≥ 20 dB, 50 Hz hum at −20 dB, DC ±0.2 FS, inverted polarity, varispeed 0.90–1.10 (steps and 2 s ramps), clipping at +12 dB gain. **Zero false frames** on 10 min of white noise and 10 min of band-limited noise. Reverse code decoded with `Direction = −1`. Sync-end stamp error ≤ 2 µs RMS clean at 48 kHz and ≤ 20 µs RMS at SNR 20 dB. Counters: injected bit flips raise `biphase`/`badBcd`/`parity` and nothing else. |
| M2 | **Format and rate detection** | Label rate (24/25/30, pairs) and DF from the code. Real rate (x vs x/1.001) settled ≤ 1.5 s after rolling starts. Speed in ppm within ±20 ppm of truth over 5 s. A configured format overrides detection, and a mismatch is reported in words ("set 25, code is 30 DF"). DF labels that do not exist (`00:01:00;00`) are refused by `Timecode.TryParse`. |
| M3 | **Fit** (`TimecodeFit`) and **sample clock** (`SampleClockFit`) | Observation jitter σ = 5 µs (LTC), 200 µs (Art-Net), 1 ms (MTC): predicted TC error at "now" ≤ 5 / 100 / 300 µs RMS after 1 s. Rate within 50 ppm. A 1.00 → 1.05 ramp over 2 s followed with error ≤ ½ frame. Capture callbacks with 0–10 ms one-sided jitter: sample→mono mapping error ≤ 200 µs after 5 s. Midnight wrap (23:59:59:24 → 00:00:00:00) gives no jump. |
| M4 | **Chase logic** (pure, Core) and **engine chase** | Over random TC runs (seeded `Pcg32`), the frame `FrameSelector.FrameAt` picks at every simulated vblank equals the reference `floor(((tc(L − delay) − S_i)·rate_i + In_i)/period)` for ≥ 99.9 % of vblanks; the rest are off by at most one frame and only within 2 frames of a jump. Lock ≤ 3 frames after the first valid LTC frame (≤ 2 complete MTC sequences). Jump re-anchored ≤ 2 frames after the discontinuity. Dropout: freewheel then OnLoss at `freewheelMs` ± 10 ms. DF regions exact at minute boundaries (an item at `00:10:00;00` and one at `00:11:00;02`). Gap `black` shows nothing before a region and the first frame exactly at its TC in. |
| M5 | **Art-Net TC in** (Net) and **MTC parser** (Core) | Real UDP on loopback, port 6454 (or a test port): 19-byte ArtTimeCode parsed, `StreamId` filtered, all four types; malformed packets counted, not thrown. MTC byte streams: quarter frames forward and reverse, full-frame locate, rate codes, user-bits SysEx, and running status interleaved with other MIDI. |
| M6 | **Verbs, STATE, context** | `TIMECODE STATUS/DEVICES/SOURCE/CHASE/ENGAGE/SET/IN/INTERNAL/FIT` behave as in §4.3, with refusals by reason. Manual transport verbs on an engaged, locked playlist are refused, naming the way out. `STATE.timecode`, `STATE.playlists[].chase` and the context items `timecode:*` and `chase:*` are asserted field by field. Journal rows on every state change of a chase (locked, freewheel, held, jump). |
| M7 | **Multi-node** | Two in-process engines (leader + follower on a shared `ManualClock` with a fixed offset). The leader chases a synthetic LTC; the follower adopts via `PLAYLIST ANCHOR`; both give bit-identical anchors and equal `FrameAt` for 10 000 random leader instants. A source on the follower (`nodeId` = follower) relayed with `TIMECODE FIT` drives the leader's chase. A follower refuses `TIMECODE ENGAGE` with the leader's name. |
| M8 | **Internal timecode** (rehearsal and master) | `TIMECODE INTERNAL <src> PLAY / STOP / LOCATE 01:00:00:00 / RATE 1.02`, chased exactly like external code (no fit needed: its line is exact). |
| M9 | **Audio input seam and LTC file source** | `IAudioInputs` implemented on OpenAL Soft capture (compile here, refuses with words when no device, which is this container) and on FFmpeg (`file:` WAV/BWF looped and `alsa`/`pulse`/`dshow` devices). An LTC WAV written by our encoder, read by the FFmpeg path, drives a chase end to end (real FFmpeg 9). |

### Should

| # | What | Acceptance |
|---|---|---|
| S1 | **TC outputs**: LTC (OpenAL device; ALC loopback in tests), Art-Net TC (UDP), MTC (encoder plus the MIDI seam) | LTC out rendered through `ALC_SOFT_loopback` decodes with our decoder at every format: frame-exact, rise time 40 ± 10 µs measured, level within 0.5 dB. Art-Net TC out received by our own receiver on loopback at one packet per frame ±1 ms. MTC encoder schedule: 4 quarter frames per frame, full frame on a locate. Sources: `source:` (conversion), `playlist:` (item TC in + position), `clock` (time of day, BGF1 set), `internal:`. |
| S2 | **TC cues**: a wire line fired at a TC | Fires once on a forward crossing; never on a jump or backwards; lookback on start; runs through the router as session `timecode`; refusals journalled. |
| S3 | **Triggered mode** with lookback | An item fires `GO … AT` the leader instant its TC in arrives (scheduled up to 250 ms ahead, frame-exact); it then free-runs; on TC start, an item whose run contains the TC starts at its due offset. |
| S4 | **Backup sources** | Primary lost and backup agreeing within ½ frame: a switch with no jump, "switched to LTC B (agrees within 0.2 frames)". Disagreeing: handled as a jump, with words. |
| S5 | **Jump preload** | With `preloadMs` > 0, a jump is applied through the next-item schedule; the old content plays until the switch, and the new position's first frame is decoded first (`IsReadyAt` asserted in a headless render test). |
| S6 | `pattern.timecode` **test card** | Big TC label, a frame counter, a bar stepping one cell per frame, a full-field flash on each TC second, and the chase state colour. Rendered on llvmpipe; the frame index read back from pixels equals `FrameAt`. |
| S7 | **Desk panel, right-click entries, Companion** surface (with the companion package) | View-model tests; Companion variables, feedbacks and actions present in the generated module. |
| S8 | `SUBSCRIBE TIMECODE` | Compact push of source and chase state at ≤ 10 Hz (or every frame with `FRAMES`). |
| S9 | `TIMECODE CALIBRATE` | LTC out → LTC in through the OpenAL loopback; the measured round trip minus the output's reported latency gives the input latency, stored in the source's `latencyNs`. |

### Later

Reverse scrub for intra-only codecs (HAP, image sequences); jump and start fades (need drawn transitions); per-item
keyframed `SET` tracks (ROADMAP R3.7; they belong with the content package's parameter animation, and this package
supplies item time); WASAPI/ASIO multichannel capture (R4.4 audio seam); VITC/ATC from SDI (DeckLink) and NDI
timecode; ST 12-3 high frame rates; SMPTE 309M date in user bits; a Show Bus 1.3 `tc` record from Stagewatch; MIDI
Show Control.

---

## 3. Architecture

### 3.1 Components and threads

```
 audio in ─► IAudioCapture (capture thread) ─► LtcDecoder ─┐
 MIDI in  ─► IMidiIn (MIDI thread) ─► MtcParser ───────────┤   per source, on its own thread:
 UDP 6454 ─► ArtNetSocket (recv loop) ─► ArtTimeCode ──────┼─► TimecodeTracker ─► TimecodeFit ─► SourceReading (volatile, latest wins)
 peer     ─► TIMECODE FIT line (wire) ─────────────────────┤
 internal ─► InternalTimecode (exact line, no fit) ────────┘
                                                              │
 control lane, every 10 ms tick: ChaseRunner.Step(project, readings, playback, leaderNow)
      └─► ChaseLogic.Decide (pure) per chased playlist ─► PlaybackLanes.Chase(...) ─► PlaybackSnapshot
             │                                                │                    ├─► render: FrameSelector at vblank+latency
             │                                                │                    ├─► AudioOut slaved to the anchor
             └─► TimecodeCues / triggered GO (AT leader ns)   └─► AnchorPublished ─► followers (PLAYLIST ANCHOR)
 TC outputs (own threads): LtcAudioOut (OpenAL feeder) · MtcOut (MIDI, mono-clock paced) · ArtNetTcOut (UDP)
```

- **One writer:** only the control lane writes the playlists' transports (`PlaybackLanes`). The input threads only
  publish immutable `SourceReading` values. The chase runs where the playlist's transport is written: on a solo node,
  or on the sync group's leader. Followers adopt anchors.
- **No new timers:** the chase runs inside the existing 10 ms lane tick (`EngineCore.Tick`). Anchors are expressed in
  leader time at the instant of the observation, so the tick's latency does not move the picture.

### 3.2 Timecode positions ("TC ns")

- `TcFormat(FrameRate Rate, bool DropFrame)`: `Rate` is the *real* rate (24000/1001, 25, 30000/1001…). The label rate
  is `Rate.Nominal` (24/25/30/50/60). Above 30, LTC carries frame pairs (§2.1 of the research).
- **A TC position is a frame index at the real rate, unwrapped across midnight**, plus a sub-frame phase. TC ns =
  `Rate.TimeOf(frame) + phase·period`. Labels convert through DF-aware counting (`Timecode.TryParse/Format`, tightened
  to refuse non-existent DF labels). So in a 29.97 DF show, `01:00:00;00` is frame 107 892 = 3599.9964 s, and content
  positions use real elapsed time. This removes WATCHOUT's "29.97 timeline time is not TC time" confusion [T1b]. The
  desk shows both the label and the real time.
- **Midnight:** the tracker keeps a day counter. A forward step from the last 2 s of a day to the first 2 s of the next
  is +1 frame; anything else crossing midnight is a jump.

### 3.3 LTC decoder (Core, pure; our own code, informed by [T16] and Stagewatch, nothing copied)

Per sample (float, one channel, any rate):
1. **DC block:** a one-pole high-pass at 10 Hz.
2. **Envelope:** peak follower, instant attack, 50 ms release. *No signal* below `floorDbfs` (default −46). The level
   is reported per frame in dBFS with WATCHOUT's bands (≥ −14 green, −26…−14 amber, < −26 red) [T1].
3. **Schmitt trigger** at ±25 % of the envelope. Between flips, track the most recent **zero crossing**, linearly
   interpolated between the two samples that straddle zero. A flip's edge time is that crossing: hysteresis gives
   noise immunity, the crossing gives sub-sample precision.
4. **Intervals** `Δ` between edges, in fractional samples. Acquisition: collect 32 intervals and split them into two
   clusters with a ratio near 2 (2-means). The bit period `P₀` is the long cluster's mean. Accept only if it lies in
   [fs/(2400·1.15), fs/(1920·0.85)] (24–30 fps label rate ±15 %; frame-pair rates run at the same bit rates).
5. **Classify:** `Δ > 0.75P` long (a 0); `0.3P < Δ ≤ 0.75P` short (half of a 1); `Δ ≤ 0.3P` glitch (count `biphase`,
   merge into the next interval); `Δ > 2.5P` dropout (count, reset the parser, keep `P`). Two shorts make a 1. A long
   after a lone short is a `biphase` error: drop the half and push 0.
6. **Track speed:** on a long, `P += 0.1(Δ − P)`; on a short, `P += 0.1(2Δ − P)`. Keep a ring of each bit's end time
   (the last 96).
7. **Sync:** a 128-bit shift register (`UInt128`) of bits in arrival order. **Forward:** the last 16 bits are
   `0011111111111101` and ≥ 80 bits have arrived since the last sync. The codeword is the last 80; the event time is
   the end of bit 79, and its TC is the start of frame `label + 1` (`2·pair + 2` for frame-pair rates). **Reverse:**
   the last 16 bits are `1011111111111100` and the 80 bits before them begin with the same reversed sync. The codeword
   is those 64 data bits, reversed. The event time is the end of the last data bit (its bit 0), and its TC is the start
   of frame `label`.
8. **Validate:** BCD ranges (frames ≤ 29, seconds/minutes ≤ 59, hours ≤ 23; else `badBcd`, dropped); a sync with
   fewer than 80 bits since the previous one counts `invalid`. Parity (an even number of zeros) is counted as `parity`
   but the word is accepted, because some generators skip the bit (*inference*).
9. **Out:** `LtcFrameEvent(LtcCodeword Word, double EndSample, double SamplesPerBit, int Direction, float PeakDbfs)`.

The cost is about 15 floating-point operations a sample, about 1 ms of CPU per second of 48 kHz audio *(estimate)*.

### 3.4 LTC encoder (Core, pure)

`LtcEncoder(TcFormat, int sampleRate, double levelDbfs = −12, double riseUs = 40)`, with
`Render(Span<float> dst, ref LtcPhase phase, Func<long,…> nextWord)`:
- The bit phase accumulates as a double. The bit rate is `80·realFps` (`40·realFps` for frame pairs), so 20.02 samples
  a bit at 29.97 never rounds per bit.
- Transitions at every cell start, and at mid-cell for a 1, are placed at their **fractional** sample time. The output
  is the analytic one-pole step response from that instant, `y = A + (y₀ − A)·e^(−(1−frac)/τ)`, with
  τ = rise/2.197 (10–90 %). Edges are therefore timed to well under a sample.
- Codeword: BCD, DF and colour flags, BGF0/1/2 at the rate's positions, user bits, polarity bit set for even zeros
  (bit 27, or bit 59 at 25-frame) [T13][T16].
- **Pacing (S1):** the feeder asks for the samples whose first leaves the speaker at `monoNs`. Under 1 frame of error
  it slews by ≤ 0.1 % (inserting or skipping a fraction of a bit's phase; biphase stays valid). Over 2 frames it starts
  a fresh word at the target frame boundary (receivers see a discontinuity, as from a desk that jumps).

### 3.5 MTC and Art-Net (Core codec; Net socket)

- **`MtcParser`** (bytes + mono stamp per message): it keeps the last 8 pieces with their arrival times. Direction
  comes from the piece order (k→k+1 forward, k→k−1 reverse). On a complete sequence it decodes `C` (binary, `rr` rate)
  and emits **eight observations**, piece k at `C + k/4` frames, at its own arrival time [T14] *(inference: the coded
  instant is piece 0)*. Full frame `F0 7F <dev> 01 01 hh mm ss ff F7` for any device id `<dev>` 00–7F (optionally filtered by a
  configured id; 7F, all-call, is always accepted) → a `Locate` observation (parked). User bits `F0 7F <dev> 01 02 …` → user
  bits, by the same rule (MIDI 1.0 Detailed Spec, MIDI Time Code, Full Message). `MtcTests` cover dev 0x00, 0x10 and 0x7F. Running status and interleaved channel messages are skipped.
- **`MtcEncoder`** (S1): due times for quarter frames from the source line; a full frame on start, locate or jump.
- **`ArtTimeCode`** codec: 19 bytes; `ID`, OpCode `0x9700` little-endian, `ProtVer` 14, `StreamId`, `F/S/M/H`, `Type`
  0–3 [T12]. Parse and build; anything malformed is counted and dropped.
- **`ArtNetSocket`** (Net): one UDP socket on 0x1936 (`SO_REUSEADDR`, broadcast receive, interface from
  `NodeSettings.ArtNetInterface`), with handlers registered per OpCode. It is shared with the control package's Art-Net
  DMX input, and whichever package lands first creates it. An observation is stamped at datagram receive (monotonic).
  `Type` gives the label rate and DF; the real rate is measured.

### 3.6 Per-source tracking and the fit

- **`SampleClockFit`** (LTC only): each capture read gives `(lastSampleIndex, readMonoNs)`. The fit
  `t(n) = t₀ + n/fs_eff` uses least squares for the slope over 10 s, and the intercept from the **lower envelope** (the
  minimum residual), because scheduling only ever delays a read. It reports the audio clock's drift against the
  monotonic clock in ppm. An LTC event's mono time is `t(EndSample) − source.LatencyNs`.
- **`TimecodeTracker`** turns observations into a `SourceReading`:
  - State: `none` (never seen) · `unavailable` (the input could not open: words) · `silent` (open, below the floor) ·
    `acquiring` · `rolling` (forward +1 steps, or +2 for MTC pairs) · `parked` (the same frame ≥ 3 times, or a
    locate) · `reverse` · `dropout` (no observation for 2.5 frame periods) · `lost` (dropout longer than 1 s).
  - Counters: `biphase`, `missing` (a forward step of 2–3 frames), `duplicate`, `discontinuity` (a jump), `invalid`,
    `badBcd`, `parity`. These are WATCHOUT's six plus parity [T1], cumulative per session, and `TIMECODE SOURCE RESET`
    clears them.
  - Format: configured, or detected (label rate + DF + real rate by measured period, settled at 1.5 s); a mismatch is
    reported in words.
- **`TimecodeFit`** (robust weighted least squares over one run, a run being the frames since the last discontinuity):
  - Window: `fitWindowMs`, default 1500 (`pitch` mode 400, for DJ varispeed, like Pixera's "Adaptive" [T5]).
  - Outliers: drop residuals above `max(4·1.4826·MAD, 0.3 frame)`, then refit.
  - Slope bounds 0.85–1.15. Outside them the state is "varispeed out of range" and nothing locks.
  - Out: `TcLine(long MonoNs, long TcNs, double Rate, double SigmaNs, int Points, long Run, int Direction, bool Running)`.
  - Cost: O(window) per observation, under 20 µs.
- **Leader domain:** `ChaseRunner` maps a `TcLine` to leader time with `L₀ = clock.ToLeader(MonoNs)` and
  `r_L = Rate / (1 + clockDrift)` (the `NetworkClock` estimator's drift). The uncertainty in STATE is the fit's σ plus
  `clock.UncertaintyNs`.
- **Internal sources** produce an exact `TcLine` (no fit). **Node relays** produce `TcLine`s already in leader time
  (§3.9).

### 3.7 TC-locked playlists: regions

`TimecodeRegions.Build(Playlist, TcFormat, mediaDurationNs) → TcRegion[]` (cached per playlist revision and format):

- An item with `tcIn` starts a region at that label. An item without one follows the previous region's end, so typing
  one TC in on item 1 lays the whole playlist out as a timeline. An item that cannot be placed (the previous item has
  no length) is reported: "item 4 has no TC in and the item before it has no known length".
- Length: `(out − in)/item.rate`, from the item's out point, its duration, or the media probe, exactly as `CueItem`
  does it.
- End actions inside a locked playlist: `loop` repeats until the next region starts; `hold` holds the last frame until
  the next region; `next`/`stop`/`goto` end the region at the item's end, leaving a gap. Goto and the list's own loop
  do not apply: timecode decides. The desk shows this as a glyph.
- Overlap: the region with the latest start at or before the TC wins (a later TC in cuts the previous item short).
  Lookup is a binary search.
- Content at TC `c`, in region i: `media = In_i + (c − S_i)·rate_i`.

### 3.8 Chase logic (Core, pure: `ChaseLogic.Decide`)

Inputs: the binding, the regions, the runtime state, the source line (leader domain, delay applied:
`tc_shown(L) = tc(L − delayNs)`), the current lane, `nowLeaderNs`. Output: the next runtime state, an action and the
words.

| State | Enters when | What the lane does |
|---|---|---|
| `disengaged` | `TIMECODE ENGAGE … OFF` | Keeps doing what it did. A running clip runs on at the item's own rate (no longer r); a held clip stays held. Manual verbs work. |
| `waiting` | Engaged, no code | Nothing changes. Before the first region with gap `hold`, the first region's first frame is cued, so it is decoded and ready. |
| `acquiring` | Code arriving, fewer than K points (LTC 3, Art-Net 3, MTC 2 sequences) | Nothing yet. |
| `locked` | A valid line with `Running` | **Anchor** = `TimelineAnchor(In_i + (tc(L₀ − delay) − S_i)·rate_i, L₀, r_L·rate_i, true)`. The reference instant is the observation instant L₀, which is always in the past, because `TimelineAnchor.MediaAt` holds before `LeaderNs`. So `MediaAt(L) = In_i + (tc(L − delay) − S_i)·rate_i` for every L ≥ L₀. The next region is pre-scheduled with `SwitchAtLeaderNs = L₀ + delay + (S_{i+1} − TC₀)/r_L` through the existing exact-frame switch. |
| `parked` | Repeating frame or MTC locate | Paused anchor at that TC's position (a locate). |
| `outside` | Rolling TC in no region | Gap `black`: the lane carries the next region, anchored, with `VisibleFromLeaderNs` set to its start (nothing shows before it, and its first frame shows exactly at its TC in). After the last region: `VisibleUntilLeaderNs` at its end. Gap `hold`: the nearest region's edge frame. |
| `freewheel` | The source enters `dropout` | The last anchor keeps running. `freewheelLeftMs` counts down. |
| `held` / `stopped` / `free` | Freewheel expires; OnLoss = `hold` / `stop` / `continue` | `hold`: pause at the predicted position, or at the last received TC when `returnOnLoss` is set (Pixera's "ensure device time" [T5]). `stop`: the lane goes idle (black). `continue`: runs on at the last rate (disguise's "show must go on" [T3]). When code returns within the jump threshold of the prediction, it re-locks seamlessly; otherwise it is a jump. |
| `reverse` | Direction −1 | Hold at the current position (the decoders do not play backwards). Located to the reverse code at ≤ 4 Hz when `reverse = locate`. Words: "timecode running backwards — holding". |
| (event) jump | The new line differs from the prediction by more than `jumpNs` (auto: max(2 frames, 5σ)), confirmed by a second observation | Re-anchor. With `preloadNs` > 0, the jump is scheduled as a switch at `now + preload`, so the old content plays until the new position is decoded (Pixera's preload [T5]). |

**Correction without visible steps.** In `locked`, the lane's anchor is replaced only when the content at `now` would
be ≥ ¼ frame off, or the rate differs by > 50 ppm, or a state or region changes. At most 4 re-anchors a second per
playlist. Typical drift (20 ppm) gives one re-anchor every few minutes; a varispeed source is followed by the rate, not
by steps. Each replacement bumps `Seq`, and followers take the new anchor. The decoder seeks only when the position
leaves its decoded window (`FrameQueue.Prepare`), so a sub-frame re-anchor costs nothing.

**Triggered mode (S3):** each item with `tcIn` gets `Go(list, i, atLeaderNs = leader instant the line reaches S_i +
delay)`, scheduled once the instant is ≤ 250 ms ahead and rescheduled if the line moves by more than ¼ frame before it
fires. After firing, the item runs free on the leader clock at its own rate. On TC start, an item whose run contains
the TC starts at its due offset, and items whose trigger lies within `lookbackNs` before the TC also start (QLab
[T8]). OnLoss applies to the playlist (pause, stop or continue).

**Manual verbs.** On an engaged, locked playlist, `VIDEO *`, `PLAYLIST GO/CUE/NEXT/PREV` are refused: "Main chases
FOH LTC (locked) — TIMECODE ENGAGE main OFF to drive it by hand". `PLAYLIST ANCHOR` from the sync leader is always
taken on a follower. `BLACKOUT`, `FREEZE` and `PATTERN` act on the air, above any chase. In triggered mode, manual
verbs work (as in QLab).

### 3.9 Multi-node behaviour

- **One chase writer per playlist:** the node that writes its transport. A solo node chases its own playlists, and
  so does every node for a playlist outside any sync group. In a sync group, the leader chases and followers adopt
  anchors (`PLAYLIST ANCHOR`), so followers cannot disagree with the leader, and no per-machine delay exists (unlike
  Millumin [T11]). Each node still applies its own per-output display
  latency at present time (`FrameSelector.PresentLeaderNs`).
- **Sources live where the cable is:** `TimecodeSource.NodeId` (`""` = the chase writer). A node that hosts a source
  it does not chase relays its line to the writer as `TIMECODE FIT <source> <json>` (≤ 10 Hz, and at once on a state
  change). The line is already in **leader ns**, valid on both nodes because both follow the same leader clock. JSON:
  `{"seq":…, "state":"rolling", "leaderNs":…, "tcNs":…, "rate":1.00002, "sigmaNs":4100, "run":3, "dir":1,
  "format":"25", "text":"01:02:03:04", "levelDb":-12.1}`. The writer uses it like a local reading. If the leader
  changes (fleet failover), relays retarget the new leader.
- **Backup sources (S4):** `chase.sources` is in priority order. The writer uses the first source that is `rolling` or
  `parked`. A switch whose prediction agrees within ½ frame is seamless; otherwise it is a jump with words. A typical
  setup is two LTC distribution feeds into two nodes.
- **Clock re-base (CARRYOVER):** when the node starts following, `NetworkClock` steps. The chase keeps its fits in
  local monotonic time and recomputes leader-domain anchors whenever `NetworkClock.Epoch` changes (a new counter,
  bumped on `Follow`/`Lead`). Chased playlists therefore move with the clock instead of jumping.
- **Anchor push (CARRYOVER):** followers need the leader's anchors pushed (`AnchorPublished` has no transport yet).
  The fleet package owns this. If it has not landed, this package adds the minimal path: the leader sends
  `PLAYLIST ANCHOR <pl> <json>` on its existing wire link to each paired follower in the group.
- **Restart:** a chased playlist needs no restored position. The recovery record keeps only the engaged flags, and the
  chase re-acquires from live code within 3 frames.
- **Stagewatch:** source kind `stagewatch` reads its snapshot's `ltc`, `ltcRunning`, and the frame's arrival time
  (whole-second updates, §6). Allowed for display, TC cues and triggered items, with the stated ±1 s. **Refused for
  locked chase:** "Stagewatch sends its timecode once a second — too coarse to lock a picture; patch its LTC out to an
  audio input here". Show Bus 1.3 proposal (SHOWBUS-NODE §5, item 7): a `tc` record
  `{frames, rate, df, atUnixMs, rolling, seq}` at 1 Hz and on change, fitted like Art-Net.

### 3.10 TC outputs and cues (Should)

- **`TimecodeOutput`** sources:
  - `source:<id>`: re-generates a source, which converts LTC ↔ MTC ↔ Art-Net.
  - `playlist:<id>`: the playlist's TC position, `S_i + (media − In_i)/rate_i`, or `tcStart` + elapsed for a playlist
    with no TC in.
  - `clock`: time of day from `ShowWallClock`, with BGF1 set.
  - `internal:<id>`.
- **Pacing:** LTC follows the audio device clock (as in QLab [T10]), with ≤ 0.1 % slew toward the show. MTC and
  Art-Net are paced from the monotonic clock: a dedicated thread waits for due times (`PeriodicTimer` at 1 ms with
  spin on the last 200 µs; jitter budget ≤ 1 ms). Outputs start off on project open (`autoStart` false by default) and
  resume after a crash from the recovery record.
- **`TimecodeCue`** (S2): `{tc, line, playlistId?, sourceId?}`. It fires once when rolling code crosses the TC forward
  (a forward step under 2 s, not a jump: Stagewatch's `Crossed` rule). It is decided on the lane tick with a 10 ms
  look-ahead. A verb whose syntax takes `AT` gets `AT <unix ms of the crossing>` appended, so it lands on time. It runs
  through the router as session transport `timecode` (internal, paired), and its receipt is journalled.

### 3.11 Performance budgets

| Item | Budget |
|---|---|
| LTC decode, one 48 kHz channel | ≤ 0.2 % of one core |
| Fit per observation | ≤ 20 µs |
| `ChaseRunner.Step` per chased playlist per 10 ms tick | ≤ 50 µs, no allocation in the steady state |
| Re-anchors | ≤ 4/s per playlist (typically < 0.01/s) |
| STATE | Timecode text is computed at write time from the line. Only state changes bump the facts revision, never frames, so STATE pushes do not rise to 25/s |
| `SUBSCRIBE TIMECODE` | ≤ 10 Hz (`FRAMES` = one line per frame per source) |
| Lock latency | ≤ 3 frames after the first valid frame |
| Jump response | ≤ 2 frames (+ `preloadMs`) |
| Picture error when locked | ≤ ½ frame by design; measured σ typically ≪ 1 ms (LTC) |

---

## 4. Spine needs

### 4.1 Model (`src/PlayMap.Core/Model/TimecodeModel.cs`, new; JSON camelCase, enums camelCase strings)

```csharp
public enum TimecodeKind { Ltc, Mtc, ArtNet, Internal, Node, Stagewatch }       // "ltc","mtc","artNet","internal","node","stagewatch"
public enum ChaseMode { Locked, Triggered }
public enum ChaseLoss { Hold, Stop, Continue }
public enum ChaseGap { Black, Hold }
public enum ChaseReverse { Hold, Locate }
public enum TimecodeOutKind { Ltc, Mtc, ArtNet }

/// <summary>A timecode input: where code arrives and how it is read.</summary>
public sealed record TimecodeSource(
    string Id,                 // "tc…" (Ids.TimecodeSource)
    string Name,               // "FOH LTC"
    TimecodeKind Kind,
    string Device = "",        // ltc: audio input key ("" = default; "file:/path.wav" loops a file); mtc: MIDI port key;
                               // artNet: "" (StreamId in Channel); node: "<nodeId>/<sourceId>"; internal/stagewatch: ""
    int Channel = 1,           // ltc: 1-based input channel; artNet: StreamId 0–255; else 0
    string Format = "auto",    // "auto" | "23.976" | "24" | "25" | "29.97df" | "29.97" | "30" | "50" | "59.94df" | "59.94" | "60"
    string NodeId = "",        // which node opens it ("" = the chase writer)
    long LatencyNs = 0,        // input path latency: code arrives this late (set by TIMECODE CALIBRATE)
    double FloorDbfs = -46,    // ltc: below this is silence
    int FitWindowMs = 1500) : IEntity;  // 400 = "pitch" (DJ varispeed)

/// <summary>How a playlist follows timecode (null on a playlist = it does not).</summary>
public sealed record TimecodeChase(
    ImmutableArray<string> Sources,        // priority order: primary, backups
    ChaseMode Mode = ChaseMode.Locked,
    long DelayNs = 0,                      // + = the picture is shown later than its timecode
    ChaseLoss OnLoss = ChaseLoss.Hold,
    long FreewheelNs = 500_000_000,        // 0–10 s; how long a dropout is ridden through on the last line
    bool ReturnOnLoss = false,             // hold at the last received TC rather than where freewheel got to
    long JumpNs = 0,                       // 0 = auto: max(2 frames, 5σ)
    long PreloadNs = 0,                    // jumps land after this, on a decoded frame (0 = at once)
    long LookbackNs = 0,                   // triggered: on TC start, also start items triggered this far back
    ChaseGap Gap = ChaseGap.Black,
    ChaseReverse Reverse = ChaseReverse.Hold);

public sealed record TimecodeOutput(
    string Id, string Name, TimecodeOutKind Kind,
    string Device = "", int Channel = 1,   // ltc: audio output key and channel; mtc: MIDI out; artNet: "" or unicast address, Channel = StreamId
    string Format = "25",
    string From = "clock",                 // "source:<id>" | "playlist:<id>" | "clock" | "internal:<id>"
    long OffsetNs = 0,                     // + = sent code is ahead (earlier) by this much
    string NodeId = "",
    bool AutoStart = false,
    double LevelDbfs = -12) : IEntity;

public sealed record TimecodeCue(
    string Id, string Name, string Tc,     // "01:00:10:00" (parsed at the scope's format)
    string Line,                           // a wire line, run as session "timecode"
    string? PlaylistId = null,             // fires while that playlist chases (its source and delay)
    string? SourceId = null,               // or on that source directly
    bool Enabled = true) : IEntity;

public sealed record TimecodeModel(
    ImmutableArray<TimecodeSource> Sources,
    ImmutableArray<TimecodeOutput> Outputs,
    ImmutableArray<TimecodeCue> Cues)
{
    public static readonly TimecodeModel Empty = new([], [], []);
}
```

Hunks in existing records. Every new parameter is the last one, with a default, so old projects load unchanged (the
`ProjectJson.Sanitize` pass turns missing arrays into empty ones):
- `PlaylistItem(…, double Volume, string? TcIn = null)`. JSON `tcIn`: the label `"01:00:00:00"` (`;` for DF), parsed
  at the chase's format. Labels are the cue sheet: changing the source format re-reads them.
- `Playlist(…, string? SyncGroup, TimecodeChase? Chase = null)`. JSON `chase`.
- `Project(…, ProjectSettings Settings, TimecodeModel? Timecode = null)`. JSON `timecode`. Accessor
  `TimecodeOrEmpty`; `AllIds()` and `Find*` gain sources, outputs and cues.
- `Ids`: `TimecodeSource = "tc"`, `TimecodeOutput = "to"`, `TimecodeCue = "tq"`.
- `ADD`/`SET`/`REMOVE`/`GET` work on the new entities through the generic path (the desk has no private API).
  `ADD timecode-source {…}` etc. join the `ADD` syntax.

### 4.2 Core value types (new files)

```csharp
public readonly record struct TcFormat(FrameRate Rate, bool DropFrame)            // Time/TimecodeFormat.cs
{
    public int LabelFps { get; }                       // 24/25/30/50/60 (Rate.Nominal)
    public bool Pairs => LabelFps > 30;                // LTC/MTC carry frame pairs
    public static bool TryParse(string text, out TcFormat f);   // "25", "29.97df", "23.976", "59.94df" …
    public string Text { get; }                        // "29.97df"
    public bool TryFrame(int h, int m, int s, int f, out long frame);  // refuses DF labels that do not exist
    public (int H, int M, int S, int F) Label(long frame);
    public long Ns(long frame, double phase = 0);      // Rate.TimeOf(frame) + phase·period
}
public enum TcObsKind { FrameEnd, QuarterFrame, Packet, Locate }
public readonly record struct TcObservation(long MonoNs, long Frame, double Phase, TcFormat Format, int Direction,
    TcObsKind Kind, uint UserBits, bool ClockFlag);
public readonly record struct TcLine(long AtNs, long TcNs, double Rate, double SigmaNs, int Points, long Run,
    int Direction, bool Running)                        // AtNs: mono (source side) or leader (relayed)
{
    public long TcAt(long ns) => Running ? TcNs + (long)Math.Round((ns - AtNs) * Rate) : TcNs;
    public long At(long tcNs);                          // the instant the line reaches a TC
}
public sealed record SourceReading(string SourceId, string State, TcLine? Line, TcFormat? Format, string Detected,
    double MeasuredFps, double SpeedPpm, double JitterUs, double LevelDbfs, TcCounters Counters, uint UserBits,
    bool ClockFlag, long LastMonoNs, string Words);
public readonly record struct TcCounters(long Biphase, long Missing, long Duplicate, long Discontinuity, long Invalid,
    long BadBcd, long Parity);
```

### 4.3 Verbs (`VerbCatalog`; new `VerbCategory.Timecode` appended at the end; `TC` is an alias of `TIMECODE` in `Match`, as `VT` is of `VIDEO`)

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| `TIMECODE STATUS` | `TIMECODE STATUS [<source>\|<playlist>]` | Low | no | JSON = `STATE.timecode` (or one entry) |
| `TIMECODE DEVICES` | `TIMECODE DEVICES` | Low | no | `{"audioIn":[{key,name,channels,rates,backend}],"audioOut":[…],"midiIn":[…],"midiOut":[…],"net":[{address,name}]}` |
| `TIMECODE SOURCE` | `TIMECODE SOURCE ADD <name> LTC [<device>[:<ch>]]\|MTC <port>\|ARTNET [STREAM <n>]\|INTERNAL\|NODE <node> <source>\|STAGEWATCH [FORMAT <fmt>] [ON <node>]` · `TIMECODE SOURCE REMOVE <source>` · `TIMECODE SOURCE RESET <source>` (counters) | Review | yes | ADD answers the id. Effect: "added FOH LTC (tc3f…); opening Focusrite input 2 — STATE says when code arrives" (an attempt, not a fact). REMOVE is refused while an engaged chase has it as its only source. |
| `TIMECODE CHASE` | `TIMECODE CHASE <playlist> <source>[,<backup>…]\|OFF [LOCKED\|TRIGGERED]` | **High** | yes | "Main chases FOH LTC (locked), engaged — waiting for code" · refused: "no source X", "Main already chases FOH LTC", "the chase of Main runs on node Leader-A (this node follows its anchors)", "Stagewatch is too coarse to lock a picture…" |
| `TIMECODE ENGAGE` | `TIMECODE ENGAGE <playlist>\|ALL ON\|OFF\|TOGGLE` | **High** | yes | "Main engaged: takes FOH LTC at its next frame" / "Main disengaged: plays on by hand from 01:02:03:04". Refused when nothing changes. |
| `TIMECODE SET` | `TIMECODE SET <playlist> DELAY <±ms>\|FREEWHEEL <ms>\|ONLOSS HOLD\|STOP\|CONTINUE\|RETURN ON\|OFF\|JUMP <ms>\|AUTO\|PRELOAD <ms>\|LOOKBACK <s>\|GAP BLACK\|HOLD\|REVERSE HOLD\|LOCATE` | Review | yes | e.g. "Main: picture 40 ms later than its timecode". DELAY is −2000…+2000 ms; FREEWHEEL 0…10000. |
| `TIMECODE IN` | `TIMECODE IN <playlist> <item> <HH:MM:SS:FF>\|NOW\|OFF` | Review | yes | "Song 2 starts at 01:04:10:00 (in Main)". NOW captures the chase's current TC (QLab's Capture [T9]). Refused: a DF label that does not exist ("00:01:00;00 does not exist at 29.97 DF — 00:01:00;02 is the first frame of that minute"); NOW with no code. |
| `TIMECODE INTERNAL` | `TIMECODE INTERNAL <source> PLAY\|STOP\|LOCATE <tc>\|RATE <0.5-2>` | **High** | yes | "Rehearsal TC rolling from 01:00:00:00" |
| `TIMECODE OUT` | `TIMECODE OUT ADD <name> LTC <device>[:<ch>]\|MTC <port>\|ARTNET [STREAM <n>] [TO <address>] FROM <source>\|PLAYLIST <playlist>\|CLOCK [FORMAT <fmt>]` · `TIMECODE OUT <out> ON\|OFF` · `TIMECODE OUT REMOVE <out>` | **High** (it drives other systems' air: lights, pyro) | yes | "LTC to lighting sending 01:02:03:04 from Main" |
| `TIMECODE CUE` | `TIMECODE CUE ADD <tc> [ON <playlist>\|<source>] <line…>` · `TIMECODE CUE REMOVE <cue>` | **High** (ADD: it schedules lines) / Review (REMOVE) | yes | ADD answers the id. Refused when the line's verb is unknown or is `CREDENTIAL`/`AUTH`/`APPROVE`. |
| `TIMECODE CALIBRATE` | `TIMECODE CALIBRATE <source> FROM <out>` | **High** (emits LTC for 5 s) | yes | "FOH LTC input latency 6.8 ms (round trip 18.2 ms, output 11.4 ms)" |
| `TIMECODE FIT` | `TIMECODE FIT <source> <json>` | **High** (node to node, like `PLAYLIST ANCHOR`) | yes | Silent on success (`Silent` reply, not journalled per line); refused when the source is not relayed from this peer. |

- **Per-argument risk** (CARRYOVER `RiskOf(line)`): `TIMECODE SOURCE ADD/RESET` Low; `TIMECODE SET … DELAY` Review;
  `TIMECODE ENGAGE … ON` High and `… OFF` High. Where `RiskOf` is not in place yet, the table above is the per-verb
  risk.
- Existing verbs: the guard in `Video`, `PlaylistGo`, `PlaylistCue` and `PlaylistStep` described in §3.8. `SUBSCRIBE`
  gains `TIMECODE [FRAMES]` (S8). It pushes
  `TIMECODE {"sources":{"tc…":["01:02:03:04","rolling"]},"chases":{"pl…":["locked","01:02:03:04","it…"]}}`.

### 4.4 STATE and context

`STATE.timecode` (new top-level object, written by `e.WriteTimecodeState(w)`):

```json
"timecode": {
  "sources": [{ "id":"tc3f2a…","name":"FOH LTC","kind":"ltc","node":"0badc0de","device":"Focusrite USB:2",
    "state":"rolling","text":"01:02:03:04","df":false,"format":"25","detected":"25","fpsMeasured":25.0004,
    "speedPpm":16,"jitterUs":4.1,"levelDb":-12.3,"level":"green","sigmaUs":1.2,
    "errors":{"biphase":0,"missing":0,"duplicate":0,"discontinuity":1,"invalid":0,"badBcd":0,"parity":0},
    "userBits":"00000000","clockFlag":false,"ageMs":7,"latencyMs":0,
    "words":"rolling at 25 fps, −12 dBFS, jitter 4 µs" }],
  "chases": [{ "playlist":"pl…","sources":["tc3f…","tc9b…"],"active":"tc3f…","mode":"locked","engaged":true,
    "state":"locked","writer":"0badc0de","text":"01:02:03:04","item":"it…","region":["01:00:00:00","01:03:12:10"],
    "rate":1.000016,"errorUs":38,"delayMs":40,"freewheelMs":500,"freewheelLeftMs":null,"onLoss":"hold","gap":"black",
    "lastJump":{"atUnixMs":1760001234567,"from":"01:01:59:20","to":"01:02:03:00"},
    "next":{"item":"it…","tcIn":"01:03:12:10","inMs":5230},
    "words":"locked to FOH LTC, picture 40 ms later, error 0.04 ms" }],
  "outputs": [{ "id":"to…","name":"LTC to lighting","kind":"ltc","from":"playlist:pl…","state":"sending",
    "text":"01:02:03:04","words":"sending 25 fps on Focusrite out 3" }],
  "cues": { "count":12, "next":{"id":"tq…","tc":"01:02:10:00","inMs":6900} }
}
```

- `STATE.playlists[]` gains `"chase": {"state":"locked","source":"tc…","text":"01:02:03:04","engaged":true}` or
  `null`.
- **Context items:** `timecode:<source>` (kind `timecode`): green `rolling`/`parked`; amber `acquiring`, `dropout`,
  level amber, or a format mismatch; red `unavailable`, `silent`/`lost` while a chase depends on it; grey with no
  chase. `chase:<playlist>` (kind `chase`): green `locked`; amber `freewheel`, `free`, `outside`, or disengaged while
  its code rolls; red `held`/`stopped` by loss; grey `waiting`. Headline: "chasing FOH LTC 01:02:03:04". The context's
  command list picks up the new verbs with their risk from the catalogue, as before.
- **Journal:** source state changes (rolling, lost, format mismatch); chase changes (locked, jump from→to, freewheel,
  held/stopped/free, engaged/disengaged, backup switch); TC cue firings with their receipt.

### 4.5 Seams

```csharp
// src/PlayMap.Engine/Timecode/TimecodeSeams.cs
public interface ITimecodeObserver
{
    void Observe(in TcObservation obs);
    void Level(double peakDbfs, long monoNs);
    void Count(TcCounterKind kind, int n = 1);
    void Fault(string words);                      // the input died: words for STATE
}
public interface ITimecodeInput : IDisposable { string SourceId { get; } bool IsOpen { get; } string Words { get; } }
public sealed record TimecodeDeviceInfo(string Kind, string Key, string Name, int Channels, IReadOnlyList<int> Rates, string Backend);
public interface ITimecodeInputs                    // the host's factory: never throws for missing hardware
{
    IReadOnlyList<TimecodeDeviceInfo> Devices();
    ITimecodeInput Open(TimecodeSource source, ITimecodeObserver observer);
}
public interface ITimecodeOutputs
{
    ITimecodeOutputRunner Open(TimecodeOutput output, Func<long, TcLine?> lineAtMono, TcFormat format);
}
public interface ITimecodeOutputRunner : IDisposable { bool IsSending { get; } string Words { get; } long? LatencyNs { get; } }

// Shared with the audio-reactivity package (src/PlayMap.Engine/Seams.cs, small hunk) — whichever lands first owns it
public sealed record AudioInputInfo(string Key, string Name, int Channels, IReadOnlyList<int> Rates, string Backend);
public interface IAudioCaptureSink
{
    void Samples(ReadOnlySpan<float> interleaved, int channels, int sampleRate, long firstSampleIndex, long readMonoNs);
    void Faulted(string words);
}
public interface IAudioCapture : IDisposable { int SampleRate { get; } int Channels { get; } }
public interface IAudioInputs
{
    IReadOnlyList<AudioInputInfo> Inputs();
    IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink);   // throws AudioUnavailableException(words)
}

// From the control package (MIDI); timecode consumes it
public readonly record struct MidiMessage(long MonoNs, ReadOnlyMemory<byte> Bytes);
public interface IMidiPorts
{
    IReadOnlyList<TimecodeDeviceInfo> Inputs();
    IReadOnlyList<TimecodeDeviceInfo> Outputs();
    IDisposable OpenIn(string key, Action<MidiMessage> onMessage);
    IMidiOut OpenOut(string key);
}
public interface IMidiOut : IDisposable { void Send(ReadOnlySpan<byte> bytes); }
```

- **`EngineServices`** gains `public TimecodeService? Timecode { get; init; }` in its own partial file. `TimecodeService`
  owns the inputs, readings, chase runtime and outputs. It is built by the host from the factories.
- **`EngineParts`** gains `static partial void UseTimecode(ref Func<TimecodeHostRequest, (ITimecodeInputs, ITimecodeOutputs)>? factory);`
  (one hunk, beside `UseRenderHost`), implemented in `Host/EngineParts.Timecode.cs`. No factory means a bare node:
  internal and node sources only.
- **`PlaybackLanes`** gains `Chase(Playlist list, int index, ClipTransport clip, int nextIndex, ClipTransport nextClip,
  long switchAtLeaderNs, long visibleFrom, long visibleUntil)`. Like `Adopt`, it takes a transport exactly; unlike
  `Adopt`, it also sets the next-region switch and the visibility window. `PlaylistPlayback` gains
  `long VisibleFromLeaderNs = long.MinValue, long VisibleUntilLeaderNs = long.MaxValue` (the defaults keep today's
  behaviour). `EngineSources.Route/PlaylistAt` return nothing outside the window (render hunk).
- **`NetworkClock.Epoch`** (an `int`, `Interlocked.Increment` in `Follow` and `Lead`), read by the chase every tick.
- **`RecoveryRecord`** gains `ImmutableArray<string> DisengagedChases = default` and
  `ImmutableArray<string> SendingTimecodeOutputs = default`.

### 4.6 NodeSettings

`NodeSettings(…, bool StagewatchEnabled, string ArtNetInterface = "")`: `""` = all interfaces, an address = that
interface only. It is shared with the control package's Art-Net input; the one socket binds there. Nothing else:
device keys and latencies live on the sources, which carry a `NodeId`.

---

## 5. Files

**New**

| File | What |
|---|---|
| `src/PlayMap.Core/Time/TimecodeFormat.cs` | `TcFormat`, DF-aware label↔frame (29.97/59.94, pairs), strict labels, midnight unwrap helpers |
| `src/PlayMap.Core/Time/Ltc/LtcCodeword.cs` | The 80-bit word: pack/unpack, flags by rate, parity, user bits |
| `src/PlayMap.Core/Time/Ltc/LtcEncoder.cs` | Fractional bit phase, analytic rise-time edges, pacing hooks |
| `src/PlayMap.Core/Time/Ltc/LtcDecoder.cs` | §3.3 |
| `src/PlayMap.Core/Time/Mtc.cs` | `MtcParser`, `MtcEncoder` |
| `src/PlayMap.Core/Time/ArtTimeCode.cs` | 19-byte codec (pure; the socket is in Net) |
| `src/PlayMap.Core/Time/TimecodeObservation.cs` | `TcObservation`, `TcLine`, `SourceReading`, `TcCounters` |
| `src/PlayMap.Core/Time/SampleClockFit.cs` | Lower-envelope sample→mono fit |
| `src/PlayMap.Core/Time/TimecodeTracker.cs` | Per-source state machine, rate detection, counters |
| `src/PlayMap.Core/Time/TimecodeFit.cs` | Robust windowed fit |
| `src/PlayMap.Core/Playback/TimecodeRegions.cs` | Regions and lookup |
| `src/PlayMap.Core/Playback/ChaseLogic.cs` | Pure chase decisions (§3.8) |
| `src/PlayMap.Core/Model/TimecodeModel.cs` | §4.1 records |
| `src/PlayMap.Net/ArtNet/ArtNetSocket.cs` | Shared UDP 6454 socket, OpCode dispatch, send (if the control package has not made it) |
| `src/PlayMap.Net/Timecode/TimecodeRelay.cs` | `TIMECODE FIT` push to the chase writer over the peer wire link |
| `src/PlayMap.Media/Audio/OpenAlAudioInputs.cs` | `IAudioInputs` on OpenAL Soft capture (`ALC_EXT_CAPTURE`) |
| `src/PlayMap.Media/Audio/FfmpegAudioInputs.cs` | `IAudioInputs` on libavdevice (`alsa`, `pulse`, `dshow`) and `file:` (looped WAV/BWF), any channel of N |
| `src/PlayMap.Media/Audio/LtcAudioOut.cs` | LTC to an OpenAL device or the loopback device; reports output latency (`AL_SAMPLE_OFFSET_LATENCY_SOFT`) |
| `src/PlayMap.Engine/Timecode/TimecodeSeams.cs` | §4.5 |
| `src/PlayMap.Engine/Timecode/TimecodeService.cs` | Owns inputs, trackers, fits, readings, outputs; device list |
| `src/PlayMap.Engine/Timecode/TimecodeInputs.cs` | Source factory: LTC (audio + decoder + `SampleClockFit`), MTC, Art-Net, internal, node, Stagewatch |
| `src/PlayMap.Engine/Timecode/InternalTimecode.cs` | Rehearsal/master generator line |
| `src/PlayMap.Engine/Timecode/ChaseRunner.cs` | On-lane step: readings → `ChaseLogic` → `PlaybackLanes.Chase`; triggered GOs; re-anchor limits |
| `src/PlayMap.Engine/Timecode/TimecodeOutputs.cs` | LTC/MTC/Art-Net out runners |
| `src/PlayMap.Engine/Timecode/TimecodeCues.cs` | Crossing detection, look-ahead, AT stamping, firing through the router |
| `src/PlayMap.Engine/EngineCore.Timecode.cs` | `TIMECODE` verbs, the transport guard helper, `WriteTimecodeState`, context items |
| `src/PlayMap.Engine/EngineServices.Timecode.cs` | `EngineServices.Timecode` init property |
| `src/PlayMap.Engine/Host/EngineParts.Timecode.cs` | `UseTimecode`: OpenAL/FFmpeg audio inputs, `ArtNetSocket`, the MIDI ports (if present), LTC out |
| `src/PlayMap.App/Views/TimecodePanel.axaml(.cs)`, `src/PlayMap.App/ViewModels/TimecodeViewModel.cs` | Desk panel (§8) |
| Tests: `tests/PlayMap.Core.Tests/Time/{TimecodeFormat,LtcCodeword,LtcDecoder,LtcEncoder,Mtc,ArtTimeCode,SampleClockFit,TimecodeTracker,TimecodeFit}Tests.cs`, `tests/PlayMap.Core.Tests/Playback/{TimecodeRegions,ChaseLogic}Tests.cs`, `tests/PlayMap.Net.Tests/ArtNetTimecodeTests.cs`, `tests/PlayMap.Media.Tests/{LtcLoopback,FfmpegLtcFile}Tests.cs`, `tests/PlayMap.Engine.Tests/{TimecodeVerbs,TimecodeChase,TimecodeMultiNode,TimecodeCues}Tests.cs`, `tests/PlayMap.Render.Tests/TimecodeTestCardTests.cs`, `tests/PlayMap.App.Tests/TimecodeViewModelTests.cs` | — |

**Touched (small hunks)**

| File | Hunk |
|---|---|
| `src/PlayMap.Core/Model/PlaylistModel.cs` | `TcIn` on `PlaylistItem`, `Chase` on `Playlist` (last parameters, defaulted) |
| `src/PlayMap.Core/Model/Project.cs` | `Timecode` (last parameter), `TimecodeOrEmpty`, `AllIds`, finders |
| `src/PlayMap.Core/Model/Ids.cs` | Three prefixes |
| `src/PlayMap.Core/Time/Timecode.cs` | `TryParse` refuses non-existent DF labels |
| `src/PlayMap.Core/Time/ClockOffsetEstimator.cs` | `NetworkClock.Epoch` |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | `VerbCategory.Timecode`, the verbs of §4.3, the `TC` alias in `Match`, `ADD` syntax words |
| `src/PlayMap.Core/Bus/Pairing.cs` | `NodeSettings.ArtNetInterface` |
| `src/PlayMap.Core/Content/GeneratorCatalog.cs` | `pattern.timecode` entry (S6) |
| `src/PlayMap.Engine/Playback/PlaybackLanes.cs` | `Chase(...)`, the visibility window fields |
| `src/PlayMap.Engine/Rendering/EngineSources.cs` | Honour the visibility window |
| `src/PlayMap.Engine/EngineCore.cs` | `Tick` calls `ChaseRunner.Step` (2 lines) |
| `src/PlayMap.Engine/EngineCore.Router.cs` | `"TIMECODE …" => Timecode(spec, args, line, session)` |
| `src/PlayMap.Engine/EngineCore.Transport.cs` | `ChaseGuard(list)` at the top of `Video`, `PlaylistGo`, `PlaylistCue`, `PlaylistStep` |
| `src/PlayMap.Engine/StateWriter.cs` | `e.WriteTimecodeState(w)`; the `chase` row in `WritePlaylist` |
| `src/PlayMap.Engine/ContextBuilder.cs` | `e.AddTimecodeContext(items)` |
| `src/PlayMap.Engine/NodeHome.cs` | Two `RecoveryRecord` fields |
| `src/PlayMap.Engine/Host/EngineParts.cs` | `UseTimecode` partial hook |
| `src/PlayMap.Engine/Host/EngineHost.cs` | Build `TimecodeService` from the hook; dispose order (outputs, then inputs, before the engine) |
| `src/PlayMap.Net/Wire/WireSession.cs` | `SUBSCRIBE TIMECODE [FRAMES]` (S8) |
| `src/PlayMap.Media/PlayMap.Media.csproj` | `Silk.NET.OpenAL.Extensions.EXT` 2.23.0 |
| `src/PlayMap.Render/…` (pattern drawing) | `pattern.timecode` drawing (S6) |
| `docs/WIRE.md`, `docs/SHOWBUS-NODE.md` (§3 Stagewatch, §5 item 7), `docs/ARCHITECTURE.md` (time row), `docs/UX.md` §4.6, `docs/ROADMAP.md` (R3.7 status), `README.md` | Papers |

---

## 6. Dependencies

- **Work packages:**
  - **control** (OSC/MIDI/Art-Net): the `IMidiPorts` seam, and the shared Art-Net socket (whichever lands first
    makes it). Without control: MTC ships as codec and tests, and opening MIDI devices waits.
  - **fleet** (redundancy, health): the anchor push to followers (CARRYOVER; the minimal fallback is in §3.9),
    leader failover (relays retarget), and the clock slew on follow (CARRYOVER).
  - **audio** (audio reactivity / R4.4): `IAudioInputs` is shared, and WASAPI/ASIO multichannel comes later from there.
  - **showsafety**: TIMECODE verbs are High where stated, and the Show Mode permission table covers them. Open
    question: whether arming forces `onLoss = continue` (§9).
  - **present/framelock**: per-output display latency (CARRYOVER) is what makes "the frame the TC says" true at the
    lens.
  - **content**: `pattern.timecode` drawing; drawn transitions (for later jump fades); parameter animation (for later
    keyframed `SET` tracks).
  - **companion**: consumes the verbs and STATE (§8).
- **Third-party:**
  - `Silk.NET.OpenAL.Extensions.EXT` 2.23.0 (MIT) for `ALC_EXT_CAPTURE`. It sits beside the already-referenced
    `Silk.NET.OpenAL` 2.23.0 and `Silk.NET.OpenAL.Soft.Native` 1.23.1 (OpenAL Soft is LGPL, a native library loaded at
    run time, already shipped by `AudioOut`).
  - FFmpeg 9 libavdevice through the existing `FFmpeg.AutoGen` 9.0.1.1 (the user's install, loaded at run time).
  - Later, Windows multichannel: `NAudio.Wasapi` 3.1.0 (MIT) or our own WASAPI interop.
  - **No libltc code** (LGPL): the method only. No Stagewatch code (no licence file): ideas only.

---

## 7. Verification here

| Area | Real in this container | Simulated (how) | Compile-only (fake standing in) |
|---|---|---|---|
| LTC codec | Encoder → decoder across the M1 matrix, at all rates and sample rates, with noise, hum, DC, polarity, varispeed ramps, reverse, dropouts; timing error against the encoder's analytic edge times | Noise from `Pcg32` (Box–Muller); hum and band-limited noise synthesised | — |
| Rate/format | Detection on synthetic code with the real rate offset 0, ±50 ppm, 1000 ppm | — | — |
| Fit, sample clock | Synthetic observations with known lines plus Gaussian/one-sided jitter; capture callbacks with 0–10 ms one-sided delay | `ManualClock`, seeded `Pcg32` | — |
| Chase | `ChaseLogic` property tests (§2 M4); engine end to end with a fake `ITimecodeInputs` that plays a scripted TC run into the real engine; STATE, context and journal asserted | Scripted runs: start, jump, dropout, return, reverse, midnight, DF minute boundaries, varispeed | — |
| Multi-node | Two engines in one process with offset `ManualClock`s; anchors over a loopback wire pair; `TIMECODE FIT` relay | Clock offset and drift injected through `ClockOffsetEstimator` samples | — |
| Art-Net | Real UDP send/receive on 127.0.0.1 (and port 6454 when free; a test port otherwise) | Packet loss and reordering by a test proxy | — |
| MTC | Byte streams with per-message stamps, MIDI jitter σ 1 ms | Synthetic MIDI streams | Real MIDI ports (the control package's `IMidiPorts`; a fake port here) |
| Audio in | FFmpeg `file:` source reading our LTC WAV through FFmpeg 9 (`/opt/ffmpeg9`) → decoder → chase | — | OpenAL capture on a device (there is no audio device here: the test asserts the "no capture device — words" path), FFmpeg `alsa`/`pulse`/`dshow`, WASAPI multichannel (Windows) |
| LTC out | `LtcAudioOut` on OpenAL Soft's **loopback** device (`ALC_SOFT_loopback`, already used by `AudioOut` tests) rendered and decoded: frame-exact, rise time and level measured | — | A real output device |
| Render | `pattern.timecode` on Mesa llvmpipe under Xvfb `:99`, frame index read back; the gap visibility window in `EngineSources` asserted on a headless render | — | — |
| Hardware lab (R0.5) | — | — | LTC from QLab/Reaper/Stagewatch into a USB interface; MTC from a DAW; Art-Net TC from a lighting console; end-to-end error measured with the camera and `LATENCY MEASURE`. Not possible here; listed honestly. |

What remains unproven here: real capture-callback timing on Windows WASAPI, macOS and PipeWire (the lower-envelope fit
is designed for it but tested only against simulated jitter); MIDI stack jitter on Windows MIDI Services; and whether
consoles send Art-Net TC at frame start (absorbed by the source's `latencyNs`, set by calibration or by eye).

---

## 8. Desk and Companion surface

**Desk**
- **Transport bar:** one big TC readout per chased playlist (the focused one first), monospace, coloured by state the
  way Pixera colours its monitor [T5]: green locked, grey waiting or parked, amber freewheel (with the countdown) or
  free, red held or stopped, violet disengaged. Below it, a source chip: "LTC 25 · −12 dB · ±4 µs · +16 ppm".
- **Playlist header:** a **Chase** switch (engage/disengage), a source picker (with backups), Locked/Triggered, and
  "picture 40 ms later" (delay, with its sign in words).
- **Items:** a TC in label per item, with follow-on items shown greyed as derived. A TC ruler strip shows regions,
  gaps and the playhead (the implied timeline). Glyphs show the end action inside a locked list.
- **Right-click an item:** *Set TC in…*, *Capture TC in now*, *Clear TC in*, *Copy TC*. **Right-click a playlist:**
  *Chase timecode…*, *Engage / Disengage*, *Delay ▸ ±1 frame / ±10 ms / set…*, *On loss ▸ Hold / Stop / Continue*,
  *Freewheel ▸ 0.5 / 1 / 2 / 5 s*, *Gap ▸ Black / Hold*. **Right-click the TC readout:** *Show source*, *Reset
  counters*, *Show test card on…*. Each entry maps to the verb of the same name; nothing is desk-only.
- **Timecode panel** (devices drawer):
  - Sources: a level meter with WATCHOUT's bands; detected/measured rate; speed (ppm and %); jitter and σ; the error
    counters; user bits; the clock flag; latency (with *Calibrate*).
  - Outputs: on/off, from, level.
  - The internal generator transport: Play, Stop, Locate (type a TC), Rate (for rehearsal without the audio desk).
  - TC cues list.
- **Test card:** *Show test card* puts `pattern.timecode` on chosen outputs (identify-style, over the show), for
  proving lock on the wall or filming two walls.

**Companion** (made by the companion package from `VERBS` and STATE; two-way: feedback shows the observed state,
never the press)
- **Variables:** `tc_<src>` (01:02:03:04), `tc_<src>_state`, `tc_<src>_fps`, `tc_<src>_level`, `tc_<src>_speed`;
  `chase_<pl>_state`, `chase_<pl>_tc`, `chase_<pl>_item`, `chase_<pl>_next` (label), `chase_<pl>_next_in`
  (countdown to the next region), `chase_<pl>_error_ms`.
- **Feedbacks:** `tc_state` (green rolling, amber dropout/acquiring, red lost/unavailable, flashing amber during
  freewheel), `chase_engaged`, `chase_locked`, `chase_loss` (red when held/stopped), `tc_out_sending`.
- **Actions:** `timecode_engage` (on/off/toggle), `timecode_delay_nudge` (±1 frame, ±10 ms), `timecode_capture_in`
  (selected item ← now), `timecode_internal` (play/stop/locate with a TC text field that takes variables),
  `timecode_out` (on/off), `timecode_onloss`.
- **Presets, made live from STATE:**
  - per source: a big TC readout key with state colours and level in the corner;
  - per chased playlist: an engage toggle with state colours, and a "next region in 5.2 s" key;
  - the rehearsal generator's Play/Stop/Locate row.

  New sources and chases add buttons as they appear (the companion package's auto-creation).

---

## 9. Risks and open questions

1. **Capture timestamps:** callback timing differs by backend (WASAPI shared 10 ms periods, PipeWire quanta, OpenAL
   Soft's ring). The lower-envelope fit handles one-sided jitter, but a backend that batches irregularly could bias
   it. *Mitigation:* the fit reports the audio-vs-mono drift and residual; `TIMECODE CALIBRATE` absorbs the constant
   part.
2. **OpenAL capture is mono/stereo.** Channel 7 of a 16-channel interface needs FFmpeg `alsa`/`dshow` (any channel of
   N) now and WASAPI/ASIO later. *Open:* which is the Windows default: FFmpeg `dshow` audio (shared mode, extra
   latency) or our own WASAPI.
3. **Art-Net port 6454 on Windows** may be held exclusively by another program (a visualiser, a console's software).
   We bind with `SO_REUSEADDR` and say so in words; a unicast port option would be non-standard.
4. **The default on loss.** `hold` (rehearsal-friendly) or `continue` (show-safe, disguise's advice [T3]). *Proposal:*
   default `hold` with 0.5 s freewheel, and the showsafety package's preflight warns when an armed show has `hold`.
   *Open:* should arming switch to `continue`?
5. **Re-anchor churn on followers:** bounded at ≤ 4/s and typically under 1/min. A follower's decoder does not seek for
   sub-frame steps. Audio slews. Acceptable.
6. **Frame-pair LTC (50/60):** two-frame label resolution. Timing still comes from every sync word, so the fit is
   unaffected; only `TIMECODE IN` labels at odd frames need the pair rule (accepted, mapped to the frame).
7. **29.97 vs 30 confusion** in a venue: detection plus words ("code is 30 NDF at 29.97 real rate — set 29.97") rather
   than silent drift (Resolume's 1 s jump symptom [T7]).
8. **Generators inside a chased playlist:** content is a function of show time, so a generator item should take its
   time from the item's clip position, not from the leader clock. *Open, for the content package:* do generators read
   clip time when they sit in a playlist?
9. **Reverse:** hold or locate only; true reverse playback needs intra-only codecs (later).
10. **MTC out timing:** `PeriodicTimer` resolution on Windows. *Unverified:* whether .NET's timer uses high-resolution
    waitable timers. The spin on the last 200 µs bounds jitter at a CPU cost of about 2 %. Measure on the lab machine.
11. **Stagewatch** is only coarse until Show Bus 1.3 adds the `tc` record (a Stagewatch change, not ours).
12. **Undo:** `TIMECODE CHASE/SET/IN` are project edits (undoable). `ENGAGE` is runtime and not undoable, which is
    deliberate: live actions stay out of undo (ROADMAP R3.5).

---

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| `TcFormat`, strict DF, codeword, encoder, decoder + test matrix | 9 |
| MTC codec, Art-Net codec and socket | 4 |
| Sample clock, tracker, fit | 5 |
| Regions, `ChaseLogic` + property tests | 6 |
| Model, verbs, guard, STATE, context, journal | 6 |
| `TimecodeService`, inputs (OpenAL, FFmpeg file/device), internal, relay, recovery, host wiring | 6 |
| Multi-node tests, anchor push fallback, clock epoch | 3 |
| **Must subtotal** | **39** |
| Outputs (LTC/MTC/Art-Net), conversion, calibrate | 5 |
| TC cues, triggered mode, backups, preload | 5 |
| Test card, `SUBSCRIBE TIMECODE`, desk panel and menus, papers | 6 |
| **Should subtotal** | **16** |
| **Total** | **55** |
