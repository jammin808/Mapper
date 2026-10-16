/** The line grammar the node speaks (WireGrammar): one line per command, an optional "@id " prefix, words split on spaces with
 * "double quotes" keeping a phrase whole and a backslash escaping inside them, JSON to the end of the line. */

/** White space as the node reads it between words (char.IsWhiteSpace: the Unicode White_Space marks of the BMP). */
export function isWhiteSpace(c: string): boolean {
	return /^[\t\n\v\f\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]$/u.test(c)
}

/** A word as a line needs it: bare when it has no white space or quote and does not open with '{' or '[' (the node reads such a
 * word as JSON running to the end of the line), else quoted with backslash escapes. */
export function quote(word: string): string {
	if (word.length > 0 && ![...word].some((c) => isWhiteSpace(c) || c === '"') && !/^[{[]/u.test(word)) return word
	return '"' + word.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"'
}

/** A line's words as the node splits them (WireGrammar.TrySplit): white space between words; a word that opens with '"' runs to the
 * closing quote, a backslash taking the mark after it as it stands; a word that opens with '{' or '[' is JSON to the end of the line.
 * `ok` is false when a quote does not close, a line the node refuses; `words` then ends with the open phrase as far as it went. */
export function splitLine(text: string): { ok: boolean; words: string[] } {
	const words: string[] = []
	let i = 0
	while (i < text.length) {
		while (i < text.length && isWhiteSpace(text.charAt(i))) i++
		if (i >= text.length) break
		let word = ''
		if (text.charAt(i) === '"') {
			i++
			let closed = false
			while (i < text.length) {
				const c = text.charAt(i)
				if (c === '\\' && i + 1 < text.length) {
					word += text.charAt(i + 1)
					i += 2
					continue
				}
				if (c === '"') {
					closed = true
					i++
					break
				}
				word += c
				i++
			}
			if (!closed) return { ok: false, words: [...words, word] }
		} else if (text.charAt(i) === '{' || text.charAt(i) === '[') {
			word = text.slice(i)
			i = text.length
		} else {
			while (i < text.length && !isWhiteSpace(text.charAt(i))) word += text.charAt(i++)
		}
		words.push(word)
	}
	return { ok: true, words }
}

/** One line the node said: a reply ("OK @c7 …", "ERR @c7 …") or a push ("STATE {…}", "NONCE …", "EVENT {…}"). */
export interface NodeLine {
	readonly word: string
	readonly id: string | null
	readonly rest: string
}

/** Reads the node's line: its first word, the request id a reply carries after it, and the rest. */
export function readLine(text: string): NodeLine {
	const t = text.replace(/[\r\n]+$/u, '').trim()
	const space = t.indexOf(' ')
	const word = space < 0 ? t : t.slice(0, space)
	let rest = space < 0 ? '' : t.slice(space + 1)
	let id: string | null = null
	if ((word === 'OK' || word === 'ERR') && rest.startsWith('@')) {
		const s = rest.indexOf(' ')
		id = s < 0 ? rest.slice(1) : rest.slice(1, s)
		rest = s < 0 ? '' : rest.slice(s + 1)
	}
	return { word, id, rest }
}
