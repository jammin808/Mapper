// Receipts on the module's clock: a refused press is red for 2 s and then clear; an accepted press whose effect no STATE shows is
// pending for 10 s and then clear; a press the node never answers fails at 5 s (its result says it was not sent through) and is red
// for 2 s. On fake timers, in its own file so its clock is its own.
import test from 'node:test'
import assert from 'node:assert/strict'
import type { JsonValue } from '@companion-module/base'
import { boot } from './harness.ts'
import { advance, fakeChannels, settle } from './fake-channel.ts'
import { fixture } from './fake-node.ts'

type Fields = Record<string, unknown>

function fields(v: JsonValue | undefined): Fields {
	assert.ok(v !== null && typeof v === 'object' && !Array.isArray(v), `a result object, not ${JSON.stringify(v)}`)
	return v as Fields
}

void test('failed clears after 2 s; accepted with no effect clears after 10 s; no answer fails the press at 5 s', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', port: 9940, min_go_ms: 0 }, {}, { factory, random: () => 0.5 })
	try {
		const ch = opened[0]
		assert.ok(ch)
		ch.events.open({})
		await settle()
		ch.ok('HELLO')
		await settle()
		ch.say('STATE ' + JSON.stringify(fixture('state.json')))
		await advance(t.mock.timers, 100)

		/** Moves the clock on, the node answering every PING so the link stays live. */
		const wait = async (ms: number): Promise<void> => {
			for (let left = ms; left > 0; left -= 250) {
				ch.pong(Date.now())
				await advance(t.mock.timers, Math.min(250, left), 10)
			}
		}
		/** The node refuses the last line of this verb with words. */
		const refuse = (verb: string, words: string): void => {
			const line = [...ch.sent].reverse().find((l) => l.replace(/^@\S+ /u, '').startsWith(verb))
			assert.ok(line, `${verb} was sent`)
			ch.say(`ERR ${line.split(' ')[0]} ${words}`)
		}
		const pending = (): unknown => m.host.feedbackValues.get('pending')
		const failed = (): unknown => m.host.feedbackValues.get('failed')
		await m.place('pending', 'receipt', { stage: 'pending' }, 'key-1')
		await m.place('failed', 'receipt', { stage: 'failed' }, 'key-1')
		await advance(t.mock.timers, 60, 10)
		assert.equal(pending(), false)
		assert.equal(failed(), false)

		// Refused: red for 2 s, then clear.
		const refused = m.press('v_blackout', { state: 'ON' }, 'key-1')
		await settle()
		refuse('BLACKOUT', 'the show is armed')
		assert.equal(fields(await refused).stage, 'refused')
		await advance(t.mock.timers, 60, 10)
		assert.equal(failed(), true)
		assert.equal(pending(), false)
		await wait(1880)
		assert.equal(failed(), true, 'still red before 2 s')
		await wait(120)
		assert.equal(failed(), false, 'clear after 2 s')
		assert.equal(pending(), false)

		// Accepted, and no STATE ever shows the effect: amber for 10 s, then clear.
		const accepted = m.press('v_freeze', { state: 'ON' }, 'key-1')
		await settle()
		ch.ok('FREEZE', '{"stage":"accepted","effect":"frozen at the next frame"}')
		assert.equal(fields(await accepted).stage, 'accepted')
		await advance(t.mock.timers, 60, 10)
		assert.equal(pending(), true)
		await wait(9880)
		assert.equal(pending(), true, 'still amber before 10 s')
		await wait(120)
		assert.equal(pending(), false, 'clear after 10 s')
		assert.equal(failed(), false)

		// Never answered: amber while the press waits, failed at 5 s, red for 2 s.
		let answered = false
		const unanswered = m.press('v_blackout', { state: 'OFF' }, 'key-1')
		void unanswered.then(() => (answered = true))
		await advance(t.mock.timers, 60, 10)
		assert.equal(pending(), true)
		await wait(4880)
		assert.equal(answered, false, 'the result waits for the node before 5 s')
		assert.equal(pending(), true)
		await wait(120)
		const result = fields(await unanswered)
		assert.equal(result.ok, false)
		assert.equal(result.stage, 'not-sent')
		assert.equal(result.reply, '')
		assert.ok(typeof result.error === 'string' && result.error.length > 0)
		assert.equal(failed(), true)
		assert.equal(pending(), false)
		assert.equal(m.host.variables.get('last_refusal'), result.error)
		await wait(2000)
		assert.equal(failed(), false, 'clear 2 s after it failed')
	} finally {
		await m.stop()
	}
})
