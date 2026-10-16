import type { JsonValue } from '@companion-module/base'
import type { Reply } from './link/session.ts'
import { parseNode } from './json.ts'

/** A press's receipt on its key: pending (sent, or accepted and its effect not yet in STATE), failed (refused, red for 2 s with the
 * node's words), or clear. The key's main light never moves on a press: only STATE moves it (attempts are not facts). */
export type ReceiptStage = 'pending' | 'failed'

interface Receipt {
	stage: ReceiptStage
	words: string
	until: number
	/** For an accepted line: the STATE seq it was accepted under, and what STATE said of the entities its line names then. */
	seq: number | null
	watch: Watch | null
}

/** What an accepted line is watched by: the ids its line names that STATE holds, and STATE's entries for them when accepted. Its
 * effect has shown when those entries move; a line naming none of STATE's entities has, when any later STATE comes. */
export interface Watch {
	readonly ids: readonly string[]
	readonly before: string
}

/** What a press's action answers (an action result for `storeResult` and `internal:logicIf`): resolved on the node's first reply,
 * never on STATE. `applied` and `accepted` are the node's OK, `refused` its ERR; `unanswered` a line that left the module and got no
 * answer (none in 5 s, or the link dropped after it was written), whose effect only STATE can say; `not-sent` a line that never left
 * the module (the fence, a guard, no link, too long). */
export interface PressResult {
	readonly ok: boolean
	readonly stage: 'accepted' | 'applied' | 'refused' | 'unanswered' | 'not-sent'
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
		this.#by.set(controlId, { stage: 'pending', words: '', until: this.#now() + PENDING_MS, seq: null, watch: null })
		this.#schedule()
		this.#changed()
	}

	/** The node's first reply to a key's line, with the STATE seq heard when it came and, for an accepted line, what watches it.
	 * `verb` names the line: its first words once the fence has passed it, else the row's name only — a receipt never repeats words
	 * that may hold a secret. */
	replied(controlId: string, verb: string, reply: Reply, stateSeq: number, watch: Watch | null = null): PressResult {
		const result = replyResult(reply)
		if (result.stage === 'refused') {
			this.#by.set(controlId, {
				stage: 'failed',
				words: reply.payload,
				until: this.#now() + FAILED_MS,
				seq: null,
				watch: null,
			})
			this.lastRefusal = reply.payload
			this.lastReceipt = `refused: ${verb} — ${reply.payload}`
		} else if (result.stage === 'accepted') {
			this.#by.set(controlId, { stage: 'pending', words: '', until: this.#now() + PENDING_MS, seq: stateSeq, watch })
			this.lastReceipt = `accepted: ${verb}${result.effect ? ' — ' + result.effect : ''}`
		} else {
			this.#by.delete(controlId)
			this.lastReceipt = `applied: ${verb}`
		}
		this.#schedule()
		this.#changed()
		return result
	}

	/** A key's line that left the module and got no answer (none in time, or the link dropped after it was written): the node may
	 * have run it, so it is red as a failure but is no refusal (last_refusal keeps the node's last), and its words say to look at
	 * STATE before it is sent again. `verb` names it as `replied` says. */
	unanswered(controlId: string, verb: string, why: string): PressResult {
		const result = unansweredResult(why)
		this.#by.set(controlId, {
			stage: 'failed',
			words: result.error,
			until: this.#now() + FAILED_MS,
			seq: null,
			watch: null,
		})
		this.lastReceipt = `unanswered: ${verb} — ${result.error}`
		this.#schedule()
		this.#changed()
		return result
	}

	/** A key's line that never left the module (the fence, the module's own guard, no link, a line the node would not read). `verb`
	 * names it as `replied` says. */
	notSent(controlId: string, verb: string, why: string): PressResult {
		this.#by.set(controlId, { stage: 'failed', words: why, until: this.#now() + FAILED_MS, seq: null, watch: null })
		this.lastRefusal = why
		this.lastReceipt = `not sent: ${verb} — ${why}`
		this.#schedule()
		this.#changed()
		return { ok: false, stage: 'not-sent', effect: '', error: why, reply: '' }
	}

	/** A STATE push: every accepted line whose effect it now shows is cleared — the entries of the entities it names moved (`look`
	 * gives STATE's entries for ids now), or, for a line naming none, STATE moved past the seq it was accepted under. Unrelated facts
	 * moving clear nothing; 10 s clears it whatever STATE says. */
	stateMoved(seq: number, look: (ids: readonly string[]) => string): void {
		let moved = false
		for (const [id, r] of this.#by) {
			if (r.seq === null) continue
			const shown = r.watch && r.watch.ids.length > 0 ? look(r.watch.ids) !== r.watch.before : seq > r.seq
			if (shown) {
				this.#by.delete(id)
				moved = true
			}
		}
		if (moved) this.#changed()
	}

	/** Whether any accepted line waits on STATE (so a push need only look when one does). */
	get watching(): boolean {
		for (const r of this.#by.values()) if (r.seq !== null) return true
		return false
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

/** The result the node's first reply makes of a press: refused on ERR, accepted on a receipt, else applied. */
export function replyResult(reply: Reply): PressResult {
	if (!reply.ok) return { ok: false, stage: 'refused', effect: '', error: reply.payload, reply: reply.payload }
	if (reply.accepted)
		return { ok: true, stage: 'accepted', effect: acceptedEffect(reply.payload), error: '', reply: reply.payload }
	return { ok: true, stage: 'applied', effect: reply.payload, error: '', reply: reply.payload }
}

/** The result of a line that left the module and got no answer: why none came, and that its effect is STATE's to say. */
export function unansweredResult(why: string): PressResult {
	return {
		ok: false,
		stage: 'unanswered',
		effect: '',
		error: `sent; ${why} — check the node's STATE for its effect`,
		reply: '',
	}
}

function acceptedEffect(payload: string): string {
	try {
		const o = parseNode(payload) as { effect?: unknown }
		return typeof o.effect === 'string' ? o.effect : ''
	} catch {
		return ''
	}
}

export const CONFIRM_MS = 3_000

/** Two presses plus a hold: the first held press arms its control for its line ("SURE?") for 3 s, and only a second held press of
 * that same line within them sends. A press of another line (the page turned, the slot moved) arms the control anew for it. Kept in
 * the module because a preset's internal actions cannot reset a button's step. */
export class Confirms {
	readonly #armed = new Map<string, { readonly line: string; readonly timer: ReturnType<typeof setTimeout> }>()
	readonly #changed: (controlId: string) => void

	constructor(changed: (controlId: string) => void) {
		this.#changed = changed
	}

	/** True when this press confirms the line its control armed (send now); false when it armed the control for this line. */
	press(controlId: string, line: string): boolean {
		const armed = this.#armed.get(controlId)
		if (armed) {
			clearTimeout(armed.timer)
			this.#armed.delete(controlId)
			if (armed.line === line) {
				this.#changed(controlId)
				return true
			}
		}
		this.#armed.set(controlId, {
			line,
			timer: setTimeout(() => {
				this.#armed.delete(controlId)
				this.#changed(controlId)
			}, CONFIRM_MS),
		})
		this.#changed(controlId)
		return false
	}

	pending(controlId: string): boolean {
		return this.#armed.has(controlId)
	}

	clear(): void {
		for (const a of this.#armed.values()) clearTimeout(a.timer)
		this.#armed.clear()
	}
}

/** The module's double-GO guard: a second GO of the same playlist within min_go_ms of the last is not sent (the node keeps its own
 * minimum too, which this never relaxes). Each playlist's window ends on its own timer, so its cooling light goes out when it may GO. */
export class GoGuard {
	readonly #last = new Map<string, number>()
	readonly #timers = new Map<string, ReturnType<typeof setTimeout>>()
	readonly #now: () => number
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
			const before = this.#timers.get(playlist)
			if (before) clearTimeout(before)
			this.#timers.set(
				playlist,
				setTimeout(() => {
					this.#timers.delete(playlist)
					this.#changed()
				}, minGoMs),
			)
			this.#changed()
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

	clear(): void {
		for (const t of this.#timers.values()) clearTimeout(t)
		this.#timers.clear()
		this.#last.clear()
	}
}
