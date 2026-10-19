# Third-party notices

What PlayMap ships or loads from others is listed here, each under the package that brought it, with its licence and the notice its
licence asks for. NDI's runtime loads from the user's own install and NVAPI from the user's NVIDIA driver, and neither is ever
shipped. Round 3 will load NVIDIA's Aftermath library from wherever the user put it, AMD's runtimes (ADLX among them) from the
driver, and the capture cards' runtimes (DeckLink's, and AJA's and Magewell's if their stretch lands) from their drivers' installs,
and will ship none of them. The installer ships FFmpeg as the LGPL build's replaceable shared libraries in `ffmpeg\` (wp3-installer,
below), and no other FFmpeg build is ever shipped (`docs/round3/PLAN.md` §9 decision 4); the portable folder ships none, and the
engine it runs loads the shared libraries the user puts in place. ANGLE's library comes beside the exe in the desk's Avalonia package
(wp2-d3d11, below). Vendored source lives under `src/ThirdParty`, outside the analyzers' fences, with its licence beside it. The
installer and the portable folder both carry this file, the licence texts named below and the packages' own licence and notice
files under `licenses\` (wp3-installer, below).

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
  `src/PlayMap.Render/Platform/Windows` and `src/PlayMap.Media/Video/Spout` follow the Windows SDK's published declarations; the
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

### wp3-installer

What the installer lays down beside PlayMap's own files is listed here, and `{app}\SHA256SUMS` names every file with its hash. This
file is laid down as `{app}\THIRD-PARTY-NOTICES.md`; each licence text sits beside the files it covers, the licence and notice files
of the packages PlayMap's publish comes from sit under `{app}\licenses\`, and `{app}\licence.txt` holds the terms the installer's
licence page shows. The portable folder carries the same publish with this file, `soft_oal-LICENSE.txt` and `licenses\` beside it
(`installer/notices.ps1` lays them down for both, and stops the build when the publish carries a package this file does not name);
FFmpeg, the Agility SDK's runtime, libvmx and `licence.txt` are the installer's alone.

- **FFmpeg** (https://ffmpeg.org) `n9.0.2-17-g2a571b6068`, commit `2a571b6068` of its `release/9.0` branch, as FFmpeg-Builds'
  `lgpl-shared` build (https://github.com/BtbN/FFmpeg-Builds, release `autobuild-2026-09-30-13-08`, the win64 zip pinned by its
  SHA-256 `7157177b8a6cb2174c1650ba8c71b363f2c78cba5330f88c4c02cf5b2b880646`). The build is configured with `--enable-version3`,
  so its libraries are under the GNU Lesser General Public License version 3 or later: the build's own `LICENSE.txt` (the LGPL
  version 3) is laid down as `ffmpeg\LICENSE.txt`, and the GNU General Public License version 3, whose terms the LGPL version 3
  adds to, as `ffmpeg\GPL-3.0.txt`. Its shared libraries are inside the installer as replaceable files in `ffmpeg\`, and a person
  may put other builds of them in their place: with the engine stopped (`PlayMap.Supervisor.exe stop` on a supervised machine; on
  any other, the desk and any engine run by hand closed), replace `avutil-61.dll`, `swresample-7.dll`, `swscale-10.dll`,
  `avcodec-63.dll`, `avformat-63.dll`, `avfilter-12.dll` and `avdevice-63.dll` in `C:\Program Files\PlayMap\ffmpeg\` with 64-bit
  Windows builds of FFmpeg 9.0 of the same names, then start it again (`PlayMap.Supervisor.exe resume`, or the desk). The engine
  loads them from that folder by full path and refuses, with words, a library whose major version is not the one it is built for.
  The install's `SHA256SUMS` then no longer matches those files, and an upgrade lays the installer's own build down again. Its
  source is https://git.ffmpeg.org/ffmpeg.git at that commit, and FFmpeg-Builds'
  scripts at that release name the source and revision of every library below. Linked into those libraries, as the build's
  configuration names them, each under its project's licence: libiconv (LGPL-2.1-or-later), zlib (Zlib), libxml2 (MIT), libvmaf
  (BSD-2-Clause-Patent), fontconfig (MIT-style), HarfBuzz (MIT), FreeType (FreeType License), FriBidi (LGPL-2.1-or-later), the
  Vulkan headers (Apache-2.0; the Vulkan loader is the driver's), dav1d (BSD-2-Clause), libogg and libvorbis (BSD-3-Clause), rav1e
  (BSD-2-Clause), librsvg (LGPL-2.1-or-later), GMP (LGPL-3.0-or-later), liblzma (0BSD), LCEVCdec (BSD-3-Clause-Clear), the OpenCL
  ICD loader (Apache-2.0), AMF's headers (MIT), libaom (BSD-2-Clause), Chromaprint (MIT), nv-codec-headers (MIT), Game_Music_Emu
  (LGPL-2.1-or-later), Kvazaar (BSD-3-Clause), aribb24 (LGPL-3.0-or-later), libaribcaption (MIT), libass (ISC), libbluray
  (LGPL-2.1-or-later), libjxl (BSD-3-Clause), LAME (LGPL-2.0-or-later), Opus (BSD-3-Clause), libplacebo (LGPL-2.1-or-later),
  librist (BSD-2-Clause), libssh (LGPL-2.1), libtheora (BSD-3-Clause), libvpx (BSD-3-Clause), libwebp (BSD-3-Clause), libzmq
  (MPL-2.0), LV2 and lilv (ISC), libvpl (MIT), OpenAL Soft (LGPL-2.0-or-later), OpenAPV (BSD-3-Clause), opencore-amr
  (Apache-2.0), OpenH264 (BSD-2-Clause), OpenJPEG (BSD-2-Clause), libopenmpt (BSD-3-Clause), SDL2 (Zlib), Snappy (BSD-3-Clause),
  libsoxr (LGPL-2.1-or-later), SRT (MPL-2.0), SVT-AV1 (BSD-3-Clause-Clear), TwoLAME (LGPL-2.1-or-later), uavs3d (BSD-3-Clause),
  libva (MIT), VVenC (BSD-3-Clause-Clear), zimg (WTFPL), ZVBI (LGPL-2.1-or-later), and the MinGW-w64 runtime with winpthreads
  (ZPL-2.1 and MIT) and GCC's libgomp (GPL-3.0-or-later with the GCC Runtime Library Exception). The GPL command-line build of the
  same release is used on CI only, to encode test media, and is never shipped.
- **The DirectX Agility SDK's `D3D12Core.dll`** (Microsoft, the `Microsoft.Direct3D.D3D12` package 1.619.6 from nuget.org,
  `tools/ci-d3d12.ps1`'s pin), under the MICROSOFT DIRECTX licence terms that ship in that package, whose DISTRIBUTABLE CODE section
  lets it be shipped: `D3D12Core.dll` is on the package's list of distributable files and is laid down in `D3D12\`; its debug layer
  (`d3d12SDKLayers.dll`) and `d3dconfig.exe`, distributable too, are left out. The installer's licence page shows those terms,
  copied verbatim from the package's `LICENSE.txt`, as the terms a person agrees to for `D3D12Core.dll` before anything is laid
  down; they are laid down with it in `{app}\licence.txt`. A quiet install, which shows no page, lays nothing down unless its command
  line carries `/ACCEPTTERMS=yes`. The portable folder carries no file of the Agility SDK.
- **OpenAL Soft** (https://openal-soft.org, source https://github.com/kcat/openal-soft at tag `1.23.1`), under the GNU Library
  General Public License version 2 or later: `soft_oal.dll`, the file the `Silk.NET.OpenAL.Soft.Native` package 1.23.1 puts beside
  the exe, with the licence's text laid down beside it as `soft_oal-LICENSE.txt`; the portable folder carries both. It is a
  replaceable file loaded at run time.
- **libvmx** (src/ThirdParty/libvmx, MIT, © 2025 Open Media Transport Contributors) is inside the installer as `libvmx.dll`, built
  from the vendored source with its upstream Windows command, with its licence laid down beside it as `libvmx-LICENSE.txt`.
- The GNU licence texts the payload carries (`installer/licenses/GPL-3.0.txt`, `installer/licenses/LGPL-2.0.txt`) are the Free
  Software Foundation's, copied verbatim as they allow; the Apache License 2.0's (`installer/licenses/Apache-2.0.txt`, laid down as
  `licenses\Apache-2.0.txt`) is the Apache Software Foundation's, copied verbatim.
- **Inno Setup 7.1.0** (Jordan Russell and Martijn Laan, https://jrsoftware.org/isinfo.php; source https://github.com/jrsoftware/issrc
  at `is-7_1_0`), under the Inno Setup License: it builds the installer, and its setup and uninstall programs are inside the
  installer EXE. The licence allows any use; its authors ask a commercial user to buy a licence, a request and not a condition, which
  is the owner's own to meet (`docs/round3/PLAN.md` §9 decision 3). The installer is downloaded on CI from the project's release,
  pinned by hash, and checked against the release's own digest and its publisher's signature.
- **PlayMap's publish**: the self-contained .NET runtime and the packages PlayMap's own files come from, which the installer lays
  down in `{app}` and the portable folder carries. Each is named here with the copyright its package states and its licence; the
  licence and notice files a package carries are laid down under `licenses\<package id>\` as the package holds them, copied from the
  build's restore, so a notice that names the projects a package's native library incorporates (the .NET runtime's, SkiaSharp's,
  HarfBuzzSharp's, ONNX Runtime's) is there whole.
  - **.NET** 10 (`Microsoft.NETCore.App.Runtime.win-x64` and `Microsoft.AspNetCore.App.Runtime.win-x64`, the runtime and ASP.NET
    Core the publish carries; MIT, Copyright (c) .NET Foundation and Contributors), each with its `LICENSE` and its
    `THIRD-PARTY-NOTICES` laid down; and the packages of the same projects, MIT under the same copyright, their packages stating
    © Microsoft Corporation: `Microsoft.Extensions.*`, `Microsoft.DotNet.PlatformAbstractions` and `System.Numerics.Tensors`, and,
    inside the supervisor's single-file exe with the same runtime, `System.ServiceProcess.ServiceController` and
    `System.Diagnostics.EventLog`.
  - **Avalonia** 12.1.4 (`Avalonia.*`; MIT, Copyright 2013-2026 © The AvaloniaUI Project), the desk's interface, with
    `MicroCom.Runtime` 0.11.6 (MIT, Copyright 2021 © Nikita Tsukanov) and `Tmds.DBus.Protocol` 0.94.1 (MIT, © Tom Deseyn). Its
    `Avalonia.Fonts.Inter` carries the Inter typeface, as PlayMap.Render does (round 1, above), under the SIL Open Font License 1.1,
    Copyright (c) 2016 The Inter Project Authors, whose text is laid down as `licenses\Inter-OFL.txt`. ANGLE
    (`Avalonia.Angle.Windows.Natives`) is wp2-d3d11's entry, above.
  - **SkiaSharp** 3.119.4 and **HarfBuzzSharp** 8.3.1.3 (`SkiaSharp.*`, `HarfBuzzSharp.*`; MIT, Copyright (c) 2015-2016 Xamarin,
    Inc. and Copyright (c) 2017-2018 Microsoft Corporation), the desk's 2D drawing and text shaping; their native libraries
    (`libSkiaSharp.dll`, `libHarfBuzzSharp.dll`) incorporate Skia, HarfBuzz and the other projects their packages'
    `THIRD-PARTY-NOTICES.txt` names, each with its notice.
  - **Silk.NET** 2.23.0 (`Silk.NET.*`; MIT, © .NET Foundation and Contributors, its packages' authors), the OpenGL, OpenAL and
    windowing bindings, and **GLFW** 3.4 (`Ultz.Native.GLFW`, `glfw3.dll`), under the zlib licence below. OpenAL Soft
    (`Silk.NET.OpenAL.Soft.Native`) is the entry above.
  - **FFmpeg.AutoGen** 9.0.1.1 (`FFmpeg.AutoGen`; MIT, Copyright (c) 2025 Ruslan Balanukhin (Rationale One)), the bindings the engine
    loads FFmpeg's libraries through.
  - **ONNX Runtime** 1.31.0 (`Microsoft.ML.OnnxRuntime`, `Microsoft.ML.OnnxRuntime.Managed`; MIT, Copyright (c) Microsoft
    Corporation), the vision models' runtime (`onnxruntime.dll`), with its `ThirdPartyNotices.txt` laid down.
  - **The Anthropic SDK for C#** 12.55.0 (`Anthropic`; MIT, Copyright 2026 Anthropic), the assistant's client.
  - **The Model Context Protocol C# SDK** 2.2.0 (`ModelContextProtocol.*`; Apache License 2.0, © Model Context Protocol a Series of
    LF Projects, LLC.), the MCP server, the licence laid down as `licenses\Apache-2.0.txt`; its packages carry no NOTICE file.
  - **NAudio** 3.1.0 (`NAudio.*`; MIT, © Mark Heath), wp2-audio's entry, above; libomtnet and libvmx, wp2-videoio's.
- **The MIT License.** Each component this file names under the MIT License is under these terms, with the copyright line named for
  it: "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation
  files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy,
  modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
  furnished to do so, subject to the following conditions: The above copyright notice and this permission notice shall be included
  in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN
  NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
  CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
  SOFTWARE."
- **GLFW's zlib licence**, Copyright (c) 2002-2006 Marcus Geelnard and Copyright (c) 2006-2019 Camilla Löwy: "This software is
  provided 'as-is', without any express or implied warranty. In no event will the authors be held liable for any damages arising
  from the use of this software. Permission is granted to anyone to use this software for any purpose, including commercial
  applications, and to alter it and redistribute it freely, subject to the following restrictions: 1. The origin of this software
  must not be misrepresented; you must not claim that you wrote the original software. If you use this software in a product, an
  acknowledgment in the product documentation would be appreciated but is not required. 2. Altered source versions must be plainly
  marked as such, and must not be misrepresented as being the original software. 3. This notice may not be removed or altered from
  any source distribution."

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
