// The lock profile's arithmetic and the page's rule for a closed socket, on their own.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { planExposure, fitGamma, afterMiss, CAP, TARGET } from '../../../../src/PlayMap.Net/Phone/www/exposure.js';
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

test('a camera with a curve lands within 190–240 in two corrections, from any settled level', () => {
  for (const curve of [1.6, 1.8, 2.2, 2.6]) {
    for (const start of [60, 80, 100, 120, 140, 180, 230, 300]) {
      // A camera whose level is light to the power 1/curve; at t 100, ISO 100 it reads `start` (above 255: clipped).
      const k = Math.pow(start / 255, curve) / 1e4;
      const level = (/** @type {number} */ t, /** @type {number} */ s) => Math.min(255, Math.round(255 * Math.pow(t * s * k, 1 / curve)));
      let base = { t: 100, s: 100, level: level(100, 100) };
      let gamma = 2.2;
      const seen = [];
      for (let round = 0; round < 2; round++) {
        const p = planExposure({ t0: base.t, s0: base.s, level: base.level, gamma, displayHz: 60 });
        const next = { t: p.t1, s: p.s1, level: level(p.t1, p.s1) };
        seen.push(next.level);
        if (next.level >= 190 && next.level <= 240) break;
        ({ base, gamma } = afterMiss(base, next, gamma));
      }
      const last = seen[seen.length - 1];
      assert.ok(last >= 190 && last <= 240, `curve ${curve}, settled at ${start}: ${seen.join(' → ')}`);
    }
  }
});

test('the curve is read from what a correction did, and kept where it says nothing', () => {
  // Light ×4 moved a gamma-2 camera's level ×2.
  assert.ok(Math.abs(fitGamma({ t: 100, s: 100, level: 50 }, { t: 400, s: 100, level: 100 }, 2.2) - 2) < 1e-9);
  assert.equal(fitGamma({ t: 100, s: 100, level: 50 }, { t: 400, s: 100, level: 255 }, 2.2), 2.2, 'clipped');
  assert.equal(fitGamma({ t: 100, s: 100, level: 100 }, { t: 101, s: 100, level: 101 }, 2.2), 2.2, 'hardly moved');
});

test('a camera whose ISO is fixed or at its floor lands wherever a flicker-safe exposure can land it', () => {
  const modes = { free: { hasIso: true }, floor: { hasIso: true, isoRange: { min: 50, max: 800 } }, none: { hasIso: false } };
  let checked = 0;
  for (const hz of [50, 60]) {
    for (const curve of [1.8, 2.2, 2.6]) {
      for (const [mode, opt] of Object.entries(modes)) {
        for (const t0 of [20, 50, 100, 150, 200, 330]) {
          for (let start = 60; start <= 248; start += 8) {
            const s0 = mode === 'floor' ? 50 : 100;
            const k = Math.pow(start / 255, curve) / (t0 * s0);
            const level = (/** @type {number} */ t, /** @type {number} */ s) => Math.min(255, Math.round(255 * Math.pow(t * s * k, 1 / curve)));
            const isoFor = (/** @type {number} */ t, /** @type {number} */ light) =>
              mode === 'none' ? s0 : mode === 'floor' ? Math.min(800, Math.max(50, light / t)) : light / t;
            // Could any exposure the rule allows (any time to 8 ms, whole frames to the cap) with the ISO the camera takes land it?
            const frame = 10000 / hz;
            const times = [...Array.from({ length: 80 }, (_, i) => i + 1), ...Array.from({ length: Math.max(1, Math.floor(CAP / frame + 1e-9)) }, (_, i) => (i + 1) * frame)];
            const light215 = Math.pow(TARGET / 255, curve) / k;
            if (!times.some((t) => { const l = level(t, isoFor(t, light215)); return l >= 190 && l <= 240; })) continue;
            let base = { t: t0, s: s0, level: start };
            let gamma = 2.2;
            /** @type {number | undefined} */
            let below;
            const seen = [];
            for (let round = 0; round < 2; round++) {
              const p = planExposure({ t0: base.t, s0: base.s, level: base.level, gamma, below, displayHz: hz, ...opt });
              const s1 = mode === 'none' ? s0 : p.s1;
              const next = { t: p.t1, s: s1, level: level(p.t1, s1) };
              seen.push(next.level);
              if (next.level >= 190 && next.level <= 240) break;
              ({ base, gamma, below } = afterMiss(base, next, gamma));
            }
            const last = seen[seen.length - 1];
            assert.ok(last >= 190 && last <= 240, `${hz} Hz, curve ${curve}, ISO ${mode}, t0 ${t0}, settled ${start}: ${seen.join(' → ')}`);
            checked++;
          }
        }
      }
    }
  }
  assert.ok(checked > 1500, `${checked} settings checked`);
  // The review's cases: no ISO, 15 ms settled at 200 (60 Hz); ISO at its floor, 12 ms settled at 170.
  assert.equal(planExposure({ t0: 150, s0: 100, level: 200, displayHz: 60, hasIso: false }).t1, 10000 / 60);
  assert.equal(planExposure({ t0: 120, s0: 50, level: 170, displayHz: 60, isoRange: { min: 50, max: 800 } }).t1, 10000 / 60);
});
