/** One event the node raised: its sequence number, the instant it happened on the node's clock, its kind and its fields. */
export interface NodeEvent {
	readonly seq: number
	/** Unix ms on the node's clock. */
	readonly at: number
	readonly kind: string
	readonly fields: Readonly<Record<string, unknown>>
}

const RECENT = 64

/** The words an event reads as on a key: its own words when it has them, else its kind and what it names. */
export function eventWords(e: NodeEvent): string {
	if (typeof e.fields.words === 'string' && e.fields.words) return e.fields.words
	const names = Object.entries(e.fields)
		.filter(([k, v]) => typeof v === 'string' && k !== 'kind')
		.map(([, v]) => v as string)
	return [e.kind, ...names].join(' ')
}

/** Reads an EVENT push's or an EVENTS reply's event object. */
export function readEvent(json: unknown): NodeEvent | null {
	if (!json || typeof json !== 'object') return null
	const o = json as Record<string, unknown>
	if (typeof o.seq !== 'number' || typeof o.kind !== 'string') return null
	const { seq, at, kind, ...fields } = o
	return { seq, at: typeof at === 'number' ? at : 0, kind, fields }
}

/** The events heard, in order of their seq, and the pulses they make: an `event_pulse` feedback is true for `hold_ms` after an event
 * of its kind (and, when it names one, naming its ref) happened — timed by the node's clock, so a replayed event pulses only for
 * what is left of its window. */
export class EventLog {
	lastSeq = 0
	last: NodeEvent | null = null
	readonly #recent: NodeEvent[] = []

	/** The node's newest seq is below the last one heard: the node started again and numbers its events afresh. */
	restarted(): void {
		this.lastSeq = 0
		this.last = null
		this.#recent.length = 0
	}

	/** Takes an event; false for one already heard (a replay overlapping a push). */
	take(e: NodeEvent): boolean {
		if (e.seq <= this.lastSeq) return false
		this.lastSeq = e.seq
		this.last = e
		this.#recent.push(e)
		if (this.#recent.length > RECENT) this.#recent.shift()
		return true
	}

	/** Whether an event of `kind` (and naming `ref`, when given) happened within `holdMs` before nodeNow, and when that ends. */
	pulse(kind: string, ref: string, holdMs: number, nodeNow: number): { on: boolean; until: number } {
		for (let i = this.#recent.length - 1; i >= 0; i--) {
			const e = this.#recent[i]
			if (!e) continue
			if (nodeNow - e.at >= holdMs || e.at > nodeNow + holdMs) continue
			if (kind && e.kind !== kind && !(kind.endsWith('*') && e.kind.startsWith(kind.slice(0, -1)))) continue
			if (ref && !Object.values(e.fields).some((v) => v === ref)) continue
			return { on: true, until: e.at + holdMs }
		}
		return { on: false, until: 0 }
	}

	recent(): readonly NodeEvent[] {
		return this.#recent
	}
}
