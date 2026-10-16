// What the module can send and what it reads, as the node's contract test checks them (tools/emit-lines.ts writes the files).
import { readFileSync } from 'node:fs'
import type { CompanionOptionValues } from '@companion-module/base'
import { BUNDLED_VERBS, generatedRows } from '../src/catalog/verbs.ts'
import { lineOf, optionIdOf, sampleOf } from '../src/catalog/write.ts'
import { fixedLines } from '../src/actions.ts'
import { fillHint, layout } from '../src/live.ts'
import { pressable, readIndex, type IndexRow, type IndexSection } from '../src/state/index-doc.ts'
import { NodeModel } from '../src/state/model.ts'
import { P } from '../src/state/paths.ts'

/** Every generated action's lines (each form with its required parameters, then with every optional one too) and every line the
 * module's own actions and presets write, sorted and unique. */
export function allLines(): string[] {
	const lines = new Set<string>()
	for (const row of generatedRows(BUNDLED_VERBS)) {
		row.formParams.forEach((params, form) => {
			const required: CompanionOptionValues = { form }
			const every: CompanionOptionValues = { form }
			for (const p of params) {
				if (!p.optional) required[optionIdOf(p, form)] = sampleOf(p)
				every[optionIdOf(p, form)] = sampleOf(p)
			}
			for (const options of [required, every]) {
				const written = lineOf(row, options)
				if ('error' in written) throw new Error(`${row.name}: ${written.error}`)
				lines.add(written.line)
			}
		})
	}
	for (const line of fixedLines()) lines.add(line)
	return [...lines].sort()
}

export function statePaths(): string[] {
	return [...new Set(Object.values(P))].sort()
}

/** A row for a section the INDEX fixture lists none of, by its hint's row kind: what a show would list there. An align group's id
 * holds a space, so the module quotes it in a line. */
export const SAMPLE_ROWS: Readonly<Record<string, IndexRow>> = {
	align: { id: 'Front wall', kind: 'align', label: 'Front wall', parent: '', light: 'grey', state: 'idle' },
	projector: { id: 'pj0a1b2c3d', kind: 'projector', label: 'Left', parent: '', light: 'green', state: 'A' },
	fx: { id: 'fx0a1b2c3d', kind: 'fx', label: 'Blur', parent: 'sf0a1b2c3d', light: 'grey', state: 'on' },
	output: { id: 'op0a1b2c3d', kind: 'output', label: 'Output 2', parent: '', light: 'green', state: 'locked' },
	chase: { id: 'pl0a1b2c3d', kind: 'chase', label: 'Show', parent: '', light: 'grey', state: 'disengaged' },
	lednode: { id: 'ln0a1b2c3d', kind: 'lednode', label: 'Truss', parent: '', light: 'grey', state: 'off' },
}

/** The INDEX fixture's sections, a section with no rows given the sample row of its kind when a line of its keys names a row (one
 * whose lines name none, SCAN NEXT, is given a row that names nothing). */
export function sampledSections(): IndexSection[] {
	const index = readIndex(JSON.parse(readFileSync(new URL('./fixtures/index.json', import.meta.url), 'utf8')))
	return index.sections.map((s) => {
		if (s.rows.length > 0) return s
		const named = s.keys.some((k) => k.line.includes('$(id)'))
		const sample = SAMPLE_ROWS[s.rowKind]
		if (named && !sample)
			throw new Error(`section ${s.section} has no rows and no sample row for its kind ${s.rowKind}`)
		return { ...s, rows: [sample ?? { id: '', kind: s.rowKind, label: '', parent: '', light: 'grey', state: '' }] }
	})
}

/** Every line a package section's key (section_press) or its Live slot (section:<name>) sends for the INDEX fixture's sections:
 * every key's line for every row that is pressed, filled by the module's fillHint, and every Live slot's line as the module lays the
 * page out. Sorted and unique. */
export function sectionLines(): string[] {
	const sections = sampledSections()
	const lines = new Set<string>()
	for (const s of sections)
		for (const k of s.keys) for (const r of s.rows.filter(pressable)) lines.add(fillHint(k.line, r))
	const model = new NodeModel()
	const doc = JSON.parse(readFileSync(new URL('./fixtures/index.json', import.meta.url), 'utf8')) as {
		sections: { section: string; rows: unknown[] }[]
	}
	for (const s of doc.sections)
		s.rows = sections.find((x) => x.section === s.section)?.rows.map((r) => ({ ...r })) ?? []
	model.setIndex(doc)
	for (const s of sections)
		for (const slot of layout(`section:${s.section}`, model, 0, () => 'instant')) if (slot.line) lines.add(slot.line)
	return [...lines].sort()
}
