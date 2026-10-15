# wp2-phonescan on the wire

The phone as a local scan camera (`docs/PHONESCAN.md`). A phone joins through an invite and becomes a camera rig of kind `Phone`
(its `source` is the phone's node-local id, `ph…`); a scan runs through that rig with the ordinary `SCAN START <camera>`.

## Verbs

| Verb | Syntax | Answers | Risk | Changes the show |
|---|---|---|---|---|
| `PHONE INVITE` | `PHONE INVITE [<name>] [FOR <minutes 1-60>]` | To the asking session only: JSON `{name, url, qr:{size, modules}, fingerprint, expiresUnixMs}`. `url` is `https://<address>:<port>/scan/#t=<ticket>` — the ticket (20 random bytes, 32 base32 letters) rides only in the fragment, which a browser never sends; `qr.modules` is the URL as a QR code (byte mode, level M, versions 1–10), row-major, most significant bit first, base64; `fingerprint` is the short fingerprint of the certificate the web port really serves (read by a TLS handshake to the port), for the person to compare with the phone's warning. The address is this machine's on the asking session's subnet, else its first private address. Default life `NodeSettings.Phone.InviteMinutes` (10). Refused for the `assistant`, `mcp` and `approval` transports, from another machine on any transport but the web port's (`http`, `ws`: the line wire is not encrypted), when phones are off (`phone.enabled`), when the web port serves no https ("turn on encrypted connections first"), for `FOR` outside 1–60 and for a name over 40 characters. The journal keeps `PHONE INVITE "<name>" → invite issued, expires HH:mm UTC`, never the link | high | yes |
| `PHONE LIST` | `PHONE LIST` | JSON `{phones:[…]}`, each as STATE | low | no |
| `PHONE FORGET` | `PHONE FORGET <phone>\|ALL` | JSON `{forgotten:[ids]}`: the key no longer opens anything and the phone's socket closes ("was forgotten"); its camera rig stays in the project. Refused "no such phone <id>" / "no phone is paired with this node" | review | yes |
| `SCAN NEXT` | `SCAN NEXT` | The wait on the pattern a cued camera is asked for now ends, and the pattern goes up again and is asked for afresh (a phone that was bumped or lost its view) — unless the camera's answer was already on its way. The journal keeps `asked: the waited pattern goes up again for the camera`; refused "no scan is waiting for a picture" | review | yes |

`SCAN START <camera>` through a Phone rig runs the lock-step capture (below). `ALIGN RUN` on a group whose camera is a Phone rig
opens it through the same hub (align's `AlignRunnerOptions.OpenCamera`): the run reads pictures one `still` cue at a time; a
phone that is not connected fails the run with "<name> is not connected — open the invite on the phone (PHONE INVITE makes one)".

`INTO`, and the rows `SCAN SUGGEST`, `SCAN SOLVE`, `SCAN VIEWS`, `SCAN DROP`, are wp2-multiscan's (W3): trimmed from this block
(Assistant.Tests `CatalogueTests.TrimmedRows`), for it to add back.

## The phone's own socket: `/scan/ws`, `playmap.camera.v1`

Served by the web port as a module under `/scan` (`PhoneWebModule`): the page's files are open (GET/HEAD, no data, no secret);
the socket is a capability — admitted only by the ticket or key in its first message, refused from a page of another origin, by a
host name that is not the node's own (the node's own rule, `WebSettings.Hosts`), and over plain HTTP from another machine. Nothing
else under `/scan` is served, and the module maps nothing outside it. Every answer carries
`Content-Security-Policy: default-src 'self'; script-src 'self'; worker-src 'self'; connect-src 'self' wss://<host>; img-src 'self' blob: data:; style-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`,
`Referrer-Policy: no-referrer`, `Permissions-Policy: camera=(self), …`, `X-Content-Type-Options: nosniff`; the page is `no-store`.

| Direction | `t` | Fields |
|---|---|---|
| phone → node | `hello` | `v:1`, `ticket` (first join) or `key`, `phone:{name}`, `ua:{platform, browser, model?}`, `camera:{label, width, height, fps, caps:{exposure[], exposureTime[min,max,step], iso[], focus[], focusDistance[], whiteBalance[], torch, zoom[]}}`, `features{}` |
| node → phone | `welcome` | `phone:{id, name}`, `key` (first join only, once), `node:{id, name}`, `camera` (the rig id), `limits:{maxInFlight, maxBinary}` |
| phone → node / node → phone | `ping` / `pong`, `clock` | `ping {t1, prev:{t1,t2,t3,t4}}` (page ms); `pong {t1, t2, t3}` (node ms); `clock {offsetMs, uncertaintyMs}` (node − page, from the least-delayed of the last eight exchanges) |
| phone → node | `state` | `exposure:{mode,timeMs,iso}`, `focus:{mode}`, `wb:{mode}`, `fps`, `motion` ("moving"/"still"), `visible` |
| node → phone | `prepare` | `profile` ("lock"/"steady"), `displayHz`, `agreeTolerance`, `stableFrames`. A new projector's run: the page forgets the last projector's `sites` and `lit`, and takes the picture it shows once its exposure is set (the probe) as the one the first frame must change from |
| phone → node | `prepared` | `exposure` ("locked"/"steady"), `exposureMs`, `iso`, `focus`, `whiteBalance`, `words` |
| node → phone | `sites` | `projector`, `minSites`, `lit:[x,y,w,h]`, `sites:[{id, cells:[[x,y]×16], rows:[top,bottom]}]` (camera pixels, unrounded: the page samples the very pixels the node's tag check does) |
| node → phone | `cue` | `id`, `kind` ("locate"/"pattern"/"still"), `projector`, `i`, `tag`, `at`, `earliest` (node ms; the page uses it once the clock exchange has given the offset), `retake`, `minChange` (the level a sample of the page's 48 × 27 grid of 3 × 3 means must move by: a picture paired by change has changed when 13 samples or more did, from the last picture taken — from the one before it, for a retake of the frame just taken); a `still` asks for the next picture taken at or after `earliest`, whatever it shows (an align run, a camera view) |
| phone → node | `seen` / `stuck` | `seen {cue, seq, tag, capture (page ms), sites, margin, settleDiff, rolling}`; `stuck {cue, why}` |
| phone → node | `moved` | `at`, `magnitude`; during a view it fails the view ("moved during the view (its motion sensor said so)") |
| phone → node | binary PMF1 | magic `PMF1`, uint32 LE header length (≤ 4096), JSON header `{kind:"pattern"\|"locate"\|"still", seq, cue, capture, w, h, roi:[x,y,w,h], format:"gray8"\|"gray16", codec:"deflate-raw"\|"none", frames, crc32}`, then the ROI's rows |
| node → phone | `ack`, `say` | `ack {seq}`; `say {level, words}` |
| phone → node | `bye` | — |

Fail closed: a message type a phone does not send closes the socket with 1008 "unknown message"; anything before `hello` with
1008 "hello first"; a missing field with 1008; text over 64 KiB or binary over 16 MiB with 1009; a PMF1 frame that is not whole
(size, ROI, CRC-32, header) or not the camera's raster (the hello's, at most 16 Mpx) with 1007; a picture kind that was not asked
for with 1008. A whole picture no cue waits for any more (cancelled, timed out, shown again) is acknowledged and dropped. Five
wrong tickets or keys from an address in a minute lock it out for the rest of the minute. At most `phone.maxConnected` (4) phones
connected at once (a phone joining again replaces its own older socket). A phone that sends nothing for ten seconds (the page
pings every second) is closed with 1008 "stopped sending: nothing for 10 s". Close reasons fit 123 bytes. A join is registered
under the gate `PHONE FORGET` takes: one in flight when its phone is forgotten is refused "was forgotten". A first join the node
cannot finish (the project refuses the camera rig, the house is full) keeps no phone and gives the ticket back until it expires;
a welcome that never goes leaves no session.

## STATE

```jsonc
"phones": [{"id":"ph…","name":"Pixel 8","rig":"cm…","platform":"android-chrome|ios-safari|other","model":"Pixel 8",
            "connected":true,"address":"192.168.1.44","transport":"ws",
            "link":{"rttMs":9.8,"offsetMs":12.3,"uncertaintyMs":4.9,"uploadMBps":6.1},
            "camera":{"label":"camera2 0, facing back","width":1920,"height":1080,"fps":30,"exposure":"locked|steady|auto",
                      "exposureMs":16.7,"iso":200,"focus":"locked|auto|not offered","whiteBalance":"locked|…"}|null,
            "motion":"still|moving|moved","visible":true,"lastSeenUnixMs":…,"words":"Ready: exposure locked, focus locked"}],
"scan": { …, "pairing": null | {"mode":"tag|timing","sites":3,"rollingRejects":1,"retakes":2,"caught":1,"timingDisagrees":0,
                                "aeDriftPct":0,"exposure":"locked|steady","words":"paired by tag at 3 places"} }
```

`phones` lists every phone this node knows, connected or not (not connected: `connected:false`, empty address and transport, the
words say why — "said goodbye (its page closed)", "said goodbye: <the page's own words>", "was forgotten …", "stopped sending: …"). It carries no ticket and no key. The
fixture is `tests/fixtures/state/phonescan.json`, its cards `phonescan.cards.json`.

## Context, preflight, INDEX

- Context: `phone:<id>` (kind `phone`) for each connected phone and each phone with a rig in the project: green "ready" (with
  "brightness kept steady" on the steady profile), amber "moved during the last view", grey "not connected — PHONE INVITE makes a
  new link".
- Preflight section `scan`: each Phone camera rig — green when its phone is connected, amber when not ("a scan through it would
  fail", fix `PHONE INVITE "<name>"`); green "no phone is a camera of this project" otherwise.
- INDEX section `scan`: a row per Phone camera rig (light green ready, amber scanning, grey not connected); keys Next picture
  (`SCAN NEXT`), Cancel scan (`SCAN CANCEL`, hold), Phones (`PHONE LIST`).

## Papers note

For the integration docs pass:

- `WIRE.md`: the four rows above; `/scan` and `/scan/ws` with `playmap.camera.v1` and PMF1; STATE `phones`, `scan.pairing`; the
  module's admission (open page, capability socket, own-host and same-origin, https from other machines).
- `MAPPING.md` (scan section): the phone path — invite, the two exposure profiles, cell locate and tags, the lock-step capture with
  observed pairing, the node's own tag check, the closing move check, the tag shadow left out of the decode.
- `ARCHITECTURE.md`: `PhoneHub` (Net) behind the `IWebModule` seam; `ICuedScanCamera` (Engine/Scanning) and `PhoneCamera`;
  `ScanRunner`'s cued branch (`CuedCapture.cs`); `EngineServices.Phones` and `PhoneDoor`.
- `ROADMAP.md`: R1.9's phone camera done (one view); next: views INTO a scan, SCAN SOLVE/VIEWS/DROP, suggestions and the Folder
  camera (wp2-multiscan); later the node-local CA, USB mode, averaging and ROI, colour frames, a native iOS client.
- `CLAUDE.md` (the loop): the page's checks — `cd tests/e2e/phonescan && npm ci && npm test`; the .NET tests run the Node and
  Chromium checks themselves when node (and, for Chromium, the Playwright package and `/opt/pw-browsers`) are there, and skip
  with the reason otherwise.
