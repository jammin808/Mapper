import type {
	CompanionOptionValues,
	DropdownChoice,
	JsonValue,
	SomeCompanionActionInputField,
} from '@companion-module/base'
import { quote } from '../link/grammar.ts'
import type { VerbParam, VerbRow } from './verbs.ts'

/** Option ids the module keeps for itself on every generated action. */
const RESERVED = new Set(['form', 'confirm'])

/** A param's option id: its name made safe ("field.path" → "field_path"), a form after the first prefixed "f<k>_". */
export function optionIdOf(param: VerbParam, form: number): string {
	const safe = param.name.replace(/[^a-zA-Z0-9]+/g, '_').replace(/^_+|_+$/g, '') || 'value'
	const base = RESERVED.has(safe) ? `p_${safe}` : safe
	return form === 0 ? base : `f${form}_${base}`
}

/** A literal word the form always says (a single-choice required word like "ALL" or "REMOTE-VIEW-ONLY"): no option. */
function isFixedWord(p: VerbParam): boolean {
	return p.kind === 'enum' && !p.optional && (p.values?.length ?? 0) === 1
}

/** Entity dropdown choices by id prefix, from INDEX. */
export type EntityChoices = (prefix: string | undefined) => DropdownChoice[]

/** The one shape a text value is written in ("<w>x<h>", "@<fps>", "#<rrggbb>"): an alt with marks of its own outside its
 * placeholders and no other alternative. Any other alt is a word beside the value ("ALL", "#<n>"). */
export function shapeOf(p: VerbParam): string | undefined {
	if (p.kind !== 'text' || !p.alt?.includes('<')) return undefined
	const marks = p.alt.replace(/<[^>]*>/gu, '')
	return marks !== '' && !marks.includes('|') ? p.alt : undefined
}

function label(p: VerbParam): string {
	let text = p.keyword ? `${p.keyword} ${p.name}` : p.name
	if (p.min !== undefined && p.max !== undefined) text += ` (${p.min}–${p.max})`
	const shape = shapeOf(p)
	if (shape) text += ` (as ${shape})`
	else if (p.alt) text += ` (or ${p.alt})`
	if (p.repeats) text += ' (several, space-separated)'
	if (p.optional) text += ' — optional'
	return text.charAt(0).toUpperCase() + text.slice(1)
}

/** The option fields of a row: one per parameter of each form (a form chooser when the row has several), visible for its form. */
export function optionsOf(row: VerbRow, choices: EntityChoices): SomeCompanionActionInputField[] {
	const fields: SomeCompanionActionInputField[] = []
	const several = row.formParams.length > 1
	if (several)
		fields.push({
			type: 'dropdown',
			id: 'form',
			label: 'Form',
			default: 0,
			// The fields of each form are shown by an expression on this chooser, which may only read a field that is never one.
			disableAutoExpression: true,
			choices: row.forms.map((f, i) => ({ id: i, label: f })),
		})
	row.formParams.forEach((params, k) => {
		const visible = several ? { isVisibleExpression: `$(options:form) == ${k}` } : {}
		for (const p of params) {
			if (isFixedWord(p)) continue
			const id = optionIdOf(p, k)
			const shape = shapeOf(p)
			const tooltip = shape ? `Written as ${shape}` : p.alt ? `Also takes ${p.alt}` : undefined
			const common = { id, label: label(p), ...visible, ...(tooltip ? { tooltip } : {}) }
			if (p.kind === 'flag') fields.push({ ...common, type: 'checkbox', default: false })
			else if (p.kind === 'enum')
				fields.push({
					...common,
					type: 'dropdown',
					default: p.optional ? '' : (p.values?.[0] ?? ''),
					choices: [
						...(p.optional ? [{ id: '', label: '(leave out)' }] : []),
						...(p.values ?? []).map((v) => ({ id: v, label: v })),
					],
				})
			else if (p.kind === 'entity')
				fields.push({
					...common,
					type: 'dropdown',
					default: '',
					allowCustom: true,
					choices: [...(p.optional ? [{ id: '', label: '(leave out)' }] : []), ...choices(p.prefix)],
				})
			else fields.push({ ...common, type: 'textinput', default: '' })
		}
	})
	return fields
}

function text(v: JsonValue | undefined): string {
	if (v === undefined || v === null) return ''
	if (typeof v === 'string') return v.trim()
	if (typeof v === 'number' || typeof v === 'boolean') return String(v)
	return JSON.stringify(v)
}

/** The line a generated action sends for these options, or why it cannot be written. Words are quoted as the node's grammar reads
 * them; JSON and an embedded line ride raw to the end. */
export function lineOf(row: VerbRow, options: CompanionOptionValues): { line: string } | { error: string } {
	const form = row.formParams.length > 1 ? Math.trunc(Number(options.form ?? 0)) || 0 : 0
	const params = row.formParams[form] ?? row.formParams[0] ?? []
	const words: string[] = [row.name]
	for (const p of params) {
		if (isFixedWord(p)) {
			words.push(p.values?.[0] ?? '')
			continue
		}
		const value = options[optionIdOf(p, form)]
		if (p.kind === 'flag') {
			if (value === true || value === 'true' || value === 1) words.push(p.values?.[0] ?? p.name.toUpperCase())
			continue
		}
		const t = text(value)
		if (/[\r\n]/u.test(t)) return { error: `${p.name} cannot hold a line break — one line is one command` }
		if (t === '') {
			if (p.optional) continue
			return { error: `${row.name} needs ${p.name} — ${row.syntax}` }
		}
		if (p.keyword) words.push(p.keyword)
		if (p.kind === 'json' || p.kind === 'line') words.push(t)
		else if (p.repeats) for (const w of t.split(/\s+/u)) words.push(quote(w))
		else words.push(quote(t))
	}
	return { line: words.join(' ') }
}

/** Sample words for a shape's placeholders; any other placeholder is "1". */
const SHAPE_SAMPLES: Readonly<Record<string, string>> = {
	rrggbb: 'FF0000',
	w: '1920',
	h: '1080',
	fps: '50',
	version: '1.0.0',
}

/** A value every parameter kind accepts, for the lines the module emits for the node's parser (test/lines.txt): a shaped value in
 * its shape, a source of a kind the parameter names, a repeated number as a pair. */
export function sampleOf(p: VerbParam): JsonValue {
	switch (p.kind) {
		case 'entity':
			return `${p.prefix ?? 'pl'}0a1b2c3d`
		case 'enum':
			return p.values?.[0] ?? ''
		case 'flag':
			return true
		case 'number':
		case 'integer': {
			const v = String(p.min ?? 1)
			return p.repeats ? `${v} ${v}` : v
		}
		case 'time':
			return p.keyword === 'AT' ? '+500' : '83'
		case 'ref': {
			const kind = p.values?.[0]
			return kind === undefined || p.values?.includes('color:#') ? 'color:#FF0000' : `${kind}0a1b2c3d`
		}
		case 'json':
			return '{"label":"Front wall"}'
		case 'line':
			return 'PLAYLIST NEXT pl0a1b2c3d'
		default: {
			const shape = shapeOf(p)
			return shape ? shape.replace(/<([^>]*)>/gu, (_m, name: string) => SHAPE_SAMPLES[name] ?? '1') : 'Front wall'
		}
	}
}
