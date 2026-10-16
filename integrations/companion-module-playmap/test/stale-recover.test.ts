// A stale link (no answer for 4 s) that hears the node again is live at once: keys light again, the status is OK. On fake timers,
// in its own file so its clock is its own.
import test from 'node:test'
import assert from 'node:assert/strict'
import { InstanceStatus } from '@companion-module/base'
import { boot } from './harness.ts'
import { advance, fakeChannels, settle } from './fake-channel.ts'

void test('a stale link that hears the node again is live at once', async (t) => {
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
		await advance(t.mock.timers, 4250, 250)
		assert.equal(m.instance.link(), 'stale')
		ch.pong(Date.now())
		await settle()
		assert.equal(m.instance.link(), 'live')
		assert.equal(m.host.status?.status, InstanceStatus.Ok)
	} finally {
		await m.stop()
	}
})
