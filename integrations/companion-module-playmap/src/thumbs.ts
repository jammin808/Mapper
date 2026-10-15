import type { Reply } from './link/session.ts'
import { parseNode } from './json.ts'
import { Lines } from './lines.ts'

/** The most pictures kept (about 3 MB at 96 px). */
export const THUMB_CACHE = 256
/** Placed-picture changes are told to the node at most this often. */
export const WATCH_DEBOUNCE_MS = 250
/** The most one picture may weigh (base64 characters): a key's picture is 144 px at most, so a reply past this is refused. */
export const MAX_PICTURE_CHARS = 256 * 1024
/** The most every picture held may weigh together (base64 characters); the least recently shown go first past it. */
export const MAX_CACHE_CHARS = 16 * 1024 * 1024
const PICTURE_TYPES = new Set(['image/jpeg', 'image/png'])

interface Cached {
	readonly etag: string
	readonly uri: string
}

/** Pictures for keys: fetched over the paired link (`THUMB <ref> <w>x<h> [IF <etag>]`) only for keys that show them, kept by ref and
 * size (least recently used out past 256), and fetched again only when the node's `THUMBS` push says a watched picture's ETag moved.
 * A feedback asking for a picture not held yet gets "" at once and is checked again when it arrives. */
export class Thumbs {
	readonly #cache = new Map<string, Cached>()
	/** The base64 characters every held picture weighs together. */
	#chars = 0
	readonly #inflight = new Set<string>()
	readonly #placed = new Map<string, { ref: string; size: number }>()
	readonly #pushed = new Map<string, string | null>()
	/** Ref and size the node refused (no picture of it now): not asked again until a push says its picture moved, or a relink. */
	readonly #refused = new Set<string>()
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
		// A ref the node says has no picture now (null in THUMBS, or refused) is not asked until a push says it moved.
		if (linked && !hit && this.#pushed.get(r) !== null && !this.#refused.has(key)) this.#fetch(r, size, undefined)
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

	/** `THUMBS {ref: etag|null}`: a watched picture whose ETag moved since the last push is fetched again; on the first push after a
	 * (re)subscribe, a held picture whose ETag is not the pushed one is asked again `IF` its own (the node answers `same` when it is). */
	pushed(etags: Record<string, unknown>): void {
		const placed = new Set(this.placedRefs())
		for (const [ref, etag] of Object.entries(etags)) {
			// Only refs a key shows are kept: a push naming others cannot grow what the module holds.
			if (!placed.has(ref)) continue
			const e = typeof etag === 'string' ? etag : null
			const had = this.#pushed.has(ref)
			const before = this.#pushed.get(ref)
			this.#pushed.set(ref, e)
			if (had && before === e) continue
			for (const key of [...this.#refused]) if (key.startsWith(`${ref}|`)) this.#refused.delete(key)
			if (e === null) {
				// No picture of it now (a playlist that stopped, a source gone): the key shows none, never the last one.
				this.#drop(ref, null)
				continue
			}
			for (const p of this.#placed.values())
				if (p.ref === ref) {
					const cached = this.#cache.get(`${ref}|${p.size}`)
					if (!had && (!cached || cached.etag === e)) continue
					this.#fetch(ref, p.size, cached?.etag)
				}
		}
	}

	/** The link is paired again: the watched set is told again and every placed picture not held is fetched. */
	relinked(): void {
		this.#watched = ''
		this.#pushed.clear()
		this.#refused.clear()
		this.#scheduleWatch()
		for (const p of this.#placed.values())
			if (!this.#cache.has(`${p.ref}|${p.size}`)) this.#fetch(p.ref, p.size, undefined)
	}

	#fetch(ref: string, size: number, ifEtag: string | undefined): void {
		const key = `${ref}|${size}`
		if (this.#inflight.has(key)) return
		this.#inflight.add(key)
		const line = Lines.thumb(ref, size, ifEtag)
		this.#ask(line)
			.then((reply) => {
				if (!reply.ok) {
					// Refused (no picture of it now): what was held is not this ref's picture any more.
					this.#refused.add(key)
					this.#drop(ref, size)
					return
				}
				const o = parseNode(reply.payload) as { etag?: unknown; same?: unknown; mime?: unknown; data?: unknown }
				if (o.same === true) return
				if (typeof o.data !== 'string' || typeof o.mime !== 'string' || typeof o.etag !== 'string') return
				// A picture of another type, or heavier than a key's picture can be, is no picture.
				if (!PICTURE_TYPES.has(o.mime) || o.data.length > MAX_PICTURE_CHARS || !/^[A-Za-z0-9+/=]*$/u.test(o.data)) {
					this.#refused.add(key)
					this.#drop(ref, size)
					return
				}
				this.#evict(key)
				this.#cache.set(key, { etag: o.etag.slice(0, 256), uri: `data:${o.mime};base64,${o.data}` })
				this.#chars += o.data.length
				for (const oldest of this.#cache.keys()) {
					if (this.#cache.size <= THUMB_CACHE && this.#chars <= MAX_CACHE_CHARS) break
					this.#evict(oldest)
				}
				const ids = [...this.#placed.entries()].filter(([, p]) => p.ref === ref && p.size === size).map(([id]) => id)
				if (ids.length > 0) this.#recheck(ids)
			})
			.catch(() => undefined) // no picture (refused, or the link dropped): the key shows none, and the next push or relink asks again
			.finally(() => this.#inflight.delete(key))
	}

	/** Lets go of one held picture. */
	#evict(key: string): boolean {
		const held = this.#cache.get(key)
		if (!held) return false
		this.#chars -= held.uri.length - held.uri.indexOf(',') - 1
		return this.#cache.delete(key)
	}

	/** Lets go of a ref's pictures (one size, or every size) and rechecks the keys that showed one. */
	#drop(ref: string, size: number | null): void {
		let dropped = false
		for (const key of [...this.#cache.keys()])
			if (size === null ? key.startsWith(`${ref}|`) : key === `${ref}|${size}`) dropped = this.#evict(key) || dropped
		if (!dropped) return
		const ids = [...this.#placed.entries()]
			.filter(([, p]) => p.ref === ref && (size === null || p.size === size))
			.map(([id]) => id)
		if (ids.length > 0) this.#recheck(ids)
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
		this.#chars = 0
		this.#pushed.clear()
		this.#refused.clear()
		this.#watched = ''
	}
}
