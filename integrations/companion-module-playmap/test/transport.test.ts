// What a key's GO, NEXT and PREV play is what the node plays for them (EngineCore.Transport), not STATE's `next` (which follows the
// item's end action): GO plays the cued item when cued, the first when idle, else the one after the current in list order — none
// past the last unless the list loops; NEXT the one after. EXPECT names exactly that item, so a press is never refused as out of
// date when the show has not moved, and the `next` variable says the same item. An item with no end (the node's remainingMs null,
// a Loop item) shows no countdown and its position wraps.
import test from 'node:test'
import assert from 'node:assert/strict'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'

interface PlaylistJson {
	id: string
	state: string
	index: number | null
	item: { id: string; label: string; source: string } | null
	next: { id: string; label: string } | null
	positionMs: number | null
	lengthMs: number | null
	remainingMs: number | null
	loop: boolean
}
interface StateJson {
	at: number
	playlists: PlaylistJson[]
}

const RED = { id: SHOW.red, label: 'Red', source: 'color:#FF0000' }
const BLUE = { id: SHOW.blue, label: 'Blue', source: 'color:#0000FF' }

/** The fixture with the show's playlist set as given. */
function stateWith(show: Partial<PlaylistJson>): StateJson {
	const s = copy(fixture('state.json') as StateJson)
	const p = s.playlists.find((e) => e.id === SHOW.playlist)
	assert.ok(p)
	Object.assign(p, show)
	return s
}

async function line(m: Booted, node: FakeNode, actionId: string): Promise<string> {
	const before = node.sent().length
	await m.press(actionId, { playlist: '', expect: true }, `key-${actionId}`)
	const sent = node
		.sent()
		.slice(before)
		.filter((l) => l.startsWith('PLAYLIST '))
	assert.equal(sent.length, 1, actionId)
	return sent[0] ?? ''
}

void test('GO and NEXT expect the item the node plays for them, cued, after a Hold item, idle, at the end, looping', async () => {
	const node = await new FakeNode().start()
	const m = await boot(configFor(node.webPort, node.wirePort, { min_go_ms: 0 }), {})
	try {
		await m.host.until(() => m.instance.linked() && m.instance.model.index !== null, 'linked, INDEX heard')
		const cases: [string, Partial<PlaylistJson>, string, string, string][] = [
			// [what, the show's playlist, GO's line, NEXT's line, the next variable]
			[
				'cued at Red: GO plays Red',
				{ state: 'cued', index: 1, item: RED, next: { id: BLUE.id, label: 'Blue' } },
				`PLAYLIST GO ${SHOW.playlist} EXPECT ${SHOW.red}`,
				`PLAYLIST NEXT ${SHOW.playlist} EXPECT ${SHOW.blue}`,
				'Red',
			],
			[
				'Red holds at its end (STATE next is Red): GO plays Blue',
				{ state: 'playing', index: 1, item: RED, next: { id: RED.id, label: 'Red' } },
				`PLAYLIST GO ${SHOW.playlist} EXPECT ${SHOW.blue}`,
				`PLAYLIST NEXT ${SHOW.playlist} EXPECT ${SHOW.blue}`,
				'Blue',
			],
			[
				'idle: GO plays the first item',
				{ state: 'idle', index: null, item: null, next: null, positionMs: null, remainingMs: null },
				`PLAYLIST GO ${SHOW.playlist} EXPECT ${SHOW.intro}`,
				`PLAYLIST NEXT ${SHOW.playlist} EXPECT ${SHOW.intro}`,
				'Intro',
			],
			[
				'Blue, the last, playing: GO plays none, so it expects none',
				{ state: 'playing', index: 2, item: BLUE, next: null, loop: false },
				`PLAYLIST GO ${SHOW.playlist}`,
				`PLAYLIST NEXT ${SHOW.playlist}`,
				'',
			],
			[
				'Blue, the last, playing in a looping list: GO plays the first',
				{ state: 'playing', index: 2, item: BLUE, next: { id: SHOW.intro, label: 'Intro' }, loop: true },
				`PLAYLIST GO ${SHOW.playlist} EXPECT ${SHOW.intro}`,
				`PLAYLIST NEXT ${SHOW.playlist} EXPECT ${SHOW.intro}`,
				'Intro',
			],
		]
		for (const [what, show, go, next, nextVar] of cases) {
			node.pushState(stateWith(show))
			await m.host.until(() => m.host.variables.get('next') === nextVar, `${what}: next`)
			assert.equal(await line(m, node, 'go_focused'), go, what)
			assert.equal(await line(m, node, 'next_focused'), next, what)
		}
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('an item with no end shows no countdown, and its position wraps at its length', async () => {
	const node = await new FakeNode().start()
	const m = await boot(configFor(node.webPort), {})
	try {
		await m.host.until(() => m.instance.linked(), 'linked')
		const s = stateWith({ state: 'playing', index: 1, item: RED, positionMs: 4000, lengthMs: 10000, remainingMs: null })
		s.at = node.now() - 9000 // 9 s ago: 13 s into a 10 s loop
		node.pushState(s)
		await m.host.until(() => m.host.variables.get('video_position') === '0:03', 'the position wrapped')
		assert.equal(m.host.variables.get('video_remaining'), '')
		assert.equal(m.host.variables.get(`pl.${SHOW.playlist}.remaining`), '')
	} finally {
		await m.stop()
		await node.stop()
	}
})
