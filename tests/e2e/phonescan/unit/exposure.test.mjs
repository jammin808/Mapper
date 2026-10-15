// The lock profile's arithmetic and the page's rule for a closed socket, on their own.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { planExposure, CAP, TARGET } from '../../../../src/PlayMap.Net/Phone/www/exposure.js';
import { afterClose } from '../../../../src/PlayMap.Net/Phone/www/protocol.js';

const frames = (/** @type {number} */ t1, /** @type {number} */ hz) => (t1 * hz) / 10000;

test('an exposure above 8 ms is a whole number of display frames, never past the 30 fps cap', () => {
  for (const hz of [24, 30, 50, 60, 120]) {
    for (const t0 of [50, 100, 200, 300, 500]) {
      for (const level of [60, 120, 200, 255]) {
        const { t1 } = planExposure({ t0, s0: 100, level, displayHz: hz });
        if (t1 > 80) {
          const n = frames(t1, hz);
          assert.ok(Math.abs(n - Math.round(n)) < 1e-6, `${hz} Hz, t0 ${t0}, L ${level}: ${n} frames`);
          assert.ok(t1 <= CAP + 1e-6 || Math.round(n) === 1, `${hz} Hz: ${t1} past the cap with ${n} frames`);
        }
      }
    }
  }
  // 50 Hz, wanting 30 ms: a whole frame under the cap (20 ms), not 33 ms of 1.65 frames; 60 Hz gets its two frames exactly.
  assert.equal(planExposure({ t0: 300, s0: 100, level: TARGET, displayHz: 50 }).t1, 200);
  assert.ok(Math.abs(planExposure({ t0: 320, s0: 100, level: TARGET, displayHz: 60 }).t1 - 10000 / 30) < 1e-6);
  // ISO makes up what the time gave away.
  const p = planExposure({ t0: 300, s0: 100, level: TARGET, displayHz: 50 });
  assert.ok(Math.abs(p.s1 * p.t1 - 100 * 300) < 1e-6);
});

test('a closed socket is tried again only when trying again can work', () => {
  assert.deepEqual(afterClose(1008, 'ERR this phone\'s key is not known here — ask for a new invite', true, 0), { again: false, forgetKey: true, delayMs: 0 });
  assert.deepEqual(afterClose(1008, 'was forgotten: its key no longer opens anything', true, 0), { again: false, forgetKey: true, delayMs: 0 });
  assert.equal(afterClose(1008, 'was replaced: the phone joined again', true, 0).again, false);
  assert.equal(afterClose(1008, 'phones are switched off on this node', true, 0).again, false);
  assert.equal(afterClose(1008, 'ERR ticket expired — ask for a new invite', false, 0).again, false);
  assert.equal(afterClose(1006, '', false, 0).again, false, 'no key: nothing to join again with');
  assert.deepEqual(afterClose(1006, '', true, 0), { again: true, forgetKey: false, delayMs: 1000 });
  assert.equal(afterClose(1006, '', true, 3).delayMs, 8000);
  assert.equal(afterClose(1006, '', true, 9).delayMs, 30000);
  assert.equal(afterClose(1008, '4 phones are connected already', true, 0).again, true);
  assert.equal(afterClose(1008, 'too many wrong invites from this address — wait a minute', true, 0).delayMs, 60000);
});
