// The module run the way Companion 5.1 runs it: through @companion-module/host's InstanceWrapper, with a ModuleHostContext that
// records everything the module hands Companion (definitions, variable and feedback values, statuses, presets, composites) and lets
// a test place feedbacks and press actions as the host does. Expressions are Companion's, so a test passes the values Companion
// would have evaluated.
import type { CompanionOptionValues, InstanceStatus, JsonValue } from '@companion-module/base'
import {
	InstanceWrapper,
	type HostActionDefinition,
	type HostFeedbackDefinition,
	type HostFeedbackValue,
	type HostVariableValue,
	type ModuleHostContext,
} from '@companion-module/host'
import PlayMapInstance, { UpgradeScripts } from '../src/main.ts'
import type { PlayMapConfig, PlayMapSecrets } from '../src/config.ts'
import type { SessionOptions } from '../src/link/session.ts'

export interface LogLine {
	readonly source: string | undefined
	readonly level: string
	readonly message: string
}

/** Every line the base and host log (the base prints to stdout without a host; tests read them instead). */
export const logs: LogLine[] = []
;(globalThis as { COMPANION_LOGGER?: unknown }).COMPANION_LOGGER = (
	source: string | undefined,
	level: string,
	message: string,
) => logs.push({ source, level, message })

type Waiter = { test: () => boolean; done: () => void }

export class RecordingHost implements ModuleHostContext<PlayMapConfig, PlayMapSecrets> {
	statuses: { status: InstanceStatus; message: string | null }[] = []
	actions = new Map<string, HostActionDefinition>()
	feedbacks = new Map<string, HostFeedbackDefinition>()
	variableDefs = new Map<string, string>()
	variables = new Map<string, JsonValue | undefined>()
	variableCalls = 0
	feedbackValues = new Map<string, unknown>()
	structure: unknown[] = []
	presets: Record<string, unknown> = {}
	presetCalls = 0
	composites: Record<string, unknown> = {}
	saved: { config: unknown; secrets: unknown }[] = []
	#waiters: Waiter[] = []

	#changed(): void {
		for (const w of [...this.#waiters])
			if (w.test()) {
				this.#waiters.splice(this.#waiters.indexOf(w), 1)
				w.done()
			}
	}

	/** Resolves when `test` holds (checked now and after every host call). Event driven: no clock is read. */
	async until(test: () => boolean, what = 'the condition'): Promise<void> {
		if (test()) return
		return new Promise((resolve, reject) => {
			const w = { test, done: resolve }
			this.#waiters.push(w)
			const guard = setTimeout(
				() => {
					this.#waiters.splice(this.#waiters.indexOf(w), 1)
					reject(new Error(`timed out waiting for ${what}`))
				},
				Number(process.env.PLAYMAP_TEST_WAIT_MS ?? 20000),
			)
			guard.unref()
			w.done = () => {
				clearTimeout(guard)
				resolve()
			}
		})
	}

	get status(): { status: InstanceStatus; message: string | null } | undefined {
		return this.statuses.at(-1)
	}

	setStatus(status: InstanceStatus, message: string | null): void {
		this.statuses.push({ status, message })
		this.#changed()
	}
	setActionDefinitions(actions: HostActionDefinition[]): void {
		this.actions = new Map(actions.map((a) => [a.id, a]))
		this.#changed()
	}
	setFeedbackDefinitions(feedbacks: HostFeedbackDefinition[]): void {
		this.feedbacks = new Map(feedbacks.map((f) => [f.id, f]))
		this.#changed()
	}
	setVariableDefinitions(definitions: { id: string; name: string }[], values: HostVariableValue[]): void {
		this.variableDefs = new Map(definitions.map((d) => [d.id, d.name]))
		for (const v of values) this.variables.set(v.id, v.value)
		this.#changed()
	}
	setPresetDefinitions(structure: unknown[], presets: Record<string, unknown>): void {
		this.structure = structure
		this.presets = presets
		this.presetCalls++
		this.#changed()
	}
	setCompositeElementDefinitions(compositeElements: Record<string, unknown>): void {
		this.composites = compositeElements
		this.#changed()
	}
	setVariableValues(values: HostVariableValue[]): void {
		this.variableCalls++
		for (const v of values) this.variables.set(v.id, v.value)
		this.#changed()
	}
	updateFeedbackValues(values: HostFeedbackValue[]): void {
		for (const v of values) this.feedbackValues.set(v.id, v.value)
		this.#changed()
	}
	saveConfig(newConfig: PlayMapConfig | undefined, newSecrets: PlayMapSecrets | undefined): void {
		this.saved.push({ config: newConfig, secrets: newSecrets })
	}
	sendOSC(): void {}
	recordAction(): void {}
	setCustomVariable(): void {}
	async sharedUdpSocketJoin(): Promise<string> {
		return 'none'
	}
	async sharedUdpSocketLeave(): Promise<void> {}
	async sharedUdpSocketSend(): Promise<void> {}
}

export interface Booted {
	readonly host: RecordingHost
	readonly wrapper: InstanceWrapper<import('../src/main.ts').PlayMapTypes>
	readonly instance: PlayMapInstance
	/** Places a feedback on a control, as the host does when a button gets it. */
	place(id: string, feedbackId: string, options: CompanionOptionValues, controlId?: string): Promise<void>
	/** Removes a placed feedback. */
	unplace(id: string): Promise<void>
	/** Presses an action on a control and answers its result. */
	press(actionId: string, options: CompanionOptionValues, controlId?: string): Promise<JsonValue | undefined>
	stop(): Promise<void>
}

let counter = 0

/** Boots the module through the host's wrapper with this config and secrets. */
export async function boot(
	config: PlayMapConfig,
	secrets: PlayMapSecrets = {},
	session?: SessionOptions,
	label = 'FOH deck',
): Promise<Booted> {
	const host = new RecordingHost()
	let instance: PlayMapInstance | null = null
	PlayMapInstance.testHooks = session ? { session } : {}
	class Captured extends PlayMapInstance {
		constructor(internal: unknown) {
			super(internal)
			// eslint-disable-next-line @typescript-eslint/no-this-alias
			instance = this
		}
	}
	const wrapper = new InstanceWrapper(`playmap-${++counter}`, host, Captured, UpgradeScripts, '2.2.1')
	await wrapper.init({ label, isFirstInit: false, config, secrets, lastUpgradeIndex: -1 })
	if (!instance) throw new Error('the wrapper made no instance')
	const placed = new Map<string, { controlId: string; feedbackId: string; options: CompanionOptionValues }>()
	return {
		host,
		wrapper,
		instance,
		async place(id, feedbackId, options, controlId = `c-${id}`) {
			placed.set(id, { controlId, feedbackId, options })
			await wrapper.updateFeedbacks({ [id]: { id, controlId, feedbackId, options } })
		},
		async unplace(id) {
			placed.delete(id)
			await wrapper.updateFeedbacks({ [id]: null })
		},
		async press(actionId, options, controlId = 'key-1') {
			const r = await wrapper.executeAction({ id: `a-${actionId}`, controlId, actionId, options }, undefined)
			if (!r.success) throw new Error(r.errorMessage)
			return r.result
		},
		async stop() {
			await wrapper.destroy()
		},
	}
}

/** The config pointing at a fake node's web port. */
export function configFor(webPort: number, wirePort = 9941, extra: PlayMapConfig = {}): PlayMapConfig {
	return { host: '127.0.0.1', port: webPort, channel: 'ws', wire_port: wirePort, ...extra }
}
