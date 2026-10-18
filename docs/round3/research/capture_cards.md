# Capture-card pass-through on Windows, for a D3D12 renderer with GL as the reference

Web research, current to October 2026. The repository was not touched.

Confidence marks:
- **[H]** I read the primary source (vendor documentation, licence text or specification).
- **[M]** A primary source covers part of the claim, or the claim rests on a reliable secondary source.
- **[L]** The claim rests on a forum post, an anecdote, or my own inference. Prove it on hardware before relying on it.

Numbers in brackets such as [S12] point to the source list at the end.

---

## 0. The answer in brief

- **Mapped pass-through has a floor of about one input frame plus render time.** A warp may read any input line to draw any
  output line, so the render cannot start until the whole input frame has arrived. Scan-out then takes one more frame on the
  output wire. Measured from the start of the source frame to the start of the projector's scan-out, the added delay is about
  1 frame plus the render time when input and output are genlocked. Unlocked, there is also a phase wait of 0 to 1 frame. The
  projector's own processing comes on top. Shipping media servers publish 2 to 4 frames (§2). A target of **2 frames or less at
  p95 unlocked, and about 1.5 frames genlocked**, is achievable and competitive. It is a target to measure, not a fact.
- **GPU-direct DMA (NVIDIA GPUDirect for Video, AMD DirectGMA) saves about 1 ms per 4K frame, not frames.** It is pro-GPU only,
  gated by NVIDIA, written for GL/D3D11/CUDA rather than D3D12, and its supported-GPU list stops at the RTX A-series [S10]. Defer
  it until a loopback measurement says the copy matters, for example in multi-input 8K work.
- **The D3D12 path that works for every vendor is "zero CPU copy".** The capture card DMAs into a host buffer that PlayMap owns.
  That same buffer is wrapped as a D3D12 heap with `OpenExistingHeapFromAddress`. One GPU pass (a copy on the copy queue, or a
  compute unpack that reads the buffer directly) moves the frame to VRAM. The fallback is one CPU memcpy into an upload heap, or
  into a GPU upload heap where ReBAR is on.
- **Nothing needs to be redistributed for the four main paths.** DeckLink is a COM API that the user's Desktop Video install
  provides. AJA's library is MIT. Magewell's runtime lives in System32 once Magewell's installer has run. Media Foundation is
  part of Windows. NDI and OMT cover the network.

---

## 1. Where the time goes

All figures are at 60 Hz, with one frame being 16.7 ms.

| Stage | Typical | Notes |
|---|---|---|
| Input wire time (the frame arrives line by line) | 1 frame | Unavoidable for a full-frame warp. Magewell's partial notification hands over lines early [S14], but a general mesh warp cannot use them. |
| Card buffering before the host sees the frame | 0 to 3+ frames | AJA AutoCirculate: "3+ frame latency" at the snapshot shown. The ping-pong method gives "three frame latency" capture→playout [S7]. Magewell low-latency mode: 64 lines [S14]. |
| DMA to host, then host to VRAM | about 0.1 to 1.5 ms | For example, 2160p60 v210 is about 22 MB per frame. [L] My arithmetic: about 1 ms over PCIe 4.0 x16, about 2 ms for a CPU memcpy at about 10 GB/s. |
| Unpack, warp, blend and composite | 1 to 5 ms | Depends on the GPU. Measured per output. |
| Phase wait for the output vblank | 0 to 1 frame (unlocked) | About 0 when genlocked with a tuned phase offset (§9). |
| Present queue | 0 to 2 frames | A waitable flip swap chain with a maximum frame latency of 1 removes queued frames [S27]. |
| Output wire time (scan-out) | 1 frame | The Holoscan tool counts "wire time … near the time for a single frame interval" [S20]. |
| Projector processing | about 0.5 to 3 frames | Off the PC. Measured once per model. |

**Beating one frame (beam racing, slices) is not worth it for mapping. [L]** Slice-based processing works only when output
line *n* depends only on input lines at or above *n*. Arbitrary meshes, rotation and blends break that rule. Beam racing also
conflicts with tear-free presentation.

---

## 2. Published and measured figures

| System and path | Figure | Source | Conf. |
|---|---|---|---|
| Magewell Pro Capture HDMI, 1080p60 RGB32, capture into the app (normal mode) | 32.04 ms total | [S14] | H (vendor test) |
| The same card in low-latency mode with partial notification | 17.68 ms total (16.7 of it is the frame arriving) | [S14] | H (vendor test) |
| Magewell Pro Capture HDMI 4K Plus, 4Kp60, with JPEG encode, normal / low-latency | 120.37 / 91.15 ms | [S14] | H (vendor test) |
| AJA ping-pong capture→process→playout through host memory | about 3 frames | [S7] | H |
| AJA AutoCirculate capture | "3+ frame latency" at the snapshot AJA shows; AJA says it is for reliability, not latency | [S7] | H |
| disguise VX 4+: GPU out → SDI VFC → own SDI capture → screen | 4 frames in the published example | [S21] | H |
| disguise latency modes | Output buffer of 2 / 1 / 0 frames (normal / low / ultra-low). Input delayed 1 frame by default (`smoothVideoInputFramesDelay`) | [S21] | H |
| disguise r27, Matrox SDI, ultra-low latency | Reduced from 4 to 3 frames at 30/50/60 Hz | [S22] | M |
| PIXERA, capture → output, by capture card | 2 to 3 frames (03/2024 chart) | [S23] | H |
| DeckLink loop-through with the card's own keyer | about 1 frame | [S5] | L (forum) |
| DeckLink Quad HDMI on Linux, user-measured | 4 frames at 60 | [S6] | L (forum) |
| TouchDesigner user: SDI in → GPU out | 4 to 5 frames; "disguise similar" | [S24] | L (forum) |
| vvvv DeckLink node: frame grabber → app, with GPUDirect | 0 to 1 frame | [S25] | L |
| NDI codec | 16 scan lines technical; "about one field" in most implementations; hardware within 8 lines | [S17] | M (vendor claim) |
| BirdDog Studio, SDI → NDI | 1 frame, as claimed by the vendor | [S21] | L |

**Reading the table:** shipping media servers sit at **2 to 4 frames** from capture to output. A PlayMap target of 2 frames or
less unlocked is therefore at or ahead of them. **[M]**

---

## 3. How glass-to-glass latency is measured

1. **Loopback with a frame counter or barcode** (disguise [S21], NVIDIA Holoscan `loopback-latency` [S20]). The node outputs a
   frame counter and a binary barcode, cables its own output back into its capture input, and composites the captured image
   beside the source. Image analysis reads the counter difference, so no human judgement is involved. disguise samples each
   latency mode at least 5 times per refresh rate, checks tearing, and adds 7 lines for the DP→SDI converter. Holoscan breaks
   the time into render, copy to host, write to hardware, vsync wait, wire, read from hardware and copy to GPU, then rounds up
   to the next vsync and adds the wire time. **[H]** This is the method PlayMap can automate with no outside equipment.
2. **Hardware timestamps mapped to the host clock.** DeckLink's `IDeckLinkVideoInputFrame::GetHardwareReferenceTimestamp` gives
   the arrival time. Only differences are meaningful [S3]. Magewell stamps frames in hardware when they are buffered [S15]. Map
   these to QPC and compare against DXGI present statistics (the flip time), which gives an in-node estimate between card
   arrival and flip. **[H]** for the APIs. The mapping method is **[L]**.
3. **Photodiode or high-speed camera** on the source monitor and on the projected image, read on a scope or by counting
   frames. This adds the projector's processing, which no in-node method can see. vvvv filmed a counter on two screens [S25].
   **[M]**

Present latency on the output side comes from the flip-model statistics. It must be reported separately from capture latency,
because the delay ring and the frames in flight are PlayMap's own choices.

---

## 4. Vendor SDKs

### 4.1 Blackmagic DeckLink (Desktop Video SDK)

- **Version.** The Desktop Video 16.0 SDK and manual were released on 8 April 2026. Desktop Video 16.4 is the current
  driver/software [S2]. Desktop Video supports Windows 10 or 11, 64-bit [S4]. I found no edition restriction, so Windows 11 Home
  should be fine. **[M]**
- **Licence.** The SDK EULA [S1] **[H]**:
  - It grants a royalty-free licence to use the SDK to create software compatible with Blackmagic products (cl. 1.1–1.2).
  - Clauses 1, 4.3, 4.4, 5, 7 and 8 **do not apply to the files in `/Win/Include`, `/Mac/Include` and `/Linux/Include`**
    (cl. 0.1). Those headers and IDL carry their own permissive notice, which must be kept in copies [S29]. **[M]**
  - Sample code may be used "only internally" (cl. 1.7). Do not copy the samples into PlayMap.
  - Name the product only as "PlayMap compatible with Blackmagic Design DeckLink" or "… for Blackmagic Design DeckLink"
    (cl. 6).
  - Blackmagic's stated policy is that its website is the only public source of the SDK [S1b].
  - An FFmpeg built with DeckLink support needs `--enable-nonfree` and must not ship [S1b]. **[M]**
- **How it loads.** On Windows the API is COM, registered by the user's Desktop Video install. The app calls
  `CoCreateInstance` and must check every `QueryInterface`, because a user's driver may be older than the SDK [S3]. **[H]**
  PlayMap therefore needs only interop declarations translated from the Include IDL, with Blackmagic's notice kept, and it
  redistributes nothing.
- **Low-latency features** [S3] **[H]**:
  - `bmdDeckLinkConfigCapturePassThroughMode`. *Direct* connects the monitoring output electrically to the input (zero
    latency, but no PlayMap processing). *CleanSwitch* plays the captured video back out, with "some latency".
  - `bmdDeckLinkConfigLowLatencyVideoOutput` "reduces output latency on some older products. On newer products, this option
    will have no effect."
  - A custom capture allocator: `IDeckLinkInput::EnableVideoInputWithAllocatorProvider` →
    `IDeckLinkVideoBufferAllocatorProvider::GetVideoBufferAllocator(bufferSize, width, height, rowBytes, pixelFormat)` →
    `IDeckLinkVideoBuffer`. Buffers are 16-byte aligned, with `StartAccess`/`EndAccess`. Added around Desktop Video 14.3 (the
    migration guide is dated November 2024). **This is the hook that lets the card DMA into a PlayMap-owned buffer.**
  - Genlock: a reference input with `bmdDeckLinkConfigReferenceInputTimingOffset` (±half a frame on wide-offset devices,
    otherwise ±511 pixels), and a genlock reference status enum (§3.39).
  - `bmdDeckLinkConfigClockTimingAdjustment` trims the output clock by ±127 ppm. That is a software lock for DeckLink *outputs*.
  - `bmdDeckLinkConfigCaptureGroup` and `PlaybackGroup` start and stop devices together.
- **GPU direct.** The manual says NVIDIA GPUDirect is supported on Windows and Linux x86_64 and "requires the use of the DVP
  library supplied by NVIDIA". AMD DirectGMA "requires the GL_AMD_pinned_memory GL extension". Both are shown only in the
  OpenGL sample LoopThroughWithOpenGLCompositing [S3]. **[H]** No D3D12 sample exists.

### 4.2 AJA NTV2 (libajantv2)

- **Licence.** "All code herein is licensed under the MIT license" (github.com/aja-video/libajantv2). Only the `release`
  branch is supported. Releases are tagged from v17.1 on [S8]. **[H]** Packagers list 18.0.0 in May 2026 [S8b]. **[M]**
  - The SDK and the Linux driver are open. The Windows and macOS drivers are closed but free, installed by the user [S8b].
    **[M]**
  - Plugin loading uses mbedtls and loads AJA-signed plugins only. `AJANTV2_DISABLE_PLUGIN_LOAD` removes both [S8]. **[H]**
- **How it loads.** NTV2 is a C++ class API, so it cannot be called from .NET with P/Invoke. Build a small C-ABI shim from the
  MIT sources and ship it with the MIT notice. The AJA driver comes from the user's AJA install. **[M]**
- **Latency** [S7] **[H]**:
  - AutoCirculate is "intended for … high reliability and that don't require low latency".
  - The low-latency model is a time-critical thread that calls `WaitForInputVerticalInterrupt` and then `DMAReadFrame`, or
    `DMAReadSegments` for single fields. AJA quotes "three frame latency" for ping-pong capture→playout.
  - DMA goes into **app-supplied host buffers** (`SetVideoBuffer`). That is the hook for a PlayMap-owned buffer.
- **GPU direct.** The repo has `demos/NVIDIA/dvplowlatencydemo` (OpenGL and the NVIDIA GPUDirect for Video SDK, Windows and
  Linux, built with a `.vcxproj`) and `cudalowlatencydemo` [S9]. RDMA straight to the GPU is a Linux kernel path
  (`driver/linux/ntv2rdma.c`). Holoscan documents it on Linux only, with the open NVIDIA kernel modules [S20b]. **[M]** I found
  no Windows RDMA.

### 4.3 Magewell (MWCapture SDK, Pro Capture)

- **Latency.** Every Pro Capture PCIe card has a **low-latency mode**: 64 lines to host memory. **Partial notification**
  (`MWCAP_NOTIFY_VIDEO_FRAME_BUFFERING`) lets software start on lines rather than whole frames. First-generation and USB Capture
  products do not support it [S14]. Enabling low-latency mode "will affect the performance of multiple streaming" (Magewell
  KB) [S14b]. **[H]** Frames carry hardware timestamps [S15].
- **Licence and redistribution.** The SDK is a free download (latest V3.3.1.1515, 17 July 2025) [S16]. The runtime
  `LibMWCapture.dll` installs into `Windows\System32` from "Magewell Capture Runtime". Products that include SDK parts must ship
  `MWCaptureRT.exe` as a silent install under `RedistLicense.rtf`. "You are not allowed to copy or move any files within
  Magewell's Run-time library to your to-be-released products" [S15b]. **[H]** for the 3.3 release note.
  - The 2025 samples now link LibMWCapture **statically** [S16]. PlayMap should not link statically, because that would embed
    Magewell code.
  - Instead, PlayMap loads `LibMWCapture.dll` from System32 at run time. If it is absent, the input says so in words and names
    the runtime to install.
- **Without the SDK.** The cards also expose generic DirectKS/DirectShow interfaces [S15], so Media Foundation sees them with no
  SDK at all, without low-latency partial frames. **[M]**

### 4.4 Deltacast, Datapath, Bluefish444

- **Deltacast VideoMaster.** It is aimed at OEMs and third-party developers. The IP virtual card needs runtime licences from
  Deltacast. It has historical GPUDirect support. GitHub holds an Unreal plugin, an FFmpeg fork and "overlay with minimal
  latency" samples [S18]. I found no public licence → **needs a vendor agreement**. **[L/M]**
- **Datapath Vision.** `RGBEasy.dll` ships with the Datapath driver. An OBS plugin reports hardware-accelerated capture about
  33% faster than DirectShow (user benchmark). Direct-to-GPU transfer applies only with Datapath's own graphics cards [S19]. I
  found no public SDK terms → **ask Datapath**. DirectShow/MF works without the SDK. **[L]**
- **Bluefish444.** The Windows 6.5.x driver's **AutoCapture** engine moves sub-frame processing into the driver: "ultra low
  latency capture" (August 2022). It supports GPUDirect and DirectGMA integrations, on Windows 10/11 [S19b]. SDK terms not found
  → **vendor agreement**. **[L/M]**

---

## 5. GPU-direct technologies

### 5.1 NVIDIA GPUDirect for Video (DVP)

- **Purpose.** It lets "IO board manufacturers write device drivers that efficiently transfer video frames in and out of NVIDIA
  GPU memory". Application developers get it **"through 3rd party partner SDKs"**, with "sub-frame transfer times in OpenGL,
  DirectX or CUDA on Windows or Linux" [S10]. **[H]**
- **Access.** The SDK is "free and available upon application and review" [S10]. AJA's demo README says a free developer account
  is enough [S9]. A 2024 forum thread reports that NVIDIA gates DVP to hardware makers, with no reply to individual requests
  [S11]. **[M]**
- **Supported GPUs** (NVIDIA page, as fetched): RTX A6000/A5500/A5000/A4500/A4000, Quadro RTX 8000–4000, GV100/GP100/P- and M-
  and K-series Quadro, A2/A10/A30/A16/A40/A100, Tesla T4/V100… [S10]. **[H]**
  - The page does **not** list RTX Ada or RTX PRO Blackwell. It looks stale; treat Ada and Blackwell support as unknown and
    probe for it.
  - GeForce is not supported [S11]. **[M]**
- **API shape.** `dvp.dll`, the DVP 1.63 header, `dvpInitGLContext`, CUDA formats and sync objects [S12]. **[M]** I found no
  D3D12 entry point, so D3D12 would need CUDA↔D3D12 interop (`cudaImportExternalMemory` with
  `cudaExternalMemoryHandleTypeD3D12Heap`/`Resource` [S28]) or D3D11On12. **[L]** for the absence of a D3D12 path.
  - `dvp.dll` reaches developers inside vendor SDK sample folders (DeckLink, AJA). I found no redistribution right → **needs an
    agreement with NVIDIA, the card vendor, or both**. **[M]**
- **What it gains.** About 1 ms per 4K frame (§1). A 2012 vvvv post calls the latency gain "marginal" [S25]. **[L]** It saves
  more on CPU and memory bandwidth with many 4K/8K inputs.

### 5.2 Other NVIDIA

- **R530 U1 (531.18, February 2023)** added "WDDM-based support (via Direct3D 12 and Mellanox Network Interface Card) for
  reading/writing to GPU framebuffer memory" [S13]. That is network RDMA, not capture cards. **[M]**
- **GPUDirect RDMA for AJA** is Linux (Holoscan) [S20b]. NVIDIA's AI Enterprise vGPU docs say "GPUDirect technology is not
  supported on Windows" (a vGPU context) [S13b]. **[M]**
- **RTX PRO Sync** (the successor to Quadro Sync II) syncs "displays or projectors to an external timing source".
  - Firmware 3.04 (March 2025) added RTX PRO Blackwell. Firmware 3.06 (July 2025) fixed framelock config on Windows 10/11.
  - Supported GPUs: RTX PRO 6000/5000/4500/4000 Blackwell, RTX 6000/5000/4500/4000 Ada and 4000 SFF Ada, the RTX A-series and
    Quadro RTX [S26]. **[H]**
  - This is the hardware frame lock for GPU outputs on NVIDIA pro cards.

### 5.3 AMD DirectGMA

- **What it is.** FirePro-era PCIe peer-to-peer into an aperture of GPU memory, for OpenGL, OpenCL and DX9–11, on workstation
  cards [S30]. **[M]**
- **Status.** Dell's 2022 guide still lists "AMD DirectGMA Technology" on the Radeon PRO W6000 range [S30b]. **[M]** I found no
  statement for the RDNA3 W7000 or later. A 2021 forum report says it broke on a W5500 after a driver update. **[L]**
- **DeckLink's AMD path** is `GL_AMD_pinned_memory` (OpenGL) [S3]. It is a GL-reference option only, with no D3D12 path. **[M]**
- **Frame lock.** AMD's FirePro S400 sync module is listed as no longer available. I found no current AMD framelock product.
  **[L]**

---

## 6. OS capture paths: Media Foundation, DirectShow, Frame Server

- **Media Foundation.** `MF_LOW_LATENCY` is set on the Source Reader when it is created (Windows 8+). It is meant for real-time
  communication [S31]. **[H]**
  - With a DXGI device manager, MF can hand over D3D11 textures (`IMFDXGIBuffer`). Chromium's MF capture copies the camera's NV12
    texture into a shared texture and waits on the D3D11 queue before handing it to D3D12 [S32]. **[H]** for Chromium.
  - Reaching D3D12 means an NT-handle shared texture and a shared fence: one GPU copy, no CPU copy. **[M]**
- **Latency** depends on the device. I found no authoritative MF-versus-DirectShow numbers. UVC HDMI dongles often compress
  (MJPEG), which adds decode time. **[L]**
- **libavdevice `dshow`** is CPU-only, with a CPU colour conversion.
- **Use MF as the universal fallback** for any card with a Windows driver (Magewell, Datapath, AVerMedia, UVC). It is free and
  part of the OS, on Windows 11 Home too.

---

## 7. Network sources, for comparison

| | Licence | Redistribution | Latency | Conf. |
|---|---|---|---|---|
| **NDI** (SDK) | Free, commercial use included. The headers may go in open-source projects under MIT, for dynamic loading | The binaries may ship if your EULA covers the NDI terms. The redistributable installer may be bundled (kept current), or link users to NDI's runtime installer and find the DLLs through `NDI_RUNTIME_DIR_V5` (or `_V6` in NDI 6). The app must link to ndi.video near every NDI selection. The Advanced SDK needs a vendor ID | Codec: 16 lines; typically about one field [S17] | H (licence), M (latency) |
| **OMT** (Open Media Transport) | MIT, royalty-free and patent-free | Free | Codec claims under one frame. VMX codec already in FFmpeg. TCP, DNS-SD. About 200 Mbps for 1080p60 HQ. In vMix 29 (27 October 2025) | H (licence), L (latency) [S18b] |

---

## 8. Getting frames into D3D12 with no copy, or one

These are rungs, best first. Each rung down is a fallback, reported in words and counted.

1. **Card DMA into a D3D12 heap over app-owned memory. Zero CPU copies.**
   - **The heap.** `ID3D12Device3::OpenExistingHeapFromAddress(pAddress)` "wraps the entire VirtualAlloc region". The heap is in
     system memory, the CPU can access it, and placed and reserved resources are allowed, "restrictions may still exist". It
     persists through device removal. Microsoft calls it "special-purpose diagnostic" [S33]. **[H]**
     `ID3D12Device13::OpenExistingHeapFromAddress1` adds a size [S33b]. **[M]**
   - **Precedent.** Dawn imports shared memory as WebGPU buffer memory on D3D12 with the sibling
     `OpenExistingHeapFromFileMapping` [S34]. **[H]**
   - **The flow.** PlayMap `VirtualAlloc`s a ring of slots. It gives each slot to the card:
     - DeckLink through its allocator provider [S3];
     - AJA as `SetVideoBuffer` host buffers [S7];
     - Magewell through "capture to virtual address" [S14b].

     The same region is opened as an `ID3D12Heap`, with placed `BUFFER` resources over the slots. At arrival, the capture thread
     submits a copy-queue `CopyTextureRegion` (buffer footprint → VRAM texture) **or** a compute dispatch that reads the raw
     buffer and unpacks UYVY/v210 straight into an RGBA/RGB10 texture. Either signals a fence value kept in the slot. The direct
     queue `Wait`s on the value on the GPU.
   - **Unproven:** **[L]** that a card's kernel driver can lock and DMA into pages that D3D12 has also wrapped, with no conflict;
     the PCIe read speed of cached system memory; and that WARP accepts the heap. Prove all three on WARP (CI) and on one real
     card before relying on this rung.
2. **One CPU copy into a GPU upload heap or an upload heap.**
   - `D3D12_HEAP_TYPE_GPU_UPLOAD` is VRAM the CPU can write (write-combined). It needs ReBAR, Agility SDK 1.613+ and
     `D3D12_FEATURE_DATA_D3D12_OPTIONS16::GPUUploadHeapSupported` [S35]. The CPU writes straight into VRAM, with no GPU copy.
   - Otherwise use an `UPLOAD` heap (L0, write-combined) and the copy queue. **[H]**
   - Copy with streaming stores; never read write-combined memory back. **[H]**
3. **An MF D3D11 texture → NT-handle shared texture + shared fence → D3D12.** One GPU copy, the same shape as the planned Spout
   bridge. **[M]**
4. **CUDA↔D3D12**, only when a vendor SDK hands over CUDA memory (DVP via CUDA, NVDEC). Use `cudaImportExternalMemory`
   (D3D12 heap/resource handles) and an external semaphore on a D3D12 fence [S28]. The CUDA runtime is NVIDIA-only and adds
   load-time weight. **[H]** for the API.

**GL reference.** Persistent-mapped PBOs (`ARB_buffer_storage`) with one memcpy, then `glTexSubImage2D` from the PBO. On AMD,
`GL_AMD_pinned_memory` gives zero copy (DeckLink's DirectGMA path [S3]). DVP is GL-native on NVIDIA pro cards [S12]. **[M]**

**Pixel formats.** Keep packed 4:2:2 (UYVY, v210) packed into the GPU and unpack it in a shader. A CPU `sws_scale` on the
capture thread costs milliseconds and halves 4:2:2 chroma when it goes to 4:2:0. **[H]** for the formats. The cost is **[M]**.

---

## 9. Frame-rate matching and frame lock

- **Hardware lock, where present.**
  - House sync (black burst or tri-level) feeds three things: the source (camera or switcher genlock), the GPU through RTX PRO
    Sync's BNC [S26], and DeckLink's reference input when DeckLink outputs are used [S3].
  - Input and output then share one clock. Set the phase so the input frame completes just before render start (DeckLink
    `ReferenceInputTimingOffset`). The PlayMap side picks a fixed delay in lines or microseconds, measured by loopback.
  - Report the reference status as a fact from the card (DeckLink's genlock status enum) and from the sync board. Never assume
    it. **[H]**
- **Software lock, the fallback.**
  - Pick the newest capture frame that has completed by the sample instant, on the node's monotonic clock.
  - Stamp its age at draw. Count repeats and drops.
  - 59.94 in and 60.000 out repeat a frame about every 16.7 s (1/0.06 s). 50 in and 60 out judder on every fifth output frame.
  - Offer the output refresh at the input's exact rational, and raise a preflight row when the two differ.
  - DeckLink *outputs* can be trimmed by ±127 ppm (`ClockTimingAdjustment`) to follow a source [S3]. **[H]**
  - GPU outputs cannot be trimmed this way; VRR can present at arrival on one standalone output (a measured option, outside
    lock groups). **[M]**
- **Interlaced inputs** cost a field or a frame to deinterlace. Prefer progressive or PsF. DeckLink has
  `Capture1080pAsPsF`/`Output1080pAsPsF` [S3]. **[H]**

---

## 10. Free to load at run time, or needs an agreement?

| Path | Free to use | Redistribute anything? | Loaded how | Needs an agreement? | Conf. |
|---|---|---|---|---|---|
| Blackmagic DeckLink | Yes (EULA) | No. The user installs Desktop Video. PlayMap ships interop translated from the Include IDL, which carries a permissive notice; the notice is kept | COM `CoCreateInstance` | No. Name only as "compatible with" | H |
| AJA NTV2 | Yes (MIT) | A shim built from MIT sources may ship with the MIT notice. The driver is user-installed | Native C-ABI shim | No | H |
| Magewell MWCapture | Yes (free SDK) | No, if PlayMap only loads `LibMWCapture.dll` from System32. Bundling `MWCaptureRT.exe` follows `RedistLicense.rtf`. Never copy runtime files, never link statically | `LoadLibrary` (System32) | No, for run-time loading | M/H |
| Media Foundation / DirectShow | Yes (OS) | No | OS | No | H |
| NDI | Yes | Optional, under the NDI EULA terms; or the user's NDI runtime | Run-time DLL load | No (Standard SDK). The Advanced SDK needs a vendor ID | H |
| OMT | Yes (MIT) | Allowed (MIT) | Library | No | H |
| Deltacast VideoMaster | Unknown | Unknown | SDK | **Yes, ask Deltacast** | L |
| Datapath RGBEasy | DLL ships with the driver | Unknown | DLL | **Ask Datapath**. MF works without it | L |
| Bluefish444 | Unknown | Unknown | SDK | **Ask Bluefish444** | L |
| NVIDIA GPUDirect for Video (DVP) | SDK "free … upon application and review" | `dvp.dll` terms not found | DLL from a vendor SDK | **Yes, NVIDIA and/or the card vendor**. Pro GPUs only | M |
| AMD DirectGMA / `GL_AMD_pinned_memory` | Driver feature | No | GL extension | No, but legacy and GL-only | M/L |
| CUDA runtime | Yes (CUDA EULA) | Redistributable runtime parts only | DLL | No, but avoid unless needed | M |

---

## 11. Recommendation for PlayMap

1. **One seam, `ICaptureProvider`, in the video-I/O hub**, alongside NDI, OMT and Spout. It gives loss policy, backup, tally,
   health and preflight for free.
   - A `capture:` scheme (and `decklink:`, `aja:`, `magewell:`, `mf:`) that **fails closed**. It is never read as an FFmpeg URL.
   - Discovery lists devices and modes as facts.
   - Each provider declares its rung (§8) and its measured arrival→flip time in STATE.
2. **Build order** (most owners first, then cheapest to write):
   1. **Media Foundation** (universal, free, every edition including Windows 11 Home). Rung 3, `MF_LOW_LATENCY`.
   2. **DeckLink** (COM, nothing redistributed). The allocator provider gives rung 1, with rung 2 as the fallback.
   3. **AJA** (MIT shim). The low-latency VBI-driven DMA thread with app buffers: rungs 1 and 2.
   4. **Magewell native** (run-time System32 load). Low-latency mode plus partial notification. Whole-frame use only, since
      mapping needs the full frame.

   NDI and OMT already exist. Deltacast, Datapath and Bluefish444 wait until an owner asks and the vendor agrees.
3. **Defer GPUDirect for Video and DirectGMA.** They save about 1 ms per 4K frame; they are pro-GPU only, gated and not D3D12.
   Revisit only if a loopback measurement shows the copy is the bottleneck. Measured, then governed.
4. **Output side for a pass-through output.**
   - One frame in flight.
   - A waitable flip swap chain with a maximum frame latency of 1.
   - Render late: start at vblank − (measured p99 render + margin), sampling the newest capture slot whose copy fence has been
     *submitted*, with a GPU-side wait.
   - A delay ring of 0.
   - Either keep other outputs out of the latency plan, or say plainly that mixing outputs adds a frame.
5. **Frame lock.** Hardware where present (RTX PRO Sync + house sync + the card's reference). Software otherwise (newest-complete
   selection, counted repeats and drops, a preflight row for rate mismatch, and DeckLink ±ppm trim for DeckLink outputs).
6. **Measurement built in.**
   - A loopback verb: frame counter and barcode out, through the card, back in. It publishes frames and ms at p50/p95/p99 with
     the method named.
   - Hardware-timestamp→QPC estimates on every live input.
   - Until hardware exists (the owner's answer 5), STATE says "unmeasured", not a number. CI uses a fake capture provider on an
     injected clock: it asserts the rung taken, the CPU copies counted (0 or 1), no CPU waits, and exact stamps.
7. **Tests that need no card:** WARP proves that `OpenExistingHeapFromAddress` plus a placed buffer and a copy or compute
   unpack produce byte-exact output against the CPU reference for UYVY and v210. The fake provider proves frame selection,
   repeats and drops at 59.94→60 and 50→60.

**Confidence overall:**
- High: the licence and loading conclusions for DeckLink, AJA, MF, NDI and OMT; Magewell's redistribution rules; the latency
  floor reasoning.
- Medium: the published competitor figures (vendor-reported) and Windows 11 Home compatibility (no edition limit found).
- Low until hardware: rung 1 (a wrapped heap as a DMA target); DVP on Ada/Blackwell; DirectGMA on RDNA3+.

---

## 12. What to verify on hardware (lab rows)

1. A DeckLink custom allocator into a `VirtualAlloc` ring wrapped by `OpenExistingHeapFromAddress`: no errors, no tearing, and
   the copy time measured. Repeat with AJA `DMAReadFrame` into the same ring.
2. GPU read bandwidth from that heap (copy queue vs compute unpack) for 1080p60 and 2160p60, v210 and UYVY.
3. Loopback latency per rung, per card, with and without genlock: the target is ≤2 frames p95 unlocked, about 1.5 locked.
4. Whether RTX PRO Sync with house sync, plus the DeckLink reference, reports locked in both places. Whether the phase offset
   holds over an hour.
5. MF latency for one PCIe card (Magewell) and one USB dongle, compared with the native SDK path.
6. The Windows 11 Home install of Desktop Video, AJA Desktop Software and the Magewell driver: any edition-specific failure.

---

## Sources

- [S1] Blackmagic Design, *End User License Agreement for the Software Development Kit* — https://www.blackmagicdesign.com/support/modal/download-with-terms-start/bmd-decklink-sdk
- [S1b] FFmpeg-devel, "configure: Fix decklink license dependency" (2019; quotes Blackmagic's reply on distribution) — https://lists.ffmpeg.org/pipermail/ffmpeg-devel/2019-January/239452.html
- [S2] Blackmagic Design developer page, Desktop Video 16.0 SDK (8 April 2026), Desktop Video 16.4 — https://www.blackmagicdesign.com/developer/products/capture-and-playback/sdk-and-software
- [S3] DeckLink SDK manual, online: section 1 (GPUDirect, DirectGMA, COM, interface stability) — https://sdk-doc.blackmagicdesign.com/decklink-sdk/section1.html ; Configuration IDs — https://sdk-doc.blackmagicdesign.com/decklink-sdk/DeckLinkAPI/enums/bmddecklinkconfigurationid.html ; Capture pass-through modes — https://sdk-doc.blackmagicdesign.com/decklink-sdk/DeckLinkAPI/enums/bmddecklinkcapturepassthroughmode.html ; IDeckLinkVideoBufferAllocatorProvider — https://sdk-doc.blackmagicdesign.com/decklink-sdk/DeckLinkAPI/interfaces/idecklinkvideobufferallocatorprovider.html ; IDeckLinkVideoBuffer — https://sdk-doc.blackmagicdesign.com/decklink-sdk/DeckLinkAPI/interfaces/idecklinkvideobuffer.html ; IDeckLinkVideoInputFrame (hardware timestamp) — https://sdk-doc.blackmagicdesign.com/decklink-sdk/DeckLinkAPI/interfaces/idecklinkvideoinputframe.html ; Migration guide (14.3) — https://documents.blackmagicdesign.com/UserManuals/DeckLinkSDKMigrationGuide.pdf
- [S4] Blackmagic, *About Desktop Video* readme (Windows 10/11 64-bit) — https://www.blackmagicdesign.com/support/content/readme/1fc57acbb3f44c05bea7b8cbdf79f6aa
- [S5] Blackmagic forum, "LoopThroughWithOpenGlCompositing playout latency" — https://forum.blackmagicdesign.com/viewtopic.php?p=306457
- [S6] Blackmagic forum, "Decklink quad HDMI latency" — https://forum.blackmagicdesign.com/viewtopic.php?p=728225
- [S7] AJA NTV2 SDK 17.5, *Capture and Playout Techniques* (AutoCirculate, ping-pong, low-latency model) — https://sdkdocs.aja.com/public/ntv2/17_5_0/d9/d9a/recordplaytechniques.html
- [S8] aja-video/libajantv2 README (MIT, release branch, CMake options) — https://github.com/aja-video/libajantv2
- [S8b] Package listings and the GStreamer discourse on AJA's open/closed drivers — https://unitedrpms.sourceforge.io/x86_64/repoview/libajantv2.html ; https://discourse.gstreamer.org/t/aja-capture-playout-plugin-move-it-upstream-or-not/75
- [S9] libajantv2 `demos/NVIDIA/dvplowlatencydemo/README.md` — https://raw.githubusercontent.com/aja-video/libajantv2/main/demos/NVIDIA/dvplowlatencydemo/README.md
- [S10] NVIDIA, *GPUDirect for Video* (purpose, partner SDKs, supported GPUs, access) — https://developer.nvidia.com/gpudirectforvideo ; program page — https://developer.nvidia.com/designworks/gpudirect-for-video-program
- [S11] NVIDIA developer forum, DVP access and `dvpInitCUDAContext` (2024) — https://forums.developer.nvidia.com/t/nvidia-gpu-direct-dvp-library-dvpinitcudacontext-return-dvp-status-unsupported-or-dvp-status-invalid-device/298670 ; "DVP and GPU Direct" — https://forums.developer.nvidia.com/t/dvp-and-gpu-direct/45722
- [S12] Blender's vendored `dvpapi.h` (DVP 1.63, GL context, `dvp.dll`) — https://projects.blender.org/archive/blender-archive/raw/commit/c50ccc847613bbe02bea64cee3f744b449da170c/intern/gpudirect/dvpapi.h
- [S13] NVIDIA R530 U1 (531.18) release notes, WDDM GPUDirect via D3D12 + Mellanox — https://www.nvidia.com/download/driverResults.aspx/199989/en-us/1000/
- [S13b] NVIDIA AI Enterprise, GPUDirect RDMA and Storage ("not supported on Windows") — https://docs.nvidia.com/ai-enterprise/release-7/latest/infra-software/vgpu/features/gpudirect.html
- [S14] Magewell, *Low Latency Mode and Partial Notification Feature of Pro Capture Cards* (measured tables) — https://magewell.com/developer/26/detail
- [S14b] Magewell, *How to achieve video capture with low latency* (call sequence) — https://magewell.com/developer/14/detail ; Magewell KB — https://www.magewell.com/kb/detail/001020014
- [S15] Magewell, *MWCapture SDK* (DirectKS/V4L2, hardware timestamps, 64 lines) — https://magewell.com/sdk
- [S15b] Magewell SDK 3.3 release note (runtime in System32, redistribution) — https://www.magewell.com/files/ReleaseNotes.pdf ; upgrade note — https://www.magewell.com/developer/20/detail
- [S16] Magewell Download Center (SDK V3.3.1.1515, 17 July 2025; samples linked statically) — https://magewell.com/downloads
- [S17] NDI docs: Software Distribution — https://docs.ndi.video/docs/sdk/software-distribution ; Licensing — https://docs.ndi.video/docs/sdk/licensing ; white paper, Encoding and Decoding (16 lines) — https://docs.ndi.video/docs/white-paper/encoding-and-decoding
- [S18] DELTACAST on GitHub — https://github.com/deltacasttv ; WATCHOUT Deltacast guide — https://docs.dataton.com/guide/watchout/devices/deltacast-videomaster.html
- [S18b] Open Media Transport — https://gitea.com/omt/.profile ; vMix 29 OMT — https://www.streamingvalley.nl/vmix-29-open-media-transport-omt/
- [S19] OBS forum, Datapath Vision Capture plugin (RGBEasy) — https://obsproject.com/forum/threads/datapath-vision-capture.12503 ; B&H VisionRGB-E2S listing — https://www.bhphotovideo.com/c/product/1152003-REG/datapath_visionrgb_e2s_dvi_rgb_hd_video_capture.html
- [S19b] Bluefish444 Windows 6.5.0/6.5.1 driver and SDK (AutoCapture) — https://www.bluefish444.com/media-centre/latest-news/120-bluefish444-release-microsoft-windows-6-5-0-driver-sdk-package-for-epoch-and-kronos-video-io-hardware.html ; https://www.bluefish444.com/media-centre/latest-news/121-bluefish444-release-microsoft-windows-6-5-1-driver-sdk-package-for-epoch-and-kronos-video-io-hardware.html
- [S20] NVIDIA Holoscan, *Video Pipeline Latency Tool* (loopback method, stage breakdown) — https://docs.nvidia.com/holoscan/archive/2.4.0/latency_tool.html
- [S20b] NVIDIA Holoscan, AJA setup (RDMA on Linux) — https://docs.nvidia.com/holoscan/sdk-user-guide/setup/third-party-hardware-setup/aja-setup
- [S21] disguise, *Capture Latency Methodology* (updated 6 October 2026) — https://help.disguise.one/hardware/performance/capture-latency-methodology ; PDF — https://azure-prod.disguise.one/media/3643/disguise-capture-latency.pdf
- [S22] disguise r27 release notes — https://azure-download.disguise.one/media/9606/r270-release-notes.pdf
- [S23] AV Stumpfl, *PIXERA Capture Cards Latency* (03/2024) — https://avstumpfl.com/fileadmin/user_upload/downloads/en/pixera/Performance_Charts/PIXERA%20two%20Liveinput%20Delay.pdf
- [S24] Derivative forum, "Video Device In latency" — https://forum.derivative.ca/t/video-device-in-latency/295560
- [S25] vvvv DeckLink nodes (GPUDirect 0–1 frame; PC→PC 2–3 frames) — https://legacy.vvvv.org/contribution/vvvv.nodes.decklink ; https://betadocs.vvvv.org/contributions/plugins/vvvv-nodes-decklink/index.html
- [S26] NVIDIA RTX PRO Sync firmware 3.04 and 3.06 (supported GPUs, Windows 10/11) — https://www.nvidia.com/en-us/drivers/firmware/3_04/nv-quadro-sync-windows-update-3-04-driver/ ; https://www.nvidia.com/en-us/drivers/firmware/3_06/nv-quadro-sync-windows-update-3-06-driver/
- [S27] Microsoft Learn, `IDXGISwapChain2::SetMaximumFrameLatency` / `GetFrameLatencyWaitableObject` — https://learn.microsoft.com/en-us/windows/desktop/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-setmaximumframelatency ; https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-getframelatencywaitableobject
- [S28] CUDA Runtime API, External Resource Interoperability (D3D12 heap/resource handle types) — https://docs.nvidia.com/cuda/archive/13.0.3/cuda-runtime-api/group__CUDART__EXTRES__INTEROP.html
- [S29] DeckLink header licence notice, as vendored by Qt — https://doc.qt.io/qt-5/qtwebengine-3rdparty-blackmagic-decklink-sdk-mac.html
- [S30] AMD DirectGMA overview (FirePro, APIs, OS) — https://www.schneider-digital.com/wp-content/downloadcenter/Treiber/Grafikkarten/AMD/BIOS/DirectGMA/FirePRO_DirectGMA_Datasheet.pdf ; https://cdrinfo.com/D7/content/amd-introduces-directgma-amds-firepro-gpus
- [S30b] Dell, *Guide to professional workstation graphics cards* (Radeon PRO W6000, DirectGMA row, 2022) — https://i.dell.com/sites/csdocuments/Premier_Docs/en/us/22q4_workstation_guide_radeon_pro_dell_precision.pdf
- [S31] Microsoft Learn, `MF_LOW_LATENCY` — https://learn.microsoft.com/en-us/windows/win32/medfound/mf-low-latency ; Source Reader attributes — https://learn.microsoft.com/en-ca/windows/win32/medfound/source-reader-attributes
- [S32] Chromium, `video_capture_device_mf_win.cc` — https://chromium.googlesource.com/chromium/src/media/+/refs/heads/main/capture/video/win/video_capture_device_mf_win.cc
- [S33] Microsoft Learn, `ID3D12Device3::OpenExistingHeapFromAddress` — https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device3-openexistingheapfromaddress
- [S33b] windows-rs, `ID3D12Device13::OpenExistingHeapFromAddress1` — https://microsoft.github.io/windows-docs-rs/doc/windows/Win32/Graphics/Direct3D12/struct.ID3D12Device13.html
- [S34] Dawn, "D3D12: Support shared memory file mapping handle as shared buffer memory" — https://dawn.googlesource.com/dawn/+/refs/heads/chromium/7476
- [S35] GPUOpen, *Effective Use of the New D3D12_HEAP_TYPE_GPU_UPLOAD* — https://gpuopen.com/learn/using-d3d12-heap-type-gpu-upload/ ; DirectX-Specs, GPU upload heaps — https://github.com/microsoft/DirectX-Specs/blob/master/d3d/D3D12GPUUploadHeaps.md
