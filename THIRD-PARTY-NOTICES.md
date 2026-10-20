# Third-party notices

What PlayMap ships or loads from others is listed here, each under the package that brought it, with its licence and the notice its
licence asks for. NDI's runtime loads from the user's own install and NVAPI from the user's NVIDIA driver, and neither is ever
shipped. Round 3 will load NVIDIA's Aftermath library from wherever the user put it, AMD's runtimes (ADLX among them) from the
driver, and the capture cards' runtimes (DeckLink's, and AJA's and Magewell's if their stretch lands) from their drivers' installs,
and will ship none of them. Today no FFmpeg is shipped: PlayMap loads the shared libraries the user puts in place. Round 3's
installer will carry the LGPL build's replaceable shared libraries and the portable folder none, and no other FFmpeg build is ever
shipped (`docs/round3/PLAN.md` §9 decision 4). ANGLE's library comes beside the exe in the desk's Avalonia package (wp2-d3d11,
below). Vendored source lives under `src/ThirdParty`, outside the analyzers' fences, with its licence beside it.

## Round 1

- Inter (SIL Open Font License 1.1), embedded in PlayMap.Render for the test patterns' type: `src/PlayMap.Render/Assets/Inter-LICENSE-OFL.txt`.

## Round 2

### wp2-show

### wp2-blind

### wp2-output

- NVIDIA NVAPI (`nvapi.h`, `nvapi_interface.h`; MIT, © 2019–2026 NVIDIA Corporation & affiliates): the sync-board structure
  layouts, their version words and the interface ids in `src/PlayMap.Render/Sync/NvApiLayout.cs` follow the header. No header or
  library is shipped: `nvapi64.dll` loads at run time from the user's NVIDIA driver.

### wp2-d3d11

- ANGLE (BSD-3-Clause, © 2018 The ANGLE Project Authors): `av_libglesv2.dll`, the file the desk's Avalonia.Angle.Windows.Natives
  package (2.1.27548.20260419) puts beside the exe, is loaded at run time by the DXGI present path; PlayMap adds no ANGLE binary of
  its own. Its licence ships in that package (`LICENSE`); the notice it asks for: "Copyright 2018 The ANGLE Project Authors. All
  rights reserved." with the BSD-3-Clause conditions and disclaimer. The render tests reference the same package so the loader
  finds the file beside the test assembly.
- The Direct3D 11, DXGI, user32 and display-configuration structure layouts and COM vtable slots in
  `src/PlayMap.Render/Gl/Windows` and `src/PlayMap.Media/Video/Spout` follow the Windows SDK's published declarations; the
  test goldens were taken from the mingw-w64 headers (public domain / ZPL; read at test time where installed, nothing copied).
- Spout 2 (BSD-2-Clause, © Lynn Jarvis and the Spout contributors): PlayMap's Spout code speaks its protocol (the shared-memory
  names and layouts, the frame counter and access lock order) written from the published protocol; no Spout source or binary is
  shipped or linked.

### wp2-decode

Nothing shipped: the HAP second stage is PlayMap's own code. Test-only packages (never in the exe):

- Snappier 1.3.1 (BSD-3-Clause), the reference Snappy codec the second stage is checked and timed against —
  https://github.com/brantburnett/Snappier.
- BCnEncoder.NET 2.3.0 (MIT OR Unlicense), writes the BC1/BC3/BC4/BC7/BC6H blocks the HAP tests upload and decodes them on the CPU
  for comparison — https://github.com/Nominom/BCnEncoder.NET.

### wp2-zerocopy

### wp2-optimise

Nothing shipped: the HAP encoder's programs, the packer and the Snappy compressor are PlayMap's own code, written from the HAP
specification (Vidvox, https://github.com/Vidvox/hap) and Google's published Snappy format description. FFmpeg is used only through
its shared libraries at run time (demux, decode, the mov muxer); none of its source is copied. The tests compare against FFmpeg's
own `hap` encoder through the `ffmpeg` command-line tool and make their clips with LGPL-compatible encoders (rawvideo, mpeg4, aac);
nothing they make is committed.

### wp2-videoio

- **libomtnet** (Open Media Transport), 1.0.0.20 at upstream `7d29031` — MIT License, Copyright (c) 2025 Open Media Transport
  Contributors. Vendored as source in `src/ThirdParty/libomtnet` (licence in `LICENSE.txt` beside it; PlayMap's patches in
  `PATCHES.md`), built as `PlayMap.ThirdParty.Omt.dll`.
- **libvmx** (OMT's VMX codec) at upstream `a1828cb` — MIT License, Copyright (c) 2025 Open Media Transport Contributors; its
  `sse2neon.h` is MIT, Copyright (c) 2015-2024 SSE2NEON Contributors. Vendored as source in `src/ThirdParty/libvmx` (licence in
  `LICENSE.txt` beside it, sse2neon's in its header); a native library (`libvmx.so`, `libvmx.dylib`, `libvmx.dll`) built per
  platform and loaded at run time.
- **NDI® SDK headers**, 6.3 — PlayMap transcribes the layouts it needs from NDI's header files, which carry this notice:
  "Copyright (C) 2023-2026 Vizrt NDI AB. All rights reserved. Permission is hereby granted, free of charge, to any person
  obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without
  restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and / or
  sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following
  conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions
  of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
  LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
  OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE." The MIT
  notice applies to the header files only, not to the NDI SDK or runtime.
- **The NDI runtime** is not shipped with PlayMap and no NDI binary is in its output or this repository: PlayMap loads the
  runtime the user installed (from https://ndi.video) at run time. The tests use a fake runtime compiled from PlayMap's own
  source (`tests/native/fake_ndi.c`) at test time.
- NDI® is a registered trademark of Vizrt NDI AB.

### wp2-control

Nothing shipped: OSC, MIDI, Art-Net, LTC and MTC are PlayMap's own code, written from the published specifications.

- Art-Net™ Designed by and Copyright Artistic Licence Holdings Ltd.
- The ALSA sequencer library (`libasound.so.2`, LGPL-2.1-or-later) is loaded at run time from the user's Linux system and linked
  dynamically; it is never shipped. WinMM (`winmm.dll`) is the system's own.
- Test only (never in the exe): the `osc` package for Node 2.4.5 (osc.js, dual MIT / GPL-2.0, used under MIT; © Colin Clark)
  writes and reads the OSC interop fixtures under `tests/interop/osc/` — https://github.com/colinbdclark/osc.js.

### wp2-companion

### wp2-companion-module

The Companion module (`integrations/companion-module-playmap`) is the one piece of this repository with its own licence, the
owner's decision for it alone: its source is MIT (`LICENSE`, as Bitfocus requires of a module's source) and its packaged form is
GPL-3.0-only (`COPYING`, named in `companion/manifest.json`). The packaged bundle carries, all MIT:

- `@companion-module/base` 2.2.1 (© Bitfocus AS) with `colord` 2.10.0 (© Vlad Shilov) and `zod` 4.6.7 (© Colin McDonnell) —
  https://github.com/bitfocus/companion-module-base.
- `ws` 8.22.0 (© Einar Otto Stangvik, Arnout Kazemier and contributors) — https://github.com/websockets/ws. Its optional native add-ons are not
  installed; it runs in JavaScript.
- `src/palette/palette.js`, vendored byte for byte from Patterns' Companion module (MIT, © jammin808).

The package's `LICENSE` is written by `companion-module-build` from the licences of what it bundled. Development only (never
packaged): `@companion-module/host` and `@companion-module/tools` (MIT, © Bitfocus AS), TypeScript (Apache-2.0, © Microsoft),
ESLint, typescript-eslint and Prettier (MIT), `@types/node` and `@types/ws` (MIT).

### wp2-fx

- The ISF format (Interactive Shader Format, VIDVOX, specification under MIT): PlayMap reads and translates ISF files with its
  own parser and translator; no ISF code is shipped. The built-in effects in `src/PlayMap.Render/Effects/Library` are PlayMap's
  own. The opt-in corpus test compiles a checkout of the public ISF files the user names (`PLAYMAP_ISF_CORPUS`); none of
  them is in the tree.

### wp2-audio

- NAudio.Wasapi 3.1.0 and NAudio.Core 3.1.0 (MIT, © Mark Heath), WASAPI capture, the system's sound and one process's sound on
  Windows — https://github.com/naudio/NAudio. Referenced from the one `net10.0` target (the package targets `net9.0`); every call is
  under `[SupportedOSPlatform("windows")]`.
- Patterns' level formula (its `Spectrum.Analyse`, the same owner's) is ported in `Core/Audio/AudioAnalyser`; the onset detector
  is written from the published SuperFlux description (Böck and Widmer, 2013) and tempo detection from the published descriptions
  of comb-filtered autocorrelation and beat scoring (Ellis 2007; Davies and Plumbley 2007); no aubio, BTrack, librosa or Ableton
  Link code is used.

### wp2-content

### wp2-led

Nothing shipped: Art-Net, sACN (E1.31) and DDP output are PlayMap's own code, written from the published specifications.

- Art-Net™ Designed by and Copyright Artistic Licence Holdings Ltd.

### wp2-fleet

### wp2-backup

### wp2-sync

### wp2-wall

### wp2-resolve

### wp2-multiview

### wp2-phonescan

### wp2-multiscan

### wp2-align

### wp2-facade

### wp2-stacks

### wp2-desk

### wp2-desk-2

## Round 3

### wp3-rhi

### wp3-platform

### wp3-shaders

The shader worker's compilers, carried with the exe (beside it once published, under `runtimes/<rid>/native` in a build) from the
engine's packages, and on Windows SPIRV-Tools' library from the Windows job's pinned download; each file is held to its SHA-256
when the worker loads it (`src/PlayMap.Engine/ShaderWorker/CompilerNatives.cs`) and loaded by the worker process only, never by
the engine:

- shaderc (Apache-2.0, © The Shaderc Authors), with glslang (BSD-3-Clause, the Khronos Group and contributors; parts under the
  MIT, Apache-2.0 and GPL-with-the-Bison-exception licences listed in glslang's `LICENSE.txt`) and SPIRV-Tools (Apache-2.0, ©
  The Khronos Group Inc.) linked inside it: `libshaderc_shared.so` / `shaderc_shared.dll` from Silk.NET.Shaderc.Native 2.23.0
  (shaderc at commit 9460514). glslang compiles the GLSL 4.50 PlayMap's translator makes of a user's ISF effect into SPIR-V; on
  Linux the same library's SPIRV-Tools validates it.
- SPIRV-Tools (Apache-2.0, © The Khronos Group Inc.) on Windows: `SPIRV-Tools-shared.dll` from Google's own Visual Studio 2022
  release build (continuous build 261 of 16 October 2026), fetched by the Windows job at its hash, since shaderc's Windows build
  exports no validator.
- SPIRV-Cross (Apache-2.0, © The Khronos Group Inc. and Arm Limited): `libspirv-cross.so` / `spirv-cross.dll` from
  Silk.NET.SPIRV.Cross.Native 2.23.0. It writes HLSL from the validated SPIR-V.
- The DirectX Shader Compiler 1.9.2609 under the LLVM Release License (University of Illinois/NCSA): `dxcompiler.dll` /
  `libdxcompiler.so` from Vortice.Dxc.Native 1.0.6, for the worker and the HLSL library's tests; and Microsoft.Direct3D.DXC
  1.9.2609.5's `dxc.exe`, run by the Windows build to compile the HLSL library and never shipped. The notice the NCSA licence asks
  for is the one both packages' `LICENSE-LLVM.txt` carries: "Copyright (c) 2003-2015 University of Illinois at
  Urbana-Champaign. All rights reserved.", the LLVM Team's credit, the licence's conditions and its disclaimer; DXC's own sources
  carry "Copyright (C) Microsoft Corporation. All rights reserved." under the same licence.
- DXC's validator, `dxil.dll` / `libdxil.so` from Vortice.Dxc.Native 1.0.6, under Microsoft's own licence for the DirectX Shader
  Compiler (`LICENSE-MS.txt`, which the package names as its licence and asks its user to accept). That licence grants use
  "solely for use on Windows", and lets its distributable code be distributed on its Distribution Requirements, among them to
  "require distributors and external end users to agree to terms that protect it and Microsoft at least as much as this
  agreement" and to "indemnify, defend, and hold harmless Microsoft". The owner's acceptance of Microsoft's distribution terms
  (`docs/round3/PLAN.md` §9 decision 23) names the Agility runtime's `D3D12Core.dll`.

### wp3-installer

### wp3-desk-ux

### wp3-d3d12-core

### wp3-d3d12-passes

### wp3-stability

### wp3-present

### wp3-video

### wp3-desk-gpu

### wp3-d3d12-passes-2

### wp3-framelock

### wp3-stability-2

### wp3-shaders-2

### wp3-desk-shell

### wp3-desk-map

### wp3-canvas

### wp3-workflows

### wp3-capture
