// The camera page in Chromium against a running node: the invite opened (fake camera), the page joined, every request it made
// and every message it sent recorded; then the same page from a plain-HTTP name, which must say it needs https and ask for no
// camera. Writes what it saw as JSON for a .NET test to judge.
import { writeFileSync } from 'node:fs';
import { load } from './load.mjs';

const { chromium } = await load('playwright');

const [base, ticket, out] = process.argv.slice(2);
const port = new URL(base).port;
const browser = await chromium.launch({
  args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--no-proxy-server', '--host-resolver-rules=MAP phone.test 127.0.0.1'],
});
const result = { requests: [], sent: [], received: [], status: '', hashAfter: null, stored: false, insecure: {} };
try {
  const context = await browser.newContext();
  await context.grantPermissions(['camera'], { origin: base });
  const page = await context.newPage();
  page.on('request', (r) => result.requests.push({ url: r.url(), headers: r.headers(), body: r.postData() ?? '' }));
  page.on('websocket', (ws) => {
    result.requests.push({ url: ws.url(), headers: {}, body: '' });
    ws.on('framesent', (f) => result.sent.push(typeof f.payload === 'string' ? f.payload : '<binary>'));
    ws.on('framereceived', (f) => result.received.push(typeof f.payload === 'string' ? f.payload : '<binary>'));
  });
  await page.goto(`${base}/scan/#t=${ticket}`);
  result.hashAfter = await page.evaluate(() => location.hash);
  await page.click('#allow');
  await page.waitForFunction(() => document.getElementById('status')?.textContent?.startsWith('Ready'), null, { timeout: 30000 });
  result.status = await page.evaluate(() => document.getElementById('status')?.textContent ?? '');
  result.stored = await page.evaluate(() => Object.keys(localStorage).some((k) => k.startsWith('playmap.phone.')));
  await page.close();

  const plain = await context.newPage();
  await plain.addInitScript(() => {
    // @ts-ignore
    window.__cameraAsks = 0;
    const md = navigator.mediaDevices;
    if (md) {
      const ask = md.getUserMedia.bind(md);
      md.getUserMedia = (...a) => {
        // @ts-ignore
        window.__cameraAsks++;
        return ask(...a);
      };
    }
  });
  let sockets = 0;
  plain.on('websocket', () => sockets++);
  await plain.goto(`http://phone.test:${port}/scan/#t=${ticket}`);
  await plain.waitForSelector('#insecure:not([hidden])', { timeout: 10000 });
  result.insecure = {
    secure: await plain.evaluate(() => window.isSecureContext),
    words: await plain.evaluate(() => document.getElementById('insecure')?.innerText ?? ''),
    link: await plain.evaluate(() => document.getElementById('https-link')?.getAttribute('href') ?? ''),
    joinShown: await plain.evaluate(() => !document.getElementById('join')?.hidden),
    // @ts-ignore
    cameraAsks: await plain.evaluate(() => window.__cameraAsks),
    sockets,
  };
} finally {
  await browser.close();
  writeFileSync(out, JSON.stringify(result));
}
