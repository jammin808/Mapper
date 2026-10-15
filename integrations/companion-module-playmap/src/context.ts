import type { DropdownChoice } from '@companion-module/base'
import type { VerbRow } from './catalog/verbs.ts'
import type { PlayMapConfig } from './config.ts'
import type { Encoders } from './encoders.ts'
import type { EventLog } from './events.ts'
import type { Confirms, GoGuard, PressResult, Receipts } from './guards.ts'
import type { LinkState, Reply } from './link/session.ts'
import type { LivePage } from './live.ts'
import type { NodeModel } from './state/model.ts'
import type { Thumbs } from './thumbs.ts'

/** How a press is sent: its receipt follows the control that pressed. */
export interface SendOptions {
	/** A GO of this playlist: the double-GO guard applies. */
	readonly goPlaylist?: string
	/** Two presses plus a hold: this press only arms the control unless it is the second within 3 s. */
	readonly confirmTwice?: boolean
}

/** What actions, feedbacks and presets read and do: the instance behind them. */
export interface Ctx {
	readonly model: NodeModel
	readonly config: PlayMapConfig
	readonly receipts: Receipts
	readonly confirms: Confirms
	readonly goGuard: GoGuard
	readonly encoders: Encoders
	readonly events: EventLog
	readonly thumbs: Thumbs
	readonly live: LivePage
	readonly verbs: readonly VerbRow[]
	link(): LinkState
	linked(): boolean
	/** The node's clock now (this machine's plus the PING offset). */
	nodeNow(): number
	/** Sends a press's line and answers its result on the node's first reply (5 s at most). */
	send(line: string, controlId: string, options?: SendOptions): Promise<PressResult>
	/** Asks a question (a query line) and answers the node's reply. */
	ask(line: string): Promise<Reply>
	/** Entity choices by id prefix from INDEX. */
	choices(prefix: string | undefined): DropdownChoice[]
	/** The press shape of a line's verb. */
	pressOf(line: string): 'instant' | 'hold' | 'double'
	minGoMs(): number
	checkFeedbacks(...types: string[]): void
	checkFeedbacksById(...ids: string[]): void
	/** Checks one feedback again after `ms` (one timer per feedback: a later ask replaces an earlier one). */
	recheckLater(feedbackId: string, ms: number): void
	setLiveRule(rule: string): void
	liveChanged(): void
}
