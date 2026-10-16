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
	/** Other names the node reads as this row (Patterns' words: EYE for FLEET, VIDEO RESUME for VIDEO PLAY …). */
	readonly aliases: readonly string[]
	readonly forms: readonly string[]
	readonly formParams: readonly (readonly VerbParam[])[]
	/** How a key sends it: at once, held to confirm, or pressed twice. */
	readonly press: 'instant' | 'hold' | 'double'
	/** Per-argument risk: the word at `arg` (0-based after the verb's words; -1 anywhere) sets the line's risk. The first match wins. */
	readonly riskWhen: readonly {
		readonly arg: number
		readonly word: string
		readonly risk: 'low' | 'review' | 'high'
	}[]
}

/** The rows the module never makes an action of: the session's own words (the module says them itself), pictures (the module asks
 * them for its keys), every row that carries a secret or a pairing token (a deck never holds one), and a row the node answers with no
 * line at all (a press would wait for an answer that never comes). The node's contract test reads this list. */
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
	// The node answers a cursor with no line at all and its lease lapses unless it is sent again: it is no key.
	'CURSOR',
])

const KINDS = new Set(['entity', 'enum', 'number', 'integer', 'text', 'json', 'time', 'ref', 'line', 'flag'])

const isText = (v: unknown): boolean => v === undefined || v === null || typeof v === 'string'
const isNumber = (v: unknown): boolean => v === undefined || v === null || (typeof v === 'number' && Number.isFinite(v))
const isFlag = (v: unknown): boolean => v === undefined || v === null || typeof v === 'boolean'

/** One parameter, every field checked: a node's VERBS is data, and a field of the wrong type leaves the parameter out. */
function readParam(p: unknown): VerbParam | null {
	if (!p || typeof p !== 'object' || Array.isArray(p)) return null
	const o = p as Record<string, unknown>
	if (typeof o.name !== 'string' || typeof o.kind !== 'string' || !KINDS.has(o.kind)) return null
	if (![o.prefix, o.keyword, o.alt].every(isText) || ![o.min, o.max, o.step].every(isNumber)) return null
	if (![o.optional, o.repeats].every(isFlag)) return null
	if (o.values !== undefined && o.values !== null)
		if (!Array.isArray(o.values) || !o.values.every((v) => typeof v === 'string')) return null
	const out: Record<string, unknown> = { name: o.name, kind: o.kind }
	for (const k of ['optional', 'prefix', 'keyword', 'values', 'min', 'max', 'step', 'alt', 'repeats'] as const)
		if (o[k] !== undefined && o[k] !== null) out[k] = o[k]
	return out as unknown as VerbParam
}

const RISKS = new Set(['low', 'review', 'high'])

function readRiskWhen(v: unknown): VerbRow['riskWhen'] {
	if (!Array.isArray(v)) return []
	return v.flatMap((w) => {
		if (!w || typeof w !== 'object') return []
		const o = w as Record<string, unknown>
		return typeof o.arg === 'number' &&
			Number.isInteger(o.arg) &&
			typeof o.word === 'string' &&
			typeof o.risk === 'string' &&
			RISKS.has(o.risk)
			? [{ arg: o.arg, word: o.word.toUpperCase(), risk: o.risk as 'low' | 'review' | 'high' }]
			: []
	})
}

/** Show classes whose rows fire at once whatever the line says (a show's emergency and its GO are never held). */
const NEVER_HELD = new Set(['panic', 'air', 'transport'])

/** How a key sends this line of this row: the row's press, made hold when a word of the line raises it to high risk (DECODE POLICY
 * … FORCE), or instant when a word lowers a held high-risk row (MEDIA OPTIMISE CANCEL), as the node's own press rule would. */
export function pressOfLine(row: VerbRow, line: string): VerbRow['press'] {
	if (row.press === 'double' || NEVER_HELD.has(row.show)) return row.press
	const args = line
		.trim()
		.split(/\s+/u)
		.slice(row.name.split(' ').length)
		.map((w) => w.toUpperCase())
	const when = row.riskWhen.find((w) => (w.arg < 0 ? args.includes(w.word) : args[w.arg] === w.word))
	if (!when) return row.press
	if (when.risk === 'high') return 'hold'
	return row.risk === 'high' ? 'instant' : row.press
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
			aliases: Array.isArray(o.aliases) ? o.aliases.filter((a): a is string => typeof a === 'string') : [],
			forms: Array.isArray(o.forms) ? o.forms.filter((f): f is string => typeof f === 'string') : [o.syntax],
			formParams: formParams.length > 0 ? formParams : [[]],
			press: o.press === 'hold' || o.press === 'double' ? o.press : 'instant',
			riskWhen: readRiskWhen(o.riskWhen),
		})
	}
	return rows
}

/** Each catalogue's rows by every name the node reads them by: a row's name, then its aliases, a later row's taking a name an
 * earlier one had (VerbCatalog's ByName). */
const BY_NAME = new WeakMap<readonly VerbRow[], ReadonlyMap<string, VerbRow>>()

function byName(rows: readonly VerbRow[]): ReadonlyMap<string, VerbRow> {
	let map = BY_NAME.get(rows)
	if (!map) {
		const built = new Map<string, VerbRow>()
		for (const row of rows) {
			built.set(row.name, row)
			for (const alias of row.aliases) built.set(alias, row)
		}
		map = built
		BY_NAME.set(rows, map)
	}
	return map
}

/** A word upper-cased as the node does it (ToUpperInvariant): each mark by its own single upper-case mark, a mark whose upper case is
 * several marks (ß, ﬁ) kept as it is. */
export function upperInvariant(word: string): string {
	let out = ''
	for (const c of word) {
		const u = c.toUpperCase()
		out += [...u].length === 1 ? u : c
	}
	return out
}

/** The row a line's words name, as the node's VerbCatalog.Match finds it: its first two words upper-cased, else its first, by name
 * or alias; VT and CLIP are VIDEO and TC is TIMECODE whatever follows. `words` is how many of the line's words the name took. */
export function matchRow(
	rows: readonly VerbRow[],
	words: readonly string[],
): { readonly row: VerbRow; readonly words: number } | undefined {
	const names = byName(rows)
	let first = upperInvariant(words[0] ?? '')
	if (first === 'VT' || first === 'CLIP') first = 'VIDEO'
	else if (first === 'TC') first = 'TIMECODE'
	const two = words.length >= 2 ? names.get(`${first} ${upperInvariant(words[1] ?? '')}`) : undefined
	if (two) return { row: two, words: 2 }
	const one = words.length >= 1 ? names.get(first) : undefined
	return one ? { row: one, words: 1 } : undefined
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
