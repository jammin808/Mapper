# Video IO for PlayMap: Spout 2.007, Syphon, OMT, NDI 6.3, capture cards, WHIP/WHEP

Research date: 13 October 2026. Scope: what PlayMap needs to receive live video (inputs as sources) and to send its
own pictures (per-output and multiview sends), with enough wire, memory and API detail to implement Spout and OMT
natively in C#, NDI through the user-installed runtime, and capture cards and WebRTC later.

**Evidence tags.** `[V]` = I opened the source (a file in a clone I made, a page fetched in full, or a local
command run on this machine). `[V-x]` = I read the passage in a search-engine extract of that page, not the full
page. `[U]` = unverified (could not open it, or it is from memory). `[I]` = my inference or arithmetic, not a
source fact. Every fact carries a URL or a path and a date. Clones were made today under
`scratchpad/ext/` (`spout2`, `omt-libomt`, `omt-libomtnet`, `omt-libvmx`, `ndi-headers`); commit IDs are given.

---

## 0. Summary

| Protocol | Latest version (date) | Licence | What PlayMap should do | Linux testable? |
|---|---|---|---|---|
| **Spout** | 2.007.018 (10 Oct 2026) [V] | BSD-2-Clause [V] | Native C# sender and receiver over D3D11 KMT shared textures plus named Win32 objects. No C++ library. GL path through `WGL_NV_DX_interop2` until the D3D11 renderer (R2.1) lands | Layouts and algorithms yes; D3D11/Win32 compile-only |
| **Syphon** | SDK 5 (2 Mar, year not shown) [V] | BSD-3-Clause [V] | macOS only, needs Objective-C (a shim or `objc_msgSend`). Defer to the macOS engine (ROADMAP L6) | No |
| **OMT** | libomtnet 1.0.0.20 in source, binary release v1.0.0.19 (24 Aug 2026) [V] | MIT (libomtnet, libomt, libvmx) [V] | Vendor the libomtnet **source** (managed, .NET Standard 2.0) and build `libvmx` per RID with clang. No NuGet package exists | **Yes, end to end** (loopback, Discovery Server, VMX via clang) |
| **NDI** | 6.3.2 (13 Apr 2026) [V] | Proprietary SDK; the headers are MIT [V] | Optional plugin that loads the user-installed runtime (`NDI_RUNTIME_DIR_V6`, `libndi.so.6`, `libndi.dylib`). Receive everything; send SDR High Bandwidth only (HX and HDR sending need the Advanced SDK) | Compile-only, plus a fake `libndi.so.6` shim for binding tests |
| **DeckLink** | Desktop Video 16.0 SDK (8 Apr 2026), driver 16.4 [V] | Blackmagic EULA; the `Include` folders are exempt from its restrictive clauses [V-x] | COM interop generated from the SDK IDL (Windows) and dispatch via `libDeckLinkAPI` (Linux/macOS). Never ship an FFmpeg built with `--enable-decklink` (that build is nonfree) [V] | Compile-only + fake COM objects |
| **AJA** | NTV2 SDK 18.0 (12 May 2026) [V] | MIT (`libajantv2`) [V]; `ajacc` static lib is proprietary [V] | Later: a small C++ shim over libajantv2, P/Invoked | Compile-only |
| **WHIP / WHEP** | WHIP = RFC 9725 (Mar 2025); WHEP = draft-ietf-wish-whep-05 (11 Oct 2026, submitted to IESG) [V-x] | Open standards | Phones and browsers: WebCodecs over the existing WebSocket first; WHIP sending through FFmpeg's `whip` muxer (in the FFmpeg 9 build PlayMap already loads); optional MIT MediaMTX sidecar for a WHEP/WHIP server. Avoid SIPSorcery (licence carries a field-of-use restriction) | Yes (Chromium + FFmpeg locally) |

The headline design decision: **one live-input abstraction (`spout:`, `omt:`, `ndi:`, `decklink:`, `syphon:`,
`whip:` and FFmpeg URLs as `MediaKind.Stream` items) and one send abstraction (taps on an output, a canvas or a
multiview)**. Frames can be CPU planes or GPU surfaces, and the GPU path is shared with D3D11VA zero-copy (R2.2). See §9.

---

## 1. Where PlayMap stands today (read from the repository, 13 Oct 2026)

All `[V]` from `/home/user/mapper` at `be9b24c` ("Merge the render host…").

- `IVideoSource` / `VideoFrameLease` (`src/PlayMap.Core/Media/VideoFrames.cs`): a lease exposes **CPU planes only**
  (`FramePlane(nint Data, int Stride, int Width, int Height)`). `PixelLayout` has `Rgba8, Bgra8, Nv12, P010,
  Yuv420P, Yuv422P, Yuv444P, Yuv420P10, Gray8, RgbaHalf`. There is **no packed 4:2:2** (`UYVY`), no `UYVA`, no
  `P216`/`PA16` (the native formats of OMT and NDI), and no GPU-surface lease (needed for Spout, D3D11VA zero-copy
  and HAP).
- `CaptureSource` (`src/PlayMap.Media/Sources/CaptureSource.cs`): libavdevice capture (`v4l2`, `dshow`,
  `avfoundation`, `lavfi`, or a network URL). Buffering is turned off, the latest frame wins, and each frame carries
  `ArrivalNs` on the monotonic clock. This is the right model for every live protocol.
- `SourceKind.Live` and `MediaKind.Stream`: `EngineSources.LiveFor` (`src/PlayMap.Engine/Rendering/EngineSources.cs`)
  makes a `MediaItem(Kind: Stream, Source: <device or url>)` from a library item or a camera rig.
  `IMediaBackend.Open(item)` returns an `IVideoSource`, and `Live()` always asks for the newest frame
  (`Acquire(0)`). A URI-scheme dispatcher in the media backend is therefore the natural place to plug in protocols.
- Readback: `GlDevice.Resources.cs` has `PixelPackRing` (non-stalling PBO readback for previews) and a blocking
  `ReadPixels`. That gives a CPU path for OMT and NDI sends on the GL renderer.
- Plans: ROADMAP R3.3 "Spout in/out; OMT in/out; NDI 6.3.x as an optional plugin through the user-installed
  runtime; NDI/OMT out per output plus multiview; NDI PTZ cameras as scan cameras". M13 "DeckLink via COM API,
  SRT/RIST inputs, WHEP previews, WHIP camera ingest". L6 "macOS engine (Metal, VideoToolbox zero-copy, Syphon)".
  COMPETITIVE §8: "Spout and OMT always available; NDI as an optional plugin through the user-installed runtime".
- The verb catalogue (`src/PlayMap.Core/Wire/VerbCatalog.cs`) uses `NOUN VERB` names (`DEVICE DISCOVER`,
  `OUTPUT SOURCE`, `LATENCY MEASURE`). It has no input or send verbs.
- Sister apps: Patterns already has an MIT NDI interop (`/home/user/patterns-svc/src/Patterns.Ndi/`, 1,457 lines:
  runtime discovery via `NDI_RUNTIME_DIR_V6/V5`, `libndi.so.6`, `libndi.dylib`; sender, receiver, `P216Converter`).
  Its receiver still uses `NDIlib_recv_capture_v2`. Stagewatch has `Timer.Output/NdiNative.cs`. Patterns is
  "MIT License, Copyright (c) 2026 jammin808" [V `/home/user/patterns-svc/LICENSE`].

---

## 2. Spout 2.007 (Windows)

### 2.1 Version, licence, repository

- Latest release **2.007.018, published 10 Oct 2026**. The repository head is commit `6b68a5b` "Version 2.007.018",
  dated Sat 10 Oct 2026 [V clone `ext/spout2`; V https://github.com/leadedge/Spout2/releases, fetched 13 Oct 2026].
  Earlier releases: 2.007.017 on 22 Oct 2025 and 2.007.016 on 1 Mar 2025 [V same page; years inferred from
  UPDATES.md].
- 2.007.018 source changes are mostly hygiene: `HoldFps` became `double`, `ReadPixelData` no longer calls
  `FlushWait`, and copy routines were optimised [V `UPDATES.md`, "10.10.26 Cumulative update"]. **None of them
  changes the shared-memory or handle protocol**, so a native implementation written against 2.007 stays
  compatible [I].
- Licence: **BSD 2-Clause**, "Copyright (c) 2020, Lynn Jarvis" (repository LICENSE); file headers say
  "Copyright (c) 2014-2026" [V `LICENSE`, `SpoutSenderNames.h`]. Reimplementing the protocol in C# is fine.
  Credit Spout in THIRDPARTY-NOTICES because the layouts come from reading its code [I].
- Spout is a Windows texture-sharing system "in a similar way to Syphon for the Mac" [V `README.md`]. The SDK has
  SpoutGL (OpenGL + DX interop), SpoutDX (D3D11), SpoutDX9, and SpoutDX12 (D3D11On12) [V directory listing].

### 2.2 Named kernel objects (the whole protocol)

Every name is an ANSI string in the session namespace (no `Global\` prefix) [V `SpoutSharedMemory.cpp`,
`SpoutFrameCount.cpp`].

| Object | Name | Size / parameters | Who creates it | Source |
|---|---|---|---|---|
| Sender-name list (file mapping, paging-file backed) | `SpoutSenderNames` | `MaxSenders × 256` bytes; default MaxSenders = **64** → 16,384 B. A map created by a 2.004 app may be 10 × 256 and never grows | First sender (`CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, size, name)`) | `SpoutSenderNames.cpp` l.169–175, 1138 [V] |
| Its lock | `SpoutSenderNames_mutex` | `CreateMutexA(NULL,false,…)`; wait **67 ms** | Everyone who opens the map | `SpoutSharedMemory.cpp` Create/Open/Lock [V] |
| Active sender | `ActiveSenderName` (+ `ActiveSenderName_mutex`) | 256 B, one null-terminated name | Set on every new registration | `SpoutSenderNames.cpp` l.1192 [V] |
| Per-sender info | `<sendername>` (+ `<sendername>_mutex`) | **280 B** `SharedTextureInfo` | Sender | `UpdateSender` l.876–877 [V] |
| Texture access mutex | `<sendername>_SpoutAccessMutex` | named mutex, wait 67 ms | Sender creates; receiver opens (or creates it if absent) | `SpoutFrameCount.cpp` l.831–903 [V] |
| Frame counter | `<sendername>_Count_Semaphore` | `CreateSemaphoreA(NULL, 1, LONG_MAX, name)` | Both, **only if** `HKCU\Software\Leading Edge\Spout\Framecount` = 1 | l.166, 321–329 [V] |
| Frame sync event (opt-in, 2.007) | `<sendername>_Sync_Event` | auto-reset, initially non-signalled | Whoever calls `SetFrameSync` | l.1006, 1310 [V] |
| Per-sender data buffer (optional) | `<sendername>_map` (+ `_map_mutex`) | caller-defined | `WriteMemoryBuffer` | `SpoutGL.cpp` l.2589–2615 [V] |
| Settings | `HKCU\Software\Leading Edge\Spout` values `MaxSenders` (DWORD), `Framecount` (DWORD) | — | SpoutSettings app | l.173, `SpoutFrameCount.cpp` l.166 [V] |

### 2.3 `SharedTextureInfo` (280 bytes, little-endian, packed)

From `SpoutSenderNames.h` [V]. Offsets are my arithmetic (all fields are 4-byte aligned, so there is no padding) [I].

| Offset | Field | Type | Meaning |
|---|---|---|---|
| 0 | `shareHandle` | `uint32` | D3D11 **legacy (KMT) shared handle**, truncated to 32 bits (`PtrToUint`). KMT handles are global, so 32 bits is enough |
| 4 | `width` | `uint32` | pixels |
| 8 | `height` | `uint32` | pixels |
| 12 | `format` | `uint32` | `DXGI_FORMAT` value; 0 or D3D9 21/22 (`D3DFMT_A8R8G8B8`/`X8R8G8B8`) mean `DXGI_FORMAT_B8G8R8A8_UNORM` (87) |
| 16 | `usage` | `uint32` | unused, written 0 |
| 20 | `description[256]` | bytes | the sender's full exe path (ANSI), from `QueryFullProcessImageNameA` |
| 276 | `partnerId` | `uint32` | bit 31 (`0x80000000`) = sender uses CPU sharing methods; bit 30 (`0x40000000`) = GL/DX-interop compatible (`SetSenderID`) |

A receiver treats `shareHandle == 0` as "no sender" (`spoutDX::ReceiveSenderData`) [V `SpoutDX.cpp` l.2290ff].
A **change of handle** means "the sender re-created its texture" (resize or format change). The receiver releases
and re-opens [V same].

### 2.4 Sender-name list algorithm

[V `SpoutSenderNames.cpp` `RegisterSenderName` l.200–262, `readSenderSetFromBuffer`/`writeBufferFromSenderSet`
l.1063–1120]

- The list is an array of fixed **256-byte, null-padded** slots, written from a C++ `std::set<std::string>`, so it
  is **sorted bytewise**. Readers stop at the first slot whose first byte is 0, or after `MaxSenders` slots. If the
  list is not full, the writer puts a 0 in the slot after the last name.
- Registration: lock `SpoutSenderNames_mutex` → read the set → if the name exists and `bNewname` is set, try
  `name_1`, `name_2`… → insert → if the insert failed, run `cleanSenderSet()` (remove names whose
  `<name>` map no longer opens) and retry → write the whole set back → `SetActiveSender(name)` → unlock.
  Registration fails when `size == MaxSenders`.
- Release (`ReleaseSenderName`): lock, remove, write back, unlock. A sender's maps disappear when the last handle
  closes (paging-file mapping), so a crashed sender leaves a stale **name** but no `<name>` map. `CleanSenders`
  removes it [V l.984–1041].
- C# notes [I]: use `CreateFileMappingW`/`OpenFileMappingW` via `LibraryImport` (names are ASCII-safe). When
  opening an existing list, find its real size with `VirtualQuery` on the view, because `OpenFileMapping` gives no
  size (Spout notes "Only the process that creates the shared memory can save its size") [V `SpoutSharedMemory.cpp`
  Open]. Compare names with ordinal byte comparison to keep the `std::set` order.

### 2.5 The shared texture

[V `SpoutDirectX.cpp` `CreateSharedDX11Texture` l.550–720, `OpenDX11shareHandle` l.940–975; `SpoutDX.cpp`
`CheckSender` l.2120–2200]

- Desc: `Usage = D3D11_USAGE_DEFAULT`, `BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE`,
  `MipLevels = 1`, `ArraySize = 1`, `SampleDesc = {1,0}`, `CPUAccessFlags = 0`, `Format` = the requested format,
  defaulting to `DXGI_FORMAT_B8G8R8A8_UNORM`.
- `MiscFlags`: `D3D11_RESOURCE_MISC_SHARED` by default. With `bKeyed`, it is `D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX`.
  With `bNThandle` on feature level > 11_0, it is `D3D11_RESOURCE_MISC_SHARED_NTHANDLE` (and optionally keyed).
- Handle: `IDXGIResource::GetSharedHandle` (KMT), or `IDXGIResource1::CreateSharedHandle(NULL,
  DXGI_SHARED_RESOURCE_READ|WRITE, NULL, &h)` for NT handles. **In 2.007.018 no caller passes `bNThandle = true`
  or `bKeyed = true`**: `SpoutDX::CheckSender` calls the defaults, and no code anywhere calls `DuplicateHandle` or
  `OpenSharedResource1` [V grep over `SPOUTSDK/`]. An NT handle is process-local, so it could not travel through
  the 32-bit `shareHandle` field anyway [I]. **In practice, Spout means KMT handles plus a named mutex.**
- Receiving: `ID3D11Device::OpenSharedResource(handle, IID_ID3D11Texture2D, …)`. Then `GetDesc` for the real
  format, which overrides the `format` field [V].
- **Adapter rule.** `OpenSharedResource` "can crash/fails if the share handle has been created using a different
  graphics adapter". `spoutDX::GetSenderAdapter` loops through adapters, creating a dummy device on each, until the
  open succeeds [V `SpoutDX.cpp` l.1470–1530]. On a multi-GPU show PC, a Spout input is only zero-copy when the
  sender runs on the same GPU as the output that draws it. PlayMap's preflight should test this [I].
- GL interop: SpoutGL links a GL texture to the shared D3D11 texture through `wglDXOpenDeviceNV` /
  `wglDXRegisterObjectNV` / `wglDXLockObjectsNV` (`WGL_NV_DX_interop` and `NV_DX_interop2`) [V `SpoutGL.cpp`
  l.729–763, 1603–1755]. Formats compatible with that interop (from the same file): `R8G8B8A8_UNORM` (28),
  `B8G8R8A8_UNORM` (87, default), `R10G10B10A2_UNORM` (24), `R16G16B16A16_UNORM` (11), `R16G16B16A16_FLOAT` (10),
  `R32G32B32A32_FLOAT` (2) [V]. These map to GL `RGBA8`, `RGB10_A2`, `RGBA16`, `RGBA16F` and `RGBA32F`.

### 2.6 Synchronisation

[V `SpoutFrameCount.cpp`; `SpoutDX.cpp` l.534–542 (send), l.823–835 (receive)]

- **Named access mutex** (default): `WaitForSingleObject("<name>_SpoutAccessMutex", 67)` → `CopyResource` →
  `Flush` → `ReleaseMutex`. Both sides hold it only for the copy, never for a whole frame.
- **Keyed mutex** (only if the texture was created keyed): `IDXGIKeyedMutex::AcquireSync(0, 67)` /
  `ReleaseSync(0)`. Spout's note: "if a DX11 texture has been created with a keyed mutex, AcquireSync/ReleaseSync
  must be used or CopyResource fails" [V l.1100–1160]. A receiver must call `QueryInterface(IDXGIKeyedMutex)` and
  use the keyed path when it succeeds.
- **Frame count** (opt-in, registry `Framecount = 1`): the sender, inside the access lock, calls
  `WaitForSingleObject(sem, 0)` then `ReleaseSemaphore(sem, 2, NULL)` (net +1 per frame). The receiver calls
  `WaitForSingleObject(sem, 0)` then `ReleaseSemaphore(sem, 1, &prev)`. **`prev` is the sender's frame number**;
  an unchanged number means "not new" [V l.500–610]. A sender that does not count leaves the value at 0, which
  means "always new".
- **Frame sync events** (2.007): the sender or receiver calls `WaitFrameSync(name, timeout)` before processing; the
  other side calls `SetFrameSync(name)` after. They pace a fast sender to a slow receiver, or the other way round.
  If the event does not exist, the wait returns true and does not block [V `Spout.cpp` l.1380–1420,
  `SpoutFrameCount.cpp` l.1000–1030]. **A show renderer must never wait on this event.** PlayMap should only ever
  `SetFrameSync` (as a receiver after it copies a frame, or as a sender after it publishes one), so third-party
  apps can pace themselves to PlayMap [I].

### 2.7 Native C# implementation plan (no C++ library)

All steps restate the verified behaviour above; the C# shape is my design [I].

**Sender (per send, D3D11 renderer):**
1. Create the shared texture with the desc in §2.5 (`MiscFlags = SHARED`, BGRA8, or RGBA16F for high-bit-depth
   sends). Get its KMT handle and `Flush`.
2. Register the name (§2.4), create the `<name>` map and `<name>_mutex`, write `SharedTextureInfo` (handle, size,
   format, usage 0, exe path, `partnerId = 0x40000000`), and create `<name>_SpoutAccessMutex`. Create
   `<name>_Count_Semaphore` (always: it is harmless and lets receivers show fps).
3. Each frame, on the present thread **after** the output is drawn: try the access mutex with a **0 ms** wait
   (not 67). If it is busy, skip this frame and count a drop. Then `CopyResource(shared, outputTap)` → `Flush` →
   semaphore +1 → `ReleaseMutex` → `SetFrameSync` if the event exists.
4. Resize: create a new texture and handle, rewrite the info, keep the name.
5. Dispose: remove the name, close the handles.

**Receiver (a `LiveInput` of scheme `spout:`):**
1. Discovery: read `SpoutSenderNames` under its mutex every 500 ms (cheap), and also read `ActiveSenderName`.
2. Open the `<name>` map and read the info. When the handle changes, `OpenSharedResource` on **PlayMap's**
   device. On failure, report "sender is on another GPU (adapter N)". Then open the access mutex and semaphore.
3. Each render frame (render thread, non-blocking): read `prev` from the semaphore; if it is new, try the access
   mutex (or keyed mutex) with a 0–1 ms wait, `CopyResource(own, shared)`, release. The lease points at **PlayMap's
   own copy**, never at the sender's texture, because the sender can free or resize its texture at any moment
   [I; mirrors `SpoutDX` receive l.823–835 V].
4. Stamp `ArrivalNs` when the frame number changes. Spout carries no timestamps.

**Current GL renderer (before R2.1):** the same objects, plus `WGL_NV_DX_interop2` to register PlayMap's own
D3D11 copy as a GL texture: `wglDXRegisterObjectNV(dev, d3dTex, glTex, GL_TEXTURE_2D, WGL_ACCESS_READ_ONLY_NV)`,
then lock and unlock around sampling. NuGet `Silk.NET.WGL.Extensions.NV` 2.23.0 exists [V
https://api.nuget.org/v3-flatcontainer/silk.net.wgl.extensions.nv/index.json, 13 Oct 2026]; whether it covers
`NV_DX_interop2` is [U]. A fallback import path is `GL_EXT_memory_object_win32`, which takes a D3D11 KMT handle
[U, Khronos registry not opened].

**Bindings:** Silk.NET.Direct3D11 / Silk.NET.DXGI 2.23.0, matching PlayMap's existing Silk.NET 2.23.0 [V NuGet
index; V `src/*/*.csproj`]. Vortice.Direct3D11 3.8.3 (MIT) is an alternative [V nuspec]. Existing .NET wrapper:
`Spout.NET` 0.1.2 (MIT, Ruminoid) [V nuspec]. It wraps the C++ library and is not maintained for .NET 10 [U];
do not use it.

**Size estimate:** about 900 lines of C# (names, maps, mutexes, semaphore, D3D11 open/copy, GL interop) [I].

---

## 3. Syphon (macOS)

- Licence **BSD-3-Clause**: "Copyright 2010 bangnoise (Tom Butterworth) & vade (Anton Marini)" [V
  https://raw.githubusercontent.com/Syphon/Syphon-Framework/master/License.txt, 13 Oct 2026].
- Latest SDK: **Syphon SDK 5**, released "02 Mar" (the page shows no year), "adds support for Core Profile
  OpenGL" [V https://github.com/Syphon/Syphon-Framework/releases]. The framework also has Metal servers and clients
  (`SyphonMetalServer`/`SyphonMetalClient`) [V-x https://cansik.github.io/syphon-python/syphon.html]. The release
  date of the Metal additions is [U].
- **Can a non-Objective-C host use it? Yes, through an Objective-C bridge.** Every wrapper found does it that way:
  - `Agash/Syphon.NET`: .NET bindings, zero-copy over IOSurface-backed Metal textures, NativeAOT-friendly,
    targets `net11.0-macos` with "a small shim" compiled with the unmodified framework [V-x
    https://github.com/Agash/Syphon.NET, licence not checked [U]].
  - `syphon-python` (PyObjC, no C wrapper), `syphon-rs` / `syphon-core` (Rust `objc2`) [V-x
    https://github.com/cansik/syphon-python, https://github.com/BlueJayLouche/syphon-rs (6 Mar 2026),
    https://crates.io/crates/syphon-core].
  - The protocol itself (IOSurface plus the server directory's distributed notifications) has no published
    specification [U]. Use the framework, not a reimplementation.
- PlayMap: Syphon belongs with the macOS engine (ROADMAP L6). The plan: a ~200-line Objective-C shim
  (`playmap_syphon.dylib`) that exports C functions (`server_create(name)`, `server_publish_iosurface`,
  `directory_list`, `client_new_frame → IOSurfaceRef`). Ship it beside `Syphon.framework` (BSD-3: reproduce the
  notice) [I]. `Syphon.NET` targets .NET 11, which is newer than PlayMap's .NET 10, so it cannot be adopted as is
  [I].

---

## 4. OMT, Open Media Transport

### 4.1 Who, licence, versions, adoption

- Made by the **vMix team** (StudioCoast). The VMX codec "began as the vMix Video Codec used for the vMix Instant
  Replay feature" [V `ext/omt-libvmx/README.md`]. Releases are published by GitHub user `vmixmartin` [V releases
  page]. The GitHub org `openmediatransport` was created 16 May 2025 and has 12 public repositories [V
  https://github.com/openmediatransport, 13 Oct 2026]. Trade press credits "developers linked to vMix and Sienna"
  [V-x https://tmbroadcast.com/omt-market-new-protocol-ndi/, 17 Nov 2025].
- Licence: **MIT** for libomtnet, libomt and libvmx ("Copyright (c) 2025 Open Media Transport Contributors") [V
  `LICENSE.txt` in each clone]. "Royalty and patent free" [V org README].
- Versions [V clones and https://github.com/openmediatransport/libomtnet/releases]:
  - libomtnet `libomtnet.csproj` `Version 1.0.0.20`, head `7d29031` "Discovery improvements and fixes on Windows",
    **13 Oct 2026**.
  - Binary release **v1.0.0.19, 24 Aug 2026** (libomtnet .NET Standard 2.0, libomt, libvmx, OBS plugin,
    OMTDiscoveryServer; "improvements to discovery under MacOS and reliability fixes to libvmx").
  - v1.0.0.16, 2 Jun 2026 ("network performance optimisation for receivers … jitter on 2.5G and higher networks").
  - v1.0.0.14, 11 Feb 2026; v1.0.0.13, 2 Oct 2025; v1.0.0.9, 16 Aug 2025.
  - libomt head `bb4b67b`, 24 Aug 2026; libvmx head `a1828cb`, 23 Sep 2026.
  - Binaries ship for Windows and macOS only. **Linux builds from source.**
- Adopters: vMix 29 (27 Oct 2025, full OMT input and output) [V-x https://blog.vmix.com/vmix-29-is-available-now/];
  official OBS plugin `omtplugin` (sends 10-bit+ when OBS is set to P010/P216) [V-x
  https://github.com/openmediatransport/omtplugin/]; Nimble Streamer (Ubuntu only, 14 Oct 2025) [V-x
  https://softvelum.com/2025/10/omt-open-media-transport-nimble-streamer/]; Raspberry Pi 5 encoder and decoder
  (`omtcapture`, `omtplayer`) [V org page]; a community TouchDesigner 2025 Custom TOP [V-x
  https://github.com/MikanseiLaboratory/touchdesigner-omt]; Zoe 1.6 [V-x https://rsmck.co.uk/blog/zoe-live-omt/,
  12 Jul 2026]. Official OMT support in Resolume, MadMapper, Millumin, disguise, Pixera or Watchout was **not
  found** [U]. Being an OMT-native mapper is therefore a differentiator [I].

### 4.2 Wire protocol (OMT Protocol 1.0)

Source: `ext/omt-libomtnet/PROTOCOL.md` [V], cross-checked with `OMTFrame.cs` (`Header = 16`,
`ExtendedHeaderVideo = 32`, `ExtendedHeaderAudio = 24`) and `OMTConstants.cs` [V]. Little-endian throughout.

**Transport:** one TCP connection per receiver (unicast) with `NoDelay = true` and keep-alive [V
`OMTChannel.cs` l.75–90]. A sender listens on a port in **6400–6600** (`NETWORK_PORT_START/END`). The URL prefix is
`omt://` [V `OMTConstants.cs`].

**Frame header, 16 bytes, packed:**

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | `Version` = 1 |
| 1 | 1 | `FrameType`: Metadata = 1, Video = 2, Audio = 4 |
| 2 | 8 | `Timestamp`, int64, **1 s = 10,000,000** (100 ns units). Sending −1 asks the sender to generate timestamps and throttle to the frame rate [V `OMTPublicTypes.cs`] |
| 10 | 2 | `MetadataLength`, uint16: per-frame XML length **including** the null |
| 12 | 4 | `DataLength`, int32: extended header + data + metadata, excluding this header |

**Video extended header, 32 bytes:** `Codec` (FourCC) · `Width` · `Height` · `FrameRateN` · `FrameRateD` ·
`AspectRatio` (float32, width/height) · `Flags` (Interlaced = 1, Alpha = 2, PreMultiplied = 4, Preview = 8,
HighBitDepth = 16) · `ColorSpace` (0 undefined, 601, 709). **There is no BT.2020, PQ or HLG field** [V; enum
`OMTColorSpace { Undefined = 0, BT601 = 601, BT709 = 709 }`].

**Audio extended header, 24 bytes:** `Codec` (`FPA1` = 32-bit float planar) · `SampleRate` ·
`SamplesPerChannel` · `Channels` · `ActiveChannels` (uint32 bitfield; silent channels are skipped) · `Reserved1`.

**FourCC values** [V `OMTPublicTypes.cs` `OMTCodec`]: `VMX1 = 0x31584D56`, `UYVY = 0x59565955`,
`YUY2 = 0x32595559`, `BGRA = 0x41524742`, `NV12 = 0x3231564E`, `YV12 = 0x32315659`, `UYVA = 0x41565955`,
`P216 = 0x36313250`, `PA16 = 0x36314150`, `FPA1 = 0x31415046`. Only `VMX1` travels on the wire. The others are
API pixel formats. Receivers can ask for `UYVY`, `UYVA`, `BGRA`/`BGRX`, `P216` or `PA16` through
`OMTPreferredVideoFormat` (`UYVY = 0`, `UYVYorBGRA = 1`, `BGRA = 2`, `UYVYorUYVA = 3`,
`UYVYorUYVAorP216orPA16 = 4`, `P216 = 5`).

**Control metadata (exact strings, matched as strings, not parsed)** [V PROTOCOL.md]:
- `<OMTSubscribe Video="true" />`, `<OMTSubscribe Audio="true" />`, `<OMTSubscribe Metadata="true" />`. A sender
  sends nothing until it receives a subscribe.
- `<OMTSettings Preview="true" />` / `"false"`: receive a preview instead of full resolution. The receiver
  `Preview` flag is documented as "a 1/8th preview" [V `OMTPublicTypes.cs`]. VMX carries a preview layer
  (`VMX_DecodePreviewBGRA` etc.) [V `vmxcodec.h`].
- `<OMTTally Preview="true" Program=="false" />`: note the **double `==` typo**, kept for compatibility. The sender
  combines tally from all receivers and broadcasts the result.
- `<OMTSettings Quality="Default|Low|Medium|High" />`. A sender set to Default takes the highest receiver
  suggestion. A sender set explicitly overrides receivers [V PROTOCOL.md; V
  https://www.vmix.com/knowledgebase/article.aspx/380/video-quality-settings-for-omt, updated 3 Nov 2025].
- `<OMTInfo ProductName="…" Manufacturer="…" Version="…" />`: optional sender information.
- **Redirect / routing**: a sender can redirect receivers to another sender (`OMTSend.SetRedirect`). The receiver
  keeps a control connection to the virtual sender [V org README "Routing"; `OMTSend.cs` l.230].

**Discovery:** DNS-SD service type `_omt._tcp`. The instance name is `HOSTNAME (Source Name)._omt._tcp.local` and
the machine name is upper-cased on Linux [V PROTOCOL.md; `LinuxPlatform.cs`]. Platform back-ends: Windows
`DnsApi` (`win32/DnsApi.cs`), macOS `dns_sd`, **Linux Avahi** (`libavahi-client.so.3`,
`libavahi-common.so.3`, which needs `avahi-daemon` running) [V `linux/AvahiClient.cs`]. **Discovery Server**:
TCP, default port **6399** (`DISCOVERY_SERVER_DEFAULT_PORT`). Clients send `<OMTAddress><Name>…</Name><Port>…</Port>
<Addresses>…</Addresses></OMTAddress>` (and `<Removed>True</Removed>` to deregister). The server ignores the
client's own addresses and uses the connection's source IP [V PROTOCOL.md, `OMTConstants.cs`].

### 4.3 The VMX codec

[V `ext/omt-libvmx/README.md`, `src/vmxcodec.h`; V org README table]

- Intra-frame only, 4:2:2:4 with alpha, 10-bit (16-bit planar API), AVX2 "capable of encoding 2160p60 on a single
  Intel i7 core". The minimum CPU is SSE4.2 + SSSE3; ARM64 uses NEON via sse2neon.
- Profiles: `VMX_PROFILE_DEFAULT = 0`, `LQ = 33`, `SQ = 66`, `HQ = 99`, `OMT_LQ = 133`, `OMT_SQ = 166`,
  `OMT_HQ = 199`. OMT's default maps to `OMT_SQ` [V `OMTVMX1Codec.cs` l.105].
- API (C, all `DllImport`-able with `IntPtr` instances): `VMX_Create(size, profile, colorSpace)`, `VMX_Encode{BGRA,
  BGRX,UYVY,UYVA,YUY2,NV12,YV12,P216,PA16}`, `VMX_Decode{BGRA,BGRX,UYVY,UYVA,YUY2,P216,PA16}`,
  `VMX_DecodePreview*`, `VMX_SaveTo`, `VMX_LoadFrom`, `VMX_SetQuality`, `VMX_SetThreads`.
- **Bandwidth** (org README; 30 fps is half of 60 fps):

| Resolution | High | Medium (default) | Low |
|---|---|---|---|
| 2160p60 | 600 Mb/s | 300 Mb/s | 200 Mb/s |
| 1080p60 | 260 Mb/s | 200 Mb/s | 86 Mb/s |
| 720p60 | 136 Mb/s | 68 Mb/s | 45 Mb/s |

- **FFmpeg decodes VMX**, but only 8-bit `YUV422P` (`libavcodec/vmixdec.c`, Paul B Mahol 2023, FourCC `VMX1` in
  `riff.c`) [V raw files from FFmpeg master, 13 Oct 2026]. That means **no alpha and no 16-bit**. The local
  FFmpeg 9.0.2 lists the decoder `vmix` and **no VMX encoder** [V `/opt/ffmpeg9/bin/ffmpeg -decoders/-encoders`].
  So `libvmx` is needed to send, and to receive alpha or high bit depth.
- Build: Linux x64 is `clang++ -O3 -std=c++17 -fdeclspec -fPIC -mlzcnt -mavx2 -mbmi -shared vmxcodec_x86.cpp
  vmxcodec_avx2.cpp vmxcodec.cpp -o libvmx.so`; scripts exist for linux-arm64, macOS universal, Windows (Intel C++
  compiler, clang, or mingw) [V `build/`]. clang and clang++ are installed in this container [V `which clang`].

### 4.4 Latency

- Design goal: an "ultra-fast, low latency codec with less than a frame of delay" [V org README]. No measured
  glass-to-glass figure was found [U]. PlayMap should measure with its existing `LATENCY MEASURE` verb [I].
- The protocol guide says a receiver should never block the network callback: keep a small frame queue with one
  frame reserved for the async receive [V PROTOCOL.md]. libomtnet uses 4 video frames in its pool
  (`VIDEO_FRAME_POOL_COUNT = 4`) and an 8 MB receive buffer [V `OMTConstants.cs`].
- **`OMTSend.Send` encodes synchronously on the calling thread, under a lock** [V `OMTSend.cs` l.660ff]. PlayMap
  must call it from a send worker, never from the render or present thread [I].

### 4.5 .NET integration choice

- **There is no NuGet package**: `libomtnet`, `libomt`, `omt` and `openmediatransport` all return `BlobNotFound`
  from `api.nuget.org/v3-flatcontainer` [V, 13 Oct 2026]. The README tells .NET apps to copy `libomtnet.dll` and the
  platform `libvmx` beside the app [V].
- libomtnet targets `netstandard2.0;net40` and uses `AllowUnsafeBlocks` [V csproj]. It is about 10,800 lines of
  C#: the TCP channel, discovery per OS, VMX P/Invoke, an FPA1 audio codec and the Discovery Server/Client
  [V `wc -l`].
- `libomt` is the same code compiled with **.NET Native AOT** into a C library (`omt_send_create`, `omt_send`,
  `omt_receive_create`, `omt_receive`, `omt_discovery_getaddresses`, `omt_settings_*`) [V `libomt.h`]. A .NET app
  should **not** P/Invoke libomt: that would load a second .NET runtime in the process [I].
- **Recommendation [I]:** vendor libomtnet **as source** (MIT; keep the notice) into `src/PlayMap.Video.Omt/External/`,
  pinned to a commit, and compile it into PlayMap with nullable warnings suppressed for that folder. Build `libvmx`
  in CI for `win-x64`, `linux-x64`, `linux-arm64` and `osx-arm64/x64`, and ship it as a native asset. Why vendor
  rather than reimplement: the protocol is small, but discovery on three OSes, tally, redirect and quality voting
  are already done and maintained upstream. Why source rather than a binary: binaries ship for Windows and macOS
  only, and source lets PlayMap add `Span`-based zero-allocation paths later. Keep a thin `IOmtTransport` seam so a
  native C# reimplementation can replace it if upstream stalls.
- Gaps to cover in PlayMap [I]:
  1. No HDR or BT.2020 signalling. Proposal: when sending P216/PA16 with PQ or HLG, PlayMap adds per-frame XML
     metadata reusing NDI's element (`<ndi_color_info transfer="bt_2100_pq" matrix="bt_2020" primaries="bt_2020"/>`)
     and reads it back on receive, then documents this convention.
  2. Unicast TCP: every receiver costs a full stream, and there is no multicast [V-x TM Broadcast article]. The
     bandwidth preflight must count receivers per send.

---

## 5. NDI 6.3

### 5.1 Versions and dates

- **NDI 6.3.0, 21 Jan 2026**: new SDK APIs for sender discovery and monitoring with the NDI Discovery Server;
  better SpeedHQ quality; the receiver passes the NDI capabilities message to the sender; per-codec passthrough
  override in the **Advanced** SDK; the Discovery Server is a standalone installer for Windows and Linux. **6.3.1,
  9 Feb 2026**: fixes, including "potential deadlock … in the new discovery server with sources from older
  versions". **6.3.2, 13 Apr 2026**: fixes, including "an intermittent crash when destroying send or recv advertiser
  objects", and SpeedHQ rate-control improvements. 6.3.2 is the latest listed [V
  https://docs.ndi.video/all/getting-started/release-notes, fetched 13 Oct 2026]. The developer page also shows SDK
  "Version 6.3.2" [V-x https://ndi.video/for-developers/].
- Press release 22 Jan 2026: monitoring and stream diagnostics, HDCP via ProITAV hardware, Agilex 7 FPGA [V-x
  https://ndi.video/stories/press/ndi-6-3-core-tech-update-brings-advanced-control-and-visibility/].
- NDI 6.2 (3 Jun 2025) added receiver discovery, monitoring and control. 6.2.1 (21 Aug 2025) **changed the SDK
  terms** (§5.5) [V release notes].

### 5.2 API (C; the headers are MIT, the SDK is not)

The headers come from DistroAV's vendored copy, `lib/ndi/*.h` on master [V
https://raw.githubusercontent.com/DistroAV/DistroAV/master/lib/ndi/, saved to `ext/ndi-headers`, 13 Oct 2026].
`Processing.NDI.Lib.h` says: "the following MIT license applies to this file ONLY and not to the SDK as a whole …
Copyright (C) 2023-2026 Vizrt NDI AB" [V]. **PlayMap may transcribe the struct and enum layouts into C#.**

- Library names and runtime location [V `Processing.NDI.Lib.h` l.51–68]:
  - Windows: `NDILIB_LIBRARY_NAME "Processing.NDI.Lib.x64.dll"`, `NDILIB_REDIST_FOLDER "NDI_RUNTIME_DIR_V6"`
    (an environment variable naming the runtime folder), `NDILIB_REDIST_URL "http://ndi.link/NDIRedistV6"`.
  - macOS: `libndi.dylib`, `http://ndi.link/NDIRedistV6Apple`. The redistributable installs in `/usr/local/lib`
    because macOS has no global environment variables [V https://docs.ndi.video/all/developing-with-ndi/sdk/dynamic-loading-of-ndi-libraries].
  - Linux: `libndi.so.6`, with an **empty redist URL** (there is no official Linux runtime installer).
- Dynamic load: `const NDIlib_v6* NDIlib_v6_load(void)` returns a function table `struct NDIlib_v6_3`
  (`typedef … NDIlib_v6`, also aliased v5/v4_5/v4/v3/v2). Members are unions of a short name and a deprecated
  long name (`initialize`/`NDIlib_initialize` …), with sections marked `// v6.1`, `// v6.2`, `// v6.3`
  [V `Processing.NDI.DynamicLoad.h` l.30–633]. Exported `NDIlib_*` symbols also work directly with
  `NativeLibrary.GetExport`, which Patterns already does [V `Patterns.Ndi/NdiInterop.cs`].
- Functions present in the 6.3 headers [V grep]: `NDIlib_initialize`, `NDIlib_destroy`, `NDIlib_version`,
  `NDIlib_find_create_v2`, `NDIlib_find_wait_for_sources`, `NDIlib_find_get_current_sources`,
  `NDIlib_find_destroy`, `NDIlib_recv_create_v3`, `NDIlib_recv_connect`, **`NDIlib_recv_capture_v3`**
  (`recv_capture_v2` is still present), `NDIlib_recv_free_video_v2`, `NDIlib_recv_free_audio_v3`,
  `NDIlib_recv_free_metadata`, `NDIlib_recv_send_metadata`, `NDIlib_recv_set_tally`, `NDIlib_recv_get_performance`,
  `NDIlib_recv_get_queue`, `NDIlib_recv_get_no_connections`, `NDIlib_recv_get_web_control`,
  `NDIlib_recv_add_connection_metadata`, `NDIlib_send_create`, `NDIlib_send_send_video_v2`,
  **`NDIlib_send_send_video_async_v2`**, `NDIlib_send_send_audio_v3`, `NDIlib_send_send_metadata`,
  `NDIlib_send_capture` (receive metadata upstream), `NDIlib_send_get_tally`, `NDIlib_send_get_no_connections`,
  `NDIlib_send_set_failover`, `NDIlib_send_add_connection_metadata`, `NDIlib_send_get_source_name`,
  `NDIlib_send_advertiser_create/add_sender/del_sender/destroy` (6.3 monitoring), and FrameSync:
  `NDIlib_framesync_create/capture_video/capture_audio(_v2)/audio_queue_depth/free_video/free_audio(_v2)/destroy`
  [V `Processing.NDI.FrameSync.h`].
- `NDIlib_send_send_video_async_v2`: the frame buffer must stay valid until the next synchronising event. Those
  events are another async send, a sync send, a send with `NULL`, or `send_destroy`. This allows "color
  conversion, compression and network sending … on separate threads" [V `Processing.NDI.Send.h` l.75–87]. Use a
  ring of at least 2 buffers [I].
- Structs:
  - `NDIlib_send_create_t { const char* p_ndi_name; const char* p_groups; bool clock_video, clock_audio; }` [V].
  - `NDIlib_recv_create_v3_t { NDIlib_source_t source_to_connect_to; NDIlib_recv_color_format_e color_format;
    NDIlib_recv_bandwidth_e bandwidth; bool allow_video_fields; const char* p_ndi_recv_name; }` [V].
  - `NDIlib_video_frame_v2_t { int xres, yres; NDIlib_FourCC_video_type_e FourCC; int frame_rate_N, frame_rate_D;
    float picture_aspect_ratio; NDIlib_frame_format_type_e frame_format_type; int64_t timecode; uint8_t* p_data;
    union { int line_stride_in_bytes; int data_size_in_bytes; }; const char* p_metadata; int64_t timestamp; }` [V
    `Processing.NDI.structs.h` l.214–252]. For `timecode`, "synthesize" is `INT64_MAX` [V Patterns
    `SendTimecodeSynthesize = long.MaxValue`]. That timecode and timestamp use 100 ns units is [U] (from memory).
- Enums [V]: video FourCCs `UYVY, UYVA, P216, PA16, YV12, I420, NV12, BGRA, BGRX, RGBA, RGBX`; audio `FLTP`.
  `NDIlib_recv_color_format`: `BGRX_BGRA = 0`, `UYVY_BGRA = 1`, `RGBX_RGBA = 2`, `UYVY_RGBA = 3`, `fastest = 100`,
  `best = 101`, `BGRX_BGRA_flipped = 1000`. `NDIlib_recv_bandwidth`: `metadata_only = −10`, `audio_only = 10`,
  `lowest = 0`, `highest = 100`. `NDIlib_frame_format_type`: `progressive = 1`, `interleaved = 0`.
- `color_format_fastest` gives the internal format with no conversion and "typically … lower latency". `best`
  gives "P216 when the underlying codec is native NDI, or 8-bpp UYVY … like H.264", or PA16 with alpha [V-x
  https://docs.ndi.video/all/developing-with-ndi/sdk/ndi-recv]. `recv_capture_v3` can be called from several
  threads at once (one for video, one for audio) [V-x same].

### 5.3 HX, HX2, HX3 and SpeedHQ

- Standard (free) SDK: "Decode NDI High Bandwidth, HX2, and HX3"; "Send multichannel SDR video … in NDI High
  Bandwidth format". Advanced (commercial) SDK: "Send all NDI formats, including HX2 and HX3 in SDR and HDR",
  "Genlock & AV Sync APIs", "KVM", "certification" [V-x https://ndi.video/for-developers/].
- So PlayMap **receives** HX, HX2 and HX3 PTZ cameras and **sends** High Bandwidth (SpeedHQ) only [I from the
  above]. On Linux, HX decoding needs FFmpeg ≥ 7.0 on the system: "otherwise it will crash" [V DistroAV wiki,
  https://github.com/DistroAV/DistroAV/wiki/1.-Installation, edited 1 May 2026].

### 5.4 HDR

[V https://docs.ndi.video/all/developing-with-ndi/sdk/hdr, fetched 13 Oct 2026]
- Standard SDK: "Can receive and process HDR content; **Cannot send content with HDR metadata**". Advanced: can
  send. NDI 5 and older show a placeholder frame.
- Signalling is per-frame XML in `p_metadata`: `<ndi_color_info transfer="bt_2100_hlg|bt_2100_pq"
  matrix="bt_2020" primaries="bt_2020" />`. It may be nested in `<ndi_metadata_group>`. Uncompressed HDR uses P216
  or PA16, **16-bit limited range** (pad 10- or 12-bit values with zero low bits). Receive with
  `color_format_best` to get P216/PA16. The SDK does not tone-map HDR to SDR when 8-bit is requested.

### 5.5 Licence, runtime, redistribution and trademark (can PlayMap load the user's runtime?)

- The SDK licence (NDI SDK License Agreement) grants a "nonexclusive royalty-free license … for the sole purpose
  of developing Products". **"Products" covers software on general-purpose computers** where the user can change
  the OS and install software, and "expressly excludes" hardware products, fixed-purpose appliances, and embedded
  devices on embedded OSes ("Windows IoT, Android, Linux … IOS") [V-x
  https://downloads.ndi.tv/SDK/NDI_SDK/NDI%20License%20Agreement.pdf]. Other clauses: §2(b) "If you are making a
  product release you must use a version of the SDK that is less than thirty (30) days old if there is one"; §3
  confidentiality and no redistribution of the SDK; §3(d) your end-user licence must forbid modifying or
  reverse-engineering the SDK [V-x same].
- Release notes for 6.2.1 (21 Aug 2025): "The NDI SDK remains royalty-free, subject to the SDK terms … updated
  list of exclusions" [V]. A commentator's reading of §2a: downloadable software on PCs or VMs is royalty-free;
  appliances need a commercial licence [V-x LinkedIn post by Elias Puurunen, 21 Aug 2025]. Fallout reported in
  2025–2026: Avolites removed NDI (COMPETITIVE.md, unverified); ETC Eos lost NDI [V-x rsmck.co.uk, 12 Jul 2026];
  an indie developer was quoted "over $5,000 per year" [V-x TouchDesigner forum, 27 Jul 2026, anecdotal].
- Distribution requirements [V https://docs.ndi.video/all/developing-with-ndi/sdk/licensing, "updated 1 month
  ago"]:
  1. a link to **ndi.video** "close to all locations where NDI is used/selected", on the website and in the docs;
  2. do not distribute NDI Tools (link to ndi.video/tools);
  3. "NDI®" with ® on first use per document, plus "**NDI® is a registered trademark of Vizrt NDI AB**" near the
     first use or in a footnote, and in the **About box**;
  4. ask NDI before using "NDI" in a product name;
  5. if you ship the DLLs, keep them in the app folder, never the system path;
  6. AAC, H.264 and H.265 licensing is your responsibility.
- **Answer [I, not legal advice]:** yes. PlayMap can load a runtime the user installed: Windows via
  `NDI_RUNTIME_DIR_V6` or winget `NDI.NDIRuntime`, macOS `/usr/local/lib/libndi.dylib`, Linux `libndi.so.6` in the
  linker path. No NDI binary ships with PlayMap, and the headers are MIT. PlayMap is a PC software "Product", so
  royalty-free terms apply. The branding obligations (1–4) should be met anyway: they are cheap, and the developer
  who downloads the SDK to test has accepted the licence. Open legal questions for review:
  - Does the 30-day "current SDK" rule bind a product that ships no SDK files? Mitigation: test each PlayMap
    release against the newest runtime, and show the runtime version (`NDIlib_version()`) in STATE.
  - A future PlayMap **appliance** (a fixed-purpose Linux box) would fall outside "Products" and need the Advanced
    SDK.
- Runtime install routes [V DistroAV wiki, 1 May 2026]: Windows `winget install --exact --id NDI.NDIRuntime` or
  https://ndi.link/NDIRedistV6. macOS `brew install --cask libndi` or https://ndi.link/NDIRedistV6Apple. Linux:
  DistroAV's `libndi-get.sh` downloads the SDK library from NDI (there is no official Linux runtime package).

### 5.6 Platforms, discovery and ports

- Linux is "fully supported". The library depends on `libavahi-common.so.3` and `libavahi-client.so.3` and needs
  `avahi-daemon` running. Config lives in `$HOME/.ndi/ndi-config.v1.json` (Windows:
  `C:\ProgramData\NDI\ndi-config.v1.json`), or the folder named by `NDI_CONFIG_DIR` [V
  https://docs.ndi.video/all/developing-with-ndi/sdk/platform-considerations]. The next major version will
  deprecate 32-bit Windows [V same].
- Firewall list used by DistroAV on Linux: 5353/udp (mDNS), 5959–5969 tcp/udp, 6960–6970 tcp/udp, 7960–7970
  tcp/udp, 5960/tcp [V DistroAV wiki].
- Discovery Server: a standalone installer for Windows and Linux since 6.3.0; a service [V release notes].

### 5.7 .NET wrappers and their licences

| Package / code | Licence | State | Verdict |
|---|---|---|---|
| `NDILibDotNetCoreBase` 2024.7.22.1 (Elias Puurunen) | MIT | targets net8.0; last release Jul 2024 (pre-6.3) [V nuspec] | Usable reference, stale |
| NDI SDK sample `NDILibDotNet2` | NDI SDK licence [U] | ships inside the SDK | Do not copy into PlayMap |
| NuGet `Ndi` 0.1.3 | — | unrelated: a DI container [V nuspec] | Name trap |
| **Patterns.Ndi** (in-house, MIT) | MIT [V] | runtime discovery, sender, receiver, P216 converter; uses `recv_capture_v2` [V] | **Port into PlayMap**, then move to `recv_capture_v3` + FrameSync |

---

## 6. Capture cards

### 6.1 Blackmagic DeckLink

- **Desktop Video 16.0 SDK, released 8 Apr 2026**, SDK manual of the same date; driver **Desktop Video 16.4**
  (adds NMOS registry IP configuration for DeckLink IP HD) [V
  https://www.blackmagicdesign.com/developer/products/capture-and-playback/sdk-and-software, 13 Oct 2026]. 14.3
  introduced `IDeckLinkVideoBuffer` and allocator providers and removed `IDeckLinkVideoFrame::GetBytes` [V-x
  DeckLink SDK Migration Guide PDF].
- API model: COM. On Windows it is "a native COM interface registered with the operating system". On other
  platforms "application code is provided to allow the same COM style interface to be used" (the dispatch source
  loads the driver's API library) [V-x https://sdk-doc.blackmagicdesign.com/decklink-sdk/]. It "requires a
  multithreaded COM apartment" on Windows. .NET bindings come from the IDL (`midl DeckLinkAPI.idl /tlb`,
  `tlbimp`) [V-x https://www.blackmagicdesign.com/developer/support/faq/desktop-video-developer-support-faqs]. An
  app built with an older SDK runs on newer drivers, not the other way round [V-x same].
- Licence: the SDK EULA says clauses 1, 4.3, 4.4, 5, 7 and 8 "do not apply to those files … in /Mac/Include,
  /Win/Include, /Linux/Include". Other files may be sub-licensed only in full. Sample code is for internal use
  only. Marks may be used only as "XXX compatible with Blackmagic Design DeckLink" [V-x
  https://downloads.blackmagicdesign.com/EULA/DeckLink/DeckLink-End-User-License-Agreement.pdf]. The `Include`
  headers carry a permissive, Boost-style notice [V-x debian-legal thread, 2017, an older source]. Projects vendor
  those headers on this basis, for example `blackmagic-output` [V-x https://github.com/nick-shaw/blackmagic-output/].
  **Conclusion [I]:** C# interop generated from the `Include` IDL or headers can live in PlayMap's repo with the
  header notice. Do not copy samples. The driver comes from the user's Desktop Video install.
- **FFmpeg**: `decklink` is in `EXTERNAL_LIBRARY_NONFREE_LIST` in FFmpeg's `configure` (with `libfdk_aac`,
  `libfdk_xhe_aac`, `libmpeghdec`) [V raw configure, master, 13 Oct 2026], so any FFmpeg with DeckLink is
  non-redistributable. BtbN variants: `gpl`, `lgpl`, `nonfree` ("Includes fdk-aac in addition to … the gpl
  variant"), plus shared variants [V https://raw.githubusercontent.com/BtbN/FFmpeg-Builds/master/README.md]. The
  local BtbN `n9.0.2-27-g7e93014c5b-20261012` build has **no decklink device**: `-devices` lists only `lavfi` and
  `v4l2` [V]. **BtbN builds do not include DeckLink. PlayMap must talk to the SDK directly**, as M13 plans.
- C# route [I]: on Windows, `[GeneratedComInterface]` interfaces (ComWrappers source generation, AOT-friendly)
  generated from `DeckLinkAPI.idl`, created via `CoCreateInstance(CLSID_CDeckLinkIterator)`. On Linux and macOS,
  `dlopen` the driver's `libDeckLinkAPI.so` / `DeckLinkAPI.framework` and call the versioned factory export (the
  same thing the SDK's `DeckLinkAPIDispatch.cpp` does), then use the same vtable interfaces through ComWrappers.
  The exact export names and whether ComWrappers works on Linux for this case are [U]. Capture with
  `IDeckLinkInput::EnableVideoInputWithAllocatorProvider` into pinned buffers that become `VideoFrameLease` planes
  (`UYVY` 8-bit, `v210` 10-bit, which needs a GPU unpack shader). Read timecode from `IDeckLinkTimecode` for
  timecode chase.

### 6.2 AJA

- `libajantv2`: "All code herein is licensed under the MIT license" [V
  https://raw.githubusercontent.com/aja-video/libajantv2/main/README.md, LICENSE "Copyright (c) 2023 AJA Video
  Systems"]. **NTV2 SDK 18.0 released 12 May 2026**; 17.6 hotfix 1 on 29 Jan 2026. The release note says Linux
  artifacts restore "missing **proprietary ajacc** static lib" [V
  https://github.com/aja-video/libajantv2/releases]. Avoid `ajacc` (closed captions).
- Route [I]: a small C++ shim built from libajantv2 (MIT) with a C ABI, P/Invoked. The driver comes from the
  user's AJA install. Low priority: DeckLink first, as ROADMAP M13 plans.

### 6.3 Everything else

USB and PCIe cards with OS-class drivers (UVC webcams, Cam Link, Magewell Pro Capture, and Blackmagic's
DirectShow filters on Windows) already work through PlayMap's `CaptureSource` (`dshow`, `v4l2`, `avfoundation`)
[V repository]. Whether Magewell exposes V4L2 or DirectShow on its current driver line is [U].

---

## 7. WebRTC WHIP and WHEP (phones and browser previews), briefly

- **WHIP = RFC 9725** (Proposed Standard, March 2025): one HTTP POST of an SDP offer and an answer, then ICE and
  DTLS [V-x https://datatracker.ietf.org/doc/rfc9725/]. **WHEP = draft-ietf-wish-whep-05**, published 11 Oct 2026,
  "Submitted to IESG for Publication", expires 14 Apr 2027 [V-x https://datatracker.ietf.org/doc/draft-ietf-wish-whep/].
- **FFmpeg**: the local FFmpeg 9.0.2 has a **`whip` muxer** (default video h264, audio opus; options
  `handshake_timeout`, `pkt_size` 1200, `authorization` Bearer, `cert_file`/`key_file`,
  `whip_flags dtls_active`). It has **no WHEP demuxer or server** [V `ffmpeg -h muxer=whip`, `-demuxers`]. So
  FFmpeg can **push** a PlayMap output to any WHIP server, but cannot accept a phone's WHIP or serve WHEP.
- .NET options and licences:
  - **SIPSorcery 10.0.17** (WebRTC in pure C#, targets net10.0): BSD-3-Clause **plus an "Additional Use
    Restriction"** that forbids use, modification or distribution inside Israel and the Occupied Territories, and
    says it overrides the BSD terms where they conflict. `SIPSorceryMedia.FFmpeg` is LGPL-2.1 [V
    https://raw.githubusercontent.com/sipsorcery-org/sipsorcery/master/LICENSE.md, 13 Oct 2026]. Shipping it
    would bind every PlayMap user to that field-of-use restriction. **Do not ship SIPSorcery** [I].
  - **MediaMTX** (Go, single binary; RTSP, SRT, WebRTC WHIP/WHEP): **MIT** [V
    https://raw.githubusercontent.com/bluenviron/mediamtx/main/LICENSE]. Suitable as an optional sidecar process.
    Its WHIP/WHEP support is from memory [U].
  - **libdatachannel** (C++ WebRTC): **MPL-2.0** [V raw LICENSE]. A P/Invoke shim is possible; MPL needs file-level
    source availability [I].
- Recommendation [I]:
  1. **Phones and glasses on the LAN: no WebRTC.** Use `getUserMedia`, then the WebCodecs `VideoEncoder` (H.264),
     then the existing WebSocket. Every chunk carries device time, intrinsics and pose (the envelope in docs/XR.md),
     and FFmpeg decodes it. This extends R1.9 (JPEG over WebSocket) with roughly 10× less bandwidth and exact
     per-frame timestamps, which scanning needs. WebCodecs support on iOS Safari is [U] and must be checked per
     device.
  2. **Browser previews:** H.264 over WebSocket into a WebCodecs `VideoDecoder`, or `<video>` with MSE as a
     fallback.
  3. **WHIP/WHEP for interop:** use FFmpeg's `whip` muxer to push to a MediaMTX sidecar the user enables. The
     sidecar serves WHEP to remote browsers and accepts WHIP from OBS or phones, and PlayMap pulls that back as RTSP
     or SRT through `CaptureSource`. No WebRTC stack inside PlayMap.

---

## 8. Protocol comparison for a projection-mapping node

| | Spout | OMT | NDI (standard SDK) | DeckLink SDI/HDMI | Browser (WebCodecs/WS) |
|---|---|---|---|---|---|
| Scope | same PC, same GPU | LAN, TCP unicast | LAN; TCP, UDP, multicast; WAN via Bridge | local card | LAN/Wi-Fi |
| Added latency | ~0 (a GPU copy) [I] | < 1 frame codec (design goal) [V] | not measured here [U] | card buffer only [U] | encoder-dependent, 1–3 frames [U] |
| Bandwidth (1080p60) | none | 86 / 200 / 260 Mb/s (Low / Medium / High) [V] | High Bandwidth ~ [U] | n/a | ~8–20 Mb/s H.264 [U] |
| Alpha | yes (BGRA, RGBA16F) | yes (UYVA, PA16, BGRA) [V] | yes (UYVA, PA16, BGRA) [V] | key/fill on some cards [U] | no (VideoEncoder) [U] |
| ≥10-bit | 10-bit, FP16, FP32 textures [V] | 16-bit planar P216/PA16 [V] | P216/PA16 receive; send SDR only [V] | v210 10-bit [U] | depends on device |
| HDR signalling | none (texture only) | none (601/709 only) [V] | receive yes, send Advanced-only [V] | HDMI/SDI metadata [U] | no |
| Discovery | shared-memory list [V] | DNS-SD `_omt._tcp` + server :6399 [V] | mDNS + Discovery Server [V] | device enumeration | the desk lists devices |
| Tally / metadata | per-sender `_map` buffer [V] | tally + XML both ways [V] | tally + XML both ways [V] | ancillary data [U] | WebSocket |
| Licence | BSD-2 | MIT | proprietary, royalty-free on PCs [V-x] | EULA; headers permissive [V-x] | n/a |
| Linux | — | yes | yes (avahi) | yes (driver) | yes |

---

## 9. Design implications for PlayMap

### 9.1 One input abstraction, one send abstraction

**Inputs stay `MediaItem { Kind = Stream, Source = <URI> }`**, which already flows through
`EngineSources.LiveFor` → `IMediaBackend.Open`. Add a scheme router in `PlayMap.Media` and a provider per
protocol [I]:

```csharp
public interface ILiveInputProvider            // one per protocol; lives in PlayMap.Video.<Proto>
{
    string Scheme { get; }                     // "spout" | "omt" | "ndi" | "decklink" | "syphon" | "ffmpeg"
    ProviderStatus Status { get; }             // runtime found? version, path, licence notice, last error
    IReadOnlyList<LiveEndpoint> Endpoints { get; }   // discovery snapshot, refreshed off-thread
    IVideoSource Open(LiveEndpoint endpoint, LiveOpenOptions options);  // never blocks; frames arrive later
}
public sealed record LiveEndpoint(string Uri, string Name, string Host, int Width, int Height,
    Rational Rate, bool Alpha, bool HighBitDepth, string? Colour, DateTimeOffset LastSeen);
```

URIs: `spout:Resolume Arena`, `omt:STUDIO-PC (Program)` or `omt://10.0.0.5:6401`, `ndi:CAM-1 (PTZ)`,
`decklink:<persistentId>/1080p50`, `syphon:<uuid>`, plus every FFmpeg URL (`rtsp://`, `srt://`, `dshow:…`,
`v4l2:/dev/video0`). Camera rigs (`StageModel.Cameras`) take the same URIs, so an **NDI PTZ, OMT camera or
DeckLink camera can be a scan camera**. That meets R3.3's "NDI PTZ cameras as scan cameras" and reuses the scan
runner's frame-ID tags and white-flash latency measurement [I].

**Sends are a new model entity** owned by the single writer (Core) [I]:

```csharp
public sealed record VideoSend(string Id, string Name, SendKind Kind,      // Spout | Omt | Ndi | Syphon | DeckLink | Preview
    SendTap Tap, string TapId,             // Output (post-warp raster) | OutputPreWarp | Canvas | Playlist | Multiview
    int Width, int Height, SendFormat Format,   // Bgra8 | Uyvy | Uyva | P216 | Pa16 | Rgba16F (Spout)
    SendQuality Quality, int RateDivider, bool Enabled) : IEntity;
```

- Taps (where in the pipeline the picture is taken) are the feature competitors expose least [I]:
  - **post-warp output raster**: feeds an LED processor, a second server or a recorder exactly what a projector
    gets;
  - **pre-warp canvas or playlist**: PlayMap acts as a content source for another mapper or for IMAG;
  - **multiview**: labelled tiles of every output with tally borders, safe area and an "input lost" overlay.
- Verbs in the catalogue's `NOUN VERB` style [I]: `INPUT LIST`, `INPUT DISCOVER`, `INPUT ADD`, `INPUT REMOVE`,
  `INPUT GET`, `INPUT FALLBACK`; `SEND LIST`, `SEND ADD`, `SEND SET`, `SEND ENABLE`, `SEND REMOVE`;
  `VIDEOIO STATUS` (runtimes and versions). Each verb has STATE keys `inputs.*`, `sends.*`, `videoio.*`, a
  `DeskAction`, and MCP exposure, following the UX.md rule that every button maps to a verb.

### 9.2 Frames: add packed formats and a GPU lease (shared with R2.2 and R2.3)

- Add `PixelLayout.Uyvy422`, `Uyva422` (UYVY plus an 8-bit alpha plane), `P216` (16-bit semi-planar 4:2:2) and
  `Pa16` (P216 plus a 16-bit alpha plane), with GPU unpack shaders. These are the native receive formats of OMT
  and NDI (§4.2, §5.2) and are missing today (§1). Add `v210` later for DeckLink [I].
- Add an optional GPU surface to `VideoFrameLease` [I]:
  `GpuSurface? Gpu { Api (D3D11|GL|Metal), nint Handle, DxgiFormat, SyncKind (None|NamedMutex|KeyedMutex|Fence), ulong SyncValue }`.
  The renderer imports when the API and adapter match and otherwise falls back to `Planes`. Spout receive, D3D11VA
  zero-copy (R2.2) and GPU HAP (R2.3) then share one import path, and STATE reports "zero-copy yes/no" per source,
  as R2.2 asks.
- Spout input leases point at **PlayMap's own copy** (§2.7), so the sender is never held up [I].

### 9.3 Sends never touch the render thread's budget

- D3D11: copy the tap into a 3-deep ring of staging textures and map it 2 frames later. **Spout has no readback**:
  it is a `CopyResource` into the shared texture with a 0 ms mutex try, skipping on contention [I].
- GL (Linux and the current Windows build): the existing `PixelPackRing`. **Pack to UYVY or P216 in a shader
  before readback.** At 1080p60, BGRA is 1920 × 1080 × 4 × 60 = 498 MB/s of PCIe and memcpy; UYVY halves that to
  249 MB/s [I arithmetic]. OMT and NDI both take UYVY natively.
- Encode and network on a per-send worker. `OMTSend.Send` encodes synchronously (§4.4), and NDI async needs a
  2-buffer ring (§5.2). Use a latest-wins queue of depth 1 and count `dropped` [I].
- Stamp every sent frame with **leader show time**: OMT `Timestamp` = leaderNs / 100; NDI `timestamp` and
  `timecode` from the same instant (units [U]). A downstream PlayMap, or any receiver, can then align by show time.
  Pace sends from the output's present clock (framelock-aware, R4.2), not from a timer [I].
- Budget numbers for preflight [V OMT table; I sums]: four 1080p60 OMT sends at Medium = 800 Mb/s, which does not
  fit 1 GbE with the show network's other traffic. 2160p60 High = 600 Mb/s per receiver (unicast). VMX encode
  ≈ 1 core per 2160p60 (AVX2) [V libvmx README].

### 9.4 Timing, determinism and backup nodes (where PlayMap can be better)

- Every live frame carries `ArrivalNs` (exists), the **sender timestamp** (OMT and NDI) or the **sender frame
  number** (Spout semaphore), and the measured offset to leader time. STATE shows per-input age, jitter (p95),
  dropped and duplicated frames, and the frame rate against the output refresh [I].
- **NDI FrameSync** (`NDIlib_framesync_capture_video` at each render frame) gives time-base-corrected pulls with
  no blocking in the standard SDK [V header]. Use it for NDI inputs drawn every frame. Build an equivalent
  2-frame jitter buffer for OMT [I].
- **Timestamp-locked live inputs (proposal, a potential first) [I]:** COMPETITIVE §5c says live inputs break the
  main/backup mirror. When main and backup receive the **same** OMT or NDI sender, each frame has the same sender
  timestamp on both nodes. Have the leader publish on the Show Bus one offset `senderTs → leaderNs + D` (D = 2–3
  frames), and have each node show the frame whose sender timestamp maps to its vblank. Both nodes then display the
  identical frame, and **network live inputs become mirror-deterministic**. Spout and capture cards still cannot
  (no shared timestamps, or a different card per node): they keep the "non-deterministic" badge and the "feed
  both nodes from one router" fix.
- "Capture to HAP" for live inputs (already in UX.md): record the incoming VMX or SpeedHQ to a file for rehearsal
  or for a deterministic show. OMT's `CompressedOnly` receive flag gives the VMX frames without decoding, so
  recording costs almost no CPU [V `OMTReceiveFlags`].

### 9.5 Show safety for live video

[I, building on the user's brief: Show Mode, preflight, health view, panic with defined outcomes]

- **Loss policy per input:** `hold` (default) / `fallback:<media or input>` / `black`, with thresholds
  `stale` (> 3 frame periods) and `lost` (> 1 s). Changes appear in the health view and as Companion feedback.
- **Redundant inputs:** a primary and a secondary URI. The secondary takes over automatically on `lost`, and the
  switch back is manual in Show Mode. On the send side, offer NDI `NDIlib_send_set_failover` [V header] and OMT
  `SetRedirect` [V] so downstream receivers follow PlayMap's backup node when the main node dies. That is the
  Watchout lesson ("keep playback running if the controller dies"), applied to video sends.
- **Preflight (`PREFLIGHT`, R2.6) checks for video IO:**
  - NDI runtime present and its version (warn when older than the newest known);
  - libvmx loaded, with its CPU features (AVX2 or SSE4.2);
  - Spout sender on the same adapter as the drawing output;
  - Desktop Video driver ≥ the SDK version built against;
  - mDNS works: on Linux, `avahi-daemon` is running;
  - firewall ports open: OMT 6400–6600 and 6399; NDI 5353/udp, 5959–5969, 6960–6970, 7960–7970;
  - NIC speed against the send bandwidth (§9.3), counting receivers for OMT;
  - every input delivering at the expected size and rate for 60 s.
- **Show Mode:** routing locks (an input can't be repointed or a send removed without unlocking). Blind edits of
  sends are allowed. **Panic** has defined outcomes: (1) inputs → their fallback; (2) sends keep running (black or
  frozen as chosen) so downstream screens don't fall back to "no source".
- **Health view** across machines: every node publishes `videoio.*` (inputs, sends, runtime, bandwidth, drops) to
  the Show Bus. Patterns' Eye can then draw a send as a link with its own light (SHOWBUS-NODE.md item 4: Patterns
  sends an NDI feed, and PlayMap maps it and returns its health).

### 9.6 Companion and control integration

[I; complements the Companion research]

- Sends and inputs as Companion variables and feedbacks: `input.<id>.state` (ok/stale/lost), `.fps`, `.age_ms`;
  `send.<id>.connections`, `.tally_program`/`.tally_preview` (OMT `OMTTally`, NDI `send_get_tally`).
- Actions: `INPUT FALLBACK`, `SEND ENABLE`, and a "route input X to surface Y" preset generated per discovered
  input (live auto-creation of buttons when a new OMT or NDI source appears).
- Tally back to cameras: when a live input is visible in any armed output, call `NDIlib_recv_set_tally` or OMT
  `SetTally(program: true)`, so camera operators and vMix see that PlayMap is using their feed.

### 9.7 Packaging and licences

[I, from §2–7]

| Component | Ships with PlayMap? | Obligations |
|---|---|---|
| Spout (native C#) | yes (own code) | credit the BSD-2 notice in THIRDPARTY-NOTICES |
| libomtnet source + libvmx binaries | yes | MIT notices; build libvmx per RID in CI |
| NDI | **no** binaries; MIT headers transcribed | user installs the runtime; ndi.video link next to every NDI picker; ® and "NDI® is a registered trademark of Vizrt NDI AB" in the About box and docs; no "NDI" in product names; plugin assembly `PlayMap.Video.Ndi` inert without a runtime |
| DeckLink interop | yes (generated from `Include` IDL) | keep the header notice; marks only as "PlayMap compatible with Blackmagic Design DeckLink"; never FFmpeg `--enable-decklink` |
| AJA shim | later | keep libajantv2's MIT notice; no `ajacc` |
| Syphon shim + framework | macOS engine only | BSD-3 notice |
| WebRTC | none in-process | FFmpeg `whip` muxer (already shipped); MediaMTX as an optional user-enabled sidecar (MIT); never SIPSorcery |

### 9.8 What can be tested on this Linux build machine and what is compile-only

| Area | On Linux CI (4 cores, llvmpipe, no GPU) | Compile-only, or needs other hardware |
|---|---|---|
| OMT | **Full**: build libvmx with clang (installed), loopback send→receive on 127.0.0.1, Discovery Server on :6399 (no avahi-daemon here), quality voting, tally and redirect, alpha (UYVA/PA16), 16-bit, preview mode, `CompressedOnly` recording; cross-check VMX bitstreams with FFmpeg's 8-bit `vmix` decoder; header byte-layout golden tests | Windows and macOS discovery back-ends |
| Spout | layout tests (280-byte struct, 256-byte name slots, sorted order, `name_1` renaming, stale-name cleanup) against an `ISharedMemory` fake; frame-count semaphore logic against a fake | Win32 maps and mutexes, D3D11 open/copy, WGL interop: a `windows-latest` CI job (WARP adapter; whether WARP allows shared KMT textures is [U]) or a lab PC |
| NDI | binding tests against a **fake `libndi.so.6`** (a ~200-line C file of PlayMap's own, compiled with clang in the test) that exports `NDIlib_*`, loops video and checks struct marshalling, async-send buffer lifetime and FrameSync calls | the real runtime (licence acceptance; not in CI) |
| DeckLink, AJA | capture state machine against fake COM objects (ComWrappers) or a fake C ABI | real cards |
| Syphon | — | macOS |
| GL send path | `PixelPackRing` + UYVY/P216 pack shader, pixel-exact against golden images on llvmpipe | D3D11 staging ring (Windows) |
| Browser ingest and previews | Playwright Chromium with `--use-fake-device-for-media-stream`: WebCodecs H.264 → WebSocket → FFmpeg decode, timestamps checked | iOS Safari specifics |
| WHIP | FFmpeg `whip` muxer against a MediaMTX sidecar, if its binary is supplied (egress is restricted here) | — |

### 9.9 Comparable or superior: the concrete list

**Parity** (what MadMapper, Resolume, Millumin, Pixera, Watchout and disguise users expect; competitor feature
lists per docs/COMPETITIVE.md, not re-verified):
1. Spout in and out (Windows), NDI in and out (receiving HX2/HX3 PTZ cameras), capture cards (DeckLink), Syphon on
   macOS later.
2. A source browser with live thumbnails. **OMT and VMX `Preview` (a 1/8-size layer) make thumbnails nearly free.**
3. Per-output and multiview sends.

**Superior** (each is achievable with the verified facts above) [I]:
1. **OMT-native mapper**: through the vendored, managed libomtnet (MIT), on Linux too. No competitor was found shipping OMT (§4.1). Supports alpha
   and 16-bit, tally and redirect.
2. **Pipeline taps**: send pre-warp, post-warp raster or multiview per output, in the same picker for every
   protocol.
3. **Timestamp-locked live inputs** across main and backup nodes, so network video stops breaking the mirror
   backup (§9.4).
4. **Failover sends** (NDI failover, OMT redirect) tied to PlayMap's redundancy, so downstream receivers survive a
   node loss.
5. **Live inputs as scan cameras** (NDI PTZ, OMT, DeckLink, phone browser) with automatic latency measurement:
   "point any camera on the network at the wall and calibrate".
6. **Preflight and health for video IO** (runtimes, adapter match, bandwidth per NIC, mDNS, ports) in the same
   view as projectors. Competitors show "no signal" only after the fact [U].
7. **Phone ingest with no app**: browser, WebCodecs and WebSocket, with intrinsics and pose per frame, all local.
   This supports the "phone-scan auto-mapping, fully local" category.
8. **HDR-aware inputs**: NDI P216 with `ndi_color_info` PQ/HLG → linear FP16 pipeline (R2.5) → tone-mapped for
   projectors; OMT 16-bit with the same metadata convention (§4.5).

### 9.10 Suggested work packages

[I; sizes as in ROADMAP]

| ID | Package | Size | Depends on |
|---|---|---|---|
| V1 | Live-input router + `ILiveInputProvider`, `VideoSend` entity, verbs, STATE, desk Inputs and Sends drawers; `PixelLayout` UYVY/UYVA/P216/PA16 + shaders; `GpuSurface` on leases | M | R1.8 |
| V2 | OMT in and out: vendored libomtnet, libvmx CI builds, Discovery Server option, tally, quality, preview thumbnails, `CompressedOnly` recording | M | V1 |
| V3 | Spout in and out: native C#; D3D11 path, plus GL via `NV_DX_interop2` until R2.1; adapter check | M | V1 (R2.1 for zero-copy out) |
| V4 | NDI plugin: port Patterns.Ndi; `recv_capture_v3`, FrameSync, async send ring, tally, failover, HDR receive; branding UI | M | V1 |
| V5 | Send taps and multiview; GL pack shaders; D3D11 staging ring; leader-time stamping | M | V1, R2.1 |
| V6 | Video IO preflight, loss policy, redundant inputs, Show Mode locks, health on the Show Bus, Companion variables, feedbacks and presets | M | V2–V4, R2.6, R3.5 |
| V7 | Timestamp-locked live inputs across nodes | M | V2/V4, R4.1 |
| V8 | Browser ingest (WebCodecs) and previews; optional MediaMTX sidecar for WHIP/WHEP | S–M | R1.9 |
| V9 | DeckLink (COM, timecode) | M | V1 (M13) |
| V10 | Syphon shim (macOS engine), AJA shim | M each | L6 / later |

---

## 10. Sources (all accessed 13 Oct 2026)

**Local clones**
- Spout2 `6b68a5b` (10 Oct 2026): https://github.com/leadedge/Spout2
- libomtnet `7d29031` (13 Oct 2026), libomt `bb4b67b` (24 Aug 2026), libvmx `a1828cb` (23 Sep 2026):
  https://github.com/openmediatransport

**Spout, OMT, NDI web pages**
- Spout releases: https://github.com/leadedge/Spout2/releases
- OMT org profile README: https://raw.githubusercontent.com/openmediatransport/.github/main/profile/README.md
- OMT releases: https://github.com/openmediatransport/libomtnet/releases
- vMix OMT quality KB (updated 3 Nov 2025): https://www.vmix.com/knowledgebase/article.aspx/380/video-quality-settings-for-omt
- vMix 29 blog (27 Oct 2025): https://blog.vmix.com/vmix-29-is-available-now/
- NDI headers (MIT): https://raw.githubusercontent.com/DistroAV/DistroAV/master/lib/ndi/
- NDI release notes: https://docs.ndi.video/all/getting-started/release-notes
- NDI licensing: https://docs.ndi.video/all/developing-with-ndi/sdk/licensing
- NDI dynamic loading: https://docs.ndi.video/all/developing-with-ndi/sdk/dynamic-loading-of-ndi-libraries
- NDI HDR: https://docs.ndi.video/all/developing-with-ndi/sdk/hdr
- NDI platform considerations: https://docs.ndi.video/all/developing-with-ndi/sdk/platform-considerations
- NDI recv: https://docs.ndi.video/all/developing-with-ndi/sdk/ndi-recv
- NDI for developers: https://ndi.video/for-developers/
- NDI License Agreement PDF: https://downloads.ndi.tv/SDK/NDI_SDK/NDI%20License%20Agreement.pdf
- NDI 6.3 press release: https://ndi.video/stories/press/ndi-6-3-core-tech-update-brings-advanced-control-and-visibility/
- DistroAV installation wiki: https://github.com/DistroAV/DistroAV/wiki/1.-Installation

**Capture cards and FFmpeg**
- Blackmagic SDK page: https://www.blackmagicdesign.com/developer/products/capture-and-playback/sdk-and-software
- DeckLink EULA: https://downloads.blackmagicdesign.com/EULA/DeckLink/DeckLink-End-User-License-Agreement.pdf
- Blackmagic developer FAQ: https://www.blackmagicdesign.com/developer/support/faq/desktop-video-developer-support-faqs
- libajantv2 README and releases: https://github.com/aja-video/libajantv2
- FFmpeg configure, vmixdec.c and riff.c (master): https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/
- BtbN README: https://raw.githubusercontent.com/BtbN/FFmpeg-Builds/master/README.md
- Local `/opt/ffmpeg9/bin/ffmpeg` n9.0.2-27-g7e93014c5b-20261012

**Syphon**
- Licence, README, releases: https://github.com/Syphon/Syphon-Framework
- Syphon.NET: https://github.com/Agash/Syphon.NET
- syphon-python: https://github.com/cansik/syphon-python
- syphon-rs: https://github.com/BlueJayLouche/syphon-rs

**WebRTC**
- RFC 9725: https://datatracker.ietf.org/doc/rfc9725/
- WHEP draft: https://datatracker.ietf.org/doc/draft-ietf-wish-whep/
- SIPSorcery licence: https://raw.githubusercontent.com/sipsorcery-org/sipsorcery/master/LICENSE.md
- MediaMTX licence: https://raw.githubusercontent.com/bluenviron/mediamtx/main/LICENSE
- libdatachannel licence: https://raw.githubusercontent.com/paullouisageneau/libdatachannel/master/LICENSE

**NuGet indexes** (`api.nuget.org/v3-flatcontainer/<id>/index.json` and nuspecs): libomtnet (none),
ndilibdotnetcorebase, spout.net, sipsorcery, vortice.direct3d11, silk.net.direct3d11, silk.net.dxgi,
silk.net.wgl.extensions.nv.

**Trade press and community** (search extracts): TM Broadcast (17 Nov 2025), Softvelum (14 Oct 2025), rsmck.co.uk
(12 Jul 2026), TouchDesigner forum (Aug 2025 – Jul 2026), LinkedIn E. Puurunen (21 Aug 2025).
