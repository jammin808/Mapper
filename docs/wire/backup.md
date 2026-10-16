# wp2-backup on the wire

What the hot backup, the takeover, the routes and the promotion of a group's follower add to the wire. Every row below is routed (VERBS
marks it `"available"`). The design is in [../FLEET.md](../FLEET.md) ("The hot backup").

## Verbs

| Verb | Syntax | Words | Risk | Changes the show |
|---|---|---|---|---|
| `FLEET BACKUP` | `FLEET BACKUP <primary node> [HOT]` | This node becomes a hot backup of a primary: it mirrors the primary's project, air and transports on its own outputs; FAILOVER makes it the primary | high | yes |
| `FLEET SOLO` | `FLEET SOLO` | This node stops backing up and plays on alone | high | yes |
| `FLEET PROMOTE` | `FLEET PROMOTE [<group>]` | This node leads the group now, continuing the old leader's show clock without a step | high | yes |
| `FAILOVER` | `FAILOVER [<primary node>]` | The backup takes the show from its primary and fences the old primary by epoch | high | yes |
| `FAILBACK` | `FAILBACK CONFIRM` | Hands the show back to the original primary (a person's press only) | high | yes |
| `FLEET FENCE` | `FLEET FENCE <epoch> <node>` | A peer that took the show tells this node: hold the outputs (node to node only) | high | yes |
| `MIRROR` | `MIRROR HELLO\|PROJECT\|AIR\|HANDBACK <json> \| MIRROR ANCHOR <playlist> <json>` | A primary's show, line by line, to its backup (node to node only) | high | yes |
| `FLEET ROUTE` | `FLEET ROUTE <output> DEVICE <device> <input> <backup input> \| FLEET ROUTE <output> NONE \| FLEET ROUTE TEST` | What FAILOVER switches so the audience sees the backup: the projector's input, confirmed by its own answer; TEST reads reachability and the current input | review | yes |
| `MEDIA FETCH` (VERSION) | `MEDIA FETCH <media> VERSION <version> [FROM <node>]` | One optimised version of a clip, by its hash, from a peer that holds it, with the peer's proof over its pinned HTTPS | low | yes |

`SUBSCRIBE MIRROR <node>` opens the mirror feed on a primary: paired only (it carries the whole show's motion), refused on a node that
leads no sync group ("FOH-A leads no sync group — SYNC LEAD <group> here first, so a backup can follow its clock"), on a node whose
fleet monitor is off ("FOH-A's fleet monitor is off: it would not hear a takeover nor fence itself — turn it on (fleet.monitor in the
node's settings) and restart it first") and on a backup or a fenced node. The feed pushes, latest of each key first to a new subscriber, then in order:

- `MIRROR HELLO {"proto":1,"node","name","epoch","group","feed"}`;
- `MIRROR PROJECT {"rev":<revision>}` on every revision (the backup pulls the show itself);
- `MIRROR ANCHOR <playlist> <anchor json>` on every transport change of any playlist (the anchor JSON of `PLAYLIST ANCHOR`);
- `MIRROR AIR {"feed","seq","line","leaderNs"}` for each of the primary's own air lines (PANIC, PANIC CLEAR, BLACKOUT, MASTER, FREEZE,
  PATTERN) that the sync group's feed does not carry; the backup runs it pinned to that leader instant, once per (feed, seq);
- `MIRROR HANDBACK {"from","epoch"}` to one backup, on FAILBACK; the backup takes the show back only while its own mirror is
  identical, else it refuses, journalled: "FOH-A is not identical to FOH-B (lost): it does not take the show back — FAILBACK CONFIRM
  on FOH-B again once it is".

The backup hands each line to its own `MIRROR` verb with the session `fleet:<primary>` (transport "fleet"); `MIRROR` and `FLEET FENCE`
from any other transport are refused "this line is node-to-node only".

Refusals (each changes nothing): "no PlayMap node 'X' is heard on the show network — NODES lists the ones that are"; "already a hot
backup of FOH-A (0badc0de)"; "FOH-A refused the mirror: it asks for its pairing token and this node holds none for it — CREDENTIAL SET
node.0badc0de <FOH-A's pairing token> here first"; "FOH-B is solo already — it backs up no primary"; "FOH-B backs up no primary —
FAILOVER runs on a hot backup (FLEET BACKUP <primary> first)"; "FOH-A does not mirror FOH-B — FLEET BACKUP FOH-B on FOH-A first, then
FAILBACK"; "FOH-A is not identical to FOH-B yet (its mirror is resyncing) — FAILBACK waits for it"; "FOH-B leads show already";
"Op 1's route is that already"; "the primary's input and the backup's are both 31: a takeover would switch nothing"; "Desk holds a
blind draft here: it would land over FOH-A's show — BLIND DISCARD (or BLIND COMMIT) first"; "Cam 1 is a live input: it is received
from the network, never fetched"; "FOH-B took FOH-A's show already (epoch 1) — FLEET BACKUP FOH-B to mirror it" (a FAILOVER on a
primary's other backup); on a node that has begun to stop, a pull asked of it by its own line: "the node is stopping — no pull
starts", and a takeover or a join, by a hand-back or its own line: "the node is stopping — it takes no show over and joins no
mirror".

On a backup a client's line that would change the show is refused "FOH-B mirrors FOH-A — send it to FOH-A (10.0.0.21), or FAILOVER to
make this node the show"; on a fenced node "fenced: FOH-B has the show (epoch 8) — FLEET BACKUP FOH-B to mirror it". Queries, panic,
Show Mode, the node's administration (credentials, pairing), `MEDIA FETCH` and the fleet's verbs stay; `FLEET ROUTE` (except TEST) is the show's plan,
edited on the primary; `BLIND ON` and `BLIND COMMIT` are refused (a draft would land over the primary's show; `BLIND DISCARD` and
`BLIND OFF` stay).

## Replies

- `FLEET BACKUP` — `accepted` "joining FOH-A (0badc0de) as a hot backup: its show, clock, transports and air mirrored here (paired by
  proof; pulls over HTTPS, pinned)"; the journal says "FOH-B mirrors FOH-A: identical (rev 12)" when it is.
- `FAILOVER` — `{"id":"tk…","epoch":8,"words":"takeover tk… of FOH-A by FOH-B (pressed) — epoch 8 committed → leading show on the same
  time line → show clock continuous (0 ms read across the change) → P1 32 observed → serving → complete in 41 ms"}`.
- `FLEET ROUTE … DEVICE` — `{"output","device","input","backupInput","primaryNode","removed"}`; `FLEET ROUTE TEST` —
  `[{"output","device","words":"P1 on 31 (primary) · reachable"}]` (or "P1 · unreachable (…)").
- `FLEET PROMOTE` — "leading show under epoch 3; the show clock continues the old leader's line (0 ms read across)".
- `MEDIA FETCH … VERSION` — `accepted` "fetching mv_1 of Clip (hap-q) from FOH-A by its hash, with proof"; the journal says
  "mv_1.hap-q pulled from FOH-A with proof over HTTPS and verified" or why not.

## The web port

`/mirror` is a web module admitted before the token gate on its own credential: `GET /mirror/project` (the show as compact JSON, its
revision in `X-PlayMap-Rev`), `GET /mirror/media/<sha256>` (a verified file, ranged) and `GET /mirror/file/<kind>/<id>` (a file the
live show names beside its folder: `mesh/<mesh>`, `warp/<output>`, `blend/<output>`, `cloud/<survey>`; 404 for anything the show
does not name, or a path outside its folder). Each request carries `X-PlayMap-Nonce`
(`<unix ms>-<32 hex>`) and `X-PlayMap-Proof` (lowercase hex HMAC-SHA256 of the node's normalised token over the nonce and
`"GET https /mirror/…"` — the method, the scheme and the path — as AUTH-PROOF's scheme); the node takes a nonce once and only within a minute of its wall clock. A header token is not
a proof, and a request with a proof that names an Origin (a browser's) is refused "a mirror pull comes from a PlayMap node, never from
a web page". From an address the node trusts, or on a node that asks no token, the node's own gate decides (its Origin and host rules).
From another machine only HTTPS is let in; the backup sends no pull in clear to another machine and none to a node whose hello drops a
pinned certificate. Every nonce is remembered for two minutes; a node that holds 8 192 of them refuses more pulls until some expire. Only GET and
HEAD.

## STATE

`fleet` (after `show`, keys in this order): `role` (solo|primary|backup|fenced), `epoch`, `state` (serving, fenced, or the mirror's
state on a backup), `of` (the primary a backup mirrors, the node a primary took over from, the node that fenced this one), `backups`
(`[{"node","name","state","light","words"}]`, a primary's backups with the state each reports in its HEALTH), `mirror` (null, or
`{"of","name","state":"joining|resyncing|identical|different|lost","rev","behindRev","words"}`), `succession` (the show's plan; `[]`),
`alarm` (null: automatic takeover is not built), `last` (null, or `{"kind":"takeover|failback|promote","id","at","words"}`), `nodes`,
`light`, `headline`, `routes` (`[{"output","device","input","backupInput","primaryNode"}]`), `routeTest` (null, or `{"at","words"}`),
`door` (the words a client's line that would change the show meets on this node — a backup's way out, or a fenced node's — exactly
as the node refuses it; null on a node with a show of its own).
`sync.epoch` is the node's epoch. On a hot backup each `led.nodes[]` row whose machine master is on says `state` `standby` (green in
the context and INDEX) and each enabled `videoio.sends[]` says `standby`, both with the words why ("silent: FOH-B is a hot backup of
FOH-A — it sends at FAILOVER"); on a fenced node both say `held` ("held black: FOH-A is fenced (FOH-B has the show) — FLEET BACKUP
FOH-B to mirror it"); on a primary that has just started with hot backups both say `standby` until it has heard them ("silent while
FOH-A listens for its hot backup (FOH-B): one may have taken the show while it was away"). `show.sound.gain` is 0 on each of them.
HEALTH adds `mirror` (the backup's state) and `sync.group`; a node is told at once when a peer's epoch, mirror or group moves. The fixture is
`tests/fixtures/state/backup.json` (a backup, its primary, the backup after FAILOVER and the old primary fenced, each keyed by its
scenario), its cards `backup.cards.json`.

Events: `fleet.takeover` (`id`, `from`, `epoch`, `words`), `fleet.fenced` (`by`, `epoch`), `sync.promoted` (`group`, `epoch`).

The context: a backup or a fenced node adds `mirror:<primary>` (kind `mirror`; the machine **mirrors** it); a follower whose leader is
silent adds `fleet:leader`, red, "leader gone — FLEET PROMOTE on the node that should lead" (on a hot backup: "primary gone — FAILOVER on
this backup to take its show"). A fence is taken only from a node that joined this node's mirror (kept in `NodeSettings.Fleet.backups`)
and is heard over its pinned certificate.

## Preflight and INDEX

Preflight section `backup`: on a backup, `backup:mirror` (green identical; `FLEET BACKUP <primary>` when lost) and `backup:files` (amber
with the first missing media file's line: `MEDIA FETCH <media> VERSION <version> FROM <primary>`; it names each file beside the show
that could not be pulled too, which no line fetches: the mirror asks for it again itself); on a primary, `backup:<node>` per backup;
`route:<output>` per route (red unreachable, `FLEET ROUTE TEST`); a fenced node `backup:fenced` red; else one grey row. INDEX section
`fleet` (preset "Fleet", press "hold"): `FAILOVER` lit by the mirror, `FAILBACK CONFIRM`, `FLEET PROMOTE` (red while the leader is
silent), and `FLEET FOCUS machine:<node>` per backup.

## Papers note

WIRE.md lists these rows and `SUBSCRIBE MIRROR`; ARCHITECTURE §4 has the promoted clock, the fence and the epoch; ROADMAP R4.1 is
built for a manual takeover. Next round: automatic takeover on silence, `FLEET SUCCESSION`, HOLD, WARM and COLD backups, project
deltas, frame keys, the router step, `FLEET FENCE` sent over the wire, the sACN lower-priority backup, the pending blind commit carried
to the backup with its instant (a landing reaches the backup as the revision after it lands), and the 10 s identical window before
FAILBACK.
