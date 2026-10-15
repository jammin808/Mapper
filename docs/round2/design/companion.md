# Work package `companion` — PlayMap for Companion 5.1: the two-way module that leads its category

*Design spec, round 2, 13 Oct 2026. Research: `r2/research/companion.md` (cited as **[C §n]**, with its source tags
**[Sn]**). Code read on `main` at `c12b186`. Patterns' module read at `/home/user/patterns-svc` (`bcdbf28`).*

**Verified here while designing** (not taken from the research):

- `@companion-module/base` 2.2.1, `@companion-module/host` 1.2.1 and `@companion-module/tools` 3.1.1 are npm `latest`.
  `ws` is 8.22.0 (MIT). All were reached through this container's proxy.
- `companion-module-build` bundles with esbuild using `format: 'esm'`, `target: 'node22'`, and a `createRequire`
  banner (`tools/dist/scripts/lib/bundle-util.js`).
  - A `ws` client and server, bundled with exactly those settings, ran here and agreed the subprotocol
    `playmap.lines.v1`.
  - The same bundle *without* the banner fails at run time ("Dynamic require of events").
- `@companion-module/host` exports `InstanceWrapper`. This is the host-side wrapper Companion uses to run a module:
  `init`, `executeAction`, `updateFeedbacks`, `upgradeActionsAndFeedbacks`, `learnAction` and `startStopRecordActions`
  are driven through a `ModuleHostContext`. Tests can therefore run the module through Companion's own host layer, not
  a hand-made fake.
- Node 22.22 here runs `.ts` files directly (type stripping), and `node:test` is available.
- `github.com` (releases and API) answers 403 through the proxy. A real Companion 5.1 build therefore **cannot** be
  fetched or run in this container (§7).

---

## 1. Goal and the bar

**Goal.** A Companion connection that makes a mapping server's whole show into live keys:

- what each key shows;
- what each key may do now;
- what the show needs next.

The keys update themselves as the project changes. They speak Patterns' colour language and words, are safe in Show
Mode, and can be set up with almost no typing. The node stays the only authority: a light on a key is a fact from
STATE, never the press ("attempts are not facts").

| What the best do today | Who | How PlayMap matches or beats it |
|---|---|---|
| A pushed state subscription instead of polling | Resolume's WebSocket `subscribe` and `parameter_update` [C §5][S27] | STATE pushes on a **WebSocket line channel** (`/ws`, subprotocol `playmap.lines.v1`), carrying the same lines as the TCP wire. The module **diffs** them, so only changed variables and only affected feedback types are touched. Clocks are **interpolated at 10 Hz** between pushes from `positionMs`, `rate` and `at`, corrected by `PING` offsets. Resolume needs an extra OSC listener for transport position [S27]; Pixera polls at 50–1000 ms [S28] |
| A watchdog with backoff, dropping state on link loss | Pixera [S28] | `PING` every 2 s doubles as watchdog and clock exchange. No answer for 4 s → **STALE**: every key dims, with a mark. No answer for 10 s → reconnect with jittered backoff (1→15 s). On a drop, state is emptied and all feedbacks rechecked, so a dead key never stays green (Patterns' rule) |
| Thumbnails on keys | Resolume: 64×64 RGB through an *advanced* feedback, a type the base package marks "discouraged … likely removed" [S27][S18] | **Value feedback `thumb` → image element `base64Image` through a 5.1 style override (`$(this:value)`)**. The pictures are JPEG/PNG data URIs at 72–144 px. Posters come from the node: **cached by content hash and ETag, pushed only on change** (`THUMBS` push), fetched only for placed keys. They travel **over the paired socket**, so no token rides in clear and there is no second connection |
| Live, show-built presets | Patterns rebuilds per show, keyed by index and name; a rename or reorder orphans linked buttons [C §6.6 gap 2] | **Template preset groups keyed by entity id** (`pl.<id>`): one layered preset per kind, one template value per entity. **Linked presets survive renames and reorders**. Renames reach keys through value feedbacks. Presets are re-sent only when the node's **INDEX signature** moves |
| Feedbacks that are only boolean | Patterns (about 107, all boolean) [C §6.6] | Booleans for trigger conditions, plus **typed value feedbacks** (2.2 `result`): `label`, `light`, `state_word`, `progress`, `remaining`, `thumb`, `score`, `temp`, `state_path`, `slot`. They drive **layered-button style overrides** in 5.1 |
| Gauges | Patterns: one ring in one preset [C §6.3] | Ring and bar gauges throughout: item progress, calibration score, projector temperature, transfer, scan, sync uncertainty. On a Stream Deck Studio the first gauge appears on the encoder LED ring (5.1) [S5] |
| Double-GO guard and show-mode awareness | QLab's `min_go` and `/showMode` [S29] | A module-side `min_go_ms` debounce **and** the node's own minimum GO interval. **Show Mode is read from the node, never guessed** (`WHOAMI` gives the verbs this session may run now): keys the node would refuse wear a lock **before** they are pressed. A locked key is still sent; the node refuses it, journals who tried, and the refusal flashes on the key. **Risk-shaped presses** (instant, 0.8 s hold, or two presses plus hold) are generated from `VerbRisk` |
| Panic | Mitti's Panic action [S31] | `PANIC <outcome>` presets for each defined outcome (from showsafety), red and instant |
| Health of every machine | Pixera's `livesystem_state` feedback [S28] | Eye keys run on CONTEXT (headline key lit by the worst light; NEXT/PREV PROBLEM; LENS) and a **fleet template** over peers. They work module-side over CONTEXT today, and drive `EYE …` on nodes that have it |
| Discovery and pairing | Patterns: mDNS pick, HMAC proof, but the token sits in plain config [C §6.6 gap 3] | `bonjour-device` over **`_playmap._tcp`**, announced by the node (new). Pairing is **HMAC proof**, byte-equal to Patterns'. The token lives in **`secret-text` secrets**. Optional **wss with certificate pinning**. *Should:* one-click **"Add to Companion"** from PlayMap's desk through Companion's REST API v2 [S11] |
| Reply matching | Patterns: first in, first out [C §6.6 gap 6] | Every line carries `@c<n>`, and replies are matched by id. Receipts move a per-control corner mark: amber while *accepted*, cleared when *applied*, red on `ERR` with the words in `last_refusal`. Mutating actions return the receipt as an **action result**, so `storeResult` and `internal:logicIf` can branch on it |
| Programming from the deck | Patterns: Navigator, raw recorder, learn [C §6.3] | A **typed** recorder: `RECORD ON` makes the node push `ACTION <line>`, which becomes a typed action through a TypeScript port of `VerbCatalog.Match`. Learn fills options from the focused playlist and its current item (and from the desk's selection once it is published). The **Live page** creates keys by itself (§3.6) |
| Platform features of 5.x | **No studied video-server module is on base 2.x** (Resolume ~1.10, Pixera ~1.4.3, QLab ^1.8, Millumin, Mitti, disguise, Watchout) [C §1.4] | Built on **base ~2.2.1**: linked presets, template groups, layered presets with composites, value-feedback style overrides, feedback-based local variables, `storeResult`, `notes`, CSS colours, `secret-text`, `$(this:delta)` encoders, action results, abort signals, `alternatives` fallbacks |
| Two-way beyond the module | Patterns drives Companion pages through its TCP API [C §6.4] | *Should:* a **Companion link** on the node. It presses buttons, sets custom variables and turns pages, from playlist items and verbs. It provisions the connection (REST v2). It dims every surface with the dark-room show strip (REST v2 `surfaces`) |

**Two firsts no peer has:**

1. **Automations from show events.** The node pushes `EVENT` lines (item start and end, armed, panic, fault, scan
   done, projector moved, proposal waiting). The module turns them into `event_pulse` boolean feedbacks and `event_*`
   variables, which Companion triggers use as `condition_true` and `variable_changed` [S10].
2. **The Live page.** One installed block of 32 slot keys fills itself from the project, live: the focused playlist's
   items, projectors, surfaces, outputs, peers, proposals or faults, or an operator-authored key page (*should*).
   - Its source is chosen by one page variable `$(page:pm_live)` (5.1 page variables [S8]).
   - While Show Mode is armed the slot layout **freezes**, so a key never moves under a finger mid-show.

---

## 2. Scope this round

### Must — the module and the node work it needs

| # | Item | Acceptance (checkable) |
|---|---|---|
| M1 | **Line channel over WebSocket**: `/ws` with `Sec-WebSocket-Protocol: playmap.lines.v1` runs the line-wire session (NONCE, HELLO, AUTH-PROOF, PING, SUBSCRIBE, pushes) over one text frame per line. TCP 9941 is unchanged. Both channels share one session table and one push fan-out | `LineWebSocketTests`: unpaired → only `NONCE`/`HELLO`/`AUTH-PROOF`/`PING` answer and everything else is `ERR not paired …`. A right proof gives `OK paired`. Five wrong proofs → close 4401. STATE pushes reach a WS session and a TCP session from the same throttle tick, and identical pushes are skipped. A binary frame closes with 1003; a frame over 64 KiB closes with 1009. `RequireHttps` refuses plain `ws://` from a non-loopback address with 403 before the upgrade |
| M2 | **Transport inference**: `HELLO Companion:<label> module=<v>` → `SessionInfo.Transport = "companion"`, `Channel = "ws"\|"tcp"`. The claim can only narrow rights: it maps to the remote-operator role | `WireServerTests`: the journal row's `who` reads `Companion:FOH deck` with transport `companion`. A session claiming `PlayMapDesk` over a WS line channel is `wire`, not `desk` (the desk links over TCP) |
| M3 | **INDEX** (query) and `SUBSCRIBE INDEX` (push `INDEX {…}`, latest-wins): every playlist with its items, surfaces, projectors, outputs, devices, generators with typed params, canvases and layers, key pages. Also `sig` (structure) and `labels` (names) hashes. `STATE.project.index` carries `sig` | `IndexTests`: adding an item moves `sig`; renaming moves only `labels`; a warp-node drag moves neither (no push). 1000 entities write in < 2 ms. The push is skipped when unchanged |
| M4 | **THUMB** (query) and `SUBSCRIBE THUMBS <ref>…` (push `THUMBS {ref: etag}`), plus `GET /api/thumb`. Sources:<br>– media posters (the optimise store if present, else one decoded frame);<br>– colour and solid generators (drawn);<br>– outputs and projectors (live, ≤ 1 fps, from preview views with internal leases); `playlist:<id>` as its current item's picture | `ThumbTests` (FFmpeg 9 here): a 2 s H.264 test clip → `THUMB media:md… 96x96` returns a JPEG data payload ≤ 12 KB, decodable, 96×54. A repeat with `IF <etag>` answers `same`. While armed an uncached poster is refused with words, and a cached one is served. A live `output:op_1` ref under the headless render host on Xvfb returns a picture and moves its ETag at most once a second. A reply fits one line (≤ 60 KiB, quality stepped down until it fits) |
| M5 | **RECORD ON\|OFF** → `ACTION <line>`. Fed from the journal's ok verb rows of transports `desk` (and later `remote`). Never from `companion`, `wire`, `mcp`, `assistant` or `approval`. Never a line whose verb carries a secret. Never `VIEW`, `THUMB` or the session words | `ActionFeedTests`: a desk `PLAYLIST GO …` reaches a recording session as `ACTION PLAYLIST GO …`. The recording session's own press does not. `LINK PATTERNS h tok` and `CREDENTIAL SET …` are never fed. RECORD from an unpaired session is refused |
| M6 | **EVENTS**: `SUBSCRIBE EVENTS` → `EVENT {…}` lines in order; `EVENTS SINCE <seq>` gives the ring of the last 256. Kinds are detected by diffing facts on the push thread, with times taken from the playback anchors | `EventsTests` with a manual clock: an item that ends at t gives `item.end` with `at` = t ± 1 ms even if detected 200 ms later. Blackout on and off give `air.blackout`. A fault appearing and clearing gives `fault.raised` and `fault.cleared`. 64 events in a second to one session coalesce into `overflow` |
| M7 | **WHOAMI** and **PRESENCE**; STATE `clients[]` and one context item per Companion client | `WHOAMI` answers `{"session","transport","client","paired","role","armed","may"}`. Without showsafety: `role:"remote"`, `armed:false`, `may:"*"`. After `PRESENCE {"kind":"companion","module":"1.0.0",…}` the client appears in `STATE.clients`, and leaves within one push of its socket closing |
| M8 | **mDNS** `_playmap._tcp` on the web port, with TXT `id, name, t=playmap, v, proto=1, ws=/ws, wire, tls, fp`. Answers PTR, SRV, TXT and A, including legacy-unicast queries. Announces on start, says goodbye on stop. `NodeSettings.Mdns` defaults to true | `MdnsTests`:<br>– codec round-trip of captured Bonjour/Avahi query packets;<br>– a unicast query from loopback to the responder's port is answered with PTR, SRV, TXT and A for `<name>._playmap._tcp.local`;<br>– a name conflict gets a probe and a rename to "<name> (2)";<br>– a bind failure becomes a `faults[]` entry `mdns`, never a crash |
| M9 | **Typed verb parameters**: `VerbParam` table beside the catalogue; `VERBS` and `/api/verbs` gain `params`. Every catalogue verb has an entry (test) | `VerbParamTests`: every `VerbCatalog.All` name has params. Each entity param names its id prefix. The syntax string's words agree with the params (a parser test turns every `syntax` into params and compares) |
| M10 | **The module**: `integrations/companion-module-playmap/`, TypeScript on base ~2.2.1, manifest `jammin808-playmap`, `node22`, API 2.2.1. It covers: connection; pairing over the WS line channel with TCP fallback; diffed STATE → variables; boolean and value feedbacks; typed actions generated from `verbs.json`; risk-shaped presses; encoders; template and linked presets keyed by id; layered keys with alternatives; composites; thumbnails; the Live page (module-side rules); events → automations; the typed recorder; learn from STATE; Show-Mode locks from WHOAMI | All tests in §7.1 green. `companion-module-check` passes. `companion-module-build` makes `pkg.tgz`, which opens as Companion opens it. Every preset survives `sanitisePresetDefinitions` unchanged (no dropped action, feedback, option or element). Every action's lines parse with `VerbCatalog.Match` and the verb params (C# contract test over `test/lines.txt`) |
| M11 | **Contracts held by tests on both sides**:<br>– palette vendored from Patterns, byte-equal and SHA-pinned;<br>– PlayMap kinds only added, never recoloured;<br>– `verbs.json` equals `VerbJson.All`;<br>– every STATE path the module reads exists in a STATE written by the real `StateWriter`;<br>– module version equal in `package.json`, `manifest.json` and `MODULE_VERSION`;<br>– `KNOWN_LINE_PROTOCOL` equals the node's `proto` | `CompanionContractTests` (C#) and `contract.test.ts` (Node) |
| M12 | **Real-engine end-to-end**: the headless engine built here plus the module run through the host's `InstanceWrapper` | `e2e-engine.test.ts` (gated on `PLAYMAP_ENGINE` env; run in CI here). It checks:<br>– pair over `ws://` with a token;<br>– `PLAYLIST NEW` and `PLAYLIST ADD color:#FF0000` via actions;<br>– template group `pl.<id>` appears within 300 ms of the INDEX push;<br>– pressing the item key's down action → `item_state` = `air` and the `pl_1_state` variable = `playing`;<br>– killing the engine → status Connecting, every feedback re-checked to off within 4.5 s;<br>– restarting it → re-paired and green again |
| M13 | **Papers**: `docs/COMPANION.md` (setup, keys, safety, recipes); `WIRE.md` (line WS, INDEX, THUMB, EVENT, ACTION, PRESENCE, WHOAMI, mDNS); `companion/HELP.md`; ROADMAP R3.4 marked done; README | Review |

### Should — this round if time allows

| # | Item | Acceptance |
|---|---|---|
| S1 | **Key pages** authored on the desk. Model `KeyPage` (`kp…`); verbs `KEYS LIST\|NEW\|SET\|CLEAR`, plus `ADD keypage`, `SET` and `REMOVE`. Right-click any entity → **Put on keys ▸ <page>**. The Live page shows them with `$(page:pm_live) = keys:<id>` | Engine tests for the verbs and undo. The module test shows authored slots override rule slots. A desk test checks the right-click item equals the toolbar's DeskAction (DeskMenu rule) |
| S2 | **Companion link** (node → Companion), `Net/Links/CompanionLink.cs` and `COMPANION …` verbs. Each sub-verb names its Companion API:<br>– `COMPANION PRESS`: HTTP `/api/location/p/r/c/press`;<br>– `COMPANION VAR`: `/api/custom-variable/<n>/value`;<br>– `COMPANION PAGE`: TCP 16759 `SURFACE <id> PAGE-SET`, refused with words when Companion's TCP API is off (its default [S12a]);<br>– `COMPANION BRIGHTNESS`: REST v2 `PATCH /api/v2/surfaces/v1/{id}`;<br>– `COMPANION ADD`: REST v2 `POST /api/v2/connections/v1`.<br>The REST v2 API key is kept in the credential store (`CREDENTIAL SET companion <key>`, this machine only) | `CompanionLinkTests` against a fake Companion built from `openapi-stable.json`'s routes [S11]: a press reaches the right path; a 401 becomes `ERR` with words; `ADD` posts `{moduleId:"jammin808-playmap",label,updatePolicy:"stable"}`, then PATCHes `config {node, transport}`. **The pairing token is sent only when Companion is on loopback**; otherwise the reply tells the operator to type it into Companion's secret field |
| S3 | **Selection** published by the desk: `SELECT <id>…\|NONE` (Low, not journalled) → `STATE.selection {by, ids}`. Learn and the Live rule `selection` use it | Desk-side test: selecting a surface in Map sends `SELECT sf…`. Module learn for `v_surface_source` fills `surface` |
| S4 | **Show Bus hello fallback discovery** in the module: `createSharedUdpSocket` on UDP 9928 lists PlayMap nodes heard in a config dropdown when mDNS is blocked | Module test with a fake shared socket; a real-socket test is marked untested on one machine |
| S5 | **Desk surface**: Network drawer "Decks" (from `STATE.clients`), "Identify deck" (an `EVENT deck.identify` that flashes that deck's keys), "Add PlayMap to Companion" (S2), "Dim decks with the show strip" (S2) | Desk view-model tests over a recorded STATE |

### Later (named so nobody builds them by accident)

- **The full Navigator.** `NAV` and `MENU` descriptor protocol 1 with Patterns' `nav.js` shared verbatim. It needs the
  desk's right-click menus moved into a Core `EntityMenus` table; that is its own package.
- **Satellite button subscriptions**: Companion buttons shown inside PlayMap's desk and `/remote` (`ADD-SUB`,
  `SUB-STATE`). Companion keeps this off by default because it bypasses PIN lockout [S14][S12a].
- **Pair by approval**: per-client keys with roles, minted on a desk approval and handed over wss only. It depends on
  showsafety's roles and on R3.14.
- **Generated `.companionconfig` page exports.** The export schema is untyped, versions 6–17 [S39].
- **A Bitfocus Buttons build on API 2.1** if Buttons cannot load 2.2 modules [S26, unverified].
- **Back-port to Patterns' module**: template groups by id, value feedbacks, secret token, `@id` matching, the shared
  composites. This is offered to Patterns' maintainers, not done in PlayMap's tree.

---

## 3. Architecture

### 3.1 The whole loop

```
 Stream Deck / Loupedeck / web buttons
        │ press, rotate ($(this:delta))
 Companion 5.1 ── host (InstanceWrapper) ── PlayMapInstance (Node 22, base 2.2.1)
                                              │
            ┌─────────────────────────────────┴───────────────────────────────┐
            │ Link: WsChannel (ws://|wss:// :9940/ws, "playmap.lines.v1")      │
            │       TcpChannel (TCPHelper :9941)  ← fallback for older nodes   │
            │ Session: NONCE→HELLO→AUTH-PROOF, @c<n> ids, receipts, PING/offset │
            └──────┬───────────────────────────────┬──────────────────────────┘
       lines out   │                               │  lines in
   "@c7 PLAYLIST GO pl… it…"                       │  STATE{…} INDEX{…} CONTEXT{…} THUMBS{…}
                   │                               │  EVENT{…} ACTION … OK @c7 … ERR @c8 …
 ──────────────────┼───────────────────────────────┼──────────────────────── network
 PlayMap engine    ▼                               │
  HttpHost /ws ─ WebSocketLineChannel ┐            │
  WireServer 9941 ─ StreamLineChannel ┴─ LineSessions (one table, one push fan-out)
                                         │   ├─ CommandRunner → EngineCore (control lane, router, gates)
                                         │   ├─ STATE/CONTEXT/PROJECT throttles (existing)
                                         │   ├─ IIndexSource  → INDEX push (on sig/labels change)
                                         │   ├─ IThumbSource  → THUMB replies, THUMBS push
                                         │   ├─ IEventSource  → EVENT lines
                                         │   └─ IActionFeed   → ACTION lines (RECORD ON)
  NetworkHost ─ MdnsResponder (_playmap._tcp on 9940, UDP 5353)
```

### 3.2 Node side

**3.2.1 One line session, two channels.** `WireSession` is split from its socket:

```csharp
// src/PlayMap.Net/Common/ILineChannel.cs
internal interface ILineChannel : IAsyncDisposable
{
    string Kind { get; }                       // "tcp" | "ws"
    IPAddress? Remote { get; }
    ValueTask<string?> ReadLineAsync(CancellationToken ct);   // null at the end; InvalidDataException past the limit
    ValueTask WriteLineAsync(string line, CancellationToken ct); // one line, no terminator (the channel frames it)
    void Abort();
}
```

- **`StreamLineChannel`** wraps today's `LineReader` and the `NetworkStream` (it writes `line + "\n"`).
- **`WebSocketLineChannel`** maps one text frame to one line. A trailing `\n` is tolerated. A binary frame closes with
  1003; a frame over 64 KiB closes with 1009.
- **`LineWriter`** keeps its rules (ordered replies, latest-wins pushes, deadlines). It now writes through the
  channel. `PushKind` gains `Index` and `Thumbs`.
- **`LineSessions`** (new) is extracted from `WireServer`. It holds the session table, the STATE/CONTEXT/PROJECT
  throttles and the new pushes, and is constructed once by `NetworkHost`.
  - `WireServer` (TCP) and `HttpHost` (WS) each call `LineSessions.ServeAsync(ILineChannel, ct)`.
  - If either door fails to bind, the other still serves lines.

**3.2.2 The WebSocket gate.** In `HttpHost.GateAsync`, a request to `/ws` that is a WebSocket upgrade and offers
`playmap.lines.v1`:

- **skips** the header-token check. The line session gates it instead: until paired, only `NONCE`, `HELLO`,
  `AUTH-PROOF` and `PING` are answered (this is **stricter** than TCP 9941, where queries are open). The HELLO
  greeting `STATE` push and every subscription wait until the session is paired; `OK paired` is followed by the
  greeting `STATE`;
- **keeps** `NeedsHttps` (403) and the per-address wrong-token lockout. Wrong proofs count against the address in the
  same `TokenGate` ledger.

The upgrade is accepted with `new WebSocketAcceptContext { SubProtocol = "playmap.lines.v1", KeepAliveInterval =
TimeSpan.FromSeconds(15) }`. The existing JSON `/ws` mode (no subprotocol) is unchanged.

**3.2.3 Transport inference.** In `WireSession.Info()`, `_helloName` starting with `Companion:` or equal to
`Companion` gives transport `"companion"`.

- `PlayMapDesk` → `desk` only on a **TCP** channel (today's rule, which keeps remote desks working). A WS line channel
  never yields `desk`: the desk links over TCP, and this keeps the most-privileged role off the web port.
- `SessionInfo` gains `public string Channel { get; init; } = "tcp";` (an init property, so positional constructors
  are unchanged).

**3.2.4 INDEX.** `IndexWriter.Write(ProjectSnapshot) → (string Json, string Sig, string Labels)` is cached per project
revision. `Sig` = SHA-256 (first 16 hex) over the ordered ids and kinds of every listed entity, plus each item's source
kind and each generator's param names. `Labels` = SHA-256 over the names and labels.

- `EngineCore` implements `IIndexSource`.
- `LineSessions` pushes `INDEX {…}` to subscribers when `Sig` or `Labels` changes. The check runs on the project
  throttle, so a warp-node drag does not push.
- The shape is in §3.5.2.

**3.2.5 Thumbnails.** `ThumbnailService` (Engine) implements `IThumbSource`.

- **Refs** are a `SourceRef` text (`media:md…`, `generator:gn…`, `canvas:cv…`, `playlist:pl…` for its current item,
  `color:#RRGGBB`, `live:…`, `timer:…`) or a view ref (`output:op…`, `projector:pj…`, `camera:cm…`).
- **Media.** It first asks `IPosterStore.TryGet(contentHash, w, h)` (from the optimise WP, optional). Otherwise
  `PosterMaker` opens the item through `IMediaBackend.Open`, prepares at 10 % of its length (≤ 3 s), waits ≤ 2 s
  for `IsReadyAt`, and acquires one frame.
  - `FrameToBgra` converts NV12, I420, P010, BGRA and RGBA to BGRA with a box-filtered downscale in one pass. It is
    pure C#, with no SIMD needed at 96 px.
  - The poster is written to `NodeHome/thumbs/<sha256[0..16]>/<w>x<h>.<jpg|png>`, so a restart never decodes again.
  - At most 2 PosterMaker jobs run at once, at `BelowNormal` priority.
  - **While armed, only cached posters are served.** An uncached one is refused: `ERR no picture of it yet — posters
    are made in rehearsal or on import`.
- **Colour and solid generators** are drawn with SkiaSharp (already in Render). Other generator kinds are drawn as a
  palette tile with the kind's name. A rendered still is *later*: it needs a source-still call on the render host.
- **Live refs** (`output:…`, `projector:…`) open an **internal preview view** `thumb-<ref>` through `Previews.Show`
  (`ViewKind.Output` or `ViewKind.Projector`) at the asked size.
  - The ring is read at ≤ 1 fps and the ETag moves with the ring's sequence.
  - Each internal view has a **lease**: hidden 10 s after the last `THUMB` or `THUMBS` interest. This is the carry-over
    "views never expire" fixed for this service's own views.
  - While armed: output views are allowed at ≤ 0.5 fps when `ThumbsWhileArmed` (default true); projector views (a
    full stage render) are always refused.
- **`playlist:<id>`** stands for its current item's source: that item's poster or tile, with the ETag moving when the
  item changes. A true programme picture needs a playlist view kind in the render host and is *later*.
- **Encoding.** SkiaSharp `SKImage.FromPixels(...).Encode(SKEncodedImageFormat.Jpeg, q)` with q = 80, stepping down by
  10 until the base64 fits 60 KiB; PNG for `color:` and drawn tiles.
- **ETags** are `"<kind>-<hash16>-<w>x<h>-<j|p>"`: the content hash for media, a params hash for generators, the
  ring sequence for live refs.
- **`THUMBS` push.** For each session the service keeps the watched set (≤ 128 refs). One latest-wins
  `THUMBS {"ref":"etag",…}` is sent when any watched ETag moves.

**3.2.6 Events.** `EngineEvents` (Engine) implements `IEventSource`.

- On each STATE revision (the push thread, not the render thread), it diffs a small facts record against the last
  one:
  - per playlist: state, item and seq;
  - air: blackout, frozen and pattern;
  - faults; scan state;
  - `ai.proposals`;
  - peers' live state;
  - devices' power, shutter and errors;
  - `show.armed`, `show.panic` and `show.blind` (when showsafety is present);
  - `calibration.moved[]` (when calibops is present).
- **Item transitions carry the exact time.** `PlaylistCursor` resolves when the previous item's out point fell, from
  the clip anchor, so `at` is exact even when the diff runs late. Content is a pure function of show time.
- Events go into a ring of 256 with a monotonic `seq`.

**3.2.7 The action feed.** `JournalRow` gains `public string? Transport { get; init; }` and
`public string? Session { get; init; }`.

- `HandleOnLaneAsync` and `RunOpsAsync` set both.
- `ActionFeed` subscribes to `Journal.Written`. It keeps rows where `Kind == "verb"`, `Ok`, `Line` is not null,
  `Transport is "desk" or "remote"`, the verb has no `SecretFrom`, and the verb is not one of `VIEW`, `THUMB`,
  `CREDENTIAL *`, `PAIRING NEW`, `SELECT` or `PRESENCE`.
- It raises `Performed(line)`. Sessions with `RECORD ON` get `ACTION <line>` through the ordered writer.

**3.2.8 Presence.** `ClientTable` (Engine):

- adds on HELLO, takes `PRESENCE <json>` (≤ 2 KiB, known keys only), and removes on session end;
- feeds `STATE.clients[]` and context items `client:<session>`;
- calls `FactsChanged()` on every change so STATE moves.

**3.2.9 mDNS.** `MdnsResponder` (Net/Discovery) is a minimal RFC 6762/6763 responder.

- **Socket.** UDP 5353 with `ReuseAddress` (Avahi and Bonjour share the port), joined to `224.0.0.251` on every
  up IPv4 interface. IPv6 `ff02::fb` is later.
- **Records.** It owns `_playmap._tcp.local` (PTR), `<Instance>._playmap._tcp.local` (SRV → `<host>.local:9940`,
  TXT) and `<host>.local` (A), and answers `_services._dns-sd._udp.local`.
- **Legacy unicast.** A query from a source port other than 5353 is answered by unicast to the sender, with the
  query id echoed and TTL ≤ 10 s (RFC 6762 §6.7).
- **Probing and announcing.** Three probes 250 ms apart; on conflict the instance becomes "<Instance> (2)". Two
  announcements 1 s apart. Goodbye (TTL 0) on dispose.
- **Codec.** `DnsMessage` reads with name compression and writes without (allowed).
- **Placement.** It sits behind `IServiceAnnouncer`, so the control WP's OSCQuery (`_oscjson._tcp`) adds its record
  to the same responder; whichever package lands first builds it.

### 3.3 The module

```
integrations/companion-module-playmap/src/
  main.ts              PlayMapInstance extends InstanceBase<PlayMapTypes>; default export; UpgradeScripts = []
  types.ts             PlayMapTypes { config; secrets: { token: string }; actions; feedbacks; variables; compositeElements }
  config.ts            fields, connectionTarget(), groups, defaults
  link/channel.ts      LineChannel; WsChannel (ws 8.x, subprotocol, TLS pin); TcpChannel (TCPHelper)
  link/session.ts      NodeSession: handshake, ids, receipts, ping/offset, watchdog, backoff, push dispatch
  link/pairing.ts      normaliseToken(), authProof() — byte-equal to Patterns' and PlayMap's Pairing
  state/store.ts       NodeModel: STATE, INDEX, CONTEXT, WHOAMI; slice hashes per feedback type
  state/variables.ts   variablesOf(model, now) — pure; definitions from INDEX
  state/clock.ts       ClockModel: offset estimate; interpolate(playlist, now)
  state/events.ts      event ring, pulses
  catalog/verbs.json   copy of VERBS (checked by the C# contract test)
  catalog/verbs.ts     typed view of verbs.json (generated at build, erasable TS)
  catalog/match.ts     port of VerbCatalog.Match + param parse → { actionId, options }
  catalog/write.ts     options → line (WireGrammar.Quote rules)
  actions/*.ts         generated verb actions; smart actions; encoders; results; learn
  feedbacks/*.ts       boolean and value feedbacks
  presets/*.ts         structure (sections, groups, templates), keys (layered), composites, alternatives
  thumbs/cache.ts      LRU by ETag; in-flight dedupe; placed-ref tracking
  live/livepage.ts     rules → slots; freeze while armed
  palette/palette.js   vendored from Patterns (SHA-256 pinned)
  palette/playmap.ts   PlayMap kinds (adds only)
  words.ts             status words, trustProblem()
```

**Threads.** A Companion module is one Node event loop. Nothing blocks:

- `JSON.parse` of a 50 KB STATE takes ~0.3 ms on a desktop and ~2 ms on a Pi 4 *(estimate)*;
- thumbnail decode is the node's job;
- the module only base64-wraps.

### 3.4 Data flows

**Connect.**

1. Open `ws(s)://host:9940/ws` with `['playmap.lines.v1']`. Under wss, check `fingerprint256` on `upgrade` before any
   line is sent.
2. If the node sends `NONCE n` → send `@c1 HELLO Companion:<label> module=<v>`, then
   `@c2 AUTH-PROOF <hex(HMAC-SHA256(norm(token), n + "Companion:<label>"))>`.
3. On `OK @c2 paired` (or `OK open …` on an open node) → in order:
   - `@c3 PRESENCE {…}`;
   - `@c4 WHOAMI`;
   - `@c5 VERBS`;
   - `@c6 SUBSCRIBE INDEX`, `@c7 SUBSCRIBE CONTEXT`, `@c8 SUBSCRIBE EVENTS`;
   - `@c9 EVENTS SINCE <last seq>`;
   - `@c10 SUBSCRIBE THUMBS <placed refs>`;
   - and, if Companion is recording, `@c11 RECORD ON`.
4. The greeting STATE arrives right after `OK paired`. On TCP it arrived after HELLO already.
5. **Upgrade refused with 400 or 404** (an older node) → fall back to TCP `host:<wire>`, where `wire` comes from config
   or 9941. Same lines.

**STATE in.**

1. Parse → `NodeModel.state`.
2. For each feedback type, recompute its slice hash (e.g. `playlist_state` hashes `playlists[].{id,state,index,
   item.id}`) and collect the types whose hash moved.
3. Compute `variablesOf()` and diff it with the last emitted map, keeping changed keys only.
4. Queue both into the **flush timer** (40 ms, 25 Hz max): `setVariableValues(changed)`, then
   `checkFeedbacks(...types)`.
5. If `show` or `clients` moved → `WHOAMI` again (debounced 100 ms).
6. If `project.index` ≠ the last INDEX's `sig` and no INDEX push came within 500 ms → ask `INDEX` (covers a node
   without `SUBSCRIBE INDEX`).

**INDEX in.**

- If `sig` changed: rebuild the variable definitions, action and feedback dropdown choices, and preset structure.
  Call `setVariableDefinitions`, `setActionDefinitions`, `setFeedbackDefinitions`, then `setPresetDefinitions`
  **once each, batched**, as [S24] asks.
- If only `labels` changed: re-send structure (template names) **debounced 3 s**. Keys relabel at once through value
  feedbacks.
- **The preset definitions themselves never change with the project.** Only the structure's template values and
  groups do, so linked buttons are not rebuilt [S16].

**Press.**

1. Companion calls the action's callback with evaluated options (expressions are Companion's [S18]).
2. The module writes the line (`catalog/write.ts`), assigns `@c<n>`, records `{controlId, line, sentAt}` and sends it.
3. The `receipt` feedback for that `controlId` shows *pending*.
4. Reply:
   - `OK @cN {"stage":"accepted",…}` → pending until STATE shows the effect or 10 s pass;
   - `OK @cN …` → applied, mark cleared;
   - `ERR @cN words` → failed: red for 2 s, and `last_refusal` = words.
5. Actions declared `hasResult` resolve with `{ ok, stage, effect, error, reply }`.
6. **The key's main light changes only when STATE says so.**

**Thumbnails.**

1. The `thumb` value-feedback callback receives `{ref, size}`. Cache hit by `(ref, size)` with a known ETag → return
   the data URI at once.
2. Miss → return `''`, then dispatch `THUMB ref WxH [IF etag]` without awaiting, store the result, and
   `checkFeedbacksById(fb.id)`.
3. `unsubscribe(fb)` drops the ref from the placed set. Placed-set changes → `SUBSCRIBE THUMBS …`, debounced 250 ms.
4. A `THUMBS` push with a new ETag for a placed ref → refetch, then `checkFeedbacksById` for its feedbacks.
5. The LRU holds 256 entries (~3 MB at 96 px).

**Encoders.**

- Rotations within 30 ms coalesce. `delta` is the sum of `$(this:delta)`, or ±1 from `rotate_left` and
  `rotate_right` on older surfaces.
- Absolute verbs send `clamp(stateValue + delta × step)`, where `stateValue` is the last STATE value plus deltas
  already sent and not yet confirmed. That *shadow* is used only to compute the next target, never shown.
- Relative verbs (`WARP NODE dx dy`, `DEVICE LENS … BY n`) send `delta × step`.
- While disconnected, a turn does nothing.

### 3.5 Formats

**3.5.1 The line channel.**

- Subprotocol: `playmap.lines.v1`.
- Framing: one UTF-8 text frame = one line ≤ 64 KiB, grammar exactly `WireGrammar` (`@id`, quoting, JSON-to-end).
- Close codes: 1000 normal; 1003 binary; 1009 too big; **4401** five wrong proofs; 1001 node stopping.

**3.5.2 INDEX** (one line; keys added at the end of objects only):

```json
INDEX {"v":1,"revision":118,"sig":"9f1c2ab07d3e4410","labels":"77aa0c1e93b2d5f0",
 "playlists":[{"id":"pl1a2b3c4d","name":"Show","loop":false,"syncGroup":null,
   "items":[{"id":"it0a1b2c3d","label":"Intro","source":"media:md5e6f7a8b","kind":"video","lengthMs":210000}]}],
 "surfaces":[{"id":"sf…","name":"Wall","tags":["wall"],"source":"playlist:pl…"}],
 "projectors":[{"id":"pj…","name":"Left","output":"op_1","device":"dv…"}],
 "outputs":[{"id":"op_1","name":"Output 1","kind":"display","projector":"pj…"}],
 "devices":[{"id":"dv…","name":"Left box","protocol":"pjlink","projector":"pj…","lens":["SHIFTH","SHIFTV","ZOOM","FOCUS"]}],
 "generators":[{"id":"gn…","name":"Sparks","kind":"particles.sparks",
   "params":[{"name":"rate","kind":"number","min":0,"max":2000,"step":1}]}],
 "canvases":[{"id":"cv_main","name":"Main canvas","layers":[{"id":"ly_base","name":"Test card"}]}],
 "media":{"count":12},
 "keys":[{"id":"kp…","name":"FOH","cols":8,"rows":4,"rule":"","slots":[{"i":1,"label":"Intro","line":"PLAYLIST GO pl… it…","ref":"it…"}]}]}
```

`media` lists only a count: items reach keys through playlists, and a 5000-file library is never pushed.

**3.5.3 THUMB.**

```
→ @c41 THUMB media:md5e6f7a8b 96x96 JPEG IF "media-5e6f7a8b9c0d1e2f-96x54-j"
← OK @c41 {"ref":"media:md5e6f7a8b","etag":"media-5e6f7a8b9c0d1e2f-96x54-j","same":true}
← OK @c41 {"ref":"…","etag":"…","mime":"image/jpeg","w":96,"h":54,"data":"/9j/4AAQ…"}
← ERR @c41 no picture of it yet — posters are made in rehearsal or on import
```

`GET /api/thumb?ref=…&w=96&h=96&fmt=jpeg` (token gate) answers the bytes with `ETag` and honours `If-None-Match` → 304.

**3.5.4 Pushes added.**

| Push | Shape | Delivery |
|---|---|---|
| `INDEX {…}` | as above | latest-wins |
| `THUMBS {…}` | `{"media:md…":"<etag>","output:op_1":"<etag>"}` (watched refs whose ETag moved since last sent) | latest-wins |
| `EVENT {…}` | `{"seq":412,"at":1760101234567,"kind":"item.end","playlist":"pl…","item":"it…","words":"Show: Intro ended"}` | ordered, ≤ 64/s, then `{"kind":"overflow","dropped":n}` |
| `ACTION <line>` | the redacted journal line | ordered, while `RECORD ON` |

**Event kinds:**

- `item.start`, `item.end`, `playlist.end`, `playlist.cued`;
- `air.blackout`, `air.freeze`, `air.pattern`;
- `show.armed`, `show.disarmed`, `show.blind`, `show.commit`, `panic` (with `outcome`);
- `fault.raised`, `fault.cleared`;
- `scan.started`, `scan.done`, `scan.failed`;
- `calibration.moved`, `calibration.recalled`;
- `proposal.new`, `proposal.decided`;
- `peer.lost`, `peer.found`;
- `device.fault`, `device.power`;
- `media.ready`, `media.missing`;
- `deck.identify` (with `session`).

**3.5.5 WHOAMI and PRESENCE.**

```
→ @c4 WHOAMI
← OK @c4 {"session":"w12","transport":"companion","channel":"ws","client":"Companion:FOH deck","paired":true,
          "role":"remote","armed":true,"may":["VIDEO PLAY","VIDEO PAUSE","PLAYLIST GO","PLAYLIST NEXT","PLAYLIST PREV",
          "BLACKOUT","FREEZE","PANIC"],"minGoMs":300,"proto":1}
→ @c3 PRESENCE {"kind":"companion","module":"1.0.0","live":"projectors","page":"1/3","recording":false,"thumbs":12}
← OK @c3
```

`may` is `"*"` when nothing is gated.

### 3.6 The Live page (live auto-created buttons)

- **Install once.** One template group of 32 slot presets (`live_slot`, template variable `n` = 1…32). The operator
  drags the group onto a page; it is linked by default (5.1).
- **Source.** Every slot's feedbacks and actions carry option `rule` = expression `$(page:pm_live)`. One page variable
  set on that page picks its source. Empty → the connection's current rule, set by the `live_source` action or config.
- **Rules,** evaluated **in the module** from INDEX, STATE and CONTEXT:
  - `focused` and `playlist:<id>` — items: GO, with thumbnail and progress;
  - `playlists` — transport keys;
  - `projectors` — tile: power, shutter, temperature ring, score; press → identify; hold → shutter;
  - `outputs` — light and fps; press → identify;
  - `surfaces` — shown and hidden; press → toggle (High → hold);
  - `devices`;
  - `peers` — fleet;
  - `proposals` — approve on hold, reject on double press;
  - `faults` — words; press → `EYE FOCUS` when present;
  - `keys:<kp>` (S1);
  - `selection` (S3).
- **Paging.** `live_page_next` and `live_page_prev`; the variable `live_page` reads "2/5".
- **Slot data:** `{label, sub, light, thumbRef, line, risk, needsHold}`.
- **Press.** A short press sends `slot_press {n, held:false}`; a hold past 800 ms sends `slot_press {n, held:true}`.
  The module sends the slot's line only if `held || !needsHold`. Otherwise `slot_hint` shows "HOLD" for 1 s.
  `needsHold` comes from the verb's risk and the transport set (§3.7).
- **Frozen while armed.** While `show.armed`, the slot → entity map **freezes**: lights, labels and thumbnails still
  update, positions do not. A slot whose entity is gone shows REMOVED in grey. `live_refresh` (or disarming) re-lays
  the page.

### 3.7 Risk-shaped presses (generated)

| Class | Verbs | Preset shape |
|---|---|---|
| Instant (never confirmed, UX §7.2) | `PLAYLIST GO`, `PLAYLIST NEXT`, `PLAYLIST PREV`, `VIDEO PLAY`, `VIDEO PAUSE`, `BLACKOUT`, `FREEZE`, `PANIC` | `down: [action]` |
| Hold 0.8 s | every other `High` verb, and `Review` verbs that change the air (`SURFACE SHOW`, `OUTPUT IDENTIFY`) | `{ down: [], up: [], 800: { options: { runWhileHeld: true }, actions: [action] } }` with `internal:buttonPushed` → amber "HOLD…" |
| Two presses plus hold | `PROJECT OPEN`, `PROJECT NEW`, `SCAN START`, `OUTPUT ENABLE … OFF`, `DEVICE POWER … OFF`, `APPROVE` | the 0.8 s hold group calls the action with option `confirm: 'twice'`. The **module** arms that control on the first call: feedback `confirm_pending` shows amber "SURE?", and the module clears it after 3 s with `checkFeedbacksById`. Only a second held press within 3 s sends. The confirm lives in the module because the internal actions a preset may use (`wait`, `customLog`, `abortButton`, `localVariableSet`) cannot reset a button's step. This replaces Patterns' step-based TAKE OVER, which never expires |

Per-argument risk is used where the node publishes it (`params[].riskWhen`, from the carry-over "per-argument risk";
owned by showsafety). Otherwise the verb's risk applies.

### 3.8 Performance budgets

| Path | Budget |
|---|---|
| STATE frame → `setVariableValues` (module) | ≤ 15 ms p95 desktop, ≤ 50 ms p95 Pi 4 (estimate), 50 KB STATE |
| Variable updates | coalesced at 40 ms (≤ 25 Hz); Companion caps a module at 50 Hz [S1] |
| Interpolation ticker | 10 Hz only while a playlist plays and a progress or remaining consumer is placed; text changes ≤ 1 Hz, gauges when Δ ≥ 0.005 |
| INDEX → `setPresetDefinitions` | ≤ 100 ms for 500 items in 20 playlists |
| Press → line on the wire | ≤ 5 ms in the module |
| Press → applied receipt (LAN) | ≤ 20 ms typical (node lane) |
| Thumbnail (node) | cache hit ≤ 2 ms; poster from disk ≤ 5 ms; fresh decode ≤ 250 ms; reply ≤ 60 KiB; live ≤ 1 fps per ref, ≤ 8 live refs per node |
| Node line WS | no per-frame work; the existing 200 ms trailing STATE throttle and ≥ 1 Hz while playing |
| mDNS | ≤ 1 datagram per query; announcements 2 at start |
| Module memory | ≤ 40 MB with 256 thumbnails |

---

## 4. Spine needs

### 4.1 Model (all backwards compatible: init properties with defaults, omitted from JSON when default)

```csharp
// src/PlayMap.Core/Model/KeyPageModel.cs  (S1)
public sealed record KeySlot(int Index, string Label, string Line, string? Ref);           // Index 1-based
public sealed record KeyPage(string Id, string Name, int Columns, int Rows, string Rule,
    ImmutableArray<KeySlot> Slots) : IEntity;                                              // JSON "keys"
// Project:  public ImmutableArray<KeyPage> Keys { get; init; } = [];
// Ids:      public const string KeyPage = "kp";
// ADD keypage {json}, SET kp… name|columns|rows|rule <json>, REMOVE kp… work through the generic edits.
```

- **Journal:** `JournalRow` gains `public string? Transport { get; init; }` and `public string? Session { get; init; }`.
  The JSONL file adds `"transport"` and `"session"` when present.
- **Session:** `SessionInfo` gains `public string Channel { get; init; } = "tcp";`.

### 4.2 Verbs (added to `VerbCatalog.All`; params in `VerbParams`)

| Verb | Syntax | Cat. | Risk | Mutates | Reply |
|---|---|---|---|---|---|
| `INDEX` | `INDEX` | Session | Low | no | `OK {INDEX json}` |
| `SUBSCRIBE` (syntax widened) | `SUBSCRIBE STATE\|CONTEXT\|PROJECT\|INDEX\|EVENTS\|THUMBS [<ref>…]\|OFF` | Session | Low | no | `OK subscribed <what>`, then the push. `THUMBS` replaces the watched set (≤ 128) |
| `EVENTS` | `EVENTS [SINCE <seq>]` | Session | Low | no | `OK {"seq":N,"events":[…]}` (≤ 256) |
| `THUMB` | `THUMB <ref> [<w>x<h>] [JPEG\|PNG] [IF <etag>]` | Media | Low | no (paired only, like VIEW) | §3.5.3 |
| `RECORD` | `RECORD ON\|OFF` | Session | Low | no (paired only) | `OK recording` / `OK not recording`; `ACTION <line>` pushes |
| `PRESENCE` | `PRESENCE <json>` | Session | Low | no (paired only) | `OK` |
| `WHOAMI` | `WHOAMI` | Session | Low | no | §3.5.5 |
| `KEYS LIST` (S1) | `KEYS LIST` | Project | Low | no | `OK [{"id","name","cols","rows","rule","slots":n}]` |
| `KEYS NEW` (S1) | `KEYS NEW <name> [<cols>x<rows>] [RULE <rule>]` | Project | Low | yes | `OK kp…` |
| `KEYS SET` (S1) | `KEYS SET <page> <slot> <json {label,line,ref}>` | Project | Low | yes | `OK` + effect. The `line` must parse with `VerbCatalog.Match` and must not be a secret-bearing verb |
| `KEYS CLEAR` (S1) | `KEYS CLEAR <page> <slot>\|ALL` | Project | Low | yes | `OK` |
| `SELECT` (S3) | `SELECT <id>…\|NONE` | Session | Low | no (paired only; not journalled) | `OK`; `STATE.selection` |
| `COMPANION LINK` (S2) | `COMPANION LINK <host[:port]>\|OFF` | Network | Review | yes | accepted; `links.companion` |
| `COMPANION PRESS` (S2) | `COMPANION PRESS <page>/<row>/<col> [DOWN\|UP\|ROTATE-LEFT\|ROTATE-RIGHT]` | Network | Review | yes | applied with Companion's HTTP status, or `ERR` with words |
| `COMPANION VAR` (S2) | `COMPANION VAR <name> <value>` | Network | Low | yes | applied |
| `COMPANION PAGE` (S2) | `COMPANION PAGE <surface>\|ALL <page>` | Network | Review | yes | applied, or `ERR Companion's TCP remote control is off — Settings → Protocols` |
| `COMPANION BRIGHTNESS` (S2) | `COMPANION BRIGHTNESS <0-100>\|RESTORE` | Network | Low | yes | applied (REST v2 key needed) |
| `COMPANION ADD` (S2) | `COMPANION ADD [<label>]` | Network | Review | yes | accepted, then a journal row with the new connection id |

**Typed params** (M9). The table is shared with the control WP's bindings; whichever package lands first owns it:

```csharp
// src/PlayMap.Core/Wire/VerbParams.cs
public enum ParamKind { Entity, Enum, Number, Integer, Text, Json, Time, Ref, Line, Flag }
public sealed record VerbParam(string Name, ParamKind Kind, bool Optional = false)
{
    public string? Prefix { get; init; }                  // Entity: "pl", "it", "sf", "pj", "op", "dv", "gn", "kp"…
    public IReadOnlyList<string>? Values { get; init; }   // Enum: "ON","OFF","TOGGLE"…
    public double? Min { get; init; } public double? Max { get; init; } public double? Step { get; init; }
    public string? Keyword { get; init; }                 // "ON" <playlist>, "AT" <time>, "FROM" <node>, "MODE" …
    public string? Alt { get; init; }                     // "#<n>" for an item by position
    public bool Repeats { get; init; }                    // SCAN START <camera> <projector>…
    public IReadOnlyList<RiskWhen>? RiskWhen { get; init; } // per-argument risk (showsafety fills)
}
public sealed record RiskWhen(string Value, VerbRisk Risk);
public static class VerbParams { public static IReadOnlyList<VerbParam> For(string verbName); }
```

`VERBS` and `/api/verbs` rows gain
`"params":[{"name":"playlist","kind":"entity","prefix":"pl"},{"name":"item","kind":"entity","prefix":"it","optional":true,"alt":"#<n>"},{"name":"at","kind":"time","keyword":"AT","optional":true}]`.

### 4.3 STATE and context keys

Keys are added at the end of their objects, per WIRE.md's rule.

| Key | Shape | Owner |
|---|---|---|
| `clients` (top level, after the last key) | `[{"session":"w12","name":"Companion:FOH deck","kind":"companion","transport":"companion","channel":"ws","address":"10.0.0.7","paired":true,"module":"1.0.0","since":1760101234567,"recording":false,"where":"Live · projectors 1/3","thumbs":12}]` | this WP |
| `project.index` | `"9f1c2ab07d3e4410"` (INDEX `sig`) | this WP |
| `events` (top level) | `{"seq":412}` | this WP |
| `selection` (S3) | `{"by":"Desk","ids":["sf…"],"kind":"surface"}` or null | this WP |
| `links.companion` (S2) | `{"state":"live\|down\|off","host":"10.0.0.9:8000","rest":true,"tcp":false,"surfaces":3}` | this WP |
| `show` | `{"armed","by","since","blind","panic","minGoMs","locks":[…]}` | **showsafety** (read tolerantly; absent = rehearsal) |
| `calibration` | `{"score","grade","words","worst","moved":[pj…],"history":n}` per projector | **calibops** |
| `fleet` | per-node health | **fleet** |
| `audio` | `{"level","bpm","beat"}` | **audio** |
| Context items | `client:<session>`, kind `deck`, light green (paired and live) or amber (connected, not paired), words `"module 1.0.0 · Live: projectors · recording"` | this WP |

### 4.4 Seams (C#)

```csharp
// src/PlayMap.Net/Contracts.cs — optional interfaces an IWireEndpoint may implement (checked with `is`)
public interface IIndexSource
{
    (string Json, string Sig, string Labels) Index();
    event Action? IndexChanged;
}
public interface IEventSource
{
    long LastEventSeq { get; }
    IReadOnlyList<string> EventsSince(long seq, int max);   // compact JSON objects
    event Action<long, string>? EventRaised;                // seq, json
}
public interface IActionFeed { event Action<string>? Performed; }   // a redacted line an operator performed
public interface IClientRegistry
{
    void Hello(SessionInfo session, string? module);
    string? Presence(string sessionId, string json);        // null = taken; else why not
    void Gone(string sessionId);
}
public enum ThumbFormat { Jpeg, Png }
public sealed record ThumbRequest(string Ref, int Width, int Height, ThumbFormat Format, string? IfNoneMatch);
public sealed record ThumbResult(bool Ok, string? Etag, string? Mime, int Width, int Height,
    ReadOnlyMemory<byte> Bytes, bool NotModified, string? Why);
public interface IThumbSource
{
    ValueTask<ThumbResult> ThumbAsync(ThumbRequest request, SessionInfo session, CancellationToken ct);
    void Watch(string sessionId, IReadOnlyCollection<string> refs);   // empty = none
    event Action<string, IReadOnlyDictionary<string, string>>? EtagsMoved;   // sessionId, ref → etag
}

// src/PlayMap.Net/Discovery/IServiceAnnouncer.cs
public sealed record ServiceRecord(string Instance, string Type, int Port, IReadOnlyDictionary<string, string> Txt);
public interface IServiceAnnouncer : IAsyncDisposable
{
    void Announce(ServiceRecord record);
    void Withdraw(string instance, string type);
}

// src/PlayMap.Engine/Seams.cs (additions; optional services as EngineServices init properties in their own file)
public interface IPosterStore    // the optimise WP implements; absent = PosterMaker decodes
{
    bool TryGet(string contentHash, int maxWidth, int maxHeight, out ReadOnlyMemory<byte> jpeg, out int width, out int height);
}
public interface IShowGate       // showsafety implements; absent = rehearsal, everything allowed
{
    bool Armed { get; }
    string RoleOf(SessionInfo session);
    IReadOnlyCollection<string>? Allowed(SessionInfo session);   // null = every verb
    int MinGoMs { get; }
}
// EngineServices.Companion.cs:
//   public IPosterStore? Posters { get; init; }
//   public IShowGate? ShowGate { get; init; }
```

### 4.5 NodeSettings additions (init properties, defaults keep today's behaviour)

```csharp
public bool Mdns { get; init; } = true;                 // announce _playmap._tcp
public bool ThumbsWhileArmed { get; init; } = true;     // cached posters always; live output thumbs ≤ 0.5 fps
public string CompanionHost { get; init; } = "";        // S2: "" = no Companion link
public int CompanionHttpPort { get; init; } = 8000;
public int CompanionTcpPort { get; init; } = 16759;
// The REST v2 API key: credential store reference "companion" (never in settings).
```

### 4.6 The module's own contracts

**Manifest:**

```json
{ "type":"connection","id":"jammin808-playmap","name":"jammin808-playmap","shortname":"playmap",
  "description":"PlayMap media server and projection mapping: playlists, surfaces, projectors, calibration, fleet health and AI proposals as live keys with thumbnails; finds the node by itself; safe in Show Mode",
  "version":"1.0.0","repository":"https://github.com/jammin808/Mapper",
  "bugs":"https://github.com/jammin808/Mapper/issues","maintainers":[{"name":"jammin808"}],"legacyIds":[],
  "runtime":{"type":"node22","api":"nodejs-ipc","apiVersion":"2.2.1","entrypoint":"../dist/main.js"},
  "manufacturer":"jammin808","products":["PlayMap"],
  "keywords":["playmap","projection mapping","media server","playlist","projector","patterns"],
  "bonjourQueries":{"node":{"type":"playmap","protocol":"tcp"}} }
```

The manifest also requires `license` (an SPDX id). It is added when the owner names the module's licence, before this
package is built.

**Config:**

- `node` (`bonjour-device`);
- `host` (`textinput`, `isVisibleExpression: "!$(options:node)"`);
- `port` (9940);
- `channel` (`ws` / `wss` / `tcp`);
- `wire_port` (9941);
- `tls_pin` (`textinput`, shown for wss; trust on first use, then saved by `saveConfig`);
- `g_<group>` ticks: show, playlists, surfaces, projectors, outputs, devices, calibration, scan, fleet, eye, live,
  encoders, ai, links, content;
- `bank_size` (8), `thumb_size` (96; 72–144), `min_go_ms` (300), `interpolate` (true).

**Secrets:** `token` (`secret-text`).

**Status words** (shared with Patterns, `words.ts`):

| Situation | Status | Words |
|---|---|---|
| No target | `BadConfig` | "Pick the PlayMap node on the network, or type its address" |
| `NONCE` and no token | `BadConfig` | "This node asks for its pairing token — the Network drawer on the PlayMap desk" |
| `ERR wrong token` | `AuthenticationFailure` | "The pairing token is not this node's — …" |
| 403 at upgrade | `BadConfig` | The node's own words from the response's `error` (TLS required, a refused `Origin`, or a host name it does not answer to), never a guess |
| Pin mismatch | `AuthenticationFailure` | "The node's certificate is not the pinned one — check the fingerprint in its Network drawer, then clear the pin" |
| `proto` > known | `UnknownWarning` | "Node speaks line protocol N, this module knows 1 — update the module" |
| Stale | `UnknownWarning` | "No answer from the node for 4 s — keys dimmed" |

**Variables** (sanitised with `sanitizeVariable()`; ids `^[a-zA-Z0-9-_.!]+$` [S20]):

- **Node:** `node_name`, `node_id`, `node_version`, `node_protocol`, `module_behind`, `link`, `light`, `headline`,
  `project`, `project_dirty`, `revision`, `faults_count`, `fault_1`…`fault_3`, `renderer`, `clients`.
- **Show:** `show_mode` (ARMED / REHEARSAL), `show_by`, `blind`, `panic`.
- **Air:** `blackout`, `frozen`, `pattern` (STATE `air.pattern` is an **object**; the module reads `.kind`, per the
  carry-over), `identify`.
- **Video row, Patterns' names kept** so a key built for Patterns' VT clock works here: `video_file`, `video_tag`,
  `video_position`, `video_length`, `video_remaining`, `video_remaining_s`, `video_chip`, `video_text`,
  `video_playing`, `video_progress`.
- **Focus:** `focused`, `focused_name`, `current`, `current_id`, `next`, `next_id`.
- **Media:** `media_ready`, `media_missing`, `transfers`, `transfer_progress`.
- **Scan:** `scan_state`, `scan_stage`, `scan_progress`, `scan_words`.
- **Sync:** `sync_role`, `sync_leader`, `sync_uncertainty_ms` (STATE's `sync.offsetMs` means leader-time
  uncertainty, per the carry-over, so the variable is named for what it is), `sync_rtt_ms`.
- **Links:** `patterns_link`, `stagewatch_link`, `stagewatch_timer`, `stagewatch_phase`.
- **AI:** `ai_proposals`, `ai_top`, `ai_top_risk`.
- **Calibration** (when published): `cal_score`, `cal_grade`, `cal_words`, `cal_moved`.
- **Audio** (when published): `bpm`, `level`.
- **Receipts:** `last_error`, `last_refusal`, `last_receipt`.
- **Events:** `event_last`, `event_last_kind`, `event_seq`.
- **Live page:** `live_rule`, `live_page`.
- **Recorder:** `recording`.
- **Banks** (positional, N = `bank_size`):
  - `pl_<n>_{id,name,state,item,next,remaining,progress}`
  - `op_<n>_{id,name,light,fps,dropped}`
  - `pj_<n>_{id,name,light,residual_px,power,shutter,temp_c}`
  - `sf_<n>_{id,name,shown}`
  - `peer_<n>_{name,light,headline}`
- **Per id** (stable under reorder):
  - `pl.<id>.{name,state,item,next,remaining,progress}`
  - `pj.<id>.{light,power,shutter,score}`
  - `op.<id>.{light,fps}`
  - `sf.<id>.{name,shown}`
  - `peer.<id>.light`
- Definitions are re-sent only when INDEX `sig` moves.

**Feedbacks — boolean** (default styles from the palette; all usable as trigger conditions [S10]):

- **Link and show:** `link_state {state}`, `stale`, `show_armed`, `show_blind`, `panic_active`.
- **Air:** `blackout`, `frozen`, `pattern_up {output?}`, `identify`.
- **Lights:** `node_light {light}`, `peer_light {peer, light}`.
- **Playlists:** `playlist_state {playlist, state}`, `item_on_air {playlist, item}`, `item_next {playlist, item}`,
  `item_cued {playlist, item}`.
- **Outputs and projectors:** `output_light {output, light}`, `output_enabled {output}`,
  `projector_state {projector, state: on|standby|warming|cooling|shutter_closed|fault|unreachable}`,
  `projector_moved {projector}`, `calibration_grade {projector?, grade}`.
- **Surfaces and locks:** `surface_shown {surface}`, `locked {ref}`.
- **Scan, media, faults, AI:** `scan_running`, `media_ready`, `transfer_active`, `fault_any`,
  `proposal_waiting {risk?}`.
- **Safety:** `verb_locked {verb}` (from WHOAMI `may`), `go_cooldown {playlist?}`, `confirm_pending` (by the
  feedback's `controlId`).
- **Receipts:** `receipt {stage: pending|failed}` (by the feedback's own `controlId`).
- **Automations:** `event_pulse {kind, ref?, hold_ms}`.
- **Live page:** `slot_empty {rule, n}`, `slot_hint {n}`.
- **Recorder:** `recording`.

**Feedbacks — value** (2.2 typed `result`):

| Feedback | Options | Result |
|---|---|---|
| `label` | `{ref}` | string; `"REMOVED"` for an unknown id |
| `light` | `{ref}` | `'green'\|'amber'\|'red'\|'grey'` |
| `state_word` | `{ref}` | string |
| `progress` | `{playlist, item?}` | number 0–1, interpolated |
| `remaining` | `{playlist, format}` | string, interpolated |
| `thumb` | `{ref, size, live}` | data URI, or `''` |
| `score` | `{projector?}` | number 0–100 |
| `temp` | `{device}` | number °C |
| `state_path` | `{path}` | any STATE value by a dotted path with `[id]` selectors, e.g. `playlists[pl…].rate` |
| `slot` | `{rule, n, field}` | Live page slot data |
| `eye` | `{field, index?}` | Eye data |
| `proposal` | `{index, field}` | proposal data |
| `receipt_word` | — | string |

**Actions:**

- **Generated** from `verbs.json`: one per verb, id `v_<name>` (e.g. `v_playlist_go`), options from params, and
  `description` = words plus risk.
  - Entity params are dropdowns of INDEX choices with `allowCustom` (ids and expressions).
  - Excluded: session words, `CREDENTIAL *`, `PAIRING NEW`, `VIEW`, `THUMB`, `RECORD`, `PRESENCE` and `WHOAMI` (the
    module's own).
- **Smart:**
  - transport: `go_focused`, `panic {outcome}`, `blackout_toggle`;
  - Live page: `slot_press {rule, n, held}`, `live_source {rule}`, `live_page_next`, `live_page_prev`,
    `live_refresh`;
  - Eye: `eye_next`, `eye_prev`, `eye_lens {lens}`, `eye_reset` (module-side over CONTEXT, `EYE …` when the node has
    it);
  - lines: `ask_node {line}` (`hasResult`: JSON of any query); `raw {line}` (`learn` = the next `ACTION`);
  - `set_node_var` *(later, with control's modulation bus)*.
- **Encoders:** `enc_rate`, `enc_seek`, `enc_surface_opacity`, `enc_generator_param`, `enc_warp_node`, `enc_lens`,
  `enc_live`.
- **Every mutating action** has `hasResult: true` and returns `{ok, stage, effect, error}`.
  - The result resolves on the first reply (accepted, applied or refused), never on STATE, and fails after 5 s.
  - Generated actions also take `confirm: 'none'|'twice'` (default from the risk class, §3.7).
- **`learn`** on entity-taking actions fills from `STATE.selection` (S3), else the focused playlist and current item.

**Composite elements** (`setCompositeElementDefinitions`):

- `pm_tile {label, sub, light}`;
- `pm_thumb {ref, label, progress}`;
- `pm_ring {value, min, max, text, stops}`;
- `pm_countdown {playlist}`;
- `pm_lock`;
- `pm_receipt`.

They are offered to Patterns as shared composites (later).

**Palette additions** (`palette/playmap.ts`; existing colour names only, plus one new colour `playmap`;
`STATES` rows added, never changed):

```js
export const COLOURS_PLAYMAP = { playmap: [255, 120, 40] }   // a PlayMap node in Patterns' Eye (offered back)
export const STATES_PLAYMAP = {
  playlist:   { playing: 'green', paused: 'amber', cued: 'amber', ended: 'steel', idle: 'dark' },
  item:       { air: 'green', next: 'amber', cued: 'amber', missing: 'red', removed: 'gone' },
  output:     { green: 'green', amber: 'amber', red: 'red', off: 'off', pattern: 'sky' },
  projector:  { on: 'green', warming: 'amber', standby: 'dark', shutter: 'off', fault: 'red', moved: 'orange' },
  surface:    { shown: 'screenOn', hidden: 'dark', locked: 'lock' },
  scan:       { running: 'amber', done: 'green', failed: 'red' },
  calibration:{ good: 'green', fair: 'amber', poor: 'red', moved: 'orange', fallback: 'steel' },
  show:       { armed: 'red', rehearsal: 'dark', blind: 'steel', locked: 'lock', panic: 'blackout' },
  ai:         { proposal: 'music', high: 'red' },
  receipt:    { pending: 'amber', failed: 'red' },
  fleet:      { leader: 'desk', follower: 'timer', backup: 'steel', gone: 'gone', playmap: 'playmap' },
}
```

A test asserts that no PlayMap kind or colour name collides with one of Patterns'.

---

## 5. Files

### 5.1 New — node (C#)

| File | What |
|---|---|
| `src/PlayMap.Net/Common/ILineChannel.cs` | `ILineChannel`, `StreamLineChannel` |
| `src/PlayMap.Net/Http/WebSocketLineChannel.cs` | one text frame = one line |
| `src/PlayMap.Net/Wire/LineSessions.cs` | the session table and push fan-out, extracted from `WireServer` |
| `src/PlayMap.Net/Discovery/DnsMessage.cs`, `MdnsResponder.cs`, `IServiceAnnouncer.cs` | mDNS |
| `src/PlayMap.Net/Links/CompanionLink.cs` (S2) | HTTP API, TCP API, REST v2 client |
| `src/PlayMap.Core/Wire/VerbParams.cs` | `VerbParam`, `ParamKind`, `RiskWhen`, and the table for every verb |
| `src/PlayMap.Core/Model/KeyPageModel.cs` (S1) | `KeyPage`, `KeySlot` |
| `src/PlayMap.Engine/IndexWriter.cs` | INDEX |
| `src/PlayMap.Engine/EngineCore.Companion.cs` | routes `INDEX`, `EVENTS`, `THUMB`, `WHOAMI`, `PRESENCE`, `KEYS …`, `SELECT`; implements the optional interfaces |
| `src/PlayMap.Engine/EngineEvents.cs` | event detection and ring |
| `src/PlayMap.Engine/ActionFeed.cs` | journal → `ACTION` |
| `src/PlayMap.Engine/ClientTable.cs` | presence and STATE `clients` |
| `src/PlayMap.Engine/Thumbs/ThumbnailService.cs`, `PosterMaker.cs`, `FrameToBgra.cs`, `ThumbEncoder.cs`, `ThumbCache.cs` | thumbnails |
| `src/PlayMap.Engine/EngineServices.Companion.cs` | `Posters`, `ShowGate` init properties |
| `docs/COMPANION.md` | the paper |

### 5.2 New — the module

The module lives in `integrations/companion-module-playmap/`:

- `package.json`:
  - name `companion-module-jammin808-playmap`, `type: module`;
  - scripts: `build` (`node tools/gen-verbs.ts && tsc -p tsconfig.build.json`), `test` (`node --test test/*.test.ts`),
    `package` (`companion-module-build`), `check` (`companion-module-check`), `lines`;
  - dependencies: `@companion-module/base ~2.2.1`, `ws ^8.22.0`;
  - devDependencies: `@companion-module/host ^1.2.1`, `@companion-module/tools ^3.1.1`, `typescript ~6.0.3`,
    `@types/ws`, `@types/node ^22`;
  - `package-lock.json`.
- Config files: `tsconfig.json` (`erasableSyntaxOnly: true`, so tests run under Node's type stripping),
  `tsconfig.build.json`, `companion/manifest.json`, `companion/HELP.md`. The module's licence (a `LICENSE` file and
  the manifest's required SPDX `license`) is the owner's decision, taken before this package is built.
- `src/**` as in §3.3; `tools/gen-verbs.ts`.
- `test/harness.ts`: the module run through `InstanceWrapper` from `@companion-module/host` with a recording
  `ModuleHostContext`. It records:
  - definitions, variable values, feedback values, status;
  - presets, composites, recorded actions.
- `test/fake-node.ts`: a PlayMap node in Node. It speaks `ws` with the subprotocol and TCP, and implements:
  - NONCE, HMAC, `@id` receipts;
  - STATE, INDEX, CONTEXT, EVENT, ACTION and THUMB, from fixtures.
- Test files:
  - `test/*.test.ts`: link, state, variables, feedbacks, actions, presets, thumbs, live, events, recorder, encoders,
    safety, contract, package, e2e-engine;
  - `test/lines.txt`, written by `npm run lines`;
  - `test/fixtures/state.*.json` and `index.*.json`, written by a C# test from the real `StateWriter` and
    `IndexWriter`, so fixtures cannot drift.

### 5.3 New — tests (C#)

| Project | Files |
|---|---|
| `tests/PlayMap.Net.Tests` | `LineWebSocketTests.cs`, `MdnsTests.cs`, `CompanionLinkTests.cs` (S2) |
| `tests/PlayMap.Engine.Tests` | `IndexTests.cs`, `ThumbTests.cs`, `EventsTests.cs`, `ActionFeedTests.cs`, `PresenceTests.cs`, `CompanionContractTests.cs` (lines.txt, verbs.json, STATE paths read by the module, palette SHA pin, versions, fixture writer) |
| `tests/PlayMap.Core.Tests` | `VerbParamTests.cs` |

### 5.4 Touched (small hunks)

| File | Change |
|---|---|
| `src/PlayMap.Core/Wire/VerbCatalog.cs` | +12 rows; SUBSCRIBE syntax |
| `src/PlayMap.Core/Wire/Endpoints.cs` | `SessionInfo.Channel` init property |
| `src/PlayMap.Core/Bus/Pairing.cs` | NodeSettings init properties (§4.5) |
| `src/PlayMap.Core/Model/Project.cs`, `Ids.cs` | `Keys` and `kp` (S1) |
| `src/PlayMap.Net/Common/LineWriter.cs` | writes through `ILineChannel`; `PushKind.Index`, `Thumbs` |
| `src/PlayMap.Net/Wire/WireSession.cs` | channel; SUBSCRIBE kinds; RECORD; transport `companion`; `desk` only on TCP loopback |
| `src/PlayMap.Net/Wire/WireServer.cs` | listener only; delegates to `LineSessions` |
| `src/PlayMap.Net/Http/HttpHost.cs` | gate exception for the subprotocol; `/ws` branch; `/api/thumb`, `/api/index` |
| `src/PlayMap.Net/Contracts.cs` | optional interfaces (§4.4) |
| `src/PlayMap.Net/Commands/CommandRunner.cs` | `VerbJson` writes `params` |
| `src/PlayMap.Net/Commands/WireWords.cs` | new words; `Subscribe` text |
| `src/PlayMap.Engine/StateWriter.cs` | `clients`, `events`, `project.index`, `selection` (S3), `links.companion` (S2) |
| `src/PlayMap.Engine/EngineCore.Router.cs` | about 10 route lines to `EngineCore.Companion.cs` |
| `src/PlayMap.Engine/EngineCore.cs`, `EngineCore.Ops.cs` | journal rows carry transport and session |
| `src/PlayMap.Engine/Journal.cs` | `JournalRow` init properties; JSONL fields |
| `src/PlayMap.Engine/ContextBuilder.cs` | client items (one call into `ClientTable`) |
| `src/PlayMap.Engine/Host/NetworkHost.cs` | one `LineSessions` for both doors; mDNS start and stop; `CompanionLink` (S2) |
| `src/PlayMap.App/Views/NetworkDrawer*` and view models (S5) | Decks list, Identify, Add to Companion, dim decks |
| `DeskAction` lists (S1) | "Put on keys ▸" |
| Papers | `docs/WIRE.md`, `docs/SHOWBUS-NODE.md` (Companion row → the module), `docs/ROADMAP.md` (R3.4, R4.9 mDNS part), `README.md` |

---

## 6. Dependencies

### 6.1 Other work packages

| WP | What this package needs from it | Without it |
|---|---|---|
| **showsafety** | `IShowGate` (armed, role, allowed verbs, `MinGoMs`); `SHOW ARM\|DISARM`, `PANIC <outcome>` with outcomes as params enum; `STATE.show`; per-argument risk (`RiskWhen`) | WHOAMI says `role:"remote", armed:false, may:"*"`. No PANIC or Show presets (VERBS lacks them). Locks never shown. The module is fully usable in rehearsal semantics |
| **fleet** / **patterns-eye** | `EYE …` verbs; `STATE.fleet`; PlayMap in Patterns' Eye | Eye keys run module-side over CONTEXT; the fleet template uses `STATE.peers` |
| **calibops** | `STATE.calibration`, `calibration.moved` events, `CALIBRATION RECALL\|HISTORY` verbs | Score keys use `stage.projectors[].residualPx` (grade from residual: < 1 px A, < 1.5 B, < 4 C, else F) |
| **optimise** | `IPosterStore` (posters made at import, keyed by content hash) | `PosterMaker` decodes one frame (rehearsal only on an armed node) |
| **control** | Shares `VerbParams` (typed params) and `MdnsResponder` (OSCQuery's `_oscjson._tcp`); the bindings layer's modulation bus for a later `set_node_var` | This package builds both; control adds its record and uses the params |
| **audio** | `STATE.audio {level,bpm}` | Variables hidden |
| **content** | `STING`/`IMPULSE` verbs (carry-over) appear as generated actions automatically | — |
| **phonescan**, **autoalign** | scan and auto-align progress already flows through `STATE.scan` | — |

### 6.2 Third party (all MIT, all bundled)

| Package | Version | Licence | Use |
|---|---|---|---|
| `@companion-module/base` | ~2.2.1 (9 Oct 2026) | MIT | the module API |
| `ws` | ^8.22.0 | MIT | WebSocket client with subprotocol and TLS pin. Bundles under `companion-module-build` (verified here). Optional native add-ons (`bufferutil`, `utf-8-validate`) are not installed; `ws` falls back to JS. Fallback: Node 22's global `WebSocket` (present here) for `ws://` without pinning |
| `@companion-module/host` (dev) | ^1.2.1 | MIT | `InstanceWrapper` and the preset sanitiser in tests |
| `@companion-module/tools` (dev) | ^3.1.1 | MIT | build, check and licence policy (its policy refuses GPL dependencies in a module) |
| `typescript` (dev) | ~6.0.3 | Apache-2.0 | compiler (not shipped) |
| SkiaSharp (node) | 3.119.4 (already referenced by Render) | MIT | JPEG and PNG encoding of thumbnails |
| FFmpeg (node, run time) | 9 via FFmpeg.AutoGen 9.0.1.1 (existing) | LGPL/GPL runtime loaded from the user's install | poster decode (existing seam) |

Patterns' `palette.js` is vendored from the same author's MIT module and SHA-256 pinned. No new NuGet packages:
mDNS and the WS line channel are written here.

---

## 7. Verification here

### 7.1 Tested for real in this container

- **Node side** (`dotnet test`; GL tests on Xvfb :99; FFmpeg 9 from `/opt/ffmpeg9`):
  - the line WS end to end: Kestrel and a real `ClientWebSocket`, pairing, pushes, limits, close codes, RequireHttps;
  - INDEX signatures and pushes; EVENTS with a `ManualClock`; the action feed; presence and WHOAMI;
  - VerbParams against every syntax string;
  - mDNS codec and legacy-unicast answers on loopback;
  - thumbnails:
    - posters from a generated H.264 clip through the real FFmpeg backend;
    - Skia JPEG and PNG encoding;
    - ETags and `IF`;
    - armed refusal with a fake `IShowGate`;
    - live output thumbnails from the headless GL render host on Mesa llvmpipe;
  - the fixture writer for the module's tests.
- **Module** (`npm test` with Node 22.22 here; npm registry reachable):
  - run through **`@companion-module/host`'s `InstanceWrapper`**, the layer Companion 5.1 itself uses, so these are
    exercised as Companion's host does them: `init`, config, definitions, `executeAction` with options,
    `updateFeedbacks` (placing and removing feedbacks with options), `learnAction`, `startStopRecordActions` and
    upgrade scripts;
  - every preset through the host's `sanitisePresetDefinitions` (nothing dropped);
  - `validateManifest`;
  - `companion-module-check`, and `companion-module-build` → `pkg.tgz` unpacked and imported like Companion does
    (Patterns' `package.test` method);
  - against `fake-node.ts`, which is exercised over both `ws` and TCP:
    - handshake, both pairing paths, wrong token → status;
    - `@id` matching under out-of-order replies; receipts → feedback values;
    - diff rates: count `setVariableValues` calls per second under 5 Hz STATE (≤ 25) and with interpolation;
    - stale guard and reconnect with backoff;
    - template growth to 500 items under 100 ms;
    - thumbnail cache, `THUMBS` pushes and `checkFeedbacksById`;
    - Live page rules, paging and the armed freeze;
    - event pulses; typed recording (`ACTION PLAYLIST GO pl1 it2` → `recordAction({actionId:'v_playlist_go',
      options:{playlist:'pl1',item:'it2'}})`); encoder coalescing.
- **Real engine end to end:** `PlayMap engine --headless` built here plus the module through `InstanceWrapper`
  (M12).
- **C# contract tests:** `lines.txt` against `VerbCatalog.Match` and params; `verbs.json` equals `VerbJson.All`;
  every STATE path the module reads exists; the palette pin; versions.

### 7.2 Simulated, and how

| Simulated | Stand-in |
|---|---|
| Companion itself | `InstanceWrapper` plus a recording `ModuleHostContext`, so **expressions are not evaluated**. Tests pass the values Companion would evaluate `$(local:item)` and `$(page:pm_live)` to |
| Stream Deck surfaces and LED rings | none; layouts are checked as data (element ids, gauge first) |
| mDNS on a real LAN | loopback unicast (multicast on `lo` is not reliable in containers) |
| Companion's HTTP, TCP and REST v2 APIs (S2) | a fake built from the stable OpenAPI routes and the documented TCP lines [S11][S13] |
| Show Mode | a fake `IShowGate` until showsafety lands |

### 7.3 Not verifiable here (needs a real Companion 5.1, a surface, or a LAN)

- **Real Companion.** github.com is refused by this container's proxy, so the release cannot be fetched. The
  following are untested at run time:
  - how Companion **draws** layered keys, composites, thumbnails and gauges;
  - whether **feedback options given as expressions on local variables inside template presets** evaluate as
    designed (the research flagged this [C §8.19]; the design's primary path is style overrides on
    `$(this:value)`, which 5.1 documents [S6]);
  - linked-preset rebuild behaviour when template names change;
  - image cost on a 32-key XL;
  - Studio LED rings from the first gauge.

  A script `integrations/companion-module-playmap/tools/companion-e2e.sh` is provided for a developer machine: it
  starts Companion 5.1 headless with the module as a developer module, then drives `/api/location/…/press` and reads
  `/api/location/…/style`. It is not run here.
- **mDNS** against Bonjour on Windows and macOS, and Avahi on Linux, sharing port 5353.
- **Bitfocus Buttons** loading an API 2.2 module [S26, unverified].

---

## 8. Desk and Companion surface

### 8.1 What Companion shows

**Preset sections,** each a tickable group; ids are stable:

| Section | Contents |
|---|---|
| **Show** | GO (focused), NEXT, PREV, BLACKOUT, FREEZE, PANIC ×outcomes, SHOW MODE, countdown, headline. GO's ring is the current item's progress, its text is the next item's label, and it shows the next item's thumbnail |
| **Playlists** | one **template group per playlist** (`pl.<id>`) of item keys. Each key has: thumbnail, name with outline, progress bar, a frame lit by on-air (green), next or cued (amber), missing (red) or removed (grey), and a receipt dot. Plus a template group of playlist transport keys |
| **Projectors** | template tiles: light bar, power or shutter word, temperature ring, calibration grade. Press → identify (Review, hold). Hold → shutter. Plus power, input and lens-encoder keys |
| **Surfaces** | template: shown or hidden toggle (High → hold); opacity encoder |
| **Outputs** | template: light and fps; identify; pattern (hold); enable (two presses plus hold) |
| **Devices** | template over projector links |
| **Calibration** | score ring with grade and the one-line reason (`cal_words`); RE-SOLVE MOVED (hold); FALLBACK (hold); HISTORY encoder (calibops) |
| **Scan** | progress ring with stage name; CANCEL (instant); APPLY (hold) |
| **Fleet** | template over peers: light, headline, sync-uncertainty ring |
| **Eye** | headline key lit by the worst light; NEXT and PREV PROBLEM; LENS ×6; RESET |
| **Live** | the 32-slot template; page next and prev; source keys (`focused`, `projectors`, …) |
| **Encoders** | Stream Deck + and Studio layouts:<br>– LCD keys show the current and next thumbnails of the focused playlist;<br>– dials: rate, seek, surface opacity, generator param;<br>– Studio rings show progress and temperature |
| **AI** | top proposal's words in its risk colour; APPROVE (two presses plus hold); REJECT; queue count. While armed: queue only |
| **Links** | Patterns and Stagewatch link state; the Stagewatch timer in its phase colour (`stagewatch_phase`) |
| **Content** | generator parameter encoders, generated from INDEX `generators[].params` |

**Every preset:**

- `notes` (2.2) holds the verb's words and risk, so a placed key explains itself;
- `keywords` aid search;
- an **`alternatives`** pair, layered first and simple second (Bitfocus Buttons, older hosts) [S23].

**Breathing keys, with no traffic:**

- armed keys pulse their red frame with `oscillate(2000)` on opacity;
- faults blink with `blink(500)`;
- the beat key pulses with `oscillate(60000/$(playmap:bpm),'square')` (when audio is present);
- score colours use `colorMix('#E0342E','#1E9E5A',$(local:score)/100)` [S7].

**HELP.md recipes:**

- triggers on `show_armed` → every surface to the Show page; `event_pulse {kind: projector.fault}` → page the tech;
  `scan.done` → beep;
- page variables `pm_live`, `pm_projector` and `pm_surface` for "one page, any target" (a two-click setup, since
  modules cannot create page variables [S8]);
- button references mirroring GO, PANIC and BLACKOUT on every page [S9].

### 8.2 What the desk shows (S5)

**Network drawer → Decks.** One row per Companion client from `STATE.clients`:

- name, address, channel (ws, wss or tcp), module version (amber "update the module" when older than
  `KNOWN_MODULE`), Live source and page, a recording dot, and the thumbnail count;
- right-click: **Identify deck** (`EVENT deck.identify`; the deck flashes for 3 s), **Copy connection details**
  (host, port, fingerprint), **Close connection**.

**Network drawer → Companion** (S2):

- address and status (HTTP, TCP and REST v2 reachability);
- **Add PlayMap to Companion** (`COMPANION ADD`);
- **Dim decks with the show strip** (`COMPANION BRIGHTNESS 5` with the strip, `RESTORE` after).

**Right-click on any entity** (S1): **Put on keys ▸ <page> / New key page…** (`KEYS SET`). The item lives in the same
`DeskAction` list as the toolbar's, so the menu and the buttons never disagree.

**Key page editor** (S1): a grid at the page's size. Drag entities in; double-click a slot to edit its label and line,
with the line checked by the catalogue as it is typed; a rule dropdown.

**Journal rows** read "GO from Companion:FOH deck". A refused locked press is journalled with the verb and the words.

---

## 9. Risks and open questions

1. **Expression options inside template presets.** Whether feedback options given as expressions (`$(local:item)`)
   on linked template buttons evaluate per button is untested at run time [C §8.19].
   - *Mitigation:* the primary path uses `$(this:value)` style overrides; local variables only carry plain ids.
   - Confirm on a real 5.1 with `tools/companion-e2e.sh` before release.
2. **One preset shared by many template groups.** `item_key` is referenced by every playlist's group. The types
   allow it and the host sanitiser accepts it (tested); the Companion UI behaviour is unverified.
   - *Fallback:* one preset per playlist (`item_key.<plid>`), still id-keyed.
3. **Label debounce and linked rebuilds.** If template value names change, linked buttons may be rebuilt
   (checksum). The 3 s debounce bounds the cost; measure on real Companion.
4. **Thumbnail cost.** Draw cost of 32 data-URI images on small Companion hosts (Pi) is unmeasured. Defaults: 96 px
   JPEG, a placed-only fetch, and LRU limits.
5. **Port 5353 sharing.** On Windows (Bonjour Service, OS mDNS) and Linux (Avahi), the fallbacks are:
   - TXT-less discovery through Companion's own browse is unaffected;
   - the Show Bus hello fallback (S4);
   - manual address entry.
   A bind failure is a fault entry, never a crash.
6. **A narrowing-only transport claim.** "companion" is inferred from a HELLO name anyone can say. It must only ever
   **narrow** rights. showsafety must map `wire` and `companion` to the same or a lower role; this is a test in both
   packages.
7. **Pairing token in provisioning (S2).** It is sent to Companion's REST API only on loopback. Remote provisioning
   leaves the token for the operator to type. Pair-by-approval (later) removes the typing.
8. **Bitfocus Buttons and API 2.2** are unverified. If Buttons matters, ship a 2.1-API variant without `storeResult`
   and `notes`.
9. **The 64 KiB line cap and thumbnails.** 144 px PNGs of photographic content can exceed 60 KiB. The node then
   steps JPEG quality down, and PNG is used only for drawn tiles.
10. **Companion's 50 Hz variable cap** under many playlists playing at once. The 25 Hz flush plus diffing keeps
    within it; this is a measured test.
11. **Upgrade scripts are append-only** [S21]. Action and feedback ids are frozen from 1.0.0. The version moves on
    every build (Patterns' rule: Companion 5 refuses a version it already has).
12. **Open: should `INDEX` include media names?** Proposed: count only, with playlists carrying item labels. A
    "Media" Live rule would need `MEDIA LIST` on demand.

---

## 10. Effort (agent-hours)

| Part | Must | Should |
|---|---|---|
| Line channel refactor, WS subprotocol, gate, transport inference, tests | 8 | — |
| INDEX, `SUBSCRIBE INDEX`, `project.index` | 3 | — |
| Thumbnails: service, PosterMaker, FrameToBgra, Skia encode, live leases, HTTP route, tests | 9 | — |
| EVENTS, ActionFeed, RECORD, PRESENCE, WHOAMI, clients, context items | 7 | — |
| VerbParams for every verb, plus the syntax cross-check | 6 | — |
| mDNS responder, codec, tests | 7 | — |
| Module: scaffold, config, manifest, packaging, HELP | 4 | — |
| Module: link, session, pairing, TLS pin, watchdog, fallback | 7 | — |
| Module: state diff, variables, clock interpolation | 7 | — |
| Module: catalogue, generated actions, smart actions, encoders, results, learn | 8 | — |
| Module: feedbacks (boolean and value), receipts, safety, locks | 6 | — |
| Module: presets (structure, templates, layered keys, composites, alternatives), Live page | 11 | — |
| Module: thumbs cache, events and pulses, typed recorder | 4 | — |
| Tests: host harness, fake node, contract tests both sides, real-engine e2e, fixtures writer | 10 | — |
| Papers (COMPANION.md, WIRE.md, ROADMAP, README) | 3 | — |
| S1 key pages (model, verbs, desk editor, right-click) | — | 8 |
| S2 Companion link (HTTP, TCP, REST v2, provisioning, brightness) | — | 7 |
| S3 selection | — | 3 |
| S4 hello fallback discovery | — | 2 |
| S5 desk Decks and Companion surfaces | — | 5 |
| **Total** | **100** | **25** |
