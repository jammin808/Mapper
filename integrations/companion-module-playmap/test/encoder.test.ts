// Encoders turned as a preset's rotate steps turn them, with $(this:delta) evaluated as Companion evaluates it: turns within 30 ms are
// one line; an absolute verb (MASTER, VIDEO RATE, VIDEO SEEK) sends the target STATE's value plus the turned steps, steered by the
// shadow (targets sent and not yet confirmed) and clamped to its range, so a run of turns never sends past the end even before STATE
// confirms, and a key shows STATE alone; a relative verb (GENERATOR SET … BY) sends delta × step; a turn while the node is not linked,
// or one still pending when the link drops, is never sent.
import test from 'node:test'
import assert from 'node:assert/strict'
import type { CompanionOptionValues, JsonValue } from '@companion-module/base'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { fakeChannels, settle, type FakeChannel } from './fake-channel.ts'
import { boot, configFor, type Booted } from './harness.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

type Expression = { isExpression: true; value: string }

interface PresetAction {
	readonly actionId: string
	readonly options?: Record<string, unknown>
}

interface PlaylistJson {
	id: string
	state: string
	positionMs: number | null
	lengthMs: number | null
	rate: number
}

interface StateJson {
	at: number
	air: { master: { level: number; to: number } }
	playlists: PlaylistJson[]
}

const isExpression = (v: unknown): v is Expression =>
	typeof v === 'object' &&
	v !== null &&
	(v as Expression).isExpression === true &&
	typeof (v as Expression).value === 'string'

/** An option expression as Companion evaluates it for one turn: $(this:delta) is the surface's turn (undefined on a surface that
 * gives none), $(local:<name>) the key's local variable, abs() the expression language's. */
function evaluate(expression: string, delta: number | undefined, locals: Readonly<Record<string, string>>): JsonValue {
	const js = expression
		.replaceAll('$(this:delta)', delta === undefined ? '(undefined)' : `(${delta})`)
		.replace(/\$\(local:([a-z_]+)\)/gu, (_, name: string) => JSON.stringify(locals[name] ?? ''))
		.replace(/\babs\(/gu, 'Math.abs(')
	// eslint-disable-next-line @typescript-eslint/no-implied-eval
	return new Function(`return (${js})`)() as JsonValue
}

/** The action one turn of a preset runs (rotate_right for a positive turn, rotate_left for a negative one), its options evaluated;
 * `step` replaces the preset's step when an operator changed it on the key; an older surface gives no $(this:delta), only the
 * direction, and turns by one detent. */
function turnOf(
	m: Booted,
	presetId: string,
	delta: number,
	locals: Readonly<Record<string, string>> = {},
	step?: string,
	older = false,
): { actionId: string; options: CompanionOptionValues } {
	const preset = m.host.presets[presetId] as
		{ variants?: { steps?: Record<string, PresetAction[] | undefined>[] }[] } | undefined
	const action = preset?.variants?.[0]?.steps?.[0]?.[delta < 0 ? 'rotate_left' : 'rotate_right']?.[0]
	assert.ok(action, `${presetId} has a ${delta < 0 ? 'rotate_left' : 'rotate_right'} action`)
	const options: CompanionOptionValues = {}
	for (const [id, v] of Object.entries(action.options ?? {}))
		options[id] = isExpression(v) ? evaluate(v.value, older ? undefined : delta, locals) : (v as JsonValue)
	assert.equal(options.delta, older ? Math.sign(delta) : delta, `${presetId}: the turn's delta`)
	if (step !== undefined) options.step = step
	return { actionId: action.actionId, options }
}

/** One turn of a preset's dial on a control. */
async function turn(
	m: Booted,
	presetId: string,
	delta: number,
	locals: Readonly<Record<string, string>> = {},
	step?: string,
	older = false,
): Promise<void> {
	const { actionId, options } = turnOf(m, presetId, delta, locals, step, older)
	await m.press(actionId, options, `dial-${presetId}`)
}

/** A line as the node reads it, without the module's request id. */
const bare = (line: string): string => line.replace(/^@c\d+ /u, '')

void test('turns within 30 ms are one line; MASTER, RATE and SEEK never pass their range, even before STATE confirms; unlinked turns are dropped', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', port: 9940 }, {}, { factory, random: () => 0.5 })
	try {
		const first = opened[0]
		assert.ok(first)
		let ch: FakeChannel = first

		// The node: PINGs answered with its clock and every line of the encoders' verbs applied (OK); STATE is what the test says.
		const answered = new Set<string>()
		const serve = (): void => {
			for (const l of ch.sent) {
				const [id = '', verb = '', t1 = ''] = l.split(' ')
				const key = `${opened.indexOf(ch)}:${id}`
				if (answered.has(key)) continue
				if (verb === 'PING') ch.say(`OK ${id} PONG ${t1} ${Date.now()} ${Date.now()}`)
				else if (['MASTER', 'VIDEO', 'GENERATOR'].includes(verb)) ch.say(`OK ${id}`)
				else continue
				answered.add(key)
			}
		}
		const wait = async (ms: number): Promise<void> => {
			for (let left = ms; left > 0; left -= 10) {
				t.mock.timers.tick(Math.min(10, left))
				await settle()
				serve()
			}
		}
		const say = (edit: (s: StateJson) => void): void => {
			const s = copy(fixture('state.json')) as StateJson
			s.at = Date.now()
			edit(s)
			ch.say('STATE ' + JSON.stringify(s))
		}
		const master = (level: number) => (s: StateJson) => {
			s.air.master = { ...s.air.master, level, to: level }
		}
		/** Every line of this verb the node heard, on any channel, without ids. */
		const sent = (verb: string): string[] =>
			opened.flatMap((c) => c.sent.map(bare)).filter((l) => l === verb || l.startsWith(verb + ' '))

		// Not linked (the channel still opening, then HELLO not yet answered): a turn does nothing, then or once linked, even when
		// the link and STATE come within its 30 ms.
		await turn(m, 'enc_master_key', 3)
		await wait(100)
		ch.events.open({})
		await settle()
		await turn(m, 'enc_master_key', 3)
		await wait(10)
		ch.ok('HELLO')
		await settle()
		serve()
		say(master(0.5))
		await wait(100)
		assert.equal(m.instance.linked(), true)
		assert.equal(m.host.variables.get('master'), 50)
		assert.deepEqual(sent('MASTER'), [], 'a turn made while not linked is never sent')

		// Three turns within 30 ms of the first: one line at 30 ms, STATE's 50 plus (1 + 1 + 2) × 5.
		await turn(m, 'enc_master_key', 1)
		await wait(10)
		await turn(m, 'enc_master_key', 1)
		await wait(10)
		await turn(m, 'enc_master_key', 2)
		await wait(9)
		assert.deepEqual(sent('MASTER'), [], 'nothing before 30 ms')
		await wait(1)
		assert.deepEqual(sent('MASTER'), ['MASTER 70'])

		// Turns further apart are a line each, each from the shadow (the target sent, not yet in STATE), never from STATE's 50.
		await turn(m, 'enc_master_key', -1)
		await wait(40)
		await turn(m, 'enc_master_key', -1)
		await wait(40)
		assert.deepEqual(sent('MASTER'), ['MASTER 70', 'MASTER 65', 'MASTER 60'])
		assert.equal(m.host.variables.get('master'), 50, 'the shadow is never shown: the variable is STATE')

		// STATE confirms 60. A run of turns past the end stops at 100 before STATE says so, and sends nothing more there.
		say(master(0.6))
		await wait(50)
		assert.equal(m.host.variables.get('master'), 60)
		await turn(m, 'enc_master_key', 5)
		await wait(40)
		await turn(m, 'enc_master_key', 5)
		await wait(40)
		await turn(m, 'enc_master_key', 1)
		await wait(40)
		await turn(m, 'enc_master_key', 3)
		await turn(m, 'enc_master_key', 2)
		await wait(40)
		assert.deepEqual(sent('MASTER').slice(3), ['MASTER 85', 'MASTER 100'])

		// STATE confirms 100; a run down stops at 0 the same way.
		say(master(1))
		await wait(50)
		await turn(m, 'enc_master_key', -15)
		await wait(40)
		await turn(m, 'enc_master_key', -5)
		await wait(40)
		await turn(m, 'enc_master_key', -1)
		await wait(40)
		await turn(m, 'enc_master_key', -30)
		await wait(40)
		assert.deepEqual(sent('MASTER').slice(5), ['MASTER 25', 'MASTER 0'])
		for (const line of sent('MASTER')) {
			const level = Number(line.split(' ')[1])
			assert.ok(level >= 0 && level <= 100, `${line} is within 0-100`)
		}

		// VIDEO RATE on the focused playlist: 0.05 a detent from STATE's 1, within 0.05-20, the shadow steering past either end.
		await turn(m, 'enc_rate_key', 2)
		await wait(40)
		await turn(m, 'enc_rate_key', -400)
		await wait(40)
		await turn(m, 'enc_rate_key', -1)
		await wait(40)
		await turn(m, 'enc_rate_key', 1000)
		await wait(40)
		await turn(m, 'enc_rate_key', 1)
		await wait(40)
		assert.deepEqual(sent('VIDEO RATE'), [
			`VIDEO RATE 1.1 ON ${SHOW.playlist}`,
			`VIDEO RATE 0.05 ON ${SHOW.playlist}`,
			`VIDEO RATE 20 ON ${SHOW.playlist}`,
		])

		// VIDEO SEEK on the focused playlist, paused 4 s into a 10 s item: a second a detent, within the item.
		say((s) => {
			const show = s.playlists.find((p) => p.id === SHOW.playlist)
			assert.ok(show)
			Object.assign(show, { state: 'paused', positionMs: 4000, lengthMs: 10000 })
		})
		await wait(50)
		await turn(m, 'enc_seek_key', 3)
		await wait(40)
		await turn(m, 'enc_seek_key', 10)
		await wait(40)
		await turn(m, 'enc_seek_key', 1)
		await wait(40)
		await turn(m, 'enc_seek_key', -30)
		await wait(40)
		await turn(m, 'enc_seek_key', -1)
		await wait(40)
		assert.deepEqual(sent('VIDEO SEEK'), [
			`VIDEO SEEK 7 ON ${SHOW.playlist}`,
			`VIDEO SEEK 10 ON ${SHOW.playlist}`,
			`VIDEO SEEK 0 ON ${SHOW.playlist}`,
		])

		// A turn still pending when the link drops is never sent, nor is one made while the link is down, even just before it
		// comes back.
		say(master(0.3))
		await wait(50)
		const before = sent('MASTER')
		await turn(m, 'enc_master_key', 1)
		await wait(10)
		ch.close()
		await settle()
		assert.equal(m.instance.linked(), false)
		await turn(m, 'enc_master_key', 2)
		await wait(1000)
		const second = opened[1]
		assert.ok(second, 'the link opens again after its backoff')
		ch = second
		ch.events.open({})
		await settle()
		await turn(m, 'enc_master_key', 2)
		await wait(10)
		ch.ok('HELLO')
		await settle()
		serve()
		say(master(0.3))
		await wait(100)
		assert.equal(m.instance.linked(), true)
		assert.deepEqual(sent('MASTER'), before, 'nothing turned before or while the link was down is sent')

		// Linked again, a turn steers from the new STATE.
		await turn(m, 'enc_master_key', 1)
		await wait(40)
		assert.deepEqual(sent('MASTER'), [...before, 'MASTER 35'])
	} finally {
		await m.stop()
	}
})

void test('a generator parameter turns by delta × step: GENERATOR SET … BY the turn, coalesced within 30 ms', async () => {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configFor(node.webPort, node.wirePort), { token: TOKEN })
	try {
		await m.host.until(
			() => m.instance.linked() && 'generator_param_key' in m.host.presets,
			"linked, with INDEX's generator keys",
		)
		const white = { generator: 'gn00000009', param: 'level' }
		const generatorLines = (): string[] => node.sent().filter((l) => l.startsWith('GENERATOR SET '))
		let marks = 0
		/** A relative turn on another dial, made after the turns before it: once the node hears it, it heard whatever they sent. */
		const mark = async (): Promise<void> => {
			const device = `dvmark${++marks}`
			await m.press('enc_lens', { device, motor: 'ZOOM', step: '1', delta: 1 }, 'dial-mark')
			await node.heard(new RegExp(`DEVICE LENS ${device} ZOOM BY 1$`, 'u'))
		}

		// Three turns at once (within 30 ms): one line, by their sum.
		await turn(m, 'generator_param_key', 3, white)
		await turn(m, 'generator_param_key', -2, white)
		await turn(m, 'generator_param_key', 4, white)
		await mark()
		assert.deepEqual(generatorLines(), ['GENERATOR SET gn00000009 level BY 5'])

		// A turn to the left by 3 detents of 1, then a key whose step is 0.25 turned right by 3.
		await turn(m, 'generator_param_key', -3, white)
		await mark()
		await turn(m, 'generator_param_key', 3, white, '0.25')
		await mark()
		assert.deepEqual(generatorLines().slice(1), [
			'GENERATOR SET gn00000009 level BY -3',
			'GENERATOR SET gn00000009 level BY 0.75',
		])

		// Turns further apart than 30 ms are a line each: a relative verb keeps no shadow and no range, it sends the step.
		await turn(m, 'generator_param_key', 2, white)
		await mark()
		await turn(m, 'generator_param_key', 2, white)
		await mark()
		assert.deepEqual(generatorLines().slice(3), [
			'GENERATOR SET gn00000009 level BY 2',
			'GENERATOR SET gn00000009 level BY 2',
		])

		// A surface that gives no $(this:delta) turns by one detent: rotate_right +1, rotate_left -1.
		await turn(m, 'generator_param_key', 1, white, undefined, true)
		await mark()
		await turn(m, 'generator_param_key', -1, white, undefined, true)
		await mark()
		assert.deepEqual(generatorLines().slice(5), [
			'GENERATOR SET gn00000009 level BY 1',
			'GENERATOR SET gn00000009 level BY -1',
		])
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a surface’s opacity dial NUDGEs it by 0.05 a detent, signed, the turns within 30 ms one line', async () => {
	const index = copy(fixture('index.json') as { surfaces: unknown[] })
	index.surfaces = [{ id: 'sf00000010', name: 'Wall', tags: ['wall'], source: '' }]
	const node = await new FakeNode({ token: TOKEN, index }).start()
	const m = await boot(configFor(node.webPort, node.wirePort), { token: TOKEN })
	try {
		await m.host.until(() => m.instance.linked() && 'surface_opacity_key' in m.host.presets, 'linked, the opacity dial')
		const wall = { surface: 'sf00000010' }
		const nudges = (): string[] => node.sent().filter((l) => l.startsWith('NUDGE '))
		const mark = async (n: number): Promise<void> => {
			await m.press('enc_lens', { device: `dvmark${n}`, motor: 'ZOOM', step: '1', delta: 1 }, 'dial-mark')
			await node.heard(new RegExp(`DEVICE LENS dvmark${n} ZOOM BY 1$`, 'u'))
		}
		await turn(m, 'surface_opacity_key', 3, wall)
		await mark(1)
		await turn(m, 'surface_opacity_key', -2, wall)
		await mark(2)
		await turn(m, 'surface_opacity_key', 1, wall)
		await turn(m, 'surface_opacity_key', 1, wall)
		await mark(3)
		assert.deepEqual(nudges(), [
			'NUDGE sf00000010 opacity +0.15',
			'NUDGE sf00000010 opacity -0.1',
			'NUDGE sf00000010 opacity +0.1',
		])
	} finally {
		await m.stop()
		await node.stop()
	}
})
