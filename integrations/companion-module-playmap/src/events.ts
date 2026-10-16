/** One event the node raised: its sequence number, the instant it happened on the node's clock, its kind and its fields. */
export interface NodeEvent {
	readonly seq: number
	/** Unix ms on the node's clock. */
	readonly at: number
	readonly kind: string
	readonly fields: Readonly<Record<string, unknown>>
}

const RECENT = 64
/** The node's event ring: it keeps the last 256 events, so a replay never answers more than this. */
export const EVENT_RING = 256
/** The most of an event's text kept (its kind, each text field), in UTF-16 units. */
export const MAX_EVENT_TEXT = 1024
/** The most fields an event keeps, and the longest field name. */
const MAX_EVENT_FIELDS = 32
const MAX_FIELD_NAME = 64

/** At most MAX_EVENT_TEXT of a text, copied when cut, so what is kept holds no reference to the longer text it came from. */
function capped(text: string): string {
	return text.length <= MAX_EVENT_TEXT
		? text
		: Buffer.from(text.slice(0, MAX_EVENT_TEXT), 'utf16le').toString('utf16le')
}

/** The words an event reads as on a key: its own words when it has them, else its kind and what it names. */
export function eventWords(e: NodeEvent): string {
	if (typeof e.fields.words === 'string' && e.fields.words) return e.fields.words
	const names = Object.entries(e.fields)
		.filter(([k, v]) => typeof v === 'string' && k !== 'kind')
		.map(([, v]) => v as string)
	return [e.kind, ...names].join(' ')
}

/** Reads an EVENT push's or an EVENTS reply's event object. What it keeps is bounded whatever the push holds: its kind and each
 * text at most 1 KiB, plain values only (text, number, true/false, null — the node writes no other), at most 32 fields. */
export function readEvent(json: unknown): NodeEvent | null {
	if (!json || typeof json !== 'object' || Array.isArray(json)) return null
	const o = json as Record<string, unknown>
	if (typeof o.seq !== 'number' || typeof o.kind !== 'string') return null
	const fields: Record<string, unknown> = {}
	let kept = 0
	for (const name in o) {
		if (kept >= MAX_EVENT_FIELDS) break
		if (name === 'seq' || name === 'at' || name === 'kind' || name.length > MAX_FIELD_NAME) continue
		const v = o[name]
		if (typeof v === 'string') fields[name] = capped(v)
		else if (typeof v === 'number' || typeof v === 'boolean' || v === null) fields[name] = v
		else continue
		kept++
	}
	return { seq: o.seq, at: typeof o.at === 'number' ? o.at : 0, kind: capped(o.kind), fields }
}

/** The events heard, in order of their seq, and the pulses they make: an `event_pulse` feedback is true for `hold_ms` after an event
 * of its kind (and, when it names one, naming its ref) happened — timed by the node's clock, so a replayed event pulses only for
 * what is left of its window. */
export class EventLog {
	lastSeq = 0
	/** The node's events.seq when this deck first heard it (0 for a node seen to start again, and for a pairing that dropped before
	 * any STATE told it): a deck that took no event yet replays from it after a drop. Taken once, so a later STATE, whose seq counts
	 * the events raised while the link was down, never moves a replay past them. */
	baseline = 0
	#baselineTaken = false
	last: NodeEvent | null = null
	readonly #recent: NodeEvent[] = []

	/** The node's events.seq, from STATE: the baseline when none is taken yet. */
	heard(seq: number): void {
		if (this.#baselineTaken) return
		this.#baselineTaken = true
		this.baseline = seq
	}

	/** The node's newest seq is below the last one heard: the node started again and numbers its events afresh, so it is replayed
	 * from its first. The baseline is taken at 0, so no later STATE moves it, even when that replay is lost with the link. */
	restarted(): void {
		this.lastSeq = 0
		this.baseline = 0
		this.#baselineTaken = true
		this.last = null
		this.#recent.length = 0
	}

	/** Where a replay starts: past the last event taken, or past the node's seq when this deck first heard it. */
	from(): number {
		return Math.max(this.lastSeq, this.baseline)
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
