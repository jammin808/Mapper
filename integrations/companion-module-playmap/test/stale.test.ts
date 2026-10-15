// The stale guard, on fake timers: a node that answers its PINGs stays live; one that says nothing for 4 s is stale (every key
// dims, the status says so in words); at 10 s the link is dropped, what the node said is emptied, every feedback is checked again,
// and the link opens again after a jittered backoff that doubles to 15 s.
import test from 'node:test'
import assert from 'node:assert/strict'
import { InstanceStatus } from '@companion-module/base'
import { boot } from './harness.ts'
import { advance, fakeChannels, settle } from './fake-channel.ts'
import { fixture } from './fake-node.ts'
import { Words } from '../src/words.ts'

void test('silence dims every key at 4 s and drops the link at 10 s; it opens again with backoff', async (t) => {
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
		ch.say('STATE ' + JSON.stringify(fixture('state.json')))
		await m.place('stale', 'stale', {})
		await m.place('onair', 'item_on_air', { item: 'it00000002' })
		await advance(t.mock.timers, 100)
		assert.equal(m.instance.link(), 'live')
		assert.equal(m.host.feedbackValues.get('stale'), false)
		assert.equal(m.host.feedbackValues.get('onair'), true, 'the fixture plays Red')

		// Answered PINGs keep it live past 4 s.
		for (let n = 0; n < 12; n++) {
			ch.pong(Date.now())
			await advance(t.mock.timers, 500)
		}
		assert.equal(m.instance.link(), 'live')

		// Silence: stale once 4 s pass (the watchdog looks every 250 ms), not before.
		ch.pong(Date.now())
		await advance(t.mock.timers, 3750)
		assert.equal(m.instance.link(), 'live')
		await advance(t.mock.timers, 500)
		assert.equal(m.instance.link(), 'stale')
		assert.equal(m.host.status?.status, InstanceStatus.UnknownWarning)
		assert.equal(m.host.status?.message, Words.stale)
		await advance(t.mock.timers, 60, 10)
		assert.equal(m.host.feedbackValues.get('stale'), true, 'every key dims')
		assert.equal(m.host.feedbackValues.get('onair'), false, 'a stale light is not a fact')

		// 10 s: dropped, emptied, every feedback checked again; no new channel before the backoff.
		await advance(t.mock.timers, 5500, 250)
		assert.equal(ch.closed, false, 'still open before 10 s')
		// Dropped by 10 s and one watchdog look (250 ms) past it, never later.
		for (let waited = 0; waited < 450 && !ch.closed; waited += 10) await advance(t.mock.timers, 10, 10)
		assert.equal(ch.closed, true, 'dropped once 10 s pass')
		assert.equal(m.instance.link(), 'connecting')
		assert.equal(m.instance.model.state, null)
		assert.equal(m.host.status?.status, InstanceStatus.ConnectionFailure)
		await advance(t.mock.timers, 60, 10)
		assert.equal(m.host.feedbackValues.get('onair'), false)
		assert.equal(opened.length, 1)
		await advance(t.mock.timers, 930, 10)
		assert.equal(opened.length, 1, 'the first retry waits 1 s')
		await advance(t.mock.timers, 10, 10)
		assert.equal(opened.length, 2)

		// A node that never opens: each retry waits longer, never past 15 s.
		const waits: number[] = []
		for (let n = 0; n < 6; n++) {
			const c = opened.at(-1)
			assert.ok(c)
			c.close()
			await settle()
			let waited = 0
			while (opened.length === n + 2) {
				await advance(t.mock.timers, 100, 100)
				waited += 100
			}
			waits.push(waited)
		}
		assert.deepEqual(waits, [2000, 4000, 8000, 15000, 15000, 15000])
	} finally {
		await m.stop()
	}
})
