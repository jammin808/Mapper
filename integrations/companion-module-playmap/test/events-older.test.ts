// An older node, whose STATE has no events and which refuses EVENTS, raises no events: a re-pair with it opens the subscription
// again and asks no replay, so it spends no refused lines and warns of no gap — whether its greeting STATE comes before the pairing
// finishes or in a frame of its own after the proof's OK, and whichever node the deck knew before the drop (one that speaks events
// included: what this session's STATE says decides, never the session before). On fake timers, in its own file so its clock is its
// own.
import test from 'node:test'
import assert from 'node:assert/strict'
import { boot, logs } from './harness.ts'
import { advance, fakeChannels, settle, type FakeChannel } from './fake-channel.ts'
import { copy, fixture } from './fake-node.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

void test('a re-pair with an older node that raises no events asks no EVENTS SINCE and warns of no gap', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const older = copy(fixture('state.json')) as Record<string, unknown>
	delete older.events
	const speaking = copy(fixture('state.json')) as Record<string, unknown>
	const cases: { what: string; late: boolean; before: Record<string, unknown> }[] = [
		{ what: 'its greeting before the pairing finishes', late: false, before: older },
		{ what: 'its greeting after the proof’s OK', late: true, before: older },
		{
			what: 'its greeting after the proof’s OK, a node that spoke events before the drop',
			late: true,
			before: speaking,
		},
	]
	for (const { what, late, before } of cases) {
		const { factory, opened } = fakeChannels()
		// A late greeting is the WS channel's after a proof (a token asked); an early one is a node that pairs on HELLO.
		const m = await boot({ host: '10.0.0.5', port: 9940 }, late ? { token: TOKEN } : {}, {
			factory,
			random: () => 0.5,
		})
		// Every line the module sent, as the node read it (the channel forgets lines once PINGs are answered).
		const heard: string[] = []
		/** Whether the node behind the channel speaks events: an older node refuses EVENTS in any form as a word it does not know. */
		let speaks = before !== older
		/** The node greets with its STATE: before the pairing finishes (in HELLO's tick), or in a frame of its own after the proof. */
		const greet = async (ch: FakeChannel, state: Record<string, unknown>): Promise<void> => {
			ch.events.open({})
			if (late) ch.say('NONCE 00112233445566778899aabbccddeeff')
			await settle()
			ch.ok('HELLO')
			if (!late) ch.say('STATE ' + JSON.stringify(state))
			await settle()
			if (!late) return
			ch.ok('AUTH-PROOF', 'paired')
			await settle()
			ch.say('STATE ' + JSON.stringify(state))
			await settle()
		}
		const serve = (ch: FakeChannel): void => {
			for (const line of ch.sent) {
				heard.push(line.replace(/^@\S+ /u, ''))
				const id = line.split(' ')[0] ?? ''
				if (/^@\S+ (SUBSCRIBE EVENTS|EVENTS)\b/u.test(line))
					ch.say(speaks ? `OK ${id} subscribed events` : `ERR ${id} unknown verb EVENTS`)
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
		const warned = logs.length
		try {
			const first = opened[0]
			assert.ok(first, what)
			await greet(first, before)
			await run(first, 500)
			assert.equal(m.instance.link(), 'live', what)
			assert.ok(heard.includes('SUBSCRIBE EVENTS'), `${what}: the first pairing subscribes`)

			// The link drops and pairs again 1 s later, with an older node.
			first.events.closed({ code: 1006, reason: 'reset' })
			await advance(t.mock.timers, 1100, 10)
			speaks = false
			const again = opened[1]
			assert.ok(again, `${what}: the link tried again`)
			const from = heard.length
			await greet(again, older)
			assert.equal(m.instance.link(), 'live', what)
			// Past every retry a replay would make (three tries, 2 s apart) and the warning after them.
			await run(again, 7000)

			const after = heard.slice(from)
			assert.ok(after.includes('SUBSCRIBE EVENTS'), `${what}: the re-pair opens the subscription again`)
			assert.deepEqual(
				heard.filter((l) => l.startsWith('EVENTS SINCE')),
				[],
				`${what}: no replay is asked of a node that raises no events`,
			)
			assert.deepEqual(
				logs.slice(warned).filter((l) => /could not be replayed/u.test(l.message)),
				[],
				`${what}: no gap is warned of`,
			)
			assert.equal(m.instance.link(), 'live', what)
		} finally {
			await m.stop()
		}
	}
})
