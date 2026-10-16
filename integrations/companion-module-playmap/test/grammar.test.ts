// The node's line grammar, as the module reads a line before it sends one: white space as the node knows it between words, a quoted
// phrase one word with its backslash escapes undone, a word that opens with '{' or '[' one JSON word to the end of the line, a quote
// that does not close a line the node refuses; and a word the module writes comes back as the same one word.
import test from 'node:test'
import assert from 'node:assert/strict'
import { quote, splitLine } from '../src/link/grammar.ts'

void test('a line splits into the words the node reads', () => {
	const cases: [string, string[]][] = [
		['PLAYLIST GO pl1', ['PLAYLIST', 'GO', 'pl1']],
		['  PLAYLIST\\tGO   pl1  '.replace('\\t', '\t'), ['PLAYLIST', 'GO', 'pl1']],
		['PLAYLIST NEW "Front wall"', ['PLAYLIST', 'NEW', 'Front wall']],
		['PLAYLIST NEW "Say \\"hi\\" \\\\ bye"', ['PLAYLIST', 'NEW', 'Say "hi" \\ bye']],
		['A "b"c', ['A', 'b', 'c']],
		['A ""', ['A', '']],
		['SET sf1 label {"label": "Front wall"} more', ['SET', 'sf1', 'label', '{"label": "Front wall"} more']],
		['DEVICE ADD [L] pjlink 10.0.0.20', ['DEVICE', 'ADD', '[L] pjlink 10.0.0.20']],
		['A\u0085B C　D E', ['A', 'B', 'C', 'D', 'E']],
		['A﻿B', ['A﻿B']],
		['', []],
	]
	for (const [line, words] of cases) assert.deepEqual(splitLine(line), { ok: true, words }, JSON.stringify(line))
	assert.deepEqual(splitLine('BIND LEARN "CREDENTIAL SET'), { ok: false, words: ['BIND', 'LEARN', 'CREDENTIAL SET'] })
	assert.deepEqual(splitLine('A "b\\'), { ok: false, words: ['A', 'b\\'] })
})

void test('a word the module quotes comes back as that one word, mid-line', () => {
	for (const word of ['[L]', '{W}', 'Front wall', 'Say "hi"', '', 'a\\b', 'x\u0085y', 'plain'])
		assert.deepEqual(splitLine(`DEVICE ADD ${quote(word)} pjlink 10.0.0.20`), {
			ok: true,
			words: ['DEVICE', 'ADD', word, 'pjlink', '10.0.0.20'],
		})
	assert.equal(quote('plain'), 'plain')
	assert.equal(quote('[L]'), '"[L]"')
})
