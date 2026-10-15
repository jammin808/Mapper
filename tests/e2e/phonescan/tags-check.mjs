// Reads every case a .NET test wrote (raw gray8 pictures and the sites the node placed) with the page's own tag reader, and
// writes what it read beside them, for the test to compare with PlayMap.Core's reader.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { readFrame } from '../../../src/PlayMap.Net/Phone/www/tags.js';

const dir = process.argv[2];
const cases = JSON.parse(readFileSync(join(dir, 'cases.json'), 'utf8'));
const out = cases.map((c) => {
  const gray = new Uint8Array(readFileSync(join(dir, c.file)));
  return readFrame(gray, c.width, c.height, c.sites, c.minSites);
});
writeFileSync(join(dir, 'js.json'), JSON.stringify(out));
