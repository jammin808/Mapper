# Audio: listening, the beat clock and the modulation bus

PlayMap listens to one sound input, analyses it, keeps a beat clock, and lets any number of a generator, a layer, a surface or an
effect slot move with the sound, the beat or an LFO — live, while the show keeps the number's base value. The verbs are in
`docs/wire/audio.md`.

## Listening

- **Inputs.** `AUDIO INPUTS` lists what this node can hear. Every node has the test inputs (`test:click` — a 1 ms click on every
  beat of the show's tempo, the downbeat louder — `test:sweep`, `test:noise`, `test:drums`), so a show rehearses in a silent room.
  A sound file is an input too (`file:/path/show.wav`, looped at the pace of the clock). On Linux FFmpeg's capture devices are
  read (`pulse`, `oss`, `openal`, through libavdevice; there is no device on the build machine, and the list says why in words);
  on Windows WASAPI gives each capture endpoint, **System sound** (the loopback of an output) and **Everything but PlayMap**
  (process loopback, Windows 10 build 19041 or later), and FFmpeg's `dshow` through the same code path as Linux.
- **The show's choice and the machine's.** `AUDIO LISTEN` writes what the show listens to (`default`, `system`, a test signal, a
  device's name or key, a file); the name is matched again on another machine by Patterns' rule (case, spaces and the socket number
  Windows writes, "Microphone (2- USB Audio CODEC)" ≡ "(3- …)"). `AUDIO LISTEN … HERE` keeps this machine's own input in the node's
  settings instead, beside the show's.
- **One device, many listeners.** The input hub (`Core/Audio/AudioInputHub`) opens a device once whoever listens; each listener
  takes its own channels (`1`, `1+2` summed, `3-4` a pair, `M`/`S` mid and side), so the analyser on 1+2 and a timecode reader on
  channel 7 share one open. The hub is the node's `IAudioInputs` (`EngineServices.AudioInputs`): the timecode package opens LTC
  through it.
- **Observed, not assumed.** `AUDIO LISTEN` answers what the node then hears: listening (with the device's name, channels and rate),
  or retrying with the device's words. A missing or lost input is tried again every 5 s.

## The analysis

One analyser per listening input (`Core/Audio/AudioAnalyser`, pure, no allocation per hop): mono at 48 kHz (other rates are
resampled), a hop of 240 samples (5 ms, 200 frames a second), each frame over the last 1024 samples:

| What | How |
|---|---|
| Level, Bass, Mid, High | Patterns' `Spectrum.Analyse` exactly: Hann (n − 1), real FFT 1024, \|X\|·4/n, bands 20–250 / 250–2000 / 2000–8000 Hz, √Σmag²·1.5, level RMS·2.5, clamped 0–1 — a look reads the same in both apps |
| 16 bands | RBJ band-pass biquads on edges 30·1.4806^k Hz (30 Hz–16 kHz), Q 2.53; a band reads unity at its centre and its neighbours ≈ −7 dB; the power averaged over 1.5 periods of the centre |
| Spectrum | 64 log bins 30 Hz–16 kHz, 0–1 over −80…0 dB |
| Peak | the hop's largest sample in dBFS |
| Onsets | log spectral flux with a 3-bin maximum filter; causal peak picking one hop late: the largest of its neighbours, above the 200 ms mean by the largest of a floor, 0.15 × the slow peak and five standard deviations; 30 ms apart at least |
| Auto-gain | each number's twin (`…/n`) divided by its slow peak (10 s release), floored at the gate; `AUDIO GAIN FIXED` makes the twins the plain values |
| Gate | below `GateDb` (−60 dBFS) for 300 ms the frame is silent |

A frame stands for the middle of its window and is stamped in leader time: the capture's own sample count is fitted to the
monotonic clock at the lower envelope of "read instant − sample time" over the last 2 s (a block read late says nothing about when
its sound arrived), minus the input's latency. STATE says the analysis delay (half a window, 10.7 ms) and, when the node's settings
give it, the input's.

## The beat clock

The beat is a pure function of leader time: anchors `beat(t) = Beat + (t − LeaderNs)·Bpm/60e9`, beat 0 at leader 0 on every node.
Every change is a new anchor one lead (40 ms) ahead of the line, or at the instant `AT` names (`AT +ms`, a Unix ms, or the next
`BEAT`, `BAR` or `PHRASE`), so every node holding the anchor moves at the same instant:

- a tempo change (`TEMPO SET`, `HALF`, `DOUBLE`, taps) carries the beat on continuously;
- a resync makes the press the next line of the beat, bar or phrase (the count never steps back), seen one lead later;
- a nudge moves the phase by slewing the tempo at most 2 % for as many bars as it takes, then returning — beat time never runs
  backwards; `FASTER`/`SLOWER` hold ±4 % for 2 s;
- taps take the last eight intervals, leave out those more than 30 % off their median, average the rest, and put the last tap on
  a beat; more than 2 s between taps starts again.

The show's tempo and meter are project settings (undoable); the clock follows them when they change without a TEMPO verb (an
undo, a loaded show).

## Tempo detection

`TEMPO SOURCE DETECT` follows the sound (`Core/Audio/TempoTracker`, written from the published descriptions of comb-filtered
autocorrelation and beat scoring — Ellis 2007, Davies and Plumbley 2007 — with no tracker's code): the last 8 s of the onset
strength; every 0.5 s its autocorrelation summed at 1–4 periods for each tempo in the window (75–180 BPM by the show's settings,
0.05 BPM steps), weighted by a log-Gaussian prior an octave wide around 120 BPM; a new tempo replaces the held one only when it is
1.2 × as salient on three updates running; the beat's phase from the strength summed at the period back from now. Drum and click
loops at 90–174 BPM are found within ±0.5 BPM, on the right octave, their beats within 15 ms, inside 6 s (tests).

Two sure estimates running (confidence ≥ 0.3) lock the clock: a tempo off by more than 0.2 BPM or a beat off by more than 10 ms is
corrected one lead ahead — slewed (at most 2 % for whole bars) within a quarter beat, jumped beyond it; a correction still slewing
is let finish. Silence holds the clock (it keeps counting at the locked tempo); 32 bars of it is `lost`. The lock states are
`searching`, `locked`, `holding`, `lost` (`manual` under `TEMPO SOURCE MANUAL`). The show's tempo is not written: detection is
this node's, and `MANUAL` brings the show's tempo back one lead ahead.

## The modulation bus

`Core/Modulation/ModulationBus` implements `IModulationBus` and `IAudioFrames`. Its sources:

| Source | Value | Determinism |
|---|---|---|
| `audio:level`, `audio:bass` (`audio:low`), `audio:mid`, `audio:high`, each with `/n` | Patterns' numbers; `/n` auto-gained | live |
| `audio:band/<0-15>[/n]`, `audio:fft/<0-63>`, `audio:peak`, `audio:flux`, `audio:centroid` | the frame's numbers | live |
| `audio:onset` | 1 at an onset, decaying with a 120 ms time constant | live |
| `beat`, `bar`, `phrase` | the phase 0–1 within the beat, the bar, the phrase | pure |
| `beat:pulse[/<ms>]`, `bar:pulse[/<ms>]` | 1 on the line, decaying (100 ms) | pure |
| `bpm` | 0–1 over 0–240 BPM (raw: the BPM) | pure |
| `lfo:<shape>/<period>[b][/<phase>]` | sine, triangle, saw, ramp, square, pulse, random; the period in seconds or beats | pure |

For ISF audio textures and FFGL buffers, `IAudioFrames` answers the spectrum (the frame's 64 bins, resampled to the bins asked)
and the mono waveform (the listener keeps the last 2 s) at a frame's instant: the samples that end there, or the newest ones when
the frame is drawn for an instant ahead of the sound that reaches it; nothing while the node listens to nothing or once the sound is
more than 1 s old.

Any source may end in `~<attack ms>[/<release ms>]`, or a binding may give `SMOOTH`: a one-pole follower on a fixed 5 ms grid of
leader time, never per render frame, so 50, 60 or 144 frames a second read the same value at the same instant. It runs as two
filters restarted in turn at absolute multiples of a span (at least 2 s and 16 time constants), the output taken from the one that
has run longer: its value depends only on the source since its restart, so a second reader (the lane's view for `MOD PEEK`) or a
node joining late replays that span and agrees. A source the node lacks (sound while listening to nothing) is missing: the number
holds its base and STATE names it; sound that stopped more than 1 s ago reads silence.

**Live** sources are this node's sound: a mirror backup does not hear them. Sharing the listener's frames with followers is next
round.

## Bindings

A binding (`ParamBinding`) is a project edit: `MOD BIND` writes it (one per parameter, undoable), `MOD UNBIND` removes it. Each
frame the render host's compose hook resolves the show's bindings once per project (`ModulationPlan`: ranges, bases, sources) and
hands the renderer a project with every bound number at its live value and the bindings taken off — the store keeps the base value
and its revision never moves for modulation. The rule:

u = curve(clamp(invert(s) − Offset, 0, 1)), quantised to `STEPS` levels, then **ADD** base + Amount·u·(max − min), **SCALE**
base·lerp(1 − |Amount|, 1, u), **REPLACE** the range's own min + u·its width (`RANGE` as fractions of the parameter's); the result
clamped to the parameter's range. Curves: linear, ease, step, exponential, logarithmic, S.

What binds: a generator's numbers (a seed, a count, a choice or a colour does not), a layer's `opacity` and `rect.x/y/w/h`, a
surface's `opacity`, an effect slot's `mix` and its kind's numbers (from the effects catalogue: built-ins and the show's ISF files).
`MOD BYPASS` holds every number (or one entity's) at its base on this node, without touching the show.

## Preflight, context, INDEX

- Preflight section `audio`: when bindings read the sound, a listening input that is heard — red with `AUDIO LISTEN DEFAULT` when
  the show listens to nothing, red with the device's words when it cannot open, amber when silent more than 10 s; green "not
  needed" when no binding reads the sound.
- Context items `audio` (red when bindings listen and nothing is heard; amber silent, opening or retrying; green listening; grey
  off) and `tempo` (green once the clock is in use).
- INDEX section `tempo`: one row, with keys TAP, RESYNC (the bar), HALF, DOUBLE and BPM ±1 for an encoder.

## Measured, not governed

No governor is added this round: there is no flash limiter on audio-driven numbers (STATE `modulation.flashLimited` is −1,
unknown), and no budget on the bus. The analyser's per-hop cost and allocation are measured by its tests (no allocation per hop).

## Next round

Shared live frames to followers (PMMD on UDP 9945, HMAC keyed from pairing) so every node reacts to the same sound (and
followers adopt the listener's detected anchors); analysis tracks at import (`.pmaa`) and an offline tracker; downbeat detection
(a detected lock keeps the count, the bar line is a resync); Ableton Link
only through a Carabiner process the user installs (GPLv2+, never bundled); MIDI clock in; named modulators (LFO, envelope,
steps, random); `MOD AUTO` presets; launch quantisation (`AT BEAT|BAR|PHRASE` on playlist verbs); discrete audio inputs for
control bindings; `AUDIO CALIBRATE`; a zero-allocation overlay for generators (content's render path) in place of the per-frame
project; RATE bindings.
