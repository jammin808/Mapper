# Projectors — how PlayMap talks to them and what it can set up inside them

PlayMap drives projectors for two reasons: to **run the show** (power, input, shutter, health on the Show Bus) and
to **map** (switch a projector's own geometry correction off before a scan, use its shutter for black frames,
read and recall its lens, show its own patterns to identify it). This paper is what the published protocols
allow, what `PlayMap.Devices` implements from them, what is verified and what is not, and where the honest
limits are — above all, whether PlayMap can push its own warp and blend *into* a projector. Short answer:
**it can switch the projector's internal geometry and blending off everywhere a protocol says how, it can set a
few published parameters (Panasonic's corner correction, blend widths, white balance), and it cannot upload a
warp grid through any public API PlayMap was built from.** PlayMap's own output stage (warp lattice, blend,
black-level pedestal) stays the one place geometry lives; the projectors' internal correction is kept neutral.

## 1. How this was researched, and how far to trust it

The vendor and standards sites were not reachable from the build machine (`pjlink.jbmia.or.jp`,
`files.support.epson.com`, `optics.fujifilm.com` and the other vendor hosts were refused by the network policy,
and the shared web-search allowance ran out after one search). What was read instead, this session:

- **Vendor documents mirrored in public repositories** — Panasonic's *LAN Control Protocol* explanation, its
  PT-RZ570 (2019) and PT-DZ21K2-series (2015) control-command tables and the PT-VZ580 table, and the client code of
  Panasonic's own monitoring software (decompiled), all in [peschuster/ntcontrol-connection][ntc] `doc/`.
- **Public implementations used against real projectors** — Bitfocus Companion's modules for
  [Christie][cc], [Panasonic][cp], [Barco Pulse][cb], [Barco DP][cbd], [Epson business][ce],
  [Digital Projection][cdp], [Vivitek][cv] and [PJLink][cpj]/[generic PJLink][cgp]; the PJLink class 1/2 library
  [aiopjlink][aio] (PyPI 1.0.5) and [pypjlink][pyp]; and the ESC/VP.net library Home Assistant uses,
  [pszafer/epson_projector][eps], which states it follows Epson's ESC/VP.net manual.
- **Patterns' own PJLink session** (`Patterns.Core/Services/DeviceProfiles.cs`), which follows class 1 to the
  letter and carries the spec's worked authentication example.
- **Recollection** of vendor manuals that could not be re-read (Sony ADCP, NEC binary control, the PJLink class 2
  search/notification timing, VESA E-EDID). Everything that rests on recollection is marked so below and, in the
  code, sits behind a gate.

The words used throughout:

| Mark | Meaning | In the code |
|---|---|---|
| **verified** | Read this session in a vendor document or in a public implementation built from one | `DriverProvenance.VendorDocument` / `PublicImplementation`; the command is enabled |
| **model-dependent** | Verified for the models a table names; other models answer "unknown command" (harmless) | enabled, with a caveat the desk shows (`IProjectorDriver.Caveat`) |
| **unverified** | Recalled, or inconsistent in the source | in `IProjectorDriver.Unverified`: refused until the link sets `AllowUnverified` after testing on the model |
| **not implemented** | No public word for it in what was read — nothing is invented | refused with "*X* has no published command for *Y*" |

## 2. The protocols at a glance

| Protocol | Port | Framing | Authentication | Provenance | Main sources |
|---|---|---|---|---|---|
| **PJLink** class 1/2 (JBMIA) | TCP 4352; UDP 4352 for search and notifications | `%1POWR 1⏎` → `%1POWR=OK⏎`; one CR-terminated line each way; the projector drops a connection idle 30 s | `PJLINK 1 <seed>` → the first command carries `md5(seed + password)` (lowercase hex) in front; `PJLINK ERRA` when wrong; `PJLINK 0` = off | verified (spec example digest reproduced) | JBMIA spec [cl2] (cited), [aio], [cgp], [pyp], Patterns |
| **Christie** serial over TCP | TCP 3002 | `(PWR 1)`, `(SHU?)` → `(SHU!001)`; a good set is answered with **silence**, a bad one with an `ERR` frame; unsolicited `FYI` frames | none on this port | verified (public implementation) | [cc] `src/api.js`, `actions.js`, `choices.js` |
| **Panasonic NTCONTROL** | TCP 1024 | `00PON⏎` → `00PON⏎`; queries `00QPW⏎` → `00001⏎`; `ERR1–5`, `ERRA`, `ER401` | `NTCONTROL 1 <8 chars>` → each command prefixed by `MD5("user:password:seed")` (32 hex; Panasonic's own client sends uppercase); `NTCONTROL 0` = off; default user `admin1` | verified (vendor document) | [ntc] `doc/Panasonic-LAN_Protocol_exp.pdf`, `com_rz570.pdf`, `pt-dz21k2u_series_rs-232c_controlspec.pdf`, `EwsNtControl.cs` |
| **Barco Pulse** | TCP 9090 | JSON-RPC 2.0 objects with **no delimiter**; notifications have no id | none needed for these calls (Pulse has an `authenticate` method for higher access levels — not read, not used) | verified (public implementation) | [cb] `src/constants.js`, `actions.js`, `utils.js` |
| **Sony ADCP** | TCP 53595 | `power "on"` CR LF → `ok`; `power_status ?` → `"standby"`; `err_…` words | greeting `NOKEY` (off) or a challenge → `sha256(challenge + password)` hex → `OK` / `err_auth` | **unverified** (recalled; whole driver gated) | Sony's ADCP protocol manual (not reachable) |
| **Epson ESC/VP.net** + ESC/VP21 | TCP 3629 (UDP 3629 `HELLO` discovery) | 16-byte header `ESC/VP.net`, 0x10, type 3 (CONNECT), status, header count; then ESC/VP21 lines `PWR?⏎` → `PWR=01⏎:`, sets → `:`, errors `ERR⏎:` | an 18-byte password header (id 1, attribute 1, 16 bytes); status 0x41 required / 0x43 wrong / 0x53 busy | verified (public implementation of Epson's manual) | [eps] `escvpnet/headers.py`, `message.py`, `const.py`; [ce] |
| **NEC / Sharp NEC** binary | TCP 7142 | command bytes, id, model code, length, data, checksum (low byte of the sum); answer first byte +0x20 (ack) / +0xA0 (error, 2 error bytes) | none | **unverified** (recalled; whole driver gated) | NEC *Projector Control Command Reference Manual* (not reachable) |
| Digital Projection | TCP 7000 | ASCII `*input.select = 0⏎` | — | read, not implemented | [cdp] |
| Barco DP (legacy cinema) | TCP 43728 | binary, `0xFE … 0xFF` with checksum | — | read, not implemented | [cbd] |
| Vivitek | TCP | `op power.on⏎`, `op shutter +` | — | read, not implemented | [cv] |
| Optoma, BenQ, InFocus, JVC… | RS-232 (some over TCP) | per model | — | not researched further: PJLink covers them | — |

## 3. What each driver does (the capability matrix)

✓ verified and enabled · ◐ enabled, model-dependent (caveat shown) · ⊘ implemented but gated as unverified ·
✗ no public word in what was read (refused, nothing invented)

| Capability | PJLink | Christie | Panasonic | Barco Pulse | Sony ADCP | Epson | NEC |
|---|---|---|---|---|---|---|---|
| Power | ✓ `POWR 1/0` | ✓ `(PWR 1)` | ✓ `PON`/`POF` | ✓ `system.poweron/off` | ⊘ `power "on"` | ✓ `PWR ON/OFF` | ⊘ `02 00…` / `02 01…` |
| Input | ✓ `INPT 31` (class 2: `3A`) | ✓ `(SIN 1)` | ✓ `IIS:HD1` | ✓ `image.window.main.source` | ⊘ `input "hdmi1"` | ✓ `SOURCE 30` | ✗ |
| Shutter / AV mute | ✓ `AVMT 31/30` | ✓ `(SHU 1)` | ✓ `OSH:1/0` | ✓ `optics.shutter.target` | ⊘ `blank "on"` | ✓ `MUTE ON/OFF` | ⊘ picture mute `02 10…` |
| Freeze | ◐ `%2FREZ` (class 2) | ✓ `(FRZ 1)` | ✓ `OFZ:1/0` | ✗ | ✗ | ✓ `FREEZE ON/OFF` | ⊘ `01 98 … 01` |
| Lens motors | ✗ | ✓ absolute `(LHO ±nnnn)`, `LVO`, `ZOM`, `FCS` | ◐ absolute shift/focus `VXX:LNSI7/8/9`; zoom by steps `LNSI5` | ◐ steps `optics.zoom.stepforward {steps}` | ✗ | ✗ | ✗ |
| Lens position read | ✗ | ✓ `(LHO?)` … | ◐ `QVX:LNSI7/8/9` (no zoom query) | ✗ | ✗ | ✗ | ✗ |
| Lens memory | ✗ | ✗ | ✓ `VXX:LNMI1=+0000n` | ✗ | ✗ | ✓ `POPLP nn` | ✗ |
| Built-in patterns | ✗ | ◐ `(ITP 0..3)` off/grid/grey/white | ◐ `OTS:00/01/02/05/08` | ◐ `image.testpattern.show` on/off | ✗ | ◐ `TESTPATTERN 00` (off only) | ✗ |
| Geometry off | ✗ | ⊘ `(WRP+SLCT 0)`, falls back to `(WPS 0)` | ◐ `VXX:GMMI0=+00000` | ✗ | ✗ | ✗ | ✗ |
| Warp upload | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |
| Edge blend | ✗ | ✗ | ◐ `VXX:EDBI0`, sides `VGL/VGR/VGU/VGB`, widths `ELWI0/ERWI0/EUWI0/EBWI0` | ✗ | ✗ | ✗ | ✗ |
| Black level | ✗ | ✗ | ⊘ `VJI:www.rrr.ggg.bbb` (the table writes it two ways) | ✗ | ✗ | ✗ | ✗ |
| Colour gains | ✗ | ✗ | ◐ `VHR/VHG/VHB:000–255` | ✗ | ✗ | ✗ | ✗ |
| Status | ✓ POWR, ERST, LAMP, AVMT, INPT, NAME, INF1/2, CLSS; class 2 SNUM, SVER, IRES, FREZ | ✓ PWR, SHU, FRZ, SIN, LPH | ✓ QPW, Q$S, QSH, QFZ, QIN, QID, QSN, SVRS0, Q$L, QTM:0–2 (°C), NSGS1 (signal) | ✓ system.state, shutter position, source, model, serial, firmware | ⊘ power_status, blank, input, modelname, serialnum | ✓ PWR, MUTE, FREEZE, SOURCE, LAMP, SNO, ERR | ⊘ running status (power, cooling) |

What PlayMap reads into `DeviceState` (every field null/unknown until read; never guessed): online, power
(standby / warming up / on / cooling down), input, shutter, freeze, light-source hours, temperatures, faults in
words with severity (PJLink's ERST six digits: fan, lamp, temperature, cover, filter, other — 1 warning, 2 error),
manufacturer, model, the manufacturer's other information (PJLink INFO), serial, firmware, signal (raster and the projector's own words), lens positions in raw units,
last seen. The light: grey = never heard (or a gated driver), green = live and fine, amber = stale, a warning or a
power transition, red = gone or an error fault.

### Per-protocol notes

**PJLink.** One connection per command (greeting → digest + command → answer → close); a status poll sends its
queries on one connection, which the spec allows (the digest goes in front of the first only). ERR1 *undefined
command* (also how a class 1 projector answers class 2), ERR2 *out of parameter*, ERR3 *unavailable time* (busy,
warming, cooling, standby), ERR4 *projector failure*, ERRA *authorization error*. A projector that serves one
controller at a time may drop a second connection before greeting: the driver retries once after 300 ms. Class 2
adds the **search** — `%2SRCH` broadcast to UDP 4352, each projector answers `%2ACKN=<MAC>` to the controller's
UDP 4352 — and **status notifications** to UDP 4352: `%2LKUP=<MAC>` when its network comes up, `%2ERST=`,
`%2POWR=`, `%2INPT=` when those change. The answer grammar is verified; the exact timing a projector may take to
answer a search, and how its notification destination is chosen, are recalled from the class 2 spec (the
discovery window is configurable, 3 s by default, and PlayMap accepts notifications only from known hosts).
IRES answers `-` for no signal and `*` for an unknown one. Epson's own PJLink page (seen as a search result)
lists AVMT 11/21 as unsupported on some models — which is why the shutter uses AVMT 31/30, the one mute every
class 1 projector has.

**Christie.** The codes come from a public implementation that drives Roadie, Roadster, Mirage, DS+/DW/HD,
Matrix, M-series and Boxer families. Error codes: 001 system crash, 002 system warning, 003 invalid parameter,
004 too many parameters, 005 too few, 006 source does not exist, 007 could not be executed, 008 checksum,
009 unknown request, 010 communication error, 011 receive break, 012 supplementary information. Commands are
spaced 150 ms (the implementation's note that Christie controllers drop closer ones). Lens shift is ±2050 raw
units in that implementation; zoom/focus ranges differ by lens. Christie's *Twist* warp/blend and the Pandoras
Box/Widget Designer media servers are configured through Christie's own software and web UI; the serial
commands read only *select* stored warps (`WRP+SLCT`, `WPS`) — none writes a grid. The newer Griffyn, Crimson
and M 4K families also take DMX/Art-Net for shutter and lens (from their manuals, as recalled — not read here).

**Panasonic.** Verified from Panasonic's own documents. Each connection greets with a fresh seed; every command
in protected mode carries the token; the projector closes after 30 s idle (some display models after every
answer). The tables are per model: what the DZ21K2-series table lists (absolute lens positions, geometry off,
corner correction, edge blending) is marked model-dependent; a model without a command answers ERR1. The
shutter's fade-in/out times are settings (`VXX:SEFS1/SEFS2`, 0.0 s by default) — the only documented shutter
timing of any vendor. *Geometry Manager Pro* writes its grid warps through a persistent binary variant of
NTCONTROL (`21ADZZ;…`, seen in captures in [ntc] `doc/captures/`) whose payloads are not published.

**Barco Pulse.** Power, source, shutter (target and position), pattern on/off, lens steps (`{"steps": n}`) and
identity (`system.modelname`, `system.serialnumber`, `firmware.firmwareversion`) are verified and used; the same
implementation also calls lens calibration (`optics.zoom.calibrate`…), `optics.shifttocenter` and
`system.articlenumber`, which PlayMap does not use yet. Barco documents warp, blend and absolute lens properties for Pulse in
its API reference, which could not be read; PlayMap refuses those rather than guess property names.
`system.state` values beyond on/ready/standby (`conditioning`, `deconditioning`, `boot`, `eco`) are recalled.

**Epson.** ESC/VP.net's header and status codes and the ESC/VP21 words PWR, SOURCE, MUTE, FREEZE, LAMP, SNO, ERR,
POPLP/PUSHLP (lens position load/save), TESTPATTERN (00 = off) are verified across the two public
implementations. PWR? values 01 on and 04 standby (network on) are verified; 00, 02 warming, 03 cooling,
05 abnormal standby are recalled from the ESC/VP21 command guide. Epson's pro models do edge blending, black
level and geometry through *Epson Projector Professional Tool*; their ESC/VP21 words were not in what was read.

**Sony ADCP, NEC binary.** Both are implemented as recalled — handshakes, power, picture blank/mute, status — and
**gated as a whole**: every command (raw lines too) is refused until the link's `AllowUnverified` is set after
testing on the model. Both families speak PJLink, which `DriverSuggestion` keeps as their default.

## 4. Other ways in

| Method | What it gives | PlayMap |
|---|---|---|
| **EDID / DisplayID over DDC** (what a GPU output reads) | manufacturer id, product code, model name, serial text, native (preferred) timing and its refresh | `Display.Edid` parses the base block; `EdidInfo.Matches` pairs an output with a networked projector by serial, then model. DisplayID extension blocks are not parsed yet. The platform reads the bytes (registry on Windows, `/sys/class/drm/*/edid` on Linux) — the engine's job |
| **DDC/CI** (MCCS) | monitor-style control | rarely offered by projectors; not used |
| **SNMP** | MIB-II system info; vendor MIBs (Panasonic, NEC, Epson) for status and traps | not used: every status field here comes from the control protocols |
| **Art-Net / sACN DMX** | large-venue Christie, Barco, Panasonic and Epson models map shutter, lens, power and patterns to DMX channels (recalled from manuals) | not used: a lighting desk usually owns those channels; PlayMap stays off them |
| **Crestron / Extron / Q-SYS** | integrators drive the same protocols | PlayMap coexists: per-command connections hold a projector's single control session for milliseconds |
| **RS-232 / HDBaseT passthrough** | the same ASCII/binary protocols on a serial line (Panasonic's serial framing differs: STX/ETX) | only over a TCP-to-serial gateway carrying the TCP framing; serial framing is not implemented |
| **Vendor software** (Panasonic Geometry Manager Pro, Christie Twist, Barco Projector Toolset, Epson Projector Professional Tool, NEC Geometric Correction Tool) | grid warps, blends, colour matching inside the projector | their upload formats are not public — see §5 |

## 5. Can PlayMap set projectors up with their own internal features?

- **Switch internal geometry and blending off — yes, where published.** Panasonic (`GMMI0=+00000`, `EDBI0=+00000`)
  verified; Christie's warp-select off gated until tested on the family. Everywhere else `PrepareForScan` tells the
  operator, in words, to reset keystone/warp on the projector's menu: a scan through the projector's own
  correction maps the correction, not the projector.
- **Push a 4-corner correction — possible on Panasonic, not built.** The DZ21K2-series and RZ570 tables publish
  corner correction (`GMMI0=+00010`, then `VXX:GMFI1…GMFI9` per-corner offsets and `GMFI5/GMFIA` linearity, in
  projector units — ±300 vertical, ±480 horizontal on the DZ21K2). A single flat surface's corner pin could be
  written there; the units are not pixels and need a measured mapping per model. Left for later, gated.
- **Push PlayMap's warp grid — no.** No protocol read here writes a warp grid: Panasonic's grid warps go through
  Geometry Manager Pro's unpublished binary payloads, Christie Twist through Christie's software and web upload,
  Barco's warp files through its own tools and API properties that could not be verified, Epson and NEC through
  their tools. `UploadGeometryAsync` exists in the driver contract and every driver refuses it with the reason.
  PlayMap's own output stage does the warp and the blend — vendor-agnostic, identical on every projector.
- **Set blend widths, white balance — on Panasonic.** Published and implemented (model-dependent); the black-level
  lift is gated because the vendor's table writes its value two different ways.
- **Recall a lens state — yes on Christie (absolute positions), Panasonic (absolute shift/focus, lens memory),
  Epson (lens memory), Barco (steps only).** PlayMap records raw positions with every scan, so a show can return a
  lens to where it was calibrated and check it got there (Christie, Panasonic read back).
- **Turn lens positions into optics — not from vendor data.** No vendor publishes the raw-unit → throw ratio /
  shift-fraction mapping. `LensFromProjector` returns the raw positions and says so; a `LensCalibration` learned
  from solves on the same lens interpolates within its span (never extrapolates) to seed the next solve.

## 6. What PlayMap uses each for

| Need | How |
|---|---|
| Health on the Show Bus | `DeviceManager` polls each link (10 s, staggered), one command in flight per projector, back-off doubling to 60 s while silent; liveness live → stale (2.5 polls, ≥ 6 s) → gone (5 polls, ≥ 12 s); each change raises `StateChanged`; `DeviceState.Light` and `Headline()` feed the context item |
| Finding projectors | `ProjectorDiscovery`: PJLink class 2 search, opt-in TCP sweep of a /24, PJLink identity (INF1/INF2/INFO/NAME/CLSS/SNUM), a probe of the native port, a `DriverSuggestion` |
| Before a scan | `MappingWorkflows.PrepareForScan` — shutter open, freeze off, patterns off, geometry and blend off, lens read and recorded; instructions for what the protocol lacks |
| Black frames | `MappingWorkflows.BlackFrame` — close, settle, capture (only if the shutter closed), open, settle; settle = documented fade (Panasonic) + a margin to measure on site (0.5 s default — no vendor documents a mechanical shutter's time) |
| Which projector is which | `MappingWorkflows.Identify` — the projector's own pattern (Christie grid, Panasonic window, Barco pattern on), else PlayMap's ID card on the output; `EdidInfo.Matches` for output ↔ network pairing |
| Lens | raw read/record (`ReadLensAsync`), recall (`MoveLensAsync`, `RecallLensMemoryAsync`), the prior (`LensFromProjector`) |
| The show | power and input through the same verbs; every result says sent / accepted / observed |

## 7. In the engine

- **Where a link lives: the project.** A link names a host on the venue's network, a credential reference and the
  PlayMap projector and output it is. It is a stage entity (`Stage.Devices`, Core's `DeviceLink`, id prefix `dv`)
  rather than a node setting because it names the project's own projector and output (a node setting would point at
  ids another show does not have), because it is edited, undone and saved with the show like the projector it
  belongs to (the stage — poses, scans — is as particular to the venue as an address is), and because a backup node
  that opens the same show controls the same boxes. Nothing in it is secret: the password lives in the node's own
  credential store under `CredentialRef` (by default the link's id), so the link travels in the file, on the wire and
  in the journal whole, and a node that opens the show elsewhere needs its own entry before a protected projector
  answers. The engine turns a `DeviceLink` into the drivers' `ProjectorLink` (the protocol's word checked).
- **The credential store** (`NodeCredentialStore`) keeps the secrets in the node's keys folder, protected at rest as
  the TLS key is (DPAPI for this user on Windows, an owner-only file elsewhere). `CREDENTIAL SET <ref> [<user>]
  <secret>` fills it from this machine only; no line, journal row, STATE or context carries a secret.
- **`DeviceService`** owns the `DeviceManager` (the one writer of device state), the PJLink UDP socket (bound once,
  shared by discovery and notifications), the discovery and the store; it keeps the manager's links in step with the
  project. STATE's `devices` rows and the context's `device:<id>` items (red for an error fault or a projector gone,
  amber for a warning, staleness or a power transition) come from `States`.
- **The verbs**, verb first as the rest of the vocabulary: `DEVICE LIST`, `DEVICE GET <device>`, `DEVICE ADD <name>
  <protocol> <host[:port]> [PROJECTOR <projector>] [OUTPUT <output>]`, `DEVICE REMOVE <device>`, `DEVICE POWER <device>
  ON|OFF`, `DEVICE SHUTTER <device> OPEN|CLOSE`, `DEVICE INPUT <device> <code>`, `DEVICE FREEZE <device> ON|OFF`,
  `DEVICE PATTERN <device> <name>|OFF`, `DEVICE LENS <device> SHIFTH|SHIFTV|ZOOM|FOCUS|MEMORY [TO|BY] <n>`,
  `DEVICE RAW <device> <line>`, `DEVICE DISCOVER [SWEEP <address>]`, `DEVICE PREPARE <device>`, `DEVICE IDENTIFY
  <device> [<seconds>]`. Power, shutter, input, freeze, patterns, lens, raw lines and PREPARE change what leaves a
  lens and are high risk; the catalogue's risk is per verb, so powering on and opening a shutter are high as well.
  The result's stage is the receipt: `Refused`, `Unreached` and `Rejected` → ERR with the words; `Sent` and
  `Accepted` → OK, not yet applied; `Observed` → applied. `AllowUnverified` is set with `SET <dv…> allowUnverified
  true` by an operator; it is never a default.
- **Scans**: a projector with a link is readied by `PrepareForScan` before its patterns and gives a `BlackFrame`
  (the room with the shutter closed); the scan's notes say what the protocol could not do.
- **In the node**, `DeviceService` is the host extension "devices" (`OpsParts`): one credential store per node, in
  its keys folder, shared with the assistant; the service attaches before the node's doors open and its links
  close after a running scan has stopped. The PJLink UDP port (4352) is bound once; when another program holds it,
  notifications and discovery answers are missed and the journal says why, while commands and polling still go
  over TCP.

## 8. Not done, and why

- Warp-grid upload to any projector (no public format); Panasonic corner-correction push (units need a measured
  mapping); Barco Pulse warp/blend/absolute lens (API reference not read); Epson blend/geometry words (not read).
- Sony ADCP and NEC binary are unverified as a whole; they need one session on a real projector of each family,
  after which their gates can be lifted per command.
- PJLink class 2 search timing and notification-destination rules are recalled; the code accepts notifications
  from known hosts only and makes the search window configurable.
- Serial (RS-232) framings, SNMP, DMX/Art-Net and DisplayID extension blocks are not implemented.
- Christie model/serial (`SST+CONF`) and signal (`SST+SIGN`) answers were seen in a public implementation but not
  their query forms, so they are not asked.

[cl2]: https://pjlink.jbmia.or.jp/english/data_cl2/PJLink_5-1.pdf
[aio]: https://github.com/HEInventions/aiopjlink
[pyp]: https://github.com/gaetano-guerriero/pypjlink
[cpj]: https://github.com/bitfocus/companion-module-pjlink
[cgp]: https://github.com/bitfocus/companion-module-generic-pjlink
[cc]: https://github.com/bitfocus/companion-module-christie-projector
[cp]: https://github.com/bitfocus/companion-module-panasonic-projector
[ntc]: https://github.com/peschuster/ntcontrol-connection
[cb]: https://github.com/bitfocus/companion-module-barco-pulse
[cbd]: https://github.com/bitfocus/companion-module-barco-dp
[ce]: https://github.com/bitfocus/companion-module-epson-businesspj
[eps]: https://github.com/pszafer/epson_projector
[cdp]: https://github.com/bitfocus/companion-module-digitalprojection-projectors
[cv]: https://github.com/bitfocus/companion-module-vivitek-projector

Further references cited by the sources above but not reachable from the build machine: Epson's ESC/VP.net manual
(https://www.epson.com.au/d/pub/epson/techtips/escvp.netmanual_e_f.pdf), Epson's PJLink notes
(https://files.support.epson.com/docid/cpd6/cpd65396/PT/Monitoring/Concepts/pjlink.html — UDP 4352 for search;
AVMT 11/21 unsupported on that model), Fujifilm's PJLink command list
(https://optics.fujifilm.com/projector/en/spec/manual/fp-zuh12000/technical_notes/pjlink/).
