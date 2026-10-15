// The link: pairing over the web port's line channel (the HMAC proof, never the token), the node's words on a refused upgrade, the
// TCP fallback for a node without a line channel, the stop on a wrong token, and replies matched by their ids, not by order.
import test from 'node:test'
import assert from 'node:assert/strict'
import { InstanceStatus } from '@companion-module/base'
import { FakeNode } from './fake-node.ts'
import { boot, configFor } from './harness.ts'
import { authProof } from '../src/link/pairing.ts'
import { Words } from '../src/words.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

void test('a token proves itself by HMAC over the line channel; the token never crosses the wire', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configFor(node.webPort), { token: 'k7qm 3xwd-p9ra' })
	try {
		await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, 'status ok')
		const hello = await node.heard(/HELLO Companion:FOH deck module=1\.0\.0$/u)
		assert.match(hello, /^@c\d+ HELLO /u)
		const proof = await node.heard('AUTH-PROOF')
		assert.match(proof, /^@c\d+ AUTH-PROOF [0-9a-f]{64}$/u)
		assert.ok(!node.lines.some((l) => l.includes('K7QM') || l.includes('k7qm')), 'the token never rides a line')
		for (const line of [
			'WHOAMI',
			'VERBS',
			'SUBSCRIBE INDEX',
			'SUBSCRIBE EVENTS',
			'PRESENCE {"kind":"companion","module":"1.0.0"',
		])
			await node.heard(line)
		await m.host.until(() => m.host.variables.get('node_name') === 'Test node', 'node_name from STATE')
		assert.equal(m.host.variables.get('link'), 'live')
		assert.equal(m.instance.linked(), true)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("the proof is the node's: HMAC-SHA256(normalised token, nonce + name), lowercase hex", () => {
	// Pairing.Proof("K7QM-3XWD-P9RA", "00112233445566778899aabbccddeeff", "Companion:FOH deck") on the node.
	assert.equal(
		authProof('K7QM-3XWD-P9RA', '00112233445566778899aabbccddeeff', 'Companion:FOH deck'),
		authProof('k7qm3xwdp9ra', '00112233445566778899aabbccddeeff', 'Companion:FOH deck'),
	)
	assert.match(authProof('K7QM-3XWD-P9RA', 'ab', 'x'), /^[0-9a-f]{64}$/u)
})

void test("a wrong token says so with the node's words and halts the link (no retry spends the address's wrong tokens)", async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configFor(node.webPort), { token: 'AAAA-BBBB-CCCC' })
	try {
		await m.host.until(() => m.host.status?.status === InstanceStatus.AuthenticationFailure, 'authentication failure')
		assert.ok(
			m.host.status?.message?.includes('wrong token — the pairing token is in PlayMap'),
			m.host.status?.message ?? '',
		)
		assert.equal(m.instance.link(), 'halted')
		assert.equal(node.lines.filter((l) => l.includes('AUTH-PROOF')).length, 1)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a node that asks for a token with none typed is bad config, in words', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.host.status?.status === InstanceStatus.BadConfig, 'bad config')
		assert.equal(m.host.status?.message, Words.tokenNeeded)
		assert.ok(!node.lines.some((l) => l.includes('AUTH-PROOF')))
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("a refused upgrade shows the node's own words from the body, never a guess", async () => {
	const words = 'this node answers its own names only — add "studio-a" to web.hosts or connect by address'
	const node = await new FakeNode({ refuse: { status: 403, error: words } }).start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.host.status?.status === InstanceStatus.BadConfig, 'bad config')
		assert.equal(m.host.status?.message, words)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a node without a line channel on its web port is linked over the TCP line wire', async () => {
	const node = await new FakeNode({ token: TOKEN, noLineChannel: true }).start()
	const m = await boot(configFor(node.webPort, node.wirePort), { token: TOKEN })
	try {
		await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, 'status ok')
		await node.heard('AUTH-PROOF')
		assert.equal([...node.sessions][0]?.kind, 'tcp')
		await m.host.until(() => m.host.variables.get('node_name') === 'Test node', 'STATE over TCP')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('replies are matched by their @id, whatever order they come in', async () => {
	const node = await new FakeNode().start()
	const held: string[] = []
	node.handlers.set('PLAYLIST LIST', () => {
		held.push('list')
		return null // answered below, after the next line's answer
	})
	node.handlers.set('PLAYLIST GET', () => null)
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		const first = m.instance.ask('PLAYLIST LIST')
		const second = m.instance.ask('PLAYLIST GET pl00000001')
		const listLine = await node.heard(/PLAYLIST LIST$/u)
		const getLine = await node.heard(/PLAYLIST GET pl00000001$/u)
		const session = [...node.sessions][0]
		assert.ok(session)
		session.send(`OK ${getLine.split(' ')[0]} {"which":"get"}`)
		session.send(`OK ${listLine.split(' ')[0]} {"which":"list"}`)
		assert.equal((await first).payload, '{"which":"list"}')
		assert.equal((await second).payload, '{"which":"get"}')
		assert.deepEqual(held, ['list'])
	} finally {
		await m.stop()
		await node.stop()
	}
})
