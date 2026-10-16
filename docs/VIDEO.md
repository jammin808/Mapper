# Video I/O: NDI and OMT in and out

A PlayMap node receives network video as live inputs and sends any point of its pipeline as a network source. Two protocols:
**OMT** (Open Media Transport — libomtnet and its VMX codec, vendored, so it works on every node out of the box) and **NDI**
(through the NDI 6 runtime the user installs; PlayMap ships none of it). **Spout** (Windows) registers beside them: GPU textures
shared with other apps on the same machine (see *Spout* below).
The verbs, STATE, the context, events, INDEX and preflight are in `docs/wire/videoio.md`; the design is
`docs/round2/design/netvideo.md`.

## A live input

A live input is a library item: `MediaItem { Kind = Stream, Location = "omt:STUDIO (Cam 1)" }` with optional `LiveSettings`
(`Proxy`, `Depth`, `Warm`, `Backup`, `OnLoss`, `Fallback`, `StaleMs`, `LostMs`, `RecoverMs`, `Tally`, `Quality`). Anything that shows a
media item shows it: a surface, an output in source mode, a playlist item, a preview, a scan camera. A live input is pictures
only: no sound is received (NDI's and OMT's audio frames are not asked for), so it never sounds on the air, and it is a stream,
never a clip optimise makes a version of; its fallback file, and a file a send taps, are media the show uses (preflight checks
them, optimise counts them).

- **One receiver per input.** `MediaBackend.Open` routes NDI and OMT locations to the engine's `VideoIoHub`, which keeps one
  receiver per input and lends every consumer a non-owning `LiveHandle`; three surfaces and a thumbnail are still one network
  stream. A warm input (the default) connects when it enters the show and stays connected while it is in it; a cold one connects at
  its first consumer and closes a second after its last lets go. An input that leaves the show is closed once nothing holds it (a camera rig's
  feed lives while its consumer does, and follows the rig to another source).
- **Receiving.** Each receiver has its own thread (OMT `Receive`, NDI `recv_capture_v3`, 50 ms waits) that copies the frame's planes
  into a `LiveFramePool` (lock-free, reference-counted slots: the writer takes a slot no reader holds, a reader's lease keeps its
  slot; a frame with every slot held is dropped and counted) and frees the protocol's buffer at once. Layouts arrive packed — UYVY,
  UYVA, P216, PA16 (`LivePlanes` documents each plane) — or BGRA. NDI delivers an interlaced source as its fields, upper first:
  they are woven into one frame of twice a field's height and published once both are in (a lone field is dropped and counted).
  Colour follows the frame's `ndi_color_info` when it carries one, else NDI's rule by size (BT.601 up to 720×576, Rec.2020 above
  1920×1080, Rec.709 between); OMT says its colour space on every frame. The GPU unpacks them (`video.unpack`: UYVY as an RGBA8 texture
  of w/2 × h, P216 as R16 luma and RG16 chroma, alpha as R8 or R16; BT.709 limited range, an odd pixel's chroma the mean of its
  pair's and the next) into the picture the renderer samples; `PackReference.Unpack` is its CPU twin.
- **Health.** The state follows the age of the newest frame on the node's monotonic clock: `ok`, `stale` past three periods of its
  own measured rate (or `StaleMs`), `lost` past `LostMs` (1 s), `failed` with the protocol's words. `INPUT RECONNECT` closes and
  reopens the receiver (refused while it delivers, and for a cold input nothing shows) and answers at the first frame, or is refused
  with the protocol's words when it will not make the receiver.
- **Tally.** The hub sees every consumer: an input the renderer drew into the air (an output or a send) in the last 250 ms is on
  program unless the air is blacked out (the frame's last pass marks the end of its air); one drawn otherwise (the desk's editor and
  projector views, any other consumer that holds the input) is on preview. The sender is told on change (NDI `recv_set_tally`, OMT
  `SetTally`), and the feed not in use is told it is on nothing.
- **The loss policy.** The feed in use is lost when its receiver says lost or failed, when it could not open, or when it has given
  no frame `LostMs` after it opened. A **backup** feed (`INPUT BACKUP`, warm beside the primary) takes over when the feed in use is
  lost and the other delivers — a pointer swap at each consumer's next acquire, journalled and raised once. A switch only ever
  leaves a lost feed, so an input never flaps on the air: a primary that recovers while the backup delivers waits for a person
  (`INPUT SWITCH … PRIMARY`, answered at the next frame from it); a backup that is lost while the primary delivers hands back to it. With no feed to take over, the input **holds** its last picture, shows **black**, or shows its **fallback** (a media
  item, a generator, a canvas, a playlist or a colour — never another live input). The renderer is routed there through
  `ISourceProvider.Route`, for a stream item, a live ref and a playlist's live item alike. A held picture is left at the first
  steady frame; black and a fallback after `RecoverMs` (2 s) of steady frames.

## A send

A send (`VideoSend`, id `sd…`) taps one point of the pipeline — `output:<id>` (the output's final picture), `canvas:<id>` or
`source:<ref>` — and announces it under a name (`HOST (Main out)`).

- **Pack and read back.** `SendStage` runs once a frame after every output is drawn and before any present. On the leader-time grid
  of the send's own rate (frame k = ⌊leaderNs·N / (D·10⁹)⌋, so nodes on one show clock send on one grid) it scales the tap to the
  send's size (fit with bars, fill, stretch), packs it on the GPU (`video.pack`: UYVY, UYVA, P216, PA16 or BGRA, rounded half up;
  16-bit samples as little-endian byte pairs in an RGBA8 target, exact on every GL context) and queues a read-back into a ring of
  four pixel-pack buffers with fences. Finished read-backs are offered to the sender without copying: the mapped buffer is the
  frame, held until the sender releases it (NDI's async send keeps it until the next send). Nothing is allocated per frame once warm.
- **Nobody watching costs nothing.** A send packs and reads back only while a receiver is connected, unless `ALWAYS`; frames skipped
  are counted. A frame due while every slot is still with the sender is counted busy, never waited for.
- **What the air shows.** An output tap is the output's final raster, after its output stage (warp, mask, blend, the measured
  correction an align run made, trim), so blackout, freeze, the test pattern, a safe look, the master and panic gain and covered
  or held outputs reach the send exactly as they reach the projector; a disabled output's tap sends black, and so does the tap of
  an output on a raw frame (a scan or an align run's pattern is not the show, and a send's light must not spoil the camera's
  capture: that output's LED fixtures go dark too). An output on a raw frame decides nothing for a canvas or source tap, which
  plays on as the rest of the LED rig does. A canvas or source tap that follows the air (`FollowAir`, the default) takes what
  the outputs do to the show, each output decided as it decides itself (a hold first, then a safe look, then the test pattern, else the live show): the live show at
  the brightest gain of the outputs that show it (its signal clamped and scaled as an output's final stage does; the key is the
  tap's own), while any does; else a hold, while any output holds (its last frame again when that frame is the air's: a send nobody watched as
  the hold began packs once and holds that); else the pattern or the safe look when every output shows it,
  drawn as the outputs draw it (as its source is, through no effect) and stretched over the picture; else black (a mixed panic plan). With no output to decide — none on (a node that only sends), or
  every one on a raw frame — it takes the master alone, as the LED rig does. A send larger than the GPU's textures is refused in words, before
  any receiver connects, and never packed.
- **Senders.** Each sender has a worker thread: the newest frame wins (an older one waiting is released and counted dropped), the
  frame is sent (OMT encodes VMX; NDI `send_send_video_async_v2`, each YUV frame tagged BT.709 so no receiver guesses its colour from
  its size — a BGRA frame is RGB, which the runtime converts by its own rule, so it carries no tag), and an idle sender releases what
  it holds after 200 ms. Receivers
  and tally are refreshed every 250 ms. The node's sends are held to a **bandwidth guard** at `SEND ADD`, `SEND ENABLE ON` and any edit of a send.
- **Stopping.** A send that stops (disabled, removed, its tap gone, the node stopping) sends one black frame of its last shape
  before its sender closes, as a stopped output and the LED rig go dark: most receivers keep a lost source's last frame. A send
  made again under its own name (a new size or format) hands over with no black between. Once the node has begun to stop no
  receiver or sender starts, and the render thread drawing on while the hub closes under it offers its frames to senders that let
  them go: nothing throws and no fault is journalled.
- **One air for a group, none for a draft.** A send reads only its frame's air, so a sync group's member sends what the group's air
  makes of its outputs (the leader's blackout, master and panic run on it as lines); its frames fall on the leader's time grid. A
  send or input staged in a blind draft is not sent or received until the draft lands: the hub reads the live project only, and
  the draft's views are drawn with no pass at all.

## OMT

`src/ThirdParty/libomtnet` is libomtnet at 7d29031 (MIT, its licence beside it), built as `PlayMap.ThirdParty.Omt` with the
third-party props (analyzers off). `PATCHES.md` lists every change: P1 no avahi daemon (discovery runs without one and says
`mdns:"none"`), P2–P3 IPv4-only hosts (sockets fall back to IPv4 when IPv6 is off), P4 the discovery server logs through
`OMTLogging`, P5 settings in memory (no file written next to the exe), P6 `Thread.Abort` replaced by a stop flag and a join, P7 a
deadlock disposing a sender with receivers connected. `src/ThirdParty/libvmx` is the VMX codec's C++ source (MIT), unpatched; the
native library is loaded at run time from `PLAYMAP_VMX_DIR`, the exe's folder, `runtimes/<rid>/native`, then the system, and the
tests build it with clang from this source. OMT's storage path is the node home's `omt` folder.

## NDI

PlayMap ships no NDI binary and links none. `NdiRuntime` loads `libndi.so.6` / `Processing.NDI.Lib.x64.dll` / `libndi.dylib` at run
time from `PLAYMAP_NDI_DIR`, then the runtime's own `NDI_RUNTIME_DIR_V6`, the exe's folder and the system's places, and binds its
exports as unmanaged function pointers over blittable structs transcribed from NDI's MIT-licensed headers (their notice is kept in
`NdiNative.cs` and in `THIRD-PARTY-NOTICES.md`). A runtime that is missing refuses with words that say where it looked, where to
get one (https://ndi.video) and the variable to set. The Discovery Server and groups come from the node's settings
(`VIDEOIO SET NDI-DISCOVERY`, `NDI-GROUPS`, applied at the next start) through the runtime's configuration file in the node home's
`ndi` folder, written before the runtime is loaded; with a 6.3 runtime or later the send and receive advertisers are created. Colour info (BT.2020, PQ, HLG) is read from
the frame's metadata. The tests run against `tests/native/fake_ndi.c`, PlayMap's own test double compiled in test setup.

NDI® is a registered trademark of Vizrt NDI AB.

## Spout

Spout 2 shares GPU textures between apps on one Windows machine. PlayMap's provider (`src/PlayMap.Media/Video/Spout`) speaks Spout
2.007's protocol over two seams — the kernel's named objects (`ISpoutKernel`) and a Direct3D 11 device (`ISpoutGpu`) — and is
registered in videoio's lists by the output part when `NodeSettings.VideoIo.SpoutEnabled` is on. Off Windows it is registered
unavailable, so `SEND ADD SPOUT …`, `INPUT ADD spout:…` and `INPUT DISCOVER SPOUT` are refused "Spout is Windows only".

- **Discovery** reads the sender list (`SpoutSenderNames`: 256-byte slots, the names in order) under its mutex — Spout's 67 ms wait,
  on the provider's own timer, never the frame path — and each sender's 280-byte info map (share handle, size, DXGI format, usage,
  the sender's exe path, partner id). `spout:*` is the active sender.
- **Receiving** (`SpoutReceiver`, on the input's own thread, a step every 2 ms — every 250 ms while no sender of the name exists —
  never blocking): the info at 0 ms on its mutex (its
  texture reopened when the handle, size or format changes); then **in Spout's order** the access lock first — the texture's keyed
  mutex at key 0 where it has one, else `<sender>_SpoutAccessMutex` at 0 ms; inside it the frame counter (take one, give it back,
  the count before; nothing given back when nothing was taken), a count equal to the last copied one being not new; then `CopyResource` into one of three staging textures,
  the lock released, `<sender>_Sync_Event` set, and only then the count committed — a busy lock or a failed copy leaves it, so the
  next step copies frame N. A copied frame is mapped one step later with `DO_NOT_WAIT` (a copy still in flight is tried again, never
  waited for) into the input's frame pool as BGRA or RGBA. B8G8R8A8 and R8G8B8A8 senders are received this round (format 0 and the
  D3D9 formats read as B8G8R8A8); a sender's texture on another GPU is refused with the words. Spout has no tally.
- **Sending** (`SpoutSender` behind a `SendSink`): the published name is `PlayMap <node> - <send>` (printable ASCII; anything else is
  `?`). Registering inserts the name in the sender list under its mutex (refused when a live sender already has it, naming its exe),
  makes the active sender when none is, makes the shared B8G8R8A8 texture, its info map (partner id 0: a DirectX sender), the
  access mutex, the frame counter (a semaphore from 1), the sync event and the data map. Each frame is the frame the **send stage**
  hands every sender of that tap — so a Spout send follows the air as outputs show it: black while blacked out, held while frozen, a
  pattern's black, the master applied, a blind draft never in it — converted to BGRA on the send's worker (UYVY and UYVA by BT.709,
  P216 and PA16 by their high bytes) and uploaded: the access mutex tried for up to 20 ms on the send's worker (a receiver still
  holding it skips the frame, counted dropped with words), the counter given net one, the mutex released, the event set, and PlayMap's facts written to `<sender>_map`
  (`{"pm":1,"node","send","rate","tap","frame","leaderNs","w","h"}`). Spout shows no receivers, so a send counts one connection (its
  shared texture) and sends whenever it is on; stopping sends one black frame and removes the name.
- **The GPU target** (`IGpuSendTarget`, videoio's GPU send provider) gives a renderer the shared texture to draw into, made on the
  renderer's own Direct3D 11 device when it has one (`IGpuShare`), with the same lock and counter (a frame of another size is
  skipped and counted, never remade on the frame path); videoio's send stage drives the packed path this round, so Spout sends go through it.
- **Spout's device** is its own Direct3D 11 device (the default adapter; WARP on CI): the renderer is OpenGL on `auto`, and the shared
  textures are legacy share handles, as Spout publishes them.
- A PC set to Spout 2.006 memory share (SpoutSettings) is said in STATE: PlayMap shares GPU textures only.

## Verified here, and in the lab

Verified on this machine: the GPU pack and unpack of every layout against the CPU twin on GL 3.3 core and GLES 3.00 (llvmpipe); OMT
for real over loopback, lock-step at 1080p in UYVY, UYVA, BGRA, P216 and PA16, with tally, quality and the 1/8 preview; the engine
sending an output and receiving it as an input; discovery with no avahi daemon; NDI against the fake runtime (loader, find with
groups and the discovery server, receive, the async send ring, tally both ways, advertisers on 6.3 and not 6.2, colour info); the
loss policy and backups on a fake provider and a hand-moved clock; Spout's protocol against a fake kernel (the info map, the sender
list, Spout's order on both sides, the busy-mutex case, a failed copy, a read-back in flight, keyed textures, a sender that left) and
a Spout send following the air on the real render host. On the Windows CI job (WARP): a sender and a receiver round-trip a 64×36
chart through two WARP devices and the kernel's real named objects. Opt-in: `NdiRealRuntimeTests` with `PLAYMAP_NDI_DIR` pointing
at a real NDI 6 runtime (120 frames sent and received back).

The lab checklist (two machines on one switch):

1. OMT: `SEND ADD OMT Main output:op_1` on A; on B `INPUT DISCOVER OMT WAIT 2000` lists `omt:A (Main)` (with avahi or a discovery
   server), `INPUT ADD` it and `SURFACE SOURCE` it; STATE shows `ok`, ~1 frame of age, `tally:"program"` on A's send.
2. NDI: the same with the NDI 6 runtime on both, and once with a Discovery Server (`VIDEOIO SET NDI-DISCOVERY <host>`, restart).
   Studio Monitor sees `A (Main)` and B's receiver; with 6.3 the receiver appears in the Discovery Server's listing.
3. Unplug A's cable: B's input goes `stale`, then `lost` at 1 s, and shows its fallback; replug: back after 2 s of steady frames.
   With a backup on a third machine: the backup takes over once; `INPUT SWITCH … PRIMARY` returns it.
4. P216 from A with B's input at `live.depth "high"`: B receives 16-bit (`format:"p216"`).
5. Blackout A: B's picture goes black; freeze A: it holds; a test pattern on A's outputs reaches B.
6. Watch the bandwidth line in STATE against the switch's own counters; `SEND ADD` past 70 % of the link is refused.
7. Spout (one Windows machine with a GPU): `SEND ADD SPOUT Main output:op_1 BGRA8 ALWAYS`; Resolume and TouchDesigner list
   `PlayMap <node> - Main` and show it; blackout reaches them. Send from Resolume; `INPUT DISCOVER SPOUT` lists it; `INPUT ADD
   spout:Arena` shows it on a surface; close Resolume and the input goes `lost`.

## Next round

Prewarp taps, multiview sends, smooth and locked pacing (`LivePacing` is accepted and shown as `latest`), PTZ, failover sends
(`SEND FAILOVER`), `LiveCamera` scans over NDI/OMT, discovery thumbnails, hosting an OMT discovery server, naming sources after Show
Bus peers, Syphon, capture cards, a per-RID libvmx in the release packaging; Spout's GPU send stage (drawing into the shared texture on
the renderer's device), the WGL bridge for GL renderers, 16-bit and float Spout formats, and the ANGLE zero-copy receive.
