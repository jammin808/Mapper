// A line's press shape follows the node's rule with its per-argument risk (VERBS' riskWhen): a word that raises the line to high
// risk holds it (DECODE POLICY … FORCE), a word that lowers a held high-risk row sends it at once (SEND ENABLE … ON), and a row
// whose words name no risk keeps the row's press. Only the design's named rows (GO, NEXT, PREV, PLAY, PAUSE, BLACKOUT, FREEZE,
// PANIC) are never held, whatever the line says; every other row of the show's air, panic and transport follows its risk.
import test from 'node:test'
import assert from 'node:assert/strict'
import { BUNDLED_VERBS, NEVER_HELD, matchRow, pressOfLine, type VerbRow } from '../src/catalog/verbs.ts'

const row = (name: string): VerbRow => {
	const r = BUNDLED_VERBS.find((v) => v.name === name)
	assert.ok(r, name)
	return r
}

void test('a word of the line that names a risk shapes its press as the node would', () => {
	assert.equal(row('DECODE POLICY').press, 'instant')
	assert.equal(pressOfLine(row('DECODE POLICY'), 'DECODE POLICY AUTO'), 'instant')
	assert.equal(
		pressOfLine(row('DECODE POLICY'), 'DECODE POLICY DIRECT FORCE'),
		'hold',
		'FORCE anywhere raises it to high',
	)
	assert.equal(pressOfLine(row('DECODE POLICY'), 'decode policy direct force'), 'hold', 'words compared upper-cased')

	assert.equal(row('SEND ENABLE').press, 'hold')
	assert.equal(pressOfLine(row('SEND ENABLE'), 'SEND ENABLE sd0a1b2c3d OFF'), 'hold')
	assert.equal(
		pressOfLine(row('SEND ENABLE'), 'SEND ENABLE sd0a1b2c3d ON'),
		'instant',
		'ON at argument 1 lowers it to review',
	)
	assert.equal(
		pressOfLine(row('SEND ENABLE'), 'SEND ENABLE ON sd0a1b2c3d'),
		'hold',
		'the word counts only at its argument',
	)

	assert.equal(pressOfLine(row('MEDIA OPTIMISE'), 'MEDIA OPTIMISE CANCEL ALL'), 'instant')
	assert.equal(pressOfLine(row('OUTPUT IDENTIFY'), 'OUTPUT IDENTIFY op_1'), 'hold', 'no risk word: the row’s press')
})

void test('only the named rows are never held, by row name; a double press stays double', () => {
	assert.deepEqual([...NEVER_HELD].sort(), [
		'BLACKOUT',
		'FREEZE',
		'PANIC',
		'PLAYLIST GO',
		'PLAYLIST NEXT',
		'PLAYLIST PREV',
		'VIDEO PAUSE',
		'VIDEO PLAY',
	])
	for (const name of NEVER_HELD) assert.equal(row(name).press, 'instant', `${name} is instant in the catalogue`)
	// A word that names a risk moves every row's press but a never-held row's and a double one's: PANIC CLEAR, MASTER, a stop, a
	// seek and a cue are raised and lowered as any other row is.
	for (const r of BUNDLED_VERBS) {
		const raised = { ...r, riskWhen: [{ arg: -1, word: 'RAISE', risk: 'high' as const }] }
		const lowered = { ...r, riskWhen: [{ arg: -1, word: 'LOWER', risk: 'low' as const }] }
		const keeps = r.press === 'double' || NEVER_HELD.has(r.name)
		assert.equal(pressOfLine(raised, `${r.name} RAISE`), keeps ? r.press : 'hold', `${r.name} raised`)
		assert.equal(
			pressOfLine(lowered, `${r.name} LOWER`),
			keeps ? r.press : r.risk === 'high' ? 'instant' : r.press,
			`${r.name} lowered`,
		)
	}
	assert.equal(
		pressOfLine({ ...row('PANIC CLEAR'), riskWhen: [{ arg: -1, word: 'NOW', risk: 'low' }] }, 'PANIC CLEAR NOW'),
		'instant',
	)
	assert.equal(
		pressOfLine({ ...row('PLAYLIST CUE'), riskWhen: [{ arg: -1, word: 'X', risk: 'high' }] }, 'PLAYLIST CUE X'),
		'hold',
	)
	// An alias is its row: VIDEO RESUME is VIDEO PLAY, never held; PANIC OFF is PANIC CLEAR, held.
	const resume = matchRow(BUNDLED_VERBS, ['VIDEO', 'RESUME', 'pl0a1b2c3d'])?.row
	assert.ok(resume)
	assert.equal(resume.name, 'VIDEO PLAY')
	assert.equal(
		pressOfLine({ ...resume, riskWhen: [{ arg: -1, word: 'X', risk: 'high' }] }, 'VIDEO RESUME pl0a1b2c3d X'),
		'instant',
	)
	const off = matchRow(BUNDLED_VERBS, ['PANIC', 'OFF'])?.row
	assert.ok(off)
	assert.equal(off.name, 'PANIC CLEAR')
	assert.equal(pressOfLine(off, 'PANIC OFF'), 'hold')
	for (const r of BUNDLED_VERBS.filter((v) => v.press === 'double'))
		assert.equal(pressOfLine(r, `${r.name} x OFF`), 'double', r.name)
})
