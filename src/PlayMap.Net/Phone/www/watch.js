// @ts-check
// What the page watches for, on its own (no camera, no socket): the tag places it was told, the pictures it took, and for each cue
// the rule that pairs a frame with what is on the glass — the cue's tag at every lit place, or (a locate frame, a pattern without
// places) a change from the last picture taken — until enough frames in a row show it and agree.

import { readFrame, Settle } from './tags.js';

const NO_LIT = [0, 0, 0, 0];

export class Watcher {
  constructor() {
    this.width = 0;
    this.height = 0;
    /** @type {{cells: number[][]}[]} */
    this.sites = [];
    this.lit = NO_LIT;
    this.minSites = 0;
    this.agreeTolerance = 3;
    this.stableFrames = 2;
    // The last picture taken and the one before it (a retake of the frame just taken changes from the one before).
    /** @type {Uint8Array | null} */
    this.lastGray = null;
    /** @type {Uint8Array | null} */
    this.beforeGray = null;
    this.lastKey = '';
  }

  /** A new picture size: everything measured on the old one is void. @param {number} width @param {number} height */
  resize(width, height) {
    this.width = width;
    this.height = height;
    this.sites = [];
    this.lit = NO_LIT;
    this.minSites = 0;
    this.lastGray = null;
    this.beforeGray = null;
    this.lastKey = '';
  }

  /**
   * A new projector's run (a prepare): the last one's tag places are void, and the picture the camera shows now (the probe, with the
   * exposure it will scan with) is what the first frame must change from.
   * @param {{agreeTolerance?: number, stableFrames?: number}} m @param {Uint8Array} probe
   */
  prepared(m, probe) {
    this.sites = [];
    this.lit = NO_LIT;
    this.minSites = 0;
    this.agreeTolerance = typeof m.agreeTolerance === 'number' ? m.agreeTolerance : 3;
    this.stableFrames = typeof m.stableFrames === 'number' ? Math.max(2, m.stableFrames) : 2;
    this.lastGray = probe.length === this.width * this.height ? probe.slice() : null;
    this.beforeGray = null;
    this.lastKey = '';
  }

  /** Where the projector's tag places land. @param {{sites?: {cells: number[][]}[], lit?: number[], minSites?: number}} m */
  placed(m) {
    this.sites = m.sites ?? [];
    this.lit = m.lit ?? NO_LIT;
    this.minSites = m.minSites ?? 0;
  }

  /**
   * A cue to watch frames for. Each frame offered answers null (keep watching), or {seen} once enough frames in a row show the cue
   * and agree — the picture is then taken (kept as the last picture) and its seen message's fields answered.
   * @param {{kind: string, projector: string, i: number, tag: number, retake?: number, minChange?: number}} m
   */
  cue(m) {
    const key = `${m.kind}:${m.projector}:${m.i}`;
    const fromGray = (m.retake ?? 0) > 0 && key === this.lastKey ? this.beforeGray : this.lastGray;
    const from = fromGray && fromGray.length === this.width * this.height ? Settle.gridOf(fromGray, this.width, this.height, this.lit) : null;
    const settle = new Settle(this.lit, this.agreeTolerance);
    const minChange = m.minChange ?? 8;
    /** @type {Record<string, number>} */
    const fails = {};
    let run = 0;
    let rolling = 0;
    const fail = (/** @type {string} */ why) => {
      fails[why] = (fails[why] ?? 0) + 1;
      return null;
    };
    return {
      /** The failure seen most often, for a stuck answer. */
      why: () => Object.entries(fails).sort((a, b) => b[1] - a[1])[0]?.[0] ?? 'no picture',
      /** @param {Uint8Array} gray @returns {{seen: {picture: Uint8Array, sites: number, margin: number, settleDiff: number, rolling: number}} | null} */
      offer: (gray) => {
        const st = settle.offer(gray, this.width, this.height);
        let read = { validSites: 0, margin: 0 };
        if (m.kind === 'pattern' && this.sites.length > 0) {
          const r = readFrame(gray, this.width, this.height, this.sites, this.minSites);
          if (r.outcome === 'disagree') {
            rolling++;
            run = 0;
            return fail('rolling shutter');
          }
          if (r.outcome !== 'read' || r.value !== m.tag) {
            run = 0;
            return fail(r.outcome === 'read' ? 'tag shows ' + r.value : 'tag not seen');
          }
          read = r;
        } else if (from && !Settle.changed(from, st.grid, minChange)) {
          run = 0;
          return fail('unchanged');
        }
        // This frame shows the cue; it counts towards the run only when it agrees with the one before.
        run = run > 0 && st.agrees ? run + 1 : 1;
        if (run > 1 && !st.agrees) fail('unsettled');
        if (run < this.stableFrames) return null;
        const picture = gray.slice();
        this.beforeGray = this.lastGray;
        this.lastGray = picture;
        this.lastKey = key;
        return { seen: { picture, sites: read.validSites, margin: read.margin, settleDiff: Math.round(st.diff * 100) / 100, rolling } };
      },
      /** A frame taken too early to show the cue. */
      early: () => fail('taken before the pattern was up'),
    };
  }
}
