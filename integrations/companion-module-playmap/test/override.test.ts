// Value overrides: a layered key's text, picture, gauge and frame colour are bound to value feedbacks through style overrides whose
// override is `$(this:value)` (the frame's through the light expression), and the simple fallback takes its text from the same
// feedback as a local variable. The light expression turns green, amber, red and grey into the palette's colours. Placed with an
// item's options, the feedbacks give the key its label, its light (green on the air, amber next, grey otherwise), its picture and its
// progress; an id the show no longer has says REMOVED in grey.
import test from 'node:test'
import assert from 'node:assert/strict'
import { isDeepStrictEqual } from 'node:util'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'
import { colourOf, css, lightExpression } from '../src/palette/playmap.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

interface Override {
	elementId: string
	elementProperty: string
	override: unknown
}

interface FeedbackEntry {
	feedbackId: string
	options: Record<string, unknown>
	styleOverrides?: Override[]
}

interface Element {
	id: string
	type: string
	children?: Element[]
	[property: string]: unknown
}

interface Variant {
	type: string
	elements?: Element[]
	style?: { text?: string }
	feedbacks: FeedbackEntry[]
	localVariables?: { variableType: string; variableName: string; feedbackId?: string; options?: unknown }[]
}

interface IndexJson {
	revision: number
	sig: string
	playlists: { id: string; items: { id: string; label: string; source: string }[] }[]
}

interface StateJson {
	playlists: { id: string; state: string; positionMs: number | null; lengthMs: number | null }[]
	outputs: { id: string; light: string }[]
}

type Expression = { isExpression: true; value: string }

const expr = (value: string): Expression => ({ isExpression: true, value })
const local = (name: string): Expression => expr(`$(local:${name})`)
const THIS_VALUE = expr('$(this:value)')
const LIGHT = expr(lightExpression())

const variants = (m: Booted, presetId: string): { layered: Variant; simple: Variant } => {
	const p = m.host.presets[presetId] as { variants?: Variant[] } | undefined
	const [layered, simple] = p?.variants ?? []
	assert.ok(
		layered?.type === 'layered' && simple?.type === 'simple',
		`${presetId} is a layered key with a simple fallback`,
	)
	return { layered, simple }
}

function element(elements: readonly Element[], id: string): Element | undefined {
	for (const e of elements) {
		if (e.id === id) return e
		const inner = element(e.children ?? [], id)
		if (inner) return inner
	}
	return undefined
}

/** The feedbacks whose style overrides set this element's property. */
const boundTo = (v: Variant, elementId: string, property: string): FeedbackEntry[] =>
	v.feedbacks.filter((f) =>
		(f.styleOverrides ?? []).some((o) => o.elementId === elementId && o.elementProperty === property),
	)

/** The override an element's property takes from the one feedback bound to it. */
function overrideOf(v: Variant, elementId: string, property: string): unknown {
	const [f] = boundTo(v, elementId, property)
	return f?.styleOverrides?.find((o) => o.elementId === elementId && o.elementProperty === property)?.override
}

/** An expression as Companion evaluates it for a value feedback: `$(this:value)` is the value, here a JS string literal. */
function evaluate(expression: string, value: string): unknown {
	// eslint-disable-next-line @typescript-eslint/no-implied-eval
	return new Function(`return (${expression.replaceAll('$(this:value)', JSON.stringify(value))})`)() as unknown
}

/** Waits until the host holds these feedback values, then asserts them (a timeout shows what it holds instead). */
async function values(m: Booted, expected: Record<string, unknown>, what: string): Promise<void> {
	const held = (): Record<string, unknown> =>
		Object.fromEntries(Object.keys(expected).map((id) => [id, m.host.feedbackValues.get(id)]))
	await m.host.until(() => isDeepStrictEqual(held(), expected), what).catch(() => undefined)
	assert.deepEqual(held(), expected, what)
}

async function linked(): Promise<{ node: FakeNode; m: Booted }> {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configFor(node.webPort), { token: TOKEN })
	await m.host.until(
		() => 'item_key' in m.host.presets && m.host.variables.get('node_name') === 'Test node',
		'the presets from INDEX and the greeting STATE',
	)
	return { node, m }
}

const indexDoc = (): IndexJson => copy(fixture('index.json') as IndexJson)

void test('the layered item, output and Live slot keys bind text, picture, gauge and frame colour by $(this:value)', async () => {
	const { node, m } = await linked()
	try {
		const slot = { rule: expr('$(page:pm_live)'), n: local('n') }
		const bindings: Record<string, [string, string, string, Record<string, unknown>, Expression][]> = {
			item_key: [
				['label', 'text', 'label', { ref: local('item') }, THIS_VALUE],
				['thumb', 'base64Image', 'thumb', { ref: local('item'), size: 96 }, THIS_VALUE],
				['gauge', 'value', 'progress', { playlist: local('playlist'), item: local('item') }, THIS_VALUE],
				['frame', 'borderColor', 'light', { ref: local('item') }, LIGHT],
			],
			output_key: [
				['label', 'text', 'label', { ref: local('output') }, THIS_VALUE],
				['sub', 'text', 'state_word', { ref: local('output') }, THIS_VALUE],
				['frame', 'borderColor', 'light', { ref: local('output') }, LIGHT],
			],
			live_slot: [
				['label', 'text', 'slot', { ...slot, field: 'label' }, THIS_VALUE],
				['sub', 'text', 'slot', { ...slot, field: 'sub' }, THIS_VALUE],
				['thumb', 'base64Image', 'slot', { ...slot, field: 'thumb' }, THIS_VALUE],
				['frame', 'borderColor', 'slot', { ...slot, field: 'light' }, LIGHT],
			],
		}
		for (const [presetId, binds] of Object.entries(bindings)) {
			const { layered, simple } = variants(m, presetId)
			for (const [elementId, property, feedbackId, options, override] of binds) {
				const what = `${presetId}: ${elementId}.${property}`
				const el = element(layered.elements ?? [], elementId)
				assert.ok(el && property in el, `${what} is an element property the key draws`)
				const bound = boundTo(layered, elementId, property)
				assert.equal(bound.length, 1, `${what} is bound by one feedback`)
				assert.equal(bound[0]?.feedbackId, feedbackId, what)
				assert.deepEqual(bound[0]?.options, options, what)
				assert.deepEqual(overrideOf(layered, elementId, property), override, what)
				assert.equal(m.host.feedbacks.get(feedbackId)?.type, 'value', `${what}: ${feedbackId} is a value feedback`)
			}
			// The simple fallback shows the same label feedback's value through a feedback-based local variable, saying "(hold)" on a
			// key that is held (the layered one draws its HOLD… element).
			const [, , labelFeedback, labelOptions] = binds[0] ?? []
			const held = element(layered.elements ?? [], 'hold') !== undefined
			assert.equal(simple.style?.text, held ? '$(local:label) (hold)' : '$(local:label)', presetId)
			assert.ok(
				(simple.localVariables ?? []).some(
					(l) =>
						l.variableType === 'feedback' &&
						l.variableName === 'label' &&
						l.feedbackId === labelFeedback &&
						isDeepStrictEqual(l.options, labelOptions),
				),
				`${presetId}: the simple key's label is the label feedback's value`,
			)
		}
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("the light expression turns green, amber, red and grey into the palette's colours", async () => {
	const { node, m } = await linked()
	try {
		const colours = {
			green: css(colourOf('light', 'green')),
			amber: css(colourOf('light', 'amber')),
			red: css(colourOf('light', 'red')),
			grey: css(colourOf('light', 'grey')),
		}
		assert.deepEqual(colours, { green: css('green'), amber: css('amber'), red: css('red'), grey: css('dark') })
		assert.equal(new Set(Object.values(colours)).size, 4, 'four lights, four colours')
		for (const presetId of ['item_key', 'output_key', 'live_slot']) {
			const override = overrideOf(variants(m, presetId).layered, 'frame', 'borderColor') as Expression | undefined
			assert.equal(override?.isExpression, true, presetId)
			const expression = override?.value ?? ''
			for (const [word, colour] of Object.entries(colours))
				assert.equal(evaluate(expression, word), colour, `${presetId}: ${word}`)
			assert.equal(evaluate(expression, ''), colours.grey, `${presetId}: no light is grey`)
			assert.equal(evaluate(expression, 'REMOVED'), colours.grey, `${presetId}: an unknown word is grey`)
		}
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("placed with an item's options, the feedbacks give the key its label, light, picture and progress", async () => {
	const { node, m } = await linked()
	try {
		const show = indexDoc().playlists.find((p) => p.id === SHOW.playlist)
		const [intro, red, blue] = show?.items ?? []
		assert.ok(intro && red && blue)
		const unknown = 'it7fffffff'
		for (const id of [intro.id, red.id, blue.id, unknown]) {
			await m.place(`label-${id}`, 'label', { ref: id })
			await m.place(`light-${id}`, 'light', { ref: id })
		}
		await m.place('thumb-red', 'thumb', { ref: red.id, size: 96 })
		await m.place('progress-red', 'progress', { playlist: SHOW.playlist, item: red.id })
		await m.place('progress-blue', 'progress', { playlist: SHOW.playlist, item: blue.id })
		await m.place('label-op', 'label', { ref: 'op_1' })
		await m.place('word-op', 'state_word', { ref: 'op_1' })
		await m.place('light-op', 'light', { ref: 'op_1' })
		await values(
			m,
			{
				[`label-${intro.id}`]: 'Intro',
				[`label-${red.id}`]: 'Red',
				[`label-${blue.id}`]: 'Blue',
				[`label-${unknown}`]: 'REMOVED',
				[`light-${intro.id}`]: 'grey',
				[`light-${red.id}`]: 'green',
				[`light-${blue.id}`]: 'amber',
				[`light-${unknown}`]: 'grey',
				'label-op': 'Output 1',
				'word-op': 'enabled',
				'light-op': 'grey',
			},
			'labels and lights: Red on the air, Blue next',
		)

		// The key as Companion draws it: its text is the label's value, its frame the light expression of the light's value.
		const frame = (overrideOf(variants(m, 'item_key').layered, 'frame', 'borderColor') as Expression).value
		const frameOf = (id: string): unknown => evaluate(frame, String(m.host.feedbackValues.get(`light-${id}`)))
		assert.deepEqual([intro.id, red.id, blue.id, unknown].map(frameOf), [
			css('dark'),
			css('green'),
			css('amber'),
			css('dark'),
		])

		// The picture: the item's source fetched over the link, as a data URI the image element takes.
		await values(
			m,
			{ 'thumb-red': `data:image/png;base64,${Buffer.from(red.source).toString('base64')}` },
			"Red's picture",
		)
		assert.ok(node.lines.some((l) => /^@c\d+ THUMB color:#FF0000 96x96$/u.test(l)))

		// Progress is STATE's position over the length for the item on the air, 0 for one that is not; the frame follows STATE.
		const state = copy(fixture('state.json') as StateJson)
		const pl = state.playlists.find((p) => p.id === SHOW.playlist)
		const op = state.outputs.find((o) => o.id === 'op_1')
		assert.ok(pl && op)
		pl.state = 'paused'
		pl.positionMs = 2500
		pl.lengthMs = 10000
		op.light = 'red'
		node.pushState(state)
		await values(m, { 'progress-red': 0.25, 'progress-blue': 0, 'light-op': 'red' }, 'progress and the output light')
		assert.equal(evaluate(frame, String(m.host.feedbackValues.get('light-op'))), css('red'))
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('an item the show no longer has says REMOVED, its frame grey', async () => {
	const { node, m } = await linked()
	try {
		const doc = indexDoc()
		const show = doc.playlists.find((p) => p.id === SHOW.playlist)
		const intro = show?.items[0]
		assert.ok(show && intro)
		await m.place('label', 'label', { ref: intro.id })
		await m.place('light', 'light', { ref: intro.id })
		await values(m, { label: 'Intro', light: 'grey' }, 'Intro, not on the air')
		show.items = show.items.slice(1)
		doc.revision += 1
		doc.sig = '0f1e2d3c4b5a6978'
		node.pushIndex(doc)
		await values(m, { label: 'REMOVED', light: 'grey' }, 'Intro is gone')
		const frame = (overrideOf(variants(m, 'item_key').layered, 'frame', 'borderColor') as Expression).value
		assert.equal(evaluate(frame, 'grey'), css(colourOf('light', 'grey')))
	} finally {
		await m.stop()
		await node.stop()
	}
})
