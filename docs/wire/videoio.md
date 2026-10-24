# wp2-videoio on the wire

NDI and OMT in and out (`docs/VIDEO.md`). An `<input>` is a live input's library id or name; a `<send>` is a send's id or name.
A live input is a stream item of the library (`MediaItem { Kind = Stream, Location = "omt:STUDIO (Cam 1)" }`) and is shown like
any other: `SURFACE SOURCE sf… media:md…`, `OUTPUT SOURCE op… media:md…`, a playlist item, a scan camera's source. Spout's rows
(`SPOUT` in `INPUT DISCOVER`, `SEND ADD`, `VIDEOIO SET`) are wp2-d3d11's; on this package's own they answer "Spout is Windows only"
or "no Spout provider on this node".

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `INPUT LIST` | `INPUT LIST` | `STATE.videoio.inputs` | low | no |
| `INPUT DISCOVER` | `INPUT DISCOVER [NDI\|OMT\|SPOUT\|ALL] [WAIT <0-5000 ms>]` | `{"ndi","omt","spout","sources":[…]}` (off the lane) | low | no |
| `INPUT ADD` | `INPUT ADD <uri> [<name>] [PROXY] [COLD]` | `{"id","uri","proto","state"}` | review | yes |
| `INPUT FALLBACK` | `INPUT FALLBACK <input> HOLD\|BLACK\|<source>` | effect "Cam 1 on loss: shows Standby loop" | review | yes |
| `INPUT BACKUP` | `INPUT BACKUP <input> <uri>\|OFF` | effect | review | yes |
| `INPUT SWITCH` | `INPUT SWITCH <input> PRIMARY\|BACKUP` | `{"using","firstFrameMs"}` at the next frame (off the lane) | high | yes |
| `INPUT RECONNECT` | `INPUT RECONNECT <input>\|ALL` | effect at the first frame, else accepted; refused with the protocol's words when it will not make the receiver (off the lane) | high | yes |
| `SEND LIST` | `SEND LIST` | `STATE.videoio.sends` | low | no |
| `SEND ADD` | `SEND ADD NDI\|OMT\|SPOUT <name> <tap> [<w>x<h>] [@<fps>] [UYVY\|UYVA\|P216\|PA16\|BGRA8] [QUALITY LOW\|MEDIUM\|HIGH] [ALWAYS]` | `{"id","fullName","w","h","estMbps"}` | review | yes |
| `SEND ENABLE` | `SEND ENABLE <send> ON\|OFF` | effect ("2 receivers lost this picture") | high (`ON` review) | yes |
| `SEND REMOVE` | `SEND REMOVE <send>` | effect | high | yes |
| `VIDEOIO STATUS` | `VIDEOIO STATUS` | `STATE.videoio` without the lists | low | no |
| `VIDEOIO CHECK` | `VIDEOIO CHECK` | the preflight rows below | low | no |
| `VIDEOIO SET` | `VIDEOIO SET NDI ON\|OFF \| NDI-GROUPS <groups> \| NDI-DISCOVERY <host,…>\|OFF \| OMT ON\|OFF \| OMT-DISCOVERY <host[:port]>\|OFF \| SPOUT ON\|OFF \| MAX-SEND-MBPS <0-100000>` | `{"applies":"now"\|"next start"}` | review | yes (node settings) |

**URIs.** `ndi:HOST (Name)` and `omt:HOST (Name)` name a source as discovery lists it (matched case-insensitively, the host's
domain suffix ignored); `ndi://ip:port` and `omt://host:port` name one by address. `INPUT ADD` refuses anything else that is not
an FFmpeg URL ("ndi:HOST (Name), ndi://ip:port, omt:HOST (Name), omt://host:port, spout:Name, or an FFmpeg URL (rtsp://…)"), a
source already in the library ("… is already in the library as md… (Cam 1)") and a protocol this node cannot receive (the
runtime's words; for NDI they name `PLAYMAP_NDI_DIR` and https://ndi.video). `PROXY` asks NDI's lowest bandwidth or OMT's 1/8
preview; `COLD` connects at the first consumer instead of when the input enters the show. A `capture:` URI is read by the one
capture grammar (`CaptureUri`: `capture:<provider>/<device>[/<input>]`, the providers `dshow`, `v4l2`, `mf`, `decklink`, `aja`
and `magewell`), never as an FFmpeg location: one the grammar refuses — an unknown provider, user information, a query — is
answered with the grammar's words, which repeat nothing of it, and every line written down (the journal's rows among them) shows
it as `capture:‹refused›`; a well-formed one answers "INPUT ADD capture: is not available on this node yet" with the sources that
are. `INPUT BACKUP` and `INPUT FALLBACK` read a `capture:` word by the grammar first in the same way ("INPUT BACKUP capture: is not
available on this node yet — a backup is ndi:, omt: or spout:"), and a generic `ADD media` or `SET` that would store a `capture:`
location or live backup is refused with the grammar's words, or "capture: sources are not available on this node yet".

**Loss.** `INPUT FALLBACK` sets `live.onLoss` and `live.fallback`: `HOLD` (the last picture stays), `BLACK`, or a source the node
always has — `media:`, `generator:`, `canvas:`, `playlist:`, `color:#RRGGBB`. Refused: unchanged ("Cam 1 already holds its last
picture on loss"), a live input or a stream item ("a fallback is a picture this node always has"), a source not in the project.
`INPUT BACKUP` sets `live.backup` (refused: not a live URI, the primary itself, unchanged, `OFF` with none, a protocol this node
cannot receive). `INPUT SWITCH` is a person's word: refused "already on its primary", "no backup set — INPUT BACKUP md… <uri>",
"Cam 1's backup has given no frame yet", "… has given no frame for 1.5 s"; answered at the next frame from the feed switched to,
or accepted when none came in 3 s.

**Sends.** A tap is `output:<id>` (the final picture of an output: after its output stage — warp, mask, blend, the measured
correction of an align run, trim — exactly what the projector gets; black while the output is off or shows a scan or align
pattern), `canvas:<id>` or `source:<sourceref>`, and names something in the show; `prewarp:` is refused
("prewarp taps are next round"). The size defaults to the tap's (a source tap's to 1920×1080) and is fitted to the format: UYVY,
P216 and PA16 to even widths, UYVA to a multiple of 4 — the effect says so ("1918 → 1916 (UYVA needs a multiple of 4)"). The rate
defaults to the output's refresh, else the show's; 23.976, 29.97, 59.94 and 119.88 are sent as 1001 rationals. `QUALITY` is OMT's
(NDI refuses it: "NDI chooses its own quality: QUALITY is OMT's"). `ALWAYS` packs and reads back even when no receiver watches
(otherwise a send costs nothing until one connects). **The bandwidth guard** refuses a `SEND ADD`, a `SEND ENABLE … ON` or an edit
of a send (`SET sd… fps 120`, `ADD send {…}`) that would take the node's enabled sends past `MAX-SEND-MBPS`, or 70 % of the NIC's
link when that is 0 ("3 sends ≈ 820 Mb/s > 700 Mb/s (70 % of eth0's 1000 Mb/s) — VIDEOIO SET MAX-SEND-MBPS <n> to allow more"); an
edit that lowers the total is never refused. The estimate is OMT 0.8 / 1.6 / 2.4 bits a pixel (low / medium / high) and NDI 1.0.
A packed target is a texture of at most 16384 rows: a PA16 send is at most 5461 rows tall, P216 8192, UYVA 10922; a send whose
targets are larger than this GPU's textures is refused in STATE (`failed`, with the GPU's limit) and never packed. A larger output
or canvas (`SET op… width`) is held to the guard too, for the sends that tap it.

**VIDEOIO SET.** `MAX-SEND-MBPS` applies now; `NDI`, `NDI-GROUPS`, `NDI-DISCOVERY`, `OMT`, `OMT-DISCOVERY` and `SPOUT` apply at
the next engine start (the runtimes read them when they load): what runs keeps running and STATE shows it (`state`, `groups`,
`discovery`, `server`), with what the next start brings beside it (`next`); a protocol turned `OFF` takes no new work of it from
then on, whichever verb asks (INPUT ADD, INPUT BACKUP, SEND ADD, SEND ENABLE ON, or an `ADD`/`SET` that adds or enables one:
"OMT is off from the next engine start — VIDEOIO SET OMT ON keeps it"; a live item's backup counts), and preflight and the context
go amber. `SPOUT` is kept for the Spout package, which reads it when it registers; on a node with no Spout provider the effect says
so. Refused when unchanged. The settings are the node's (`NodeSettings.VideoIo`), not the show's.

**Show Mode.** Every line that changes a send is content, whichever verb says it (`SEND ADD`, `SEND ENABLE`, `SEND REMOVE`, and the
generic `ADD send`, `SET sd…`, `REMOVE sd…`), as `LOCK AREA content` locks sends: armed, an operator runs them, a remote client is
refused and an assistant proposes. `INPUT ADD`, `INPUT FALLBACK` and `INPUT BACKUP` are content; `INPUT SWITCH` and
`INPUT RECONNECT` are repairs an operator may run armed (device class); `VIDEOIO SET` is set-up, refused armed.

**Blind edit.** A session editing blind stages `INPUT ADD`, `INPUT FALLBACK`, `INPUT BACKUP`, `SEND ADD`, `SEND ENABLE` and
`SEND REMOVE` in its draft. The hub reads the live project only, so nothing staged is received or sent until the draft lands, and
the answer says so: `INPUT ADD` answers `"state":"staged"` and "staged Cam (omt:STUDIO (Cam 1)) in the blind draft — it is received
when the blind draft lands"; `SEND ADD` answers its id with an empty `fullName` and "staged Main from output:op_1 in the blind draft
— it sends when the blind draft lands"; the others "Main stops when the blind draft lands" and the like.

**Stopping.** A send that stops — `SEND ENABLE … OFF`, `SEND REMOVE`, its tap gone, its protocol off, the node stopping — sends one
black frame of its last size and format before its sender closes, so a receiver that keeps a lost source's last frame keeps black;
a send made again under its own name (a new size or format) hands over with none. Once the node has begun to stop no receiver or
sender starts: a consumer gets a handle only on an input already received, the hub makes nothing the project names, and an
`INPUT RECONNECT` already past the door is refused "the node is stopping — no receiver starts".

Generic verbs keep working: `SET md… live {"pacing":"smooth"}`, `SET sd… width 1280`, `REMOVE sd…`, `REMOVE md…`, `ADD send {…}`.
The checks refuse a send name outside 1–48 printable characters, an enabled send whose tap is empty, does not parse or names nothing
in the show, fps outside 0–240, a size outside
0×0 (the tap's) to 16384×16384, a quality other than default/low/medium/high, a format NDI and OMT do not send this round
(RGBA8, RGB10A2, RGBA16F), a duplicate name per protocol; and on a live item `lostMs` outside 100–60000, `staleMs` above
`lostMs`, `recoverMs` outside 0–600000, a backup that is not a live URI or is the item's own location, a fallback that is live or
shows a live input (a playlist's items, a canvas's layers, to eight deep; deeper is refused, since it cannot be shown clear) or names
nothing in the show — checked when the fallback is set or changed, so an edit that leaves it alone is never refused for it — and
`onLoss` fallback with no fallback. An item a fallback names cannot be removed while it does; a fallback that came to show a live
input since is black on loss.

## STATE

Top level `videoio` (after `decode`):

```json
"videoio":{"ndi":{"state":"ready|absent|failed|off","version":"6.3.2","path":"/usr/lib/libndi.so.6","words":"","link":"https://ndi.video",
                  "groups":"","discovery":"","advertisers":true,"next":{"state":"on|off","groups":"","discovery":""}},
           "omt":{"state":"ready|failed|off","version":"1.0.0.20","vmx":"avx2|sse42|neon|scalar|missing","mdns":"avahi|dnsapi|bonjour|none",
                  "server":"","words":"","next":{"state":"on|off","server":""}},
           "spout":{"state":"unavailable","version":"","senders":0,"words":"no Spout provider on this node (Spout is Windows only)"},
           "discoveredCount":2,
           "discovered":[{"uri":"omt:STUDIO (Cam 1)","proto":"omt","name":"Cam 1","host":"STUDIO","address":"10.0.0.5:6400","added":"md…"}],
           "inputs":[{"id":"md…","name":"Cam 1","uri":"omt:STUDIO (Cam 1)","proto":"omt","state":"ok|stale|lost|connecting|failed",
                      "words":"","w":1920,"h":1080,"fps":50,"ageMs":20,"received":30,"dropped":0,"repeated":0,
                      "using":"primary|backup","tally":"program|preview|none","onAir":true,
                      "onLoss":"hold|black|fallback","fallback":"media:md…","loss":"|hold|black|fallback",
                      "backup":"omt:STUDIO2 (Cam 1)","backupState":"|ok|stale|lost|connecting|failed",
                      "format":"uyvy|uyva|p216|pa16|bgra8","alpha":false,"pacing":"latest","warm":true,"mbps":1716.1}],
           "sends":[{"id":"sd…","name":"Main out","proto":"omt","fullName":"SHOWPC (Main out)","tap":"output:op_1","w":1280,"h":720,
                     "fps":60,"format":"uyvy","state":"sending|idle|off|failed|starting","words":"","connections":2,
                     "tally":{"program":true,"preview":false},"sent":0,"dropped":0,"enabled":true,"skippedUnwatched":0,"mbps":0}],
           "bandwidth":{"nic":"eth0","linkMbps":1000,"inMbps":1716.1,"outMbps":0,"limitMbps":700,"light":"green|amber|red"}}
```

- `ndi.state` is `absent` when no runtime was found (`words` name where it was looked for, `PLAYMAP_NDI_DIR` and
  https://ndi.video), `failed` when one was found and would not initialise, `off` when `VIDEOIO SET NDI OFF`; `advertisers` is true
  when the 6.3 send and receive advertisers are up. `omt.vmx` is libvmx's CPU path (`missing` when it did not load: OMT then
  refuses with the loader's words). A runtime path that is not absolute (`videoio.ndiRuntimePath`, `PLAYMAP_NDI_DIR`,
  `NDI_RUNTIME_DIR_V6`, `PLAYMAP_VMX_DIR`) is refused and said in `ndi.words` / `omt.words`, ready or not (`docs/VIDEO.md`, Native
  libraries); `mdns` is the service discovery this machine offers OMT (`none` here without avahi: OMT then
  finds sources only by `omt://host:port` or a discovery server).
- An input's `state` follows the age of its newest frame on the node's monotonic clock: `connecting` until the first, `ok`,
  `stale` past `live.staleMs` (0 = three periods of its own measured rate), `lost` past `live.lostMs` (1000; "no frame for 1.1 s"),
  `failed` while the protocol says it cannot receive (words). `pacing` is `latest` this round whatever is asked.
- `loss` is what the loss policy does now: empty while the feed in use delivers; `hold`, `black` or `fallback` from the tick that
  found it lost until it is back (a held picture at its first steady frame; black and a fallback after `live.recoverMs` of steady
  frames). `using` is the feed consumers read; a backup takes over when the feed in use is lost and the other delivers.
- `tally` is `program` when the renderer drew the input into the air (an output or a send) in the last 250 ms and the air is not
  blacked out, `preview` when it was drawn otherwise (the desk's editor and projector views, any other consumer that holds the
  input) or under a blackout; it is told to the sender on change, and the feed not in use is told it is on nothing.
- `ndi` / `omt` say what runs (the state, groups, discovery and server the runtime started with); `next` what `VIDEOIO SET` asked
  for the next start. A ready runtime may still have words, and preflight shows them amber: NDI's configuration could not be
  written, so its discovery server is not applied and this node's other NDI tools do not get its groups (its own finder and senders
  still use them).
- A send's `state`: `sending` (frames go out), `idle` (no receiver, `onlyWhenWatched`), `off`, `failed` (`words`: no runtime, a tap
  with nothing behind it, a refused sender, a packed target larger than this GPU's textures, a GPU protocol whose stage this node
  does not run), `starting`. `skippedUnwatched` counts frames not packed because nobody watched.
- `discovered` is at most 64 rows (`discoveredCount` is all of them), sorted by URI; `added` is the library id that names it.

## Context

`videoio` (light and words: red when a runtime the show uses is absent or an input on air is lost; amber when an input is stale,
lost or failed, shows black or its fallback, or is on its backup, or a send drops more than 1 %; green "1 input ok, 1 send to 3
receivers"); `input:<id>` for each input on air or not delivering or standing in ("ok · on its backup", "lost — no frame for 1.1 s
· shows its fallback"), linked `feeds` to the surfaces and outputs it shows on; `send:<id>` for each enabled send ("3 receivers ·
on program"), linked `sends` from its output.

## Events

`input.lost` (`input`, `name`, `words`, `shows`: `hold|black|fallback`, `fallback`: its name), `input.switched` (`shows`:
`backup|primary`; raised by a takeover on loss and by `INPUT SWITCH`), `input.back` (`words`: "steady for 2.0 s" or "frames
again"). Each is journalled once per loss, kind `videoio`.

## INDEX

Section `video`: a row per live input (kind `input`, its state, lit as the context lights it); keys Reconnect (`INPUT RECONNECT
$(id)`), Backup (`INPUT SWITCH $(id) BACKUP`), Primary (`INPUT SWITCH $(id) PRIMARY`), all `hold`. Section `sends`: a row per send
(kind `send`, its STATE state `sending|idle|off|failed|starting`, lit as the context lights it, parent the tapped output or
canvas); keys On and Off (`SEND ENABLE $(id) ON|OFF`), `hold`.

## Preflight

Section `videoio`: one green "not used" row when the show has no live input and no send; else `videoio.ndi.runtime` /
`videoio.omt.runtime` for each protocol the show uses (red "missing" with the runtime's words and the fix `VIDEOIO SET NDI ON`,
`VIDEOIO SET OMT ON` or `VIDEOIO STATUS`), `videoio.input.<id>` per input (green delivering; amber stale or not delivering, red
when on air; fix `INPUT RECONNECT <id>`), `videoio.send.<id>` per enabled send (red failed with the fix `SEND ENABLE <id> OFF`).
The file an enabled send taps (`source:media:md…`) and the fallback a live input the show uses shows on loss are media the show uses:
the `media` section checks them as it checks an output's source, and the `optimise` section counts them among what the show plays.

## Papers note

For the integration docs pass (WIRE.md, ARCHITECTURE.md, PLAYBACK.md, ROADMAP.md, UX.md):

- NDI and OMT in and out. A live input is a stream item of the library, received once by a warm receiver however many surfaces,
  outputs, previews and scan cameras show it; its health, tally and loss policy (hold, black or a fallback; a backup feed that
  takes over on loss, back only by `INPUT SWITCH`) are STATE's. Sends tap an output, a canvas or a source, are packed on the GPU
  (UYVY, UYVA, P216, PA16, BGRA) into a read-back ring and cost nothing while nobody watches; blackout, freeze, the test pattern
  and covered/held outputs reach them exactly as they reach the air.
- OMT is libomtnet and libvmx, vendored under `src/ThirdParty` with their licences, patched as `PATCHES.md` lists (avahi absent,
  IPv4-only hosts, no `Thread.Abort`, a send-dispose deadlock). NDI is the user's own NDI 6 runtime, loaded at run time from
  `PLAYMAP_NDI_DIR` (or the runtime's `NDI_RUNTIME_DIR_V6`, or the standard install folders), each by its absolute path and never
  by bare name; no NDI binary is shipped. NDI® is a registered trademark of Vizrt NDI AB.
- `SourceResolver` routes a live input to what its loss policy shows (`ISourceProvider.Route` now answers for live inputs too).
- Next round (`docs/VIDEO.md`): prewarp taps, multiview sends, smooth and locked pacing, PTZ, failover sends, `LiveCamera` scans
  over NDI/OMT, discovery thumbnails, hosting an OMT discovery server, Syphon, capture cards.
