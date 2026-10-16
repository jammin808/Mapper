// Decodes the QR codes a .NET test wrote (PlayMap.Core's QrMatrix as a base64 bitset) with jsQR, an outside decoder, drawn as a
// picture would be: four modules of quiet zone, four pixels a module.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { load } from './load.mjs';

const jsQR = (await load('jsqr')).default;

const dir = process.argv[2];
const codes = JSON.parse(readFileSync(join(dir, 'qr.json'), 'utf8'));
const out = codes.map(({ size, bits }) => {
  const bytes = Buffer.from(bits, 'base64');
  const scale = 4;
  const side = (size + 8) * scale;
  const rgba = new Uint8ClampedArray(side * side * 4).fill(255);
  for (let r = 0; r < size; r++) {
    for (let c = 0; c < size; c++) {
      const i = r * size + c;
      if (!(bytes[i >> 3] & (0x80 >> (i & 7)))) continue;
      for (let y = 0; y < scale; y++) {
        for (let x = 0; x < scale; x++) {
          const p = (((r + 4) * scale + y) * side + (c + 4) * scale + x) * 4;
          rgba[p] = rgba[p + 1] = rgba[p + 2] = 0;
        }
      }
    }
  }
  const code = jsQR(rgba, side, side, { inversionAttempts: 'dontInvert' });
  return code ? code.data : null;
});
writeFileSync(join(dir, 'js.json'), JSON.stringify(out));
