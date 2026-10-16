// Variables are a pure function of what the node said: STATE and INDEX become the node, show, air, focus (Patterns' video names),
// fault, positional bank and per-id variables, every id one the host takes and every value set under a definition; per-id variables
// follow their entity through a reorder; every variable design 4.6 names is defined. Between pushes a playing item's clock runs at
// 10 Hz from STATE's positionMs, rate and at plus the PING offset, while the module hands Companion at most 25 variable updates a
// second; once the link drops, every value that came from STATE is emptied and only INDEX's names stay. A calibration score is read
// on the scale its unit says.
import test from 'node:test'
import assert from 'node:assert/strict'
import type { JsonValue } from '@companion-module/base'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { fakeChannels, settle } from './fake-channel.ts'
import { boot, configFor, logs, type Booted } from './harness.ts'
import { readFileSync } from 'node:fs'
import { NodeModel } from '../src/state/model.ts'
import { projectorScore, variablesOf, type ModuleFacts } from '../src/state/variables.ts'

const TOKEN = 'K7QM-3XWD-P9RA'
/** The host's rule for a variable id. */
const VARIABLE_ID = /^[a-zA-Z0-9-_.!]+$/u

interface PlaylistJson {
	id: string
	name: string
	focused: boolean
	state: string
	index: number
	item: { id: string; label: string; source: string } | null
	next: { id: string; label: string } | null
	positionMs: number | null
	lengthMs: number | null
	rate: number
}

interface StateJson {
	at: number
	light: string
	headline: string
	air: {
		blackout: boolean
		frozen: boolean
		identify: boolean
		pattern: unknown
		panic: unknown
		master: { level: number; to: number }
	}
	show: { armed: boolean; by: string; blind: unknown }
	playlists: PlaylistJson[]
	outputs: Record<string, unknown>[]
	peers: Record<string, unknown>[]
	faults: { part: string; words: string }[]
}

interface IndexJson {
	playlists: { id: string; name: string; items: unknown[] }[]
	projectors: unknown[]
	surfaces: unknown[]
}

const stateDoc = (): StateJson => copy(fixture('state.json') as StateJson)
const indexDoc = (): IndexJson => copy(fixture('index.json') as IndexJson)

/** The values the host holds for these ids. */
const valuesOf = (m: Booted, ids: readonly string[]): Record<string, JsonValue | undefined> =>
	Object.fromEntries(ids.map((id) => [id, m.host.variables.get(id)]))

/** Linked to a fake node over the line channel, its greeting STATE and INDEX turned into variables. */
async function linked(index?: unknown): Promise<{ node: FakeNode; m: Booted }> {
	const node = await new FakeNode({ token: TOKEN, ...(index ? { index } : {}) }).start()
	const m = await boot(configFor(node.webPort, node.wirePort), { token: TOKEN })
	try {
		await m.host.until(
			() =>
				m.host.variables.get('node_name') === 'Test node' &&
				m.host.variables.get(`pl.${SHOW.playlist}.state`) === 'playing',
			'the greeting STATE and INDEX as variables',
		)
	} catch (e) {
		await m.stop()
		await node.stop()
		throw e
	}
	return { node, m }
}

/** Every variable id the host was given is one it takes, and every value set has a definition (else the host drops it). */
function assertIdsValid(m: Booted): void {
	const bad = [...m.host.variableDefs.keys(), ...m.host.variables.keys()].filter((id) => !VARIABLE_ID.test(id))
	assert.deepEqual(bad, [], 'every variable id matches the host rule')
	const undefinedIds = [...m.host.variables.keys()].filter((id) => !m.host.variableDefs.has(id))
	assert.deepEqual(undefinedIds, [], 'every value set has a definition')
}

/** The host's warnings about variable ids since `from` (invalid or reserved ids it ignored). */
const variableWarnings = (from: number): string[] =>
	logs
		.slice(from)
		.filter((l) => l.level === 'warn' && /variable/iu.test(l.message))
		.map((l) => l.message)

void test("the STATE fixture becomes the node, show, air, focus, fault, bank and per-id variables, Patterns' video names kept", async () => {
	const from = logs.length
	const { node, m } = await linked()
	try {
		assert.deepEqual(
			valuesOf(m, [
				'node_name',
				'node_id',
				'node_version',
				'link',
				'light',
				'headline',
				'project',
				'project_dirty',
				'revision',
				'renderer',
				'clients',
				'show_mode',
				'show_by',
				'blind',
				'panic',
				'blackout',
				'frozen',
				'pattern',
				'identify',
				'master',
				'video_file',
				'video_tag',
				'video_length',
				'video_text',
				'video_playing',
				'focused',
				'focused_name',
				'current',
				'current_id',
				'next',
				'next_id',
				'faults_count',
				'fault_1',
				'fault_2',
				'fault_3',
				'media_ready',
				'media_missing',
				'transfers',
				'transfer_progress',
				'scan_state',
				'scan_stage',
				'scan_progress',
				'scan_words',
				'sync_role',
				'sync_leader',
				'sync_uncertainty_ms',
				'sync_rtt_ms',
				'patterns_link',
				'stagewatch_link',
				'stagewatch_timer',
				'stagewatch_phase',
				'ai_proposals',
				'cal_moved',
				'bpm',
			]),
			{
				node_name: 'Test node',
				node_id: '0badc0de',
				node_version: '0.1.0',
				link: 'live',
				light: 'red',
				headline: 'Show · playing Red, 0:09 left · outputs not measured',
				project: 'Test node',
				project_dirty: true,
				revision: 9,
				renderer: 'none',
				clients: 0,
				show_mode: 'REHEARSAL',
				show_by: '',
				blind: false,
				panic: '',
				blackout: false,
				frozen: false,
				pattern: '',
				identify: false,
				master: 100,
				video_file: 'Red',
				video_tag: 'Show',
				video_length: '0:10',
				video_text: 'Show Red · 0:01 / 0:10 · 0:09 left',
				video_playing: true,
				focused: SHOW.playlist,
				focused_name: 'Show',
				current: 'Red',
				current_id: SHOW.red,
				next: 'Blue',
				next_id: SHOW.blue,
				faults_count: 1,
				fault_1: 'the network announcement could not open: port 5353 is taken',
				fault_2: '',
				fault_3: '',
				media_ready: true,
				media_missing: 0,
				transfers: 1,
				transfer_progress: 0.25,
				scan_state: 'projecting',
				scan_stage: '12/40',
				scan_progress: 0.3,
				scan_words: 'pattern 12 of 40 on Left',
				sync_role: 'solo',
				sync_leader: '',
				sync_uncertainty_ms: 0,
				sync_rtt_ms: 0,
				patterns_link: 'off',
				stagewatch_link: 'live',
				stagewatch_timer: '4:59',
				stagewatch_phase: 'running',
				ai_proposals: 0,
				cal_moved: 0,
				bpm: 120,
			},
		)
		// Positional banks: the n-th playlist, output and machine of STATE; past the last, empty.
		assert.deepEqual(
			valuesOf(m, [
				'pl_1_id',
				'pl_1_name',
				'pl_1_state',
				'pl_1_item',
				'pl_1_next',
				'pl_2_id',
				'pl_2_name',
				'pl_2_state',
				'pl_2_item',
				'pl_2_next',
				'pl_2_remaining',
				'pl_2_progress',
				'pl_3_id',
				'pl_3_name',
				'op_1_id',
				'op_1_name',
				'op_1_light',
				'op_1_fps',
				'op_1_dropped',
				'op_2_id',
				'peer_1_name',
				'peer_1_light',
				'peer_1_headline',
				'peer_2_name',
				'peer_2_light',
			]),
			{
				pl_1_id: SHOW.playlist,
				pl_1_name: 'Show',
				pl_1_state: 'playing',
				pl_1_item: 'Red',
				pl_1_next: 'Blue',
				pl_2_id: SHOW.loops,
				pl_2_name: 'Loops',
				pl_2_state: 'idle',
				pl_2_item: '',
				pl_2_next: 'Green',
				pl_2_remaining: '',
				pl_2_progress: 0,
				pl_3_id: '',
				pl_3_name: '',
				op_1_id: 'op_1',
				op_1_name: 'Output 1',
				op_1_light: 'grey',
				op_1_fps: 0,
				op_1_dropped: 0,
				op_2_id: '',
				peer_1_name: 'Stage left',
				peer_1_light: 'amber',
				peer_1_headline: 'Stage left · output 2 dropping frames',
				peer_2_name: '',
				peer_2_light: '',
			},
		)
		// Per id: INDEX's playlists and outputs by their own ids.
		assert.deepEqual(
			valuesOf(m, [
				`pl.${SHOW.playlist}.name`,
				`pl.${SHOW.playlist}.state`,
				`pl.${SHOW.playlist}.item`,
				`pl.${SHOW.playlist}.next`,
				`pl.${SHOW.loops}.name`,
				`pl.${SHOW.loops}.state`,
				`pl.${SHOW.loops}.item`,
				`pl.${SHOW.loops}.next`,
				`pl.${SHOW.loops}.remaining`,
				`pl.${SHOW.loops}.progress`,
				'op.op_1.light',
				'op.op_1.fps',
			]),
			{
				[`pl.${SHOW.playlist}.name`]: 'Show',
				[`pl.${SHOW.playlist}.state`]: 'playing',
				[`pl.${SHOW.playlist}.item`]: 'Red',
				[`pl.${SHOW.playlist}.next`]: 'Blue',
				[`pl.${SHOW.loops}.name`]: 'Loops',
				[`pl.${SHOW.loops}.state`]: 'idle',
				[`pl.${SHOW.loops}.item`]: '',
				[`pl.${SHOW.loops}.next`]: 'Green',
				[`pl.${SHOW.loops}.remaining`]: '',
				[`pl.${SHOW.loops}.progress`]: 0,
				'op.op_1.light': 'grey',
				'op.op_1.fps': 0,
			},
		)
		assertIdsValid(m)
		assert.deepEqual(variableWarnings(from), [], 'the host warned of no variable id')

		// The show armed, the air changed, more faults: the variables follow the next STATE.
		const s = stateDoc()
		s.light = 'amber'
		s.headline = 'Show · armed'
		s.show = { ...s.show, armed: true, by: 'FOH desk', blind: { by: 'desk' } }
		s.air = {
			...s.air,
			blackout: true,
			frozen: true,
			identify: true,
			pattern: { kind: 'grid', outputs: ['op_1'] },
			panic: { outcome: 'BLACK' },
			master: { level: 0.42, to: 0.42 },
		}
		s.faults = [
			{ part: 'mdns', words: 'the network announcement could not open: port 5353 is taken' },
			{ part: 'output', words: 'Output 1 dropped 12 frames' },
			{ part: 'media', words: 'Red.mov is missing' },
		]
		node.pushState(s)
		await m.host.until(() => m.host.variables.get('master') === 42, 'the next STATE')
		assert.deepEqual(
			valuesOf(m, [
				'light',
				'headline',
				'show_mode',
				'show_by',
				'blind',
				'blackout',
				'frozen',
				'identify',
				'pattern',
				'panic',
				'master',
				'faults_count',
				'fault_2',
				'fault_3',
			]),
			{
				light: 'amber',
				headline: 'Show · armed',
				show_mode: 'ARMED',
				show_by: 'FOH desk',
				blind: true,
				blackout: true,
				frozen: true,
				identify: true,
				pattern: 'grid',
				panic: 'BLACK',
				master: 42,
				faults_count: 3,
				fault_2: 'Output 1 dropped 12 frames',
				fault_3: 'Red.mov is missing',
			},
		)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('per-id variables follow their entity through a reorder; positional banks follow the order', async () => {
	const { node, m } = await linked()
	try {
		const s = stateDoc()
		s.playlists.reverse()
		s.outputs = [{ ...s.outputs[0], id: 'op_2', name: 'Output 2', light: 'green', fps: 60 }, ...s.outputs]
		s.peers = [
			{ t: 'playmap', id: 'e5f6a7b8', name: 'Stage right', light: 'green', headline: 'Stage right · fine', live: true },
			...s.peers,
		]
		node.pushState(s)
		await m.host.until(() => m.host.variables.get('pl_1_id') === SHOW.loops, 'the reordered STATE')
		assert.deepEqual(
			valuesOf(m, [
				'pl_1_id',
				'pl_1_name',
				'pl_2_id',
				'pl_2_state',
				'focused',
				'current',
				`pl.${SHOW.playlist}.name`,
				`pl.${SHOW.playlist}.state`,
				`pl.${SHOW.playlist}.item`,
				`pl.${SHOW.loops}.state`,
				`pl.${SHOW.loops}.next`,
				'op_1_id',
				'op_1_light',
				'op_2_id',
				'op.op_1.light',
				'op.op_1.fps',
				'peer_1_name',
				'peer_2_name',
				'peer_2_light',
			]),
			{
				pl_1_id: SHOW.loops,
				pl_1_name: 'Loops',
				pl_2_id: SHOW.playlist,
				pl_2_state: 'playing',
				focused: SHOW.playlist,
				current: 'Red',
				[`pl.${SHOW.playlist}.name`]: 'Show',
				[`pl.${SHOW.playlist}.state`]: 'playing',
				[`pl.${SHOW.playlist}.item`]: 'Red',
				[`pl.${SHOW.loops}.state`]: 'idle',
				[`pl.${SHOW.loops}.next`]: 'Green',
				op_1_id: 'op_2',
				op_1_light: 'green',
				op_2_id: 'op_1',
				'op.op_1.light': 'grey',
				'op.op_1.fps': 0,
				peer_1_name: 'Stage right',
				peer_2_name: 'Stage left',
				peer_2_light: 'amber',
			},
		)
		// A machine's light by its id (design 4.6: peer.<id>.light), wherever it sits in the list; the same flush sets it.
		assert.deepEqual(valuesOf(m, ['peer.a1b2c3d4.light', 'peer.e5f6a7b8.light']), {
			'peer.a1b2c3d4.light': 'amber',
			'peer.e5f6a7b8.light': 'green',
		})
		assertIdsValid(m)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('an entity id the host would refuse becomes a variable id it takes; the host warns of none', async () => {
	const from = logs.length
	const index = indexDoc()
	index.playlists.push({ id: 'pl hand/made:1', name: 'Hand made', items: [] })
	const { node, m } = await linked(index)
	try {
		await m.host.until(() => m.host.variables.get('pl.plhandmade1.name') === 'Hand made', 'the hand-made playlist')
		assertIdsValid(m)
		assert.deepEqual(variableWarnings(from), [], 'the host warned of no variable id')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('every variable design 4.6 names is defined, positional banks to bank_size and per id for every entity', async () => {
	const index = indexDoc()
	index.projectors = [{ id: 'pj00000009', name: 'Left', output: 'op_1', device: null }]
	index.surfaces = [{ id: 'sf0000000a', name: 'Wall', tags: ['wall'], source: `playlist:${SHOW.playlist}` }]
	const { node, m } = await linked(index)
	try {
		await m.host.until(() => m.host.variableDefs.has('pl.pl00000001.progress'), 'definitions from INDEX')
		const banks = (prefix: string, fields: readonly string[]): string[] =>
			[1, 8].flatMap((n) => fields.map((f) => `${prefix}_${n}_${f}`))
		const named = [
			// Node
			'node_name',
			'node_id',
			'node_version',
			'node_protocol',
			'module_behind',
			'link',
			'light',
			'headline',
			'project',
			'project_dirty',
			'revision',
			'faults_count',
			'fault_1',
			'fault_2',
			'fault_3',
			'renderer',
			'clients',
			// Show and air
			'show_mode',
			'show_by',
			'blind',
			'panic',
			'blackout',
			'frozen',
			'pattern',
			'identify',
			'master',
			// The video row, Patterns' names
			'video_file',
			'video_tag',
			'video_position',
			'video_length',
			'video_remaining',
			'video_remaining_s',
			'video_chip',
			'video_text',
			'video_playing',
			'video_progress',
			// Focus
			'focused',
			'focused_name',
			'current',
			'current_id',
			'next',
			'next_id',
			// Media, scan, sync, links, AI
			'media_ready',
			'media_missing',
			'transfers',
			'transfer_progress',
			'scan_state',
			'scan_stage',
			'scan_progress',
			'scan_words',
			'sync_role',
			'sync_leader',
			'sync_uncertainty_ms',
			'sync_rtt_ms',
			'patterns_link',
			'stagewatch_link',
			'stagewatch_timer',
			'stagewatch_phase',
			'ai_proposals',
			// Calibration and audio (the fixture's node publishes both)
			'cal_score',
			'cal_grade',
			'cal_words',
			'cal_moved',
			'bpm',
			'level',
			// Receipts, events, the Live page
			'last_error',
			'last_refusal',
			'last_receipt',
			'event_last',
			'event_last_kind',
			'event_seq',
			'live_rule',
			'live_page',
			// Banks (bank_size 8)
			...banks('pl', ['id', 'name', 'state', 'item', 'next', 'remaining', 'progress']),
			...banks('op', ['id', 'name', 'light', 'fps', 'dropped']),
			...banks('pj', ['id', 'name', 'light', 'residual_px']),
			...banks('sf', ['id', 'name']),
			...banks('peer', ['name', 'light', 'headline']),
			// Per id
			...[SHOW.playlist, SHOW.loops].flatMap((id) =>
				['name', 'state', 'item', 'next', 'remaining', 'progress'].map((f) => `pl.${id}.${f}`),
			),
			...['light', 'score', 'power', 'shutter'].map((f) => `pj.pj00000009.${f}`),
			'pj_1_power',
			'pj_1_shutter',
			...['light', 'fps'].map((f) => `op.op_1.${f}`),
			'sf.sf0000000a.name',
			'peer.a1b2c3d4.light',
		]
		const missing = named.filter((id) => !m.host.variableDefs.has(id))
		assert.deepEqual(missing, [], 'every variable design 4.6 names is defined')
		// The rest of design 4.6's list reads facts the node does not publish in STATE: the AI's top proposal (STATE has the
		// count), a projector's temperature (its box reports light-source hours, not a temperature), a surface's shown flag, and the
		// recorder (RECORD is next round). None of them is defined, so no key reads a value the node never gave. A projector's power
		// and shutter are its box's, from STATE.devices.
		const unpublished = ['ai_top', 'ai_top_risk', 'pj_1_temp_c', 'sf_1_shown', 'recording']
		assert.deepEqual(
			unpublished.filter((id) => m.host.variableDefs.has(id)),
			[],
		)
		assertIdsValid(m)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("a calibration score is read on its unit's scale: px, lower better, becomes points whose grades are the node's", () => {
	const facts: ModuleFacts = {
		link: 'live',
		moduleBehind: false,
		lastError: '',
		lastRefusal: '',
		lastReceipt: '',
		eventLast: '',
		eventLastKind: '',
		eventSeq: 0,
		liveRule: 'focused',
		livePage: '',
	}
	const modelOf = (state: unknown): NodeModel => {
		const model = new NodeModel()
		model.setState(state)
		return model
	}
	// The node's own STATE (tests/fixtures/state/wall.json): Projector 1 solved to 0.2 px, grade A, green.
	const wall = JSON.parse(
		readFileSync(new URL('../../../tests/fixtures/state/wall.json', import.meta.url), 'utf8'),
	) as {
		calibration: { targets: Record<string, unknown>[] }
	}
	const one = wall.calibration.targets.find((t) => t.id === 'pj_test1')
	assert.deepEqual([one?.score, one?.unit, one?.grade, one?.light], [0.2, 'px', 'A', 'green'])
	const model = modelOf(wall)
	const score = projectorScore(model, 'pj_test1')
	assert.equal(score, 97, 'grade A at 0.2 px is a high score')
	// The projector preset's ground, colorMix('#E0342E', '#1E9E5A', score / 100), is on the green side.
	const mix = (red: number, green: number): number => red + (green - red) * (score / 100)
	assert.ok(mix(0x34, 0x9e) > mix(0xe0, 0x1e), 'more green than red')
	const v = variablesOf(model, Date.now(), facts, 8)
	assert.deepEqual([v.cal_score, v.cal_grade], [97, 'A'], 'the worst target (Projector 2, amber at 0.2 px) in points')

	// px converts piecewise so the grade boundaries coincide (A ≤ 1.0 px is ≥ 85, B ≤ 1.5 is ≥ 70, C ≤ 4 is ≥ 50); points are points.
	const scoreOf = (value: number, unit: string): number =>
		projectorScore(modelOf({ calibration: { targets: [{ id: 'pj_x', score: value, unit, light: 'green' }] } }), 'pj_x')
	assert.deepEqual(
		[0, 0.5, 1, 1.25, 1.5, 4, 6, 8, 99.9].map((px) => scoreOf(px, 'px')),
		[100, 93, 85, 78, 70, 50, 25, 0, 0],
	)
	assert.deepEqual(
		[100, 85, 70, 50, 0].map((points) => scoreOf(points, 'points')),
		[100, 85, 70, 50, 0],
	)
	// A solve with no calibration score is its residual on the same scale; a unit the module does not know is no score.
	const solved = { stage: { projectors: [{ id: 'pj_y', solved: true, residualPx: 1.5 }] } }
	assert.equal(projectorScore(modelOf(solved), 'pj_y'), 70)
	const unknown = { ...solved, calibration: { targets: [{ id: 'pj_y', score: 12, unit: 'furlongs', light: 'green' }] } }
	assert.equal(projectorScore(modelOf(unknown), 'pj_y'), 70)

	// The worst of targets with the same light is the lowest in points, whatever their units.
	const worst = (targets: unknown[]): unknown =>
		variablesOf(modelOf({ calibration: { targets } }), Date.now(), facts, 8).cal_score
	assert.equal(
		worst([
			{ id: 'a', score: 0.5, unit: 'px', light: 'green' },
			{ id: 'b', score: 80, unit: 'points', light: 'green' },
		]),
		80,
	)
	assert.equal(
		worst([
			{ id: 'a', score: 3, unit: 'px', light: 'amber' },
			{ id: 'b', score: 80, unit: 'points', light: 'amber' },
		]),
		58,
	)
})

void test('once the link drops, every value that came from STATE is emptied; INDEX names stay', async () => {
	const { node, m } = await linked()
	let stopped = false
	try {
		await node.stop()
		stopped = true
		await m.host.until(
			() => m.host.variables.get('link') === 'connecting' && m.host.variables.get('node_name') === '',
			'the dropped link, emptied',
		)
		assert.deepEqual(
			valuesOf(m, [
				'node_name',
				'node_id',
				'node_version',
				'node_protocol',
				'light',
				'headline',
				'project',
				'project_dirty',
				'revision',
				'renderer',
				'show_by',
				'blind',
				'panic',
				'blackout',
				'frozen',
				'pattern',
				'identify',
				'master',
				'video_file',
				'video_tag',
				'video_position',
				'video_length',
				'video_remaining',
				'video_remaining_s',
				'video_chip',
				'video_text',
				'video_playing',
				'video_progress',
				'focused',
				'focused_name',
				'current',
				'current_id',
				'next',
				'next_id',
				'faults_count',
				'fault_1',
				'media_ready',
				'sync_role',
				'patterns_link',
				'stagewatch_link',
				'bpm',
				'pl_1_id',
				'pl_1_name',
				'pl_1_state',
				'pl_1_item',
				'pl_1_next',
				'pl_1_remaining',
				'pl_1_progress',
				'op_1_id',
				'op_1_name',
				'op_1_light',
				'peer_1_name',
				'peer_1_light',
				'peer_1_headline',
				`pl.${SHOW.playlist}.state`,
				`pl.${SHOW.playlist}.item`,
				`pl.${SHOW.playlist}.next`,
				`pl.${SHOW.playlist}.remaining`,
				`pl.${SHOW.playlist}.progress`,
				'op.op_1.light',
				'op.op_1.fps',
				`pl.${SHOW.playlist}.name`,
				`pl.${SHOW.loops}.name`,
			]),
			{
				node_name: '',
				node_id: '',
				node_version: '',
				node_protocol: 0,
				light: 'grey',
				headline: '',
				project: '',
				project_dirty: false,
				revision: 0,
				renderer: '',
				show_by: '',
				blind: false,
				panic: '',
				blackout: false,
				frozen: false,
				pattern: '',
				identify: false,
				master: 0,
				video_file: '',
				video_tag: '',
				video_position: '',
				video_length: '',
				video_remaining: '',
				video_remaining_s: 0,
				video_chip: '',
				video_text: '',
				video_playing: false,
				video_progress: 0,
				focused: '',
				focused_name: '',
				current: '',
				current_id: '',
				next: '',
				next_id: '',
				faults_count: 0,
				fault_1: '',
				media_ready: false,
				sync_role: '',
				patterns_link: '',
				stagewatch_link: '',
				bpm: 0,
				pl_1_id: '',
				pl_1_name: '',
				pl_1_state: '',
				pl_1_item: '',
				pl_1_next: '',
				pl_1_remaining: '',
				pl_1_progress: 0,
				op_1_id: '',
				op_1_name: '',
				op_1_light: '',
				peer_1_name: '',
				peer_1_light: '',
				peer_1_headline: '',
				[`pl.${SHOW.playlist}.state`]: '',
				[`pl.${SHOW.playlist}.item`]: '',
				[`pl.${SHOW.playlist}.next`]: '',
				[`pl.${SHOW.playlist}.remaining`]: '',
				[`pl.${SHOW.playlist}.progress`]: 0,
				'op.op_1.light': 'grey',
				'op.op_1.fps': 0,
				// INDEX's names stay, so a dimmed key still says what it is.
				[`pl.${SHOW.playlist}.name`]: 'Show',
				[`pl.${SHOW.loops}.name`]: 'Loops',
			},
		)
		// Words that came from STATE are no fact once it is gone: no REHEARSAL, no idle scan, from a node not heard.
		assert.deepEqual(valuesOf(m, ['show_mode', 'scan_state']), { show_mode: '', scan_state: '' })
	} finally {
		await m.stop()
		if (!stopped) await node.stop()
	}
})

/** The node's clock minus the module's: two hours ahead, so a clock that ignored the PING offset would not move at all. */
const OFFSET_MS = 7_200_000

void test('between pushes the clocks run at 10 Hz from positionMs, rate, at and the PING offset; at most 25 updates a second', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', port: 9940 }, {}, { factory, random: () => 0.5 })
	try {
		const ch = opened[0]
		assert.ok(ch)
		const nodeNow = (): number => Date.now() + OFFSET_MS

		// The node answers each PING half way through the round trip, so the offset the module measures is exactly the node's.
		const answered = new Set<string>()
		const serve = (): void => {
			for (const l of ch.sent) {
				const [id = '', verb = '', t1 = ''] = l.split(' ')
				if (verb !== 'PING' || answered.has(id)) continue
				answered.add(id)
				const at = Number(t1) + OFFSET_MS + (Date.now() - Number(t1)) / 2
				ch.say(`OK ${id} PONG ${t1} ${at} ${at}`)
			}
		}

		// Each setVariableValues call's time, and each watched variable's changes since `origin`.
		const calls: number[] = []
		let counted = m.host.variableCalls
		const watched = [
			'video_remaining',
			'pl_1_remaining',
			`pl.${SHOW.playlist}.remaining`,
			'pl_2_remaining',
			`pl.${SHOW.loops}.remaining`,
			'pl_1_progress',
		]
		let origin = Date.now()
		let changes = new Map<string, [number, JsonValue | undefined][]>()
		const record = (): void => {
			for (; counted < m.host.variableCalls; counted++) calls.push(Date.now())
			for (const id of watched) {
				const log = changes.get(id) ?? []
				const v = m.host.variables.get(id)
				if (log.length === 0 || log.at(-1)?.[1] !== v) log.push([Date.now() - origin, v])
				changes.set(id, log)
			}
		}
		const wait = async (ms: number): Promise<void> => {
			for (let left = ms; left > 0; left -= 10) {
				t.mock.timers.tick(Math.min(10, left))
				await settle()
				serve()
				await settle() // the module reads each PONG at the instant it was answered
				record()
			}
		}

		/** STATE as the node writes it now: Show playing a 10 s item at rate 1 from 1 s in, Loops a 60 s item at rate 2 from its top,
		 * both since `started` on the node's clock. */
		let started = 0
		const playing = (headline?: string): StateJson => {
			const elapsed = nodeNow() - started
			const s = stateDoc()
			s.at = nodeNow()
			if (headline) s.headline = headline
			const [show, loops] = s.playlists
			assert.ok(show && loops)
			Object.assign(show, {
				state: 'playing',
				positionMs: 1000 + elapsed,
				lengthMs: 10000,
				remainingMs: 9000 - elapsed,
				rate: 1,
			})
			Object.assign(loops, {
				state: 'playing',
				index: 0,
				item: { id: 'it00000005', label: 'Green', source: 'color:#00FF00' },
				next: null,
				positionMs: 2 * elapsed,
				lengthMs: 60000,
				remainingMs: 60000 - 2 * elapsed,
				rate: 2,
			})
			return s
		}

		ch.events.open({})
		await settle()
		ch.ok('HELLO')
		await settle()
		serve()
		await settle()
		ch.ok('SUBSCRIBE INDEX', 'subscribed index')
		ch.say('INDEX ' + JSON.stringify(fixture('index.json')))
		await wait(100)
		assert.equal(m.instance.model.offsetMs, OFFSET_MS, 'the PING offset is the node’s')

		// One push; 40 ms later every clock reads it, moved on by those 40 ms on the node's clock.
		started = nodeNow()
		origin = Date.now()
		ch.say('STATE ' + JSON.stringify(playing()))
		await wait(50)
		assert.deepEqual(
			valuesOf(m, [
				'video_position',
				'video_remaining',
				'video_remaining_s',
				'video_chip',
				'video_progress',
				'pl_1_remaining',
				'pl_1_progress',
				`pl.${SHOW.playlist}.remaining`,
				`pl.${SHOW.playlist}.progress`,
				'pl_2_remaining',
				`pl.${SHOW.loops}.remaining`,
			]),
			{
				video_position: '0:01',
				video_remaining: '0:09',
				video_remaining_s: 9,
				video_chip: 'Show 0:09',
				video_progress: 0.105,
				pl_1_remaining: '0:09',
				pl_1_progress: 0.105,
				[`pl.${SHOW.playlist}.remaining`]: '0:09',
				[`pl.${SHOW.playlist}.progress`]: 0.105,
				pl_2_remaining: '1:00',
				[`pl.${SHOW.loops}.remaining`]: '1:00',
			},
		)

		// No push for 3 s: each countdown turns over within one 100 ms tick (and the 40 ms flush) of the second it crosses; Loops at
		// rate 2 every half second; the progress gauge steps every tick.
		changes = new Map()
		record()
		await wait(3150)
		const turnsOver = (id: string, values: string[], everyMs: number): void => {
			const log = changes.get(id) ?? []
			assert.deepEqual(
				log.map(([, v]) => v),
				values,
				`${id} counts down`,
			)
			log.slice(1).forEach(([at], k) => {
				const due = (k + 1) * everyMs
				assert.ok(at >= due && at <= due + 140, `${id} turned to ${String(values[k + 1])} at ${at} ms, due ${due} ms`)
			})
		}
		for (const id of ['video_remaining', 'pl_1_remaining', `pl.${SHOW.playlist}.remaining`])
			turnsOver(id, ['0:09', '0:08', '0:07', '0:06'], 1000)
		for (const id of ['pl_2_remaining', `pl.${SHOW.loops}.remaining`])
			turnsOver(id, ['1:00', '0:59', '0:58', '0:57', '0:56', '0:55', '0:54'], 500)
		const steps = (changes.get('pl_1_progress') ?? []).filter(([at]) => at > 100 && at <= 1100)
		assert.equal(steps.length, 10, 'the progress gauge moves ten times a second')

		// Five pushes a second for 2 s, then fifty a second for 1 s, each with new words: never more than 25 updates in any second,
		// the clocks continuous across the pushes, the last push's words shown.
		const pushStart = calls.length
		for (let n = 1; n <= 10; n++) {
			ch.say('STATE ' + JSON.stringify(playing(`Show · push ${n}`)))
			await wait(200)
		}
		assert.equal(m.host.variables.get('video_remaining'), '0:04', '9 s less the 5.2 s since the start, counted up')
		for (let n = 11; n <= 60; n++) {
			ch.say('STATE ' + JSON.stringify(playing(`Show · push ${n}`)))
			await wait(20)
		}
		await wait(100)
		assert.equal(m.host.variables.get('headline'), 'Show · push 60')
		const pushed = calls.slice(pushStart)
		assert.ok(pushed.length > 0)
		const busiest = Math.max(...pushed.map((at) => pushed.filter((x) => x > at - 1000 && x <= at).length))
		assert.ok(busiest <= 25, `${busiest} setVariableValues calls in one second`)
		const all = Math.max(...calls.map((at) => calls.filter((x) => x > at - 1000 && x <= at).length))
		assert.ok(all <= 25, `${all} setVariableValues calls in one second over the whole run`)
	} finally {
		await m.stop()
	}
})
