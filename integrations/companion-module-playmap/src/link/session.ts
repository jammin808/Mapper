import { InstanceStatus } from '@companion-module/base'
import {
	MAX_LINE_BYTES,
	openChannel,
	type ChannelClosed,
	type ChannelFactory,
	type ChannelTarget,
	type LineChannel,
} from './channel.ts'
import { readLine } from './grammar.ts'
import { authProof, normaliseToken } from './pairing.ts'
import { Words, isLockedOut, isNotPaired, isWrongToken } from '../words.ts'
import { Lines } from '../lines.ts'

/** Where the node is, as the connection's config says it. */
/** How many of the last round trips the node's clock offset is chosen from. */
const CLOCK_SAMPLES = 8

export interface NodeTarget {
	readonly host: string
	/** The web port (the line channel's door). */
	readonly port: number
	/** The TCP line wire, used when the web port has no line channel. */
	readonly wirePort: number
	readonly channel: 'ws' | 'wss' | 'tcp'
	readonly pin: string
}

/** The link as keys show it: dark while connecting or halted, live, or stale (no answer for 4 s; keys dimmed). */
export type LinkState = 'connecting' | 'live' | 'stale' | 'halted'

/** The node's one answer to a line: OK (applied, or accepted with a receipt JSON) or ERR with its words. */
export interface Reply {
	readonly ok: boolean
	/** True when the node answered `{"stage":"accepted",…}`: queued, its effect not yet in STATE. */
	readonly accepted: boolean
	/** The payload after OK, or the words after ERR. */
	readonly payload: string
}

/** What the session tells its instance. */
export interface SessionHost {
	/** "Companion:<label>": the HELLO name and the proof's message. */
	helloName(): string
	token(): string
	target(): NodeTarget | { readonly error: string } | null
	status(status: InstanceStatus, words: string | null): void
	link(state: LinkState): void
	/** Paired (or the node asked no proof): subscriptions and questions may start. */
	paired(): void
	/** A push the node made: its word (STATE, INDEX, CONTEXT, EVENT, THUMBS …) and the rest of the line. */
	push(word: string, rest: string): void
	/** The node's clock minus this machine's, from the PING exchange. */
	offset(ms: number): void
	/** wss with no pin yet: the certificate seen first, to be kept. */
	pinLearned(fingerprint: string): void
}

export interface SessionOptions {
	factory?: ChannelFactory
	now?: () => number
	random?: () => number
	pingMs?: number
	staleMs?: number
	dropMs?: number
	replyTimeoutMs?: number
}

interface Pending {
	resolve(reply: Reply): void
	reject(error: Error): void
	timer: ReturnType<typeof setTimeout>
	/** The HELLO: its OK is read in the same tick as the greeting STATE that follows it, so it marks the hello answered at once. */
	hello: boolean
}

/** The link to one node: opens the channel, pairs (HELLO, then AUTH-PROOF when the node sends a NONCE), numbers every line "@c<n>"
 * and matches each reply by its id, pings every 2 s for liveness and the clock, and guards against a silent node — no line for 4 s
 * is stale (keys dim), for 10 s the link is dropped and opened again with jittered backoff (1 s doubling to 15 s). A wrong token, a
 * locked-out address or a certificate that is not the pinned one halts the link until the config changes: retrying would only spend
 * the address's wrong tokens. */
export class NodeSession {
	readonly #host: SessionHost
	readonly #factory: ChannelFactory
	readonly #now: () => number
	readonly #random: () => number
	readonly #pingMs: number
	readonly #staleMs: number
	readonly #dropMs: number
	readonly #replyTimeoutMs: number

	#channel: LineChannel | null = null
	#generation = 0
	#counter = 0
	#pending = new Map<string, Pending>()
	#nonce: string | null = null
	#state: LinkState = 'halted'
	#helloDone = false
	#paired = false
	#lastHeard = 0
	#attempts = 0
	#useTcp = false
	#stopped = true
	#halt = false
	#dropWhy: string | null = null
	#watchdog: ReturnType<typeof setInterval> | null = null
	#pinger: ReturnType<typeof setInterval> | null = null
	#reconnect: ReturnType<typeof setTimeout> | null = null
	/** The last CLOCK_SAMPLES round trips; the node's offset is the one with the least delay among them. */
	readonly #samples: { rtt: number; offset: number }[] = []
	#offset: number | null = null

	constructor(host: SessionHost, options: SessionOptions = {}) {
		this.#host = host
		this.#factory = options.factory ?? openChannel
		this.#now = options.now ?? ((): number => Date.now())
		this.#random = options.random ?? ((): number => Math.random())
		this.#pingMs = options.pingMs ?? 2000
		this.#staleMs = options.staleMs ?? 4000
		this.#dropMs = options.dropMs ?? 10000
		this.#replyTimeoutMs = options.replyTimeoutMs ?? 5000
	}

	get state(): LinkState {
		return this.#state
	}

	/** Live or stale: lines may be sent (a stale link still carries them; its replies are what un-stales it). */
	get linked(): boolean {
		return this.#paired && (this.#state === 'live' || this.#state === 'stale')
	}

	/** Which channel the link is on now. */
	get channelKind(): ChannelTarget['kind'] | null {
		return this.#channel?.kind ?? null
	}

	start(): void {
		this.#stopped = false
		this.#halt = false
		this.#attempts = 0
		this.#useTcp = false
		this.#connect()
	}

	stop(): void {
		this.#stopped = true
		this.#clearReconnect()
		this.#drop(null)
		this.#setState('halted')
	}

	/** Sends a line and waits for its one reply (or rejects when the link drops or nothing answers in 5 s). */
	async ask(line: string, timeoutMs?: number): Promise<Reply> {
		if (!this.linked) throw new Error('the node is not linked')
		return this.#request(line, timeoutMs)
	}

	async #request(line: string, timeoutMs?: number, hello = false): Promise<Reply> {
		const channel = this.#channel
		if (!channel) throw new Error('the node is not linked')
		// One line is one command: a line break would put a second, unnumbered line on the TCP wire.
		if (/[\r\n]/u.test(line)) throw new Error('a line cannot hold a line break')
		if (Buffer.byteLength(line, 'utf8') + 16 > MAX_LINE_BYTES)
			throw new Error('the line is longer than the node reads (64 KiB)')
		const id = `c${++this.#counter}`
		return new Promise<Reply>((resolve, reject) => {
			const timer = setTimeout(() => {
				this.#pending.delete(id)
				reject(new Error(`no answer from the node within ${Math.round((timeoutMs ?? this.#replyTimeoutMs) / 1000)} s`))
			}, timeoutMs ?? this.#replyTimeoutMs)
			this.#pending.set(id, { resolve, reject, timer, hello })
			channel.send(`@${id} ${line}`)
		})
	}

	#connect(): void {
		this.#reconnect = null
		if (this.#stopped) return
		const t = this.#host.target()
		if (!t || 'error' in t) {
			this.#host.status(InstanceStatus.BadConfig, t ? t.error : Words.noTarget)
			this.#setState('halted')
			return
		}
		const target: ChannelTarget =
			this.#useTcp || t.channel === 'tcp'
				? { kind: 'tcp', host: t.host, port: t.wirePort, pin: '' }
				: { kind: t.channel, host: t.host, port: t.port, pin: t.pin }
		const generation = ++this.#generation
		this.#nonce = null
		this.#helloDone = false
		this.#paired = false
		this.#dropWhy = null
		this.#setState('connecting')
		if (this.#attempts === 0) this.#host.status(InstanceStatus.Connecting, null)
		// A channel that cannot even be opened (an address the platform refuses) halts with words, never throws into Companion.
		try {
			this.#channel = this.#open(target, generation)
		} catch (e) {
			this.#channel = null
			this.#host.status(InstanceStatus.BadConfig, e instanceof Error ? e.message : String(e))
			this.#setState('halted')
		}
	}

	#open(target: ChannelTarget, generation: number): LineChannel {
		return this.#factory(target, {
			open: (info) => {
				if (generation !== this.#generation) return
				if (target.kind === 'wss' && !target.pin && info.fingerprint) this.#host.pinLearned(info.fingerprint)
				this.#lastHeard = this.#now()
				this.#startWatchdog()
				void this.#handshake(generation)
			},
			line: (text) => {
				if (generation === this.#generation) this.#heard(text)
			},
			closed: (info) => {
				if (generation === this.#generation) this.#closed(info, target)
			},
		})
	}

	async #handshake(generation: number): Promise<void> {
		try {
			const hello = await this.#request(Lines.hello(this.#host.helloName()), undefined, true)
			if (generation !== this.#generation) return
			// Only what retrying cannot mend halts (a token, a lockout, a pin); any other refusal is the node's passing fault.
			if (!hello.ok) return this.#dropAndRetry(hello.payload)
			if (this.#nonce !== null) {
				const token = normaliseToken(this.#host.token())
				if (token.length === 0) return this.#fail(InstanceStatus.BadConfig, Words.tokenNeeded)
				const proof = await this.#request(Lines.proof(authProof(token, this.#nonce, this.#host.helloName())))
				if (generation !== this.#generation) return
				if (!proof.ok) {
					if (isWrongToken(proof.payload))
						return this.#fail(InstanceStatus.AuthenticationFailure, Words.wrongToken(proof.payload))
					if (isLockedOut(proof.payload))
						return this.#fail(InstanceStatus.AuthenticationFailure, Words.lockedOut(proof.payload))
					return this.#dropAndRetry(proof.payload)
				}
			}
			this.#paired = true
			this.#attempts = 0
			this.#setState('live')
			this.#host.status(InstanceStatus.Ok, null)
			this.#startPinger()
			this.#host.paired()
		} catch (e) {
			// The link dropped mid-handshake: its close already decided what happens next.
			if (generation === this.#generation && this.#channel)
				this.#dropAndRetry(e instanceof Error ? e.message : String(e))
		}
	}

	/** A refusal that retrying cannot fix: said once, and the link stays down until the config changes. */
	#fail(status: InstanceStatus, words: string): void {
		this.#halt = true
		this.#host.status(status, words)
		this.#drop(null)
		this.#setState('halted')
	}

	#heard(text: string): void {
		this.#lastHeard = this.#now()
		if (this.#state === 'stale') {
			this.#setState('live')
			this.#host.status(InstanceStatus.Ok, null)
		}
		const line = readLine(text)
		if (line.id !== null) {
			const pending = this.#pending.get(line.id)
			if (!pending) return
			this.#pending.delete(line.id)
			clearTimeout(pending.timer)
			if (pending.hello && line.word === 'OK') this.#helloDone = true
			// A paired session the node now calls unpaired: its token was changed on the node. Proving the old one again would only
			// spend the address's wrong tokens, so the link halts until the token is typed again.
			if (line.word === 'ERR' && this.#paired && isNotPaired(line.rest)) {
				pending.resolve({ ok: false, accepted: false, payload: line.rest })
				this.#fail(InstanceStatus.AuthenticationFailure, Words.unpaired)
				return
			}
			pending.resolve({
				ok: line.word === 'OK',
				accepted: line.word === 'OK' && line.rest.startsWith('{"stage":"accepted"'),
				payload: line.rest,
			})
			return
		}
		if (line.word === 'NONCE') {
			if (!this.#helloDone) this.#nonce = line.rest.trim()
			return
		}
		if (line.word === 'OK' || line.word === 'ERR') return
		if (this.#helloDone || this.#paired) this.#host.push(line.word, line.rest)
	}

	#startWatchdog(): void {
		this.#stopTimers()
		this.#watchdog = setInterval(() => {
			const silent = this.#now() - this.#lastHeard
			if (silent >= this.#dropMs) {
				this.#dropAndRetry(`no answer from the node for ${Math.round(silent / 1000)} s`)
				return
			}
			if (silent >= this.#staleMs && this.#state === 'live') {
				this.#setState('stale')
				this.#host.status(InstanceStatus.UnknownWarning, Words.stale)
			}
		}, 250)
	}

	#startPinger(): void {
		if (this.#pinger) clearInterval(this.#pinger)
		const ping = (): void => {
			const t1 = this.#now()
			this.#request(Lines.ping(t1))
				.then((reply) => this.#pong(reply, t1))
				.catch(() => undefined) // a lost PING is what the watchdog counts; nothing to say here
		}
		ping()
		this.#pinger = setInterval(ping, this.#pingMs)
	}

	/** "PONG t1 t2 t3": the node read at t2 and answered at t3; the offset of the round trip with the least delay of the last 8. */
	#pong(reply: Reply, t1: number): void {
		const t4 = this.#now()
		const words = reply.payload.split(' ')
		if (!reply.ok || words[0] !== 'PONG' || words.length < 4) return
		const t2 = Number(words[2])
		const t3 = Number(words[3])
		if (!Number.isFinite(t2) || !Number.isFinite(t3)) return
		const rtt = t4 - t1 - (t3 - t2)
		this.#samples.push({ rtt, offset: (t2 - t1 + (t3 - t4)) / 2 })
		if (this.#samples.length > CLOCK_SAMPLES) this.#samples.shift()
		const best = this.#samples.reduce((a, b) => (b.rtt < a.rtt ? b : a))
		if (best.offset !== this.#offset) {
			this.#offset = best.offset
			this.#host.offset(best.offset)
		}
	}

	#dropAndRetry(why: string): void {
		this.#dropWhy = why
		const channel = this.#channel
		if (channel) channel.close()
	}

	#closed(info: ChannelClosed, target: ChannelTarget): void {
		this.#drop(info)
		if (this.#stopped || this.#halt) {
			this.#setState('halted')
			return
		}
		if (info.pinMismatch) return this.#fail(InstanceStatus.AuthenticationFailure, Words.pinMismatch)
		if (info.code === 4401) return this.#fail(InstanceStatus.AuthenticationFailure, Words.lockedOut(info.reason))
		// Only a plain ws link falls back to plain TCP: a wss link never trades its certificate for cleartext.
		if (info.fallback && target.kind === 'wss') return this.#fail(InstanceStatus.BadConfig, Words.noLineChannelTls)
		if (info.fallback && target.kind === 'ws') {
			this.#useTcp = true
			this.#setState('connecting')
			this.#reconnect = setTimeout(() => this.#connect(), 0)
			return
		}
		const delay = this.#backoff()
		const where = `${target.host}:${target.port}`
		if (info.httpStatus === 403 || info.httpStatus === 429)
			this.#host.status(InstanceStatus.BadConfig, info.httpWords ?? Words.refusedUpgrade(info.httpStatus))
		else
			this.#host.status(
				InstanceStatus.ConnectionFailure,
				this.#dropWhy ?? Words.unreachable(where, Math.round(delay / 1000)),
			)
		this.#setState('connecting')
		this.#reconnect = setTimeout(() => this.#connect(), delay)
	}

	/** 1 s, 2 s, 4 s, 8 s, then 15 s, each ±20 %, never past 15 s. */
	#backoff(): number {
		const base = Math.min(15000, 1000 * 2 ** this.#attempts)
		this.#attempts++
		return Math.min(15000, Math.round(base * (0.8 + 0.4 * this.#random())))
	}

	/** Ends the channel: timers stopped, every waiting line told the link dropped. */
	#drop(info: ChannelClosed | null): void {
		this.#stopTimers()
		this.#generation++
		const channel = this.#channel
		this.#channel = null
		this.#paired = false
		this.#helloDone = false
		this.#samples.length = 0
		this.#offset = null
		for (const [, p] of this.#pending) {
			clearTimeout(p.timer)
			p.reject(new Error(info ? `the link to the node dropped (${info.reason})` : 'the link to the node closed'))
		}
		this.#pending.clear()
		if (channel && !info) channel.close()
	}

	#stopTimers(): void {
		if (this.#watchdog) clearInterval(this.#watchdog)
		if (this.#pinger) clearInterval(this.#pinger)
		this.#watchdog = null
		this.#pinger = null
	}

	#clearReconnect(): void {
		if (this.#reconnect) clearTimeout(this.#reconnect)
		this.#reconnect = null
	}

	#setState(state: LinkState): void {
		if (this.#state === state) return
		this.#state = state
		this.#host.link(state)
	}
}
