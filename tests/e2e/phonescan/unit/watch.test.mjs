// The page's pairing rule on synthetic pictures: a new projector's run watches the whole picture again (its predecessor's place
// holds its last frame and never changes), a frame shown again for a retake changes from the picture before the one taken, a frame
// paired by change waits out a stale picture, and a tagged pattern is matched by its tag.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Watcher } from '../../../../src/PlayMap.Net/Phone/www/watch.js';

const W = 160;
const H = 90;

/** A picture: the left half at one level, the right half at another. @param {number} left @param {number} right */
function picture(left, right) {
  const g = new Uint8Array(W * H);
  for (let k = 0; k < g.length; k++) g[k] = k % W < W / 2 ? left : right;
  return g;
}

/** Offers the same picture until the cue answers (or gives up after a few frames). */
function offer(/** @type {any} */ cue, /** @type {Uint8Array} */ gray, frames = 4) {
  for (let n = 0; n < frames; n++) {
    const v = cue.offer(gray);
    if (v) return v;
  }
  return null;
}

test('a new projector run watches the whole picture again, whatever place the last one had', () => {
  const w = new Watcher();
  w.resize(W, H);
  // Projector A lights the left half; its run ends with its last frame held there.
  w.prepared({}, picture(30, 30));
  assert.ok(offer(w.cue({ kind: 'locate', projector: 'A', i: 0, tag: 0, minChange: 8 }), picture(200, 30)), 'A\'s first frame is seen');
  w.placed({ sites: [], lit: [0, 0, W / 2, H], minSites: 0 });
  assert.ok(offer(w.cue({ kind: 'pattern', projector: 'A', i: 5, tag: 0, minChange: 8 }), picture(120, 30)), 'A\'s pattern is seen in its place');
  // Projector B lands in the right half; A's half holds still. Without the reset this waits forever inside A's place.
  w.prepared({}, picture(120, 30));
  const b = w.cue({ kind: 'locate', projector: 'B', i: 0, tag: 0, minChange: 8 });
  assert.equal(offer(b, picture(120, 30)), null, 'nothing changed yet');
  assert.ok(offer(b, picture(120, 200)), 'B\'s first frame is seen where it lands');
});

test('a stale picture is waited out, and a retake of the frame just taken changes from the one before it', () => {
  const w = new Watcher();
  w.resize(W, H);
  w.prepared({}, picture(30, 30));
  assert.ok(offer(w.cue({ kind: 'locate', projector: 'A', i: 0, tag: 0 }), picture(200, 200)));
  const next = w.cue({ kind: 'locate', projector: 'A', i: 1, tag: 0 });
  assert.equal(offer(next, picture(200, 200)), null, 'the frame before is still on the glass');
  assert.equal(next.why(), 'unchanged');
  assert.ok(offer(next, picture(90, 90)));
  // Its picture never reached the node: the same frame again, compared with the picture before it, not with itself.
  assert.ok(offer(w.cue({ kind: 'locate', projector: 'A', i: 1, tag: 0, retake: 1 }), picture(90, 90)));
  // A cue for another frame is not a retake of this one.
  assert.equal(offer(w.cue({ kind: 'locate', projector: 'A', i: 2, tag: 0, retake: 1 }), picture(90, 90)), null);
});

test('two agreeing frames in a row are needed, more when the node asks', () => {
  const w = new Watcher();
  w.resize(W, H);
  w.prepared({ stableFrames: 3 }, picture(30, 30));
  const cue = w.cue({ kind: 'locate', projector: 'A', i: 0, tag: 0 });
  assert.equal(cue.offer(picture(200, 200)), null);
  assert.equal(cue.offer(picture(200, 200)), null);
  assert.ok(cue.offer(picture(200, 200)));
});

test('every retake of a frame changes from the picture before that frame, however many times it is lost', () => {
  const w = new Watcher();
  w.resize(W, H);
  w.prepared({}, picture(30, 30));
  assert.ok(offer(w.cue({ kind: 'locate', projector: 'A', i: 0, tag: 0 }), picture(200, 200)));
  assert.ok(offer(w.cue({ kind: 'locate', projector: 'A', i: 1, tag: 0 }), picture(90, 90)));
  // Frame 1's picture is lost twice: each retake is still seen, against frame 0's picture.
  assert.ok(offer(w.cue({ kind: 'locate', projector: 'A', i: 1, tag: 0, retake: 1 }), picture(90, 90)));
  assert.ok(offer(w.cue({ kind: 'locate', projector: 'A', i: 1, tag: 0, retake: 2 }), picture(90, 90)));
});
