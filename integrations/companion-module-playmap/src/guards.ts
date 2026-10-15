import type { JsonValue } from '@companion-module/base'
import type { Reply } from './link/session.ts'

/** A press's receipt on its key: pending (sent, or accepted and its effect not yet in STATE), failed (refused, red for 2 s with the
 * node's words), or clear. The key's main light never moves on a press: only STATE moves it (attempts are not facts). */
export type ReceiptStage = 'pending' | 'failed'

interface Receipt {
	stage: ReceiptStage
	words: string
	until: number
	/** For an accepted line: the STATE seq it was accepted under; a STATE past it shows the effect. */
	seq: number | null
}

/** What a press's action answers (an action result for `storeResult` and `internal:logicIf`): resolved on the node's first reply,
 * never on STATE. */
export interface PressResult {
	readonly ok: boolean
	readonly stage: 'accepted' | 'applied' | 'refused' | 'not-sent'
	readonly effect: string
	readonly error: string
	readonly reply: string
	[key: string]: JsonValue
}

export const PENDING_MS = 10_000
export const FAILED_MS = 2_000

/** Every key's receipt, by the control that pressed. */
export class Receipts {
	readonly #by = new Map<string, Receipt>()
	readonly #changed: () => void
	readonly #now: () => number
	#timer: ReturnType<typeof setTimeout> | null = null
	lastRefusal = ''
	lastReceipt = ''

	constructor(changed: () => void, now: () => number = (): number => Date.now()) {
		this.#changed = changed
		this.#now = now
	}

	sent(controlId: string): void {
		this.#by.set(controlId, { stage: 'pending', words: '', until: this.#now() + PENDING_MS, seq: null })
		this.#schedule()
		this.#changed()
	}

	/** The node's one reply to a press, with the STATE seq heard when it came. */
	replied(controlId: string, line: string, reply: Reply, stateSeq: number): PressResult {
		const verb = line.split(' ').slice(0, 2).join(' ')
		let result: PressResult
		if (!reply.ok) {
			this.#by.set(controlId, { stage: 'failed', words: reply.payload, until: this.#now() + FAILED_MS, seq: null })
			this.lastRefusal = reply.payload
			this.lastReceipt = `refused: ${verb} — ${reply.payload}`
			result = { ok: false, stage: 'refused', effect: '', error: reply.payload, reply: reply.payload }
		} else if (reply.accepted) {
			this.#by.set(controlId, { stage: 'pending', words: '', until: this.#now() + PENDING_MS, seq: stateSeq })
			const effect = acceptedEffect(reply.payload)
			this.lastReceipt = `accepted: ${verb}${effect ? ' — ' + effect : ''}`
			result = { ok: true, stage: 'accepted', effect, error: '', reply: reply.payload }
		} else {
			this.#by.delete(controlId)
			this.lastReceipt = `applied: ${verb}`
			result = { ok: true, stage: 'applied', effect: reply.payload, error: '', reply: reply.payload }
		}
		this.#schedule()
		this.#changed()
		return result
	}

	/** The press never reached the node (no link, no answer in time, refused by the module's own guard). */
	notSent(controlId: string, line: string, why: string): PressResult {
		this.#by.set(controlId, { stage: 'failed', words: why, until: this.#now() + FAILED_MS, seq: null })
		this.lastRefusal = why
		this.lastReceipt = `not sent: ${line.split(' ').slice(0, 2).join(' ')} — ${why}`
		this.#schedule()
		this.#changed()
		return { ok: false, stage: 'not-sent', effect: '', error: why, reply: '' }
	}

	/** A STATE push: every accepted line whose effect it now carries is cleared. */
	stateMoved(seq: number): void {
		let moved = false
		for (const [id, r] of this.#by)
			if (r.seq !== null && seq > r.seq) {
				this.#by.delete(id)
				moved = true
			}
		if (moved) this.#changed()
	}

	stage(controlId: string): ReceiptStage | null {
		const r = this.#by.get(controlId)
		if (!r) return null
		if (r.until <= this.#now()) {
			this.#by.delete(controlId)
			return null
		}
		return r.stage
	}

	words(controlId: string): string {
		return this.stage(controlId) === 'failed' ? (this.#by.get(controlId)?.words ?? '') : ''
	}

	clear(): void {
		this.#by.clear()
		if (this.#timer) clearTimeout(this.#timer)
		this.#timer = null
	}

	/** One timer, for the earliest receipt to expire; it tells the keys and arms the next. */
	#schedule(): void {
		if (this.#timer) clearTimeout(this.#timer)
		this.#timer = null
		let first = Number.POSITIVE_INFINITY
		for (const r of this.#by.values()) first = Math.min(first, r.until)
		if (!Number.isFinite(first)) return
		this.#timer = setTimeout(
			() => {
				this.#timer = null
				const now = this.#now()
				for (const [id, r] of this.#by) if (r.until <= now) this.#by.delete(id)
				this.#changed()
				this.#schedule()
			},
			Math.max(0, first - this.#now()),
		)
	}
}

function acceptedEffect(payload: string): string {
	try {
		const o = JSON.parse(payload) as { effect?: unknown }
		return typeof o.effect === 'string' ? o.effect : ''
	} catch {
		return ''
	}
}

export const CONFIRM_MS = 3_000

/** Two presses plus a hold: the first held press arms its control ("SURE?") for 3 s, and only a second held press within them sends.
 * Kept in the module because a preset's internal actions cannot reset a button's step. */
export class Confirms {
	readonly #armed = new Map<string, ReturnType<typeof setTimeout>>()
	readonly #changed: (controlId: string) => void

	constructor(changed: (controlId: string) => void) {
		this.#changed = changed
	}

	/** True when this press confirms (send now); false when it armed the control. */
	press(controlId: string): boolean {
		const timer = this.#armed.get(controlId)
		if (timer) {
			clearTimeout(timer)
			this.#armed.delete(controlId)
			this.#changed(controlId)
			return true
		}
		this.#armed.set(
			controlId,
			setTimeout(() => {
				this.#armed.delete(controlId)
				this.#changed(controlId)
			}, CONFIRM_MS),
		)
		this.#changed(controlId)
		return false
	}

	pending(controlId: string): boolean {
		return this.#armed.has(controlId)
	}

	clear(): void {
		for (const t of this.#armed.values()) clearTimeout(t)
		this.#armed.clear()
	}
}

/** The module's double-GO guard: a second GO of the same playlist within min_go_ms of the last is not sent (the node keeps its own
 * minimum too, which this never relaxes). */
export class GoGuard {
	readonly #last = new Map<string, number>()
	readonly #now: () => number
	#timer: ReturnType<typeof setTimeout> | null = null
	readonly #changed: () => void

	constructor(changed: () => void, now: () => number = (): number => Date.now()) {
		this.#changed = changed
		this.#now = now
	}

	/** True when the GO may go; records it. */
	allow(playlist: string, minGoMs: number): boolean {
		const now = this.#now()
		const last = this.#last.get(playlist)
		if (minGoMs > 0 && last !== undefined && now - last < minGoMs) return false
		this.#last.set(playlist, now)
		if (minGoMs > 0) {
			if (this.#timer) clearTimeout(this.#timer)
			this.#changed()
			this.#timer = setTimeout(() => {
				this.#timer = null
				this.#changed()
			}, minGoMs)
		}
		return true
	}

	cooling(playlist: string | null, minGoMs: number): boolean {
		const now = this.#now()
		if (playlist) {
			const last = this.#last.get(playlist)
			return last !== undefined && now - last < minGoMs
		}
		for (const last of this.#last.values()) if (now - last < minGoMs) return true
		return false
	}
}
