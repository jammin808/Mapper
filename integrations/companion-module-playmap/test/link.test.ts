// The link: pairing over the web port's line channel (the HMAC proof, never the token), the node's words on a refused upgrade, the
// TCP fallback for a node without a line channel, the stop on a wrong token, and replies matched by their ids, not by order.
import test from 'node:test'
import assert from 'node:assert/strict'
import { InstanceStatus, type JsonValue } from '@companion-module/base'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'
import { fakeChannels, settle } from './fake-channel.ts'
import { authProof } from '../src/link/pairing.ts'
import { NodeSession, type SessionHost } from '../src/link/session.ts'
import { Words } from '../src/words.ts'
import { parseAddress, type PlayMapConfig } from '../src/config.ts'
import { MODULE_VERSION } from '../src/version.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

/** The HELLO a connection labelled `label` says, whole, at this module's version. */
function helloOf(label: string): RegExp {
	const literal = (text: string): string => text.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&')
	return new RegExp(`^@c\\d+ HELLO ${literal(`Companion:${label}`)} module=${literal(MODULE_VERSION)}$`, 'u')
}

void test('a token proves itself by HMAC over the line channel; the token never crosses the wire', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configFor(node.webPort), { token: 'k7qm 3xwd-p9ra' })
	try {
		await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, 'status ok')
		await node.heard(helloOf('FOH deck'))
		const proof = await node.heard('AUTH-PROOF')
		assert.match(proof, /^@c\d+ AUTH-PROOF [0-9a-f]{64}$/u)
		assert.ok(!node.lines.some((l) => l.includes('K7QM') || l.includes('k7qm')), 'the token never rides a line')
		for (const line of [
			'WHOAMI',
			'VERBS',
			'SUBSCRIBE INDEX',
			'SUBSCRIBE EVENTS',
			`PRESENCE {"kind":"companion","module":"${MODULE_VERSION}"`,
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
	// Pairing.Proof("K7QM-3XWD-P9RA", "00112233445566778899aabbccddeeff", "Companion:FOH deck") on the node: the node's tests pin the
	// same answer.
	const known = 'b07c009ef4196cc819f65ae7b624f6f7eff03ea67f0d73b99a634e8579fa1d3d'
	assert.equal(authProof('K7QM-3XWD-P9RA', '00112233445566778899aabbccddeeff', 'Companion:FOH deck'), known)
	assert.equal(authProof('k7qm 3xwd-p9ra', '00112233445566778899aabbccddeeff', 'Companion:FOH deck'), known)
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

void test('an address locked out at the upgrade (429) halts with the lockout words, as one locked out in a line does; it is not tried again', async () => {
	const words = 'too many wrong tokens from this address — wait a minute'
	const node = await new FakeNode({ token: TOKEN, refuse: { status: 429, error: words } }).start()
	const m = await boot(configFor(node.webPort), { token: TOKEN })
	try {
		await m.host.until(() => m.host.status?.status === InstanceStatus.AuthenticationFailure, 'the lockout halts')
		assert.equal(m.host.status?.message, Words.lockedOut(words))
		assert.equal(m.instance.link(), 'halted')
		assert.equal(node.upgrades, 1, 'one upgrade asked, none after it')
		assert.ok(
			m.host.statuses.every((s) => s.status !== InstanceStatus.BadConfig),
			'a lockout is no config fault to retry past',
		)
	} finally {
		await m.stop()
		await node.stop()
	}
})

for (const [older, options] of [
	['its /ws takes no subprotocol (the screens’ socket)', { noLineChannel: true }],
	['its web token gate answers 401', { refuse: { status: 401, error: 'this node needs its token' } }],
	['its web port has no /ws (404)', { refuse: { status: 404, error: 'no such route' } }],
] as const)
	void test(`an older node is linked over the TCP line wire: ${older}`, async () => {
		const node = await new FakeNode({ token: TOKEN, ...options }).start()
		const m = await boot(configFor(node.webPort, node.wirePort), { token: TOKEN })
		try {
			await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, 'status ok')
			await node.heard('AUTH-PROOF')
			assert.deepEqual(
				[...node.sessions].map((s) => s.kind),
				['tcp'],
			)
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

void test('an address is read as an operator types it; one no link could open halts with words, never an exception', async () => {
	assert.deepEqual(parseAddress('10.0.0.5', 9940), { host: '10.0.0.5', port: 9940 })
	assert.deepEqual(parseAddress(' 10.0.0.5:9950 ', 9940), { host: '10.0.0.5', port: 9950 })
	assert.deepEqual(parseAddress('[fe80::1]:9941', 9940), { host: 'fe80::1', port: 9941 })
	assert.deepEqual(parseAddress('fe80::1', 9940), { host: 'fe80::1', port: 9940 })
	assert.deepEqual(parseAddress('ws://stage-left.local:9942/ws', 9940), { host: 'stage-left.local', port: 9942 })
	for (const bad of ['studio a', '10.0.0.5:99999', '10.0.0.5:abc', 'node@host', '[fe80::1'])
		assert.equal(parseAddress(bad, 9940), null, bad)

	const node = await new FakeNode({ token: TOKEN }).start()
	try {
		// A typed address with its port, and a picked node (as Companion hands it) over a typed address: both pair.
		const configs: PlayMapConfig[] = [
			{ host: `127.0.0.1:${node.webPort}`, port: 1 },
			{ node: `127.0.0.1:${node.webPort}`, host: '10.9.9.9', port: 1 },
		]
		for (const config of configs) {
			const m = await boot(config, { token: TOKEN })
			try {
				await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, JSON.stringify(config))
			} finally {
				await m.stop()
			}
		}
		for (const host of ['studio a', 'http://10.0.0.5:abc']) {
			const m = await boot({ host, port: node.webPort }, { token: TOKEN })
			try {
				await m.host.until(() => m.host.status?.status === InstanceStatus.BadConfig, host)
				assert.equal(m.host.status?.message, Words.badAddress(host))
				assert.equal(m.instance.link(), 'halted')
			} finally {
				await m.stop()
			}
		}
	} finally {
		await node.stop()
	}
})

void test('a token changed on the node halts the live link with words; the old one is never proved again', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configFor(node.webPort), { token: TOKEN })
	try {
		await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, 'paired')
		const proofs = node.sent().filter((l) => l.startsWith('AUTH-PROOF')).length
		// PAIRING NEW on the node: every paired session is unpaired at once.
		for (const s of node.sessions) s.paired = false
		const r = (await m.press('blackout_toggle', {}, 'key-bo')) as { ok: boolean; stage: string }
		assert.equal(r.ok, false)
		await m.host.until(() => m.host.status?.status === InstanceStatus.AuthenticationFailure, 'unpaired')
		assert.equal(m.host.status?.message, Words.unpaired)
		assert.equal(m.instance.link(), 'halted')
		assert.equal(
			node.sent().filter((l) => l.startsWith('AUTH-PROOF')).length,
			proofs,
			'the old token is not proved again',
		)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a HELLO the node fails to greet is tried again after the backoff; only a token, a lockout or a pin halts', async () => {
	const node = await new FakeNode({ token: TOKEN, helloFaults: 1 }).start()
	const m = await boot(configFor(node.webPort), { token: TOKEN }, { random: () => 0 })
	try {
		await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, 'paired on the second try')
		assert.equal(node.sent().filter((l) => l.startsWith('HELLO')).length, 2)
		assert.ok(m.host.statuses.every((s) => s.status !== InstanceStatus.UnknownError || m.instance.link() !== 'halted'))
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a config edit that moves nothing the link depends on keeps it, and keeps a halt; a new token pairs again', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const config: PlayMapConfig = configFor(node.webPort)
	const m = await boot(config, { token: 'K7QM-3XWD-WRNG' })
	try {
		await m.host.until(() => m.host.status?.status === InstanceStatus.AuthenticationFailure, 'a wrong token halts')
		const proofs = (): number => node.sent().filter((l) => l.startsWith('AUTH-PROOF')).length
		const halted = proofs()
		// A preset group and the bank size: the halt holds, no wrong token is spent.
		await m.wrapper.configUpdateAndLabel(
			'FOH deck',
			{ ...config, g_fleet: false, bank_size: 4 },
			{ token: 'K7QM-3XWD-WRNG' },
		)
		assert.equal(m.instance.link(), 'halted')
		assert.equal(proofs(), halted, 'no proof on an edit that does not touch the token')
		// The right token: proved, paired.
		await m.wrapper.configUpdateAndLabel('FOH deck', { ...config, g_fleet: false, bank_size: 4 }, { token: TOKEN })
		await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, 'paired with the new token')
		const session = [...node.sessions][0]
		const hellos = node.sent().filter((l) => l.startsWith('HELLO')).length
		// Another unrelated edit: the same session, no new HELLO.
		await m.wrapper.configUpdateAndLabel(
			'FOH deck',
			{ ...config, g_fleet: true, live_rule: 'outputs' },
			{ token: TOKEN },
		)
		await m.instance.ask('WHOAMI')
		assert.deepEqual([...node.sessions], [session])
		assert.equal(node.sent().filter((l) => l.startsWith('HELLO')).length, hellos)
		assert.equal(m.instance.link(), 'live')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a rename relinks: the node hears the new name in a second HELLO and pairs it on its proof', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const config = configFor(node.webPort)
	const m = await boot(config, { token: TOKEN })
	try {
		await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, 'paired as FOH deck')
		await node.heard(helloOf('FOH deck'))
		// Companion renames the connection: the host sets the label, then hands the same config and secrets.
		await m.wrapper.configUpdateAndLabel('FOH-B', config, { token: TOKEN })
		await node.heard(helloOf('FOH-B'))
		await m.host.until(
			() => m.host.status?.status === InstanceStatus.Ok && m.instance.linked(),
			'paired again as FOH-B',
		)
		assert.ok(
			[...node.sessions].some((s) => s.paired && s.name === 'Companion:FOH-B'),
			'the node pairs the new name on its proof',
		)
		assert.equal(node.sent().filter((l) => l.startsWith('HELLO')).length, 2)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a rename mid-handshake proves the name the HELLO said', async () => {
	const { factory, opened } = fakeChannels()
	let name = 'Companion:FOH deck'
	const host: SessionHost = {
		helloName: () => name,
		token: () => TOKEN,
		target: () => ({ host: '10.0.0.5', port: 9940, wirePort: 9941, channel: 'ws', pin: '' }),
		status: () => undefined,
		link: () => undefined,
		paired: () => undefined,
		push: () => undefined,
		offset: () => undefined,
		pinLearned: () => undefined,
	}
	const session = new NodeSession(host, { factory })
	session.start()
	try {
		const ch = opened[0]
		assert.ok(ch)
		const nonce = '00112233445566778899aabbccddeeff'
		ch.say(`NONCE ${nonce}`)
		ch.events.open({})
		await settle()
		assert.deepEqual(
			ch.sent.map((l) => l.replace(/^@\S+ /u, '')),
			[`HELLO Companion:FOH deck module=${MODULE_VERSION}`],
		)
		// The label moves between the HELLO and its answer.
		name = 'Companion:FOH-B'
		ch.ok('HELLO')
		await settle()
		const proof = ch.sent.find((l) => / AUTH-PROOF /u.test(l))
		assert.ok(proof, ch.sent.join(' | '))
		assert.equal(proof.split(' ')[2], authProof(TOKEN, nonce, 'Companion:FOH deck'))
	} finally {
		session.stop()
	}
})

/** Whether the host was ever handed this value for this variable. */
const published = (m: Booted, id: string, value: JsonValue): boolean =>
	m.host.variableLog.some((v) => v.id === id && v.value === value)

void test('an unpaired TCP link publishes nothing: a STATE before the proof is answered, then a drop, leaves no fact and every feedback checked again', async () => {
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', channel: 'tcp', wire_port: 9941 }, { token: TOKEN }, { factory })
	try {
		await m.place('onair', 'item_on_air', { item: SHOW.red })
		await m.host.until(() => m.host.feedbackValues.has('onair'), 'the key placed')
		const ch = opened[0]
		assert.ok(ch)
		assert.equal(ch.kind, 'tcp')
		ch.events.open({})
		ch.say('NONCE 00112233445566778899aabbccddeeff')
		await settle()
		ch.ok('HELLO')
		// The TCP wire greets with STATE at once, before the proof is answered.
		ch.say('STATE ' + JSON.stringify(fixture('state.json')))
		await settle()
		assert.ok(
			ch.sent.some((l) => / AUTH-PROOF /u.test(l)),
			'the proof is on its way',
		)
		assert.equal(m.instance.model.state, null, 'nothing the node said is taken before pairing')
		m.host.feedbackValues.delete('onair')
		// The link drops before the proof's answer; the link was connecting all along.
		ch.close()
		await m.host.until(() => m.host.feedbackValues.has('onair'), 'every feedback checked again on the drop')
		await m.host.until(() => m.host.variables.get('last_error') !== '', 'the drop published')
		assert.equal(m.instance.link(), 'connecting')
		assert.equal(m.instance.model.state, null)
		assert.equal(m.host.feedbackValues.get('onair'), false)
		assert.ok(!published(m, 'node_name', 'Test node'), 'the STATE of an unpaired link never published its name')
		assert.ok(!published(m, 'pl_1_state', 'playing'), 'nor its playlists')
	} finally {
		await m.stop()
	}
})

void test('what the node pushed before pairing is taken when it pairs, the latest of each', async () => {
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', channel: 'tcp', wire_port: 9941 }, { token: TOKEN }, { factory })
	try {
		const ch = opened[0]
		assert.ok(ch)
		ch.events.open({})
		ch.say('NONCE 00112233445566778899aabbccddeeff')
		await settle()
		ch.ok('HELLO')
		const later = copy(fixture('state.json') as { name: string })
		later.name = 'Later node'
		ch.say('STATE ' + JSON.stringify(fixture('state.json')))
		ch.say('INDEX ' + JSON.stringify(fixture('index.json')))
		ch.say('STATE ' + JSON.stringify(later))
		await settle()
		assert.equal(m.instance.model.state, null)
		assert.equal(m.instance.model.index, null)
		ch.ok('AUTH-PROOF', 'paired')
		await m.host.until(() => m.host.variables.get('node_name') === 'Later node', 'the latest STATE taken on pairing')
		assert.ok(m.instance.model.index, 'the INDEX taken on pairing')
		assert.equal(m.instance.link(), 'live')
		assert.ok(
			!published(m, 'node_name', 'Test node'),
			'a STATE an unpaired link heard and a later one replaced is never published',
		)
	} finally {
		await m.stop()
	}
})
