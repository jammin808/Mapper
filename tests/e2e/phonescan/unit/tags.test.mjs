// The page's tag reader on its own: every value reads back, every flipped pair is caught by the parity.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cellLit, readMeans, tagValue } from '../../../../src/PlayMap.Net/Phone/www/tags.js';

const means = (value, flip = -1) => {
  const m = Array.from({ length: 16 }, (_, c) => (cellLit(value, c) ? 190 : 35));
  if (flip >= 0) [m[2 * flip + 1], m[2 * flip + 2]] = [m[2 * flip + 2], m[2 * flip + 1]];
  return m;
};

test('every value reads back', () => {
  assert.equal(tagValue(0), 1);
  assert.equal(tagValue(63), 1);
  for (let v = 1; v <= 63; v++) {
    const r = readMeans(means(v));
    assert.equal(r.state, 'read');
    assert.equal(r.value, v);
  }
});

test('every single flipped pair is caught', () => {
  for (let v = 1; v <= 63; v++) for (let k = 0; k < 7; k++) assert.equal(readMeans(means(v, k)).state, 'unsure', `${v} ${k}`);
});
