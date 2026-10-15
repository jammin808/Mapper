# The phone as a scan camera

Any phone with a browser becomes the node's scan camera, fully local: the node serves the camera page itself, on its own
certificate, on the show network. The verbs and the protocol are in `docs/wire/phonescan.md`; the design behind this is
`docs/round2/design/phonescan.md` (Wave A) and the research `docs/round2/research/phonescan.md`.

## Joining

1. On the desk, `PHONE INVITE Pixel 8` answers the person asking — and nobody else: never an assistant, MCP or an approval — with
   a link, its QR code and the short fingerprint of the certificate the web port serves. The link is
   `https://<the node's address>:<port>/scan/#t=<ticket>`. The ticket is 20 random bytes, good once, for ten minutes (`FOR`
   changes it); the node keeps only its SHA-256.
2. The phone opens the link (it compares the fingerprint when it warns about the certificate). The page reads the ticket from the
   fragment — which a browser never sends in any request — takes it out of the address at once, and sends it only in the first
   message on its socket. A page over plain HTTP from another machine says it needs the node's https address and asks for no
   camera (a browser gives a camera only to a secure page).
3. The node exchanges the ticket for the phone's own key: 32 random bytes, sent once, kept in the page's storage. `phones.json` in
   the node's folder (never the project) holds only `sha256:` digests, readable by this user alone. With the key the phone joins
   again without a new invite; `PHONE FORGET` makes it worthless.
4. On its first join the phone becomes a camera rig of kind Phone in the project (one undo step), with a phone camera's field of
   view as its prior; a later join with another camera or raster resets the solved intrinsics.

The phone's session is that capability alone. It reaches nothing but its own socket: the module serves only under `/scan`; the
node's `/api`, `/ws` and `/mcp` keep their own gate (origin, own host names, token). The socket itself refuses a page of another
origin, a host name that is not the node's own (a name some page pointed at this machine), plain HTTP from another machine, five
wrong tickets or keys a minute from an address, and any message type a phone does not send.

## Exposure: two profiles

- **Lock** (a camera that offers manual exposure — Chrome on Android): while the probe (the coarsest Gray stripes) is on the glass
  the page lets auto-exposure settle, measures the top of the lit stripes once, sets exposure time and ISO explicitly for a level of
  215 (rounded up to whole display frames above 8 ms, at most 33 ms), locks focus and white balance where offered, and checks the
  camera kept them. One exposure, one measurement, one correction.
- **Steady** (a camera without those controls — iPhone Safari among them): the camera keeps its own exposure, which the node keeps
  still by never showing a full white or black frame (they would make it ramp). The scan's white and black are made afterwards from
  the three coarsest Gray pairs of each axis (`ReferenceSynthesis`): per pixel, the pair that differs most. Each coded frame's gain
  is then read from the picture — a pattern and its inverse, and two sinusoid steps half a period apart, sum to white plus black at
  every lit pixel, so each pair's two gains are a least-squares fit — and levelled to the median (`GainNormaliser`). The drift in
  STATE (`scan.pairing.aeDriftPct`) is the camera's: a still camera reads near zero whatever the picture shows.

## Pairing a picture with its pattern by what it shows

- **Locate** (12 frames): every candidate tag cell (40 sites of 4 × 4 cells, c = max(6, W/80) px, inside a 4 % inset) is named by a
  Gray code over ten frames after an all-lit and an all-dark one (`CellLocate`). A site counts when all 16 cells are found and a
  homography through them fits within 0.5 px; up to three are chosen — the topmost, the bottommost, then the farthest.
- **Tags**: every pattern carries its tag (value (i mod 63) + 1) at the chosen sites: cell 0 lit and cell 15 dark as references,
  cells 1–14 seven differential pairs carrying six bits and their parity (`FrameTag`). The phone's reader (`tags.js`) and the
  node's read the same value from the same pixels — same samples, same integer rule (checked under Node 22 on rendered pictures).
- **Lock-step**: the next pattern goes up only when the phone says it saw this one — its tag at every lit site in two consecutive
  frames that also agree. A picture whose sites disagree is a rolling-shutter mix and is thrown away (counted). Uploads trail, at
  most six in flight. A phone stuck on a pattern has it shown again (twice at most), then the view fails with its words; `SCAN NEXT`
  shows it again by hand.
- **Checked on the node**: every picture's tag is read again when it lands; one that shows another pattern (`caught`) or never
  arrives is shot again. A closing picture of the first pattern that differs from its first one (brightness levelled) by more than
  six levels means the phone moved, and the view fails — observed, never assumed.
- **Timing** is the cross-check (a picture taken before its pattern was presented is counted, `timingDisagrees`) and, where the
  camera sees no tag place, the pairing (`mode:"timing"`, with words to keep the phone very still).
- The tags are not the pattern: their footprint, and anything decoded within 2 px of a tag place, is left out of the decode.

## Verified here, and what is not

- Core: PMF1 (round trip, CRC, every refusal), the QR code (read back by an independent reader and by jsQR), the tag codec (every
  value, every flipped pair caught, the rolling-shutter mix refused), cell locate against the simulator's truth, references and
  gains (a walking gain read within 1 % per frame; a still camera at zero drift).
- Net: tickets once and expiring (on a hand-moved clock), keys as digests, lockout, the limits' close codes, CSP and headers, the
  socket's origin and own-host rules, the clock exchange.
- The page in Chromium (Playwright, `/opt/pw-browsers`): no request carries the ticket; the hello carries it once; the lock profile
  sets manual exposure on the fake camera; a camera with no controls runs the steady profile with its words; plain HTTP refuses
  with words and no camera.
- Engine: a whole scan through a phone over its real socket (a C# phone that behaves as the page), with late pictures, a
  rolling-shutter mix, a dropped upload and a false picture all paired right (the projector within 1 cm, 0.25°, 0.6 % of throw);
  the steady profile with a wandering gain; a phone that hangs up or goes silent stops the scan cleanly with words in STATE;
  `SCAN NEXT`; the verbs, the preflight row, INDEX; the ticket and key in no STATE, context, journal or project.
- Simulator: a camera hunting 1 % a frame with no white or black decodes ≥ 90 % of the locked pixels and holds the pose within twice
  the locked thresholds.
- **Lab only**: real phones (Chrome Android's exposure lock, iOS Safari's steady behaviour, lens switching, rolling-shutter timing,
  Wi-Fi), the certificate click-through, motion sensors. **Next** (wp2-multiscan): views INTO a scan, the multi-view solve, safe
  suggestions, the Folder camera, the closed browser loop.
