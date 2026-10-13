# Control IO: OSC and OSCQuery, MIDI 1.0/2.0, Art-Net 4 and sACN input, bindings and feedback

*Research for PlayMap round 2, 13 October 2026. Topic key: `control`. The last section, "Design implications for
PlayMap", feeds `design/control.md`.*

## 0. How to read this

- Every fact carries a source tag `[Cn]`. §9 gives each tag's URL, its date and whether I opened it in this session
  (**V**, verified: the text was read) or not (**U**, unverified: known from memory or a secondary mention only).
- *(inference)* and *(design)* mark my own conclusions.
- Local copies (read-only data, nothing executed) are under `scratchpad/ext/`: `oscquery/README.md`, `artnet.txt`
  (the Art-Net 4 PDF as text), `ola/ArtNetPackets.h`, `sacn/*.h`, `drywetmidi/README.md`.
- A loopback probe I wrote (`r2/bench/control/mcast_probe.py`) showed that in this container a UDP socket can join
  `239.255.0.1` on `lo` and receive its own multicast, and that a broadcast to `127.255.255.255:6454` is received.
  So sACN multicast and Art-Net broadcast can be tested for real here. `/dev/snd` does not exist, so no ALSA device
  or sequencer can be opened, although `libasound.so.2` is installed.

## 1. Findings in brief

1. **OSC 1.0** (Wright, 26 March 2002) is the normative text. It defines 32-bit-aligned packets; messages
   (address, type-tag string, arguments) and bundles (`#bundle`, a 64-bit NTP timetag, size-prefixed elements);
   required tags `i f s b`; optional tags `h t d S c r m T F N I [ ]`; pattern matching with `? * [] {}`; and
   timetag semantics. Bundle elements must run in order and atomically; a future timetag must be held until its
   time [C1].
   OSC 1.1 was never published as a specification: the NIME 2009 paper is "the authoritative reflection" [C2].
2. **OSCQuery** (Vidvox proposal) works like this:
   - HTTP/1.1 JSON at the path equal to the OSC address. Query strings select attributes (`?VALUE`, `?HOST_INFO`).
   - Required attributes: `FULL_PATH`, `CONTENTS`, `TYPE`, `HOST_INFO`. Optional ones include `ACCESS` (0–3),
     `VALUE`, `RANGE` (`MIN`, `MAX`, `VALS`), `DESCRIPTION`, `TAGS`, `EXTENDED_TYPE`, `UNIT`, `CRITICAL`,
     `CLIPMODE` and `OVERLOADS`.
   - An optional WebSocket on the same port carries JSON `{"COMMAND","DATA"}` messages (`LISTEN`, `IGNORE`,
     `PATH_CHANGED`, `PATH_RENAMED`, `PATH_REMOVED`, `PATH_ADDED`) and binary OSC frames.
   - Discovery is by zeroconf `_oscjson._tcp`.

   Source: [C3].
3. **Windows MIDI Services reached general availability on 17 Feb 2026, rolling out in-box to Windows 11 retail**
   [C4].
   - WinMM and WinRT MIDI 1.0 on 24H2 and later already go through the MIDI service, which makes them
     **multi-client** [C4][C6].
   - The new app API is `Windows.Devices.Midi2` (UMP endpoints, scheduling, virtual devices). It needs Windows 11
     25H2 (build 26200) or later. The `Microsoft.Windows.Devices.Midi2` preview package is "being dropped entirely in
     Q1 CY 2027" [C6].
   - Two Microsoft pages give different in-box dates: "the last week of November 2026" for 25H2 [C5] and "Q1 CY
     2027" [C6]. Both are plans.
4. **DryWetMIDI** is MIT and well maintained: 8.0.3 on 15 Dec 2025, 8.0.4-prerelease5 on 17 Sep 2026 [C7]. Its
   **device ("Multimedia") API works on Windows and macOS only** [C8], so it cannot be PlayMap's Linux backend.
   RtMidi.Core last shipped in Sep 2023 (MIT, needs native rtmidi) and managed-midi in Dec 2022 [C7]. Using our own
   P/Invoke backends (WinMM, CoreMIDI, ALSA sequencer) is the honest cross-platform path *(inference)*.
5. **Art-Net 4** (revision DP, © 1998–2025):
   - UDP port `0x1936` (6454) and a 15-bit Port-Address (Net 7 bits, Sub-Net 4, Universe 4); Port-Address 0 is
     deprecated.
   - Opcodes: `OpPoll 0x2000`, `OpPollReply 0x2100`, `OpDmx 0x5000`, `OpSync 0x5200`, `OpTimeCode 0x9700`,
     `OpTrigger 0x9900`.
   - A controller polls every 2.5–3 s. **ArtPollReply must be unicast** and sent after a random delay of up to
     1 s.
   - Directed broadcast goes to `2.255.255.255` or `10.255.255.255`, never `255.255.255.255`.
   - A product must apply for an **OEM code**.

   Source: [C9]. The ArtDmx and ArtTimeCode layouts are cross-checked against OLA's header [C10].
6. **sACN (E1.31)**, as ETC's reference library encodes it [C11]:
   - Port 5568. Multicast `0xEFFF0000 | universe` (239.255.hi.lo). Universes 1–63999. Discovery universe 64214,
     sent every 10 s.
   - Priority 0–200 at byte offset 108. Sequence at offset 111. Options at offset 112: Preview `0x80`,
     Stream_Terminated `0x40`, Force_Sync `0x20`. Start code at offset 125.
   - Per-address priority uses start code `0xDD`.
   - Source loss after 2500 ms. A new universe is sampled for 1500 ms. The sync packet is 49 bytes.
7. **Binding practice in the field:**
   - **Ableton** defines the takeover modes **None / Pick-Up / Value Scaling** and four relative encoder codings
     (Signed Bit, Signed Bit 2, Bin Offset, Twos Complement), each with a linear variant, and detects them while
     learning [C12].
   - **Resolume** offers:
     - for MIDI CC: Absolute, Button, Relative (Steps/Step Size, Loop) and Fake Relative;
     - for notes: Toggle, Piano (with Invert), Range, Value and Velocity;
     - feedback per shortcut to the same device by default, colour lookup tables for pads, and an off/on velocity;
     - for OSC: `"+"`, `"-"` and `"*"` relative arguments, `"?"` queries, normalised 0–1 floats with an `"a"`
       absolute form, and output presets.

     Source: [C13][C14].
   - **QLab 5**:
     - **Capture** for MIDI and OSC, with the warning that "any" velocity on Note On double-triggers on
       velocity-0 releases;
     - MSC with a device ID from 0 to 126 and 127 as all-call;
     - OSC access by four-digit passcode with **view/edit/control** permissions;
     - Show Mode disables "editing cue properties using AppleScript and OSC".

     Source: [C15][C16].
   - **MadMapper 6**:
     - right-click **Add Control** or a Learn toggle, with colour by protocol (MIDI green, OSC yellow, DMX purple);
     - a Control List (Cmd/Ctrl+L) and a feedback device for motor faders and LEDs;
     - 14-bit pitch bend;
     - DMX input over Art-Net or sACN;
     - full **OSCQuery** support and "Copy OSC Address";
     - `*` wildcards (`/surfaces/*/opacity 0`);
     - predefined addresses bypass a control's ranges and filters.

     Source: [C17][C18].
   - **Chataigne** separates Actions (conditional, punctual) from Mappings (continuous, filtered) inside a state
     machine [C19].
8. **Feedback conventions:**
   - **Mackie Control:** fader position is 14-bit pitch bend per channel (1–9). Touch notes are 104–112. V-Pots send
     relative CC 16–23 (1 = +1 click, 65 = −1). LED rings take CC 48–55 with mode bits 5–4 and value bits 3–0.
     LED velocity: 0 = off, 1 = blink, 127 = on [C20][C21].
   - **Launchpad X:** Programmer mode is `F0 00 20 29 02 0C 0E 01 F7`. Colour index = note velocity. Channel 1 is
     static, 2 flashing and 3 pulsing, synced to MIDI clock. Velocity-0 Note On is a release [C22].
9. **Patterns** (read at `bcdbf28`, 12 Oct 2026):
   - MIDI arrives as text lines (`NOTE 1 53 127`, `NOTEOFF 1 53`, `CC 1 7 50`, `BEND 1 40`, `PROGRAM 1 3`) with
     1-based channels.
   - A control is bound as a trigger row `match → command`, where `*` carries the value.
   - Axes are coalesced at **20 ms** and quantised to **percent**; edges pass straight through.
   - OSC lives under `/patterns/…` on UDP 9698 with feedback `/patterns/state/…`, gated by pairing or the OSC ALLOW
     list. Bundles are flattened and timetags ignored.
   - Learn is target-first ("MIDI LEARN ▸" in every right-click menu), the last mapping wins and says what it
     replaced, maps live with the show, and lamps are `LAMP ch note velocity`.

   Source: [C23].

## 2. OSC

### 2.1 Wire format (OSC 1.0) [C1] — V

| Item | Rule |
|---|---|
| Atomic types | `int32` (big-endian two's complement), `float32` (IEEE 754 big-endian), OSC-string (ASCII, NUL, padded to 4), OSC-blob (int32 size + bytes + pad), OSC-timetag (64-bit fixed point) |
| Packet | Size always a multiple of 4. On a stream transport, each packet is preceded by an int32 size (1.0). The first byte distinguishes a message (`/`) from a bundle (`#`) |
| Message | Address pattern (OSC-string starting `/`), then the type tag string (starts `,`), then the arguments. "Implementations should be robust in the case of a missing OSC Type Tag String" |
| Standard tags | `i` int32, `f` float32, `s` string, `b` blob |
| Optional tags (must use these letters if used) | `h` int64, `t` timetag, `d` float64, `S` symbol, `c` char, `r` RGBA, `m` 4-byte MIDI (port, status, d1, d2), `T` true, `F` false, `N` nil, `I` infinitum, `[` `]` arrays. "Discard any message whose type tag string contains any unrecognized OSC type tags" |
| Bundle | `"#bundle"` (8 bytes), timetag (8), then elements of int32 size + contents; bundles nest |
| Names | Printable ASCII except space `# * , / ? [ ] { }` |
| Pattern matching | `?` one char; `*` zero or more; `[abc]`, `[a-z]` and `[!…]` (negated); `{foo,bar}` alternatives; same number of parts |
| Timetag | NTP format: seconds since 1900-01-01 (32 bits) plus fraction (32 bits); `0x0000000000000001` means "immediately". A past timetag runs now (unless configured to drop late); a future one must be held. "OSC does not provide any mechanism for clock synchronization" |
| Ordering | A bundle's messages are invoked in packet order, atomically ("in immediate succession as if no other processing took place"). A nested bundle's timetag must be ≥ its parent's |

**OSC 1.1:** "there is no 1.1 'specification' … [the NIME 2009 paper is] the authoritative reflection" [C2] — V. Two
details are from memory **(U)**: 1.1 recommends SLIP (RFC 1055) framing for streams instead of the 1.0 size prefix,
and it adds the `//` path-traversal wildcard. Treat both as optional.

### 2.2 OSCQuery [C3] — V

- **HTTP:**
  - The URL path is the OSC address (`http://host:port/foo/bar` describes `/foo/bar`). The query string names an
    attribute (`?VALUE`); the fragment is ignored.
  - Replies are `application/json`. Errors: **204** (inappropriate, e.g. VALUE when ACCESS = 0), **400** (bad or
    unsupported attribute), **404** (no such node); 408 is generated by the client.
- **Required attributes:** `FULL_PATH`; `CONTENTS` (a container's children, so the root returns the whole tree);
  `TYPE` (the type tag string, without the comma in the examples); and `HOST_INFO`, returned only when asked.
  `HOST_INFO` has `NAME`, `EXTENSIONS` (attribute → bool), `OSC_IP`, `OSC_PORT`, `OSC_TRANSPORT` (`"UDP"` or
  `"TCP"`), `WS_IP` and `WS_PORT`.
- **Optional attributes:**
  - `ACCESS`: 0 none, 1 read, 2 write, 3 read/write.
  - `VALUE`: an array, one item per type tag; an empty object when there is no value yet.
  - `RANGE`: `[{"MIN","MAX","VALS"}]`.
  - `DESCRIPTION`, `TAGS`, `EXTENDED_TYPE`, `UNIT` and `CRITICAL` ("delivery needs to be guaranteed … use a TCP
    connection").
  - `CLIPMODE`: `none`, `low`, `high` or `both`.
  - `OVERLOADS`.
- **WebSocket:** same IP and port unless `WS_*` say otherwise.
  - Text frames carry JSON `{"COMMAND":…, "DATA":…}`.
  - Client to server: `LISTEN <path>` (exact path, not its children) and `IGNORE <path>`.
  - Server to client: `PATH_CHANGED`, `PATH_RENAMED {OLD, NEW}`, `PATH_REMOVED` and `PATH_ADDED`. Multiple
    `PATH_CHANGED` may be coalesced to the common parent.
  - Binary frames are raw OSC packets in both directions.
- **UI conventions:** `f`/`d` → slider; `i`/`h` → slider, toggle when 0–1, or menu when `VALS`; `T`/`F` → toggle;
  `N`/`I` → momentary button; `r` → colour picker; `ff` → 2-D point.
- **Discovery:** `_oscjson._tcp.`
- **Who reads it:** MadMapper 6 serves OSCQuery, and Chataigne's MadMapper module connects to port **8010** [C18]
  (the 8010 figure comes from a third-party page; the OSCQuery support itself is from MadMapper's own docs).
- **.NET library:** `OscQueryLibrary` 2.0.1 (MIT, 11 Sep 2026, net10.0) exists on NuGet [C24], but it is VRChat-
  oriented and I did not read its code (U). Writing our own is small: one JSON tree plus the WS commands
  *(inference)*.

### 2.3 OSC as other show tools expose it

| Tool | Address scheme | Values | Reply and feedback | Security | Source |
|---|---|---|---|---|---|
| Resolume Arena 7 | Fixed tree `/composition/layers/1/clips/1/connect`; absolute and relative (selected layer) variants | Normalised 0–1 floats by default; `"a"` + value for absolute; `"+"`, `"-"`, `"*"` + value for relative | `"?"` → reply on the same address; OSC Output presets ("Output all", per item, wildcard scope "All Layers", custom address) | — | [C14] V |
| MadMapper 6 | Name-based `/surfaces/Quad-1/opacity`, `/media/<name>/…`, `/timelines/Bank-1/conductor/…`, `/master/master_level` | BOOL, FLOAT, INT, STRING, nil; predefined addresses bypass the control's ranges and filters | Input and feedback ports (8000 typical); "Get" | — | [C17][C18] V |
| QLab 5 | `/go`, `/panic`, `/cue/{n}/start` …; UDP 53000, replies on 53001 (from the Companion module study) | — | `/updates 1` subscription (QLab 5) | Passcodes (4 digits) with view/edit/control; Show Mode disables OSC edits | [C15][C16] V; ports from `research/companion.md` |
| Patterns | `/patterns/<verb words>`, a value as the next segment or the first argument | Integer percent, or float 0–1 as fader | `/patterns/state/…` from the STATE JSON; `/patterns/error`, `/patterns/pong` | Paired address, trusted list or OSC ALLOW list; queries open | [C23] V |
| Stagewatch | OSC in, feedback host:port, "bind all" switch | — | Feedback host | Bind to loopback unless "bind all" | `/home/user/jammin808/stagewatch/src/Timer.App/ViewModels/MainViewModel.cs` (read 13 Oct 2026) V |

## 3. MIDI

### 3.1 MIDI 1.0 essentials (U unless marked)

From the MIDI 1.0 specification, from memory.

- Channel voice status bytes: `8n` note off, `9n` note on (velocity 0 = off), `An` poly pressure, `Bn` CC, `Cn`
  program, `Dn` channel pressure, `En` pitch bend (LSB, MSB; 14-bit, centre 8192). The status byte layouts are
  confirmed in [C20] V.
- **14-bit CC:** CC 0–31 are MSBs and CC 32–63 their LSBs. A receiver must cope with an MSB-only sender.
- **NRPN and RPN:** parameter select CC 99/98 (NRPN MSB/LSB) or 101/100 (RPN); data entry CC 6 (MSB) and 38
  (LSB); increment and decrement CC 96 and 97; null RPN 101 = 127, 100 = 127.
- **System messages:**
  - SysEx: `F0 … F7`. Universal non-realtime `7E` and realtime `7F`. Device Inquiry is `F0 7E 7F 06 01 F7`, and the
    Launchpad X answers it [C22] V.
  - MTC quarter frame `F1`; song position `F2`.
  - Clock `F8`, start `FA`, continue `FB`, stop `FC`, active sensing `FE`.
  - Running status must be handled on raw byte streams.
- **MIDI Show Control (MSC):** `F0 7F <device> 02 <command_format> <command> <data…> F7`.
  - Commands: `01` GO, `02` STOP, `03` RESUME, `04` TIMED_GO, `05` LOAD, `06` SET, `07` FIRE, `08` ALL_OFF,
    `09` RESTORE, `0A` RESET, `0B` GO_OFF.
  - Cue numbers are ASCII with `00` delimiters (U).
  - QLab confirms the behaviour: it responds to GO [cue], STOP, RESUME, LOAD, ALL_OFF (panic), STANDBY±,
    SEQUENCE±, RESET; device ID 0–126 and **127 = all call**; categories Audio, Lighting and Video (General) [C15]
    V.
- **MIDI Machine Control (MMC):** `F0 7F <device> 06 <command> F7`, with `01` stop, `02` play, `03` deferred play,
  `04` fast forward, `05` rewind, `06` record strobe, `09` pause and `44` locate (U).

### 3.2 MIDI 2.0 and UMP

- **Specification:** M2-104-UM "UMP Format and MIDI 2.0 Protocol" v1.1.2 [C25] V. The UMP is 32-bit words. Message
  type (MT) nibble and size:

  | MT | Size | Contents |
  |---|---|---|
  | `0x0` | 32 | Utility (groupless) |
  | `0x1` | 32 | System common and real-time |
  | `0x2` | 32 | MIDI 1.0 channel voice |
  | `0x3` | 64 | Data, including SysEx7 |
  | `0x4` | 64 | MIDI 2.0 channel voice |
  | `0x5` | 128 | SysEx8 and mixed data |
  | `0xD` | 128 | Flex data |
  | `0xF` | 128 | UMP stream (groupless) |

  There are **16 groups × 16 channels** (256 channels per connection).
- **Microsoft's UMP primer** [C26] V, with examples:
  - **16-bit velocity and 32-bit CC, pressure and pitch bend.** Note On is `40903C00 C9240000`, CC 7 = 100 is
    `40B00700 C9249249`, pitch bend centre is `80000000`.
  - **RPN and NRPN as one message each** ("Registered/Assignable Controller").
  - Program change with a bank in one message.
  - When MIDI 2.0 messages go to MIDI 1.0 receivers, **Windows down-converts** them: values are scaled to 7 or 14
    bits, Registered/Assignable Controllers become RPN/NRPN CC sequences, and a velocity that would scale to 0 is
    sent as 1.
  - Messages with no MIDI 1.0 equivalent are left out: per-note controllers, relative controllers, SysEx8, flex
    data and UMP stream.
- **Windows MIDI Services:**
  - GA was announced on 17 Feb 2026 [C4] V.
  - It is in-box via controlled feature rollout on Windows 11 24H2, 25H2 and 26H1. It brings multi-client MIDI 1.0,
    built-in loopbacks, MIDI 2.0 and a new USB class driver. "Timestamps and message scheduling are … available to
    apps using the new … SDK." Tools install with `winget install Microsoft.WindowsMIDIServicesSDK` [C4][C27] V.
  - A Korg BLE MIDI driver that is loaded is "not multi-client" [C28] V.
  - The registry must keep `midi` = `wdmaud.drv` and `midi1` = `wdmaud2.drv` [C28] V.
- **API guidance for libraries** [C6] V, read 13 Oct 2026:
  - Target the in-box `Windows.Devices.Midi2`. It needs Windows 11 25H2 (build 26200); read the build with
    `RtlGetVersion`. Check `MidiApi.IsSystemProvided()` and `MidiApi.EnsureServiceAvailable()`, and treat
    `GetCurrentlySelectedApiMode()` → Legacy as a supported customer choice: **fall back to WinMM**.
  - Open **one `MidiEndpointConnection` per endpoint** and refcount it.
  - A "port" is endpoint + group + direction.
  - Use `MidiGroupEndpointListener` for per-group filtering on the WinRT path. The COM extensions give zero-
    allocation multi-message callbacks but no listeners.
  - Windows 11 24H2 leaves support in October 2026; WinMM there already goes through the service.
- **Release notes** [C5] V, read 13 Oct 2026:
  - "The Windows MIDI Services API is locked. The only addition since Preview 10 is `MidiApi.IsSystemProvided()`."
  - NuGet `0.99.92-preview.11`.
  - Transports: Network MIDI 2.0, RTP-MIDI, BLE MIDI 1.0, Basic Loopback and the GM Synth.
  - "Plan is to have all of these transports and the API in Windows 11 25h2 and higher starting during the last
    week of November 2026." Minimum OS for the preview SDK is Windows 11 25H2.
- **macOS and Linux:**
  - CoreMIDI has `MIDIClientCreate`, `MIDIInputPortCreate` (`MIDIPacketList`, deprecated since macOS 11) and
    `MIDIInputPortCreateWithProtocol` (`MIDIEventList`, UMP, block-based). Status from memory (U).
  - The ALSA sequencer (`libasound.so.2`): `snd_seq_open`, `snd_seq_create_simple_port`, `snd_seq_connect_from`,
    `snd_seq_event_input` and `snd_midi_event_decode`; the system announce port gives hot-plug. API names from
    memory (U). The library is present in this container but there is no `/dev/snd/seq` (V, probe).

### 3.3 .NET libraries (NuGet registration API, read 13 Oct 2026) — V

| Package | Latest | Date | Licence | Targets | Notes |
|---|---|---|---|---|---|
| Melanchall.DryWetMidi | 8.0.3 (stable); 8.0.4-prerelease5; 9.0.0-prerelease8 | 15 Dec 2025; 17 Sep 2026; 30 May 2026 | MIT | netstandard2.0, net45; 9.x adds net7/net8 | Device API **Windows and macOS only** [C8] |
| RtMidi.Core | 1.0.53 | 5 Sep 2023 | MIT | netstandard2.0 | Wraps native rtmidi; stale |
| managed-midi | 1.10.1 | 28 Dec 2022 | MIT (licence URL) | — | Stale |
| Microsoft.Windows.Devices.Midi2 | 0.99.92-preview.11 per [C5] | 2026 | "go live" licence; **not redistributable**; dropped Q1 2027 [C6] | — | Use the in-box `Windows.Devices.Midi2` instead |

### 3.4 Controller feedback conventions

| Surface | Input | Feedback | Source |
|---|---|---|---|
| Mackie Control (X-Touch, MCU) | Buttons are note bangs on channel 1 (127 press, 0 release). V-Pot CC 16–23: 1 = +1, 65 = −1 (signed-bit). Fader touch notes 104–112. Faders send 14-bit pitch bend on channels 1–9 | LEDs: same note, velocity 0 off, 1 blink, 127 on. Rings: CC 48–55, bit 6 = centre LED, bits 5–4 = mode (dot, boost/cut, wrap, spread), bits 3–0 = value. Faders are moved by the same pitch bend; touch tells the host to stop driving the motor. LCD via SysEx `F0 00 00 66 14 12 …` | [C20][C21] V |
| Launchpad X | Programmer mode via SysEx `F0 00 20 29 02 0C 0E 01 F7` (Live mode `…0E 00 F7`). Pads send notes (or CC for top and side) | Note velocity = palette index. Channel 1 static, 2 flashing (50 % duty, one beat), 3 pulsing (two beats), synced to MIDI clock (default 120 BPM). Example: `90 0B 05` = bottom-left pad static red. Full RGB via LED SysEx | [C22] V |
| APC40 mkII, APC mini mk2, nanoKONTROL2, MIDI Fighter Twister | Mixed absolute and relative encoders; APC rings by CC type | Pad colours by velocity tables (Resolume ships lookup tables for "most controllers") | [C13] V for Resolume's tables; device details **U** |

## 4. Art-Net 4 [C9] V, layouts [C10] V

### 4.1 Rules

- One UDP port, `0x1936` (6454), as both source and destination.
- Packets start with `ID[8] = "Art-Net\0"`, then the OpCode (Int16, **low byte first**), then ProtVerHi/Lo (14).
- **Port-Address:** 15 bits. Bit 15 = 0. Net is bits 14–8, Sub-Net bits 7–4 and Universe bits 3–0. That gives
  32,768 universes and 128 Nets. "Port-Address = 0 is deprecated" (revision DP).
- **Discovery:**
  - A controller broadcasts `ArtPoll` to the directed broadcast address (`2.255.255.255` primary, `10.255.255.255`
    secondary in the 2.x/10.x schemes) every **2.5–3 s**. The limited broadcast `255.255.255.255` is not
    recommended.
  - Nodes reply with `ArtPollReply` by **unicast** ("ArtPollReply broadcast not allowed") after a random delay of up
    to **1 s**.
  - Targeted mode: ArtPoll Flags bit 5, with TargetPortAddressTop and Bottom.
- **Identity:** an OEM code identifies the product, and manufacturers must apply for one.

### 4.2 Opcodes

| Name | Opcode |
|---|---|
| OpPoll | `0x2000` |
| OpPollReply | `0x2100` |
| OpDiagData | `0x2300` |
| OpCommand | `0x2400` |
| OpDataRequest/Reply | `0x2700`/`0x2800` |
| OpDmx | `0x5000` |
| OpNzs | `0x5100` |
| OpSync | `0x5200` |
| OpAddress | `0x6000` |
| OpInput | `0x7000` |
| OpTodRequest/Data/Control | `0x8000`/`0x8100`/`0x8200` |
| OpRdm | `0x8300` |
| OpRdmSub | `0x8400` |
| OpMedia/Patch/Control/ControlReply | `0x9000`–`0x9300` |
| OpTimeCode | `0x9700` |
| OpTimeSync | `0x9800` |
| OpTrigger | `0x9900` |
| OpDirectory/Reply | `0x9A00`/`0x9B00` |
| OpIpProg/Reply | `0xF800`/`0xF900` |
| OpFirmwareMaster/Reply | `0xF200`/`0xF300` |

### 4.3 Packet layouts

- **ArtDmx** [C10]: ID, OpCode, ProtVer, `Sequence` (0 disables), `Physical`, `SubUni` (low byte of Port-Address),
  `Net` (high 7 bits), `LengthHi`, `Length` (2–512), then `Data[Length]`.
- **ArtTimeCode** [C10]: ID, OpCode, ProtVer, two filler bytes, `Frames`, `Seconds`, `Minutes`, `Hours`, `Type`.
  Type values (Film 24, EBU 25, DF 29.97, SMPTE 30) are from memory (U).
- **ArtSync:** ID, OpCode, ProtVer, Aux1, Aux2. It "force[s] synchronous transfer of ArtDmx packets to a node's
  output" [C9].
- **ArtPollReply** carries:
  - NetSwitch and SubSwitch (bits 14–8 and 7–4 of the Port-Address);
  - Oem;
  - a Status byte whose bits 5–4 give the Port-Address programming authority;
  - SwIn[4] and SwOut[4] (bits 3–0 per port);
  - a Status flag "supports 15-bit Port-Address".

  Field positions are in the spec text (`artnet.txt`, lines 894–1110).

## 5. sACN (ANSI E1.31-2018) — the ETC reference library [C11] V; the standard itself not opened (ESTA download)

### 5.1 Data packet

- **Transport:** UDP **5568**. IPv4 multicast `239.255.<universe hi>.<universe lo>` (`0xefff0000 | universe` in
  `sockets.c`). IPv6 is `FF18::83:00:hi:lo` (U).
- **Universes and slots:** 1–63999. DMX start code `0x00`; per-address priority start code **`0xDD`**; 512 slots.
- **Offsets:**

  | Offset | Field |
  |---|---|
  | 38 | Framing layer starts |
  | 44 | Source name (64 bytes, NUL-terminated) |
  | **108** | Priority (0–200; default 100, U) |
  | 109–110 | Sync address |
  | **111** | Sequence number |
  | **112** | Options |
  | 113–114 | Universe |
  | 115 | DMP layer |
  | **125** | Start code |
  | 126 | Data begins (data header size 126) |

  Option bits: `0x80` Preview, `0x40` Stream_Terminated, `0x20` Force_Synchronization.
- **Vectors:**
  - Framing: data `0x00000002`; extended synchronisation `0x00000001`; extended discovery `0x00000002`.
  - Universe discovery list `0x00000001`.
  - Root vectors are data `0x00000004` and extended `0x00000008` (U, from E1.31).

### 5.2 Timing and other rules

- **Timing:** source loss ("network data loss") at **2500 ms**. The **sampling period** for a new universe is
  **1500 ms**, and the wait for a `0xDD` packet after a new source is 1500 ms. The **discovery universe is 64214**,
  announced every **10 s**, up to 512 universes per page. The sync packet is 49 bytes. Maximum transmission units:
  data packet 638 bytes, discovery packet 1144.
- **Sequence rule:** discard a packet when −20 < (new − last) ≤ 0, as a signed 8-bit difference (U, E1.31 §6.7.2,
  from memory).
- **Merging:** the highest priority wins. Equal priorities from several sources are merged (HTP is common) or
  flagged; ETC's library ships a `dmx_merger` module (`SACN_FEATURE_DMX_MERGER`).
- **Preview data:** receivers may filter it (`kSacnReceiverOptsFilterPreviewData`).

### 5.3 Libraries

| Package | Version | Date | Licence | Targets |
|---|---|---|---|---|
| Haukcode.ArtNet | 4.0.115 | 10 Sep 2026 | MIT | net8/net9 |
| Haukcode.sACN | 2.0.112 | 10 Sep 2026 | MIT | net8/net9/net10 |
| LXProtocols.ArtNet | 3.0.8.2 | 21 Jan 2025 | none stated | — |

Source: [C24] V. Parsing the packets we need takes about 300 lines; a dependency is not needed *(inference)*.

## 6. Binding patterns across tools

| Concern | Ableton Live 12 [C12] | Resolume 7 [C13][C14] | QLab 5 [C15][C16] | MadMapper 6 [C17][C18] | Patterns [C23] | Chataigne [C19] |
|---|---|---|---|---|---|---|
| Learn gesture | Map mode (Ctrl/Cmd+M), click, move | Shortcut mode, click, move; Esc exits | **Capture** button per control or trigger | Right-click **Add Control**, or the Learn toggle with colour per protocol | Right-click **MIDI LEARN ▸** lists the menu's own wire lines; Esc cancels | Module values linked in the inspector |
| Press semantics | Note → button | Toggle, Piano (+Invert), Range, Value, **Velocity** | Note On with `any` velocity; `<`/`>` operators | Notes trigger or toggle | A press binds `NOTE 1 53 *`; a release never binds | Actions (conditions → consequences) |
| Continuous | Absolute with min/max | Absolute (+Invert, Range), Button, Relative (Steps/Step Size, Loop), Fake Relative | Values as `any` or compared | CC to opacity, scale, speed; **14-bit pitch bend** | CC/BEND → `* ` as percent | Mappings with filters |
| Soft takeover | **None / Pick-Up / Value Scaling** | Not documented in the pages read (U) | — | Not stated (U) | Left out on purpose | — |
| Relative encoders | Signed Bit, Signed Bit 2, Bin Offset, Twos Complement (+linear); auto-detect while learning | Relative, Steps, Step Size, Loop | — | — | Refused ("a nudge is not a position") | — |
| Feedback | Native control-surface scripts | Per shortcut, to the same device by default, any device or none; colour lookup tables per controller | — | Feedback device in preferences | `LAMP ch note vel` rows | Module outputs |
| Map storage | With the Set | Application-wide plus composition layer | With the workspace | With the project | With the show | With the project |
| Map overview | Mapping browser | Overlay of the map on the UI | Settings tab | Control List (Cmd+L) | MAPPED CONTROLS, STATE `midi` | Inspector |

**What the field agrees on** (Patterns' MIDI-LEARN study [C23] V, plus the sources above):

1. Target first, control second.
2. A visible armed state with a way out.
3. A press is a press: Note On with velocity 0 is a release, and a release never binds.
4. A sweep binds a value; relative and takeover are per mapping.
5. The last mapping wins, and the tool says so.
6. The map lives with the project, plus an optional house layer.
7. The map can be read as a whole.
8. Feedback goes back to the surface.

## 7. Patterns' vocabulary to align with (read 13 Oct 2026, `bcdbf28`) — V

- `MidiLines.Format`:
  - `NOTE <ch1-16> <note> <vel>`, `NOTEOFF <ch> <note>` (velocity-0 Note On becomes NOTEOFF), `CC <ch> <cc>
    <percent>`, `PROGRAM <ch> <n>`, `BEND <ch> <percent>`, `MIDI <ch> <d1> <d2>`.
  - Trigger form `NOTE 1 53 *`, `CC 1 7 *`, `BEND 1 *`.
  - `LAMP` is the same wire as NOTE (feedback); `BEND` sets a motor fader with 14 bits.
- `MidiBindings`:
  - A row is `Match → Command`. A continuous control learned on `AUDIO LEVEL 50` becomes `AUDIO LEVEL *`; on a pad
    it stays `AUDIO LEVEL 50`.
  - "The last mapping wins and says what it replaced": "NOTE 1 53 on APC40 → LOOK Keynote (was LOOK Walk-in)".
- `MidiCoalescer`:
  - Edges pass now; axes go to slots and are drained every **20 ms** (`SampleMs`).
  - Unchanged values are dropped and the driver thread allocates nothing.
  - `Forget()` on reopen, so a fader moved while unplugged is not ignored.
- `OscMap`/`OscFeedback`/`OscService`:
  - Prefix `/patterns/`; the value rides as the next segment or the first argument.
  - `/patterns/state/…` built from the STATE JSON.
  - `/patterns/error <words>`, `/patterns/pong`, `/patterns/status <json>`.
  - UDP 9698, feedback host:port.
  - The gate is open when there is no token, when loopback is trusted, when the address is paired, or when it is on
    the **OSC ALLOW** list (`OscGate.NotPaired` words). A datagram must never take the port down.
- Verbs: `MIDI LEARN <line>`, `MIDI LEARN OFF`, `MIDI STATUS`; `RECORD ON|OFF` → `ACTION <line>` pushes.
- The Patterns OSC *device* sends `/address` with arguments typed by shape (`3` int, `0.5` float, `true` bool,
  `"quoted"` string); `docs/ENDPOINTS.md`. Patterns can therefore address `/playmap/…` directly.

## 8. Design implications for PlayMap

1. **One bindings layer, no private API.**
   - Every input (a MIDI message, an OSC address, a DMX channel) becomes a normalised *input event*. A binding turns
     it into a **wire line** (a verb, or `SET <id> <path> *`) run through the same router, pairing, risk, Show Mode
     and journal as every other caller.
   - Bindings are project entities (`bd…`), so they can be undone, travel with the show and are read by STATE, the
     context and MCP.
2. **Shape per binding, with the field's best options:**
   - Range in and out, invert, curves (linear, exponential, logarithmic, S, steps).
   - **Takeover None / Pick-Up / Value Scaling** (Ableton's names).
   - **Relative codings** Twos Complement, Bin Offset, Signed Bit, Signed Bit 2 and NRPN increment, with
     auto-detection at learn, step size and acceleration.
   - Button behaviours Trigger, Toggle, Momentary (Piano) with invert, Value, Velocity.
   - **Hold-to-fire for high-risk targets**, which no surveyed tool documents.
3. **Resolution:** keep 7-bit, 14-bit CC, NRPN/RPN and 14-bit pitch bend at full resolution (normalised doubles).
   Patterns quantises to percent. MIDI 2.0's 32-bit values arrive down-converted on WinMM. A UMP-native backend is
   later work.
4. **Coalesce like Patterns, but at frame rate:**
   - Edges dispatch at once.
   - Axes are latest-wins per slot and drained every 10 ms into **one lane batch**, with no allocation on driver
     threads.
   - Continuous edits are journalled once per gesture.
5. **Learn the way the field agrees:**
   - Target first: every right-click menu item's verb and every value inspector gets **Bind…**.
   - An armed state shows in STATE and the context with a timeout and Esc.
   - Releases, clock and active sensing never bind.
   - The last mapping wins with "(was …)".
   - Also a MadMapper-style **bind mode** overlay coloured by protocol.
6. **Feedback:**
   - A ≤ 50 Hz loop per device (Companion's cap) reads immutable snapshots and sends only changes.
   - It uses controller **profiles** as data: Mackie Control (14-bit faders, touch notes, ring modes) and
     Launchpad X (programmer-mode init, palette, flash and pulse channels) from published references; others
     marked unverified until tested on hardware.
   - An echo guard and a motor-touch hold, plus a full refresh on reconnect.
7. **OSC address space mirroring the vocabulary:**
   - `/playmap/<verb words>`; `/playmap/line "<line>"`; `/playmap/set/<id>/<path>`.
   - Friendly name trees (`/playmap/surface/<name>/opacity`).
   - Resolume's `"+"`/`"-"`/`"*"`/`"?"`; MadMapper's `*` wildcards, but **atomic as one edit**.
   - **Bundles atomic, timetags honoured** as `AT` on the show clock: an improvement on Patterns, which flattens
     bundles.
   - Replies on `/playmap/reply`, `/playmap/error`, `/playmap/pong`; state feedback on `/playmap/state/…` to
     subscribers (QLab-style `/playmap/subscribe`).
   - The Patterns-style gate (paired address, trusted list, OSC allow list).
8. **OSCQuery** on its own HTTP+WS port, generated from the catalogue, the project and STATE:
   - `ACCESS`, `RANGE`, `CLIPMODE`, `UNIT`, `DESCRIPTION` and `TAGS` (with risk).
   - **`CRITICAL` on high-risk verbs**.
   - `LISTEN` streaming and `PATH_*` on structure changes.
   - Announced as `_oscjson._tcp` and `_osc._udp`.

   MadMapper is the bar here.
9. **MIDI backends are ours, behind a seam:**
   - **WinMM** first: it works on every Windows and is multi-client through MIDI Services on 24H2 and later.
   - `Windows.Devices.Midi2` later, detected by the three checks, with one connection per endpoint.
   - **ALSA sequencer** on Linux and **CoreMIDI** on macOS, by P/Invoke.
   - A fake for tests.
   - DryWetMIDI is not used (no Linux device API).
10. **DMX input:**
    - Art-Net 4: unicast ArtPollReply with an OEM code (apply), ArtDmx sequence check, **ArtSync** batching,
      ArtTimeCode handed to the timecode package.
    - sACN: priority, sequence, Stream_Terminated, 2.5 s loss, the 1.5 s sampling period, **E1.31 sync**, Preview
      ignored, discovery universe 64214 for the desk's picker.
    - A source allow-list, off until a universe is bound.
11. **Safety and AI:**
    - A binding fires with the authority of the paired person who made it; network inputs pass the source gate
      first.
    - A binding inherits its target's risk (`RiskOf(line)`; carry-over item), so an assistant may propose maps but a
      map to a high-risk verb waits for a person.
    - `CONTROL ENABLE OFF` is the "all control input off" switch for Show Mode.
12. **Portability:**
    - The project names *logical* surfaces (`APC40`); each node maps them to its own port names.
    - A controller on the desk's machine can drive a remote node by **forwarding input events**
      (`CONTROL INJECT`).
    - MSC (GO, STOP, RESUME, LOAD, ALL_OFF) and MMC map to playlist verbs as optional built-in bindings.

## 9. Sources

| Tag | What | URL | Date | Opened |
|---|---|---|---|---|
| C1 | OpenSoundControl Specification 1.0 (Wright) | https://opensoundcontrol.stanford.edu/spec-1_0.html | 26 Mar 2002 | V |
| C2 | OSC 1.1 page (points to the NIME 2009 paper) | https://opensoundcontrol.stanford.edu/spec-1_1.html | undated; paper 2009 | V (paper itself U) |
| C3 | OSCQuery Proposal README (Vidvox) | https://raw.githubusercontent.com/Vidvox/OSCQueryProposal/master/README.md (copy at `ext/oscquery/README.md`) | fetched 13 Oct 2026; commit date not readable (GitHub API refused) | V |
| C4 | "Making music with MIDI just got a real boost in Windows 11" (Windows Experience Blog) | https://blogs.windows.com/windowsexperience/2026/02/17/making-music-with-midi-just-got-a-real-boost-in-windows-11/ | 17 Feb 2026 | V (highlights) |
| C5 | microsoft/MIDI releases | https://github.com/microsoft/MIDI/releases | fetched 13 Oct 2026 (preview 11 notes, update of 3 Sep 2026) | V (highlights) |
| C6 | "Porting a MIDI Library or Framework to Windows MIDI Services" | https://microsoft.github.io/MIDI/kb/porting-midi-libraries/ | fetched 13 Oct 2026 | V |
| C7 | NuGet registration data (DryWetMIDI, RtMidi.Core, managed-midi, Haukcode.*, OscQueryLibrary, Makaretu, Tmds.MDns) | https://api.nuget.org/v3/registration5-gz-semver2/<id>/index.json | read 13 Oct 2026 | V |
| C8 | DryWetMIDI "Supported OS" and "Devices overview" | https://melanchall.github.io/drywetmidi/articles/dev/Supported-OS.html | fetched 13 Oct 2026 | V |
| C9 | Art-Net 4 specification (Artistic Licence), revision DP | https://art-net.org.uk/downloads/art-net.pdf (text at `ext/artnet.txt`, first 60 k characters) | © 1998–2025; fetched 13 Oct 2026 | V (partial: opcodes, addressing, poll rules, ArtPollReply fields) |
| C10 | OLA `plugins/artnet/ArtNetPackets.h` (layout cross-check; LGPL, read only) | https://raw.githubusercontent.com/OpenLightingProject/ola/master/plugins/artnet/ArtNetPackets.h | fetched 13 Oct 2026 | V |
| C11 | ETC sACN library headers and source (`include/sacn/common.h`, `src/sacn/private/pdu.h`, `src/sacn/private/common.h`, `src/sacn/sockets.c`) | https://github.com/ETCLabs/sACN (raw files) | main branch, fetched 13 Oct 2026 | V |
| C12 | Ableton Reference Manual 12, "MIDI and Key Remote Control" (§34.1.3 Takeover, §34.2.4 Relative) | https://www.ableton.com/en/live-manual/12/midi-and-key-remote-control/ | current (fetched 13 Oct 2026) | V |
| C13 | Resolume "MIDI shortcuts" | https://resolume.com/support/en/midi-shortcuts | undated; fetched 13 Oct 2026 | V |
| C14 | Resolume "OSC" | https://resolume.com/support/en/osc | undated; fetched 13 Oct 2026 | V |
| C15 | QLab 5 "Using MIDI and MSC" and "Workspace Settings" | https://qlab.app/docs/v5/networking/using-midi-and-msc/ , https://qlab.app/docs/v5/fundamentals/workspace-settings/ | undated; fetched 13 Oct 2026 | V |
| C16 | QLab 5 "The Workspace" (Show Mode) and "Inspector" (MIDI triggers) | https://qlab.app/docs/v5/fundamentals/workspace/ , https://qlab.app/docs/v5/fundamentals/inspector/ | undated; fetched 13 Oct 2026 | V |
| C17 | MadMapper 6 docs, "Live performance and control" | https://docs.madmapper.com/madmapper/6/11.-live-performance-and-control | undated; fetched 13 Oct 2026 | V |
| C18 | MadMapper 6 "OSC Commands & Channels List"; Chataigne MadMapper module page (OSCQuery port 8010) | https://docs.madmapper.com/madmapper/6/11.-live-performance-and-control/osc-commands-and-channels-list ; https://benkuper.notion.site/MadMapper-b07c0a8528ab4935a0b7422a9ebfd42f | undated; fetched 13 Oct 2026 | V |
| C19 | Chataigne wiki "Getting Started" (state machine, actions vs mappings) | https://github.com/benkuper/Chataigne/wiki/Getting-Started | undated | V (filter list U) |
| C20 | Mackie Control Protocol (htlab.net PDF) | https://htlab.net/computer/protocol/mackie-control/MackieControlProtocol_EN.pdf | undated | V |
| C21 | TouchMCU `doc/mackie_control_protocol.md`; libMackieControl `doc/MackieControl.md` | https://github.com/EMATech/TouchMCU/blob/main/doc/mackie_control_protocol.md ; https://github.com/Do-sth-sharp/libMackieControl/blob/main/doc/MackieControl.md | undated | V |
| C22 | Novation Launchpad X Programmer's Reference Manual | https://fael-downloads-prod.focusrite.com/customer/prod/s3fs-public/downloads/Launchpad%20X%20-%20Programmers%20Reference%20Manual.pdf | undated (current) | V (first 12 k characters) |
| C23 | Patterns: `src/Patterns.Core/Services/MidiBindings.cs`, `MidiCoalescer.cs`, `MidiLines.cs`, `OscMap.cs`, `OscFeedback.cs`, `Osc.cs`, `Resilience.cs` (OscGate), `src/Patterns.Devices/OscService.cs`, `docs/MIDI-LEARN.md`, `docs/ENDPOINTS.md` | /home/user/patterns-svc | `bcdbf28`, 12 Oct 2026 | V |
| C24 | NuGet: OscQueryLibrary 2.0.1 (11 Sep 2026, MIT); Haukcode.ArtNet 4.0.115 and Haukcode.sACN 2.0.112 (10 Sep 2026, MIT); Makaretu.Dns.Multicast.New 0.38.0 (23 Nov 2024, MIT); Tmds.MDns 0.10.1 (28 Sep 2026, MIT) | api.nuget.org registration | read 13 Oct 2026 | V (metadata only; code not read) |
| C25 | M2-104-UM UMP Format and MIDI 2.0 Protocol v1.1.2 | https://amei-music.github.io/midi2.0-docs/amei-pdf/M2-104-UM_v1-1-2_UMP_and_MIDI_2-0_Protocol_Specification.pdf | v1.1.2 (2023–2024); fetched 13 Oct 2026 | V (MT table, groups) |
| C26 | "A primer for UMP data format" (Windows MIDI Services KB) | https://microsoft.github.io/MIDI/kb/ump-data-format-primer/ | fetched 13 Oct 2026 | V |
| C27 | "Get Windows MIDI Services SDK Runtime and Tools" | https://microsoft.github.io/MIDI/get-latest/ | fetched 13 Oct 2026 | V |
| C28 | "Windows MIDI Services 2026 release — known issues and workarounds" | https://devblogs.microsoft.com/windows-music-dev/windows-midi-services-rollout-known-issues-and-workarounds/ | 4 Feb 2026 | V |

Not opened (U): the ESTA E1.31-2018 standard text; the MIDI 1.0 detailed specification; the MSC 1.0 and MMC
specifications; the TouchDesigner and Millumin binding documentation; the Chataigne filter list; the APC40 mkII
protocol PDF.
