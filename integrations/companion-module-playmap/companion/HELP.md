# PlayMap

PlayMap's whole show as live keys: the playlists and their items, the outputs and projectors, every package's keys (effects, calibration, tempo, the show's arm and panic), with pictures, countdowns and lights that come from the node — a key lights because the node says so, never because it was pressed.

## Connect

1. **Node on the network** — pick the PlayMap node from the list (it announces itself as `_playmap._tcp`). Or leave it empty and type its **Address** and **Web port** (9940).
2. **Pairing token** — the token from the Network drawer on the PlayMap desk (`K7QM-3XWD-P9RA`; dashes and case do not matter). It is kept as a secret and never crosses the wire: the link proves it.
3. **Link** — WebSocket (ws) on the web port; WebSocket over TLS (wss) when the node asks for HTTPS (the node's certificate fingerprint is kept on the first link; clear it to trust a new one); or the TCP line wire (port 9941) for an older node. A node without a line channel on its web port is reached over TCP by itself.

The status says what is wrong in words:

| Status                                        | What to do                                                                                                               |
| --------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| This node asks for its pairing token          | Type the token from the Network drawer                                                                                   |
| The pairing token is not this node's          | Type it again; the link stops until you do, so wrong tokens are not spent                                                |
| The node closed the link after wrong tokens   | Wait a minute and check the token, then type it or disable and enable the connection; the link stops until then          |
| The node's own words (refused upgrade)        | The node answers its own address and names only: connect by its address, or add the name you use to the node's web hosts |
| No answer from the node for 4 s — keys dimmed | The node or the network is busy; keys come back by themselves                                                            |
| Node speaks line protocol N                   | Update this module                                                                                                       |

## Keys

The presets come from the show, by id: renaming or reordering an item never orphans a linked key.

- **Show** — GO, NEXT and PREV on the focused playlist (GO sends `EXPECT` the next item, so a list that moved under the key is refused, not played), BLACKOUT, FREEZE, PANIC and one per outcome, PANIC CLEAR, the countdown, Show Mode and the node's headline.
- **Playlists** — one key per playlist, and one template group per playlist (`pl.<id>`) with a key per item: its picture, its name, its progress bar, a frame lit green on the air, amber next, grey when the show no longer has it.
- **Outputs**, **Projectors** (light and calibration score ring), **Surfaces** (show or hide, held), **Fleet** (the machines this node hears).
- **Live page** — 32 slots that fill themselves (below).
- **Encoders** — master, rate, seek and the Live page on dials; a generator's parameters.
- **Every package's section** — the node describes its keys (effects bypass and mix, calibration re-solve, tempo, timecode chase, the show's arm and panic, the Eye …) and the module builds them; a new package reaches the deck without a new module.

Untick a group in the connection to leave it out.

### How a key is pressed

The press shape comes from the node's vocabulary:

- **At once** — PANIC, BLACKOUT, FREEZE, MASTER, GO, NEXT, PREV and every transport verb: never held, never confirmed.
- **Held 0.8 s** — verbs that change the air at high risk (show or hide a surface …): the key says HOLD… while held.
- **Two presses** — a projector's power: the first held press says SURE? for 3 s, the second sends.

A corner dot shows the press's receipt: amber while the node has it and its effect is not yet in STATE, red for 2 s when refused, with the node's words in `last_refusal`. Every action returns its receipt as its result (`ok`, `stage`, `effect`, `error`), so `Store result` and _if_ blocks can branch on it.

### Show Mode

While the show is armed the node says which verbs this deck may run (`WHOAMI`); a key the node would refuse wears a lock before it is pressed. A locked key is still sent: the node refuses it, names who tried, and the refusal shows on the key. The Live page's slots stay where they are while armed.

## The Live page

Install it once: drag the **Live slots** template group onto a page. Set the page variable `pm_live` on that page to its source:

| `pm_live`                                   | The slots show                                    |
| ------------------------------------------- | ------------------------------------------------- |
| `focused` (empty)                           | the focused playlist's items — press to GO        |
| `playlist:<id>`                             | that playlist's items                             |
| `playlists`                                 | the playlists — press to GO                       |
| `outputs`                                   | the outputs — hold to identify                    |
| `projectors`, `surfaces`, `peers`, `faults` | their lights and words                            |
| `section:<name>`                            | a package's rows (`section:fx`, `section:show` …) |

PAGE + and PAGE − turn its pages (`live_page` reads `2/5`). While the show is armed the slots are frozen in place: lights, labels and pictures still move, positions do not, and a slot whose thing is gone reads REMOVED. RE-LAY (or disarming) lays the page out again. A slot that needs a hold says HOLD on a short press.

## Variables

`node_name`, `link`, `light`, `headline`, `show_mode`, `blackout`, `master`, `focused_name`, `current`, `next`, `faults_count`, `fault_1`…`fault_3`, `last_refusal`, `last_receipt`, `event_last`, `event_last_kind`, `live_rule`, `live_page`, and Patterns' video row (`video_file`, `video_remaining`, `video_chip`, `video_progress` …) so a key made for Patterns' VT clock works here. Banks by position (`pl_1_name`, `pl_1_remaining`, `op_2_light`, `pj_1_light`, `peer_1_headline` …) and by id (`pl.<id>.state`, `op.<id>.light`, `pj.<id>.light`).

## Recipes

These use only Companion 5.1's own expression functions. The presets already breathe this way: Show Mode pulses while armed, the headline's ⚠ blinks on a fault, the Beat key pulses on the beat, and a projector's ground runs from red to green with its score.

**An armed key breathes.** On a key's red frame, set the opacity to an expression:

```
$(playmap:show_mode) == 'ARMED' ? 40 + 60 * oscillate(2000) : 100
```

**A fault blinks.** On a text element's enabled property:

```
$(playmap:faults_count) > 0 ? blink(500) : true
```

**A score in colour.** On a projector key's ground, from its calibration score (a feedback-based local variable `score` from the score feedback):

```
colorMix('#E0342E', '#1E9E5A', $(local:score) / 100)
```

**The countdown turns red in the last ten seconds.**

```
$(playmap:video_remaining_s) <= 10 && $(playmap:video_playing) ? '#E0342E' : '#14161C'
```

**One page, any projector.** Put the projector key on a page, set its `projector` option to `$(page:pm_projector)`, and set the page variable `pm_projector` to a projector's id (page variables are set with the internal action _Page Variable: Set value_). The same works for `pm_surface` and the Live page's `pm_live`.

**GO, PANIC and BLACKOUT on every page.** Copy the GO, PANIC and BLACKOUT keys, then _Paste as reference_ on each page: one key each, pressed and lit everywhere.

**Triggers from the show.** A trigger with the condition _PlayMap: Event: happened just now_ (`event_pulse`, kind `item.end`) turns the page or presses a key when an item ends; kind `fault.raised` pages the technician; `scan_state` changing to "done" (a `variable_changed` trigger) sounds a beep; _PlayMap: Show: armed_ (`show_armed`) sends every surface to the Show page when the show is armed. `event_last_kind` changing is a `variable_changed` trigger.

**Round a rate for a label.**

```
round($(playmap:pl_1_progress) * 100) + '%'
```

## When something is wrong

- Keys dark with a ⚠ — the node has said nothing for 4 s, or the link is down; it comes back by itself.
- A red corner dot — the node refused the press; `last_refusal` says why in its words.
- A key reads REMOVED — the show no longer has that item; the key keeps its place until you move it.
