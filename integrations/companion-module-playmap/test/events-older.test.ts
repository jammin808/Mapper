// An older node, whose STATE has no events and which refuses EVENTS, raises no events: a re-pair with it opens the subscription
// again and asks no replay, so it spends no refused lines and warns of no gap. On fake timers, in its own file so its clock is its
// own.
import test from 'node:test'
import assert from 'node:assert/strict'
import { boot, logs } from './harness.ts'
import { advance, fakeChannels, settle, type FakeChannel } from './fake-channel.ts'
import { copy, fixture } from './fake-node.ts'

void test('a re-pair with an older node that raises no events asks no EVENTS SINCE and warns of no gap', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const state = copy(fixture('state.json')) as Record<string, unknown>
	delete state.events
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', port: 9940 }, {}, { factory, random: () => 0.5 })
	// Every line the module sent, as the node read it (the channel forgets lines once PINGs are answered).
	const heard: string[] = []
	/** The older node: its greeting STATE before the pairing finishes, and EVENTS in any form refused as a word it does not know. */
	const greet = async (ch: FakeChannel): Promise<void> => {
		ch.events.open({})
		await settle()
		ch.ok('HELLO')
		ch.say('STATE ' + JSON.stringify(state))
		await settle()
	}
	const serve = (ch: FakeChannel): void => {
		for (const line of ch.sent) {
			heard.push(line.replace(/^@\S+ /u, ''))
			const id = line.split(' ')[0] ?? ''
			if (/^@\S+ (SUBSCRIBE EVENTS|EVENTS)\b/u.test(line)) ch.say(`ERR ${id} unknown verb EVENTS`)
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
	try {
		const first = opened[0]
		assert.ok(first)
		await greet(first)
		await run(first, 500)
		assert.equal(m.instance.link(), 'live')
		assert.ok(heard.includes('SUBSCRIBE EVENTS'), 'the first pairing subscribes')

		// The link drops and pairs again 1 s later.
		first.events.closed({ code: 1006, reason: 'reset' })
		await advance(t.mock.timers, 1100, 10)
		const again = opened[1]
		assert.ok(again, 'the link tried again')
		const from = heard.length
		await greet(again)
		assert.equal(m.instance.link(), 'live')
		// Past every retry a replay would make (three tries, 2 s apart) and the warning after them.
		await run(again, 7000)

		const after = heard.slice(from)
		assert.ok(after.includes('SUBSCRIBE EVENTS'), 'the re-pair opens the subscription again')
		assert.deepEqual(
			heard.filter((l) => l.startsWith('EVENTS SINCE')),
			[],
			'no replay is asked of a node that raises no events',
		)
		assert.deepEqual(
			logs.filter((l) => /could not be replayed/u.test(l.message)),
			[],
			'no gap is warned of',
		)
		assert.equal(m.instance.link(), 'live')
	} finally {
		await m.stop()
	}
})
