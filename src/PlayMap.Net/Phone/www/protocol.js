// @ts-check
// playmap.camera.v1 on the phone's side: the PMF1 binary frame (magic "PMF1", little-endian header length, JSON header, the ROI's
// rows compressed raw-deflate) and its CRC-32 — the same frame PlayMap.Core.Phone.Pmf1 reads.

export const SUBPROTOCOL = 'playmap.camera.v1';
export const MAGIC = 0x31464d50;

const table = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

/** CRC-32 (IEEE, reflected). @param {Uint8Array} data */
export function crc32(data) {
  let c = 0xffffffff;
  for (let i = 0; i < data.length; i++) c = table[(c ^ data[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

/**
 * Raw deflate through the platform's CompressionStream; null where the platform has none (the frame goes uncompressed).
 * @param {Uint8Array} data
 * @returns {Promise<Uint8Array | null>}
 */
export async function deflateRaw(data) {
  if (typeof CompressionStream === 'undefined') return null;
  try {
    const stream = new Blob([data]).stream().pipeThrough(new CompressionStream('deflate-raw'));
    return new Uint8Array(await new Response(stream).arrayBuffer());
  } catch {
    return null;
  }
}

/**
 * A PMF1 frame of gray8 pixels (the ROI's rows, row-major).
 * @param {{kind: string, seq: number, cue?: number, capture: number, w: number, h: number, roi: number[], frames?: number}} header
 * @param {Uint8Array} pixels
 * @param {boolean} [compress]
 */
export async function pmf1(header, pixels, compress = true) {
  const [, , rw, rh] = header.roi;
  if (pixels.length !== rw * rh) throw new Error('the pixels are not the ROI\'s size');
  const deflated = compress ? await deflateRaw(pixels) : null;
  const json = JSON.stringify({
    kind: header.kind,
    seq: header.seq,
    ...(header.cue !== undefined ? { cue: header.cue } : {}),
    capture: header.capture,
    w: header.w,
    h: header.h,
    roi: header.roi,
    format: 'gray8',
    codec: deflated ? 'deflate-raw' : 'none',
    frames: header.frames ?? 1,
    crc32: crc32(pixels),
  });
  const head = new TextEncoder().encode(json);
  const payload = deflated ?? pixels;
  const out = new Uint8Array(8 + head.length + payload.length);
  const view = new DataView(out.buffer);
  view.setUint32(0, MAGIC, true);
  view.setUint32(4, head.length, true);
  out.set(head, 8);
  out.set(payload, 8 + head.length);
  return out;
}

/**
 * What a closed socket means for the page: whether to try again (and after how long), and whether its stored key is worthless now.
 * A refusal that cannot change by trying again (a key or invite the node does not know, a phone forgotten, replaced by another tab,
 * phones switched off) stops the page; a forgotten phone or an unknown key also drops the key. Anything else (the link dropped, the
 * node restarting, too many phones for now, a quiet spell) tries again, waiting longer each time up to half a minute, a minute after
 * too many wrong tries from this address.
 * @param {number} code @param {string} reason @param {boolean} hasKey @param {number} attempt how many tries in a row have failed
 * @returns {{again: boolean, forgetKey: boolean, delayMs: number}}
 */
export function afterClose(code, reason, hasKey, attempt) {
  const forget = /key is not known|was forgotten/.test(reason);
  const final = forget || /^ERR |was replaced|switched off/.test(reason);
  if (final || !hasKey) return { again: false, forgetKey: forget, delayMs: 0 };
  const delayMs = /too many wrong/.test(reason) ? 60000 : Math.min(30000, 1000 * 2 ** Math.min(5, attempt));
  return { again: true, forgetKey: false, delayMs };
}
