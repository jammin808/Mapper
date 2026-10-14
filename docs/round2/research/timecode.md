# Timecode chase: LTC, MTC, Art-Net timecode, drop-frame, freewheel

*Research for PlayMap round 2, written 13 October 2026. PlayMap read at `/home/user/mapper` `c12b186` (13 Oct 2026),
Stagewatch at `/home/user/jammin808/stagewatch` `5744bae`, Patterns at `/home/user/patterns-svc`. The last section,
"Design implications for PlayMap", feeds `design/timecode.md`.*

## 0. How to read this

- Every fact carries a source tag `[Tn]`. §9 lists each tag's URL, its date, and whether I opened it (**V**, verified:
  I read the page or file) or not (**U**, unverified). **V-h** means I read the search engine's extract of the page,
  not the whole page. Statements marked *(computed)* I worked out myself; *(inference)* and *(design)* are mine.
- libltc is LGPL-3.0-or-later. I read its decoder to learn the method. Nothing from it is to be copied. The same goes
  for Stagewatch's decoder: Stagewatch has no licence file.

## 1. The findings in brief

1. **Every competitor does chase the same way, and every one has gaps PlayMap can close.** WATCHOUT 7's LTC Bridge
   sends one sync message a second plus a "time dilation" ratio, and pauses the timelines after about 100 ms without
   code [T1]. Pixera 26.3 has the richest set of options: timeout frames, a no-input behaviour, an offset, "pause until
   TC reaches offset", drift correction (none/standard/adaptive), a jump threshold, a jump fade and a preload delay,
   and hot regions [T5]. disguise has engage/disengage, per-track TC tags and TC Adjust, "on clock lost" stop versus
   keep playing, and Locked versus Normal clips (jam sync versus triggered) [T2][T3][T4]. QLab has five on-start
   lookback rules, on-stop pause/stop/keep-running with a 0–2 s freewheel, and a Capture button [T8][T9]. Resolume has
   two inputs, a per-clip offset and a delay in frames, but no SMPTE on clips that carry audio [T7]. Millumin has a
   freewheel in seconds, a per-machine delay and a test card with a blinking circle and a beep [T11].
2. **No competitor documents a sub-frame fit or a measured residual.** WATCHOUT's 1 Hz message carrying a median
   dilation is the nearest [T1]. A least-squares fit of every LTC frame end (one per frame) or every MTC quarter-frame
   (four per frame) against the monotonic clock gives a rate and an offset with microsecond residuals *(inference)*.
   That is exactly what PlayMap's `TimelineAnchor` (media time at a leader instant, at a rate) needs.
3. **LTC's timing reference is exact.** In ST 12-1, bit 0 of a codeword starts at the video reference datum of the
   frame it labels, so the end of the sync word is the start of the next frame [T13]. For rates above 30 fps, one
   codeword labels a *frame pair*, and the second frame starts at bit 40 [T13]. A decoder that timestamps the
   sync-word end to a fraction of a sample has the best timing information LTC can carry *(inference)*.
4. **Rate needs three facts, not one.** The label rate (24/25/30) comes from the bit rate and the frame count. The
   DF flag is bit 10. The real rate (x or x/1.001) comes from the measured frame period against the audio clock: the
   0.1 % pull-down is 1000 ppm, far above crystal tolerance, so about one second of code settles it *(computed)*.
   Stagewatch snaps to 24/25/30 and so cannot tell 29.97 NDF from 30 (local code). WATCHOUT shows the detected rate,
   the measured rate and the speed deviation side by side [T1].
5. **Art-Net timecode is a 19-byte UDP packet** on port 0x1936 (6454), OpCode 0x9700, with fields
   `StreamId, Frames, Seconds, Minutes, Hours, Type` (0 Film 24, 1 EBU 25, 2 DF 29.97, 3 SMPTE 30). It is usually
   broadcast by a single controller [T12].
6. **MTC** sends eight quarter-frame messages (`F1 0nnndddd`) every two frames, and a full-frame SysEx
   (`F0 7F 7F 01 01 hh mm ss ff F7`) to locate. The rate rides in the hour byte (`0rrhhhhh`: 24/25/29.97DF/30) [T14].
7. **Stagewatch already decodes and generates LTC.** It decodes 24/25/30 with the DF flag, chases a timer, fires TC
   triggers, and puts out LTC as time of day or as the countdown. Over the Show Bus it publishes only `ltc` (text,
   republished on whole-second changes), `ltcRunning` and `ltcChase`. That is good enough to show, but not to chase
   frame-accurately (local code, §6).

## 2. SMPTE ST 12-1 linear timecode (LTC)

### 2.1 Codeword layout (80 bits, transmitted bit 0 first, BCD least significant bit first)

| Bits | Content | Source |
|---|---|---|
| 0–3, 8–9 | Frame units, frame tens (0–2) | [T13][T15] |
| 10 | Drop-frame flag (30-frame systems; unused at 25 and 24) | [T13][T15] |
| 11 | Colour-frame flag (30 and 25; not 24) | [T13] |
| 16–19, 24–26 | Seconds units, tens | [T15] |
| 32–35, 40–42 | Minutes units, tens | [T15] |
| 48–51, 56–57 | Hours units, tens | [T15] |
| 4–7, 12–15, 20–23, 28–31, 36–39, 44–47, 52–55, 60–63 | Eight 4-bit binary groups (32 user bits) | [T15][T16] |
| 27 / 43 / 58 / 59 | **30- and 24-frame:** 27 polarity correction, 43 BGF0, 58 BGF1, 59 BGF2. **25-frame:** 59 polarity correction, 27 BGF0, 58 BGF1, 43 BGF2 | ST 12-1 Table 3 [T13]; same table in libltc's header [T16] |
| 64–79 | Sync word `0011 1111 1111 1101`, in transmit order. It is preceded by `00` and followed by `01`, which shows the direction | [T15][T16] |

- **Biphase mark:** every bit cell starts with a transition, and a 1 has a second transition at mid-cell. The code is
  self-clocking and polarity-free [T15]. At 30 fps, 80 bits give 2400 bit/s: 1.2 kHz for all zeros and 2.4 kHz for all
  ones [T16]. At 48 kHz a bit is 25.0 / 24.0 / 20.02 / 20.0 samples at 24 / 25 / 29.97 / 30 fps *(computed)*.
- **Polarity correction bit:** it makes the number of zeros in the whole word even, so every word starts on the same
  edge polarity [T15][T16]. Some generators do not set it (libltc leaves BGFs to the application [T16]). A reader
  should count a parity miss and still accept the word *(inference)*.
- **BGF1 (bit 58):** "the time is locked to wall clock" (an external clock) [T15][T16]. **BGF0/BGF2** give the format
  of the user bits: none, 8-bit characters, date and time zone (SMPTE 309M), or page/line [T16].
- **Rise time:** 40 ± 10 µs (libltc's encoder comment, which cites the standard) [T16]. ST 12-1 §9 has a modulation
  section [T13] that I did not read in full.
- **Frame alignment:** "For 525/60 the first transition occurs at the beginning of line 5 ± 1.5 lines; for 625/50 at
  line 2 ± 1.5; for 1125/60 exactly at the vertical sync reference ± 1 line" (libltc `LTCFrameExt` notes [T16]). The
  2014 standard says: "for digital systems the video reference datum is the start of the video frame… for 525/59.94
  the start of line 4 and for all other systems the start of line 1" [T13 V-h]. The offset is at most a few lines
  (under 0.3 ms) *(computed)*. That is far below what matters for projection, so PlayMap treats bit 0 as the start of
  the frame *(design)*.
- **Above 30 fps (50, 59.94, 60, and 48 from Amendment 1):** "counting is done on frame pairs… an edit resolution
  of two frames". "The first frame of the pair [is] aligned with LTC bits 0 through 39 and the second frame of the pair
  aligned with LTC bits 40 through 79" [T13][T13a]. ST 12-2 notes that many implementations never set the field-mark
  flag that labels the frames of a pair [T13b]. ST 12-3 extends to 72, 96, 100 and 120 fps using the user bits, with
  drop frame only at 120 [T17 V-h].

### 2.2 Direction, speed, level

- **Reverse:** played backwards, the sync word arrives as `1011 1111 1111 1100` (0xBFFC in the decoder's shift
  register). libltc detects that, reverses the bits and swaps the bytes to recover the word, and flags it `reverse`
  [T16].
- **Varispeed:** libltc keeps a running bit-period estimate, `period = (3·period + interval)/4`, at every transition.
  It splits "long" (a whole cell, a 0) from "short" (a half cell, two of which make a 1) at ¾ of the period [T16]. A
  gap longer than four periods resets the frame parser and is not used for tracking [T16]. Stagewatch uses the same
  idea with different constants: calibrate on the longest of the first 24 intervals, ignore intervals shorter than 0.4
  of a half bit at 35 fps, and treat anything longer than 1.3 bits at 20 fps as a dropout (local code).
- **Level:** libltc follows min/max envelopes that decay 1/16 per sample toward the centre, and switches state at 50 %
  of the envelope, which works as an adaptive Schmitt trigger. It reports the volume in dBFS and the min/max per frame
  [T16]. Stagewatch removes DC (about 10 Hz high-pass), uses a peak follower with fast attack and about 70 ms release,
  a hysteresis band of 0.2 × peak, and a −42 dBFS floor (local code). WATCHOUT's levels: **≥ −14 dBFS green, −26 to −14
  dBFS orange, < −26 dBFS red; "clipping is harmless. Only too-quiet is a problem"** [T1].
- **Per-frame timing:** libltc records `off_start` (the sample of the first transition of bit 0), `off_end`, and
  `biphase_tics[80]` (the time per bit within the frame), which sum to the frame length in samples [T16]. *(inference)*
  For chase, the one number that matters is the sub-sample instant of the sync word's end. Interpolating each
  threshold crossing between two samples gives about 1/20 of a sample, roughly 1 µs at 48 kHz, before any averaging.

### 2.3 Generating LTC

- Build the 80 bits (BCD, flags, user bits, sync), set the polarity bit for the rate's position, and toggle the level
  at every cell start and mid-cell for each 1. Then shape the edges, for example with a one-pole low-pass of
  τ ≈ rise/2.2, for a 40 µs 10–90 % rise [T16] *(inference)*. A fractional samples-per-bit (20.02 at 29.97 and 48 kHz)
  needs accumulated phase, not rounding per bit *(inference)*. Stagewatch rounds per bit and supports only 24/25/30
  (local code).
- **Clock:** "LTC follows the clock of the audio device… and is guaranteed not to drift from that clock. MTC follows
  the computer's internal clock" (QLab) [T10]. A generator must therefore pace samples from the device clock and
  correct toward the show clock by stretching or dropping a fraction of a sample, never by stepping frames
  *(inference)*.
- **Pre-roll:** "devices listening to timecode sometimes require a few frames… start timecode output at least a few
  frames earlier… treat 1:00:00:00 as the beginning of the timeline and use hour 0 for preroll" [T10]. The same
  convention appears in Resolume ("Show 1 will start at 01:00:00:00, Show 2 at 02:00:00:00") [T7]. Wikipedia adds
  "to create negative time code add one hour to time (avoid midnight effect)" [T15].

## 3. MIDI timecode and Art-Net timecode

### 3.1 MTC [T14]

- **Hour byte** `0rrhhhhh`: rr = 00 → 24, 01 → 25, 10 → 29.97 DF, 11 → 30. Then `00mmmmmm`, `00ssssss`, `000fffff`.
  The values are binary, not BCD.
- **Quarter frame** `F1 0ppp dddd`. Pieces 0–7 carry: frame low nibble, frame high bit, seconds low, seconds high,
  minutes low, minutes high, hours low, and `0rrh` (the rate and the hour's high bit). A full time takes eight
  messages, which is two frames. "When time is running forward, the piece numbers increment from 0–7; the time at
  which piece 0 is transmitted is the coded instant." Rewinding counts the pieces backwards. There are 96, 100 or 120
  messages a second *(computed)*.
- **Full frame (locate)** `F0 7F 7F 01 01 hh mm ss ff F7`, sent on a jump. "After a jump, the time clock stops until
  the first following quarter-frame message is received." The user bits ride in SysEx, not in the quarter frames.
- **No 23.976 code and no 59.94 code:** MTC tells film speed from video speed "only by the rate at which timecode
  advances"; 29.97 DF is "30 frame/s dropframe at 0.1% pulldown" [T14].
- *(inference)* Every quarter frame is a timing tick: piece k of a sequence coded C arrives at C + k/4 frames (forward).
  That gives four fit points per frame against MIDI's roughly 1 ms jitter. Wikipedia advises a separate MIDI port for
  MTC because a busy stream adds jitter [T14].

### 3.2 Art-Net ArtTimeCode [T12]

| Offset | Field | Size | Value |
|---|---|---|---|
| 0 | ID | 8 | `Art-Net\0` |
| 8 | OpCode | 2, little-endian | `0x9700` (OpTimeCode) |
| 10 | ProtVerHi, ProtVerLo | 1 + 1 | 0, 14 |
| 12 | Filler1 | 1 | 0 (receivers ignore it) |
| 13 | StreamId | 1 | "Used to identify different streams of time code. Value of 0x00 is the master." Added in revision DK |
| 14 | Frames | 1 | 0–29 "depending on mode" |
| 15 | Seconds, Minutes, Hours | 1 + 1 + 1 | 0–59, 0–59, 0–23 |
| 18 | Type | 1 | 0 = Film (24), 1 = EBU (25), 2 = DF (29.97), 3 = SMPTE (30) |

- 19 bytes in all; one UDP port, 0x1936, for both source and destination; directed broadcast to 2.255.255.255 or
  10.255.255.255, or the subnet's broadcast. "Use of the packet is application specific but in general a single
  controller will broadcast the packet to the network" [T12]. The 19-byte layout matches two open implementations
  [T12b].
- Type codes cover only 24/25/29.97DF/30. There are no 23.976, 29.97 NDF, 50 or 60 codes [T12]. The packet carries
  no sub-frame time, so a receiver timestamps arrival *(inference)*.

## 4. Frame rates and drop-frame arithmetic (computed; rules from [T13])

| Rate | Labels a second | Real frame period | Drop rule | TC minus wall time after 1 h |
|---|---|---|---|---|
| 23.976 (24000/1001) | 24 (00–23) | 41.7083 ms | none | TC behind by 3.6 s (labels count 86 400 a TC-hour; 86 313.7 real frames a wall hour) |
| 24 | 24 | 41.6667 ms | none | 0 |
| 25 | 25 | 40.0 ms | none ("no counting mode such as drop frame… is applicable to 25-frame counting" [T13]) | 0 |
| 29.97 DF (30000/1001) | 30 | 33.3667 ms | omit labels 00 and 01 at the start of each minute except 00, 10, 20, 30, 40, 50 [T13]: 17 982 frames per 10 minutes, 1798 per dropped minute | 3.6 ms ahead (≈ 86 ms a day) |
| 29.97 NDF | 30 | 33.3667 ms | none | 3.6 s behind ("+108 frames (+3.6 s) in one hour" [T13]) |
| 30 | 30 | 33.3333 ms | none | 0 |
| 50 | 25 frame pairs in LTC (or 50 labels in software) | 20.0 ms | none | 0 |
| 59.94 DF (60000/1001) | 30 pairs in LTC (or 60 labels) | 16.6833 ms | the same two pair labels, which is 4 frames a minute except every tenth minute: 35 964 frames per 10 minutes, 3596 per dropped minute | 3.6 ms ahead |
| 60 | 30 pairs / 60 labels | 16.6667 ms | none | 0 |

- Frame number from label (DF, 30 labels): `n = (3600·h + 60·m + s)·30 + f − 2·(M − ⌊M/10⌋)`, with `M = 60·h + m`.
  At 59.94 replace 30 with 60 and 2 with 4. Labels ;00 and ;01 (;00–;03 at 59.94) do not exist in dropped minutes. A
  reader must refuse them, or map them as Stagewatch does when the operator typed `:` (local code). PlayMap's
  `Timecode.TryParse` accepts them silently today (local code, `src/PlayMap.Core/Time/Timecode.cs`).
- *(computed)* Telling the real rate (x/1.001 or not) from the label rate: the difference is 1000 ppm. Audio and
  crystal clocks are within about ±50 ppm. After one second at 30 fps, the frame-period error separates the two by
  about 48 samples at 48 kHz.
- **Midnight:** timecode repeats every 24 h [T14]. A chase must unwrap 23:59:59:29 → 00:00:00:00 as +1 frame, not as a
  jump *(inference)*.

## 5. Chase behaviour in the products already in the truck

| Product | Inputs | Lock and follow | Loss (freewheel) | Offsets | Jumps, stops, reverse | TC-triggered vs locked |
|---|---|---|---|---|---|---|
| **WATCHOUT 7** (LTC Bridge) [T1][T1a][T1b] | LTC over WASAPI, WASAPI Exclusive, ASIO; one channel (1-based); one bridge per node, each with its own timelines | Play/pause on state change; **a sync message every 1 s** to `/v0/timelinesync` with LTC time + per-timeline offset, a wall-clock anchor for interpolation, and "**time dilation**… the median ratio of frame time to system time" | "silent or invalid for longer than ~100 ms, the led timelines are paused" | Per-timeline signed ms, `ltc_time + offset` | "resyncing when a new sequence is detected". Six error counters: bi-phase mark, missing, duplicate, discontinuity, invalid frame, bad BCD | Locked only. A forum user's workaround for glitches was a TC-locked timeline that triggers non-locked timelines [T1b]. In v7.5.3 a timeline cannot be offset, so cues sit at 1:03:36 on a 1:10:00 timeline [T1b] |
| **Pixera 26.3** [T5][T5a][T5b] | USB SMPTE interface (Director only), LTC via audio or Dante, Art-Net TC, MTC (native since 26.3 inter 46). One TC input at a time | **Correct Drift:** None (start once), Standard (continuously compare and correct), Adaptive (larger pitch ranges, DJ sets) | **Timeout Frames** (example: 4) then **No-Input Behavior**; "**Ensure Device Time at Pause**" jumps back to the last TC on loss | General Offset (subtracted by default); "**Pause Until TC Reaches Offset**" | **TC Jump Fade Threshold**: below it drift correction, above it a jump; single dropped frames are bridged. **Jump Fade Duration**; **Jump Preload Delay** keeps the old video while the new position preloads; **Fade at TC Start** | **Hot Regions**: time ranges where the timeline follows TC; outside them it plays normally. While on TC, "stop cues and pause cues have no effect" |
| **disguise** [T2][T3][T4][T4a] | LTC (audioline with gain), MTC, MIDI beat clock, tap tempo, system time; one TC transport at a time | **Engage/disengage**, persistent across restarts; "disengaged, all external control signals are ignored… edit while rehearsals are taking place" | **On clock lost**: stop (rehearsal) or keep playing ("the show must go on") | "Adjust (sec)" on the transport; **TC Adjust** per track, for front-of-house delay | "Timecode can change track": searches TC tags across the set list and jumps; play mode normal, to end of section (then "ignore timecode" until TC restarts or jumps), or loop | **Mode Locked** = jam sync, jumps with TC. **Mode Normal** = fired at the TC tag, then free-running. Advice: leave bars of pre-roll before a tag |
| **QLab 5** [T8][T9][T10] | LTC (device and channel) or MTC, per cue list | Cues fire at their TC triggers; "Capture" takes the incoming frame | **On Stop**: pause, stop or keep running; **Freewheel 0–2 s** | Enter triggers as TC or real time | **On Start** has five lookback rules: none / last minute / last hour / X seconds / all. A cue whose duration ends before the lookback is not started | Triggered (QLab is a cue system). **Timecode cues** generate any number of LTC/MTC streams |
| **Resolume Arena** [T7] | LTC on 2 inputs (SMPTE 1/2), each with its own rate | Clip playhead follows TC (skip, faster, slower) | Not documented | Per-clip **offset** (start TC); **delay compensation in frames**, can be negative | A 1 s jump in the playhead means the wrong rate | Locked; the clip must already be active ("the clip trigger itself is not sent via SMPTE"); **not available on clips with audio** |
| **Millumin** [T11] | LTC (device and channel), MTC, NTC (its own experimental network TC, open source) | Timeline bound to TC; after a jump in the sender, "it takes a few milliseconds… to be resynchronized" | **Free wheel**: seconds before the media stops | **Delay** per machine; a **test card** (blinking circle and beep) for lining machines up by eye | Follows jumps | Generates MTC/LTC/NTC from "timecode media" with loop modes (infinite, once-freeze, once-stop, auto-follow) |
| **Stagewatch 2.7** (local) | LTC in from an audio input (winmm), 24/25/30 auto, DF bit | Timer starts when code rolls (3 consecutive frames) and pauses when it stops; with an end TC, remaining = end − TC (corrected beyond 0.15 s) | "Rolling" ends after 4 frames + 0.1 s without code; the reading is fresh for 1 s | — | Forward-only TC triggers (`Crossed`: under 2 s forward, not a jump) | Also LTC **out** (time of day or the countdown), 24/25/30 |

What no product documents (*inference*): a measured residual or jitter figure; how a jump is told apart from noise
beyond a threshold; reverse play; and how chase behaves across a redundant pair of machines, beyond WATCHOUT's
"Target Director" and Pixera's Director/Client wiring.

## 6. Stagewatch and Patterns: what PlayMap can chase over the Show Bus

- **Stagewatch publishes:** `ltc` (text), `ltcRunning`, `ltcChase` in its snapshot (`src/Timer.Core/SnapshotJson.cs`
  l.76–78). The view model republishes only when the rolling state or the whole-second text changes ("frames would
  republish 25×/s", `MainViewModel.Ltc.cs`). Its LTC out runs at 24/25/30 as time of day or the countdown
  (`Ltc.cs`). Its Companion module has `ltc_chase`, `ltc_end`, the feedbacks `ltc_running` and `ltc_chase`, and a
  preset key (`companion-module/src/*.ts`).
- **So:** Stagewatch's bus data can show "the room's timecode is 01:02:03 and rolling" in PlayMap's context, and can
  start or arm with about ±1 s accuracy. It cannot lock content to a frame. For frame accuracy, Stagewatch's LTC out
  goes into PlayMap's LTC in on a cable. Or (Show Bus 1.3 proposal) Stagewatch publishes a `tc` record
  `{frames, rate, df, atUnixMs, rolling, seq}` once a second, which PlayMap fits like any other source (*design*).
- **Patterns:** has no timecode in or out today (`docs/PRO-FEATURES.md` l.92 lists LTC/MTC in as future work; `Help.cs`
  l.918 says "no MSC or timecode"). PlayMap's TC chase is therefore new to the ecosystem. The Patterns desk can drive
  PlayMap's TIMECODE verbs over its Lines device.

## 7. PlayMap today (local code, `c12b186`)

- `FrameRate` (rational) and `Timecode.Format/TryParse` (DF for 29.97 and 59.94 via `fps/15` dropped labels) in
  `src/PlayMap.Core/Time/Timecode.cs`. There is no LTC, MTC or Art-Net code anywhere in `src/`.
- `TimelineAnchor(MediaNs, LeaderNs, Rate, Running)` and `FrameSelector.PresentLeaderNs(clock, vblank, latency)`: the
  frame is a function of leader time at the predicted vblank plus display latency.
- `PlaybackLanes.Adopt` takes a leader's transport exactly; `Schedule` pre-anchors the next item so that a switch lands
  on the exact frame on every node. The control lane ticks every 10 ms.
- `NetworkClock` maps local monotonic time to leader time through `ClockOffsetEstimator` (min-RTT plus drift).
  `AnchorPublished` exists, but no push to followers yet (CARRYOVER). Starting to follow steps the clock (CARRYOVER).
- `PlayMap.Media/Audio/AudioOut.cs` already loads OpenAL Soft (Silk.NET.OpenAL 2.23.0, native runtime package) with a
  **loopback** mode for tests. `Silk.NET.OpenAL.Extensions.EXT` 2.23.0 (MIT, NuGet, checked 13 Oct 2026) exposes
  `ALC_EXT_CAPTURE` (`CaptureOpenDevice`, `CaptureStart`, `CaptureSamples`, `AudioCapture<T>.AvailableSamples`). OpenAL
  capture is mono or stereo only, so channel 7 of a 16-channel interface needs WASAPI/ASIO (Windows) or ALSA/PipeWire
  (*inference*). NAudio 3.1.0 is MIT (NuGet nuspec, checked 13 Oct 2026).

## 8. Design implications for PlayMap

1. **Timecode becomes a fitted anchor, not a playhead.** Every source yields observations `(local mono ns, TC frame,
   sub-frame phase)`: one per LTC frame end, four per MTC frame, one per Art-Net packet. A sliding robust linear fit
   gives `tc(t) = TC₀ + r·(t − t₀)` with a residual. Mapped to leader time through `NetworkClock`, that is exactly a
   `TimelineAnchor` whose `Rate` is `r` (varispeed and source drift included). It gives frame identity across nodes,
   no per-machine delay knob (Millumin), and no 1 Hz sync messages (WATCHOUT).
2. **LTC decoder, ours and pure C#** (Core, no device): DC block, envelope, Schmitt trigger with sub-sample crossing
   interpolation, adaptive period, forward and reverse sync words, BCD and parity checks, WATCHOUT's six error
   counters, level in dBFS with WATCHOUT's thresholds, and a stamp of the sync word's end. Test it against our own
   encoder with varispeed ±10 %, levels −40…0 dBFS, noise, hum, inverted polarity, reverse, dropouts, 44.1/48/96 kHz.
3. **Three-fact rate detection** (label rate, DF flag, real rate by period), shown as WATCHOUT shows it: detected,
   measured, speed deviation. A configured format overrides it.
4. **Explicit chase states with defined outcomes:** idle → acquiring → locked → freewheel → (hold | stop | continue)
   on loss; parked (locate); jump (debounced, optionally with preload so that the switch lands on a decoded frame,
   like Pixera's preload delay, using PlayMap's existing exact-frame switch); reverse (hold by default). Disengage for
   rehearsal (disguise). "Return to last TC on pause" (Pixera). Every state reaches STATE, the context and the desk.
5. **TC-locked playlists = an implied timeline:** an item with a TC in-point anchors itself, and the items after it
   follow end to end. Regions are looked up by TC ("timecode can change track", disguise; hot regions, Pixera). A
   per-playlist mode Locked or Triggered (disguise Locked/Normal, QLab triggers) with a lookback rule (QLab).
   One signed delay with its sign in words: "+40 ms = picture later".
6. **Inputs:** LTC (audio capture seam: OpenAL Soft now, WASAPI/ASIO multichannel later, plus a WAV/FFmpeg file
   source), MTC (MIDI seam from the control package), Art-Net TC (UDP 6454 with OpCode dispatch shared with Art-Net
   DMX), an internal generator for rehearsal, a peer node relay, and Stagewatch (coarse: display and arming only).
7. **Outputs:** LTC paced by the audio device clock with fractional bit phase and a 40 µs rise; MTC with quarter
   frames; Art-Net TC. Each can be fed from a source, a playlist's position, or time of day (QLab, Millumin,
   Stagewatch).
8. **Multi-node:** one chase writer per playlist (the sync group's leader). A TC reader on another node relays its fit
   in leader nanoseconds. Backup sources are ranked and switched without a jump when they agree within a frame.
   Followers get anchors, not timecode, so they cannot disagree.
9. **Show Bus 1.3 proposal:** a `tc` record from Stagewatch, and PlayMap's own TC state in its context, so the Patterns
   Eye shows "chasing LTC 01:02:03:04, locked, ±40 µs".

## 9. Sources

| Tag | What | URL | Date | Status |
|---|---|---|---|---|
| T1 | WATCHOUT 7 User Guide: LTC Bridge | https://docs.dataton.com/guide/watchout/external-control/ltc-bridge.html | undated; read 13 Oct 2026 | V |
| T1a | WATCHOUT 7 User Guide (older page): LTC Bridge | https://docs.dataton.com/watchout-7-new/watchout/external-control/ltc-bridge.html | undated | V-h |
| T1b | Dataton forum, "Using LTC with Watchout 7.5.3" | https://forum.dataton.com/topic/81332-using-ltc-with-watchout-753/ | 13 Aug 2025 | V-h |
| T2 | disguise User Guide: LTC Setup | https://help.disguise.one/designer/timeline-tracks-transports/timecode/setup-ltc | last updated 11 Jun 2026 | V |
| T3 | disguise User Guide: Transport Manager | https://help.disguise.one/designer/timeline-tracks-transports/transports-manager | last updated 25 Jun 2026 | V |
| T4 | disguise User Guide: Triggering Cues with Timecode | https://help.disguise.one/designer/timeline-tracks-transports/timecode/triggering-playback-with-timecode | undated | V-h |
| T4a | disguise User Guide: Timecode Overview | https://help.disguise.one/designer/timeline-tracks-transports/timecode/timecode-overview | undated | V-h |
| T5 | PIXERA help: Timecode PIXERA (SMPTE settings) | https://help.pixera.one/timecode/1216391-smpte-timecode | "PIXERA 26.3 inter 46, 27 August 2026" | V |
| T5a | PIXERA help: MIDI Timecode | https://help.pixera.one/timecode/midi-timecode | undated (mentions 26.3 inter 46) | V-h |
| T5b | PIXERA help: Timecode LTC via Audio | https://help.pixera.one/timecode/smpte-audio-in-and-smpte-hot-regions | undated | V-h |
| T7 | Resolume support: SMPTE | https://resolume.com/support/en/smpte | undated | V |
| T8 | QLab 5 docs: Cue Lists (Timecode tab) | https://qlab.app/docs/v5/fundamentals/cue-lists/ | undated | V (page text cut off mid-freewheel example) |
| T9 | QLab 5 docs: Inspector (Timecode trigger) | https://qlab.app/docs/v5/fundamentals/inspector/ | undated | V-h |
| T10 | QLab 5 docs: Timecode Cues | https://qlab.app/docs/v5/networking/timecode-cues/ | undated | V-h |
| T11 | Millumin help: Timecodes | https://help.millumin.com/docs/connect/timecodes/ | undated | V-h |
| T12 | Art-Net 4 specification (Artistic Licence), ArtTimeCode p.54 | https://art-net.org.uk/downloads/art-net.pdf | © 1998–2025, latest revision DP | V (read the ArtTimeCode section, revision notes and port text) |
| T12b | artnet-go `timecode.go`; clj-artnet `spec.clj` (19-byte layout) | https://git.jwetzell.com/jwetzell/artnet-go/raw/branch/main/timecode.go ; https://github.com/robinlahtinen/clj-artnet/blob/main/src/clj_artnet/impl/protocol/codec/spec.clj | undated | V-h |
| T13 | SMPTE ST 12-1:2014 Time and Control Code (PDF hosted by Drastic) | https://www.drastic.tv/images/documentation/st0012-1-2014.pdf | 2014 | V-h (extracts: drop-frame rule, flag-bit table, frame pairs, reference datum) |
| T13a | SMPTE ST 12-1:2008 Amendment 1:2013 (48 fps) | https://exa.ai/library/publication/9frl0jq32wx | 29 Aug 2013 | V-h |
| T13b | SMPTE ST 12-2:2014 (ATC; field-mark caveat) | https://www.drastic.tv/images/documentation/st0012-2-2014.pdf | 2014 | V-h |
| T14 | Wikipedia: MIDI timecode | https://en.wikipedia.org/wiki/MIDI_timecode | revision of 11 Feb 2026 | V |
| T15 | Wikipedia: Linear timecode | https://en.wikipedia.org/wiki/Linear_timecode | revision of 26 Sep 2026 | V |
| T16 | x42/libltc `src/decoder.c`, `src/ltc.h`, `src/ltc.c`, `src/encoder.c` (LGPL-3.0-or-later; method only) | https://raw.githubusercontent.com/x42/libltc/master/src/decoder.c (and `ltc.h`, `ltc.c`, `encoder.c`) | master as fetched 13 Oct 2026 | V |
| T17 | SMPTE blog: Understanding Standards: Time Code | https://www.smpte.org/blog/understanding-standards-time-code | 26 Feb 2025 | V-h |
| T18 | NuGet: Silk.NET.OpenAL.Extensions.EXT 2.23.0 (MIT; `ALC_EXT_CAPTURE`), NAudio 3.1.0 (MIT), PortAudioSharp2 1.0.6 | https://api.nuget.org/v3-flatcontainer/silk.net.openal.extensions.ext/index.json (and `naudio`, `portaudiosharp2`) | read 13 Oct 2026 | V |

Not opened: the SMPTE ST 12-1 full text beyond the extracts (paywalled; the Drastic PDF was read only as extracts), the
MIDI 1.0 detailed specification for MTC (Wikipedia used instead), and Millumin's NTC source code.
