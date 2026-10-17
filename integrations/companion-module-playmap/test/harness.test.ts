// The host's waits look again whenever the module takes what the node says: a wait on a STATE field that no variable or feedback
// shows holds once that STATE has come, whether or not a host call follows it (nothing plays here, so no clock ticks between pushes).
import test from 'node:test'
import assert from 'node:assert/strict'
import { FakeNode, copy, fixture } from './fake-node.ts'
import { settle } from './fake-channel.ts'
import { boot, configFor } from './harness.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

type Client = { kind: string; module: string | null }

void test('a wait on a STATE field no variable shows holds once that STATE comes, with no host call after it', async () => {
	const state = copy(fixture('state.json')) as { playlists: { id: string; state: string }[]; clients: Client[] }
	for (const p of state.playlists) p.state = 'idle'
	state.clients = [{ kind: 'companion', module: null }]
	const node = await new FakeNode({ token: TOKEN, state }).start()
	const m = await boot(configFor(node.webPort, node.wirePort), { token: TOKEN })
	try {
		// The greeting taken: STATE and INDEX as variables, down to each playlist's own.
		await m.host.until(
			() =>
				m.host.variables.get('node_name') === 'Test node' &&
				state.playlists.every((p) => m.host.variables.get(`pl.${p.id}.state`) === 'idle'),
			'the greeting STATE and INDEX as variables',
		)
		const module = (): string | null | undefined =>
			((m.instance.model.state?.clients ?? []) as Client[]).find((c) => c.kind === 'companion')?.module
		const outcome = { held: false }
		const wait = m.host
			.until(() => module() === '9.9.9', 'the client with its module')
			.then(() => {
				outcome.held = true
			})
		const calls = m.host.variableCalls
		node.pushState({ ...state, clients: [{ kind: 'companion', module: '9.9.9' }] })
		// The node answers in order on the one channel: once PING's reply is in, the STATE before it has been taken.
		await m.instance.ask('PING')
		await settle()
		assert.equal(module(), '9.9.9', 'the module took the STATE')
		assert.equal(m.host.variableCalls, calls, 'no variable moved')
		assert.equal(outcome.held, true, 'the wait held on the STATE alone')
		await wait
	} finally {
		await m.stop()
		await node.stop()
	}
})
