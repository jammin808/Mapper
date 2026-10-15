import { sanitizeVariable, type CompanionVariableDefinitions, type JsonValue } from '@companion-module/base'
import { clockText, type NodeModel, type PlaylistNow } from './model.ts'
import { P, at, bool, field, list, num, str } from './paths.ts'

/** What the module itself knows beside the node's facts: the link, the receipts, the last event, the Live page. */
export interface ModuleFacts {
	readonly link: string
	readonly moduleBehind: boolean
	readonly lastError: string
	readonly lastRefusal: string
	readonly lastReceipt: string
	readonly eventLast: string
	readonly eventLastKind: string
	readonly eventSeq: number
	readonly liveRule: string
	readonly livePage: string
}

/** The fixed variables, by id, with the names Companion lists. The video row keeps Patterns' names, so a key built for Patterns' VT
 * clock works on PlayMap. */
const FIXED: Record<string, string> = {
	node_name: 'Node name',
	node_id: 'Node id',
	node_version: 'Node version',
	node_protocol: 'Line protocol the node speaks',
	module_behind: 'The node speaks a newer protocol than this module (true/false)',
	link: 'Link to the node (live, stale, connecting, halted)',
	light: "The node's light (green, amber, red, grey)",
	headline: "The node's headline",
	project: 'Project name',
	project_dirty: 'Project has unsaved changes',
	revision: 'Project revision',
	faults_count: 'Faults on the node',
	fault_1: 'First fault',
	fault_2: 'Second fault',
	fault_3: 'Third fault',
	renderer: 'Renderer',
	clients: 'Controllers connected to the node',
	show_mode: 'Show Mode (ARMED or REHEARSAL)',
	show_by: 'Who armed the show',
	blind: 'Blind edit open',
	panic: 'Panic outcome on the air ("" when none)',
	blackout: 'Blackout on',
	frozen: 'Freeze on',
	pattern: 'Test pattern up ("" when none)',
	identify: 'Identify on',
	master: 'Grand master (0-100)',
	video_file: 'Focused item: file or label',
	video_tag: 'Focused playlist',
	video_position: 'Focused item: position',
	video_length: 'Focused item: length',
	video_remaining: 'Focused item: time left',
	video_remaining_s: 'Focused item: seconds left',
	video_chip: 'Focused item: chip',
	video_text: 'Focused item: one line',
	video_playing: 'Focused item playing',
	video_progress: 'Focused item: progress (0-1)',
	focused: 'Focused playlist id',
	focused_name: 'Focused playlist name',
	current: 'Current item',
	current_id: 'Current item id',
	next: 'Next item',
	next_id: 'Next item id',
	media_ready: 'All media present',
	media_missing: 'Media files missing',
	transfers: 'Media transfers running',
	transfer_progress: 'Media transfers: how far, together (0-1)',
	scan_state: 'Scan: its state (projecting …, or idle)',
	scan_stage: 'Scan: pattern n of all (12/40)',
	scan_progress: 'Scan: how far (0-1)',
	scan_words: 'Scan: in words',
	sync_role: 'Sync role',
	sync_leader: 'Sync leader',
	sync_uncertainty_ms: 'Leader-time uncertainty (ms, half the round trip)',
	sync_rtt_ms: 'Sync round trip (ms)',
	patterns_link: 'Patterns link',
	stagewatch_link: 'Stagewatch link',
	stagewatch_timer: "Stagewatch's timer, as it reads",
	stagewatch_phase: "Stagewatch's timer phase",
	cal_score: 'Calibration: the worst score (0-100)',
	cal_grade: 'Calibration: the worst grade',
	cal_words: 'Calibration: the worst, in words',
	ai_proposals: 'AI proposals waiting for a person',
	cal_moved: 'Projectors that moved since calibration',
	bpm: 'Tempo (BPM)',
	level: 'Audio level',
	last_error: 'Last error on the link',
	last_refusal: "The node's words for the last refused press",
	last_receipt: 'Last receipt (accepted, applied or refused)',
	event_last: 'Last event, in words',
	event_last_kind: 'Last event kind',
	event_seq: 'Last event sequence number',
	live_rule: "The Live page's source",
	live_page: "The Live page's page (2/5)",
}

const BANK_FIELDS = {
	pl: ['id', 'name', 'state', 'item', 'next', 'remaining', 'progress'],
	op: ['id', 'name', 'light', 'fps', 'dropped'],
	pj: ['id', 'name', 'light', 'residual_px'],
	sf: ['id', 'name'],
	peer: ['name', 'light', 'headline'],
} as const

/** A variable id from an entity id (ids are already safe; sanitising keeps a hand-made id from failing the host's rule). */
export const idVar = (prefix: string, id: string, f: string): string => sanitizeVariable(`${prefix}.${id}.${f}`)

/** Every variable this module defines for this INDEX and bank size: re-sent only when INDEX's structure moves. */
export function variableDefinitions(model: NodeModel, bankSize: number): CompanionVariableDefinitions {
	const defs: CompanionVariableDefinitions = {}
	for (const [id, name] of Object.entries(FIXED)) defs[id] = { name }
	for (const [prefix, fields] of Object.entries(BANK_FIELDS))
		for (let n = 1; n <= bankSize; n++)
			for (const f of fields) defs[`${prefix}_${n}_${f}`] = { name: `${prefix} ${n} ${f}` }
	const index = model.index
	for (const p of index?.playlists ?? [])
		for (const f of BANK_FIELDS.pl.slice(1)) defs[idVar('pl', p.id, f)] = { name: `${p.name}: ${f}` }
	for (const o of index?.outputs ?? [])
		for (const f of ['light', 'fps']) defs[idVar('op', o.id, f)] = { name: `${o.name}: ${f}` }
	for (const pj of index?.projectors ?? [])
		for (const f of ['light', 'residual_px', 'score']) defs[idVar('pj', pj.id, f)] = { name: `${pj.name}: ${f}` }
	for (const sf of index?.surfaces ?? []) defs[idVar('sf', sf.id, 'name')] = { name: `${sf.name}: name` }
	// The machines this node hears come from STATE, not INDEX: their definitions move when the set of peers does.
	for (const peer of list(model.state, P.peers)) {
		const id = str(field(peer, P.peerId))
		if (id) defs[idVar('peer', id, 'light')] = { name: `${str(field(peer, P.peerName), id)}: light` }
	}
	return defs
}

/** A projector's light from its solve: green under 1.5 px, amber under 4, red past it, grey unsolved. */
export function projectorLight(p: unknown): string {
	if (!bool(field(p, P.pjSolved))) return 'grey'
	const r = num(field(p, P.pjResidualPx))
	return r < 1.5 ? 'green' : r < 4 ? 'amber' : 'red'
}

function playlistFields(p: PlaylistNow | undefined): Record<string, JsonValue> {
	return {
		id: p?.id ?? '',
		name: p?.name ?? '',
		state: p?.state ?? '',
		item: p?.itemLabel ?? '',
		next: p?.nextLabel ?? '',
		remaining: p ? clockText(p.remainingMs, true) : '',
		progress: p?.progress !== null && p?.progress !== undefined ? Math.round(p.progress * 200) / 200 : 0,
	}
}

/** Every variable's value now: a pure function of what the node said, the module's own facts and the node's clock. Remaining times
 * are whole seconds and progress steps of 0.005, so a 10 Hz tick changes a value at most once a second or once per step. */
export function variablesOf(
	model: NodeModel,
	nodeNow: number,
	facts: ModuleFacts,
	bankSize: number,
): Record<string, JsonValue> {
	const s = model.state
	const v: Record<string, JsonValue> = {}
	v.node_name = str(at(s, P.name))
	v.node_id = str(at(s, P.id))
	v.node_version = str(at(s, P.version))
	v.node_protocol = model.whoami?.proto ?? 0
	v.module_behind = facts.moduleBehind
	v.link = facts.link
	v.light = s ? str(at(s, P.light), 'grey') : 'grey'
	v.headline = str(at(s, P.headline))
	v.project = str(at(s, P.projectName))
	v.project_dirty = bool(at(s, P.projectDirty))
	v.revision = num(at(s, P.projectRevision))
	const faults = list(s, P.faults)
	v.faults_count = faults.length
	for (let i = 1; i <= 3; i++) v[`fault_${i}`] = str(field(faults[i - 1], P.faultWords))
	v.renderer = str(at(s, P.renderer))
	v.clients = list(s, P.clients).length
	// A word about a node the module no longer hears is no fact: empty until STATE or WHOAMI says again.
	v.show_mode = s || model.whoami ? (model.armed ? 'ARMED' : 'REHEARSAL') : ''
	v.show_by = str(at(s, P.showBy))
	v.blind = at(s, P.showBlind) !== null && at(s, P.showBlind) !== undefined
	v.panic = str(at(s, P.panicOutcome))
	v.blackout = bool(at(s, P.blackout))
	v.frozen = bool(at(s, P.frozen))
	const pattern = at(s, P.pattern)
	v.pattern = pattern && typeof pattern === 'object' ? str((pattern as Record<string, unknown>).kind, 'on') : ''
	v.identify = bool(at(s, P.identify))
	v.master = Math.round(num(at(s, P.masterLevel), s ? 1 : 0) * 100)

	const playlists = model.playlists(nodeNow)
	const focused = playlists.find((p) => p.focused) ?? playlists[0]
	const video = at(s, P.video)
	v.video_file = str(at(s, P.videoFile))
	v.video_tag = str(at(s, P.videoTag))
	v.video_position = video ? clockText(focused?.positionMs ?? null) : ''
	v.video_length = str(at(s, P.videoLengthText))
	v.video_remaining = video ? clockText(focused?.remainingMs ?? null, true) : ''
	v.video_remaining_s =
		video && focused?.remainingMs !== null && focused ? Math.ceil((focused.remainingMs ?? 0) / 1000) : 0
	v.video_chip = video && focused ? `${focused.name} ${clockText(focused.remainingMs, true)}`.trim() : ''
	v.video_text = str(at(s, P.videoText))
	v.video_playing = bool(at(s, P.videoPlaying))
	v.video_progress = video && focused?.progress != null ? Math.round(focused.progress * 200) / 200 : 0
	v.focused = focused?.id ?? ''
	v.focused_name = focused?.name ?? ''
	v.current = focused?.itemLabel ?? ''
	v.current_id = focused?.itemId ?? ''
	v.next = focused?.nextLabel ?? ''
	v.next_id = focused?.nextId ?? ''

	v.media_ready = s ? bool(at(s, P.mediaReady)) : false
	v.media_missing = num(at(s, P.mediaMissing))
	v.transfers = list(s, P.transfers).length
	const transfers = list(s, P.transfers)
	v.transfer_progress =
		transfers.length > 0
			? Math.round(
					(transfers.reduce((sum: number, t) => sum + num(field(t, P.transferProgress)), 0) / transfers.length) * 100,
				) / 100
			: 0
	const scanning = at(s, P.scan) != null
	v.scan_state = s ? (scanning ? str(at(s, P.scanState), 'running') : 'idle') : ''
	const of = num(at(s, P.scanOf))
	v.scan_stage = scanning ? `${num(at(s, P.scanPattern))}/${of}` : ''
	v.scan_progress = scanning && of > 0 ? Math.round((num(at(s, P.scanPattern)) / of) * 100) / 100 : 0
	v.scan_words = str(at(s, P.scanWords))
	v.sync_role = str(at(s, P.syncRole))
	v.sync_leader = str(at(s, P.syncLeader))
	v.sync_uncertainty_ms = num(at(s, P.syncOffsetMs))
	v.sync_rtt_ms = num(at(s, P.syncRttMs))
	v.patterns_link = str(at(s, P.patternsLink))
	v.stagewatch_link = str(at(s, P.stagewatchLink))
	v.stagewatch_timer = str(at(s, P.stagewatchText))
	v.stagewatch_phase = str(at(s, P.stagewatchPhase))
	const worst = worstTarget(list(s, P.calTargets))
	const score = num(field(worst, P.calTargetScore), -1)
	v.cal_score = worst && score >= 0 ? Math.round(score) : ''
	v.cal_grade = str(field(worst, P.calTargetGrade))
	v.cal_words = str(field(worst, P.calTargetWords))
	v.ai_proposals = num(at(s, P.proposals))
	v.cal_moved = list(s, P.calMoved).length
	v.bpm = num(at(s, P.bpm))
	v.level = Math.round(num(at(s, P.level)) * 100) / 100
	v.last_error = facts.lastError
	v.last_refusal = facts.lastRefusal
	v.last_receipt = facts.lastReceipt
	v.event_last = facts.eventLast
	v.event_last_kind = facts.eventLastKind
	v.event_seq = facts.eventSeq
	v.live_rule = facts.liveRule
	v.live_page = facts.livePage

	for (let n = 1; n <= bankSize; n++) {
		for (const [f, value] of Object.entries(playlistFields(playlists[n - 1]))) v[`pl_${n}_${f}`] = value
		const op = list(s, P.outputs)[n - 1]
		v[`op_${n}_id`] = str(field(op, P.opId))
		v[`op_${n}_name`] = str(field(op, P.opName))
		v[`op_${n}_light`] = op ? str(field(op, P.opLight), 'grey') : ''
		v[`op_${n}_fps`] = num(field(op, P.opFps))
		v[`op_${n}_dropped`] = num(field(op, P.opDropped))
		const pj = list(s, P.projectors)[n - 1]
		v[`pj_${n}_id`] = str(field(pj, P.pjId))
		v[`pj_${n}_name`] = str(field(pj, P.pjName))
		v[`pj_${n}_light`] = pj ? projectorLight(pj) : ''
		v[`pj_${n}_residual_px`] = num(field(pj, P.pjResidualPx))
		const sf = model.index?.surfaces[n - 1]
		v[`sf_${n}_id`] = sf?.id ?? ''
		v[`sf_${n}_name`] = sf?.name ?? ''
		const peer = list(s, P.peers)[n - 1]
		v[`peer_${n}_name`] = str(field(peer, P.peerName))
		v[`peer_${n}_light`] = peer ? (bool(field(peer, P.peerLive)) ? str(field(peer, P.peerLight), 'grey') : 'grey') : ''
		v[`peer_${n}_headline`] = str(field(peer, P.peerHeadline))
	}
	for (const p of model.index?.playlists ?? []) {
		const now = playlists.find((x) => x.id === p.id)
		const values = playlistFields(now)
		values.name = now?.name ?? p.name
		for (const f of BANK_FIELDS.pl.slice(1)) v[idVar('pl', p.id, f)] = values[f] ?? ''
	}
	for (const o of model.index?.outputs ?? []) {
		const op = model.output(o.id)
		v[idVar('op', o.id, 'light')] = op ? str(field(op, P.opLight), 'grey') : 'grey'
		v[idVar('op', o.id, 'fps')] = num(field(op, P.opFps))
	}
	for (const pj of model.index?.projectors ?? []) {
		const p = model.projector(pj.id)
		v[idVar('pj', pj.id, 'light')] = p ? projectorLight(p) : 'grey'
		v[idVar('pj', pj.id, 'residual_px')] = num(field(p, P.pjResidualPx))
		v[idVar('pj', pj.id, 'score')] = projectorScore(model, pj.id)
	}
	for (const sf of model.index?.surfaces ?? []) v[idVar('sf', sf.id, 'name')] = sf.name
	for (const peer of list(s, P.peers)) {
		const id = str(field(peer, P.peerId))
		if (id)
			v[idVar('peer', id, 'light')] = bool(field(peer, P.peerLive)) ? str(field(peer, P.peerLight), 'grey') : 'grey'
	}
	return v
}

/** A projector's calibration score (0-100): the node's when it has one, else from its solve's residual (1 px off costs 20). */
export function projectorScore(model: NodeModel, id: string): number {
	const target = list(model.state, P.calTargets).find((t) => field(t, P.calTargetId) === id)
	const score = num(field(target, P.calTargetScore), -1)
	if (score >= 0) return Math.round(score)
	const pj = model.projector(id)
	if (!pj || !bool(field(pj, P.pjSolved))) return 0
	return Math.max(0, Math.min(100, Math.round(100 - num(field(pj, P.pjResidualPx)) * 20)))
}

const LIGHT_ORDER: Record<string, number> = { red: 0, amber: 1, grey: 2, green: 3 }

/** The calibration target that needs a look first: the worst light, then the lowest score. */
function worstTarget(targets: readonly unknown[]): unknown {
	let worst: unknown = undefined
	for (const t of targets) {
		if (worst === undefined) {
			worst = t
			continue
		}
		const a = LIGHT_ORDER[str(field(t, P.calTargetLight), 'grey')] ?? 2
		const b = LIGHT_ORDER[str(field(worst, P.calTargetLight), 'grey')] ?? 2
		if (a < b || (a === b && num(field(t, P.calTargetScore), 101) < num(field(worst, P.calTargetScore), 101))) worst = t
	}
	return worst
}

/** The values that differ from the last emitted map (a key in last missing from next is not sent: definitions clear it). */
export function changedValues(
	last: Record<string, JsonValue>,
	next: Record<string, JsonValue>,
): Record<string, JsonValue> {
	const out: Record<string, JsonValue> = {}
	for (const [k, value] of Object.entries(next)) if (last[k] !== value) out[k] = value
	return out
}
