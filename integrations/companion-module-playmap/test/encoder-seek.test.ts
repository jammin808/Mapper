// A seek dial on a playing item: the playhead moves by itself, so a target sent steers the next turn only until the first STATE
// written after it (its seq moved), which shows where the air went; a turn after that seeks from the playhead STATE shows, never
// behind it. A seek's target is its playlist's and its item's. On fake timers, in its own file so its clock is its own.
import test from 'node:test'
import assert from 'node:assert/strict'
import { boot } from './harness.ts'
import { fakeChannels, settle } from './fake-channel.ts'
import { SHOW, copy, fixture } from './fake-node.ts'

interface PlaylistJson {
	id: string
	[key: string]: unknown
}

interface StateJson {
	seq: number
	at: number
	playlists: PlaylistJson[]
}

void test('a seek turned while the item plays steers from the playhead once a STATE written after it has come', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const { factory, opened } = fakeChannels()
	const m = await boot({ host: '10.0.0.5', port: 9940 }, {}, { factory, random: () => 0.5 })
	try {
		const ch = opened[0]
		assert.ok(ch)
		// The node: PINGs answered with its clock, every VIDEO line applied.
		const answered = new Set<string>()
		const serve = (): void => {
			for (const l of ch.sent) {
				const [id = '', verb = '', t1 = ''] = l.split(' ')
				if (answered.has(id)) continue
				if (verb === 'PING') ch.say(`OK ${id} PONG ${t1} ${Date.now()} ${Date.now()}`)
				else if (verb === 'VIDEO') ch.say(`OK ${id}`)
				else continue
				answered.add(id)
			}
		}
		const wait = async (ms: number): Promise<void> => {
			for (let left = ms; left > 0; left -= 10) {
				t.mock.timers.tick(Math.min(10, left))
				await settle()
				serve()
			}
		}
		// The air: Show plays Red, 300 s long, at rate 1, from 100 s in. Each STATE is written now, with the next seq.
		let seq = 100
		let playhead = { at: Date.now(), ms: 100_000 }
		const position = (): number => playhead.ms + (Date.now() - playhead.at)
		const say = (): void => {
			const s = copy(fixture('state.json')) as StateJson
			s.seq = ++seq
			s.at = Date.now()
			const show = s.playlists.find((p) => p.id === SHOW.playlist)
			assert.ok(show)
			const ms = position()
			Object.assign(show, { state: 'playing', positionMs: ms, lengthMs: 300_000, remainingMs: 300_000 - ms, rate: 1 })
			ch.say('STATE ' + JSON.stringify(s))
		}
		ch.events.open({})
		await settle()
		ch.ok('HELLO')
		await settle()
		serve()
		say()
		await wait(100)
		assert.equal(m.instance.linked(), true)
		const seeks = (): string[] =>
			ch.sent.map((l) => l.replace(/^@c\d+ /u, '')).filter((l) => l.startsWith('VIDEO SEEK '))
		const seconds = (line: string | undefined): number => Number(line?.split(' ')[2])
		const turnRight = async (): Promise<void> => {
			await m.press('enc_seek', { playlist: SHOW.playlist, delta: '1', step: '1' }, 'dial-seek')
			await wait(40)
		}

		// A turn right seeks a second past the playhead; the node plays on from there.
		const before = position()
		await turnRight()
		assert.equal(seeks().length, 1)
		const target = seconds(seeks()[0])
		assert.ok(Math.abs(target * 1000 - (before + 1000)) <= 100, `${seeks()[0]}: a second past ${before} ms`)
		playhead = { at: Date.now(), ms: target * 1000 }
		const key = `seek:${SHOW.playlist}:${SHOW.red}`
		assert.equal(m.instance.encoders.shadow(key), target, "the target is the playlist's and its item's, and steers")

		// The node's next STATE, written 200 ms after the seek, shows the item playing on from it: the target steers no more.
		await wait(200)
		say()
		await wait(50)
		assert.equal(m.instance.encoders.shadow(key), null, 'the first STATE written after the seek drops the target')

		// 1.5 s after the first, a second turn right seeks a second past where the playhead is now, never behind it.
		await wait(1000)
		say()
		await wait(200)
		const now = position()
		await turnRight()
		assert.equal(seeks().length, 2)
		const second = seconds(seeks()[1])
		assert.ok(second * 1000 > now, `${seeks()[1]} is forward of the playhead at ${now} ms`)
		assert.ok(Math.abs(second * 1000 - (now + 1000)) <= 100, `${seeks()[1]}: a second past ${now} ms`)
	} finally {
		await m.stop()
	}
})
