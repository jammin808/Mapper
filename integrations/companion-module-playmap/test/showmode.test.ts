// Show Mode's locks from WHOAMI: verb_locked lights a key whose verb this deck may not run now (WHOAMI's `may`), and goes out for one
// it may; WHOAMI is asked again when STATE says Show Mode moved — armed, its rules (remote view only), its locks or blind — or the
// controllers changed, so a key's lock follows the node, not the moment the deck paired. The presets that send a lockable verb wear
// the lock.
import test from 'node:test'
import assert from 'node:assert/strict'
import { isDeepStrictEqual } from 'node:util'
import { FakeNode, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'

interface StateJson {
	show: { armed: boolean; rules: { remoteViewOnly: boolean }; locks: unknown[] }
}

async function values(m: Booted, expected: Record<string, unknown>, what: string): Promise<void> {
	const held = (): Record<string, unknown> =>
		Object.fromEntries(Object.keys(expected).map((id) => [id, m.host.feedbackValues.get(id)]))
	await m.host.until(() => isDeepStrictEqual(held(), expected), what).catch(() => undefined)
	assert.deepEqual(held(), expected, what)
}

void test('verb_locked follows WHOAMI’s may, asked again when Show Mode’s armed state, rules or locks move', async () => {
	const node = await new FakeNode({ whoami: { proto: 1, may: '*', armed: false } }).start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		await m.place('go', 'verb_locked', { verb: 'PLAYLIST GO' })
		await m.place('blackout', 'verb_locked', { verb: 'BLACKOUT' })
		await values(m, { go: false, blackout: false }, 'rehearsal: every verb may run')
		const whoamis = (): number => node.sent().filter((l) => l === 'WHOAMI').length

		// Armed: the node's WHOAMI now lists only what a remote deck may run.
		node.whoami = { proto: 1, may: ['PLAYLIST GO', 'PANIC'], armed: true }
		const armed = copy(fixture('state.json') as StateJson)
		armed.show.armed = true
		let asked = whoamis()
		node.pushState(armed)
		await m.host.until(() => whoamis() > asked, 'WHOAMI asked again on arming')
		await values(m, { go: false, blackout: true }, 'armed: BLACKOUT is locked for this deck, GO is not')

		// The desk sets remote view only, armed all along: WHOAMI is asked again, and GO locks too.
		node.whoami = { proto: 1, may: [], armed: true }
		const viewOnly = copy(armed)
		viewOnly.show.rules.remoteViewOnly = true
		asked = whoamis()
		node.pushState(viewOnly)
		await m.host.until(() => whoamis() > asked, 'WHOAMI asked again on a rule change')
		await values(m, { go: true, blackout: true }, 'remote view only: every verb locked')

		// A lock lifted: asked again.
		node.whoami = { proto: 1, may: ['PLAYLIST GO'], armed: true }
		const locked = copy(viewOnly)
		locked.show.rules.remoteViewOnly = false
		locked.show.locks = [{ verb: 'BLACKOUT', by: 'Desk' }]
		asked = whoamis()
		node.pushState(locked)
		await m.host.until(() => whoamis() > asked, 'WHOAMI asked again on a lock')
		await values(m, { go: false, blackout: true }, 'GO may run again')

		// The keys that send a lockable verb wear the lock.
		for (const [presetId, verb] of [
			['show_go', 'PLAYLIST GO'],
			['item_key', 'PLAYLIST GO'],
		] as const) {
			const layered = (
				m.host.presets[presetId] as {
					variants: { feedbacks: { feedbackId: string; options: Record<string, unknown> }[] }[]
				}
			).variants[0]
			assert.ok(
				layered?.feedbacks.some((f) => f.feedbackId === 'verb_locked' && f.options.verb === verb),
				`${presetId} wears the lock of ${verb}`,
			)
		}
	} finally {
		await m.stop()
		await node.stop()
	}
})
