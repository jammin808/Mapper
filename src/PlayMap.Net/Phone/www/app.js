// @ts-check
// PlayMap's camera page: makes this phone a scan camera of the node that served it (playmap.camera.v1 on /scan/ws).
//
// JOIN ──Allow (camera, motion, wake lock)──▶ CONNECTING ──hello/welcome──▶ READY
// READY ──prepare──▶ exposure profile ──prepared──▶ READY ; READY ──cue──▶ WATCHING ──seen──▶ (upload) ──▶ READY
// (a "still" cue is answered with the next picture as it is: an align run or a camera view reads frames that way)
// any ──socket lost──▶ RECONNECTING (with the stored key) ──▶ READY
//
// The invite's ticket arrives in the URL's fragment, which a browser never sends; it is taken out of the address at once and
// travels only in the first message on the socket. The key the node gives back is kept in this browser's storage.

import { SUBPROTOCOL, pmf1 } from './protocol.js';
import { readFrame, Settle } from './tags.js';
import { canLock, litTop, planExposure, applyDropping } from './exposure.js';

/** @param {string} id */
const el = (id) => /** @type {HTMLElement} */ (document.getElementById(id));

const storageKey = 'playmap.phone.' + location.host;
const PATTERN_TIMEOUT_MS = 5000;

const s = {
  ticket: '',
  key: '',
  name: '',
  /** @type {WebSocket | null} */ ws: null,
  /** @type {MediaStreamTrack | null} */ track: null,
  /** @type {any} */ caps: {},
  width: 0,
  height: 0,
  fps: 0,
  label: '',
  offsetMs: 0,
  /** @type {any} */ exchange: null,
  /** @type {{cells: number[][]}[]} */ sites: [],
  lit: [0, 0, 0, 0],
  minSites: 0,
  /** @type {any} */ cue: null,
  /** @type {Uint8Array | null} */ lastGrid: null,
  seq: 0,
  maxInFlight: 6,
  inFlight: 0,
  /** @type {((gray: Uint8Array, captureMs: number) => void) | null} */ onFrame: null,
  stopped: false,
  moving: false,
  /** @type {any} */ wakeLock: null,
  /** @type {CanvasRenderingContext2D | null} */ ctx: null,
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
  if (!window.isSecureContext) {
    show('insecure', true);
    const link = /** @type {HTMLAnchorElement} */ (el('https-link'));
    link.href = 'https://' + location.host + '/scan/';
    link.textContent = 'https://' + location.host + '/scan/';
    return;
  }
  const m = /(?:^#|&)t=([A-Z2-7]{32})(?:&|$)/.exec(location.hash);
  if (m) s.ticket = m[1];
  if (location.hash) history.replaceState(null, '', location.pathname + location.search);
  try {
    s.key = localStorage.getItem(storageKey) ?? '';
  } catch {
    s.key = '';
  }
  show('join', true);
  if (!s.ticket && !s.key) {
    el('join-words').textContent = 'Scan the invite the desk shows to make this phone its camera.';
    /** @type {HTMLButtonElement} */ (el('allow')).disabled = true;
    return;
  }
  el('allow').addEventListener('click', () => {
    allow().catch((e) => say('The camera did not open: ' + (e?.message ?? e), 'warn'));
  });
}

/** The back camera with one physical lens where the labels tell; the stream at up to 1080p 30. */
async function allow() {
  /** @type {HTMLButtonElement} */ (el('allow')).disabled = true;
  const want = { width: { ideal: 1920 }, height: { ideal: 1080 }, frameRate: { ideal: 30 } };
  let stream = await navigator.mediaDevices.getUserMedia({ video: { ...want, facingMode: { ideal: 'environment' } }, audio: false });
  const devices = (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === 'videoinput');
  const back = devices.filter((d) => /back|rear|environment/i.test(d.label));
  const single = back.find((d) => !/dual|triple|virtual|ultra|tele/i.test(d.label)) ?? back[0];
  const current = stream.getVideoTracks()[0];
  if (single && current.getSettings().deviceId !== single.deviceId) {
    current.stop();
    stream = await navigator.mediaDevices.getUserMedia({ video: { ...want, deviceId: { exact: single.deviceId } }, audio: false });
  }
  const track = stream.getVideoTracks()[0];
  s.track = track;
  s.caps = typeof track.getCapabilities === 'function' ? track.getCapabilities() : {};
  const settings = track.getSettings();
  s.width = settings.width ?? 0;
  s.height = settings.height ?? 0;
  s.fps = settings.frameRate ?? 0;
  s.label = track.label;
  await applyDropping(track, { zoom: 1, torch: false });
  const video = /** @type {HTMLVideoElement} */ (el('video'));
  video.srcObject = stream;
  await video.play();
  s.width = video.videoWidth || s.width;
  s.height = video.videoHeight || s.height;
  const canvas = document.createElement('canvas');
  canvas.width = s.width;
  canvas.height = s.height;
  s.ctx = canvas.getContext('2d', { willReadFrequently: true });
  await motionAndWake();
  frames(video);
  show('join', false);
  show('ready', true);
  connect();
}

/** Motion (iOS asks on the tap) and the screen kept awake while the page is open. */
async function motionAndWake() {
  const dme = /** @type {any} */ (window).DeviceMotionEvent;
  if (dme && typeof dme.requestPermission === 'function') {
    try {
      await dme.requestPermission();
    } catch {
      say('Motion is not shared: the node checks for movement from the pictures alone.');
    }
  }
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

/** Every camera frame as gray8 with when it was taken (page ms), to whoever is waiting for frames. @param {HTMLVideoElement} video */
function frames(video) {
  const rvfc = typeof video.requestVideoFrameCallback === 'function';
  /** @param {number} now @param {any} [meta] */
  const tick = (now, meta) => {
    if (s.onFrame && s.ctx) {
      s.ctx.drawImage(video, 0, 0, s.width, s.height);
      const rgba = s.ctx.getImageData(0, 0, s.width, s.height).data;
      const gray = new Uint8Array(s.width * s.height);
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
  if (s.ticket) m.ticket = s.ticket;
  else m.key = s.key;
  // A ticket is good once: it is not kept for a reconnect.
  s.ticket = '';
  return m;
}

function connect() {
  status('Connecting to the node…');
  const ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/scan/ws', SUBPROTOCOL);
  ws.binaryType = 'arraybuffer';
  s.ws = ws;
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
    s.ws = null;
    s.cue = null;
    s.onFrame = null;
    if (e.reason) say(e.reason, 'warn');
    if (!s.stopped && s.key) {
      status('Reconnecting…');
      window.setTimeout(connect, 1000);
    } else {
      status('Not connected');
    }
  });
}

/** @param {any} m */
function onMessage(m) {
  switch (m.t) {
    case 'welcome':
      if (m.key) {
        s.key = m.key;
        try {
          localStorage.setItem(storageKey, m.key);
        } catch {
          say('This browser keeps nothing: the phone needs a new invite next time.');
        }
      }
      s.maxInFlight = m.limits?.maxInFlight ?? 6;
      el('title').textContent = (m.node?.name ?? 'PlayMap') + ' camera';
      status('Ready: ' + (m.phone?.name ?? 'this phone') + ' is the scan camera');
      chips();
      break;
    case 'pong':
      s.exchange = { t1: m.t1, t2: m.t2, t3: m.t3, t4: performance.now() };
      break;
    case 'clock':
      s.offsetMs = m.offsetMs;
      break;
    case 'prepare':
      prepare(m).catch((e) => send({ t: 'prepared', exposure: 'steady', focus: 'auto', whiteBalance: 'auto', words: 'the exposure could not be set: ' + (e?.message ?? e) }));
      break;
    case 'sites':
      s.sites = m.sites ?? [];
      s.lit = m.lit ?? [0, 0, 0, 0];
      s.minSites = m.minSites ?? 0;
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

/** The next frames until a condition holds or time runs out. @param {(gray: Uint8Array) => boolean} done @param {number} ms */
function until(done, ms) {
  return new Promise((resolve) => {
    const end = performance.now() + ms;
    s.onFrame = (gray) => {
      if (done(gray) || performance.now() > end) {
        s.onFrame = null;
        resolve(gray);
      }
    };
  });
}

/** Mean brightness, stable within 2 % over half a second (at most 4 s). */
async function settleExposure() {
  /** @type {{t: number, m: number}[]} */
  const means = [];
  return /** @type {Promise<Uint8Array>} */ (until((gray) => {
    let sum = 0;
    for (let k = 0; k < gray.length; k += 16) sum += gray[k];
    const mean = sum / Math.ceil(gray.length / 16);
    const now = performance.now();
    means.push({ t: now, m: mean });
    while (means.length && now - means[0].t > 500) means.shift();
    const lo = Math.min(...means.map((x) => x.m));
    const hi = Math.max(...means.map((x) => x.m));
    return means.length > 3 && now - means[0].t > 400 && hi - lo <= 0.02 * Math.max(1, hi);
  }, 4000));
}

/** The exposure profile the node asks for, as far as this camera allows. @param {any} m */
async function prepare(m) {
  const track = /** @type {MediaStreamTrack} */ (s.track);
  const c = s.caps;
  const focusOffered = Array.isArray(c.focusMode) && c.focusMode.includes('manual');
  const wbOffered = Array.isArray(c.whiteBalanceMode) && c.whiteBalanceMode.includes('manual');
  if (m.profile === 'lock' && canLock(c)) {
    await applyDropping(track, { exposureMode: 'continuous', focusMode: 'continuous' });
    const gray = await settleExposure();
    const settled = /** @type {any} */ (track.getSettings());
    const plan = planExposure({
      t0: settled.exposureTime ?? 100,
      s0: settled.iso ?? c.iso?.min ?? 100,
      level: litTop(gray),
      displayHz: m.displayHz,
      timeRange: c.exposureTime,
      isoRange: c.iso,
    });
    /** @type {Record<string, any>} */
    const want = { exposureMode: 'manual', exposureTime: plan.t1 };
    if (c.iso) want.iso = plan.s1;
    if (focusOffered) {
      want.focusMode = 'manual';
      if (settled.focusDistance !== undefined) want.focusDistance = settled.focusDistance;
    }
    if (wbOffered) want.whiteBalanceMode = 'manual';
    const applied = await applyDropping(track, want);
    await until(() => false, 150);
    const after = /** @type {any} */ (track.getSettings());
    const locked = after.exposureMode === 'manual';
    send({
      t: 'prepared',
      exposure: locked ? 'locked' : 'steady',
      exposureMs: Math.round((after.exposureTime ?? plan.t1) * 10) / 100,
      iso: after.iso ?? plan.s1,
      focus: applied.focusMode === 'manual' ? 'locked' : focusOffered ? 'auto' : 'not offered',
      whiteBalance: applied.whiteBalanceMode === 'manual' ? 'locked' : wbOffered ? 'auto' : 'not offered by this camera',
      words: locked ? 'Exposure locked' : 'The camera did not keep the exposure it was given: brightness kept steady instead',
    });
    return;
  }
  const applied = wbOffered ? await applyDropping(track, { whiteBalanceMode: 'manual' }) : {};
  await settleExposure();
  send({
    t: 'prepared',
    exposure: 'steady',
    exposureMs: 0,
    iso: 0,
    focus: 'auto',
    whiteBalance: applied.whiteBalanceMode === 'manual' ? 'locked' : 'not offered by this camera',
    words: platform() === 'ios-safari'
      ? 'iPhone cannot lock exposure from a web page: brightness kept steady instead'
      : 'This camera cannot lock its exposure from a web page: brightness kept steady instead',
  });
}

/**
 * A cue: watch the frames until two in a row show it and agree, then say seen at once and upload the second.
 * A pattern with tag sites is matched by its tag; a locate frame (or a pattern without sites) by a change from the last picture
 * taken. Frames taken before the cue's earliest instant cannot show it.
 * @param {any} m
 */
function watch(m) {
  const settle = new Settle(s.lit, 3);
  /** @type {Record<string, number>} */
  const fails = {};
  const cue = { id: m.id, kind: m.kind, tag: m.tag, earliest: m.earliest - s.offsetMs, minChange: m.minChange ?? 3, prevOk: false, rolling: 0 };
  s.cue = cue;
  const fail = (/** @type {string} */ why) => {
    fails[why] = (fails[why] ?? 0) + 1;
  };
  const timer = window.setTimeout(() => {
    if (s.cue !== cue) return;
    s.cue = null;
    s.onFrame = null;
    const why = Object.entries(fails).sort((a, b) => b[1] - a[1])[0]?.[0] ?? 'no picture';
    send({ t: 'stuck', cue: cue.id, why });
  }, PATTERN_TIMEOUT_MS);
  s.onFrame = (gray, capture) => {
    if (s.cue !== cue) return;
    if (capture < cue.earliest) {
      fail('taken before the pattern was up');
      return;
    }
    if (cue.kind === 'still') {
      // A still is the next picture as it is: whoever asked settles and pairs it.
      window.clearTimeout(timer);
      s.cue = null;
      s.onFrame = null;
      const seq = ++s.seq;
      send({ t: 'seen', cue: cue.id, seq, tag: 0, capture, sites: 0, margin: 0, settleDiff: 0, rolling: 0 });
      upload('still', seq, cue.id, capture, gray);
      return;
    }
    const st = settle.offer(gray, s.width, s.height);
    let read = { validSites: 0, margin: 0 };
    if (cue.kind === 'pattern' && s.sites.length > 0) {
      const r = readFrame(gray, s.width, s.height, s.sites, s.minSites);
      if (r.outcome === 'disagree') {
        cue.rolling++;
        cue.prevOk = false;
        fail('rolling shutter');
        return;
      }
      if (r.outcome !== 'read' || r.value !== cue.tag) {
        cue.prevOk = false;
        fail(r.outcome === 'read' ? 'tag shows ' + r.value : 'tag not seen');
        return;
      }
      read = r;
    } else {
      const change = s.lastGrid ? Settle.diff(s.lastGrid, st.grid) : Infinity;
      if (change < cue.minChange) {
        cue.prevOk = false;
        fail('unchanged');
        return;
      }
    }
    if (!(cue.prevOk && st.agrees)) {
      cue.prevOk = true;
      if (!st.agrees) fail('unsettled');
      return;
    }
    window.clearTimeout(timer);
    s.cue = null;
    s.onFrame = null;
    s.lastGrid = st.grid;
    const seq = ++s.seq;
    send({ t: 'seen', cue: cue.id, seq, tag: cue.tag, capture, sites: read.validSites, margin: read.margin, settleDiff: Math.round(st.diff * 100) / 100, rolling: cue.rolling });
    upload(cue.kind === 'locate' ? 'locate' : 'pattern', seq, cue.id, capture, gray);
  };
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
