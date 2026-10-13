# Work package `control`: OSC and OSCQuery, MIDI, Art-Net and sACN input, and the bindings layer

*Design for PlayMap round 2, 13 October 2026. The research is in `r2/research/control.md`; its tags `[Cn]` are
cited here. Read against `main` at `c12b186`. Roadmap items: R3.1 (bindings) and R3.2 (OSC, MIDI, Art-Net/sACN), plus
the MSC/MMC and timecode inputs that feed R3.7.*

---

## 1. Goal and the bar

**Goal.** Any physical or network control can drive anything PlayMap can do:

- a MIDI pad, fader or encoder;
- an OSC address from TouchOSC, QLab, Chataigne, TouchDesigner, Patterns or Stagewatch;
- a DMX channel from a lighting console, over Art-Net or sACN.

It does this through the **same verbs and `SET` paths** the desk, Patterns and an AI use. Every binding:

- shapes the value (range, curve, takeover, relative encoders);
- feeds back to the surface (LEDs, rings, motor faders, OSC clients);
- is learnt by right-clicking the thing you want to control;
- is a project entity that STATE, the context and MCP can read.

PlayMap's own verbs and STATE are also published as an OSC address space with OSCQuery, so OSC tools can find them
without a manual.

### The bar, and how PlayMap meets or beats it

| Best in the field (research) | PlayMap |
|---|---|
| **Ableton:** takeover None / Pick-Up / Value Scaling; four relative codings with auto-detect [C12] | Same three modes per binding. `Auto` picks Pick-Up for unmotorised absolute faders and None for motorised or OSC. All four codings plus NRPN increment/decrement, auto-detected during learn. Acceleration and a fine modifier on top. |
| **Resolume:** Piano/Toggle/Range/Value/Velocity on notes; Absolute/Relative/Steps on CC; feedback per shortcut with colour tables; OSC `+ - * ?` and normalised floats [C13][C14] | All of those behaviours. OSC `"+"`, `"-"`, `"*"`, `"?"` and `"a"` arguments. **Toggles read the fact from STATE** instead of a private bit, so a toggle never drifts out of step. Feedback LEDs show **receipts**: flashing while *accepted*, solid when *applied/observed*, red when *refused* ("attempts are not facts"). |
| **MadMapper:** right-click Add Control, Learn toggle coloured by protocol, Control List, OSCQuery, `*` wildcards, Copy OSC Address, 14-bit pitch bend, DMX in [C17][C18] | Right-click **Bind ▸** on every object, generated from that object's own `DeskAction` verbs, so every menu item can be bound with no per-view work. A bind-mode overlay coloured MIDI green, OSC yellow, DMX purple. A Bindings drawer. OSCQuery with `RANGE`/`ACCESS`/`CLIPMODE`/`TAGS` and **`CRITICAL` on high-risk verbs**. Wildcards (`/playmap/surfaces/*/opacity 0`) apply as **one atomic edit and one undo step**. Copy OSC Address. 7- and 14-bit CC, NRPN/RPN and 14-bit bend at full resolution. |
| **QLab:** Capture; MSC with device ID and all-call 127; OSC passcodes with view/edit/control; Show Mode blocks OSC edits; "any velocity" double-trigger warning [C15][C16] | Learn ignores releases, clock and active sensing. A velocity-0 Note On is a release, so there is no double trigger. MSC and MMC as optional built-in bindings. The OSC gate is the node's pairing, its trusted list and an OSC allow list. Bindings and OSC lines pass the same Show Mode table as every other transport (`binding` and `osc` transports). |
| **Patterns:** target-first learn, "last mapping wins (was …)", map lives with the show, 20 ms axis coalescing with no allocation, `/patterns/state/…` feedback, OSC ALLOW [C23] | Same rules and the same MIDI words (`NOTE 1 53`, `CC 1 7`, 1-based channels). Patterns' bare rows are accepted on import. Better in four ways: **full resolution** where Patterns uses percent; **10 ms sampling into one lane batch**; **OSC bundles atomic, with timetags honoured as `AT` on the show clock** where Patterns flattens them; and soft takeover and relative encoders, which Patterns left out. |
| **Pixera, Watchout, disguise:** DMX/Art-Net/sACN control of the media server (from the round-1 COMPETITIVE table) | sACN priority, sequence, Stream_Terminated, 2.5 s loss, the 1.5 s sampling period and E1.31 sync. Art-Net with ArtSync, unicast ArtPollReply and ArtTimeCode. A per-universe source allow-list. Optional DMX **personality templates** that generate bindings and a channel sheet for the lighting designer. |
| **Companion** caps module variable updates at 50 Hz (research/companion.md) | Feedback is capped at 50 Hz per control, with dedupe, an echo guard and a motor-touch hold. Companion encoders use the new `NUDGE` verb. |
| Most tools run bindings in the UI process | Bindings run **in the engine (the node)**. They keep working with the desk closed, which is Watchout's lesson. A controller on the desk's machine can drive a remote node by forwarding input events. |
| AI | Bindings are entities and verbs, so an assistant can propose "map my APC40 faders to the eight surfaces". A binding inherits its target's risk (`RiskOf(line)`), so a map to a high-risk verb waits for a person. `CONTROL INJECT` is refused from assistant transports, so an AI cannot press a bound button to get round approval. |

---

## 2. Scope this round

### Must

| # | Item | Acceptance (all automated unless marked) |
|---|---|---|
| M1 | **Model and verbs.** `ControlSet` (surfaces `ct…`, bindings `bd…`) on `Project`. The verbs in §4.2. Generic `ADD binding`/`ADD controlsurface`, `SET bd… shape.takeover "pickUp"`, `REMOVE`, `GET`. | A round-1 project file loads with `control` empty and round-trips byte-identical apart from the new key. `BIND ADD MIDI APC40 CC 1 48 TO SET sf… opacity *` answers `{"id":"bd…",…}`. `CONTROL INJECT MIDI APC40 CC 1 48 0.5` makes `Store.Project.FindSurface(sf).Opacity == 0.5`. Injecting an unbound input is refused with `nothing is bound to MIDI APC40 CC 1 48`. A target with a secret-bearing verb (`CREDENTIAL SET`, `LINK PATTERNS … <token>`), a session verb, `BIND …` or `CONTROL INJECT` is refused with the reason. |
| M2 | **Shaping.** In and out ranges, invert, curves (linear, exponential, logarithmic, S, steps; log-domain targets such as `VIDEO RATE`), takeover (None/PickUp/ValueScaling/Auto), relative codings (TwosComplement, BinaryOffset, SignedBit, SignedBit2, Increment) with step and acceleration, button behaviours (Trigger, Toggle, Momentary + invert, Value, Velocity), axis-as-button with hysteresis, hold-to-fire. | Table tests for every mode (§3.5). Pick-Up with target 0.8: the control moving 0.10 → 0.50 leaves the target unchanged, and the first reading at or past 0.8 tracks 1:1. Value Scaling reaches the end of travel exactly when the control does. Decodes: TwosComplement `0x7F` → −1, BinaryOffset `0x3F` → −1, SignedBit `0x41` → −1 (MCU [C20]), SignedBit2 `0x01` → −1. Hold 600 ms released at 400 ms runs nothing, and the journal says "held 0.4 s of 0.6 s". |
| M3 | **MIDI in and out.** Parser: running status; velocity-0 Note On as a release; 14-bit CC pairing; NRPN/RPN state machine; 14-bit bend; SysEx assembly; MSC/MMC decode; MTC quarter frames to a sink. Allocation-free input path. The `IMidiBackend` seam with **WinMM**, **ALSA sequencer**, **CoreMIDI** and **Fake** backends. Port aliasing (the logical surface name maps to this machine's port), 2 s hot-plug, reconnect with takeover re-armed and a full feedback refresh. | Parser vectors taken from the MCU and Launchpad X documents [C20][C22]. **0 bytes allocated** over 10,000 messages on the input path (`GC.GetAllocatedBytesForCurrentThread`). On this container the ALSA backend loads `libasound.so.2`, fails `snd_seq_open`, and STATE says `"midi":{"backend":"alsa-seq","open":false,"words":"no ALSA sequencer: /dev/snd/seq is missing"}`. WinMM and CoreMIDI compile here. |
| M4 | **OSC server.** UDP 9943; codec 1.0 with bundles, timetags, arrays, `h t d S c r m T F N I`; pattern matching; the address map (§3.7); gate; replies; subscribers and the `/playmap/state/…` mirror at ≤ 20 Hz. Wildcards and bundles are atomic. | Loopback tests: `/playmap/blackout ,i 1` makes STATE `air.blackout` true. An unpaired sender on a token node gets `/playmap/error "not paired — …"` and nothing changes. `/playmap/surfaces/*/opacity ,f 0` is **one undo step and one published snapshot**. A bundle with timetag now + 500 ms carrying `/playmap/playlist/go ,s Show` makes a transport anchored at leader time ± 1 ms (ManualClock). `"?"` replies with the value. A malformed datagram never closes the port (fuzz 10,000 random packets). |
| M5 | **OSCQuery** on TCP 9944 (HTTP + WS): tree, attributes, `HOST_INFO`, `VALUE`, `LISTEN`/`IGNORE`, `PATH_ADDED`/`PATH_REMOVED`/`PATH_RENAMED`/`PATH_CHANGED`. | `GET /` returns a tree in which every `FULL_PATH` answers its own GET. `GET /?HOST_INFO` has `OSC_PORT 9943` and `"OSC_TRANSPORT":"UDP"`. Unsupported attributes answer 400, unknown paths 404, and VALUE on a write-only node 204. A `LISTEN` on `/playmap/surfaces/Lobby/opacity` gets a binary OSC frame within 50 ms of `CONTROL INJECT`. Adding a surface sends `PATH_ADDED`; renaming one sends `PATH_RENAMED`. |
| M6 | **Feedback engine.** Value and lamp feedback; predicates inferred from the target verb; controller profiles **generic**, **mackie-control** and **novation-launchpad-x** (doc-verified); echo guard; motor-touch hold; ≤ 50 Hz per control; receipt colours; refresh on (re)connect; profile init and exit SysEx. | With the fake backend: a desk `SET` on opacity sends the CC once (dedupe); a 1 kHz change stream yields ≤ 50 messages a second. MCU: the fader is driven by 14-bit pitch bend on channel n; while touch note 104 + n − 1 is held, nothing is sent until it is released. Launchpad X: opening the port sends `F0 00 20 29 02 0C 0E 01 F7`; closing sends `…0E 00 F7`. `DEVICE SHUTTER dv… OPEN` bound to a pad flashes while *accepted* and goes solid when the projector reports *observed*. |
| M7 | **Engine integration.** STATE `control`, context items, faults, one journal row per gesture, risk propagation, assistant fences, atomic batch API. | STATE contains `"control":{…}` as the last key (raw-substring test). A context item `control:ct…` is amber "bound to 12 controls, not connected" when the fake port is removed. A 2 s fader sweep journals one row. `BIND ADD … TO BLACKOUT ON` from transport `mcp` is queued as a proposal (high); `CONTROL INJECT` from `mcp`, `assistant` or `approval` is refused. |
| M8 | **DMX input.** Art-Net 4: OpDmx with sequence check, OpSync, OpPoll → **unicast** OpPollReply after a random delay of up to 1 s, OpTimeCode to the timecode sink. sACN: join per bound universe, priority, sequence, Stream_Terminated, 2.5 s loss, 1.5 s sampling, E1.31 sync, Preview ignored. 8-bit and 16-bit channels. Allow-list. **Off until enabled.** | Loopback broadcast and multicast (both confirmed working in this container, `bench/control/mcast_probe.py`): two sACN sources at priorities 100 and 150 → the 150 source wins; terminating it falls back to the 100 source within one packet; silence → "source lost" after 2500 ± 50 ms with the bound value held. ArtPoll → one ArtPollReply sent unicast to the poller. ArtDmx with sequence 5 after 6 is dropped. With ArtSync seen, DMX is held until the sync arrives and applied as one batch. |
| M9 | **Desk.** **Bind ▸** in every `DeskMenu`, generated from its `DeskAction`s; **Bind value…** on inspector sliders; badges on bound controls; Bindings drawer (surfaces, bindings, monitor, network); shape editors in the last-action panel; Esc cancels learn. | App tests: the Bind ▸ items equal the menu's action verbs. Choosing one sends `BIND LEARN <verb line>`. A slider's Bind sends `BIND LEARN SET <id> <path> *`. *Manual:* the "Bind a MIDI control ≤ 3 s" target from docs/UX.md §5.0, timed with the fake surface, then on hardware. |

### Should

| # | Item | Acceptance |
|---|---|---|
| S1 | Bind-mode overlay (key B), Copy OSC address, learn auto-detection of relative coding and 14-bit pairs | Learning a CC that sends 127, 127, 126 picks TwosComplement. A CC n plus CC n+32 within 5 ms becomes `CC14`. |
| S2 | MSC and MMC built-in maps, enabled per surface: MSC GO [cue] → `PLAYLIST GO <focused> [#cue]`, STOP → `VIDEO PAUSE`, RESUME → `VIDEO PLAY`, LOAD → `PLAYLIST CUE`, ALL_OFF → `BLACKOUT ON` (or the showsafety PANIC once it exists). Device ID 0–126 plus 127 all-call [C15]. MIDI clock, SPP, MTC and ArtTimeCode go to `ITimecodeInput`. | Vectors from the QLab command list. |
| S3 | **Windows MIDI Services** backend (`Windows.Devices.Midi2`), detected by Microsoft's three checks: build ≥ 26200, `MidiApi` resolves, `EnsureServiceAvailable()`. One connection per endpoint, refcounted; group listeners. Falls back to WinMM in Legacy mode [C6]. | Compile-only (Windows TFM leg). |
| S4 | The sACN discovery universe (64214) lists sources and universes in the desk picker; per-address priority `0xDD` | Loopback test with a hand-built discovery packet. |
| S5 | DMX personality templates (`media-server-8`: master, playlist select, GO, 8 surface opacities, 4 generator parameters) → bindings plus a channel sheet (CSV/text) | `CONTROL PERSONALITY SACN 7 1 media-server-8` adds 14 bindings in one undo step. |
| S6 | House bindings: a node-level layer in `playmap.bindings.json`, shown with a house badge; a project binding on the same input wins | A test with both layers. |
| S7 | mDNS: `_oscjson._tcp` (9944) and `_osc._udp` (9943), sharing the responder the Companion package needs for `_playmap._tcp` | A query on loopback 224.0.0.251:5353 gets PTR/SRV/TXT/A. |
| S8 | Input forwarding: `CONTROL FORWARD <node>|OFF`. The desk-side engine sends coalesced `CONTROL INJECT` lines over its paired wire to the node the desk drives | A two-engine test on loopback. |

### Later

- A UMP-native MIDI 2.0 path (32-bit values, per-note controllers).
- OSC over TCP (SLIP) and WebSocket *writes*, gated by pairing (a token-bearing OSC transport).
- HID and gamepad inputs; desk keyboard shortcuts as bindings.
- A GDTF or fixture-profile export for lighting desks.
- RTP-MIDI and Network MIDI 2.0 (Windows transports exist [C5]).
- Smoothing filters (they integrate time on the control path; see risk R9).
- Audio-feature inputs (`AUDIO <band>`) once the audio-reactive package lands.

---

## 3. Architecture

### 3.1 Components and data flow

```
 drivers / sockets (their own threads)            control thread "PlayMap control" (AboveNormal)           engine lane
 ┌───────────────────────────────┐   Offer()    ┌───────────────────────────────────────────────┐  RunControlBatchAsync  ┌──────────────┐
 │ MIDI backend callback/poll    │─────────────▶│ InputRouter: BindingTable (frozen, per        │──────────────────────▶│ Route(line)  │
 │ OSC UDP 9943 receive          │─┐            │   project revision)                           │  one lane work item;  │ ProjectStore │
 │ Art-Net 6454 / sACN 5568      │ │  learn tap │ axis slots (latest wins) · edge ring (1024)   │  deferred republish   │ AirState     │
 └───────────────────────────────┘ │  monitor   │ BindingRuntime: shape → takeover → value       │  → one snapshot       │ Journal      │
             ▲                     │  ring (64) │ Learner · gestures → journal                  │                       └──────┬───────┘
             │ feedback sends      │            │ FeedbackEngine (≤ 50 Hz): snapshot + STATE     │◀── StateChanged ───────────┘
             └─────────────────────┼────────────│   facts → per-control values → dedupe → send  │      Snapshot (lock-free read)
                                   │            └───────────────────────────────────────────────┘
                                   └─ direct OSC addresses (/playmap/…) → OscAddressMap → lines → HandleAsync(session "osc")
 OSCQuery (Kestrel 9944, Net) ◀── IOscQuerySource (tree cached per structural signature; values from the FeedbackEngine's cache)
```

- **Driver threads never block, allocate or call the engine.** `InputRouter.Offer(in InputEvent)` does three
  things:
  - looks the input up in the frozen `BindingTable` (`FrozenDictionary<ulong, int[]>` keyed by a packed
    `InputKey`; OSC addresses are interned to keys when the table is built, and pattern-bound OSC inputs go in a
    short matcher list);
  - for an **axis**, writes the value into its slot (`double` + a dirty flag with `Volatile`/`Interlocked`);
  - for an **edge** (press, release, program, trigger), enqueues to a bounded MPSC ring and signals the control
    thread.

  It also writes to the 64-entry monitor ring and, while learning, to the learner's slot.
- **The control thread** runs every 10 ms (`PeriodicTimer`; `Thread.Sleep` is banned) or at once on an edge
  signal:
  1. Drains edges in arrival order, then dirty axis slots (latest wins).
  2. Runs each through `BindingRuntime` (behaviour → in-range → invert → curve → out-range → takeover) and makes the
     binding's line(s).
  3. Hands the batch to `EngineCore.RunControlBatchAsync`.
  4. Every 20 ms runs the `FeedbackEngine`.

  Holding both on one thread means takeover state, toggles and feedback dedupe need no locks.
- **Edges are never delayed by sampling**: an edge wakes the thread and goes out in its own batch.
- **Lines, not private calls.** Every action is a `WireLine` handed to the router with a `SessionInfo`. The
  doctrine "the desk has no private API" applies to bindings too.

### 3.2 Input normalisation (`PlayMap.Core/Control`)

```csharp
public enum InputKind : byte { Press, Release, Axis, Relative, Program, Trigger }

/// <summary>One control event, as a value: what moved (packed), how, and where it is now.</summary>
public readonly record struct InputEvent(ulong Key, InputKind Kind, double Value, int Raw, byte Bits, long MonoNs);
```

- **`Key` packing:** bits 63–60 hold the source (1 MIDI, 2 OSC, 3 Art-Net, 4 sACN); 59–52 the surface index
  (0 = any); 51–48 the message family (note, CC, CC14, NRPN, RPN, bend, program, pressure, MSC, MMC); 47–44 the
  channel; 43–24 the number (note/CC/NRPN 0–16383, DMX universe), 23–0 the channel or slot.
  `InputAddress.Parse(words)` → `Key` and `InputAddress.Format(Key)` → words both live in Core and are tested as
  round-trips.
- **Values.**
  - 7-bit: `Value = raw / 127`.
  - 14-bit CC, NRPN and bend: `raw / 16383`. Bend centre 8192 is 0.5000305; snapped to 0.5 when `|raw − 8192| ≤ 1`.
  - DMX 8-bit: `/255`; 16-bit coarse+fine: `/65535`.
  - OSC `f`/`d`: the value as given; `i`: as given; `T`/`F`: 1/0.
  - Relative: `Value` = signed ticks.
- **Press semantics.** Note On with velocity > 0 is a Press with `Value = velocity / 127`. Note On with velocity 0
  and Note Off are a Release ([C22]; Patterns' rule [C23]). An OSC `T` or a non-zero number on a binding whose
  behaviour is a button is a Press, and `F` or 0 is a Release.

### 3.3 MIDI parsing (`MidiParser`, pure, per port)

- **Running status:** WinMM and ALSA decode deliver whole messages, but CoreMIDI packet lists and raw streams can
  carry running status.
- **System real-time bytes** (`F8`–`FF`) can appear between any data bytes. They go to the clock sink, never to
  learn.
- **14-bit CC** (only when a binding or learn asks for `CC14 ch n`):
  - On CC n (0–31), keep the MSB and arm a 5 ms deadline.
  - On CC n+32, emit `(msb << 7) | lsb` with `Bits = 14`.
  - If the deadline passes without an LSB, emit `msb << 7`.
  - A new MSB clears the LSB (MIDI 1.0 rule, U).
  - Once a pair has been seen on a port, that CC is marked 14-bit and emits on the LSB with no wait.
- **NRPN and RPN:** per-channel state machine.
  - CC 99/98 select an NRPN; CC 101/100 select an RPN; 127/127 is the null RPN.
  - CC 6 is data MSB (emitted after 5 ms if no LSB); CC 38 is data LSB (emitted at once).
  - CC 96/97 are increment and decrement: `Relative` ±(value or 1).
  - The CCs themselves are swallowed while a parameter is selected *and* a binding uses NRPN/RPN on that channel.
    Otherwise they pass as plain CCs, so a controller that happens to send CC 6 still works.
- **SysEx:** assembled into a per-port 4 KiB buffer (WinMM `MIM_LONGDATA` buffers are re-queued with
  `midiInAddBuffer`). MSC is `F0 7F dev 02 fmt cmd data F7`; MMC is `F0 7F dev 06 cmd F7`. Anything else is
  monitor-only or SysEx-pattern bindings (`MIDI <surface> SYSEX F0 00 20 29 * F7`).
- **Allocation-free:** the parser state is struct fields per port; events go out through a `delegate*` or an
  `IInputSink` interface on the router.

### 3.4 MIDI backends (`PlayMap.Control/Midi`, behind a seam)

```csharp
public sealed record MidiPortInfo(string Id, string Name, bool IsInput);

/// <summary>The machine's MIDI as the control side sees it. Callbacks come on the backend's own thread and must
/// not block; the message span is valid only during the call.</summary>
public interface IMidiBackend : IDisposable
{
    string Name { get; }                                   // "winmm", "midi-services", "coremidi", "alsa-seq", "fake"
    string? Unavailable { get; }                           // words when the backend cannot run here, else null
    IReadOnlyList<MidiPortInfo> Ports();
    IMidiInput OpenInput(MidiPortInfo port, IMidiReceiver receiver);
    IMidiOutput OpenOutput(MidiPortInfo port);
    event Action? PortsChanged;                            // hot-plug (polled every 2 s where the OS has no event)
}

public interface IMidiReceiver { void Receive(ReadOnlySpan<byte> message, long monoNs); }
public interface IMidiInput : IDisposable { }
public interface IMidiOutput : IDisposable { bool Send(ReadOnlySpan<byte> message); }
```

| Backend | API (P/Invoke with `[LibraryImport]`) | Notes |
|---|---|---|
| `winmm` (Windows, **must**) | `midiInGetNumDevs`, `midiInGetDevCapsW`, `midiInOpen(CALLBACK_FUNCTION)` with an `[UnmanagedCallersOnly]` callback, `midiInStart`, `MIM_DATA`/`MIM_LONGDATA`, `midiInPrepareHeader`/`midiInAddBuffer`, `midiOutOpen`, `midiOutShortMsg`, `midiOutLongMsg` | Multi-client through Windows MIDI Services on 24H2 and later [C4][C6]. Hot-plug by polling `midiInGetNumDevs` and names every 2 s. The callback copies into the parser; no `midiIn*` call is made from inside it (a WinMM rule). |
| `alsa-seq` (Linux, **must**) | `libasound.so.2`: `snd_seq_open(…, "default", SND_SEQ_OPEN_DUPLEX, SND_SEQ_NONBLOCK)`, `snd_seq_set_client_name`, `snd_seq_create_simple_port`, `snd_seq_connect_from`/`_to`, `snd_seq_query_next_client`/`_port`, `snd_seq_poll_descriptors`, `snd_seq_event_input`, `snd_midi_event_new`/`_decode`/`_encode` (raw bytes, so no struct layouts beyond pointers); the system announce port for hot-plug | Loaded at run time (LGPL library, dynamic linking only, nothing copied). One reader thread `PlayMap midi-in` blocked in `poll()`. |
| `coremidi` (macOS, **must**, compile-only here) | `MIDIClientCreate` (notify proc for hot-plug), `MIDIInputPortCreate` with a `MIDIReadProc` function pointer (`MIDIPacketList`), `MIDIGetNumberOfSources`/`MIDIGetSource`, `MIDIPortConnectSource`, `MIDIOutputPortCreate`, `MIDISend`, `MIDIObjectGetStringProperty(kMIDIPropertyDisplayName)` | The legacy packet-list API is deprecated since macOS 11 (U). A block shim for `MIDIInputPortCreateWithProtocol` is later work. |
| `midi-services` (**should**, S3) | `Windows.Devices.Midi2`: `MidiSession`, `MidiEndpointDeviceWatcher`, `MidiEndpointConnection` (one per endpoint, refcounted), `MidiGroupEndpointListener` | The in-box namespace only, never the preview `Microsoft.…` package [C6]. Needs a Windows TFM leg; compile-only. |
| `fake` | In-memory ports; tests replay byte streams and read sent bytes | Used by every engine and feedback test. |

**Port resolution.**

- A project names a *logical* surface (`APC40`). The node finds its port by
  `NodeSettings.Control.SurfacePorts["APC40"]` (this machine's override, a glob), else by the surface's own `match`
  glob, else by an exact name.
- Two matches → the first, plus an amber context item "two ports match APC40: … — set CONTROL ALIAS".
- An input with no surface (`MIDI CC 1 7`) listens on every enabled port not in `ControlSettings.MidiIgnore`.

### 3.5 Shaping (`BindingMath`, pure; one table test per row)

Order: behaviour → in-range clip → invert → curve → out-range → takeover → format.

| Step | Rule |
|---|---|
| In-range | `x = clamp((v − inMin)/(inMax − inMin), 0, 1)` (dead zones at either end) |
| Invert | `x = 1 − x` |
| Curve | Linear `x`; Exponential `x^k`; Logarithmic `1 − (1 − x)^k`; SCurve `x²(3 − 2x)`; Steps `round(x(n − 1))/(n − 1)`. `k` = `curveAmount` (default 2), `n` = `curveAmount` for Steps |
| Out-range | `y = outMin + x(outMax − outMin)`. `outMin`/`outMax` null → `TargetRanges` (§4.5). Log-domain targets (`VIDEO RATE`) interpolate in log space |
| Takeover | Normalised `c` (control) and `t` (target), ε = 1/2^bits ×2. **None**: `t := c`. **PickUp**: hold until `sign(c_prev − t) ≠ sign(c − t)` or `|c − t| ≤ ε`, then 1:1; the desk shows the direction to move. **ValueScaling**: for Δ = c − c_prev toward edge e (1 or 0), `t := t + Δ(e − t)/(e − c_prev)`, then 1:1 once `|c − t| ≤ ε` [C12]. **Auto**: PickUp for MIDI absolute axes on surfaces whose profile has no motor faders; None for motorised faders, OSC and DMX (the lighting desk's fader is the truth). Takeover re-arms when the target is changed by anyone else (read value ≠ last written by > ε), on reconnect and on `BIND ENABLE` |
| Relative | Decode: TwosComplement `v ≤ 63 ? v : v − 128`; BinaryOffset `v − 64`; SignedBit `(v & 64) != 0 ? −(v & 63) : v & 63`; SignedBit2 `(v & 64) != 0 ? v & 63 : −(v & 63)`; Increment (NRPN 96/97) ±n. Then `Δtarget = ticks × step × (1 + acceleration × (|ticks| − 1)) × (1 + acceleration × burst)`, where `burst` counts ticks in the last 30 ms. `step` 0 means range/128. A fine modifier (a binding with behaviour `Modifier FINE`, held) multiplies by 0.1. Wrap or clamp |
| Buttons | **Trigger**: press → line; release ignored. **Toggle**: press → ON/OFF chosen from the **fact** (the feedback predicate, §3.8), not a private bit. **Momentary**: press → ON form, release → OFF form (`invert` swaps). **Value**: press → `*` = `pressValue`; release → `releaseValue` if set. **Velocity**: press → `*` = shaped velocity |
| Axis as button | Rising through 0.56 is a Press; falling through 0.44 is a Release (DMX GO channels, OSC faders) |
| Hold-to-fire | `holdMs > 0`: the press fires only after being held that long, and the desk and feedback show a "hold" pulse. Default 600 ms when a binding is made to a **high-risk** target from a button. The operator can set 0 |
| Debounce | Presses closer than `debounceMs` (default 30) to the previous press of the same input are dropped |
| Format | `*` → invariant `R`-trimmed to 6 significant digits; `*%` → 0–100 integer; `#*` → 1-based integer index (`PLAYLIST GO show #*` scrolls items) |

### 3.6 Lines into the engine: batches, journal, undo

- **`RunControlBatchAsync(IReadOnlyList<ControlLine> lines, CancellationToken ct)`** (new, `EngineCore.Control.cs`)
  where `record ControlLine(WireLine Line, SessionInfo Session, bool Gesture)`:
  - Runs every lane-routed line in **one lane work item**.
  - Operations verbs (`DEVICE …`) go through `TryOps` as today and are awaited after the lane item.
  - While the batch runs, `Republish()` is **deferred** (a `_batchDepth` counter in `EngineCore.cs`), so the render
    thread sees **one** snapshot for a bundle, a wildcard or a DMX frame with ArtSync.
- **Undo.**
  - Wildcard `SET`s and OSC bundles made only of `SET`s compose into **one `Store.Apply`** with one label (one undo
    step).
  - A mixed bundle uses `ProjectStore.ApplyGroup(label, IReadOnlyList<Func<Project, Project>>)`, a new small store
    method giving one undo entry for several edits.
  - Fader sweeps already coalesce through the store's 750 ms window (`coalesceKey: "set {id} {path}"`).
- **Journal.**
  - Lines flagged `Gesture` (axis-driven `SET`/`NUDGE`/`GENERATOR SET`) skip the per-line journal row.
  - The control thread writes **one row per gesture**, closed after 400 ms idle: `control` · "APC40 CC 1 48 →
    Lobby opacity 0.42 → 0.80 over 1.2 s (12 edits)".
  - Edges are journalled per line like any verb, with `who` = `binding:<id> (<input words>)`.
- **Sessions.**
  - Binding: `new SessionInfo("bind:" + id, "binding", source, false, "<surface> <input words>", Paired: true)`.
    A binding fires with the authority of the paired person who made it. The source gate (OSC allow list, DMX
    allow list) runs *before* a binding is looked up.
  - Direct OSC lines: `Transport "osc"`, `Paired` decided by the OSC gate (§3.7).
- **Rate governor** ("measured, then governed"):
  - The control thread measures lane time per batch (Stopwatch around the work item, returned in the batch
    result).
  - If the 1 s moving average exceeds 8 % of one core, axis sampling drops from 100 Hz to 50 Hz and then 25 Hz.
  - Each step is announced in STATE `control.axisHz` and journalled, and the rate returns when the load falls.
  - A test drives the cause (64 continuous axes with the slow `SetPath` route forced) and asserts the steps.

**The live-field fast path** (`PlayMap.Core/Model/LiveFields.cs`, new):

- `SET` today serialises the whole entity to JSON and back (`ProjectFile.SetPath`). That is fine for a click and
  too costly for 64 faders at 100 Hz.
- `LiveFields` holds compiled typed getters and setters for the paths controls hit most:
  - surface `opacity`, `visible`;
  - layer `opacity`, `visible`, `rect.x|y|w|h`;
  - output `color.*`, `blend.gamma`;
  - generator `params.values.<name>`;
  - playlist `rate`, if the model carries it.
- `SET` uses the fast path when one exists, and the slow path otherwise (same validation, same result).
- Reads used by takeover, `NUDGE` and feedback use `LiveFields.TryRead` and fall back to a cached JSON read.
- A test asserts fast and slow paths give byte-identical project JSON.

### 3.7 OSC (`Core/Control/Osc` pure; `PlayMap.Control/Osc` sockets)

**Codec.** OSC 1.0 exactly as [C1]:

- Big-endian, 4-byte padding.
- Tags `i f s b h t d S c r m T F N I [ ]`; an unknown tag discards the message.
- A missing type tag string is read as no arguments (spec: "robust").
- Bundles nest to depth 8.
- Decoding into a pooled `OscMessage` is allocation-light. Strings allocate only for addresses not in the intern
  table.

**Pattern matching.** `OscPattern` compiles `? * [a-z] [!…] {a,b}` once per pattern into a small matcher (not a
`Regex`), compared part by part.

**Address map** (`OscAddressMap`, pure, round-trip-tested against `VerbCatalog`):

| Address | Becomes | Notes |
|---|---|---|
| `/playmap/line ,s "<line>"` | the line | Universal; the Patterns OSC device can send it |
| `/playmap/<verb words…>[/<word>…] [args]` | `<VERB WORDS> <words…> <args…>` | Longest verb match on the first 1–2 segments, as `VerbCatalog.Match` does. Leftover segments become words, then the arguments. Argument → word: `s` as is (quoted when needed via `WireGrammar.Quote`); `i h` invariant integer; `f d` invariant `R`; `T`/`F` → `ON`/`OFF` when the syntax offers `ON\|OFF`, else `true`/`false`. **No argument** on a verb whose syntax offers `TOGGLE` → `TOGGLE`. Examples: `/playmap/blackout ,i 1` → `BLACKOUT ON`; `/playmap/playlist/go/Show ,i 3` → `PLAYLIST GO Show #3` (an integer in an item slot is `#n`); `/playmap/video/seek ,f 83.5` → `VIDEO SEEK 83.5` |
| `/playmap/set/<id>/<field>/<path…> <value>` | `SET <id> field.path <json>` | One argument → a JSON literal; several → a JSON array. **Patterns allowed in `<id>`** (`sf*`): expanded over matching ids into one edit |
| `/playmap/set/… ,s "+" ,f 0.1` (also `"-"`, `"*"`) | `NUDGE <id> <path> <±delta>` (multiply → absolute `SET` computed from the read value) | Resolume's relative form [C14] |
| `/playmap/set/… ,s "?"` | reply `<same address> <value>` to the sender | Resolume's query [C14] |
| `/playmap/<kinds>/<name>/<field…>` with kinds `surfaces`, `layers/<canvas>`, `outputs`, `generators`, `playlists`, `projectors`, `devices` | `SET`/`GENERATOR SET`/verb, by name | **Plural** kinds never collide with verb words (`SURFACE …`). Names are sanitised: `space # * , / ? [ ] { }` → `_` [C1]; the id always works too. Playlist and device leaves include verbs: `/playmap/playlists/Show/go`, `/next`, `/prev`, `/cue <n>`; `/playmap/devices/Left/shutter ,s OPEN` |
| `/playmap/state/<path…>` | read-only mirror (feedback out; a `"?"` reads) | Built from STATE with a curated list: `light`, `headline`, `air/*`, `video/*`, `playlists/<name>/{state,item,remaining,positionMs,next}`, `outputs/<name>/{open,fps,dropped,light}`, `devices/<name>/{power,shutter,light}`, `sync/{role,offsetMs}`, `scan/{state,progress}`, `control/learning` |
| `/playmap/subscribe [,i port] [,s prefix]` / `/playmap/unsubscribe` | lease 60 s, renewed by any message from that sender | Full dump on subscribe, then diffs at ≤ 20 Hz in bundles of ≤ 1400 bytes (QLab's `/updates` idea [C15]) |
| `/playmap/ping` → `/playmap/pong ,hhh t1 t2 t3` | — | Same clock words as `PING` |
| replies | `/playmap/reply ,ss "OK"\|"ERR" "<payload>"` (,sss with the request id when a second argument of `/line` gives one); refusals also on `/playmap/error ,s` | Patterns' `/patterns/error` [C23] |

**Bundles and timetags.**

- A bundle's messages become lines run as **one atomic batch** (§3.6).
- **Timetag**: NTP → Unix ms (minus 2,208,988,800 s), read against the node's show wall clock
  (`Services.Wall.UnixMs`):
  - "immediately" (`1`) or a past time → run now;
  - future and ≤ 60 s → verbs that take `AT` (`VIDEO PLAY`, `VIDEO RESTART`, `PLAYLIST GO`) get
    `AT <unix ms>`, so the frame is a pure function of show time; all other lines are held by the
    `ControlScheduler` (mono deadline, 1 ms resolution) and run as one batch when due;
  - beyond 60 s → run now, count it, and warn once per sender: "OSC timetags from 10.0.0.9 are 3 min ahead — is
    its clock set?".

  `ControlSettings.OscTimetags` can switch timetags off.

**Gate** (`OscGate`, the Patterns rule [C23]). Mutating lines pass when:

- the node has no token; or
- the sender is loopback and `TrustLoopback`; or
- the sender's address holds a live paired wire/web session (new `INetworkView.PairedAddresses()`, small hunk); or
- the address is in `NodeSettings.TrustedAddresses` or `ControlSettings.OscAllow`.

Queries (`"?"`, `/playmap/state`, `VERBS`, `STATE`) are open. Malformed packets are counted, never thrown. One
datagram can never close the port: a handler fault replies `/playmap/error "fault …"` and the loop goes on.

**Echo and loop guard.** Messages from this node's own addresses and ports are dropped. An OSC feedback target that
sends the same address and value back within 50 ms of our send is treated as echo.

### 3.8 Feedback (`FeedbackEngine`, control thread, ≤ 50 Hz)

- **Sources of truth.**
  - `SET`/`NUDGE`/`GENERATOR SET` targets: the value from `engine.Snapshot` via `LiveFields`.
  - Verb targets: a **predicate** over STATE, compiled once from the binding's `feedback.when` or **inferred** from
    the target line. The STATE JSON is parsed once per `StateChanged` (not per tick) into a reused `JsonDocument`.

  | Target | Inferred predicate |
  |---|---|
  | `BLACKOUT …` | `air.blackout` |
  | `FREEZE …` | `air.frozen` |
  | `PATTERN <k>` | `air.pattern.kind == k` |
  | `PLAYLIST GO <pl> [item]` | `playlists[pl].state == playing` (and the item id) |
  | `PLAYLIST CUE` | `…state == cued` |
  | `VIDEO PLAY` / `VIDEO PAUSE` | `video.playing` / `video.paused` |
  | `SURFACE SHOW <sf> ON` | surface visible |
  | `OUTPUT ENABLE <op> ON` | `outputs[op].open` |
  | `DEVICE SHUTTER <dv> OPEN` | `devices[dv].shutter == open` |
  | `DEVICE POWER <dv> ON` | `devices[dv].power == on` |
  | `CONTROL ENABLE` | `control.enabled` |

- **Receipt states.** The control thread remembers the last reply per binding:
  - `accepted` (Applied = false) → **flash** until the predicate turns true or 10 s pass;
  - refused → **red flash** for 2 s, or the off colour on surfaces without red;
  - applied → solid from the predicate.
- **Per-output dedupe**: each output slot (device, status, data1) remembers its last value. Rate caps: ≤ 50 Hz per
  control, ≤ 500 messages/s per USB device, ≤ 300 for DIN-class profiles, ≤ 20 Hz bundles per OSC subscriber.
- **Echo guard**: no value feedback to a control within 300 ms of input from it.
- **Touch hold**: profiles with touch notes (MCU 104–112 [C20]) suppress fader feedback while touched.
- **Refresh**: on (re)connect, send the profile's init SysEx, then every bound output, then resume diffs. On close,
  send the exit SysEx.
- **Profiles** (`PlayMap.Control/Profiles/*.json`, embedded; data, not code):

```json
{"id":"novation-launchpad-x","name":"Novation Launchpad X","match":["*Launchpad X*","LPX MIDI*"],"verified":"doc",
 "init":["F0 00 20 29 02 0C 0E 01 F7"],"exit":["F0 00 20 29 02 0C 0E 00 F7"],
 "lamp":{"kind":"noteVelocity","staticChannel":1,"flashChannel":2,"pulseChannel":3,
   "palette":{"off":0,"red":5,"amber":9,"yellow":13,"green":21,"cyan":37,"blue":45,"white":3}},
 "faders":{"motor":false}}
```

```json
{"id":"mackie-control","name":"Mackie Control (X-Touch, MCU)","match":["*X-Touch*","*MCU*","*Mackie*"],"verified":"doc",
 "faders":{"motor":true,"kind":"pitchBend","channels":[1,2,3,4,5,6,7,8,9],"touchNotes":[104,105,106,107,108,109,110,111,112]},
 "encoders":{"ccs":[16,17,18,19,20,21,22,23],"relative":"signedBit"},
 "rings":{"ccs":[48,49,50,51,52,53,54,55],"modes":{"dot":0,"boostCut":1,"wrap":2,"spread":3}},
 "lamp":{"kind":"noteVelocity","staticChannel":1,"palette":{"off":0,"blink":1,"on":127}}}
```

- **Verification status of profiles.**
  - Launchpad X: the init SysEx, the channels and `red = 5` are from [C22]. The other palette entries are
    **U** until checked on hardware.
  - Mackie Control: from [C20][C21].
  - `akai-apc40-mk2`, `akai-apc-mini-mk2`, `korg-nanokontrol2` and `djtt-midi-fighter-twister` ship as
    `"verified":"unverified"`; the desk shows "profile not yet checked on hardware".
- **OSC surfaces** (TouchOSC, Chataigne) get value feedback on their own input address, sent to the surface's
  `feedbackTo` (or the sender's address:port), so a TouchOSC fader follows the desk.

### 3.9 Learn

- `BIND LEARN [FROM <surface>|MIDI|OSC|ARTNET|SACN] <target line>`:
  - validates the target (§4.3);
  - arms the `Learner` for 30 s;
  - answers `accepted`;
  - publishes `control.learning` in STATE and an amber context item.
- **What the learner ignores:**
  - Releases (the press is waited for, so a release never binds).
  - Real-time bytes, active sensing, MTC and ArtTimeCode.
  - Any input already bound to *this* target.
  - Axis jitter under 3 steps of the first reading.
  - DMX channels that do not move, so a whole universe at rest never "learns".
- **First qualifying event wins.**
  - A continuous input on a target with `*` → an axis binding.
  - A press on a target without `*` → a Trigger.
  - A press on a target with a value → Value with that value. Patterns' rule: `AUDIO LEVEL 50` learnt on a pad
    stays 50; on a fader it becomes `*` [C23]. The same applies to `SET … 0.5`.
- **Detection during learn** (should):
  - **Relative coding**: the operator is asked to turn left slowly (Ableton's tip [C12]); 127/126 →
    TwosComplement, 63 → BinaryOffset, 65 → SignedBit, 1 → SignedBit2.
  - **CC14**: an LSB partner within 5 ms.
  - **NRPN**: a 99/98 prefix.
- **The last mapping wins and says so**: any binding on the same input (same surface) is replaced in the same
  undo step. The words are Patterns': "CC 1 48 on APC40 → Lobby opacity (was Stage opacity)". Learn makes a binding
  for one input only; `BIND ADD` keeps any others and says "also bound to …", so one fader can drive several targets
  on purpose.
- **Ways out**: `BIND LEARN OFF`, the desk's Esc, and the 30 s timeout. Each one changes nothing and says so.

### 3.10 DMX input (`Core/Control/Dmx` pure; `PlayMap.Control/Dmx` sockets)

**Art-Net 4** [C9][C10]:

- **Socket.** UDP 6454 on `ControlSettings.ArtNetBind` (a NIC address; empty means all), with `ReuseAddress` so
  broadcasts reach co-resident apps on Linux and macOS. On Windows a refused bind is a fault with words naming the
  likely culprit ("another Art-Net program holds port 6454").
- **Header.** `"Art-Net\0"`, opcode low byte first, ProtVer ≥ 14.
- **OpDmx.** Port-Address = `(Net & 0x7F) << 8 | SubUni`. Data is accepted only for universes a binding or monitor
  uses. Sequence: 0 disables the check; otherwise drop when `(seq − last) mod 256` is in 128–255.
- **Merge.** Per universe, per source IP: LTP from the newest source, unless `merge: htp` is set on the surface.
  Two live sources make the context amber.
- **OpSync.** Once seen on a NIC, DMX frames are held until the next ArtSync (or 4 s without one, which reverts to
  immediate) and applied as **one batch**.
- **OpPoll.** Reply with **unicast** ArtPollReply to the poller after a random delay of up to 1 s:
  - ShortName `"PlayMap"`, LongName `"<node name> (PlayMap media server)"`;
  - NodeReport `"#0001 [0000] control input"`;
  - NumPorts up to 4 and PortTypes "output from Art-Net" for the bound universes;
  - SwOut, NetSwitch and SubSwitch from the first four bound universes;
  - Status2 bit 3 set (15-bit Port-Address);
  - Oem `ControlSettings.ArtNetOem` (see R1).
- **OpTimeCode** → `ITimecodeInput.ArtTimeCode(frames, seconds, minutes, hours, type)`.
- **Never transmits ArtDmx.** Output and pixel mapping are roadmap item M4.

**sACN (E1.31)** [C11]:

- **Socket.** UDP 5568. For each bound universe, join `239.255.hi.lo` on `ControlSettings.SacnInterface`. Leave
  when the universe is no longer bound. Unicast is also accepted.
- **Packet checks.** Root preamble 0x0010, ACN PID `"ASC-E1.17\0\0\0"`, root vector data `0x4` or extended `0x8`;
  framing data `0x2`, sync `0x1`, discovery `0x2`.
- **Priority.** Offset 108. Highest priority wins. Equal-priority sources are LTP, or HTP when set on the surface;
  the context is amber either way.
- **Sequence.** Offset 111; discard when −20 < Δ ≤ 0 (U).
- **Options.** Offset 112: Preview `0x80` is ignored unless the surface accepts preview; Stream_Terminated `0x40`
  removes the source **at once**.
- **Loss and sampling.** A source is lost after **2500 ms** of silence. A new universe is sampled for **1500 ms**
  before bound values move, so a console coming up does not flash intermediate values. When a universe has no
  source, bound values **hold** (default) or **release** to the binding's `releaseValue`, per surface.
- **Sync.** When a data packet's sync address is non-zero, the frame is held until the E1.31 sync packet on that
  universe (or Force_Synchronization says otherwise) and applied as one batch.
- **Discovery (S4).** Universe 64214 lists sources and their universes for the desk picker.
- **Per-address priority (S4).** Start code `0xDD`: slots with priority 0 are "not sourced".
- **Values.** `ARTNET <port-address> <ch>` / `SACN <universe> <ch>` give 8 bits. `…<ch>+<fine>` gives 16 bits
  coarse and fine.

**Source gate.**

- `ControlSettings.DmxAllow` lists IPv4 addresses or sACN CIDs. Empty means any source on the bound NIC.
- DMX is **off by default** (`ArtNetEnabled`/`SacnEnabled = false`). Enabling it is a node setting, `CONTROL DMX
  ARTNET|SACN ON|OFF [<nic>]`.

### 3.11 OSCQuery (`PlayMap.Net/Http/OscQueryHost.cs` + `Core/Control/Osc/OscQueryTree.cs`)

**Host.**

- A second Kestrel instance on `ControlSettings.OscQueryPort` (9944), HTTP only, bound like the web port.
- `GET /<osc path>[?ATTR]` returns JSON; the WebSocket is on the same port.
- Data comes through:

```csharp
public interface IOscQuerySource
{
    /// <summary>The tree as UTF-8 JSON and its structural signature (changes only when paths are added, removed or
    /// renamed).</summary>
    (ReadOnlyMemory<byte> Json, long Signature) Tree();
    bool TryValue(string path, out OscValue value);        // for ?VALUE and LISTEN
    event Action<OscPathChange>? PathsChanged;             // PATH_ADDED/REMOVED/RENAMED/CHANGED
    event Action<string, OscValue>? ValueChanged;          // ≤ 30 Hz per path, only for listened paths
}
```

**The tree** (built off-lane on project change, cached per structural signature):

- **Verb methods** under `/playmap/<verb words>`: `TYPE` from the syntax (no args `N`; `ON|OFF|TOGGLE` → `s` with
  `RANGE [{"VALS":["ON","OFF","TOGGLE"]}]`), `ACCESS 2`, `DESCRIPTION` = `VerbSpec.Words`, `TAGS ["verb",
  "risk:high"]` and **`CRITICAL: true` for high-risk verbs**.
- **Entity trees** `/playmap/surfaces/<name>/{opacity f 0–1, visible T, show N, source s}`,
  `/playmap/layers/<canvas>/<layer>/…`, `/playmap/outputs/<name>/color/{brightness,contrast,gamma,…}`,
  `/playmap/generators/<name>/<param>` (`TYPE` and `RANGE` from `GeneratorCatalog.ParamSpec`),
  `/playmap/playlists/<name>/{go,next,prev,cue,state,remaining}`. Each has `ACCESS 3` (or 1 for state),
  `VALUE`, `RANGE`, `CLIPMODE ["both"]` and `UNIT` (`"time.s"` for `remaining`). `TAGS` holds the entity id.
- **`/playmap/state/…`**: read-only (`ACCESS 1`) mirror nodes.
- **`HOST_INFO`**:

  ```json
  {"NAME":"<node name> (PlayMap)","OSC_PORT":9943,"OSC_TRANSPORT":"UDP",
   "EXTENSIONS":{"ACCESS":true,"VALUE":true,"RANGE":true,"DESCRIPTION":true,"TAGS":true,"CLIPMODE":true,"UNIT":true,
   "CRITICAL":true,"LISTEN":true,"PATH_CHANGED":true,"PATH_ADDED":true,"PATH_REMOVED":true,"PATH_RENAMED":true}}
  ```

- **Errors** per [C3]: 204, 400, 404.

**WebSocket.**

- Text `{"COMMAND":"LISTEN"|"IGNORE","DATA":"<path>"}`. Exact path only.
- Values go out as **binary OSC frames** at ≤ 30 Hz per path.
- `PATH_*` messages follow structure changes, coalesced to the common parent within 250 ms.
- OSC packets *received* on the WS are refused this round (writes stay on the gated UDP port; later).

**Security.** OSCQuery is read-only, like `GET /api/state` (open). It shows names and values, never secrets.

**Discovery (S7).** `_oscjson._tcp` on 9944 and `_osc._udp` on 9943, instance name `<node name> (PlayMap)`, TXT
`txtvers=1`.

### 3.12 Performance budgets (all measured by tests on this container; 4 cores, no GPU)

| Path | Budget | How it is measured |
|---|---|---|
| Driver → `Offer` per MIDI message | ≤ 2 µs median, **0 allocations** | Microbenchmark test with 10,000 messages and an allocation counter |
| OSC decode + map, per message | ≤ 3 µs median | Test with 50,000 messages |
| Edge: input → line applied on the lane | p95 ≤ 3 ms (idle lane) | Real threads, fake backend, mono clock stamps |
| Axis: input → snapshot | p95 ≤ 13 ms (10 ms sampling + lane) | Same |
| Lane cost, 64 continuous axes at 100 Hz | ≤ 5 % of one core with LiveFields; the governor steps down above 8 % | Lane time from the batch result |
| Feedback | ≤ 50 Hz per control; ≤ 500 msg/s per USB device | Fake backend counter |
| OSC sustained | 5,000 msg/s on loopback, latest value per address never lost | UDP loopback test |
| OSCQuery tree, 500 entities | ≤ 300 KB; served from cache ≤ 5 ms; rebuilt off-lane ≤ 50 ms | Test |
| DMX | 16 universes at 44 Hz: parse ≤ 1 % CPU | Loopback test |

---

## 4. Spine needs

### 4.1 Model (`src/PlayMap.Core/Model/ControlModel.cs`, new) — backwards compatible

```csharp
namespace PlayMap.Core.Model;

/// <summary>The show's control map: the logical surfaces it names and the bindings from their inputs to wire lines.
/// It lives in the project because a map made for a show belongs to that show; which port a surface is on this
/// machine is the node's (NodeSettings.Control.SurfacePorts). An older file without it reads it as empty.</summary>
public sealed record ControlSet
{
    public static readonly ControlSet Empty = new();
    public ImmutableArray<ControlSurface> Surfaces { get; init; } = [];
    public ImmutableArray<Binding> Bindings { get; init; } = [];
}

/// <param name="Id">"ct" + 8 hex (<see cref="Ids.ControlSurface"/>).</param>
/// <param name="Name">The word input lines use ("APC40"): letters, digits, '-', '_', 1–32 characters, unique.</param>
/// <param name="Kind">"midi", "osc", "artnet" or "sacn".</param>
/// <param name="Match">midi: port-name glob; osc: sender address[:port]; artnet/sacn: source address or CID; "" = any.</param>
/// <param name="Profile">A controller profile id ("mackie-control"); "" = generic.</param>
/// <param name="FeedbackTo">midi: output-port glob ("" = the input's name); osc: host:port ("" = the sender's).</param>
public sealed record ControlSurface(string Id, string Name, string Kind, string Match, string Profile, string FeedbackTo, bool Enabled) : IEntity
{
    /// <summary>Equal-priority DMX sources: "ltp" (default) or "htp"; what a lost DMX source leaves: "hold" or "release".</summary>
    public string Merge { get; init; } = "ltp";
    public string OnLoss { get; init; } = "hold";
    public bool AcceptPreview { get; init; }
    /// <summary>MSC/MMC built-in maps (S2): the MSC device id this surface answers (0–126, 127 all-call), -1 = off.</summary>
    public int MscDevice { get; init; } = -1;
}

/// <param name="Id">"bd" + 8 hex (<see cref="Ids.Binding"/>).</param>
/// <param name="Input">Input words (§4.4): "MIDI APC40 CC 1 48", "OSC /fader/1", "SACN 7 101+102".</param>
/// <param name="Target">A wire line; "*" carries the value ("SET sf… opacity *"), "#*" an item number, "*%" percent.</param>
public sealed record Binding(string Id, string Name, string Input, string Target, BindingShape Shape, BindingFeedback Feedback, bool Enabled) : IEntity;

public enum ControlBehaviour { Auto, Trigger, Toggle, Momentary, Value, Velocity, Absolute, Relative, ModifierFine }
public enum CurveKind { Linear, Exponential, Logarithmic, SCurve, Steps }
public enum TakeoverMode { Auto, None, PickUp, ValueScaling }
public enum RelativeCoding { Auto, TwosComplement, BinaryOffset, SignedBit, SignedBit2, Increment }
public enum FeedbackMode { Auto, Off, Value, Lamp }

public sealed record BindingShape
{
    public static readonly BindingShape Default = new();
    public ControlBehaviour Behaviour { get; init; } = ControlBehaviour.Auto;
    public double InMin { get; init; }
    public double InMax { get; init; } = 1;
    public double? OutMin { get; init; }            // null = the target's own range (TargetRanges)
    public double? OutMax { get; init; }
    public bool Invert { get; init; }
    public CurveKind Curve { get; init; } = CurveKind.Linear;
    public double CurveAmount { get; init; } = 2;
    public TakeoverMode Takeover { get; init; } = TakeoverMode.Auto;
    public RelativeCoding Relative { get; init; } = RelativeCoding.Auto;
    public double Step { get; init; }               // 0 = range / 128 per tick
    public double Acceleration { get; init; } = 0.5;
    public bool Wrap { get; init; }
    public string PressValue { get; init; } = "";
    public string ReleaseValue { get; init; } = "";
    public int HoldMs { get; init; }
    public int DebounceMs { get; init; } = 30;
}

public sealed record BindingFeedback
{
    public static readonly BindingFeedback Default = new();
    public FeedbackMode Mode { get; init; } = FeedbackMode.Auto;
    public string To { get; init; } = "";           // "" = the same control; else output words ("MIDI APC40 NOTE 1 53", "OSC 10.0.0.9:9000 /led/1")
    public string When { get; init; } = "";         // "" = inferred from the target; else a STATE predicate ("air.blackout")
    public string On { get; init; } = "";           // palette name, "vel:21" or "#ff8000"
    public string Off { get; init; } = "";
}
```

**Changes to existing model types** (small hunks):

- `Project`: `public ControlSet Control { get; init; } = ControlSet.Empty;`. This is an init property, as
  `Stage.Devices` is, so no positional caller changes. JSON key `"control"`, camelCase. Enums are camelCase strings
  (the project's `JsonStringEnumConverter`).
- `Project.AllIds()` yields surface and binding ids.
- `Ids`: `Binding = "bd"`, `ControlSurface = "ct"` (neither prefix is in use today).
- **Schema stays 1**: the key is optional, and readers of older builds ignore unknown properties. Confirm when
  format versioning (R4.6) lands.
- `EngineCore.Edits.Locate` gains `binding` and `controlsurface`; `Add` gains both kinds.
- `Validate`:
  - parses `Input` with `InputAddress.TryParse`;
  - parses `Target` with `TargetLine.TryParse` (§4.3);
  - checks that surface names are unique;
  - checks that a binding's surface name exists (or none is given).

**Example JSON.**

```json
"control":{"surfaces":[{"id":"ct1a2b3c4d","name":"APC40","kind":"midi","match":"APC40 mkII*","profile":"akai-apc40-mk2","feedbackTo":"","enabled":true,"merge":"ltp","onLoss":"hold","acceptPreview":false,"mscDevice":-1}],
 "bindings":[{"id":"bd5e6f7a8b","name":"","input":"MIDI APC40 CC 1 48","target":"SET sf0a1b2c3d opacity *","shape":{"takeover":"pickUp"},"feedback":{},"enabled":true}]}
```

### 4.2 Verbs (`VerbCatalog`: new `VerbCategory.Control`)

| Verb | Syntax | Risk | Mutates | Reply |
|---|---|---|---|---|
| `CONTROL STATUS` | `CONTROL STATUS` | Low | no | STATE's `control` object |
| `CONTROL PORTS` | `CONTROL PORTS` | Low | no | `{"backend":"winmm","inputs":[{"id","name","surface","open"}],"outputs":[…],"osc":{"port","open"},"artnet":{…},"sacn":{…}}` |
| `CONTROL MONITOR` | `CONTROL MONITOR [<n 1-64>]` | Low | no | `[{"at":unixms,"input":"MIDI APC40 CC 1 48","raw":"B0 30 35","value":0.4173,"binding":"bd…"\|null,"source":"APC40 mkII"}]` |
| `CONTROL PROFILES` | `CONTROL PROFILES` | Low | no | `[{"id","name","verified"}]` |
| `CONTROL ENABLE` | `CONTROL ENABLE ON\|OFF\|TOGGLE` | Review | yes | `OK` effect "control input off — 24 bindings paused"; "already on" is refused |
| `CONTROL SURFACE ADD` | `CONTROL SURFACE ADD <name> MIDI\|OSC\|ARTNET\|SACN [<match>] [PROFILE <id>]` | Review | yes | `{"id":"ct…"}` |
| `CONTROL ALIAS` | `CONTROL ALIAS <surface> <port glob>\|OFF` (this machine; NodeSettings) | Review | yes | effect "APC40 is 'USB MIDI 2' on this machine" |
| `CONTROL FEEDBACK` | `CONTROL FEEDBACK ADD\|REMOVE <host:port>` (OSC state targets; NodeSettings) | Review | yes | effect |
| `CONTROL DMX` | `CONTROL DMX ARTNET\|SACN ON\|OFF [<nic address>]` (NodeSettings) | Review | yes | effect, or a refusal with the port's words |
| `CONTROL INJECT` | `CONTROL INJECT <input words> <value>` | Review; **refused from `assistant`, `mcp` and `approval` transports** | yes | `{"fired":[{"binding":"bd…","line":"SET … 0.5","ok":true,"reply":"…"}]}`; nothing bound is refused |
| `CONTROL FORWARD` (S8) | `CONTROL FORWARD <node>\|OFF` | Review | yes | effect |
| `CONTROL PERSONALITY` (S5) | `CONTROL PERSONALITY ARTNET\|SACN <universe> <start> <template>` | Review | yes | `{"bindings":["bd…"],"sheet":"ch1 master …"}` |
| `BIND LIST` | `BIND LIST [<surface>\|<entity id>]` | Low | no | `[{"id","input","target","words","enabled","state":"live\|waiting\|unplugged\|off","takeover":"up\|down\|null","value":0.42}]` |
| `BIND ADD` | `BIND ADD <input words> TO <target line>` or `BIND ADD {json}` | **max(Review, RiskOf(target))** | yes | `{"id":"bd…","words":"CC 1 48 on APC40 → Lobby opacity","alsoBound":["bd…"]}` |
| `BIND LEARN` | `BIND LEARN [FROM <surface>\|MIDI\|OSC\|ARTNET\|SACN] <target line>` · `BIND LEARN OFF` | **max(Review, RiskOf(target))**; OFF Low | yes | `OK {"stage":"accepted","effect":"waiting for a control for Lobby opacity (30 s)","until":unixms}`; the outcome is observed in STATE (`control.learning` → null, `control.last.binding`) and the journal |
| `BIND FORGET` | `BIND FORGET <target line>\|<input words>` | Review | yes | effect "2 bindings forgotten"; none is refused |
| `BIND ENABLE` | `BIND ENABLE <binding>\|<surface> ON\|OFF` | Review | yes | effect |
| `NUDGE` | `NUDGE <id> <field.path> <±delta>` | Review | yes | effect "Lobby opacity 0.40 → 0.45"; refused at a bound: "Lobby opacity is already at its top (1)" |

- **`RiskOf(WireLine)`** (new in `VerbCatalog`, also the carry-over's "per-argument risk"):
  - returns the verb's risk;
  - for `BIND ADD/LEARN` and `CONTROL PERSONALITY`, the maximum of that and the targets' risk;
  - `CommandGate.RiskOf(string)` in Assistant delegates to it, so the MCP approval path sees the real risk.
- **Show Mode:** the showsafety package's armed table decides for transports `binding` and `osc`. The proposal:
  bindings keep firing when armed (they are the operator's instruments), and `BIND …` and `CONTROL SURFACE|ALIAS|DMX`
  are refused when armed unless blind.
- **Routing:**
  - `EngineCore.Router.cs` gains one arm: `"CONTROL …" or "BIND …" or "NUDGE" => Control(spec, args, line,
    session)`.
  - `CONTROL PORTS`, `MONITOR`, `INJECT`, `ALIAS`, `DMX`, `FEEDBACK` and `BIND LEARN` need the edge; they are
    answered off-lane by `Services.Control` (the `TryOps` pattern), and any project edit still goes through the
    lane.

### 4.3 Target lines (`TargetLine`, pure)

- **Parsing.** Parse with `WireGrammar.TryParse`, then `VerbCatalog.Match`; there must be a match.
  - Placeholders are whole words only: `*`, `*%`, `#*`, at most one per line. A placeholder inside JSON is refused
    ("a value can ride only as a whole word").
- **Refused targets:**
  - session verbs (`HELLO NONCE AUTH AUTH-PROOF PING SUBSCRIBE STATE CONTEXT HELP VERBS`);
  - every verb with `SecretFrom` (no secret is ever stored in a binding);
  - `CREDENTIAL *`, `APPROVE`, `REJECT`, `ASK` (a person's words);
  - `PAIRING NEW`;
  - `BIND *`, `CONTROL INJECT`, `CONTROL FORWARD` (no recursion);
  - queries (they would change nothing; "a verb that would change nothing is refused").
- **Inferences.** `TargetLine` exposes `Spec`, `Risk`, `HasValue`, `FieldTarget` (`SET`/`NUDGE`/`GENERATOR SET` id
  and path), `SwitchForms` (ON/OFF words when the syntax has them) and `InferredPredicate`.

### 4.4 Input words (`InputAddress`, pure; round-trips with the packed key)

```
MIDI [<surface>] NOTE <ch 1-16> <note 0-127>        · MIDI [<surface>] CC <ch> <0-127>   · MIDI [<surface>] CC14 <ch> <0-31>
MIDI [<surface>] NRPN <ch> <0-16383>                 · MIDI [<surface>] RPN <ch> <0-16383> · MIDI [<surface>] BEND <ch>
MIDI [<surface>] PROGRAM <ch> <0-127>                · MIDI [<surface>] PRESSURE <ch>      · MIDI [<surface>] POLYPRESSURE <ch> <note>
MIDI [<surface>] SYSEX <hex with * wildcards>        · MIDI [<surface>] MSC <dev> <GO|STOP|RESUME|LOAD|ALL_OFF|…> · MIDI [<surface>] MMC <dev> <PLAY|STOP|…>
OSC [<surface>] <address pattern> [ARG <n>]
ARTNET [<surface>] <port-address 1-32767> <channel 1-512>[+<fine>]
SACN [<surface>] <universe 1-63999> <channel 1-512>[+<fine>]
```

Patterns' bare rows (`NOTE 1 53 *`, `CC 1 7 *`, `BEND 1 *`) are accepted on import as `MIDI NOTE 1 53` and so on
[C23].

### 4.5 `TargetRanges` (pure table; GeneratorCatalog supplies generator parameters)

| Target | Range | Notes |
|---|---|---|
| surface/layer `opacity` | 0–1 | |
| `rect.x\|y\|w\|h` | 0 … canvas size | |
| output `color.brightness\|contrast\|gainR\|gainG\|gainB` | 0–2 | |
| `color.gamma` | 0.2–5 | log |
| `color.lift` | −0.5–0.5 | |
| `blend.gamma` | 0.5–4 | |
| `VIDEO RATE *` | 0.05–20 | log; centre detent at 1.0 for 7-bit faders: 62–65 → 1.0 |
| `VIDEO SEEK *` | 0 … clip length (s) | scrub |
| generator parameters | `ParamSpec.Min/Max`; integers stepped; choices → Steps over `Choices` | |
| unknown numeric field | 0–1, with a desk warning "no known range — set OUT" | |

### 4.6 STATE and context

**STATE**: new last key, `control`, written by `e.WriteControlState(w)` after `e.WriteOpsState(w)`:

```json
"control":{"enabled":true,"axisHz":100,
 "osc":{"port":9943,"open":true,"queryPort":9944,"queryOpen":true,"senders":[{"address":"10.0.0.9","paired":true,"lastAt":1760101234000}],
   "subscribers":1,"feedback":["10.0.0.9:9000"],"refused":0,"malformed":0},
 "midi":{"backend":"winmm","open":true,"words":"","inputs":[{"name":"APC40 mkII","surface":"ct1a2b3c4d","open":true}],"outputs":[{"name":"APC40 mkII","surface":"ct1a2b3c4d","open":true}]},
 "dmx":{"artnet":{"enabled":false},"sacn":{"enabled":true,"open":true,"interface":"10.0.0.5",
   "universes":[{"universe":7,"sources":[{"name":"ETC Eos","address":"10.0.0.20","priority":100}],"lost":false,"sync":false}]}},
 "surfaces":[{"id":"ct1a2b3c4d","name":"APC40","kind":"midi","connected":true,"port":"APC40 mkII","profile":"akai-apc40-mk2","verified":false,"bindings":12,"light":"green"}],
 "bindings":{"count":24,"enabled":22,"waiting":1,"unplugged":2},
 "learning":null,
 "last":{"input":"MIDI APC40 CC 1 48","value":0.4173,"binding":"bd5e6f7a8b","at":1760101234567}}
```

- `learning` while armed: `{"target":"SET sf… opacity *","words":"Lobby opacity","until":1760101264567,"from":"MIDI"}`.
- `last` moves STATE's revision at most 5 times a second (`FactsChanged` throttled), so a fader sweep does not
  flood STATE pushes. The desk's monitor polls `CONTROL MONITOR` at 10 Hz while it is open.
- **Faults:** `control` parts ("osc: port 9943 refused — …", "oscquery: …", "artnet: …", "sacn: …", "midi: …")
  join STATE `faults` through a new `ExtensionSetup.AddFaults(INodeFaults)` init hook that `HostFaults`
  concatenates. This is a small hunk in `EngineExtension.cs` and `EngineHost.cs`.

**Context** (`AddControlContext(items, links)`, called from `ContextBuilder`):

- `control:<ct>`, kind `control`, label = surface name:
  - **green**: connected and bound;
  - **amber**: "bound to 12 controls, not connected" / "two sACN sources at priority 100 on universe 7" / "profile
    not yet checked on hardware" (only while not connected) / "two ports match APC40";
  - **red**: its port or socket was refused while bindings need it;
  - **grey**: no bindings.
- `control:learning`, amber: "waiting for a control for Lobby opacity (24 s)".
- `control:off`, amber: "control input is off — 24 bindings paused".
- **Links**: `control:<ct>` → `surface:<sf>` / `playlist:<pl>` / `device:<dv>`, kind `controls`.

### 4.7 Seams and hooks (C#)

```csharp
// PlayMap.Engine/EngineServices.Control.cs (new; the Ops pattern)
public sealed partial record EngineServices
{
    /// <summary>Control input and feedback (MIDI, OSC, DMX, bindings); null leaves BIND/CONTROL verbs refusing
    /// with the reason.</summary>
    public PlayMap.Control.ControlService? Control { get; init; }
}

// PlayMap.Engine/Host/EngineParts.cs (hunk): a third partial hook, so the control side plugs in without editing Ops'
static partial void AddControlExtensions(List<EngineExtensionFactory> extensions);

// PlayMap.Engine/EngineCore.Control.cs (new)
public Task<ControlBatchResult> RunControlBatchAsync(IReadOnlyList<ControlLine> lines, BatchOptions options, CancellationToken ct);
public sealed record ControlLine(WireLine Line, SessionInfo Session, bool Gesture);
public sealed record BatchOptions(bool Atomic = true, bool OneUndo = false, string? UndoLabel = null);
public sealed record ControlBatchResult(IReadOnlyList<CommandReply> Replies, double LaneMs);

// PlayMap.Core/Model/ProjectStore.cs (hunk): several edits, one undo entry, one Changed
public EditResult ApplyGroup(string label, IReadOnlyList<Func<Project, Project>> edits);

// PlayMap.Core/Wire/VerbCatalog.cs (hunk)
public static VerbRisk RiskOf(WireLine line);

// PlayMap.Engine/Seams.cs (hunk)
public interface INetworkView { /* … */ IReadOnlyCollection<string> PairedAddresses(); }   // OSC gate

// PlayMap.Core/Control/ITimecodeInput.cs (new; consumed by the timecode package)
public interface ITimecodeInput
{
    void ArtTimeCode(int frames, int seconds, int minutes, int hours, int type, string source, long monoNs);
    void MtcQuarterFrame(int piece, int value, string port, long monoNs);
    void MidiClock(byte status, string port, long monoNs);      // F8 FA FB FC; F2 SPP via MidiSongPosition
}
```

`PlayMap.Control.ControlService` implements `IOscQuerySource` and `INodeFaults`, and owns:

- `IMidiBackend` (chosen by OS; `ControlServiceOptions.Midi` overrides it for tests);
- `OscServer`, `ArtNetReceiver`, `SacnReceiver`;
- the control thread;
- `Attach(EngineCore)` → `IAsyncDisposable`.

### 4.8 NodeSettings (`src/PlayMap.Core/Bus/ControlSettings.cs`, new; one init property on `NodeSettings`)

```csharp
public sealed record ControlSettings
{
    public static readonly ControlSettings Default = new();
    public bool InputEnabled { get; init; } = true;             // CONTROL ENABLE, kept across restarts
    public bool OscEnabled { get; init; } = true;
    public int OscPort { get; init; } = 9943;                   // UDP
    public string OscBind { get; init; } = "";                  // "" = Bind of the node; "127.0.0.1" = this machine only
    public ImmutableArray<string> OscAllow { get; init; } = [];
    public ImmutableArray<string> OscFeedback { get; init; } = [];   // host:port state targets
    public bool OscTimetags { get; init; } = true;
    public bool OscQueryEnabled { get; init; } = true;
    public int OscQueryPort { get; init; } = 9944;              // TCP, HTTP + WS
    public bool MidiEnabled { get; init; } = true;
    public string MidiBackend { get; init; } = "";              // "" = the OS default; "winmm" | "midi-services" | "coremidi" | "alsa-seq"
    public ImmutableArray<string> MidiIgnore { get; init; } = [];
    public ImmutableDictionary<string, string> SurfacePorts { get; init; } = ImmutableDictionary<string, string>.Empty;
    public bool ArtNetEnabled { get; init; }
    public string ArtNetBind { get; init; } = "";
    public int ArtNetOem { get; init; } = 0x00FF;               // until Artistic Licence assigns ours (R1)
    public bool SacnEnabled { get; init; }
    public string SacnInterface { get; init; } = "";
    public ImmutableArray<string> DmxAllow { get; init; } = [];
    public string ForwardTo { get; init; } = "";                // S8
    public int AxisHz { get; init; } = 100;
    public int FeedbackHz { get; init; } = 50;
}
// NodeSettings (Pairing.cs hunk): public ControlSettings Control { get; init; } = ControlSettings.Default;
```

**Ports**: 9943/udp OSC and 9944/tcp OSCQuery, beside 9940 (web), 9941 (wire) and 9942 (sync). They clash with
neither Patterns (9696–9700) nor Stagewatch (9924). Add `ShowBusConstants.PlayMapOscPort` and `PlayMapOscQueryPort`.

---

## 5. Files

### New

| Path | What |
|---|---|
| `src/PlayMap.Core/Model/ControlModel.cs` | `ControlSet`, `ControlSurface`, `Binding`, `BindingShape`, `BindingFeedback`, enums |
| `src/PlayMap.Core/Model/LiveFields.cs` | Typed fast getters and setters for hot `SET` paths |
| `src/PlayMap.Core/Bus/ControlSettings.cs` | The node's control settings |
| `src/PlayMap.Core/Control/InputAddress.cs`, `InputEvent.cs` | Input words ↔ packed key; the event struct |
| `src/PlayMap.Core/Control/TargetLine.cs`, `TargetRanges.cs`, `BindingMath.cs`, `FeedbackPredicate.cs` | Target parsing, ranges, shaping, predicates |
| `src/PlayMap.Core/Control/MidiParser.cs`, `MidiWords.cs` | Bytes → events; MSC/MMC; Patterns' row import |
| `src/PlayMap.Core/Control/ITimecodeInput.cs` | Seam for the timecode package |
| `src/PlayMap.Core/Control/Osc/OscCodec.cs`, `OscPattern.cs`, `OscTime.cs`, `OscAddressMap.cs`, `OscQueryTree.cs`, `IOscQuerySource.cs` | OSC, pure |
| `src/PlayMap.Core/Control/Dmx/ArtNetPacket.cs`, `SacnPacket.cs`, `DmxUniverseMerge.cs` | DMX, pure |
| `src/PlayMap.Control/PlayMap.Control.csproj` | New edge module: references Core only; `AllowUnsafeBlocks` for P/Invoke |
| `src/PlayMap.Control/ControlService.cs`, `InputRouter.cs`, `ControlThread.cs`, `BindingRuntime.cs`, `Learner.cs`, `FeedbackEngine.cs`, `ControlScheduler.cs`, `Profiles.cs` + `Profiles/*.json` | Runtime |
| `src/PlayMap.Control/Osc/OscServer.cs`, `OscGate.cs`, `OscSubscribers.cs` | Sockets |
| `src/PlayMap.Control/Dmx/ArtNetReceiver.cs`, `SacnReceiver.cs` | Sockets |
| `src/PlayMap.Control/Midi/IMidiBackend.cs`, `WinMmBackend.cs`, `AlsaSeqBackend.cs`, `CoreMidiBackend.cs`, `FakeMidiBackend.cs`, `MidiPorts.cs` (+ `MidiServicesBackend.cs` in S3 behind a Windows TFM) | MIDI backends |
| `src/PlayMap.Net/Http/OscQueryHost.cs` | Kestrel on 9944, HTTP + WS |
| `src/PlayMap.Net/Bus/MdnsAnnouncer.cs` (S7, unless the Companion package lands it first) | RFC 6762/6763 responder: PTR, SRV, TXT, A |
| `src/PlayMap.Engine/EngineCore.Control.cs` | Verbs, batches, STATE row, context items |
| `src/PlayMap.Engine/EngineServices.Control.cs` | `EngineServices.Control` |
| `src/PlayMap.Engine/ControlParts.cs` | The extension factory: makes `ControlService`, starts OSCQuery through `NetworkHost` |
| `src/PlayMap.Engine/Host/EngineParts.Control.cs` | Implements `AddControlExtensions` |
| `src/PlayMap.App/ViewModels/BindingsViewModel.cs`, `src/PlayMap.App/Views/BindingsDrawer.axaml(.cs)`, `src/PlayMap.App/Controls/BindMode.cs`, `src/PlayMap.App/Controls/BindBadge.cs` | Desk |
| `tests/PlayMap.Core.Tests/Control/*Tests.cs` | Codecs, parser, math, address map, OSCQuery tree, DMX parse and merge, input words, target lines |
| `tests/PlayMap.Control.Tests/` (new project) | Router, control thread, learner, feedback with fake MIDI, UDP loopback OSC/Art-Net/sACN, allocation and latency benchmarks |
| `tests/PlayMap.Engine.Tests/ControlVerbsTests.cs`, `tests/PlayMap.Net.Tests/OscQueryHostTests.cs` | Integration |
| `docs/CONTROL.md` | The paper: bindings, input words, OSC address space, OSCQuery, MIDI backends, DMX, profiles, safety |

### Existing files touched (small hunks)

| File | Change |
|---|---|
| `src/PlayMap.Core/Model/Project.cs` | `Control` init property; `AllIds` |
| `src/PlayMap.Core/Model/Ids.cs` | `Binding`, `ControlSurface` |
| `src/PlayMap.Core/Model/ProjectStore.cs` | `ApplyGroup` |
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | `VerbCategory.Control`, the verbs, `RiskOf(WireLine)` |
| `src/PlayMap.Core/Bus/Pairing.cs` | `NodeSettings.Control` |
| `src/PlayMap.Core/Bus/ShowBus.cs` | Two port constants |
| `src/PlayMap.Engine/EngineCore.cs` | Deferred republish (`_batchDepth`) |
| `src/PlayMap.Engine/EngineCore.Router.cs` | One routing arm |
| `src/PlayMap.Engine/EngineCore.Edits.cs` | Locate, Add, Validate for the two kinds; `SET` uses `LiveFields` |
| `src/PlayMap.Engine/StateWriter.cs` | `e.WriteControlState(w)` |
| `src/PlayMap.Engine/ContextBuilder.cs` | `engine.AddControlContext(items, links)` |
| `src/PlayMap.Engine/Seams.cs` | `PairedAddresses()` |
| `src/PlayMap.Engine/Host/EngineParts.cs` | The new partial hook |
| `src/PlayMap.Engine/Host/EngineExtension.cs` | `ExtensionSetup.AddFaults` |
| `src/PlayMap.Engine/Host/EngineHost.cs` | `HostFaults` sources |
| `src/PlayMap.Engine/Host/NetworkHost.cs` | Start/stop `OscQueryHost`; `PairedAddresses` |
| `src/PlayMap.Engine/PlayMap.Engine.csproj`, `PlayMap.sln` | Project references |
| `src/PlayMap.Assistant/CommandGate.cs` | `RiskOf` delegates to `VerbCatalog.RiskOf(line)` |
| `src/PlayMap.App/Controls/DeskMenu.cs` | **Bind ▸**, **Bound to ▸**, **Copy OSC address** appended in `Fill` |
| `src/PlayMap.App/Views/InspectorView.axaml(.cs)` | Slider right-click: **Bind value…** |
| `docs/WIRE.md`, `docs/ARCHITECTURE.md` (module table), `docs/UX.md` §4.10, `docs/ROADMAP.md` R3.1/R3.2, `README.md` (ports) | Papers |

---

## 6. Dependencies

**On other work packages:**

- **Round-2 spine**: `Project.Control`, `Ids`, `VerbCategory.Control`, `RiskOf(line)`, `ApplyGroup`, deferred
  republish, `EngineParts` hook, `ExtensionSetup.AddFaults`, `NodeSettings.Control`. All are small; the list is in
  §5.
- **showsafety**: the armed permission table for transports `binding` and `osc`; `PANIC` as the MSC ALL_OFF target;
  "live actions out of undo". `CONTROL ENABLE` is offered to Show Mode as its "all control input" switch.
- **companion**: shares `NUDGE` (encoders, `$(this:delta)`), the mDNS responder (`_playmap._tcp` beside
  `_oscjson._tcp`), and STATE `control` for its variables and feedbacks.
- **timecode** (R3.7): consumes `ITimecodeInput` (ArtTimeCode, MTC, clock, SPP).
- **creative and audio**: effect and audio parameters become bind targets through `SET`/`GENERATOR SET`, with
  ranges from their parameter specs. Audio features as an input kind is later work.
- **fleet**: a binding's edits reach mirror and follower nodes only if project edits propagate (R9). Input
  forwarding (S8) uses the paired wire the fleet package hardens.

**Third-party:** none required.

- MIDI is P/Invoke to OS libraries: `winmm.dll`, `CoreMIDI.framework`, `libasound.so.2` (LGPL, loaded at run time,
  nothing copied).
- Optional: `Microsoft.Windows.CsWinRT` (MIT) for the S3 backend.
- Optional alternative for mDNS: `Makaretu.Dns.Multicast.New` 0.38.0 (MIT, Nov 2024) [C24]. An in-house responder is
  preferred: about 400 lines, testable, shared with Companion.
- **Not used:**
  - DryWetMIDI: MIT, but no Linux device API [C8].
  - RtMidi.Core and managed-midi: stale [C7].
  - Haukcode.ArtNet and Haukcode.sACN: MIT and current [C24], but a few hundred lines of parsing do not justify a
    dependency. They stay a fallback.
  - OscQueryLibrary: VRChat-oriented, code not read.

---

## 7. Verification here

**Tested for real in this container:**

- All pure code with spec vectors:
  - OSC 1.0 encode/decode, including bundles, nesting, timetags, arrays and the "robust to a missing type tag" rule;
  - every pattern-matching rule of [C1];
  - the address map round-trip against every catalogue verb;
  - the OSCQuery tree against [C3]'s example shapes;
  - MIDI parser vectors from [C20][C22] (MCU faders, V-Pots, touch, rings; Launchpad programmer mode);
  - 14-bit CC, NRPN/RPN, running status, SysEx, MSC/MMC;
  - Art-Net headers and opcodes from [C9], ArtDmx and ArtTimeCode layouts cross-checked with [C10];
  - sACN offsets and options from [C11];
  - every shaping mode (§3.5 table);
  - input words and target-line validation.
- **Real sockets on loopback:**
  - OSC unicast (UDP 9943 on an ephemeral port in tests);
  - **Art-Net broadcast to 127.255.255.255** and **sACN multicast 239.255.x.y on `lo`** (both confirmed by
    `bench/control/mcast_probe.py`);
  - ArtPoll → unicast ArtPollReply;
  - OSCQuery over Kestrel with `HttpClient` and `ClientWebSocket` (LISTEN binary frames, `PATH_*`);
  - mDNS on 224.0.0.251 over `lo` (S7).

  Test packets are built by test code from the published layouts. No third-party sender is executed.
- **Engine integration** with `FakeMidiBackend` and `CONTROL INJECT`:
  - STATE and context keys asserted as fields;
  - journal gesture rows;
  - undo grouping;
  - one snapshot per atomic batch;
  - risk propagation and assistant fences;
  - takeover re-arm when the desk moves the value.
- **ALSA backend failure path**: `libasound.so.2` loads, `snd_seq_open` fails (no `/dev/snd/seq`), and STATE carries
  the words.
- **Benchmarks** (§3.12): allocations on the input path, latencies with real threads, lane share, the governor's
  steps. Results go to `r2/bench/control/` when implemented.

**Simulated, and how:**

- **Controllers**: `FakeMidiBackend` replays byte sequences from the MCU and Launchpad documents and records what
  feedback sends (pitch bend, ring CCs, init SysEx).
- **Lighting consoles**: a test sender emits two prioritised sACN sources, termination, silence and sync packets,
  plus Art-Net with sequence gaps and ArtSync.
- **TouchOSC, QLab, Chataigne**: their message shapes are reproduced in tests (TouchOSC fader floats; QLab `/go`;
  Chataigne OSCQuery `GET /` then `LISTEN`).

**Compile-only (needs Windows, macOS or hardware):**

- the WinMM backend (Windows);
- the CoreMIDI backend (macOS);
- the Windows MIDI Services backend (S3, Windows TFM leg);
- real ALSA I/O (needs `/dev/snd/seq`; a developer machine with `snd-virmidi` can run the same tests).

Fakes stand in for all of them through `IMidiBackend`.

**Not verified until a hardware day** (checklist goes in `docs/CONTROL.md`):

- APC40 mkII, Launchpad X, X-Touch, nanoKONTROL2 and MIDI Fighter Twister on Windows, macOS and Linux;
- ETC Eos and grandMA3 sending sACN and Art-Net;
- TouchOSC, Chataigne and MadMapper's OSCQuery browser against our OSCQuery;
- the "Bind a MIDI control ≤ 3 s" time target from UX.md §5.0.

Profiles stay marked `"verified":"doc"` or `"unverified"` until then.

---

## 8. Desk and Companion surface

**Right-click on anything:**

- **Bind ▸** lists the menu's own actions by label, each a verb line ("Go Intro", "Blackout", "Shutter open").
  Choosing one sends `BIND LEARN <that line>`, the status line reads "Move a control for Blackout — Esc cancels",
  and the item's menu entry reads LEARNING.
- **Bound to ▸** "CC 1 48 on APC40 · Forget", for every binding whose target is this object.
- **Copy OSC address** gives `/playmap/surfaces/Lobby/opacity`, or the verb's address.
- Inspector sliders have **Bind value…** (`BIND LEARN SET <id> <path> *`) and **Copy OSC address**.

**Bind mode** (toolbar button or **B**): every bindable control is tinted MIDI green / OSC yellow / DMX purple, as
MadMapper does [C17]. Click a control, move the hardware, done. Esc leaves.

**Badges** on bound controls show a small chip ("CC48", "/fader/1", "U7.101"):

- grey when the surface is unplugged;
- an **amber arrow ↑/↓** while Pick-Up is waiting (which way to move the fader);
- a pulse on hold-to-fire.

**Last-action panel** after a bind, generated from `BindingShape`:

- behaviour chips;
- a dual-handle range slider (in and out);
- curve presets with a mini graph;
- takeover None / Pick-up / Scale;
- the relative coding (auto-detected chip, changeable);
- hold-to-fire (shown and pre-set for high-risk targets);
- feedback colours from the surface's palette;
- **Test**, which sends `CONTROL INJECT` with 0 and 1.

**Bindings drawer** (UX.md's drawer list already names it):

- **Surfaces**: connection light, port on this machine (`CONTROL ALIAS`), profile, verified badge, **Identify**
  (flashes every bound LED for 3 s).
- **Bindings**: filter, live value bar, enable switch, forget.
- **Monitor**: last events with raw bytes and normalised value.
- **Network**:
  - the OSC port and the allow list;
  - the OSCQuery URL with "Open in browser";
  - feedback targets;
  - the DMX switch, NIC and universes with their sources and priorities;
  - **Import from Patterns** (paste trigger rows).
- A master **Control input on/off** (`CONTROL ENABLE`).

**Companion** (built by the companion package on this spine):

- Variables: `control_enabled`, `control_learning`, `control_last_input`, `control_surfaces_connected`,
  `bindings_count`.
- Feedbacks: `control_learning`, `surface_connected`, `takeover_waiting`.
- Actions: `control_enable` (on/off/toggle), `bind_learn_cancel`, and **`nudge`** for any field, with
  `$(this:delta)` on encoders and Satellite rotation amounts.
- Presets: a "Control" page (input on/off, learn cancel, surface status keys).

**Patterns:**

- A Patterns cue or Interactive device can send `BIND ENABLE <surface> ON|OFF` (a bank of bindings per scene) or any
  `/playmap/…` address through its OSC device.
- Patterns' MIDI rows can be imported.

---

## 9. Risks and open questions

| # | Risk or question | Handling |
|---|---|---|
| R1 | Art-Net requires a manufacturer **OEM code** [C9]. `0x00FF` is believed to be the "unknown" code (U) | Apply to Artistic Licence before release; the setting carries the value. |
| R2 | **Port 6454 contention** with another Art-Net program on the same machine (Windows lacks the Linux/macOS `SO_REUSEADDR` broadcast sharing) | A fault with words; the desk suggests sACN. |
| R3 | **OSC is unauthenticated UDP**; an allow-list by IP can be spoofed | Default bind to the node's `Bind` NIC; recommend a control VLAN; queries open, writes gated; later work adds token-bearing OSC over TCP/WS. Document in `docs/CONTROL.md` and the security posture (R3.14). |
| R4 | **Timetags assume the sender's clock** is close to ours ("OSC does not provide any mechanism for clock synchronization" [C1]) | Honour only within 60 s, warn per sender, switch off in settings. Patterns and PlayMap peers can be trusted because they share the Show Bus clock. |
| R5 | **Windows MIDI Services timing**: Microsoft says in-box in late November 2026 [C5] and also Q1 2027 [C6] | WinMM is the must (multi-client through the service on 24H2 and later); S3 waits for it. |
| R6 | CoreMIDI's packet-list API is deprecated (U) | Works today; a block shim (`MIDIInputPortCreateWithProtocol`) is later work. |
| R7 | **`SET` cost** for continuous control | `LiveFields` fast path plus the measured governor (§3.6). The test proves both. |
| R8 | Feedback loops (two apps echoing OSC; a motor fader fighting the operator) | Echo guard 300 ms, touch hold, self-address drop, dedupe. |
| R9 | **Sync groups and mirrors**: a binding edits the node it runs on. Followers do not see the edit unless project edits propagate | The fleet package decides propagation. Until then, bindings belong on the leader, and `CONTROL FORWARD` sends inputs where they belong. The doctrine "content is a pure function of show time" holds: bindings create edits, not integrated deltas. Smoothing stays later work for this reason. |
| R10 | A **grand master** is a natural first binding target, but the model has none | Ask the showsafety or present package to add `air.master` (0–1, render multiply). Until then bind `color.brightness` per output. |
| R11 | Bindings and Show Mode: should armed mode stop bindings? | Proposed: bindings fire (instruments), editing them does not. `CONTROL ENABLE OFF` is the operator's stop. The showsafety package decides. |
| R12 | Unverified controller profiles could send wrong colours or init bytes | Ship as `unverified`, send no init SysEx for unverified profiles, and verify on a hardware day. |
| R13 | ALSA struct layouts across distributions | Use `snd_midi_event_decode`/`encode` (byte buffers); P/Invoke only pointers and ints. |
| R14 | OSCQuery's open read exposes entity names to the LAN | Same exposure as `GET /api/state` today. It can be switched off (`OscQueryEnabled`); the security posture (R3.14) may tie it to pairing. |
| Q1 | Should OSC values be normalised 0–1 (Resolume) or native units (MadMapper)? | Native units by default, with `RANGE` in OSCQuery; normalised via `/playmap/norm/…` if users ask. |
| Q2 | House bindings (S6): project or node wins on conflict? | Project wins, and the desk shows both. |

---

## 10. Effort (agent-hours)

| Part | Hours |
|---|---|
| Core: model, ids, input words, target lines, ranges, `LiveFields`, `RiskOf` | 6 |
| Core: OSC codec, pattern, address map, OSCQuery tree | 5 |
| Core: MIDI parser, MSC/MMC, Patterns import | 4 |
| Core: Art-Net and sACN parse, merge | 3 |
| Control module: router, control thread, runtime shaping, learner, scheduler, governor | 7 |
| Feedback engine, profiles, predicates | 5 |
| OSC server, gate, subscribers; Art-Net and sACN receivers | 5 |
| MIDI backends: WinMM, ALSA, CoreMIDI, fake, ports and aliases | 7 |
| OSCQuery host (Kestrel + WS) | 3 |
| Engine integration: verbs, batch API, `ApplyGroup`, deferred republish, STATE, context, faults, journal | 6 |
| Desk: Bind ▸ menus, Bind value, badges, bind mode, drawer, last-action panel | 7 |
| Tests and benchmarks beyond the above; papers (`CONTROL.md`, `WIRE.md`, `UX.md`) | 5 |
| **Must total** | **≈ 63** |
| Should items S1–S8 | +14 |
