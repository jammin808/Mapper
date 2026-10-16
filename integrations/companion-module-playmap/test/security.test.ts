// A node's data is data, never instructions, and a key never carries what the deck keeps to itself: a STATE nested past what a node
// writes is dropped (no stack runs out in Companion's process); a VERBS answer whose parameters are of the wrong type keeps the deck
// working; a line a section key, a Live slot, raw or a generated action would send passes one fence — no session word, secret or
// pairing verb, at its head or carried inside it (a run of its words, a quoted word, a string of its JSON, a field SET to a line),
// ever reaches the node — and a refusal names only the outer verb, never the operator's words (a token typed as a line stays out
// of every variable); a press answered after destroy touches nothing of the host.
import test from 'node:test'
import assert from 'node:assert/strict'
import type { CompanionOptionValues } from '@companion-module/base'
import { FakeNode, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

interface IndexJson {
	sig: string
	sections: {
		section: string
		hint: { keys: { line: string }[] }
		rows: { id: string; label: string; light: string }[]
	}[]
}

async function linked(): Promise<{ node: FakeNode; m: Booted }> {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configFor(node.webPort), { token: TOKEN })
	await m.host.until(
		() => m.instance.linked() && m.host.variables.get('node_name') === 'Test node',
		'linked, STATE heard',
	)
	return { node, m }
}

/** Every variable value the host holds, as text. */
const allValues = (m: Booted): string => [...m.host.variables.values()].map((v) => JSON.stringify(v ?? null)).join('\n')

void test('a STATE nested past what a node writes is dropped; the module runs on and the link stays live', async () => {
	const { node, m } = await linked()
	const uncaught: unknown[] = []
	const onUncaught = (e: unknown): void => {
		uncaught.push(e)
	}
	process.on('uncaughtException', onUncaught)
	try {
		const deep = '['.repeat(100_000) + ']'.repeat(100_000)
		node.push(`STATE {"name":"x","faults":${deep}}`)
		node.push(`INDEX {"sig":"deep","labels":"deep","playlists":${deep}}`)
		// The next lines on the link are answered, so both pushes were read before them.
		await m.instance.ask('WHOAMI')
		await new Promise<void>((resolve) => setImmediate(resolve))
		await m.instance.ask('WHOAMI')
		assert.deepEqual(uncaught, [])
		assert.equal(m.instance.link(), 'live')
		assert.equal(m.host.variables.get('node_name'), 'Test node', 'the nested STATE was not taken')
	} finally {
		process.off('uncaughtException', onUncaught)
		await m.stop()
		await node.stop()
	}
})

void test('a VERBS answer with parameters of the wrong type keeps the deck working through the next INDEX', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	node.handlers.set(
		'VERBS',
		() =>
			'OK ' +
			JSON.stringify([
				{
					name: 'PLAYLIST GO',
					syntax: 'PLAYLIST GO <playlist>',
					formParams: [[{ name: 'playlist', kind: 'entity', values: 'abc' }]],
				},
				{ name: 'MASTER', syntax: 'MASTER <n>', formParams: [[{ name: 'n', kind: 'number', min: 'zero' }]] },
			]),
	)
	const m = await boot(configFor(node.webPort), { token: TOKEN })
	try {
		await m.host.until(
			() => m.host.actions.has('v_master') && !m.host.actions.has('v_blackout'),
			'the node’s vocabulary taken',
		)
		// A parameter whose fields are of the wrong type is left out; the row stays.
		assert.deepEqual(
			m.host.actions.get('v_master')?.options.map((o) => o.id),
			['confirm'],
		)
		const index = copy(fixture('index.json') as IndexJson)
		index.sig = 'moved'
		node.push('INDEX ' + JSON.stringify(index))
		await m.host.until(() => m.instance.model.index?.sig === 'moved', 'the next INDEX taken')
		assert.equal(m.instance.link(), 'live')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('no key sends a session, secret or pairing verb, at the head of its line or carried in it; a refusal names only the verb', async () => {
	const { node, m } = await linked()
	try {
		// A node whose INDEX lists a section row whose id is a line the deck keeps to itself.
		const index = copy(fixture('index.json') as IndexJson)
		const eye = index.sections.find((s) => s.section === 'eye')
		assert.ok(eye && eye.hint.keys[0]?.line === '$(id)')
		eye.rows.push({ id: 'PAIRING NEW', label: 'NEW TOKEN', light: 'grey' })
		node.push('INDEX ' + JSON.stringify(index))
		await m.host.until(
			() => m.instance.model.index?.sections.some((s) => s.rows.some((r) => r.id === 'PAIRING NEW')) === true,
			'INDEX',
		)
		const slot = eye.rows.length

		const presses: [string, CompanionOptionValues][] = [
			['section_press', { section: 'eye', row: 'PAIRING NEW', key: 0, confirm: 'none' }],
			['slot_press', { rule: 'section:eye', n: slot, held: true }],
			['raw', { line: `AUTH ${TOKEN}` }],
			['raw', { line: TOKEN }],
			['raw', { line: 'BIND ADD MIDI 1 TO CREDENTIAL SET dv1 admin hunter2' }],
			['raw', { line: `BIND LEARN AUTH ${TOKEN}` }],
			['v_bind_add', { input: 'MIDI 1', to: `AUTH ${TOKEN}`, confirm: 'none' }],
		]
		const results: unknown[] = []
		for (const [actionId, options] of presses) {
			const r = (await m.press(actionId, options, `key-${actionId}`)) as { ok: boolean; stage: string }
			assert.equal(r.ok, false, `${actionId} ${JSON.stringify(options)}`)
			assert.equal(r.stage, 'not-sent', `${actionId} ${JSON.stringify(options)}`)
			results.push(r)
		}
		// A question is refused naming only its row, or none: never the words typed as the line.
		for (const line of [TOKEN, `${TOKEN} PLAYLIST LIST`, `AUTH ${TOKEN}`, `BIND ADD MIDI 1 TO AUTH ${TOKEN}`]) {
			const r = (await m.press('ask_node', { line }, 'key-ask')) as { ok: boolean; error: string }
			assert.equal(r.ok, false, line)
			results.push(r)
		}
		assert.ok(!JSON.stringify(results).includes(TOKEN), 'no result holds the token')
		assert.ok(!JSON.stringify(results).includes('hunter2'), 'no result holds a secret')
		await m.instance.ask('WHOAMI')
		const heard = node.sent().join('\n')
		for (const word of ['PAIRING NEW', TOKEN, 'hunter2', 'CREDENTIAL', 'BIND'])
			assert.ok(!heard.includes(word), `the node never heard ${word}`)
		await m.host.until(
			() => JSON.stringify(m.host.variables.get('last_receipt') ?? '').startsWith('"not sent: BIND'),
			'receipts published',
		)
		assert.ok(!allValues(m).includes(TOKEN), 'no variable holds the token')
		assert.ok(!allValues(m).includes('hunter2'), 'no variable holds a secret')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('the fence sees inside a line the node reads as a line: quoted words, JSON strings, a field SET to a target', async () => {
	const { node, m } = await linked()
	try {
		const link = `LINK PATTERNS 10.0.0.5 ${TOKEN}`
		const credential = 'CREDENTIAL SET dv1 admin hunter2'
		// Each is refused unsent, its refusal naming only the outer verb.
		const refused: [string, CompanionOptionValues, string][] = [
			['v_bind_add', { form: 1, f1_json: JSON.stringify({ input: 'MIDI 1', target: link }) }, 'BIND ADD'],
			['raw', { line: `BIND ADD ${JSON.stringify({ input: 'MIDI 1', target: link })}` }, 'BIND ADD'],
			[
				'raw',
				{ line: `BIND ADD ${JSON.stringify({ bindings: [{ input: 'MIDI 1', target: credential }] })}` },
				'BIND ADD',
			],
			['raw', { line: `BIND ADD MIDI 1 TO "LINK" PATTERNS 10.0.0.5 ${TOKEN}` }, 'BIND ADD'],
			['raw', { line: `BIND ADD MIDI 1 TO "${link}"` }, 'BIND ADD'],
			['raw', { line: 'BIND LEARN "CREDENTIAL" SET dv1 admin hunter2' }, 'BIND LEARN'],
			['raw', { line: `BIND FORGET "AUTH" ${TOKEN}` }, 'BIND FORGET'],
			['raw', { line: `SET bd0a1b2c3d target "${link}"` }, 'SET'],
			['raw', { line: `set bd0a1b2c3d target "link patterns 10.0.0.5 ${TOKEN}"` }, 'SET'],
			['v_set', { id: 'bd0a1b2c3d', field_path: 'target', json: JSON.stringify(link) }, 'SET'],
			// A line in a string of JSON in a string of JSON; a phrase inside a quoted phrase; an alias of a kept row.
			[
				'raw',
				{
					line: `BIND ADD ${JSON.stringify({ input: 'MIDI 1', target: `BIND ADD ${JSON.stringify({ input: 'MIDI 2', target: `AUTH ${TOKEN}` })}` })}`,
				},
				'BIND ADD',
			],
			['raw', { line: `BIND LEARN "x \\"AUTH ${TOKEN}\\""` }, 'BIND LEARN'],
			['raw', { line: 'BIND ADD MIDI 1 TO STATUS' }, 'BIND ADD'],
			// JSON a strict reader refuses (a comment, a trailing comma) is read for its strings all the same.
			['raw', { line: `BIND ADD {"input":"MIDI 1", /* a note */ "target":"AUTH ${TOKEN}",}` }, 'BIND ADD'],
			['raw', { line: `BIND ADD {"input":"MIDI 1","target":"AUTH ${TOKEN}` }, 'BIND ADD'],
			['raw', { line: `DEVICE RAW dv0a1b2c3d "${credential}"` }, 'DEVICE RAW'],
		]
		for (const [actionId, options, verb] of refused) {
			const r = (await m.press(actionId, options, 'key-inside')) as { ok: boolean; stage: string; error: string }
			const what = `${actionId} ${JSON.stringify(options)}`
			assert.equal(r.ok, false, what)
			assert.equal(r.stage, 'not-sent', what)
			assert.equal(r.error, `${verb} would carry a line that is never sent from a key`, what)
		}
		// What carries a line of the show still goes.
		const passed: [string, CompanionOptionValues][] = [
			['raw', { line: 'BIND ADD MIDI 1 TO PLAYLIST GO pl00000001' }],
			['raw', { line: `BIND ADD ${JSON.stringify({ input: 'MIDI 1', target: 'PLAYLIST GO pl00000001' })}` }],
			['v_bind_add', { form: 1, f1_json: JSON.stringify({ input: 'MIDI 2', target: 'BLACKOUT TOGGLE' }) }],
			['raw', { line: 'BIND LEARN FROM MIDI PLAYLIST GO pl00000001' }],
			['raw', { line: 'SET bd0a1b2c3d target "PLAYLIST GO pl00000001"' }],
			['raw', { line: 'SET sf00000009 label {"label": "Front wall"}' }],
		]
		for (const [actionId, options] of passed) {
			const r = (await m.press(actionId, options, 'key-inside')) as { ok: boolean; stage: string }
			assert.equal(r.stage, 'applied', `${actionId} ${JSON.stringify(options)}`)
		}
		await m.instance.ask('WHOAMI')
		const heard = node.sent().join('\n')
		for (const word of [TOKEN, 'hunter2', 'CREDENTIAL', 'LINK PATTERNS', 'link patterns', 'STATUS'])
			assert.ok(!heard.includes(word), `the node never heard ${word}`)
		assert.ok(heard.includes('BIND ADD MIDI 1 TO PLAYLIST GO pl00000001'))
		assert.ok(heard.includes('BLACKOUT TOGGLE'))
		assert.ok(!allValues(m).includes(TOKEN), 'no variable holds the token')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a press answered after destroy touches nothing of the host', async () => {
	const { node, m } = await linked()
	try {
		node.handlers.set('BLACKOUT', () => null) // never answered
		const press = m.press('blackout_toggle', {}, 'key-bo')
		await node.heard(/BLACKOUT TOGGLE$/u)
		await m.stop()
		const calls = m.host.variableCalls
		const r = (await press) as { ok: boolean; stage: string; error: string }
		// The line left for the node before the connection stopped: its effect is unknown, so it is unanswered, never "not sent".
		assert.equal(r.ok, false)
		assert.equal(r.stage, 'unanswered')
		assert.equal(r.error, "sent; the connection stopped before an answer — check the node's STATE for its effect")
		// No failed receipt is kept (its clearing would arm a 2 s timer) and nothing more reached the host.
		assert.equal(m.instance.receipts.stage('key-bo'), null)
		assert.equal(m.host.variableCalls, calls)
	} finally {
		await node.stop()
	}
})
