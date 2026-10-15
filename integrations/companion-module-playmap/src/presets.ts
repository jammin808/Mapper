import {
	ButtonGraphicsDecorationType,
	type CompanionButtonStepActions,
	type CompanionGraphicsCompositeElementDefinitions,
	type CompanionOptionValues,
	type CompanionPresetDefinitions,
	type CompanionPresetLocalVariable,
	type CompanionPresetSection,
	type CompanionPresetGroup,
	type CompanionLayeredButtonPresetDefinition,
	type CompanionSimplePresetDefinition,
	type SomeButtonGraphicsElement,
	type SomePresetActionEntry,
	type SomePresetLayeredFeedbackEntry,
	type SomePresetSimpleFeedbackEntry,
} from '@companion-module/base'
import { PANIC_OUTCOMES } from './actions.ts'
import { actionIdOf } from './catalog/verbs.ts'
import { groupOn, type PlayMapConfig } from './config.ts'
import { RULES, SLOTS, fillHint } from './live.ts'
import { colourOf, css, inkOn, lightExpression, rgbOf } from './palette/playmap.ts'
import type { IndexDoc } from './state/index-doc.ts'

type PresetAction = SomePresetActionEntry
type ExprValue = { isExpression: true; value: string }

const expr = (value: string): ExprValue => ({ isExpression: true, value })
const local = (name: string): ExprValue => expr(`$(local:${name})`)
const THIS_VALUE = expr('$(this:value)')

/** How a key is pressed: at once on the down; held 0.8 s (the action runs while held); or held twice within 3 s. */
export type PressShape = 'instant' | 'hold' | 'double'

/** One action as a key's steps for its press shape. A hold carries `confirm: 'twice'` on a double press when the action has it. */
export function stepsFor(press: PressShape, action: PresetAction): CompanionButtonStepActions[] {
	if (press === 'instant') return [{ down: [action], up: [] }]
	const held =
		press === 'double' && 'options' in action
			? ({ ...action, options: { ...(action.options as CompanionOptionValues), confirm: 'twice' } } as PresetAction)
			: action
	return [{ down: [], up: [], 800: { options: { runWhileHeld: true }, actions: [held] } }]
}

/** What one key shows and does, before it is drawn as a layered key and a simple fallback. */
interface KeySpec {
	readonly name: string
	readonly notes: string
	readonly keywords: readonly string[]
	readonly locals: readonly { readonly name: string; readonly startup: string | number }[]
	/** A fixed label, or a value feedback giving it. */
	readonly label: string | { readonly feedbackId: string; readonly options: CompanionOptionValues }
	readonly sub?: { readonly feedbackId: string; readonly options: CompanionOptionValues }
	readonly light?: { readonly feedbackId: string; readonly options: CompanionOptionValues }
	readonly thumb?: { readonly feedbackId: string; readonly options: CompanionOptionValues }
	readonly gauge?: {
		readonly feedbackId: string
		readonly options: CompanionOptionValues
		readonly ring: boolean
		readonly max?: number
	}
	/** Boolean feedbacks that colour the ground (the palette's name). */
	readonly grounds?: readonly {
		readonly feedbackId: string
		readonly options: CompanionOptionValues
		readonly colour: string
	}[]
	readonly ground?: string
	readonly press: PressShape
	readonly action?: PresetAction
	readonly steps?: CompanionButtonStepActions[]
	readonly receipt: boolean
	/** A verb whose Show-Mode lock the key wears (WHOAMI's may list). */
	readonly lockVerb?: string
	/** A Live slot: empty dims it, a short press on a held slot says HOLD. */
	readonly slot?: CompanionOptionValues
}

const ROOT = 'root'

function layered(spec: KeySpec): CompanionLayeredButtonPresetDefinition {
	const ground = spec.ground ?? 'dark'
	const children: SomeButtonGraphicsElement[] = [
		{
			type: 'box',
			id: 'frame',
			x: 0,
			y: 0,
			width: 100,
			height: 100,
			color: css(ground),
			borderWidth: 6,
			borderColor: css(ground),
			borderPosition: 'inside',
			cornerRadius: 6,
		},
	]
	if (spec.thumb)
		children.push({ type: 'image', id: 'thumb', x: 6, y: 6, width: 88, height: 56, base64Image: '', fillMode: 'fit' })
	if (spec.gauge)
		children.push(
			spec.gauge.ring
				? {
						type: 'gauge',
						id: 'gauge',
						x: 8,
						y: 8,
						width: 84,
						height: 84,
						orientation: 'ring',
						value: 0,
						min: 0,
						max: spec.gauge.max ?? 1,
						ringWidth: 8,
					}
				: {
						type: 'gauge',
						id: 'gauge',
						x: 6,
						y: 90,
						width: 88,
						height: 6,
						orientation: 'horizontal',
						value: 0,
						min: 0,
						max: spec.gauge.max ?? 1,
					},
		)
	children.push({
		type: 'text',
		id: 'label',
		x: 4,
		y: spec.thumb ? 62 : spec.sub ? 18 : 10,
		width: 92,
		height: spec.thumb ? 26 : spec.sub ? 46 : 78,
		text: typeof spec.label === 'string' ? spec.label : '',
		fontsizeAllowShrink: true,
		color: inkOn(ground),
		halign: 'center',
		valign: 'center',
		weight: 'bold',
	})
	if (spec.sub)
		children.push({
			type: 'text',
			id: 'sub',
			x: 4,
			y: 64,
			width: 92,
			height: 22,
			text: '',
			fontsizeAllowShrink: true,
			color: css('white'),
			halign: 'center',
			valign: 'center',
		})
	if (spec.receipt)
		children.push({
			type: 'circle',
			id: 'receipt',
			x: 80,
			y: 4,
			width: 14,
			height: 14,
			color: css('amber'),
			enabled: false,
		})
	if (spec.lockVerb)
		children.push({
			type: 'composite',
			id: 'lock',
			elementId: 'pm_lock',
			x: 2,
			y: 2,
			width: 22,
			height: 22,
			options: {},
			enabled: false,
		})
	if (spec.press !== 'instant')
		children.push({
			type: 'text',
			id: 'hold',
			x: 0,
			y: 0,
			width: 100,
			height: 26,
			text: spec.press === 'double' ? 'SURE?' : 'HOLD…',
			color: css('amber'),
			halign: 'center',
			valign: 'top',
			enabled: false,
		})
	if (spec.slot)
		children.push({
			type: 'text',
			id: 'hint',
			x: 0,
			y: 0,
			width: 100,
			height: 26,
			text: 'HOLD',
			color: css('amber'),
			halign: 'center',
			valign: 'top',
			enabled: false,
		})
	children.push({
		type: 'text',
		id: 'mark',
		x: 70,
		y: 70,
		width: 30,
		height: 30,
		text: '⚠',
		color: css('amber'),
		halign: 'right',
		valign: 'bottom',
		enabled: false,
	})

	const feedbacks: SomePresetLayeredFeedbackEntry[] = []
	const override = (
		elementId: string,
		elementProperty: string,
		value: ExprValue | string | number | boolean,
	): { elementId: string; elementProperty: string; override: ExprValue | string | number | boolean } => ({
		elementId,
		elementProperty,
		override: value,
	})
	if (typeof spec.label !== 'string')
		feedbacks.push({
			feedbackId: spec.label.feedbackId,
			options: spec.label.options,
			styleOverrides: [override('label', 'text', THIS_VALUE)],
		})
	if (spec.sub)
		feedbacks.push({
			feedbackId: spec.sub.feedbackId,
			options: spec.sub.options,
			styleOverrides: [override('sub', 'text', THIS_VALUE)],
		})
	if (spec.thumb)
		feedbacks.push({
			feedbackId: spec.thumb.feedbackId,
			options: spec.thumb.options,
			styleOverrides: [override('thumb', 'base64Image', THIS_VALUE)],
		})
	if (spec.gauge)
		feedbacks.push({
			feedbackId: spec.gauge.feedbackId,
			options: spec.gauge.options,
			styleOverrides: [override('gauge', 'value', THIS_VALUE)],
		})
	if (spec.light)
		feedbacks.push({
			feedbackId: spec.light.feedbackId,
			options: spec.light.options,
			styleOverrides: [override('frame', 'borderColor', expr(lightExpression()))],
		})
	for (const g of spec.grounds ?? [])
		feedbacks.push({
			feedbackId: g.feedbackId,
			options: g.options,
			styleOverrides: [override('frame', 'color', css(g.colour)), override('label', 'color', inkOn(g.colour))],
		})
	if (spec.receipt) {
		feedbacks.push({
			feedbackId: 'receipt',
			options: { stage: 'pending' },
			styleOverrides: [override('receipt', 'enabled', true), override('receipt', 'color', css('amber'))],
		})
		feedbacks.push({
			feedbackId: 'receipt',
			options: { stage: 'failed' },
			styleOverrides: [override('receipt', 'enabled', true), override('receipt', 'color', css('red'))],
		})
	}
	if (spec.lockVerb)
		feedbacks.push({
			feedbackId: 'verb_locked',
			options: { verb: spec.lockVerb },
			styleOverrides: [override('lock', 'enabled', true)],
		})
	if (spec.press === 'hold')
		feedbacks.push({
			feedbackId: 'internal:buttonPushed',
			options: {},
			styleOverrides: [override('hold', 'enabled', true)],
		})
	if (spec.press === 'double')
		feedbacks.push({ feedbackId: 'confirm_pending', options: {}, styleOverrides: [override('hold', 'enabled', true)] })
	if (spec.slot) {
		feedbacks.push({ feedbackId: 'slot_empty', options: spec.slot, styleOverrides: [override(ROOT, 'opacity', 25)] })
		feedbacks.push({ feedbackId: 'slot_hint', options: spec.slot, styleOverrides: [override('hint', 'enabled', true)] })
	}
	// Last, so it wins (the lowest feedback in the list wins): a key the node no longer answers is dimmed with a mark.
	feedbacks.push({
		feedbackId: 'stale',
		options: {},
		styleOverrides: [override(ROOT, 'opacity', 35), override('mark', 'enabled', true)],
	})

	return {
		type: 'layered',
		name: spec.name,
		notes: spec.notes,
		keywords: [...spec.keywords],
		canvas: { decoration: ButtonGraphicsDecorationType.None },
		elements: [{ type: 'group', id: ROOT, x: 0, y: 0, width: 100, height: 100, children }],
		feedbacks,
		steps: spec.steps ?? (spec.action ? stepsFor(spec.press, spec.action) : [{ down: [], up: [] }]),
		localVariables: localsOf(spec),
	}
}

function localsOf(spec: KeySpec): CompanionPresetLocalVariable[] {
	return spec.locals.map((l) => ({ variableType: 'simple', variableName: l.name, startupValue: l.startup }))
}

/** The same key for hosts that cannot draw layered keys: its label through a feedback-based local variable. */
function simple(spec: KeySpec): CompanionSimplePresetDefinition {
	const locals = localsOf(spec)
	let text = typeof spec.label === 'string' ? spec.label : '$(local:label)'
	if (typeof spec.label !== 'string')
		locals.push({
			variableType: 'feedback',
			variableName: 'label',
			feedbackId: spec.label.feedbackId,
			options: spec.label.options,
		})
	if (spec.press !== 'instant') text = text + (spec.press === 'double' ? ' (×2)' : ' (hold)')
	const feedbacks: SomePresetSimpleFeedbackEntry[] = []
	for (const g of spec.grounds ?? [])
		feedbacks.push({
			feedbackId: g.feedbackId,
			options: g.options,
			style: { bgcolor: rgbOf(g.colour), color: rgbOf(g.colour === 'amber' ? 'ink' : 'white') },
		})
	if (spec.receipt)
		feedbacks.push({ feedbackId: 'receipt', options: { stage: 'failed' }, style: { bgcolor: rgbOf('red') } })
	if (spec.press === 'double')
		feedbacks.push({
			feedbackId: 'confirm_pending',
			options: {},
			style: { bgcolor: rgbOf('amber'), color: rgbOf('ink'), text: 'SURE?' },
		})
	if (spec.slot)
		feedbacks.push({
			feedbackId: 'slot_empty',
			options: spec.slot,
			style: { bgcolor: rgbOf('dim'), color: rgbOf('dimText') },
		})
	feedbacks.push({ feedbackId: 'stale', options: {}, style: { bgcolor: rgbOf('dim'), color: rgbOf('dimText') } })
	return {
		type: 'simple',
		name: spec.name,
		notes: spec.notes,
		keywords: [...spec.keywords],
		style: { text, size: 'auto', color: rgbOf('white'), bgcolor: rgbOf(spec.ground ?? 'dark') },
		feedbacks,
		steps: spec.steps ?? (spec.action ? stepsFor(spec.press, spec.action) : [{ down: [], up: [] }]),
		localVariables: locals,
	}
}

/** A key as Companion takes it: the layered key first, the simple one for hosts that cannot draw it. */
function key(spec: KeySpec): CompanionPresetDefinitions[string] {
	return { type: 'alternatives', variants: [layered(spec), simple(spec)] }
}

/** The preset definitions: fixed for a config — they never change with the project, so linked keys are not rebuilt by a show edit
 * (only the structure's template values and groups move, and those are keyed by entity id). */
export function presetDefinitions(config: PlayMapConfig): CompanionPresetDefinitions {
	const thumbSize = Math.min(144, Math.max(72, Math.round(Number(config.thumb_size ?? 96)) || 96))
	const presets: CompanionPresetDefinitions = {}
	const focusField = (field: string): { feedbackId: string; options: CompanionOptionValues } => ({
		feedbackId: 'focus',
		options: { field },
	})

	presets.show_go = key({
		name: 'GO',
		notes:
			'PLAYLIST GO on the focused playlist with EXPECT its next item (refused if the next item moved). Instant — never held. The ring is the current item; the label and picture are the next.',
		keywords: ['go', 'next', 'transport', 'playlist'],
		locals: [],
		label: focusField('next'),
		thumb: focusField('next_thumb'),
		gauge: { ...focusField('progress'), ring: true },
		grounds: [{ feedbackId: 'go_cooldown', options: { playlist: '' }, colour: 'amber' }],
		ground: 'green',
		press: 'instant',
		action: { actionId: 'go_focused', options: { playlist: '', expect: true } },
		receipt: true,
		lockVerb: 'PLAYLIST GO',
	})
	presets.show_next = key({
		name: 'NEXT',
		notes: 'PLAYLIST NEXT on the focused playlist with EXPECT. Instant.',
		keywords: ['next', 'transport'],
		locals: [],
		label: 'NEXT',
		press: 'instant',
		action: { actionId: 'next_focused', options: { playlist: '', expect: true } },
		receipt: true,
		lockVerb: 'PLAYLIST NEXT',
	})
	presets.show_prev = key({
		name: 'PREV',
		notes: 'PLAYLIST PREV on the focused playlist. Instant.',
		keywords: ['prev', 'previous', 'transport'],
		locals: [],
		label: 'PREV',
		press: 'instant',
		action: { actionId: 'prev_focused', options: { playlist: '' } },
		receipt: true,
		lockVerb: 'PLAYLIST PREV',
	})
	presets.show_blackout = key({
		name: 'BLACKOUT',
		notes: 'BLACKOUT TOGGLE — every output to black and back. Instant; lit from STATE, never from the press.',
		keywords: ['blackout', 'black', 'air'],
		locals: [],
		label: 'BLACK OUT',
		grounds: [{ feedbackId: 'blackout', options: {}, colour: colourOf('transport', 'blackout') }],
		press: 'instant',
		action: { actionId: 'blackout_toggle', options: {} },
		receipt: true,
	})
	presets.show_freeze = key({
		name: 'FREEZE',
		notes: 'FREEZE TOGGLE — every output holds its frame. Instant.',
		keywords: ['freeze', 'hold', 'air'],
		locals: [],
		label: 'FREEZE',
		grounds: [{ feedbackId: 'frozen', options: {}, colour: colourOf('transport', 'frozen') }],
		press: 'instant',
		action: { actionId: 'freeze_toggle', options: {} },
		receipt: true,
	})
	for (const outcome of PANIC_OUTCOMES)
		presets[outcome ? `show_panic_${outcome.toLowerCase().replace(/[^a-z]+/g, '_')}` : 'show_panic'] = key({
			name: outcome ? `PANIC ${outcome}` : 'PANIC',
			notes: `PANIC ${outcome || "(the show's planned outcome)"} — never confirmed, never held, never refused for an operator.`,
			keywords: ['panic', 'emergency', outcome.toLowerCase()].filter((k) => k),
			locals: [],
			label: outcome ? `PANIC\n${outcome}` : 'PANIC',
			ground: 'red',
			grounds: [{ feedbackId: 'panic_active', options: {}, colour: colourOf('show', 'panic') }],
			press: 'instant',
			action: { actionId: 'panic', options: { outcome } },
			receipt: true,
		})
	presets.show_panic_clear = key({
		name: 'PANIC CLEAR',
		notes: 'PANIC CLEAR — brings the air back from a panic over its fade. Instant.',
		keywords: ['panic', 'clear'],
		locals: [],
		label: 'PANIC\nCLEAR',
		grounds: [{ feedbackId: 'panic_active', options: {}, colour: 'amber' }],
		press: 'instant',
		action: { actionId: actionIdOf('PANIC CLEAR'), options: {} },
		receipt: true,
	})
	presets.show_countdown = key({
		name: 'Countdown',
		notes: 'The focused item’s time left, running between pushes (10 Hz), with its progress.',
		keywords: ['countdown', 'remaining', 'clock', 'timer'],
		locals: [],
		label: focusField('remaining'),
		sub: focusField('current'),
		gauge: { ...focusField('progress'), ring: false },
		press: 'instant',
		receipt: false,
	})
	presets.show_mode = key({
		name: 'Show Mode',
		notes: 'Armed (red) or rehearsal — read from the node, never guessed.',
		keywords: ['show', 'armed', 'rehearsal', 'mode'],
		locals: [],
		label: { feedbackId: 'state_path', options: { path: 'show.armed' } },
		grounds: [{ feedbackId: 'show_armed', options: {}, colour: colourOf('show', 'armed') }],
		press: 'instant',
		receipt: false,
	})
	presets.show_headline = key({
		name: 'Node headline',
		notes: "The node's headline, its light on the frame.",
		keywords: ['headline', 'health', 'light'],
		locals: [],
		label: { feedbackId: 'state_path', options: { path: 'headline' } },
		grounds: [
			{ feedbackId: 'node_light', options: { light: 'amber' }, colour: 'amber' },
			{ feedbackId: 'node_light', options: { light: 'red' }, colour: 'red' },
		],
		press: 'instant',
		receipt: false,
	})

	presets.item_key = key({
		name: 'Item',
		notes:
			'PLAYLIST GO <playlist> <item> — linked by id, so a rename or a reorder never orphans it. Frame: on air green, next amber, gone grey.',
		keywords: ['item', 'clip', 'go', 'playlist'],
		locals: [
			{ name: 'item', startup: '' },
			{ name: 'playlist', startup: '' },
		],
		label: { feedbackId: 'label', options: { ref: local('item') } },
		thumb: { feedbackId: 'thumb', options: { ref: local('item'), size: thumbSize } },
		light: { feedbackId: 'light', options: { ref: local('item') } },
		gauge: { feedbackId: 'progress', options: { playlist: local('playlist'), item: local('item') }, ring: false },
		press: 'instant',
		action: { actionId: actionIdOf('PLAYLIST GO'), options: { playlist: local('playlist'), item: local('item') } },
		receipt: true,
		lockVerb: 'PLAYLIST GO',
	})
	presets.playlist_key = key({
		name: 'Playlist',
		notes: 'GO on this playlist with EXPECT its next item. Instant.',
		keywords: ['playlist', 'go'],
		locals: [{ name: 'playlist', startup: '' }],
		label: { feedbackId: 'label', options: { ref: local('playlist') } },
		thumb: { feedbackId: 'thumb', options: { ref: local('playlist'), size: thumbSize } },
		light: { feedbackId: 'light', options: { ref: local('playlist') } },
		gauge: { feedbackId: 'progress', options: { playlist: local('playlist'), item: '' }, ring: false },
		press: 'instant',
		action: { actionId: 'go_focused', options: { playlist: local('playlist'), expect: true } },
		receipt: true,
		lockVerb: 'PLAYLIST GO',
	})
	presets.output_key = key({
		name: 'Output',
		notes: 'OUTPUT IDENTIFY — its name over its picture. Frame: the output’s light.',
		keywords: ['output', 'identify'],
		locals: [{ name: 'output', startup: '' }],
		label: { feedbackId: 'label', options: { ref: local('output') } },
		sub: { feedbackId: 'state_word', options: { ref: local('output') } },
		light: { feedbackId: 'light', options: { ref: local('output') } },
		press: 'instant',
		action: { actionId: actionIdOf('OUTPUT IDENTIFY'), options: { output: local('output') } },
		receipt: true,
	})
	presets.projector_key = key({
		name: 'Projector',
		notes: 'A projector’s calibration: its light and score ring (0-100).',
		keywords: ['projector', 'calibration', 'score'],
		locals: [{ name: 'projector', startup: '' }],
		label: { feedbackId: 'label', options: { ref: local('projector') } },
		sub: { feedbackId: 'state_word', options: { ref: local('projector') } },
		light: { feedbackId: 'light', options: { ref: local('projector') } },
		gauge: { feedbackId: 'score', options: { projector: local('projector') }, ring: true, max: 100 },
		press: 'instant',
		receipt: false,
	})
	for (const [id, state] of [
		['surface_show', 'ON'],
		['surface_hide', 'OFF'],
	] as const)
		presets[id] = key({
			name: state === 'ON' ? 'Show surface' : 'Hide surface',
			notes: `SURFACE SHOW <surface> ${state} — held 0.8 s (it changes the air).`,
			keywords: ['surface', state === 'ON' ? 'show' : 'hide'],
			locals: [{ name: 'surface', startup: '' }],
			label: { feedbackId: 'label', options: { ref: local('surface') } },
			ground: state === 'ON' ? colourOf('surface', 'shown') : 'dark',
			press: 'hold',
			action: { actionId: actionIdOf('SURFACE SHOW'), options: { surface: local('surface'), state } },
			receipt: true,
			lockVerb: 'SURFACE SHOW',
		})
	presets.peer_key = key({
		name: 'Machine',
		notes: 'A machine this node hears: its light and headline.',
		keywords: ['fleet', 'peer', 'machine', 'health'],
		locals: [{ name: 'peer', startup: '' }],
		label: { feedbackId: 'label', options: { ref: local('peer') } },
		sub: { feedbackId: 'state_word', options: { ref: local('peer') } },
		light: { feedbackId: 'light', options: { ref: local('peer') } },
		press: 'instant',
		receipt: false,
	})
	presets.generator_param_key = key({
		name: 'Generator parameter',
		notes: 'An encoder on a generator’s parameter: GENERATOR SET … BY the turn.',
		keywords: ['generator', 'content', 'encoder', 'parameter'],
		locals: [
			{ name: 'param', startup: '' },
			{ name: 'generator', startup: '' },
		],
		label: { feedbackId: 'label', options: { ref: local('generator') } },
		sub: { feedbackId: 'echo', options: { text: local('param') } },
		press: 'instant',
		steps: encoderSteps('enc_generator_param', { generator: local('generator'), param: local('param'), step: '1' }),
		receipt: true,
	})

	const slot = { rule: expr('$(page:pm_live)'), n: local('n') }
	presets.live_slot = key({
		name: 'Live slot',
		notes:
			'One of the Live page’s 32 slots: set the page variable pm_live (focused, playlists, outputs, projectors, surfaces, peers, faults, playlist:<id>, section:<name>) on its page. Frozen in place while the show is armed.',
		keywords: ['live', 'slot', 'page', 'auto'],
		locals: [{ name: 'n', startup: 1 }],
		label: { feedbackId: 'slot', options: { ...slot, field: 'label' } },
		sub: { feedbackId: 'slot', options: { ...slot, field: 'sub' } },
		thumb: { feedbackId: 'slot', options: { ...slot, field: 'thumb' } },
		light: { feedbackId: 'slot', options: { ...slot, field: 'light' } },
		press: 'instant',
		steps: [
			{
				down: [],
				up: [{ actionId: 'slot_press', options: { ...slot, held: false } }],
				800: {
					options: { runWhileHeld: true },
					actions: [{ actionId: 'slot_press', options: { ...slot, held: true } }],
				},
			},
		],
		receipt: true,
		slot,
	})
	const liveNav = (name: string, actionId: string, label: string): void => {
		presets[name] = key({
			name: label,
			notes: `The Live page: ${label.toLowerCase()}.`,
			keywords: ['live', 'page'],
			locals: [],
			label,
			press: 'instant',
			action: { actionId, options: actionId === 'live_refresh' ? {} : { rule: expr('$(page:pm_live)') } },
			receipt: false,
		})
	}
	liveNav('live_prev_key', 'live_page_prev', 'PAGE −')
	liveNav('live_next_key', 'live_page_next', 'PAGE +')
	liveNav('live_refresh_key', 'live_refresh', 'RE-LAY')
	presets.live_source_key = key({
		name: 'Live source',
		notes: "Sets the connection's Live source (pages with pm_live keep their own).",
		keywords: ['live', 'source'],
		locals: [{ name: 'rule', startup: 'focused' }],
		label: { feedbackId: 'echo', options: { text: local('rule') } },
		press: 'instant',
		action: { actionId: 'live_source', options: { rule: local('rule') } },
		receipt: false,
	})

	presets.enc_master_key = key({
		name: 'Master (encoder)',
		notes: 'Turn: MASTER by 5 per detent from the value STATE shows (plus turns not yet confirmed), within 0-100.',
		keywords: ['master', 'encoder', 'dial'],
		locals: [],
		label: { feedbackId: 'state_path', options: { path: 'air.master.level' } },
		press: 'instant',
		steps: encoderSteps('enc_master', { step: '5' }),
		receipt: true,
	})
	presets.enc_rate_key = key({
		name: 'Rate (encoder)',
		notes: 'Turn: VIDEO RATE on the focused playlist by 0.05 per detent, within 0.05-20.',
		keywords: ['rate', 'speed', 'encoder'],
		locals: [],
		label: 'RATE',
		press: 'instant',
		steps: encoderSteps('enc_rate', { playlist: '', step: '0.05' }),
		receipt: true,
	})
	presets.enc_seek_key = key({
		name: 'Seek (encoder)',
		notes: 'Turn: VIDEO SEEK on the focused playlist by a second per detent, within the item.',
		keywords: ['seek', 'scrub', 'encoder'],
		locals: [],
		label: focusField('remaining'),
		gauge: { ...focusField('progress'), ring: true },
		press: 'instant',
		steps: encoderSteps('enc_seek', { playlist: '', step: '1' }),
		receipt: true,
	})
	presets.enc_live_key = key({
		name: 'Live page (encoder)',
		notes: 'Turn: the Live page’s pages.',
		keywords: ['live', 'page', 'encoder'],
		locals: [],
		label: 'LIVE',
		press: 'instant',
		steps: encoderSteps('enc_live', { rule: expr('$(page:pm_live)') }),
		receipt: false,
	})

	for (const press of ['instant', 'hold', 'double'] as const)
		presets[`section_key_${press}`] = key({
			name: 'Section key',
			notes: `A key a package's INDEX section describes (its line, its light), pressed ${press === 'instant' ? 'at once' : press === 'hold' ? 'held 0.8 s' : 'twice, held'}.`,
			keywords: ['section', 'package'],
			locals: [
				{ name: 'row', startup: '' },
				{ name: 'section', startup: '' },
				{ name: 'key', startup: 0 },
			],
			label: {
				feedbackId: 'section_label',
				options: { section: local('section'), row: local('row'), key: local('key') },
			},
			light: {
				feedbackId: 'section_value',
				options: { section: local('section'), row: local('row'), key: local('key') },
			},
			press,
			action: {
				actionId: 'section_press',
				options: { section: local('section'), row: local('row'), key: local('key') },
			},
			receipt: true,
		})
	return presets
}

/** An encoder key's steps: a press does nothing; each turn sends the turn ($(this:delta) on surfaces that give it, else ±1). */
function encoderSteps(actionId: string, options: CompanionOptionValues): CompanionButtonStepActions[] {
	return [
		{
			down: [],
			up: [],
			rotate_left: [{ actionId, options: { ...options, delta: expr('-abs($(this:delta) || 1)') } }],
			rotate_right: [{ actionId, options: { ...options, delta: expr('abs($(this:delta) || 1)') } }],
		},
	]
}

/** The facts the structure is built from beyond INDEX: the machines this node hears (by id, with their names). */
export interface StructureExtras {
	readonly peers: readonly { readonly id: string; readonly name: string }[]
}

/** The preset structure: sections of groups, every template group keyed by the entity it lists (`pl.<id>` for a playlist's items),
 * so a linked key follows its entity through renames and reorders. Built from INDEX; a group the connection does not tick is left
 * out. */
export function presetStructure(
	index: IndexDoc | null,
	config: PlayMapConfig,
	extras: StructureExtras,
): CompanionPresetSection[] {
	const sections: CompanionPresetSection[] = []
	const template = (
		id: string,
		name: string,
		presetId: string,
		variable: string,
		values: { name: string; value: string | number }[],
		common?: Record<string, string | number>,
	): CompanionPresetGroup => ({
		id,
		type: 'template',
		name,
		presetId,
		templateVariableName: variable,
		templateValues: values,
		...(common ? { commonVariableValues: common } : {}),
	})
	if (groupOn(config, 'show'))
		sections.push({
			id: 'show',
			name: 'Show',
			description: 'GO, NEXT and PREV on the focused playlist, blackout, freeze, panic, the countdown and Show Mode',
			definitions: [
				{
					id: 'show_transport',
					type: 'simple',
					name: 'Transport',
					presets: ['show_go', 'show_next', 'show_prev', 'show_countdown'],
				},
				{
					id: 'show_air',
					type: 'simple',
					name: 'Air',
					presets: ['show_blackout', 'show_freeze', 'show_mode', 'show_headline'],
				},
				{
					id: 'show_panic',
					type: 'simple',
					name: 'Panic',
					presets: [
						...PANIC_OUTCOMES.map((o) => (o ? `show_panic_${o.toLowerCase().replace(/[^a-z]+/g, '_')}` : 'show_panic')),
						'show_panic_clear',
					],
				},
			],
		})
	if (groupOn(config, 'playlists') && index)
		sections.push({
			id: 'playlists',
			name: 'Playlists',
			description: 'A key per item of each playlist, linked by id',
			definitions: [
				template(
					'playlists',
					'Playlists',
					'playlist_key',
					'playlist',
					index.playlists.map((p) => ({ name: p.name, value: p.id })),
				),
				...index.playlists.map((p) =>
					template(
						`pl.${p.id}`,
						p.name,
						'item_key',
						'item',
						p.items.map((i) => ({ name: i.label, value: i.id })),
						{ playlist: p.id },
					),
				),
			],
		})
	if (groupOn(config, 'outputs') && index && index.outputs.length > 0)
		sections.push({
			id: 'outputs',
			name: 'Outputs',
			definitions: [
				template(
					'outputs',
					'Outputs',
					'output_key',
					'output',
					index.outputs.map((o) => ({ name: o.name, value: o.id })),
				),
			],
		})
	if (groupOn(config, 'projectors') && index && index.projectors.length > 0)
		sections.push({
			id: 'projectors',
			name: 'Projectors',
			definitions: [
				template(
					'projectors',
					'Projectors',
					'projector_key',
					'projector',
					index.projectors.map((p) => ({ name: p.name, value: p.id })),
				),
			],
		})
	if (groupOn(config, 'surfaces') && index && index.surfaces.length > 0)
		sections.push({
			id: 'surfaces',
			name: 'Surfaces',
			definitions: [
				template(
					'surfaces_show',
					'Show',
					'surface_show',
					'surface',
					index.surfaces.map((s) => ({ name: s.name, value: s.id })),
				),
				template(
					'surfaces_hide',
					'Hide',
					'surface_hide',
					'surface',
					index.surfaces.map((s) => ({ name: s.name, value: s.id })),
				),
			],
		})
	if (groupOn(config, 'live'))
		sections.push({
			id: 'live',
			name: 'Live page',
			description:
				'Install the 32 slots once on a page and set its pm_live page variable: the slots fill themselves from the show',
			definitions: [
				template(
					'live',
					'Live slots',
					'live_slot',
					'n',
					Array.from({ length: SLOTS }, (_, i) => ({ name: `Slot ${i + 1}`, value: i + 1 })),
				),
				{
					id: 'live_nav',
					type: 'simple',
					name: 'Pages',
					presets: ['live_prev_key', 'live_next_key', 'live_refresh_key'],
				},
				template(
					'live_sources',
					'Sources',
					'live_source_key',
					'rule',
					RULES.map((r) => ({ name: r, value: r })),
				),
			],
		})
	if (groupOn(config, 'encoders')) {
		const generators = (index?.generators ?? []).filter((g) => g.params.some((p) => p.kind === 'number'))
		sections.push({
			id: 'encoders',
			name: 'Encoders',
			description: 'Stream Deck + and Studio dials',
			definitions: [
				{
					id: 'encoders',
					type: 'simple',
					name: 'Dials',
					presets: ['enc_master_key', 'enc_rate_key', 'enc_seek_key', 'enc_live_key'],
				},
				...generators.map((g) =>
					template(
						`gn.${g.id}`,
						g.name,
						'generator_param_key',
						'param',
						g.params.filter((p) => p.kind === 'number').map((p) => ({ name: `${g.name}: ${p.name}`, value: p.name })),
						{ generator: g.id },
					),
				),
			],
		})
	}
	if (groupOn(config, 'fleet') && extras.peers.length > 0)
		sections.push({
			id: 'fleet',
			name: 'Fleet',
			definitions: [
				template(
					'fleet',
					'Machines',
					'peer_key',
					'peer',
					extras.peers.map((p) => ({ name: p.name, value: p.id })),
				),
			],
		})
	if (groupOn(config, 'sections'))
		for (const s of index?.sections ?? []) {
			if (s.rows.length === 0 || s.keys.length === 0) continue
			sections.push({
				id: `sec.${s.section}`,
				name: s.title || s.section,
				description: `From the node's ${s.section} section`,
				definitions: s.keys.map((k, i) =>
					template(
						`sec.${s.section}.${i}`,
						fillHint(k.label, { id: '', label: k.label, light: '' }).replace('$(label)', '') || s.title,
						`section_key_${k.press === 'hold' || k.press === 'double' ? k.press : 'instant'}`,
						'row',
						s.rows.map((r) => ({
							name: fillHint(k.label, r) === r.label ? r.label : `${fillHint(k.label, r)} ${r.label}`,
							value: r.id,
						})),
						{ section: s.section, key: i },
					),
				),
			})
		}
	return sections
}

/** Composite elements a user (or a Patterns page) can put on any key: a tile, a ring, the lock and the receipt dot. */
export function compositeDefinitions(): CompanionGraphicsCompositeElementDefinitions {
	return {
		pm_tile: {
			type: 'composite',
			name: 'PlayMap tile',
			description: 'A label over a sub-line on a ground lit by a light word',
			options: [
				{ type: 'textinput', id: 'label', label: 'Label', default: '' },
				{ type: 'textinput', id: 'sub', label: 'Sub-line', default: '' },
				{ type: 'textinput', id: 'light', label: 'Light (green, amber, red, grey)', default: 'grey' },
			],
			elements: [
				{
					type: 'box',
					id: 'ground',
					x: 0,
					y: 0,
					width: 100,
					height: 100,
					color: expr(lightExpression('$(options:light)')),
				},
				{
					type: 'text',
					id: 'label',
					x: 4,
					y: 10,
					width: 92,
					height: 50,
					text: expr('$(options:label)'),
					fontsizeAllowShrink: true,
					halign: 'center',
				},
				{
					type: 'text',
					id: 'sub',
					x: 4,
					y: 64,
					width: 92,
					height: 26,
					text: expr('$(options:sub)'),
					fontsizeAllowShrink: true,
					halign: 'center',
				},
			],
		},
		pm_ring: {
			type: 'composite',
			name: 'PlayMap ring',
			description: 'A ring gauge from red to green',
			options: [
				{ type: 'number', id: 'value', label: 'Value', default: 0, min: -1000000, max: 1000000 },
				{ type: 'number', id: 'min', label: 'Min', default: 0, min: -1000000, max: 1000000 },
				{ type: 'number', id: 'max', label: 'Max', default: 100, min: -1000000, max: 1000000 },
			],
			elements: [
				{
					type: 'gauge',
					id: 'ring',
					x: 0,
					y: 0,
					width: 100,
					height: 100,
					orientation: 'ring',
					value: expr('$(options:value)'),
					min: expr('$(options:min)'),
					max: expr('$(options:max)'),
					multiColour: true,
					stops: [
						{ value: 0, color: css('red'), gradient: true },
						{ value: 1, color: css('green'), gradient: true },
					],
				},
			],
		},
		pm_lock: {
			type: 'composite',
			name: 'PlayMap lock',
			description: 'Show Mode refuses this key from this deck',
			options: [],
			elements: [
				{ type: 'box', id: 'plate', x: 0, y: 0, width: 100, height: 100, color: css('lock'), cornerRadius: 30 },
				{
					type: 'text',
					id: 'glyph',
					x: 0,
					y: 0,
					width: 100,
					height: 100,
					text: '🔒',
					halign: 'center',
					valign: 'center',
				},
			],
		},
		pm_receipt: {
			type: 'composite',
			name: 'PlayMap receipt dot',
			description: 'Amber while a press waits for its effect, red when refused',
			options: [{ type: 'textinput', id: 'stage', label: 'Stage (pending, failed)', default: 'pending' }],
			elements: [
				{
					type: 'circle',
					id: 'dot',
					x: 0,
					y: 0,
					width: 100,
					height: 100,
					color: expr(`$(options:stage) == 'failed' ? '${css('red')}' : '${css('amber')}'`),
				},
			],
		},
	}
}

/** The presets the structure names (a preset no group shows yet is left out until one does; a definition itself never changes). */
export function referencedPresets(
	presets: CompanionPresetDefinitions,
	structure: readonly CompanionPresetSection[],
): CompanionPresetDefinitions {
	const named = new Set<string>()
	for (const section of structure)
		for (const d of section.definitions) {
			if (typeof d === 'string') named.add(d)
			else if (d.type === 'simple') for (const id of d.presets) named.add(id)
			else named.add(d.presetId)
		}
	return Object.fromEntries(Object.entries(presets).filter(([id]) => named.has(id)))
}
