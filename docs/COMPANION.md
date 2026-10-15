# PlayMap and Companion: the node's side of two-way control

Bitfocus Companion (and the Stream Deck, X-keys or Loupedeck it drives) is the show's hardware panel. PlayMap's module for Companion
is a separate package; this paper is the node's side it talks to. The node gives a controller five things, all on the same wire
every other client uses (the desk has no private API, and neither does a controller):

1. **A door** — the line wire over the web port's WebSocket, so a controller needs one port and works through the same TLS as the
   web.
2. **The show's structure** — `INDEX`, so a deck's dropdowns and presets are built from data, and rebuilt only when the structure
   moves.
3. **What happened** — `EVENTS`, so automations trigger on an item's end, a fault, an arm, at the instant it happened.
4. **Who it is** — `WHOAMI` and `PRESENCE`, so a key greys out what this controller may not run while the show is armed, and the
   desk sees which deck shows what.
5. **Pictures and types** — `THUMB` for a key's picture, and typed parameters with a press shape for every verb (`VERBS`), so the
   module's actions are generated rather than written by hand.

The node also announces itself as `_playmap._tcp` on the local network, so Companion's device list finds it without an address.

`docs/wire/companion.md` has every verb, push, field and close code.

## The door

`/ws` with the subprotocol `playmap.lines.v1` runs the line session TCP 9941 runs — the same grammar, request ids, receipts,
pushes, `PING` clock exchange and pairing — one text frame per line. Three things differ, all stricter:

- The web's header token is not asked at the upgrade (a WebSocket client cannot always set one), so the line session itself gates the
  connection: until it proves the token (`NONCE`, then `AUTH-PROOF` — HMAC-SHA256 of the nonce and the HELLO name keyed by the token)
  it is answered nothing but pairing, and its greeting `STATE` waits for `OK paired`. The address's wrong-token lockout and
  `RequireHttps` hold before the upgrade, and a wrong proof counts against the address like a wrong header token.
- A web page from another site never reaches it: an upgrade whose `Origin` is not this node's own is refused (403) before it opens,
  since on a trusted address it would be paired at once. Controllers (Companion's module, scripts) send no `Origin`; pages the node
  serves itself keep their origin and are let in.
- Plain `AUTH <token>` is never taken there.

The sessions behind each door are their own (`LineSessions`): either door serves lines when the other could not open.

## Who a client is

A HELLO name is a claim, and a claim only narrows what a session may do. `Companion:<label>` makes a session a controller
(transport `companion`, Show Mode's remote role). The desk is the operator, so its role is proved, never claimed: `PlayMapDesk` is the
desk only on the TCP wire, with the node's token proved on that very connection, from this machine — the desk reads the token
from the node's own settings file — or from an address the node lists in `Companion.DeskAddresses` for a desk on another machine.
A client that is only trusted by address, or that says the desk's name on the WebSocket, is a remote client; a trusted one that says the
desk's name is sent a `NONCE` at once, so the desk's link proves the token without waiting.

## INDEX

INDEX lists playlists with their items, surfaces, projectors, outputs, devices, generators with typed parameters, canvases and the
media count, then one section per package that adds something a controller acts on (effect slots, LED fixtures, inputs and sends,
calibration targets, peers, proposals…). A package contributes its section through `IIndexContributor`: rows from published state,
and a preset hint — a title, a row kind, and key templates (a label, the line a press sends with `$(id)` for the row, the feedback
that lights it, the press shape). The module builds a preset section per hint, so a new package reaches the deck without module code.

Two signatures keep a deck quiet: `sig` moves only when the structure does (ids and kinds, an item's source kind, a generator's
parameter names, a section's rows and hint), `labels` only when names do. STATE carries `project.index` (the `sig`), so a controller
that does not subscribe still knows when to ask. `SUBSCRIBE INDEX` pushes when either moves, latest wins; a burst of edits is one push.

## EVENTS

Packages raise events through `RaiseEvent` with the show-clock instant they happened at (an item's end is its scheduled instant, not
when the lane noticed). The node keeps the last 256 with a sequence number; `EVENTS SINCE <seq>` replays and says how many the ring
no longer holds; `SUBSCRIBE EVENTS` pushes each in order. A gap is never silent: a session that falls behind, or is sent more than 64
in a second, is closed with an overflow line naming the seq to replay from. The node's own faults become `fault.raised` and
`fault.cleared`.

## WHOAMI and PRESENCE

`WHOAMI` answers the session as the node sees it — transport and channel, its name, paired, its Show Mode role, whether the show is
armed, the double-GO window, and the verbs it may run now (`"*"` when nothing is gated). A controller greys out the rest instead of
learning by refusal. `PRESENCE` is a controller's short note (its module version, the page it shows, whether it records, how many
pictures it holds); the desk and the context show it. STATE lists every controller under `clients`; the context has an item per
Companion — green paired and live, amber connected but not paired.

## Pictures

`THUMB <ref> [<w>x<h>] [JPEG|PNG] [IF <etag>]` answers a small picture and its ETag, or "same" when the asker holds the ETag. A clip's
picture comes from the node's poster store when there is one, else one frame decoded at a tenth of the clip (at most 3 s in),
box-filtered on the CPU and encoded as JPEG at the highest quality that fits; it is kept in memory and under the node's folder, so a
restart never decodes it again. Flat-field and colour-cycle generators are drawn as their colour, other generators as a named tile,
`color:#RRGGBB` as itself, and `playlist:<id>` as its current item. ETags follow content: a clip's hash, a generator's parameters, a
colour. **An armed node never decodes for a key**: only a poster made in rehearsal or on import is served, anything else is refused
with words. `SUBSCRIBE THUMBS <ref>…` pushes the watched set's ETags when one moves; `GET /api/thumb` serves the same bytes over HTTP
with `ETag` and 304.

## Typed parameters and press shapes

`VerbParams` reads every catalogue row's forms through `VerbSyntax` into typed parameters — an entity with its id prefix, a literal
choice, a number with its range, a time, a source, a line, JSON, a keyword and its value, another form it accepts (`#<n>` for an item
by position), whether it repeats — with overrides by row name where the syntax says too little. A row's press shape comes from its
show class first: Panic, Air and Transport rows fire at once, always; otherwise a high-risk row is held to confirm and a projector's
power is pressed twice. `VERBS` and `/api/verbs` carry `params`, `formParams` and `press`, so the module generates its actions.

## Announcement

A minimal mDNS / DNS-SD responder in `PlayMap.Net/Discovery` answers for `_playmap._tcp.local` on UDP 5353 (sharing the port with
Avahi and Bonjour): PTR, SRV to the web port, TXT (`id`, `name`, `proto`, `ws`, `wire`) and A, plus the service list. It probes before
it announces, renames itself `"<name> (2)"` when another host holds the name, answers legacy unicast queries, and says goodbye on
stop. It binds the node's bind address. A bind the machine refuses is a fault in STATE and a red preflight row — never a stopped node.
`Companion.Mdns` turns it off.

## Preflight

The controllers section has a row per connected Companion: green paired and live, amber connected but not paired (enter the node's
pairing token in Companion's PlayMap connection — nothing on the node pairs it), and red when the announcement could not open.

## MCP and Show Mode

MCP's `run_command` asks the node's Show Mode before it runs or queues a line: what Show Mode refuses is answered with its words and
neither runs nor waits for a person; what it holds for a person goes to the node, whose gate keeps it on the node's own list.

## Next round

Key pages authored on the desk (`KEYS …`) and `COMPANION PAGE`/`COMPANION ADD` (REST provisioning); the node → Companion link
(`COMPANION PRESS`, `COMPANION VAR`, `COMPANION BRIGHTNESS`, the REST key in the credential store); `RECORD` (the operator's lines as
`ACTION` pushes) and `SELECT`; live output and projector thumbnails from preview views with leases; streamed `ASK` replies; Show Bus
hello discovery in the module; the desk's Decks drawer; browsing (OSCQuery, WLED) on the same responder; the TLS fingerprint in the
TXT record.
