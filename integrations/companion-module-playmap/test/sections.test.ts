// A package's INDEX section key is lit from what the node says now: a hint feedback that is a STATE path to a light word ("$(id)"
// the row's id) is read from STATE; any other ("$(light)", none, or a name that is no STATE light) is the row's light, which the node
// pushes again in INDEX when it moves (its structure and labels kept, so no preset is rebuilt). A link that is not live shows grey.
import test from 'node:test'
import assert from 'node:assert/strict'
import { isDeepStrictEqual } from 'node:util'
import { FakeNode, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'

interface Row {
	id: string
	light: string
}
interface IndexJson {
	sig: string
	labels: string
	sections: { section: string; rows: Row[] }[]
}
interface StateJson {
	seq: number
	at: number
	outputs: { id: string; present: unknown }[]
}

async function values(m: Booted, expected: Record<string, unknown>, what: string): Promise<void> {
	const held = (): Record<string, unknown> =>
		Object.fromEntries(Object.keys(expected).map((id) => [id, m.host.feedbackValues.get(id)]))
	await m.host.until(() => isDeepStrictEqual(held(), expected), what).catch(() => undefined)
	assert.deepEqual(held(), expected, what)
}

const rowOf = (index: IndexJson, section: string, id: string): Row => {
	const row = index.sections.find((s) => s.section === section)?.rows.find((r) => r.id === id)
	assert.ok(row, `${section} ${id} in the fixture`)
	return row
}

void test('a section key is lit from a STATE light its hint names, else from its row, which a lights-only INDEX push moves', async () => {
	const node = await new FakeNode().start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked() && m.instance.model.index !== null, 'linked, INDEX heard')
		const definitions = (): number => m.host.presetCalls
		await m.place('arm', 'section_value', { section: 'show', row: 'SHOW ARM', key: 0 })
		await m.place('present', 'section_value', { section: 'present', row: 'op_1', key: 0 })
		await m.place('rejoin', 'section_value', { section: 'present', row: 'op_1', key: 1 })
		await m.place('tap', 'section_value', { section: 'tempo', row: 'tempo', key: 0 })
		const index = copy(fixture('index.json') as IndexJson)
		assert.equal(rowOf(index, 'show', 'SHOW ARM').light, 'grey')
		await values(m, { arm: 'grey', present: 'grey', rejoin: 'grey', tap: 'grey' }, 'the rows as INDEX wrote them')

		// The output's present light comes up in STATE: its key follows STATE (a lock state is no light: the row's stays).
		const state = copy(fixture('state.json') as StateJson)
		const output = state.outputs.find((o) => o.id === 'op_1')
		assert.ok(output)
		output.present = { light: 'green' }
		// As a node writes it: every STATE it publishes moves its seq and its at.
		state.seq += 1
		state.at += 1
		node.push('STATE ' + JSON.stringify(state))
		await values(m, { arm: 'grey', present: 'green', rejoin: 'grey', tap: 'grey' }, 'a STATE light the hint names')

		// The show arms: the node pushes INDEX again with the rows' new lights, its sig and labels the same.
		const presetsBefore = definitions()
		rowOf(index, 'show', 'SHOW ARM').light = 'green'
		rowOf(index, 'tempo', 'tempo').light = 'amber'
		node.push('INDEX ' + JSON.stringify(index))
		await values(m, { arm: 'green', present: 'green', rejoin: 'grey', tap: 'amber' }, 'the rows’ lights pushed again')
		assert.equal(definitions(), presetsBefore, 'a lights-only INDEX rebuilds no preset')

		// The link drops: no key says what the node no longer says.
		for (const s of [...node.sessions]) s.close(1001)
		await values(m, { arm: 'grey', present: 'grey', rejoin: 'grey', tap: 'grey' }, 'grey once the link is not live')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('an encoder row is turned, never pressed: no section preset lists it, and section_press refuses it unsent', async () => {
	const node = await new FakeNode().start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked() && m.instance.model.index !== null, 'linked, INDEX heard')
		const index = fixture('index.json') as { sections: { section: string; rows: (Row & { kind: string })[] }[] }
		const master = index.sections.find((s) => s.section === 'show')?.rows.find((r) => r.kind === 'encoder')
		assert.ok(master, 'the fixture show section has its encoder row')

		// The show section's preset lists every pressed row and not the encoder's.
		type Definition = { id: string; templateValues?: { value: unknown }[] }
		const show = (m.host.structure as { id: string; definitions: Definition[] }[]).find((s) => s.id === 'sec.show')
		const listed = (show?.definitions[0]?.templateValues ?? []).map((v) => v.value)
		assert.ok(listed.includes('MASTER 100') && listed.includes('SHOW ARM'), JSON.stringify(listed))
		assert.ok(!listed.includes(master.id), 'the encoder row is no key of the preset')

		// Pressed anyway (a key made by hand): nothing reaches the node, and the result says why.
		const before = node.lines.length
		const pressed = (await m.press('section_press', { section: 'show', row: master.id, key: 0 }, 'key-master')) as {
			ok: boolean
			stage: string
			error: string
		}
		assert.equal(pressed.ok, false)
		assert.equal(pressed.stage, 'not-sent')
		assert.match(pressed.error, /turned with a value, never pressed/u)
		assert.deepEqual(
			node.lines.slice(before).filter((l) => /\bMASTER\b/u.test(l)),
			[],
			'no bare MASTER sent',
		)
	} finally {
		await m.stop()
		await node.stop()
	}
})
