// @ts-check
// The pattern tag as the phone reads it: the same rule, the same integer arithmetic and the same sample positions as
// PlayMap.Core.Phone.FrameTag, so the phone and the node read the same value from the same pixels.

/** A site is lit enough when its two reference cells differ by this many levels. */
export const MIN_CONTRAST = 20;
/** A differential pair is certain at this many levels and a fifth of the contrast. */
export const MIN_PAIR_DELTA = 6;

/** @param {number} patternIndex */
export function tagValue(patternIndex) {
  return (patternIndex % 63) + 1;
}

/** The seven code bits: b0…b5 the value's bits, b6 their even parity. @param {number} value */
export function tagCode(value) {
  let ones = 0;
  for (let v = value; v; v >>= 1) ones += v & 1;
  return value | ((ones & 1) << 6);
}

/** Whether cell `cell` (0–15, row-major) of a site showing `value` is lit. @param {number} value @param {number} cell */
export function cellLit(value, cell) {
  if (cell === 0) return true;
  if (cell === 15) return false;
  const k = (cell - 1) >> 1;
  const bit = (tagCode(value) >> k) & 1;
  return (cell - 1) % 2 === 0 ? bit === 1 : bit === 0;
}

/**
 * The mean of the 3 × 3 pixels round a cell centre, clamped to the raster, rounded half up.
 * @param {Uint8Array | Uint8ClampedArray} gray @param {number} width @param {number} height @param {number} x @param {number} y
 */
export function sample(gray, width, height, x, y) {
  const cx = Math.floor(x);
  const cy = Math.floor(y);
  let sum = 0;
  for (let dy = -1; dy <= 1; dy++) {
    const yy = Math.min(height - 1, Math.max(0, cy + dy));
    for (let dx = -1; dx <= 1; dx++) sum += gray[yy * width + Math.min(width - 1, Math.max(0, cx + dx))];
  }
  return Math.floor((sum + 4) / 9);
}

/**
 * One site from its 16 cell means.
 * @param {number[]} means
 * @returns {{state: 'dark'|'unsure'|'read', value: number, contrast: number, margin: number}}
 */
export function readMeans(means) {
  const contrast = means[0] - means[15];
  if (contrast < MIN_CONTRAST) return { state: 'dark', value: 0, contrast, margin: 0 };
  let code = 0;
  let margin = Number.MAX_SAFE_INTEGER;
  let certain = true;
  for (let k = 0; k < 7; k++) {
    const d = means[2 * k + 1] - means[2 * k + 2];
    const a = Math.abs(d);
    margin = Math.min(margin, a);
    if (a < MIN_PAIR_DELTA || a * 5 < contrast) certain = false;
    if (d > 0) code |= 1 << k;
  }
  let ones = 0;
  for (let v = code; v; v >>= 1) ones += v & 1;
  const value = code & 63;
  return certain && (ones & 1) === 0 && value !== 0
    ? { state: 'read', value, contrast, margin }
    : { state: 'unsure', value: 0, contrast, margin };
}

/**
 * One placed site in a picture.
 * @param {Uint8Array | Uint8ClampedArray} gray @param {number} width @param {number} height
 * @param {{cells: number[][]}} site cell centres [x, y] in camera pixels, row-major
 */
export function readSite(gray, width, height, site) {
  const means = site.cells.map(([x, y]) => sample(gray, width, height, x, y));
  return readMeans(means);
}

/**
 * A picture's tag: read when every lit site reads one value and at least minSites are lit; disagree when two lit sites read
 * different values (a rolling-shutter mix); unsure when a lit site does not read; none when too few are lit.
 * @param {Uint8Array | Uint8ClampedArray} gray @param {number} width @param {number} height
 * @param {{cells: number[][]}[]} sites @param {number} minSites
 * @returns {{outcome: 'none'|'unsure'|'disagree'|'read', value: number, validSites: number, margin: number}}
 */
export function readFrame(gray, width, height, sites, minSites) {
  let valid = 0;
  let value = 0;
  let margin = Number.MAX_SAFE_INTEGER;
  let unsure = false;
  let disagree = false;
  for (const site of sites) {
    const r = readSite(gray, width, height, site);
    if (r.state === 'dark') continue;
    valid++;
    if (r.state === 'unsure') {
      unsure = true;
      continue;
    }
    margin = Math.min(margin, r.margin);
    if (value === 0) value = r.value;
    else if (value !== r.value) disagree = true;
  }
  if (disagree) return { outcome: 'disagree', value: 0, validSites: valid, margin: 0 };
  if (unsure) return { outcome: 'unsure', value: 0, validSites: valid, margin: 0 };
  if (valid < Math.max(1, minSites) || value === 0) return { outcome: 'none', value: 0, validSites: valid, margin: 0 };
  return { outcome: 'read', value, validSites: valid, margin };
}

/**
 * Settles a stream of pictures: two consecutive frames agree when the mean absolute difference over a sparse grid (48 × 27
 * samples inside the lit rectangle) is under the tolerance.
 */
export class Settle {
  /** @param {number[]} lit [x, y, w, h] @param {number} tolerance */
  constructor(lit, tolerance) {
    this.lit = lit;
    this.tolerance = tolerance;
    /** @type {Uint8Array | null} */
    this.previous = null;
  }

  /** The grid of a picture. @param {Uint8Array | Uint8ClampedArray} gray @param {number} width @param {number} height */
  grid(gray, width, height) {
    const [x0, y0, w, h] = this.lit[2] > 0 ? this.lit : [0, 0, width, height];
    const g = new Uint8Array(48 * 27);
    for (let j = 0; j < 27; j++) {
      const y = Math.min(height - 1, Math.floor(y0 + ((j + 0.5) * h) / 27));
      for (let i = 0; i < 48; i++) {
        const x = Math.min(width - 1, Math.floor(x0 + ((i + 0.5) * w) / 48));
        g[j * 48 + i] = gray[y * width + x];
      }
    }
    return g;
  }

  /** Mean absolute difference between two grids. @param {Uint8Array} a @param {Uint8Array} b */
  static diff(a, b) {
    let s = 0;
    for (let k = 0; k < a.length; k++) s += Math.abs(a[k] - b[k]);
    return s / a.length;
  }

  /**
   * Offers the next picture; answers whether it agrees with the one before, and the difference.
   * @param {Uint8Array | Uint8ClampedArray} gray @param {number} width @param {number} height
   */
  offer(gray, width, height) {
    const g = this.grid(gray, width, height);
    const before = this.previous;
    this.previous = g;
    if (!before) return { agrees: false, diff: Infinity, grid: g };
    const d = Settle.diff(before, g);
    return { agrees: d < this.tolerance, diff: d, grid: g };
  }

  reset() {
    this.previous = null;
  }
}
