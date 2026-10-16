/** INDEX: the show's structure as the node writes it for a deck (docs/wire/companion.md). Read tolerantly: a key the node does not
 * write yet reads as empty, a key it adds later is ignored. */

export interface IndexItem {
	readonly id: string
	readonly label: string
	readonly source: string
	readonly kind: string
	readonly lengthMs: number | null
}

export interface IndexPlaylist {
	readonly id: string
	readonly name: string
	readonly loop: boolean
	readonly items: readonly IndexItem[]
}

export interface IndexEntity {
	readonly id: string
	readonly name: string
}

export interface IndexSurface extends IndexEntity {
	readonly tags: readonly string[]
	readonly source: string
}

export interface IndexProjector extends IndexEntity {
	readonly output: string | null
	readonly device: string | null
}

export interface IndexOutput extends IndexEntity {
	readonly kind: string
	readonly projector: string | null
	readonly enabled: boolean
}

export interface IndexDevice extends IndexEntity {
	readonly protocol: string
	readonly projector: string | null
}

export interface IndexGeneratorParam {
	readonly name: string
	readonly kind: string
	readonly min?: number
	readonly max?: number
	readonly step?: number
	readonly choices?: readonly string[]
}

export interface IndexGenerator extends IndexEntity {
	readonly kind: string
	readonly params: readonly IndexGeneratorParam[]
}

export interface IndexCanvas extends IndexEntity {
	readonly layers: readonly IndexEntity[]
}

/** One key template of a package's section: its label, the line its press sends ("$(id)" is the row's id), the STATE path or
 * "$(light)" that lights it, and its press shape. */
export interface IndexKey {
	readonly label: string
	readonly line: string
	readonly feedback: string | null
	readonly press: string
}

export interface IndexRow {
	readonly id: string
	readonly kind: string
	readonly label: string
	readonly parent: string
	readonly light: string
	readonly state: string
}

/** Whether a section row is pressed: every row but an encoder's (the show's MASTER), which a dial turns with its value and whose
 * line is never sent bare. */
export function pressable(row: IndexRow): boolean {
	return row.kind !== 'encoder'
}

export interface IndexSection {
	readonly section: string
	readonly title: string
	readonly rowKind: string
	readonly keys: readonly IndexKey[]
	readonly rows: readonly IndexRow[]
}

export interface IndexDoc {
	readonly revision: number
	readonly sig: string
	readonly labels: string
	readonly playlists: readonly IndexPlaylist[]
	readonly surfaces: readonly IndexSurface[]
	readonly projectors: readonly IndexProjector[]
	readonly outputs: readonly IndexOutput[]
	readonly devices: readonly IndexDevice[]
	readonly generators: readonly IndexGenerator[]
	readonly canvases: readonly IndexCanvas[]
	readonly mediaCount: number
	readonly sections: readonly IndexSection[]
}

type Obj = Record<string, unknown>

const obj = (v: unknown): Obj => (v !== null && typeof v === 'object' && !Array.isArray(v) ? (v as Obj) : {})
const arr = (v: unknown): readonly unknown[] => (Array.isArray(v) ? v : [])
const s = (v: unknown): string => (typeof v === 'string' ? v : '')
const sn = (v: unknown): string | null => (typeof v === 'string' ? v : null)
const n = (v: unknown): number | undefined => (typeof v === 'number' && Number.isFinite(v) ? v : undefined)

/** Reads INDEX's JSON (the push's or the reply's payload). */
export function readIndex(json: unknown): IndexDoc {
	const root = obj(json)
	const entity = (e: unknown): IndexEntity => ({ id: s(obj(e).id), name: s(obj(e).name) })
	return {
		revision: n(root.revision) ?? 0,
		sig: s(root.sig),
		labels: s(root.labels),
		playlists: arr(root.playlists).map((p) => ({
			...entity(p),
			loop: obj(p).loop === true,
			items: arr(obj(p).items).map((i) => {
				const o = obj(i)
				return { id: s(o.id), label: s(o.label), source: s(o.source), kind: s(o.kind), lengthMs: n(o.lengthMs) ?? null }
			}),
		})),
		surfaces: arr(root.surfaces).map((e) => ({
			...entity(e),
			tags: arr(obj(e).tags).map(s),
			source: s(obj(e).source),
		})),
		projectors: arr(root.projectors).map((e) => ({
			...entity(e),
			output: sn(obj(e).output),
			device: sn(obj(e).device),
		})),
		outputs: arr(root.outputs).map((e) => ({
			...entity(e),
			kind: s(obj(e).kind),
			projector: sn(obj(e).projector),
			enabled: obj(e).enabled !== false,
		})),
		devices: arr(root.devices).map((e) => ({
			...entity(e),
			protocol: s(obj(e).protocol),
			projector: sn(obj(e).projector),
		})),
		generators: arr(root.generators).map((e) => ({
			...entity(e),
			kind: s(obj(e).kind),
			params: arr(obj(e).params).map((p) => {
				const o = obj(p)
				const min = n(o.min)
				const max = n(o.max)
				const step = n(o.step)
				return {
					name: s(o.name),
					kind: s(o.kind),
					...(min !== undefined ? { min } : {}),
					...(max !== undefined ? { max } : {}),
					...(step !== undefined ? { step } : {}),
					...(Array.isArray(o.choices) ? { choices: arr(o.choices).map(s) } : {}),
				}
			}),
		})),
		canvases: arr(root.canvases).map((e) => ({ ...entity(e), layers: arr(obj(e).layers).map(entity) })),
		mediaCount: n(obj(root.media).count) ?? 0,
		sections: arr(root.sections).map((x) => {
			const o = obj(x)
			const hint = obj(o.hint)
			return {
				section: s(o.section),
				title: s(hint.title),
				rowKind: s(hint.rowKind),
				keys: arr(hint.keys).map((k) => {
					const ko = obj(k)
					return { label: s(ko.label), line: s(ko.line), feedback: sn(ko.feedback), press: s(ko.press) || 'instant' }
				}),
				rows: arr(o.rows).map((r) => {
					const ro = obj(r)
					return {
						id: s(ro.id),
						kind: s(ro.kind),
						label: s(ro.label),
						parent: s(ro.parent),
						light: s(ro.light),
						state: s(ro.state),
					}
				}),
			}
		}),
	}
}

/** Every item of the show by id, with its playlist. */
export function itemsById(
	index: IndexDoc | null,
): Map<string, { playlist: IndexPlaylist; item: IndexItem; at: number }> {
	const out = new Map<string, { playlist: IndexPlaylist; item: IndexItem; at: number }>()
	for (const playlist of index?.playlists ?? [])
		playlist.items.forEach((item, at) => out.set(item.id, { playlist, item, at }))
	return out
}
