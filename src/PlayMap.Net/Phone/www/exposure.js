// @ts-check
// The two exposure profiles. Lock (a camera that offers manual exposure): let auto-exposure settle on the probe, measure the
// brightest lit level once, set exposure time and ISO explicitly for a target of 215, lock focus and white balance, verify. Steady
// (a camera without those controls, iPhone Safari among them): the camera keeps its own exposure, which the node keeps still by
// showing no full white or black, and white balance is locked where offered.

export const TARGET = 215;

/**
 * Whether a track's capabilities allow the lock profile.
 * @param {any} caps MediaTrackCapabilities
 */
export function canLock(caps) {
  return !!caps && Array.isArray(caps.exposureMode) && caps.exposureMode.includes('manual') && !!caps.exposureTime;
}

/**
 * The level 99.5 % of the brightest 45 % of lit pixels stay under — the lit stripes' top.
 * @param {Uint8Array | Uint8ClampedArray} gray
 */
export function litTop(gray) {
  const hist = new Uint32Array(256);
  for (let k = 0; k < gray.length; k++) hist[gray[k]]++;
  const lit = Math.max(1, Math.floor(gray.length * 0.45));
  let seen = 0;
  let top = 255;
  // The brightest 45 %: from the top of the histogram down.
  const brightest = [];
  for (let v = 255; v >= 0 && seen < lit; v--) {
    const take = Math.min(hist[v], lit - seen);
    if (take > 0) brightest.push([v, take]);
    seen += take;
  }
  const at = Math.floor(seen * 0.005);
  let acc = 0;
  for (const [v, n] of brightest) {
    acc += n;
    if (acc > at) {
      top = v;
      break;
    }
  }
  return top;
}

/** The longest exposure a 30 fps picture allows, in the track's units (100 µs). */
export const CAP = 10000 / 30;

/**
 * The exposure for the lock profile from the settled one. A phone's picture is gamma-encoded (a level is light to the power 1/γ),
 * so the light needed is (215/L)^γ times what gave level L: t₁ = t₀·(215/L)^γ (0.6·t₀ when clipped). Above 8 ms it is a whole number
 * of the display's frames, so every picture holds the same light from a projector that flickers frame by frame: rounded up, or,
 * where that passes the 30 fps cap, down to the most whole frames under it (one frame at least). ISO makes up the rest within the
 * camera's range. Times in the track's units (100 µs).
 * @param {{t0: number, s0: number, level: number, displayHz: number, gamma?: number, timeRange?: {min: number, max: number, step?: number}, isoRange?: {min: number, max: number}}} p
 */
export function planExposure(p) {
  const clipped = p.level >= 250;
  const want = clipped ? 0.6 * p.t0 : p.t0 * Math.pow(TARGET / Math.max(1, p.level), p.gamma ?? 2.2);
  let t1 = want;
  const frame = p.displayHz > 0 ? 10000 / p.displayHz : 0; // one display frame in 100 µs units
  if (frame > 0 && t1 > 80) {
    t1 = Math.ceil(t1 / frame - 1e-9) * frame;
    if (t1 > CAP + 1e-9) t1 = Math.max(1, Math.floor(CAP / frame + 1e-9)) * frame;
  } else {
    t1 = Math.min(t1, CAP);
  }
  if (p.timeRange) t1 = Math.min(p.timeRange.max, Math.max(p.timeRange.min, t1));
  let s1 = (p.s0 * want) / Math.max(1e-6, t1);
  if (p.isoRange) s1 = Math.min(p.isoRange.max, Math.max(p.isoRange.min, s1));
  return { t1, s1, clipped };
}

/**
 * The camera's curve from what one correction did: the light went by (t₁·s₁)/(t₀·s₀) and the level by L₁/L₀, so γ is the ratio of
 * their logarithms, kept within 1–3; the prior where the step tells nothing (a clipped level, a level that hardly moved).
 * @param {{t: number, s: number, level: number}} a @param {{t: number, s: number, level: number}} b @param {number} prior
 */
export function fitGamma(a, b, prior) {
  const levels = Math.log(b.level / a.level);
  if (a.level >= 250 || b.level >= 250 || a.level < 1 || b.level < 1 || Math.abs(levels) < 0.05) return prior;
  const light = Math.log((b.t * b.s) / (a.t * a.s));
  const gamma = light / levels;
  return Number.isFinite(gamma) && gamma > 0 ? Math.min(3, Math.max(1, gamma)) : prior;
}

/**
 * What a correction that missed leaves for the next: the exposure to correct from and the curve to plan with. One that clipped (its
 * level unknown above 250) after an unclipped one overshot: the next plans again from the unclipped one, on a gentler curve (×0.75);
 * otherwise from the correction itself, on the curve the two measurements show.
 * @param {{t: number, s: number, level: number}} before @param {{t: number, s: number, level: number}} after @param {number} gamma
 */
export function afterMiss(before, after, gamma) {
  if (after.level >= 250 && before.level < 250) return { base: before, gamma: Math.max(1, gamma * 0.75) };
  return { base: after, gamma: fitGamma(before, after, gamma) };
}

/**
 * Applies constraints one call at a time, dropping a member the track refuses by name and trying again.
 * @param {MediaStreamTrack} track @param {Record<string, any>} wanted
 */
export async function applyDropping(track, wanted) {
  const left = { ...wanted };
  for (let attempt = 0; attempt < 6 && Object.keys(left).length > 0; attempt++) {
    try {
      await track.applyConstraints(/** @type {any} */ ({ advanced: [left] }));
      return left;
    } catch (e) {
      const name = /** @type {any} */ (e)?.constraint;
      if (!name || !(name in left)) return {};
      delete left[name];
    }
  }
  return left;
}
