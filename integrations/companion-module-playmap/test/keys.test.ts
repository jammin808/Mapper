// What keys say, read from what the node publishes: a projector's power and shutter are its box's (STATE.devices, linked by
// projector), a box's own key shows its light and power; a scan runs while its state is not an end; a playlist key keeps its
// name when the link drops (INDEX still names it — it is not REMOVED); a machine's key follows its name and headline when only the
// peers move; the Show Mode key says ARMED or REHEARSAL and the master dial its 0-100.
import test from 'node:test'
import assert from 'node:assert/strict'
import { isDeepStrictEqual } from 'node:util'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'

interface StateJson {
	stage: { projectors: unknown[] }
	devices: Record<string, unknown>[]
	scan: Record<string, unknown> | null
	peers: Record<string, unknown>[]
	show: { armed: boolean }
	air: { master: { level: number } }
}
interface IndexJson {
	projectors: unknown[]
}

async function values(m: Booted, expected: Record<string, unknown>, what: string): Promise<void> {
	const held = (): Record<string, unknown> =>
		Object.fromEntries(
			Object.keys(expected).map((id) => [id, m.host.feedbackValues.get(id) ?? m.host.variables.get(id)]),
		)
	await m.host.until(() => isDeepStrictEqual(held(), expected), what).catch(() => undefined)
	assert.deepEqual(held(), expected, what)
}

const PJ = 'pj00000009'

void test('a projector’s power and shutter are its box’s; a box’s key shows its light and power; a scan runs until it ends', async () => {
	const index = copy(fixture('index.json') as IndexJson)
	index.projectors = [{ id: PJ, name: 'Left', output: 'op_1', device: null }]
	const state = copy(fixture('state.json') as StateJson)
	state.stage.projectors = [{ id: PJ, name: 'Left', solved: true, residualPx: 0.8, grade: 'A' }]
	const box = state.devices[0]
	assert.ok(box)
	const boxId = String(box.id)
	Object.assign(box, { projector: PJ, power: 'on', shutter: 'open', light: 'green', headline: 'Left box · on' })
	const node = await new FakeNode({ index, state }).start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked() && m.instance.model.index !== null, 'linked')
		await m.place('boxLabel', 'label', { ref: boxId })
		await m.place('boxLight', 'light', { ref: boxId })
		await m.place('boxWord', 'state_word', { ref: boxId })
		await m.place('scanning', 'scan_running', {})
		await values(
			m,
			{
				pj_1_power: 'on',
				pj_1_shutter: 'open',
				[`pj.${PJ}.power`]: 'on',
				[`pj.${PJ}.shutter`]: 'open',
				boxLabel: 'Left box',
				boxLight: 'green',
				boxWord: 'on',
				scanning: true,
			},
			'the box’s facts on the projector and on its own key; the scan running',
		)

		// The shutter closes; the scan ends (STATE keeps the last scan).
		const moved = copy(state)
		Object.assign(moved.devices[0] ?? {}, { shutter: 'closed' })
		moved.scan = { ...moved.scan, state: 'done', message: 'scan ready' }
		node.pushState(moved)
		await values(
			m,
			{ pj_1_shutter: 'closed', scanning: false, scan_state: 'done', scan_progress: 0, scan_words: 'scan ready' },
			'closed; a scan that ended is not running',
		)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a playlist key keeps its name when the link drops; a machine’s key follows a peers-only change', async () => {
	const node = await new FakeNode().start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked() && m.instance.model.index !== null, 'linked')
		await m.place('show', 'label', { ref: SHOW.playlist })
		await m.place('peerName', 'label', { ref: 'a1b2c3d4' })
		await m.place('peerWord', 'state_word', { ref: 'a1b2c3d4' })
		await values(m, { show: 'Show', peerName: 'Stage left' }, 'the names')

		const state = copy(fixture('state.json') as StateJson)
		Object.assign(state.peers[0] ?? {}, { name: 'Stage left B', headline: 'Stage left B · ok', light: 'green' })
		node.pushState(state)
		await values(m, { peerName: 'Stage left B', peerWord: 'Stage left B · ok' }, 'a peers-only change relabels the key')

		for (const s of [...node.sessions]) s.close(1001)
		await m.host.until(() => m.instance.link() !== 'live', 'the link down')
		await values(m, { show: 'Show' }, 'INDEX still names the playlist: not REMOVED')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('the Show Mode key says ARMED or REHEARSAL and the master dial its 0-100, as their variables do', async () => {
	const node = await new FakeNode().start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		const labelOf = (presetId: string): { feedbackId: string; options: Record<string, unknown> } | undefined => {
			const layered = (
				m.host.presets[presetId] as {
					variants: { feedbacks: { feedbackId: string; options: Record<string, unknown> }[] }[]
				}
			).variants[0]
			return layered?.feedbacks[0]
		}
		// The keys' labels are bound to those variables.
		for (const [presetId, variable] of [
			['show_mode', 'show_mode'],
			['enc_master_key', 'master'],
		] as const) {
			assert.equal(labelOf(presetId)?.feedbackId, 'variable', presetId)
			assert.deepEqual(labelOf(presetId)?.options, { variable }, presetId)
		}
		await m.place('mode', 'variable', { variable: 'show_mode' })
		await m.place('master', 'variable', { variable: 'master' })
		await values(m, { mode: 'REHEARSAL', master: 100 }, 'rehearsal, full master')
		const state = copy(fixture('state.json') as StateJson)
		state.show.armed = true
		state.air.master.level = 0.95
		node.pushState(state)
		await values(m, { mode: 'ARMED', master: 95 }, 'armed, the master at 95')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a test pattern up says its kind, read from the STATE the engine wrote with one', async () => {
	const node = await new FakeNode().start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		await m.place('up', 'pattern_up', {})
		await values(m, { pattern: '', up: false }, 'no pattern')
		node.pushState(fixture('state-pattern.json'))
		await values(m, { pattern: 'pattern.grid', up: true }, 'the grid up')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a key’s time left reads as m:ss or seconds; a projector’s score ring reads its calibration, else its solve', async () => {
	const index = copy(fixture('index.json') as IndexJson)
	index.projectors = [{ id: PJ, name: 'Left', output: 'op_1', device: null }]
	const state = copy(fixture('state.json') as StateJson & { playlists: Record<string, unknown>[] })
	state.stage.projectors = [{ id: PJ, name: 'Left', solved: true, residualPx: 0.8, grade: 'A' }]
	// Paused, so no clock runs between pushes: 72.5 s left.
	Object.assign(state.playlists[0] ?? {}, { state: 'paused', positionMs: 1000, lengthMs: 73500, remainingMs: 72500 })
	const node = await new FakeNode({ index, state }).start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked() && m.instance.model.index !== null, 'linked')
		await m.place('clock', 'remaining', { playlist: SHOW.playlist, format: 'clock' })
		await m.place('seconds', 'remaining', { playlist: SHOW.playlist, format: 'seconds' })
		await m.place('none', 'remaining', { playlist: SHOW.loops, format: 'clock' })
		await m.place('score', 'score', { projector: PJ })
		await values(
			m,
			{ clock: '1:13', seconds: 73, none: '', score: 84 },
			'time left rounded up; a solve 0.8 px off scores 84',
		)

		// The node's calibration score wins over the solve's.
		const scored = copy(state) as unknown as { calibration: { targets: unknown[] } }
		scored.calibration.targets = [{ id: PJ, score: 96.4, grade: 'A', words: 'Left is good' }]
		node.pushState(scored)
		await values(m, { score: 96 }, 'the calibration’s own score')
	} finally {
		await m.stop()
		await node.stop()
	}
})
