// The test node's own wait: a line the module never sends fails the wait once the deadline passes (PLAYMAP_TEST_WAIT_MS, 20 s, as
// RecordingHost.until's), naming the pattern and the lines heard so far, so a line the module stops sending fails the run and never
// hangs it. On fake timers (the one such test in this file).
import test from 'node:test'
import assert from 'node:assert/strict'
import { FakeNode } from './fake-node.ts'
import { settle } from './fake-channel.ts'

void test('a line never heard fails the wait at its deadline, naming the pattern and the lines heard so far', async (t) => {
	t.mock.timers.enable({ apis: ['setTimeout'] })
	const node = new FakeNode()
	node.lines.push('@c1 HELLO Companion:FOH deck module=1.0.0')
	const outcome: { error: unknown } = { error: null }
	const error = (): unknown => outcome.error
	const wait = node.heard(/^@c\d+ NEVER SENT$/u).then(
		() => null,
		(e: unknown) => {
			outcome.error = e
		},
	)
	const deadline = Number(process.env.PLAYMAP_TEST_WAIT_MS ?? 20000)
	t.mock.timers.tick(deadline - 1)
	await settle()
	assert.equal(error(), null, 'not before the deadline')
	t.mock.timers.tick(1)
	await settle()
	const failed = error()
	assert.ok(failed instanceof Error, 'the wait failed at its deadline')
	assert.match(failed.message, /NEVER SENT/u, 'it names the pattern')
	assert.match(failed.message, /@c1 HELLO Companion:FOH deck module=1\.0\.0/u, 'and the lines heard so far')
	await wait
})
