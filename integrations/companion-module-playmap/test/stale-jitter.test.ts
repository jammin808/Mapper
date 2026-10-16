// The backoff's jitter, on fake timers: each wait is its base (1, 2, 4, 8, then 15 s) times 0.8 to 1.2 by the random draw, and never
// past 15 s — the lowest draw waits 0.8 of each base, the highest just under 1.2 of it, the cap holding at 15 s.
import test from 'node:test'
import assert from 'node:assert/strict'
import { boot } from './harness.ts'
import { advance, fakeChannels, settle } from './fake-channel.ts'

void test('each retry waits its base times 0.8 to 1.2 by the draw, never past 15 s', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout', 'setInterval', 'Date'], now: 1_760_000_000_000 })
	const runs: number[][] = []
	for (const draw of [0, 0.999]) {
		const { factory, opened } = fakeChannels()
		const m = await boot({ host: '10.0.0.5', port: 9940 }, {}, { factory, random: () => draw })
		try {
			const waits: number[] = []
			for (let n = 0; n < 6; n++) {
				const c = opened.at(-1)
				assert.ok(c)
				c.close()
				await settle()
				let waited = 0
				while (opened.length === n + 1 && waited < 20_000) {
					await advance(t.mock.timers, 10, 10)
					waited += 10
				}
				waits.push(waited)
			}
			runs.push(waits)
		} finally {
			await m.stop()
		}
	}
	const bases = [1000, 2000, 4000, 8000, 15000, 15000]
	const [low, high] = runs
	assert.ok(low && high)
	// Measured in 10 ms steps: a wait reads as the first step at or past it.
	assert.deepEqual(
		low,
		bases.map((b) => Math.ceil((b * 0.8) / 10) * 10),
	)
	assert.deepEqual(
		high,
		bases.map((b) => Math.min(15000, Math.ceil(Math.round(b * (0.8 + 0.4 * 0.999)) / 10) * 10)),
	)
	assert.ok(
		high.every((w) => w <= 15000),
		'never past 15 s',
	)
})
