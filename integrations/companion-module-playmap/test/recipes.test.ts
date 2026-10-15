// The recipes and the module's own expressions speak Companion 5.1: every function a HELP.md recipe calls, and every function an
// expression in the module's presets and composite elements calls, is one Companion 5.1 has (the list below, pinned with the API);
// oscillate never sits in an action option (Companion refuses it there); every $(…) variable a recipe or a preset names is one this
// module defines, or a page, local or this variable (a composite's own options too); the trigger recipe names only the module's
// feedbacks and variables, the node's event kinds and Companion's trigger events; and the keys and recipes are the ones the design
// draws (an armed frame breathing with oscillate, a fault blinking, a score in colorMix, references to GO, PANIC and BLACKOUT).
import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'
import { presetDefinitions } from '../src/presets.ts'

/** Companion 5.1's expression functions, as the research pins them with the API (docs/round2/research/companion.md §3.3). The
 * research names the string, array, time and date groups without their members, so none of those is pinned: a recipe that needs
 * one adds it here with its source first. */
const COMPANION_51_FUNCTIONS: ReadonlySet<string> = new Set([
	// Numeric; sqrt, pow, log and trigonometry are 5.1's.
	'round',
	'floor',
	'ceil',
	'abs',
	'fromRadix',
	'toRadix',
	'sqrt',
	'pow',
	'log',
	'sin',
	'cos',
	'tan',
	'asin',
	'acos',
	'atan',
	'atan2',
	// Colour (5.1): builders, converters, adjusters, the Lab mix and the dark test.
	'rgb',
	'rgba',
	'hsl',
	'hsla',
	'hsv',
	'colorToRgb',
	'colorToHsl',
	'colorToHsv',
	'colorToHex',
	'isColor',
	'colorAlpha',
	'colorLighten',
	'colorDarken',
	'colorSaturate',
	'colorInvert',
	'colorMix',
	'colorIsDark',
	// Time-driven: blink(period, ?duty); oscillate(period, ?waveform, ?phase) is 5.1's and never in an action option.
	'blink',
	'oscillate',
	// Variables.
	'parseVariables',
	'getVariable',
])

/** Words of the expression language that take brackets and are not functions (control flow, since 5.0). */
const KEYWORDS: ReadonlySet<string> = new Set(['if', 'for', 'while', 'switch', 'return'])

/** The node's event kinds (design §3.5.4): what an event_pulse kind may name. */
const EVENT_KINDS: ReadonlySet<string> = new Set([
	'item.start',
	'item.end',
	'playlist.end',
	'playlist.cued',
	'air.blackout',
	'air.freeze',
	'air.pattern',
	'show.armed',
	'show.disarmed',
	'show.blind',
	'show.commit',
	'panic',
	'fault.raised',
	'fault.cleared',
	'scan.started',
	'scan.done',
	'scan.failed',
	'calibration.moved',
	'calibration.recalled',
	'proposal.new',
	'proposal.decided',
	'peer.lost',
	'peer.found',
	'device.fault',
	'device.power',
	'media.ready',
	'media.missing',
	'deck.identify',
])

/** Companion 5.1's trigger events (research §3.6): a module reaches triggers only through its feedbacks and variables. */
const TRIGGER_EVENTS: ReadonlySet<string> = new Set([
	'interval',
	'intervalRandom',
	'timeofday',
	'specificDate',
	'sun_event',
	'startup',
	'client_connect',
	'button_press',
	'button_release',
	'condition_true',
	'condition_false',
	'variable_changed',
	'computer_locked',
	'computer_unlocked',
])

/** The variable namespaces a recipe may name besides the connection's own: Companion's page, local and this variables. */
const COMPANION_NAMESPACES: ReadonlySet<string> = new Set(['page', 'local', 'this'])

const HELP = readFileSync(new URL('../companion/HELP.md', import.meta.url), 'utf8')

/** The functions an expression calls. Variable references and quoted text are taken out first, so neither "$(" nor a bracket in a
 * string reads as a call; a dotted call (Math.sin) keeps its dots, so it can never pass as a pinned name. */
function callsIn(expression: string): string[] {
	const bare = expression
		.replace(/\$\([^)]*\)/gu, '0')
		.replace(/'(?:[^'\\]|\\.)*'|"(?:[^"\\]|\\.)*"|`(?:[^`\\]|\\.)*`/gu, "''")
	const calls: string[] = []
	for (const m of bare.matchAll(/([A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)\s*\(/gu)) {
		const name = (m[1] ?? '').replace(/\s+/gu, '')
		if (!KEYWORDS.has(name)) calls.push(name)
	}
	return calls
}

/** Every $(namespace:name) an expression or a text names. */
function variablesIn(text: string): { namespace: string; name: string }[] {
	return [...text.matchAll(/\$\(([^:()\s]+):([^()\s]+)\)/gu)].map((m) => ({ namespace: m[1] ?? '', name: m[2] ?? '' }))
}

interface Recipe {
	/** The paragraph that says where the expression goes. */
	readonly lead: string
	readonly code: string
}

/** HELP.md's fenced code blocks, each with the paragraph before it. */
function recipes(): Recipe[] {
	return [...HELP.matchAll(/```[^\n]*\n([\s\S]*?)```/gu)].map((m) => {
		const before = HELP.slice(0, m.index).trimEnd()
		return { lead: before.slice(before.lastIndexOf('\n\n') + 2), code: (m[1] ?? '').trim() }
	})
}

/** The paragraph of HELP.md that starts with this bold title. */
function paragraph(title: string): string {
	const at = HELP.indexOf(`**${title}`)
	assert.ok(at >= 0, `HELP.md has the recipe "${title}"`)
	const end = HELP.indexOf('\n\n', at)
	return HELP.slice(at, end < 0 ? undefined : end)
}

/** A section of HELP.md by its "## " heading, up to the next. */
function section(heading: string): string {
	const at = HELP.indexOf(`\n## ${heading}\n`)
	assert.ok(at >= 0, `HELP.md has the section ${heading}`)
	const end = HELP.indexOf('\n## ', at + 1)
	return HELP.slice(at, end < 0 ? undefined : end)
}

/** Every `backticked` word of a text (fenced code blocks taken out first, so their fences pair with nothing). */
const ticked = (text: string): string[] =>
	[...text.replace(/```[\s\S]*?```/gu, '').matchAll(/`([^`\n]+)`/gu)].map((m) => m[1] ?? '')

interface Expression {
	readonly where: string
	/** The element property (or option) the expression sets. */
	readonly property: string
	readonly value: string
}

/** Every expression value ({isExpression: true, value}) under a document, with the property it sets: a style override sets the
 * element property it names, anything else the key it sits under. */
function expressionsIn(doc: unknown, where: string, property = ''): Expression[] {
	if (Array.isArray(doc)) return doc.flatMap((d, i) => expressionsIn(d, `${where}[${i}]`, property))
	if (!doc || typeof doc !== 'object') return []
	const o = doc as Record<string, unknown>
	if (o.isExpression === true && typeof o.value === 'string') return [{ where, property, value: o.value }]
	const overrides = typeof o.elementProperty === 'string' ? o.elementProperty : null
	return Object.entries(o).flatMap(([k, v]) =>
		expressionsIn(v, `${where}.${k}`, overrides !== null && k === 'override' ? overrides : k),
	)
}

interface ActionEntry {
	readonly where: string
	readonly actionId: string
	readonly options: unknown
}

/** Every action entry (an object with an actionId) under a document. */
function actionsIn(doc: unknown, where: string): ActionEntry[] {
	if (Array.isArray(doc)) return doc.flatMap((d, i) => actionsIn(d, `${where}[${i}]`))
	if (!doc || typeof doc !== 'object') return []
	const o = doc as Record<string, unknown>
	const own: ActionEntry[] = typeof o.actionId === 'string' ? [{ where, actionId: o.actionId, options: o.options }] : []
	return [...own, ...Object.entries(o).flatMap(([k, v]) => actionsIn(v, `${where}.${k}`))]
}

interface Variant {
	type: string
	localVariables?: { variableName: string }[]
}

/** Each variant of each preset the module handed Companion. */
function variants(presets: Record<string, unknown>): { id: string; variant: Variant }[] {
	return Object.entries(presets).flatMap(([id, p]) =>
		((p as { variants?: Variant[] }).variants ?? [p as Variant]).map((variant) => ({ id, variant })),
	)
}

const PROJECTOR = 'pj00000011'

/** A node whose INDEX names every kind of entity, so every preset the module defines is handed over; booted until the presets,
 * the composite elements and the per-id variables are all with Companion. */
async function linked(): Promise<{ node: FakeNode; m: Booted }> {
	const doc = copy(fixture('index.json')) as {
		surfaces: unknown[]
		projectors: unknown[]
		sections: { section: string; rows: unknown[] }[]
	}
	doc.surfaces = [{ id: 'sf00000010', name: 'Wall', tags: ['wall'], source: `playlist:${SHOW.playlist}` }]
	doc.projectors = [{ id: PROJECTOR, name: 'Left', output: 'op_1', device: null }]
	const align = doc.sections.find((s) => s.section === 'align')
	assert.ok(align)
	align.rows = [{ id: PROJECTOR, kind: 'projector', label: 'Left', parent: null, light: 'grey', state: 'idle' }]
	const node = await new FakeNode({ index: doc }).start()
	const m = await boot(configFor(node.webPort), {})
	const every = Object.keys(presetDefinitions(m.instance.config))
	await m.host.until(
		() =>
			every.every((id) => id in m.host.presets) &&
			Object.keys(m.host.composites).length > 0 &&
			m.host.variableDefs.has(`pl.${SHOW.playlist}.state`) &&
			m.host.variableDefs.has(`pj.${PROJECTOR}.light`),
		'every preset, the composites and the per-id variables with Companion',
	)
	return { node, m }
}

/** Every expression the module handed Companion: its presets (as made and as the host kept them) and its composite elements. */
function moduleExpressions(m: Booted): Expression[] {
	const handed = m.instance.lastPresets
	assert.ok(handed, 'the module handed presets')
	return [
		...expressionsIn(handed.presets, 'presets'),
		...expressionsIn(m.host.presets, 'host.presets'),
		...expressionsIn(m.host.composites, 'composites'),
	]
}

void test('the call finder sees each call an expression makes, and no variable or quoted text as one', async () => {
	assert.deepEqual(callsIn("round($(playmap:pl_1_progress) * 100) + '%'"), ['round'])
	assert.deepEqual(callsIn("$(playmap:show_mode) == 'ARMED' ? 40 + 60 * oscillate(2000, 'sine') : 100"), ['oscillate'])
	assert.deepEqual(callsIn("colorMix('#E0342E', '#1E9E5A', $(local:score) / 100)"), ['colorMix'])
	assert.deepEqual(callsIn('\'blink(500)\' + "floor(1)" + Math.sin(PI) + Math . cos (0)'), ['Math.sin', 'Math.cos'])
	assert.deepEqual(callsIn('-abs($(this:delta) || 1)'), ['abs'])
	assert.deepEqual(callsIn("$(this:value) == 'green' ? '#00a000' : '#202020'"), [])
	assert.deepEqual(callsIn('if (x) { return pow(2, sqrt(9)) }'), ['pow', 'sqrt'])
	assert.deepEqual(variablesIn('$(page:pm_live) + $(playmap:pl.pl00000001.state) $(this:value)'), [
		{ namespace: 'page', name: 'pm_live' },
		{ namespace: 'playmap', name: 'pl.pl00000001.state' },
		{ namespace: 'this', name: 'value' },
	])
})

void test("every HELP.md recipe calls only Companion 5.1's functions, and one that oscillates sits on an element, not an action", async () => {
	const all = recipes()
	assert.ok(all.length >= 4, `HELP.md shows its recipes as code (${all.length})`)
	const called = new Set<string>()
	for (const r of all)
		for (const name of callsIn(r.code)) {
			called.add(name)
			assert.ok(COMPANION_51_FUNCTIONS.has(name), `${name}() in "${r.code}" is not a Companion 5.1 function`)
		}
	// The plan's recipes: oscillate and colorMix (and the design's blink).
	for (const name of ['oscillate', 'colorMix', 'blink']) assert.ok(called.has(name), `a recipe uses ${name}()`)
	for (const r of all.filter((x) => callsIn(x.code).includes('oscillate')))
		assert.doesNotMatch(
			r.lead,
			/\baction/iu,
			`an oscillate recipe is for an element property, not an action: ${r.lead}`,
		)
})

void test('every $(…) variable HELP.md names is one the module defines, or a page, local or this variable; so is every one it lists', async () => {
	const { node, m } = await linked()
	try {
		const named = variablesIn(HELP)
		assert.ok(
			named.some((v) => v.namespace === 'playmap'),
			"the recipes read the connection's variables",
		)
		assert.ok(
			named.some((v) => v.namespace === 'page'),
			'a recipe reads a page variable',
		)
		for (const v of named)
			if (v.namespace === 'playmap')
				assert.ok(m.host.variableDefs.has(v.name), `$(playmap:${v.name}) is a variable the module defines`)
			else
				assert.ok(
					COMPANION_NAMESPACES.has(v.namespace),
					`$(${v.namespace}:${v.name}) is a page, local or this variable`,
				)

		// The Variables section's names, with an id of the show for <id>.
		const ids: Record<string, string> = {
			pl: SHOW.playlist,
			op: 'op_1',
			pj: PROJECTOR,
			sf: 'sf00000010',
			peer: 'a1b2c3d4',
		}
		const listed = ticked(section('Variables')).map((name) =>
			name.replace('<id>', ids[name.split('.')[0] ?? ''] ?? '<no id for this prefix>'),
		)
		assert.ok(listed.length >= 20, `HELP.md lists the variables (${listed.length})`)
		for (const name of listed) assert.ok(m.host.variableDefs.has(name), `the listed variable ${name} is defined`)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("every expression in the module's presets and composite elements calls only Companion 5.1's functions and names only variables it has", async () => {
	const { node, m } = await linked()
	try {
		const expressions = moduleExpressions(m)
		assert.ok(expressions.length > 0, 'the presets carry expressions')
		const composites = expressionsIn(m.host.composites, 'composites')
		assert.ok(composites.length > 0, 'the composite elements carry expressions')
		for (const e of expressions) {
			for (const name of callsIn(e.value))
				assert.ok(
					COMPANION_51_FUNCTIONS.has(name),
					`${name}() at ${e.where} ("${e.value}") is not a Companion 5.1 function`,
				)
			const inComposite = e.where.startsWith('composites')
			for (const v of variablesIn(e.value))
				if (v.namespace === 'playmap')
					assert.ok(m.host.variableDefs.has(v.name), `$(playmap:${v.name}) at ${e.where} is defined`)
				else
					assert.ok(
						COMPANION_NAMESPACES.has(v.namespace) || (inComposite && v.namespace === 'options'),
						`$(${v.namespace}:${v.name}) at ${e.where} is a page, local or this variable${inComposite ? ', or an option of the composite' : ''}`,
					)
		}

		// A key's $(local:…) is one of its own local variables.
		const handed = m.instance.lastPresets?.presets as Record<string, unknown>
		for (const { id, variant } of variants(handed)) {
			const declared = new Set((variant.localVariables ?? []).map((l) => l.variableName))
			for (const v of variablesIn(JSON.stringify(variant)).filter((x) => x.namespace === 'local'))
				assert.ok(declared.has(v.name), `${id} (${variant.type}) reads $(local:${v.name}), one of its local variables`)
		}
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('oscillate is never in an action option: not in a preset, an action definition or a composite', async () => {
	const { node, m } = await linked()
	try {
		const handed = m.instance.lastPresets
		assert.ok(handed)
		const entries = [...actionsIn(handed.presets, 'presets'), ...actionsIn(m.host.presets, 'host.presets')]
		assert.ok(entries.length > 0, 'the presets carry actions')
		assert.ok(
			entries.some((a) => expressionsIn(a.options, a.where).length > 0),
			'some action option is an expression (so the check reads them)',
		)
		for (const a of entries)
			assert.doesNotMatch(
				JSON.stringify(a.options ?? null),
				/oscillate\s*\(/u,
				`${a.actionId} at ${a.where}: oscillate in an action option`,
			)
		assert.ok(m.host.actions.size > 0)
		for (const [id, def] of m.host.actions)
			assert.doesNotMatch(JSON.stringify(def.options), /oscillate\s*\(/u, `action ${id}: oscillate in an option`)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("the trigger recipe names only the module's feedbacks and variables, the node's event kinds and Companion's trigger events", async () => {
	const { node, m } = await linked()
	try {
		const words = ticked(paragraph('Triggers from the show.'))
		assert.ok(words.length > 0)
		const kinds = words.filter((w) => /^[a-z]+\.[a-z]+$/u.test(w))
		assert.ok(kinds.length > 0, 'the recipe names event kinds')
		for (const k of kinds) assert.ok(EVENT_KINDS.has(k), `${k} is one of the node's event kinds`)
		for (const w of words.filter((x) => !kinds.includes(x)))
			assert.ok(
				m.host.feedbacks.has(w) || m.host.variableDefs.has(w) || TRIGGER_EVENTS.has(w),
				`${w} is a feedback or variable of the module, or a Companion trigger event`,
			)
		for (const id of ['event_pulse', 'show_armed'])
			assert.equal(m.host.feedbacks.get(id)?.type, 'boolean', `${id} is a boolean feedback (a trigger condition)`)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('the keys breathe as the design draws them: an armed frame on oscillate(2000), a fault on blink(500), a score in colorMix', async () => {
	const { node, m } = await linked()
	try {
		const expressions = moduleExpressions(m).filter((e) => !e.where.startsWith('composites'))
		const calling = (name: string): Expression[] => expressions.filter((e) => callsIn(e.value).includes(name))
		assert.deepEqual(
			{
				'an armed key pulses its frame: opacity on oscillate(2000)': calling('oscillate').some(
					(e) => e.property === 'opacity' && /oscillate\(\s*2000\b/u.test(e.value),
				),
				'a fault blinks: blink(500)': calling('blink').some((e) => /blink\(\s*500\b/u.test(e.value)),
				'a score is coloured with colorMix': calling('colorMix').length > 0,
			},
			{
				'an armed key pulses its frame: opacity on oscillate(2000)': true,
				'a fault blinks: blink(500)': true,
				'a score is coloured with colorMix': true,
			},
		)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('the recipes are the ones the design lists: page variables, references to GO, PANIC and BLACKOUT, a scan.done trigger', async () => {
	const missing: string[] = []
	const pages = new Set(
		variablesIn(HELP)
			.filter((v) => v.namespace === 'page')
			.map((v) => v.name),
	)
	const words = new Set(ticked(HELP))
	if (pages.size === 0) missing.push('a recipe that reads a page variable as $(page:…)')
	for (const name of ['pm_live', 'pm_projector', 'pm_surface'])
		if (!pages.has(name) && !words.has(name)) missing.push(`the page variable ${name}`)

	const references = HELP.split('\n\n').filter((p) => /Paste as reference/u.test(p))
	if (references.length === 0) missing.push('a recipe that places button references')
	for (const key of ['GO', 'PANIC', 'BLACKOUT'])
		if (!references.some((p) => new RegExp(`\\b${key}\\b`, 'u').test(p)))
			missing.push(`a button reference mirroring ${key} on every page`)

	const kinds = new Set(ticked(paragraph('Triggers from the show.')))
	for (const kind of ['item.end', 'fault.raised', 'scan.done'])
		if (!kinds.has(kind)) missing.push(`a trigger on ${kind}`)
	assert.deepEqual(missing, [], 'HELP.md lacks recipes the design lists')
})
