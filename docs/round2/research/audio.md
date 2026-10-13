# Research: audio reactivity and modulation — capture, analysis, tempo, Link and its alternatives, modulation UX

Topic key `audio`. Read on 13 October 2026. Status tags: **V** = I opened the primary source this session and the
fact is as stated; **V-s** = verified from a reliable secondary source (named); **U** = unverified (not opened, or
inferred). Sources are listed in §10 with tags [A1]…; local code is cited by path and commit.

What this report serves: an audio input and analyser service publishing modulation sources (bands, levels, onsets,
beat phase, BPM) that any parameter can bind to, deterministic across nodes where possible. Sibling packages that
already fixed shared seams (timecode, effects, control, companion) are summarised in §9 so the design can adopt them.

---

## 1. Capture: getting sound into a .NET 10 process on each platform

### 1.1 Windows — WASAPI

- **Endpoint loopback** captures "the audio stream that is being played by a rendering endpoint device": open the
  render endpoint (`GetDefaultAudioEndpoint(eRender…)`) and initialise a capture stream with
  `AUDCLNT_STREAMFLAGS_LOOPBACK`; then `IAudioClient::GetService` → `IAudioCaptureClient` [A1] **V**.
  - Loopback is **shared mode only** ("Exclusive-mode streams cannot operate in loopback mode") [A1] **V**.
  - Event-driven loopback works from **Windows 10 1703**; before that it needed a render-stream workaround [A1] **V**.
  - Works whether or not the hardware has a "Stereo Mix" device; DRM-protected streams are not delivered to
    untrusted loopback [A1] **V**.
- **Process loopback** (capture one process's render streams, or everything *except* one process tree):
  `ActivateAudioInterfaceAsync` with `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS { DWORD TargetProcessId;
  PROCESS_LOOPBACK_MODE ProcessLoopbackMode; }`, minimum client **Windows 10 Build 20348** [A2] **V**.
  - *Inference (U):* build 20348 is the Server 2022 line; Windows 11 (22000+) qualifies, Windows 10 21H2/22H2
    (1904x) does not. So "everything except PlayMap's own playback" exists on Windows 11 only; Windows 10 falls back
    to endpoint loopback.
- **Latency.** In Windows 10+ the shared capture path's engine latency is "~0 ms" and the driver may report buffers
  below the old fixed 10 ms (e.g. 5, 3, 1 ms) for apps using the low-latency WASAPI/AudioGraph APIs; exclusive mode
  and ASIO bypass the engine [A3] **V**. A shared-mode default period of 10 ms is the common case (*U*, typical).
- **Library:** NAudio **3.1.0** and NAudio.Wasapi **3.1.0**, MIT (`<license type="expression">MIT`), targets
  `net9.0` / `net9.0-windows10.0.19041` (nuspec, read 13 Oct 2026) [A4] **V**. Patterns already captures WASAPI
  loopback and inputs through NAudio 2.2.1 (`src/Patterns.App/Services/AudioAnalyserService.cs`, §7) **V**.

### 1.2 FFmpeg libavdevice (already loaded by PlayMap.Media)

- Input devices in FFmpeg master's `doc/indevs.texi`: `alsa`, `android_camera`, `avfoundation`, `decklink`, `dshow`,
  `fbdev`, `gdigrab`, `iec61883`, `jack`, `kmsgrab`, `lavfi`, `libcdio`, `libdc1394`, **`openal`**, `oss`,
  **`pulse`**, `sndio`, `v4l2`, `vfwcap`, `x11grab` [A5] **V**. **There is no WASAPI and no PipeWire-native input**
  (PipeWire is reached through its Pulse server, *U*).
- `dshow` audio: `audio_buffer_size` "in milliseconds… Defaults to the device's default buffer size (typically some
  multiple of 500ms)" [A5] **V** — unusable for reactive visuals unless set (≈ 10–20 ms), and dshow has no loopback.
- `pulse`: `fragment_size` "by default… 50 ms amount of data" [A5] **V**.
- FFmpeg 9.0's Changelog (released) adds, among others, an "ONNX Runtime DNN backend" — nothing for audio capture
  [A6] **V**.
- Conclusion: avdevice is fine for Linux (`alsa`, `pulse`) and for *file-as-input* rehearsal, but not the Windows
  default.

### 1.3 OpenAL Soft capture

- PlayMap already loads OpenAL Soft (Silk.NET.OpenAL 2.23.0 + `Silk.NET.OpenAL.Soft.Native` 1.23.1) for `AudioOut`
  (`src/PlayMap.Media/Audio/AudioOut.cs`, loopback mode for tests) **V** (code, `c12b186`).
- `Silk.NET.OpenAL.Extensions.EXT` 2.23.0 (MIT) exposes `ALC_EXT_CAPTURE` (`CaptureOpenDevice`, `CaptureStart`,
  `CaptureSamples`) — confirmed by the timecode research (r2/research/timecode.md §7, T18) **V-s**.
- OpenAL capture is mono or stereo only (timecode research) **V-s**; OpenAL Soft is LGPL and is loaded at run time
  (no code copied).

### 1.4 Other .NET options (all read from NuGet nuspecs, 13 Oct 2026) [A4]

| Package | Version | Licence | Notes |
|---|---|---|---|
| NAudio / NAudio.Wasapi | 3.1.0 | MIT **V** | WASAPI capture, loopback, process loopback (Windows) |
| ppy.SDL3-CS | 2026.1002.1 | MIT **V** (SDL3 itself zlib, *U*) | SDL3 recording devices on WASAPI/PipeWire/ALSA/CoreAudio (*U*); no loopback |
| PortAudioSharp2 | 1.0.6 | not stated in nuspec (**U**) | PortAudio wrapper |
| miniaudio.net | 0.2.0 | MIT **V** | young wrapper; miniaudio itself is public domain / MIT-0 [A7] **V** |
| SoundFlow | 1.4.1 | licence file, not checked (**U**) | — |

### 1.5 What this means

One device should be opened once and fanned out: the LTC reader (timecode WP) and the analyser may listen to
different channels of the same interface. The timecode WP has already specified the shared seam `IAudioInputs` /
`IAudioCapture` / `IAudioCaptureSink.Samples(interleaved, channels, rate, firstSampleIndex, readMonoNs)` and a
`SampleClockFit` (lower-envelope least squares of sample index against the monotonic clock) (§9) **V** (local
spec). Link's own README gives the same advice: when the audio callback has no host timestamp, "filter it based on
the provided sample time" — a linear regression between system time and sample time (`HostTimeFilter`) [A8] **V**.

---

## 2. Analysis practice

### 2.1 Windows, FFT sizes and latency

- TouchDesigner's Audio Spectrum CHOP: FFT sizes **64–16384** (default 8192); "The more samples, the more accurate
  the spectrum but the more it doesn't represent the most recent sound"; their worked example: 2048 samples at
  44.1 kHz ≈ 50 ms "is plenty"; logarithmic scaling "Each octave is represented with the same number of samples"
  [A9] **V**.
- Patterns: **1024-sample Hann**, radix-2 FFT, half-window hop (≈ 86 analyses a second at 44.1 kHz) [§7] **V**.
- *Computed:* at 48 kHz a 1024 window is 21.3 ms with 46.9 Hz bins; a 30–44 Hz band holds less than one bin. Low
  frequencies need either a long window (latency) or a filterbank. A 2nd-order band-pass with Q ≈ 2.53 has a group
  delay ≈ 2Q/(2πf₀): **≈ 22 ms at 36 Hz, 10 ms at 80 Hz, 3 ms at 260 Hz** (computed for 16 log bands 30 Hz–16 kHz,
  ratio 1.4806 = 0.566 octave per band). Frequency resolution at low frequency costs latency whichever way it is
  computed (U, physics).

### 2.2 Bands, levels, smoothing (what the field exposes)

| Tool | Bands | Smoothing / gain |
|---|---|---|
| Patterns | Level (RMS × 2.5) + Low 20–250, Mid 250–2000, High 2000–8000 Hz (√power × 1.5), clamped 0–1 | `LevelSmoother`: attack **30 ms**, release **250 ms**; levels older than **1 s** read as silence [§7] **V** |
| Resolume 7 | **L, M, H** buttons or a custom frequency range (in/out points on the spectrum) | **Gain**, **Fall** (how fast it falls from a peak); direction modes `>`, `<`, `-`, `+` (the last two drive the *speed* of the parameter) [A10] **V** |
| MadMapper 6 | amplitude, bass, medium, treble | master input level, "reactive range"; ISF `audioFFT` with `SIZE`, `ATTACK`, `DECAY`, `RELEASE` in the JSON header [A11][A12] **V** |
| Notch 2026.2 | **16 bands**, "Each increasing band corresponds to a doubling of the band's center frequency"; per-band EQ gains | Spikiness (rate limit), Smoothness, Offset; Sound FFT Modifier: rectangular frequency × amplitude **regions** → triggers with Attack/Decay and shape powers; channel Stereo/Left/Right/**Mid/Side** [A13][A14] **V** |
| TouchDesigner palette `audioAnalysis` | Low/Mid/High + **Kick**, **Snare**, **Rhythm**, spectral centroid, slow/fast partial density | per band Threshold, Smooth, Gain, Add [A15] **V** |
| VDMX | any number of **filters** (centre frequency + range), each a data source | gain; per-filter smoothing [A16] **V** |

*Field consensus:* three named bands plus user-defined ranges, a gain, an asymmetric attack/release (or "fall"),
and a few percussive detectors (kick/snare). Notch alone ships analysis as a **file written at import**: "As an
audio file is imported into Notch… a separate .fft file will be created… used to drive Notch's audio reactive
tools" [A17] **V**.

### 2.3 Onsets

- aubio's onset detection functions (from `src/spectral/specdesc.h`, read only — GPLv3, no code used): `energy`,
  `hfc` (Masri), `complex` (Duxbury, Davies, Sandler), `phase` (Bello et al.), `wphase` and `specflux` (Dixon,
  "Onset Detection Revisited", DAFx 2006), `specdiff`, `kl` (Hainsworth & Macleod), `mkl` [A18] **V**.
- Method choice (from the literature, **U**: papers not reopened this session): log-magnitude spectral flux with a
  maximum filter across frequency ("SuperFlux", Böck & Widmer, DAFx 2013) is robust to vibrato; causal (online)
  peak picking with a moving mean/median threshold, a short pre-max window and a minimum inter-onset gap (~30 ms)
  costs zero or one hop of look-ahead (Böck, Krebs, Schedl, ISMIR 2012).

### 2.4 Tempo and beat tracking

| Project | What | Licence | Use for PlayMap |
|---|---|---|---|
| **BTrack** 1.0.7 (31 Dec 2025) | "a causal beat tracking algorithm intended for real-time use"; method in Stark, Davies & Plumbley, DAFx-09 and Stark's thesis | **GPLv3** [A19] **V** | Ideas only (from the papers): onset detection function → tempo by comb-filtered autocorrelation → cumulative score with a log-Gaussian transition window → beat prediction |
| **aubio** | onset, tempo, beat ("aubiotrack") | **GPLv3** [A18] **V** | none (read only) |
| **madmom** | onsets, beats, downbeats (RNN+DBN) | code **BSD-2**; **models CC BY-NC-SA 4.0** [A20] **V** | models unusable commercially |
| **Beat This!** (ISMIR 2024) | beats + downbeats "without DBN postprocessing"; `final*` ≈ 78 MB, **`small*` ≈ 8.1 MB** per model; PyTorch checkpoints | "The code and the published model weights are released under the **MIT** license"; some training data under limited licences, "up to the user to assess" [A21] **V** | Offline (import-time) beat/downbeat grid, if exported to ONNX and run on ONNX Runtime 1.31 (already in PlayMap.Vision). Not causal. Export not done here (needs PyTorch) |
| **librosa** | `beat_track` (Ellis 2007 dynamic programming) | **ISC** [A22] **V** | reference for the offline DP method (U: method from the paper) |
| Essentia | — | AGPL (U) | none |

Practice from the tools: VDMX's Clock plugin has a **"BPM Detection"** mode ("an extremely accurate beat tracker")
beside Internal (tap, `/2`, `*2`, up/down) and MIDI clock modes [A16] **V**. Resolume's BPM page documents
**Tap**, **Resync** ("everything… set to BPM Sync will jump back to the first beat of the first bar of the first
phrase"), **+/−**, **Nudge Up/Down** (temporary speed change while held), a metronome to headphones, MIDI clock
and Ableton Link; 4 beats = a bar, 8 bars = a phrase; it does not document audio BPM detection [A23] **V**.
MadMapper's master has Global BPM with **TAP**, **Resync**, `BPM_Source`, `Ableton_Link` (OSC addresses
`/master/Global_BPM/TAP`, `/Resync`, `/BPM_Source`, `/Ableton_Link`, `/BPM`) and "Beat Detection" in its project
settings [A11][A24] **V**.

### 2.5 Tap tempo, bars and phase

- TouchDesigner's Beat CHOP: ramps every ¼…32 beats or any length (12-beat bars, 3.33 beats), **Multiples** with
  **Shift Step** (staggered channels: "eight objects delayed by one beat each"), Count, Count+Ramp, Bar, Beat,
  Sixteenths, Ramp Bar, Ramp Beat, BPM; **Locked to Timeline** (scrubbing updates output; "The output will be the
  same for each pass") vs **Local Sequential**; Reset Bar Value; a single global beat source [A25] **V**.
- Notch's BPM Modifier: BPM, Num Beats, offsets, Attack/Decay/Pulse Sharpness; **Time Mode "Locked": "the same
  time will always yield the same numerical value"** [A26] **V** — the determinism PlayMap's doctrine requires.

### 2.6 Smoothing and determinism

PlayMap's doctrine forbids integrating frame deltas. A one-pole filter evaluated per *render frame* gives different
values at different frame rates. Two deterministic forms exist (U, design reasoning): (a) evaluate filters on a
**fixed tick grid in leader time** from shared input frames (every node runs the same sequence); (b) closed forms
of a bounded history window. The particle simulator already does (a): "a fixed step of 1/120 s on the leader
clock's grid… identically-seeded sims stay bit-exact whatever their frame timing"
(`src/PlayMap.Core/Content/ParticleSim.cs`) **V**. The effects WP's draft smooths live sources per render frame and
badges them "live" (§9) **V** (local spec).

---

## 3. Latency budgets for visuals

- **ITU-R BT.1359-1 (1998):** detectability thresholds about **+45 ms** (sound before picture) to **−125 ms**
  (sound after picture); acceptability about **+90 / −185 ms** [A27] **V-s** (TV Technology, 2007 and 2012; the ITU
  page [A28] was found but the text not opened).
- **EBU R37-2007:** **+40 ms / −60 ms** at the output feeding the transmitter; **ATSC IS-191:** +15 / −45 ms
  [A27] **V-s**.
- Audio-reactive visuals are always "sound first": the picture can only react after the sound was analysed. So the
  relevant number is the **+45 ms detectability / +90 ms acceptability** of sound leading picture (U: the thresholds
  were measured for lip sync, not music visuals).
- **Acoustic delay helps** (computed): sound travels 343 m/s, so a listener **10 / 17 / 30 m** from the PA hears it
  **29 / 50 / 88 ms** after it leaves the speaker, while the projector's light is instantaneous. A visual 60 ms
  behind the electrical signal is ~10 ms behind the sound for a listener at 17 m.
- **The pipeline (estimates to measure in r2/bench):** capture buffer 3–20 ms (WASAPI shared, §1.1) + analysis
  group delay (½ window ≈ 10.7 ms at 1024/48 kHz, or the filterbank's 3–22 ms) + hop wait ≤ 5 ms + network to a
  follower 1–3 ms + PlayMap's light lead (time to the next vblank 0–16.7 ms + the output's display latency, which
  the render host already adds: `leaderNs = engine.LeaderNowNs + lead + DisplayLatencyNs`,
  `src/PlayMap.Engine/Rendering/RenderHost.cs`) ≈ **40–100 ms** total. Reactive (level/onset) visuals cannot beat
  this; **beat-locked** visuals can, because a tempo model predicts the beat: Link's whole design is a beat/time
  mapping ("what beat value corresponds to a given moment in time") [A29] **V**, and Pro DJ Link beat packets carry
  "the number of milliseconds in which the very next beat will arrive" [A30] **V**.
- Link's latency guidance: add the audio system's **output latency** to system time before calling Link, so devices
  align "the moment at which their signals hit the speaker" [A8] **V**. The visual analogue is PlayMap's existing
  "draw for the instant the light leaves the lens".

---

## 4. Ableton Link

### 4.1 What it is

- Synchronises **tempo**, **beat alignment**, **phase** (with a per-app **quantum** in beats) and, since version 3,
  **start/stop** across apps on a LAN; no master; "each participant chooses to adopt the last tempo value that
  they've seen proposed" [A29] **V**.
- A **session state** = timeline `(beat, time, tempo)` + start/stop state, captured and committed; real-time-safe
  capture/commit pair for the audio thread and a blocking pair for other threads [A29] **V**.
- **Quantized launch** is "strongly recommend[ed]": start at the next quantum boundary [A29] **V**.
- **Link Audio** (new): named audio **channels** published by a **sink** and received by **sources**, "interleaved
  16-bit signed integer" buffers aligned to the Link timeline; sinks send only when a source exists [A29][A8] **V**.
- **Test plan** (TEMPO-1…5, BEATTIME-1/2, STARTSTOPSTATE-1/2): e.g. "Opening an app with Link enabled should not
  change the tempo of an existing Link session"; tempo range 20–999 BPM ("If App does not support the full range…
  switch to a multiple"); enabling Link with no session must not jump beat time [A31] **V**.

### 4.2 Licence

- "Ableton Link is dual licensed under **GPLv2+** and a **proprietary** license. If you would like to incorporate Link
  into a proprietary software application, please contact link-devs@ableton.com" [A8][A32] **V**.
- The repository ships "Ableton Link Guidelines.pdf" (branding/integration guidelines; not opened, **U**) [A32].
- **Implementations seen and their licences:**
  - `ableton-link-rs` (Rust): "Licensed under the GNU General Public License v3.0, consistent with the original";
    it tracks the C++ upstream as a vendored submodule ("port watermark") — a **port, therefore a derivative** [A33]
    **V**.
  - Go bindings (`abletonlink-go` GPL v2+, `essaim-dev/al` GPLv3), Carabiner (GPL v2) — all link the C++ library
    [A33][A34] **V**.
  - **`link-bpm`** (Compeller, Rust): "**Unofficial clean-room** crate for reading the BPM advertised by an Ableton
    Link session": passively joins multicast **224.76.78.75:20808**, parses the session tempo from the timeline
    payload, never announces itself; "built from observed network traffic and contains no Ableton source code";
    dual **MIT / Apache-2.0**; "'Ableton' and 'Link' are trademarks… used here only to describe protocol
    compatibility" [A35] **V**.
- **May an MIT app implement the protocol independently?** (legal reasoning, **U**, not legal advice): copyright
  covers the C++ code, not the protocol's behaviour; a clean-room implementation from observed traffic (as
  `link-bpm` did for the tempo) is generally possible, but (1) a full participant must also implement the
  measurement exchange and timeline negotiation, which are undocumented, (2) reading or porting the GPL source
  taints the work (the Rust port is GPL for that reason), and (3) the name and logo are trademarks governed by
  Ableton's guidelines. **Safe routes for an MIT exe:** (a) talk to a separate, user-installed GPL program over a
  socket (Carabiner — "mere aggregation"/separate programs, **U** as legal conclusion); (b) a passive tempo-only
  clean-room listener; (c) a commercial licence from Ableton for in-process Link.

### 4.3 Carabiner (the socket bridge) [A34] **V**

- "A loose connector" embedding Link and exposing it over **TCP 127.0.0.1:17000** (`--port` to change); licence
  **GPL v2**; it "does not support the full power and elegance of Link"; **must run on the same host** as the client
  because timestamps are Link's monotonic clock in **microseconds**.
- Line commands and replies (edn-style maps):
  - `status` → `status { :peers 0 :bpm 120.000000 :start 73743731220 :beat 597.737570 }` (`:playing` when
    start/stop sync is on; a status is also sent on connect and on change);
  - `bpm 140.0` (bad → `bad-bpm`);
  - `beat-at-time <µs> <quantum>` → `{ :when … :quantum 4.000000 :beat 5.250000 }`;
  - `phase-at-time <µs> <quantum>`; `time-at-beat <beat> <quantum>`;
  - `force-beat-at-time <beat> <µs> <quantum>` (abrupt; for an external source that cannot negotiate);
  - **`request-beat-at-time <beat> <µs> <quantum>`** (graceful: lands at the next time with the same phase —
    quantized launch);
  - `enable-start-stop-sync` / `disable-start-stop-sync`, `start-playing <µs>`, `stop-playing <µs>`, `version`.

---

## 5. Alternatives to Link

### 5.1 MIDI clock

- 24 pulses per quarter note: clock `F8`, start `FA`, continue `FB`, stop `FC` (r2/research/control.md §… from the
  MIDI 1.0 summary) **V-s**; Song Position Pointer `F2` in sixteenths (MIDI 1.0, **U** not reopened).
- The control WP routes `F8 FA FB FC` (and SPP) to `ITimecodeInput.MidiClock(byte status, string port, long monoNs)`
  (§9) **V** (local spec). Launchpad X flashes/pulses its LEDs on MIDI clock (control research [C22]) **V-s**.
- Tempo from MIDI clock is a robust line fit of pulse times (jittery USB/OS stamps); the timecode WP's lower-envelope
  fit is the same tool (§9).

### 5.2 Pioneer/AlphaTheta Pro DJ Link (reverse engineered, unsanctioned)

- Deep Symmetry's analysis: packets start with ten bytes `51 73 70 74 31 57 6d 4a 4f 4c` ("Qspt1WmJOL") then a kind
  byte; **UDP 50000** device announce/numbering, **50001** beat sync (kinds `28` Beat, `0b` Absolute Position,
  `26/27` master handoff, `2a` sync control), **50002** status (`0a` CDJ status, `29` mixer status) [A30] **V**.
- **Beat packets** are `0x60` bytes on 50001, sent on each beat (CDJs only while playing rekordbox-analysed tracks;
  the mixer always): ms to the next beat at `0x24–0x27`, to the second beat `0x28`, to the next bar `0x2c`, to the
  4th beat `0x30`, next-but-one bar `0x34`, 8th beat `0x38`; **BPM×100 at `0x5a–0x5b`**; **beat within bar 1–4 at
  `0x5c`** (meaningful from the master player) [A30][A36] **V**.
- **CDJ-3000 Absolute Position** packets every **30 ms** on 50001, with pitch ×64… and BPM ×10 [A30] **V**.
- dysentery is **EPL 1.0**, "in no way a sanctioned implementation"; beat-link (Java), prolink-connect (TypeScript)
  [A37][A38] **V**. A passive listener written from the published packet analysis needs no code from them.

### 5.3 OSC and Show Bus

- No standard OSC tempo message exists (U). MadMapper's `/master/Global_BPM/BPM`, `/TAP`, `/Resync` [A24] **V** and
  PlayMap's own verb-mirroring OSC space (control WP: `/playmap/<verb words>`) cover tap/resync/set from any
  OSC sender (§9).
- Patterns and Stagewatch have no musical tempo today (§7).

---

## 6. Modulation and binding UX in the field

| Tool | Sources | Shaping | Binding gesture | Presets |
|---|---|---|---|---|
| **Resolume 7** [A10][A39] **V** | Timeline (play modes, loop/ping-pong/random), **BPM Sync** (time in beats; Phase Lock can be freed), **Dashboard**, **Clip Position**, **Audio Analysis** (External/Composition/Clip/Layer/Group **FFT**), Crossfader Phase | **Envelope** applied to "any input" (FFT, MIDI, OSC, mouse): keyframes with **Quadratic, Sine, Circular, Exponential, Elastic, Back, Bounce, Hold** In/Out/In-Out; x-axis is **phase**, not seconds; toggles, dropdowns and **palettes** animate (colour interpolation); **Start Settings** (composition load / clip trigger / column trigger) | grey cogwheel per parameter | parameter presets (play mode, duration, envelope…, not in/out); envelope presets |
| **MadMapper 6** [A11][A12][A24] **V** | Control List tabs Keyboard, MIDI, DMX, OSC, **Audio** (amplitude, bass, medium, treble, `ltc_time`, `bpm`, `beatCount`, `1_beats`, `4_beats`, `16_beats`), MadMapper modules (**OscillatorBank**, Macro…), Gamepad, Other (Ableton Link) | **Source Range / Target Range / Filter (averaging) / Curve / Toggle** (gamepad page); ISF `time_base` generator with `link_speed_to_global_bpm` | right-click **Add Control**, or Learn coloured per protocol (keyboard blue, MIDI green, OSC yellow, DMX purple); Copy OSC Address | — |
| **TouchDesigner** [A9][A15][A25][A40][A41] **V** | Audio Spectrum, audioAnalysis palette, **Beat CHOP**, **LFO CHOP** (Sine, Gaussian, Triangle, Ramp, Square, Pulse; Bias, Phase, Offset, Amplitude; Octave Control input; reset conditions), **Ableton Link CHOP** (numpeers, linked, waiting, synced; ramp/pulse/bar/beat/sixteenths/tempo/beats/phase; Start Stop Sync; signature) | any CHOP math | exports (channel name is path) | palette components |
| **Notch** [A13][A14][A26][A42] **V** | Sound Modifier, Sound FFT Modifier (regions), BPM Modifier | Smooth Value, Combiner, Accumulator, **Bake Modifier** ("Bakes incoming modifier values to the timeline") | node links | — |
| **VDMX** [A16] **V** | Audio Analysis filters, **LFO** (any number of waveforms, piecewise + **bezier**, duration in seconds or **measures** when synced to a Clock), **Clock** (Internal / MIDI clock / BPM detection; data sources per beat, downbeat, bars) | per receiver | every UI item is a data-source receiver | slider presets toggle automations |

**What the field agrees on:** (1) a global tempo with tap, resync, ½/×2 and nudge; (2) beat-synced sources in
beats, bars and phrases, staggerable; (3) audio bands with gain and an asymmetric fall; (4) an envelope/curve
stage that applies to *any* input; (5) binding by right-click on the parameter; (6) presets. **Nobody documents**
cross-machine determinism of audio-reactive values, explicit latency figures, or flash safety on audio-driven
brightness (U: absence in the pages read).

---

## 7. Patterns (read-only, `bcdbf28`, 12 Oct 2026) — what to align with **V**

- `src/Patterns.Rendering/Effects/Spectrum.cs`:
  - `AudioLevelFrame(float Level, float Low, float Mid, float High)`, `IsSilent` at 0.001.
  - `Spectrum.Analyse`: `Window = 1024`, Hann, radix-2 FFT, bands **20–250 / 250–2000 / 2000–8000 Hz**,
    `mag = |X|·4/n`, band = `√Σmag²·1.5`, level = `RMS·2.5`, each clamped 0–1.
  - `LevelSmoother`: attack **0.03 s**, release **0.25 s**, `k = 1 − exp(−dt/τ)` with dt from the sample count.
  - `AudioLevels`: one static channel; levels older than **1 s** read as zero ("a capture that stopped never leaves
    a picture frozen mid-beat").
- `src/Patterns.App/Services/AudioAnalyserService.cs`: NAudio **WASAPI loopback of the default output, or an input**;
  half-window hop; reconcile every 1 s; retry a missing input every **5 s**; Windows only, "elsewhere it says so".
- `src/Patterns.Core/Services/AudioInput.cs`: "Default input" saved as text; device names matched again **with the
  Windows port number removed** (`"Microphone (2- USB Audio CODEC)"` ≡ `"Microphone (3- USB Audio CODEC)"`).
- `AudioSourceKind { None, Internal, External }`; `ReactiveOptions.AudioAmount` default **0.6**.
- `ReactiveView.Of(o, seconds, audio, surge)`: `Time = seconds·speed·(1 + Low·amount·0.6)`; depth `+ Mid·amount·0.25`;
  brightness through `FlashGuard.Brightness(baseline, level, amount)` with **`MaxAudioBrightnessSwing = 0.25`**.
  - *Defect to avoid (computed):* multiplying absolute seconds by an audio-dependent factor makes the scene's
    clock jump: at t = 3600 s a 1 % change in `lift` moves `Time` by 36 s. PlayMap must integrate rate changes on
    a fixed grid (or anchor them), never scale absolute time.
- Help text: flashes limited to **three a second**; "a beat at 128 BPM taken on the off-beat as well is faster than
  it — so the limit lives in the engine rather than in the presets" (`HelpBodies.cs`).
- `LinkClock` in Patterns is the twin-desk clock exchange, not Ableton Link. No tempo/BPM feature exists in Patterns.

---

## 8. PlayMap today (`main`, `c12b186`) **V**

- `ReactiveView` (Core) already carries `Level`, `Low`, `Amount` and passes `uLow`/`uAmount` to the shaders, but
  they are always **0** (`ReactiveView.Of` sets them to 0); `Mid`/`High` were dropped in the port; `reactive.scene`
  has no sound parameter (`GeneratorCatalog`). `FlashGuard.Brightness(…, 0, 0)` is called with no audio.
- `GeneratorFrame(Seconds, ShowFrame, FrameRate, Impulse, FlashGuard)`; `FrameInputs` has no modulation;
  `EffectImpulseChannel` (one sting at a time) is the only live input to generators.
- `ParticleSim`: fixed 1/120 s steps on the leader grid (determinism model to reuse).
- `AudioOut` (OpenAL Soft) slaves file audio to a timeline (±0.5 %); `FfmpegAudioDecoder` gives float32 stereo
  48 kHz with sample-exact seek. Neither is wired into the engine yet; nothing captures audio.
- No tempo, beat, LFO or binding concept anywhere; VerbCatalog has 12 categories and no audio verbs.
- ROADMAP: M6 "Audio analysis (FFT, onset, BPM) as a modulation source broadcast with show time; Ableton Link; launch
  quantisation"; COMPETITIVE §5c: "audio analysis… break[s] frame identity between nodes".

## 9. Seams already fixed by sibling packages (local specs, 13 Oct 2026) **V**

- **timecode** (`r2/design/timecode.md` §4): `AudioInputInfo(Key, Name, Channels, Rates, Backend)`,
  `IAudioCaptureSink.Samples(ReadOnlySpan<float> interleaved, int channels, int sampleRate, long firstSampleIndex,
  long readMonoNs)` + `Faulted(words)`, `IAudioCapture`, `IAudioInputs.Inputs()/Open(key, preferredRate, sink)`;
  implementations on OpenAL Soft capture and FFmpeg (`file:` WAV looped, `alsa`/`pulse`/`dshow`); `SampleClockFit`;
  "whichever lands first owns it"; WASAPI multichannel left to "audio / R4.4".
- **effects** (`r2/design/effects.md` §3.7, §4.4): `IModulationBus { bool TryRead(string source, long leaderNs, out
  double value01); bool IsLive(string source); }`, `IAudioFrames { Channels; TryWave(leaderNs, samples, span);
  TrySpectrum(leaderNs, bins, span); }`, `ParamBinding(Param, Source, Amount, Offset, BindingCurve Curve,
  SmoothSeconds)`; built-ins `lfo:<shape>/<period s>[/<phase>]`, `ramp:<s>`, `sting`; reserved names
  `audio:level|bass|mid|high|onset|fft/<bin>`, `beat` (bar phase), `bpm`, `midi:`, `osc:`, `dmx:`, `var:`; "a
  source this node does not have reads 0.5"; `FrameInputs.Modulation` / `.Audio` init properties; FFGL
  `FF_SET_BEATINFO {bpm, barPhase}`.
- **control** (`r2/design/control.md` §4): `Binding(Id, Name, Input, Target, BindingShape, BindingFeedback,
  Enabled)` with input words (`MIDI … CC 1 48`, `OSC /x`, `SACN 7 101`) and target **wire lines** (`SET sf… opacity
  *`); "Audio-feature inputs (`AUDIO <band>`) once the audio-reactive package lands"; smoothing deferred because it
  "integrate[s] time on the control path"; MIDI clock → `ITimecodeInput.MidiClock`.
- **companion** (`r2/design/companion.md` §4.3): expects `STATE.audio {"level","bpm","beat"}` and variables `bpm`,
  `level`; Companion 5.1's `oscillate(period, waveform, phase)` "updates 10 times a second" (companion research) —
  whether its phase is wall-clock based is **U**.

---

## Design implications for PlayMap

1. **Adopt the shared capture seam** (`IAudioInputs`, timecode's shape) and add what audio needs: a **WASAPI
   backend** on NAudio.Wasapi 3.1.0 (MIT) with input capture, endpoint **loopback** ("System sound") and **process
   loopback excluding PlayMap** (Windows 11+, else endpoint loopback), plus an **input hub** that opens a device once
   and fans channels out to the LTC reader and the analyser. Device names keep Patterns' rule (default input as a
   word; the Windows port number ignored).
2. **The show's own sound is not "live"**: analyse media at import (Notch's `.fft` precedent, Watchout's "optimise on
   import") into an **analysis track** keyed by content hash — bands, onsets, beats, downbeats, tempo — so
   `track:` sources are a **pure function of media time**: identical on every node, zero latency, even look-ahead.
   Resolume's Composition/Clip FFT becomes deterministic here.
3. **Two analysis rates, one frame type:** a 200 Hz analysis frame (hop 5 ms at 48 kHz): 1024-point Hann FFT for
   spectrum, flux and Patterns-identical Level/Bass/Mid/High; a 16-band biquad filterbank (30 Hz–16 kHz, 0.57
   octave) for low-latency bands; onsets by log spectral flux with causal peak picking, plus kick/snare/hat
   sub-band detectors.
4. **Beat phase is a function of leader time.** Tempo lives as scheduled **tempo anchors** `(leaderNs, beat, bpm,
   meter)`; taps, resyncs, detection, Link and MIDI clock all produce anchors with a small lead, published to
   followers like playlist anchors. Corrections are slewed by extra anchors, never by integration. Beat-locked
   visuals are then on time at the lens (prediction), while level/onset visuals are honestly late by a measured,
   shown latency (≈ 40–100 ms, softened by acoustic delay).
5. **Live analysis becomes shared, not local:** the listening node publishes its frames (stamped with the leader time
   of the sound, HMAC-tagged UDP) and every node — the listener included — evaluates modulation from the same
   frames after one group-wide playout delay. Smoothing and envelopes run on a fixed 200 Hz tick grid in leader
   time with replay, so a mirror backup draws the same audio-reactive frame.
6. **Link without GPL in the exe:** talk to a user-installed **Carabiner** over loopback TCP 17000 (tempo, beat/phase
   at time, quantised `request-beat-at-time`, start/stop), map its microsecond clock to ours with a min-RTT exchange;
   offer a clean-room passive tempo listener later; never port the C++ code. MIDI clock in through the control
   seam; Pro DJ Link as a later passive listener of documented beat packets.
7. **Modulation UX at the field's level and beyond:** one bus (adopting effects' `IModulationBus`/`ParamBinding`
   names) bound from any numeric parameter of generators, layers, surfaces and effects; Resolume's
   gain/fall/direction and envelopes, TouchDesigner's staggered beat ramps, Notch's Locked BPM and regions,
   MadMapper's beat divisions, VDMX's per-filter sources; plus drag-a-source-onto-a-slider, one-click "Make it
   react" presets, and flash safety on luminous targets (three flashes a second, Patterns' 0.25 swing).
8. **Fix the inherited defect:** never scale absolute time by audio; speed modulation integrates on the tick grid
   from an anchored origin (`RATE` mode), like the particle sim.
9. **Launch quantisation** (`AT BEAT|BAR|PHRASE`) falls out of the beat clock and is what Link recommends.

---

## 10. Sources

| Tag | What | URL | Date | Status |
|---|---|---|---|---|
| A1 | Microsoft Learn: Loopback Recording (WASAPI) | https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording | undated; read 13 Oct 2026 | V |
| A2 | Microsoft Learn: AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS | https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params | undated; read 13 Oct 2026 | V |
| A3 | Microsoft Learn: Low Latency Audio | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio | undated; read 13 Oct 2026 | V |
| A4 | NuGet nuspecs: naudio 3.1.0, naudio.wasapi 3.1.0, ppy.sdl3-cs 2026.1002.1, portaudiosharp2 1.0.6, miniaudio.net 0.2.0, soundflow 1.4.1; Microsoft.ML.OnnxRuntime 1.31.0 | https://api.nuget.org/v3-flatcontainer/<id>/<ver>/<id>.nuspec | read 13 Oct 2026 | V |
| A5 | FFmpeg `doc/indevs.texi` (master) | https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/doc/indevs.texi | fetched 13 Oct 2026 | V |
| A6 | FFmpeg `Changelog` (master; 9.0 section) | https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/Changelog | fetched 13 Oct 2026 | V |
| A7 | miniaudio LICENSE (public domain or MIT-0) | https://raw.githubusercontent.com/mackron/miniaudio/master/LICENSE | fetched 13 Oct 2026 | V |
| A8 | Ableton Link README (Link Audio, HostTimeFilter, latency compensation, licence) | https://raw.githubusercontent.com/Ableton/link/master/README.md | fetched 13 Oct 2026 | V |
| A9 | TouchDesigner: Audio Spectrum CHOP | https://docs.derivative.ca/Audio_Spectrum_CHOP | undated; read 13 Oct 2026 | V |
| A10 | Resolume: Parameter Animation | https://resolume.com/support/en/parameter-animation | undated; read 13 Oct 2026 | V |
| A11 | MadMapper 6 docs: Live Performance & Control | https://docs.madmapper.com/madmapper/6/11.-live-performance-and-control | undated; read 13 Oct 2026 | V |
| A12 | MadMapper 6 docs: Writing custom GLSL materials (audioFFT SIZE/ATTACK/DECAY/RELEASE, time_base) | https://docs.madmapper.com/madmapper/6/3.-media/writing-custom-glsl-materials | undated; search highlights 13 Oct 2026 | V (highlights) |
| A13 | Notch Manual 2026.2: Sound Modifier | https://manual.notch.one/2026.2/en/docs/reference/nodes/modifiers/sound-modifier/ | 2026.2; read 13 Oct 2026 | V (highlights) |
| A14 | Notch Manual 2026.2: Sound FFT Modifier | https://manual.notch.one/2026.2/en/docs/reference/nodes/modifiers/sound-fft-modifier/ | 2026.2 | V (highlights) |
| A15 | TouchDesigner: Palette:audioAnalysis | https://docs.derivative.ca/Palette:audioAnalysis | undated; read 13 Oct 2026 | V |
| A16 | VDMX Plugins (Audio Analysis, LFO, Clock) | https://docs.vidvox.net/vdmx/vdmx_plugins | undated; search highlights 13 Oct 2026 | V (highlights) |
| A17 | Notch Manual 2026.1: Working With Audio (.fft file at import) | https://manual.notch.one/2026.1/en/docs/learning/working-with-audio/ | 2026.1 | V (highlights) |
| A18 | aubio README (GPLv3) and `src/spectral/specdesc.h` (ODF list, citations) | https://raw.githubusercontent.com/aubio/aubio/master/README.md ; …/src/spectral/specdesc.h | fetched 13 Oct 2026 | V (read only) |
| A19 | BTrack README (GPLv3, 1.0.7 of 31 Dec 2025, DAFx-09 method) | https://raw.githubusercontent.com/adamstark/BTrack/master/README.md | fetched 13 Oct 2026 | V |
| A20 | madmom LICENSE (code BSD-2, models CC BY-NC-SA 4.0) | https://raw.githubusercontent.com/CPJKU/madmom/main/LICENSE | fetched 13 Oct 2026 | V |
| A21 | Beat This! README and LICENSE (MIT code and weights; model sizes) | https://raw.githubusercontent.com/CPJKU/beat_this/main/README.md | fetched 13 Oct 2026 | V |
| A22 | librosa LICENSE (ISC) | https://raw.githubusercontent.com/librosa/librosa/main/LICENSE.md | fetched 13 Oct 2026 | V |
| A23 | Resolume: BPM | https://resolume.com/support/en/bpm | undated; read 13 Oct 2026 | V |
| A24 | MadMapper 6: OSC Commands & Channels List (Global_BPM addresses) | https://docs.madmapper.com/madmapper/6/11.-live-performance-and-control/osc-commands-and-channels-list | undated; highlights | V (highlights) |
| A25 | TouchDesigner: Beat CHOP | https://docs.derivative.ca/Beat_CHOP | undated; read 13 Oct 2026 | V |
| A26 | Notch Manual 2026.1: BPM Modifier | https://manual.notch.one/2026.1/en/docs/reference/nodes/modifiers/bpm-modifier/ | updated 4 Dec 2023 | V (highlights) |
| A27 | TV Technology, "Managing lip sync" (BT.1359, EBU R37, ATSC IS-191 figures) | https://www.tvtechnology.com/opinions/managing-lip-sync-265013 ; https://www.tvtechnology.com/opinions/managing-lip-sync-267386 | 1 Sep 2007; 11 May 2012 | V-s |
| A28 | ITU-R BT.1359-1 page; EBU R 037 page | http://www.itu.int/rec/R-REC-BT.1359-1-199811-I/en ; https://tech.ebu.ch/publications/r037 | 1998; undated | found, text U |
| A29 | Ableton: Link documentation (concepts, session state, Link Audio) | https://ableton.github.io/link/ | undated; read 13 Oct 2026 | V |
| A30 | Deep Symmetry: DJ Link Ecosystem Analysis — Packet Types; Tracking BPM, Beats, and Position | https://djl-analysis.deepsymmetry.org/djl-analysis/packets.html ; …/beats.html | undated; read 13 Oct 2026 | V (highlights) |
| A31 | Ableton Link TEST-PLAN.md | https://raw.githubusercontent.com/Ableton/link/master/TEST-PLAN.md | fetched 13 Oct 2026 | V |
| A32 | Ableton Link LICENSE.md; repo listing (Guidelines PDF) | https://raw.githubusercontent.com/Ableton/link/master/LICENSE.md ; https://github.com/Ableton/link | fetched 13 Oct 2026 | V (PDF U) |
| A33 | ableton-link-rs (GPLv3 port); abletonlink-go; essaim-dev/al | https://github.com/anweiss/ableton-link-rs ; https://github.com/DatanoiseTV/abletonlink-go ; https://github.com/essaim-dev/al | search 13 Oct 2026 | V (highlights) |
| A34 | Carabiner README (GPL v2, port 17000, commands) | https://raw.githubusercontent.com/Deep-Symmetry/carabiner/main/README.md | fetched 13 Oct 2026 | V |
| A35 | Compeller `link-bpm` README (clean-room passive tempo listener, MIT/Apache-2.0) | https://raw.githubusercontent.com/Compellerai/link-bpm/main/README.md | fetched 13 Oct 2026 | V |
| A36 | Deep Symmetry: DJ Link Packet Analysis (PDF) | https://deepsymmetry.org/dysentery/Analysis.pdf | undated | V (highlights) |
| A37 | dysentery (EPL 1.0, "not a sanctioned implementation") | https://github.com/Deep-Symmetry/dysentery | search 13 Oct 2026 | V (highlights) |
| A38 | beat-link 8.0.0 API overview | https://deepsymmetry.org/beatlink/apidocs/ | undated | V (highlights) |
| A39 | Resolume: Envelopes | https://resolume.com/support/en/envelopes | undated; read 13 Oct 2026 | V |
| A40 | TouchDesigner: LFO CHOP | https://docs.derivative.ca/LFO_CHOP | undated; read 13 Oct 2026 | V |
| A41 | TouchDesigner: Ableton Link CHOP | https://docs.derivative.ca/Ableton_Link_CHOP | undated; read 13 Oct 2026 | V |
| A42 | Notch Manual 2026.2: Modifiers index (Bake, Smooth Value, Combiner…) | https://manual.notch.one/2026.2/en/docs/reference/nodes/modifiers/ | 2026.2 | V (highlights) |
| L1 | Patterns source (`Spectrum.cs`, `AudioAnalyserService.cs`, `AudioInput.cs`, `ReactiveField.cs`, `FlashGuard.cs`, `HelpBodies.cs`, `LinkClock.cs`, `Enums.cs`) | /home/user/patterns-svc | `bcdbf28` | V |
| L2 | PlayMap source (`ReactiveField.cs`, `GeneratorCatalog.cs`, `ParticleSim.cs`, `AudioOut.cs`, `FfmpegAudioDecoder.cs`, `RenderHost.cs`, `GeneratorRenderer.cs`, `VerbCatalog.cs`) | /home/user/mapper | `c12b186` | V |
| L3 | Sibling specs: r2/design/timecode.md, effects.md, control.md, companion.md | scratchpad r2 | 13 Oct 2026 | V |
