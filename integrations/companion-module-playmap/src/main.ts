import {
	InstanceBase,
	InstanceStatus,
	type CompanionActionSchemaWithResult,
	type CompanionActionSchemaWithoutResult,
	type CompanionCompositeElementSchema,
	type CompanionFeedbackSchema,
	type CompanionOptionValues,
	type CompanionStaticUpgradeScript,
	type CompanionVariableValues,
	type DropdownChoice,
	type JsonValue,
	type SomeCompanionConfigField,
} from '@companion-module/base'
import { Lines, actionDefinitions, rowOfLine } from './actions.ts'
import { BUNDLED_VERBS, readVerbs, type VerbRow } from './catalog/verbs.ts'
import { configFields, connectionTarget, num, type PlayMapConfig, type PlayMapSecrets } from './config.ts'
import type { Ctx, SendOptions } from './context.ts'
import { Encoders } from './encoders.ts'
import { EventLog, eventWords, readEvent } from './events.ts'
import { FEEDBACK_SLICES, feedbackDefinitions } from './feedbacks.ts'
import { Confirms, GoGuard, Receipts, type PressResult } from './guards.ts'
import { NodeSession, type LinkState, type Reply, type SessionOptions } from './link/session.ts'
import { LivePage } from './live.ts'
import { compositeDefinitions, presetDefinitions, presetStructure, referencedPresets } from './presets.ts'
import { NodeModel } from './state/model.ts'
import { P, at, field, list, num as numOf, str } from './state/paths.ts'
import { changedValues, variableDefinitions, variablesOf } from './state/variables.ts'
import { Thumbs } from './thumbs.ts'
import { KNOWN_LINE_PROTOCOL, MODULE_VERSION } from './version.ts'
import { Words } from './words.ts'

export { MODULE_VERSION } from './version.ts'

/** The module's typing for the base: dynamic actions and feedbacks (generated from the node's vocabulary), JSON variables. */
export interface PlayMapTypes {
	config: PlayMapConfig
	secrets: PlayMapSecrets | undefined
	actions: Record<
		string,
		| CompanionActionSchemaWithoutResult<CompanionOptionValues>
		| CompanionActionSchemaWithResult<CompanionOptionValues, JsonValue>
	>
	feedbacks: Record<string, CompanionFeedbackSchema<CompanionOptionValues>>
	variables: CompanionVariableValues
	compositeElements?: Record<string, CompanionCompositeElementSchema<CompanionOptionValues>>
}

/** Upgrade scripts are append-only: an action or feedback id, once shipped, never changes. */
export const UpgradeScripts: CompanionStaticUpgradeScript<PlayMapConfig, PlayMapSecrets | undefined>[] = []

/** Variable updates are coalesced to at most 25 a second (Companion takes 50 from a module). */
export const FLUSH_MS = 40
/** Clocks run between pushes at 10 Hz while a playlist plays. */
export const TICK_MS = 100
/** A rename re-sends the preset structure (template names) at most this often; keys relabel at once through value feedbacks. */
export const LABELS_DEBOUNCE_MS = 3000
/** STATE's project.index moved and no INDEX push came within this long: INDEX is asked. */
export const INDEX_WAIT_MS = 500

/** Test seams: the link's channel, clocks and randomness (the host never passes them). */
export interface PlayMapTestHooks {
	session?: SessionOptions
}

/** A PlayMap node as a Companion connection: one link (the web port's line channel, or the TCP wire), STATE and INDEX diffed into
 * variables and the feedback types they move, actions generated from the node's vocabulary, presets keyed by entity id. The node is
 * the only authority: a key's light is STATE, never the press. */
export default class PlayMapInstance extends InstanceBase<PlayMapTypes> implements Ctx {
	/** Set by a test before init (the host never sets it). */
	static testHooks: PlayMapTestHooks = {}

	readonly model = new NodeModel()
	config: PlayMapConfig = {}
	#secrets: PlayMapSecrets = {}
	verbs: readonly VerbRow[] = BUNDLED_VERBS
	readonly events = new EventLog()
	readonly receipts = new Receipts(() => this.#touch('receipts'))
	readonly confirms = new Confirms(() => this.#touch('confirm'))
	readonly goGuard = new GoGuard(() => this.#touch('go'))
	readonly encoders = new Encoders((line, controlId) => void this.send(line, controlId))
	readonly thumbs = new Thumbs(
		async (line) => this.ask(line),
		(ids) => this.checkFeedbacksById(...ids),
		(refs) => this.#watchThumbs(refs),
	)
	live = new LivePage('focused')
	#session: NodeSession | null = null
	#lastVars: Record<string, JsonValue> = {}
	#slices = new Map<string, string>()
	#forced = new Set<string>()
	#flushTimer: ReturnType<typeof setTimeout> | null = null
	#ticker: ReturnType<typeof setInterval> | null = null
	#rechecks = new Map<string, ReturnType<typeof setTimeout>>()
	#labelsTimer: ReturnType<typeof setTimeout> | null = null
	#indexTimer: ReturnType<typeof setTimeout> | null = null
	#whoamiTimer: ReturnType<typeof setTimeout> | null = null
	#presenceTimer: ReturnType<typeof setTimeout> | null = null
	#lastSig = ''
	#lastLabels = ''
	#structureKey = ''
	#presetsKey = ''
	#peersKey = ''
	#whoamiKey = ''
	#protoWarning: string | null = null
	#lastError = ''
	#definedVariables = ''
	/** Events pushed while an EVENTS SINCE replay is in flight, taken after it; null when no replay is. */
	#held: unknown[] | null = null
	/** What the instance last handed Companion's presets (the test reads it to compare with what the host kept). */
	lastPresets: { structure: unknown; presets: unknown } | null = null

	async init(config: PlayMapConfig, _isFirstInit: boolean, secrets: PlayMapSecrets | undefined): Promise<void> {
		this.config = config
		this.#secrets = secrets ?? {}
		this.live = new LivePage(typeof config.live_rule === 'string' ? config.live_rule : 'focused')
		this.#defineAll()
		this.#start()
	}

	async configUpdated(config: PlayMapConfig, secrets: PlayMapSecrets | undefined): Promise<void> {
		this.#stop()
		this.config = config
		this.#secrets = secrets ?? {}
		this.live.rule = typeof config.live_rule === 'string' && config.live_rule ? config.live_rule : this.live.rule
		this.#presetsKey = ''
		this.#structureKey = ''
		this.#defineAll()
		this.#start()
	}

	async destroy(): Promise<void> {
		this.#stop()
		this.thumbs.clear()
	}

	getConfigFields(): SomeCompanionConfigField[] {
		return configFields()
	}

	// ── Ctx ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

	link(): LinkState {
		return this.#session?.state ?? 'halted'
	}

	linked(): boolean {
		return this.#session?.linked === true
	}

	nodeNow(): number {
		return this.model.nodeNow(Date.now())
	}

	minGoMs(): number {
		return num(this.config.min_go_ms, 300)
	}

	async send(line: string, controlId: string, options: SendOptions = {}): Promise<PressResult> {
		if (options.goPlaylist && !this.goGuard.allow(options.goPlaylist, this.minGoMs()))
			return this.receipts.notSent(
				controlId,
				line,
				`pressed again within ${this.minGoMs()} ms — the double-GO guard held it`,
			)
		const session = this.#session
		if (!session?.linked) return this.receipts.notSent(controlId, line, 'the node is not linked')
		this.receipts.sent(controlId)
		try {
			const reply = await session.ask(line)
			return this.receipts.replied(controlId, line, reply, numOf(at(this.model.state, P.seq)))
		} catch (e) {
			return this.receipts.notSent(controlId, line, e instanceof Error ? e.message : String(e))
		}
	}

	async ask(line: string): Promise<Reply> {
		const session = this.#session
		if (!session) throw new Error('the node is not linked')
		return session.ask(line)
	}

	choices(prefix: string | undefined): DropdownChoice[] {
		const index = this.model.index
		if (!index) return []
		const named = (list: readonly { id: string; name: string }[]): DropdownChoice[] =>
			list.map((e) => ({ id: e.id, label: e.name }))
		switch (prefix) {
			case 'pl':
				return named(index.playlists)
			case 'it':
				return index.playlists.flatMap((p) => p.items.map((i) => ({ id: i.id, label: `${p.name} › ${i.label}` })))
			case 'op':
				return named(index.outputs)
			case 'pj':
				return named(index.projectors)
			case 'sf':
				return named(index.surfaces)
			case 'dv':
				return named(index.devices)
			case 'gn':
				return named(index.generators)
			case 'cv':
				return named(index.canvases)
			case 'ly':
				return index.canvases.flatMap((c) => c.layers.map((l) => ({ id: l.id, label: `${c.name} › ${l.name}` })))
			default:
				return []
		}
	}

	pressOf(line: string): 'instant' | 'hold' | 'double' {
		return rowOfLine(this.verbs, line)?.press ?? 'instant'
	}

	override checkFeedbacks(...types: string[]): void {
		if (types.length > 0) super.checkFeedbacks(types[0] as string, ...types.slice(1))
	}

	recheckLater(feedbackId: string, ms: number): void {
		// A stopped connection keeps no timer: the host may still ask a feedback once after destroy.
		if (!this.#session) return
		const before = this.#rechecks.get(feedbackId)
		if (before) clearTimeout(before)
		this.#rechecks.set(
			feedbackId,
			setTimeout(() => {
				this.#rechecks.delete(feedbackId)
				this.checkFeedbacksById(feedbackId)
			}, ms),
		)
	}

	setLiveRule(rule: string): void {
		this.live.rule = rule || 'focused'
		this.liveChanged()
	}

	liveChanged(): void {
		this.#touch('live')
		this.#presenceSoon()
	}

	// ── The link ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

	#start(): void {
		const session = new NodeSession(
			{
				helloName: () => `Companion:${this.label}`,
				token: () => (typeof this.#secrets.token === 'string' ? this.#secrets.token : ''),
				target: () => connectionTarget(this.config),
				status: (status, words) => this.#status(status, words),
				link: (state) => this.#linkChanged(state),
				paired: () => this.#paired(),
				push: (word, rest) => this.#push(word, rest),
				offset: (ms) => {
					this.model.offsetMs = ms
				},
				pinLearned: (fp) => {
					this.config = { ...this.config, tls_pin: fp }
					this.saveConfig(this.config, this.#secrets)
				},
			},
			PlayMapInstance.testHooks.session ?? {},
		)
		this.#session = session
		session.start()
	}

	#stop(): void {
		this.#session?.stop()
		this.#session = null
		for (const t of [this.#flushTimer, this.#labelsTimer, this.#indexTimer, this.#whoamiTimer, this.#presenceTimer])
			if (t) clearTimeout(t)
		this.#flushTimer = this.#labelsTimer = this.#indexTimer = this.#whoamiTimer = this.#presenceTimer = null
		if (this.#ticker) clearInterval(this.#ticker)
		this.#ticker = null
		for (const t of this.#rechecks.values()) clearTimeout(t)
		this.#rechecks.clear()
		this.#emptied()
	}

	#status(status: InstanceStatus, words: string | null): void {
		if (status === InstanceStatus.Ok && this.#protoWarning) {
			this.updateStatus(InstanceStatus.UnknownWarning, this.#protoWarning)
			return
		}
		if (status !== InstanceStatus.Ok && status !== InstanceStatus.Connecting && words) {
			this.#lastError = words
			this.#touch('link')
		}
		this.updateStatus(status, words)
	}

	#linkChanged(state: LinkState): void {
		if (state === 'connecting' || state === 'halted') this.#emptied()
		this.#touch('link')
		this.#flushNow()
		// Every light reads the link (a stale or dropped link shows no fact), so a link change checks every feedback.
		this.checkAllFeedbacks()
	}

	/** The link dropped: what the node said is no longer a fact, so it is emptied and every feedback checked again (a dead key never
	 * stays green). INDEX's names are kept so keys still say what they are, dimmed. */
	#emptied(): void {
		const held = this.#held
		this.#held = null
		if (held) this.#takeAll(held)
		this.model.state = null
		this.model.whoami = null
		this.#whoamiKey = ''
		this.receipts.clear()
		this.confirms.clear()
		this.goGuard.clear()
		this.encoders.clear()
		this.live.clear()
		if (this.#ticker) clearInterval(this.#ticker)
		this.#ticker = null
	}

	/** Paired: who this deck is, what it may run, the vocabulary, the structure, the events missed, the pictures shown. */
	#paired(): void {
		this.#lastSig = ''
		this.#lastLabels = ''
		this.#sendPresence()
		this.#askWhoami()
		this.ask('VERBS')
			.then((reply) => {
				if (!reply.ok) return
				const rows = readVerbs(JSON.parse(reply.payload))
				if (rows.length === 0) return
				const key = (r: readonly VerbRow[]): string =>
					r.map((v) => `${v.name}|${v.available}|${v.press}|${v.syntax}`).join('\n')
				if (key(rows) === key(this.verbs)) return
				this.verbs = rows
				this.setActionDefinitions(actionDefinitions(this))
			})
			.catch((e: unknown) => this.log('debug', `VERBS: ${e instanceof Error ? e.message : String(e)}`))
		this.#subscribe('SUBSCRIBE INDEX')
		if (this.events.lastSeq > 0) this.#replayEvents()
		else this.#subscribe('SUBSCRIBE EVENTS')
		this.thumbs.relinked()
		// A greeting STATE may have come before the pairing finished: the clocks start now if something plays.
		this.#runTicker()
		this.#dirty()
	}

	/** Opens EVENTS and replays what this deck missed since the last seq it took. Events pushed while the replay is in flight are held
	 * and taken after it, oldest first, so the last seq taken never passes a replayed event and every event is taken once. */
	#replayEvents(): void {
		const held: unknown[] = []
		this.#held = held
		this.#subscribe('SUBSCRIBE EVENTS')
		const since = async (seq: number): Promise<void> => {
			const reply = await this.ask(Lines.eventsSince(seq))
			if (!reply.ok || this.#held !== held) return
			const o = JSON.parse(reply.payload) as { seq?: unknown; events?: unknown }
			// A node whose newest seq is below the last one heard started again: its events are numbered afresh, from the start.
			if (typeof o.seq === 'number' && o.seq < this.events.lastSeq) {
				this.events.restarted()
				return since(0)
			}
			this.#takeAll(Array.isArray(o.events) ? o.events : [])
		}
		since(this.events.lastSeq)
			.catch((e: unknown) => this.log('debug', `EVENTS SINCE: ${e instanceof Error ? e.message : String(e)}`))
			.finally(() => {
				if (this.#held !== held) return
				this.#held = null
				this.#takeAll(held)
			})
	}

	#takeAll(events: readonly unknown[]): void {
		const read = events.map(readEvent).filter((e) => e !== null)
		read.sort((a, b) => a.seq - b.seq)
		let took = false
		for (const e of read) took = this.events.take(e) || took
		if (took) this.#touch('events')
	}

	#subscribe(line: string): void {
		this.ask(line)
			.then((reply) => {
				if (!reply.ok) this.log('warn', `${line}: ${reply.payload}`)
			})
			.catch((e: unknown) => this.log('debug', `${line}: ${e instanceof Error ? e.message : String(e)}`))
	}

	#askWhoami(): void {
		this.ask('WHOAMI')
			.then((reply) => {
				if (!reply.ok) return
				const who = this.model.setWhoami(JSON.parse(reply.payload))
				this.#protoWarning =
					who.proto > KNOWN_LINE_PROTOCOL ? Words.newerProtocol(who.proto, KNOWN_LINE_PROTOCOL) : null
				if (this.#protoWarning) this.updateStatus(InstanceStatus.UnknownWarning, this.#protoWarning)
				this.#touch('whoami')
			})
			.catch((e: unknown) => this.log('debug', `WHOAMI: ${e instanceof Error ? e.message : String(e)}`))
	}

	#sendPresence(): void {
		const json = JSON.stringify({
			kind: 'companion',
			module: MODULE_VERSION,
			live: this.live.rule,
			page: this.live.pageText(this.live.rule, this.model, this.nodeNow(), (l) => this.pressOf(l)),
			thumbs: this.thumbs.placedRefs().length,
		})
		this.#subscribe(Lines.presence(json))
	}

	#presenceSoon(): void {
		if (this.#presenceTimer || !this.linked()) return
		this.#presenceTimer = setTimeout(() => {
			this.#presenceTimer = null
			if (this.linked()) this.#sendPresence()
		}, 1000)
	}

	/** The watched set replaces the node's last one, so an empty set (the last picture key gone) is told too. */
	#watchThumbs(refs: string[]): void {
		if (!this.linked()) return
		this.#subscribe(Lines.watchThumbs(refs.slice(0, 128)))
		this.#presenceSoon()
	}

	#push(word: string, rest: string): void {
		// The node closed this session's EVENTS (it read too slowly, or too many came at once): open it again and replay the gap.
		if (word === 'EVENTS' && /^overflow\b/u.test(rest)) {
			this.#replayEvents()
			return
		}
		let json: unknown
		try {
			json = rest ? JSON.parse(rest) : null
		} catch {
			this.log('debug', `a ${word} push that is not JSON`)
			return
		}
		switch (word) {
			case 'STATE':
				this.#state(json)
				return
			case 'INDEX':
				this.#index(json)
				return
			case 'EVENT':
				this.#event(json)
				return
			case 'THUMBS':
				if (json && typeof json === 'object') this.thumbs.pushed(json as Record<string, unknown>)
				return
			default:
				return
		}
	}

	#state(json: unknown): void {
		this.model.setState(json)
		const s = this.model.state
		this.receipts.stateMoved(numOf(at(s, P.seq)))
		this.encoders.stateMoved()
		// A node whose INDEX moved says so in STATE; if no INDEX push follows (a node without SUBSCRIBE INDEX), it is asked.
		const sig = str(at(s, P.projectIndex))
		if (sig && sig !== this.#lastSig && !this.#indexTimer)
			this.#indexTimer = setTimeout(() => {
				this.#indexTimer = null
				if (str(at(this.model.state, P.projectIndex)) === this.#lastSig || !this.linked()) return
				this.ask('INDEX')
					.then((reply) => {
						if (reply.ok) this.#index(JSON.parse(reply.payload))
					})
					.catch((e: unknown) => this.log('debug', `INDEX: ${e instanceof Error ? e.message : String(e)}`))
			}, INDEX_WAIT_MS)
		// Show Mode or the controllers moved: what this deck may run may have too.
		const whoKey = JSON.stringify([at(s, P.showArmed), list(s, P.clients).length])
		if (this.#whoamiKey && whoKey !== this.#whoamiKey && !this.#whoamiTimer)
			this.#whoamiTimer = setTimeout(() => {
				this.#whoamiTimer = null
				if (this.linked()) this.#askWhoami()
			}, 100)
		this.#whoamiKey = whoKey
		const peers = JSON.stringify(list(s, P.peers).map((p) => [field(p, P.peerId), field(p, P.peerName)]))
		if (peers !== this.#peersKey) {
			this.#peersKey = peers
			this.#defineVariables()
			this.#definePresets()
		}
		this.#runTicker()
		this.#dirty()
	}

	#index(json: unknown): void {
		const doc = this.model.setIndex(json)
		if (this.#indexTimer) clearTimeout(this.#indexTimer)
		this.#indexTimer = null
		if (doc.sig !== this.#lastSig) {
			this.#lastSig = doc.sig
			this.#lastLabels = doc.labels
			if (this.#labelsTimer) clearTimeout(this.#labelsTimer)
			this.#labelsTimer = null
			this.#defineAll()
		} else if (doc.labels !== this.#lastLabels) {
			this.#lastLabels = doc.labels
			if (!this.#labelsTimer)
				this.#labelsTimer = setTimeout(() => {
					this.#labelsTimer = null
					this.#defineAll()
				}, LABELS_DEBOUNCE_MS)
		}
		this.#touch('index')
	}

	#event(json: unknown): void {
		if (this.#held) {
			this.#held.push(json)
			return
		}
		const e = readEvent(json)
		if (!e || !this.events.take(e)) return
		this.#touch('events')
	}

	// ── Definitions ────────────────────────────────────────────────────────────────────────────────────────────────────────────

	/** Variables, actions, feedbacks, composites and presets, once each, in the order the base asks (presets after the actions and
	 * feedbacks they name). */
	#defineAll(): void {
		this.#defineVariables()
		this.setActionDefinitions(actionDefinitions(this))
		this.setFeedbackDefinitions(feedbackDefinitions(this))
		this.setCompositeElementDefinitions(compositeDefinitions())
		this.#definePresets()
		this.#dirty()
	}

	/** The variable definitions for this INDEX, these peers and this bank size, re-sent only when the set of ids moves. */
	#defineVariables(): void {
		const defs = variableDefinitions(this.model, this.#bankSize())
		const defsKey = JSON.stringify(defs)
		if (defsKey === this.#definedVariables) return
		this.#definedVariables = defsKey
		this.setVariableDefinitions(defs)
		this.#lastVars = {}
	}

	#definePresets(): void {
		const peers = list(this.model.state, P.peers).map((p) => ({
			id: str(field(p, P.peerId)),
			name: str(field(p, P.peerName)),
		}))
		const structure = presetStructure(this.model.index, this.config, { peers })
		const presets = referencedPresets(presetDefinitions(this.config), structure)
		const presetsKey = JSON.stringify(presets)
		const structureKey = JSON.stringify(structure)
		if (presetsKey === this.#presetsKey && structureKey === this.#structureKey) return
		this.#presetsKey = presetsKey
		this.#structureKey = structureKey
		this.lastPresets = { structure, presets }
		this.setPresetDefinitions(structure, presets)
	}

	#bankSize(): number {
		return Math.min(64, Math.max(1, Math.round(num(this.config.bank_size, 8))))
	}

	// ── The flush ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

	/** A module fact moved (receipts, the link, the Live page …): its feedback types are checked at the next flush. */
	#touch(slice: string): void {
		this.#forced.add(slice)
		this.#dirty()
	}

	#dirty(): void {
		if (this.#flushTimer) return
		this.#flushTimer = setTimeout(() => {
			this.#flushTimer = null
			this.#flush()
		}, FLUSH_MS)
	}

	#flushNow(): void {
		if (this.#flushTimer) clearTimeout(this.#flushTimer)
		this.#flushTimer = null
		this.#flush()
	}

	/** The variables that changed and the feedback types whose STATE slice moved, sent together. */
	#flush(): void {
		const nodeNow = this.nodeNow()
		const last = this.events.last
		const vars = variablesOf(
			this.model,
			nodeNow,
			{
				link: this.link(),
				moduleBehind: this.#protoWarning !== null,
				lastError: this.#lastError,
				lastRefusal: this.receipts.lastRefusal,
				lastReceipt: this.receipts.lastReceipt,
				eventLast: last ? eventWords(last) : '',
				eventLastKind: last?.kind ?? '',
				eventSeq: this.events.lastSeq,
				liveRule: this.live.rule,
				livePage: this.live.pageText(this.live.rule, this.model, nodeNow, (l) => this.pressOf(l)),
			},
			this.#bankSize(),
		)
		const changed = changedValues(this.#lastVars, vars)
		this.#lastVars = vars
		if (Object.keys(changed).length > 0) this.setVariableValues(changed)

		const moved = new Set(this.#forced)
		this.#forced.clear()
		for (const [slice, value] of Object.entries(this.#sliceValues(nodeNow))) {
			if (this.#slices.get(slice) !== value) moved.add(slice)
			this.#slices.set(slice, value)
		}
		const types = Object.entries(FEEDBACK_SLICES)
			.filter(([, slices]) => slices.some((s) => moved.has(s)))
			.map(([type]) => type)
		this.checkFeedbacks(...types)
	}

	/** Each STATE slice a feedback type reads, as a comparable string. */
	#sliceValues(nodeNow: number): Record<string, string> {
		const s = this.model.state
		const j = (v: unknown): string => JSON.stringify(v ?? null)
		const playlists = this.model.playlists(nodeNow)
		return {
			link: this.link(),
			node: j([at(s, P.light), at(s, P.headline)]),
			air: j([at(s, P.blackout), at(s, P.frozen), at(s, P.identify), at(s, P.pattern), at(s, P.panic)]),
			show: j([at(s, P.showArmed), at(s, P.showBlind)]),
			whoami: j(
				this.model.whoami
					? { ...this.model.whoami, may: this.model.whoami.may ? [...this.model.whoami.may] : '*' }
					: null,
			),
			playlists: j(playlists.map((p) => [p.id, p.state, p.itemId, p.nextId, p.focused, p.rate])),
			clock: j(
				playlists.map((p) => [
					p.progress === null ? null : Math.round(p.progress * 200),
					p.remainingMs === null ? null : Math.ceil(p.remainingMs / 1000),
				]),
			),
			outputs: j(
				list(s, P.outputs).map((o) => [
					field(o, P.opId),
					field(o, P.opName),
					field(o, P.opEnabled),
					field(o, P.opLight),
					field(o, P.opFps),
				]),
			),
			projectors: j(at(s, P.projectors)),
			peers: j(at(s, P.peers)),
			faults: j(at(s, P.faults)),
			media: j([at(s, P.mediaReady), list(s, P.transfers).length]),
			scan: j(at(s, P.scan) != null),
			ai: j(at(s, P.proposals)),
			calibration: j(at(s, P.calTargets)),
			state: j([at(s, P.seq), at(s, P.at)]),
			index: `${this.model.index?.sig ?? ''}|${this.model.index?.labels ?? ''}`,
		}
	}

	/** 10 Hz while a playlist plays and clocks run between pushes. */
	#runTicker(): void {
		const want = this.config.interpolate !== false && this.model.playing && this.linked()
		if (want && !this.#ticker) this.#ticker = setInterval(() => this.#dirty(), TICK_MS)
		else if (!want && this.#ticker) {
			clearInterval(this.#ticker)
			this.#ticker = null
		}
	}
}
