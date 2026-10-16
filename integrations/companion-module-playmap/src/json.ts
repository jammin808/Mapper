/** The deepest a node's JSON may nest. STATE, INDEX and every reply stay far inside it; past it a document is refused, so no later
 * walk or stringify of it can run out of stack in Companion's process. */
export const MAX_DEPTH = 64

/** JSON from the node: parsed, then refused (thrown) when it nests past MAX_DEPTH. */
export function parseNode(text: string): unknown {
	const value: unknown = JSON.parse(text)
	if (!within(value, MAX_DEPTH)) throw new Error(`JSON nested past ${MAX_DEPTH} levels`)
	return value
}

/** Whether a parsed value nests no deeper than `max` (walked without recursion). */
export function within(value: unknown, max: number): boolean {
	const stack: { v: unknown; d: number }[] = [{ v: value, d: 0 }]
	while (stack.length > 0) {
		const top = stack.pop()
		if (!top || top.v === null || typeof top.v !== 'object') continue
		if (top.d >= max) return false
		for (const child of Array.isArray(top.v) ? top.v : Object.values(top.v as Record<string, unknown>))
			if (child !== null && typeof child === 'object') stack.push({ v: child, d: top.d + 1 })
	}
	return true
}

/** JSON's one-mark escapes, by the mark after the backslash. */
const ESCAPES: ReadonlyMap<string, string> = new Map([
	['"', '"'],
	['\\', '\\'],
	['/', '/'],
	['b', '\b'],
	['f', '\f'],
	['n', '\n'],
	['r', '\r'],
	['t', '\t'],
])

/** A JSON string's body with its escapes decoded (`L`, `\n`, `\"` …); the body as it stands when an escape is not JSON's. */
function decoded(body: string): string {
	if (!body.includes('\\')) return body
	let out = ''
	for (let i = 0; i < body.length; i++) {
		const c = body.charAt(i)
		if (c !== '\\') {
			out += c
			continue
		}
		const mark = body.charAt(i + 1)
		const simple = ESCAPES.get(mark)
		const hex = body.slice(i + 2, i + 6)
		if (simple !== undefined) {
			out += simple
			i++
		} else if (mark === 'u' && /^[0-9a-fA-F]{4}$/u.test(hex)) {
			out += String.fromCharCode(Number.parseInt(hex, 16))
			i += 5
		} else return body
	}
	return out
}

/** What ends a `//` comment: any line end (the node's reader ends one at a line feed or carriage return; ending it sooner only reads
 * more). */
const LINE_END = /[\n\r\u2028\u2029]/u

/** Every string literal of a text, read the way the node's JSON reader reads one (System.Text.Json, comments skipped): outside a
 * string, a `//` comment runs to the end of its line and a block comment to its close or to the end of the text; each `"` opens a
 * string that runs to the next unescaped `"`, or to the end of a text cut short, its escapes decoded. Keys and values alike, each as
 * often as it is given, whether or not the text is JSON a strict reader takes — so no string the node's reader could take from it
 * goes unseen. */
export function stringLiterals(text: string): string[] {
	const out: string[] = []
	let i = 0
	while (i < text.length) {
		const c = text.charAt(i)
		const next = text.charAt(i + 1)
		if (c === '/' && next === '/') {
			const end = text.slice(i + 2).search(LINE_END)
			i = end < 0 ? text.length : i + 2 + end + 1
		} else if (c === '/' && next === '*') {
			const end = text.indexOf('*/', i + 2)
			i = end < 0 ? text.length : end + 2
		} else if (c === '"') {
			let j = i + 1
			while (j < text.length && text.charAt(j) !== '"') j += text.charAt(j) === '\\' ? 2 : 1
			out.push(decoded(text.slice(i + 1, Math.min(j, text.length))))
			i = j + 1
		} else i++
	}
	return out
}
