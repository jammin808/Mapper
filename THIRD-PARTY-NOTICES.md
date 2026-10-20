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

The shader worker's compilers, carried with the exe (beside it once published; in a build under `runtimes/<rid>/native` as their
packages lay them down, or beside the engine's assemblies where the engine's build copies them), from the engine's packages, on
Windows SPIRV-Tools' library from the Windows job's pinned download, and on Linux DXC's compiler from DXC's pinned release; each
file is held to its SHA-256 when the worker loads it (`src/PlayMap.Engine/ShaderWorker/CompilerNatives.cs`) and loaded by the
worker process only, never by the engine:

- shaderc (Apache-2.0, © The Shaderc Authors), with glslang (BSD-3-Clause, the Khronos Group and contributors; parts under the MIT,
  Apache-2.0 and GPL-with-the-Bison-exception licences listed in glslang's `LICENSE.txt`) and SPIRV-Tools (Apache-2.0, © The
  Khronos Group Inc.) linked inside it: `libshaderc_shared.so` / `shaderc_shared.dll` from Silk.NET.Shaderc.Native 2.23.0 (built by
  Silk.NET at its commit 9460514). glslang compiles the GLSL 4.50 PlayMap's translator makes of a user's ISF effect into SPIR-V; on
  Linux the same library's SPIRV-Tools validates it.
- SPIRV-Tools (Apache-2.0, © The Khronos Group Inc.) on Windows: `SPIRV-Tools-shared.dll` from Google's own Visual Studio 2022
  release build (continuous build 261 of 16 October 2026), fetched by the Windows job at its hash, since shaderc's Windows build
  exports no validator.
- SPIRV-Cross (Apache-2.0, © The Khronos Group Inc. and Arm Limited): `libspirv-cross.so` / `spirv-cross.dll` from
  Silk.NET.SPIRV.Cross.Native 2.23.0. It writes HLSL from the validated SPIR-V.
- The DirectX Shader Compiler (DXC) 1.9.2609's compiler, dxcompiler, the one part of DXC PlayMap ships and loads at run time, under
  the LLVM Release License (University of Illinois/NCSA Open Source License): `dxcompiler.dll` from Microsoft.Direct3D.DXC
  1.9.2609.5 (https://www.nuget.org/packages/Microsoft.Direct3D.DXC/1.9.2609.5; the package declares `LICENSE-LLVM.txt` as its
  licence, and its x64 `dxcompiler.dll` and that `LICENSE-LLVM.txt` are the files of it the engine's build copies) on Windows, and
  `libdxcompiler.so` from DXC v1.9.2609's GitHub release `linux_dxc_2026_09_28.x86_x64.tar.gz`
  (https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2609/linux_dxc_2026_09_28.x86_x64.tar.gz) on Linux,
  for the worker and the HLSL library's tests. The same package's `dxc.exe`, under the same licence, runs only in the Windows build,
  to compile the HLSL library, and is never shipped; the build starts it without `DXC_DXIL_DLL_PATH`, so dxcompiler's built-in
  validator signs that DXIL too. DXC's release notes (`ReleaseNotes.md`, in the package and in the archive) give `LICENSE-LLVM.txt`
  to every file but `d3d12shader.h` (MIT); the package and the archive also carry a `LICENSE-MS.txt` that the notes' table assigns
  to no file, and no output carries it. dxcompiler's built-in validator validates the DXIL and writes its hash, so DXC's separate
  validator library (`dxil.dll`, `libdxil.so`) is neither used nor shipped, and dxcompiler is the one binary of DXC's that is
  shipped (`docs/round3/PLAN.md` §9 decision 24). Wherever the engine's build copies dxcompiler, in every build and publish, it lays
  down beside it, in `licenses/DXC/`, the text the licence asks to travel with the binary: the package's `LICENSE-LLVM.txt` (the
  same file the archive carries), and DXC's own `LICENSE.TXT` and `ThirdPartyNotices.txt` at tag v1.9.2609
  (https://github.com/microsoft/DirectXShaderCompiler/tree/v1.9.2609; carried byte for byte in
  `src/PlayMap.Engine/ShaderWorker/DxcNotices`), which give the licences of the third-party code inside it. The licence, as
  `LICENSE-LLVM.txt` carries it:

  ```text
  ==============================================================================
  LLVM Release License
  ==============================================================================
  University of Illinois/NCSA
  Open Source License

  Copyright (c) 2003-2015 University of Illinois at Urbana-Champaign.
  All rights reserved.

  Developed by:

      LLVM Team

      University of Illinois at Urbana-Champaign

      http://llvm.org

  Permission is hereby granted, free of charge, to any person obtaining a copy of
  this software and associated documentation files (the "Software"), to deal with
  the Software without restriction, including without limitation the rights to
  use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
  of the Software, and to permit persons to whom the Software is furnished to do
  so, subject to the following conditions:

      * Redistributions of source code must retain the above copyright notice,
        this list of conditions and the following disclaimers.

      * Redistributions in binary form must reproduce the above copyright notice,
        this list of conditions and the following disclaimers in the
        documentation and/or other materials provided with the distribution.

      * Neither the names of the LLVM Team, University of Illinois at
        Urbana-Champaign, nor the names of its contributors may be used to
        endorse or promote products derived from this Software without specific
        prior written permission.

  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
  FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
  CONTRIBUTORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS WITH THE
  SOFTWARE.
  ```

  DXC's own sources carry "Copyright (C) Microsoft Corporation. All rights reserved." under the same licence. The third-party code
  compiled into dxcompiler, as DXC's `LICENSE.TXT` and `ThirdPartyNotices.txt` name it: LLVM's support library, its System
  Interface Library under the same licence with the further copyright "Copyright (C) 2004 eXtensible Systems, Inc."; the OpenBSD
  regular-expression code, whose notices follow; miniz (MIT, Copyright 2013-2014 RAD Game Tools and Valve Software and Copyright
  2010-2014 Rich Geldreich and Tenacious Software LLC; its `miniz.c` in the public domain); `ConvertUTF` (Copyright 2001-2004
  Unicode, Inc., under its notice); the MD5 code (written by Alexander Peslyak and placed in the public domain, or else Copyright (c)
  2001 Alexander Peslyak with redistribution and use in source and binary forms permitted); and `regstrlcpy.c` (Copyright (c) 1998
  Todd C. Miller, under its permission notice). The OpenBSD regular-expression code's notices, as `ThirdPartyNotices.txt` carries
  them:

  ```text
  $OpenBSD: COPYRIGHT,v 1.3 2003/06/02 20:18:36 millert Exp $

  Copyright 1992, 1993, 1994 Henry Spencer.  All rights reserved.
  This software is not subject to any license of the American Telephone
  and Telegraph Company or of the Regents of the University of California.

  Permission is granted to anyone to use this software for any purpose on
  any computer system, and to alter it and redistribute it, subject
  to the following restrictions:

  1. The author is not responsible for the consequences of use of this
     software, no matter how awful, even if they arise from flaws in it.

  2. The origin of this software must not be misrepresented, either by
     explicit claim or by omission.  Since few users ever read sources,
     credits must appear in the documentation.

  3. Altered versions must be plainly marked as such, and must not be
     misrepresented as being the original software.  Since few users
     ever read sources, credits must appear in the documentation.

  4. This notice may not be removed or altered.

  =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=
  /*-
   * Copyright (c) 1994
   *	The Regents of the University of California.  All rights reserved.
   *
   * Redistribution and use in source and binary forms, with or without
   * modification, are permitted provided that the following conditions
   * are met:
   * 1. Redistributions of source code must retain the above copyright
   *    notice, this list of conditions and the following disclaimer.
   * 2. Redistributions in binary form must reproduce the above copyright
   *    notice, this list of conditions and the following disclaimer in the
   *    documentation and/or other materials provided with the distribution.
   * 3. Neither the name of the University nor the names of its contributors
   *    may be used to endorse or promote products derived from this software
   *    without specific prior written permission.
   *
   * THIS SOFTWARE IS PROVIDED BY THE REGENTS AND CONTRIBUTORS ``AS IS'' AND
   * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
   * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
   * ARE DISCLAIMED.  IN NO EVENT SHALL THE REGENTS OR CONTRIBUTORS BE LIABLE
   * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
   * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
   * OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
   * HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
   * LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
   * OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
   * SUCH DAMAGE.
   *
   *	@(#)COPYRIGHT	8.1 (Berkeley) 3/16/94
   */
  ```

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
