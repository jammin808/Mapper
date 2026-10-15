# wp2-decode on the wire

HAP on the GPU (`docs/DECODE.md`). A `<media>` is a library item's id or name.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `MEDIA INSPECT` | `MEDIA INSPECT <media>` | JSON (below), answered off the control lane | low | no |

`MEDIA INSPECT` reads every packet of the clip once (a disk pass) and decodes the second stage of its first eight frames on one
core. It answers:

```json
{"media":"md…","codec":"hap_q","texture":"ycocg-bc3","width":3840,"height":2160,"oddSize":false,"frames":18000,"fps":60,
 "chunks":{"min":16,"max":16,"typical":16},"secondStage":{"snappy":0.96,"raw":0.04},"mbPerFrame":{"avg":0.71,"max":1.9},
 "mbPerSecond":42.6,"refused":0,"hdr":null,
 "budget":{"node":"a1b2c3d4","layers":12,"layers4k60":12,"limit":"upload",
           "words":"upload 6.0 GB/s ÷ 498 MB/s per layer (8.3 MB of BC3 at 60 fps) = 12; disk 1.9 GB/s ÷ 114 MB/s at peak = 16; cores 4 × 16.7 ms ÷ 4.5 ms = 14"},
 "fixes":[{"words":"1 chunk: each layer's frame decodes on one core (4.5 ms of its 16.7 ms)","line":"MEDIA OPTIMISE md… TO rechunk"}]}
```

- `codec` is the fourcc's name: `hap`, `hap_alpha`, `hap_q`, `hap_q_alpha`, `hap_alpha_only`, `hap_r`, `hap_hdr` (any other codec
  answers FFmpeg's name, `texture` empty, and a fix line to a HAP version).
- `budget.layers` is this clip as it is; `layers4k60` the same clip's costs scaled to 3840×2160 at 60 fps. The budget is
  min(disk ÷ peak MB/s per layer, cores × frame time ÷ decode ms per layer, upload ÷ texture MB/s per layer); a term not measured
  is left out and the words say so. Disk is the rate this pass read at; upload is what this process's compressed uploads of 1 MB or
  more ran at so far (unmeasured before one has been drawn: a smaller frame's fixed cost per call says nothing about a 4K rate).
- `hdr` is HAP HDR's reading in words (`"linear · white 203 nits (default)"`, `"pq (colr)"`, `"linear · white 100 nits (item)"`).
- The answer is kept by content hash and the item's colour override for the life of the process.

**Refusals**, with words: a word the verb does not take (`ON <node>` is not offered: ask that node); no such media; not a file; the file is not on this node; the show is armed ("MEDIA INSPECT reads the
whole file from disk — disarm first, or inspect it on a node that is not playing"); the file cannot be read.

**The colour override** is edited with the generic `SET`: `SET md… color.transfer "pq"`, `SET md… color.whiteNits 100`. The check
refuses a transfer other than `auto|sdr|pq|hlg|linear`, a white level outside 0 (the rule's) to 10000 nits, a range other than
`auto|full|limited` and a matrix other than `auto|bt601|bt709|bt2020`.

## STATE

Top level `decode` (after `cameras`), and `machine.gl` (`glx|egl|wgl|cgl`, present while a render context exists: the binding
the context came through, as that binding's own library answered on the render thread; `unknown` when none did).

```json
"decode":{"platform":{"blocks":{"bc1":true,"bc3":true,"bc4":true,"bc7":true,"bc6h":true,"staging":"copy","arenaMb":0,"arenaFreeMb":0},
                      "hapLayers4k60":null|{"value":9,"limit":"cores","measuredAt":1760101234567,"decodeMs":7.4,"cores":4,"words":"…"},
                      "light":"green|amber|red","words":"1 decoder · 1 HAP on the GPU"},
          "sources":[{"key":"md…","media":"md…","name":"Intro","codec":"hap_q","path":"hap-gpu|hw-copy-back|software","zeroCopy":true,
                      "decodeMsP95":2.1,"busMbPerFrame":8.294,"dropped":0,"late":0,"status":"ready","why":"","light":"green",
                      "hap":null|{"texture":"ycocg-bc3","chunks":16,"rawShare":0,"secondStage":"snappy|none|mixed","stageMsP95":2.1,
                                  "uploadMb":8.294,"staging":"copy","corrupt":0,"lastRefusal":"","hdr":null}}]}
```

- `blocks` are the families the render context samples (all false and `staging` `none` with no render context). `staging` is
  `copy` this round (the persistently mapped arena is next round's); `arenaMb` and `arenaFreeMb` are 0.
- `hapLayers4k60` is null until measured (the preflight check measures it once, on a worker: the second stage of a synthetic 4K
  HAP Q frame on one core; upload excluded).
- `sources` lists every clip decoder open on the node (HAP and FFmpeg), with why it is not on a better path (`why`: "this GPU's
  GL lacks S3TC; FFmpeg decodes HAP on the CPU", or the source's own failure). An FFmpeg clip's `why` names each hardware rung
  it passed and why ("vaapi skipped: libva-drm.so.2 is not on this machine; cuda: no device (…)"); a device whose user-space
  library FFmpeg binds through process-aborting stubs (VA-API, QSV on Linux) is asked for only when that library loads.
- `light`: red when a decoder failed, or a HAP R / HAP HDR item is in the show on a GPU without BPTC; amber when a HAP source has
  refused frames; green otherwise.

Fixture: `tests/fixtures/state/decode.json`; cards: `tests/fixtures/state/decode.cards.json`.

## Context

Item `decode` (kind `decode`), present while decoders are open or the show has HAP media; light and words as `platform` above.

## Preflight

Section `decode`: row `decode.hap-layers` — "The show needs N HAP layers at 4K60; this node sustains M" (amber when N > M, fix
`MEDIA INSPECT <heaviest>`); a red row `decode.hap-gpu:<md>` per HAP R or HAP HDR item this GPU cannot sample (fix
`MEDIA OPTIMISE <md> TO hap-q`).

## Papers note

For the integration docs pass: `docs/WIRE.md` gains `MEDIA INSPECT` and STATE's `decode` block; `docs/PLAYBACK.md` (or
ARCHITECTURE's media section) gains "HAP plays on the GPU: all seven codecs, HAP R and HAP HDR included, block-compressed upload
and the `hap` pass; HAP HDR is read linear with 1.0 = 203 nits unless the item or the file says otherwise"; ROADMAP marks the HAP
family done and lists this package's next round (`docs/DECODE.md`).
