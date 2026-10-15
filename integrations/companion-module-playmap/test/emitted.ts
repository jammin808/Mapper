// What the module can send and what it reads, as the node's contract test checks them (tools/emit-lines.ts writes both files).
import type { CompanionOptionValues } from '@companion-module/base'
import { BUNDLED_VERBS, generatedRows } from '../src/catalog/verbs.ts'
import { lineOf, optionIdOf, sampleOf } from '../src/catalog/write.ts'
import { fixedLines } from '../src/actions.ts'
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
