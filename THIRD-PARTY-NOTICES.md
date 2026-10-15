# Third-party notices

What PlayMap ships or loads from others is listed here, each under the package that brought
it, with its licence and the notice its licence asks for. Restrictive runtimes (NDI, FFmpeg, ANGLE) load from the user's own
install and are never shipped; vendored source lives under `src/ThirdParty`, outside the analyzers' fences, with its licence beside
it.

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

### wp2-control

Nothing shipped: OSC, MIDI, Art-Net, LTC and MTC are PlayMap's own code, written from the published specifications.

- Art-Net™ Designed by and Copyright Artistic Licence Holdings Ltd.
- The ALSA sequencer library (`libasound.so.2`, LGPL-2.1-or-later) is loaded at run time from the user's Linux system and linked
  dynamically; it is never shipped. WinMM (`winmm.dll`) is the system's own.
- Test only (never in the exe): the `osc` package for Node 2.4.5 (osc.js, dual MIT / GPL-2.0, used under MIT; © Colin Clark)
  writes and reads the OSC interop fixtures under `tests/interop/osc/` — https://github.com/colinbdclark/osc.js.

### wp2-companion

### wp2-companion-module

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
