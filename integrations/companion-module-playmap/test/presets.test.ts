// Presets: every preset reaches Companion through the host's sanitiser exactly as the module made it, and the sanitiser says
// nothing; the structure from INDEX gives a template group per playlist keyed `pl.<id>` (its item ids as values, its labels as
// names, the playlist as a common value), a section per package section that has rows, and the Live page's 32 slots; a rename and a
// reorder move only template values, never a preset's id or bytes; a surface gets an opacity encoder keyed by its id; the composite
// elements are the ones the design names; Live slot n of the focused source is the focused playlist's n-th item (32 + n on its
// second page), and while the show is armed every slot holds its entity.
import test from 'node:test'
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { isDeepStrictEqual } from 'node:util'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { boot, configFor, logs, type Booted } from './harness.ts'
import { SLOTS } from '../src/live.ts'
import { compositeDefinitions, presetDefinitions } from '../src/presets.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

interface IndexItemJson {
	id: string
	label: string
	source: string
	[key: string]: unknown
}

interface IndexRowJson {
	id: string
	label: string
	[key: string]: unknown
}

interface IndexJson {
	revision: number
	sig: string
	labels: string
	playlists: { id: string; name: string; items: IndexItemJson[] }[]
	surfaces: unknown[]
	projectors: unknown[]
	sections: {
		section: string
		hint: { title: string; keys: { label: string; press: string }[] }
		rows: IndexRowJson[]
	}[]
}

interface StateJson {
	show: { armed: boolean }
}

interface TemplateValue {
	name?: string
	value: unknown
}

interface Group {
	id: string
	type: string
	name: string
	presetId?: string
	presets?: string[]
	templateVariableName?: string
	templateValues?: TemplateValue[]
	commonVariableValues?: Record<string, unknown>
}

interface Section {
	id: string
	name: string
	definitions: Group[]
}

interface Variant {
	type: string
	notes?: string
	keywords?: string[]
	localVariables?: { variableType: string; variableName: string }[]
	feedbacks: { feedbackId: string; options: Record<string, unknown> }[]
	steps: Record<string, unknown>[]
}

interface Preset {
	type: string
	variants: Variant[]
}

const indexDoc = (): IndexJson => copy(fixture('index.json') as IndexJson)
const stateDoc = (): StateJson => copy(fixture('state.json') as StateJson)

const sections = (m: Booted): Section[] => m.host.structure as Section[]
const groups = (m: Booted): Group[] => sections(m).flatMap((s) => s.definitions)
const group = (m: Booted, id: string): Group | undefined => groups(m).find((g) => g.id === id)
const presetsOf = (m: Booted): Record<string, Preset> => m.host.presets as Record<string, Preset>

const expr = (value: string): { isExpression: true; value: string } => ({ isExpression: true, value })
const local = (name: string): { isExpression: true; value: string } => expr(`$(local:${name})`)

/** Each preset's checksum: SHA-256 of its definition's JSON. */
function checksums(presets: Record<string, unknown>): Record<string, string> {
	return Object.fromEntries(
		Object.entries(presets).map(([id, p]) => [id, createHash('sha256').update(JSON.stringify(p)).digest('hex')]),
	)
}

/** What the host's preset and composite sanitisers logged as a warning or an error (a preset it had to change or drop). */
function sanitiserWords(): string[] {
	return logs
		.filter(
			(l) =>
				(l.source?.includes('PresetDefinitionsManager') || l.source?.includes('CompositeElementDefinitionsManager')) &&
				(l.level === 'warn' || l.level === 'error'),
		)
		.map((l) => `${l.source ?? ''}: ${l.message}`)
}

/** Waits until the host holds these feedback values, then asserts them (a timeout shows what it holds instead). */
async function values(m: Booted, expected: Record<string, unknown>, what: string): Promise<void> {
	const held = (): Record<string, unknown> =>
		Object.fromEntries(Object.keys(expected).map((id) => [id, m.host.feedbackValues.get(id)]))
	await m.host.until(() => isDeepStrictEqual(held(), expected), what).catch(() => undefined)
	assert.deepEqual(held(), expected, what)
}

async function linked(index?: IndexJson): Promise<{ node: FakeNode; m: Booted }> {
	const node = await new FakeNode({ token: TOKEN, ...(index ? { index } : {}) }).start()
	const m = await boot(configFor(node.webPort), { token: TOKEN })
	await m.host.until(
		() => group(m, `pl.${SHOW.playlist}`) !== undefined && group(m, 'fleet') !== undefined,
		'the structure from INDEX and STATE',
	)
	return { node, m }
}

void test("every preset reaches Companion through the host's sanitiser unchanged, and the sanitiser says nothing", async () => {
	// An INDEX that names every kind of entity, so every preset the module defines is referenced and handed over.
	const doc = indexDoc()
	doc.surfaces = [{ id: 'sf00000010', name: 'Wall', tags: ['wall'], source: `playlist:${SHOW.playlist}` }]
	doc.projectors = [{ id: 'pj00000011', name: 'Left', output: 'op_1', device: null }]
	const align = doc.sections.find((s) => s.section === 'align')
	assert.ok(align)
	align.rows = [{ id: 'pj00000011', kind: 'projector', label: 'Left', parent: null, light: 'grey', state: 'idle' }]
	const node = await new FakeNode({ token: TOKEN, index: doc }).start()
	const m = await boot(configFor(node.webPort), { token: TOKEN })
	try {
		const every = Object.keys(presetDefinitions(m.instance.config)).sort()
		await m.host.until(() => every.every((id) => id in m.host.presets), 'every preset handed to Companion')
		const handed = m.instance.lastPresets
		assert.ok(handed)
		assert.deepEqual(Object.keys(m.host.presets).sort(), every)
		assert.deepEqual(m.host.presets, handed.presets, 'no action, feedback, option or element dropped')
		assert.deepEqual(m.host.structure, handed.structure)
		// The definitions are fixed for a config: the project never changes them.
		assert.deepEqual(m.host.presets, presetDefinitions(m.instance.config))
		assert.deepEqual(m.host.composites, compositeDefinitions())
		assert.deepEqual(sanitiserWords(), [])
		for (const [id, p] of Object.entries(presetsOf(m))) {
			assert.equal(p.type, 'alternatives', id)
			assert.deepEqual(
				p.variants.map((v) => v.type),
				['layered', 'simple'],
				`${id}: layered first, simple for older hosts`,
			)
			for (const v of p.variants) {
				assert.ok((v.notes ?? '').length > 0, `${id} explains itself`)
				assert.ok((v.keywords ?? []).length > 0, `${id} can be searched for`)
			}
		}
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('INDEX makes a template group per playlist keyed pl.<id>, a section per package section with rows, and 32 Live slots', async () => {
	const { node, m } = await linked()
	try {
		const doc = indexDoc()
		assert.deepEqual(group(m, 'playlists'), {
			id: 'playlists',
			type: 'template',
			name: 'Playlists',
			presetId: 'playlist_key',
			templateVariableName: 'playlist',
			templateValues: doc.playlists.map((p) => ({ name: p.name, value: p.id })),
		})
		for (const p of doc.playlists)
			assert.deepEqual(group(m, `pl.${p.id}`), {
				id: `pl.${p.id}`,
				type: 'template',
				name: p.name,
				presetId: 'item_key',
				templateVariableName: 'item',
				templateValues: p.items.map((i) => ({ name: i.label, value: i.id })),
				commonVariableValues: { playlist: p.id },
			})
		assert.deepEqual(
			group(m, `pl.${SHOW.playlist}`)?.templateValues?.map((v) => v.name),
			['Intro', 'Red', 'Blue'],
		)
		assert.deepEqual(group(m, `pl.${SHOW.loops}`)?.templateValues, [{ name: 'Green', value: 'it00000005' }])
		assert.deepEqual(group(m, 'outputs')?.templateValues, [{ name: 'Output 1', value: 'op_1' }])
		assert.deepEqual(group(m, 'fleet')?.templateValues, [{ name: 'Stage left', value: 'a1b2c3d4' }])

		const live = group(m, 'live')
		assert.equal(SLOTS, 32)
		assert.equal(live?.type, 'template')
		assert.equal(live.presetId, 'live_slot')
		assert.equal(live.templateVariableName, 'n')
		assert.deepEqual(
			live.templateValues,
			Array.from({ length: 32 }, (_, i) => ({ name: `Slot ${i + 1}`, value: i + 1 })),
		)

		for (const s of doc.sections) {
			const section = sections(m).find((x) => x.id === `sec.${s.section}`)
			if (s.rows.length === 0 || s.hint.keys.length === 0) {
				assert.equal(section, undefined, `${s.section} has no rows, so no section`)
				continue
			}
			assert.ok(section, `a section for ${s.section}`)
			assert.equal(section.name, s.hint.title)
			assert.deepEqual(
				section.definitions.map((g) => g.id),
				s.hint.keys.map((_, i) => `sec.${s.section}.${i}`),
			)
			s.hint.keys.forEach((k, i) => {
				const g = section.definitions[i]
				assert.equal(g?.type, 'template')
				assert.equal(g.presetId, `section_key_${k.press}`, `${s.section} key ${i} is pressed ${k.press}`)
				assert.equal(g.templateVariableName, 'row')
				assert.deepEqual(
					g.templateValues?.map((v) => v.value),
					s.rows.map((r) => r.id),
				)
				assert.deepEqual(g.commonVariableValues, { section: s.section, key: i })
				if (k.label === '$(label)')
					assert.deepEqual(
						g.templateValues?.map((v) => v.name),
						s.rows.map((r) => r.label),
					)
			})
		}

		// A template's variable and its common values land in local variables its preset declares, in both variants.
		for (const g of groups(m)) {
			if (g.type !== 'template') continue
			const preset = presetsOf(m)[g.presetId ?? '']
			assert.ok(preset, `${g.id} names a preset Companion has`)
			for (const v of preset.variants) {
				const locals = new Set(
					(v.localVariables ?? []).filter((l) => l.variableType === 'simple').map((l) => l.variableName),
				)
				for (const name of [g.templateVariableName ?? '', ...Object.keys(g.commonVariableValues ?? {})])
					assert.ok(locals.has(name), `${g.id}: ${name} is a local variable of ${g.presetId ?? ''} (${v.type})`)
			}
		}
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("a rename and a reorder move only the template values: every preset's id and checksum stay", async () => {
	const { node, m } = await linked()
	try {
		const before = checksums(m.host.presets)
		const calls = m.host.presetCalls
		const doc = indexDoc()
		const show = doc.playlists.find((p) => p.id === SHOW.playlist)
		assert.ok(show)
		const [intro, red, blue] = show.items
		assert.ok(intro && red && blue)
		show.name = 'Main show'
		red.label = 'Crimson'
		show.items = [blue, red, intro]
		// As the node writes it: the order is structure (sig), the names are labels.
		doc.revision += 1
		doc.sig = '0f1e2d3c4b5a6978'
		doc.labels = '1234abcd5678ef90'
		node.pushIndex(doc)
		await m.host.until(
			() => group(m, `pl.${SHOW.playlist}`)?.templateValues?.[0]?.value === blue.id,
			'the new order in the structure',
		)
		assert.ok(m.host.presetCalls > calls)
		assert.deepEqual(group(m, `pl.${SHOW.playlist}`), {
			id: `pl.${SHOW.playlist}`,
			type: 'template',
			name: 'Main show',
			presetId: 'item_key',
			templateVariableName: 'item',
			templateValues: [
				{ name: 'Blue', value: blue.id },
				{ name: 'Crimson', value: red.id },
				{ name: 'Intro', value: intro.id },
			],
			commonVariableValues: { playlist: SHOW.playlist },
		})
		assert.deepEqual(group(m, 'playlists')?.templateValues, [
			{ name: 'Main show', value: SHOW.playlist },
			{ name: 'Loops', value: SHOW.loops },
		])
		assert.deepEqual(Object.keys(m.host.presets).sort(), Object.keys(before).sort())
		assert.deepEqual(checksums(m.host.presets), before)
		assert.deepEqual(sanitiserWords(), [])

		// A linked item key presses and reads by id, so it follows its item through both.
		for (const v of presetsOf(m).item_key?.variants ?? []) {
			const down = v.steps[0]?.down as { actionId: string; options: Record<string, unknown> }[] | undefined
			assert.deepEqual(down?.[0]?.options, { playlist: local('playlist'), item: local('item') })
		}
		await m.place('red-label', 'label', { ref: red.id })
		await values(m, { 'red-label': 'Crimson' }, 'the renamed item’s label')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("Live slot n of the focused source is the focused playlist's n-th item, and follows a reorder", async () => {
	const { node, m } = await linked()
	try {
		await m.host.until(() => m.host.variables.get('node_name') === 'Test node', 'STATE')
		// Every slot reads its source from its page ($(page:pm_live)) and its number from the template ($(local:n)).
		for (const v of presetsOf(m).live_slot?.variants ?? []) {
			const slotFeedbacks = v.feedbacks.filter((f) => f.feedbackId.startsWith('slot'))
			assert.ok(slotFeedbacks.length > 0)
			for (const f of slotFeedbacks) {
				assert.deepEqual(f.options.rule, expr('$(page:pm_live)'))
				assert.deepEqual(f.options.n, local('n'))
			}
		}

		const doc = indexDoc()
		const show = doc.playlists.find((p) => p.id === SHOW.playlist)
		assert.ok(show)
		// "focused" as a page names it, and "" (the page sets none): the connection's source, focused by default.
		for (const rule of ['focused', '']) {
			for (let n = 1; n <= 4; n++) {
				await m.place(`label-${rule}-${n}`, 'slot', { rule, n, field: 'label' })
				await m.place(`line-${rule}-${n}`, 'slot', { rule, n, field: 'line' })
				await m.place(`light-${rule}-${n}`, 'slot', { rule, n, field: 'light' })
				await m.place(`empty-${rule}-${n}`, 'slot_empty', { rule, n })
			}
			const expected: Record<string, unknown> = {}
			const lights = ['grey', 'green', 'amber', 'grey']
			for (let n = 1; n <= 4; n++) {
				const item = show.items[n - 1]
				expected[`label-${rule}-${n}`] = item?.label ?? ''
				expected[`line-${rule}-${n}`] = item ? `PLAYLIST GO ${show.id} ${item.id}` : ''
				expected[`light-${rule}-${n}`] = lights[n - 1]
				expected[`empty-${rule}-${n}`] = item === undefined
			}
			await values(m, expected, `the slots of source "${rule}"`)
		}
		await m.place('loops-1', 'slot', { rule: `playlist:${SHOW.loops}`, n: 1, field: 'label' })
		await values(m, { 'loops-1': 'Green' }, 'a page on another playlist')

		// Not armed: a reorder lays the slots out again.
		show.items.reverse()
		doc.revision += 1
		doc.sig = '0f1e2d3c4b5a6978'
		node.pushIndex(doc)
		await values(
			m,
			{ 'label-focused-1': 'Blue', 'label-focused-2': 'Red', 'label-focused-3': 'Intro' },
			'the slots follow the new order',
		)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('while the show is armed every slot holds its entity: a reorder moves none, a removed one shows REMOVED in grey', async () => {
	const { node, m } = await linked()
	try {
		await m.host.until(() => m.host.variables.get('node_name') === 'Test node', 'STATE')
		await m.place('armed', 'show_armed', {})
		for (let n = 1; n <= 3; n++) {
			await m.place(`s${n}`, 'slot', { rule: 'focused', n, field: 'label' })
			await m.place(`l${n}`, 'slot', { rule: 'focused', n, field: 'light' })
		}
		await values(
			m,
			{ armed: false, s1: 'Intro', s2: 'Red', s3: 'Blue', l1: 'grey', l2: 'green', l3: 'amber' },
			'the slots before arming',
		)

		const armed = stateDoc()
		armed.show.armed = true
		node.pushState(armed)
		await values(m, { armed: true }, 'armed')

		// Intro leaves the show and the rest swap: the slots stay on their entities. Red, on the air, is now the last item, so a GO
		// plays nothing and Blue is no longer next (its light follows; its place does not).
		const doc = indexDoc()
		const show = doc.playlists.find((p) => p.id === SHOW.playlist)
		assert.ok(show)
		const [, red, blue] = show.items
		assert.ok(red && blue)
		show.items = [blue, red]
		doc.revision += 1
		doc.sig = '0f1e2d3c4b5a6978'
		node.pushIndex(doc)
		await values(
			m,
			{ s1: 'REMOVED', s2: 'Red', s3: 'Blue', l1: 'grey', l2: 'green', l3: 'grey' },
			'frozen while armed; the gone one REMOVED in grey',
		)

		// RE-LAY lays the page out from the show as it is now, still armed.
		await m.press('live_refresh', {})
		await values(m, { s1: 'Blue', s2: 'Red', s3: '', l1: 'grey', l2: 'green', l3: 'grey' }, 'laid out again')

		// Held again: a reorder (and a rename, which a held slot still shows) leaves every slot where it is.
		red.label = 'Crimson'
		show.items = [red, blue]
		doc.revision += 1
		doc.sig = '1f2e3d4c5b6a7988'
		doc.labels = '1234abcd5678ef90'
		node.pushIndex(doc)
		await values(m, { s1: 'Blue', s2: 'Crimson', s3: '' }, 'held after RE-LAY, relabelled in place')

		// Disarmed: laid out again.
		node.pushState(stateDoc())
		await values(m, { armed: false, s1: 'Crimson', s2: 'Blue', s3: '' }, 'laid out again once disarmed')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a surface in INDEX gets an opacity encoder keyed by its id (surfaces on encoders)', async () => {
	const doc = indexDoc()
	doc.surfaces = [{ id: 'sf00000010', name: 'Wall', tags: ['wall'], source: `playlist:${SHOW.playlist}` }]
	const { node, m } = await linked(doc)
	try {
		assert.equal(
			m.host.actions.get('enc_surface_opacity')?.id,
			'enc_surface_opacity',
			'the encoder action of design 4.6',
		)
		type Turn = { actionId: string; options: Record<string, unknown> }
		const turns = (presetId: string | undefined): Turn[] =>
			(presetsOf(m)[presetId ?? '']?.variants ?? []).flatMap((v) =>
				v.steps.flatMap((s) => [
					...((s.rotate_left as Turn[] | undefined) ?? []),
					...((s.rotate_right as Turn[] | undefined) ?? []),
				]),
			)
		const encoder = groups(m).find(
			(g) =>
				g.type === 'template' &&
				isDeepStrictEqual(
					g.templateValues?.map((v) => v.value),
					['sf00000010'],
				) &&
				turns(g.presetId).some((a) => a.actionId === 'enc_surface_opacity'),
		)
		assert.ok(encoder, 'a template group of surface opacity encoders over the surfaces INDEX lists')
		for (const a of turns(encoder.presetId).filter((x) => x.actionId === 'enc_surface_opacity'))
			assert.ok(
				Object.values(a.options).some((o) => isDeepStrictEqual(o, local(encoder.templateVariableName ?? ''))),
				'each turn names the surface by its template value (its id)',
			)
		assert.deepEqual(sanitiserWords(), [])
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('the composite elements design 4.6 names reach Companion with their options', async () => {
	const { node, m } = await linked()
	try {
		const designed: Record<string, string[]> = {
			pm_tile: ['label', 'sub', 'light'],
			pm_thumb: ['ref', 'label', 'progress'],
			pm_ring: ['value', 'min', 'max', 'text', 'stops'],
			pm_countdown: ['playlist'],
			pm_lock: [],
			pm_receipt: [],
		}
		const composites = m.host.composites as Record<string, { options?: { id: string }[] } | undefined>
		// What is missing, by composite: the composite itself, or the options it lacks.
		const missing: Record<string, string[]> = {}
		for (const [id, options] of Object.entries(designed)) {
			const composite = composites[id]
			const has = new Set((composite?.options ?? []).map((o) => o.id))
			const lacks = composite ? options.filter((o) => !has.has(o)) : ['(the composite)']
			if (lacks.length > 0) missing[id] = lacks
		}
		assert.deepEqual(missing, {}, 'every composite the design names, with its options')
		assert.deepEqual(sanitiserWords(), [])
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('past 32 items the Live page pages: slot n of page 2 is item 32 + n, and live_page says so', async () => {
	const doc = indexDoc()
	const show = doc.playlists.find((p) => p.id === SHOW.playlist)
	assert.ok(show)
	// Forty items, item k labelled by its place: Intro, Red, Blue, then Clip 4 … Clip 40.
	for (let k = 4; k <= 40; k++)
		show.items.push({ id: `it${String(100 + k).padStart(8, '0')}`, label: `Clip ${k}`, source: 'color:#FFFFFF' })
	const { node, m } = await linked(doc)
	try {
		await m.host.until(() => m.host.variables.get('node_name') === 'Test node', 'STATE')
		for (const n of [1, 8, 9]) await m.place(`p${n}`, 'slot', { rule: '', n, field: 'label' })
		await values(m, { p1: 'Intro', p8: 'Clip 8', p9: 'Clip 9' }, 'page 1')
		await m.host.until(() => m.host.variables.get('live_page') === '1/2', 'live_page 1/2')

		await m.press('live_page_next', { rule: '' })
		await values(m, { p1: 'Clip 33', p8: 'Clip 40', p9: '' }, 'page 2: slot n is item 32 + n')
		await m.host.until(() => m.host.variables.get('live_page') === '2/2', 'live_page 2/2')

		await m.press('live_page_prev', { rule: '' })
		await values(m, { p1: 'Intro', p8: 'Clip 8', p9: 'Clip 9' }, 'back on page 1')
	} finally {
		await m.stop()
		await node.stop()
	}
})
