// Pictures on keys: a thumb feedback for an item id shows its source's picture, asked over the link once per ref and size
// (`THUMB <ref> <n>x<n>`); the key shows '' until the picture arrives and is then checked again by its id with the data URI.
// Every key with that ref and size shows the held picture at once and nothing is asked again; the cache keeps the 256 shown last.
// The refs placed keys show are told to the node with one SUBSCRIBE THUMBS a debounce after they change: a key that leaves drops
// its ref, and the last one leaving tells the node to watch none. A THUMBS push whose ETag moved for a placed ref fetches it again
// with IF <the ETag held>, after a relink too; a refused THUMB leaves the key without a picture and the module running. A key on an
// item shows its source as INDEX has it now; a picture of the air goes with the link, a colour's stays; one refused while the show
// was armed is asked again when it disarms.
import test from 'node:test'
import assert from 'node:assert/strict'
import { isDeepStrictEqual } from 'node:util'
import type { HostFeedbackValue } from '@companion-module/host'
import { FakeNode, SHOW, copy, fixture } from './fake-node.ts'
import { boot, configFor, logs, type Booted } from './harness.ts'
import { advance, fakeChannels, settle, type FakeChannel, type FakeTimers } from './fake-channel.ts'
import { MAX_CACHE_CHARS, MAX_PICTURE_CHARS, THUMB_CACHE, Thumbs, WATCH_DEBOUNCE_MS } from '../src/thumbs.ts'
import type { SessionOptions } from '../src/link/session.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

interface ItemJson {
	id: string
	label: string
	source: string
}

interface IndexJson {
	playlists: { id: string; items: ItemJson[] }[]
}

/** The show's items as INDEX lists them (Intro's source is a generator, Red's and Blue's are colours). */
function items(): { intro: ItemJson; red: ItemJson; blue: ItemJson } {
	const show = copy(fixture('index.json') as IndexJson).playlists.find((p) => p.id === SHOW.playlist)
	const [intro, red, blue] = show?.items ?? []
	assert.ok(intro && red && blue)
	assert.equal(red.id, SHOW.red)
	assert.equal(blue.id, SHOW.blue)
	return { intro, red, blue }
}

/** The picture the fake node answers for a ref: its bytes are the ref's own. */
const pictureOf = (ref: string): string => `data:image/png;base64,${Buffer.from(ref).toString('base64')}`

const escape = (s: string): string => s.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&')

/** A line the module sent, by its words after the request id. */
const sentLine = (words: string): RegExp => new RegExp(`^@c\\d+ ${escape(words)}$`, 'u')

/** The THUMB lines the node heard for a ref, without their request ids. */
const thumbLines = (node: FakeNode, ref: string): string[] => node.sent().filter((l) => l.startsWith(`THUMB ${ref} `))

/** Waits until the host holds these feedback values, then asserts them (a timeout shows what it holds instead). */
async function values(m: Booted, expected: Record<string, unknown>, what: string): Promise<void> {
	const held = (): Record<string, unknown> =>
		Object.fromEntries(Object.keys(expected).map((id) => [id, m.host.feedbackValues.get(id)]))
	await m.host.until(() => isDeepStrictEqual(held(), expected), what).catch(() => undefined)
	assert.deepEqual(held(), expected, what)
}

/** A paired node (token proof over ws) whose INDEX and greeting STATE the module has heard. */
async function linked(session?: SessionOptions): Promise<{ node: FakeNode; m: Booted }> {
	const node = await new FakeNode({ token: TOKEN }).start()
	const m = await boot(configFor(node.webPort, node.wirePort), { token: TOKEN }, session)
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

/** Places several thumb feedbacks in one host call, as Companion does when a page of keys loads. */
async function placeTogether(
	m: Booted,
	feedbacks: Record<string, { feedbackId?: string; options: object }>,
): Promise<void> {
	await m.wrapper.updateFeedbacks(
		Object.fromEntries(
			Object.entries(feedbacks).map(([id, f]) => [
				id,
				{ id, controlId: `c-${id}`, feedbackId: f.feedbackId ?? 'thumb', options: { ...f.options } },
			]),
		),
	)
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

// On fake timers (the debounce is measured); first in the file, so no link of a test before it is still closing when its clock is
// mocked.
void test('placed refs are told with one SUBSCRIBE THUMBS 250 ms after they change; a key that leaves drops its ref, the last one leaving tells the node to watch none', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', port: 9940 }, {}, { factory, random: () => 0.5 })
	try {
		assert.equal(WATCH_DEBOUNCE_MS, 250)
		const { intro, red, blue } = items()
		const ch = opened[0]
		assert.ok(ch)
		const answered = new Set<string>()
		ch.events.open({})
		await settle()
		ch.ok('HELLO')
		await settle()
		ch.say('STATE ' + JSON.stringify(fixture('state.json')))
		ch.say('INDEX ' + JSON.stringify(fixture('index.json')))
		// Past the watch the pairing itself scheduled, so the keys below start their own.
		await run(t.mock.timers, ch, answered, WATCH_DEBOUNCE_MS + 50)
		assert.equal(m.instance.link(), 'live')
		const watches = (): string[] =>
			ch.sent.filter((l) => /^@c\d+ SUBSCRIBE THUMBS\b/u.test(l)).map((l) => l.replace(/^@\S+ /u, ''))
		assert.deepEqual(watches(), [], 'no key shows a picture: none is watched')

		// A page of keys loads: Red by its item id and by its source, Blue, and Live slot 1 (Intro, whose source is a generator).
		await placeTogether(m, {
			red: { options: { ref: red.id, size: 96 } },
			red2: { options: { ref: red.source, size: 96 } },
			blue: { options: { ref: blue.id, size: 96 } },
			slot: { feedbackId: 'slot', options: { rule: '', n: 1, field: 'thumb' } },
		})
		await run(t.mock.timers, ch, answered, WATCH_DEBOUNCE_MS - 10)
		assert.deepEqual(watches(), [], 'nothing told before the debounce')
		await run(t.mock.timers, ch, answered, 20)
		assert.deepEqual(
			watches(),
			[`SUBSCRIBE THUMBS ${[red.source, blue.source, intro.source].sort().join(' ')}`],
			'one line, each ref once',
		)
		ch.ok('SUBSCRIBE THUMBS', 'subscribed thumbs')
		// The node hears how many pictures this deck shows.
		await run(t.mock.timers, ch, answered, 1000)
		assert.ok(
			ch.sent.some((l) => /^@c\d+ PRESENCE \{.*"thumbs":3[,}]/u.test(l)),
			'PRESENCE counts the pictures shown',
		)

		// Blue's key leaves: the next line drops it.
		await m.unplace('blue')
		await run(t.mock.timers, ch, answered, WATCH_DEBOUNCE_MS + 10)
		assert.deepEqual(watches().slice(1), [`SUBSCRIBE THUMBS ${[red.source, intro.source].sort().join(' ')}`])
		ch.ok('SUBSCRIBE THUMBS', 'subscribed thumbs')

		// One of the two keys showing Red leaves: Red is still shown, so the watched set is the same and nothing is said.
		await m.unplace('red')
		await run(t.mock.timers, ch, answered, WATCH_DEBOUNCE_MS + 50)
		assert.equal(watches().length, 2)

		// The last keys leave: the set the node watches is replaced by none.
		await m.unplace('red2')
		await m.unplace('slot')
		await run(t.mock.timers, ch, answered, WATCH_DEBOUNCE_MS + 50)
		assert.deepEqual(watches().slice(2), ['SUBSCRIBE THUMBS'], 'the node is told no key shows a picture')
		assert.equal(m.instance.link(), 'live')
	} finally {
		await m.stop()
	}
})

void test("an item's picture is asked once as its source's THUMB: '' until it arrives, then the data URI, rechecked by id", async () => {
	const { node, m } = await linked()
	try {
		const { red, blue } = items()
		// Red's picture is held back until the test answers it.
		node.handlers.set('THUMB', (_line, words) => (words[1] === red.source ? null : undefined))
		const rechecked: string[][] = []
		const recheck = m.instance.checkFeedbacksById.bind(m.instance)
		m.instance.checkFeedbacksById = (...ids: string[]): void => {
			rechecked.push([...ids].sort())
			recheck(...ids)
		}
		await m.place('label', 'label', { ref: red.id })
		await m.place('red', 'thumb', { ref: red.id, size: 96 })
		const asked = await node.heard(sentLine(`THUMB ${red.source} 96x96`))
		// A second key with the same picture while it is on its way, and a key with another.
		await m.place('red2', 'thumb', { ref: red.source, size: 96 })
		await m.place('blue', 'thumb', { ref: blue.id, size: 96 })
		await values(
			m,
			{ label: red.label, red: '', red2: '', blue: pictureOf(blue.source) },
			"'' while Red's picture is on its way",
		)

		const session = [...node.sessions][0]
		assert.ok(session)
		const picture = { ref: red.source, etag: 'color-FF0000-96x96-p', mime: 'image/png', w: 96, h: 96, data: 'UkVE' }
		session.send(`OK ${asked.split(' ')[0]} ${JSON.stringify(picture)}`)
		await values(
			m,
			{ red: 'data:image/png;base64,UkVE', red2: 'data:image/png;base64,UkVE' },
			'the picture, on both keys',
		)
		assert.deepEqual(thumbLines(node, red.source), [`THUMB ${red.source} 96x96`], 'asked once')
		assert.deepEqual(
			rechecked.filter((ids) => ids.includes('red') || ids.includes('red2')),
			[['red', 'red2']],
			'rechecked by id: the keys showing it, together, once',
		)
		assert.ok(!rechecked.flat().includes('label'), 'no other key is rechecked for a picture')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a picture held is shown at once to a second key with the same ref and size and never asked again; another size is its own', async () => {
	const { node, m } = await linked()
	try {
		const { red } = items()
		await m.place('a', 'thumb', { ref: red.id, size: 96 })
		await values(m, { a: pictureOf(red.source) }, "Red's picture")

		const seen: unknown[] = []
		const update = m.host.updateFeedbackValues.bind(m.host)
		m.host.updateFeedbackValues = (sent: HostFeedbackValue[]): void => {
			for (const v of sent) if (v.id === 'b') seen.push(v.value)
			update(sent)
		}
		await m.place('b', 'thumb', { ref: red.source, size: 96 })
		await values(m, { b: pictureOf(red.source) }, 'the held picture')
		assert.deepEqual(seen, [pictureOf(red.source)], "the second key's first value is the picture, never ''")

		await m.place('c', 'thumb', { ref: red.id, size: 144 })
		await values(m, { c: pictureOf(red.source) }, 'the 144 px picture')
		assert.deepEqual(
			thumbLines(node, red.source),
			[`THUMB ${red.source} 96x96`, `THUMB ${red.source} 144x144`],
			'each size asked once',
		)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a THUMBS push whose ETag moved for a placed ref fetches it again with IF <etag>; an unmoved or unplaced ref is not asked', async () => {
	const { node, m } = await linked()
	try {
		const { red, blue } = items()
		const second = (ref: string): { ref: string; etag: string; mime: string; w: number; h: number; data: string } => ({
			ref,
			etag: `etag-${ref}-2`,
			mime: 'image/png',
			w: 96,
			h: 96,
			data: Buffer.from(`${ref} repainted`).toString('base64'),
		})
		// Asked IF its first ETag, a picture is the repainted one; asked IF the repainted one's, it is the same.
		node.handlers.set('THUMB', (line, words) => {
			if (!line.includes(' IF ')) return undefined
			const ref = words[1] ?? ''
			return words.at(-1) === `etag-${ref}-2`
				? `OK ${JSON.stringify({ ref, etag: `etag-${ref}-2`, same: true })}`
				: `OK ${JSON.stringify(second(ref))}`
		})
		await placeTogether(m, {
			red: { options: { ref: red.id, size: 96 } },
			blue: { options: { ref: blue.id, size: 96 } },
		})
		await node.heard(sentLine(`SUBSCRIBE THUMBS ${[red.source, blue.source].sort().join(' ')}`))
		await values(m, { red: pictureOf(red.source), blue: pictureOf(blue.source) }, 'the first pictures')

		// Red's ETag moves (Blue's does not; Green is on no key).
		node.push(
			'THUMBS ' +
				JSON.stringify({
					[red.source]: `etag-${red.source}-2`,
					[blue.source]: `etag-${blue.source}-1`,
					'color:#00FF00': 'etag-color:#00FF00-2',
				}),
		)
		await node.heard(sentLine(`THUMB ${red.source} 96x96 IF etag-${red.source}-1`))
		const repainted = (ref: string): string => `data:image/png;base64,${second(ref).data}`
		await values(m, { red: repainted(red.source), blue: pictureOf(blue.source) }, "Red's new picture")

		// Then Blue's moves and Red's stays.
		node.push(
			'THUMBS ' + JSON.stringify({ [red.source]: `etag-${red.source}-2`, [blue.source]: `etag-${blue.source}-2` }),
		)
		await node.heard(sentLine(`THUMB ${blue.source} 96x96 IF etag-${blue.source}-1`))
		await values(m, { red: repainted(red.source), blue: repainted(blue.source) }, "Blue's new picture")

		// Red moves again and the node says it is the same picture: the key keeps it.
		node.push(
			'THUMBS ' + JSON.stringify({ [red.source]: `etag-${red.source}-3`, [blue.source]: `etag-${blue.source}-2` }),
		)
		await node.heard(sentLine(`THUMB ${red.source} 96x96 IF etag-${red.source}-2`))
		// Its answer came before WHOAMI's on the link, and what the module does with it once read is promise work that settles within
		// a turn. The cache is asked as the key asks it, unlinked (nothing is fetched) and with the key's own ref and size (nothing is
		// placed again).
		await m.instance.ask('WHOAMI')
		await settle()
		assert.equal(m.instance.thumbs.get('red', red.source, 96, false), repainted(red.source), 'the picture held')
		assert.equal(m.host.feedbackValues.get('red'), repainted(red.source))

		assert.deepEqual(thumbLines(node, red.source), [
			`THUMB ${red.source} 96x96`,
			`THUMB ${red.source} 96x96 IF etag-${red.source}-1`,
			`THUMB ${red.source} 96x96 IF etag-${red.source}-2`,
		])
		assert.deepEqual(thumbLines(node, blue.source), [
			`THUMB ${blue.source} 96x96`,
			`THUMB ${blue.source} 96x96 IF etag-${blue.source}-1`,
		])
		assert.deepEqual(thumbLines(node, 'color:#00FF00'), [], 'a ref on no key is never asked')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a picture the node says is gone (null in THUMBS), or refused when asked again, leaves its keys with none', async () => {
	const { node, m } = await linked()
	try {
		const { red, blue } = items()
		// Asked again, the node has no picture of it now.
		node.handlers.set('THUMB', (line) => (line.includes(' IF ') ? 'ERR Show is idle — no item on it' : undefined))
		await placeTogether(m, {
			red: { options: { ref: red.id, size: 96 } },
			blue: { options: { ref: blue.id, size: 96 } },
		})
		await values(m, { red: pictureOf(red.source), blue: pictureOf(blue.source) }, 'the first pictures')
		node.push('THUMBS ' + JSON.stringify({ [red.source]: null, [blue.source]: `etag-${blue.source}-1` }))
		await values(m, { red: '', blue: pictureOf(blue.source) }, 'null: the key shows none, never the last picture')
		node.push('THUMBS ' + JSON.stringify({ [red.source]: null, [blue.source]: `etag-${blue.source}-2` }))
		await node.heard(sentLine(`THUMB ${blue.source} 96x96 IF etag-${blue.source}-1`))
		await values(m, { red: '', blue: '' }, 'refused when asked again: the key shows none')
		await m.instance.ask('WHOAMI')
		assert.deepEqual(thumbLines(node, red.source), [`THUMB ${red.source} 96x96`], 'a picture that is gone is not asked')
		assert.deepEqual(
			thumbLines(node, blue.source),
			[`THUMB ${blue.source} 96x96`, `THUMB ${blue.source} 96x96 IF etag-${blue.source}-1`],
			'a refused picture is not asked again on its own',
		)

		// Until the node says it moved: then it is asked, and shown.
		node.handlers.delete('THUMB')
		node.push(
			'THUMBS ' + JSON.stringify({ [red.source]: `etag-${red.source}-1`, [blue.source]: `etag-${blue.source}-3` }),
		)
		await values(
			m,
			{ red: pictureOf(red.source), blue: pictureOf(blue.source) },
			'a picture again once the node has one',
		)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a refused THUMB leaves the key without a picture, and the module runs on', async () => {
	const { node, m } = await linked()
	try {
		const { intro, red } = items()
		const words = 'no picture of it yet — posters are made in rehearsal or on import'
		node.handlers.set('THUMB', (_line, w) => (w[1] === intro.source ? `ERR ${words}` : undefined))
		const before = logs.length
		await m.place('intro', 'thumb', { ref: intro.id, size: 96 })
		await node.heard(sentLine(`THUMB ${intro.source} 96x96`))
		// Red's picture is asked after Intro's refusal was said, so its arrival means the refusal was read.
		await m.place('red', 'thumb', { ref: red.id, size: 96 })
		await values(m, { intro: '', red: pictureOf(red.source) }, "no picture for Intro, Red's for Red")
		assert.equal(m.instance.link(), 'live')
		assert.deepEqual(
			logs.slice(before).filter((l) => l.level === 'error'),
			[],
			'no feedback check failed',
		)
		// What last_refusal and last_receipt are made from, set as a reply is read (the variables follow at the next flush).
		assert.deepEqual(
			{ refusal: m.instance.receipts.lastRefusal, receipt: m.instance.receipts.lastReceipt },
			{ refusal: '', receipt: '' },
			'a picture is not a press: no key’s refusal or receipt',
		)
		assert.notEqual(m.host.variables.get('last_refusal'), words, 'a picture is not a press: no key’s refusal')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('after the link drops and pairs again, a picture whose ETag moved meanwhile is fetched again', async () => {
	const { node, m } = await linked({ random: () => 0.5 })
	try {
		const { red } = items()
		await m.place('red', 'thumb', { ref: red.id, size: 96 })
		await values(m, { red: pictureOf(red.source) }, 'the first picture')
		await node.heard(sentLine(`SUBSCRIBE THUMBS ${red.source}`))

		// While the link is down the picture is repainted on the node; its THUMBS push after the relink names the new ETag.
		const fresh = {
			ref: red.source,
			etag: `etag-${red.source}-2`,
			mime: 'image/png',
			w: 96,
			h: 96,
			data: Buffer.from('repainted').toString('base64'),
		}
		node.handlers.set('THUMB', () => `OK ${JSON.stringify(fresh)}`)
		const resubscribed = new Promise<void>((resolve) => {
			node.handlers.set('SUBSCRIBE THUMBS', (_line, words) => {
				node.push('THUMBS ' + JSON.stringify(Object.fromEntries(words.slice(2).map((r) => [r, `etag-${r}-2`]))))
				resolve()
				return 'OK subscribed thumbs'
			})
		})
		const dropped = node.lines.length
		for (const s of node.sessions) s.close(1001)
		await resubscribed
		// Two round trips: the push has been read, and any line it made has reached the node.
		await m.instance.ask('WHOAMI')
		await m.instance.ask('WHOAMI')
		assert.ok(
			node.lines.slice(dropped).some((l) => sentLine(`THUMB ${red.source} 96x96 IF etag-${red.source}-1`).test(l)),
			`the moved picture is asked again IF the ETag held; the node heard: ${node.lines
				.slice(dropped)
				.filter((l) => / THUMBS? /u.test(l))
				.join(' | ')}`,
		)
		await values(m, { red: `data:image/png;base64,${fresh.data}` }, 'the key shows the picture as it is now')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('the cache keeps the 256 pictures shown last: one more lets go of the least recently shown, which is asked again', async () => {
	const asked: string[] = []
	const thumbs = new Thumbs(
		async (line) => {
			asked.push(line)
			const ref = line.split(' ')[1] ?? ''
			const data = Buffer.from(ref).toString('base64')
			return { ok: true, accepted: false, payload: JSON.stringify({ ref, etag: `e-${ref}`, mime: 'image/png', data }) }
		},
		() => undefined,
		() => undefined,
	)
	try {
		assert.equal(THUMB_CACHE, 256)
		const ref = (i: number): string => `color:#${i.toString(16).toUpperCase().padStart(6, '0')}`
		for (let i = 0; i < THUMB_CACHE; i++) assert.equal(thumbs.get(`k${i}`, ref(i), 96, true), '')
		await settle()
		assert.equal(asked.length, THUMB_CACHE)
		// Picture 0 is shown again (now the most recent), then a 257th arrives.
		assert.equal(thumbs.get('k0', ref(0), 96, true), pictureOf(ref(0)))
		assert.equal(thumbs.get(`k${THUMB_CACHE}`, ref(THUMB_CACHE), 96, true), '')
		await settle()
		assert.equal(thumbs.get('k0', ref(0), 96, true), pictureOf(ref(0)), 'shown lately: kept')
		assert.equal(thumbs.get('k2', ref(2), 96, true), pictureOf(ref(2)), 'kept')
		assert.equal(thumbs.get(`k${THUMB_CACHE}`, ref(THUMB_CACHE), 96, true), pictureOf(ref(THUMB_CACHE)), 'the newest')
		assert.equal(asked.length, THUMB_CACHE + 1, 'held pictures are not asked again')
		assert.equal(thumbs.get('k1', ref(1), 96, true), '', 'the least recently shown was let go')
		assert.deepEqual(asked.slice(THUMB_CACHE + 1), [`THUMB ${ref(1)} 96x96`])
		await settle()
	} finally {
		thumbs.clear()
	}
})

void test('a picture of another type, or heavier than a key’s picture can be, is refused and not asked again; the cache has a weight', async () => {
	const asked: string[] = []
	const replies: Record<string, { mime: string; chars: number }> = {
		'color:#000001': { mime: 'image/png', chars: MAX_PICTURE_CHARS + 4 },
		'color:#000002': { mime: 'text/html', chars: 8 },
	}
	const thumbs = new Thumbs(
		async (line) => {
			asked.push(line)
			const ref = line.split(' ')[1] ?? ''
			const r = replies[ref] ?? { mime: 'image/png', chars: MAX_PICTURE_CHARS }
			const data = 'A'.repeat(r.chars)
			return { ok: true, accepted: false, payload: JSON.stringify({ ref, etag: `e-${ref}`, mime: r.mime, data }) }
		},
		() => undefined,
		() => undefined,
	)
	try {
		for (const ref of Object.keys(replies)) assert.equal(thumbs.get(ref, ref, 96, true), '')
		await settle()
		for (const ref of Object.keys(replies)) assert.equal(thumbs.get(ref, ref, 96, true), '', `${ref}: no picture`)
		assert.equal(asked.length, 2, 'a refused picture is not asked again on its own')

		// Pictures of the heaviest a key may take: the cache keeps no more than its weight, the least recently shown going first.
		const n = Math.floor(MAX_CACHE_CHARS / MAX_PICTURE_CHARS) + 2
		const ref = (i: number): string => `color:#${(16 + i).toString(16).toUpperCase().padStart(6, '0')}`
		for (let i = 0; i < n; i++) thumbs.get(`h${i}`, ref(i), 96, true)
		await settle()
		const held = Array.from({ length: n }, (_, i) => thumbs.get(`h${i}`, ref(i), 96, false) !== '').filter(
			Boolean,
		).length
		assert.equal(held, Math.floor(MAX_CACHE_CHARS / MAX_PICTURE_CHARS))
		assert.equal(thumbs.get('h0', ref(0), 96, false), '', 'the first shown went first')
	} finally {
		thumbs.clear()
	}
})

void test("an item key's picture follows its item's source when INDEX moves it", async () => {
	const { node, m } = await linked()
	try {
		const { red } = items()
		await m.place('red', 'thumb', { ref: red.id, size: 96 })
		await values(m, { red: pictureOf(red.source) }, "Red's picture")

		// The item's source changes on the node (and its label with it): INDEX says so, and the key shows the new source's picture.
		const doc = copy(fixture('index.json') as IndexJson & { labels: string })
		const item = doc.playlists.find((p) => p.id === SHOW.playlist)?.items.find((i) => i.id === red.id)
		assert.ok(item)
		item.source = 'color:#00FF00'
		item.label = 'Green'
		doc.labels = 'feedc0de12345678'
		node.pushIndex(doc)
		await values(m, { red: pictureOf('color:#00FF00') }, "the new source's picture")
		assert.deepEqual(thumbLines(node, 'color:#00FF00'), ['THUMB color:#00FF00 96x96'])
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("a picture of the air (a playlist's current item) goes with the link; a colour's stays", async () => {
	const { node, m } = await linked()
	let stopped = false
	try {
		const { red } = items()
		await m.place('show', 'thumb', { ref: SHOW.playlist, size: 96 })
		await m.place('red', 'thumb', { ref: red.id, size: 96 })
		await values(
			m,
			{ show: pictureOf(`playlist:${SHOW.playlist}`), red: pictureOf(red.source) },
			"the playlist's picture and Red's",
		)
		await node.stop()
		stopped = true
		await m.host.until(() => m.instance.link() === 'connecting', 'the dropped link')
		await values(
			m,
			{ show: '', red: pictureOf(red.source) },
			'what is on the air is no fact without the link; a colour is',
		)
	} finally {
		await m.stop()
		if (!stopped) await node.stop()
	}
})

void test('a picture the node refused while the show was armed is asked again once the show disarms', async () => {
	const { node, m } = await linked()
	try {
		const { red } = items()
		await m.place('red', 'thumb', { ref: red.id, size: 96 })
		await values(m, { red: pictureOf(red.source) }, "Red's picture in rehearsal")
		await node.heard(sentLine(`SUBSCRIBE THUMBS ${red.source}`))

		// Armed, the node serves only posters made before, and Red's picture moved: asked again, it is refused.
		let armed = true
		node.handlers.set('THUMB', () =>
			armed ? 'ERR no picture of it yet — posters are made in rehearsal or on import' : undefined,
		)
		const state = copy(fixture('state.json')) as { show: { armed: boolean } }
		state.show.armed = true
		node.pushState(state)
		await m.host.until(() => m.host.variables.get('show_mode') === 'ARMED', 'armed')
		node.push('THUMBS ' + JSON.stringify({ [red.source]: `etag-${red.source}-2` }))
		await node.heard(sentLine(`THUMB ${red.source} 96x96 IF etag-${red.source}-1`))
		await values(m, { red: '' }, 'refused while armed: the key shows none')

		// Disarmed: the node makes pictures again, and the key's is asked again and shown.
		armed = false
		state.show.armed = false
		node.pushState(state)
		await values(m, { red: pictureOf(red.source) }, 'asked again once disarmed, and shown')
		assert.deepEqual(thumbLines(node, red.source), [
			`THUMB ${red.source} 96x96`,
			`THUMB ${red.source} 96x96 IF etag-${red.source}-1`,
			`THUMB ${red.source} 96x96`,
		])
	} finally {
		await m.stop()
		await node.stop()
	}
})
