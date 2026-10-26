// The scripted peer of Show Bus 1.3 (docs/SHOWBUS.md, 13.20): one dependency-free Node 22 file, byte-identical in every tree that
// copies the contract. It plays either end of a PeerLink - the connector or the acceptor - on `line` (TCP, TLS told from plain by the
// first byte, or `playmap.lines.v1` over a WebSocket), on `ws` (a node link on a Stagewatch-like `/ws`) and on side calls, so each
// tree's own test runs 13.20's scenarios against its own links over real sockets. Each end is honest - it keeps section 13's rules as
// a connector, or an acceptor offering its features, does - unless a command names an `attack`, and then it does what an attacker on
// the show network would, and reports what it did ({"ev":"attack"}) and what came back.
//
// It takes no arguments. It is driven over stdin and stdout, one JSON object a line. Keys, codes and certificates come from files a
// command names, each readable by its owner alone (mode 0600). On Windows no mode is read: the file's own ACL, a file in the user's
// profile, is what keeps it there, an exemption 13.20 names.
// It has no clock of its own: the link under test keeps its injected clock, and the times the peer stamps - a pong's, a receipt's
// age - are the ones the host gives it (`clock`). It never writes a key, a code, K or S of its own - from its files, or made from
// them - to stdout; what crossed a wire, its own or the other end's, it reports as it was, so a host reads what went in clear.
//
// Commands, {"do": ...} - each answered by one event that carries the command's "tag" as "re", or by {"ev":"error","re",...,"words"}:
//   listen  {"end","carrier":"line"|"ws","transport":"tcp"|"ws","tls":"sniff"|"plain"|"only","cert","certKey","port","host",
//            "node":{"t","id"},"keys":[{"file","kind":"key"|"code","bound":"t:id"}],"level":1|0,"can":[],"stuckMs","expireMs",
//            "inst","scope","greet":["state","nonce"],"clock","attack"}   -> {"ev":"listening","end","host","port","fingerprint"}
//           An acceptor. `line` with transport `ws` is the line grammar over `playmap.lines.v1`; `ws` is a node link door with its side
//           calls. `tls` sniff (the default) tells TLS by its first byte (13.8.5), and closes a connection that sends none within
//           peerlink.handshake.ms; `only` is TLS alone; `plain` offers none. `host` is the one address it binds (127.0.0.1 unless
//           named; never every interface, 13.3.5). `level` 0 is an acceptor that does not speak LINK (13.6.8): on `line` it answers
//           LINK `unknown command`, and on `ws` its first message is a state frame. With no `keys` it is an open node (13.6.7): on
//           `line` it greets with STATE and answers LINK `open`, and on `ws` its verdict is `open`. A key is bound to the connector
//           `bound` names, or to the first one that proves it, and refused to any other (`binding`, 13.5.2); a code is not bound.
//           `can` lists the features its verdict names and it keeps (13.1, 13.6.6): `final` (an accepted command's final receipt,
//           sent on `set final`), `receipts` (13.12.6's record per verified connector: a repeated id answered from it with
//           "repeat":true and not run, accepted while it still runs, and RECEIPTS asks), `stuck` (`stuckMs`,
//           show.stuck.min.ms-show.stuck.max.ms; a pong says STUCK on `line` while `set stuck` holds), `expire` (`expireMs`, at most
//           cmd.expire.max.ms: a command read later than it is refused and not run) and `enrol` (13.5.7: a code proved over TLS
//           enrols a key, named in the verdict's `enrolled`).
//           It reads a line or a lines.v1 message up to link.command.bytes and answers a longer one `ERR ... - closed`, then closes;
//           a node link message up to link.ws.in.bytes, and closes a longer one (1009). A side call carrying a right S in clear from
//           another machine is refused and its link ended (13.7.1); a wrong S, in clear or not, ends nothing.
//           `attack`: {"snonce": 32 hex} offers this snonce on every connection (a reused nonce); {"nonce": a string} greets with
//           this NONCE (one that is not 32 lowercase hex, say); {"verdict":"replay"} answers a proof, once one link has verified
//           here, with that link's verdict as it was, checking nothing; {"verdict":"reflect"} answers a proof with a verdict whose
//           proof is the connector's own client proof, checking nothing.
//   dial    {"carrier","transport","host","port","tls":true|false,"pin":<64 hex>|null,"node":{"t","id"},"key":{"file","kind"},
//            "meant":{"t","id"}|null,"name","legacy":true|false,"attack"}  -> {"ev":"connected","conn","tls","fingerprint","remote"}
//           A connector. It proves once, only to the id it meant and over the certificate it was shown, and takes the acceptor's
//           verdict only once its proof verifies. A pinned dial that is shown another certificate sends nothing. It never sends a
//           key, a code or plain AUTH; `legacy` lets it answer a 32-hex NONCE with AUTH-PROOF once an acceptor turns out not to speak
//           LINK. A keyed `ws` dial without TLS sends nothing unless the socket's remote address is loopback (13.1, 13.3.2). It reads
//           a line or message up to link.read.min.bytes; a longer one is reported `oversize`, skipped to its end, and the link stays.
//           A verdict that enrols (over TLS, with a code) is checked by deriving the same key, kept in this process, and proved from
//           then on to the node `meant` names; a refusal on a live link (a revoked key) ends the link's standing and its S.
//           `attack`: {"kid": a kid} names that kid in LINK while proving with its own key (a forged kid); {"replay": a conn} sends
//           the LINK that connection of this peer's sent - its cnonce, kid and client proof - as it was, from the same node on the
//           same carrier, and reads the verdict with its key (a replayed LINK: it links only where the acceptor offers the snonce it
//           offered then); {"role":true} sends its server proof as its client proof; {"twice":true} proves again on the same
//           connection once its verdict came; {"unpaired":true} connects and sends nothing more (a flood is several); {"reads":false}
//           reads nothing more once linked (a slow reader); {"origin","host"} are the upgrade's Origin and Host headers.
//   send    {"conn","line"} on a line carrier, {"conn","message"} on ws    -> {"ev":"sent","conn"}
//   call    {"conn","method","path","body","attack"} a side call on a verified ws link it dialled, with its S (13.7): only inside the
//           TLS the link pinned, or to a socket whose remote address is loopback   -> {"ev":"answer","status","body"}
//           `attack`: {"clear":true} carries S on a plain socket whatever the address; {"lid": 16 hex} names another lid; {"closed":
//           true} calls with the S of a link that has ended; {"origin","host"} are the request's Origin and Host headers.
//   set     {"end","clock","pongs":true|false|"unmatched"|"backwards","answer":"applied"|"accepted"|"refused"|"silent"|"drop",
//            "final":"applied"|"refused","late","stuck","revoke":<kid>,"code":{"file","kind":"code"}}   -> {"ev":"set"}
//           What an acceptor does from now: `pongs` false answers no ping, "unmatched" answers with a t1 never sent, "backwards" with
//           t3 before t2 (a lying clock is `clock` set far off); `answer` is how commands are answered ("silent" runs one and answers
//           nothing, "drop" runs one and ends the connection); `final` sends the final receipt of each command left accepted;
//           `late` is how late, in ms, each command is read (lateMs past receipt.late.ms; refused past `expireMs`); `stuck` makes its
//           show stuck (a `ws` link hears {"type":"beat","live":false}) or live again (a state frame); `revoke` revokes a key, and
//           `code` replaces the code (Patterns' NEW TOKEN) and revokes every key enrolled from the old one - each refusing at once
//           every verified link that used them (`line`: ERR LINK refused, then closed; `ws`: the refused verdict) and ending its S.
//   expect  {"match","after","within"}: the first event after "after" (its "n") that holds every field of "match" - a string that
//           ends in "*" matches by prefix - waiting at most "within" ms, never more than 2000 -> {"ev":"expect","ok","got"}
//   silence {"match","after","ms"}: no such event for "ms" (at most 2000; "match" defaults to any read) -> {"ev":"silence","ok","got"}
//   close   {"conn"} or {"end"}                                         -> {"ev":"close"}
//   quit                                                                 -> {"ev":"done"}, and the peer exits
// An unknown command, field or attack, or a value or shape a command does not take, is refused, never defaulted.
//
// Events, {"ev","n",...}, numbered from 1: ready; listening; connected; upgrade {conn,path,query,authorization: none|bearer|showbus|
// other,origin,host}; read {conn,line|message}; wrote {conn,line|message}; link {conn,role,carrier,lid,kid,verified,...}; refused
// {conn,role,class,words}; level0 {conn,words}; open {conn,words}; paired {conn,how}; enrolled {conn,role,kid}; revoked {conn,kid};
// command {conn,id,line|message}; repeat {conn,id}; expired {conn,id}; attack {conn,mode}; served {conn,method,path,status,
// authorization,origin,host,ended}; answer; oversize {conn,bytes}; closed {conn,by: peer - this peer ended it - or other}; and each
// command's answer.

import crypto from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import readline from 'node:readline';
import tls from 'node:tls';

// Every number below that section 14 of the contract names carries its row's name after "// 14: ", and each tree's test holds it to
// that row; the others are this peer's own.
const LABEL = 'showbus-peerlink-1'; // 14: peerlink.label
const VERSION = 1; // 14: peerlink.version
const FEATURES = ['final', 'receipts', 'stuck', 'expire', 'enrol']; // 14: peerlink.feature
const CAN_WORDS = 8; // 14: peerlink.can.words
// A show writer's stuckMs, and an expireMs's ceiling.
const STUCK_MIN_MS = 1000; // 14: show.stuck.min.ms
const STUCK_MAX_MS = 10000; // 14: show.stuck.max.ms
const EXPIRE_MAX_MS = 10000; // 14: cmd.expire.max.ms
// A command read later than this says so in its receipt.
const LATE_MS = 1000; // 14: receipt.late.ms
// 13.12.6's record: the ids kept per connector and for how long, and the most ids a RECEIPTS ask is answered for.
const RECEIPTS_KEPT = 256; // 14: receipts.kept
const RECEIPTS_KEPT_MS = 600000; // 14: receipts.kept.ms
const RECEIPTS_ASK_MAX = 32; // 14: receipts.ask.max
// How an acceptor answers commands, and its pongs (`set`).
const ANSWERS = ['applied', 'accepted', 'refused', 'silent', 'drop'];
const PONGS = [true, false, 'unmatched', 'backwards'];
// What 13.12.6's record answers for an id it holds that still runs: accepted.
const RUNNING = { ok: true, stage: 'accepted', effect: '' };
// The words a live link hears when the key it proved is revoked, or the code it proved is replaced (13.5.5).
const REVOKED_WORDS = 'this key was revoked';
const REPLACED_WORDS = 'the pairing code was replaced';
const PROOF_WORDS = 'the proof did not verify: a wrong key, or another machine between us';
const NOT_PAIRED = 'not paired \u2014 this node shows its state only to a paired connection';
const OPEN_WORDS = 'this node asks for no key';
// An expect or a silence waits at most this long in real time (13.20).
const WAIT_MAX_MS = 2000;
// What a connector reads whole; a longer line or message is reported oversize and skipped.
const READ_MAX = 262144; // 14: link.read.min.bytes
// A command line an acceptor reads; a longer one is answered ERR ... - closed (13.14).
const COMMAND_MAX = 65536; // 14: link.command.bytes
// A message a node link's acceptor reads, as Stagewatch's /ws does; a longer one ends the socket.
const WS_IN_MAX = 4096; // 14: link.ws.in.bytes
// How long a connection may go without finishing LINK, and so how long a sniffing acceptor waits for a first byte.
const HANDSHAKE_MS = 10000; // 14: peerlink.handshake.ms
// An HTTP head, and a side call's body.
const HEAD_MAX = 16384;
const BODY_MAX = 65536; // 14: context.bytes
// The events kept for expect and silence.
const LOG_MAX = 20000;
// The beat a verdict names.
const BEAT_MS = 2000; // 14: link.beat.ms
const WS_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
const LINES_PROTOCOL = 'playmap.lines.v1';

class Refusal extends Error {}

if (process.argv.length > 2) {
  process.stderr.write('the scripted peer takes no arguments: drive it with NDJSON on stdin (Show Bus 13.20); keys and certificates come from 0600 files a command names\n');
  process.exit(2);
}
if (Number(process.versions.node.split('.')[0]) < 22) {
  process.stderr.write(`the scripted peer needs Node 22 or later (this is ${process.versions.node})\n`);
  process.exit(2);
}

// ------------------------------------------------------------------ events

let seq = 0;
const log = [];
const waiters = new Set();

function emit(event) {
  const e = { ev: event.ev, n: ++seq, ...event };
  log.push(e);
  if (log.length > LOG_MAX) log.shift();
  process.stdout.write(JSON.stringify(e) + '\n');
  for (const w of [...waiters]) w(e);
  return e;
}

/** Whether `actual` holds every field of `want`: objects field by field, a string ending in "*" by prefix, anything else exactly. */
function holds(actual, want) {
  if (want !== null && typeof want === 'object' && !Array.isArray(want))
    return actual !== null && typeof actual === 'object' && Object.entries(want).every(([k, v]) => holds(actual[k], v));
  if (typeof want === 'string' && want.endsWith('*')) return typeof actual === 'string' && actual.startsWith(want.slice(0, -1));
  if (Array.isArray(want)) return JSON.stringify(actual) === JSON.stringify(want);
  return actual === want;
}

const isAnswer = e => e.ev === 'expect' || e.ev === 'silence';

// ------------------------------------------------------------------ 13.5 and 13.6: keys, the transcript, the proofs

const utf8 = s => Buffer.from(s, 'utf8');
const sha256 = b => crypto.createHash('sha256').update(b).digest();
const hmacHex = (k, s) => crypto.createHmac('sha256', k).update(utf8(s)).digest('hex');
const isHex = (s, n) => typeof s === 'string' && s.length === n && /^[0-9a-f]*$/.test(s);
const isKind = t => typeof t === 'string' && /^[a-z0-9_-]{1,16}$/.test(t);
const isId = id => typeof id === 'string' && /^[A-Za-z0-9_-]{6,40}$/.test(id);
const isKid = k => k === 'code' || isHex(k, 16);
const isBind = b => b === 'plain' || (typeof b === 'string' && b.startsWith('tls:sha256:') && isHex(b.slice(11), 64));
const isCount = s => typeof s === 'string' && /^[0-9]{1,9}$/.test(s);
const isNode = s => typeof s === 'string' && s.indexOf(':') > 0 && isKind(s.slice(0, s.indexOf(':'))) && isId(s.slice(s.indexOf(':') + 1));
const fresh = () => crypto.randomBytes(16).toString('hex');

// The characters 13.5.1's canonical form removes.
const REMOVED = '-\u0009\u000A\u000B\u000C\u000D\u0020\u0085\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF';

/** 13.5.1: a code's canonical form, or null when what is left is not [A-Z0-9]{1,64}. */
function canon(code) {
  let s = '';
  for (let i = 0; i < code.length; i++) {
    const c = code[i];
    if (REMOVED.includes(c)) continue;
    s += c >= 'a' && c <= 'z' ? c.toUpperCase() : c;
  }
  return /^[A-Z0-9]{1,64}$/.test(s) ? s : null;
}

const codeK = c => sha256(utf8(`${LABEL} code\n${c}`));
const keyK = key => sha256(utf8(key));
const kidOf = K => sha256(Buffer.concat([utf8(`${LABEL} kid\n`), K])).toString('hex').slice(0, 16);

/** 13.6.1: T, its ten lines joined by LF; null when a field breaks its shape. */
function transcript(f) {
  if (!['line', 'ws'].includes(f.carrier) || !Number.isInteger(f.offer) || f.offer < 0 || !Number.isInteger(f.speaks) || f.speaks < 0
    || !isNode(f.server) || !isNode(f.client) || !isKid(f.kid) || !isHex(f.snonce, 32) || !isHex(f.cnonce, 32) || !isBind(f.bind)) return null;
  return [LABEL, `carrier=${f.carrier}`, `offer=${f.offer}`, `speaks=${f.speaks}`, `server=${f.server}`, `client=${f.client}`,
    `kid=${f.kid}`, `snonce=${f.snonce}`, `cnonce=${f.cnonce}`, `bind=${f.bind}`].join('\n');
}

const clientProof = (K, T) => hmacHex(K, `client\n${T}`);
const serverProof = (K, T) => hmacHex(K, `server\n${T}`);
const lidOf = T => sha256(utf8(`${LABEL} lid\n${T}`)).toString('hex').slice(0, 16);
const sessionOf = (K, T) => hmacHex(K, `session\n${T}`);

/** Whether `given` is `expected`, compared in constant time once both are lowercase hex of one length (13.6.3). */
function same(expected, given) {
  if (typeof expected !== 'string' || !isHex(given, expected.length)) return false;
  return crypto.timingSafeEqual(Buffer.from(expected), Buffer.from(given));
}

/** 13.6.8: the token as 1.1 desks compare it; null when nothing is left or a character 13.5.1 removes is still in it. */
function legacyKey(token) {
  let s = '';
  for (let i = 0; i < token.length; i++) {
    const c = token[i];
    if ('- \t\r\n'.includes(c)) continue;
    if (REMOVED.includes(c)) return null;
    const u = c.toUpperCase();
    s += u.length === 1 ? u : c;
  }
  return s.length > 0 ? s : null;
}

/** 13.6.8: AUTH-PROOF, HMAC-SHA256(the legacy key, nonce + HELLO name); null for a nonce that is not 32 lowercase hex. */
function legacyProof(token, nonce, name) {
  const k = isHex(nonce, 32) ? legacyKey(token) : null;
  return k ? hmacHex(utf8(k), nonce + name) : null;
}

/** 13.5.7: the key both ends derive when a code proved over TLS enrols, K_link = HMAC-SHA256(K_code, "enrol\n" + T). */
const enrolK = (K, T) => crypto.createHmac('sha256', K).update(utf8(`enrol\n${T}`)).digest();

/** One line of at most `max` characters, as a header's value or a NONCE an attacker sends. */
const isOneLine = (s, max) => typeof s === 'string' && s.length > 0 && s.length <= max && !/[\r\n]/.test(s);

/**
 * A command's `attack`: an object whose every field is one of `modes`, each value of the shape its mode takes; empty for none. An
 * unknown mode or a value of another shape is refused, never defaulted.
 */
function attackOf(cmd, modes) {
  if (cmd.attack === undefined) return {};
  const a = cmd.attack;
  if (a === null || typeof a !== 'object' || Array.isArray(a)) throw new Refusal(`${cmd.do}: attack is an object of modes`);
  for (const [k, v] of Object.entries(a)) {
    if (!Object.hasOwn(modes, k)) throw new Refusal(`${cmd.do}: attack has no mode ${k} (${Object.keys(modes).join(', ')})`);
    if (!modes[k](v)) throw new Refusal(`${cmd.do}: attack ${k} does not take ${JSON.stringify(v)}`);
  }
  return a;
}

const isTrue = v => v === true;
const isHeader = v => isOneLine(v, 256);

// ------------------------------------------------------------------ files

/** A key, code or certificate file: readable by its owner alone. */
function guarded(path, what) {
  if (typeof path !== 'string' || path.length === 0) throw new Refusal(`${what}: a file path`);
  let st;
  try {
    st = fs.statSync(path);
  } catch {
    throw new Refusal(`${what}: no file at ${path}`);
  }
  if (!st.isFile()) throw new Refusal(`${what}: ${path} is not a file`);
  if (process.platform !== 'win32' && (st.mode & 0o077) !== 0)
    throw new Refusal(`${what}: ${path} is open to others (mode ${(st.mode & 0o777).toString(8)}) \u2014 keys and certificates come from 0600 files`);
  return fs.readFileSync(path);
}

/**
 * What a key or code file holds: its K, its kid and the secret itself, which never leaves this process but in a proof. `fields` are
 * the fields the spec may carry besides file and kind.
 */
function credential(spec, what, fields = []) {
  if (spec === null || typeof spec !== 'object' || Array.isArray(spec)) throw new Refusal(`${what}: {"file","kind"}`);
  const unknown = Object.keys(spec).filter(k => k !== 'file' && k !== 'kind' && !fields.includes(k));
  if (unknown.length > 0) throw new Refusal(`${what}: unknown field ${unknown.join(', ')}`);
  const raw = guarded(spec.file, what).toString('utf8').replace(/[\r\n]+$/, '');
  if (spec.kind === 'key') {
    if (raw.length === 0) throw new Refusal(`${what}: the key file is empty`);
    const K = keyK(raw);
    return { kind: 'key', K, kid: kidOf(K), raw };
  }
  if (spec.kind === 'code') {
    const c = canon(raw);
    if (c === null) throw new Refusal(`${what}: PeerLink pairs a token of letters and digits \u2014 this one reaches only an older build`);
    return { kind: 'code', K: codeK(c), kid: 'code', raw };
  }
  throw new Refusal(`${what}: kind is key or code`);
}

/** A certificate and its private key, with the certificate's fingerprint (13.8.1: SHA-256 of its DER, lowercase hex). */
function material(cmd, what) {
  if (cmd.cert === undefined && cmd.certKey === undefined) return null;
  const cert = guarded(cmd.cert, `${what} cert`);
  const key = guarded(cmd.certKey, `${what} certKey`);
  let fp;
  try {
    fp = sha256(new crypto.X509Certificate(cert).raw).toString('hex');
  } catch {
    throw new Refusal(`${what}: cert is not a PEM certificate`);
  }
  return { cert, key, fp };
}

const fingerprint = peer => (peer && peer.raw ? sha256(peer.raw).toString('hex') : null);
const short = fp => (fp ? `${fp.slice(0, 4)} ${fp.slice(4, 8)}` : 'none');

// ------------------------------------------------------------------ framing

/**
 * Lines as they arrive, each at most `max` bytes before its LF. A longer one is reported once to onOversize with its first bytes,
 * and everything up to its LF is skipped, so its tail is never read as a line of its own.
 */
function splitLines(max, onLine, onOversize) {
  let held = [];
  let size = 0;
  let skipping = false;
  return chunk => {
    let at = 0;
    for (;;) {
      const lf = chunk.indexOf(0x0a, at);
      const piece = chunk.subarray(at, lf < 0 ? chunk.length : lf);
      if (!skipping) {
        if (size + piece.length > max) {
          skipping = true;
          const head = Buffer.concat([...held, piece]).subarray(0, 64);
          held = [];
          size = 0;
          onOversize(head);
        } else {
          held.push(piece);
          size += piece.length;
        }
      }
      if (lf < 0) return;
      if (!skipping) {
        let line = Buffer.concat(held).toString('utf8');
        if (line.endsWith('\r')) line = line.slice(0, -1);
        onLine(line);
      }
      held = [];
      size = 0;
      skipping = false;
      at = lf + 1;
    }
  };
}

function wsFrame(opcode, payload, masked) {
  const len = payload.length;
  let head;
  if (len < 126) head = Buffer.from([0x80 | opcode, (masked ? 0x80 : 0) | len]);
  else if (len < 65536) {
    head = Buffer.alloc(4);
    head[0] = 0x80 | opcode;
    head[1] = (masked ? 0x80 : 0) | 126;
    head.writeUInt16BE(len, 2);
  } else {
    head = Buffer.alloc(10);
    head[0] = 0x80 | opcode;
    head[1] = (masked ? 0x80 : 0) | 127;
    head.writeBigUInt64BE(BigInt(len), 2);
  }
  if (!masked) return Buffer.concat([head, payload]);
  const mask = crypto.randomBytes(4);
  const body = Buffer.from(payload);
  for (let i = 0; i < body.length; i++) body[i] ^= mask[i & 3];
  return Buffer.concat([head, mask, body]);
}

/**
 * A WebSocket's frames as they arrive: whole messages of at most `max` bytes to onMessage, control frames to onControl. A message
 * that would pass `max` is reported once (onControl 'oversize', with the bytes it declared so far) and its frames' payloads are
 * skipped byte for byte to its final frame, so the stream stays in step and the next frame is read as a frame.
 */
function wsReader(max, onMessage, onControl) {
  let buf = Buffer.alloc(0);
  let parts = [];
  let size = 0;
  let op = 0;
  let over = false;
  let skip = 0;
  let skipEnds = false;
  const endSkip = () => {
    if (!skipEnds) return;
    over = false;
    parts = [];
    size = 0;
  };
  return chunk => {
    buf = buf.length === 0 ? chunk : Buffer.concat([buf, chunk]);
    for (;;) {
      if (skip > 0) {
        const n = Math.min(skip, buf.length);
        buf = buf.subarray(n);
        skip -= n;
        if (skip > 0) return;
        endSkip();
      }
      if (buf.length < 2) return;
      const fin = (buf[0] & 0x80) !== 0;
      const opcode = buf[0] & 0x0f;
      const masked = (buf[1] & 0x80) !== 0;
      let len = buf[1] & 0x7f;
      let off = 2;
      if (len === 126) {
        if (buf.length < 4) return;
        len = buf.readUInt16BE(2);
        off = 4;
      } else if (len === 127) {
        if (buf.length < 10) return;
        const big = buf.readBigUInt64BE(2);
        len = big > BigInt(Number.MAX_SAFE_INTEGER) ? Number.MAX_SAFE_INTEGER : Number(big);
        off = 10;
      }
      const maskAt = off;
      if (masked) off += 4;
      if (buf.length < off) return;
      const control = opcode >= 8;
      if (!control && opcode !== 0) {
        op = opcode;
        parts = [];
        size = 0;
        over = false;
      }
      // A control frame carries at most 125 bytes (RFC 6455 5.5); a data frame at most what its message has left of `max`.
      if (control ? len > 125 : over || size + len > max) {
        if (!control && !over) {
          over = true;
          parts = [];
          onControl('oversize', size + len);
        }
        buf = buf.subarray(off);
        skip = len;
        skipEnds = !control && fin;
        if (skip === 0) endSkip();
        continue;
      }
      if (buf.length < off + len) return;
      const payload = Buffer.from(buf.subarray(off, off + len));
      if (masked) for (let i = 0; i < payload.length; i++) payload[i] ^= buf[maskAt + (i & 3)];
      buf = buf.subarray(off + len);
      if (control) {
        onControl(opcode, payload);
        continue;
      }
      parts.push(payload);
      size += payload.length;
      if (fin) {
        onMessage(op, Buffer.concat(parts));
        parts = [];
        size = 0;
      }
    }
  };
}

/**
 * An HTTP head: its start line, its headers (lower-case names) and the bytes that followed it. Bytes that cannot start one (a TLS
 * handshake on a plain door, say) fail at once, as a plain HTTP server answers them.
 */
function readHead(sock, onHead, onFail) {
  let buf = Buffer.alloc(0);
  const onData = chunk => {
    buf = Buffer.concat([buf, chunk]);
    if (!(buf[0] >= 0x41 && buf[0] <= 0x5a)) {
      sock.removeListener('data', onData);
      onFail('not an HTTP request', true);
      return;
    }
    const end = buf.indexOf('\r\n\r\n');
    if (end < 0) {
      if (buf.length > HEAD_MAX) {
        sock.removeListener('data', onData);
        onFail('an HTTP head too long');
      }
      return;
    }
    sock.removeListener('data', onData);
    const [start, ...rest] = buf.subarray(0, end).toString('latin1').split('\r\n');
    const headers = {};
    for (const l of rest) {
      const i = l.indexOf(':');
      if (i > 0) headers[l.slice(0, i).trim().toLowerCase()] = l.slice(i + 1).trim();
    }
    onHead(start, headers, buf.subarray(end + 4));
  };
  sock.on('data', onData);
  sock.resume();
}

function respond(sock, status, body) {
  const text = JSON.stringify(body);
  const reason = { 200: 'OK', 400: 'Bad Request', 401: 'Unauthorized', 404: 'Not Found', 413: 'Payload Too Large' }[status] ?? 'Status';
  sock.end(`HTTP/1.1 ${status} ${reason}\r\nContent-Type: application/json\r\nContent-Length: ${Buffer.byteLength(text)}\r\nConnection: close\r\n\r\n${text}`);
}

function authorization(header) {
  if (header === undefined) return 'none';
  if (/^Bearer /i.test(header)) return 'bearer';
  if (header.startsWith('ShowBus ')) return 'showbus';
  return 'other';
}

/**
 * Whether a socket's remote address is a loopback address (13.1's local): 127.0.0.0/8, ::1, or IPv4-mapped 127.0.0.0/8. Never a host
 * name - `localhost` included - nor an address this machine owns.
 */
function isLoopbackAddress(address) {
  if (typeof address !== 'string') return false;
  const a = address.toLowerCase();
  const v4 = a.startsWith('::ffff:') ? a.slice(7) : a;
  if (net.isIPv4(v4)) return v4.split('.')[0] === '127';
  return a === '::1' || a === '0:0:0:0:0:0:0:1';
}

/** One address to bind: an IP literal, never a name and never every interface (13.3.5's bind address). */
const isBindable = host => typeof host === 'string' && net.isIP(host) !== 0 && !['0.0.0.0', '::', '0:0:0:0:0:0:0:0'].includes(host.toLowerCase());

// ------------------------------------------------------------------ connections

const ends = new Map();
const conns = new Map();
let nextConn = 1;

function connection(o) {
  const c = { id: nextConn++, open: true, ...o };
  conns.set(c.id, c);
  return c;
}

function closed(conn) {
  if (!conn.open) return;
  conn.open = false;
  const end = conn.end ? ends.get(conn.end) : null;
  if (end) {
    for (const [lid, s] of end.sessions) if (s.conn === conn.id) end.sessions.delete(lid);
    end.links.delete(conn.id);
    for (const [k, p] of end.pending) if (p.conn === conn) end.pending.delete(k);
  }
  emit({ ev: 'closed', conn: conn.id, by: conn.hungUp ? 'peer' : 'other' });
}

/** This peer ends a connection: its closed event says so ("by":"peer"), apart from one the other end ended. */
function hangUp(conn, how = 'end') {
  conn.hungUp = true;
  if (how === 'destroy') conn.sock.destroy();
  else conn.sock.end();
}

/** A WebSocket on a connection: the client masks what it sends (RFC 6455). */
function wsSession(conn, masked, rest) {
  const ws = { closing: false };
  const raw = (opcode, payload) => {
    if (conn.open && !conn.sock.destroyed) conn.sock.write(wsFrame(opcode, payload, masked));
  };
  ws.text = s => raw(1, utf8(s));
  // 1000 a normal close; 1009 a message too big to take (RFC 6455 7.4.1).
  ws.close = (code = 1000) => {
    if (ws.closing) return;
    ws.closing = true;
    const status = Buffer.alloc(2);
    status.writeUInt16BE(code);
    raw(8, status);
    hangUp(conn);
  };
  /** Reads text messages of at most `max` bytes to onText; a longer one goes to onOversize with the bytes it declared. */
  ws.start = (onText, max, onOversize) => {
    const reader = wsReader(max,
      (opcode, payload) => {
        // Nothing is read once this socket is closing: an answer after the close frame would not be sent.
        if (opcode === 1 && !ws.closing) onText(payload.toString('utf8'));
      },
      (opcode, payload) => {
        if (opcode === 'oversize') onOversize(payload);
        else if (opcode === 8) {
          if (!ws.closing) {
            ws.closing = true;
            raw(8, payload.subarray(0, 2));
          }
          conn.sock.end();
        } else if (opcode === 9) raw(10, payload);
      });
    conn.sock.on('data', reader);
    if (rest.length > 0) reader(rest);
    conn.sock.resume();
  };
  return ws;
}

/** The line grammar on a stream; onOversize hears the first bytes of a line longer than `max`. */
const tcpLines = conn => ({
  send: line => conn.sock.write(line + '\n'),
  start: (onLine, max, onOversize) => {
    conn.sock.on('data', splitLines(max, onLine, head => onOversize(head.toString('utf8'))));
    conn.sock.resume();
  },
  close: () => hangUp(conn),
});

/** The line grammar on `playmap.lines.v1`, one text message a line; onOversize hears nothing of a message longer than `max`. */
const wsLines = ws => ({ send: line => ws.text(line), start: (onLine, max, onOversize) => ws.start(onLine, max, () => onOversize('')), close: () => ws.close() });

// ------------------------------------------------------------------ the acceptor

function listen(cmd) {
  const name = cmd.end ?? 'a';
  if (typeof name !== 'string' || !/^[A-Za-z0-9_-]{1,16}$/.test(name)) throw new Refusal('listen: end is a name of 1-16 letters, digits, - or _');
  if (ends.has(name)) throw new Refusal(`listen: end ${name} is already open`);
  const host = cmd.host ?? '127.0.0.1';
  if (!isBindable(host)) throw new Refusal('listen: host is the one IP address this end binds, never a name or every interface (13.3.5)');
  if (!['line', 'ws'].includes(cmd.carrier)) throw new Refusal('listen: carrier is line or ws');
  const transport = cmd.transport ?? 'tcp';
  if (!['tcp', 'ws'].includes(transport) || (cmd.carrier === 'ws' && transport !== 'tcp')) throw new Refusal('listen: transport is tcp, or ws for a line carrier');
  const tlsMode = cmd.tls ?? 'sniff';
  if (!['sniff', 'plain', 'only'].includes(tlsMode)) throw new Refusal('listen: tls is sniff, plain or only');
  const mat = material(cmd, 'listen');
  if (tlsMode !== 'plain' && mat === null) throw new Refusal('listen: TLS needs cert and certKey, or tls plain');
  const node = cmd.node;
  if (!node || !isKind(node.t) || !isId(node.id)) throw new Refusal('listen: node is {"t","id"} in \u00a72\'s shapes');
  const level = cmd.level ?? 1;
  if (level !== 0 && level !== 1) throw new Refusal('listen: level is 1, or 0 for an acceptor that does not speak LINK');
  const keys = cmd.keys ?? [];
  if (!Array.isArray(keys)) throw new Refusal('listen: keys is a list');
  const creds = keys.map((k, i) => {
    const c = credential(k, `listen keys[${i}]`, ['bound']);
    if (k.bound !== undefined && (c.kind !== 'key' || !isNode(k.bound)))
      throw new Refusal(`listen keys[${i}]: bound names the connector t:id a key speaks for; a code is bound to no one connector (13.5.2)`);
    c.bound = k.bound ?? null;
    return c;
  });
  const can = cmd.can ?? [];
  if (!Array.isArray(can) || can.length > CAN_WORDS || !can.every(w => FEATURES.includes(w)) || new Set(can).size !== can.length)
    throw new Refusal('listen: can lists \u00a714 peerlink.feature words, each once');
  // A verdict names stuckMs when it lists stuck and expireMs when it lists expire (13.6.6), each within its \u00a714 bounds.
  const stuckMs = cmd.stuckMs ?? null;
  if (can.includes('stuck') !== (stuckMs !== null) || (stuckMs !== null && (!Number.isInteger(stuckMs) || stuckMs < STUCK_MIN_MS || stuckMs > STUCK_MAX_MS)))
    throw new Refusal(`listen: stuckMs (${STUCK_MIN_MS}-${STUCK_MAX_MS}) comes with can stuck, and only with it`);
  const expireMs = cmd.expireMs ?? null;
  if (can.includes('expire') !== (expireMs !== null) || (expireMs !== null && (!Number.isInteger(expireMs) || expireMs < 1 || expireMs > EXPIRE_MAX_MS)))
    throw new Refusal(`listen: expireMs (1-${EXPIRE_MAX_MS}) comes with can expire, and only with it`);
  if (level === 0 && can.length > 0) throw new Refusal('listen: an acceptor that does not speak LINK gives no verdict, so it lists no feature');
  const attack = attackOf(cmd, { snonce: v => isHex(v, 32), nonce: v => isOneLine(v, 128), verdict: v => v === 'replay' || v === 'reflect' });
  const inst = cmd.inst ?? crypto.randomBytes(4).toString('hex');
  if (typeof inst !== 'string' || !/^[A-Za-z0-9]{1,8}$/.test(inst)) throw new Refusal('listen: inst is 1-8 letters and digits');
  const scope = cmd.scope ?? 'post';
  if (!['follow', 'post', 'drive', 'operate'].includes(scope)) throw new Refusal('listen: scope is follow, post, drive or operate');
  // Show Bus 7.2: a node with a token greets an untrusted line with NONCE (Patterns 88.1's level 0 with STATE first); an open node with STATE.
  const greet = cmd.greet ?? (cmd.carrier !== 'line' ? [] : creds.length === 0 ? ['state'] : level === 0 ? ['state', 'nonce'] : ['nonce']);
  if (!Array.isArray(greet) || !greet.every(g => g === 'state' || g === 'nonce')) throw new Refusal('listen: greet lists state and nonce');
  const clock = cmd.clock ?? 0;
  if (!Number.isSafeInteger(clock)) throw new Refusal('listen: clock is a whole number of ms');
  const port = cmd.port ?? 0;
  if (!Number.isInteger(port) || port < 0 || port > 65535) throw new Refusal('listen: port is 0-65535');
  const end = {
    name, carrier: cmd.carrier, transport, tlsMode, mat, node, level, creds, can, stuckMs, expireMs, attack, inst, scope, greet, clock,
    pongs: true, answer: 'applied', late: 0, stuck: false, sessions: new Map(), links: new Map(), books: new Map(), pending: new Map(),
    replay: null, rev: 0, server: null,
  };
  const server = net.createServer(sock => accept(end, sock));
  end.server = server;
  ends.set(name, end);
  server.on('error', e => {
    ends.delete(name);
    emit({ ev: 'error', re: cmd.tag, words: `listen: ${e.message}` });
  });
  server.listen(port, host, () =>
    emit({ ev: 'listening', re: cmd.tag, end: name, host, port: server.address().port, fingerprint: mat ? mat.fp : null }));
}

function accept(end, raw) {
  const conn = connection({ end: end.name, role: 'acceptor', carrier: end.carrier, sock: raw, tls: false, remote: raw.remoteAddress });
  raw.on('error', () => {});
  raw.on('close', () => closed(conn));
  const go = () => {
    emit({ ev: 'connected', conn: conn.id, end: end.name, role: 'acceptor', tls: conn.tls, fingerprint: conn.tls ? end.mat.fp : null });
    if (end.carrier === 'line' && end.transport === 'tcp') lineAcceptor(end, conn, tcpLines(conn));
    else httpAcceptor(end, conn);
  };
  const wrap = () => {
    const t = new tls.TLSSocket(raw, { isServer: true, key: end.mat.key, cert: end.mat.cert });
    t.on('error', () => {});
    t.on('close', () => closed(conn));
    conn.sock = t;
    conn.tls = true;
    t.once('secure', go);
  };
  if (end.tlsMode === 'plain') return go();
  if (end.tlsMode === 'only') return wrap();
  // 13.8.5: the first byte says TLS or plain. A connector speaks first on every carrier (its HELLO, its upgrade or its TLS
  // handshake), so a sniffing acceptor greets only once that byte has come, and closes a connection that sends none in time.
  const silent = setTimeout(() => hangUp(conn, 'destroy'), HANDSHAKE_MS);
  raw.once('data', chunk => {
    clearTimeout(silent);
    raw.pause();
    raw.unshift(chunk);
    if (chunk[0] === 0x16) wrap();
    else go();
  });
  raw.once('close', () => clearTimeout(silent));
}

function stateLine(end, paired) {
  return { rev: ++end.rev, node: end.node.id, paired };
}

function frame(end, linked) {
  return { type: 'state', v: 1, seq: ++end.rev, node: end.node.id, public: !linked };
}

/** What an acceptor holds for a kid: its credential, or for a kid it does not know a fresh K, so the proof fails as a wrong one would (13.6.3). */
function credFor(end, kid) {
  return end.creds.find(x => x.kid === kid) ?? { K: crypto.randomBytes(32), kind: kid === 'code' ? 'code' : 'key', kid, unknown: true };
}

/** 13.6.6: what a verdict names after its proof and lid: the level, the features this end keeps, its inst and beat, and their numbers. */
function verdictTail(end) {
  const v = { level: VERSION, can: end.can, inst: end.inst, beatMs: BEAT_MS };
  if (end.stuckMs !== null) v.stuckMs = end.stuckMs;
  if (end.expireMs !== null) v.expireMs = end.expireMs;
  return v;
}

/** The scope a verified link holds: a code's is operate (13.5.1), an enrolled key's follow (13.5.7), a key's this end's. */
const scopeOf = (end, cred) => (cred.kind === 'code' ? 'operate' : cred.kind === 'link' ? 'follow' : end.scope);

/**
 * After a proof verified (13.5.2, 13.5.7): a key bound to another connector is refused with its words (`binding`, never counted), and
 * one bound to none is bound to this one; a code is bound to no one connector, and proved over TLS it enrols when this end offers
 * `enrol` - over plain it never does. {binding: words} or {enrolled: the kid it enrolled, or null}.
 */
function admit(end, conn, cred, T, peer) {
  if (cred.kind !== 'code') {
    if (cred.bound && cred.bound !== peer) return { binding: `this key speaks for ${cred.bound.replace(':', ' ')}` };
    cred.bound = peer;
    return { enrolled: null };
  }
  if (!end.can.includes('enrol') || !conn.tls) return { enrolled: null };
  const K = enrolK(cred.K, T);
  const kid = kidOf(K);
  end.creds = end.creds.filter(c => !(c.enrolled && c.bound === peer));
  end.creds.push({ kind: 'link', K, kid, raw: null, bound: peer, enrolled: true });
  emit({ ev: 'enrolled', conn: conn.id, role: 'acceptor', kid, peer });
  return { enrolled: kid };
}

/** A verified link this end keeps, so that a revocation reaches it at once (13.5.5); `refuse` says so on its carrier. */
function registerLink(end, conn, cred, refuse) {
  end.links.set(conn.id, { conn, cred, refuse });
}

/** 13.5.5: refuses at once every verified link whose credential `gone` takes, ending its S, with `words`. */
function revokeLinks(end, gone, words) {
  for (const [id, l] of [...end.links]) {
    if (!gone(l.cred)) continue;
    end.links.delete(id);
    for (const [lid, s] of end.sessions) if (s.conn === id) end.sessions.delete(lid);
    emit({ ev: 'revoked', conn: id, kid: l.cred.kid });
    l.refuse(words);
  }
}

/**
 * 13.12.6's record for one verified connector (`t:id`): its ids in the order first read, each with when (this end's clock) and its
 * final receipt (null while it still runs). It holds at most the last receipts.kept, none older than receipts.kept.ms.
 */
function bookOf(end, peer) {
  let book = end.books.get(peer);
  if (!book) end.books.set(peer, (book = new Map()));
  trim(end, book);
  return book;
}

/** Drops a record's oldest ids until it holds at most receipts.kept, none older than receipts.kept.ms. */
function trim(end, book) {
  for (const [id, r] of book) {
    if (book.size <= RECEIPTS_KEPT && end.clock - r.at <= RECEIPTS_KEPT_MS) break;
    book.delete(id);
  }
}

/**
 * 13.12.6: a RECEIPTS answer for the asking connector's own ids - each id's final receipt, accepted for one that still runs, or
 * "unknown" for one it does not hold - with this end's inst.
 */
function receiptsAnswer(end, peer, ids) {
  const book = bookOf(end, peer);
  const receipts = {};
  for (const id of [...new Set(ids)].slice(0, RECEIPTS_ASK_MAX)) receipts[id] = book.has(id) ? book.get(id).receipt ?? RUNNING : 'unknown';
  return { inst: end.inst, receipts };
}

/**
 * A command read under request id `id` on a connection that may run commands, answered as this end's `answer` says (13.12.3): from
 * 13.12.6's record when its link verified and it keeps `receipts` - a repeated id is answered with "repeat":true and never run again,
 * accepted while it still runs - refused unrun when read later than its expireMs (13.12.5), and saying lateMs when read later than receipt.late.ms. A command run
 * and left unanswered ("silent", "drop") is kept applied: it ran. `out.receipt(id, fields, final)` writes the carrier's receipt and
 * `out.drop()` ends the connection unanswered.
 */
function command(end, conn, id, said, out) {
  const book = conn.peer && end.can.includes('receipts') ? bookOf(end, conn.peer) : null;
  if (book && book.has(id)) {
    emit({ ev: 'repeat', conn: conn.id, id });
    out.receipt(id, { ...(book.get(id).receipt ?? RUNNING), repeat: true }, false);
    return;
  }
  const keep = receipt => {
    if (!book) return;
    book.set(id, { at: book.get(id)?.at ?? end.clock, receipt });
    trim(end, book);
  };
  if (end.expireMs !== null && end.late > end.expireMs) {
    const r = { ok: false, error: `expired \u2014 read ${end.late} ms late at ${end.clock}, not run` };
    emit({ ev: 'expired', conn: conn.id, id });
    keep(r);
    out.receipt(id, r, false);
    return;
  }
  emit({ ev: 'command', conn: conn.id, id, ...said });
  const applied = { ok: true, stage: 'applied', effect: '', ...(end.late > LATE_MS ? { lateMs: end.late } : {}) };
  switch (end.answer) {
    case 'silent':
      keep(applied);
      return;
    case 'drop':
      keep(applied);
      out.drop();
      return;
    case 'refused': {
      const r = { ok: false, error: 'the scripted peer refuses it' };
      keep(r);
      out.receipt(id, r, false);
      return;
    }
    case 'accepted':
      keep(null);
      if (end.can.includes('final')) end.pending.set(`${conn.id} ${id}`, { conn, id, out, keep });
      out.receipt(id, { ok: true, stage: 'accepted', effect: 'queued', ...(end.late > LATE_MS ? { lateMs: end.late } : {}) }, false);
      return;
    default:
      keep(applied);
      out.receipt(id, applied, false);
  }
}

/** A receipt on a line link (13.12.3): OK @id, OK @id {json} or ERR @id <words>, `final` after the id on a follow-up. */
function lineReceipt(send, id, r, final) {
  const f = final ? ' final' : '';
  if (r.ok === false && !r.repeat) {
    send(`ERR @${id}${f} ${r.error}`);
    return;
  }
  const bare = r.ok === true && r.stage === 'applied' && r.effect === '' && Object.keys(r).length === 3;
  send(`OK @${id}${f}${bare ? '' : ` ${JSON.stringify(r)}`}`);
}

/** A pong's stamps for a ping's t1: this end's clock for t2 and t3, unless `pongs` lies - a t1 never sent, or t3 before t2 (13.11.2). */
function pongOf(end, t1) {
  if (end.pongs === false) return null;
  const t = end.pongs !== 'unmatched' ? t1 : typeof t1 === 'number' ? t1 + 1 : /^[0-9]{1,15}$/.test(t1) ? String(Number(t1) + 1) : `${t1}1`;
  return { t, t2: end.clock, t3: end.pongs === 'backwards' ? end.clock - 1 : end.clock };
}

function lineAcceptor(end, conn, io) {
  // An open node (no code, no keys) shows everything to every connection (Show Bus 7.2).
  const open = end.creds.length === 0;
  const s = { paired: open, linked: false, hello: '', nonce: null, ask: null, snonce: null, proved: false, ended: false };
  const bind = conn.tls ? `tls:sha256:${end.mat.fp}` : 'plain';
  const send = line => {
    if (!conn.open || s.ended) return;
    io.send(line);
    emit({ ev: 'wrote', conn: conn.id, line });
  };
  conn.write = send;
  conn.close = io.close;
  /** This end ends the connection after what it has written: nothing it reads afterwards is answered. */
  const finish = () => {
    s.ended = true;
    io.close();
  };
  const greet = () => send(`STATE ${JSON.stringify(stateLine(end, s.paired))}`);
  const paired = how => {
    s.paired = true;
    emit({ ev: 'paired', conn: conn.id, how });
    greet();
  };
  const out = { receipt: (id, r, final) => lineReceipt(send, id, r, final), drop: finish };
  for (const g of end.greet) {
    if (g === 'state') greet();
    else {
      s.nonce = end.attack.nonce ?? fresh();
      send(`NONCE ${s.nonce}`);
    }
  }
  // 13.14: a command line is read up to link.command.bytes; a longer one is answered with its id when it has one, and closed.
  const oversize = head => {
    if (s.ended) return;
    const at = /^@([A-Za-z0-9_-]{1,32}) /.exec(head);
    emit({ ev: 'oversize', conn: conn.id, bytes: COMMAND_MAX + 1 });
    send(`ERR${at ? ` @${at[1]}` : ''} a line longer than ${COMMAND_MAX} bytes \u2014 closed`);
    finish();
  };
  io.start(line => {
    if (s.ended) return;
    emit({ ev: 'read', conn: conn.id, line });
    const m = /^@([A-Za-z0-9_-]{1,32}) (.*)$/.exec(line);
    const id = m ? m[1] : null;
    const body = m ? m[2] : line;
    const sp = body.indexOf(' ');
    const verb = (sp < 0 ? body : body.slice(0, sp)).toUpperCase();
    const arg = sp < 0 ? '' : body.slice(sp + 1);
    const reply = (ok, text) => send(`${ok ? 'OK' : 'ERR'}${id ? ` @${id}` : ''}${text ? ` ${text}` : ''}`);
    switch (verb) {
      case 'HELLO':
        s.hello = arg;
        reply(true, '');
        return;
      case 'PING': {
        // 13.10.5: with feature stuck, a pong says STUCK while this end's show is stuck.
        const p = pongOf(end, arg.split(' ')[0]);
        if (p) reply(true, `PONG ${p.t} ${p.t2} ${p.t3}${s.linked && end.stuck && end.can.includes('stuck') ? ' STUCK' : ''}`);
        return;
      }
      case 'LINK':
        if (end.level === 0) reply(false, `unknown command '${body}'`);
        else if (open) {
          // 13.6.7: an open acceptor proves nothing and asks for nothing.
          reply(false, `LINK open \u2014 ${OPEN_WORDS}`);
          emit({ ev: 'open', conn: conn.id, words: OPEN_WORDS });
        } else link(body, reply);
        return;
      case 'AUTH-PROOF': {
        const code = end.creds.find(c => c.kind === 'code');
        const want = code && s.nonce ? legacyProof(code.raw, s.nonce, s.hello) : null;
        if (!s.paired && want !== null && same(want, arg)) {
          reply(true, 'paired');
          paired('auth-proof');
        } else reply(false, 'wrong token');
        return;
      }
      case 'AUTH': {
        const code = end.creds.find(c => c.kind === 'code');
        if (!isLoopbackAddress(conn.remote)) reply(false, 'plain AUTH is for this machine only \u2014 answer the NONCE line with AUTH-PROOF');
        else if (!s.paired && code && legacyKey(arg) !== null && legacyKey(arg) === legacyKey(code.raw)) {
          reply(true, 'paired');
          paired('auth');
        } else reply(false, 'wrong token');
        return;
      }
      case 'RECEIPTS':
        // 13.12.6: the link's own ask, not a command, for the asking connector's own ids.
        if (!s.paired) reply(false, NOT_PAIRED);
        else if (!s.linked || !end.can.includes('receipts')) reply(false, 'RECEIPTS \u2014 this node keeps no receipts: its verdict lists no receipts');
        else reply(true, JSON.stringify(receiptsAnswer(end, conn.peer, arg.split(' ').filter(x => x.length > 0))));
        return;
      default:
        if (!s.paired) reply(false, NOT_PAIRED);
        else if (id) command(end, conn, id, { line: body }, out);
        else reply(true, '');
    }
  }, COMMAND_MAX, oversize);

  function link(body, reply) {
    const w = body.split(' ');
    if (s.proved || s.paired) {
      reply(false, 'LINK malformed \u2014 one LINK a connection');
      return;
    }
    if (w[1] === 'PROOF') {
      if (s.ask === null || w.length !== 3) {
        reply(false, 'LINK malformed \u2014 LINK PROOF answers this node\'s offer');
        return;
      }
      s.proved = true;
      const cred = credFor(end, s.ask.kid);
      const peer = `${s.ask.t}:${s.ask.id}`;
      const T = transcript({ carrier: 'line', offer: s.ask.offer, speaks: VERSION, server: `${end.node.t}:${end.node.id}`, client: peer,
        kid: s.ask.kid, snonce: s.snonce, cnonce: s.ask.cnonce, bind });
      // The acceptor as an attacker answers the proof without checking it: a verdict replayed from a link that verified here before,
      // or one whose proof is the connector's own.
      if (end.attack.verdict === 'reflect' || (end.attack.verdict === 'replay' && end.replay !== null)) {
        reply(true, end.attack.verdict === 'reflect'
          ? `paired ${JSON.stringify({ v: 1, verified: 'key', scope: end.scope, proof: w[2], lid: lidOf(T), ...verdictTail(end) })}`
          : end.replay);
        emit({ ev: 'attack', conn: conn.id, mode: end.attack.verdict });
        return;
      }
      if (!(same(clientProof(cred.K, T), w[2]) && !cred.unknown)) {
        reply(false, `LINK refused \u2014 ${PROOF_WORDS}`);
        emit({ ev: 'refused', conn: conn.id, role: 'acceptor', class: 'refused', kid: s.ask.kid, known: !cred.unknown });
        finish();
        return;
      }
      const a = admit(end, conn, cred, T, peer);
      if (a.binding) {
        reply(false, `LINK binding \u2014 ${a.binding}`);
        emit({ ev: 'refused', conn: conn.id, role: 'acceptor', class: 'binding', kid: s.ask.kid, known: true });
        finish();
        return;
      }
      const verdict = { v: 1, verified: cred.kind, scope: scopeOf(end, cred), proof: serverProof(cred.K, T), lid: lidOf(T), ...verdictTail(end),
        ...(a.enrolled ? { enrolled: a.enrolled } : {}) };
      const text = `paired ${JSON.stringify(verdict)}`;
      if (end.replay === null) end.replay = text;
      reply(true, text);
      s.linked = true;
      conn.peer = peer;
      registerLink(end, conn, cred, words => {
        send(`ERR LINK refused \u2014 ${words}`);
        finish();
      });
      emit({ ev: 'link', conn: conn.id, role: 'acceptor', carrier: 'line', lid: verdict.lid, kid: s.ask.kid, verified: cred.kind,
        peer: { t: s.ask.t, id: s.ask.id }, level: VERSION });
      paired('link');
      return;
    }
    if (s.ask !== null || w.length !== 6 || !isCount(w[1]) || !isKind(w[2]) || !isId(w[3]) || !isHex(w[4], 32) || !isKid(w[5])) {
      reply(false, 'LINK malformed \u2014 LINK <offer> <t> <id> <cnonce> <kid>');
      return;
    }
    const offer = Number(w[1]);
    if (offer < VERSION) {
      reply(false, `LINK version \u2014 this node speaks PeerLink ${VERSION}`);
      return;
    }
    s.ask = { offer, t: w[2], id: w[3], cnonce: w[4], kid: w[5] };
    s.snonce = end.attack.snonce ?? fresh();
    reply(true, `LINK ${VERSION} ${end.node.t} ${end.node.id} ${s.snonce} ${bind}`);
  }
}

function httpAcceptor(end, conn) {
  readHead(conn.sock, (start, headers, rest) => {
    const [method, target] = start.split(' ');
    let url;
    try {
      url = new URL(target ?? '', 'http://peer');
    } catch {
      respond(conn.sock, 400, { ok: false, error: 'a request this node cannot read' });
      return;
    }
    if ((headers.upgrade ?? '').toLowerCase() === 'websocket' && url.pathname === '/ws') {
      upgrade(end, conn, url, headers, rest);
      return;
    }
    if (end.carrier === 'line') {
      respond(conn.sock, 404, { ok: false, error: 'this door speaks the line grammar on its WebSocket alone' });
      return;
    }
    sideCall(end, conn, method, url, headers, rest);
  }, (why, notHttp) => {
    conn.hungUp = true;
    if (notHttp) respond(conn.sock, 400, { ok: false, error: 'a request this node cannot read' });
    else hangUp(conn, 'destroy');
  });
}

function upgrade(end, conn, url, headers, rest) {
  const key = headers['sec-websocket-key'];
  const protocols = (headers['sec-websocket-protocol'] ?? '').split(',').map(p => p.trim()).filter(p => p.length > 0);
  const lines = end.carrier === 'line';
  if (!key || (lines && !protocols.includes(LINES_PROTOCOL))) {
    respond(conn.sock, 400, { ok: false, error: lines ? `this door speaks ${LINES_PROTOCOL}` : 'a WebSocket upgrade names its key' });
    return;
  }
  const accepted = crypto.createHash('sha1').update(key + WS_GUID).digest('base64');
  conn.sock.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accepted}\r\n`
    + (lines ? `Sec-WebSocket-Protocol: ${LINES_PROTOCOL}\r\n` : '') + '\r\n');
  const q = url.searchParams;
  emit({ ev: 'upgrade', conn: conn.id, path: url.pathname, query: { node: q.get('node'), t: q.get('t'), link: q.get('link'), pin: q.has('pin') },
    authorization: authorization(headers.authorization), origin: headers.origin ?? null, host: headers.host ?? null });
  const ws = wsSession(conn, false, rest);
  conn.close = ws.close;
  if (lines) lineAcceptor(end, conn, wsLines(ws));
  else nodeLink(end, conn, ws, q, headers);
}

/** A node link on the `/ws` door: LINK when the address asks for it and this end speaks it (13.6.5), else 1.2's door. */
function nodeLink(end, conn, ws, q, headers) {
  const node = q.get('node');
  const t = q.get('t');
  const asked = q.get('link');
  const bind = conn.tls ? `tls:sha256:${end.mat.fp}` : 'plain';
  let phase = 'public';
  let snonce = null;
  const send = obj => {
    if (!conn.open) return;
    const text = typeof obj === 'string' ? obj : JSON.stringify(obj);
    ws.text(text);
    emit({ ev: 'wrote', conn: conn.id, message: text });
  };
  conn.sendJson = send;
  const out = { receipt: (id, r, final) => send({ type: 'receipt', id, ...r, ...(final ? { final: true } : {}) }), drop: () => ws.close() };
  // 13.10.5: a stuck show is a beat with "live":false; only a state frame makes it live again.
  conn.showStuck = stuck => {
    if (phase === 'linked' || phase === 'bearer') send(stuck ? { type: 'beat', live: false } : frame(end, true));
  };
  if (end.level >= 1 && asked !== null) {
    phase = 'offered';
    snonce = end.attack.snonce ?? fresh();
    send({ type: 'link', v: 1, speaks: VERSION, server: { t: end.node.t, id: end.node.id }, snonce, bind });
  } else {
    const bearer = /^Bearer (.+)$/i.exec(headers.authorization ?? '');
    const cred = bearer ? end.creds.find(c => c.kind === 'key' && same(sha256(utf8(c.raw)).toString('hex'), sha256(utf8(bearer[1])).toString('hex'))) : null;
    if (cred) {
      phase = 'bearer';
      send({ type: 'auth', ok: true, scope: end.scope, node: { t, id: node, verified: 'key' } });
      emit({ ev: 'paired', conn: conn.id, how: 'bearer' });
    } else if (bearer) send({ type: 'auth', ok: false, error: 'unknown or revoked API key', retry: 60 });
    send(frame(end, Boolean(cred)));
  }
  ws.start(text => {
    emit({ ev: 'read', conn: conn.id, message: text });
    let msg = null;
    try {
      msg = JSON.parse(text);
    } catch {
      msg = null;
    }
    if (phase === 'offered') {
      phase = 'answered';
      verdict(msg);
      return;
    }
    if (msg && (msg.type === 'ping' || msg.cmd === 'ping')) {
      const p = pongOf(end, msg.t);
      if (p) send({ type: 'pong', t: p.t, t2: p.t2, t3: p.t3 });
      return;
    }
    if (msg && typeof msg.cmd === 'string' && typeof msg.id === 'string' && (phase === 'linked' || phase === 'bearer' || phase === 'open')) {
      // 13.12.6: the link's own ask, not a command, for the asking connector's own ids.
      if (msg.cmd === 'receipts') {
        if (phase === 'linked' && end.can.includes('receipts') && Array.isArray(msg.args) && msg.args.every(a => typeof a === 'string'))
          send({ type: 'receipt', id: msg.id, ok: true, ...receiptsAnswer(end, conn.peer, msg.args) });
        else send({ type: 'receipt', id: msg.id, ok: false, error: 'this node keeps no receipts: its verdict lists no receipts' });
        return;
      }
      command(end, conn, msg.id, { message: text }, out);
    }
  }, WS_IN_MAX, bytes => {
    // As Stagewatch's /ws does with a message past link.ws.in.bytes: the socket ends (1009).
    emit({ ev: 'oversize', conn: conn.id, bytes });
    ws.close(1009);
  });

  function verdict(msg) {
    const kid = msg && typeof msg.kid === 'string' ? msg.kid : null;
    const fail = (cls, error, known) => {
      phase = cls === 'open' ? 'open' : 'public';
      send(cls === 'open' ? { type: 'auth', ok: false, error, class: cls } : { type: 'auth', ok: false, error, class: cls, retry: 60 });
      emit(cls === 'open' ? { ev: 'open', conn: conn.id, words: error } : { ev: 'refused', conn: conn.id, role: 'acceptor', class: cls, kid, known });
      send(frame(end, false));
    };
    const client = msg && msg.client;
    if (!msg || msg.type !== 'link' || !client || client.t !== t || client.id !== node || !isKind(t) || !isId(node) || !isCount(asked)
      || !isHex(msg.cnonce, 32) || !isKid(kid) || typeof msg.proof !== 'string') {
      fail('malformed', 'a link message names its client as its address does, with its cnonce, kid and proof', false);
      return;
    }
    const offer = Number(asked);
    if (offer < VERSION) {
      fail('version', `this node speaks PeerLink ${VERSION}`, false);
      return;
    }
    // 13.6.7: an open acceptor proves nothing and asks for nothing; its socket shows what it shows everyone.
    if (end.creds.length === 0) {
      fail('open', OPEN_WORDS, false);
      return;
    }
    const cred = credFor(end, kid);
    const peer = `${t}:${node}`;
    const T = transcript({ carrier: 'ws', offer, speaks: VERSION, server: `${end.node.t}:${end.node.id}`, client: peer, kid, snonce, cnonce: msg.cnonce,
      bind });
    // The acceptor as an attacker answers the proof without checking it: a verdict replayed from a link that verified here before,
    // or one whose proof is the connector's own.
    if (end.attack.verdict === 'reflect' || (end.attack.verdict === 'replay' && end.replay !== null)) {
      phase = 'attacked';
      send(end.attack.verdict === 'reflect'
        ? { type: 'auth', ok: true, scope: end.scope, node: { t, id: node, verified: 'key' }, proof: msg.proof, lid: lidOf(T), ...verdictTail(end) }
        : end.replay);
      emit({ ev: 'attack', conn: conn.id, mode: end.attack.verdict });
      return;
    }
    if (!(same(clientProof(cred.K, T), msg.proof) && !cred.unknown)) {
      fail('refused', PROOF_WORDS, !cred.unknown);
      return;
    }
    const a = admit(end, conn, cred, T, peer);
    if (a.binding) {
      fail('binding', a.binding, true);
      return;
    }
    const lid = lidOf(T);
    end.sessions.set(lid, { S: sessionOf(cred.K, T), conn: conn.id });
    phase = 'linked';
    conn.peer = peer;
    const auth = JSON.stringify({ type: 'auth', ok: true, scope: scopeOf(end, cred), node: { t, id: node, verified: cred.kind }, proof: serverProof(cred.K, T),
      lid, ...verdictTail(end), ...(a.enrolled ? { enrolled: a.enrolled } : {}) });
    if (end.replay === null) end.replay = auth;
    send(auth);
    // 13.5.5: a revocation refuses the live link at once; the socket stays a public viewer, as after any refused verdict (13.6.5).
    registerLink(end, conn, cred, words => {
      phase = 'public';
      conn.peer = null;
      send({ type: 'auth', ok: false, error: words, class: 'refused', retry: 60 });
      send(frame(end, false));
    });
    emit({ ev: 'link', conn: conn.id, role: 'acceptor', carrier: 'ws', lid, kid, verified: cred.kind, peer: { t, id: node }, level: VERSION });
    send(frame(end, true));
  }
}

/**
 * A side call (13.7): taken with the S of a live, verified link its lid names; anything else is 401 with relink. A right S that came
 * in clear from another machine is refused, and its link ended (13.7.1): it crossed the network readable.
 */
function sideCall(end, conn, method, url, headers, rest) {
  const length = Number(headers['content-length'] ?? '0');
  if (!Number.isInteger(length) || length < 0 || length > BODY_MAX) {
    respond(conn.sock, 413, { ok: false, error: `a body is at most ${BODY_MAX} bytes` });
    return;
  }
  let got = rest.length;
  const done = () => {
    const kind = authorization(headers.authorization);
    let status = 401;
    let ended = null;
    let body = { ok: false, error: 'this call carries no session of a live link \u2014 link again', relink: true };
    const m = /^ShowBus lid=([0-9a-f]{16}), s=([0-9a-f]{64})$/.exec(headers.authorization ?? '');
    if (m) {
      const session = end.sessions.get(m[1]);
      if (session && same(session.S, m[2]) && !conn.tls && !isLoopbackAddress(conn.remote)) {
        end.sessions.delete(m[1]);
        end.links.delete(session.conn);
        ended = session.conn;
        body = { ok: false, error: 'a session credential came in clear from another machine \u2014 its link is ended; link again', relink: true };
        const linked = conns.get(session.conn);
        if (linked?.open) (linked.close ?? (() => hangUp(linked)))();
      } else if (session && same(session.S, m[2])) {
        status = 200;
        body = { ok: true, path: url.pathname };
      } else body = { ok: false, error: 'the link ended', relink: true };
    }
    respond(conn.sock, status, body);
    emit({ ev: 'served', conn: conn.id, method, path: url.pathname, status, authorization: kind, origin: headers.origin ?? null, host: headers.host ?? null,
      ...(ended !== null ? { ended } : {}) });
  };
  if (got >= length) {
    done();
    return;
  }
  const onData = chunk => {
    got += chunk.length;
    if (got >= length) {
      conn.sock.removeListener('data', onData);
      done();
    }
  };
  conn.sock.on('data', onData);
  conn.sock.resume();
}

// ------------------------------------------------------------------ the connector

// What this connector enrolled (13.5.7), by the node it enrolled with (t:id): the key both ends derived and the code's K it came from.
// Kept in this process alone, never written out.
const enrolments = new Map();

function dial(cmd) {
  if (!['line', 'ws'].includes(cmd.carrier)) throw new Refusal('dial: carrier is line or ws');
  const transport = cmd.transport ?? 'tcp';
  if (!['tcp', 'ws'].includes(transport) || (cmd.carrier === 'ws' && transport !== 'tcp')) throw new Refusal('dial: transport is tcp, or ws for a line carrier');
  const host = cmd.host ?? '127.0.0.1';
  const port = cmd.port;
  if (typeof host !== 'string' || !Number.isInteger(port) || port < 1 || port > 65535) throw new Refusal('dial: host and port');
  if (typeof cmd.tls !== 'boolean') throw new Refusal('dial: tls is true or false');
  const pin = cmd.pin ?? null;
  if (pin !== null && !isHex(pin, 64)) throw new Refusal('dial: pin is 64 lowercase hex, or null');
  const me = cmd.node;
  if (!me || !isKind(me.t) || !isId(me.id)) throw new Refusal('dial: node is {"t","id"} in \u00a72\'s shapes');
  const meant = cmd.meant ?? null;
  if (meant !== null && (!isKind(meant.t) || !isId(meant.id))) throw new Refusal('dial: meant is {"t","id"}, or null');
  let cred = cmd.key === undefined || cmd.key === null ? null : credential(cmd.key, 'dial key');
  const name = cmd.name ?? 'PeerLink peer';
  if (typeof name !== 'string' || !/^[^\r\n]{1,64}$/.test(name)) throw new Refusal('dial: name is one line of at most 64 characters');
  const legacy = cmd.legacy ?? false;
  if (typeof legacy !== 'boolean') throw new Refusal('dial: legacy is true or false');
  const attack = attackOf(cmd, { kid: isKid, replay: v => Number.isInteger(v) && v > 0, role: isTrue, twice: isTrue, unpaired: isTrue,
    reads: v => v === false, origin: isHeader, host: isHeader });
  if (cred === null && (attack.kid || attack.replay || attack.role || attack.twice))
    throw new Refusal('dial: an attack on LINK names the key file it proves, or reads the verdict, with');
  // A replayed LINK is the one a connection of this peer's sent, as it was: its carrier, transport and node too.
  const was = attack.replay ? conns.get(attack.replay) : null;
  if (attack.replay && (!was || was.role !== 'connector' || !was.sentLink))
    throw new Refusal('dial: replay names a connection this peer dialled that sent its LINK');
  if (was && (was.sentLink.carrier !== cmd.carrier || was.sentLink.transport !== transport || was.sentLink.node !== `${me.t}:${me.id}`))
    throw new Refusal('dial: a replayed LINK goes as it was sent: on its carrier and transport, from its node');
  const replay = was ? was.sentLink : null;
  // 13.5.7: a code that enrolled with the node it means is proved, from then on, with the key it enrolled.
  const enrolled = cred?.kind === 'code' && meant ? enrolments.get(`${meant.t}:${meant.id}`) : null;
  if (enrolled && enrolled.from.equals(cred.K)) cred = { kind: 'link', K: enrolled.K, kid: enrolled.kid, raw: null };
  const conn = connection({ end: null, role: 'connector', carrier: cmd.carrier, tls: cmd.tls, host, port, shown: null, linked: false });
  const sock = cmd.tls
    ? tls.connect({ host, port, rejectUnauthorized: false, checkServerIdentity: () => undefined, ...(net.isIP(host) ? {} : { servername: host }) })
    : net.connect({ host, port });
  conn.sock = sock;
  let up = false;
  sock.on('error', e => {
    if (!up) emit({ ev: 'error', re: cmd.tag, conn: conn.id, words: `dial: ${e.message}` });
  });
  sock.on('close', () => closed(conn));
  sock.once(cmd.tls ? 'secureConnect' : 'connect', () => {
    up = true;
    conn.shown = cmd.tls ? fingerprint(sock.getPeerCertificate()) : null;
    conn.remote = sock.remoteAddress;
    emit({ ev: 'connected', re: cmd.tag, conn: conn.id, role: 'connector', tls: cmd.tls, fingerprint: conn.shown, remote: conn.remote });
    // A flood's connection: up, and nothing more - no HELLO, no upgrade, no LINK.
    if (attack.unpaired) {
      emit({ ev: 'attack', conn: conn.id, mode: 'unpaired' });
      sock.on('data', () => {});
      return;
    }
    if (pin !== null && conn.shown !== pin) {
      emit({ ev: 'refused', conn: conn.id, role: 'connector', class: 'certificate',
        words: `CERTIFICATE CHANGED: it now shows ${short(conn.shown)} \u2026 \u2014 nothing sent` });
      hangUp(conn, 'destroy');
      return;
    }
    // 13.3.2: off this machine a ws link carries a credential only inside TLS. Local is the socket's remote address (13.1).
    if (cmd.carrier === 'ws' && !cmd.tls && cred !== null && !isLoopbackAddress(conn.remote)) {
      emit({ ev: 'refused', conn: conn.id, role: 'connector', class: 'no-tls',
        words: `${host} offers no TLS on this dial \u2014 the key stays on this machine; nothing sent` });
      hangUp(conn, 'destroy');
      return;
    }
    const opts = { me, meant, cred, name, legacy, attack, replay, transport };
    if (cmd.carrier === 'line' && transport === 'tcp') {
      lineConnector(conn, tcpLines(conn), opts);
      return;
    }
    const target = cmd.carrier === 'ws' ? `/ws?node=${me.id}&t=${me.t}${cred ? `&link=${VERSION}` : ''}` : '/ws';
    wsConnect(conn, target, cmd.carrier === 'line' ? LINES_PROTOCOL : null, attack, ws => {
      conn.close = ws.close;
      if (cmd.carrier === 'line') lineConnector(conn, wsLines(ws), opts);
      else nodeConnector(conn, ws, opts);
    });
  });
}

function wsConnect(conn, target, protocol, attack, done) {
  const key = crypto.randomBytes(16).toString('base64');
  const head = [`GET ${target} HTTP/1.1`, `Host: ${attack.host ?? `${conn.host}:${conn.port}`}`, 'Upgrade: websocket', 'Connection: Upgrade',
    `Sec-WebSocket-Key: ${key}`, 'Sec-WebSocket-Version: 13'];
  if (attack.origin) head.push(`Origin: ${attack.origin}`);
  if (protocol) head.push(`Sec-WebSocket-Protocol: ${protocol}`);
  conn.sock.write(head.join('\r\n') + '\r\n\r\n');
  emit({ ev: 'wrote', conn: conn.id, upgrade: target });
  readHead(conn.sock, (start, headers, rest) => {
    const want = crypto.createHash('sha1').update(key + WS_GUID).digest('base64');
    if (start.split(' ')[1] !== '101' || headers['sec-websocket-accept'] !== want) {
      emit({ ev: 'refused', conn: conn.id, role: 'connector', class: 'malformed', words: `the upgrade was answered ${start}` });
      hangUp(conn, 'destroy');
      return;
    }
    done(wsSession(conn, true, rest));
  }, why => {
    emit({ ev: 'refused', conn: conn.id, role: 'connector', class: 'malformed', words: why });
    hangUp(conn, 'destroy');
  });
}

/**
 * The proof a connector writes for T: its client proof, or as an attacker the one a replayed LINK carried or its server proof in the
 * client's place.
 */
const proofFor = (cred, T, attack, replay) => replay?.proof ?? (attack.role ? serverProof(cred.K, T) : clientProof(cred.K, T));

/** What an attack on LINK is called in its event: a replayed LINK, a server proof as a client's, or a forged kid; null for none. */
const linkAttack = attack => (attack.replay ? 'replay' : attack.role ? 'role' : attack.kid ? 'kid' : null);

/**
 * A verdict that names `enrolled` (13.5.7): over TLS, with a code, the connector derives the same key and keeps it for the node, to
 * prove with from then on. Over plain it enrols nothing.
 */
function takeEnrolment(conn, cred, T, bind, server, kid) {
  if (typeof kid !== 'string' || cred.kind !== 'code' || bind === 'plain') return;
  const K = enrolK(cred.K, T);
  const derived = kidOf(K) === kid;
  if (derived) enrolments.set(server, { from: cred.K, K, kid });
  emit({ ev: 'enrolled', conn: conn.id, role: 'connector', kid, derived });
}

/** A slow reader (`reads` false): once linked it reads nothing more, so what the acceptor sends backs up on the wire. */
function deafen(conn, attack) {
  if (attack.reads !== false) return;
  conn.deaf = true;
  conn.sock.pause();
  emit({ ev: 'attack', conn: conn.id, mode: 'reads' });
}

function lineConnector(conn, io, { me, meant, cred, name, legacy, attack, replay, transport }) {
  const s = { n: 0, cnonce: replay?.cnonce ?? fresh(), offerId: null, proofId: null, proof: null, legacyId: null, T: null, nonce: null, hello: true,
    server: null, again: false };
  const bind = conn.tls ? `tls:sha256:${conn.shown}` : 'plain';
  const kid = cred === null ? null : replay?.kid ?? attack.kid ?? cred.kid;
  const send = line => {
    if (!conn.open) return;
    io.send(line);
    emit({ ev: 'wrote', conn: conn.id, line });
  };
  conn.write = send;
  if (!conn.close) conn.close = io.close;
  const end = (cls, words) => {
    emit({ ev: 'refused', conn: conn.id, role: 'connector', class: cls, words });
    io.close();
  };
  // An attacker that proves twice: once its verdict came, the same proof again on the same connection, under a new id.
  const twice = () => {
    if (!attack.twice || s.again || s.proof === null) return;
    s.again = true;
    s.proofId = `l${++s.n}`;
    emit({ ev: 'attack', conn: conn.id, mode: 'twice' });
    send(`@${s.proofId} LINK PROOF ${s.proof}`);
  };
  const err = words => {
    if (words.startsWith('LINK ')) {
      const cls = words.slice(5).split(' ')[0];
      const said = words.slice(5 + cls.length).replace(/^ \u2014 /, '');
      // 13.6.7: `open` refuses nothing; the acceptor asks for no key.
      emit(cls === 'open' ? { ev: 'open', conn: conn.id, words: said } : { ev: 'refused', conn: conn.id, role: 'connector', class: cls, words: said });
      return;
    }
    // An acceptor that does not speak LINK (13.6.8).
    emit({ ev: 'level0', conn: conn.id, words });
    if (legacy && cred && cred.kind === 'code' && isHex(s.nonce, 32)) {
      const proof = legacyProof(cred.raw, s.nonce, name);
      if (proof !== null) {
        s.legacyId = `pa${++s.n}`;
        send(`@${s.legacyId} AUTH-PROOF ${proof}`);
      }
    }
  };
  io.start(line => {
    if (conn.deaf) return;
    emit({ ev: 'read', conn: conn.id, line });
    if (line.startsWith('NONCE ')) {
      s.nonce = line.slice(6);
      return;
    }
    if (line === 'OK' && s.hello) {
      s.hello = false;
      return;
    }
    // 13.5.5: a live link refused - its key revoked, its code replaced - with no id, as the acceptor ends it.
    const live = /^ERR LINK (\S+)(?: \u2014 (.*))?$/.exec(line);
    if (live && conn.linked) {
      conn.linked = false;
      emit({ ev: 'refused', conn: conn.id, role: 'connector', class: live[1], words: live[2] ?? '', live: true });
      return;
    }
    const m = /^(OK|ERR) @(\S+)(?: (.*))?$/.exec(line);
    if (!m) return;
    const [, verdict, id, rest = ''] = m;
    if (id === s.offerId) {
      s.offerId = null;
      if (verdict === 'ERR') {
        err(rest);
        return;
      }
      const w = rest.split(' ');
      if (w.length !== 6 || w[0] !== 'LINK' || !isCount(w[1]) || !isKind(w[2]) || !isId(w[3]) || !isHex(w[4], 32) || !isBind(w[5])) {
        end('malformed', 'an answer to LINK this peer cannot read');
        return;
      }
      if (Number(w[1]) !== VERSION) {
        end('version', `it speaks PeerLink ${w[1]}`);
        return;
      }
      if (meant && (w[2] !== meant.t || w[3] !== meant.id)) {
        end('other-id', `${w[2]} ${w[3]} answers \u2014 not ${meant.t} ${meant.id}`);
        return;
      }
      if (w[5] !== bind) {
        end('bind', PROOF_WORDS);
        return;
      }
      s.server = `${w[2]}:${w[3]}`;
      s.T = transcript({ carrier: 'line', offer: VERSION, speaks: VERSION, server: s.server, client: `${me.t}:${me.id}`, kid, snonce: w[4],
        cnonce: s.cnonce, bind });
      s.proofId = `l${++s.n}`;
      s.proof = proofFor(cred, s.T, attack, replay);
      if (linkAttack(attack)) emit({ ev: 'attack', conn: conn.id, mode: linkAttack(attack) });
      send(`@${s.proofId} LINK PROOF ${s.proof}`);
      conn.sentLink = { carrier: 'line', transport, node: `${me.t}:${me.id}`, cnonce: s.cnonce, kid, proof: s.proof };
      return;
    }
    if (id === s.proofId) {
      s.proofId = null;
      if (verdict === 'ERR') {
        err(rest);
        twice();
        return;
      }
      let v = null;
      try {
        v = rest.startsWith('paired ') ? JSON.parse(rest.slice(7)) : null;
      } catch {
        v = null;
      }
      if (conn.linked) {
        twice();
        return;
      }
      if (!v || !same(serverProof(cred.K, s.T), v.proof)) {
        end('proof', PROOF_WORDS);
        return;
      }
      conn.linked = true;
      conn.lid = lidOf(s.T);
      emit({ ev: 'link', conn: conn.id, role: 'connector', carrier: 'line', lid: conn.lid, kid, verified: v.verified,
        scope: v.scope, level: v.level, can: v.can, inst: v.inst });
      takeEnrolment(conn, cred, s.T, bind, s.server, v.enrolled);
      twice();
      deafen(conn, attack);
      return;
    }
    if (id === s.legacyId) {
      s.legacyId = null;
      if (verdict === 'OK') emit({ ev: 'paired', conn: conn.id, how: 'auth-proof' });
    }
  }, READ_MAX, () => emit({ ev: 'oversize', conn: conn.id, bytes: READ_MAX + 1 }));
  send(`HELLO ${name}`);
  if (cred === null) return;
  s.offerId = `l${++s.n}`;
  send(`@${s.offerId} LINK ${VERSION} ${me.t} ${me.id} ${s.cnonce} ${kid}`);
}

function nodeConnector(conn, ws, { me, meant, cred, attack, replay, transport }) {
  const s = { first: true, cnonce: replay?.cnonce ?? fresh(), T: null, awaiting: false, server: null, message: null, again: false };
  const bind = conn.tls ? `tls:sha256:${conn.shown}` : 'plain';
  const kid = cred === null ? null : replay?.kid ?? attack.kid ?? cred.kid;
  const send = obj => {
    if (!conn.open) return;
    const text = typeof obj === 'string' ? obj : JSON.stringify(obj);
    ws.text(text);
    emit({ ev: 'wrote', conn: conn.id, message: text });
  };
  conn.sendJson = send;
  const end = (cls, words) => {
    emit({ ev: 'refused', conn: conn.id, role: 'connector', class: cls, words });
    ws.close();
  };
  // An attacker that proves twice: once its verdict came, the same link message again on the same socket.
  const twice = () => {
    if (!attack.twice || s.again || s.message === null) return;
    s.again = true;
    emit({ ev: 'attack', conn: conn.id, mode: 'twice' });
    send(s.message);
  };
  ws.start(text => {
    if (conn.deaf) return;
    emit({ ev: 'read', conn: conn.id, message: text });
    let msg = null;
    try {
      msg = JSON.parse(text);
    } catch {
      msg = null;
    }
    if (s.first) {
      s.first = false;
      if (!msg || msg.type !== 'link') {
        if (cred !== null) emit({ ev: 'level0', conn: conn.id, words: 'its first message is not link' });
        return;
      }
      const server = msg.server ?? {};
      if (msg.speaks !== VERSION || !isKind(server.t) || !isId(server.id) || !isHex(msg.snonce, 32) || !isBind(msg.bind)) {
        end('malformed', 'a link offer this peer cannot read');
        return;
      }
      if (meant && (server.t !== meant.t || server.id !== meant.id)) {
        end('other-id', `${server.t} ${server.id} answers \u2014 not ${meant.t} ${meant.id}`);
        return;
      }
      if (cred === null) return;
      if (msg.bind !== bind) {
        end('bind', PROOF_WORDS);
        return;
      }
      s.server = `${server.t}:${server.id}`;
      s.T = transcript({ carrier: 'ws', offer: VERSION, speaks: msg.speaks, server: s.server, client: `${me.t}:${me.id}`, kid, snonce: msg.snonce,
        cnonce: s.cnonce, bind });
      s.awaiting = true;
      if (linkAttack(attack)) emit({ ev: 'attack', conn: conn.id, mode: linkAttack(attack) });
      const proof = proofFor(cred, s.T, attack, replay);
      s.message = JSON.stringify({ type: 'link', client: { t: me.t, id: me.id }, cnonce: s.cnonce, kid, proof });
      send(s.message);
      conn.sentLink = { carrier: 'ws', transport, node: `${me.t}:${me.id}`, cnonce: s.cnonce, kid, proof };
      return;
    }
    if (!msg || msg.type !== 'auth') return;
    // 13.5.5: a live link refused - its key revoked - ends its standing and its S; the socket stays a public viewer.
    if (conn.linked) {
      if (msg.ok !== false) return;
      conn.linked = false;
      emit({ ev: 'refused', conn: conn.id, role: 'connector', class: msg.class ?? 'refused', words: msg.error ?? '', retry: msg.retry ?? null, live: true });
      return;
    }
    if (!s.awaiting) {
      end('proof', PROOF_WORDS);
      return;
    }
    s.awaiting = false;
    if (msg.ok !== true) {
      // The socket stays a public viewer (13.6.5); `open` refuses nothing (13.6.7).
      if (msg.class === 'open') emit({ ev: 'open', conn: conn.id, words: msg.error ?? '' });
      else emit({ ev: 'refused', conn: conn.id, role: 'connector', class: msg.class ?? 'refused', words: msg.error ?? '', retry: msg.retry ?? null });
      twice();
      return;
    }
    if (!same(serverProof(cred.K, s.T), msg.proof)) {
      end('proof', PROOF_WORDS);
      return;
    }
    conn.linked = true;
    conn.lid = lidOf(s.T);
    conn.session = sessionOf(cred.K, s.T);
    emit({ ev: 'link', conn: conn.id, role: 'connector', carrier: 'ws', lid: conn.lid, kid, verified: msg.node?.verified ?? null,
      scope: msg.scope, level: msg.level, can: msg.can, inst: msg.inst });
    takeEnrolment(conn, cred, s.T, bind, s.server, msg.enrolled);
    twice();
    deafen(conn, attack);
  }, READ_MAX, bytes => emit({ ev: 'oversize', conn: conn.id, bytes }));
}

/**
 * A side call on a verified ws link this peer dialled: its S only inside the TLS that link pinned, or to a socket whose remote address
 * is loopback (13.7.1) - the link's, and the call's own before anything is written. As an attacker it carries S in clear whatever
 * the address, names another lid, or calls with the S of a link that has ended.
 */
function call(cmd) {
  const conn = conns.get(cmd.conn);
  if (!conn || conn.role !== 'connector' || conn.carrier !== 'ws') throw new Refusal('call: conn is a ws link this peer dialled');
  const attack = attackOf(cmd, { clear: isTrue, lid: v => isHex(v, 16), closed: isTrue, origin: isHeader, host: isHeader });
  if (!conn.session) throw new Refusal('call: the link never verified \u2014 no session to carry');
  if (!(conn.open && conn.linked) && !attack.closed) throw new Refusal('call: the link is not live and verified \u2014 no session to carry');
  const clear = attack.clear === true;
  if (!clear && !conn.tls && !isLoopbackAddress(conn.remote)) throw new Refusal('call: S rides only inside pinned TLS or to this machine');
  const method = cmd.method ?? 'GET';
  const path = cmd.path ?? '/api/context';
  if (typeof method !== 'string' || !/^[A-Z]{3,7}$/.test(method) || typeof path !== 'string' || !path.startsWith('/') || /[\s]/.test(path))
    throw new Refusal('call: method and path');
  const body = cmd.body === undefined ? '' : typeof cmd.body === 'string' ? cmd.body : JSON.stringify(cmd.body);
  const secure = conn.tls && !clear;
  const sock = secure
    ? tls.connect({ host: conn.host, port: conn.port, rejectUnauthorized: false, checkServerIdentity: () => undefined })
    : net.connect({ host: conn.host, port: conn.port });
  let answered = false;
  const answer = e => {
    if (answered) return;
    answered = true;
    emit({ re: cmd.tag, conn: conn.id, ...e });
  };
  sock.on('error', e => answer({ ev: 'error', words: `call: ${e.message}` }));
  sock.once(secure ? 'secureConnect' : 'connect', () => {
    if (secure && fingerprint(sock.getPeerCertificate()) !== conn.shown) {
      sock.destroy();
      answer({ ev: 'error', words: 'call: another certificate than the link pinned \u2014 nothing sent' });
      return;
    }
    if (!secure && !clear && !isLoopbackAddress(sock.remoteAddress)) {
      sock.destroy();
      answer({ ev: 'error', words: 'call: this socket is not to this machine and carries no TLS \u2014 nothing sent' });
      return;
    }
    if (clear || attack.lid || attack.closed) emit({ ev: 'attack', conn: conn.id, mode: clear ? 'clear' : attack.lid ? 'lid' : 'closed' });
    sock.write(`${method} ${path} HTTP/1.1\r\nHost: ${attack.host ?? `${conn.host}:${conn.port}`}\r\n${attack.origin ? `Origin: ${attack.origin}\r\n` : ''}`
      + `Authorization: ShowBus lid=${attack.lid ?? conn.lid}, s=${conn.session}\r\n`
      + `Content-Type: application/json\r\nContent-Length: ${Buffer.byteLength(body)}\r\nConnection: close\r\n\r\n${body}`);
    readHead(sock, (start, headers, rest) => {
      const chunks = [rest];
      let size = rest.length;
      sock.on('data', c => {
        size += c.length;
        if (size <= BODY_MAX) chunks.push(c);
        else {
          sock.destroy();
          answer({ ev: 'error', words: `call: an answer longer than ${BODY_MAX} bytes` });
        }
      });
      sock.on('end', () => answer({ ev: 'answer', status: Number(start.split(' ')[1]), retryAfter: headers['retry-after'] ?? null,
        body: Buffer.concat(chunks).toString('utf8') }));
    }, why => answer({ ev: 'error', words: `call: ${why}` }));
  });
}

// ------------------------------------------------------------------ the host's other commands

function send(cmd) {
  const conn = conns.get(cmd.conn);
  if (!conn || !conn.open) throw new Refusal('send: conn is an open connection');
  if (typeof cmd.line === 'string') {
    if (!conn.write || /[\r\n]/.test(cmd.line)) throw new Refusal('send: line goes on a line carrier, one line');
    conn.write(cmd.line);
  } else if (cmd.message !== undefined) {
    if (!conn.sendJson) throw new Refusal('send: message goes on a ws carrier');
    conn.sendJson(cmd.message);
  } else throw new Refusal('send: line or message');
  emit({ ev: 'sent', re: cmd.tag, conn: conn.id });
}

/**
 * What an acceptor does from now (the head of this file says each field). Every field is checked before any is applied: a set that is
 * refused changes nothing.
 */
function set(cmd) {
  const end = ends.get(cmd.end ?? 'a');
  if (!end) throw new Refusal('set: end is an open end');
  if (cmd.clock !== undefined && !Number.isSafeInteger(cmd.clock)) throw new Refusal('set: clock is a whole number of ms');
  if (cmd.pongs !== undefined && !PONGS.includes(cmd.pongs)) throw new Refusal('set: pongs is true, false, "unmatched" or "backwards"');
  if (cmd.answer !== undefined && !ANSWERS.includes(cmd.answer)) throw new Refusal(`set: answer is ${ANSWERS.join(', ')}`);
  if (cmd.final !== undefined && (!['applied', 'refused'].includes(cmd.final) || !end.can.includes('final')))
    throw new Refusal('set: final is applied or refused, on an end whose verdict lists final');
  if (cmd.late !== undefined && (!Number.isSafeInteger(cmd.late) || cmd.late < 0)) throw new Refusal('set: late is a whole number of ms');
  if (cmd.stuck !== undefined && typeof cmd.stuck !== 'boolean') throw new Refusal('set: stuck is true or false');
  if (cmd.revoke !== undefined && (!isHex(cmd.revoke, 16) || !end.creds.some(c => c.kid === cmd.revoke)))
    throw new Refusal('set: revoke names the kid of a key this end holds');
  const code = cmd.code === undefined ? null : credential(cmd.code, 'set code');
  if (code !== null && code.kind !== 'code') throw new Refusal('set: code is a code file ({"file","kind":"code"})');
  if (cmd.clock !== undefined) end.clock = cmd.clock;
  if (cmd.pongs !== undefined) end.pongs = cmd.pongs;
  if (cmd.answer !== undefined) end.answer = cmd.answer;
  if (cmd.late !== undefined) end.late = cmd.late;
  if (cmd.stuck !== undefined && cmd.stuck !== end.stuck) {
    end.stuck = cmd.stuck;
    for (const c of conns.values()) if (c.end === end.name && c.open && c.showStuck) c.showStuck(cmd.stuck);
  }
  if (cmd.final !== undefined) {
    // 13.12.4: one follow-up for each command left accepted, its final receipt kept for repeats and RECEIPTS.
    const r = cmd.final === 'applied' ? { ok: true, stage: 'applied', effect: 'ran' } : { ok: false, error: 'the scripted peer refuses it' };
    for (const [k, p] of [...end.pending]) {
      end.pending.delete(k);
      p.keep(r);
      if (p.conn.open) p.out.receipt(p.id, r, true);
    }
  }
  if (cmd.revoke !== undefined) {
    end.creds = end.creds.filter(c => c.kid !== cmd.revoke);
    revokeLinks(end, c => c.kid === cmd.revoke, REVOKED_WORDS);
  }
  if (code !== null) {
    // Patterns' NEW TOKEN (13.5.5): the old code, and every key enrolled from it, stop at once.
    end.creds = [...end.creds.filter(c => c.kind !== 'code' && !c.enrolled), code];
    revokeLinks(end, c => c.kind === 'code' || c.enrolled === true, REPLACED_WORDS);
  }
  emit({ ev: 'set', re: cmd.tag, end: end.name });
}

function bounded(cmd, field, fallback) {
  const ms = cmd[field] ?? fallback;
  if (!Number.isInteger(ms) || ms < 0) throw new Refusal(`${cmd.do}: ${field} is a whole number of ms`);
  return Math.min(ms, WAIT_MAX_MS);
}

function expect(cmd) {
  const within = bounded(cmd, 'within', WAIT_MAX_MS);
  const after = cmd.after ?? 0;
  const match = cmd.match ?? {};
  if (!Number.isInteger(after) || match === null || typeof match !== 'object') throw new Refusal('expect: match and after');
  const test = e => e.n > after && !isAnswer(e) && holds(e, match);
  const found = log.find(test);
  if (found) {
    emit({ ev: 'expect', re: cmd.tag, ok: true, got: found });
    return;
  }
  const w = e => {
    if (!test(e)) return;
    waiters.delete(w);
    clearTimeout(timer);
    emit({ ev: 'expect', re: cmd.tag, ok: true, got: e });
  };
  const timer = setTimeout(() => {
    waiters.delete(w);
    emit({ ev: 'expect', re: cmd.tag, ok: false, got: null });
  }, within);
  waiters.add(w);
}

function silence(cmd) {
  const ms = bounded(cmd, 'ms', 500);
  const after = cmd.after ?? seq;
  const match = cmd.match ?? { ev: 'read' };
  if (!Number.isInteger(after) || match === null || typeof match !== 'object') throw new Refusal('silence: match and after');
  let hit = log.find(e => e.n > after && !isAnswer(e) && holds(e, match)) ?? null;
  const w = e => {
    if (hit === null && !isAnswer(e) && holds(e, match)) hit = e;
  };
  waiters.add(w);
  setTimeout(() => {
    waiters.delete(w);
    emit({ ev: 'silence', re: cmd.tag, ok: hit === null, got: hit });
  }, ms);
}

function close(cmd) {
  if (cmd.conn !== undefined) {
    const conn = conns.get(cmd.conn);
    if (!conn) throw new Refusal('close: conn is a connection');
    if (conn.open) (conn.close ?? (() => hangUp(conn)))();
  } else {
    const end = ends.get(cmd.end ?? '');
    if (!end) throw new Refusal('close: conn or end');
    end.server.close();
    ends.delete(end.name);
    for (const c of conns.values()) if (c.end === end.name && c.open) hangUp(c, 'destroy');
  }
  emit({ ev: 'close', re: cmd.tag });
}

function quit(cmd) {
  for (const end of ends.values()) end.server.close();
  for (const c of conns.values()) if (c.open) c.sock.destroy();
  input.close();
  const e = { ev: 'done', n: ++seq, re: cmd?.tag };
  process.stdout.write(JSON.stringify(e) + '\n', () => process.exit(0));
}

const commands = { listen, dial, send, call, set, expect, silence, close, quit };

// Each command's fields; any other is refused.
const FIELDS = {
  listen: ['end', 'carrier', 'transport', 'tls', 'cert', 'certKey', 'port', 'host', 'node', 'keys', 'level', 'can', 'stuckMs', 'expireMs', 'inst',
    'scope', 'greet', 'clock', 'attack'],
  dial: ['carrier', 'transport', 'host', 'port', 'tls', 'pin', 'node', 'key', 'meant', 'name', 'legacy', 'attack'],
  send: ['conn', 'line', 'message'],
  call: ['conn', 'method', 'path', 'body', 'attack'],
  set: ['end', 'clock', 'pongs', 'answer', 'final', 'late', 'stuck', 'revoke', 'code'],
  expect: ['match', 'after', 'within'],
  silence: ['match', 'after', 'ms'],
  close: ['conn', 'end'],
  quit: [],
};

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on('line', text => {
  if (text.trim().length === 0) return;
  let cmd = null;
  try {
    cmd = JSON.parse(text);
  } catch {
    emit({ ev: 'error', words: 'a command is one JSON object a line' });
    return;
  }
  if (cmd === null || typeof cmd !== 'object' || typeof cmd.do !== 'string' || !Object.hasOwn(commands, cmd.do)) {
    emit({ ev: 'error', re: cmd?.tag, words: `unknown command: ${cmd?.do} (${Object.keys(commands).join(', ')})` });
    return;
  }
  const unknown = Object.keys(cmd).filter(k => k !== 'do' && k !== 'tag' && !FIELDS[cmd.do].includes(k));
  if (unknown.length > 0) {
    emit({ ev: 'error', re: cmd.tag, words: `${cmd.do}: unknown field ${unknown.join(', ')}` });
    return;
  }
  try {
    commands[cmd.do](cmd);
  } catch (e) {
    emit({ ev: 'error', re: cmd.tag, words: e instanceof Refusal ? e.message : `the peer faulted: ${e.message}` });
  }
});
input.on('close', () => {
  for (const end of ends.values()) end.server.close();
  for (const c of conns.values()) if (c.open) c.sock.destroy();
});

emit({ ev: 'ready', peer: 'peerlink-peer', peerlink: VERSION, node: process.versions.node });
