# wp2-zerocopy on the wire

The decode-path ladder and hardware imports (`docs/DECODE.md`). The package adds to wp2-decode's verbs, STATE block, context item
and preflight section.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `DECODE STATUS` | `DECODE STATUS` | the `decode` object STATE carries (below) | low | no |
| `DECODE POLICY` | `DECODE POLICY AUTO\|DIRECT\|GPU-COPY\|COPY-BACK\|SOFTWARE [FORCE]` | `{"stage":"applied","policy":"software","forced":false,"open":3,"effect":"decoders opened from now on; 3 open keep their path"}` | review (`FORCE` high) | yes (`decode.policy` in the node's settings) |

- `DECODE POLICY` sets the best rung decoders opened from now on may take; open ones keep their path. `AUTO` is the platform's best.
  `DIRECT` on a D3D11 renderer needs direct sampling proven on the driver, else it is refused ("direct sampling is not proven on
  this driver (the proof runs next round): DECODE POLICY DIRECT FORCE takes it unproven, or AUTO takes one GPU copy"); VAAPI images
  are sampled in place on every driver, so `DIRECT` needs no proof there. `FORCE` goes with `DIRECT` only. On a node whose renderer
  offers no zero-copy the policy is still kept, and `effect` says "(zero-copy is not ready here: …)". Refusals: a policy equal to the
  current one ("the decode policy is already software"), an unknown word, a settings file that cannot be written. Each change is a
  journal row (`decode`). Show class: setup (an armed node refuses it through the gate).
- `DECODE PROBE` (the direct-sampling proof) is next round: it is not in the catalogue.

## STATE

Added to wp2-decode's `decode` block (new keys at the end of each object):

```json
"decode":{"platform":{"blocks":{…,"staging":"persistent|copy|none","arenaMb":256,"arenaFreeMb":240,"arenaWords":""},
                      "hapLayers4k60":…,"light":"green|amber|red","words":"…",
                      "api":"dmabuf|d3d11|opengl|none","describe":"VAAPI → EGL dmabuf on /dev/dri/renderD128 (…)","gl":"egl|glx|wgl|none",
                      "zeroCopy":"ready|unavailable","direct":"proven|unproven|n/a","why":"the GL is software (llvmpipe): zero-copy needs a GPU"},
          "sources":[{…,"path":"hw-direct|hw-gpu-copy|hap-gpu|hw-copy-back|software","zeroCopy":true,…,"why":"…",
                      "device":"vaapi|d3d11va|cuda|","gpuCopies":0,"width":3840,"height":2160,"surfaces":19,"vramMb":236.4,"importMsP95":0}],
          "policy":"auto|direct|gpu-copy|copy-back|software","forced":false}
```

- `sources[].path` is the path the source's latest frame came by (before its first frame, the rung it opened on); `why` names each
  rung it passed and why, then the renderer's words when it opened below the top. `busMbPerFrame` is what crosses the bus per frame
  (a copied-back frame twice, a software frame once, a zero-copy frame 0); `surfaces` and `vramMb` are a zero-copy decoder's pool.
- `platform.zeroCopy` is `ready` when the renderer's device imports decoder surfaces (a GPU's render node and dmabuf imports, or the
  D3D11 share); `why` says why not. `staging` is `persistent` when the context has an upload arena (`arenaWords` says why not).
- `light` is amber when a clip of 4K or more decodes on the CPU ("1 clip at 4K decodes on the CPU: Intro (hw-copy-back)").

Fixture: `tests/fixtures/state/decode.json` and `decode.cards.json` (wp2-decode's, extended).

## Context

The `decode` item (wp2-decode's) turns amber with the same words when a 4K clip decodes on the CPU.

## Preflight

Section `decode`: row `decode.cpu-4k:<md>` — "Intro decodes in software at 3840×2160 — …" (or "copies every frame back through
memory"), amber, fix `MEDIA OPTIMISE <md> TO hap-q`, for each non-HAP clip of 4K or more the show plays whose frames come through
memory. The path is the open decoder's own; a clip not open now is read from the rung it would open on, and the row says "would".
None: one green row `decode.cpu-4k`.

## Papers note

For the integration docs pass: `docs/WIRE.md` gains `DECODE STATUS`, `DECODE POLICY` and STATE.decode's new keys; `docs/PLAYBACK.md`
(or ARCHITECTURE's media section) gains "every decoder says the path its frames come by: VAAPI dmabuf imports on Linux and D3D11VA
on the renderer's device on Windows when the renderer can import them, else copy-back or software with the words why; HAP frames are
written straight into the render context's upload arena"; ROADMAP marks the ladder, the dmabuf import half and the arena done, and
VAAPI and D3D11VA decode as compile-only (lab), with the direct-sampling proof, the EGLImage cache and the per-item override next.
