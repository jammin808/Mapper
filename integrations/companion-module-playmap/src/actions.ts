import type {
	CompanionActionDefinition,
	CompanionActionDefinitions,
	CompanionInputFieldDropdown,
	CompanionOptionValues,
	JsonValue,
	SomeCompanionActionInputField,
} from '@companion-module/base'
import { EXCLUDED, SECRET, actionIdOf, generatedRows, matchRow, type VerbRow } from './catalog/verbs.ts'
import { splitLine } from './link/grammar.ts'
import { lineOf, optionIdOf, optionsOf } from './catalog/write.ts'
import type { Ctx } from './context.ts'
import type { EncoderTarget } from './encoders.ts'
import { liveSlotOptions, slotOf } from './feedbacks.ts'
import type { PressResult } from './guards.ts'
import { RULES, fillHint } from './live.ts'
import { P, at, num } from './state/paths.ts'
import { parseNode } from './json.ts'
import { Lines } from './lines.ts'
import { pressable } from './state/index-doc.ts'

/** The verbs whose press is a GO of a playlist (the double-GO guard applies). */
const GO_VERBS = new Set(['PLAYLIST GO', 'PLAYLIST NEXT', 'PLAYLIST PREV'])

/** The PANIC outcomes a key can name (the PANIC row's choices; "" is the show's default outcome). */
export const PANIC_OUTCOMES = [
	'',
	'BLACK',
	'RUN',
	'REWIND',
	'NEXT',
	'FREEZE',
	'SAFE',
	'SHUTTER',
	'FX-OFF',
	'HARD',
] as const

export { Lines } from './lines.ts'

/** Every line the module's own actions, presets and link write (with sample ids), for the node's parser test. */
export function fixedLines(): string[] {
	const pl = 'pl0a1b2c3d'
	const it = 'it0a1b2c3d'
	return [
		Lines.go(pl, it),
		Lines.go(pl, null),
		Lines.next(pl, it),
		Lines.prev(pl),
		Lines.goItem(pl, it),
		...PANIC_OUTCOMES.map(Lines.panic),
		Lines.blackoutToggle,
		Lines.freezeToggle,
		'PANIC CLEAR',
		Lines.master(55),
		Lines.rate(pl, 1.05),
		Lines.seek(pl, 12.5),
		Lines.generatorBy('gn0a1b2c3d', 'level', -1),
		Lines.lensBy('dv0a1b2c3d', 'ZOOM', 2),
		Lines.nudge('sf0a1b2c3d', 'opacity', 0.05),
		Lines.nudge('sf0a1b2c3d', 'opacity', -0.05),
		Lines.identifyOutput('op_1'),
		Lines.thumb('color:#FF0000', 96),
		Lines.thumb('media:md0a1b2c3d', 144, 'media-5e6f7a8b9c0d1e2f-144x81-j'),
		Lines.hello('Companion:FOH deck'),
		Lines.proof('0'.repeat(64)),
		Lines.ping(1760101234567),
		Lines.presence('{"kind":"companion","module":"1.0.0","live":"focused","page":"1/1","thumbs":0}'),
		Lines.whoami,
		Lines.verbs,
		Lines.index,
		Lines.subscribe('INDEX'),
		Lines.subscribe('EVENTS'),
		Lines.watchThumbs(['color:#FF0000', 'media:md0a1b2c3d']),
		Lines.watchThumbs([]),
		Lines.eventsSince(412),
	]
}

const text = (v: JsonValue | undefined): string =>
	typeof v === 'string' ? v.trim() : typeof v === 'number' ? String(v) : ''

function notSentResult(error: string): PressResult {
	return { ok: false, stage: 'not-sent', effect: '', error, reply: '' }
}

/** Two presses within 3 s, for a key whose verb is pressed twice (the module keeps the first press; the second sends). */
const CONFIRM: CompanionInputFieldDropdown = {
	type: 'dropdown',
	id: 'confirm',
	label: 'Confirm',
	default: 'none',
	choices: [
		{ id: 'none', label: 'Send on the press' },
		{ id: 'twice', label: 'Two presses within 3 s' },
	],
}

/** A result for a press that only armed its control (the first of two presses). */
const ARMED: PressResult = {
	ok: false,
	stage: 'not-sent',
	effect: '',
	error: 'press again within 3 s to confirm',
	reply: '',
}

/** One generated action per catalogue row (`v_<name>`): options from the row's typed parameters (entity dropdowns from INDEX),
 * the press shape's confirm, the line written by the node's grammar, the receipt on the key and the node's first reply as the
 * action's result. Learn fills a playlist from the focused one and an item from its current item. */
function generatedAction(ctx: Ctx, row: VerbRow): CompanionActionDefinition {
	const options: SomeCompanionActionInputField[] = [
		...optionsOf(row, (prefix) => ctx.choices(prefix)),
		{ ...CONFIRM, default: row.press === 'double' ? 'twice' : 'none' },
	]
	const learnable = (row.formParams[0] ?? []).filter((p) => p.prefix === 'pl' || p.prefix === 'it')
	return {
		name: row.name,
		description: `${row.words} — ${row.risk} risk${row.available ? '' : ' (this node does not answer it)'}`,
		options,
		hasResult: true,
		callback: async (action) => {
			const written = lineOf(row, action.options)
			if ('error' in written) return ctx.receipts.notSent(action.controlId, row.name, written.error)
			if (action.options.confirm === 'twice' && !ctx.confirms.press(action.controlId, written.line)) return ARMED
			const goPlaylist = GO_VERBS.has(row.name)
				? text(action.options[optionIdOf({ name: 'playlist', kind: 'entity' }, 0)])
				: ''
			return ctx.send(written.line, action.controlId, goPlaylist ? { goPlaylist } : {})
		},
		...(learnable.length > 0
			? {
					learn: (action): CompanionOptionValues | undefined => {
						const focused = ctx.model.focused(ctx.nodeNow())
						if (!focused) return undefined
						// Only what is learned is returned, so an option the operator set (or an expression) is kept — unless the playlist
						// moved, when an item or EXPECT not learned is cleared ('' leaves it out) rather than kept from another playlist.
						const moved = learnable.some(
							(p) => p.prefix === 'pl' && text(action.options[optionIdOf(p, 0)]) !== focused.id,
						)
						const item = focused.itemId
						// EXPECT names the item this line plays, as the node picks it: a GO naming its item needs none, a GO without one
						// plays what a GO plays; NEXT and PREV their step; a VIDEO verb the item on the air.
						const expect =
							row.name === 'PLAYLIST GO'
								? item !== null
									? ''
									: focused.nextId
								: row.name === 'PLAYLIST NEXT'
									? focused.stepId
									: row.name === 'PLAYLIST PREV'
										? focused.prevId
										: item
						const out: CompanionOptionValues = {}
						for (const p of learnable) {
							const value = p.prefix === 'pl' ? focused.id : p.keyword === 'EXPECT' ? expect : item
							if (value !== null) out[optionIdOf(p, 0)] = value
							else if (moved) out[optionIdOf(p, 0)] = ''
						}
						return out
					},
				}
			: {}),
	}
}

/** The verb a line names, as the node reads it: the line split by the node's grammar, then its row by name or alias (matchRow). A
 * line the node cannot split names none. */
export function rowOfLine(rows: readonly VerbRow[], line: string): VerbRow | undefined {
	const split = splitLine(line)
	return split.ok ? matchRow(rows, split.words)?.row : undefined
}

/** A row whose words may hold a line the node runs later, or JSON that does (a binding's target, a field SET to one): a row with a
 * line or JSON parameter in any of its forms, and SET. */
const carriesLines = (row: VerbRow): boolean =>
	row.name === 'SET' || row.formParams.some((f) => f.some((p) => p.kind === 'line' || p.kind === 'json'))

/** Every string a JSON word holds, walked without recursion. JSON a strict reader refuses (the node's reader takes comments and
 * trailing commas; a text cut short) is read for every quoted run in it, closed or not, so no string in it goes unseen. */
function stringsOf(json: string): string[] {
	const out: string[] = []
	let parsed: unknown
	try {
		parsed = JSON.parse(json)
	} catch {
		for (let i = json.indexOf('"'); i >= 0;) {
			let j = i + 1
			let body = ''
			while (j < json.length && json.charAt(j) !== '"') {
				body += json.charAt(j) === '\\' ? json.slice(j, j + 2) : json.charAt(j)
				j += json.charAt(j) === '\\' ? 2 : 1
			}
			try {
				out.push(JSON.parse(`"${body}"`) as string)
			} catch {
				out.push(body.replace(/\\([\s\S])/gu, '$1'))
			}
			i = j < json.length ? json.indexOf('"', j + 1) : -1
		}
		return out
	}
	const stack: unknown[] = [parsed]
	while (stack.length > 0) {
		const v = stack.pop()
		if (typeof v === 'string') out.push(v)
		else if (v !== null && typeof v === 'object') for (const child of Object.values(v) as unknown[]) stack.push(child)
	}
	return out
}

/** Whether a line the node may read inside these words starts with a row that carries a secret or pairs (by name or alias): every
 * suffix of the words, every word read as a line, every string of a JSON word read as a line — and each of those read so in turn.
 * Any other word is the operator's own (a label "Record", a field "index", a device's STATUS). */
function carriesSecret(rows: readonly VerbRow[], words: readonly string[]): boolean {
	const kept = (ws: readonly string[], from: number): boolean => {
		const m = matchRow(rows, ws, from)
		return m !== undefined && SECRET.has(m.row.name)
	}
	const read = new Set<string>()
	const lines: (readonly string[])[] = [words]
	while (lines.length > 0) {
		const ws = lines.pop() ?? []
		for (let i = 0; i < ws.length; i++) {
			if (kept(ws, i)) return true
			const word = ws[i] ?? ''
			if (read.has(word)) continue
			read.add(word)
			if (kept([word], 0)) return true
			if (word.startsWith('{') || word.startsWith('['))
				for (const text of stringsOf(word)) lines.push(splitLine(text).words)
			else {
				const inner = splitLine(word).words
				if (inner.length !== 1 || inner[0] !== word) lines.push(inner)
			}
		}
	}
	return false
}

/** The one fence every key's line passes before it is sent, whoever wrote it (a generated action, raw, a section key or Live slot
 * from INDEX): read as the node reads it, a line must start with a verb of the node, never one the module keeps to itself (the
 * session's words, secrets, pairing, a row the node answers with no line), nor carry a secret or a pairing inside it where the node
 * may read a line. What it refuses names only the outer verb, never the operator's words (a token typed as a line). */
export function fence(
	rows: readonly VerbRow[],
	line: string,
): { ok: true } | { ok: false; verb: string; error: string } {
	const split = splitLine(line)
	const row = split.ok ? matchRow(rows, split.words)?.row : undefined
	if (!row) return { ok: false, verb: 'line', error: 'no verb of the node starts this line' }
	if (EXCLUDED.has(row.name) || SECRET.has(row.name))
		return { ok: false, verb: row.name, error: `${row.name} is the module's own, never sent from a key` }
	if (carriesLines(row) && carriesSecret(rows, split.words))
		return { ok: false, verb: row.name, error: `${row.name} would carry a line that is never sent from a key` }
	return { ok: true }
}

/** An encoder's turn of $(this:delta): the delta option's evaluated number. */
function deltaOf(options: CompanionOptionValues): number {
	const d = Number(options.delta)
	return Number.isFinite(d) ? d : 0
}

const DELTA: SomeCompanionActionInputField = {
	type: 'textinput',
	id: 'delta',
	label: 'Turn (a preset gives $(this:delta); -1 and 1 on older surfaces)',
	default: '1',
}

function encoder(
	ctx: Ctx,
	name: string,
	description: string,
	extra: SomeCompanionActionInputField[],
	step: number,
	target: (options: CompanionOptionValues, step: number) => { key: string; target: EncoderTarget } | null,
): CompanionActionDefinition {
	return {
		name,
		description,
		options: [...extra, DELTA, { type: 'textinput', id: 'step', label: 'Step per detent', default: String(step) }],
		callback: (action) => {
			if (!ctx.linked()) return // a turn while the node is not linked does nothing
			const t = target(action.options, num(Number(action.options.step), step))
			if (t) ctx.encoders.turn(t.key, deltaOf(action.options), t.target, action.controlId)
		},
	}
}

/** Every action: the generated ones, then the module's own (smart transport, the Live page, encoders, lines). */
export function actionDefinitions(ctx: Ctx): CompanionActionDefinitions {
	const defs: CompanionActionDefinitions = {}
	for (const row of generatedRows(ctx.verbs)) defs[actionIdOf(row.name)] = generatedAction(ctx, row)
	const pl = ctx.choices('pl')
	const focusedOr = (options: CompanionOptionValues): ReturnType<Ctx['model']['focused']> => {
		const id = text(options.playlist)
		return id ? ctx.model.playlist(id, ctx.nodeNow()) : ctx.model.focused(ctx.nodeNow())
	}
	const playlistOption: SomeCompanionActionInputField = {
		type: 'dropdown',
		id: 'playlist',
		label: 'Playlist (the focused one when empty)',
		default: '',
		allowCustom: true,
		choices: [{ id: '', label: '(focused)' }, ...pl],
	}
	const transport = (verb: 'go' | 'next' | 'prev', name: string): CompanionActionDefinition => ({
		name,
		description:
			verb === 'prev'
				? 'The previous item of the focused playlist'
				: `With EXPECT the node refuses if the next item is not the one this key showed`,
		options: [
			playlistOption,
			...(verb === 'prev'
				? []
				: [{ type: 'checkbox', id: 'expect', label: 'EXPECT the next item', default: true } as const]),
		],
		hasResult: true,
		callback: async (action) => {
			const p = focusedOr(action.options)
			if (!p) return ctx.receipts.notSent(action.controlId, name, 'no playlist to send it to')
			// EXPECT names the item this very press plays, as the node picks it; a press that would play none sends none.
			const target = verb === 'go' ? p.nextId : verb === 'next' ? p.stepId : null
			const expect = action.options.expect !== false ? target : null
			const line =
				verb === 'go' ? Lines.go(p.id, expect) : verb === 'next' ? Lines.next(p.id, expect) : Lines.prev(p.id)
			return ctx.send(line, action.controlId, { goPlaylist: p.id })
		},
	})
	defs.go_focused = transport('go', 'GO (focused playlist)')
	defs.next_focused = transport('next', 'NEXT (focused playlist)')
	defs.prev_focused = transport('prev', 'PREV (focused playlist)')
	defs.panic = {
		name: 'PANIC',
		description: 'Each output to its planned outcome — never confirmed, never held',
		options: [
			{
				type: 'dropdown',
				id: 'outcome',
				label: 'Outcome',
				default: '',
				choices: PANIC_OUTCOMES.map((o) => ({ id: o, label: o || "the show's default" })),
			},
		],
		hasResult: true,
		callback: async (action) => ctx.send(Lines.panic(text(action.options.outcome)), action.controlId),
	}
	defs.blackout_toggle = {
		name: 'Blackout: toggle',
		options: [],
		hasResult: true,
		callback: async (action) => ctx.send(Lines.blackoutToggle, action.controlId),
	}
	defs.freeze_toggle = {
		name: 'Freeze: toggle',
		options: [],
		hasResult: true,
		callback: async (action) => ctx.send(Lines.freezeToggle, action.controlId),
	}
	defs.slot_press = {
		name: 'Live: press a slot',
		description:
			'Sends the slot’s line as its press says: an instant one at once, a held one only held, one pressed twice on the second held press within 3 s; a short press of a held slot says HOLD',
		options: [
			...liveSlotOptions(),
			{ type: 'checkbox', id: 'held', label: 'Held past 800 ms', default: false },
			{
				type: 'dropdown',
				id: 'phase',
				label: 'Part of the press (the Live slot preset sets it)',
				default: 'press',
				choices: [
					{ id: 'press', label: 'The press (one action on a plain key)' },
					{ id: 'down', label: 'Down: an instant slot sends' },
					{ id: 'up', label: 'Up: a held slot says HOLD' },
					{ id: 'hold800', label: 'Held 800 ms: a held slot sends' },
				],
			},
		],
		hasResult: true,
		callback: async (action) => {
			const slot = slotOf(ctx, action.options)
			const phase = text(action.options.phase) || 'press'
			const instant = slot?.press === 'instant'
			// Each part of the preset's press does one thing, so an instant slot is sent once, on the down. A part that does nothing
			// for this slot answers the control's last result unchanged, so what the preset stores from each part stays the press's.
			if ((phase === 'down' && !instant) || ((phase === 'up' || phase === 'hold800') && instant))
				return (
					ctx.slotResults.get(action.controlId) ??
					notSentResult(instant ? 'an instant slot is sent on the down' : 'a held slot is sent when held')
				)
			const pressed = async (): Promise<PressResult> => {
				if (!slot?.line) return notSentResult('this slot sends nothing')
				const held = phase === 'hold800' || (phase === 'press' && action.options.held === true)
				if (!instant && !held) {
					const rule = ctx.live.source(action.options.rule)
					const n = num(Number(action.options.n), 0)
					ctx.live.hint(rule, n, () => ctx.checkFeedbacks('slot_hint'))
					ctx.checkFeedbacks('slot_hint')
					return notSentResult('hold this key to send it')
				}
				if (slot.press === 'double' && !ctx.confirms.press(action.controlId, slot.line)) return ARMED
				const go = slot.line.startsWith('PLAYLIST GO ') ? (slot.line.split(' ')[2] ?? '') : ''
				return ctx.send(slot.line, action.controlId, go ? { goPlaylist: go } : {})
			}
			const result = await pressed()
			ctx.slotResults.set(action.controlId, result)
			return result
		},
	}
	defs.live_source = {
		name: 'Live: the connection’s source',
		options: [
			{
				type: 'dropdown',
				id: 'rule',
				label: 'Source',
				default: 'focused',
				allowCustom: true,
				choices: RULES.map((r) => ({ id: r, label: r })),
			},
		],
		callback: (action) => ctx.setLiveRule(text(action.options.rule)),
	}
	const page = (by: number): CompanionActionDefinition => ({
		name: by > 0 ? 'Live: next page' : 'Live: previous page',
		options: [liveSlotOptions()[0] as SomeCompanionActionInputField],
		callback: (action) => {
			ctx.live.turn(ctx.live.source(action.options.rule), by, ctx.model, ctx.nodeNow(), (l) => ctx.pressOf(l))
			ctx.liveChanged()
		},
	})
	defs.live_page_next = page(1)
	defs.live_page_prev = page(-1)
	defs.live_refresh = {
		name: 'Live: lay the page out again',
		description: 'While armed the slots stay where they were; this lays them out from the show as it is now',
		options: [],
		callback: () => {
			ctx.live.refresh()
			ctx.liveChanged()
		},
	}
	defs.section_press = {
		name: "A package section's key",
		description: 'The line a section of INDEX gives its key, for its row',
		options: [
			{ type: 'textinput', id: 'section', label: 'Section', default: '' },
			{ type: 'textinput', id: 'row', label: 'Row id', default: '' },
			{ type: 'number', id: 'key', label: 'Key (0-based)', default: 0, min: 0, max: 32 },
			CONFIRM,
		],
		hasResult: true,
		callback: async (action) => {
			const section = ctx.model.index?.sections.find((s) => s.section === text(action.options.section))
			const row = section?.rows.find((r) => r.id === text(action.options.row))
			const key = section?.keys[num(Number(action.options.key), 0)]
			if (!section || !row || !key)
				return ctx.receipts.notSent(action.controlId, 'section', 'the node no longer lists this key')
			if (!pressable(row))
				return ctx.receipts.notSent(action.controlId, 'section', `${row.label} is turned with a value, never pressed`)
			const line = fillHint(key.line, row)
			if (action.options.confirm === 'twice' && !ctx.confirms.press(action.controlId, line)) return ARMED
			return ctx.send(line, action.controlId)
		},
	}
	defs.ask_node = {
		name: 'Ask the node (a query line)',
		description:
			'Its answer is the action’s result (store it in a variable); only questions — a press is a line action',
		options: [{ type: 'textinput', id: 'line', label: 'Line (PLAYLIST LIST, FLEET …)', default: 'PLAYLIST LIST' }],
		hasResult: true,
		callback: async (action) => {
			const line = text(action.options.line)
			const row = rowOfLine(ctx.verbs, line)
			const refused = (error: string): JsonValue => ({ ok: false, error })
			// A refusal names only the row, never the words typed as the line (a token pasted there).
			if (!row) return refused('no verb of the node starts this line')
			if (row.mutates || !fence(ctx.verbs, line).ok) return refused(`${row.name} is not a question this action asks`)
			try {
				const reply = await ctx.ask(line)
				let value: JsonValue = reply.payload
				try {
					value = parseNode(reply.payload) as JsonValue
				} catch {
					value = reply.payload // words, not JSON: the result is the words
				}
				const answered: JsonValue = { ok: reply.ok, reply: value }
				return answered
			} catch (e) {
				return refused(e instanceof Error ? e.message : String(e))
			}
		},
	}
	defs.raw = {
		name: 'Send a line',
		description:
			'Any line of the vocabulary (session words and secrets are refused); its receipt and result like any press',
		options: [{ type: 'textinput', id: 'line', label: 'Line', default: '' }],
		hasResult: true,
		callback: async (action) => {
			return ctx.send(text(action.options.line), action.controlId)
		},
	}
	defs.enc_master = encoder(ctx, 'Encoder: grand master', 'MASTER, 0-100', [], 5, (_o, step) => ({
		key: 'master',
		target: {
			value: () => (ctx.model.state ? Math.round(num(at(ctx.model.state, P.masterTo), 1) * 100) : null),
			min: 0,
			max: 100,
			step,
			absolute: true,
			write: Lines.master,
		},
	}))
	defs.enc_rate = encoder(
		ctx,
		'Encoder: playback rate',
		'VIDEO RATE on a playlist, 0.05-20',
		[playlistOption],
		0.05,
		(o, step) => {
			const p = focusedOr(o)
			if (!p) return null
			return {
				key: `rate:${p.id}`,
				target: {
					value: () => ctx.model.playlist(p.id, ctx.nodeNow())?.rate ?? null,
					min: 0.05,
					max: 20,
					step,
					absolute: true,
					write: (r) => Lines.rate(p.id, r),
				},
			}
		},
	)
	defs.enc_seek = encoder(
		ctx,
		'Encoder: seek',
		'VIDEO SEEK on a playlist, in seconds',
		[playlistOption],
		1,
		(o, step) => {
			const p = focusedOr(o)
			if (!p || p.lengthMs === null) return null
			return {
				// A seek's target is its item's: one sent before a GO never steers the next item's.
				key: `seek:${p.id}:${p.itemId ?? ''}`,
				target: {
					value: () => {
						const now = ctx.model.playlist(p.id, ctx.nodeNow())
						return now?.positionMs != null ? Math.round(now.positionMs / 100) / 10 : null
					},
					min: 0,
					max: p.lengthMs / 1000,
					step,
					absolute: true,
					moving: true,
					write: (s) => Lines.seek(p.id, s),
				},
			}
		},
	)
	defs.enc_generator_param = encoder(
		ctx,
		'Encoder: a generator parameter',
		'GENERATOR SET … BY a step',
		[
			{
				type: 'dropdown',
				id: 'generator',
				label: 'Generator',
				default: '',
				allowCustom: true,
				choices: ctx.choices('gn'),
			},
			{ type: 'textinput', id: 'param', label: 'Parameter', default: 'level' },
		],
		1,
		(o, step) => {
			const generator = text(o.generator)
			const param = text(o.param)
			if (!generator || !param) return null
			return {
				key: `gen:${generator}:${param}`,
				target: {
					value: () => null,
					min: -Infinity,
					max: Infinity,
					step,
					absolute: false,
					write: (by) => Lines.generatorBy(generator, param, by),
				},
			}
		},
	)
	defs.enc_lens = encoder(
		ctx,
		'Encoder: a lens motor',
		'DEVICE LENS … BY a step',
		[
			{ type: 'dropdown', id: 'device', label: 'Device', default: '', allowCustom: true, choices: ctx.choices('dv') },
			{
				type: 'dropdown',
				id: 'motor',
				label: 'Motor',
				default: 'ZOOM',
				choices: ['SHIFTH', 'SHIFTV', 'ZOOM', 'FOCUS'].map((m) => ({ id: m, label: m })),
			},
		],
		1,
		(o, step) => {
			const device = text(o.device)
			const motor = text(o.motor) || 'ZOOM'
			if (!device) return null
			return {
				key: `lens:${device}:${motor}`,
				target: {
					value: () => null,
					min: -Infinity,
					max: Infinity,
					step,
					absolute: false,
					write: (by) => Lines.lensBy(device, motor, by),
				},
			}
		},
	)
	defs.enc_surface_opacity = encoder(
		ctx,
		'Encoder: a surface’s opacity',
		'NUDGE <surface> opacity by a step (the node clamps it to 0-1 and says so at an end)',
		[{ type: 'dropdown', id: 'surface', label: 'Surface', default: '', allowCustom: true, choices: ctx.choices('sf') }],
		0.05,
		(o, step) => {
			const surface = text(o.surface)
			if (!surface) return null
			return {
				key: `opacity:${surface}`,
				target: {
					value: () => null,
					min: -Infinity,
					max: Infinity,
					step,
					absolute: false,
					write: (by) => Lines.nudge(surface, 'opacity', by),
				},
			}
		},
	)
	defs.enc_live = {
		name: 'Encoder: Live page',
		options: [liveSlotOptions()[0] as SomeCompanionActionInputField, DELTA],
		callback: (action) => {
			const d = deltaOf(action.options)
			if (d === 0) return
			ctx.live.turn(ctx.live.source(action.options.rule), Math.sign(d), ctx.model, ctx.nodeNow(), (l) => ctx.pressOf(l))
			ctx.liveChanged()
		},
	}
	return defs
}
