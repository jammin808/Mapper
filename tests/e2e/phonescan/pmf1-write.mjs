// Writes PMF1 frames with the page's own encoder (one raw-deflate, one uncompressed) for a .NET test to read with PlayMap.Core.
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pmf1 } from '../../../src/PlayMap.Net/Phone/www/protocol.js';

const dir = process.argv[2];
const w = 320;
const h = 240;
const pixels = new Uint8Array(w * h);
let x = 12345;
for (let i = 0; i < pixels.length; i++) {
  x = (x * 1103515245 + 12345) >>> 0;
  pixels[i] = (i % w) < 160 ? (x >>> 24) : (i >> 3) & 255;
}
writeFileSync(join(dir, 'pixels.gray'), pixels);
writeFileSync(join(dir, 'deflate.pmf1'), await pmf1({ kind: 'pattern', seq: 7, cue: 3, capture: 1234.5, w, h, roi: [0, 0, w, h] }, pixels, true));
writeFileSync(join(dir, 'none.pmf1'), await pmf1({ kind: 'locate', seq: 8, cue: 4, capture: 1300, w, h, roi: [0, 0, w, h] }, pixels, false));
