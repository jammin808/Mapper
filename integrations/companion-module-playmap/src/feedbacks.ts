import type {
	CompanionFeedbackDefinitions,
	CompanionOptionValues,
	DropdownChoice,
	JsonValue,
	SomeCompanionFeedbackInputField,
} from '@companion-module/base'
import type { Ctx } from './context.ts'
import { RULES, fillHint } from './live.ts'
import { colourOf, rgbOf } from './palette/playmap.ts'
import { clockText } from './state/model.ts'
import { P, at, bool, field, list, num, str } from './state/paths.ts'
import { movingTransfers, projectorLight, projectorScore, scanRunning } from './state/variables.ts'

/** What each feedback type depends on: a push or a module change that moves one of these rechecks only the types that read it. */
export const FEEDBACK_SLICES: Record<string, readonly string[]> = {
	link_state: ['link'],
	stale: ['link'],
	show_armed: ['show', 'whoami'],
	show_blind: ['show'],
	panic_active: ['air'],
	blackout: ['air'],
	frozen: ['air'],
	identify: ['air'],
	pattern_up: ['air'],
	node_light: ['node'],
	playlist_state: ['playlists'],
	item_on_air: ['playlists'],
	item_next: ['playlists'],
	item_cued: ['playlists'],
	output_light: ['outputs'],
	output_enabled: ['outputs'],
	projector_light: ['projectors'],
	peer_light: ['peers'],
	fault_any: ['faults'],
	media_ready: ['media'],
	transfer_active: ['media'],
	scan_running: ['scan'],
	proposal_waiting: ['ai'],
	verb_locked: ['whoami'],
	go_cooldown: ['go'],
	confirm_pending: ['confirm'],
	receipt: ['receipts'],
	event_pulse: ['events'],
	slot_empty: ['live', 'playlists', 'outputs', 'projectors', 'peers', 'faults', 'index', 'show'],
	slot_hint: ['live'],
	slot_light: ['live', 'playlists', 'outputs', 'projectors', 'peers', 'faults', 'index', 'show', 'link'],
	section_light: ['state', 'index', 'link'],
	label: ['playlists', 'outputs', 'projectors', 'peers', 'devices', 'index', 'link'],
	light: ['playlists', 'outputs', 'projectors', 'peers', 'devices', 'index', 'link'],
	state_word: ['playlists', 'outputs', 'projectors', 'peers', 'devices', 'index', 'link'],
	progress: ['playlists', 'clock'],
	remaining: ['playlists', 'clock'],
	thumb: ['thumbs', 'playlists', 'index'],
	score: ['projectors', 'calibration'],
	state_path: ['state'],
	slot: ['live', 'playlists', 'outputs', 'projectors', 'peers', 'faults', 'index', 'show', 'thumbs'],
	section_value: ['state', 'index'],
	receipt_word: ['receipts'],
	focus: ['playlists', 'clock', 'thumbs', 'index'],
	echo: [],
	variable: ['variables'],
	section_label: ['index'],
}

const style = (bg: string): { bgcolor: number; color: number } => ({
	bgcolor: rgbOf(bg),
	color: ['amber', 'orange', 'brightGreen', 'sky', 'cyan'].includes(bg) ? rgbOf('ink') : rgbOf('white'),
})

const LIGHTS: DropdownChoice[] = ['green', 'amber', 'red', 'grey'].map((l) => ({ id: l, label: l }))

function entityField(id: string, label: string, choices: DropdownChoice[]): SomeCompanionFeedbackInputField {
	return { type: 'dropdown', id, label, default: '', allowCustom: true, choices }
}

const text = (v: unknown): string => (typeof v === 'string' ? v.trim() : typeof v === 'number' ? String(v) : '')

/** An entity by id as a key shows it: its label ("REMOVED" for one the show no longer has), its light and its state word. */
export function entityView(ctx: Ctx, ref: string): { label: string; light: string; state: string } {
	const id = ref.trim()
	const live = ctx.link() === 'live'
	const dim = (v: { label: string; light: string; state: string }): { label: string; light: string; state: string } =>
		live ? v : { ...v, light: 'grey' }
	const model = ctx.model
	const now = ctx.nodeNow()
	if (!id) return { label: '', light: 'grey', state: '' }
	const pl = model.playlist(id, now)
	if (pl) {
		const light = pl.state === 'playing' ? 'green' : pl.state === 'paused' || pl.state === 'cued' ? 'amber' : 'grey'
		return dim({ label: pl.name, light, state: pl.state })
	}
	const item = model.item(id)
	const playlists = model.playlists(now)
	const airIn = playlists.find((p) => p.itemId === id && p.state !== 'idle')
	const nextIn = playlists.find((p) => p.nextId === id)
	if (item || airIn || nextIn) {
		// STATE says what is on the air before INDEX is heard; INDEX gives the label of an item that is neither.
		// A cued item holds its first frame, ready to go: amber (Patterns' cue colour), like the next one; playing or paused is on air.
		const cued = airIn?.state === 'cued'
		return dim({
			label: item?.item.label ?? airIn?.itemLabel ?? nextIn?.nextLabel ?? '',
			light: airIn ? (cued ? 'amber' : 'green') : nextIn ? 'amber' : 'grey',
			state: airIn ? (cued ? 'cued' : 'air') : nextIn ? 'next' : '',
		})
	}
	const op = model.output(id)
	if (op) {
		const enabled = bool(field(op, P.opEnabled))
		return dim({
			label: str(field(op, P.opName)),
			light: enabled ? str(field(op, P.opLight), 'grey') : 'grey',
			state: enabled ? 'enabled' : 'disabled',
		})
	}
	const pj = model.projector(id)
	if (pj) return dim({ label: str(field(pj, P.pjName)), light: projectorLight(pj), state: str(field(pj, P.pjGrade)) })
	// A projector box: its light and its power as STATE has them.
	const box = list(model.state, P.devices).find((d) => field(d, P.dvId) === id)
	if (box)
		return dim({
			label: str(field(box, P.dvName)),
			light: str(field(box, P.dvLight), 'grey'),
			state: str(field(box, P.dvPower)),
		})
	const peer = list(model.state, P.peers).find((p) => field(p, P.peerId) === id)
	if (peer)
		return dim({
			label: str(field(peer, P.peerName)),
			light: bool(field(peer, P.peerLive)) ? str(field(peer, P.peerLight), 'grey') : 'grey',
			state: str(field(peer, P.peerHeadline)),
		})
	// Not in STATE (the link dropped, or STATE not heard yet): INDEX still names it, so a key says what it is, dimmed — never REMOVED.
	const index = model.index
	for (const e of [
		...(index?.playlists ?? []),
		...(index?.outputs ?? []),
		...(index?.projectors ?? []),
		...(index?.surfaces ?? []),
		...(index?.devices ?? []),
		...(index?.generators ?? []),
		...(index?.canvases ?? []),
	])
		if (e.id === id) return dim({ label: e.name, light: 'grey', state: '' })
	for (const s of index?.sections ?? [])
		for (const r of s.rows) if (r.id === id) return dim({ label: r.label, light: r.light || 'grey', state: r.state })
	return { label: 'REMOVED', light: 'grey', state: 'removed' }
}

/** Any STATE value by a dotted path; a segment "[id]" or an id on its own picks the element of an array with that id
 * ("playlists[pl1a2b3c4d].rate", "outputs.op_1.present.light"). */
export function statePath(root: unknown, path: string): JsonValue {
	let cur: unknown = root
	for (const raw of path
		.trim()
		.split('.')
		.filter((x) => x.length > 0)) {
		const m = /^([^[\]]*)\[([^\]]+)\]$/u.exec(raw)
		const parts = m ? [m[1] ?? '', m[2] ?? ''] : [raw]
		for (const part of parts) {
			if (part === '') continue
			if (Array.isArray(cur)) {
				const byId = cur.find((e) => e && typeof e === 'object' && (e as Record<string, unknown>).id === part)
				cur = byId ?? (/^\d+$/u.test(part) ? cur[Number(part)] : undefined)
			} else if (cur && typeof cur === 'object') cur = (cur as Record<string, unknown>)[part]
			else return null
		}
	}
	return cur === undefined ? null : (cur as JsonValue)
}

/** The feedbacks: booleans for lights and trigger conditions (Patterns' colours by default), and typed values a layered key binds
 * through `$(this:value)` style overrides. Every one reads what the node said — a press never lights a key. */
export function feedbackDefinitions(ctx: Ctx): CompanionFeedbackDefinitions {
	const pl = ctx.choices('pl')
	const items = ctx.choices('it')
	const ops = ctx.choices('op')
	const pjs = ctx.choices('pj')
	const refs: DropdownChoice[] = [...pl, ...items, ...ops, ...pjs, ...ctx.choices('sf'), ...ctx.choices('gn')]
	const linkLive = (): boolean => ctx.link() === 'live'
	const air = (path: Parameters<typeof at>[1]): boolean => linkLive() && bool(at(ctx.model.state, path))
	return {
		link_state: {
			type: 'boolean',
			name: 'Link: in a state',
			description: 'The link to the node is live, stale (no answer for 4 s), connecting or halted',
			options: [
				{
					type: 'dropdown',
					id: 'state',
					label: 'State',
					default: 'live',
					choices: ['live', 'stale', 'connecting', 'halted'].map((s) => ({ id: s, label: s })),
				},
			],
			defaultStyle: style('green'),
			callback: (fb) => ctx.link() === text(fb.options.state),
		},
		stale: {
			type: 'boolean',
			name: 'Link: no answer (keys dimmed)',
			description: 'True while the node has said nothing for 4 s, and while the link is down',
			options: [],
			defaultStyle: style('dim'),
			callback: () => ctx.link() !== 'live',
		},
		show_armed: {
			type: 'boolean',
			name: 'Show: armed',
			options: [],
			defaultStyle: style(colourOf('show', 'armed')),
			callback: () => linkLive() && ctx.model.armed,
		},
		show_blind: {
			type: 'boolean',
			name: 'Show: blind edit open',
			options: [],
			defaultStyle: style(colourOf('show', 'blind')),
			callback: () => linkLive() && at(ctx.model.state, P.showBlind) != null,
		},
		panic_active: {
			type: 'boolean',
			name: 'Air: panic on',
			options: [],
			defaultStyle: style(colourOf('show', 'panic')),
			callback: () => linkLive() && at(ctx.model.state, P.panic) != null,
		},
		blackout: {
			type: 'boolean',
			name: 'Air: blackout',
			options: [],
			defaultStyle: style(colourOf('transport', 'blackout')),
			callback: () => air(P.blackout),
		},
		frozen: {
			type: 'boolean',
			name: 'Air: frozen',
			options: [],
			defaultStyle: style(colourOf('transport', 'frozen')),
			callback: () => air(P.frozen),
		},
		identify: {
			type: 'boolean',
			name: 'Air: identify on',
			options: [],
			defaultStyle: style('sky'),
			callback: () => air(P.identify),
		},
		pattern_up: {
			type: 'boolean',
			name: 'Air: a test pattern is up',
			options: [],
			defaultStyle: style(colourOf('output', 'pattern')),
			callback: () => linkLive() && at(ctx.model.state, P.pattern) != null,
		},
		node_light: {
			type: 'boolean',
			name: 'Node: its light is',
			options: [{ type: 'dropdown', id: 'light', label: 'Light', default: 'red', choices: LIGHTS }],
			defaultStyle: style('red'),
			callback: (fb) => linkLive() && str(at(ctx.model.state, P.light)) === text(fb.options.light),
		},
		playlist_state: {
			type: 'boolean',
			name: 'Playlist: in a state',
			options: [
				entityField('playlist', 'Playlist', pl),
				{
					type: 'dropdown',
					id: 'state',
					label: 'State',
					default: 'playing',
					choices: ['playing', 'paused', 'cued', 'ended', 'idle'].map((s) => ({ id: s, label: s })),
				},
			],
			defaultStyle: style('green'),
			callback: (fb) =>
				linkLive() && ctx.model.playlist(text(fb.options.playlist), ctx.nodeNow())?.state === text(fb.options.state),
		},
		item_on_air: {
			type: 'boolean',
			name: 'Item: on the air',
			options: [entityField('item', 'Item', items)],
			defaultStyle: style(colourOf('item', 'air')),
			callback: (fb) => linkLive() && entityView(ctx, text(fb.options.item)).state === 'air',
		},
		item_next: {
			type: 'boolean',
			name: 'Item: next',
			options: [entityField('item', 'Item', items)],
			defaultStyle: style(colourOf('item', 'next')),
			callback: (fb) => linkLive() && entityView(ctx, text(fb.options.item)).state === 'next',
		},
		item_cued: {
			type: 'boolean',
			name: 'Item: cued (its first frame held, ready to go)',
			options: [entityField('item', 'Item', items)],
			defaultStyle: style(colourOf('item', 'cued')),
			callback: (fb) => linkLive() && entityView(ctx, text(fb.options.item)).state === 'cued',
		},
		output_light: {
			type: 'boolean',
			name: 'Output: its light is',
			options: [
				entityField('output', 'Output', ops),
				{ type: 'dropdown', id: 'light', label: 'Light', default: 'red', choices: LIGHTS },
			],
			defaultStyle: style('red'),
			callback: (fb) => linkLive() && entityView(ctx, text(fb.options.output)).light === text(fb.options.light),
		},
		output_enabled: {
			type: 'boolean',
			name: 'Output: enabled',
			options: [entityField('output', 'Output', ops)],
			defaultStyle: style('green'),
			callback: (fb) => linkLive() && entityView(ctx, text(fb.options.output)).state === 'enabled',
		},
		projector_light: {
			type: 'boolean',
			name: 'Projector: its light is',
			options: [
				entityField('projector', 'Projector', pjs),
				{ type: 'dropdown', id: 'light', label: 'Light', default: 'red', choices: LIGHTS },
			],
			defaultStyle: style('red'),
			callback: (fb) => linkLive() && entityView(ctx, text(fb.options.projector)).light === text(fb.options.light),
		},
		peer_light: {
			type: 'boolean',
			name: "Fleet: a machine's light is",
			options: [
				{ type: 'textinput', id: 'peer', label: 'Machine id', default: '' },
				{ type: 'dropdown', id: 'light', label: 'Light', default: 'red', choices: LIGHTS },
			],
			defaultStyle: style('red'),
			callback: (fb) => linkLive() && entityView(ctx, text(fb.options.peer)).light === text(fb.options.light),
		},
		fault_any: {
			type: 'boolean',
			name: 'Node: a fault',
			options: [],
			defaultStyle: style('red'),
			callback: () => linkLive() && list(ctx.model.state, P.faults).length > 0,
		},
		media_ready: {
			type: 'boolean',
			name: 'Media: every file present',
			options: [],
			defaultStyle: style('green'),
			callback: () => air(P.mediaReady),
		},
		transfer_active: {
			type: 'boolean',
			name: 'Media: a transfer running',
			options: [],
			defaultStyle: style('amber'),
			callback: () => linkLive() && movingTransfers(ctx.model.state).length > 0,
		},
		scan_running: {
			type: 'boolean',
			name: 'Scan: running',
			options: [],
			defaultStyle: style(colourOf('scan', 'running')),
			callback: () => linkLive() && scanRunning(ctx.model.state),
		},
		proposal_waiting: {
			type: 'boolean',
			name: 'AI: a proposal waits for a person',
			options: [],
			defaultStyle: style(colourOf('ai', 'proposal')),
			callback: () => linkLive() && num(at(ctx.model.state, P.proposals)) > 0,
		},
		verb_locked: {
			type: 'boolean',
			name: 'Show Mode: this verb is locked for this deck',
			description:
				'From WHOAMI: the verbs this session may run now. A locked key is still sent; the node refuses it with words',
			options: [{ type: 'textinput', id: 'verb', label: 'Verb (PLAYLIST GO)', default: '' }],
			defaultStyle: style(colourOf('show', 'locked')),
			callback: (fb) => {
				const may = ctx.model.whoami?.may
				if (!linkLive() || !may) return false
				const verb = text(fb.options.verb).toUpperCase()
				return verb.length > 0 && !may.has(verb)
			},
		},
		go_cooldown: {
			type: 'boolean',
			name: 'Double-GO guard: cooling',
			options: [entityField('playlist', 'Playlist (any when empty)', pl)],
			defaultStyle: style('amber'),
			callback: (fb) => ctx.goGuard.cooling(text(fb.options.playlist) || null, ctx.minGoMs()),
		},
		confirm_pending: {
			type: 'boolean',
			name: 'Press again to confirm (SURE?)',
			options: [],
			defaultStyle: { ...style('amber'), text: 'SURE?' },
			callback: (fb) => ctx.confirms.pending(fb.controlId),
		},
		receipt: {
			type: 'boolean',
			name: "Receipt: this key's press",
			description: 'Pending while sent or accepted, failed for 2 s when refused (the words are in last_refusal)',
			options: [
				{
					type: 'dropdown',
					id: 'stage',
					label: 'Stage',
					default: 'pending',
					choices: [
						{ id: 'pending', label: 'pending' },
						{ id: 'failed', label: 'failed' },
					],
				},
			],
			defaultStyle: style('amber'),
			callback: (fb) => ctx.receipts.stage(fb.controlId) === text(fb.options.stage),
		},
		event_pulse: {
			type: 'boolean',
			name: 'Event: happened just now (for triggers)',
			description:
				'True for hold_ms after the node raises an event of this kind (item.end, fault.raised, show.armed …)',
			options: [
				{ type: 'textinput', id: 'kind', label: 'Kind (item.end, fault.*)', default: 'item.end' },
				{ type: 'textinput', id: 'ref', label: 'Naming (an id; empty = any)', default: '' },
				{ type: 'number', id: 'hold_ms', label: 'Hold (ms)', default: 1000, min: 100, max: 60000 },
			],
			defaultStyle: style('amber'),
			callback: (fb) => {
				const hold = num(Number(fb.options.hold_ms), 1000)
				const pulse = ctx.events.pulse(text(fb.options.kind), text(fb.options.ref), hold, ctx.nodeNow())
				if (pulse.on) ctx.recheckLater(fb.id, Math.max(1, pulse.until - ctx.nodeNow()))
				return pulse.on
			},
		},
		slot_empty: {
			type: 'boolean',
			name: 'Live: slot empty',
			options: liveSlotOptions(),
			defaultStyle: style('dim'),
			callback: (fb) => slotOf(ctx, fb.options) === null,
		},
		slot_hint: {
			type: 'boolean',
			name: 'Live: hold to send (HOLD hint)',
			options: liveSlotOptions(),
			defaultStyle: { ...style('amber'), text: 'HOLD' },
			callback: (fb) => ctx.live.hinted(ctx.live.source(fb.options.rule), num(Number(fb.options.n), 0)),
		},
		slot_light: {
			type: 'boolean',
			name: "Live: a slot's light is",
			description: 'The light the slot shows (grey while the link is not live): a simple key wears it as its ground',
			options: [
				...liveSlotOptions(),
				{ type: 'dropdown', id: 'light', label: 'Light', default: 'green', choices: LIGHTS },
			],
			defaultStyle: style('green'),
			callback: (fb) => {
				const s = slotOf(ctx, fb.options)
				return linkLive() && s !== null && s.light === text(fb.options.light)
			},
		},
		section_light: {
			type: 'boolean',
			name: "Section: a package section key's light is",
			description: "The light the key shows (its hint's STATE light, else its row's; grey while the link is not live)",
			options: [
				{ type: 'textinput', id: 'section', label: 'Section', default: '' },
				{ type: 'textinput', id: 'row', label: 'Row id', default: '' },
				{ type: 'number', id: 'key', label: 'Key (0-based)', default: 0, min: 0, max: 32 },
				{ type: 'dropdown', id: 'light', label: 'Light', default: 'green', choices: LIGHTS },
			],
			defaultStyle: style('green'),
			callback: (fb) =>
				sectionValue(ctx, text(fb.options.section), text(fb.options.row), num(Number(fb.options.key), 0)) ===
				text(fb.options.light),
		},
		label: {
			type: 'value',
			name: 'Value: label',
			description: 'An entity\'s name or label by id ("REMOVED" once the show has none)',
			options: [entityField('ref', 'Entity', refs)],
			callback: (fb) => entityView(ctx, text(fb.options.ref)).label,
		},
		light: {
			type: 'value',
			name: 'Value: light (green, amber, red, grey)',
			options: [entityField('ref', 'Entity', refs)],
			callback: (fb) => entityView(ctx, text(fb.options.ref)).light,
		},
		state_word: {
			type: 'value',
			name: 'Value: state word',
			options: [entityField('ref', 'Entity', refs)],
			callback: (fb) => entityView(ctx, text(fb.options.ref)).state,
		},
		progress: {
			type: 'value',
			name: 'Value: progress (0-1)',
			description:
				"The playlist's current item, interpolated between pushes; an item named that is not on the air is 0",
			options: [entityField('playlist', 'Playlist', pl), entityField('item', 'Item (any when empty)', items)],
			callback: (fb) => {
				const p = ctx.model.playlist(text(fb.options.playlist), ctx.nodeNow())
				const item = text(fb.options.item)
				if (!p || p.progress === null || (item && p.itemId !== item)) return 0
				return Math.round(p.progress * 200) / 200
			},
		},
		remaining: {
			type: 'value',
			name: 'Value: time left',
			options: [
				entityField('playlist', 'Playlist', pl),
				{
					type: 'dropdown',
					id: 'format',
					label: 'Format',
					default: 'clock',
					choices: [
						{ id: 'clock', label: 'm:ss' },
						{ id: 'seconds', label: 'seconds' },
					],
				},
			],
			callback: (fb) => {
				const p = ctx.model.playlist(text(fb.options.playlist), ctx.nodeNow())
				if (!p || p.remainingMs === null) return ''
				return text(fb.options.format) === 'seconds' ? Math.ceil(p.remainingMs / 1000) : clockText(p.remainingMs, true)
			},
		},
		thumb: {
			type: 'value',
			name: 'Value: picture (a data URI for an image element)',
			description: 'media:…, generator:…, playlist:…, color:#RRGGBB, or an item id (its source)',
			options: [
				{ type: 'textinput', id: 'ref', label: 'Picture of', default: '' },
				{ type: 'number', id: 'size', label: 'Size (px)', default: 96, min: 72, max: 144 },
			],
			callback: (fb) => thumbOf(ctx, fb.id, text(fb.options.ref), num(Number(fb.options.size), 96)),
			unsubscribe: (fb) => ctx.thumbs.forget(fb.id),
		},
		score: {
			type: 'value',
			name: "Value: a projector's calibration score (0-100)",
			options: [entityField('projector', 'Projector', pjs)],
			callback: (fb) => projectorScore(ctx.model, text(fb.options.projector)),
		},
		state_path: {
			type: 'value',
			name: 'Value: any STATE value by path',
			description:
				'A dotted path; "[id]" or an id picks an element ("playlists[pl…].rate", "outputs.op_1.present.light")',
			options: [{ type: 'textinput', id: 'path', label: 'Path', default: 'headline' }],
			callback: (fb) =>
				linkLive() || ctx.link() === 'stale' ? statePath(ctx.model.state, text(fb.options.path)) : null,
		},
		slot: {
			type: 'value',
			name: 'Value: a Live slot',
			options: [
				...liveSlotOptions(),
				{
					type: 'dropdown',
					id: 'field',
					label: 'Field',
					default: 'label',
					choices: ['label', 'sub', 'light', 'thumb', 'line'].map((f) => ({ id: f, label: f })),
				},
			],
			callback: (fb) => {
				const s = slotOf(ctx, fb.options)
				const f = text(fb.options.field)
				if (!s) return f === 'light' ? 'grey' : ''
				if (f === 'thumb') return s.thumbRef ? thumbOf(ctx, fb.id, s.thumbRef, num(ctx.config.thumb_size, 96)) : ''
				if (f === 'light') return ctx.link() === 'live' ? s.light : 'grey'
				return f === 'sub' ? s.sub : f === 'line' ? (s.line ?? '') : s.label
			},
			unsubscribe: (fb) => ctx.thumbs.forget(fb.id),
		},
		section_value: {
			type: 'value',
			name: "Value: a package section key's light",
			options: [
				{ type: 'textinput', id: 'section', label: 'Section', default: '' },
				{ type: 'textinput', id: 'row', label: 'Row id', default: '' },
				{ type: 'number', id: 'key', label: 'Key (0-based)', default: 0, min: 0, max: 32 },
			],
			callback: (fb) =>
				sectionValue(ctx, text(fb.options.section), text(fb.options.row), num(Number(fb.options.key), 0)),
		},
		focus: {
			type: 'value',
			name: 'Value: the focused playlist',
			description: 'Its name, current and next item, time left, progress, or the current or next item’s picture',
			options: [
				{
					type: 'dropdown',
					id: 'field',
					label: 'Field',
					default: 'next',
					choices: ['name', 'current', 'next', 'remaining', 'progress', 'current_thumb', 'next_thumb'].map((f) => ({
						id: f,
						label: f,
					})),
				},
			],
			callback: (fb) => {
				const p = ctx.model.focused(ctx.nodeNow())
				const f = text(fb.options.field)
				if (!p) return f === 'progress' ? 0 : ''
				switch (f) {
					case 'name':
						return p.name
					case 'current':
						return p.itemLabel
					case 'remaining':
						return clockText(p.remainingMs, true)
					case 'progress':
						return p.progress === null ? 0 : Math.round(p.progress * 200) / 200
					case 'current_thumb':
						return p.itemId ? thumbOf(ctx, fb.id, p.itemId, num(ctx.config.thumb_size, 96)) : ''
					case 'next_thumb':
						return p.nextId ? thumbOf(ctx, fb.id, p.nextId, num(ctx.config.thumb_size, 96)) : ''
					default:
						return p.nextLabel
				}
			},
			unsubscribe: (fb) => ctx.thumbs.forget(fb.id),
		},
		echo: {
			type: 'value',
			name: 'Value: the text given (a template value on a key)',
			options: [{ type: 'textinput', id: 'text', label: 'Text', default: '' }],
			callback: (fb) => text(fb.options.text),
		},
		variable: {
			type: 'value',
			name: "Value: one of this connection's variables",
			description: 'A key bound to it follows the variable without naming the connection (show_mode, master …)',
			options: [{ type: 'textinput', id: 'variable', label: 'Variable', default: 'show_mode' }],
			callback: (fb) => ctx.variable(text(fb.options.variable)),
		},
		section_label: {
			type: 'value',
			name: "Value: a package section key's label",
			options: [
				{ type: 'textinput', id: 'section', label: 'Section', default: '' },
				{ type: 'textinput', id: 'row', label: 'Row id', default: '' },
				{ type: 'number', id: 'key', label: 'Key (0-based)', default: 0, min: 0, max: 32 },
			],
			callback: (fb) => {
				const section = ctx.model.index?.sections.find((s) => s.section === text(fb.options.section))
				const row = section?.rows.find((r) => r.id === text(fb.options.row))
				const key = section?.keys[num(Number(fb.options.key), 0)]
				if (!row) return 'REMOVED'
				if (!key) return row.label
				const label = fillHint(key.label, row)
				return key.label.includes('$(') ? label : `${label}\n${row.label}`
			},
		},
		receipt_word: {
			type: 'value',
			name: "Value: this key's refusal words",
			options: [],
			callback: (fb) => ctx.receipts.words(fb.controlId),
		},
	}
}

export function liveSlotOptions(): SomeCompanionFeedbackInputField[] {
	return [
		{
			type: 'dropdown',
			id: 'rule',
			label: 'Source (empty: the connection’s; a page uses $(page:pm_live))',
			default: '',
			allowCustom: true,
			choices: [{ id: '', label: '(the connection’s)' }, ...RULES.map((r) => ({ id: r, label: r }))],
		},
		{ type: 'number', id: 'n', label: 'Slot (1-32)', default: 1, min: 1, max: 32 },
	]
}

export function slotOf(ctx: Ctx, options: CompanionOptionValues): ReturnType<Ctx['live']['slot']> {
	return ctx.live.slot(ctx.live.source(options.rule), num(Number(options.n), 0), ctx.model, ctx.nodeNow(), (l) =>
		ctx.pressOf(l),
	)
}

/** A key's picture: an item id stands for its source, a playlist id for its current item's picture. */
function thumbOf(ctx: Ctx, feedbackId: string, ref: string, size: number): string {
	const item = ctx.model.item(ref)
	const source = item
		? item.item.source
		: ctx.model.index?.playlists.some((p) => p.id === ref)
			? `playlist:${ref}`
			: ref
	return ctx.thumbs.get(feedbackId, source, size, ctx.linked())
}

const LIGHT_WORDS = new Set(['green', 'amber', 'red', 'grey'])

/** A section key's light: a hint feedback that is a STATE path (the row's id in for "$(id)") to a light word lights it from STATE;
 * otherwise ("$(light)", none, or a name that is no STATE light) it is the row's light, which INDEX pushes again when it moves. A
 * link that is not live shows grey. */
export function sectionValue(ctx: Ctx, sectionId: string, rowId: string, keyIndex: number): JsonValue {
	const section = ctx.model.index?.sections.find((s) => s.section === sectionId)
	const row = section?.rows.find((r) => r.id === rowId)
	if (!section || !row) return ''
	if (ctx.link() !== 'live') return 'grey'
	const feedback = section.keys[keyIndex]?.feedback
	if (feedback && feedback !== '$(light)') {
		const value = statePath(ctx.model.state, fillHint(feedback, row).replaceAll('"', ''))
		if (typeof value === 'string' && LIGHT_WORDS.has(value)) return value
	}
	return LIGHT_WORDS.has(row.light) ? row.light : 'grey'
}
