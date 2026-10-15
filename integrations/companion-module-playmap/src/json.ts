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
