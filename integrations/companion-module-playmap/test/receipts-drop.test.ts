// A receipt is the deck's own fact about its press, so the link going away does not take it: a press refused unsent while the link
// is down, and a press the drop left unanswered, stay red for their 2 s through every failed retry, and clear at their time. On fake
// timers, in its own file so its clock is its own.
import test from 'node:test'
import assert from 'node:assert/strict'
import type { JsonValue } from '@companion-module/base'
import { boot } from './harness.ts'
import { advance, fakeChannels, settle } from './fake-channel.ts'
import { fixture } from './fake-node.ts'

function stageOf(v: JsonValue | undefined): unknown {
	assert.ok(v !== null && typeof v === 'object' && !Array.isArray(v), `a result object, not ${JSON.stringify(v)}`)
	return (v as Record<string, unknown>).stage
}

void test('a drop and a failed retry leave a red receipt red until its 2 s are up', async (t) => {
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
		assert.equal(m.instance.link(), 'live')
		await m.place('failed', 'receipt', { stage: 'failed' }, 'key-1')
		await m.place('failed-2', 'receipt', { stage: 'failed' }, 'key-2')
		const failed = (id = 'failed'): unknown => m.host.feedbackValues.get(id)

		// A press in flight when the link drops: unanswered, red.
		const inFlight = m.press('v_blackout', { state: 'ON' }, 'key-2')
		await settle()
		assert.ok(
			ch.sent.some((l) => /^@c\d+ BLACKOUT ON$/u.test(l)),
			'the line left the module',
		)
		ch.events.closed({ code: 1006, reason: 'reset' })
		assert.equal(stageOf(await inFlight), 'unanswered')
		await settle()
		assert.equal(m.instance.link(), 'connecting')
		await advance(t.mock.timers, 100, 10)
		assert.equal(failed('failed-2'), true, 'the press the drop left unanswered is red')

		// A press while the link is down: not sent, red.
		assert.equal(stageOf(await m.press('v_freeze', { state: 'ON' }, 'key-1')), 'not-sent')
		await advance(t.mock.timers, 60, 10)
		assert.equal(failed(), true, 'a press with no link is red')
		assert.equal(m.instance.receipts.stage('key-1'), 'failed')

		// The retry 1 s after the drop fails before it opens: a drop like any other, and both marks stay red.
		await advance(t.mock.timers, 900, 10)
		const retry = opened[1]
		assert.ok(retry, 'the link tried again')
		retry.events.closed({ reason: 'connect ECONNREFUSED 10.0.0.5:9940' })
		await advance(t.mock.timers, 60, 10)
		assert.equal(m.instance.link(), 'connecting')
		assert.equal(failed(), true, 'still red after a failed retry, before its 2 s')
		assert.equal(failed('failed-2'), true, 'the unanswered press is still red after a failed retry, before its 2 s')
		assert.equal(m.instance.receipts.stage('key-1'), 'failed')

		// Each clears at its own time: the unanswered one 2 s after the drop, the unsent one 2 s after its press.
		await advance(t.mock.timers, 940, 10)
		assert.equal(failed('failed-2'), false, 'the unanswered press clears 2 s after it failed')
		assert.equal(failed(), true, 'the unsent press is red until 2 s after it')
		await advance(t.mock.timers, 100, 10)
		assert.equal(failed(), false, 'the unsent press clears 2 s after it')
		assert.equal(m.instance.receipts.stage('key-1'), null)
	} finally {
		await m.stop()
	}
})
