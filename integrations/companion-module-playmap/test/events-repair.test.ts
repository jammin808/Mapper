// A re-pair decides whether to replay events from this session's own STATE, never from the session before: the node's WS channel
// greets with STATE in a frame of its own after the proof's OK, so until it comes the deck neither replays nor only subscribes. A deck
// whose first pairing dropped before its STATE, and one that knew an older node replaced during a drop by a node that speaks events,
// both take every event raised while the link was down. On fake timers, in its own file so its clock is its own.
import test from 'node:test'
import assert from 'node:assert/strict'
import { boot, logs } from './harness.ts'
import { advance, fakeChannels, settle, type FakeChannel } from './fake-channel.ts'
import { copy, fixture } from './fake-node.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

/** The fixture's STATE as a node that speaks events greets with it (its events.seq), or as an older node does (no events). */
function stateOf(seq: number | null): Record<string, unknown> {
	const state = copy(fixture('state.json')) as Record<string, unknown>
	if (seq === null) delete state.events
	else state.events = { seq }
	return state
}

void test('a re-pair whose STATE comes after the proof’s OK replays from that STATE: after a pairing that dropped before its STATE, and after an older node gave way to one that speaks events', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const cases: { what: string; first: Record<string, unknown> | null }[] = [
		{ what: 'a first pairing that dropped before its STATE', first: null },
		{ what: 'an older node, replaced during the drop by one that speaks events', first: stateOf(null) },
	]
	for (const { what, first } of cases) {
		const { factory, opened } = fakeChannels()
		const m = await boot({ host: '10.0.0.5', port: 9940 }, { token: TOKEN }, { factory, random: () => 0.5 })
		// Every line the module sent, as the node read it (the channel forgets lines once PINGs are answered).
		const heard: string[] = []
		/** The node's event ring, and whether the node behind the channel speaks events (an older node refuses EVENTS in any form). */
		const ring: Record<string, unknown>[] = []
		let speaks = first === null
		const serve = (ch: FakeChannel): void => {
			for (const line of ch.sent) {
				const id = line.split(' ')[0] ?? ''
				const said = line.replace(/^@\S+ /u, '')
				heard.push(said)
				const since = /^EVENTS SINCE (\d+)$/u.exec(said)
				if (/^(SUBSCRIBE EVENTS|EVENTS)\b/u.test(said) && !speaks) ch.say(`ERR ${id} unknown verb EVENTS`)
				else if (said.startsWith('SUBSCRIBE ')) ch.say(`OK ${id} subscribed`)
				else if (since) {
					const events = ring.filter((e) => typeof e.seq === 'number' && e.seq > Number(since[1]))
					ch.say(`OK ${id} ${JSON.stringify({ seq: ring.length, missed: 0, events })}`)
				}
			}
			ch.pong(Date.now())
		}
		const run = async (ch: FakeChannel, ms: number): Promise<void> => {
			for (let left = ms; left > 0; left -= 250) {
				serve(ch)
				await advance(t.mock.timers, Math.min(250, left), 10)
			}
			serve(ch)
		}
		/** The node pairs the deck by proof; its greeting STATE is not said here (the WS channel says it in a frame of its own). */
		const pair = async (ch: FakeChannel): Promise<void> => {
			ch.events.open({})
			ch.say('NONCE 00112233445566778899aabbccddeeff')
			await settle()
			ch.ok('HELLO')
			await settle()
			ch.ok('AUTH-PROOF', 'paired')
			await settle()
		}
		const eventsLines = (from: number): string[] =>
			heard.slice(from).filter((l) => /^(SUBSCRIBE EVENTS|EVENTS SINCE)\b/u.test(l))
		const warned = logs.length
		try {
			const one = opened[0]
			assert.ok(one, what)
			await pair(one)
			assert.equal(m.instance.link(), 'live', what)
			await run(one, 500)
			if (first) {
				one.say('STATE ' + JSON.stringify(first))
				await run(one, 500)
			}
			assert.deepEqual(
				eventsLines(0),
				['SUBSCRIBE EVENTS'],
				`${what}: the first pairing subscribes and replays nothing`,
			)

			// The link drops; while it is down the node (another one, in the second case) raises three events.
			one.events.closed({ code: 1006, reason: 'reset' })
			await advance(t.mock.timers, 1100, 10)
			speaks = true
			for (const seq of [1, 2, 3]) ring.push({ seq, at: Date.now(), leaderNs: seq * 1_000_000, kind: 'item.end' })
			const two = opened[1]
			assert.ok(two, `${what}: the link tried again`)
			const from = heard.length
			await pair(two)
			assert.equal(m.instance.link(), 'live', what)
			await run(two, 500)
			assert.deepEqual(eventsLines(from), [], `${what}: nothing is decided before this session's STATE`)

			// This session's STATE, in a frame of its own after the proof's OK: the node speaks events, so the gap is replayed.
			two.say('STATE ' + JSON.stringify(stateOf(3)))
			await run(two, 500)
			assert.deepEqual(
				eventsLines(from),
				['SUBSCRIBE EVENTS', 'EVENTS SINCE 0'],
				`${what}: subscribed, then every event raised while the link was down asked`,
			)
			assert.deepEqual(
				m.instance.events.recent().map((e) => e.seq),
				[1, 2, 3],
				`${what}: events 1..3 taken`,
			)
			assert.equal(m.host.variables.get('event_seq'), 3, what)
			assert.deepEqual(
				logs.slice(warned).filter((l) => /could not be replayed/u.test(l.message)),
				[],
				`${what}: no gap is warned of`,
			)
		} finally {
			await m.stop()
		}
	}
})
