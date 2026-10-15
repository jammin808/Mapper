// A line's press shape follows the node's rule with its per-argument risk (VERBS' riskWhen): a word that raises the line to high
// risk holds it (DECODE POLICY … FORCE), a word that lowers a held high-risk row sends it at once (SEND ENABLE … ON), and a row
// whose words name no risk keeps the row's press. Panic, air and transport rows are never held, whatever the line says.
import test from 'node:test'
import assert from 'node:assert/strict'
import { BUNDLED_VERBS, pressOfLine, type VerbRow } from '../src/catalog/verbs.ts'

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

void test('panic, air and transport rows are never held, and a double press stays double', () => {
	for (const r of BUNDLED_VERBS.filter((v) => ['panic', 'air', 'transport'].includes(v.show)))
		assert.equal(pressOfLine(r, `${r.name} FORCE`), r.press, r.name)
	for (const r of BUNDLED_VERBS.filter((v) => v.press === 'double'))
		assert.equal(pressOfLine(r, `${r.name} x OFF`), 'double', r.name)
})
