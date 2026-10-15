// A press's receipt on its key (the `receipt` feedback on the control that pressed): pending (amber) from the moment its @c<n> line
// is sent; an accepted reply keeps it pending until a STATE push with a higher seq carries the effect; a plain OK clears it; an ERR
// makes it failed (red) with the node's words in last_refusal and on the key. The key's main light moves only with STATE, never with
// the press, and each receipt follows its own control whatever order the replies come in.
import test from 'node:test'
import assert from 'node:assert/strict'
import type { JsonValue } from '@companion-module/base'
import { FakeNode, copy, fixture } from './fake-node.ts'
import { boot, configFor, type Booted } from './harness.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

type Fields = Record<string, unknown>

function fields(v: JsonValue | undefined): Fields {
	assert.ok(v !== null && typeof v === 'object' && !Array.isArray(v), `a result object, not ${JSON.stringify(v)}`)
	return v as Fields
}

/** A module booted against a fake node that asks a token; the node's handlers are set before the module links. */
async function linked(handlers: Record<string, () => string | null> = {}): Promise<{ node: FakeNode; m: Booted }> {
	const node = await new FakeNode({ token: TOKEN }).start()
	for (const [verb, handler] of Object.entries(handlers)) node.handlers.set(verb, handler)
	try {
		const m = await boot(configFor(node.webPort, node.wirePort, { min_go_ms: 0 }), { token: TOKEN })
		return { node, m }
	} catch (e) {
		await node.stop()
		throw e
	}
}

async function ready(m: Booted): Promise<void> {
	await m.host.until(
		() => m.instance.linked() && m.host.variables.get('node_name') === 'Test node',
		'linked, with STATE',
	)
}

/** The receipt feedbacks a key wears: pending, failed and the refusal's words, placed on its control. */
async function wear(m: Booted, key: string): Promise<void> {
	await m.place(`${key}:pending`, 'receipt', { stage: 'pending' }, key)
	await m.place(`${key}:failed`, 'receipt', { stage: 'failed' }, key)
	await m.place(`${key}:words`, 'receipt_word', {}, key)
	await m.host.until(
		() =>
			m.host.feedbackValues.get(`${key}:pending`) === false &&
			m.host.feedbackValues.get(`${key}:failed`) === false &&
			m.host.feedbackValues.get(`${key}:words`) === '',
		`${key} quiet`,
	)
}

const receipt = (m: Booted, key: string, which: 'pending' | 'failed' | 'words'): unknown =>
	m.host.feedbackValues.get(`${key}:${which}`)

/** The node answers a held line by its @c<n> id (OK or ERR and what follows). */
function answer(node: FakeNode, line: string, reply: string): void {
	const session = [...node.sessions][0]
	assert.ok(session, 'a session')
	const id = line.split(' ')[0] ?? ''
	session.send(reply.replace(/^(OK|ERR)/u, (w) => `${w} ${id}`))
}

void test('a press is pending while sent and cleared by a plain OK; only STATE moves the key’s light', async () => {
	const { node, m } = await linked({ BLACKOUT: () => null })
	try {
		await ready(m)
		await wear(m, 'key-1')
		await m.place('black', 'blackout', {}, 'key-1')
		await m.host.until(() => m.host.feedbackValues.get('black') === false, 'not black')

		const press = m.press('v_blackout', { state: 'ON' }, 'key-1')
		const line = await node.heard(/^@c\d+ BLACKOUT ON$/u)
		await m.host.until(() => receipt(m, 'key-1', 'pending') === true, 'pending while sent')
		assert.equal(receipt(m, 'key-1', 'failed'), false)

		answer(node, line, 'OK')
		const result = fields(await press)
		assert.equal(result.ok, true)
		assert.equal(result.stage, 'applied')
		await m.host.until(() => receipt(m, 'key-1', 'pending') === false, 'cleared by OK')
		assert.equal(receipt(m, 'key-1', 'failed'), false)
		assert.equal(m.instance.receipts.stage('key-1'), null)
		await m.host.until(() => m.host.variables.get('last_receipt') === 'applied: BLACKOUT ON', 'last_receipt')
		assert.equal(
			m.host.feedbackValues.get('black'),
			false,
			'an answered press is not a fact: the light waits for STATE',
		)

		const state = copy(fixture('state.json')) as { seq: number; air: { blackout: boolean } }
		state.seq += 1
		state.air.blackout = true
		node.pushState(state)
		await m.host.until(() => m.host.feedbackValues.get('black') === true, 'lit by STATE')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('an accepted press stays pending until a STATE push with a higher seq carries its effect', async () => {
	const accepted = '{"stage":"accepted","effect":"frozen at the next frame"}'
	const { node, m } = await linked({ FREEZE: () => `OK ${accepted}` })
	try {
		await ready(m)
		await wear(m, 'key-2')
		const seq = (fixture('state.json') as { seq: number }).seq

		const result = fields(await m.press('v_freeze', { state: 'ON' }, 'key-2'))
		assert.deepEqual(result, {
			ok: true,
			stage: 'accepted',
			effect: 'frozen at the next frame',
			error: '',
			reply: accepted,
		})
		assert.equal(m.instance.receipts.stage('key-2'), 'pending', 'accepted is not applied')
		await m.host.until(
			() => m.host.variables.get('last_receipt') === 'accepted: FREEZE ON — frozen at the next frame',
			'last_receipt',
		)
		await m.host.until(() => receipt(m, 'key-2', 'pending') === true, 'amber while accepted')

		// A STATE at the same seq does not carry the effect yet.
		const same = copy(fixture('state.json')) as { seq: number; headline: string }
		same.headline = 'the same seq, a new headline'
		node.pushState(same)
		await m.host.until(() => m.host.variables.get('headline') === 'the same seq, a new headline', 'that STATE read')
		assert.equal(m.instance.receipts.stage('key-2'), 'pending')
		assert.equal(receipt(m, 'key-2', 'pending'), true)
		assert.equal(receipt(m, 'key-2', 'failed'), false)

		const next = copy(fixture('state.json')) as { seq: number; air: { frozen: boolean } }
		next.seq = seq + 1
		next.air.frozen = true
		node.pushState(next)
		await m.host.until(() => receipt(m, 'key-2', 'pending') === false, 'cleared by the STATE that shows it')
		assert.equal(receipt(m, 'key-2', 'failed'), false)
		assert.equal(m.instance.receipts.stage('key-2'), null)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a refused press is failed (red) with the node’s words in last_refusal and on the key', async () => {
	const words = 'the show is armed — FREEZE is the desk’s while armed'
	const { node, m } = await linked({ FREEZE: () => `ERR ${words}` })
	try {
		await ready(m)
		await wear(m, 'key-3')
		const result = fields(await m.press('v_freeze', { state: 'ON' }, 'key-3'))
		assert.deepEqual(result, { ok: false, stage: 'refused', effect: '', error: words, reply: words })
		await m.host.until(
			() =>
				receipt(m, 'key-3', 'failed') === true &&
				receipt(m, 'key-3', 'pending') === false &&
				receipt(m, 'key-3', 'words') === words,
			'red with the words',
		)
		assert.equal(m.instance.receipts.stage('key-3'), 'failed')
		await m.host.until(() => m.host.variables.get('last_refusal') === words, 'last_refusal')
		assert.equal(m.host.variables.get('last_receipt'), `refused: FREEZE ON — ${words}`)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('each receipt follows the control that pressed, whatever order the replies come in', async () => {
	const words = 'no output is enabled'
	const { node, m } = await linked({ BLACKOUT: () => null, FREEZE: () => null })
	try {
		await ready(m)
		await wear(m, 'key-a')
		await wear(m, 'key-b')
		const a = m.press('v_blackout', { state: 'ON' }, 'key-a')
		const b = m.press('v_freeze', { state: 'ON' }, 'key-b')
		const lineA = await node.heard(/^@c\d+ BLACKOUT ON$/u)
		const lineB = await node.heard(/^@c\d+ FREEZE ON$/u)
		assert.notEqual(lineA.split(' ')[0], lineB.split(' ')[0], 'each press has its own @c<n>')
		await m.host.until(
			() => receipt(m, 'key-a', 'pending') === true && receipt(m, 'key-b', 'pending') === true,
			'both pending',
		)

		answer(node, lineB, `ERR ${words}`)
		answer(node, lineA, 'OK')
		assert.equal(fields(await a).stage, 'applied')
		assert.deepEqual(fields(await b), { ok: false, stage: 'refused', effect: '', error: words, reply: words })
		await m.host.until(
			() =>
				receipt(m, 'key-a', 'pending') === false &&
				receipt(m, 'key-a', 'failed') === false &&
				receipt(m, 'key-b', 'pending') === false &&
				receipt(m, 'key-b', 'failed') === true &&
				receipt(m, 'key-b', 'words') === words,
			'a cleared, b red',
		)
		assert.equal(receipt(m, 'key-a', 'words'), '')
		assert.equal(m.host.variables.get('last_refusal'), words)
	} finally {
		await m.stop()
		await node.stop()
	}
})
