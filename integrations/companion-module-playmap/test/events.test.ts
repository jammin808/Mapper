// Events from the node, for triggers: an EVENT push is taken once, in seq order — event_last_kind, event_seq and event_last (the
// node's own words) follow it — and an event_pulse feedback is true for hold_ms after an event whose kind matches (exactly, or by a
// "fault.*" prefix) and, when it names a ref, one of whose fields is that ref, timed by the node's clock; an event at or below the
// last seq heard is never taken again; after a drop the re-paired link asks EVENTS SINCE the last seq heard and takes what it missed,
// whatever the node pushes meanwhile; an EVENTS overflow is replayed and the subscription opened again, never left a silent gap.
// The hold's end on the node's clock runs on fake timers (the one such test in this file).
import test from 'node:test'
import assert from 'node:assert/strict'
import { isDeepStrictEqual } from 'node:util'
import { FakeNode, SHOW } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'
import { advance, fakeChannels, settle } from './fake-channel.ts'

/** One event as the node's ring writes it: {seq, at, leaderNs, kind, …fields}. */
function eventJson(
	seq: number,
	at: number,
	kind: string,
	fields: Record<string, unknown> = {},
): Record<string, unknown> {
	return { seq, at, leaderNs: seq * 1_000_000, kind, ...fields }
}

/** The EVENT push of one event. */
function eventLine(seq: number, at: number, kind: string, fields: Record<string, unknown> = {}): string {
	return 'EVENT ' + JSON.stringify(eventJson(seq, at, kind, fields))
}

/** Waits until the host holds these feedback values, then asserts them (a timeout shows what it holds instead). */
async function values(m: Booted, expected: Record<string, unknown>, what: string): Promise<void> {
	const held = (): Record<string, unknown> =>
		Object.fromEntries(Object.keys(expected).map((id) => [id, m.host.feedbackValues.get(id)]))
	await m.host.until(() => isDeepStrictEqual(held(), expected), what).catch(() => undefined)
	assert.deepEqual(held(), expected, what)
}

/** Waits until the module has published this last event seq. */
async function seqHeard(m: Booted, seq: number, what: string): Promise<void> {
	await m.host.until(() => m.host.variables.get('event_seq') === seq, what).catch(() => undefined)
	assert.equal(m.host.variables.get('event_seq'), seq, what)
}

/** The seqs the module took, oldest first. */
const taken = (m: Booted): number[] => m.instance.events.recent().map((e) => e.seq)

/** A node whose EVENTS SINCE answers from its ring as the engine does: every event after the seq asked, oldest first. */
function serveRing(node: FakeNode, ring: Record<string, unknown>[], asked: string[], before?: () => void): void {
	node.handlers.set('EVENTS SINCE', (_line, words) => {
		const since = Number(words[2])
		asked.push(words[2] ?? '')
		before?.()
		const events = ring.filter((e) => typeof e.seq === 'number' && e.seq > since)
		const last = Math.max(0, ...ring.map((e) => (typeof e.seq === 'number' ? e.seq : 0)))
		return 'OK ' + JSON.stringify({ seq: last, missed: 0, events })
	})
}

const HOLD = 60000

void test("a pulse ends when hold_ms has passed on the node's clock; a replayed event pulses for what is left of its window", async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', port: 9940 }, {}, { factory, random: () => 0.5 })
	try {
		const ch = opened[0]
		assert.ok(ch)
		ch.events.open({})
		await settle()
		ch.ok('HELLO')
		await settle()
		// The node's clock runs 90 s ahead of this machine's: the first PING's answer says so (no time passes on the round trip).
		const AHEAD = 90_000
		const nodeNow = (): number => Date.now() + AHEAD
		ch.pong(nodeNow())
		await settle()
		await m.place('end', 'event_pulse', { kind: 'item.end', ref: '', hold_ms: 1000 })
		await advance(t.mock.timers, 60, 10)
		assert.equal(m.host.feedbackValues.get('end'), false)

		// Now on the node: true for 1000 ms of the node's clock, then false.
		const t0 = Date.now()
		ch.say(eventLine(7, nodeNow(), 'item.end', { item: 'it00000002' }))
		await advance(t.mock.timers, 60, 10)
		assert.equal(m.host.variables.get('event_seq'), 7)
		assert.equal(m.host.feedbackValues.get('end'), true)
		await advance(t.mock.timers, t0 + 990 - Date.now(), 10)
		assert.equal(m.host.feedbackValues.get('end'), true, 'still on before hold_ms')
		await advance(t.mock.timers, 20, 10)
		assert.equal(m.host.feedbackValues.get('end'), false, 'off once hold_ms has passed')

		// Replayed 700 ms after it happened on the node: on for the 300 ms left of its window.
		const t1 = Date.now()
		ch.say(eventLine(8, nodeNow() - 700, 'item.end', { item: 'it00000003' }))
		await advance(t.mock.timers, 60, 10)
		assert.equal(m.host.variables.get('event_seq'), 8)
		assert.equal(m.host.feedbackValues.get('end'), true)
		await advance(t.mock.timers, t1 + 290 - Date.now(), 10)
		assert.equal(m.host.feedbackValues.get('end'), true, 'on for what is left of the window')
		await advance(t.mock.timers, 20, 10)
		assert.equal(m.host.feedbackValues.get('end'), false, 'off when the window ends')

		// Heard again, as new on the node: an event already taken never pulses twice.
		ch.say(eventLine(8, nodeNow(), 'item.end', { item: 'it00000003' }))
		ch.say(eventLine(6, nodeNow(), 'item.end', { item: 'it00000002' }))
		await advance(t.mock.timers, 100, 10)
		assert.equal(m.host.variables.get('event_seq'), 8)
		assert.equal(m.host.feedbackValues.get('end'), false, 'a seq already heard does not pulse again')
	} finally {
		await m.stop()
	}
})

void test('an EVENT sets event_last_kind, event_seq and event_last, and pulses event_pulse by kind, "fault.*" and ref', async () => {
	const node = await new FakeNode().start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		await node.heard('SUBSCRIBE EVENTS')
		await m.place('end', 'event_pulse', { kind: 'item.end', ref: '', hold_ms: HOLD })
		await m.place('endRed', 'event_pulse', { kind: 'item.end', ref: SHOW.red, hold_ms: HOLD })
		await m.place('endBlue', 'event_pulse', { kind: 'item.end', ref: SHOW.blue, hold_ms: HOLD })
		await m.place('endShow', 'event_pulse', { kind: 'item.end', ref: SHOW.playlist, hold_ms: HOLD })
		await m.place('start', 'event_pulse', { kind: 'item.start', ref: '', hold_ms: HOLD })
		await m.place('faults', 'event_pulse', { kind: 'fault.*', ref: '', hold_ms: HOLD })
		await m.place('lost', 'event_pulse', { kind: 'fault.raised', ref: 'op_1', hold_ms: HOLD })
		await m.place('lostOther', 'event_pulse', { kind: 'fault.raised', ref: 'op_2', hold_ms: HOLD })
		const none = {
			end: false,
			endRed: false,
			endBlue: false,
			endShow: false,
			start: false,
			faults: false,
			lost: false,
			lostOther: false,
		}
		await values(m, none, 'no event heard: no pulse')
		await seqHeard(m, 0, 'no event heard: event_seq 0')
		assert.equal(m.host.variables.get('event_last_kind'), '')
		assert.equal(m.host.variables.get('event_last'), '')

		// An item ends: the kind, its seq and the node's words; pulses of that kind, and of the ids it names.
		node.push(
			eventLine(412, node.now(), 'item.end', {
				playlist: SHOW.playlist,
				item: SHOW.red,
				words: 'Show: Red ended',
			}),
		)
		await seqHeard(m, 412, 'event_seq from the push')
		assert.equal(m.host.variables.get('event_last_kind'), 'item.end')
		assert.equal(m.host.variables.get('event_last'), 'Show: Red ended')
		await values(
			m,
			{ ...none, end: true, endRed: true, endShow: true },
			'item.end pulses its kind, its item and its playlist; not another item, kind or fault',
		)

		// A fault: "fault.*" takes every fault kind; a ref pulses only the event naming it.
		node.push(
			eventLine(413, node.now(), 'fault.raised', {
				fault: 'output.lost',
				ref: 'op_1',
				words: 'Output 1 lost its display',
			}),
		)
		await seqHeard(m, 413, 'event_seq from the fault')
		assert.equal(m.host.variables.get('event_last_kind'), 'fault.raised')
		assert.equal(m.host.variables.get('event_last'), 'Output 1 lost its display')
		await values(
			m,
			{ ...none, end: true, endRed: true, endShow: true, faults: true, lost: true },
			'fault.raised naming op_1 pulses fault.* and fault.raised op_1, not op_2',
		)
		assert.deepEqual(taken(m), [412, 413])
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("a pulse is timed by the node's clock: an event that happened now there pulses, one an hour old there does not", async () => {
	// The node's clock runs an hour ahead of this machine's; the module learns it from the PING exchange, which the node answers
	// before SUBSCRIBE EVENTS on the same link, so it is known before any event arrives.
	const node = await new FakeNode({ clockOffsetMs: 3_600_000 }).start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		await node.heard('SUBSCRIBE EVENTS')
		await m.place('fresh', 'event_pulse', { kind: 'item.start', ref: '', hold_ms: HOLD })
		await m.place('old', 'event_pulse', { kind: 'item.end', ref: '', hold_ms: HOLD })
		await values(m, { fresh: false, old: false }, 'no event heard: no pulse')

		// "Now" on this machine is an hour ago on the node: past any hold. "Now" on the node is within it.
		node.push(eventLine(20, Date.now(), 'item.end', { item: SHOW.red }))
		node.push(eventLine(21, node.now(), 'item.start', { item: SHOW.blue }))
		await seqHeard(m, 21, 'both events taken')
		await values(m, { fresh: true, old: false }, "the node's now pulses; an hour before the node's now does not")
		assert.deepEqual(taken(m), [20, 21])
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('an event at or below the last seq heard is never taken again', async () => {
	const node = await new FakeNode().start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		await node.heard('SUBSCRIBE EVENTS')
		await m.place('end', 'event_pulse', { kind: 'item.end', ref: '', hold_ms: HOLD })
		await m.place('armed', 'event_pulse', { kind: 'show.armed', ref: '', hold_ms: HOLD })
		await m.place('panic', 'event_pulse', { kind: 'panic', ref: '', hold_ms: HOLD })
		await m.place('start', 'event_pulse', { kind: 'item.start', ref: '', hold_ms: HOLD })
		await values(m, { end: false, armed: false, panic: false, start: false }, 'no event heard: no pulse')

		node.push(eventLine(412, node.now(), 'item.end', { item: SHOW.red, words: 'Show: Red ended' }))
		await seqHeard(m, 412, 'the first event')
		await values(m, { end: true, armed: false, panic: false, start: false }, 'item.end pulses')

		// The same seq again (as itself and as another kind) and a lower seq: none is taken. A newer one is, after them in order.
		node.push(eventLine(412, node.now(), 'item.end', { item: SHOW.red, words: 'Show: Red ended' }))
		node.push(eventLine(412, node.now(), 'show.armed', { by: 'Desk', words: 'Armed by Desk' }))
		node.push(eventLine(411, node.now(), 'panic', { outcome: 'BLACK', words: 'PANIC BLACK' }))
		node.push(eventLine(413, node.now(), 'item.start', { item: SHOW.blue, words: 'Show: Blue started' }))
		await seqHeard(m, 413, 'the newer event, after the old ones')
		assert.equal(m.host.variables.get('event_last_kind'), 'item.start')
		assert.equal(m.host.variables.get('event_last'), 'Show: Blue started')
		await values(m, { end: true, armed: false, panic: false, start: true }, 'only the newer event pulses')
		assert.deepEqual(taken(m), [412, 413], 'each seq taken once')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('after a drop the re-paired link asks EVENTS SINCE its last seq, after subscribing, and takes what it missed once', async () => {
	const node = await new FakeNode().start()
	const ring: Record<string, unknown>[] = []
	const asked: string[] = []
	serveRing(node, ring, asked)
	const m = await boot(configFor(node.webPort), {}, { random: () => 0 })
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		await node.heard('SUBSCRIBE EVENTS')
		await m.place('end', 'event_pulse', { kind: 'item.end', ref: '', hold_ms: HOLD })
		await m.place('fault', 'event_pulse', { kind: 'fault.raised', ref: '', hold_ms: HOLD })
		await m.place('scan', 'event_pulse', { kind: 'scan.done', ref: '', hold_ms: HOLD })
		await values(m, { end: false, fault: false, scan: false }, 'no event heard: no pulse')

		const first = eventJson(412, node.now(), 'item.end', { item: SHOW.red, words: 'Show: Red ended' })
		ring.push(first)
		node.push('EVENT ' + JSON.stringify(first))
		await seqHeard(m, 412, 'the pushed event')

		// The link drops; while it is down the node raises two more events, which no push carries.
		const sessionsBefore = [...node.sessions]
		const linesBefore = node.lines.length
		for (const s of sessionsBefore) s.close(1001)
		await m.host.until(() => m.host.variables.get('link') !== 'live', 'the link down')
		ring.push(eventJson(413, node.now(), 'fault.raised', { ref: 'op_1', words: 'Output 1 lost its display' }))
		ring.push(eventJson(414, node.now(), 'scan.done', { scan: 'sc00000001', words: 'Scan done: 412 points' }))

		await seqHeard(m, 414, 'the missed events replayed after the re-pair')
		assert.ok(
			[...node.sessions].every((s) => !sessionsBefore.includes(s)),
			'the replay came on a new session',
		)
		assert.deepEqual(
			asked.filter((seq) => seq !== '0'),
			['412'],
			'after the drop, EVENTS SINCE the last seq heard, asked once',
		)
		const after = node.sent().slice(linesBefore)
		const subscribed = after.indexOf('SUBSCRIBE EVENTS')
		const since = after.indexOf('EVENTS SINCE 412')
		assert.ok(subscribed >= 0, 'subscribed again')
		assert.ok(since > subscribed, 'subscribed before asking what was missed, so nothing falls between')
		assert.equal(m.host.variables.get('event_last_kind'), 'scan.done')
		assert.equal(m.host.variables.get('event_last'), 'Scan done: 412 points')
		await values(m, { end: true, fault: true, scan: true }, 'the replayed events pulse')
		assert.deepEqual(taken(m), [412, 413, 414])
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a node that started again numbers its events afresh: the re-paired link takes them from its first', async () => {
	const node = await new FakeNode().start()
	const ring: Record<string, unknown>[] = []
	const asked: string[] = []
	serveRing(node, ring, asked)
	const m = await boot(configFor(node.webPort), {}, { random: () => 0 })
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		await node.heard('SUBSCRIBE EVENTS')
		await m.place('fault', 'event_pulse', { kind: 'fault.raised', ref: '', hold_ms: HOLD })
		node.push(eventLine(412, node.now(), 'item.end', { item: SHOW.red, words: 'Show: Red ended' }))
		await seqHeard(m, 412, 'the pushed event')

		// The node starts again: its ring is empty and its seqs begin at 1. Two events are raised before the deck pairs again.
		for (const s of [...node.sessions]) s.close(1001)
		await m.host.until(() => m.host.variables.get('link') !== 'live', 'the link down')
		ring.push(eventJson(1, node.now(), 'show.armed', { words: 'Show armed' }))
		ring.push(eventJson(2, node.now(), 'fault.raised', { ref: 'op_1', words: 'Output 1 lost its display' }))

		await seqHeard(m, 2, 'the new numbering taken')
		assert.deepEqual(
			asked,
			['412', '0'],
			'asked from the last seq heard, then, the node being behind it, from its start',
		)
		assert.equal(m.host.variables.get('event_last'), 'Output 1 lost its display')
		await values(m, { fault: true }, 'the restarted node’s fault pulses')
		assert.deepEqual(taken(m), [1, 2])

		// The next push, numbered on from there, is taken too.
		node.push(eventLine(3, node.now(), 'item.start', { item: SHOW.blue, words: 'Show: Blue started' }))
		await seqHeard(m, 3, 'the push after the replay')
		assert.deepEqual(taken(m), [1, 2, 3])
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('an event pushed while the replay is in flight does not hide the replayed events older than it', async () => {
	const node = await new FakeNode().start()
	const ring: Record<string, unknown>[] = []
	const asked: string[] = []
	const m = await boot(configFor(node.webPort), {}, { random: () => 0 })
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		await node.heard('SUBSCRIBE EVENTS')
		await m.place('fault', 'event_pulse', { kind: 'fault.raised', ref: '', hold_ms: HOLD })
		await m.place('scan', 'event_pulse', { kind: 'scan.done', ref: '', hold_ms: HOLD })
		await m.place('start', 'event_pulse', { kind: 'item.start', ref: '', hold_ms: HOLD })
		await values(m, { fault: false, scan: false, start: false }, 'no event heard: no pulse')

		const first = eventJson(412, node.now(), 'item.end', { item: SHOW.red, words: 'Show: Red ended' })
		ring.push(first)
		node.push('EVENT ' + JSON.stringify(first))
		await seqHeard(m, 412, 'the pushed event')

		// Down: 413 and 414 are raised unheard. Back: the subscription is open again, so 415, raised as EVENTS SINCE is answered,
		// is pushed to the module ahead of the reply that carries 413, 414 and 415.
		const newest = eventJson(415, node.now(), 'item.start', { item: SHOW.blue, words: 'Show: Blue started' })
		serveRing(node, ring, asked, () => {
			ring.push(newest)
			node.push('EVENT ' + JSON.stringify(newest))
		})
		for (const s of [...node.sessions]) s.close(1001)
		await m.host.until(() => m.host.variables.get('link') !== 'live', 'the link down')
		ring.push(eventJson(413, node.now(), 'fault.raised', { ref: 'op_1', words: 'Output 1 lost its display' }))
		ring.push(eventJson(414, node.now(), 'scan.done', { scan: 'sc00000001', words: 'Scan done: 412 points' }))

		await seqHeard(m, 415, 'the newest event')
		assert.deepEqual(asked, ['412'], 'after the drop, EVENTS SINCE the last seq heard')
		await values(m, { fault: true, scan: true, start: true }, 'the missed events pulse, and the newest one')
		assert.deepEqual(taken(m), [412, 413, 414, 415], 'every event once, in seq order')
		assert.equal(m.host.variables.get('event_seq'), 415)
		assert.equal(m.host.variables.get('event_last_kind'), 'item.start')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('an EVENTS overflow is replayed from the last seq heard and the subscription opened again', async () => {
	const node = await new FakeNode().start()
	const ring: Record<string, unknown>[] = []
	const asked: string[] = []
	serveRing(node, ring, asked)
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		await node.heard('SUBSCRIBE EVENTS')
		await m.place('start', 'event_pulse', { kind: 'item.start', ref: '', hold_ms: HOLD })
		await m.place('fault', 'event_pulse', { kind: 'fault.raised', ref: '', hold_ms: HOLD })
		await values(m, { start: false, fault: false }, 'no event heard: no pulse')
		const first = eventJson(412, node.now(), 'item.end', { item: SHOW.red })
		ring.push(first)
		node.push('EVENT ' + JSON.stringify(first))
		await seqHeard(m, 412, 'the pushed event')
		const subscriptions = (): number => node.sent().filter((l) => l === 'SUBSCRIBE EVENTS').length
		assert.equal(subscriptions(), 1)

		// The node's two overflow lines (more than 64 a second; a session that reads too slowly) both end the subscription there.
		const overflows: [number, string, string][] = [
			[
				413,
				'item.start',
				'EVENTS overflow — more than 64 events in a second; EVENTS SINCE 412 replays what this session missed, then SUBSCRIBE EVENTS again',
			],
			[414, 'fault.raised', 'EVENTS overflow — this session read too slowly; SUBSCRIBE EVENTS again'],
		]
		for (const [seq, kind, line] of overflows) {
			ring.push(eventJson(seq, node.now(), kind, { ref: 'op_1' }))
			const subscribed = subscriptions()
			node.push(line)
			await seqHeard(m, seq, `the event the overflow dropped (${seq}), replayed`)
			assert.equal(asked.at(-1), String(seq - 1), 'EVENTS SINCE the last seq heard')
			await m.host.until(() => subscriptions() > subscribed, 'SUBSCRIBE EVENTS again').catch(() => undefined)
			assert.equal(subscriptions(), subscribed + 1, 'the subscription opened again')
		}
		await values(m, { start: true, fault: true }, 'the replayed events pulse')
		assert.deepEqual(taken(m), [412, 413, 414])
	} finally {
		await m.stop()
		await node.stop()
	}
})
