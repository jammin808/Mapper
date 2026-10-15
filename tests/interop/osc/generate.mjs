// Writes OSC packets with the osc package for Node (osc.js, used under its MIT licence) and reads PlayMap's packets back with it.
//   node generate.mjs <dir>     writes <dir>/node-packets.json: each packet as osc.js wrote it (hex) and as it reads it (normalised)
//                               and, when <dir>/ours.json exists, <dir>/ours-read.json: PlayMap's packets as osc.js reads them.
// Run it from a folder where `npm install osc@2.4.5` has been done (NODE_PATH or a node_modules beside it).
import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";

const require = createRequire(path.join(process.cwd(), "noop.js"));
const osc = require("osc");
const dir = process.argv[2] ?? ".";

// One normal form for both sides: messages {address, args:[{type, value}]}, bundles {timeTag:"<hex 64>", packets:[…]};
// int64 as decimal text, blobs as hex, floats as numbers, arrays as {type:"[", value:[…]}.
function normal(packet) {
  if (packet.packets) {
    const raw = packet.timeTag.raw;
    const tag = (BigInt(raw[0]) << 32n | BigInt(raw[1])).toString(16).padStart(16, "0");
    return { timeTag: tag, packets: packet.packets.map(normal) };
  }
  return { address: packet.address, args: (packet.args ?? []).map(arg) };
}

function arg(a) {
  if (Array.isArray(a)) return { type: "[", value: a.map(arg) };
  switch (a.type) {
    case "h": return { type: "h", value: BigInt.asIntN(64, BigInt(a.value.high) << 32n | BigInt(a.value.low >>> 0)).toString() };
    case "b": return { type: "b", value: Buffer.from(a.value).toString("hex") };
    case "T": case "F": case "N": case "I": return { type: a.type };
    case "t": return { type: "t", value: (BigInt(a.value.raw[0]) << 32n | BigInt(a.value.raw[1])).toString() };
    default: return { type: a.type, value: a.value };
  }
}

const Long = require("long");
const packets = [
  { name: "int", packet: { address: "/playmap/blackout", args: [{ type: "i", value: 1 }] } },
  { name: "negative int", packet: { address: "/fader/1", args: [{ type: "i", value: -42 }] } },
  { name: "float", packet: { address: "/fader/2", args: [{ type: "f", value: 0.5 }] } },
  { name: "float32 rounding", packet: { address: "/fader/3", args: [{ type: "f", value: 0.1 }] } },
  { name: "double", packet: { address: "/playmap/video/seek", args: [{ type: "d", value: 83.5 }] } },
  { name: "string", packet: { address: "/playmap/line", args: [{ type: "s", value: "PLAYLIST GO show" }] } },
  { name: "string of 3 (padding)", packet: { address: "/abc", args: [{ type: "s", value: "abc" }] } },
  { name: "string of 4 (padding)", packet: { address: "/abcd", args: [{ type: "s", value: "abcd" }] } },
  { name: "utf-8", packet: { address: "/name", args: [{ type: "s", value: "Bühne" }] } },
  { name: "int64", packet: { address: "/big", args: [{ type: "h", value: Long.fromString("-1234567890123") }] } },
  { name: "blob", packet: { address: "/blob", args: [{ type: "b", value: new Uint8Array([1, 2, 3, 4, 5]) }] } },
  { name: "true false nil impulse", packet: { address: "/flags", args: [{ type: "T", value: true }, { type: "F", value: false }, { type: "N", value: null }, { type: "I", value: 1.0 }] } },
  { name: "no arguments", packet: { address: "/playmap/ping", args: [] } },
  { name: "mixed", packet: { address: "/mixed", args: [{ type: "i", value: 7 }, { type: "f", value: 0.25 }, { type: "s", value: "x" }, { type: "d", value: -1.5 }] } },
  { name: "array", packet: { address: "/array", args: [{ type: "i", value: 1 }, [{ type: "i", value: 2 }, { type: "s", value: "b" }], { type: "i", value: 3 }] } },
  { name: "bundle immediately", packet: { timeTag: { raw: [0, 1] }, packets: [
      { address: "/playmap/set/sf_a/opacity", args: [{ type: "f", value: 0.5 }] },
      { address: "/playmap/blackout", args: [{ type: "i", value: 0 }] }] } },
  { name: "bundle at a time", packet: { timeTag: { raw: [3969262642, 2147483648] }, packets: [
      { address: "/playmap/playlist/go", args: [{ type: "s", value: "show" }] }] } },
  { name: "nested bundle", packet: { timeTag: { raw: [0, 1] }, packets: [
      { timeTag: { raw: [0, 1] }, packets: [{ address: "/a", args: [{ type: "i", value: 1 }] }] },
      { address: "/b", args: [{ type: "s", value: "two" }] }] } },
];

const written = packets.map(p => {
  const bytes = osc.writePacket(p.packet, { metadata: true });
  const read = osc.readPacket(bytes, { metadata: true });
  return { name: p.name, hex: Buffer.from(bytes).toString("hex"), read: normal(read) };
});
fs.writeFileSync(path.join(dir, "node-packets.json"), JSON.stringify(written, null, 2) + "\n");

const oursPath = path.join(dir, "ours.json");
if (fs.existsSync(oursPath)) {
  const ours = JSON.parse(fs.readFileSync(oursPath, "utf8"));
  // A packet of its own buffer: osc.js reads blobs at offsets of the underlying ArrayBuffer, which a pooled Buffer does not start.
  const read = ours.map(p => ({ name: p.name, read: normal(osc.readPacket(new Uint8Array(Buffer.from(p.hex, "hex")), { metadata: true })) }));
  fs.writeFileSync(path.join(dir, "ours-read.json"), JSON.stringify(read, null, 2) + "\n");
}
