// The closed loop's phone: the camera page in Chromium, its camera a track made from the pictures a test bridge sends (the scene lit
// by what the node's output really shows), so the real page — locate, tags, settle, the exposure profile, compression, upload —
// scans through the real node. The profile is "android" (a camera offering manual exposure, focus and white balance, whose settings
// the bridge honours) or "ios" (a camera offering none: the bridge's auto-exposure keeps hunting). Runs until a line "quit" on stdin,
// then writes what it saw as JSON.
//   node loop.mjs <page url with #t=ticket> <bridge ws url> <android|ios> <latency ms> <out.json>
import { writeFileSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { load } from './load.mjs';

const { chromium } = await load('playwright');
const [pageUrl, bridge, profile, latency, out] = process.argv.slice(2);

/** The camera the page is given: a MediaStreamTrackGenerator fed by the bridge; with "android", the controls a phone offers. */
function shim(cfg) {
  const ws = new WebSocket(cfg.bridge);
  ws.binaryType = 'arraybuffer';
  const gen = new MediaStreamTrackGenerator({ kind: 'video' });
  const writer = gen.writable.getWriter();
  let timestamp = 0;
  let size = { width: 0, height: 0 };
  /** Each frame's capture instant by its timestamp: a phone's camera says when it took a picture (captureTime). */
  const captures = new Map();
  const rvfc = HTMLVideoElement.prototype.requestVideoFrameCallback;
  HTMLVideoElement.prototype.requestVideoFrameCallback = function (callback) {
    return rvfc.call(this, (now, meta) => {
      const at = captures.get(Math.round(meta.mediaTime * 1e6));
      callback(now, at === undefined ? meta : { ...meta, captureTime: at });
    });
  };
  const state = { exposureMode: 'continuous', exposureTime: 100, iso: 100, focusMode: 'continuous', whiteBalanceMode: 'continuous', focusDistance: 2 };
  ws.onmessage = (e) => {
    if (typeof e.data === 'string') {
      const m = JSON.parse(e.data);
      // What auto-exposure holds now, as a phone reports it while exposure is continuous.
      if (m.t === 'auto' && state.exposureMode === 'continuous') state.exposureTime = m.exposureTime;
      return;
    }
    const view = new DataView(e.data);
    const w = view.getUint16(0, true);
    const h = view.getUint16(2, true);
    size = { width: w, height: h };
    const i420 = new Uint8Array((w * h * 3) / 2);
    i420.set(new Uint8Array(e.data, 4, w * h));
    i420.fill(128, w * h);
    timestamp += 33333;
    // The bridge sends each picture its latency after taking it: taken then, on this page's clock.
    captures.set(timestamp, performance.now() - cfg.latencyMs);
    if (captures.size > 120) captures.delete(captures.keys().next().value);
    const frame = new VideoFrame(i420, { format: 'I420', codedWidth: w, codedHeight: h, timestamp });
    writer.write(frame).catch(() => frame.close());
  };
  const base = gen.getSettings.bind(gen);
  gen.getSettings = () => ({ ...base(), ...size, frameRate: 30, ...(cfg.profile === 'android' ? state : {}) });
  if (cfg.profile === 'android') {
    gen.getCapabilities = () => ({
      exposureMode: ['manual', 'continuous'],
      exposureTime: { min: 1, max: 333, step: 1 },
      iso: { min: 50, max: 1600, step: 1 },
      focusMode: ['manual', 'continuous'],
      focusDistance: { min: 0, max: 10, step: 0.01 },
      whiteBalanceMode: ['manual', 'continuous'],
    });
    gen.applyConstraints = async (c) => {
      const wanted = (c && c.advanced && c.advanced[0]) || c || {};
      for (const key of ['exposureMode', 'exposureTime', 'iso', 'focusMode', 'focusDistance', 'whiteBalanceMode']) {
        if (key in wanted) state[key] = wanted[key];
      }
      if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ t: 'exposure', mode: state.exposureMode, exposureTime: state.exposureTime, iso: state.iso }));
    };
  } else {
    gen.getCapabilities = () => ({});
    gen.applyConstraints = async () => {};
  }
  const stream = new MediaStream([gen]);
  navigator.mediaDevices.getUserMedia = async () => stream;
  // No label a back-camera rule would switch away from: the page keeps the one camera it was given.
  navigator.mediaDevices.enumerateDevices = async () => [{ kind: 'videoinput', label: 'loop camera', deviceId: 'loop', groupId: 'loop', toJSON() { return this; } }];
}

const browser = await chromium.launch({ args: ['--no-proxy-server', '--use-fake-ui-for-media-stream', '--autoplay-policy=no-user-gesture-required'] });
const result = { status: '', console: [], sent: 0, received: 0, binary: 0, prepared: [], said: [] };
try {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, bypassCSP: true });
  const page = await context.newPage();
  page.on('console', (m) => result.console.push(m.type() + ': ' + m.text()));
  page.on('pageerror', (e) => result.console.push('error: ' + e.message));
  page.on('websocket', (ws) => {
    if (!ws.url().includes('/scan/ws')) return;
    ws.on('framesent', (f) => {
      result.sent++;
      if (typeof f.payload !== 'string') result.binary++;
      else if (f.payload.includes('"t":"prepared"')) result.prepared.push(JSON.parse(f.payload));
    });
    ws.on('framereceived', (f) => {
      result.received++;
      if (typeof f.payload === 'string' && f.payload.includes('"t":"say"')) result.said.push(JSON.parse(f.payload).words);
    });
  });
  await page.addInitScript(shim, { bridge, profile, latencyMs: Number(latency) });
  await page.goto(pageUrl);
  await page.click('#allow');
  await page.waitForFunction(() => document.getElementById('status')?.textContent?.startsWith('Ready'), null, { timeout: 30000 });
  result.status = await page.evaluate(() => document.getElementById('status')?.textContent ?? '');
  process.stdout.write('READY\n');
  const lines = createInterface({ input: process.stdin });
  for await (const line of lines) {
    if (line.trim() === 'quit') break;
  }
  result.status = await page.evaluate(() => document.getElementById('status')?.textContent ?? '');
} catch (e) {
  result.console.push('driver: ' + (e?.message ?? e));
  process.stdout.write('FAILED ' + (e?.message ?? e) + '\n');
} finally {
  writeFileSync(out, JSON.stringify(result));
  await Promise.race([browser.close(), new Promise((resolve) => setTimeout(resolve, 10000))]);
  // stdin stays open (the test holds it): the driver ends here, not when node runs out of work.
  process.exit(0);
}
