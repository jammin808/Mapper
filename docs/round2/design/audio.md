# WP "audio" — audio reactivity, tempo and the modulation bus

Audio input and analysis (levels, bands, spectrum, onsets, kick/snare/hat), a tempo clock (tap, resync, detection,
Ableton Link through Carabiner, MIDI clock, the playlist's own track), named modulators (LFO, envelope, steps,
random), and one modulation bus that any numeric parameter of a generator, layer, surface or effect binds to —
**deterministic across nodes**: beat phase is a function of leader time, the show's own sound is analysed at import
into a track that is a function of media time, and live sound is analysed once and shared as stamped frames that
every node evaluates identically.

Research: `r2/research/audio.md`, cited **[R:§n]** (sources inside are [A…]). Code read on `main` (`c12b186`).
Sibling specs adopted, not redefined: `r2/design/timecode.md` (the audio capture seam), `r2/design/effects.md`
(`IModulationBus`, `IAudioFrames`, `ParamBinding`), `r2/design/control.md` (`Binding`, MIDI clock routing),
`r2/design/companion.md` (`STATE.audio`).

---

## 1. Goal and the bar

PlayMap today has no audio input and no tempo. `ReactiveView` already carries `Level`, `Low` and `Amount`, and the
shaders read `uLow`/`uAmount`, but `ReactiveView.Of` sets them to 0 [R:§8]. COMPETITIVE §5c says "audio analysis…
break[s] frame identity between nodes". ROADMAP M6 asks for "audio analysis (FFT, onset, BPM) as a modulation source
broadcast with show time; Ableton Link; launch quantisation".

| What the best do (verified) | PlayMap this round | Matches / beats |
|---|---|---|
| **Resolume 7**: per-parameter Audio Analysis from External/Composition/Clip/Layer/Group FFT; L/M/H or a custom range; Gain, Fall; direction modes `>` `<` `-` `+` (the last two drive the parameter's *speed*); envelopes with ten easing curves applied to any input; BPM Sync with Phase Lock; Tap, Resync, ±, Nudge, MIDI clock, Link; presets [R:§2.2, §2.4, §6] | `MOD BIND` on any numeric parameter; Bass/Mid/High, 16 filterbank bands, 64-bin spectrum; Amount (gain), asymmetric attack/release (fall); modes ADD / SCALE / REPLACE / **RATE** (Resolume's `-`/`+`, integrated deterministically); curves; beat-synced LFOs; Tap, Resync (beat/bar/phrase), ½, ×2, Nudge, MIDI clock, Link; one-click presets | **Match** on vocabulary. **Beat:** the *Composition/Clip FFT* equivalent (`track:` sources) is analysed at import and is **identical on every node and zero-latency**; Resolume analyses live on one machine |
| **MadMapper 6**: Audio tab (amplitude, bass, medium, treble, `bpm`, `beatCount`, `1_beats`/`4_beats`/`16_beats`, `ltc_time`); Global BPM with TAP, Resync, BPM source, Link (all on OSC); ISF `audioFFT` with SIZE/ATTACK/DECAY/RELEASE; `time_base` linked to BPM; right-click Add Control [R:§2.2, §2.4, §6] | Same sources plus onsets per drum class; `beat:div/<k>` for any division; every verb on OSC through the control WP's mirror; `IAudioFrames` for ISF `audio`/`audioFFT` (effects WP); rate parameters integrated on a tick grid instead of `time_base` | **Match**; **beat** on determinism and drum classes |
| **TouchDesigner**: Beat CHOP (ramps every ¼–32 beats or any length, **Multiples with Shift Step** to stagger, Bar/Beat/Sixteenths, **Locked to Timeline**); audioAnalysis palette (Low/Mid/High, **Kick, Snare, Rhythm**, centroid); LFO CHOP (six shapes, bias, phase); Ableton Link CHOP (peers, synced, phase) [R:§2.5, §6] | `beat:div/<k>` with `/shift` stagger, `beat:in/<n>`, phrase; kick/snare/hat; centroid; LFO shapes incl. bias and random; Link status (peers, synced) in STATE | **Match**; **beat**: no patching needed — it is a right-click |
| **Notch**: 16 bands with per-band EQ, spikiness, smoothness; **Sound FFT regions** → triggers with attack/decay; channel **Mid/Side**; **BPM modifier with "Locked" time mode — same time, same value**; **`.fft` analysis file written at import** [R:§2.2, §2.5] | 16 bands with gains; region triggers become envelope modulators triggered by band onsets; Mid/Side channel picks; every beat source "locked" by construction; the analysis track (`.pmaa`) at import with **beats, downbeats and tempo** as well as bands | **Match** on the import file; **beat**: beat grid in the file, shared across nodes and fetched like media |
| **VDMX**: Clock plugin with **BPM Detection** mode, MIDI clock, tap, ÷2, ×2; filters as data sources; LFOs in seconds or measures with bezier curves [R:§2.4, §6] | `TEMPO SOURCE DETECT` with confidence and lock states; filterbank bands; LFOs in seconds or beats; steps sequencer; curves | **Match**; **beat**: detection confidence and "holding" through breakdowns are shown, not hidden |
| **Patterns** (sister app): WASAPI loopback or an input; Level/Low/Mid/High with fixed band edges and scalings; 30/250 ms smoothing; stale after 1 s; device names matched without the Windows port number; flash guard bounds the audio brightness swing at 0.25 [R:§7] | The same four numbers computed by the **same formula** (test vectors), the same input-naming rule, the same flash bound | **Match** — a look reads the same in both apps; **beat**: fixes Patterns' clock defect (absolute time scaled by audio, §3.9) |
| **Ableton Link**: tempo, beat, phase (quantum), start/stop; quantised launch "strongly recommended"; GPLv2+ or commercial [R:§4] | Link through a user-installed **Carabiner** over loopback TCP (no GPL code in the exe); `AT BEAT|BAR|PHRASE` launch quantisation on every verb that takes `AT` | **Match** on function, licence-clean; quantisation extends to playlist GO and stings |
| Nobody documents: cross-machine determinism of audio-reactive values, a stated latency, or flash safety on audio-driven brightness [R:§6] | Frames shared from one listener, a group-wide playout delay, fixed-step evaluation; latency measured and shown with the acoustic delay; flash limiter on luminous targets | **Beat** — a mirror backup draws the same audio-reactive frame; the operator sees "63 ms behind the sound, 13 ms at 15 m" |

**Doctrine fit.** Attempts are not facts: every verb answers accepted/applied with what changed, and refuses no-ops
("audio:bass is already bound to Plasma speed with amount 0.5"). Content is a pure function of show time: beat
sources are a function of leader time; track sources of media time; live sources of shared frames on a fixed grid.
One writer: the project holds bindings and settings; tempo anchors are written by one node (the listener); modulated
values never write the project. Measured, then governed: the playout delay, the flash limiter and the tempo slew
each have a measured cause and a deterministic test.

**Folded defect (found in this read).** `ReactiveView.Of` computes `Time = seconds * speed` with
`speed = o.Speed * (1 + surge.Speed * 0.9) * …` (`src/PlayMap.Core/Content/ReactiveField.cs:70`). `seconds` is the
leader's monotonic seconds (hours or days of uptime), so a sting's speed surge moves the scene clock by up to
`0.9 × uptime` — the scene leaps during every sting. Patterns has the same shape with audio (`Time = seconds · speed
· (1 + Low·amount·0.6)`) [R:§7]. This WP replaces it with integrated phase (§3.9).

---

## 2. Scope this round

### Must (each criterion is a test unless marked *manual*)

| # | Item | Acceptance |
|---|---|---|
| M1 | **Analyser core** (Core, pure): real FFT 1024 Hann at hop 240 (48 kHz → 200 frames/s); Level/Bass/Mid/High by Patterns' formula; 16-band biquad filterbank 30 Hz–16 kHz; 64-bin log spectrum; peak dBFS; flux; centroid; auto-gain; gate | FFT equals a naive DFT to 1e-9 on random input. A sine at each band centre puts ≥ 80 % of band energy in its own band. Level/Bass/Mid/High equal Patterns' `Spectrum.Analyse` on 50 recorded vectors to 1e-6 (formula ported, vectors generated here). A −20 dBFS sine reads peak −20.0 ± 0.1 dB. Zero allocations per hop after warm-up (`GC.GetAllocatedBytesForCurrentThread` delta 0 over 10 000 hops). ≤ 2 % of one core at 48 kHz (bench, recorded) |
| M2 | **Onsets**: log spectral flux with a 3-bin maximum filter, causal peak picking (moving mean + δ, 1-hop look-ahead, ≥ 30 ms gap); kick/snare/hat sub-band detectors | Synthetic drum patterns (§7) at 0 and −20 dB SNR: overall F-measure ≥ 0.95 within ±25 ms; per class ≥ 0.85. Stamps (sound time) within ±3 ms of truth after group-delay compensation |
| M3 | **Tempo**: tap (median of the last ≤ 8 intervals, outliers > 30 % dropped, reset after 2 s); detection (ACF comb + cumulative-score beat tracker, octave window 75–180 BPM, lock states searching/locked/holding/lost); half/double; nudge; resync; meter (beats per bar, bars per phrase) | Click and drum loops at 90, 100, 120, 128, 140, 174 BPM: BPM within ±0.5 within 6 s; octave right for ≥ 5 of 6; predicted-beat error p95 ≤ 15 ms once locked; a 120→128 step followed within 4 s; 10 s of silence → `holding`, beat count continues exactly. 6 taps at 500 ms ± 20 ms jitter → 120.0 ± 1 BPM |
| M4 | **Beat clock as anchors**: `TempoAnchor(leaderNs, beat, bpm, beatsPerBar, barsPerPhrase, seq, source)`; every change is a scheduled anchor (lead 40 ms); phase corrections slewed over one bar by extra anchors (≤ 2 % rate change), never integrated | Two engines on `ManualClock`s with the same anchors give bit-identical `beat`, `beat:phase`, `beat:pulse` at 10 000 random instants. A resync at T makes beat N+1 land at T on every node, with the visible change at T + lead on all of them. A slewed correction never moves beat time backwards |
| M5 | **Modulation bus** implementing effects' `IModulationBus` and `IAudioFrames`: sources `audio:*`, `beat*`, `bpm`, `lfo:*` (effects' built-ins plus beat periods), `mod:<id>`, `track:<playlist>/<feature>`, `sting`; smoothing suffix `~a/r`; time-offset suffix `@±ms` on pure sources; fixed 200 Hz tick grid in leader time; two-instance restart smoothing; envelopes; rate integrators with keyframes; flash limiter | A source a node lacks reads 0.5 and STATE names it. Two buses fed the same frames in different arrival orders (and one joining 3 s late) produce bit-identical values at every tick after replay. Render-rate independence: sampling at 50, 59.94 and 144 Hz gives the same values at shared instants. 100 bindings evaluate in ≤ 50 µs per frame, 0 bytes allocated |
| M6 | **Bindings on everything numeric**: `ParamBinding` (effects' record) attached to `Generator`, `Layer` and `Surface` as an init list; modes ADD, SCALE, REPLACE, RATE; invert, steps, range; `ParamSpec.Bindable` and `ParamSpec.Rate` flags; generators read effective values through a zero-allocation overlay | `MOD BIND gn… speed audio:bass AMOUNT 0.5` changes the drawn picture on the GL test (llvmpipe) and never changes `PROJECT GET`. Binding `seed` or `count` is refused with the reason. Undo removes a binding in one step. Copying a generator (ADD from its JSON) keeps its bindings |
| M7 | **Capture**: adopt timecode's `IAudioInputs` seam; an **input hub** that opens a device once and fans channels out (analyser + LTC); `WasapiAudioInputs` (NAudio.Wasapi 3.1.0): input capture, **System sound** (endpoint loopback), **Everything but PlayMap** (process loopback, Windows 11+); `SyntheticAudioInputs` (`test:click|sweep|noise` at the current tempo); Patterns' device-name rule | Hub test: one fake device, two sinks with different channel picks, one open. `test:click` drives the full chain (analyser → tempo detect locks to the project BPM within 6 s). Name matching passes Patterns' vectors ("Microphone (2- USB Audio CODEC)" ≡ "(3- …)"). WASAPI: **compile-only here**; NAudio.Wasapi 3.1.0 targets `net9.0`, so it is referenced from the one `net10.0` TFM with every call guarded by `OperatingSystem.IsWindows()`; on the repository's Windows CI job a `[WindowsFact]` enumerates (no endpoint on the runner → an empty list with words, never a throw) |
| M8 | **Shared live frames** (listener → followers): `PMMD` packets on UDP 9945, two frames per packet (each frame sent twice), HMAC-SHA256/128 tag keyed from the pairing token; group playout delay L; follower reports; tempo anchors in the same channel; roles listen/follow/solo/auto | Two engines on loopback: follower values bit-identical to the listener's at every tick with 10 % packet loss and reordering. A packet with a wrong tag is dropped and counted; a frame from a node that is not the configured listener is refused. L adapts to the slowest follower's report at a scheduled instant |
| M9 | **Analysis track at import** (`.pmaa`, keyed by content hash): 100 Hz frames (levels, bands, 64-bin spectrum, flux, onsets), beats/downbeats/tempo segments from an offline tracker; `track:` sources and `TEMPO SOURCE TRACK` follow the playlist's transport | A file made with FFmpeg 9 (`aevalsrc` clicks at 128 BPM + noise, AAC in MP4) imports with `bpm 128.0 ± 0.1`, beats within ±5 ms. Two engines reading the same track give identical `track:` values at the same leader instants while the playlist plays, seeks and changes rate. The track rebuilt on another node has the same SHA-256 (determinism of decode + analysis on x64) |
| M10 | **Verbs, STATE, context, journal** (§4.2–4.3) | EngineTests assert fields and published STATE for every verb, refusals included; `VERBS` lists them with risk; HELP generated |
| M11 | **Generators listen**: `reactive.scene` and the fractals gain `sound` (0–1, default 0 for old projects, 0.6 for new ones) reading Level/Bass/Mid/High as Patterns does; the reactive clock becomes an integrated phase (fixes §1's defect); particles read the bus at their own 1/120 s steps | GL test: a reactive scene with `sound 0.6` and `test:click` differs from silence; the brightness swing never exceeds Patterns' 0.25 bound; a sting at uptime 10 h moves the scene clock by < 1 s (today: hours) |
| M12 | **Desk**: tempo strip, level meter, audio drawer, modulation socket and popover on every bindable inspector row, drag-a-source-onto-a-slider, right-click **Modulate ▸** and **Make it react ▸** | Desk unit tests: every menu item maps to a catalogued verb (the UX CI rule). *Manual*: bind a param in ≤ 5 s from a cold desk |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **Link via Carabiner** (`TEMPO SOURCE LINK [host:port]`): status, tempo both ways, beat/phase mapping with a min-RTT clock exchange, quantised resync via `request-beat-at-time`, start/stop sync, peers in STATE | Against a fake Carabiner (our TCP emulator of the documented lines): mapping error ≤ 0.2 ms; a PlayMap tap proposes `bpm`; a remote tempo change becomes an anchor within one status. Real Link: *manual* (needs Carabiner + Live) |
| S2 | **MIDI clock in** through the control WP's `ITimecodeInput.MidiClock` (24 PPQN, start/continue/stop, SPP) | Synthetic F8 at 128 BPM with 0–2 ms one-sided jitter → 128.00 ± 0.05 BPM in 4 s; FA → beat 0 at the next F8 |
| S3 | **Named modulators** (`MOD NEW`): LFO (shapes, period in s or beats, phase, bias, min/max, seed), envelope (A/H/D/S/R, trigger: onset class, beat, bar, phrase, threshold, manual), steps (1–32, glide), random walk | Each shape pure on show or beat time; envelope triggered by kick onsets identical on two engines |
| S4 | **`MOD AUTO`** "Make it react" presets (Gentle / Punchy / On the beat) per generator kind, as data | One undo step; each preset binds only bindable params and stays within the flash bound |
| S5 | **Launch quantisation**: `AT BEAT|BAR|PHRASE[+n]` resolved by the beat clock in `TakeAt` | `PLAYLIST GO show AT BAR` starts on the next bar line on every node |
| S6 | **Discrete audio inputs for the control layer**: input words `AUDIO ONSET [KICK|SNARE|HAT]`, `TEMPO BEAT [<n>]`, `TEMPO BAR [<every n>]`, `TEMPO PHRASE`, `MOD <source> ABOVE <x>` → bindings fire lines (stings, `PLAYLIST NEXT`) at the event's leader time | A control binding `TEMPO BAR 8 → PLAYLIST NEXT show` advances exactly every 8 bars on two engines |
| S7 | **Latency calibration** without a camera: `AUDIO CALIBRATE` plays a click on the audio out and times it on the input (round trip − reported output latency = input latency) | OpenAL loopback device → 0 ± 1 ms; real device *manual* |

### Later (named, not built)

- Offline neural beat/downbeat tracking at import with **Beat This! `small0`** (MIT code and weights, 8.1 MB)
  exported to ONNX and run on the ONNX Runtime 1.31 already in PlayMap.Vision [R:§2.4]; the algorithmic tracker
  stays the default and the fallback.
- **Pro DJ Link** passive listener (UDP 50001 beat packets, CDJ-3000 absolute position) — unsanctioned protocol,
  written from the published analysis only [R:§5.2].
- A clean-room passive Link tempo listener (multicast 224.76.78.75:20808), tempo only [R:§4.2].
- ASIO and WASAPI exclusive multichannel (ROADMAP R4.4); MIDI clock **out** from the beat clock; Link Audio.
- Section detection (drops, breakdowns) in analysis tracks; "Bake" a live modulation to a keyframed track (Notch's
  Bake modifier).

---

## 3. Architecture

### 3.1 Components and threads

```
 device thread (backend)            "PlayMap audio" (AboveNormal)                      control lane (10 ms tick)
 ┌──────────────────────┐ Samples  ┌──────────────────────────────────────────────┐ tempo obs ┌──────────────────────┐
 │ IAudioCapture        │────────▶ │ AudioInputHub → SPSC ring (float, 2 s)       │──────────▶│ TempoLane            │
 │ (WASAPI / OpenAL /   │          │ SampleClockFit (sample → mono → leader ns)   │           │  anchors (tap, resync│
 │  FFmpeg / synthetic) │──▶ LTC   │ AudioAnalyser: hop 240 → AnalysisFrame 200 Hz│           │  detect, link, midi, │
 └──────────────────────┘  reader  │ OnsetDetector · TempoTracker                 │           │  track) → snapshot   │
                       (timecode)  │ LocalFrameRing ◀── frames                    │           └─────────┬────────────┘
                                   │ ModFrameChannel.Publish (listener)           │                     │ TEMPO ANCHOR
                                   └───────────────┬──────────────────────────────┘                     ▼ (UDP kind 2 + wire)
                                                   │ UDP 9945 (PMMD)                     followers: FrameRing + TempoTimeline
 render thread                                     ▼
 ┌─────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
 │ ModulationBus.BeginFrame(leaderNs): advance 200 Hz ticks (playout L) → derived channels (smoothing, envelopes,│
 │ rate integrators, flash limiter) → ParamOverlay per bound entity → generators read effective values;          │
 │ IAudioFrames → AudioTextures (ISF audio/audioFFT, FFGL); particles sample the bus at their 1/120 s steps      │
 └─────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
 "PlayMap analysis" (BelowNormal, throttled while playing): AnalysisTrackBuilder(media) ← FfmpegAudioDecoder → .pmaa
```

- **One writer per thing.** The project (bindings, modulators, audio and tempo settings) is written by the
  `ProjectStore` on the control lane. Tempo anchors are written by the `TempoLane` of the **listener** node and
  adopted by followers. Analysis frames are written by the listener's audio thread. The bus is read-only on the
  render thread (it owns its tick state; nothing else writes it).
- **No allocation** on the device callback, the audio thread, the frame channel's receive path, or the render path:
  rings, fixed arrays, `InlineArray` frame fields, `delegate*` sinks.

### 3.2 Capture (adopted seam, plus a hub and a Windows backend)

The timecode WP's seam is adopted unchanged [R:§9]: `IAudioInputs.Inputs()`, `Open(key, preferredRate, sink)`,
`IAudioCaptureSink.Samples(interleaved, channels, rate, firstSampleIndex, readMonoNs)`, `Faulted(words)`, and its
`SampleClockFit` (lower-envelope least squares; Link's `HostTimeFilter` idea [R:§1.5]).

- **`AudioInputHub`** (Core) wraps any `IAudioInputs`: a device key is opened **once**; each subscriber states a
  `ChannelPick` (`"1"`, `"1+2"` summed, `"3-4"` stereo, `"M"`/`"S"` mid/side like Notch); the hub de-interleaves into
  each subscriber's ring. LTC on channel 7 and analysis on 1+2 share one device.
- **`WasapiAudioInputs`** (Media, Windows, NAudio.Wasapi 3.1.0, MIT):
  - keys `wasapi:in:<endpoint id>`, `wasapi:loop:<render endpoint id>` (shown as "System sound — <device>"),
    `wasapi:proc:exclude-self` ("Everything but PlayMap"; `ActivateAudioInterfaceAsync` with
    `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS { TargetProcessId = own PID, PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE }`,
    Windows build ≥ 20348 [R:§1.1]; elsewhere it answers "needs Windows 11 — System sound is the fallback");
  - shared mode, event-driven, period from `NodeSettings.Audio.PeriodMs` (10 ms default; `IAudioClient3` minimum
    period when the driver offers it);
  - device hot-plug via `IMMNotificationClient`; a lost device reopens every **5 s** (Patterns' `RetryEvery`).
- **OpenAL Soft and FFmpeg inputs** come from the timecode WP (`OpenAlAudioInputs`, `FfmpegAudioInputs` with
  `file:` looped WAV and libavdevice inputs). This FFmpeg 9 build offers `pulse`, `oss` and `openal` capture on Linux (no `alsa`;
  here there is no device, which gives words), and `dshow` on Windows through the same code path. FFmpeg has no WASAPI input;
  WASAPI is NAudio's (above).
- **`SyntheticAudioInputs`** (Media; also the CI fake): `test:click` (a 1 ms 2 kHz click on every beat of the
  current tempo, accent on the downbeat), `test:sweep` (20 Hz–20 kHz log sweep, 10 s), `test:noise` (pink),
  `test:drums` (the §7 generator). Rehearsal in a silent office: the walls react to a known beat.
- **Input naming** (`AudioInputMatch`, Core, Patterns' rule [R:§7]): `""` off; `default` (the machine's input);
  `system` (loopback of the default output); `system-but-playmap`; `track:<playlist>`; `test:<kind>`; or a device
  name matched case-insensitively and then by `Key()` with Windows' "2- " socket number removed. The project keeps
  the logical name; `NodeSettings.Audio.Input` overrides it per machine (`AUDIO LISTEN … HERE`).

### 3.3 The analyser (Core, pure, one instance per listening input)

Input: mono `float` (the channel pick), device rate `fs` (resampled to 48 kHz by a polyphase FIR when the device
runs at 44.1 kHz, so every frame grid is the same). Each **hop of 240 samples (5 ms)** produces one
`AnalysisFrame`:

| Field | Computation | Notes |
|---|---|---|
| `SoundLeaderNs` | mono time of the hop's centre sample (SampleClockFit) − input latency, mapped to leader ns (`NetworkClock`) | the instant the sound reached the input |
| `Level`, `Bass`, `Mid`, `High` | **Patterns' formula**: 1024 Hann, `mag = |X|·4/n`, bands 20–250 / 250–2000 / 2000–8000 Hz, `√Σmag²·1.5`, level `RMS·2.5`, clamp 0–1 [R:§7] | names follow effects (`bass`); `audio:low` is an alias |
| `PeakDb` | max |x| over the hop, dBFS | −120 floor |
| `Bands[16]` | RBJ biquad band-pass filterbank, edges 30·1.4806^k Hz (30, 44.4, 65.8, 97.4, 144, 214, 316, 468, 693, 1026, 1519, 2249, 3329, 4930, 7299, 10806, 16000), Q 2.53; RMS per hop; per-band gain | group delay ≈ 22 ms at 36 Hz, 10 ms at 80 Hz, 3 ms at 260 Hz [R:§2.1] — reported per band |
| `Spectrum[64]` | FFT magnitudes pooled into 64 log bins 30 Hz–16 kHz (low bins interpolated), dB | ISF `audioFFT` and FFGL FFT buffers upsample from it |
| `Flux`, `OnsetStrength`, `Onsets` | §3.4 | `Onsets` flags: Any, Kick, Snare, Hat (+ Beat, Downbeat set by the tempo tracker) |
| `Centroid` | spectral centroid, log-mapped 30 Hz–16 kHz → 0–1 | "brightness" of the sound |

- **Auto-gain** (on by default): per band, value ÷ max(gate, slow peak), slow peak = peak hold with 10 s release.
  Quiet rooms and loud clubs both use the whole 0–1 range ("the first five minutes": nobody sets a gain). Patterns'
  four numbers are kept un-normalised (`audio:level` etc.) and normalised variants are `audio:bass/n`, so a
  Patterns look stays identical.
- **Gate:** below `GateDb` (−60 dBFS) for 300 ms → `silent` (sources fall to 0 through their release, never freeze;
  Patterns' "never frozen mid-beat").
- **Analyser group delay is compensated in the stamps**: the frame's time is the window's centre, so a kick at T is
  stamped T ± 3 ms regardless of window length.

### 3.4 Onsets and drum classes

- **ODF:** log-magnitude spectral flux `Σ max(0, L[k] − maxfilt₃(L_prev)[k])`, `L = log(1 + 100·|X|)` (SuperFlux
  form [R:§2.3], written from the paper; no aubio/BTrack code).
- **Peak picking (causal, 1-hop look-ahead):** onset at n−1 when ODF[n−1] ≥ max(ODF[n−3…n]) and ≥ mean(ODF[n−40…n])
  + δ (δ adaptive: 1.5 × the 2 s median absolute deviation) and ≥ 30 ms since the last onset.
- **Classes** by sub-band flux with the same picker: kick 30–150 Hz (filterbank bands 0–4), snare 150–600 Hz and
  1.5–5 kHz both above threshold, hat 6–16 kHz (bands 13–15) with low bass flux.
- Each onset also produces an **envelope source** (`audio:onset`, `audio:kick`, …): value 1 at the onset, decaying
  `exp(−Δt/120 ms)`; a function of the onset times in shared frames (deterministic).

### 3.5 Tempo: tap, detection, external sources → anchors

- **`TempoAnchor`** (Core): `beat(t) = Beat + (t − LeaderNs)·Bpm/60e9`. A `TempoTimeline` holds ≤ 4 anchors
  (current, slew, scheduled); `At(t)` picks the last anchor with `LeaderNs ≤ t`. Every change is a *new anchor at a
  future instant* (`TempoLeadMs`, 40 ms) so followers hold it before it applies:
  - **tempo change** (set, tap, half, double): continuous — the new anchor's `Beat` is the old timeline's beat at its
    instant;
  - **resync** ("now is the downbeat"): the press time T is beat N+1's instant (N = the bar or phrase start just
    before), effective at T + lead on every node — the jump is seen 40 ms after the press, everywhere at once;
  - **phase correction** (detection, Link, MIDI clock): a slew anchor at `bpm·(1 + Δ/beatsPerBar)` for one bar, then
    the plain anchor; |rate change| ≤ 2 %; larger errors (> ¼ beat) jump at the next bar line;
  - **nudge** `±ms` = a phase correction by that amount; `FASTER`/`SLOWER` = ±4 % while held, auto-released after
    2 s (Resolume's nudge buttons).
- **Tap** (`TapTempo`): median of the last ≤ 8 intervals; an interval > 30 % off the median is dropped; > 2 s gap
  restarts; the first tap of a series sets the downbeat when `TapSetsDownbeat`. A tap is stamped when the line
  arrives (`TryAudio` stamps `MonoNow` before the lane queue), or by the client (`AT <unix ms>`, Companion's press
  time).
- **Detection** (`TempoTracker`, written from Ellis 2007 / Davies & Plumbley 2007 / Stark et al. 2009, no GPL code
  [R:§2.4]):
  1. ODF at 200 Hz; every 0.5 s an ACF over the last 8 s (FFT 4096);
  2. comb salience over lags 67–171 (180–70 BPM) summing ACF at l, 2l, 3l, 4l, weighted by a log-Gaussian prior
     centred at 120 BPM inside `DetectMinBpm…DetectMaxBpm` (default 75–180, one octave plus);
  3. tempo hysteresis: a new candidate replaces the current only if its salience is ≥ 1.2× for 3 consecutive
     estimates; half/double candidates are reported (`candidates` in STATE) for a one-click override;
  4. beat phase by a cumulative score `C[n] = ODF[n] + max_{v∈[n−2τ, n−τ/2]} W(n−v)·C[v]` (log-Gaussian W), the next
     beat predicted by projecting C forward one period;
  5. beats → observations `(leaderNs, index)`; a robust line fit over the last 8 beats gives `(bpm, phase)`; the
     lane issues an anchor when |phase error| > 10 ms or |Δbpm| > 0.2;
  6. downbeat guess: kick strength accumulated per beat position over 8 bars; adopted at ≥ 60 % confidence, else the
     tap/resync downbeat stays;
  7. lock states: `searching` (confidence < 0.3), `locked`, `holding` (silence or confidence drop: freewheel at the
     last anchor up to 32 bars), `lost`.
- **External sources** produce the same observations:
  - **Link via Carabiner** (S1): `status` every 250 ms and on push; the Link clock (µs) is mapped to our mono clock
    by a min-RTT exchange (`status` round trips on loopback; both are the OS monotonic clock, so the offset is
    stable); `:beat` at `:start` gives the timeline; quantum = `BeatsPerBar`. PlayMap → Link: `bpm <x>` on tap/set,
    `request-beat-at-time` on resync (graceful), `start-playing`/`stop-playing` when start/stop sync is on. Test
    plan behaviours honoured: joining never changes the session tempo (TEMPO-2) [R:§4.1].
  - **MIDI clock** (S2): F8 stamps from `ITimecodeInput.MidiClock` → lower-envelope line fit over 2 s (24 PPQN) →
    tempo; FA = beat 0 at the next F8; FB continue; FC stop (holding); SPP sets the beat count.
  - **Track** (`TEMPO SOURCE TRACK <playlist>`): the current item's analysis-track tempo segments and downbeats,
    mapped through the playlist's `TimelineAnchor` (rate included) → piecewise anchors, recomputed on item change,
    seek or rate change. Pure: every node computes the same anchors.
  - **Patterns / node** (`TEMPO SOURCE PATTERNS|NODE <id>`): adopt anchors published by that peer.

### 3.6 Sharing live analysis: listener, playout delay, packets

- **Roles.** `listener` (opens the input, analyses, writes tempo anchors, publishes frames), `follower` (receives
  frames and anchors from one listener), `solo`. `auto`: in a sync group the group's leader listens if its input
  opens, else the first node (by id) that has `AUDIO LISTEN` and an open input; outside a group, solo. Taps and
  resyncs pressed on a follower are forwarded to the listener as lines (`TEMPO TAP AT <unix ms>`), keeping one tempo
  writer.
- **Playout delay L.** A frame describes sound at `SoundLeaderNs`. A node drawing light instant t (the render host
  already draws for "predicted vblank + display latency") reads live sources at **τ = t − L**. It must already hold
  that frame, so `L ≥ A + P + net`, where A = input latency + ½ window + hop (≈ 15–35 ms), P = that node's light
  lead (≤ 1 frame + display latency), net ≤ 3 ms on a wired LAN. Solo: L = A + P + 2 ms. Group: each follower
  reports its minimum every 1 s (packet kind 4); the listener publishes `L = max + 2 ms` inside the tempo packet with
  an effective instant, so every node switches at the same tick. Typical L: **40–100 ms** [R:§3].
- **Honest latency.** STATE shows `total = L` (sound at the input → light), the acoustic delay at
  `ListenerDistanceM` (`d/343 m/s`), and `perceived = L − acoustic`: 63 ms at the input is 19 ms after the sound for
  a listener at 15 m. Beat-locked sources are on time (they read t, not τ); `AlignBeatsToListener` shifts them by
  the acoustic delay so the downbeat lands at the listener's ears.
- **Packets (`PMMD` v1, UDP 9945, little-endian; unicast to each follower in the group).**

```
header (32 B)
 0  u32  magic 0x444D4D50 ("PMMD")
 4  u8   version 1
 5  u8   kind: 1 frames · 2 tempo · 3 trigger · 4 follower report · 5 integrator keyframe
 6  u16  payload length
 8  u32  sender node id (the 8-hex NodeId as a number)
12  u32  session (listener epoch: changes when the input reopens)
16  u64  sequence
24  i64  sender leader ns at send
kind 1 payload: u8 count (1–2) · u8 bands (16) · u8 bins (64) · u8 0 · count × frame (140 B):
     i64 soundLeaderNs · f32 level, peakDb, bass, mid, high, flux, centroid, onsetStrength · u16 onsets · u16 0 ·
     f16 bands[16] · u8 spectrum[64] (dB, 0.375 dB per step, −96…0)
kind 2 payload: u8 count (1–4) · u8[3] 0 · i64 playoutNs · i64 playoutEffectiveNs · count × anchor (48 B):
     i64 leaderNs · f64 beat · f64 bpm · u8 beatsPerBar · u8 barsPerPhrase · u8 source · u8 state · u32 0 · i64 seq · f64 confidence
kind 3 payload: i64 leaderNs · u32 modulator hash · u32 0                      (MOD TRIGGER, scheduled)
kind 4 payload: i64 minPlayoutNs · u32 lateTicks · u32 lostFrames               (follower → listener, 1 Hz)
kind 5 payload: u8 count · count × (u32 binding hash · i64 tick · f64 value)    (rate-integrator keyframes, 1 Hz)
trailer: 16 B HMAC-SHA256/128 over header+payload; key = HKDF-SHA256(pairing token, "playmap pmmd v1")
```

  - Every frame rides in two consecutive packets (loss tolerance): 332 B × 200 packets/s ≈ **0.53 Mbit/s** per
    follower; tempo packets on change and every 1 s.
  - **The listener eats its own packets:** it feeds its bus the frames *after* a round trip through the codec (f16
    bands, u8 spectrum), never its full-precision values, so listener and followers evaluate identical numbers.
  - **Fail closed:** wrong magic, version, length or tag → dropped and counted; frames only from the node configured
    (or elected) as listener, at an address the Show Bus knows for that node; an open node (no token) accepts only
    from paired or trusted addresses. No token or key rides in any packet; the HMAC key is derived locally.

### 3.7 The modulation bus (`ModulationBus : IModulationBus, IAudioFrames`)

**Source names** (effects' reserved names kept [R:§9]; additions marked +):

| Source | Value (0–1 for `TryRead`; raw for `TryReadRaw`) | Determinism |
|---|---|---|
| `audio:level`, `audio:bass` (`audio:low`+), `audio:mid`, `audio:high` | Patterns' numbers | shared |
| `audio:peak`+ | −60…0 dBFS → 0…1 (raw dB) | shared |
| `audio:band/<0-15>`+, `audio:fft/<bin>` | filterbank band; spectrum bin (effects) | shared |
| `audio:onset`, `audio:kick`+, `audio:snare`+, `audio:hat`+ | onset envelope (1 → exp decay 120 ms) | shared |
| `audio:flux`+, `audio:centroid`+ | normalised | shared |
| `…/n` suffix+ | auto-gain normalised variant (`audio:bass/n`) | shared |
| `beat` | bar phase 0–1 (effects) | pure |
| `beat:phase`+, `beat:phrase`+ | beat phase, phrase phase | pure |
| `beat:pulse[/<ms>]`+ | 1 on each beat, exp decay (default 100 ms) | pure |
| `beat:div/<k>[/<shift>]`+ | ramp 0→1 every k beats (k = `1/4`, `2`, `3.33`…), shifted by `shift` periods (TouchDesigner's stagger) | pure |
| `beat:in/<n>`+ | 1 during beat n of the bar | pure |
| `bpm` (raw BPM; 0–1 over 0–240), `beat:count`+ (raw) | | pure |
| `lfo:<shape>/<period>[b]/<phase>` | effects' built-in; **`b` = period in beats**+; shapes sine, triangle, saw, ramp, square, pulse, random, smooth+, noise+ | pure |
| `mod:<id>`+ | a named modulator (§3.8) | pure or shared (by its trigger) |
| `track:<playlist>/<feature>`+ | any `audio:` feature of the playlist's current item's analysis track at its media time | pure |
| `sting` | effects' built-in | pure (scheduled) |
| `midi:`, `osc:`, `dmx:`, `var:` | registered by control/companion/effects | live |
| suffix `~<attack ms>/<release ms>`+ | asymmetric smoothing of any shared/live source (`audio:bass~10/300`) | as its source |
| suffix `@<±ms>`+ | time offset on pure sources (`track:pl…/kick@-30` looks 30 ms ahead) | pure |

`SourceDeterminism` (+): **Pure** (a function of show/beat/media time), **Shared** (a function of the listener's
frames — identical on every node that holds them), **Live** (this node only). STATE and the desk badge only *Live*
bindings as "breaks mirror backup" (COMPETITIVE §5c); Shared ones show "shared from <listener>".

**Evaluation.**

1. **Tick grid.** Δ = 5 ms of leader time; tick k = ⌊t/Δ⌋. `BeginFrame(t)` advances ticks up to ⌊(t − L)/Δ⌋ + 1
   (catching up at most 2 s; beyond that it re-anchors and marks `warming`). Shared inputs at tick k are the frame
   values interpolated at k·Δ from the frame ring (2× redundancy, 16 s deep).
2. **Derived channels** are created by the compiled plan (`ModPlan`) for every distinct `(source, smoothing)`,
   envelope, rate integrator and flash limiter in use:
   - *Smoothing* `~a/r` = one-pole attack/release, run as **two instances restarted at absolute tick multiples of
     H** (H = max(2 s, 8τ)); the output comes from the instance that has run ≥ H. Any node holding the last 2H of
     frames computes bit-identical values — late joiners included; the seam at a restart is < e⁻⁸ (0.03 %) of a step.
   - *Envelopes* are closed forms of the last trigger time (triggers are onset flags in frames, beats from anchors,
     or scheduled `MOD TRIGGER` instants) — deterministic.
   - *Flash limiter* (luminous targets): a rise of more than half the target's range within 1/6 s counts as a flash;
     at most **3 per second** pass (Patterns' rule); excess rises are held to the previous value — windowed (1 s),
     deterministic.
   - *Rate integrators* (`MODE RATE`, and bound rate params §3.9): `value_k = value_{k−1} + rate_k·Δ` from the
     binding's `AnchorNs`; the group leader publishes keyframes once a second (kind 5) so joiners adopt the exact
     value. The only stateful channel; badged "shared" only when keyframes are flowing.
   - *Late frames.* A tick computed before its frame arrived is provisional; when the frame arrives inside the
     window the bus replays from that tick. Frames drawn with provisional values are counted (`diverged`) in STATE.
3. **Sampling.** `TryRead(slot, t)`: pure sources are evaluated at t directly; shared/live/derived ones interpolate
   linearly between ticks ⌊(t−L)/Δ⌋ and +1. Particles call it at each 1/120 s step's leader time.
4. **Binding evaluation** (`BindingEval`, extends effects' rule): with `u = curve(invert(s) − Offset)` and the
   parameter's `Min/Max`:
   - `ADD` (default, effects' rule): `clamp(base + Amount·u·(Max − Min))`;
   - `SCALE`: `base · lerp(1 − |Amount|, 1, u)` (quiet pulls the value down);
   - `REPLACE`: `Min' + u·(Max' − Min')` with `RangeMin/RangeMax` (fractions of the param range);
   - `RATE`: integrates `Amount·(u − 0.5)·2·(Max − Min)` per second (wraps when the param is cyclic, else clamps);
   - `Steps > 0` quantises u; Bool `u ≥ 0.5`; Choice `⌊u·n⌋`; Colour: hue turn `Amount·u` (effects).
5. **`ParamOverlay`** (Render): per bound entity, an array of effective numbers by parameter index (NaN = not
   bound), refreshed once per frame from the plan; generators read `Num(i)`; zero allocation. The project's
   `ParamSet` is never rewritten per frame.

**`IAudioFrames`** for ISF and FFGL: `TrySpectrum(t, bins)` resamples `Spectrum[64]` at τ; `TryWave(t, n)` reads the
raw sample ring **on the listener only** (followers answer false and STATE says "waveform on the listener only" —
192 kB/s per channel is not worth the LAN).

### 3.8 Named modulators (`Modulator`, kinds from `ModulatorCatalog`, params typed like generators)

| Kind | Params (`ParamSpec`) | Value |
|---|---|---|
| `lfo` | `shape` (sine, triangle, saw, ramp, square, pulse, random, smooth, noise), `period` (s), `beats` (0 = use `period`), `phase` 0–1, `bias` 0–1 (triangle peak / square duty), `min`, `max`, `seed` | pure in show or beat time; random = `Pcg32` hash of (seed, period index) |
| `envelope` | `attack`, `hold`, `decay` (ms), `sustain` 0–1, `release` (ms), `curve` (power), `trigger` (`manual`, `onset`, `kick`, `snare`, `hat`, `beat`, `bar`, `phrase`, `above:<source>:<x>`), `retrigger` (restart / ignore) | closed form from the last trigger |
| `steps` | `values` (1–32 numbers), `beats` per step, `glide` 0–1 | pure in beat time |
| `random` | `rate` (changes/s or per beat), `smooth` 0–1, `seed` | pure (hash noise) |

### 3.9 Generators listen, and rate parameters integrate

- `reactive.scene`, `fractal.*` gain `sound` (Number 0–1). `ReactiveView.Of(settings, phase, audio, surge)` reads
  `audio:level/bass/mid/high` (Patterns' mapping: depth `+ Mid·sound·0.25`, brightness through
  `FlashGuard.Brightness(base, Level, sound)` with the 0.25 swing) and restores `Mid`/`High` to the view.
- **Rate parameters** (`ParamSpec.Rate = true`: reactive `speed` and `rotation`, fractal `speed`, relight `speed`)
  no longer multiply absolute seconds. The scene clock is a **phase integrated on the bus's tick grid**:
  `phase_k = phase_{k−1} + speed_k·(1 + 0.9·surge.Speed_k)·(1 − slow_k)·(1 + 0.6·Bass_k·sound)·Δ`, anchored at the
  generator's last edit (the `ParamSet` change instant, stored in the overlay) — the same model as the particle sim
  and effects' anchored phases. A sting no longer leaps the clock; a speed edit no longer jumps it.

### 3.10 Analysis tracks (`.pmaa`) — "optimise on import" for sound

- **When:** at import of any media with an audio stream (an import step registered with the optimise WP's pipeline,
  or directly from `MediaImport` until it lands), on `AUDIO ANALYSE`, and on a node that fetched the media but not
  its track (fetched with it when a peer has it; recomputed otherwise).
- **How:** `FfmpegAudioDecoder` (48 kHz float stereo) → the same analyser in **offline mode**: hop 480 (100 Hz);
  non-causal improvements allowed (centred smoothing, full-song dynamic-programming beat tracking à la Ellis, a
  downbeat pass over the whole song, tempo segments). Runs on "PlayMap analysis" (BelowNormal), paused while a
  playlist on this node plays and the CPU budget is short (the same throttle as media transfer).
- **Format** (little-endian):

```
 0 "PMAA" · u16 version 1 · u16 flags
 8 u8[32] media SHA-256
40 u32 sampleRate 48000 · u32 hop 480 · u32 frames · u16 bands 16 · u16 bins 64
56 f32 bandEdgesHz[17]
124 u32 onsets · u32 beats · u32 tempoSegments · u32 reserved
frames × 112 B: f16 level, peakDb, bass, mid, high, flux, centroid · u16 onsets · f16 bands[16] · u8 spectrum[64]
onsets × 8 B: u32 frame · u8 classes · u8 strength · u16 0
beats × 16 B: f64 mediaSeconds · u8 beatInBar · u8 confidence% · u16 0 · u32 0
tempo segments × 16 B: f64 startSeconds · f32 bpm · f32 confidence
trailer: SHA-256 of all bytes above
```

  40 MB per hour of audio (computed). Stored at `<node media cache>/analysis/<media sha256>.pmaa`; the project
  records `MediaItem.Analysis` (`AudioAnalysisRef`: the track's own hash, version, BPM, beats, first downbeat,
  confidence), so every node can verify it holds the same track (`MEDIA STATUS` lists mismatches).
- **Reading:** `track:<playlist>/<feature>` → the playlist lane's current item and clip position at t
  (`PlaybackLanes.Snapshot` → `lane.At(t)` → `clip.PositionAt(t)`) → frame index, linear interpolation; during a
  transition the two items' features are mixed by the transition weight. Audio out is slaved to the same timeline
  (±10 ms, `AudioOut`), so the feature matches what leaves the speaker — zero analysis latency by construction.
- `AUDIO LISTEN TRACK <playlist>` makes every `audio:` source read the track instead of a device (Resolume's
  Composition/Clip FFT, deterministic). An item without a track yet is analysed live from its decoder and badged
  until its track exists.

### 3.11 Safety and failure

| Event | Behaviour |
|---|---|
| Input unplugged / fails to open | sources release to 0 (not frozen); STATE `input.state:"missing"` with words; context amber (red if bindings depend on it); reopen every 5 s; tempo holds (freewheel) |
| Silence > 8 s | detection `holding` up to 32 bars, then `lost` (anchors stay; beat sources keep running) |
| Listener node dies | followers' frames stop → release; tempo anchors continue (freewheel); with `auto`, the next eligible node starts listening after 2 s and publishes a new session (a visible discontinuity of levels only, counted) |
| Late frames on a follower | provisional ticks, replay, `diverged` count; STATE suggests the L that would have covered them |
| Flash risk | luminous bindings pass the limiter (≤ 3 flashes/s); audio brightness swing ≤ 0.25 (Patterns); Show Mode cannot disable the limiter |
| Panic | showsafety's outcomes gain "hold modulation" = `MOD BYPASS ON` (every effective value returns to its base on the next frame) |
| AI | `MOD BIND/AUTO/UNBIND`, `AUDIO LISTEN`, `TEMPO SOURCE` are Review: an assistant proposes, a person approves; `TEMPO ANCHOR` is High |

### 3.12 Performance budgets (estimates; measured in `r2/bench`)

| Path | Budget |
|---|---|
| Analyser per hop (FFT 1024 + 16 biquads × 240 samples + onsets) | ≤ 60 µs (≤ 1.2 % of a core) |
| Tempo tracker every 0.5 s (ACF 4096 + comb) | ≤ 1 ms |
| Bus `BeginFrame` with 100 bindings, 30 derived channels | ≤ 50 µs, 0 B |
| Frame channel receive per packet | ≤ 5 µs, 0 B |
| Analysis track build | ≥ 30× real time per core (10 min of audio ≤ 20 s) |
| Network | 0.53 Mbit/s per follower |
| Memory | frame ring 16 s ≈ 450 kB; sample ring 2 s per input |

---

## 4. Spine needs

### 4.1 Model (Core; all new members are init properties with defaults, so old files read unchanged; schema stays 1)

```csharp
// src/PlayMap.Core/Model/AudioModel.cs (new)
namespace PlayMap.Core.Model;

/// <summary>What the show listens to and how (machine choices — the device — may be overridden per node).</summary>
public sealed record AudioSettings
{
    public static readonly AudioSettings Default = new();
    /// <summary>"" off · "default" · "system" · "system-but-playmap" · "track:&lt;playlist&gt;" · "test:click|sweep|noise|drums"
    /// · a device name (matched by AudioInputMatch: case, then Windows' socket number ignored).</summary>
    public string Input { get; init; } = "";
    public string Channels { get; init; } = "1+2";     // "1", "1+2" (summed), "3-4", "M", "S"
    public double GainDb { get; init; }                 // −24…+24
    public bool AutoGain { get; init; } = true;
    public double GateDb { get; init; } = -60;
    public double ListenerDistanceM { get; init; }      // 0 = not shown; acoustic delay d/343
    public bool AlignBeatsToListener { get; init; }
    public ImmutableArray<double> BandGainsDb { get; init; } = [];   // 16 or empty
}

/// <summary>The show's beat clock: its source and the values it starts from.</summary>
public sealed record TempoSettings
{
    public static readonly TempoSettings Default = new();
    public string Source { get; init; } = "manual";     // manual · detect · link · midi:<port> · track:<playlist> · patterns · node:<id>
    public double Bpm { get; init; } = 120;             // 20–999 (Link's range)
    public int BeatsPerBar { get; init; } = 4;          // 1–16
    public int BarsPerPhrase { get; init; } = 8;        // 1–64
    public double DetectMinBpm { get; init; } = 75;
    public double DetectMaxBpm { get; init; } = 180;
    public bool TapSetsDownbeat { get; init; } = true;
    public bool LinkStartStop { get; init; }
}

/// <summary>A named modulator many parameters share: an LFO, an envelope, a step sequence, a random walk.</summary>
public sealed record Modulator(string Id, string Name, string Kind, ParamSet Params) : IEntity
{
    public bool Enabled { get; init; } = true;
}

/// <summary>What the import found in a file's sound: the analysis track's own hash and its headline facts.</summary>
public sealed record AudioAnalysisRef(string TrackHash, int Version, double Bpm, int Beats, double FirstDownbeatSeconds, double Confidence);
```

Changes to existing types (small hunks):

| Type | Added (JSON key) | Default |
|---|---|---|
| `Project` | `Audio` (`"audio"`), `Tempo` (`"tempo"`), `Modulators` (`"modulators"`) | `AudioSettings.Default`, `TempoSettings.Default`, `[]` |
| `Project.AllIds()` | yields modulator ids | — |
| `Generator`, `Layer`, `Surface` | `Bindings` (`"bindings"`, `ImmutableArray<ParamBinding>`) | `[]` |
| `MediaItem` | `Analysis` (`"analysis"`, `AudioAnalysisRef?`) | `null` |
| `ParamSpec` | `Bindable` (true), `Rate` (false), `Luminous` (false), `Cyclic` (false) | as stated; `seed`, `count`, `preset`, `label`, `iterations`, `colors`, `variant` set `Bindable = false` |
| `GeneratorCatalog` | `sound` on `reactive.scene` and `fractal.*` (Number 0–1; default "0" in the catalogue so old shows are unchanged; `GENERATOR NEW` sets 0.6) | — |
| `Ids` | `Modulator = "mo"` | — |

`ParamBinding` is the effects WP's record; this WP adds init members (effects' positional members unchanged):

```csharp
public sealed record ParamBinding(string Param, string Source, double Amount, double Offset, BindingCurve Curve, double SmoothSeconds)
{
    public BindingMode Mode { get; init; } = BindingMode.Add;
    public double AttackSeconds { get; init; } = -1;   // −1: SmoothSeconds both ways (effects' meaning)
    public double ReleaseSeconds { get; init; } = -1;
    public double RangeMin { get; init; }               // REPLACE: fraction of the param range
    public double RangeMax { get; init; } = 1;
    public bool Invert { get; init; }
    public int Steps { get; init; }                     // 0 = continuous
    public long AnchorNs { get; init; }                 // RATE: leader ns the integral starts at (stamped by the router)
    public bool Enabled { get; init; } = true;
}
public enum BindingMode { Add, Scale, Replace, Rate }
public enum BindingCurve { Linear, Ease, Step, Exponential, Logarithmic, SCurve }   // effects' three + control's names
```

Example: `"bindings":[{"param":"speed","source":"audio:bass~10/250","amount":0.5,"offset":0,"curve":"linear","smoothSeconds":0,"mode":"add","enabled":true}]`.

Runtime types (Core, not in the project file):

```csharp
// src/PlayMap.Core/Audio/BeatClock.cs
public enum TempoSourceKind { Manual, Tap, Detect, Link, MidiClock, Track, Patterns, Node, ProDjLink }
public enum TempoLock { Manual, Searching, Locked, Holding, Lost }
public readonly record struct TempoAnchor(long LeaderNs, double Beat, double Bpm, int BeatsPerBar, int BarsPerPhrase, long Seq, TempoSourceKind Source)
{
    public double BeatAt(long leaderNs) => Beat + (leaderNs - LeaderNs) * (Bpm / 60e9);
    public long LeaderNsAtBeat(double beat) => LeaderNs + (long)Math.Round((beat - Beat) * 60e9 / Bpm);
}
public sealed record TempoTimeline(ImmutableArray<TempoAnchor> Anchors, TempoLock Lock, double Confidence)
{
    public static TempoTimeline Fixed(double bpm, int beatsPerBar, int barsPerPhrase, long originNs);
    public TempoAnchor At(long leaderNs);
    public double BeatAt(long leaderNs);
    public long NextBoundaryNs(long leaderNs, BeatUnit unit, int count = 1);     // AT BEAT|BAR|PHRASE[+n]
    public TempoTimeline Schedule(in TempoAnchor next);                           // keeps continuity rules (§3.5)
    public TempoTimeline Slew(long fromNs, double beatError, double maxRate = 0.02);
}
public enum BeatUnit { Beat, Bar, Phrase }

// src/PlayMap.Core/Audio/AnalysisFrame.cs
[Flags] public enum OnsetFlags : ushort { None = 0, Any = 1, Kick = 2, Snare = 4, Hat = 8, Beat = 16, Downbeat = 32 }
[InlineArray(16)] public struct Bands16 : IEquatable<Bands16> { private float _e; /* Equals/GetHashCode over the span */ }
[InlineArray(64)] public struct Spectrum64 : IEquatable<Spectrum64> { private float _e; }
public record struct AnalysisFrame
{
    public long SoundLeaderNs;
    public float Level, PeakDb, Bass, Mid, High, Flux, Centroid, OnsetStrength;
    public OnsetFlags Onsets;
    public Bands16 Bands;
    public Spectrum64 Spectrum;
}
```

`EngineSnapshot` gains `public TempoTimeline Tempo { get; init; }` (the render thread reads beat sources from it).

### 4.2 Verbs (`VerbCatalog.All`; new `VerbCategory.Audio` and `VerbCategory.Modulation`, appended to the enum)

Every name is two words (`Match` reads at most two). Mutating verbs refuse a no-op with the reason.

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| `AUDIO INPUTS` | `AUDIO INPUTS` | Low | no | `[{"key":"wasapi:in:{…}","name":"Line In (Scarlett 2i2)","kind":"capture","channels":2,"rates":[44100,48000],"backend":"wasapi","default":false}]` (+ `default`, `system`, `system-but-playmap`, `test:*`, `track:<playlist>` pseudo-inputs) |
| `AUDIO LISTEN` | `AUDIO LISTEN <input>\|DEFAULT\|SYSTEM\|SYSTEM-BUT-PLAYMAP\|TRACK <playlist>\|TEST CLICK\|SWEEP\|NOISE\|DRUMS\|OFF [CHANNELS 1\|1+2\|3-4\|M\|S] [GAIN <dB>] [HERE]` | Review | yes | applied when the input opened: `{"stage":"applied","effect":"listening to Line In (Scarlett 2i2) 1+2 at 48 kHz","input":{…}}`; accepted + words when it is retrying ("Line In is not on this machine — retrying every 5 s"); `HERE` writes `NodeSettings.Audio.Input` instead of the project |
| `AUDIO GAIN` | `AUDIO GAIN <−24…24>\|AUTO\|FIXED` | Low | yes | effect `gain +6 dB` |
| `AUDIO STATUS` | `AUDIO STATUS` | Low | no | `STATE.audio` (§4.3) with full bands and the spectrum |
| `AUDIO ANALYSE` | `AUDIO ANALYSE <media>\|ALL [FORCE]` (alias `AUDIO ANALYZE`) | Low | yes (cache) | accepted `{"queued":["md…"],"skipped":[{"media":"md…","why":"no audio stream"}]}`; progress in `STATE.audio.analysing`; done → journal row with BPM |
| `AUDIO ROLE` | `AUDIO ROLE LISTEN\|FOLLOW [<node>]\|SOLO\|AUTO` | Review | yes | effect `following Stage-left's analysis (L 64 ms)` |
| `AUDIO DISTANCE` | `AUDIO DISTANCE <metres 0–200>` | Low | yes | effect `listener at 15 m hears the PA 44 ms late` |
| `AUDIO CALIBRATE` (S7) | `AUDIO CALIBRATE [<output device>]` | Review | yes | `{"inputLatencyMs":7.9,"roundTripMs":31.2,"outputLatencyMs":23.3}` |
| `TEMPO STATUS` | `TEMPO STATUS` | Low | no | `STATE.audio.tempo` |
| `TEMPO SET` | `TEMPO SET <20–999> [AT …]` | Low | yes | effect `128.0 BPM from beat 213 (next beat)`; refused `"already 128.0 BPM"` |
| `TEMPO TAP` | `TEMPO TAP [AT <unix ms>]` | Low | yes | `{"taps":4,"bpm":127.9,"applied":true}` (applied from 2 taps) |
| `TEMPO RESYNC` | `TEMPO RESYNC [BEAT\|BAR\|PHRASE] [AT <unix ms>]` | Low | yes | effect `downbeat at 21:04:07.512 (bar 1 of phrase 1)` |
| `TEMPO NUDGE` | `TEMPO NUDGE <±1–200 ms>\|FASTER\|SLOWER\|STOP` | Low | yes | effect `phase −10 ms over one bar` |
| `TEMPO HALF` / `TEMPO DOUBLE` | `TEMPO HALF` · `TEMPO DOUBLE` | Low | yes | effect `64.0 BPM` |
| `TEMPO SOURCE` | `TEMPO SOURCE MANUAL\|DETECT\|LINK [<host:port>]\|MIDI <port>\|TRACK <playlist>\|PATTERNS\|NODE <node>` | Review | yes | accepted, then `STATE.audio.tempo.source` (+ `"link":{"peers":2,"state":"synced"}`); refused with words ("Carabiner is not answering on 127.0.0.1:17000 — start it, or TEMPO SOURCE DETECT") |
| `TEMPO METER` | `TEMPO METER <beats/bar 1–16> [<bars/phrase 1–64>]` | Low | yes | effect `4/4, 8-bar phrases` |
| `TEMPO ANCHOR` | `TEMPO ANCHOR <json>` | High | yes | leader → followers (also on UDP kind 2); refused from anyone but the elected listener |
| `MOD SOURCES` | `MOD SOURCES [<prefix>]` | Low | no | `[{"name":"audio:bass","kind":"audio","determinism":"shared","value":0.42,"on":"listener a1b2c3d4"}, …]` |
| `MOD BIND` | `MOD BIND <entity> <param> <source> [AMOUNT <−1…1>] [OFFSET <x>] [MODE ADD\|SCALE\|REPLACE\|RATE] [CURVE LINEAR\|EASE\|STEP\|EXP\|LOG\|S] [SMOOTH <attack ms>[/<release ms>]] [RANGE <min> <max>] [STEPS <n>] [INVERT]` | Review | yes | effect `Plasma speed ← audio:bass (add 0.5, 10/250 ms)`; refusals: unbindable param ("seed is not bindable — a new seed re-draws the field"), unknown source (with the nearest name), no-op |
| `MOD UNBIND` | `MOD UNBIND <entity> <param>\|ALL` | Review | yes | effect `Plasma speed unbound` |
| `MOD AUTO` | `MOD AUTO <entity> GENTLE\|PUNCHY\|BEAT\|OFF` | Review | yes | `{"bound":["depth","brightness","rotation"],"effect":"Plasma reacts (punchy)"}`; one undo step |
| `MOD NEW` | `MOD NEW LFO\|ENVELOPE\|STEPS\|RANDOM [<name>]` | Low | yes | the new id `mo…` |
| `MOD SET` | `MOD SET <modulator> <param> <value>` | Review | yes | effect; refusals from the kind's schema |
| `MOD TRIGGER` | `MOD TRIGGER <modulator> [AT …]` | Low | yes | effect `fired at +40 ms` (scheduled; kind 3 to followers) |
| `MOD BYPASS` | `MOD BYPASS ON\|OFF\|TOGGLE [<entity>\|ALL]` | Review | yes | effect `modulation held on every target` |
| `MOD PEEK` | `MOD PEEK <entity>` | Low | no | `[{"param":"speed","base":1.0,"live":1.37,"source":"audio:bass~10/250","sourceValue":0.74,"determinism":"shared"}]` |

- **`AT` grows** `BEAT|BAR|PHRASE[+n]` (S5) in `TakeAt`: resolved by `TempoTimeline.NextBoundaryNs` on the lane.
  Every verb that takes `AT` (`PLAYLIST GO`, `VIDEO PLAY`, `MOD TRIGGER`, the content WP's `STING`) gets launch
  quantisation for free.
- `GET/SET/ADD/REMOVE` learn `modulator`; `SET gn… bindings.0.amount 0.3` works through the generic path; the
  router stamps `AnchorNs` on any binding whose mode or source changed.
- Routing: `AUDIO *` and `TEMPO *` go through `TryAudio` (a frozen set like `OpsVerbs`) that stamps the arrival
  instant, answers device work off the lane and posts edits to the lane; `MOD *` run on the lane.
- `RiskOf(line)` (showsafety carry-over): `MOD BIND` to a `Luminous` param with `AMOUNT > 0.5` is High.

### 4.3 STATE and context

New keys at the **end** of STATE (WIRE.md's rule): `audio` then `modulation`. Live numbers are quantised to 100 ms
(meters, not sync); the companion WP's `level`, `bpm`, `beat` come first.

```json
"audio": {
  "level": 0.42, "bpm": 128.0, "beat": 3, "bar": 17, "phrase": 3, "phase": 0.42,
  "bass": 0.61, "mid": 0.30, "high": 0.18, "peakDb": -6.1, "silent": false,
  "input": {"key":"wasapi:in:{…}","name":"Line In (Scarlett 2i2)","channels":"1+2","rateHz":48000,"state":"listening","words":"","backend":"wasapi","gainDb":0,"autoGain":true},
  "role": "listener", "listener": "a1b2c3d4", "followers": [{"id":"b2c3d4e5","lateTicks":0,"minPlayoutMs":41.2}],
  "tempo": {"source":"detect","lock":"locked","confidence":0.86,"bpm":128.0,"beatsPerBar":4,"barsPerPhrase":8,
            "candidates":[128.0,64.0,256.0],"epochUnixMs":1760101234567,"anchor":{"leaderNs":0,"beat":0,"bpm":128.0,"seq":12},
            "link":null,"midi":null,"track":null},
  "latencyMs": {"input":10.0,"analysis":13.3,"net":1.1,"light":38.0,"total":62.4,"listenerM":15,"acoustic":43.7,"perceived":18.7,
                "bandDelay":[22.1,14.9,10.1,6.8,4.6,3.1]},
  "frames": {"rateHz":200,"playoutMs":62.4,"late":0,"lost":0,"diverged":0,"warming":false},
  "analysing": [{"media":"md1a2b3c4d","progress":0.4}]
},
"modulation": {"bindings":12,"bypassed":false,"modulators":3,"live":1,"shared":7,"pure":4,
               "unbound":["audio:bass (no audio on this node)"],"flashLimited":0,"integrators":2}
```

- `epochUnixMs` = the Unix ms (show wall clock) of beat 0 of the current anchor, so a Companion module or a phone
  computes the beat locally with zero traffic.
- **CONTEXT** items: `audio` (green listening with signal; amber silent > 10 s, retrying, frames late; red missing
  while bindings depend on it; grey off), headline "Line In · 128.0 BPM locked · beat 3/4 · 62 ms behind the sound
  (19 ms at 15 m)"; `tempo` (green locked/manual; amber searching/holding; red lost with beat bindings); determinism
  list "breaks mirror backup" gains Live bindings. Commands: the new verbs with risks (generated).
- **Hello caps:** add `tempo` (16 of 16 with the LED WP's `led`; if the budget is exceeded, `tempo` wins over a
  less-used cap — decided at spine time).
- **Show Bus 1.3 proposal** (SHOWBUS-NODE §5): a `tempo` record in the context
  (`{"bpm","beatsPerBar","epochUnixMs","lock","source"}`) so Patterns and Stagewatch can show a beat light; Patterns'
  reactive scenes may subscribe to PMMD frames — the bands equal Patterns' `AudioLevelFrame`, so a Patterns desk
  could react to the same analysis as the walls.

### 4.4 Seams (C#)

```csharp
// Core — adopted from timecode (unchanged; whichever lands first owns src/PlayMap.Core/Audio/AudioInputs.cs)
public sealed record AudioInputInfo(string Key, string Name, int Channels, IReadOnlyList<int> Rates, string Backend)
{
    public AudioInputKind Kind { get; init; } = AudioInputKind.Capture;   // + (audio)
    public bool IsDefault { get; init; }                                   // + (audio)
}
public enum AudioInputKind { Capture, Loopback, ProcessLoopback, File, Synthetic }
public interface IAudioCaptureSink { void Samples(ReadOnlySpan<float> interleaved, int channels, int sampleRate, long firstSampleIndex, long readMonoNs); void Faulted(string words); }
public interface IAudioCapture : IDisposable { int SampleRate { get; } int Channels { get; } }
public interface IAudioInputs { IReadOnlyList<AudioInputInfo> Inputs(); IAudioCapture Open(string key, int preferredRate, IAudioCaptureSink sink); }

// Core — new
public readonly record struct ChannelPick(int First, int Count, ChannelMix Mix)   // "1+2" → (0,2,Sum); "M" → (0,2,Mid)
{ public static bool TryParse(string? text, out ChannelPick pick); }
public enum ChannelMix { Single, Sum, Stereo, Mid, Side }
public sealed class AudioInputHub : IAudioInputs, IDisposable
{
    public AudioInputHub(IAudioInputs inner);
    public IAudioCapture Subscribe(string key, ChannelPick pick, int preferredRate, IAudioCaptureSink sink);   // one device open per key
}

// Core — effects' interfaces with default-implemented additions (NoModulation keeps compiling)
public interface IModulationBus
{
    bool TryRead(string source, long leaderNs, out double value01);
    bool IsLive(string source);
    int Resolve(string source) => -1;                                                     // + hot-path slot
    bool TryRead(int slot, long leaderNs, out double value01) { value01 = 0.5; return false; }   // +
    bool TryReadRaw(string source, long leaderNs, out double value) { value = 0; return false; } // + bpm, dB, counts
    SourceDeterminism Determinism(string source) => IsLive(source) ? SourceDeterminism.Live : SourceDeterminism.Pure; // +
}
public enum SourceDeterminism { Pure, Shared, Live }
public interface IModSourceProvider            // the bus's plug-ins (audio, beat, lfo, mod, track; control registers midi/osc/dmx)
{
    string Prefix { get; }                       // "audio:", "beat", "lfo:", "mod:", "track:"
    bool TryBind(string name, out ModSourceHandle handle, out string why);
    double Read(in ModSourceHandle handle, long leaderNs);   // pure providers: exact at t; shared ones: the tick input
    SourceDeterminism Determinism { get; }
}
public readonly record struct ModSourceHandle(int Provider, int Index, double A, double B);

// Engine — src/PlayMap.Engine/Seams.Audio.cs (new)
public sealed record AudioView(string State, string Words, AudioInputInfo? Input, string Role, string? Listener, TempoTimeline Tempo, LatencyView Latency, FrameStats Frames);
public readonly record struct LatencyView(double InputMs, double AnalysisMs, double NetMs, double LightMs, double TotalMs, double AcousticMs);
public readonly record struct FrameStats(long Late, long Lost, long Diverged, bool Warming, double PlayoutMs);

/// <summary>The listener's frames to followers and back (PlayMap.Net implements it on UDP 9945; tests use a loopback fake).</summary>
public interface IModFrameTransport : IAsyncDisposable
{
    void Publish(ReadOnlySpan<AnalysisFrame> frames);                      // listener; allocation-free
    void PublishTempo(TempoTimeline timeline, long playoutNs, long playoutEffectiveNs);
    void PublishTrigger(long leaderNs, uint modulatorHash);
    void Report(long minPlayoutNs, long lateTicks, long lostFrames);       // follower → listener
    void Target(string? listenerNodeId, IReadOnlyList<string> followerNodeIds);
    IModFrameSink? Sink { get; set; }
    ModTransportStats Stats { get; }
}
public interface IModFrameSink
{
    void Frames(string fromNode, uint session, ReadOnlySpan<AnalysisFrame> frames);
    void Tempo(string fromNode, TempoTimeline timeline, long playoutNs, long playoutEffectiveNs);
    void Trigger(string fromNode, long leaderNs, uint modulatorHash);
    void Report(string fromNode, long minPlayoutNs, long lateTicks, long lostFrames);
    void Keyframes(string fromNode, ReadOnlySpan<(uint Binding, long Tick, double Value)> values);
}
public readonly record struct ModTransportStats(long Sent, long Received, long BadTag, long Refused, long Malformed);

/// <summary>An external tempo (Link via Carabiner, MIDI clock, later Pro DJ Link) as observations for the tempo lane.</summary>
public interface ITempoDriver : IAsyncDisposable
{
    string Kind { get; }                                    // "link", "midi", "prodjlink"
    string State { get; }                                   // "connecting", "synced", "unreachable: …"
    event Action<TempoObservation>? Observed;
    Task<string> ProposeAsync(double? bpm, (double Beat, long LeaderNs)? beatAt, CancellationToken ct);   // Link: bpm / request-beat-at-time
}
public readonly record struct TempoObservation(long LeaderNs, double Beat, double Bpm, double Quantum, int Peers, bool? Playing, double Confidence);

/// <summary>Analysis tracks by content hash (the node's media cache).</summary>
public interface IAnalysisStore
{
    bool TryOpen(string contentHash, out AnalysisTrack track);   // memory-mapped, read-only
    Task<AudioAnalysisRef> BuildAsync(MediaItem item, IProgress<double>? progress, CancellationToken ct);
    string? PathFor(string contentHash);                         // for MEDIA FETCH to carry it
}

// Engine — EngineServices.Audio.cs (init property, as Ops does)
public sealed partial record EngineServices { public AudioService? Audio { get; init; } }

// Render — FrameInputs (effects adds Modulation/Audio; this WP fills them) and GeneratorFrame
public readonly record struct GeneratorFrame(double Seconds, long ShowFrame, double FrameRate, EffectImpulse Impulse, bool FlashGuard)
{
    public IModulationBus? Modulation { get; init; }     // + read by Reactive/Fractal/Particle generators
}
```

`AudioService` (Engine/Audio) owns the hub subscription, the analyser thread, `TempoLane`, the bus, the role logic and
analysis jobs; `Attach(EngineCore)` returns an `IAsyncDisposable` like the ops services. Composition: a new partial
hook `static partial void AddAudioParts(List<EngineExtensionFactory> extensions)` in `EngineParts` (the LED WP's
pattern), implemented in `Host/EngineParts.Audio.cs`; `AudioExtension.Configure` adds `EngineServices.Audio`, and the
render host reads `engine.Services.Audio?.Bus` when it builds `FrameInputs`.

### 4.5 NodeSettings additions (one init property `NodeSettings.Audio`, record in `src/PlayMap.Core/Bus/AudioNodeSettings.cs`)

| Property | Default | Meaning |
|---|---|---|
| `Input` | `""` | this machine's input override ("" = the project's) |
| `Channels` | `""` | override of the project's channel pick |
| `Backend` | `"auto"` | `auto` (WASAPI on Windows, Pulse/OSS/OpenAL via FFmpeg's libavdevice on Linux) · `wasapi` · `openal` · `ffmpeg` · `none` |
| `PeriodMs` | `10` | capture period request |
| `InputLatencyMs` | `-1` | measured or entered input latency (−1 = the backend's report) |
| `Role` | `"auto"` | `auto` · `listen` · `follow` · `solo` · `off` |
| `ListenFrom` | `""` | listener node id to follow ("" = elected) |
| `ModPort` | `9945` | PMMD UDP port (`ShowBusConstants.PlayMapModPort`) |
| `PlayoutMs` | `0` | 0 = computed; else a fixed group playout delay |
| `TempoLeadMs` | `40` | lead of every scheduled tempo change |
| `CarabinerHost` | `"127.0.0.1:17000"` | Link bridge |
| `AnalyseOnImport` | `true` | build analysis tracks at import |

---

## 5. Files

### New

| Path | What |
|---|---|
| `src/PlayMap.Core/Model/AudioModel.cs` | `AudioSettings`, `TempoSettings`, `Modulator`, `AudioAnalysisRef` |
| `src/PlayMap.Core/Bus/AudioNodeSettings.cs` | the node's audio settings |
| `src/PlayMap.Core/Audio/AudioInputs.cs` | the shared seam (if timecode has not landed it), `AudioInputKind` |
| `src/PlayMap.Core/Audio/AudioInputHub.cs`, `ChannelPick.cs`, `AudioInputMatch.cs` | fan-out; channel picks; Patterns' naming rule |
| `src/PlayMap.Core/Audio/Fft.cs`, `Windows.cs`, `Biquad.cs`, `FilterBank.cs`, `Resampler.cs` | DSP primitives (written here; no third-party code) |
| `src/PlayMap.Core/Audio/AnalysisFrame.cs`, `AudioAnalyser.cs`, `AutoGain.cs` | the analyser |
| `src/PlayMap.Core/Audio/OnsetDetector.cs`, `TempoTracker.cs`, `DownbeatGuess.cs`, `TapTempo.cs`, `MidiClockFollower.cs` | onsets and tempo |
| `src/PlayMap.Core/Audio/BeatClock.cs` | `TempoAnchor`, `TempoTimeline`, slew, boundaries, JSON |
| `src/PlayMap.Core/Audio/AnalysisTrack.cs`, `AnalysisTrackWriter.cs`, `OfflineAnalyser.cs` | `.pmaa` format and offline analysis |
| `src/PlayMap.Core/Modulation/ModulationBus.cs`, `ModTicks.cs`, `DerivedChannels.cs`, `ModPlan.cs`, `BindingEval.cs`, `ModSourceName.cs` | the bus |
| `src/PlayMap.Core/Modulation/Providers/AudioSources.cs`, `BeatSources.cs`, `LfoSources.cs` (extends effects'), `ModulatorSources.cs`, `TrackSources.cs` | source providers |
| `src/PlayMap.Core/Modulation/ModulatorCatalog.cs`, `ModulatorMath.cs`, `AutoReact.cs` | named modulators; `MOD AUTO` presets as data |
| `src/PlayMap.Media/Audio/WasapiAudioInputs.cs` | WASAPI capture / loopback / process loopback (`[SupportedOSPlatform("windows")]`, inside `net10.0`) |
| `src/PlayMap.Media/Audio/SyntheticAudioInputs.cs`, `DrumSynth.cs` | test inputs (also used by `AUDIO LISTEN TEST`) |
| `src/PlayMap.Media/Audio/AnalysisStore.cs` | `IAnalysisStore` on the media cache; decode with `FfmpegAudioDecoder` |
| `src/PlayMap.Net/Sync/ModPacket.cs`, `ModFrameChannel.cs` | PMMD codec and UDP transport (HMAC, redundancy, reorder, refusals) |
| `src/PlayMap.Net/Links/CarabinerLink.cs` | `ITempoDriver` over Carabiner's line protocol (S1) |
| `src/PlayMap.Engine/Seams.Audio.cs` | §4.4 engine seams |
| `src/PlayMap.Engine/Audio/AudioService.cs`, `TempoLane.cs`, `AudioRoles.cs`, `AudioStateWriter.cs`, `AudioExtension.cs` | the service |
| `src/PlayMap.Engine/EngineCore.Audio.cs`, `EngineServices.Audio.cs` | verbs; the init property |
| `src/PlayMap.Engine/Host/EngineParts.Audio.cs` | `AddAudioParts` |
| `src/PlayMap.Render/Modulation/ParamOverlay.cs` | per-entity effective values |
| `src/PlayMap.Render/Fx/AudioTextures.cs` | only if the effects WP has not landed it |
| `src/PlayMap.App/Views/TempoStrip.axaml(.cs)`, `AudioDrawer.axaml(.cs)`, `ModulationPopover.axaml(.cs)` | desk views |
| `src/PlayMap.App/ViewModels/TempoViewModel.cs`, `AudioViewModel.cs`, `ModulationViewModel.cs` | desk models |
| `tests/PlayMap.Core.Tests/Audio/*` (FFT, filterbank, analyser vs Patterns vectors, onsets, tempo, tap, MIDI clock, beat clock, track format) | unit tests |
| `tests/PlayMap.Core.Tests/Modulation/*` (bus determinism, replay, joiners, smoothing seams, envelopes, limiter, bindings) | unit tests |
| `tests/PlayMap.Net.Tests/ModFrameChannelTests.cs`, `CarabinerLinkTests.cs` (fake server) | loopback sockets |
| `tests/PlayMap.Engine.Tests/AudioVerbTests.cs`, `AudioTwoNodeTests.cs` | verbs, STATE, two engines |
| `tests/PlayMap.Render.Tests/ModulatedGeneratorTests.cs` | GL on llvmpipe |
| `tests/PlayMap.Media.Tests/AnalysisTrackTests.cs` | FFmpeg-decoded fixtures |

### Touched (small hunks)

| Path | Hunk |
|---|---|
| `src/PlayMap.Core/Model/Project.cs` | three init properties; `AllIds` yields modulators |
| `src/PlayMap.Core/Model/Content.cs` | `Generator.Bindings`, `Layer.Bindings` |
| `src/PlayMap.Core/Model/StageModel.cs` | `Surface.Bindings` |
| `src/PlayMap.Core/Model/MediaModel.cs` | `MediaItem.Analysis` |
| `src/PlayMap.Core/Model/Ids.cs` | `Modulator = "mo"` |
| `src/PlayMap.Core/Content/GeneratorCatalog.cs` | `ParamSpec` flags; `sound` params; non-bindable flags |
| `src/PlayMap.Core/Content/ReactiveField.cs`, `FractalMath.cs` | audio in the views; phase instead of `seconds·speed` |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | two categories, 27 verbs (`AUDIO ANALYZE` alias) |
| `src/PlayMap.Core/Bus/Pairing.cs`, `ShowBus.cs` | `NodeSettings.Audio`; `PlayMapModPort = 9945` |
| `src/PlayMap.Engine/EngineCore.cs` | `EngineSnapshot.Tempo`; `IsAnimating` true while listening; `TryAudio` before `TryOps` |
| `src/PlayMap.Engine/EngineCore.Router.cs` | `TakeAt` learns `BEAT|BAR|PHRASE[+n]` |
| `src/PlayMap.Engine/EngineCore.Edits.cs` | `Locate`/`Add` learn `modulator`; stamp `AnchorNs` |
| `src/PlayMap.Engine/StateWriter.cs`, `ContextBuilder.cs` | one call each |
| `src/PlayMap.Engine/Host/EngineParts.cs` | the `AddAudioParts` partial hook |
| `src/PlayMap.Engine/Rendering/RenderHost.cs` | fill `FrameInputs.Modulation/Audio`; `bus.BeginFrame` |
| `src/PlayMap.Render/Engine/RenderEngine.cs`, `Generators/GeneratorRenderer.cs`, `GeneratorHost.cs`, `ShaderGenerators.cs`, `ParticleGenerator.cs` | `GeneratorFrame.Modulation`; overlays; reactive/fractal audio; particles sample at steps |
| `src/PlayMap.Media/PlayMap.Media.csproj` | `NAudio.Wasapi` 3.1.0 (targets `net9.0`; no separate TFM) |
| `src/PlayMap.Net/Bus/HelloService.cs` | cap `tempo` |
| `src/PlayMap.App/ViewModels/GeneratorInspector.cs`, `Inspectors.cs`, `MapInspectors.cs`, `MainViewModel.cs` | modulation sockets, menus, the strip |
| `docs/WIRE.md`, `ARCHITECTURE.md`, `SHOWBUS-NODE.md`, `COMPETITIVE.md` (§5c), `ROADMAP.md` (M6) | the papers |

---

## 6. Dependencies

### 6.1 Work packages

| WP | Needed from it | Without it |
|---|---|---|
| **timecode** | `IAudioInputs` seam, OpenAL and FFmpeg inputs, `SampleClockFit` | this WP lands the seam and the FFmpeg `file:` input itself |
| **effects** | `IModulationBus`, `IAudioFrames`, `ParamBinding`, `BindingCurve`, `FrameInputs.Modulation/Audio`, `lfo:`/`ramp:`/`sting` built-ins, AudioTextures | this WP lands the interfaces and records (same names, same shapes); ISF/FFGL consumers come with effects |
| **control** | `ITimecodeInput.MidiClock` (S2); input kind for `AUDIO`/`TEMPO` events (S6); `RiskOf(line)`; OSC mirror of the verbs | MIDI clock waits; S6 waits; tempo verbs still reachable on the wire and HTTP |
| **companion** | consumes `STATE.audio`, verbs (generated actions), INDEX for the Modulators page | — |
| **fleet / framelock** | sync-group membership (who follows whom), clock slew on follow (CARRYOVER), anchor push for playlists | roles read sync groups from `PLAYLIST SYNC`; tempo anchors use PMMD kind 2, which does not wait for fleet |
| **showsafety** | Show Mode permission table (TAP/RESYNC/NUDGE allowed armed; `MOD BIND/AUTO` blind), panic outcome "hold modulation", preflight hooks (§8) | modulation always editable live |
| **optimise** | the import pipeline (analysis as one step) | `MediaImport` calls `IAnalysisStore.BuildAsync` directly |
| **content** | `STING` verb (CARRYOVER) quantised with `AT BAR`; `sync` (free/beat/bar) params on periodic generators (outline, sweep, colorcycle) reading `beat:*`; particle `burst` param | stings fire unquantised; beat-locked phase only through bindings |
| **led** | — (LEDs sample sources, so they react automatically) | — |
| **present** | per-output display latency (P in the playout delay) | the render host's single `DisplayLatencyNs` |

### 6.2 Third-party (MIT-compatible; restrictive runtimes outside the exe)

| Package | Version | Licence | Use |
|---|---|---|---|
| NAudio.Wasapi (+ NAudio.Core) | 3.1.0 | MIT [R:§1.4] | WASAPI capture, loopback, process loopback (package targets `net9.0`; calls guarded by `OperatingSystem.IsWindows()`) |
| Silk.NET.OpenAL (+ .Extensions.EXT) | 2.23.0 | MIT | already referenced / timecode WP; OpenAL Soft (LGPL) loaded at run time |
| FFmpeg.AutoGen | 9.0.1.1 | MIT (bindings); FFmpeg libraries from the user's install | decode for analysis tracks; `pulse`/`oss`/`openal` (Linux) and `dshow` (Windows) input |
| Microsoft.ML.OnnxRuntime | 1.31.0 (already in Vision) | MIT | *later*: Beat This! `small0` (MIT weights) |
| **Carabiner** | user-installed | **GPL v2** — a separate program on loopback TCP, never shipped or linked | Link bridge (S1) |
| **No code from** aubio, BTrack (GPLv3), madmom models (CC BY-NC-SA), Essentia (AGPL), Ableton Link or its ports (GPL) | — | — | methods from papers only |

---

## 7. Verification here

| Area | Real in this container | Simulated (and how) | Compile-only / manual |
|---|---|---|---|
| DSP, analyser, onsets, tempo, tap | All, on synthetic signals: sines, sweeps, pink noise, `DrumSynth` (kick: 60 Hz sine + click, 150 ms decay; snare: band-passed noise + 180 Hz, 120 ms; hat: > 7 kHz noise, 40 ms) at known times, swing, tempo ramps, −20 dB beds; Patterns-formula vectors | — | — |
| Real decode → track | FFmpeg 9 at `/opt/ffmpeg9`: fixtures generated in the test with `ffmpeg -f lavfi -i "aevalsrc=…"` (click track 128 BPM) and encoded to WAV, FLAC and AAC-in-MP4 → `FfmpegAudioDecoder` → `.pmaa` → BPM, beats, determinism of the file hash on two runs | — | ARM vs x64 hash equality (no ARM here) |
| Bus determinism | Two buses / two engines on `ManualClock`, shuffled frame arrival, 10 % loss, a 3 s-late joiner, render rates 50/59.94/144 Hz; bit-equality asserted | — | — |
| Network | `ModFrameChannel` over real loopback UDP between two engines in one process; tag failures; foreign senders | loss/reorder by a test shim between socket and decoder | multi-machine LAN (bench, manual) |
| Capture | `SyntheticAudioInputs`; timecode's FFmpeg `file:` input paced in real time | device timing jitter injected into `readMonoNs` (0–10 ms one-sided) for `SampleClockFit` | WASAPI capture/loopback/process loopback (NAudio; enumeration runs on the Windows CI job); OpenAL device capture (asserts the "no capture device" words path here); PipeWire/Pulse device |
| Link | `CarabinerLink` against a fake TCP server speaking the documented lines (`status {…}`, `beat-at-time`, `request-beat-at-time`, `bpm`, start/stop) | — | real Carabiner + Ableton Live: *manual* (Carabiner is GPL and is not built here) |
| MIDI clock | `MidiClockFollower` on synthetic F8/FA/FC streams with jitter | the control WP's fake MIDI backend | real controllers |
| Render | GL tests under Xvfb `:99` (Mesa llvmpipe): reactive scene with `sound`, a bound generator, two `RenderEngine`s with the same bus → identical pixels; the sting-clock defect regression | fake `IAudioFrames` for ISF consumers | GPU drivers |
| Verbs/STATE/context | `EngineTests`: every verb's fields, refusals, journal rows, STATE keys and CONTEXT items | — | — |
| Desk | view-model tests: every menu item → catalogued verb; drag-bind builds the right line | — | look and feel: *manual* |
| Latency | the arithmetic and STATE fields; analyser stamps vs truth | — | real input and display latencies (needs hardware); `AUDIO CALIBRATE` on a real device |

Honest gaps: no audio device, no Windows and no network of machines here, so real capture timing, real WASAPI
behaviour, real Link sessions and LAN jitter are unproven until the bench rig runs them.

---

## 8. Desk and Companion surface

### Desk

- **Tempo strip** (always visible in the mode frame, beside readiness): `♩ 128.0` (drag to change, double-click to
  type), four beat dots and `17.3` (bar.beat) with a phrase ring, source chip `DETECT ✓ 0.86` / `TAP` / `LINK 3` /
  `MIDI` / `TRACK`, **TAP** (key **T**; Shift+T taps the downbeat), **RESYNC** (key **R**; right-click: beat / bar /
  phrase), **½**, **×2**, nudge **‹ ›** (hold = slower/faster), latency chip `62 ms` (click: the breakdown bar —
  input, analysis, network, light, minus acoustic = perceived). Every control's tooltip shows its verb.
- **Level meter chip**: input name, Level + Bass/Mid/High bars, peak LED, "silent 12 s" timer; click opens the drawer.
- **Audio drawer**: input list (Default input, System sound, Everything but PlayMap, devices, Track ▸ playlist, Test
  ▸ click/sweep/noise/drums), channel pick, gain/auto-gain, gate; a live 64-bin spectrum with the 16 bands and
  Patterns' three bands shaded; onset lanes (kick/snare/hat ticks); tempo panel (lock state, confidence, candidates
  128 / 64 / 256 as buttons, source, Link peers); role (listen / follow ▸ node) with each follower's late counter;
  listener distance.
- **Inspector sockets**: every bindable row has a `~` socket. Bound rows show a ring in the source family's colour
  and a thin live needle over the base handle (Resolume's input/output lines). The popover: source chips (Bass,
  Mid, High, Level, Kick, Snare, Hat, Onset, Beat pulse, Bar ramp, LFO ▸ ¼ · 1 · 4 · 16 beats, Random per beat,
  Track ▸, Named modulators, MIDI/OSC/DMX from control), Amount (bipolar), Mode, Curve, Smooth attack/release,
  Range, Steps, Invert, a 2 s scope of source and result, and the determinism badge ("shared from Stage-left" /
  "live — breaks mirror backup").
- **Drag a source onto a slider**: any meter bar, beat dot or modulator chip drags onto any slider → `MOD BIND` with
  amount 0.5 and 30/250 ms smoothing. The five-second gesture.
- **Right-click**: on a parameter → **Modulate ▸** Bass · Beat pulse · LFO ▸ · Random per beat · From track ▸ ·
  Remove · Copy/Paste modulation; on a generator, layer or surface → **Make it react ▸** Gentle · Punchy · On the
  beat · Off (`MOD AUTO`); on media or an item → **Analyse sound** · **Use for tempo** · **Listen to this playlist**.
- **PLAY**: items with a track show a BPM chip and beat ticks on their mini waveform; the GO button's menu offers
  launch quantisation (now / beat / bar / phrase).
- **Bind mode (B)** overlay: every bound parameter wears its source chip.
- **Show Mode** (showsafety): TAP, RESYNC, NUDGE, ½, ×2 stay live; binding edits go blind; PANIC → hold modulation.
- **Preflight** (showsafety): input present and signal in the last 10 s; tempo source alive; bindings to sources this
  node lacks; Live bindings in a mirrored group; analysis tracks present on every node for `track:` sources; playout
  delay ≤ 90 ms (BT.1359's acceptability) or the words to fix it.

### Companion (through the companion WP's generated module)

- **Actions**: `tempo_tap` (sends `TEMPO TAP AT <press unix ms on the show clock>`), `tempo_resync {beat|bar|phrase}`,
  `tempo_nudge` (press/release = faster/slower; or ±ms), `tempo_half`, `tempo_double`, `tempo_set {bpm}`,
  `enc_tempo` (rotary: ±0.1 BPM per detent, push = tap), `tempo_source {…}`, `audio_listen {input}`,
  `audio_test {click|off}`, `mod_bypass {on|off|toggle}`, `mod_trigger {modulator}`, `mod_auto {entity, preset}`.
- **Feedbacks**: `beat` (on for the first 25 % of each beat; option downbeat only) computed **in the module** from
  `bpm` and `beat_epoch_ms`, which change only when an anchor changes — beat-accurate with no traffic; `tempo_locked`,
  `tempo_source_is`, `audio_ok`, `audio_silent`, `mod_bypassed`; value feedback `level` for 5.1 gauge LEDs.
- **Variables**: `bpm`, `beat`, `bar`, `phrase`, `beat_epoch_ms`, `tempo_source`, `tempo_lock`, `tempo_confidence`,
  `audio_input`, `level`, `bass`, `mid`, `high`, `latency_ms`, `mod_bindings`.
- **Presets**: a *Tempo* page (big TAP key showing `$(playmap:bpm)` and flashing on the beat, RESYNC, ½, ×2, nudges,
  source cycle, BPM encoder); a *Meters* page (four gauge keys); an auto-generated *Modulators* page, one key per
  named modulator (bypass toggle; trigger for envelopes), rebuilt from INDEX when modulators change.
- Fallback if module-side timing is not wanted: `oscillate(60000/$(playmap:bpm),'square', phase)` — whether
  Companion's `oscillate` phase is wall-clock based is unverified [R:§9].

---

## 9. Risks and open questions

| # | Risk / question | Mitigation / proposal |
|---|---|---|
| R1 | **Live determinism depends on frames arriving within L** (Wi-Fi followers, congested LANs) | Auto L from follower reports, replay, `diverged` counters, preflight words; Live badge when keyframes or frames stop |
| R2 | **Tempo detection errors** (octave, rubato, live bands, breakdowns) | Confidence and lock states shown; candidates one click away; tap always wins and re-seeds the tracker's prior; holding through silence |
| R3 | **Link via Carabiner**: a user-installed GPL program; same-host clock assumption; maintenance pace unknown (release dates not checked) | Min-RTT clock mapping with a drift check; words when unreachable; the commercial Link licence remains a business option; a passive clean-room tempo listener later |
| R4 | **Process loopback needs Windows 11 / build ≥ 20348**; DRM streams are silent in loopback | "System sound" fallback with words; the docs say so |
| R5 | **Low-band latency** is physics (22 ms at 36 Hz) | Per-band delays in STATE; kick detection uses flux (fast) not the band envelope; beat-locked sources for tight hits |
| R6 | **Flash safety on bindings** cannot see the picture's luminance (a colour param on a full-field generator) | Luminous flags on opacity/brightness/level/mix/bypass; the limiter; Patterns' 0.25 swing on scenes; preflight lists luminous Live bindings |
| R7 | **Rate integrators are stateful** | Keyframes from the leader; solo nodes anchor at the binding; badge when keyframes are missing |
| R8 | **Analysis-track identity across CPU architectures** (float rounding) | Track hash recorded in the project; mismatch → fetch the leader's track (media transfer) rather than recompute |
| R9 | **Sound before picture**: reactive (non-beat) visuals stay 40–100 ms behind the electrical signal | Shown honestly with the acoustic delay; `track:` sources for played-back sound; beat sources for rhythm |
| R10 | **Hello cap budget** (16 caps; LED and audio push it to the limit) | Decide at spine time; one cap `tempo` |
| R11 | **Pro DJ Link** is unsanctioned and may break with firmware | Later, passive only, off by default |
| Q1 | Should `AUDIO LISTEN` write the project (logical input) or the node? | Proposed: project by default (the show says "listen to the system sound"), `HERE` for a machine-only override |
| Q2 | Default role in a sync group when several nodes have inputs | Proposed: the group leader listens; others follow; explicit `AUDIO ROLE` overrides |
| Q3 | Are tempo controls allowed while armed? | Proposed yes (performance controls), listed in showsafety's table |
| Q4 | Should STATE carry per-binding live values? | No (size, churn); `MOD PEEK` and the desk's popover read them on demand |

---

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| DSP primitives, analyser, auto-gain, frame types, Patterns vectors | 8 |
| Onsets, drum classes, tempo tracker, downbeat, tap, MIDI clock follower | 10 |
| Beat clock, anchors, slew, boundaries, `AT BEAT|BAR|PHRASE` | 4 |
| Modulation bus: providers, names, ticks, derived channels, replay, integrators, limiter, plan, bindings eval | 10 |
| Model, verbs, router, STATE, context, journal, risk hooks | 7 |
| Capture: hub, WASAPI backend, synthetic inputs, naming, roles and election | 6 |
| PMMD channel: codec, HMAC, redundancy, reports, keyframes, tempo packets | 5 |
| Analysis tracks: format, offline analyser, store, import step, track sources, track tempo | 7 |
| Render integration: overlays, reactive/fractal sound and phase fix, particles at steps, `IAudioFrames` | 7 |
| Carabiner link (S1) | 3 |
| Named modulators, `MOD AUTO` presets (S3–S4) | 4 |
| Desk: strip, meter, drawer, sockets/popover, drag-bind, menus | 10 |
| Tests, bench, papers | 6 |
| **Total** | **87** |
