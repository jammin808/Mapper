// @ts-check
// PlayMap's camera page: makes this phone a scan camera of the node that served it (playmap.camera.v1 on /scan/ws).
//
// JOIN ──Allow (camera, motion, wake lock)──▶ CONNECTING ──hello/welcome──▶ READY
// READY ──prepare──▶ exposure profile ──prepared──▶ READY ; READY ──cue──▶ WATCHING ──seen──▶ (upload) ──▶ READY
// (a "still" cue is answered with the next picture as it is: an align run or a camera view reads frames that way)
// any ──socket lost──▶ RECONNECTING (with the stored key, waiting longer each time) ──▶ READY
// any ──refused for good (key unknown, forgotten, replaced, switched off)──▶ STOPPED (a forgotten key is dropped)
// any ──the phone turned (its picture changed size)──▶ joins again with the new size
//
// The invite's ticket arrives in the URL's fragment, which a browser never sends; it is taken out of the address at once and
// travels only in the first message on the socket. The key the node gives back is kept in this browser's storage until the desk
// forgets the phone or the person forgets the node here.

import { SUBPROTOCOL, pmf1, afterClose } from './protocol.js';
import { canLock, litTop, planExposure, afterMiss, applyDropping } from './exposure.js';
import { Watcher } from './watch.js';

/** @param {string} id */
const el = (id) => /** @type {HTMLElement} */ (document.getElementById(id));

const storageKey = 'playmap.phone.' + location.host;
const PATTERN_TIMEOUT_MS = 5000;
const watcher = new Watcher();

const s = {
  ticket: '',
  key: '',
  name: '',
  /** @type {WebSocket | null} */ ws: null,
  /** @type {MediaStreamTrack | null} */ track: null,
  /** @type {HTMLVideoElement | null} */ video: null,
  /** @type {any} */ caps: {},
  width: 0,
  height: 0,
  fps: 0,
  label: '',
  offsetMs: 0,
  hasClock: false,
  /** @type {any} */ exchange: null,
  /** @type {any} */ cue: null,
  seq: 0,
  maxInFlight: 6,
  inFlight: 0,
  /** @type {((gray: Uint8Array, captureMs: number) => void) | null} */ onFrame: null,
  stopped: false,
  // The page closed its own socket (to join again, or to forget the node): the node's echo of that close is not news.
  closingSelf: false,
  // The phone turned during a first join: it joins again once the welcome has given it its key.
  rejoin: false,
  rvfc: false,
  attempts: 0,
  moving: false,
  /** @type {any} */ wakeLock: null,
  /** @type {CanvasRenderingContext2D | null} */ ctx: null,
  /** @type {Uint8Array} */ gray: new Uint8Array(0),
};

/** @param {string} id @param {boolean} on */
function show(id, on) {
  el(id).hidden = !on;
}

/** @param {string} words @param {string} [level] */
function say(words, level = 'info') {
  const p = el('say');
  p.textContent = words;
  p.hidden = words.length === 0;
  p.dataset.level = level;
}

/** @param {string} words */
function status(words) {
  el('status').textContent = words;
}

function start() {
  // The ticket leaves the address before anything else, secure page or not.
  const m = /(?:^#|&)t=([A-Z2-7]{32})(?:&|$)/.exec(location.hash);
  if (m) s.ticket = m[1];
  if (location.hash) history.replaceState(null, '', location.pathname + location.search);
  if (!window.isSecureContext) {
    show('insecure', true);
    const link = /** @type {HTMLAnchorElement} */ (el('https-link'));
    link.href = 'https://' + location.host + '/scan/';
    link.textContent = 'https://' + location.host + '/scan/';
    return;
  }
  try {
    s.key = localStorage.getItem(storageKey) ?? '';
  } catch {
    s.key = '';
  }
  show('join', true);
  el('forget').addEventListener('click', forgetHere);
  show('forget', s.key.length > 0);
  if (!s.ticket && !s.key) {
    el('join-words').textContent = 'Scan the invite the desk shows to make this phone its camera.';
    /** @type {HTMLButtonElement} */ (el('allow')).disabled = true;
    return;
  }
  el('allow').addEventListener('click', () => {
    // iOS asks for motion only within the tap itself: asked before anything is awaited.
    const dme = /** @type {any} */ (window).DeviceMotionEvent;
    const motion = dme && typeof dme.requestPermission === 'function' ? dme.requestPermission().catch(() => 'denied') : Promise.resolve('granted');
    const button = /** @type {HTMLButtonElement} */ (el('allow'));
    button.disabled = true;
    allow(motion).catch((e) => {
      say('The camera did not open: ' + (e?.message ?? e), 'warn');
      // The ticket is still in hand: the person may try again.
      button.disabled = false;
    });
  });
}

/** Drops this node's key from this browser: the phone needs a new invite to join again. */
function forgetHere() {
  try {
    localStorage.removeItem(storageKey);
  } catch {
    // Nothing was kept.
  }
  s.key = '';
  s.stopped = true;
  closeSelf('forgot this node');
  show('forget', false);
  // With no invite in hand there is nothing to join with.
  if (!s.ticket) /** @type {HTMLButtonElement} */ (el('allow')).disabled = true;
  say('This phone keeps nothing for this node now: scan a new invite to join again.');
}

/** The back camera with one physical lens where the labels tell; the stream at up to 1080p 30. @param {Promise<string>} motion */
async function allow(motion) {
  // The person asked to join: whatever stopped the page before no longer holds.
  s.stopped = false;
  s.attempts = 0;
  const want = { width: { ideal: 1920 }, height: { ideal: 1080 }, frameRate: { ideal: 30 } };
  let stream = await navigator.mediaDevices.getUserMedia({ video: { ...want, facingMode: { ideal: 'environment' } }, audio: false });
  const devices = (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === 'videoinput');
  const back = devices.filter((d) => /back|rear|environment/i.test(d.label));
  const single = back.find((d) => !/dual|triple|virtual|ultra|tele/i.test(d.label)) ?? back[0];
  const current = stream.getVideoTracks()[0];
  if (single && current.getSettings().deviceId !== single.deviceId) {
    try {
      const other = await navigator.mediaDevices.getUserMedia({ video: { ...want, deviceId: { exact: single.deviceId } }, audio: false });
      current.stop();
      stream = other;
    } catch {
      // The single-lens camera did not open: the one already open stays.
    }
  }
  const track = stream.getVideoTracks()[0];
  s.track = track;
  s.caps = typeof track.getCapabilities === 'function' ? track.getCapabilities() : {};
  const settings = track.getSettings();
  s.fps = settings.frameRate ?? 0;
  s.label = track.label;
  await applyDropping(track, { zoom: 1, torch: false });
  const video = /** @type {HTMLVideoElement} */ (el('video'));
  video.srcObject = stream;
  await video.play();
  s.video = video;
  raster(video.videoWidth || settings.width || 0, video.videoHeight || settings.height || 0);
  // The phone turned (or the camera changed its size): everything measured on the old raster is void, so it joins again.
  video.addEventListener('resize', () => {
    if (video.videoWidth && video.videoHeight && (video.videoWidth !== s.width || video.videoHeight !== s.height)) {
      raster(video.videoWidth, video.videoHeight);
      chips();
      say('The phone turned: it joins again with its new picture size. Keep it still in one place for the scan.', 'warn');
      // A join made with an invite (its ticket kept until the welcome) joins again once it has its key; one made with the key now.
      if (s.ticket) s.rejoin = true;
      else closeSelf('the phone turned: joining again with its new picture size');
    }
  });
  await motionAndWake(motion);
  frames(video);
  show('join', false);
  show('ready', true);
  connect();
}

/** Closes the page's own socket, its words its own. @param {string} why */
function closeSelf(why) {
  if (!s.ws) return;
  s.closingSelf = true;
  s.ws.close(1000, why);
}

/** The picture's size, and everything sized by it. @param {number} w @param {number} h */
function raster(w, h) {
  s.width = w;
  s.height = h;
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  s.ctx = canvas.getContext('2d', { willReadFrequently: true });
  s.gray = new Uint8Array(w * h);
  watcher.resize(w, h);
  endCue();
}

/** Motion (asked on the tap) and the screen kept awake while the page is open. @param {Promise<string>} motion */
async function motionAndWake(motion) {
  if ((await motion) !== 'granted') say('Motion is not shared: the node checks for movement from the pictures alone.');
  let since = 0;
  window.addEventListener('devicemotion', (e) => {
    const a = e.acceleration;
    const r = e.rotationRate;
    const accel = a ? Math.hypot(a.x ?? 0, a.y ?? 0, a.z ?? 0) : 0;
    const turn = r ? Math.max(Math.abs(r.alpha ?? 0), Math.abs(r.beta ?? 0), Math.abs(r.gamma ?? 0)) : 0;
    const now = performance.now();
    if (accel > 0.15 || turn > 3) {
      if (since === 0) since = now;
      if (now - since > 100 && !s.moving) {
        s.moving = true;
        send({ t: 'state', motion: 'moving' });
        if (s.cue) send({ t: 'moved', at: now, magnitude: Math.round(accel * 100) / 100 });
      }
    } else if (since !== 0 && s.moving && now - since > 1000) {
      since = 0;
      s.moving = false;
      send({ t: 'state', motion: 'still' });
    } else if (!s.moving) {
      since = 0;
    }
  });
  await wake();
  document.addEventListener('visibilitychange', () => {
    send({ t: 'state', visible: document.visibilityState === 'visible' });
    if (document.visibilityState === 'visible') wake();
  });
}

async function wake() {
  const lock = /** @type {any} */ (navigator).wakeLock;
  if (!lock) return;
  try {
    s.wakeLock = await lock.request('screen');
  } catch {
    s.wakeLock = null;
  }
}

/**
 * Every camera frame as gray8 with when it was taken (page ms), to whoever is waiting for frames. The gray buffer is reused:
 * whoever keeps a frame copies it. Without frame callbacks the page draws at the screen's rate and cannot tell one camera frame
 * from the next, so it takes no pattern by agreeing frames (see watch()).
 * @param {HTMLVideoElement} video
 */
function frames(video) {
  const rvfc = typeof video.requestVideoFrameCallback === 'function';
  s.rvfc = rvfc;
  /** @param {number} now @param {any} [meta] */
  const tick = (now, meta) => {
    if (s.onFrame && s.ctx && s.width > 0) {
      s.ctx.drawImage(video, 0, 0, s.width, s.height);
      const rgba = s.ctx.getImageData(0, 0, s.width, s.height).data;
      const gray = s.gray;
      for (let k = 0, p = 0; k < gray.length; k++, p += 4) gray[k] = (54 * rgba[p] + 183 * rgba[p + 1] + 19 * rgba[p + 2] + 128) >> 8;
      const capture = meta?.captureTime ?? (meta?.presentationTime ?? now) - (s.fps > 0 ? 1000 / s.fps : 33);
      s.onFrame(gray, capture);
    }
    if (rvfc) video.requestVideoFrameCallback(tick);
    else requestAnimationFrame(tick);
  };
  if (rvfc) video.requestVideoFrameCallback(tick);
  else requestAnimationFrame(tick);
}

/** @param {any} message */
function send(message) {
  if (s.ws && s.ws.readyState === WebSocket.OPEN) s.ws.send(JSON.stringify(message));
}

function platform() {
  const ua = navigator.userAgent;
  if (/Android/.test(ua) && /Chrome\//.test(ua)) return 'android-chrome';
  if (/iPhone|iPad/.test(ua) && /Safari/.test(ua)) return 'ios-safari';
  return 'other';
}

/** @param {any} range */
const span = (range) => (range && typeof range.min === 'number' ? [range.min, range.max, range.step ?? 0] : []);

function hello() {
  const c = s.caps;
  /** @type {any} */
  const m = {
    t: 'hello',
    v: 1,
    phone: { name: s.name },
    ua: { platform: platform(), browser: navigator.userAgent.slice(0, 80) },
    camera: {
      label: s.label,
      width: s.width,
      height: s.height,
      fps: s.fps,
      caps: {
        exposure: c.exposureMode ?? [],
        exposureTime: span(c.exposureTime),
        iso: span(c.iso),
        focus: c.focusMode ?? [],
        focusDistance: span(c.focusDistance),
        whiteBalance: c.whiteBalanceMode ?? [],
        torch: !!c.torch,
        zoom: span(c.zoom),
      },
    },
    features: {
      rvfc: 'requestVideoFrameCallback' in HTMLVideoElement.prototype,
      deflate: typeof CompressionStream !== 'undefined',
      motion: 'DeviceMotionEvent' in window,
      wakeLock: 'wakeLock' in navigator,
    },
  };
  // The ticket until the welcome (a socket lost before it may try again with it), then the key.
  if (s.ticket) m.ticket = s.ticket;
  else m.key = s.key;
  return m;
}

function connect() {
  status('Connecting to the node…');
  const ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/scan/ws', SUBPROTOCOL);
  ws.binaryType = 'arraybuffer';
  s.ws = ws;
  s.hasClock = false;
  /** @type {number | undefined} */
  let pinger;
  ws.addEventListener('open', () => {
    ws.send(JSON.stringify(hello()));
    pinger = window.setInterval(() => send({ t: 'ping', t1: performance.now(), prev: s.exchange }), 1000);
  });
  ws.addEventListener('message', (e) => {
    if (typeof e.data === 'string') onMessage(JSON.parse(e.data));
  });
  ws.addEventListener('close', (e) => {
    window.clearInterval(pinger);
    if (s.ws === ws) s.ws = null;
    endCue();
    s.inFlight = 0;
    const self = s.closingSelf;
    s.closingSelf = false;
    if (e.reason && !self) say(e.reason, 'warn');
    // A refused invite is not tried again; a socket the page closed itself to join again joins again at once.
    if (/^ERR /.test(e.reason)) s.ticket = '';
    const next = self ? { again: !s.stopped, forgetKey: false, delayMs: 0 } : afterClose(e.code, e.reason, s.key.length > 0 || s.ticket.length > 0, s.attempts);
    if (next.forgetKey) forgetHere();
    if (!s.stopped && next.again) {
      s.attempts++;
      status('Reconnecting…');
      window.setTimeout(connect, next.delayMs);
    } else {
      s.stopped = true;
      status(s.key && !/^ERR /.test(e.reason) ? 'Not connected' : 'Not connected: scan a new invite to join again');
    }
  });
}

/** @param {any} m */
function onMessage(m) {
  switch (m.t) {
    case 'welcome':
      s.attempts = 0;
      s.stopped = false;
      s.ticket = '';
      if (m.key) {
        s.key = m.key;
        try {
          localStorage.setItem(storageKey, m.key);
          show('forget', true);
        } catch {
          say('This browser keeps nothing: the phone needs a new invite next time.');
        }
      }
      s.maxInFlight = m.limits?.maxInFlight ?? 6;
      el('title').textContent = (m.node?.name ?? 'PlayMap') + ' camera';
      status('Ready: ' + (m.phone?.name ?? 'this phone') + ' is the scan camera');
      chips();
      if (s.rejoin) {
        s.rejoin = false;
        closeSelf('the phone turned: joining again with its new picture size');
      }
      break;
    case 'pong':
      s.exchange = { t1: m.t1, t2: m.t2, t3: m.t3, t4: performance.now() };
      break;
    case 'clock':
      s.offsetMs = m.offsetMs;
      s.hasClock = true;
      break;
    case 'prepare':
      prepare(m).catch((e) => send({ t: 'prepared', exposure: 'steady', focus: 'auto', whiteBalance: 'auto', words: 'the exposure could not be set: ' + (e?.message ?? e) }));
      break;
    case 'sites':
      watcher.placed(m);
      break;
    case 'cue':
      watch(m);
      break;
    case 'ack':
      s.inFlight = Math.max(0, s.inFlight - 1);
      break;
    case 'say':
      say(m.words, m.level);
      break;
    default:
      break;
  }
}

function chips() {
  const list = el('chips');
  list.textContent = '';
  const items = [
    [canLock(s.caps) ? 'Exposure can lock' : 'Brightness kept steady (no exposure lock from a web page here)', canLock(s.caps)],
    [`${s.width}×${s.height} ${Math.round(s.fps)} fps`, true],
  ];
  for (const [words, ok] of items) {
    const li = document.createElement('li');
    li.textContent = String(words);
    if (ok) li.className = 'ok';
    list.appendChild(li);
  }
}

/**
 * The next frames until a condition holds or time runs out; answers a copy of the last frame seen.
 * @param {(gray: Uint8Array) => boolean} done @param {number} ms @returns {Promise<Uint8Array>}
 */
function until(done, ms) {
  return new Promise((resolve) => {
    const end = performance.now() + ms;
    let over = false;
    const finish = (/** @type {Uint8Array} */ gray) => {
      if (over) return;
      over = true;
      window.clearTimeout(guard);
      if (s.onFrame === handler) s.onFrame = null;
      resolve(gray.slice());
    };
    const handler = (/** @type {Uint8Array} */ gray) => {
      if (done(gray) || performance.now() > end) finish(gray);
    };
    // Frames that stopped coming (or were taken by something else) still end the wait.
    const guard = window.setTimeout(() => finish(s.gray), ms + 1000);
    s.onFrame = handler;
  });
}

/** Mean brightness, stable within 2 % over half a second (at most 4 s). */
async function settleExposure() {
  /** @type {{t: number, m: number}[]} */
  const means = [];
  return until((gray) => {
    let sum = 0;
    for (let k = 0; k < gray.length; k += 16) sum += gray[k];
    const mean = sum / Math.ceil(gray.length / 16);
    const now = performance.now();
    means.push({ t: now, m: mean });
    while (means.length && now - means[0].t > 500) means.shift();
    const lo = Math.min(...means.map((x) => x.m));
    const hi = Math.max(...means.map((x) => x.m));
    return means.length > 3 && now - means[0].t > 400 && hi - lo <= 0.02 * Math.max(1, hi);
  }, 4000);
}

/** The level the lock profile aims the lit stripes at, give or take. */
const LOCK_LOW = 190;
const LOCK_HIGH = 240;

/**
 * The exposure profile the node asks for, as far as this camera allows. A new projector's run: the last projector's tag places
 * are void, and the probe on the glass now is the picture the first locate frame must change from.
 * @param {any} m
 */
async function prepare(m) {
  endCue();
  const prepared = await profile(m);
  // What the camera shows now, with the exposure it will scan with: the first picture taken must change from it.
  watcher.prepared(m, await until(() => true, 1000));
  send(prepared);
}

/** @param {any} m @returns {Promise<any>} */
async function profile(m) {
  const track = /** @type {MediaStreamTrack} */ (s.track);
  const c = s.caps;
  const focusOffered = Array.isArray(c.focusMode) && c.focusMode.includes('manual');
  const wbOffered = Array.isArray(c.whiteBalanceMode) && c.whiteBalanceMode.includes('manual');
  if (m.profile === 'lock' && canLock(c)) {
    await applyDropping(track, { exposureMode: 'continuous', focusMode: 'continuous' });
    const gray = await settleExposure();
    const settled = /** @type {any} */ (track.getSettings());
    if (typeof settled.exposureTime === 'number') {
      let t = settled.exposureTime;
      let iso = settled.iso ?? c.iso?.min ?? 100;
      let level = litTop(gray);
      // A phone's video is gamma-encoded; the first correction tells the curve this camera really has.
      let gamma = 2.2;
      /** @type {number | undefined} */
      let below;
      let landed = 'the camera did not take manual exposure';
      // One measurement, one exposure; measured again, and corrected once more if it did not land.
      for (let round = 0; round < 2; round++) {
        const plan = planExposure({ t0: t, s0: iso, level, gamma, below, hasIso: !!c.iso, displayHz: m.displayHz, timeRange: c.exposureTime, isoRange: c.iso });
        /** @type {Record<string, any>} */
        const want = { exposureMode: 'manual', exposureTime: plan.t1 };
        if (c.iso) want.iso = plan.s1;
        if (focusOffered) {
          want.focusMode = 'manual';
          if (settled.focusDistance !== undefined) want.focusDistance = settled.focusDistance;
        }
        if (wbOffered) want.whiteBalanceMode = 'manual';
        await applyDropping(track, want);
        const after = /** @type {any} */ (track.getSettings());
        const now = await until(() => false, 250);
        if (after.exposureMode !== 'manual') break;
        const next = { t: after.exposureTime ?? plan.t1, s: after.iso ?? plan.s1, level: litTop(now) };
        if (next.level >= LOCK_LOW && next.level <= LOCK_HIGH) {
          // What the camera says it holds, never what it was asked.
          return {
            t: 'prepared',
            exposure: 'locked',
            exposureMs: Math.round(next.t * 10) / 100,
            iso: after.iso ?? 0,
            focus: after.focusMode === 'manual' ? 'locked' : focusOffered ? 'auto' : 'not offered',
            whiteBalance: after.whiteBalanceMode === 'manual' ? 'locked' : wbOffered ? 'auto' : 'not offered by this camera',
            words: 'Exposure locked',
          };
        }
        landed = `its level landed at ${next.level}, outside ${LOCK_LOW}–${LOCK_HIGH}`;
        const miss = afterMiss({ t, s: iso, level }, next, gamma);
        gamma = miss.gamma;
        below = miss.below;
        ({ t, s: iso, level } = miss.base);
      }
      await applyDropping(track, { exposureMode: 'continuous' });
      await settleExposure();
      return steady(track, focusOffered, wbOffered, `The exposure lock did not hold (${landed}): brightness kept steady instead`);
    }
  }
  if (wbOffered) await applyDropping(track, { whiteBalanceMode: 'manual' });
  await settleExposure();
  return steady(track, focusOffered, wbOffered, platform() === 'ios-safari'
    ? 'iPhone cannot lock exposure from a web page: brightness kept steady instead'
    : 'This camera cannot lock its exposure from a web page: brightness kept steady instead');
}

/**
 * What the camera holds on the steady profile.
 * @param {MediaStreamTrack} track @param {boolean} focusOffered @param {boolean} wbOffered @param {string} words
 */
function steady(track, focusOffered, wbOffered, words) {
  const after = /** @type {any} */ (track.getSettings());
  return {
    t: 'prepared',
    exposure: 'steady',
    exposureMs: 0,
    iso: 0,
    focus: after.focusMode === 'manual' ? 'locked' : focusOffered ? 'auto' : 'not offered',
    whiteBalance: after.whiteBalanceMode === 'manual' ? 'locked' : wbOffered ? 'auto' : 'not offered by this camera',
    words,
  };
}

/** The cue in hand ends (answered, replaced, or void): its timer with it, and its frame handler if still its own. */
function endCue() {
  const cue = s.cue;
  if (!cue) return;
  window.clearTimeout(cue.timer);
  if (s.onFrame === cue.handler) s.onFrame = null;
  s.cue = null;
}

/**
 * A cue: watch the frames until enough in a row show it and agree (the watcher's rule), then say seen at once and upload the picture.
 * Frames taken before the cue's earliest instant cannot show it — known only once the clock exchange has given the node's clock.
 * A still is the next picture as it is: whoever asked settles and pairs it.
 * @param {any} m
 */
function watch(m) {
  endCue();
  if (!s.rvfc && m.kind !== 'still') {
    // Without frame times two agreeing draws may be one camera frame: no pattern is taken that way.
    send({ t: 'stuck', cue: m.id, why: 'this browser cannot tell one camera frame from the next: update it, or use another' });
    return;
  }
  const watching = watcher.cue(m);
  /** @type {any} */
  const cue = { id: m.id, timer: 0, handler: null };
  cue.timer = window.setTimeout(() => {
    if (s.cue !== cue) return;
    endCue();
    send({ t: 'stuck', cue: cue.id, why: watching.why() });
  }, PATTERN_TIMEOUT_MS);
  cue.handler = (/** @type {Uint8Array} */ gray, /** @type {number} */ capture) => {
    if (s.cue !== cue) return;
    if (s.hasClock && capture < m.earliest - s.offsetMs) {
      watching.early();
      return;
    }
    if (m.kind === 'still') {
      endCue();
      const seq = ++s.seq;
      send({ t: 'seen', cue: cue.id, seq, tag: 0, capture, sites: 0, margin: 0, settleDiff: 0, rolling: 0 });
      upload('still', seq, cue.id, capture, gray.slice());
      return;
    }
    const verdict = watching.offer(gray);
    if (!verdict) return;
    endCue();
    const seen = verdict.seen;
    const seq = ++s.seq;
    send({ t: 'seen', cue: cue.id, seq, tag: m.tag, capture, sites: seen.sites, margin: seen.margin, settleDiff: seen.settleDiff, rolling: seen.rolling });
    upload(m.kind === 'locate' ? 'locate' : 'pattern', seq, cue.id, capture, seen.picture);
  };
  s.cue = cue;
  s.onFrame = cue.handler;
}

/** @param {string} kind @param {number} seq @param {number} cue @param {number} capture @param {Uint8Array} gray */
async function upload(kind, seq, cue, capture, gray) {
  const frame = await pmf1({ kind, seq, cue, capture, w: s.width, h: s.height, roi: [0, 0, s.width, s.height] }, gray);
  if (s.ws && s.ws.readyState === WebSocket.OPEN) {
    s.inFlight++;
    s.ws.send(frame);
  }
}

start();
