// The page's PMF1 frame on its own: magic, header length, header fields, CRC-32 of the pixels.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { crc32, pmf1, MAGIC } from '../../../../src/PlayMap.Net/Phone/www/protocol.js';

test('crc32 is the standard check value', () => {
  assert.equal(crc32(new TextEncoder().encode('123456789')), 0xcbf43926);
});

test('a frame names its fields and carries its pixels', async () => {
  const pixels = new Uint8Array(64 * 48).map((_, i) => (i * 7) & 255);
  const frame = await pmf1({ kind: 'pattern', seq: 3, cue: 9, capture: 12.5, w: 64, h: 48, roi: [0, 0, 64, 48] }, pixels, false);
  const view = new DataView(frame.buffer);
  assert.equal(view.getUint32(0, true), MAGIC);
  const n = view.getUint32(4, true);
  const header = JSON.parse(new TextDecoder().decode(frame.subarray(8, 8 + n)));
  assert.deepEqual(header, { kind: 'pattern', seq: 3, cue: 9, capture: 12.5, w: 64, h: 48, roi: [0, 0, 64, 48], format: 'gray8', codec: 'none', frames: 1, crc32: crc32(pixels) });
  assert.deepEqual(frame.subarray(8 + n), pixels);
});
