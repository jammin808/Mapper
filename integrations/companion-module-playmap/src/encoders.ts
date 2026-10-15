/** What one encoder drives: an absolute value read from STATE (sent as a target, clamped to its range) or a relative step (sent as
 * the step itself). */
export interface EncoderTarget {
	/** The value STATE shows now, or null when the node does not show it (an absolute turn then does nothing). */
	readonly value: () => number | null
	readonly min: number
	readonly max: number
	readonly step: number
	readonly absolute: boolean
	/** The line for an absolute target or a relative step. */
	readonly write: (n: number) => string
}

/** Turns within 30 ms of each other are one send. */
export const COALESCE_MS = 30
/** A target the node never confirmed stops steering the next one after this long. */
export const SHADOW_MS = 2_000

/** Encoders with `$(this:delta)`: turns coalesce for 30 ms, then an absolute target is the shadow value (the last STATE value plus
 * the steps already sent and not yet confirmed) plus the turned steps, clamped to the range; a relative one sends the steps. The
 * shadow only computes the next target — it is never shown, and a key shows STATE alone. */
export class Encoders {
	readonly #pending = new Map<
		string,
		{ delta: number; target: EncoderTarget; controlId: string; timer: ReturnType<typeof setTimeout> }
	>()
	readonly #shadow = new Map<string, { value: number; at: number }>()
	readonly #targets = new Map<string, EncoderTarget>()
	readonly #send: (line: string, controlId: string) => void
	readonly #now: () => number

	constructor(send: (line: string, controlId: string) => void, now: () => number = (): number => Date.now()) {
		this.#send = send
		this.#now = now
	}

	/** A turn of `delta` detents (positive right) on the encoder keyed `key`, from the control that turned. */
	turn(key: string, delta: number, target: EncoderTarget, controlId: string): void {
		if (!Number.isFinite(delta) || delta === 0) return
		this.#targets.set(key, target)
		const pending = this.#pending.get(key)
		if (pending) {
			pending.delta += delta
			pending.target = target
			pending.controlId = controlId
			return
		}
		const entry = {
			delta,
			target,
			controlId,
			timer: setTimeout(() => {
				this.#pending.delete(key)
				this.#flush(key, entry.delta, entry.target, entry.controlId)
			}, COALESCE_MS),
		}
		this.#pending.set(key, entry)
	}

	/** The shadow value an encoder would turn from now (for tests and learn). */
	shadow(key: string): number | null {
		const s = this.#shadow.get(key)
		return s && this.#now() - s.at < SHADOW_MS ? s.value : null
	}

	/** STATE moved: a shadow STATE now shows is confirmed and dropped. */
	stateMoved(): void {
		for (const [key, s] of this.#shadow) {
			const v = this.#targets.get(key)?.value() ?? null
			if (v !== null && Math.abs(v - s.value) < 1e-6) this.#shadow.delete(key)
		}
	}

	clear(): void {
		for (const p of this.#pending.values()) clearTimeout(p.timer)
		this.#pending.clear()
		this.#shadow.clear()
		this.#targets.clear()
	}

	#flush(key: string, delta: number, t: EncoderTarget, controlId: string): void {
		if (!t.absolute) {
			this.#send(t.write(round(delta * t.step)), controlId)
			return
		}
		const base = this.shadow(key) ?? t.value()
		if (base === null) return
		const next = round(Math.min(t.max, Math.max(t.min, base + delta * t.step)))
		if (next === base) return
		this.#shadow.set(key, { value: next, at: this.#now() })
		this.#send(t.write(next), controlId)
	}
}

/** A number without binary noise (0.1 + 0.2 → 0.3), to six places. */
function round(n: number): number {
	return Math.round(n * 1e6) / 1e6
}
