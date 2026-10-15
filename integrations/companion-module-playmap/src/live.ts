import { quote } from './link/grammar.ts'
import type { NodeModel } from './state/model.ts'
import { P, bool, field, list, num, str } from './state/paths.ts'
import { projectorLight } from './state/variables.ts'

/** What one Live slot shows and sends. */
export interface Slot {
	/** The entity it stands for (what the armed freeze keeps in place). */
	readonly key: string
	readonly label: string
	readonly sub: string
	readonly light: string
	/** A picture ref for THUMB, or null. */
	readonly thumbRef: string | null
	/** The line its press sends, or null for a slot that only shows. */
	readonly line: string | null
	readonly needsHold: boolean
}

export const SLOTS = 32
/** A held slot's HOLD hint shows this long after a short press. */
export const HINT_MS = 1000

/** The Live page's sources, as the `$(page:pm_live)` page variable or the live_source action names them. */
export const RULES = ['focused', 'playlists', 'outputs', 'projectors', 'surfaces', 'peers', 'faults'] as const

export const REMOVED: Slot = {
	key: '',
	label: 'REMOVED',
	sub: '',
	light: 'grey',
	thumbRef: null,
	line: null,
	needsHold: false,
}

/** A press shape for a line, from the catalogue (instant needs no hold). */
export type PressOf = (line: string) => 'instant' | 'hold' | 'double'

/** Every slot a rule lays out, in order (before paging), from INDEX and STATE. */
export function layout(rule: string, model: NodeModel, nodeNow: number, pressOf: PressOf): Slot[] {
	const hold = (line: string): boolean => pressOf(line) !== 'instant'
	const slot = (s: Omit<Slot, 'needsHold'>): Slot => ({ ...s, needsHold: s.line !== null && hold(s.line) })
	const index = model.index
	if (rule === 'focused' || rule.startsWith('playlist:')) {
		const playlists = model.playlists(nodeNow)
		const id =
			rule === 'focused' ? (playlists.find((p) => p.focused) ?? playlists[0])?.id : rule.slice('playlist:'.length)
		const pl = index?.playlists.find((p) => p.id === id)
		const now = playlists.find((p) => p.id === id)
		if (!pl) return []
		return pl.items.map((item) => {
			const onAir = now?.itemId === item.id && now.state !== 'idle'
			const next = now?.nextId === item.id
			return slot({
				key: item.id,
				label: item.label,
				sub: onAir ? (now.state === 'playing' ? 'ON AIR' : now.state.toUpperCase()) : next ? 'NEXT' : '',
				light: onAir ? (now.state === 'cued' ? 'amber' : 'green') : next ? 'amber' : 'grey',
				thumbRef: item.source || null,
				line: `PLAYLIST GO ${pl.id} ${item.id}`,
			})
		})
	}
	if (rule === 'playlists')
		return model.playlists(nodeNow).map((p) =>
			slot({
				key: p.id,
				label: p.name,
				sub: p.itemLabel || p.state,
				light: p.state === 'playing' ? 'green' : p.state === 'paused' || p.state === 'cued' ? 'amber' : 'grey',
				thumbRef: `playlist:${p.id}`,
				line: `PLAYLIST GO ${p.id}`,
			}),
		)
	if (rule === 'outputs')
		return list(model.state, P.outputs).map((o) =>
			slot({
				key: str(field(o, P.opId)),
				label: str(field(o, P.opName)),
				sub: `${num(field(o, P.opFps))} fps`,
				light: bool(field(o, P.opEnabled)) ? str(field(o, P.opLight), 'grey') : 'grey',
				thumbRef: null,
				line: `OUTPUT IDENTIFY ${str(field(o, P.opId))}`,
			}),
		)
	if (rule === 'projectors')
		return list(model.state, P.projectors).map((p) =>
			slot({
				key: str(field(p, P.pjId)),
				label: str(field(p, P.pjName)),
				sub: bool(field(p, P.pjSolved)) ? `${num(field(p, P.pjResidualPx))} px` : 'not solved',
				light: projectorLight(p),
				thumbRef: null,
				line: null,
			}),
		)
	if (rule === 'surfaces')
		return (index?.surfaces ?? []).map((s) =>
			slot({ key: s.id, label: s.name, sub: s.tags.join(' '), light: 'grey', thumbRef: s.source || null, line: null }),
		)
	if (rule === 'peers')
		return list(model.state, P.peers).map((p) =>
			slot({
				key: str(field(p, P.peerId)),
				label: str(field(p, P.peerName)),
				sub: str(field(p, P.peerHeadline)),
				light: bool(field(p, P.peerLive)) ? str(field(p, P.peerLight), 'grey') : 'grey',
				thumbRef: null,
				line: null,
			}),
		)
	if (rule === 'faults')
		return list(model.state, P.faults).map((f) =>
			slot({
				key: str(field(f, P.faultPart)),
				label: str(field(f, P.faultPart)),
				sub: str(field(f, P.faultWords)),
				light: 'red',
				thumbRef: null,
				line: null,
			}),
		)
	if (rule.startsWith('section:')) {
		const section = index?.sections.find((s) => s.section === rule.slice('section:'.length))
		const key = section?.keys[0]
		return (section?.rows ?? []).map((r) =>
			slot({
				key: r.id,
				label: key ? fillHint(key.label, r) : r.label,
				sub: r.state,
				light: r.light || 'grey',
				thumbRef: null,
				line: key ? fillHint(key.line, r) : null,
			}),
		)
	}
	return []
}

/** A section key's template with the row's words in: "$(id)", "$(label)", "$(light)". An id holding a space stays one word. */
export function fillHint(template: string, row: { id: string; label: string; light: string }): string {
	return template
		.replaceAll('$(id)', template.trim() === '$(id)' ? row.id : quote(row.id))
		.replaceAll('$(label)', row.label)
		.replaceAll('$(light)', row.light)
}

/** The Live page: 32 slots a page, its source picked per page (`$(page:pm_live)`) or by the connection, laid out by the module from
 * INDEX and STATE. While the show is armed the slot → entity map of each source is frozen as it stood, so a key never moves under a
 * finger: labels, lights and pictures still update, and a slot whose entity is gone shows REMOVED in grey. `refresh` (or disarming)
 * lays it out again. */
export class LivePage {
	rule: string
	readonly #page = new Map<string, number>()
	readonly #frozen = new Map<string, string[]>()
	readonly #hints = new Map<string, ReturnType<typeof setTimeout>>()

	constructor(rule: string) {
		this.rule = rule || 'focused'
	}

	/** A short press on a slot that needs a hold: the slot says HOLD for 1 s, then `done` rechecks it. */
	hint(rule: string, n: number, done: () => void): void {
		const key = `${rule}#${n}`
		const before = this.#hints.get(key)
		if (before) clearTimeout(before)
		this.#hints.set(
			key,
			setTimeout(() => {
				this.#hints.delete(key)
				done()
			}, HINT_MS),
		)
	}

	hinted(rule: string, n: number): boolean {
		return this.#hints.has(`${rule}#${n}`)
	}

	/** The source a slot reads: its page's (the option evaluated from `$(page:pm_live)`) or the connection's. */
	source(rule: unknown): string {
		const r = typeof rule === 'string' ? rule.trim() : ''
		return r || this.rule
	}

	/** Slot n (1-based) of a source's current page. */
	slot(rule: string, n: number, model: NodeModel, nodeNow: number, pressOf: PressOf): Slot | null {
		const all = this.#arranged(rule, model, nodeNow, pressOf)
		const at = this.page(rule, all.length) * SLOTS + n - 1
		return n >= 1 && n <= SLOTS ? (all[at] ?? null) : null
	}

	/** The page index (0-based), kept within the pages there are. */
	page(rule: string, count: number): number {
		const pages = Math.max(1, Math.ceil(count / SLOTS))
		const p = Math.min(this.#page.get(rule) ?? 0, pages - 1)
		return Math.max(0, p)
	}

	pageText(rule: string, model: NodeModel, nodeNow: number, pressOf: PressOf): string {
		const count = this.#arranged(rule, model, nodeNow, pressOf).length
		return `${this.page(rule, count) + 1}/${Math.max(1, Math.ceil(count / SLOTS))}`
	}

	turn(rule: string, by: number, model: NodeModel, nodeNow: number, pressOf: PressOf): void {
		const count = this.#arranged(rule, model, nodeNow, pressOf).length
		const pages = Math.max(1, Math.ceil(count / SLOTS))
		this.#page.set(rule, (this.page(rule, count) + by + pages) % pages)
	}

	/** Lays every source out again (and lets go of an armed freeze until the next layout while armed). */
	refresh(): void {
		this.#frozen.clear()
	}

	clear(): void {
		for (const t of this.#hints.values()) clearTimeout(t)
		this.#hints.clear()
		this.#frozen.clear()
	}

	#arranged(rule: string, model: NodeModel, nodeNow: number, pressOf: PressOf): Slot[] {
		const fresh = layout(rule, model, nodeNow, pressOf)
		if (!model.armed) {
			this.#frozen.delete(rule)
			return fresh
		}
		const keys = this.#frozen.get(rule)
		if (!keys) {
			this.#frozen.set(
				rule,
				fresh.map((s) => s.key),
			)
			return fresh
		}
		const byKey = new Map(fresh.map((s) => [s.key, s]))
		return keys.map((k) => byKey.get(k) ?? { ...REMOVED, key: k })
	}
}
