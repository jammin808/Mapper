# Timecode chase

A playlist can follow timecode: each item starts at its TC in, and what the outputs show at every vblank is a pure function of the
code — `media(L) = In + (tc(L − delay) − S) · rate` — fitted from the code's arrivals, never integrated frame by frame. The verbs
are in `docs/wire/control.md`.

```
TIMECODE SOURCE ADD FOH LTC Focusrite:2
TIMECODE IN Main "Song 1" 01:00:00:00
TIMECODE CHASE Main FOH
TIMECODE SET Main DELAY 40
TIMECODE ENGAGE Main OFF        (the operator takes the playlist by hand)
```

## Sources

| Kind | Arrives on | Read by |
|---|---|---|
| LTC | an audio input and a channel, opened through the audio hub as `<device or name>@<channel>` (one open shared with the analyser), off the control loop | `LtcDecoder`, on the capture thread |
| MTC | a MIDI port (glob) | the port's parser and `MtcParser` |
| Art-Net | the node's Art-Net socket, a stream id 0–255 | ArtTimeCode |
| Internal | the node itself | `TIMECODE INTERNAL <source> PLAY\|STOP\|LOCATE\|RATE` |

A source's format is set (`FORMAT 25`, `29.97df`, …) or detected from the code. Positions are frame indices at the **real** rate:
at 29.97 drop frame `01:00:00;00` is frame 107 892 = 3 599.9964 s, and a drop-frame label that does not exist (`00:01:00;00`) is
refused with the first that does.

**LTC** (SMPTE ST 12-1, written from the standard; no LTC library is used or opened): a 10 Hz DC block, a peak envelope with a
floor (−46 dBFS by default), a Schmitt trigger at ±25 % of the envelope whose edges are timed at the interpolated zero crossing,
the bit period acquired from 32 intervals and tracked, biphase mark decoded, the sync word found forward and backward. It reads
24, 25, 29.97 (drop and non-drop) and 30 fps at any sample rate, ±10 % in speed, through 20 dB of noise; the level is reported per
frame (green ≥ −14 dBFS, amber to −26, red below). The tests' goldens are three 80-bit words laid out by hand from the standard's
bit table and an LTC file written by an independent writer (`tests/fixtures/ltc/make_ltc_wav.py`). An LTC frame's end is the next
frame's start; the audio's sample clock is mapped to the node's monotonic clock by the lower envelope of the reads (scheduling only
ever delays a read).

**MTC** (MIDI 1.0, MIDI Time Code): eight quarter frames make the code at piece 0; each piece is an observation at its own arrival;
a full-frame message is a locate (the code parks there); any device id, 7F always.

## The tracker and its line

Each source's observations become a reading (`TimecodeTracker`): its state — none, unavailable, silent, acquiring, rolling,
parked, reverse, dropout (no code for 2.5 frames), lost (1 s) — its counts (biphase, missing, duplicate, discontinuity, invalid,
bad BCD, parity; `TIMECODE SOURCE RESET` clears them) and its **line**: a robust least-squares fit of code against time over the
last 1.5 s of the run (outliers past max(4 × 1.4826 × MAD, 0.3 frame) dropped, then refitted; slope 0.85–1.15, else "varispeed out
of range" and nothing locks). A jump — code off the line by more than max(2 frames, 5σ) — is taken when the next observation agrees
with it; a lone outlier is counted invalid.

## The chase

The chase runs in the engine's lane tick. A playlist's items lay out as **regions** on the code: an item with a TC in starts one;
an item without follows the one before it (so one TC in lays out a whole list); an item that loops or holds runs until the next
region; a later TC in cuts the one before it short. For the active source (the first rolling or parked one of the chase's
sources, in order), `ChaseLogic` decides the lane:

| State | When | The lane |
|---|---|---|
| waiting / acquiring | engaged, no line yet | unchanged |
| locked | a rolling line | the region's item anchored at the line's own instant, at the line's rate × the item's; the next region scheduled to take over on its exact frame |
| parked | the code stands still (a locate, a repeated frame) | the item held at that code's frame |
| outside | rolling code between regions | black (or the last frame, `GAP HOLD`) until the next region takes over on its exact frame |
| freewheel | the source drops out | the last line rides on for `FREEWHEEL` (500 ms) |
| held / stopped / free | the freewheel ran out | `ONLOSS HOLD` holds where the line said, `STOP` goes black, `CONTINUE` runs on at the last rate |
| reverse | the code runs backwards | holds |
| disengaged | `TIMECODE ENGAGE … OFF`, or a panic step that moves the playlist's transport | the playlist plays on by hand from where it is, at its items' own rates (a panic's step then pauses or cues it) |

Within a region the lane is re-anchored only when the content would be off by a quarter frame or more, or the rate by 50 ppm,
at most four times a second; a region change takes effect at once. While a chase is engaged, `PLAYLIST GO/CUE/NEXT/PREV` and the
`VIDEO` clip verbs on its playlist are refused with "Main chases FOH (locked) — TIMECODE ENGAGE Main OFF to drive it by hand".
Which chases are disengaged, and what disengaged each (a panic), is kept for recovery. A `PANIC` whose outcome moves transport
(BLACK, HARD, REWIND, NEXT) disengages a chased playlist's chase at its step's instant, so the panic holds whatever the code does;
the chase's words say "disengaged by PANIC BLACK — TIMECODE ENGAGE Main ON to follow the code again" (after a restart too), and
`PANIC CLEAR` leaves it so. Caught between two items (`GAP BLACK`), the playlist is left with nothing loaded, so the next item does
not start under the panic when its code comes. A chase turned off just before the panic, whose guard the lane has not yet lifted,
lets go the same way. RUN, FREEZE and SAFE leave the chase engaged.

## STATE, context, preflight, INDEX

`STATE.timecode` lists the sources (state, code, format, measured rate, speed in ppm, the fit's scatter as `jitterUs`, level,
counts, words) and the chases (state, code, item, rate, delay, freewheel left, loss and gap rules, the last jump, words); each
playlist carries `chase`. A source's or a chase's state, format or words changing moves STATE's revision; the code's frames never
do (its text is written from the line when STATE is read), so STATE pushes do not rise to the frame rate. The context has
`timecode:<tc>` and `chase:<pl>` items; controllers hear the events `chase.locked` and `chase.lost` (the freewheel ran out and the
loss rule took over). Preflight's `timecode` section checks every chased playlist's source is present and rolling. INDEX's
`timecode` section lists the chased playlists with an Engage key.

## Not in this build

Timecode outputs (LTC, MTC, Art-Net), TC cues, triggered mode, backup-source switching beyond "the first rolling source", jump
preload, the timecode test card, `SUBSCRIBE TIMECODE`, `TIMECODE CALIBRATE`, LTC from a looping file, MIDI clock, and the
leader/follower relay of a chase across a sync group — the next round.
