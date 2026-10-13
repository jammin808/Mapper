# Work package `texshare`: GPU texture sharing — Spout (Windows) and Syphon (macOS)

*Design for round 2, written 13 Oct 2026 against `main` at `c12b186` (`/home/user/mapper`). Roadmap item R3.3
("Spout in/out"), plus the Syphon part of L6.*

**Evidence marks:** **[V]** opened in this session; **[M]** measured here; **[U]** unverified; **[E]** estimate.
Research basis: `r2/research/videoio.md` §2 (Spout 2.007, read from the Spout2 source), §3 (Syphon), §8 and §9 (one
input abstraction and one send abstraction, taps, show safety); `r2/research/present.md` §4, §5 and §10 (WGL interop
costs; ANGLE on PlayMap's own D3D11 device); and the `zerocopy` design (`r2/design/zerocopy.md`), whose GPU surface,
share and importer this package reuses.

Re-checked for this design:

- **Spout2 `6b68a5b` "Version 2.007.018", 10 Oct 2026** (cloned to `scratchpad/ext/spout2`). Facts read there:
  - `SharedTextureInfo` is 280 bytes: `shareHandle`, `width`, `height`, `format`, `usage` (all `uint32`), then
    `description[256]`, then `partnerId`. `SpoutMaxSenderNameLen 256`.
  - `SetSenderID` writes `partnerId = 0`; it sets `0x80000000` when the sender shares through the CPU and ORs in
    `0x40000000` when it is GL/DX compatible. SpoutDX senders never call it, so they leave 0.
  - The 2.006 memory-share mode is chosen per user by the registry value `HKCU\Software\Leading Edge\Spout\MemoryShare = 1`.
  - The per-sender data buffer is a map named `<sender>_map`. Its size is the data length + 32. The first 16 bytes
    hold the data length as ASCII decimal (`_itoa_s`); the data starts at byte 16 and is null-terminated. It is locked
    by `<sender>_map_mutex`, with a 67 ms wait.
  - `WriteGLDXtexture` runs: `CheckTextureAccess` (access mutex) → `LockInteropObject` → blit → `SetNewFrame` (frame
    counter) → unlock → `AllowTextureAccess`.

  [V, `SpoutSenderNames.h` l.60–79, `SpoutSenderNames.cpp` l.691–713, `SpoutGL.cpp` l.1976–2010, 2589–2744, 3765,
  `SpoutSharedMemory.cpp` l.154–277]
- **ANGLE `EGL_ANGLE_d3d_texture_client_buffer` v12 (2026-10-09).** An `ID3D11Texture2D` can be wrapped as an
  EGLImage or a pbuffer if it is `D3D11_USAGE_DEFAULT`, was made on the display's own device, and has one of these
  formats: `R8G8B8A8_UNORM(_SRGB)`, `B8G8R8A8_UNORM`, `R16G16B16A16_FLOAT`, `R32G32B32A32_FLOAT`,
  `R10G10B10A2_UNORM`, `R8/R16/R8G8/R16G16_UNORM`, `NV12/P010/P016` (as planes), or `Y410/Y210` (sample-only).
  **`R16G16B16A16_UNORM` is not on the list** [V, `scratchpad/ext/angle/EGL_ANGLE_d3d_texture_client_buffer.txt`].
  Token: `EGL_D3D_TEXTURE_ANGLE 0x33A3` [V, `eglext_angle.h`].
- **Syphon-Framework headers (master).**
  - `SyphonOpenGLServer`: `initWithName:context:options:`, `publishFrameTexture:textureTarget:imageRegion:textureDimensions:flipped:`
    (target `GL_TEXTURE_RECTANGLE_EXT` or `GL_TEXTURE_2D`; "the texture is copied"; Core-profile work happens in a
    private shared context), `hasClients`, `stop`.
  - `SyphonOpenGLClient`: `initWithServerDescription:context:options:newFrameHandler:`, `hasNewFrame`, `newFrameImage`.
    `SyphonOpenGLImage.textureName` is a `GL_TEXTURE_RECTANGLE`.
  - `SyphonServerDirectory`: `sharedDirectory`, `servers`, `serversMatchingName:appName:`, the keys
    `SyphonServerDescriptionUUIDKey/NameKey/AppNameKey`, and the Announce/Update/Retire notifications.
  - `SyphonMetalServer`: `initWithName:device:options:` and `publishFrameTexture:onCommandBuffer:imageRegion:flipped:`.

  Licence BSD-3, © 2010 bangnoise & vade [V, `scratchpad/ext/syphon/*.h`, `License.txt`].

---

## 1. Goal and the bar

**Goal.**
- Any picture in PlayMap can go to Spout:
  - an output after its warp and blend (exactly what a projector gets);
  - an output before its warp;
  - a canvas or a playlist;
  - any single source.
- Any Spout sender on the machine is a live source PlayMap can map.
- Both directions stay on the GPU, never make the render loop wait, and say what they are doing.
- Syphon is planned to the same contracts for the macOS engine.

**The bar** (videoio.md §9.9; COMPETITIVE.md §3 and the interop rows):

| Who | What they offer | How PlayMap matches or beats it |
|---|---|---|
| **MadMapper** | Syphon/Spout/NDI in and out (COMPETITIVE.md) | Matched in and out. Beaten on **taps**: per send, choose the post-warp output, the pre-warp image, a canvas, a playlist or any single source. Competitors expose fewer tap points (videoio.md §9.1) |
| **Resolume, HeavyM, TouchDesigner, Millumin** | Spout in/out on Windows, Syphon on macOS. The common rig is "Resolume performs, MadMapper maps" over Spout, Syphon or NDI (COMPETITIVE.md) | PlayMap becomes the mapper behind Resolume or TouchDesigner on one machine with no copy through the CPU. Their frame number and PlayMap's show time ride along in the Spout per-sender data buffer (below) |
| **SpoutGL itself** (what most apps embed) | Waits up to **67 ms** on the access mutex, a frame counter only if a registry switch is on, frame-sync events that **block** the caller, and GL/DX interop locks on every frame (videoio.md §2.6; present.md §4 records interop lock costs in Godot's prototype: 1846 → 660 fps) | **Never stall the air.** PlayMap tries the access mutex with 0 ms and skips a busy frame (and counts it); it never waits on a frame-sync event, but **signals** one so partner apps can pace themselves to PlayMap. On the D3D11 output path (R2.1) the shared texture is opened on PlayMap's **own** device and drawn by ANGLE, with **no interop lock** on the hot path |
| **Everyone** | A lost Spout sender is a frozen or black layer with no explanation | Each input says `live` / `stale` / `lost` / `waiting`, with its frame rate, age, size, format, the sender's program path and the GPU it runs on. A sender on another GPU is named with the fix. On loss the input follows its policy (hold, black, or a fallback source), the same policy OMT and NDI inputs use |
| **High bit depth** | Most apps send BGRA8 | Sends in `RGBA16F` and `RGB10A2` for linear or 10-bit pipelines. All Spout formats are received, including `RGBA16` (CPU path on ANGLE, GPU on WGL) |
| **Frame identity** | Spout carries no timestamps (videoio.md §9.4) | PlayMap writes `{frame, leader time, rate, tap}` into the sender's `<name>_map` data buffer each frame. This is Spout's own per-frame metadata channel [V]. Another PlayMap or a TouchDesigner script can then align by frame, which is a first step to ROADMAP L7 "frame-numbered texture sharing with the sync anchor" |

## 2. Scope this round

### Must

| # | Item | Acceptance criteria |
|---|---|---|
| M1 | **Shared video-IO spine** (co-owned with the OMT and NDI packages; whichever lands first owns it, and the grammar here is the proposal): `ILiveInputProvider`, `LiveEndpoint`, `ISendSinkProvider`, `IGpuSendTarget`, the `VideoSend` entity, `LiveInputPolicy` on stream items, scheme routing in the media backend, the `INPUT`/`SEND`/`VIDEOIO` verbs, and `STATE.inputs`/`sends`/`videoio` | Engine tests: `INPUT ADD spout:Arena` makes a `Stream` item with `location = "spout:Arena"`; `SEND ADD` makes a `VideoSend` (`sd…`) that round-trips through `ProjectJson`. An old project file with no `sends` reads as empty. Each verb's reply is asserted by its fields |
| M2 | **Spout protocol layer** (pure C#, OS calls behind `ISpoutKernel`): sender-name list read/write (sorted bytewise, 256-byte slots, MaxSenders from the registry, real size by `VirtualQuery`), the 280-byte info, the access mutex, the frame counter semaphore, the frame-sync event, the `_map` data buffer, and reading the registry switches | Golden-byte tests against a fake kernel: slot order equals `std::set<std::string>` order (ordinal bytes); a full list refuses registration with the reason; a name whose `<name>` map is gone counts as stale; the info bytes match the layout field by field; the semaphore arithmetic gives "new frame" exactly once per sender frame; the `_map` header is `"<length>\0…"` in 16 bytes with data at 16 |
| M3 | **Spout receiver** (`spout:<name>`, or `spout:*` to follow Spout's active sender): discovery every 500 ms off the render thread; opens the shared texture on the renderer's D3D11 device; copies it into PlayMap's own texture when the frame counter moves (or every frame for senders that do not count); a state machine with words; CPU path for `RGBA16` on ANGLE and for senders on another GPU | Fake-kernel + fake-device tests: handle change → reopen; format change → new copy; busy mutex → no copy and the frame is held (counted, never waited); sender gone → `stale` after max(3 sender intervals, 100 ms), `lost` after 1 s → policy applied; sender back → `live` (automatic in rehearsal, manual on an armed node) |
| M4 | **Spout sender** (a `VideoSend` of kind `spout`): any tap; `BGRA8` (default), `RGBA8`, `RGB10A2`, `RGBA16F`; size from the tap or set; rate divider; `followAir` (blackout and freeze apply to canvas and source taps too, on by default); per-frame metadata; resize keeps the name | GL tests on llvmpipe with a fake target (a GL texture stands in for the D3D11 shared texture): the tap is resolved and drawn with **no flip** (row 0 = top, the same convention as D3D11; asserted on read-back); colour encoding and dither checked against golden pixels; a busy mutex skips the frame without drawing; `RateDivider = 2` publishes every other frame; blackout makes a canvas send black when `followAir` is on |
| M5 | **GPU bridges** in Render: (a) the **ANGLE bridge** on R2.1's shared device (EGLImage of PlayMap's own textures, `EGL_D3D_TEXTURE_ANGLE`); (b) the **WGL bridge** for today's renderer (a TexShare-owned D3D11 device on the GL's adapter + `WGL_NV_DX_interop2`, locked only around one blit) | Compile-only here (§7). Lab: a 1080p60 Spout loop PlayMap → Resolume or TouchDesigner → PlayMap, with `dropped = 0` over 10 min and receive copy CPU < 50 µs p95 |
| M6 | **STATE, context, MCP, NodeSettings, journal; the desk's Inputs/Sends lists** | Engine tests on JSON fields; context item light rules; MCP tools from the catalogue; desk tests with the fake engine |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | **Syphon**: the C ABI of a ~250-line Objective-C shim (`libplaymap_syphon.dylib`), the managed wrapper, provider and send sink against the same contracts, and a **fake shim** in C built with clang in the tests | Managed wrapper tests against the fake (directory list, server publish with `flipped`, client frames, `hasClients`); the shim's Objective-C source written and built on a macOS runner when one exists |
| S2 | **Thumbnails and routing in one press**: `VIEW <view> SHOW input:<id>`; `INPUT USE <uri> ON <surface>\|<output>\|<canvas>/<layer>` (adds the input if needed, then routes) | Engine tests; desk drag-from-list |
| S3 | **Reading frame metadata** from senders that write it (another PlayMap): shown in STATE (`sender.frame`, `sender.leaderNs`) and used to report the sender's lag | Fake-kernel test |
| S4 | **Companion presets per discovered sender**, created live (the Companion package builds them from `videoio.discovered`) | Contract test on the STATE shape the module reads |

### Later

- Syphon live on the macOS engine (L6), first on today's GL (Core profile is supported by SDK 5 [V]), then Metal
  (`SyphonMetalServer/Client` [V]).
- A multiview tap (with the OMT/NDI package).
- Spout over D3D12 (`SpoutDX12` uses D3D11On12 [V videoio.md §2.1]); only if R2.1 ever moves to D3D12.
- Texture sharing on Linux: there is no common standard; OMT covers same-machine and network on Linux.

## 3. Architecture

### 3.1 Components

```
PlayMap.Core      LiveInputs.cs  ILiveInputProvider · LiveEndpoint · ProviderStatus · LiveOpenOptions
                  GpuSends.cs    ISendSinkProvider · IGpuSendTarget · SendFrameInfo · SendHealth
                  VideoIoModel   VideoSend · SendTap · LiveInputPolicy (+ zerocopy's GpuSurface, IGpuShare)

PlayMap.TexShare  Spout/ISpoutKernel ── Win32SpoutKernel (kernel32/advapi32) | FakeSpoutKernel (tests)
(new edge:        Spout/SpoutNames (list algorithm) · SpoutInfo (280 B) · SpoutFrameCounter · SpoutFrameSync
 Core + Silk.NET  Spout/SpoutDataBuffer (_map) · SpoutDirectory (discovery thread)
 D3D11/DXGI)      Spout/SpoutSource (IVideoSource: GPU or CPU lease) · Spout/SpoutSender (IGpuSendTarget)
                  D3D11/ISharedTextures ── SilkSharedTextures | FakeSharedTextures (tests)
                  Spout/SpoutProvider (ILiveInputProvider + ISendSinkProvider)
                  Syphon/SyphonNative (C ABI) · SyphonProvider · native/playmap_syphon.m · native/fake_syphon.c

PlayMap.Render    Engine/SendStage (after the outputs are drawn, before present) · "send" shader program
                  Video/AngleD3D11Importer (zerocopy; RGBA surfaces here) · Platform/WglDxInterop (bridge b)

PlayMap.Engine    VideoIo/VideoIoRegistry (providers by scheme) · VideoIo/EngineSends (project.Sends → targets)
                  EngineCore.VideoIo (INPUT/SEND/VIDEOIO verbs) · VideoIo/VideoIoState (STATE + context)
                  Host/EngineParts.VideoIo (partial hook AddVideoIo) · VideoIo/EngineParts.TexShare (plugs Spout/Syphon in)
```

The module rules hold:
- TexShare references Core and its own native libraries (d3d11, dxgi, kernel32, advapi32; the Syphon shim on
  macOS).
- Render never references TexShare. They meet through Core's `GpuSurface` (a D3D11 texture pointer) and
  `IGpuSendTarget`.
- TexShare never calls GL.

### 3.2 Threads

| Thread | Work |
|---|---|
| **Discovery** (one per provider, 500 ms, `SpoutDiscoveryMs`) | Lock `SpoutSenderNames_mutex` (67 ms like Spout; this thread may wait); copy the list; unlock. For each name, open the `<name>` map and read the 280 bytes under `<name>_mutex`. Read `ActiveSenderName`. Publish an immutable endpoint snapshot (latest wins). Allocation is allowed here, not on the render thread |
| **Render** | Receivers: the per-frame counter check and the copy. Senders: draw and publish. Everything is non-blocking (0 ms waits; at most `SpoutReceiveWaitMs`, default 0, maximum 2) |
| **Control lane / pool** | `SEND ADD`/`REMOVE`: register and unregister the name under the list mutex (may wait 67 ms; never on the render thread). Sources are opened on the pool thread, as `EngineSources` does today |

### 3.3 Receive, per bridge

Common, on the render thread, when `EngineSources.Acquire` asks a `SpoutSource` for its frame:

1. If the discovery snapshot shows the sender's handle changed (or this is the first open): `OpenSharedResource(handle,
   IID_ID3D11Texture2D)` on the bridge's device, then `GetDesc` (the real format overrides the info's) and
   `QueryInterface(IDXGIKeyedMutex)` (present only on keyed textures). Allocate PlayMap's **own** texture: same size and
   format, `DEFAULT`, `SHADER_RESOURCE | RENDER_TARGET`. Mark the surface `Persistent` with a new `PoolEpoch`.
2. Access first, as Spout does (`GetNewFrame` runs inside the sender's lock, SpoutFrameCount.cpp): the keyed mutex if present
   (`AcquireSync(0, 0)`), else `WaitForSingleObject("<name>_SpoutAccessMutex", 0)`. If it is busy, count `busy`, hold the
   picture and stop — `lastCount` is not touched, so the frame is copied at the next render. If the mutex does not exist (an old
   sender), copy without it.
3. Frame counter, inside the lock (when the sender has `<name>_Count_Semaphore`): `WaitForSingleObject(sem, 0)` then
   `ReleaseSemaphore(sem, 1, &prev)`. If `prev` equals `lastCount`, the frame is not new: release the mutex, answer the previous
   lease (same generation) and stop.
4. `CopyResource(own, shared)`, release the mutex (or `ReleaseSync(0)`), then `SetEvent("<name>_Sync_Event")` if it exists, so a
   sender that paces with `WaitFrameSync` sees PlayMap took the frame. PlayMap never waits on it. **Only now commit
   `lastCount = prev`** (a failed copy leaves it, so no sender frame is skipped). A fake-kernel test advances the count while the
   mutex is busy and asserts the next render copies frame N.
5. Generation + 1; `ArrivalNs = MonoClock.NowNs`. The lease carries `GpuSurface{Api=D3D11, Handle=own, Format,
   Persistent=true}` and `DecodePath.GpuShare`.

The lease always points at PlayMap's own copy, never the sender's texture, because the sender may free or resize its
texture at any moment (videoio.md §2.7).

**Bridge (a), ANGLE on R2.1's device.** The device is `IGpuShare.D3D11Device` (the renderer's). Render's
`AngleD3D11Importer` wraps `own` once:
`eglCreateImageKHR(dpy, EGL_NO_CONTEXT, EGL_D3D_TEXTURE_ANGLE, own, {EGL_NONE})` →
`glEGLImageTargetTexture2DOES` → an adopted `GpuTexture` (zerocopy's `GlDevice.AdoptExternal`). The cache entry is
kept while the surface's `PoolEpoch` is unchanged. The copy, the draw and the rest of the frame run on one immediate
context, in order, so no fence is needed (zerocopy's one-device rule).

**Bridge (b), WGL interop (today's renderer and the fallback).**
- TexShare makes its own `ID3D11Device` on the GL's adapter. The adapter is the DXGI adapter whose `EnumOutputs` list
  holds the monitor of PlayMap's first output window; failing that, the vendor match with `GL_VENDOR`, then the
  default. `wglDXOpenDeviceNV` confirms it.
- Render registers `own` once with `wglDXRegisterObjectNV(hDev, own, glName, GL_TEXTURE_2D, WGL_ACCESS_READ_ONLY_NV)`.
- Each new frame: `wglDXLockObjectsNV` → `glBlitFramebuffer` from the interop texture into a GL-native texture of
  the same size → `wglDXUnlockObjectsNV`. The lock covers one blit, and the rest of the frame samples GL-native
  memory.
- Interop formats: `RGBA8`, `BGRA8`, `RGB10A2`, `RGBA16`, `RGBA16F`, `RGBA32F` (videoio.md §2.5 [V SpoutGL.cpp]).

**CPU path (automatic fallback).** Used for `R16G16B16A16_UNORM` senders on bridge (a), which ANGLE cannot wrap
[V]; for senders whose texture is on another adapter (`OpenSharedResource` fails with `E_INVALIDARG`; the adapter is
found as Spout does, with a probe device per adapter [V videoio.md §2.5]); and for CPU consumers (a Spout source used
as a scan camera).
- TexShare opens the texture on a device on the sender's adapter and copies it into a 3-deep ring of
  `D3D11_USAGE_STAGING` / `CPU_ACCESS_READ` textures.
- It maps two frames later with `D3D11_MAP_FLAG_DO_NOT_WAIT` (a `DXGI_ERROR_WAS_STILL_DRAWING` skips the frame).
- The lease's plane points into the mapped memory and unmaps on dispose. Layouts: `Bgra8`, `Rgba8`, `RgbaHalf`, and
  new `Rgba16` (Core `PixelLayout`).
- STATE says `path: "cpu-copy"` with words such as "sender 'Arena' is on GPU 1 (Radeon PRO W7600); PlayMap draws on
  GPU 0 — run it on GPU 0 for a GPU path". The light is amber.

### 3.4 Send, per bridge

At registration (control lane, never the render thread):
1. Check the name (§3.6). Lock `SpoutSenderNames_mutex`, read the set, refuse if the name exists and its map is live
   ("a Spout sender named 'X' already exists: C:\…\Arena.exe"), insert it, write the set back, set `ActiveSenderName`,
   unlock.
2. Create the shared texture on the bridge's device: `{W, H, Mip 1, Array 1, format, {1,0}, DEFAULT,
   RENDER_TARGET|SHADER_RESOURCE, 0, D3D11_RESOURCE_MISC_SHARED}`. Read its KMT handle from
   `IDXGIResource::GetSharedHandle`, then `Flush`.
3. Create the `<name>` map (280 B) and `<name>_mutex`. Write the info: the handle truncated to 32 bits (as Spout
   does), size, DXGI format, `usage = 0`, the exe path (`QueryFullProcessImageNameA`), and
   `partnerId = 0` on bridge (a) (a DX sender) or `0x40000000` on bridge (b) (GL/DX compatible).
4. Create `<name>_SpoutAccessMutex`, `<name>_Count_Semaphore` (`CreateSemaphoreA(NULL, 1, LONG_MAX, …)`, always, so
   receivers can show fps), `<name>_Sync_Event` (auto-reset) and `<name>_map` (length 256, map size 288).

Per frame, on the render thread, in `RenderEngine.Render` after the outputs are drawn and before the present loop:
1. `SendStage` checks the divider, `Enabled`, and whether the tap exists this frame. It resolves the tap texture
   (§3.5).
2. `target.TryBegin(w, h, format, out surface)`: `WaitForSingleObject(accessMutex, 0)`. `WAIT_TIMEOUT` → `busy++`,
   return false (the frame is skipped, never waited for). `WAIT_ABANDONED` (a receiver died holding it) counts as
   acquired, with a journal row once. A size or format change re-creates the texture and rewrites the info (same
   name).
3. Render draws the `send` program into the target: on bridge (a) through an EGLImage of the shared texture attached
   to an FBO (cached per handle); on bridge (b) between `wglDXLockObjectsNV` and `wglDXUnlockObjectsNV` on a
   registration with `WGL_ACCESS_WRITE_DISCARD_NV`. Then `glFlush` (on ANGLE this submits the immediate context).
4. `target.End(drawn, info)`: on bridge (b), `Flush` on TexShare's device. Then semaphore `WaitForSingleObject(sem, 0)`
   + `ReleaseSemaphore(sem, 2, NULL)` (net +1, Spout's sender rule [V videoio.md §2.6]), `ReleaseMutex`,
   `SetEvent(sync)`. Then the `_map` metadata, written under `<name>_map_mutex` with a 0 ms try (skipped if busy):
   `{"pm":1,"node":"a1b2c3d4","send":"sd…","frame":123456,"leaderNs":1760360000123456789,"rate":"60000/1001","tap":"output:op_1","w":1920,"h":1080}`.

Unregister (control lane): remove the name under the list mutex and close every handle. The maps vanish with the last
handle.

### 3.5 Taps

| Tap (wire form) | Picture | Natural size |
|---|---|---|
| `output:<op>` | `RenderEngine.OutputPicture(op)`: post-warp, post-blend, post-trim, what the projector gets (8-bit) | output W×H |
| `prewarp:<op>` | the output's projector pass (`OutputState.Projector`, RGBA16F) for projector outputs; the output's source before warp for canvas/source outputs | output W×H |
| `canvas:<cv>`, `playlist:<pl>`, `media:<md>`, `generator:<gn>`, `live:<md>`, `timer:<id>`, `color:#…` (any `SourceRef`) | `SourceResolver.Resolve(ref, w, h, ctx)`; generators draw at the send's size | canvas W×H; media native; generators 1920×1080 unless set |

- **`followAir`** (default `true`): blackout makes every send black, and freeze holds them. The panic outcomes stay
  predictable. Output taps follow the air anyway because they are the air.
- **The `send` program** samples the tap and writes RGBA:
  - `encoding = display` (default): values as the pipeline holds them;
  - `encoding = linear`: the sRGB curve is removed, for float receivers that expect linear light;
  - an ordered dither (the output stage's) when quantising RGBA16F to 8 bits.
- **No flip.** PlayMap's targets store row 0 at the top (`RenderTarget` doc) and so do D3D11 textures. Syphon is
  bottom-up GL, so the shim publishes with `flipped: YES` and flips received images on blit.

### 3.6 Names and formats

- **Names.** Printable ASCII (32–126) only, at most 255 bytes (Spout's names are ANSI C strings in 256-byte slots
  [V]). The default name is `PlayMap <node name> - <send name>`. Non-ASCII characters are replaced with `?` in the
  default name; an explicit name with them is refused.
- **Formats:**

| `SendFormat` / received | DXGI | ANGLE wrap | WGL interop | GL internal |
|---|---|---|---|---|
| `Bgra8` (default) | `B8G8R8A8_UNORM` 87 | yes | yes | `GL_RGBA8` (ANGLE swizzles) |
| `Rgba8` | `R8G8B8A8_UNORM` 28 | yes | yes | `GL_RGBA8` |
| `Rgb10A2` | `R10G10B10A2_UNORM` 24 | yes | yes | `GL_RGB10_A2` |
| `Rgba16F` | `R16G16B16A16_FLOAT` 10 | yes | yes | `GL_RGBA16F` |
| receive only: `RGBA16` | `R16G16B16A16_UNORM` 11 | **no** → CPU path | yes | `GL_RGBA16` |
| receive only: `RGBA32F` | `R32G32B32A32_FLOAT` 2 | yes | yes | `GL_RGBA32F` |
| receive: format 0 or D3D9 21/22 | read as `B8G8R8A8_UNORM` 87 [V videoio.md §2.3] | | | |

- **Spout 2.006 memory share** (registry `MemoryShare = 1`): PlayMap shares textures only. STATE says "this PC's
  Spout is set to 2.006 memory share (SpoutSettings); PlayMap shares GPU textures only" and the provider shows
  `available: false`.

### 3.7 Performance budgets

| Step | Budget | Basis |
|---|---|---|
| Receive, render-thread CPU per input per frame | ≤ 30 µs (2 semaphore calls, 1 mutex try, `CopyResource`, release, `SetEvent`) | [E] |
| Receive GPU copy, 1080p BGRA8 | 8.3 MB in VRAM, ≤ 0.05 ms | [E] |
| Send, render-thread CPU per send | ≤ 100 µs (mutex, a draw, flush, semaphore, release, metadata) | [E] |
| WGL bridge lock + blit + unlock | measured in the lab; the island stays off the present path (present.md §4) | [U] |
| Discovery | ≤ 1 ms every 500 ms on its own thread | [E] |
| Allocation on the render thread | 0 bytes per frame after warm-up (endpoint snapshots are made on the discovery thread; the metadata is formatted into a reused `byte[]` with `Utf8Formatter`) | R2.8 |

### 3.8 Syphon plan (S1 now, live with the macOS engine)

C ABI of `libplaymap_syphon.dylib` (Objective-C inside, ~250 lines, links `Syphon.framework` and ships it with its
BSD-3 notice):

```c
typedef struct pm_syphon_server pm_syphon_server;
typedef struct pm_syphon_client pm_syphon_client;
int  pm_syphon_version(void);                                       // ABI version, 1
int  pm_syphon_directory(char *json, int cap);                      // [{"uuid","name","app"}], bytes written or -needed
pm_syphon_server *pm_syphon_server_gl(const char *name);            // uses CGLGetCurrentContext(); NULL on failure
int  pm_syphon_server_publish(pm_syphon_server *, unsigned tex, unsigned target, int w, int h, int flipped);
int  pm_syphon_server_has_clients(pm_syphon_server *);
void pm_syphon_server_free(pm_syphon_server *);                     // -stop, release
pm_syphon_client *pm_syphon_client_gl(const char *uuid);
int  pm_syphon_client_frame(pm_syphon_client *, unsigned *rectTex, int *w, int *h, unsigned long long *serial); // 1 new, 0 none, <0 gone
void pm_syphon_client_free(pm_syphon_client *);
```

- URIs use stable names, `syphon:<App>/<Server>`, resolved to a UUID at open, because UUIDs change every run.
- Receive blits the `GL_TEXTURE_RECTANGLE` image into a GL_TEXTURE_2D with a Y flip. Send publishes PlayMap's
  GL_TEXTURE_2D target with `flipped: 1`.
- `hasClients` becomes `receivers: "yes|no"` in STATE. Spout cannot report receivers; Syphon can.
- Dependency: the macOS render host must run GL on the main thread (CARRYOVER: "macOS main-thread GL would need
  another hook"). The Metal variant (`SyphonMetalServer` with `publishFrameTexture:onCommandBuffer:…`) follows L6.

## 4. Spine needs

### 4.1 Model (Core; backward compatible: init properties with defaults, lists default to empty)

```csharp
// src/PlayMap.Core/Model/VideoIoModel.cs
public enum SendKind { Spout, Syphon, Omt, Ndi }
public enum SendFormat { Bgra8, Rgba8, Rgb10A2, Rgba16F, Uyvy, P216 }   // the last two for OMT/NDI
public enum SendEncoding { Display, Linear }
public enum LiveLoss { Hold, Black, Fallback }

/// <summary>A picture leaving the node: which tap, as what. The tap is "output:<op>", "prewarp:<op>" or any
/// SourceRef's wire form.</summary>
public sealed record VideoSend(
    string Id, string Name, SendKind Kind, string Tap,
    int Width, int Height,            // 0 = the tap's natural size
    SendFormat Format, int RateDivider, bool Enabled) : IEntity
{
    public string PublishedName { get; init; } = "";        // "" = "PlayMap <node> - <name>"
    public bool FollowAir { get; init; } = true;
    public SendEncoding Encoding { get; init; } = SendEncoding.Display;
}

/// <summary>What a live input does when its feed goes quiet. Stale and lost are measured on the node's clock.</summary>
public sealed record LiveInputPolicy(LiveLoss OnLoss, string? Fallback, int StaleMs, int LostMs)
{
    public static readonly LiveInputPolicy Default = new(LiveLoss.Hold, null, 0 /* 3 frame periods */, 1000);
}

// src/PlayMap.Core/Model/Project.cs (record body)
public ImmutableArray<VideoSend> Sends { get; init; } = [];
public VideoSend? FindSend(string? id) => Sends.Find(id);
// src/PlayMap.Core/Model/MediaModel.cs (MediaItem body)
public LiveInputPolicy? Live { get; init; }                  // stream items only; null = Default
// src/PlayMap.Core/Model/Ids.cs
public const string Send = "sd";
// src/PlayMap.Core/Media/VideoFrames.cs — PixelLayout gains Rgba16 (16-bit unsigned normalised RGBA, CPU path)
```

Generic `ADD`/`SET`/`REMOVE` learn `send` (the `ADD` syntax list gains `send`). JSON is camel-case like every entity:
`{"id":"sd…","name":"To Arena","kind":"spout","tap":"output:op_1","width":0,"height":0,"format":"bgra8","rateDivider":1,"enabled":true,"publishedName":"","followAir":true,"encoding":"display"}`.

### 4.2 Contracts (Core)

```csharp
// src/PlayMap.Core/Media/LiveInputs.cs
public sealed record LiveEndpoint(string Uri, string Name, string Host, int Width, int Height, string Format,
    bool Alpha, bool HighBitDepth, string Detail, long LastSeenNs, bool Stale);   // Detail: the sender's exe path
public sealed record ProviderStatus(string Scheme, bool Available, string Version, string Words);
public sealed record LiveOpenOptions(string Key, IGpuShare? Gpu, bool CpuFrames);  // CpuFrames: a CPU consumer
public interface ILiveInputProvider : IDisposable
{
    string Scheme { get; }                                   // "spout", "syphon", "omt", "ndi"
    ProviderStatus Status { get; }
    IReadOnlyList<LiveEndpoint> Endpoints { get; }           // the discovery snapshot, latest wins
    IVideoSource Open(string uri, LiveOpenOptions options);  // never blocks; frames arrive later
}

// src/PlayMap.Core/Media/GpuSends.cs
public readonly record struct SendFrameInfo(long Frame, long LeaderNs, string Rate, string Tap, int Width, int Height);
public sealed record SendHealth(string State, long Frames, long Dropped, long Busy, double Fps, string Receivers, string Words);
public interface IGpuSendTarget : IDisposable
{
    string Key { get; }
    SendHealth Health { get; }
    /// <summary>Render thread, non-blocking: the surface to draw this frame into, or false to skip it (counted).</summary>
    bool TryBegin(int width, int height, SendFormat format, out GpuSurface target);
    void End(bool drawn, in SendFrameInfo info);
}
public interface ISendSinkProvider
{
    SendKind Kind { get; }
    ProviderStatus Status { get; }
    /// <summary>Control lane: registers the send's name and makes its shared texture (may wait on the name list's lock).</summary>
    IGpuSendTarget Create(VideoSend send, string publishedName, IGpuShare gpu);
}
```

Render's `FrameInputs` gains `public ReadOnlyMemory<SendRequest> Sends { get; init; }`, where
`SendRequest(VideoSend Send, IGpuSendTarget Target)`. The engine rebuilds the array only when `Project.Sends` or a
target changes, so a frame allocates nothing.

### 4.3 Seams (Engine)

```csharp
// src/PlayMap.Engine/Host/EngineParts.VideoIo.cs
public sealed partial record EngineParts
{
    public IReadOnlyList<VideoIoFactory> VideoIo { get; init; } = [];
    /// <summary>The side that owns texture sharing (and OMT, NDI) adds its providers here, each in its own file.</summary>
    static partial void AddVideoIo(List<VideoIoFactory> providers);
}
public delegate VideoIoProvider VideoIoFactory(VideoIoSetup setup);   // setup: settings, logs, mono clock
public sealed record VideoIoProvider(ILiveInputProvider? Inputs, ISendSinkProvider? Sends);
```

`MediaBackend.Open(item)`: a `Stream` item whose location has a registered scheme (`spout:`, `syphon:`, …) goes to
`provider.Open(uri, new LiveOpenOptions(item.Id, share, cpuFrames))`. Every other location goes to FFmpeg's
`CaptureSource`, as today. Live loss policy: `EngineSources.Live` reads the source's health state and, on `lost`,
answers the fallback's lease (`hold` = the last lease; `black` = `SourceRef.None`; `fallback` = the named source).

### 4.4 Verbs (new category `VideoIo`; shared with OMT and NDI)

| Verb | Grammar | Risk | Mutates | Reply (fields) |
|---|---|---|---|---|
| `INPUT DISCOVER` | `INPUT DISCOVER [SPOUT\|SYPHON\|OMT\|NDI\|ALL]` | Low | no | `OK {"endpoints":[{"uri":"spout:Arena","name":"Arena","width":1920,"height":1080,"format":"bgra8","detail":"C:\\…\\Arena.exe","stale":false}],"providers":[{"scheme":"spout","available":true,"version":"2.007","words":""}]}` |
| `INPUT LIST` | `INPUT LIST` | Low | no | `OK {"inputs":[…]}` (the `STATE.inputs` rows) |
| `INPUT ADD` | `INPUT ADD <uri> [<name>]` | Low | yes | `OK {"stage":"applied","id":"md…","observed":"live\|waiting\|unavailable here: <words>"}`. Refused: unknown scheme, malformed URI, a duplicate URI ("already in the library as md…") |
| `INPUT FALLBACK` | `INPUT FALLBACK <input> HOLD\|BLACK\|<source> [STALE <ms>] [LOST <ms>]` | Review | yes | `OK {"stage":"applied","input":"md…","onLoss":"fallback","fallback":"media:md…"}` |
| `INPUT USE` (S2) | `INPUT USE <uri>\|<input> ON <surface>\|<output>\|<canvas>/<layer>` | High | yes | the `SURFACE SOURCE`-style receipt plus the input id |
| `SEND LIST` | `SEND LIST` | Low | no | `OK {"sends":[…]}` |
| `SEND ADD` | `SEND ADD <name> SPOUT\|SYPHON\|OMT\|NDI <tap> [<w>x<h>] [FORMAT BGRA8\|RGBA8\|RGB10A2\|RGBA16F] [RATE /<n>] [AS "<published name>"]` | Review | yes | `OK {"stage":"applied","id":"sd…","as":"PlayMap Stage left - To Arena","observed":"live"}`. Refused: provider unavailable on this node (the project keeps the send; this node says why), a name in use by another process, a non-ASCII published name, an unknown tap |
| `SEND ENABLE` | `SEND ENABLE <send> ON\|OFF` | High | yes | `OK {"stage":"observed","send":"sd…","state":"live\|off"}`. Refused when already in that state |
| `SEND REMOVE` | `SEND REMOVE <send>` | High | yes | `OK {"stage":"applied"}` |
| `VIDEOIO STATUS` | `VIDEOIO STATUS` | Low | no | `OK {…}` (the `STATE.videoio` object) |

Field edits use the generic verbs: `SET sd… tap "canvas:cv_main"`, `SET sd… format "rgba16f"`. Risk follows
`SET` (Review); on an armed node the Show Mode package locks routing (videoio.md §9.5).

### 4.5 STATE and context (new keys at the end of the top-level object)

```json
"inputs":[{"id":"md…","name":"Arena","uri":"spout:Arena","kind":"spout","state":"live","path":"gpu-share",
  "width":1920,"height":1080,"format":"bgra8","fps":60.0,"ageMs":4,"dropped":0,"busy":2,
  "sender":{"app":"C:\\Program Files\\Resolume Arena\\Arena.exe","gpu":"same","counts":true,"frame":null},
  "onLoss":"hold","fallback":null,"words":""}],
"sends":[{"id":"sd…","name":"To Arena","kind":"spout","as":"PlayMap Stage left - To Arena","tap":"output:op_1",
  "state":"live","width":1920,"height":1080,"format":"bgra8","fps":60.0,"dropped":0,"busy":0,"receivers":"unknown","words":""}],
"videoio":{"spout":{"available":true,"version":"2.007","senders":3,"maxSenders":64,"frameCount":true,"memoryShare":false,
  "bridge":"angle|wgl|none","words":""},
  "syphon":{"available":false,"words":"Syphon is macOS only"},
  "discovered":[{"uri":"spout:Arena","name":"Arena","width":1920,"height":1080,"format":"bgra8","app":"Arena.exe","stale":false}]}
```

- `inputs[].state`: `live` | `stale` | `lost` | `waiting` (no such sender yet) | `unavailable` (no provider on this
  node) | `refused` (with words). `path`: `gpu-share` | `cpu-copy`. `sender.gpu`: `same` | `other`.
- `sends[].state`: `live` | `off` | `busy` (the mutex was busy for over 1 s) | `refused`. `receivers`: `unknown`
  (Spout) | `yes` | `no` (Syphon `hasClients`).
- `videoio.discovered` holds at most 64 rows (Spout's default `MaxSenders`).
- Changes of state call `FactsChanged()`; rates ride along.

**Context items:**
- `input:<id>`: green when live; amber when stale or on the CPU path; red when lost and on air, with the fallback
  named ("Arena lost 3 s ago; showing Loop B").
- `send:<id>`: green when live; amber when busy; red when refused.

The headline mentions a lost on-air input before anything else.

**Journal:** state changes, never per-frame events. Sends never carry secrets, so nothing is redacted.

### 4.6 NodeSettings (init properties)

```csharp
public bool SpoutEnabled { get; init; } = true;
public int SpoutDiscoveryMs { get; init; } = 500;
public int SpoutReceiveWaitMs { get; init; } = 0;        // 0–2; never more on the render thread
public bool SpoutFrameMetadata { get; init; } = true;    // write {frame, leaderNs, …} to <name>_map
public bool SyphonEnabled { get; init; } = true;
```

## 5. Files

**New**

| File | What |
|---|---|
| `src/PlayMap.Core/Model/VideoIoModel.cs`, `src/PlayMap.Core/Media/LiveInputs.cs`, `src/PlayMap.Core/Media/GpuSends.cs` | §4.1–4.2 (shared spine) |
| `src/PlayMap.TexShare/PlayMap.TexShare.csproj` | references Core, Silk.NET.Direct3D11 2.23.0, Silk.NET.DXGI 2.23.0 |
| `src/PlayMap.TexShare/Spout/ISpoutKernel.cs`, `Win32SpoutKernel.cs` | `CreateFileMappingA/OpenFileMappingA/MapViewOfFile/VirtualQuery/UnmapViewOfFile`, `CreateMutexA/OpenMutexA/WaitForSingleObject/ReleaseMutex`, `CreateSemaphoreA/OpenSemaphoreA/ReleaseSemaphore`, `CreateEventA/OpenEventA/SetEvent`, `RegGetValueA`, `QueryFullProcessImageNameA`, all through `LibraryImport`, `[SupportedOSPlatform("windows")]` |
| `src/PlayMap.TexShare/Spout/SpoutNames.cs`, `SpoutInfo.cs`, `SpoutFrameCounter.cs`, `SpoutFrameSync.cs`, `SpoutDataBuffer.cs`, `SpoutDirectory.cs` | M2 |
| `src/PlayMap.TexShare/Spout/SpoutSource.cs`, `SpoutSender.cs`, `SpoutProvider.cs` | M3, M4 |
| `src/PlayMap.TexShare/D3D11/ISharedTextures.cs`, `SilkSharedTextures.cs` | open, create shared, copy, keyed mutex, staging ring, adapter probe |
| `src/PlayMap.TexShare/Syphon/SyphonNative.cs`, `SyphonProvider.cs`, `native/playmap_syphon.m`, `native/fake_syphon.c` | S1 |
| `src/PlayMap.Render/Engine/SendStage.cs` | tap resolution, the `send` draw, `TryBegin`/`End` |
| `src/PlayMap.Render/Platform/WglDxInterop.cs` | `wglDXOpenDeviceNV`, `RegisterObjectNV`, `LockObjectsNV`, `UnlockObjectsNV`, `UnregisterObjectNV`, `CloseDeviceNV` via `wglGetProcAddress` |
| `src/PlayMap.Engine/VideoIo/VideoIoRegistry.cs`, `EngineSends.cs`, `VideoIoState.cs`, `EngineParts.TexShare.cs`; `src/PlayMap.Engine/EngineCore.VideoIo.cs`; `src/PlayMap.Engine/Host/EngineParts.VideoIo.cs` | §4.3–4.5 |
| `tests/PlayMap.TexShare.Tests/` (`SpoutNamesTests`, `SpoutInfoTests`, `FrameCounterTests`, `DataBufferTests`, `SpoutSourceTests`, `SpoutSenderTests`, `SyphonFakeShimTests`), `tests/PlayMap.Render.Tests/SendStageTests.cs`, `tests/PlayMap.Engine.Tests/VideoIoVerbTests.cs`, `VideoIoStateTests.cs` | §7 |
| `THIRDPARTY-NOTICES` entries | Spout (BSD-2, Lynn Jarvis: layouts and behaviour learned from its source), Syphon (BSD-3, shipped with the macOS shim) |

**Touched (small hunks)**

| File | Hunk |
|---|---|
| `src/PlayMap.Core/Model/Project.cs`, `MediaModel.cs`, `Ids.cs` | `Sends`, `FindSend`; `MediaItem.Live`; `Ids.Send` |
| `src/PlayMap.Core/Media/VideoFrames.cs` | `PixelLayout.Rgba16` |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | `VerbCategory.VideoIo`; ten rows; `send` in the `ADD` syntax |
| `src/PlayMap.Media/Frames/FramePool.cs` (`FrameLayout`), `src/PlayMap.Render/Video/VideoTextureSet.cs`, `src/PlayMap.Render/Gpu/GpuTypes.cs` | the `Rgba16` layout, upload and `TextureFormat.Rgba16` |
| `src/PlayMap.Render/Engine/Contracts.cs`, `RenderEngine.cs` | `FrameInputs.Sends`; one call to `SendStage.Run` before the present loop |
| `src/PlayMap.Render/Shaders/ShaderLibrary.cs` | the `send` program |
| `src/PlayMap.Engine/Host/MediaBackend.cs` | scheme routing in `Open` |
| `src/PlayMap.Engine/Rendering/EngineSources.cs`, `RenderHost.cs` | live loss policy; pass sends into `FrameInputs` |
| `src/PlayMap.Engine/Host/EngineHost.cs` | build the registry from `EngineParts.VideoIo`, dispose it after the transports |
| `src/PlayMap.Engine/StateWriter.cs`, `ContextBuilder.cs` | one call each |
| `src/PlayMap.App/…` | Inputs and Sends lists (Outputs drawer tab), right-click items, the fake engine's STATE |
| `PlayMap.sln`; the module-rules check (ARCHITECTURE.md §2 names `ModuleRulesTests`; add the TexShare row) | |
| `docs/WIRE.md`, `docs/PLAYBACK.md` (video IO section), `docs/ARCHITECTURE.md` (module table), `docs/ROADMAP.md` | papers |

## 6. Dependencies

**Work packages:**
- **zerocopy** — `GpuSurface`, `IGpuShare`, `IGpuImporter`, the ANGLE importer and `GlDevice.AdoptExternal`. This
  package is blocked on its M1 only.
- **R2.1 D3D11 output (option C)** — bridge (a). Until then bridge (b) runs.
- **OMT and NDI packages** — co-own the M1 spine. Whichever lands first lands it.
- **Show Mode** — armed routing locks, the manual return from fallback, the panic outcome "sends keep running, black
  or frozen".
- **Preflight** — checks that the Spout sender is on the drawing GPU, the bridge in use, and `MemoryShare` off.
- **Companion** — variables, feedbacks and auto-presets from `videoio.discovered`.
- **Health view / fleet** — `inputs` and `sends` per node on the Show Bus.

**Third party:**

| What | Version | Licence | How |
|---|---|---|---|
| Spout protocol | 2.007.018 behaviour | BSD-2-Clause (Lynn Jarvis) | **reimplemented in C#**, no Spout binary or source copied; notice in THIRDPARTY-NOTICES |
| Silk.NET.Direct3D11, Silk.NET.DXGI | 2.23.0 (same family as the tree's Silk.NET) | MIT | NuGet |
| `WGL_NV_DX_interop2` | driver | — | function pointers |
| ANGLE | through R2.1 | BSD-3 | — |
| Syphon.framework | SDK 5 / master | BSD-3 | shipped beside the macOS shim with its notice |
| Not used | Spout.NET 0.1.2 (wraps the C++ library, not maintained for .NET 10 [U]); Syphon.NET (targets .NET 11 [V-x videoio.md §3]) | | |

## 7. Verification here

| Area | Real on this machine | Simulated, and how | Compile-only |
|---|---|---|---|
| Spout protocol layer | **Yes**: every algorithm (name list, info bytes, semaphore arithmetic, data buffer header, renaming refusal, stale detection, registry interpretation) runs against `FakeSpoutKernel`, an in-memory implementation of named maps, mutexes, semaphores and events with Windows semantics (`WAIT_TIMEOUT`, `WAIT_ABANDONED`, a semaphore's previous count). Golden bytes come from the Spout source [V] | A second fake "process" (another kernel client in the same test) plays Resolume: it registers, publishes frames, crashes holding the mutex | The Win32 calls themselves |
| Receiver and sender state machines | **Yes**, with `FakeSharedTextures` (CPU byte arrays standing in for D3D11 textures: open, copy, keyed mutex, staging map with "still drawing") | adapter mismatch, handle change, format change and resize injected | `SilkSharedTextures` |
| Send stage (taps, `send` program, no flip, dither, divider, `followAir`, skip on busy) | **Yes** on llvmpipe: the fake target hands out a GL texture in place of the D3D11 surface, and the read-back is compared with golden pixels | — | the EGLImage and FBO of a real D3D11 texture (ANGLE), the WGL lock path |
| Receive path into the renderer | **Yes**: a `GpuSurface` with `Api = None` and a test importer that returns a GL texture runs `VideoTextureSet` and the resolver for real; the CPU path (`Bgra8`, `Rgba16`) uploads for real | — | `AngleD3D11Importer` with RGBA surfaces, `WglDxInterop` |
| Verbs, STATE, context, MCP, desk | **Yes** | a fake provider registry reports each state | — |
| Syphon | managed wrapper **yes**, against `fake_syphon.c` built with clang (installed) into a `.so` exporting the same ABI | — | the Objective-C shim (needs macOS and Xcode) |
| Windows end to end | — | — | **Compile-only** here (no Windows, no GPU). A future `windows-latest` CI job (R0.4) can run the Win32 kernel for real, and D3D11 shared textures on WARP if WARP allows `MISC_SHARED` [U]. The lab runs PlayMap ↔ Resolume, TouchDesigner and SpoutDemo loops (§2 M5) |

Honest summary: the protocol, the logic and the renderer side are tested for real. Every call into Windows, D3D11
and Objective-C is compile-only here, behind small interfaces whose fakes encode the behaviour read from the Spout and
Syphon sources.

## 8. Desk and Companion surface

- **Outputs drawer → "Inputs" tab:** discovered senders (Spout, then Syphon, OMT, NDI in their own groups), each with
  a live thumbnail (S2, `VIEW … input:`), its size, format, fps and the sender's program. Drag one onto a surface,
  output or canvas layer (`INPUT USE`), or press **Add** (`INPUT ADD`). Right-click: "Add as input", "Use on
  selected surface", "On loss → Hold / Black / Fallback…" (`INPUT FALLBACK`), "Show sender program".
- **"Sends" tab:** one row per send with a state chip (`live`, `busy`, `off`, `refused`), fps, drops and the
  published name. Right-click: "Enable/Disable" (`SEND ENABLE`, High: the button says so), "Tap → …", "Format → …",
  "Copy published name".
- **Right-click everywhere it belongs:** on an output, "Send this output to Spout" (post-warp) and "Send before warp"
  (`SEND ADD … output:/prewarp:`); on a canvas, playlist or generator, "Send to Spout"; on a live input in the
  library, the input menu above.
- **Readiness bar:** a lost on-air input turns it red with "Arena lost — showing Loop B".
- **Companion** (the Companion package generates the module):
  - variables `input_<id>_state`, `_fps`, `_size`; `send_<id>_state`, `_fps`; `spout_senders`;
  - feedbacks: input live/stale/lost (green/amber/red); send live/busy/off;
  - actions: `SEND ENABLE` (High, confirm), `INPUT FALLBACK`, `INPUT USE`;
  - **live auto-created presets** from `videoio.discovered`: a new Spout sender appears as a button "Spout: Arena →
    selected surface" within one discovery tick, and a vanished sender's button turns grey.

## 9. Risks and open questions

1. **Rendering into an EGLImage of a D3D11 texture through ANGLE** (the send path on bridge (a)) [U].
   Fallback: `eglCreatePbufferFromClientBuffer(EGL_D3D_TEXTURE_ANGLE)` and a make-current per send. Lab test 2 in
   present.md §12 adds this case.
2. **WGL interop lock cost** varies by vendor (present.md §4: Intel's unlock waits for all GL work). Mitigation: one
   short blit inside the lock; bridge (a) as soon as R2.1 lands; the lab measures it per vendor.
3. **Cross-process synchronisation without keyed mutexes** relies on WDDM's implicit synchronisation of shared
   resources, exactly as Spout itself does [U]. Keyed textures are honoured when a sender makes them.
4. **`R16G16B16A16_UNORM` on ANGLE** goes through the CPU path (amber). This is common with 16-bit GL senders that
   use SpoutGL's interop [E].
5. **Senders on another GPU** take the CPU path, as in Spout. Preflight names the fix.
6. **Privacy:** any program in the Windows session can read a Spout send. A confidential show should not send.
   STATE says this in `videoio.spout.words` when a send exists, and so does the Show Mode checklist.
7. **Name collisions:** PlayMap refuses rather than renaming (Spout renames `name_1`, `name_2` when asked [V]),
   because a silently renamed send breaks the receiver's saved routing.
8. **Spout version drift:** 2.007.018 changed no protocol [V videoio.md §2.1]. Protocol constants live in one file
   with their source line references, so a future change is one diff.
9. **Open:** does WARP allow `MISC_SHARED` textures and `OpenSharedResource` in one process (for a Windows CI job
   without a GPU)? Does the macOS render host get its main-thread hook this round (if not, Syphon stays at S1)?

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| M1 shared spine (model, contracts, scheme routing, ten verbs, STATE, context), if not landed by OMT/NDI | 9 |
| M2 Spout protocol layer + fake kernel + golden tests | 9 |
| M3 receiver (GPU and CPU paths, adapter probe, state machine) + fake device + tests | 8 |
| M4 sender (registration, per-frame publish, metadata, resize) + tests | 6 |
| M5 Render: SendStage, `send` program, WGL interop, ANGLE send/receive wiring (compile-only parts written to the verified APIs) | 9 |
| M6 desk lists and right-click, NodeSettings, papers | 4 |
| **Must total** | **45** (36 if the OMT/NDI package has landed the spine) |
| S1 Syphon wrapper, provider, shim source, fake shim tests | 6 |
| S2 thumbnails and `INPUT USE`; S3 metadata read; S4 Companion contract | 5 |
| **With should** | **56** |
