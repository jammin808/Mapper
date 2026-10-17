# Optimise on import

A clip the operator drops in becomes the version this node plays best, in the background, without touching the show or the source.
PlayMap makes HAP, HAP Alpha, HAP Q and HAP Q Alpha versions with its own GPU encoder, keeps HDR sources as they are, and
plays a version from the item's next open. The wire is `docs/wire/optimise.md`.

## The parts

| Part | Where | What |
|---|---|---|
| Planner | `Core/Media/Optimise/OptimisePlanner.cs` | Pure: probe facts, tags, the project's settings and the target nodes → keep, encode, ask or never, with the recipe, the rule and its words |
| Recipe | `Core/Media/Optimise/Recipe.cs` | The target, quality, chunks, second stage, raw threshold and audio; canonical JSON (sorted keys, invariant numbers); key `ok:` + 32 hex of SHA-256 over source hash, recipe and the encoder algorithm (`hapenc/1`) |
| Version choice | `Core/Media/Optimise/VersionChoice.cs` | Pure: which file an item plays here (below) |
| Governor | `Core/Media/Optimise/Governor.cs` | Pure state machine: run, trickle, paused, waiting |
| Encoder | `Render/Encode/GlHapEncoder.cs`, programs `hapenc.bc1/.bc3/.ycocg/.bc4` | GL 3.3 core or ES 3.0 fragment programs, one texel per 4×4 block into an RGBA32UI target, in tiles of at most 256 × 256 blocks |
| Packer | `Core/Media/Optimise/HapEncoding.cs`, `SnappyCompressor.cs` | Chunks on block boundaries; each chunk Snappy-compressed (PlayMap's own compressor, written from the published format description) or stored raw when Snappy saves under 10 %; the frame through decode's `HapFrameWriter` |
| Transcode | `Media/Optimise/HapTranscode.cs` | Decode in order, RGBA through the frame's own matrix and range, encode, mux to `.mov` with the source's timestamps and its audio stream-copied; then read back |
| Service | `Engine/Optimise/OptimiseService.cs`, `JobStore.cs`, `OptimiseSupervisor.cs` | The queue, the worker's life, the governor loop, recording |
| Worker | `Engine/Optimise/OptimiseWorker.cs`, entry `optimise-worker` | Its own process and GL context |

## The rules

The show's own rules (`Project.Optimise.Rules`) are consulted first, then the built-in table in this order; the first that matches
decides. A rule that names a target encodes under the project's mode `auto`, waits for a person under `ask` (the default: imports
queue nothing; `MEDIA OPTIMISE` runs the rules by hand) and does nothing under `never`.

| Rule | When | Decision | Words |
|---|---|---|---|
| `hap-keep` | the codec is HAP-family | keep | already a GPU codec |
| `hdr` | PQ, HLG or linear transfer, or ≥ 10 bits in BT.2020 | keep | HDR source (PQ): kept as it is — a HAP HDR encoder arrives next round |
| `stills` | an image | keep | a still uploads once |
| `alpha` | a video with alpha | HAP Q Alpha | prores with alpha: HAP Q Alpha keeps it on the GPU |
| `long-gop-hw` | long-GOP, every target decodes it in hardware | keep | every target node decodes it on the GPU |
| `long-gop-small` | long-GOP below 1920 × 1080 | keep | 1280×720 h264 decodes on the CPU at little cost |
| `long-gop-cpu` | long-GOP, 1080p or larger, a target decodes it on the CPU | HAP Q | node a1b2c3d4 decodes this h264 on the CPU: HAP Q plays on the GPU |
| `cpu-intra` | ProRes, DNxHD, CineForm, MJPEG, QuickTime Animation, PNG, TIFF, FFV1, … | HAP Q | prores decodes on the CPU here: HAP Q plays on the GPU |
| `fallback` | anything else (image sequences included) | keep | no rule asks for a change |

The target nodes are this node alone: the block families its renderer samples and no hardware decoder (proving those is
wp2-zerocopy's decode-path ladder), so long-GOP at 1080p and larger becomes HAP Q here.

## Jobs

`NodeHome/optimise/jobs.json` holds the queue, written whole through a temporary file and a rename on every state change. A job is
`queued → encoding → verifying → done | failed | cancelled` (planning happens when the job is asked for, before it is queued); PAUSE holds a queued job back and a running one between frames. One job
runs at a time, oldest first, through the worker:

- **The worker** is the same exe in a third role, `PlayMap optimise-worker` (the node host's `PlayMap.Node optimise-worker` in
  tests). It lowers its own CPU priority (below normal, `OptimiseOptions.WorkerPriority`, so the encode yields the CPU to the air; the
  test rig runs it at the suite's own priority), makes its GL context through the platform seam (hidden, or EGL surfaceless on Linux with no
  display), and speaks one JSON object a line on its standard input and output (run, knobs, cancel, stop; hello, progress, done,
  failed). It ends when its input closes, and after `IdleExitS` (120 s) with nothing to do.
- **A worker that will not end.** Asked to end, a worker has `StopGrace` (5 s) and is then killed; a killed worker has
  `KillWait` (5 s) to go. One that has not (a thread held in a GPU or codec driver, a disk that went away) is journalled — "the
  optimise worker (pid …) has not ended 5 s after it was killed (a driver or a disk holds it): the node goes on without it" —
  and the node goes on: its stop, the recovery record and the journal's flush never wait on it. A stop kills only the worker it
  asked to end; a job that comes while a worker is on its way out goes to a fresh worker at once.
- **A worker that stops** (a crash, a kill) is started again after a backoff (1 s, doubling with each crash in a row, at most 30 s; a version made resets it); its job starts again from
  its first frame. A clip that stops the worker `MaxAttempts` (3) times fails with "the worker stopped (exit code …) 3 times on
  this clip — it is not optimised". A node that restarts mid-job queues the job again with its words.
- **Verify.** After the trailer the worker reads its file back: every packet must parse as a HAP frame of the codec, and the first,
  middle and last frames, decoded by FFmpeg's own HAP decoder, are compared with the source's in PSNR over RGB (and over alpha, for HAP Alpha and HAP Q Alpha: the lower counts). A read error is an error, never the end of
  the file, and a version shorter than the container's own frame count fails. The engine then
  refuses a version whose lowest PSNR is below `MinPsnr` (30 dB) with "lowest 28.4 dB at 1.2 s is below this node's floor of 30
  dB — the version is not kept", hashes the file, probes it, moves it into the node's media store by its hash and records the
  `MediaVersion` on its item as one edit ("optimised Clip → hap-q"). When a blind commit waiting for its boundary lands that
  item, its guard refuses the edit: the job stays verifying ("recorded when the blind commit lands: …") and the edit is made once
  no commit waits (it landed or was discarded); a lock the operator set still fails the job with its words.
- **Audio** is stream-copied into the `.mov` with its own timestamps when the muxer takes the codec (AAC, PCM, ALAC among them);
  otherwise the version is silent and its words say so (decoding to PCM is next round).
- **Cancel** kills a running job's worker and deletes its partial file; a cancelled or failed job leaves no file, and the work
  folder is swept of every partial whose job is not running — whenever a worker's end is reported, and when a worker replaced on its
  way out has gone (on Windows a killed worker's files can be deleted only then). A clean stop of the node leaves a running job
  queued for its next start.
- **The stop.** The service closes before it stops its worker: a worker's line or end, a sweep, a line or a step that comes later
  does nothing — a step under way hands out no job and starts no worker — and a request, CANCEL, PAUSE or RESUME is refused with
  "the node is stopping — its optimise queue takes no more changes". The work begun before the close (a write of the queue, a
  recording, a plan, a worker's end being heard) ends before the worker is stopped — a recording the stop cuts short leaves its
  job verifying, queued again at the next start. Once the stop is over nothing of the service's writes the queue or the work
  folder; only a killed worker that would not end (above) may still write its partial file there, swept at the next start.
- **The import hook.** When an import's probe lands (an item gains its content hash), the item is planned under the project's mode.

## The governor

Stepped once a second from what the engine already publishes (outputs' dropped frames, open decoders' late frames, the LED
sender's dropped and skipped frames and its late nodes); its thresholds are `NodeSettings.Optimise`.

| State | When | The worker | Words |
|---|---|---|---|
| waiting | the show is armed | holds between frames; nothing starts | optimise waits: the show is armed |
| paused | an output or the LED sender dropped a frame, or a source or an LED node was late, while a job ran, for `PauseAfterDropS` (60 s) after the last | holds | paused for 60 s: 1 frame dropped while it ran |
| trickle | something plays, and until 10 s after it stopped | a rest after each frame so the frame's whole cost (demux, decode, encode, mux) is `TrickleGpuDuty` (15 %) of the time; a job started at trickle also decodes in software on one thread | a show plays: one stream, GPU ≤ 15 % |
| run | idle | no rest; a job started at run decodes on every core but one, hardware decode allowed | |

A job's decoder threads and hardware decode are fixed when it starts; the rest, the hold and the pause apply to a running job from
its next frame. The read-back after the trailer decodes only the three sampled frames.

`Role: never` in the node's settings makes a node that never encodes.

## Playing versions

`MediaBackend.Open` asks `VersionChoice` which file to open, at each open. With `use: "auto"` it takes a version whose codec this
GPU samples, not marked for review, whose file is in this node's store — the highest measured quality, then the newest — else the
source. `"source"` plays the source; a version id plays that version when this node can (else the source, and `MEDIA VERSIONS` says
why). The air's sound reads the source; when the source does not resolve here and the version that plays has sound, it reads
the version's file. A change reaches the air at the item's next open: an open decoder keeps what it opened (the render host's source slots compare
an item without its versions and use).

## Measured here

On this machine (4 cores, Mesa llvmpipe, FFmpeg 9), testsrc2 at 960 × 540, both encoders' files decoded by FFmpeg's HAP decoder:

| | PlayMap (Show) | FFmpeg's `hap` encoder |
|---|---|---|
| HAP, clean | 39.25 dB | 38.94 dB |
| HAP Q, clean | 46.36 dB | 45.98 dB |
| HAP, noisy (`noise=alls=12:allf=t`) | 33.75 dB | 33.31 dB |
| HAP Q, noisy | 38.13 dB | 37.49 dB |

A 2 s 1080p MPEG-4 clip with AAC becomes HAP Q at 43.5 dB average through the real worker; ffprobe reads the version's audio start
and duration equal to the source's.

## Next round

`MEDIA RULE MODE ASK` with its suggestions (`STATE.media.optimise.suggested`, the accept line); the lossless rewriter (`rechunk`,
`strip`, `snappy`); `MEDIA ESTIMATE` and the rate table; `MEDIA VERSION REMOVE` and `MEDIA PRUNE`; rule editing (`MEDIA RULE ADD`,
`REMOVE`); keyframe-aligned segments and resume at a segment; `MEDIA FETCH` of each node's own pick with dedupe by key
(`MEDIA DERIVED`); the farm over idle peers; stale versions; posters and the scrub strip (`IPosterStore`); watch folders;
hardware long-GOP targets; VMAF; HAP Alpha-Only, HAP R and HAP HDR from our own encoder (BC7, BC6H); image sequences as sources;
audio decoded to PCM when the muxer refuses its codec; the disk token bucket and I/O priority.
