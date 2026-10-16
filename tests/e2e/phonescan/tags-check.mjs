// Reads every case a .NET test wrote (raw gray8 pictures and the sites message the node sends) with the page's own tag reader and
// settle grid, and writes what it read beside them, for the test to compare with PlayMap.Core's.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { readFrame, Settle } from '../../../src/PlayMap.Net/Phone/www/tags.js';

const dir = process.argv[2];
const cases = JSON.parse(readFileSync(join(dir, 'cases.json'), 'utf8'));
const out = cases.map((c) => {
  const gray = new Uint8Array(readFileSync(join(dir, c.file)));
  const read = readFrame(gray, c.width, c.height, c.sites, c.minSites);
  const b64 = (/** @type {Uint8Array} */ g) => Buffer.from(g).toString('base64');
  return {
    ...read,
    grid: b64(Settle.gridOf(gray, c.width, c.height, [0, 0, 0, 0])),
    litGrid: b64(Settle.gridOf(gray, c.width, c.height, c.lit)),
  };
});
writeFileSync(join(dir, 'js.json'), JSON.stringify(out));
