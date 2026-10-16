// What a key's GO, NEXT and PREV play is what the node plays for them (EngineCore.Transport), not STATE's `next` (which follows the
// item's end action): GO plays the cued item when cued, the first when idle, else the one after the current in list order — none
// past the last unless the list loops; NEXT the one after. EXPECT names exactly that item, so a press is never refused as out of
// date when the show has not moved, and the `next` variable says the same item. An item with no end (the node's remainingMs null,
// a Loop item) shows no countdown and its position wraps, read on fake timers with the test as the node.
import test from 'node:test'
import assert from 'node:assert/strict'
import type { JsonValue } from '@companion-module/base'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'
import { advance, fakeChannels, settle, type FakeChannel, type FakeTimers } from './fake-channel.ts'

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

/** Moves fake time on while the node answers every PING half way through its round trip on the module's clock, so the link stays
 * live however long and the offset the module measures is 0. */
async function live(timers: FakeTimers, ch: FakeChannel, ms: number): Promise<void> {
	for (let t = 0; t < ms; t += 50) {
		for (const l of ch.sent.splice(0)) {
			const [id = '', verb = '', t1 = ''] = l.split(' ')
			if (verb !== 'PING') continue
			const at = Number(t1) + (Date.now() - Number(t1)) / 2
			ch.say(`OK ${id} PONG ${t1} ${at} ${at}`)
		}
		await settle() // each PONG is read at the instant it was answered
		await advance(timers, Math.min(50, ms - t), 50)
	}
}

void test('an item with no end shows no countdown, and its position wraps at its length', async (t) => {
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
		ch.ok('SUBSCRIBE INDEX', 'subscribed index')
		ch.say('INDEX ' + JSON.stringify(fixture('index.json')))
		await live(t.mock.timers, ch, 100)
		assert.equal(m.instance.link(), 'live')
		assert.equal(m.instance.nodeNow(), Date.now(), "the node's clock is the module's")

		// Red, 4 s in when STATE was written 9 s ago: 13 s into its 10 s loop. Read at the flush 50 ms after the push.
		const s = stateWith({ state: 'playing', index: 1, item: RED, positionMs: 4000, lengthMs: 10000, remainingMs: null })
		s.at = Date.now() - 9000
		ch.say('STATE ' + JSON.stringify(s))
		await live(t.mock.timers, ch, 50)
		const clock = (): Record<string, JsonValue | undefined> => ({
			position: m.host.variables.get('video_position'),
			remaining: m.host.variables.get('video_remaining'),
			playlistRemaining: m.host.variables.get(`pl.${SHOW.playlist}.remaining`),
		})
		assert.deepEqual(clock(), { position: '0:03', remaining: '', playlistRemaining: '' }, '13.05 s into a 10 s loop')

		// With no push, it runs on and wraps again: 20.05 s in.
		await live(t.mock.timers, ch, 7000)
		assert.equal(m.instance.link(), 'live')
		assert.deepEqual(clock(), { position: '0:00', remaining: '', playlistRemaining: '' }, '20.05 s into a 10 s loop')
	} finally {
		await m.stop()
	}
})

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
