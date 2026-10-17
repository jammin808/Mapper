# The phone as a scan camera

Any phone with a browser becomes the node's scan camera, fully local: the node serves the camera page itself, on its own
certificate, on the show network. The verbs and the protocol are in `docs/wire/phonescan.md`; the design behind this is
`docs/round2/design/phonescan.md` (Wave A) and the research `docs/round2/research/phonescan.md`.

## Joining

1. On the desk, `PHONE INVITE Pixel 8` answers the person asking — and nobody else: never an assistant, MCP or an approval, and
   from another machine only through the web port, never on the line wire, which is not encrypted — with a link, its QR code and the
   short fingerprint of the certificate the web port serves. The link is
   `https://<the node's address>:<port>/scan/#t=<ticket>`. The ticket is 20 random bytes, good once, for ten minutes (`FOR`
   changes it); the node keeps only its SHA-256.
2. The phone opens the link (it compares the fingerprint when it warns about the certificate). The page reads the ticket from the
   fragment — which a browser never sends in any request — takes it out of the address at once, and sends it only in the first
   message on its socket. A page over plain HTTP from another machine says it needs the node's https address and asks for no
   camera (a browser gives a camera only to a secure page).
3. The node exchanges the ticket for the phone's own key: 32 random bytes, sent once, kept in the page's storage (the page says so,
   and its "Forget this node" drops it). `phones.json` in the node's folder (never the project) holds only `sha256:` digests,
   readable by this user alone. With the key the phone joins again without a new invite; `PHONE FORGET` makes it worthless — a
   join in flight when it lands is refused, never written back.
4. On its first join the phone becomes a camera rig of kind Phone in the project (one undo step), with a phone camera's field of
   view as its prior; a later join with another camera or raster resets the solved intrinsics. A join the project refuses (the
   show armed: mapping is locked — for a new phone, and for one coming back with another camera or raster, whose rig's optics would
   have to be forgotten) is refused to the phone with those words, and STATE, the context and preflight say it ("was refused: …"),
   so a rig never describes a camera other than the one its phone has; no phone is kept, and the invite stays good for another
   try within its life. So does an invite whose join met a full house (`phone.maxConnected`). A Phone rig whose phone this node no
   longer knows (forgotten, or its first join never finished) is paired again by an invite under its name: the phone takes that rig.
5. The page tries a lost connection again, waiting longer each time (to half a minute); it stops for good on a refusal trying again
   cannot change (a key or invite the node does not know, a forgotten phone — whose key it drops — another tab of the same phone,
   phones switched off). A phone that turns (its picture changes size) joins again with its new size — a join made with an invite
   once its welcome has given it its key, never in the middle of it.

The phone's session is that capability alone. It reaches nothing but its own socket: the module serves only under `/scan`; the
node's `/api`, `/ws` and `/mcp` keep their own gate (origin, own host names, token). The socket itself refuses a page of another
origin, a host name that is not the node's own (a name some page pointed at this machine), plain HTTP from another machine, five
wrong tickets or keys a minute from an address, and any message type a phone does not send. A picture must be the camera's own
size (as its hello said, at most 16 Mpx) and is kept only when a cue the node still waits on named it; anything else is
acknowledged and dropped. A phone that sends nothing for ten seconds (its page pings every second) is ended with those words.

## Exposure: two profiles

- **Lock** (a camera that offers manual exposure — Chrome on Android): while the probe (the coarsest Gray stripes) is on the glass
  the page lets auto-exposure settle, measures the top of the lit stripes once, sets exposure time and ISO explicitly for a level of
  215 — the light wanted read through the camera's curve (γ 2.2 first, then the curve the first correction shows; after a
  correction that clipped, planned again from the one before on a gentler curve and below the light that clipped), and the
  exposure chosen among those a flickering projector allows (any time to 8 ms, whole display frames above it up to the 30 fps
  cap) with the ISO the camera can really take (none, or within its range) as the one whose light comes nearest — then locks focus
  and white balance where offered, and measures again: the level must land within 190–240, after at most one
  more correction, or the camera runs the steady profile with words that say why (it took no manual exposure, or where its level
  landed). Focus, white balance and ISO are reported from what the camera says it holds, never from what it was asked.
- **Steady** (a camera without those controls — iPhone Safari among them): the camera keeps its own exposure, which the node keeps
  still by never showing a full white or black frame (they would make it ramp). The scan's white and black are made afterwards from
  the three coarsest Gray pairs of each axis (`ReferenceSynthesis`): per pixel, the pair that differs most. Each coded frame's gain
  is then read from the picture — a pattern and its inverse, and two sinusoid steps half a period apart, sum to white plus black at
  every lit pixel, so each pair's two gains are a least-squares fit — and levelled to the median (`GainNormaliser`). A phone's
  picture is gamma-encoded, which bends the sinusoids' mid-levels (binary patterns show only white and black, which no curve bends
  apart), so the camera's response power is fitted first — the one under which the sinusoid pairs sum best — and the pictures are
  read and levelled as light. The drift in STATE (`scan.pairing.aeDriftPct`) is the camera's: a still camera reads near zero
  whatever the picture shows and whatever its curve.

## Pairing a picture with its pattern by what it shows

- **Locate** (14 frames): every candidate tag cell (40 sites of 4 × 4 cells, c = max(6, W/80) px, inside a 4 % inset) is named by a
  Gray code over ten frames after all-lit and all-dark ones (`CellLocate`). The background between the cells takes two levels in
  turn, frame by frame (each bit frame read against the all-lit and all-dark pair on its own background), so every frame changes
  from the one before wherever the projector lands in the picture — whatever the cells show, however few the camera sees. A site
  counts when all 16 cells are found and a homography through them fits within 0.5 px; up to three are chosen — the topmost, the
  bottommost, then the farthest.
- **Paired by change** (locate frames; patterns where the camera sees no tag place): the page's grid (48 × 27 samples over the lit
  place, each a 3 × 3 mean; `PictureGrid`) must have moved by 8 levels at 13 samples or more from the last picture taken — a few
  samples suffice when the projection is small in the picture. Each projector's prepare starts afresh: the last projector's place
  is forgotten and the probe on the glass is what the first frame must change from; a frame shown again for a retake changes from
  the picture before that frame (a frame whose picture was lost is asked for again as a retake). A browser without frame callbacks
  cannot tell one camera frame from the next, so it takes no pattern that way and says so.
- **Tags**: every pattern carries its tag (value (i mod 63) + 1) at the chosen sites: cell 0 lit and cell 15 dark as references,
  cells 1–14 seven differential pairs carrying six bits and their parity (`FrameTag`). The phone's reader (`tags.js`) and the
  node's read the same value from the same pixels — same samples, same integer rule (checked under Node 22 on rendered pictures).
- **Lock-step**: the next pattern goes up only when the phone says it saw this one — its tag at every lit site in two consecutive
  frames that also agree. A picture whose sites disagree is a rolling-shutter mix and is thrown away (counted). Uploads trail, at
  most six in flight. A phone stuck on a pattern — it says it cannot see it, or says nothing for the pattern timeout (8 s, on the
  scan's own clock: `ScanRunnerOptions.Time`, the system's on a node) — has it shown again (twice at most), then the view fails with
  its words; `SCAN NEXT`
  asks for it again by hand (the wait ends and the pattern goes up again, unless the phone's answer was already on its way).
- **Checked on the node**: every picture's tag is read again when it lands; one that shows another pattern (`caught`) or never
  arrives is shot again. At the end the all-cells-lit locate frame goes up again (shot again if its picture never arrives): each
  chosen site, found again in it by zero-mean correlation with the run's own all-lit picture (its window sized along each of its
  own axes), standing more than a pixel from where it was means the phone moved, and the view fails — observed, never assumed. A
  site too near the picture's edge for the search is left out; without any site to measure, the two pictures are compared level by
  level. The phone's motion sensor reporting a movement starting, cue or no cue, any time after the scan's first view began —
  within a projector's run or between two — fails the scan at once: one scan is one camera place. A picture whose size is not the
  rig's fails the view with words.
- **Timing** is the cross-check (a picture taken before its pattern was presented is counted, `timingDisagrees`) and, where the
  camera sees no tag place, the pairing (`mode:"timing"`, with words to keep the phone very still).
- The tags are not the pattern: their footprint, and anything decoded within 2 px of a tag place, is left out of the decode.

## Other readers of the phone's pictures

A Phone rig opens wherever a camera rig does (`ScanCameras.Open`): an align run (`ALIGN RUN`) reads its pictures one `still` cue
at a time — the phone answers with its next picture, stamped on the node's clock through the clock exchange. The stills stop the
moment a scan drives the phone, so a scan's cues are the only ones it sees.

## Verified here, and what is not

- Core: PMF1 (round trip, CRC, every refusal, nothing allocated for a frame that lies about its size), the QR code (read back by an
  independent reader and by jsQR), the tag codec (every value, every flipped pair caught, the rolling-shutter mix refused), cell
  locate against the simulator's truth, every locate frame changing from the one before with the projection a twentieth of the
  picture, references and gains (a walking gain read within 1 % per frame, linear or gamma 2.2; a still camera near zero drift with
  either; the curve's power found).
- Net: tickets once and expiring (on a hand-moved clock), keys as digests, lockout, the limits' close codes, CSP and headers, the
  socket's origin and own-host rules, the clock exchange; a frame that lies about its size, pictures nobody waits for dropped
  however many and none inflated, a broken one a cue waits for closing the socket, a phone's own strings named and never echoed
  into STATE, a quiet phone ended, a forget racing a join, a join the node cannot finish (no phone kept, the ticket good again), a
  welcome that never went, the connected limit for a phone that joined before, close reasons within 123 bytes.
- The page in Chromium (Playwright, `/opt/pw-browsers`): no request — the socket's upgrade with all its headers among them —
  carries the ticket; the hello carries it once; the lock profile sets manual exposure on the fake camera; a camera with no
  controls runs the steady profile with its words; the next projector's first locate frame is seen where the picture changes;
  plain HTTP refuses with words and no camera. Under Node: the tag reader and the settle grid read what Core's read from the sites
  message the node sends; the pairing rule frame by frame (a new projector's run, a stale picture, a retake, a frame lost twice);
  the exposure plan's whole frames; curved cameras landing within 190–240 in two corrections from any settled level, and — with
  ISO fixed, at its floor or free, at 50 and 60 Hz — wherever a flicker-safe exposure could land them; the reconnect rule.
- Engine: a whole scan through a phone over its real socket (a C# phone that behaves as the page), with late pictures, a
  rolling-shutter mix, a dropped upload and a false picture all paired right (the projector within 1 cm, 0.25°, 0.6 % of throw),
  every picture after the probe put up only once the phone had answered the one before; the steady profile with a wandering gain,
  never a full white or black on the glass; a phone that hangs up or goes silent stops the scan cleanly with words in STATE; a
  phone whose motion sensor fires (in a cue or between cues), or nudged two pixels mid-view (locked or steady), fails the view with
  words, and so does a movement between two projectors' runs of one scan; a lost locate or closing picture asked for again without
  a stall; the end check's measure to a fraction of a pixel on a dark and a lit background and on a site seen aslant, a site at the
  picture's edge left out;
  `SCAN NEXT` answered before its timeout with no retake; a phone joining while the show is armed refused with the lock's words,
  its invite good after SHOW DISARM, and one coming back with another camera refused until then (its rig unchanged, the refusal in
  STATE and preflight) while the same camera is admitted; an invite under a forgotten phone's rig name pairing that rig again; an
  invite refused on the line wire from another machine; an align run through a phone
  reaching its staged result, and failing with the phone's words when it is gone; the verbs, the preflight row, INDEX; the ticket and key in no STATE, context, journal or project.
- Simulator: a camera hunting 1 % a frame with no white or black decodes ≥ 90 % of the locked pixels and holds the pose within twice
  the locked thresholds.
- The closed browser loop (`PhoneScanE2ETests`, with `PLAYMAP_E2E=1`): a real node with its render host on Xvfb, the real page
  in Chromium, its camera a track made from the simulator's scene lit by what the node's output really shows (30 frames a second,
  120 ms late, gamma-encoded, stamped with when each was taken). The Android profile (manual exposure honoured) locks and places
  the projector within 10 mm, 0.25°, 0.6 % of throw (measured: under a millimetre), and a second view from 35 cm aside joins the
  scan INTO it, both solved together under half a pixel; the iOS profile (no controls, auto-exposure hunting ±3 %) completes with
  brightness kept steady. Each projector's run takes about 400 camera frames — under 15 s at 30 fps.
- **Lab only**: real phones (Chrome Android's exposure lock, iOS Safari's steady behaviour and its motion permission, lens
  switching, rolling-shutter timing, turning the phone, Wi-Fi), the certificate click-through, motion sensors.

## Running the tests

The phone's tests run with the Engine suite. The closed browser loop (`PhoneScanE2ETests`) runs only with `PLAYMAP_E2E=1` and
takes a few minutes: it needs a display (Xvfb, `DISPLAY`), node 22 or later, Playwright (`npm ci` in `tests/e2e/phonescan`, or
installed globally) and its Chromium (`PLAYWRIGHT_BROWSERS_PATH`, `/opt/pw-browsers` when unset); without them its tests are
skipped with the reason. The Folder camera's tests read their photos through FFmpeg 9 (`PLAYMAP_FFMPEG_DIR`, `/opt/ffmpeg9/lib`
when unset) and are skipped with the reason where it is missing.

## Many views, surfaces, the folder

A phone (or any camera) can stand in more places: `SCAN START phone:<id> … INTO <scan>` adds a view and every view is solved
together (`docs/SCAN-MULTIVIEW.md`, `docs/wire/multiscan.md`). Scans keep their views on disk and work after a restart.
`SCAN SUGGEST` offers the measured regions as surfaces with their reasons, each a proposal on the node's own list that only a
person's `APPROVE` runs (an assistant, asked with `ASSISTANT`, sees text descriptors only and may only name and rank them).

The **Folder camera** (a camera rig of kind Folder) is a tethered camera or a person shooting into a folder: in tether mode each new
image after a pattern goes up is that pattern's picture once it has bytes and its size stands still (a file the tool has made but not
yet filled is waited for); in manual mode `SCAN NEXT` takes the newest. Photos carry the tags too: a
photo of another pattern is refused ("photo 17 shows pattern 16: re-shoot") and the scan waits; photos of another size than the
camera rig's fail the scan at the first one with the size to shoot. JPEG, PNG and TIFF (16-bit as
gray16) are read through the user's FFmpeg; RAW is refused with what to do. Verified with a fake tether writing the simulator's
pictures (the projector within 1 cm, 0.25°, 0.6 % of throw), a wrong photo refused, a manual run through `SCAN NEXT`, a 16-bit TIFF.
