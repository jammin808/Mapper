import bundled from './verbs.json' with { type: 'json' }

/** One typed parameter of a verb's form, as VERBS writes it (VerbParams on the node). */
export interface VerbParam {
	readonly name: string
	readonly kind: 'entity' | 'enum' | 'number' | 'integer' | 'text' | 'json' | 'time' | 'ref' | 'line' | 'flag'
	readonly optional?: boolean
	readonly prefix?: string
	readonly keyword?: string
	readonly values?: readonly string[]
	readonly min?: number
	readonly max?: number
	readonly step?: number
	readonly alt?: string
	readonly repeats?: boolean
}

/** One catalogue row, as VERBS and /api/verbs write it. */
export interface VerbRow {
	readonly name: string
	readonly syntax: string
	readonly words: string
	readonly category: string
	readonly risk: 'low' | 'review' | 'high'
	readonly mutates: boolean
	readonly show: string
	readonly available: boolean
	readonly pairedOnly: boolean
	readonly forms: readonly string[]
	readonly formParams: readonly (readonly VerbParam[])[]
	/** How a key sends it: at once, held to confirm, or pressed twice. */
	readonly press: 'instant' | 'hold' | 'double'
}

/** The rows the module never makes an action of: the session's own words (the module says them itself), pictures (the module asks
 * them for its keys), and every row that carries a secret or a pairing token (a deck never holds one). The node's contract test reads
 * this list. */
export const EXCLUDED: ReadonlySet<string> = new Set([
	'HELLO',
	'NONCE',
	'AUTH',
	'AUTH-PROOF',
	'PING',
	'STATE',
	'CONTEXT',
	'HELP',
	'VERBS',
	'SUBSCRIBE',
	'INDEX',
	'EVENTS',
	'PRESENCE',
	'WHOAMI',
	'THUMB',
	'VIEW',
	'RECORD',
	'PAIRING NEW',
	'CREDENTIAL SET',
	'CREDENTIAL CLEAR',
	'CREDENTIAL LIST',
	'LINK PATTERNS',
	'LINK STAGEWATCH',
])

const KINDS = new Set(['entity', 'enum', 'number', 'integer', 'text', 'json', 'time', 'ref', 'line', 'flag'])

function readParam(p: unknown): VerbParam | null {
	if (!p || typeof p !== 'object') return null
	const o = p as Record<string, unknown>
	if (typeof o.name !== 'string' || typeof o.kind !== 'string' || !KINDS.has(o.kind)) return null
	return o as unknown as VerbParam
}

/** Reads VERBS' JSON (the bundled copy, or the node's own answer). A row the module cannot read is left out, never guessed. */
export function readVerbs(json: unknown): VerbRow[] {
	if (!Array.isArray(json)) return []
	const rows: VerbRow[] = []
	for (const r of json) {
		if (!r || typeof r !== 'object') continue
		const o = r as Record<string, unknown>
		if (typeof o.name !== 'string' || typeof o.syntax !== 'string') continue
		const formParams = Array.isArray(o.formParams)
			? o.formParams.map((f) => (Array.isArray(f) ? f.map(readParam).filter((x): x is VerbParam => x !== null) : []))
			: [Array.isArray(o.params) ? o.params.map(readParam).filter((x): x is VerbParam => x !== null) : []]
		rows.push({
			name: o.name,
			syntax: o.syntax,
			words: typeof o.words === 'string' ? o.words : '',
			category: typeof o.category === 'string' ? o.category : '',
			risk: o.risk === 'high' || o.risk === 'review' ? o.risk : 'low',
			mutates: o.mutates === true,
			show: typeof o.show === 'string' ? o.show : '',
			available: o.available !== false,
			pairedOnly: o.pairedOnly === true,
			forms: Array.isArray(o.forms) ? o.forms.filter((f): f is string => typeof f === 'string') : [o.syntax],
			formParams: formParams.length > 0 ? formParams : [[]],
			press: o.press === 'hold' || o.press === 'double' ? o.press : 'instant',
		})
	}
	return rows
}

/** The catalogue this module was built with (the node's VERBS at merge; a node answers its own at connect). */
export const BUNDLED_VERBS: readonly VerbRow[] = readVerbs(bundled)

/** The rows that become actions. */
export function generatedRows(rows: readonly VerbRow[]): VerbRow[] {
	return rows.filter((r) => !EXCLUDED.has(r.name))
}

/** "PLAYLIST GO" → "v_playlist_go". Frozen from 1.0.0: an action id never changes (upgrade scripts are append-only). */
export function actionIdOf(name: string): string {
	return (
		'v_' +
		name
			.toLowerCase()
			.replace(/[^a-z0-9]+/g, '_')
			.replace(/^_+|_+$/g, '')
	)
}
