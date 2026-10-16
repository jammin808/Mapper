# wp2-multiscan on the wire

Many views into one scan, every projector solved together, regions per projector, scans that survive a restart, safe surface
suggestions and the Folder camera (`docs/SCAN-MULTIVIEW.md` for the solve, `docs/PHONESCAN.md` for the phone). The rows are in
wp2-phonescan's catalogue block; `SCAN START` and `SCAN SURFACE` are first-round rows whose `INTO` and `ON` forms this package
answers.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `SCAN START` (`INTO`, `phone:`) | `SCAN START <camera>\|phone:<phone> [<projector>…] [MODE FAST\|ROBUST\|DIFFICULT] [INTO <scan>]` | Accepted `{stage:"accepted", scan}`; STATE.scan then follows the run. `phone:<id>` names the camera rig a phone joined as. `INTO` adds a view (one past the highest number the scan has used) to a scan this node holds (in memory, or read from its folder by the run itself) and, once captured, solves every view together and records the scan again in place. Refused: no scan, a scan that keeps no views (made before views were kept), a simulated scan given a real camera or the reverse, a scan being solved ("add the view once it is done"), a scan already running, a stopping node | high | yes |
| `SCAN SOLVE` | `SCAN SOLVE <scan> [HOLD <projector>…]` | JSON as `SCAN VIEWS` once the solve is kept and recorded. `HOLD` (ids or names) keeps those projectors at their stage poses and optics, bit for bit, and puts the solve in the stage's frame. Refused "nothing would change: every view of … was solved together already — HOLD <projector> re-solves holding one" (and for a one-view scan with no HOLD, and for the same HOLD over the same views again), a projector the scan's views did not see, a scan taking a view or being solved, a stopping node | review | yes |
| `SCAN VIEWS` | `SCAN VIEWS <scan>` | `{scan, views:[{id, camera, projectors, state ("done"/"left out"/"dropped"), rmsPx, tracks, pairing ("tag"/"timing"/"stream"/"simulated"), exposure, words}], coverage:[{projector, seen, twice}], rmsPx, held:[…], disagreeing (the view to drop, or null), metric, words}` | low | no |
| `SCAN DROP` | `SCAN DROP <scan> <view>` | As `SCAN VIEWS`; the effect says the residual before and after. The view stays in the scan's folder, marked dropped; the rest are solved again warm (the frame and its scale kept). Refused for a view the scan does not have, one dropped already, and the last view | review | yes |
| `SCAN SUGGEST` | `SCAN SUGGEST <scan> [ASSISTANT]` | `{scan, from, suggestions:[{id, region, label, kind ("plane"/"curved"/"unknown"), confidence, reasons[], measured, projectors[], proposals[], state}]}`. Each measured suggestion's lines are queued by the engine on the node's own approval list — `SCAN SURFACE <scan> REGION <region> [ON <projector>] <name>`, and for a plane `SURFACE QUAD <projector> <x1> <y1> … <x4> <y4> RASTER AS <name>` — one batch each, so a person approves any one alone; nothing runs before `APPROVE`. `ASSISTANT` asks the node's model once, with text descriptors only (below), to name and rank them; a name for an id the geometry did not find is a grey entry (`measured:false`, "AI only — not measured", no proposal). A second `SCAN SUGGEST` of a scan replaces its suggestions: their proposals still waiting are rejected "replaced by a newer SCAN SUGGEST". Refused: no scan, no region worth a surface, no approval list (no assistant service), `ASSISTANT` with the assistant off or no model reachable, `ASSISTANT` from an assistant, MCP or approval line ("an assistant's line cannot ask the assistant"), such a line while the scan's suggestions wait for a person, a second SCAN SUGGEST of the scan while one runs | low | no |
| `SCAN SURFACE` (`ON`) | `SCAN SURFACE <scan> <x> <y> [ON <projector>] [<name>]` \| `SCAN SURFACE <scan> REGION <region> [ON <projector>] [<name>]` | The surface's id, one undoable edit (the scan's mesh joins the stage when it is not there). On a multi-view scan the region is one of the joined regions, and a click (0–1 on `ON`'s smart scan, the first solved projector's without `ON`) picks the region whose depth cell is there; `ON` keeps what that projector lights of it. On a single-view scan `ON` takes that projector's smart scan for the click and keeps the region's points in its picture. A single-view scan without `ON` is the first round's `SCAN SURFACE`. Refused: a projector the scan did not solve, one that does not light the region (with who does), a region the scan does not have, a place it measured nothing | review | yes |
| `SCAN NEXT` | `SCAN NEXT` | A manual Folder camera takes the newest picture since the pattern went up (journal: `taken: the newest picture in the folder is the pattern's on the glass`); before it waits for one, refused "the folder camera is not waiting for a picture yet …" (a manual camera's pattern is never put up again by SCAN NEXT); any other cued camera as wp2-phonescan says | review | yes |

**What an assistant is told** (`SuggestionDescriptor`, one request, JSON text): `id`, `kind`, `areaM2`, `planarityRmsMm`,
`normalToViewDeg`, `heightM`, `projectors:[{name, coveragePct}]`, `measuredLabel` — no picture, no pixel, no path. It answers only
through the tool `name_surfaces` (`items:[{id, name, rank, reason}]`); a name over 40 characters, with characters other than letters,
digits, spaces, hyphens and apostrophes, or that reads as a line (a whole verb, or a verb's word in capitals) is dropped and the
measured name stands.

## STATE

`scan` (the running or last scan; a SOLVE, DROP or SUGGEST shows its scan) gains:

```jsonc
"scan": { …,
  "view": "v3",
  "views": [{"id":"v1","camera":"cm…","projectors":["pj…","pj…"],"state":"done","rmsPx":0.009,"tracks":874,
             "pairing":"tag","exposure":"locked","words":""}],
  "coverage": [{"projector":"pj…","seen":0.999,"twice":0.883}],
  "regions": [{ …, "projector":"pj…", "projectors":["pj…","pj…"] }],
  "suggestions": [{"id":"sg1","region":"rg1","label":"Wall","kind":"plane","confidence":0.96,
                   "reasons":["flat: 2.4 mm RMS over 4.05 m²","lit by Left and Right","faces the projector at 9°; measured from 3 views"],
                   "measured":true,"projectors":["pj…","pj…"],"proposals":["ap1","ap2"],"state":"pending"}] }
```

Each `views`, `coverage` and `suggestions` row carries its `scan`; a suggestion its first `proposal` too (the SCAN SURFACE line).
Once a scan is solved again (INTO, SOLVE, DROP) its suggestions are withdrawn — their proposals still waiting are rejected "the scan
was solved again — SCAN SUGGEST again" — since they name regions and corners of the solve they came from; a re-solve takes the new
mesh off the record until SCAN APPLY (the camera the apply placed stays the record's), and SCAN SURFACE refuses while the stage's
mesh is not the scan's current one. `state` of a suggestion comes from its proposals now: `pending`, `approved` (one ran), `rejected`, `failed`, `expired`, or
`not measured`. The fixture is `tests/fixtures/state/phonescan.json` (its `scan.view`, `views`, `coverage`, `regions`,
`suggestions`) with cards in `phonescan.cards.json`.

## Files

A scan's folder (`scans/<id>/`) holds, beside scan.json and the mesh: `views/<view>/view.json` and one `<projector>.pmcm` per
decode (`CorrespondenceArchive`: `PMCM`, version, rasters, region, axes, bit counts, then the per-pixel arrays, deflated),
`multiview.json` (the joint solve, the projectors held, each view's influence) and `depth/<projector>.bin` (dense depth with its
cloud). Coverage, the merged mesh and the regions are made again from them on reading.

## Context, preflight, INDEX, score

- Context: `scan:suggestions` (amber, "N surface suggestions wait for a person — APPROVE ap… (or REJECT)") while any wait; a
  projector the last scan's views saw less than 80 % of: amber, "SCAN START <camera> INTO <scan> (aim at what is not seen)".
- Preflight section `scan`: each structured-light scan the stage depends on (its mesh on the stage, or a projector's current
  calibration from it) — green "on disk", amber
  "not on disk" with the fix `SCAN START <camera> <projectors…>` when its folder's scan.json is gone.
- INDEX section `scan`: a row per suggestion waiting (kind `suggestion`, id the proposal that approves it, amber).
- `SCAN APPLY` of a multi-view scan scores each projector by the joint solve: dense fit over its tracks, the decoded share of its
  views, the views that saw it; when a view disagrees with the rest the dense fit's term is `where:<view>`, fix
  `SCAN DROP <scan> <view>`.

## Papers note

For the integration docs pass:

- `WIRE.md`: the rows above; STATE `scan.view`, `views`, `coverage`, `regions[].projector/projectors`, `suggestions`.
- `MAPPING.md` (scanning): views INTO a scan from anywhere that sees the light; SCAN SOLVE (HOLD to re-solve only what moved),
  SCAN VIEWS, SCAN DROP of the view that disagrees; regions per projector joined across projectors and `ON <projector>`; scans
  kept on disk and used after a restart; safe suggestions (measured, text-only to an assistant, a person approves each); the Folder
  camera (tether and manual).
- Hunks in other packages' files: `EngineCore.Content.cs` (SCAN START's `INTO` and `phone:`), `EngineCore.Mapping.cs` (the first
  round's SCAN SURFACE asks `ScanSurfaceMultiscanAsync` first: ON and multi-view scans), `Core/Calibration/Scoring/CalibrationScoring.cs`
  (`ScanScoreInputs.ScanId`/`DropView`, the dense fit's fix `SCAN DROP`).
- `ARCHITECTURE.md`: `Engine/Scanning/MultiViewScan` (the engine over Core's multi-view solver), `ScanViewArchive`,
  `ScanSuggestions`, `FolderCamera`, `Media/Stills/StillImageReader`; `ICuedScanCamera.Patience`/`TakeNow`.
- `ROADMAP.md`: R1.9 Wave B done (views INTO, joint solve, suggestions, persistence, Folder camera, the closed browser loop);
  next: coverage on the wall (`SCAN COVERAGE … SHOW`), metric scale from a tape or a phone's depth, the desk's phone path.
- `CLAUDE.md` (the loop): the closed browser loop runs with `PLAYMAP_E2E=1` (a few minutes; Xvfb, node, Playwright and
  `/opt/pw-browsers`); the Folder camera's tests need FFmpeg 9.
