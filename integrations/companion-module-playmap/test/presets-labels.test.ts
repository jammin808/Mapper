// A rename alone (INDEX's labels move, its sig does not) relabels keys at once through their value feedbacks and re-sends the
// preset structure only after 3 s; a structural change (the sig moves) re-sends it at once. Neither changes a preset's id or bytes.
// On fake timers, in its own file so its clock is its own.
import test from 'node:test'
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { boot, type Booted } from './harness.ts'
import { advance, fakeChannels, settle, type FakeChannel, type FakeTimers } from './fake-channel.ts'
import { SHOW, copy, fixture } from './fake-node.ts'
import { LABELS_DEBOUNCE_MS } from '../src/main.ts'

interface IndexJson {
	revision: number
	sig: string
	labels: string
	playlists: { id: string; name: string; items: { id: string; label: string }[] }[]
}

interface Group {
	id: string
	templateValues?: { name?: string; value: unknown }[]
}

const names = (m: Booted): (string | undefined)[] | undefined =>
	(m.host.structure as { definitions: Group[] }[])
		.flatMap((s) => s.definitions)
		.find((g) => g.id === `pl.${SHOW.playlist}`)
		?.templateValues?.map((v) => v.name)

function checksums(presets: Record<string, unknown>): Record<string, string> {
	return Object.fromEntries(
		Object.entries(presets).map(([id, p]) => [id, createHash('sha256').update(JSON.stringify(p)).digest('hex')]),
	)
}

/** Moves fake time on while the node answers every PING, so the link stays live however long the test waits. */
async function live(timers: FakeTimers, ch: FakeChannel, ms: number): Promise<void> {
	for (let t = 0; t < ms; t += 50) {
		ch.pong(Date.now())
		await advance(timers, Math.min(50, ms - t), 50)
	}
}

void test('a rename alone re-sends the structure after 3 s, a structural change at once; no preset changes', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', port: 9940 }, {}, { factory, random: () => 0.5 })
	try {
		assert.equal(LABELS_DEBOUNCE_MS, 3000)
		const ch = opened[0]
		assert.ok(ch)
		ch.events.open({})
		await settle()
		ch.ok('HELLO')
		await settle()
		ch.say('STATE ' + JSON.stringify(fixture('state.json')))
		const doc = copy(fixture('index.json') as IndexJson)
		ch.say('INDEX ' + JSON.stringify(doc))
		await live(t.mock.timers, ch, 100)
		assert.equal(m.instance.link(), 'live')
		assert.deepEqual(names(m), ['Intro', 'Red', 'Blue'])
		await m.place('red', 'label', { ref: SHOW.red })
		await live(t.mock.timers, ch, 100)
		assert.equal(m.host.feedbackValues.get('red'), 'Red')
		const calls = m.host.presetCalls
		const structure = copy(m.host.structure)
		const sums = checksums(m.host.presets)

		// A rename: labels move, sig does not.
		const show = doc.playlists.find((p) => p.id === SHOW.playlist)
		const red = show?.items.find((i) => i.id === SHOW.red)
		assert.ok(show && red)
		red.label = 'Crimson'
		doc.labels = '1234abcd5678ef90'
		ch.say('INDEX ' + JSON.stringify(doc))
		await live(t.mock.timers, ch, 100)
		assert.equal(m.host.feedbackValues.get('red'), 'Crimson', 'the key relabels at once')
		assert.equal(m.host.presetCalls, calls, 'the structure waits')
		await live(t.mock.timers, ch, LABELS_DEBOUNCE_MS - 150)
		assert.equal(m.host.presetCalls, calls, 'still waiting 50 ms before 3 s')
		assert.deepEqual(m.host.structure, structure)
		await live(t.mock.timers, ch, 50)
		assert.equal(m.host.presetCalls, calls + 1, 're-sent once, at 3 s')
		assert.deepEqual(names(m), ['Intro', 'Crimson', 'Blue'])
		assert.deepEqual(checksums(m.host.presets), sums)

		// A reorder: sig moves, and the structure is re-sent before any time passes.
		show.items.reverse()
		doc.revision += 1
		doc.sig = '0f1e2d3c4b5a6978'
		ch.say('INDEX ' + JSON.stringify(doc))
		await settle()
		assert.equal(m.host.presetCalls, calls + 2, 'at once')
		assert.deepEqual(names(m), ['Blue', 'Crimson', 'Intro'])
		assert.deepEqual(checksums(m.host.presets), sums)
		assert.equal(m.instance.link(), 'live')
	} finally {
		await m.stop()
	}
})
