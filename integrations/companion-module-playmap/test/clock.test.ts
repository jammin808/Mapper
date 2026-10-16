// The node's clock, as the module reads it from PING: each PONG is a sample (round trip and offset), and the offset used is the one
// of the sample with the least round trip among the last eight — a slow, lopsided round trip never moves the clock while a quicker
// one is among them, and a quick one ages out once eight newer ones came. On fake timers: the test is the node.
import test from 'node:test'
import assert from 'node:assert/strict'
import { boot } from './harness.ts'
import { advance, fakeChannels, settle, type FakeChannel, type FakeTimers } from './fake-channel.ts'

const AHEAD = 90_000

/** Answers the next PING after `rtt` ms on the way, the node reading it `there` ms after it was sent (half the trip: symmetric). */
async function answer(timers: FakeTimers, ch: FakeChannel, rtt: number, there = rtt / 2): Promise<void> {
	const ping = [...ch.sent].reverse().find((l) => / PING \d+$/u.test(l))
	assert.ok(ping, `a PING was sent: ${ch.sent.join(' | ')}`)
	ch.sent.splice(0)
	const [id, , t1] = ping.split(' ')
	await advance(timers, rtt, rtt)
	const t = Number(t1) + AHEAD + there
	ch.say(`OK ${id} PONG ${t1} ${t} ${t}`)
	await settle()
}

void test('the offset is the least-delay sample of the last eight; a slow lopsided one never moves it', async (t) => {
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
		const offset = (): number => m.instance.nodeNow() - Date.now()
		// The pinger's next PING, at most its 2 s away.
		const nextPing = async (): Promise<void> => {
			for (let waited = 0; waited <= 2000 && !ch.sent.some((l) => / PING \d+$/u.test(l)); waited += 10)
				await advance(t.mock.timers, 10, 10)
		}

		await answer(t.mock.timers, ch, 2) // symmetric: AHEAD exactly
		assert.equal(offset(), AHEAD)
		// Seven slower samples (3 ms, 1 ms lopsided): the 2 ms one is still among the last eight.
		for (let n = 0; n < 7; n++) {
			await nextPing()
			await answer(t.mock.timers, ch, 3, 2.5)
			assert.equal(offset(), AHEAD, `sample ${n + 2}: the 2 ms round trip still rules`)
		}
		// The eighth newer one pushes the 2 ms sample out: the best of the last eight is a 3 ms one.
		await nextPing()
		await answer(t.mock.timers, ch, 3, 2.5)
		assert.equal(offset(), AHEAD + 1)
		// A 400 ms round trip, 300 ms of it on the way back: never the offset while a 3 ms one is among the last eight.
		await nextPing()
		await answer(t.mock.timers, ch, 400, 350)
		assert.equal(offset(), AHEAD + 1)
	} finally {
		await m.stop()
	}
})
