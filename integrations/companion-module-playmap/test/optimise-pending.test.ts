// A running optimise job's pause or resume is accepted, its job in the reply's `pending`, until the node's worker holds or goes on:
// the key that pressed it stays pending until STATE says so (media.optimise.jobs[].held), never on the pause asked (`paused`) or a
// progress line alone. A pause clears once each pending job is held, or no longer runs (queued again, or over); a resume once each is
// no longer held. MEDIA OPTIMISE PAUSE ALL watches the jobs its reply names, not its line's words.
import test from 'node:test'
import assert from 'node:assert/strict'
import { FakeNode, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'

const TOKEN = 'K7QM-3XWD-P9RA'
const JOB = 'oj0a1b2c3d'
const OTHER = 'oj4e5f6a7b'

interface Job {
	id: string
	state?: string
	paused: boolean
	held: boolean
	framesDone: number
}

function withJobs(seq: number, jobs: readonly Job[]): unknown {
	const s = copy(fixture('state.json')) as { seq: number; media: Record<string, unknown> }
	s.seq = seq
	s.media.optimise = {
		mode: 'auto',
		governor: 'run',
		words: '',
		running: jobs.filter((j) => (j.state ?? 'encoding') === 'encoding').length,
		queued: jobs.filter((j) => j.state === 'queued').length,
		failed: 0,
		worker: { pid: 4242, gl: '', encoder: '', restarts: 0 },
		jobs: jobs.map((j) => ({
			id: j.id,
			media: 'md00000099',
			name: 'Clip',
			to: 'hap',
			state: j.state ?? 'encoding',
			framesDone: j.framesDone,
			frames: 1800,
			fps: 30,
			etaS: 10,
			attempts: 0,
			paused: j.paused,
			held: j.held,
			version: '',
			psnrAvg: 0,
			words: '',
		})),
	}
	return s
}

function acceptedReply(effect: string, payload: unknown): string {
	return `OK ${JSON.stringify({ stage: 'accepted', effect, payload: JSON.stringify(payload) })}`
}

/** A module linked to a fake node whose MEDIA OPTIMISE answers `reply`, with `jobs` in STATE; the base seq of the fixture. */
async function linked(reply: string, jobs: readonly Job[]): Promise<{ node: FakeNode; m: Booted; seq: number }> {
	const node = await new FakeNode({ token: TOKEN }).start()
	node.handlers.set('MEDIA OPTIMISE', () => reply)
	try {
		const m = await boot(configFor(node.webPort, node.wirePort, { min_go_ms: 0 }), { token: TOKEN })
		await m.host.until(() => m.instance.linked() && m.host.variables.get('node_name') === 'Test node', 'linked')
		const seq = (fixture('state.json') as { seq: number }).seq + 1
		node.pushState(withJobs(seq, jobs))
		await m.host.until(() => (m.instance.model.state as { seq: number }).seq === seq, 'the jobs in STATE')
		return { node, m, seq }
	} catch (e) {
		await node.stop()
		throw e
	}
}

/** Pushes STATE with `jobs` at `seq` and waits until the module has read it. */
async function push(node: FakeNode, m: Booted, seq: number, jobs: readonly Job[]): Promise<void> {
	node.pushState(withJobs(seq, jobs))
	await m.host.until(() => (m.instance.model.state as { seq: number }).seq === seq, `STATE ${seq} read`)
}

async function press(m: Booted, line: string): Promise<void> {
	const result = (await m.press('raw', { line }, 'key-9')) as Record<string, unknown>
	assert.equal(result.stage, 'accepted')
	assert.equal(m.instance.receipts.stage('key-9'), 'pending')
}

void test('a pause press stays pending through the hold asked and a progress line, and clears once STATE says held', async () => {
	const reply = acceptedReply(`pause asked of ${JOB}: it holds at its next frame`, { paused: [JOB], pending: [JOB] })
	const { node, m, seq } = await linked(reply, [{ id: JOB, paused: false, held: false, framesDone: 10 }])
	try {
		await press(m, `MEDIA OPTIMISE PAUSE ${JOB}`)
		await push(node, m, seq + 1, [{ id: JOB, paused: true, held: false, framesDone: 10 }])
		assert.equal(m.instance.receipts.stage('key-9'), 'pending', 'the hold asked is not the hold')
		await push(node, m, seq + 2, [{ id: JOB, paused: true, held: false, framesDone: 11 }])
		assert.equal(m.instance.receipts.stage('key-9'), 'pending', 'a progress line is not the hold')
		await push(node, m, seq + 3, [{ id: JOB, paused: true, held: true, framesDone: 11 }])
		assert.equal(m.instance.receipts.stage('key-9'), null, 'held: the pause is a fact')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a resume press stays pending while the job is held, and clears once STATE says it goes on', async () => {
	const reply = acceptedReply(`resume asked of ${JOB}: it goes on when its worker does`, {
		resumed: [JOB],
		pending: [JOB],
	})
	const { node, m, seq } = await linked(reply, [{ id: JOB, paused: true, held: true, framesDone: 40 }])
	try {
		await press(m, `MEDIA OPTIMISE RESUME ${JOB}`)
		await push(node, m, seq + 1, [{ id: JOB, paused: false, held: true, framesDone: 40 }])
		assert.equal(m.instance.receipts.stage('key-9'), 'pending', 'the resume asked is not the job going on')
		await push(node, m, seq + 2, [{ id: JOB, paused: false, held: false, framesDone: 41 }])
		assert.equal(m.instance.receipts.stage('key-9'), null, 'no longer held: the resume is a fact')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('PAUSE ALL watches the jobs its reply names pending, each until held or no longer running', async () => {
	const reply = acceptedReply(`pause asked of ${JOB}, ${OTHER}: it holds at its next frame`, {
		paused: [JOB, OTHER],
		pending: [JOB, OTHER],
	})
	const both = (a: Partial<Job>, b: Partial<Job>): Job[] => [
		{ id: JOB, paused: true, held: false, framesDone: 5, ...a },
		{ id: OTHER, paused: true, held: false, framesDone: 7, ...b },
	]
	const { node, m, seq } = await linked(reply, both({ paused: false }, { paused: false }))
	try {
		await press(m, 'MEDIA OPTIMISE PAUSE ALL')
		await push(node, m, seq + 1, both({}, {}))
		assert.equal(m.instance.receipts.stage('key-9'), 'pending', 'a later STATE alone is not the hold')
		await push(node, m, seq + 2, both({ held: true }, {}))
		assert.equal(m.instance.receipts.stage('key-9'), 'pending', 'one job of two held')
		await push(node, m, seq + 3, both({ held: true }, { state: 'queued' }))
		assert.equal(m.instance.receipts.stage('key-9'), null, 'one held, the other no longer running')
	} finally {
		await m.stop()
		await node.stop()
	}
})
