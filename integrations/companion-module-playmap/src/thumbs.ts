import { quote } from './link/grammar.ts'
import type { Reply } from './link/session.ts'

/** The most pictures kept (about 3 MB at 96 px). */
export const THUMB_CACHE = 256
/** Placed-picture changes are told to the node at most this often. */
export const WATCH_DEBOUNCE_MS = 250

interface Cached {
	readonly etag: string
	readonly uri: string
}

/** Pictures for keys: fetched over the paired link (`THUMB <ref> <w>x<h> [IF <etag>]`) only for keys that show them, kept by ref and
 * size (least recently used out past 256), and fetched again only when the node's `THUMBS` push says a watched picture's ETag moved.
 * A feedback asking for a picture not held yet gets "" at once and is checked again when it arrives. */
export class Thumbs {
	readonly #cache = new Map<string, Cached>()
	readonly #inflight = new Set<string>()
	readonly #placed = new Map<string, { ref: string; size: number }>()
	readonly #pushed = new Map<string, string | null>()
	readonly #ask: (line: string) => Promise<Reply>
	readonly #recheck: (feedbackIds: string[]) => void
	readonly #watch: (refs: string[]) => void
	#watchTimer: ReturnType<typeof setTimeout> | null = null
	#watched = ''

	constructor(
		ask: (line: string) => Promise<Reply>,
		recheck: (feedbackIds: string[]) => void,
		watch: (refs: string[]) => void,
	) {
		this.#ask = ask
		this.#recheck = recheck
		this.#watch = watch
	}

	/** The picture a feedback shows now: a data URI, or "" while it is fetched (or when there is none). */
	get(feedbackId: string, ref: string, size: number, linked: boolean): string {
		const r = ref.trim()
		const before = this.#placed.get(feedbackId)
		if (!r) {
			if (before) this.forget(feedbackId)
			return ''
		}
		if (!before || before.ref !== r || before.size !== size) {
			this.#placed.set(feedbackId, { ref: r, size })
			this.#scheduleWatch()
		}
		const key = `${r}|${size}`
		const hit = this.#cache.get(key)
		if (hit) {
			this.#cache.delete(key)
			this.#cache.set(key, hit)
		}
		if (linked && !hit) this.#fetch(r, size, undefined)
		return hit?.uri ?? ''
	}

	/** A feedback left its key: its picture is no longer watched. */
	forget(feedbackId: string): void {
		if (this.#placed.delete(feedbackId)) this.#scheduleWatch()
	}

	/** The refs placed keys show. */
	placedRefs(): string[] {
		return [...new Set([...this.#placed.values()].map((p) => p.ref))].sort()
	}

	/** `THUMBS {ref: etag|null}`: a watched picture whose ETag moved since the last push is fetched again. */
	pushed(etags: Record<string, unknown>): void {
		for (const [ref, etag] of Object.entries(etags)) {
			const e = typeof etag === 'string' ? etag : null
			const had = this.#pushed.has(ref)
			const before = this.#pushed.get(ref)
			this.#pushed.set(ref, e)
			if (!had || before === e) continue
			for (const p of this.#placed.values())
				if (p.ref === ref) {
					const cached = this.#cache.get(`${ref}|${p.size}`)
					this.#fetch(ref, p.size, cached?.etag)
				}
		}
	}

	/** The link is paired again: the watched set is told again and every placed picture not held is fetched. */
	relinked(): void {
		this.#watched = ''
		this.#pushed.clear()
		this.#scheduleWatch()
		for (const p of this.#placed.values())
			if (!this.#cache.has(`${p.ref}|${p.size}`)) this.#fetch(p.ref, p.size, undefined)
	}

	#fetch(ref: string, size: number, ifEtag: string | undefined): void {
		const key = `${ref}|${size}`
		if (this.#inflight.has(key)) return
		this.#inflight.add(key)
		const line = `THUMB ${quote(ref)} ${size}x${size}` + (ifEtag ? ` IF ${quote(ifEtag)}` : '')
		this.#ask(line)
			.then((reply) => {
				if (!reply.ok) return
				const o = JSON.parse(reply.payload) as { etag?: unknown; same?: unknown; mime?: unknown; data?: unknown }
				if (o.same === true) return
				if (typeof o.data !== 'string' || typeof o.mime !== 'string' || typeof o.etag !== 'string') return
				this.#cache.delete(key)
				this.#cache.set(key, { etag: o.etag, uri: `data:${o.mime};base64,${o.data}` })
				while (this.#cache.size > THUMB_CACHE) {
					const oldest = this.#cache.keys().next().value
					if (oldest === undefined) break
					this.#cache.delete(oldest)
				}
				const ids = [...this.#placed.entries()].filter(([, p]) => p.ref === ref && p.size === size).map(([id]) => id)
				if (ids.length > 0) this.#recheck(ids)
			})
			.catch(() => undefined) // no picture (refused, or the link dropped): the key shows none, and the next push or relink asks again
			.finally(() => this.#inflight.delete(key))
	}

	#scheduleWatch(): void {
		if (this.#watchTimer) return
		this.#watchTimer = setTimeout(() => {
			this.#watchTimer = null
			const refs = this.placedRefs()
			const text = refs.join(' ')
			if (text === this.#watched) return
			this.#watched = text
			this.#watch(refs)
		}, WATCH_DEBOUNCE_MS)
	}

	clear(): void {
		if (this.#watchTimer) clearTimeout(this.#watchTimer)
		this.#watchTimer = null
		this.#placed.clear()
		this.#cache.clear()
		this.#pushed.clear()
		this.#watched = ''
	}
}
