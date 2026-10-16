/** The line grammar the node speaks (WireGrammar): one line per command, an optional "@id " prefix, words split on spaces with
 * "double quotes" keeping a phrase whole and a backslash escaping inside them, JSON to the end of the line. */

/** A word as a line needs it: bare when it has no space or quote and does not open with '{' or '[' (the node reads such a word as
 * JSON running to the end of the line), else quoted with backslash escapes. */
export function quote(word: string): string {
	if (word.length > 0 && !/[\s"]/u.test(word) && !/^[{[]/u.test(word)) return word
	return '"' + word.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"'
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
