import { readIndex, itemsById, type IndexDoc, type IndexItem, type IndexPlaylist } from './index-doc.ts'
import { P, at, bool, field, list, num, str } from './paths.ts'

/** WHOAMI as the node answers it: the session, its role, whether the show is armed and the verbs it may run now. */
export interface Whoami {
	readonly session: string
	readonly transport: string
	readonly channel: string
	readonly role: string
	readonly armed: boolean
	/** null = every verb ("*"). */
	readonly may: ReadonlySet<string> | null
	readonly minGoMs: number
	readonly proto: number
}

/** One playlist's transport, interpolated to an instant on the node's clock. */
export interface PlaylistNow {
	readonly id: string
	readonly name: string
	readonly state: string
	readonly focused: boolean
	readonly itemId: string | null
	readonly itemLabel: string
	readonly itemSource: string | null
	readonly nextId: string | null
	readonly nextLabel: string
	readonly positionMs: number | null
	readonly lengthMs: number | null
	readonly remainingMs: number | null
	/** 0 to 1 of the current item, null without a length. */
	readonly progress: number | null
	readonly rate: number
}

/** What the module knows of the node: STATE, INDEX and WHOAMI as last heard, and the node's clock offset. Emptied when the link drops,
 * so a key never shows a fact from a node it no longer hears. */
export class NodeModel {
	state: Record<string, unknown> | null = null
	index: IndexDoc | null = null
	whoami: Whoami | null = null
	/** The node's clock minus this machine's (ms), from the PING exchange. */
	offsetMs = 0
	#items = new Map<string, { playlist: IndexPlaylist; item: IndexItem; at: number }>()

	clear(): void {
		this.state = null
		this.index = null
		this.whoami = null
		this.#items.clear()
	}

	setState(json: unknown): void {
		this.state =
			json !== null && typeof json === 'object' && !Array.isArray(json) ? (json as Record<string, unknown>) : null
	}

	setIndex(json: unknown): IndexDoc {
		this.index = readIndex(json)
		this.#items = itemsById(this.index)
		return this.index
	}

	setWhoami(json: unknown): Whoami {
		const o = (json !== null && typeof json === 'object' ? json : {}) as Record<string, unknown>
		const may =
			o.may === '*' || !Array.isArray(o.may) ? null : new Set(o.may.filter((v): v is string => typeof v === 'string'))
		this.whoami = {
			session: str(o.session),
			transport: str(o.transport),
			channel: str(o.channel),
			role: str(o.role),
			armed: bool(o.armed),
			may,
			minGoMs: num(o.minGoMs),
			proto: num(o.proto, 1),
		}
		return this.whoami
	}

	/** The node's clock now, from this machine's. */
	nodeNow(localNow: number): number {
		return localNow + this.offsetMs
	}

	item(id: string): { playlist: IndexPlaylist; item: IndexItem; at: number } | undefined {
		return this.#items.get(id)
	}

	/** Armed, from STATE (show) or WHOAMI, whichever says so. */
	get armed(): boolean {
		return bool(at(this.state, P.showArmed)) || this.whoami?.armed === true
	}

	playlists(nodeNow: number): PlaylistNow[] {
		const stateAt = num(at(this.state, P.at), nodeNow)
		return list(this.state, P.playlists).map((p) => playlistNow(p, stateAt, nodeNow))
	}

	playlist(id: string, nodeNow: number): PlaylistNow | undefined {
		const p = list(this.state, P.playlists).find((e) => field(e, P.plId) === id)
		return p ? playlistNow(p, num(at(this.state, P.at), nodeNow), nodeNow) : undefined
	}

	focused(nodeNow: number): PlaylistNow | undefined {
		const all = this.playlists(nodeNow)
		return all.find((p) => p.focused) ?? all[0]
	}

	output(id: string): unknown {
		return list(this.state, P.outputs).find((e) => field(e, P.opId) === id)
	}

	projector(id: string): unknown {
		return list(this.state, P.projectors).find((e) => field(e, P.pjId) === id)
	}

	/** Whether any playlist plays now (the interpolation ticker runs only then). */
	get playing(): boolean {
		return list(this.state, P.playlists).some((p) => field(p, P.plState) === 'playing')
	}
}

/** A playlist's transport at nodeNow: STATE says where it was at STATE's `at`; a playing one has moved on at its rate since. */
export function playlistNow(p: unknown, stateAt: number, nodeNow: number): PlaylistNow {
	const state = str(field(p, P.plState), 'idle')
	const rate = num(field(p, P.plRate), 1)
	const lengthRaw = field(p, P.plLengthMs)
	const lengthMs = typeof lengthRaw === 'number' ? lengthRaw : null
	const posRaw = field(p, P.plPositionMs)
	let positionMs: number | null = typeof posRaw === 'number' ? posRaw : null
	if (positionMs !== null && state === 'playing') positionMs += Math.max(0, nodeNow - stateAt) * rate
	if (positionMs !== null) positionMs = Math.max(0, lengthMs !== null ? Math.min(lengthMs, positionMs) : positionMs)
	const remainingMs = positionMs !== null && lengthMs !== null ? Math.max(0, lengthMs - positionMs) : null
	const itemId = field(p, P.plItemId)
	const nextId = field(p, P.plNextId)
	const source = field(p, P.plItemSource)
	return {
		id: str(field(p, P.plId)),
		name: str(field(p, P.plName)),
		state,
		focused: bool(field(p, P.plFocused)),
		itemId: typeof itemId === 'string' ? itemId : null,
		itemLabel: str(field(p, P.plItemLabel)),
		itemSource: typeof source === 'string' ? source : null,
		nextId: typeof nextId === 'string' ? nextId : null,
		nextLabel: str(field(p, P.plNextLabel)),
		positionMs,
		lengthMs,
		remainingMs,
		progress: positionMs !== null && lengthMs !== null && lengthMs > 0 ? positionMs / lengthMs : null,
		rate,
	}
}

/** "m:ss" or "h:mm:ss" of milliseconds, rounded up for a countdown (a clip with 0.4 s left still shows 0:01). */
export function clockText(ms: number | null, up = false): string {
	if (ms === null) return ''
	const total = up ? Math.ceil(ms / 1000) : Math.floor(ms / 1000)
	const h = Math.floor(total / 3600)
	const m = Math.floor((total % 3600) / 60)
	const sec = total % 60
	const ss = String(sec).padStart(2, '0')
	return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${m}:${ss}`
}
