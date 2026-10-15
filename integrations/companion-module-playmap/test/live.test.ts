// The Live page: 32 slots a page whose source is the page's own (the rule option, which Companion evaluates from $(page:pm_live))
// or, when the page names none, the connection's (focused unless config or live_source says otherwise), laid out by the module from
// INDEX and STATE. Each slot gives its label, light, picture and line; a slot past the end is empty. Past 32 entries the page turns,
// and live_page says which page of how many. While the show is armed the slot → entity map holds as the keys showed it when it was
// armed: an entity that leaves reads REMOVED in grey, lights and labels still follow STATE and INDEX, and a press sends the line of
// the entity the key shows; live_refresh or disarming lays the page out again. A short press sends the slot's line when its verb is
// instant; a slot whose verb needs a hold only says HOLD (for 1 s) on a short press and sends when held.
import test from 'node:test'
import assert from 'node:assert/strict'
import { isDeepStrictEqual } from 'node:util'
import type { CompanionOptionValues } from '@companion-module/base'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'
import { advance, fakeChannels, settle, type FakeChannel, type FakeTimers } from './fake-channel.ts'
import { HINT_MS, SLOTS } from '../src/live.ts'
import type { PlayMapConfig } from '../src/config.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

interface ItemJson {
	id: string
	label: string
	source: string
	[key: string]: unknown
}

interface RowJson {
	id: string
	kind: string
	label: string
	parent: string | null
	light: string
	state: string
}

interface SectionJson {
	section: string
	hint: {
		title: string
		rowKind: string
		keys: { label: string; line: string; feedback: string | null; press: string }[]
	}
	rows: RowJson[]
}

interface IndexJson {
	revision: number
	sig: string
	labels: string
	playlists: { id: string; name: string; items: ItemJson[] }[]
	sections: SectionJson[]
}

interface PeerJson {
	id: string
	name: string
	light: string
	headline: string
	live: boolean
	[key: string]: unknown
}

interface PlaylistJson {
	id: string
	state: string
	item: { id: string; label: string; source: string } | null
	next: { id: string; label: string } | null
	[key: string]: unknown
}

interface StateJson {
	show: { armed: boolean }
	playlists: PlaylistJson[]
	outputs: { id: string; name: string; light: string }[]
	faults: { part: string; words: string }[]
	peers: PeerJson[]
}

const indexDoc = (): IndexJson => copy(fixture('index.json') as IndexJson)
const stateDoc = (): StateJson => copy(fixture('state.json') as StateJson)

function playlistOf(doc: IndexJson, id: string): IndexJson['playlists'][number] {
	const p = doc.playlists.find((x) => x.id === id)
	assert.ok(p, `INDEX lists ${id}`)
	return p
}

/** The picture the fake node answers for a ref: its bytes are the ref's own. */
const pictureOf = (ref: string): string => `data:image/png;base64,${Buffer.from(ref).toString('base64')}`

/** Waits until the host holds these feedback values, then asserts them (a timeout shows what it holds instead). */
async function values(m: Booted, expected: Record<string, unknown>, what: string): Promise<void> {
	const held = (): Record<string, unknown> =>
		Object.fromEntries(Object.keys(expected).map((id) => [id, m.host.feedbackValues.get(id)]))
	await m.host.until(() => isDeepStrictEqual(held(), expected), what).catch(() => undefined)
	assert.deepEqual(held(), expected, what)
}

async function variable(m: Booted, id: string, value: unknown, what: string): Promise<void> {
	await m.host.until(() => m.host.variables.get(id) === value, what).catch(() => undefined)
	assert.equal(m.host.variables.get(id), value, what)
}

/** A paired node (token proof over ws) whose INDEX and greeting STATE the module has heard. */
async function linked(
	options: { index?: IndexJson; state?: StateJson; config?: PlayMapConfig } = {},
): Promise<{ node: FakeNode; m: Booted }> {
	const node = await new FakeNode({ token: TOKEN, index: options.index, state: options.state }).start()
	const m = await boot(configFor(node.webPort, node.wirePort, options.config ?? {}), { token: TOKEN })
	try {
		await m.host.until(
			() =>
				m.host.variables.get('node_name') === 'Test node' &&
				m.host.variables.get(`pl.${SHOW.playlist}.name`) !== undefined,
			'the greeting STATE and INDEX',
		)
	} catch (e) {
		await m.stop()
		await node.stop()
		throw e
	}
	return { node, m }
}

/** What a slot shows: null for a slot past the end. */
interface SlotView {
	label: string
	light: string
	line: string
	thumb: string
	sub?: string
}

const EMPTY = { label: '', light: 'grey', line: '', thumb: '' } as const

/** Places the slot value feedbacks (label, light, line, thumb, and sub when expected) and slot_empty for slots 1… of a rule, and
 * answers the values they should hold. */
async function placeSlots(
	m: Booted,
	rule: string,
	views: readonly (SlotView | null)[],
	prefix = rule,
): Promise<Record<string, unknown>> {
	const expected: Record<string, unknown> = {}
	for (const [i, view] of views.entries()) {
		const n = i + 1
		const shown = view ?? EMPTY
		for (const field of ['label', 'light', 'line', 'thumb', 'sub'] as const) {
			const value = field === 'sub' ? view?.sub : shown[field]
			if (value === undefined) continue
			const id = `${prefix}|${n}|${field}`
			await m.place(id, 'slot', { rule, n, field })
			expected[id] = value
		}
		await m.place(`${prefix}|${n}|empty`, 'slot_empty', { rule, n })
		expected[`${prefix}|${n}|empty`] = view === null
	}
	return expected
}

/** Answers every PING the module sent and nobody answered yet (the sent lines stay for the test to read). */
function answerPings(ch: FakeChannel, answered: Set<string>): void {
	for (const l of ch.sent) {
		const ping = /^@(\S+) PING (\S+)$/u.exec(l)
		if (!ping || answered.has(ping[1] ?? '')) continue
		answered.add(ping[1] ?? '')
		ch.say(`OK @${ping[1]} PONG ${ping[2]} ${Date.now()} ${Date.now()}`)
	}
}

/** Moves fake time on in 10 ms steps, the node answering every PING, so the link stays live. */
async function run(timers: FakeTimers, ch: FakeChannel, answered: Set<string>, ms: number): Promise<void> {
	for (let t = 0; t < ms; t += 10) {
		answerPings(ch, answered)
		await advance(timers, Math.min(10, ms - t), 10)
	}
}

// On fake timers (the HOLD hint lasts 1 s); first in the file, so no link of a test before it is still closing when its clock is
// mocked.
void test('a slot whose verb needs a hold says HOLD for 1 s on a short press and sends only when held', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', port: 9940 }, {}, { factory, random: () => 0.5 })
	try {
		const ch = opened[0]
		assert.ok(ch)
		const answered = new Set<string>()
		ch.events.open({})
		await settle()
		ch.ok('HELLO')
		await settle()
		// A package section whose key shows or hides a surface: SURFACE SHOW is a verb pressed with a hold.
		const doc = indexDoc()
		doc.sections.push({
			section: 'stage',
			hint: {
				title: 'Stage',
				rowKind: 'surface',
				keys: [{ label: '$(label)', line: 'SURFACE SHOW $(id) ON', feedback: '$(light)', press: 'hold' }],
			},
			rows: [
				{ id: 'sf00000010', kind: 'surface', label: 'Wall', parent: null, light: 'grey', state: 'hidden' },
				{ id: 'sf00000011', kind: 'surface', label: 'Floor', parent: null, light: 'grey', state: 'hidden' },
			],
		})
		ch.say('STATE ' + JSON.stringify(fixture('state.json')))
		ch.say('INDEX ' + JSON.stringify(doc))
		const rule = 'section:stage'
		await m.place('label', 'slot', { rule, n: 1, field: 'label' })
		await m.place('line', 'slot', { rule, n: 1, field: 'line' })
		await m.place('hint1', 'slot_hint', { rule, n: 1 })
		await m.place('hint2', 'slot_hint', { rule, n: 2 })
		await run(t.mock.timers, ch, answered, 100)
		assert.equal(m.instance.link(), 'live')
		assert.equal(m.host.feedbackValues.get('label'), 'Wall')
		assert.equal(m.host.feedbackValues.get('line'), 'SURFACE SHOW sf00000010 ON')
		assert.equal(m.host.feedbackValues.get('hint1'), false)
		const surfaceLines = (): string[] => ch.sent.filter((l) => / SURFACE SHOW /u.test(l))

		// A short press: nothing sent, HOLD on that slot only, for 1 s.
		const short = (await m.press('slot_press', { rule, n: 1, held: false }, 'key-1')) as CompanionOptionValues
		assert.equal(short.ok, false)
		assert.equal(short.stage, 'not-sent')
		await run(t.mock.timers, ch, answered, 10)
		assert.equal(m.host.feedbackValues.get('hint1'), true, 'HOLD after a short press')
		assert.equal(m.host.feedbackValues.get('hint2'), false, 'only on the slot pressed')
		assert.deepEqual(surfaceLines(), [], 'a short press sends nothing')
		await run(t.mock.timers, ch, answered, HINT_MS - 30)
		assert.equal(m.host.feedbackValues.get('hint1'), true, 'still HOLD just before 1 s')
		await run(t.mock.timers, ch, answered, 30)
		assert.equal(m.host.feedbackValues.get('hint1'), false, 'gone at 1 s')

		// Held past 800 ms: the line is sent and the press answers the node's reply.
		const held = m.press('slot_press', { rule, n: 1, held: true }, 'key-1')
		await settle()
		assert.equal(surfaceLines().length, 1)
		assert.match(surfaceLines()[0] ?? '', /^@c\d+ SURFACE SHOW sf00000010 ON$/u)
		ch.ok('SURFACE SHOW')
		const result = (await held) as CompanionOptionValues
		assert.equal(result.ok, true)
		assert.equal(result.stage, 'applied')
		await run(t.mock.timers, ch, answered, 10)
		assert.equal(m.host.feedbackValues.get('hint1'), false, 'a held press says no HOLD')
		assert.equal(m.instance.link(), 'live')
	} finally {
		await m.stop()
	}
})

void test('every source lays its slots out from INDEX and STATE, and a slot past the end is empty', async () => {
	const { node, m } = await linked()
	try {
		const index = indexDoc()
		const state = stateDoc()
		const show = playlistOf(index, SHOW.playlist)
		const loops = playlistOf(index, SHOW.loops)
		const [intro, red, blue] = show.items
		const [green] = loops.items
		const showSection = index.sections.find((s) => s.section === 'show')
		const [fault] = state.faults
		const [peer] = state.peers
		const [output] = state.outputs
		assert.ok(intro && red && blue && green && showSection && fault && peer && output)
		assert.equal(state.playlists.find((p) => p.id === show.id)?.item?.id, red.id, 'the fixture plays Red')
		assert.equal(state.playlists.find((p) => p.id === show.id)?.next?.id, blue.id, 'and Blue is next')
		assert.equal(state.playlists.find((p) => p.id === loops.id)?.next?.id, green.id, 'Loops is idle, Green next')

		// Items of a playlist: GO of the item, its source's picture; green on the air, amber next, grey otherwise.
		const itemSlot = (pl: string, item: ItemJson, light: string): SlotView => ({
			label: item.label,
			light,
			line: `PLAYLIST GO ${pl} ${item.id}`,
			thumb: pictureOf(item.source),
		})
		const focused = [
			itemSlot(show.id, intro, 'grey'),
			itemSlot(show.id, red, 'green'),
			itemSlot(show.id, blue, 'amber'),
			null,
		]
		const sources: Record<string, (SlotView | null)[]> = {
			// The page names no source: the connection's, focused by default.
			'': focused,
			focused,
			[`playlist:${loops.id}`]: [itemSlot(loops.id, green, 'amber'), null],
			// Playlists: transport keys, lit by their state, with the playlist's picture.
			playlists: [
				{ label: 'Show', light: 'green', line: `PLAYLIST GO ${show.id}`, thumb: pictureOf(`playlist:${show.id}`) },
				{ label: 'Loops', light: 'grey', line: `PLAYLIST GO ${loops.id}`, thumb: pictureOf(`playlist:${loops.id}`) },
				null,
			],
			outputs: [{ label: output.name, light: output.light, line: `OUTPUT IDENTIFY ${output.id}`, thumb: '' }, null],
			// Faults: the part and its words, red; the key only shows.
			faults: [{ label: fault.part, sub: fault.words, light: 'red', line: '', thumb: '' }, null],
			// The fleet: each machine's name, headline and light.
			peers: [{ label: peer.name, sub: peer.headline, light: peer.light, line: '', thumb: '' }, null],
			// A package's section: its first key's label and line with each row's words in, lit by the row.
			'section:show': [
				...showSection.rows.map((r) => ({ label: r.label, light: r.light || 'grey', line: r.id, thumb: '' })),
				null,
			],
			// A source the module does not know lays out nothing.
			'no-such-source': [null],
		}
		const expected: Record<string, unknown> = {}
		for (const [rule, views] of Object.entries(sources)) Object.assign(expected, await placeSlots(m, rule, views))
		await values(m, expected, 'every source’s slots')

		// The slots follow STATE: the output's light moves, Blue goes on the air.
		const moved = stateDoc()
		const op = moved.outputs.find((o) => o.id === output.id)
		const pl = moved.playlists.find((p) => p.id === show.id)
		assert.ok(op && pl)
		op.light = 'red'
		pl.item = { id: blue.id, label: blue.label, source: blue.source }
		pl.next = null
		node.pushState(moved)
		await values(
			m,
			{ 'outputs|1|light': 'red', '|1|light': 'grey', '|2|light': 'grey', '|3|light': 'green', '|3|label': 'Blue' },
			'the slots’ lights follow STATE',
		)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("a page that names no source shows the connection's: config's live_rule, then live_source, as live_rule says", async () => {
	const { node, m } = await linked({ config: { live_rule: 'peers' } })
	try {
		const state = stateDoc()
		const [peer] = state.peers
		const [output] = state.outputs
		const intro = playlistOf(indexDoc(), SHOW.playlist).items[0]
		assert.ok(peer && output && intro)
		await m.place('own', 'slot', { rule: '', n: 1, field: 'label' })
		await m.place('focused', 'slot', { rule: 'focused', n: 1, field: 'label' })
		await values(m, { own: peer.name, focused: intro.label }, "the connection's source is config's")
		await variable(m, 'live_rule', 'peers', 'live_rule from config')

		await m.press('live_source', { rule: 'outputs' })
		await values(m, { own: output.name, focused: intro.label }, 'live_source moves only the pages that name none')
		await variable(m, 'live_rule', 'outputs', 'live_rule follows live_source')
		// The node hears which source this deck shows: at pairing, and again once it moves.
		assert.match(await node.heard('PRESENCE {"kind":"companion"'), /"live":"peers"/u)
		assert.match(await node.heard('"live":"outputs"'), /^@c\d+ PRESENCE \{/u)

		await m.press('live_source', { rule: '' })
		await values(m, { own: intro.label }, 'an empty source is focused')
		await variable(m, 'live_rule', 'focused', 'live_rule focused')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('past 32 entries the page turns: slot n of page 2 is entry 32 + n, live_page says 2/2, each source keeps its own page', async () => {
	const doc = indexDoc()
	const show = playlistOf(doc, SHOW.playlist)
	const [intro] = show.items
	assert.ok(intro)
	// Forty items: Intro, Red, Blue, then Clip 4 … Clip 40 (item k at place k).
	for (let k = 4; k <= 40; k++)
		show.items.push({ id: `it${String(100 + k).padStart(8, '0')}`, label: `Clip ${k}`, source: 'color:#FFFFFF' })
	const clip = (k: number): ItemJson => {
		const item = show.items[k - 1]
		assert.ok(item)
		return item
	}
	const { node, m } = await linked({ index: doc, config: { min_go_ms: 0 } })
	try {
		assert.equal(SLOTS, 32)
		for (const n of [1, 8, 9, 32]) {
			await m.place(`p${n}`, 'slot', { rule: '', n, field: 'label' })
			await m.place(`e${n}`, 'slot_empty', { rule: '', n })
		}
		await m.place('other', 'slot', { rule: `playlist:${show.id}`, n: 1, field: 'label' })
		await values(
			m,
			{ p1: intro.label, p8: 'Clip 8', p9: 'Clip 9', p32: 'Clip 32', e9: false, e32: false, other: intro.label },
			'page 1',
		)
		await variable(m, 'live_page', '1/2', 'live_page on page 1')

		await m.press('live_page_next', { rule: '' })
		await values(
			m,
			{ p1: 'Clip 33', p8: 'Clip 40', p9: '', p32: '', e1: false, e8: false, e9: true, e32: true, other: intro.label },
			'page 2: slot n is entry 32 + n, slots past the end empty; another source stays on its page',
		)
		await variable(m, 'live_page', '2/2', 'live_page on page 2')

		// A press on page 2 sends the entry it shows.
		const pressed = (await m.press('slot_press', { rule: '', n: 1, held: false })) as CompanionOptionValues
		assert.equal(pressed.ok, true)
		await node.heard(new RegExp(`^@c\\d+ PLAYLIST GO ${show.id} ${clip(33).id}$`, 'u'))

		await m.press('live_page_prev', { rule: '' })
		await values(m, { p1: intro.label, p8: 'Clip 8', p9: 'Clip 9', e9: false }, 'back on page 1')
		await variable(m, 'live_page', '1/2', 'live_page back on page 1')

		// The source shrinks to one page while page 2 is up: the page is the last there is.
		await m.press('live_page_next', { rule: '' })
		await variable(m, 'live_page', '2/2', 'page 2 again')
		const short = indexDoc()
		short.revision += 2
		short.sig = '0f1e2d3c4b5a6978'
		node.pushIndex(short)
		await variable(m, 'live_page', '1/1', 'one page left')
		await values(m, { p1: intro.label, e9: true }, 'the one page there is')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('while armed every slot holds its entity: one that leaves reads REMOVED in grey, lights and labels follow, a press sends what the key shows', async () => {
	const twoPeers = stateDoc()
	const [left] = twoPeers.peers
	assert.ok(left)
	const right: PeerJson = { ...left, id: 'e5f6a7b8', name: 'Stage right', light: 'green', headline: 'Stage right · ok' }
	twoPeers.peers = [left, right]
	const { node, m } = await linked({ state: twoPeers, config: { min_go_ms: 0 } })
	try {
		const doc = indexDoc()
		const show = playlistOf(doc, SHOW.playlist)
		const [intro, red, blue] = show.items
		assert.ok(intro && red && blue)
		await m.place('armed', 'show_armed', {})
		for (let n = 1; n <= 4; n++) {
			await m.place(`s${n}`, 'slot', { rule: '', n, field: 'label' })
			await m.place(`l${n}`, 'slot', { rule: '', n, field: 'light' })
			await m.place(`e${n}`, 'slot_empty', { rule: '', n })
		}
		for (let n = 1; n <= 2; n++) {
			await m.place(`p${n}`, 'slot', { rule: 'peers', n, field: 'label' })
			await m.place(`q${n}`, 'slot', { rule: 'peers', n, field: 'light' })
		}
		await values(
			m,
			{
				armed: false,
				s1: 'Intro',
				s2: 'Red',
				s3: 'Blue',
				s4: '',
				l1: 'grey',
				l2: 'green',
				l3: 'amber',
				e4: true,
				p1: 'Stage left',
				p2: 'Stage right',
				q1: left.light,
				q2: 'green',
			},
			'the slots before arming',
		)

		const armed = copy(twoPeers)
		armed.show.armed = true
		node.pushState(armed)
		await values(m, { armed: true }, 'armed')

		// Red leaves, Intro is renamed, the rest swap and Encore arrives; Stage left leaves the fleet and Blue goes on the air — so a
		// GO plays the item after Blue in the new order, Opening, which is lit next where it stands.
		const encore: ItemJson = {
			id: 'it00000099',
			label: 'Encore',
			source: 'color:#FFFF00',
			kind: 'color',
			lengthMs: null,
		}
		intro.label = 'Opening'
		show.items = [blue, intro, encore]
		doc.revision += 1
		doc.sig = '0f1e2d3c4b5a6978'
		doc.labels = '1234abcd5678ef90'
		node.pushIndex(doc)
		const moved = copy(armed)
		moved.peers = [right]
		const pl = moved.playlists.find((p) => p.id === show.id)
		assert.ok(pl)
		pl.item = { id: blue.id, label: blue.label, source: blue.source }
		pl.next = null
		node.pushState(moved)
		await values(
			m,
			{
				s1: 'Opening',
				s2: 'REMOVED',
				s3: 'Blue',
				s4: '',
				l1: 'amber',
				l2: 'grey',
				l3: 'green',
				e4: true,
				p1: 'REMOVED',
				p2: 'Stage right',
				q1: 'grey',
				q2: 'green',
			},
			'held in place: the gone ones REMOVED in grey, the rest relabelled and lit where they stand',
		)

		// A press on a held slot sends the entity it shows; the REMOVED one sends nothing.
		const removed = (await m.press('slot_press', { rule: '', n: 2, held: false }, 'key-2')) as CompanionOptionValues
		assert.equal(removed.ok, false)
		assert.equal(removed.stage, 'not-sent')
		const pressed = (await m.press('slot_press', { rule: '', n: 3, held: false }, 'key-3')) as CompanionOptionValues
		assert.equal(pressed.ok, true)
		await node.heard(new RegExp(`^@c\\d+ PLAYLIST GO ${show.id} ${blue.id}$`, 'u'))
		assert.deepEqual(
			node.sent().filter((l) => l.startsWith('PLAYLIST GO')),
			[`PLAYLIST GO ${show.id} ${blue.id}`],
			'one GO, of the entity the key shows',
		)

		// RE-LAY: laid out from the show as it is now, still armed.
		await m.press('live_refresh', {})
		await values(
			m,
			{
				s1: 'Blue',
				s2: 'Opening',
				s3: 'Encore',
				s4: '',
				l1: 'green',
				l2: 'amber',
				l3: 'grey',
				p1: 'Stage right',
				p2: '',
			},
			'laid out again',
		)

		// Held again from the new layout: a reorder (with a rename, shown in place) moves nothing until the show is disarmed.
		encore.label = 'Finale'
		show.items = [encore, intro, blue]
		doc.revision += 1
		doc.sig = '1f2e3d4c5b6a7988'
		doc.labels = '2345bcde6789f0a1'
		node.pushIndex(doc)
		await values(m, { s1: 'Blue', s2: 'Opening', s3: 'Finale' }, 'held after RE-LAY, relabelled in place')
		const disarmed = copy(moved)
		disarmed.show.armed = false
		node.pushState(disarmed)
		await values(m, { armed: false, s1: 'Finale', s2: 'Opening', s3: 'Blue' }, 'laid out again once disarmed')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('the slots hold as the keys showed them when armed, even when INDEX moves before the next flush', async () => {
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', port: 9940 }, {}, { factory, random: () => 0.5 })
	try {
		const ch = opened[0]
		assert.ok(ch)
		ch.events.open({})
		await settle()
		ch.ok('HELLO')
		await settle()
		const state = stateDoc()
		const doc = indexDoc()
		ch.say('STATE ' + JSON.stringify(state))
		ch.say('INDEX ' + JSON.stringify(doc))
		await m.place('armed', 'show_armed', {})
		for (let n = 1; n <= 3; n++) await m.place(`s${n}`, 'slot', { rule: '', n, field: 'label' })
		await values(m, { armed: false, s1: 'Intro', s2: 'Red', s3: 'Blue' }, 'the slots before arming')

		// The node arms the show and, a moment later, reorders the playlist: both before the module's next flush.
		state.show.armed = true
		playlistOf(doc, SHOW.playlist).items.reverse()
		doc.revision += 1
		doc.sig = '0f1e2d3c4b5a6978'
		ch.say('STATE ' + JSON.stringify(state))
		ch.say('INDEX ' + JSON.stringify(doc))
		await m.host.until(() => m.host.feedbackValues.get('armed') === true, 'armed')
		assert.deepEqual(
			{ s1: m.host.feedbackValues.get('s1'), s2: m.host.feedbackValues.get('s2'), s3: m.host.feedbackValues.get('s3') },
			{ s1: 'Intro', s2: 'Red', s3: 'Blue' },
			'no key moved under a finger once the show was armed',
		)
	} finally {
		await m.stop()
	}
})

void test("a short press sends the slot's line when its verb is instant, an output's only held; a slot that only shows, or is past the end, sends nothing", async () => {
	// A long double-GO guard: a playlist's second GO in this test is held, whichever slot sends it.
	const { node, m } = await linked({ config: { min_go_ms: 60_000 } })
	try {
		const index = indexDoc()
		const show = playlistOf(index, SHOW.playlist)
		const loops = playlistOf(index, SHOW.loops)
		const [, red, blue] = show.items
		const [green] = loops.items
		assert.ok(red && blue && green)
		const press = async (options: CompanionOptionValues, controlId: string): Promise<CompanionOptionValues> =>
			(await m.press('slot_press', options, controlId)) as CompanionOptionValues
		await m.place('hint', 'slot_hint', { rule: '', n: 3 })
		await values(m, { hint: false }, 'no hint')

		// Slots that send nothing: faults only show, slot 4 of the focused playlist is past its end.
		for (const options of [
			{ rule: 'faults', n: 1, held: false },
			{ rule: '', n: 4, held: false },
			{ rule: 'faults', n: 1, held: true },
		]) {
			const r = await press(options, 'key-none')
			assert.equal(r.ok, false, JSON.stringify(options))
			assert.equal(r.stage, 'not-sent', JSON.stringify(options))
		}

		const sent: string[] = []
		for (const [options, line] of [
			[{ rule: '', n: 3, held: false }, `PLAYLIST GO ${show.id} ${blue.id}`],
			[{ rule: `playlist:${loops.id}`, n: 1, held: false }, `PLAYLIST GO ${loops.id} ${green.id}`],
			[{ rule: 'section:show', n: 1, held: false }, 'SHOW ARM'],
			// Held, an instant slot sends all the same.
			[{ rule: 'section:show', n: 2, held: true }, 'SHOW DISARM'],
		] as const) {
			const r = await press(options, `key-${sent.length}`)
			assert.equal(r.ok, true, line)
			assert.equal(r.stage, 'applied', line)
			await node.heard(new RegExp(`^@c\\d+ ${line}$`, 'u'))
			sent.push(line)
		}

		// An output identifies only when held: it puts a picture over the air.
		const short = await press({ rule: 'outputs', n: 1, held: false }, 'key-output')
		assert.equal(short.ok, false)
		assert.equal(short.stage, 'not-sent')
		const held = await press({ rule: 'outputs', n: 1, held: true }, 'key-output')
		assert.equal(held.stage, 'applied')
		await node.heard(/^@c\d+ OUTPUT IDENTIFY op_1$/u)
		sent.push('OUTPUT IDENTIFY op_1')

		// The double-GO guard holds a second GO of the show's playlist from another slot.
		const again = await press({ rule: '', n: 2, held: true }, 'key-again')
		assert.equal(again.ok, false)
		assert.equal(again.stage, 'not-sent')
		await m.press('raw', { line: 'PLAYLIST LIST' }, 'key-list')
		await node.heard(/^@c\d+ PLAYLIST LIST$/u)

		const written = node.sent().filter((l) => /^(PLAYLIST GO|OUTPUT IDENTIFY|SHOW |PANIC)/u.test(l))
		assert.deepEqual(written, sent, 'only the instant slots’ lines and the held output’s, once each, in order')
		assert.ok(!node.sent().includes(`PLAYLIST GO ${show.id} ${red.id}`))
		await values(m, { hint: false }, 'an instant slot never says HOLD')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a deck that links to an armed show, or relinks to one, lays its slots out; focus moving while armed moves none', async () => {
	const armedState = stateDoc()
	armedState.show.armed = true
	const { node, m } = await linked({ state: armedState, config: { min_go_ms: 0 } })
	try {
		for (let n = 1; n <= 3; n++) await m.place(`s${n}`, 'slot', { rule: '', n, field: 'label' })
		await values(
			m,
			{ s1: 'Intro', s2: 'Red', s3: 'Blue' },
			'linked to an armed show: the show’s items, not an empty page',
		)

		// The link drops and comes back, the show still armed: the same slots.
		for (const s of [...node.sessions]) s.close(1001)
		await m.host.until(() => m.instance.link() !== 'live', 'the link down')
		await m.host.until(() => m.instance.link() === 'live', 'linked again')
		await values(m, { s1: 'Intro', s2: 'Red', s3: 'Blue' }, 'relinked while armed: held as before the drop')

		// The desk focuses Loops while armed: the slots keep Show's items, and a press sends Show's GO of the item shown.
		const moved = copy(armedState)
		for (const p of moved.playlists) p.focused = p.id === SHOW.loops
		node.pushState(moved)
		await m.host.until(() => m.host.variables.get('focused') === SHOW.loops, 'Loops focused')
		await values(m, { s1: 'Intro', s2: 'Red', s3: 'Blue' }, 'focus moved while armed: no slot moves or reads REMOVED')
		const r = (await m.press('slot_press', { rule: '', n: 3, held: false }, 'key-slot')) as { ok: boolean }
		assert.equal(r.ok, true)
		await node.heard(new RegExp(`^@c\\d+ PLAYLIST GO ${SHOW.playlist} ${SHOW.blue}$`, 'u'))
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a slot is pressed as its line says: instant on the down, held at 800 ms, twice held within 3 s; a section key alike', async () => {
	const doc = indexDoc()
	const align = doc.sections.find((s) => s.section === 'align')
	const calibration = doc.sections.find((s) => s.section === 'calibration')
	assert.ok(align && calibration)
	align.rows = [{ id: 'al00000001', kind: 'align', label: 'Wall', parent: null, light: 'grey', state: 'idle' }]
	calibration.rows = [
		{ id: 'pj00000001', kind: 'projector', label: 'Left', parent: null, light: 'green', state: 'A 96.0' },
	]
	const { node, m } = await linked({ index: doc, config: { min_go_ms: 0 } })
	try {
		const press = async (rule: string, phase: string, key = `key-${rule}`): Promise<{ ok: boolean; stage: string }> =>
			(await m.press('slot_press', { rule, n: 1, held: phase === 'hold800', phase }, key)) as {
				ok: boolean
				stage: string
			}
		const sent = (): string[] => node.sent().filter((l) => /^(PLAYLIST GO|OUTPUT IDENTIFY|ALIGN RUN)/u.test(l))

		// An instant slot (a GO): sent on the down, once; its release and its 800 ms do nothing.
		assert.equal((await press('', 'down')).stage, 'applied')
		assert.equal((await press('', 'up')).stage, 'not-sent')
		assert.equal((await press('', 'hold800')).stage, 'not-sent')
		assert.deepEqual(sent(), [`PLAYLIST GO ${SHOW.playlist} ${SHOW.intro}`])

		// A held slot (an output's identify): nothing on the down, HOLD on a short press's release, sent at 800 ms.
		assert.equal((await press('outputs', 'down')).stage, 'not-sent')
		assert.equal((await press('outputs', 'up')).stage, 'not-sent')
		await m.place('hint', 'slot_hint', { rule: 'outputs', n: 1 })
		await values(m, { hint: true }, 'HOLD on a short press')
		assert.equal((await press('outputs', 'hold800')).stage, 'applied')

		// A slot pressed twice (the hint says so for ALIGN RUN): the first held press arms it (SURE?), the second sends.
		await m.place('sure', 'confirm_pending', {}, 'key-section:align')
		const first = await press('section:align', 'hold800')
		assert.equal(first.stage, 'not-sent')
		await values(m, { sure: true }, 'SURE? after the first held press')
		assert.equal((await press('section:align', 'hold800')).stage, 'applied')
		assert.deepEqual(sent(), [
			`PLAYLIST GO ${SHOW.playlist} ${SHOW.intro}`,
			'OUTPUT IDENTIFY op_1',
			'ALIGN RUN al00000001',
		])

		// A section key is pressed as the stronger of its hint's press and its line's: CALIBRATION RESOLVE is held by the catalogue
		// though its hint says instant.
		const structure = m.host.structure as { id: string; definitions: { id: string; presetId?: string }[] }[]
		const calibrationKeys = structure.find((s) => s.id === 'sec.calibration')?.definitions ?? []
		assert.equal(calibrationKeys.find((d) => d.id === 'sec.calibration.0')?.presetId, 'section_key_hold')
		assert.equal(calibrationKeys.find((d) => d.id === 'sec.calibration.2')?.presetId, 'section_key_instant')
		const alignKeys = structure.find((s) => s.id === 'sec.align')?.definitions ?? []
		assert.equal(alignKeys.find((d) => d.id === 'sec.align.0')?.presetId, 'section_key_double')
	} finally {
		await m.stop()
		await node.stop()
	}
})
