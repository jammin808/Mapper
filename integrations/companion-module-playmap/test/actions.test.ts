// Actions as Companion runs them: one generated action per catalogue row (the session's own words and every row that carries a
// secret aside), id v_<name>, writing the node's grammar; the GO, NEXT, PREV, BLACKOUT, FREEZE and panic keys press on the down
// with no held group, PANIC CLEAR's is held, and a key of a generated row presses as the row's press field says; the lines the node's contract test parses are the ones the module
// writes now; two presses confirm within 3 s and only then, each line armed on its key with its own 3 s (a key with two such
// actions confirms both on its second press); the double-GO guard holds a second GO of a playlist within min_go_ms;
// raw and ask_node never put a session word, a secret or (for ask_node) a change on the wire; learn fills from the focused
// playlist; every press answers ok, stage, effect and error. An older node's untyped VERBS keeps the bundled rows' forms and presses.
import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import type { CompanionOptionValues, JsonValue } from '@companion-module/base'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { advance, fakeChannels, settle } from './fake-channel.ts'
import { boot, configFor, type Booted } from './harness.ts'
import { allLines, sectionLines, statePaths } from './emitted.ts'
import { PANIC_OUTCOMES, actionDefinitions, fence, rowOfLine } from '../src/actions.ts'
import { BUNDLED_VERBS, EXCLUDED, actionIdOf, generatedRows, pressOfLine, type VerbRow } from '../src/catalog/verbs.ts'
import { lineOf, optionIdOf, optionsOf, sampleOf } from '../src/catalog/write.ts'
import type { PlayMapConfig } from '../src/config.ts'
import { stepsFor } from '../src/presets.ts'

type Fields = Record<string, unknown>

/** A press's result as an object (every press here answers one). */
function fields(v: JsonValue | undefined): Fields {
	assert.ok(v !== null && typeof v === 'object' && !Array.isArray(v), `a result object, not ${JSON.stringify(v)}`)
	return v as Fields
}

/** A line as the node reads it, without the module's request id. */
const bare = (line: string): string => line.replace(/^@c\d+ /u, '')

/** Whether a line (without its id) is of this verb. */
const isOf = (line: string, verb: string): boolean => line === verb || line.startsWith(verb + ' ')

/** The lines the node heard with a request id of the module's, without it. */
const numbered = (node: FakeNode): string[] => node.lines.filter((l) => /^@c\d+ /u.test(l)).map(bare)

/** The last line of this verb the node heard. */
const lastOf = (node: FakeNode, verb: string): string | undefined =>
	numbered(node)
		.filter((l) => isOf(l, verb))
		.at(-1)

/** A row's line with every required parameter of its first form given a sample value. */
function sampleLine(row: VerbRow): string {
	const options: CompanionOptionValues = { form: 0 }
	for (const p of row.formParams[0] ?? []) if (!p.optional) options[optionIdOf(p, 0)] = sampleOf(p)
	const written = lineOf(row, options)
	assert.ok('line' in written, `${row.name}: ${'error' in written ? written.error : ''}`)
	return written.line
}

/** Linked to the fake node, its STATE heard. */
async function ready(m: Booted): Promise<void> {
	await m.host.until(
		() => m.instance.linked() && m.host.variables.get('node_name') === 'Test node',
		'linked, with STATE',
	)
}

/** The pairing token the fake nodes ask for (the paired path: the greeting STATE follows OK paired). */
const TOKEN = 'K7QM-3XWD-P9RA'
const SECRETS = { token: TOKEN }

/** The config for a fake node with the double-GO guard off (a test that wants it sets it). */
const configOf = (node: FakeNode, extra: PlayMapConfig = {}): PlayMapConfig =>
	configFor(node.webPort, node.wirePort, { min_go_ms: 0, ...extra })

/** What Companion's learn button hands back for an action with these options. */
async function learn(m: Booted, actionId: string, options: CompanionOptionValues): Promise<CompanionOptionValues> {
	const learned = await m.wrapper.learnActionWithSignal(
		{ id: `learn-${actionId}`, controlId: 'key-learn', actionId, options },
		new AbortController().signal,
	)
	assert.ok(learned.options, `${actionId} learned something`)
	return learned.options
}

interface PresetAction {
	readonly actionId: string
	readonly options?: Fields
}
type Step = Record<string, unknown>
interface Variant {
	readonly type: string
	readonly steps: Step[]
}

/** A step's numbered (held for a duration) groups. */
const heldGroups = (step: Step): string[] => Object.keys(step).filter((k) => /^\d+$/u.test(k))

/** A preset's two variants as Companion keeps them: the layered key, then the simple one. */
function variantsOf(m: Booted, id: string): Variant[] {
	const preset = m.host.presets[id] as { type?: string; variants?: Variant[] } | undefined
	assert.ok(preset, `preset ${id}`)
	assert.equal(preset.type, 'alternatives', id)
	assert.deepEqual(
		preset.variants?.map((v) => v.type),
		['layered', 'simple'],
		id,
	)
	return preset.variants ?? []
}

/** The key presses on the down: one action there, nothing on the up, no group held for a duration. */
function assertOnDown(variant: Variant, actionId: string, where: string): PresetAction {
	assert.equal(variant.steps.length, 1, `${where}: one step`)
	const step = variant.steps[0] ?? {}
	assert.deepEqual(heldGroups(step), [], `${where}: no held group`)
	const down = (step.down as PresetAction[] | undefined) ?? []
	assert.equal(down.length, 1, `${where}: one action on the down`)
	const action = down[0]
	assert.ok(action, where)
	assert.equal(action.actionId, actionId, where)
	assert.deepEqual(step.up ?? [], [], `${where}: nothing on the up`)
	return action
}

// On fake timers, first in the file: no socket an earlier test closed is still closing on the real clock while this one mocks it.
void test('two presses confirm within 3 s and only then; the double-GO guard holds a second GO within min_go_ms', async (t) => {
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
		ch.say('STATE ' + JSON.stringify(fixture('state.json')))
		await advance(t.mock.timers, 100)

		// The channel forgets lines once PINGs are answered; the log keeps every one.
		const log: string[] = []
		const sent = (verb: string): string[] => [...log, ...ch.sent].map(bare).filter((l) => isOf(l, verb))
		const wait = async (ms: number): Promise<void> => {
			for (let left = ms; left > 0; left -= 250) {
				log.push(...ch.sent)
				ch.pong(Date.now())
				await advance(t.mock.timers, Math.min(250, left), 10)
			}
		}
		/** Presses; a line it sends is answered OK. */
		const press = async (
			actionId: string,
			options: CompanionOptionValues,
			controlId: string,
			verb: string,
		): Promise<Fields> => {
			const before = sent(verb).length
			const result = m.press(actionId, options, controlId)
			await settle()
			if (sent(verb).length > before) ch.ok(verb)
			return fields(await result)
		}
		const fb = (id: string): unknown => m.host.feedbackValues.get(id)

		// Two presses plus a hold: the first arms its own key (SURE?), only a second within 3 s sends.
		const power = { device: 'dv0a1b2c3d', state: 'OFF', confirm: 'twice' }
		await m.place('sure', 'confirm_pending', {}, 'key-power')
		await m.place('sure-other', 'confirm_pending', {}, 'key-other')
		await advance(t.mock.timers, 60, 10)
		assert.equal(fb('sure'), false)
		const armed = await press('v_device_power', power, 'key-power', 'DEVICE POWER')
		assert.equal(armed.ok, false)
		assert.equal(armed.stage, 'not-sent')
		await advance(t.mock.timers, 60, 10)
		assert.equal(fb('sure'), true, 'SURE? on the key pressed')
		assert.equal(fb('sure-other'), false)
		assert.deepEqual(sent('DEVICE POWER'), [])
		const other = await press('v_device_power', power, 'key-other', 'DEVICE POWER')
		assert.equal(other.stage, 'not-sent', 'another key arms itself; it confirms nothing of this one')
		assert.deepEqual(sent('DEVICE POWER'), [])

		await wait(2800)
		assert.equal(fb('sure'), true, 'still armed before 3 s')
		const confirmed = await press('v_device_power', power, 'key-power', 'DEVICE POWER')
		assert.equal(confirmed.stage, 'applied')
		assert.deepEqual(sent('DEVICE POWER'), ['DEVICE POWER dv0a1b2c3d OFF'])
		await advance(t.mock.timers, 60, 10)
		assert.equal(fb('sure'), false, 'confirmed: SURE? is gone')

		// Past 3 s an armed key disarms: the next press arms it again and sends nothing.
		const rearmed = await press('v_device_power', power, 'key-power', 'DEVICE POWER')
		assert.equal(rearmed.stage, 'not-sent')
		await advance(t.mock.timers, 60, 10)
		assert.equal(fb('sure'), true)
		await wait(3000)
		assert.equal(fb('sure'), false, 'disarmed after 3 s')
		assert.equal(fb('sure-other'), false, 'the other key disarmed too')
		const late = await press('v_device_power', power, 'key-power', 'DEVICE POWER')
		assert.equal(late.stage, 'not-sent', 'a press after 3 s only arms')
		assert.deepEqual(sent('DEVICE POWER'), ['DEVICE POWER dv0a1b2c3d OFF'])
		const once = await press(
			'v_device_power',
			{ device: 'dv0a1b2c3d', state: 'ON', confirm: 'none' },
			'key-on',
			'DEVICE POWER',
		)
		assert.equal(once.stage, 'applied', 'a key set to send on the press sends at once')
		assert.deepEqual(sent('DEVICE POWER'), ['DEVICE POWER dv0a1b2c3d OFF', 'DEVICE POWER dv0a1b2c3d ON'])
		await wait(3000)

		// The double-GO guard (min_go_ms 300 by default): a playlist's second GO within 300 ms is held, whichever key sends it.
		await m.place('cool', 'go_cooldown', { playlist: SHOW.playlist }, 'key-cool')
		await m.place('cool-loops', 'go_cooldown', { playlist: SHOW.loops }, 'key-cool-loops')
		const go = { playlist: '', expect: true }
		const first = await press('go_focused', go, 'key-go', 'PLAYLIST GO')
		assert.equal(first.stage, 'applied')
		assert.deepEqual(
			sent('PLAYLIST GO'),
			[`PLAYLIST GO ${SHOW.playlist} EXPECT ${SHOW.blue}`],
			'GO with EXPECT the next',
		)
		await advance(t.mock.timers, 60, 10)
		assert.equal(fb('cool'), true)
		await wait(140)
		const held = await press('go_focused', go, 'key-go', 'PLAYLIST GO')
		assert.equal(held.ok, false)
		assert.equal(held.stage, 'not-sent')
		const heldElsewhere = await press('v_playlist_go', { playlist: SHOW.playlist }, 'key-go-2', 'PLAYLIST GO')
		assert.equal(heldElsewhere.stage, 'not-sent', 'the guard is the playlist’s, whichever key sends the GO')
		const loops = await press('v_playlist_go', { playlist: SHOW.loops }, 'key-loops', 'PLAYLIST GO')
		assert.equal(loops.stage, 'applied', 'another playlist goes')
		const panic = await press('panic', { outcome: '' }, 'key-panic', 'PANIC')
		assert.equal(panic.stage, 'applied', 'PANIC is never held')
		assert.deepEqual(sent('PLAYLIST GO'), [
			`PLAYLIST GO ${SHOW.playlist} EXPECT ${SHOW.blue}`,
			`PLAYLIST GO ${SHOW.loops}`,
		])
		await wait(99)
		const stillHeld = await press('go_focused', go, 'key-go', 'PLAYLIST GO')
		assert.equal(stillHeld.stage, 'not-sent', 'held at 299 ms')
		await wait(1)
		const third = await press('go_focused', go, 'key-go', 'PLAYLIST GO')
		assert.equal(third.stage, 'applied', 'sent at 300 ms')
		assert.equal(sent('PLAYLIST GO').length, 3)

		// Each playlist's cooling light goes out when its own window ends (Loops' at 500 ms), whatever GOs came after.
		await wait(260)
		assert.equal(fb('cool-loops'), false, 'Loops cooled at 500 ms')
	} finally {
		await m.stop()
	}
})

void test('one action per catalogue row but the module’s own words and secrets, v_<name>, its press shape from the row', async () => {
	const m = await boot({}, {})
	try {
		const rows = generatedRows(BUNDLED_VERBS)
		const generated = [...m.host.actions.keys()].filter((id) => id.startsWith('v_')).sort()
		assert.deepEqual(generated, rows.map((r) => actionIdOf(r.name)).sort())
		assert.equal(new Set(generated).size, rows.length, 'no two rows share an action id')
		assert.equal(actionIdOf('PLAYLIST GO'), 'v_playlist_go')
		assert.equal(actionIdOf('PANIC CLEAR'), 'v_panic_clear')

		// The session's words, pictures and every row carrying a secret or a pairing token have no action.
		for (const name of [
			'HELLO',
			'AUTH',
			'AUTH-PROOF',
			'PING',
			'VERBS',
			'SUBSCRIBE',
			'WHOAMI',
			'PRESENCE',
			'THUMB',
			'VIEW',
			'PAIRING NEW',
			'LINK PATTERNS',
			'LINK STAGEWATCH',
		])
			assert.ok(EXCLUDED.has(name), name)
		for (const row of BUNDLED_VERBS) {
			if (/^CREDENTIAL /u.test(row.name)) assert.ok(EXCLUDED.has(row.name), row.name)
			if (EXCLUDED.has(row.name)) assert.equal(m.host.actions.has(actionIdOf(row.name)), false, row.name)
		}

		const defs = actionDefinitions(m.instance)
		for (const row of rows) {
			const id = actionIdOf(row.name)
			const def = m.host.actions.get(id)
			assert.ok(def, id)
			assert.equal(def.hasResult, true, `${id} answers a result`)
			const own = defs[id]
			assert.ok(own, id)
			const ids = own.options.map((o) => o.id)
			assert.equal(new Set(ids).size, ids.length, `${id}: option ids are unique`)
			for (const o of ids) assert.doesNotMatch(o, /token|secret|password/iu, `${id} holds no secret`)
			// The press shape is the row's: a double row confirms twice by default, every other row sends on the press.
			const confirm = def.options.find((o) => o.id === 'confirm') as { default?: unknown } | undefined
			assert.equal(confirm?.default, row.press === 'double' ? 'twice' : 'none', `${id} confirm`)
			// A line's press is its row's, unless a word of it names a risk (press.test.ts holds that rule).
			if (row.riskWhen.length === 0) assert.equal(m.instance.pressOf(sampleLine(row)), row.press, `${row.name} press`)
			else assert.equal(m.instance.pressOf(sampleLine(row)), pressOfLine(row, sampleLine(row)), `${row.name} press`)
		}
		// The design's named rows are instant on every key the module makes of them; every other high-risk row is held, lifting a
		// panic (by its alias too), the master, a stop and an LED output among them.
		for (const line of [
			'PANIC',
			'PANIC BLACK',
			'BLACKOUT ON',
			'FREEZE ON',
			'PLAYLIST GO pl0a1b2c3d',
			'PLAYLIST NEXT pl0a1b2c3d',
			'PLAYLIST PREV pl0a1b2c3d',
			'VIDEO PLAY pl0a1b2c3d',
			'VIDEO RESUME pl0a1b2c3d',
			'VIDEO PAUSE pl0a1b2c3d',
		])
			assert.equal(m.instance.pressOf(line), 'instant', line)
		for (const line of [
			'PANIC CLEAR',
			'PANIC OFF',
			'MASTER 0',
			'VIDEO STOP pl0a1b2c3d',
			'VIDEO SEEK pl0a1b2c3d 10',
			'LED OUTPUT OFF ALL',
			'TIMECODE CHASE tc0a1b2c3d ON',
		])
			assert.equal(m.instance.pressOf(line), 'hold', line)
	} finally {
		await m.stop()
	}
})

void test('PANIC (every outcome), BLACKOUT, FREEZE, GO, NEXT and PREV keys press on the down, never held; PANIC CLEAR is held', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configOf(node), SECRETS)
	try {
		await m.host.until(
			() => m.host.structure.some((s) => (s as { id?: string }).id === 'playlists'),
			'the playlists section from INDEX',
		)
		const show = m.host.structure.find((s) => (s as { id?: string }).id === 'show') as
			{ definitions: { id: string; presets?: string[] }[] } | undefined
		assert.ok(show, 'the Show section')
		const panicIds = show.definitions.find((d) => d.id === 'show_panic')?.presets ?? []
		assert.equal(panicIds.length, PANIC_OUTCOMES.length + 1, 'a key per outcome, and PANIC CLEAR')

		const outcomes: unknown[] = []
		let clears = 0
		for (const id of panicIds)
			for (const variant of variantsOf(m, id)) {
				const step = variant.steps[0] ?? {}
				const held = ((step['800'] as { actions?: PresetAction[] } | undefined)?.actions ?? [])[0]
				if (held?.actionId === actionIdOf('PANIC CLEAR')) {
					// Lifting a panic puts content back on the air: held 0.8 s, nothing on the down or the up.
					const where = `${id} ${variant.type}`
					assert.equal(id, 'show_panic_clear', where)
					assert.equal(variant.steps.length, 1, where)
					assert.deepEqual(heldGroups(step), ['800'], where)
					assert.deepEqual((step['800'] as { options?: Fields }).options, { runWhileHeld: true }, where)
					assert.deepEqual(step.down, [], where)
					assert.deepEqual(step.up, [], where)
					clears++
					continue
				}
				const panic = assertOnDown(variant, 'panic', `${id} ${variant.type}`)
				if (variant.type === 'layered') outcomes.push(panic.options?.outcome)
			}
		assert.deepEqual([...outcomes].sort(), [...PANIC_OUTCOMES].sort())
		assert.equal(clears, 2, 'PANIC CLEAR, layered and simple, held')

		for (const [id, actionId, options] of [
			['show_go', 'go_focused', { playlist: '', expect: true }],
			['show_next', 'next_focused', { playlist: '', expect: true }],
			['show_prev', 'prev_focused', { playlist: '' }],
			['show_blackout', 'blackout_toggle', {}],
			['show_freeze', 'freeze_toggle', {}],
		] as const)
			for (const variant of variantsOf(m, id))
				assert.deepEqual(assertOnDown(variant, actionId, `${id} ${variant.type}`).options, options)
		for (const [id, actionId] of [
			['item_key', actionIdOf('PLAYLIST GO')],
			['playlist_key', 'go_focused'],
		] as const)
			for (const variant of variantsOf(m, id)) assertOnDown(variant, actionId, `${id} ${variant.type}`)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a key of a generated row, or of the module’s own GO, NEXT, PREV, PANIC, BLACKOUT and FREEZE, presses as its row’s press field says', async () => {
	const index = copy(fixture('index.json')) as { surfaces: unknown[] }
	index.surfaces.push({ id: 'sf00000009', name: 'Wall', tags: ['wall'], source: '' })
	const node = await new FakeNode({ index, token: TOKEN }).start()
	const m = await boot(configOf(node), SECRETS)
	try {
		await m.host.until(
			() => m.host.structure.some((s) => (s as { id?: string }).id === 'surfaces'),
			'the surfaces section from INDEX',
		)
		const byAction = new Map(generatedRows(BUNDLED_VERBS).map((r) => [actionIdOf(r.name), r]))
		// The module's own actions send these rows' lines.
		for (const [actionId, name] of [
			['go_focused', 'PLAYLIST GO'],
			['next_focused', 'PLAYLIST NEXT'],
			['prev_focused', 'PLAYLIST PREV'],
			['panic', 'PANIC'],
			['blackout_toggle', 'BLACKOUT'],
			['freeze_toggle', 'FREEZE'],
		] as const) {
			const row = BUNDLED_VERBS.find((r) => r.name === name)
			assert.ok(row, name)
			byAction.set(actionId, row)
		}
		const seen = new Set<string>()
		const keyed = new Map<string, string>()
		for (const [id, preset] of Object.entries(m.host.presets))
			for (const variant of (preset as { variants?: Variant[] }).variants ?? [])
				for (const step of variant.steps) {
					const down = (step.down as PresetAction[] | undefined) ?? []
					const held = heldGroups(step).flatMap((k) => (step[k] as { actions?: PresetAction[] }).actions ?? [])
					for (const action of [...down, ...held]) {
						const row = byAction.get(action.actionId)
						if (!row) continue
						const where = `${id} (${variant.type}): ${row.name} is ${row.press}`
						seen.add(row.press)
						keyed.set(id, row.name)
						if (row.press === 'instant') {
							assert.ok(down.includes(action), where)
							assert.deepEqual(heldGroups(step), [], where)
							continue
						}
						assert.deepEqual(down, [], where)
						assert.deepEqual(heldGroups(step), ['800'], where)
						assert.deepEqual((step['800'] as { options?: Fields }).options, { runWhileHeld: true }, where)
						assert.equal(action.options?.confirm, row.press === 'double' ? 'twice' : undefined, where)
					}
				}
		assert.ok(
			seen.has('instant') && seen.has('hold'),
			`keys of instant and held rows were checked (${[...seen].join(', ')})`,
		)
		// Every hard-coded key that presses a row was checked, PANIC CLEAR's (held) among them.
		for (const [id, name] of [
			['show_go', 'PLAYLIST GO'],
			['show_next', 'PLAYLIST NEXT'],
			['show_prev', 'PLAYLIST PREV'],
			['show_blackout', 'BLACKOUT'],
			['show_freeze', 'FREEZE'],
			['show_panic', 'PANIC'],
			['show_panic_clear', 'PANIC CLEAR'],
			['item_key', 'PLAYLIST GO'],
			['playlist_key', 'PLAYLIST GO'],
			['output_key', 'OUTPUT IDENTIFY'],
			['surface_show', 'SURFACE SHOW'],
			['surface_hide', 'SURFACE SHOW'],
		] as const)
			assert.equal(keyed.get(id), name, `${id} presses ${name}`)
		assert.equal(BUNDLED_VERBS.find((r) => r.name === 'PANIC CLEAR')?.press, 'hold')

		// The one shape each press takes on a key.
		const action = { actionId: actionIdOf('DEVICE POWER'), options: { device: 'dv0a1b2c3d', state: 'OFF' } }
		assert.deepEqual(stepsFor('instant', action), [{ down: [action], up: [] }])
		assert.deepEqual(stepsFor('hold', action), [
			{ down: [], up: [], 800: { options: { runWhileHeld: true }, actions: [action] } },
		])
		assert.deepEqual(stepsFor('double', action), [
			{
				down: [],
				up: [],
				800: {
					options: { runWhileHeld: true },
					actions: [{ ...action, options: { ...action.options, confirm: 'twice' } }],
				},
			},
		])
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a key with two actions pressed twice arms each for its own line, and its second press confirms both', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configOf(node), SECRETS)
	try {
		await ready(m)
		await m.place('sure', 'confirm_pending', {}, 'key-both')
		// One button step: both projectors on, each a projector's power (pressed twice by its row).
		const both = ['dv00000001', 'dv00000002'].map((device) => ({ device, state: 'ON', confirm: 'twice' }))
		const press = async (): Promise<Fields[]> => {
			const out: Fields[] = []
			for (const options of both) out.push(fields(await m.press('v_device_power', options, 'key-both')))
			return out
		}
		const first = await press()
		assert.deepEqual(
			first.map((r) => [r.stage, r.error]),
			[
				['not-sent', 'press again within 3 s to confirm'],
				['not-sent', 'press again within 3 s to confirm'],
			],
			'the first press arms each action’s line',
		)
		assert.equal(m.instance.confirms.pending('key-both'), true)
		await m.host.until(() => m.host.feedbackValues.get('sure') === true, 'SURE? on the key')
		const second = await press()
		assert.deepEqual(
			second.map((r) => r.stage),
			['applied', 'applied'],
			'the second press confirms both',
		)
		assert.equal(m.instance.confirms.pending('key-both'), false, 'nothing is left armed')
		await m.host.until(() => m.host.feedbackValues.get('sure') === false, 'SURE? gone')
		assert.deepEqual(
			node.sent().filter((l) => isOf(l, 'DEVICE POWER')),
			['DEVICE POWER dv00000001 ON', 'DEVICE POWER dv00000002 ON'],
			'each projector’s line sent once, on the second press',
		)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('CURSOR is no key: its lease lapses unless it is sent again, so no action sends it and raw refuses it unsent', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configOf(node), SECRETS)
	try {
		await ready(m)
		assert.ok(EXCLUDED.has('CURSOR'))
		assert.equal(m.host.actions.has('v_cursor'), false, 'no generated action of CURSOR')
		assert.ok(
			!allLines().some((l) => rowOfLine(BUNDLED_VERBS, l)?.name === 'CURSOR'),
			'no line of the module is a CURSOR',
		)
		const r = fields(await m.press('raw', { line: 'CURSOR op_1 0.5 0.5' }, 'key-cursor'))
		assert.equal(r.ok, false)
		assert.equal(r.stage, 'not-sent')
		const after = fields(await m.press('raw', { line: 'PLAYLIST LIST' }, 'key-after'))
		assert.equal(after.stage, 'applied')
		assert.ok(!node.sent().some((l) => isOf(l, 'CURSOR')), 'the node never heard a CURSOR')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a line names its row as the node reads it: by name or alias, VT and CLIP as VIDEO, TC as TIMECODE; an alias is its row', async () => {
	// Every alias VERBS lists resolves to its row, alone or with a word after it.
	let aliases = 0
	for (const row of BUNDLED_VERBS)
		for (const alias of row.aliases) {
			aliases++
			assert.equal(rowOfLine(BUNDLED_VERBS, alias)?.name, row.name, alias)
			assert.equal(rowOfLine(BUNDLED_VERBS, `${alias} x0a1b2c3d`)?.name, row.name, `${alias} x0a1b2c3d`)
		}
	assert.ok(aliases > 20, `${aliases} aliases`)
	for (const [line, name] of [
		['VT PLAY', 'VIDEO PLAY'],
		['clip pause', 'VIDEO PAUSE'],
		['VT RESUME', 'VIDEO PLAY'],
		['TC STATUS', 'TIMECODE STATUS'],
		['EYE NEXT', 'FLEET NEXT'],
		['"PLAYLIST GO" pl0a1b2c3d', 'PLAYLIST GO'],
	] as const)
		assert.equal(rowOfLine(BUNDLED_VERBS, line)?.name, name, line)

	const node = await new FakeNode({ token: TOKEN }).start()
	node.handlers.set('EYE', () => 'OK {"eyes":[]}')
	const m = await boot(configOf(node), SECRETS)
	try {
		await ready(m)
		// Patterns' words are sent as typed: the node reads them as their rows.
		for (const line of ['EYE NEXT', 'VT PLAY', 'IDENTIFY op_1', 'TC STATUS']) {
			const r = fields(await m.press('raw', { line }, 'key-alias'))
			assert.equal(r.stage, 'applied', line)
			assert.equal(lastOf(node, line), line, line)
		}
		assert.deepEqual(fields(await m.press('ask_node', { line: 'EYE' }, 'key-ask')), { ok: true, reply: { eyes: [] } })
		// An alias of a row the deck keeps to itself is that row, and refused as it is.
		const status = fields(await m.press('raw', { line: 'STATUS' }, 'key-status'))
		assert.equal(status.stage, 'not-sent')
		assert.equal(status.error, "STATE is the module's own, never sent from a key")
		await m.press('raw', { line: 'PLAYLIST LIST' }, 'key-after')
		assert.ok(!node.sent().some((l) => isOf(l, 'STATUS')), 'STATUS never reached the node')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('test/lines.txt, test/section-lines.txt and test/state-paths.txt are what the module writes now', () => {
	const read = (name: string): string => readFileSync(new URL(`./${name}`, import.meta.url), 'utf8')
	const lines = allLines()
	assert.equal(read('lines.txt'), lines.join('\n') + '\n', 'npm run lines writes test/lines.txt again')
	// The section keys' and Live slots' lines, as the module fills them from the INDEX fixture (a sample row for a section with none).
	const sectioned = sectionLines()
	assert.equal(
		read('section-lines.txt'),
		sectioned.join('\n') + '\n',
		'npm run lines writes test/section-lines.txt again',
	)
	assert.ok(sectioned.includes('ALIGN RUN "Front wall"'), 'an id with a space is quoted, one word')
	assert.ok(
		sectioned.includes('PANIC CLEAR') && sectioned.includes('STING Bloom'),
		'a row that is its line, and a row by id',
	)
	assert.ok(!sectioned.includes('MASTER'), 'an encoder row is never sent bare')
	for (const line of sectioned) assert.deepEqual(fence(BUNDLED_VERBS, line), { ok: true }, line)
	assert.equal(
		read('state-paths.txt'),
		statePaths().join('\n') + '\n',
		'npm run lines writes test/state-paths.txt again',
	)
	for (const line of lines) {
		assert.doesNotMatch(line, /[\r\n]/u, line)
		assert.ok(Buffer.byteLength(line, 'utf8') + 16 <= 64 * 1024, line)
		const row = rowOfLine(BUNDLED_VERBS, line)
		assert.ok(row, `${line} starts with a verb of the catalogue`)
		assert.ok(
			!['AUTH', 'PAIRING NEW', 'LINK PATTERNS', 'LINK STAGEWATCH'].includes(row.name) &&
				!/^CREDENTIAL /u.test(row.name),
			`${line}: no line of the module's carries a secret`,
		)
		// Every line an action writes passes the fence; only the session's own lines (HELLO, PING …) are kept from keys.
		if (!EXCLUDED.has(row.name)) assert.deepEqual(fence(BUNDLED_VERBS, line), { ok: true }, line)
	}
	const named = new Set(lines.map((l) => rowOfLine(BUNDLED_VERBS, l)?.name))
	for (const row of generatedRows(BUNDLED_VERBS)) assert.ok(named.has(row.name), `${row.name} has a line in the file`)
})

void test('a generated action writes the node’s grammar: phrases quoted, keywords, optional words left out, the chosen form', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configOf(node), SECRETS)
	try {
		await ready(m)
		const cases: [string, CompanionOptionValues, string][] = [
			['v_playlist_new', { name: 'Front wall' }, 'PLAYLIST NEW "Front wall"'],
			['v_playlist_new', { name: 'Say "hi" \\ bye' }, 'PLAYLIST NEW "Say \\"hi\\" \\\\ bye"'],
			[
				'v_playlist_go',
				{ playlist: SHOW.playlist, item: SHOW.blue, at: '+500', expect: SHOW.blue },
				`PLAYLIST GO ${SHOW.playlist} ${SHOW.blue} AT +500 EXPECT ${SHOW.blue}`,
			],
			['v_playlist_go', { playlist: SHOW.playlist, item: '', at: '', expect: '' }, `PLAYLIST GO ${SHOW.playlist}`],
			[
				'v_generator_set',
				{
					form: 1,
					generator: 'gn_other',
					param: 'rate',
					value: '9',
					f1_generator: 'gn_testcard',
					f1_param: 'level',
					f1_by: '-2',
				},
				'GENERATOR SET gn_testcard level BY -2',
			],
			[
				'v_generator_set',
				{ form: 0, generator: 'gn_testcard', param: 'level', value: '0.5', f1_by: '7' },
				'GENERATOR SET gn_testcard level 0.5',
			],
			['v_show_set', { form: 1, f1_state: 'ON' }, 'SHOW SET REMOTE-VIEW-ONLY ON'],
			[
				'v_panic',
				{ choice: 'BLACK', on: 'op_1 op_2', fade: '1000', at: '', here: true },
				'PANIC BLACK ON op_1 op_2 FADE 1000 HERE',
			],
			[
				'v_set',
				{ id: 'sf00000009', field_path: 'label', json: '{"label": "Front wall"}' },
				'SET sf00000009 label {"label": "Front wall"}',
			],
			['v_master', { value: '55', fade: '', here: false }, 'MASTER 55'],
			// A word whose first mark opens JSON is quoted: bare, the node would read it and the rest of the line as one JSON word.
			[
				'v_device_add',
				{ name: '[L]', protocol: 'pjlink', host: '10.0.0.20', projector: '', output: '' },
				'DEVICE ADD "[L]" pjlink 10.0.0.20',
			],
			['v_generator_new', { kind: 'flat', name: '{W}', preset: 'warm' }, 'GENERATOR NEW flat "{W}" PRESET warm'],
		]
		for (const [actionId, options, line] of cases) {
			const verb = rowOfLine(BUNDLED_VERBS, line)?.name ?? ''
			const result = fields(await m.press(actionId, options, 'key-grammar'))
			assert.equal(result.stage, 'applied', `${actionId} ${JSON.stringify(options)}`)
			assert.equal(lastOf(node, verb), line, `${actionId} ${JSON.stringify(options)}`)
		}

		// A required word missing: nothing is sent, and the result says so.
		const missing = fields(await m.press('v_playlist_loop', { playlist: '', state: 'ON' }, 'key-missing'))
		assert.equal(missing.ok, false)
		assert.equal(missing.stage, 'not-sent')
		assert.equal(missing.reply, '')
		assert.ok(typeof missing.error === 'string' && missing.error.length > 0, 'the result says why')
		const fence = fields(await m.press('v_playlist_list', {}, 'key-fence'))
		assert.equal(fence.stage, 'applied')
		assert.equal(lastOf(node, 'PLAYLIST LIST'), 'PLAYLIST LIST')
		assert.equal(lastOf(node, 'PLAYLIST LOOP'), undefined, 'the unwritable line never reached the node')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('an older node’s VERBS, with no typed forms or press, keeps the bundled rows and takes only what the node answers', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	// A node from before typed forms: its rows say no params, formParams, press or riskWhen. It does not answer SCAN START, and it
	// has a row this module does not know.
	const older = (
		JSON.parse(readFileSync(new URL('../src/catalog/verbs.json', import.meta.url), 'utf8')) as Fields[]
	).map((row) => {
		const { params: _p, formParams: _f, press: _r, riskWhen: _w, ...rest } = row
		return rest.name === 'SCAN START' ? { ...rest, available: false } : rest
	})
	older.push({
		name: 'OLD THING',
		syntax: 'OLD THING <id>',
		words: 'a row since retired',
		risk: 'high',
		available: true,
	})
	node.handlers.set('VERBS', () => `OK ${JSON.stringify(older)}`)
	const m = await boot(configOf(node), SECRETS)
	try {
		await ready(m)
		await m.host.until(
			() => m.host.actions.get(actionIdOf('SCAN START'))?.description?.includes('does not answer') === true,
			'the node’s VERBS taken',
		)
		const optionIds = (id: string): string[] => (m.host.actions.get(id)?.options ?? []).map((o) => o.id)
		assert.deepEqual(
			optionIds('v_playlist_go').filter((o) => o !== 'confirm'),
			['playlist', 'item', 'at', 'expect'],
			'the action keeps its typed options',
		)
		assert.deepEqual(
			optionIds('v_device_power').filter((o) => o !== 'confirm'),
			['device', 'state', 'p_confirm'],
		)
		assert.equal(m.instance.pressOf('OUTPUT IDENTIFY op_1'), 'hold', 'an identify is still held')
		assert.equal(m.instance.pressOf('DEVICE POWER dv1 OFF'), 'double', "a projector's power is still pressed twice")
		assert.equal(m.instance.pressOf('DECODE POLICY SOFTWARE FORCE'), 'hold', 'a risk a word names is still the row’s')
		assert.equal(
			m.instance.verbs.find((r) => r.name === 'SCAN START')?.available,
			false,
			'what the node answers is its own',
		)
		assert.equal(m.host.actions.has('v_old_thing'), false, 'a row the module cannot type is left out, never guessed')

		const result = fields(await m.press('v_playlist_go', { playlist: SHOW.playlist, item: SHOW.blue }, 'key-older'))
		assert.equal(result.stage, 'applied')
		assert.equal(lastOf(node, 'PLAYLIST GO'), `PLAYLIST GO ${SHOW.playlist} ${SHOW.blue}`)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a keyword and its choice are one field; a shaped value is labelled and written in its shape; a trail is pairs', () => {
	const row = (name: string): VerbRow => {
		const found = BUNDLED_VERBS.find((r) => r.name === name)
		assert.ok(found, name)
		return found
	}
	const scan = row('SCAN START')
	const mode = optionsOf(scan, () => []).find((f) => f.id === 'mode')
	assert.equal(mode?.type, 'dropdown')
	assert.equal(mode?.label, 'MODE mode — optional')
	assert.equal(
		optionsOf(scan, () => []).filter((f) => f.id.startsWith('choice')).length,
		0,
		'no choice parted from its keyword',
	)
	assert.deepEqual(lineOf(scan, { camera: 'cm_1', projector: '', mode: 'ROBUST', into: '' }), {
		line: 'SCAN START cm_1 MODE ROBUST',
	})
	assert.deepEqual(lineOf(scan, { camera: 'cm_1', projector: '', mode: '', into: '' }), { line: 'SCAN START cm_1' })

	const send = row('SEND ADD')
	const fps = optionsOf(send, () => []).find((f) => f.id === 'fps')
	assert.equal(fps?.label, 'Fps (as @<fps>) — optional')
	assert.equal(fps?.tooltip, 'Written as @<fps>')
	const fpsParam = send.formParams[0]?.find((p) => p.name === 'fps')
	assert.ok(fpsParam)
	assert.equal(sampleOf(fpsParam), '@50')
	assert.deepEqual(
		lineOf(send, {
			kind: 'NDI',
			name: 'Stage left',
			tap: 'output:op_1',
			size: '1920x1080',
			fps: '@50',
			format: '',
			quality: 'HIGH',
			always: false,
		}),
		{ line: 'SEND ADD NDI "Stage left" output:op_1 1920x1080 @50 QUALITY HIGH' },
	)

	const cursor = row('CURSOR')
	const trail = cursor.formParams[0]?.find((p) => p.name === 'trail')
	assert.ok(trail)
	assert.equal(sampleOf(trail), '-0.5 -0.5', 'a repeated number samples as a pair')
	assert.deepEqual(lineOf(cursor, { output: 'op_1', x: '0.5', y: '0.5', trail: '0.1 0.2 0.3 0.4', color: '#00FF00' }), {
		line: 'CURSOR op_1 0.5 0.5 TRAIL 0.1 0.2 0.3 0.4 COLOR #00FF00',
	})
	assert.equal(row('AUDIO GAIN').formParams[0]?.[0]?.repeats, undefined, 'a range inside a placeholder is not a repeat')
	assert.deepEqual(lineOf(row('MEDIA HAS'), { sha256: 'sha256:ab cd' }), { line: 'MEDIA HAS "sha256:ab cd"' })
})

void test('raw refuses session and secret words, ask_node refuses anything that changes the show; neither sends them', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const listed = { playlists: [{ id: SHOW.playlist, name: 'Show' }] }
	node.handlers.set('PLAYLIST LIST', () => `OK ${JSON.stringify(listed)}`)
	const m = await boot(configOf(node), SECRETS)
	try {
		await ready(m)
		const from = node.lines.length
		const rawRefused = [
			'AUTH K7QM-3XWD-P9RA',
			'auth k7qm-3xwd-p9ra',
			'CREDENTIAL SET device:dv00000001 admin hunter2',
			'CREDENTIAL CLEAR device:dv00000001',
			'LINK PATTERNS 10.0.0.9:9940 K7QM-3XWD-P9RA',
			'LINK STAGEWATCH 10.0.0.9 K7QM-3XWD-P9RA',
			'PAIRING NEW',
			'HELLO Companion:another',
			'SUBSCRIBE STATE',
			'NOSUCH VERB',
		]
		for (const line of rawRefused) {
			const r = fields(await m.press('raw', { line }, 'key-raw'))
			assert.equal(r.ok, false, line)
			assert.equal(r.stage, 'not-sent', line)
		}
		const askRefused = [
			`PLAYLIST GO ${SHOW.playlist}`,
			'PANIC',
			'BLACKOUT ON',
			'AUTH K7QM-3XWD-P9RA',
			'CREDENTIAL LIST',
			'NOSUCH VERB',
		]
		for (const line of askRefused) {
			const r = fields(await m.press('ask_node', { line }, 'key-ask'))
			assert.equal(r.ok, false, line)
		}

		// What they do send: a line of the vocabulary as a press, and a question whose answer is the result.
		const sent = fields(await m.press('raw', { line: `PLAYLIST LOOP ${SHOW.playlist} ON` }, 'key-raw'))
		assert.equal(sent.stage, 'applied')
		const asked = fields(await m.press('ask_node', { line: 'PLAYLIST LIST' }, 'key-ask'))
		assert.deepEqual(asked, { ok: true, reply: listed })

		const after = node.lines.slice(from).map(bare)
		assert.ok(after.includes(`PLAYLIST LOOP ${SHOW.playlist} ON`))
		assert.ok(after.includes('PLAYLIST LIST'))
		for (const line of [...rawRefused, ...askRefused])
			assert.ok(!after.some((l) => l.toUpperCase() === line.toUpperCase()), `${line} never reached the node`)
		for (const verb of ['AUTH', 'CREDENTIAL', 'LINK', 'PAIRING', 'HELLO', 'PANIC', 'BLACKOUT', 'PLAYLIST GO', 'NOSUCH'])
			assert.ok(!after.some((l) => isOf(l.toUpperCase(), verb)), `no ${verb} line was sent`)
		assert.ok(!node.lines.some((l) => l.includes('hunter2') || /k7qm/iu.test(l)), 'no secret rode a line')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a line break never carries a second line onto the TCP wire', async () => {
	const node = await new FakeNode({ noLineChannel: true, token: TOKEN }).start()
	const m = await boot(configOf(node), SECRETS)
	try {
		await ready(m)
		assert.equal([...node.sessions][0]?.kind, 'tcp')
		const raw = fields(await m.press('raw', { line: 'PLAYLIST LIST\nAUTH K7QM-3XWD-P9RA' }, 'key-raw'))
		const asked = fields(await m.press('ask_node', { line: 'PLAYLIST LIST\nPANIC' }, 'key-ask'))
		const named = fields(await m.press('v_playlist_new', { name: 'Front\nPANIC' }, 'key-new'))
		// TCP keeps order: once this is answered, every line before it has been read.
		const fence = fields(await m.press('raw', { line: 'PLAYLIST LIST' }, 'key-fence'))
		assert.equal(fence.stage, 'applied')
		assert.deepEqual(
			node.lines.filter((l) => !/^@c\d+ /u.test(l)),
			[],
			'every line the node read is one the module numbered: nothing rode behind a line break',
		)
		assert.equal(raw.ok, false)
		assert.equal(raw.stage, 'not-sent')
		assert.equal(asked.ok, false)
		assert.equal(named.ok, false)
		assert.equal(named.stage, 'not-sent')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('learn fills the playlist from the focused one and the item from its current item', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configOf(node), SECRETS)
	try {
		await ready(m)
		assert.equal(m.host.actions.get('v_playlist_go')?.hasLearn, true)
		assert.equal(m.host.actions.get('v_playlist_loop')?.hasLearn, true)
		assert.equal(m.host.actions.get('v_blackout')?.hasLearn, false, 'nothing to learn without a playlist or an item')

		// What is learned is what the node would take: a GO naming its item needs no EXPECT (with one, it must be that item).
		const go = await learn(m, 'v_playlist_go', { playlist: '', item: '', at: '', expect: SHOW.blue })
		assert.equal(go.playlist, SHOW.playlist, 'the focused playlist')
		assert.equal(go.item, SHOW.red, 'its item on the air')
		assert.equal(go.expect, '', 'no EXPECT that would contradict the item named')
		// NEXT and PREV expect their step in INDEX's order (Intro, Red, Blue): what the node would play for them.
		await m.host.until(() => m.instance.model.index !== null, 'INDEX heard')
		assert.deepEqual(await learn(m, 'v_playlist_next', { playlist: '', expect: '' }), {
			playlist: SHOW.playlist,
			expect: SHOW.blue,
		})
		assert.deepEqual(await learn(m, 'v_playlist_prev', { playlist: '', expect: '' }), {
			playlist: SHOW.playlist,
			expect: SHOW.intro,
		})
		assert.deepEqual(await learn(m, 'v_playlist_loop', { playlist: '', state: 'ON' }), { playlist: SHOW.playlist })
		const play = await learn(m, 'v_video_play', { on: '', at: '', expect: '' })
		assert.equal(play.on, SHOW.playlist)
		assert.equal(play.expect, SHOW.red)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('learn on a focused playlist with nothing on the air clears the item, never keeping another playlist’s', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configOf(node), SECRETS)
	try {
		await ready(m)
		const state = copy(fixture('state.json')) as { seq: number; playlists: { focused: boolean }[] }
		state.seq += 1
		for (const [i, p] of state.playlists.entries()) p.focused = i === 1
		node.pushState(state)
		await m.host.until(() => m.host.variables.get('focused') === SHOW.loops, 'Loops focused')
		// Companion merges what learn answers into the options it had: an item left unanswered stays the old one.
		const go = await learn(m, 'v_playlist_go', { playlist: SHOW.playlist, item: SHOW.red, at: '', expect: SHOW.red })
		assert.equal(go.playlist, SHOW.loops)
		assert.equal(go.item, '', 'Loops has nothing on the air: the item is left out, not kept from Show')
		assert.equal(go.expect, 'it00000005', 'EXPECT is what a GO of Loops plays next (its Green), never kept from Show')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('learn reads the greeting STATE of a node that asks no token', async () => {
	const node = await new FakeNode().start()
	const m = await boot(configOf(node), {})
	try {
		// The node greets with STATE right after its OK to HELLO, before it answers SUBSCRIBE INDEX: once INDEX is in, so is STATE.
		await m.host.until(
			() => m.instance.linked() && m.host.structure.some((s) => (s as { id?: string }).id === 'playlists'),
			'linked, with INDEX',
		)
		assert.equal(m.host.variables.get('node_name'), 'Test node', 'the greeting STATE was read')
		const go = await learn(m, 'v_playlist_go', { playlist: '', item: '', at: '', expect: '' })
		assert.equal(go.playlist, SHOW.playlist)
		assert.equal(go.item, SHOW.red)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('every press answers ok, stage, effect and error: applied, accepted, refused, not sent', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	node.handlers.set('BLACKOUT', () => 'OK blackout on — every output black')
	node.handlers.set('FREEZE', () => 'OK {"stage":"accepted","effect":"frozen at the next frame"}')
	node.handlers.set('PLAYLIST LOOP', () => 'ERR already not looping')
	const m = await boot(configOf(node), SECRETS)
	try {
		await ready(m)
		for (const id of [
			'go_focused',
			'next_focused',
			'prev_focused',
			'panic',
			'blackout_toggle',
			'freeze_toggle',
			'slot_press',
			'section_press',
			'raw',
			'ask_node',
		])
			assert.equal(m.host.actions.get(id)?.hasResult, true, `${id} answers a result`)

		assert.deepEqual(fields(await m.press('v_blackout', { state: 'ON' }, 'key-r')), {
			ok: true,
			stage: 'applied',
			effect: 'blackout on — every output black',
			error: '',
			reply: 'blackout on — every output black',
		})
		assert.deepEqual(fields(await m.press('v_freeze', { state: 'ON' }, 'key-r')), {
			ok: true,
			stage: 'accepted',
			effect: 'frozen at the next frame',
			error: '',
			reply: '{"stage":"accepted","effect":"frozen at the next frame"}',
		})
		assert.deepEqual(fields(await m.press('v_playlist_loop', { playlist: SHOW.playlist, state: 'OFF' }, 'key-r')), {
			ok: false,
			stage: 'refused',
			effect: '',
			error: 'already not looping',
			reply: 'already not looping',
		})
		const notSent = fields(await m.press('v_playlist_loop', { playlist: '', state: 'OFF' }, 'key-r'))
		assert.equal(notSent.ok, false)
		assert.equal(notSent.stage, 'not-sent')
		assert.equal(notSent.effect, '')
		assert.ok(typeof notSent.error === 'string' && notSent.error.length > 0)
		const smart = fields(await m.press('blackout_toggle', {}, 'key-r'))
		assert.equal(smart.stage, 'applied')
		assert.equal(lastOf(node, 'BLACKOUT'), 'BLACKOUT TOGGLE')
		const panic = fields(await m.press('panic', { outcome: 'SAFE' }, 'key-r'))
		assert.equal(panic.ok, true)
		assert.equal(lastOf(node, 'PANIC'), 'PANIC SAFE')
	} finally {
		await m.stop()
		await node.stop()
	}

	// No node: the press is not sent, and its result says so.
	const alone = await boot({}, {})
	try {
		const r = fields(await alone.press('v_blackout', { state: 'ON' }, 'key-r'))
		assert.equal(r.ok, false)
		assert.equal(r.stage, 'not-sent')
		assert.equal(r.reply, '')
		assert.ok(typeof r.error === 'string' && r.error.length > 0)
	} finally {
		await alone.stop()
	}
})

void test('a field shown by an expression reads only fields that are never expressions (base 2.2)', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configOf(node), SECRETS)
	try {
		await ready(m)
		let checked = 0
		const definitions = [...m.host.actions.values(), ...m.host.feedbacks.values()] as unknown as {
			id: string
			options: { id: string; isVisibleExpression?: string; disableAutoExpression?: boolean }[]
		}[]
		for (const def of definitions)
			for (const o of def.options) {
				if (!o.isVisibleExpression) continue
				for (const [, ref] of o.isVisibleExpression.matchAll(/\$\(options:([a-zA-Z0-9_]+)\)/gu)) {
					const field = def.options.find((x) => x.id === ref)
					assert.equal(field?.disableAutoExpression, true, `${def.id}.${o.id} reads ${ref}`)
					checked++
				}
			}
		assert.ok(checked > 100, `${checked} references checked`)
	} finally {
		await m.stop()
		await node.stop()
	}
})
