// A PlayMap node in Node for the module's tests: the web port's line channel (`/ws`, subprotocol playmap.lines.v1) and the TCP line
// wire, the same session on both — NONCE and the HMAC proof, `@id` replies, PING with the node's clock, STATE/INDEX/EVENT/THUMBS
// pushes from fixtures — on loopback, port 0. A test can make it refuse the upgrade with words, speak no line channel, go silent, or
// answer a verb its own way.
import { createServer as createHttpServer, type IncomingMessage, type Server } from 'node:http'
import { createServer as createTcpServer, type Server as TcpServer, type Socket } from 'node:net'
import { createHmac, randomBytes, timingSafeEqual } from 'node:crypto'
import { readFileSync } from 'node:fs'
import type { Duplex } from 'node:stream'
import { WebSocketServer, type WebSocket } from 'ws'
import { readLine } from '../src/link/grammar.ts'

export const fixture = (name: string): unknown =>
	JSON.parse(readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8'))

const verbsJson = readFileSync(new URL('../src/catalog/verbs.json', import.meta.url), 'utf8')

export interface FakeNodeOptions {
	/** The pairing token ("" = this node asks for none). */
	token?: string
	state?: unknown
	index?: unknown
	whoami?: unknown
	/** Answer the upgrade with this HTTP status and the node's JSON words. */
	refuse?: { status: number; error: string }
	/** The web port has no line channel (an older node): its /ws is the screens' JSON socket, answered with no subprotocol. An older
	 * node's token gate (401) or a port with no /ws at all (404) is `refuse`. */
	noLineChannel?: boolean
	/** The node's clock minus the test machine's (ms). */
	clockOffsetMs?: number
	/** HELLO is answered with the node's fault words this many times first (a session verb that faulted). */
	helloFaults?: number
}

/** One connected session as the fake node keeps it. */
interface Session {
	readonly kind: 'ws' | 'tcp'
	send(line: string): void
	close(code?: number): void
	nonce: string
	name: string
	paired: boolean
}

type Handler = (line: string, words: string[]) => string | null | undefined

/** A token as the node keeps it (Pairing.Normalise): dashes and whitespace removed, upper-cased. The fake's own, not the module's. */
function nodeToken(token: string | undefined): string {
	return [...(token ?? '')]
		.filter((c) => c !== '-' && !/\s/u.test(c))
		.join('')
		.toUpperCase()
}

/** The node's check of AUTH-PROOF (Pairing.Verify): HMAC-SHA256(key = the normalised token, message = nonce + the HELLO name),
 * written here and not taken from the module, so a proof the module gets wrong is refused here as the node refuses it. */
function proofIsRight(token: string | undefined, nonce: string, name: string, proofHex: string | undefined): boolean {
	if (!proofHex || !/^[0-9a-fA-F]{64}$/u.test(proofHex)) return false
	const expected = createHmac('sha256', Buffer.from(nodeToken(token), 'utf8'))
		.update(Buffer.from(nonce + name, 'utf8'))
		.digest()
	return timingSafeEqual(Buffer.from(proofHex, 'hex'), expected)
}

export class FakeNode {
	readonly options: FakeNodeOptions
	readonly lines: string[] = []
	readonly sessions = new Set<Session>()
	readonly handlers = new Map<string, Handler>()
	state: unknown
	index: unknown
	whoami: unknown
	/** While true the node reads lines but answers nothing (a hung node). */
	silent = false
	webPort = 0
	wirePort = 0
	#http: Server | null = null
	#wss: WebSocketServer | null = null
	#screens: WebSocketServer | null = null
	#helloFaulted = 0
	/** Sockets an older node's /ws took (no line channel on them). */
	readonly screens: WebSocket[] = []
	#tcp: TcpServer | null = null
	#waits: { match: (l: string) => boolean; done: (l: string) => void }[] = []

	constructor(options: FakeNodeOptions = {}) {
		this.options = options
		this.state = options.state ?? fixture('state.json')
		this.index = options.index ?? fixture('index.json')
		this.whoami = options.whoami ?? {
			session: 'l1',
			transport: 'companion',
			channel: 'ws',
			client: 'Companion:FOH deck',
			paired: true,
			role: 'remote',
			armed: false,
			may: '*',
			minGoMs: 0,
			proto: 1,
		}
	}

	async start(webPort = 0, wirePort = 0): Promise<this> {
		this.#wss = new WebSocketServer({
			noServer: true,
			handleProtocols: (protocols) => (protocols.has('playmap.lines.v1') ? 'playmap.lines.v1' : false),
		})
		// An older node's /ws: the screens' JSON socket, which takes no subprotocol.
		this.#screens = new WebSocketServer({ noServer: true, handleProtocols: () => false })
		this.#http = createHttpServer((_req, res) => {
			res.writeHead(404)
			res.end()
		})
		this.#http.on('upgrade', (req: IncomingMessage, socket: Duplex, head: Buffer) => this.#upgrade(req, socket, head))
		await new Promise<void>((resolve) => this.#http?.listen(webPort, '127.0.0.1', resolve))
		this.webPort = (this.#http.address() as { port: number }).port
		this.#tcp = createTcpServer((socket) => this.#serveTcp(socket))
		await new Promise<void>((resolve) => this.#tcp?.listen(wirePort, '127.0.0.1', resolve))
		this.wirePort = (this.#tcp.address() as { port: number }).port
		return this
	}

	async stop(): Promise<void> {
		for (const s of this.sessions) s.close(1001)
		this.sessions.clear()
		for (const ws of this.screens) ws.terminate()
		await new Promise<void>((resolve) => (this.#wss ? this.#wss.close(() => resolve()) : resolve()))
		await new Promise<void>((resolve) => (this.#screens ? this.#screens.close(() => resolve()) : resolve()))
		await new Promise<void>((resolve) => (this.#http ? this.#http.close(() => resolve()) : resolve()))
		this.#http?.closeAllConnections()
		await new Promise<void>((resolve) => (this.#tcp ? this.#tcp.close(() => resolve()) : resolve()))
	}

	/** The node's clock now. */
	now(): number {
		return Date.now() + (this.options.clockOffsetMs ?? 0)
	}

	/** Pushes a line to every paired session. */
	push(line: string): void {
		for (const s of this.sessions) if (s.paired) s.send(line)
	}

	pushState(state?: unknown): void {
		if (state !== undefined) this.state = state
		this.push('STATE ' + JSON.stringify(this.state))
	}

	pushIndex(index?: unknown): void {
		if (index !== undefined) this.index = index
		this.push('INDEX ' + JSON.stringify(this.index))
	}

	/** Resolves with the first line the module sent (from now or before) that matches. One not heard within PLAYMAP_TEST_WAIT_MS
	 * (20 s, as RecordingHost.until's) fails the wait with the pattern and the lines heard so far: a line the module stops sending
	 * fails the run, never hangs it. */
	async heard(match: RegExp | string): Promise<string> {
		const test = (l: string): boolean => (typeof match === 'string' ? l.includes(match) : match.test(l))
		const before = this.lines.find(test)
		if (before) return before
		const ms = Number(process.env.PLAYMAP_TEST_WAIT_MS ?? 20000)
		return new Promise((resolve, reject) => {
			const wait = {
				match: test,
				done: (line: string): void => {
					clearTimeout(guard)
					resolve(line)
				},
			}
			const guard = setTimeout(() => {
				this.#waits = this.#waits.filter((w) => w !== wait)
				reject(
					new Error(`no line matching ${String(match)} within ${ms} ms; heard: ${this.lines.join(' | ') || 'nothing'}`),
				)
			}, ms)
			guard.unref()
			this.#waits.push(wait)
		})
	}

	/** Lines the module sent, without their request ids. */
	sent(): string[] {
		return this.lines.map((l) => l.replace(/^@\S+ /u, ''))
	}

	#upgrade(req: IncomingMessage, socket: Duplex, head: Buffer): void {
		const refuse = this.options.refuse
		if (refuse) {
			const body = JSON.stringify({ error: refuse.error })
			socket.end(
				`HTTP/1.1 ${refuse.status} Refused\r\nContent-Type: application/json\r\nContent-Length: ${Buffer.byteLength(body)}\r\nConnection: close\r\n\r\n${body}`,
			)
			return
		}
		if (req.headers.origin) {
			socket.end('HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n')
			return
		}
		if (this.options.noLineChannel) {
			this.#screens?.handleUpgrade(req, socket, head, (ws: WebSocket) => this.screens.push(ws))
			return
		}
		this.#wss?.handleUpgrade(req, socket, head, (ws: WebSocket) => this.#serveWs(ws))
	}

	#serveWs(ws: WebSocket): void {
		const session: Session = {
			kind: 'ws',
			send: (line) => ws.send(line),
			close: (code = 1000) => ws.close(code),
			nonce: '',
			name: '',
			paired: false,
		}
		this.sessions.add(session)
		ws.on('message', (data) => this.#line(session, (data as Buffer).toString('utf8')))
		ws.on('close', () => this.sessions.delete(session))
		this.#open(session)
	}

	#serveTcp(socket: Socket): void {
		let buffer = ''
		const session: Session = {
			kind: 'tcp',
			send: (line) => socket.write(line + '\n'),
			close: () => socket.destroy(),
			nonce: '',
			name: '',
			paired: false,
		}
		this.sessions.add(session)
		socket.setEncoding('utf8')
		socket.on('data', (chunk: string) => {
			buffer += chunk
			let nl = buffer.indexOf('\n')
			while (nl >= 0) {
				this.#line(session, buffer.slice(0, nl))
				buffer = buffer.slice(nl + 1)
				nl = buffer.indexOf('\n')
			}
		})
		socket.on('close', () => this.sessions.delete(session))
		socket.on('error', () => this.sessions.delete(session))
		this.#open(session)
	}

	#open(session: Session): void {
		if (nodeToken(this.options.token).length > 0) {
			session.nonce = randomBytes(16).toString('hex')
			session.send(`NONCE ${session.nonce}`)
		} else session.paired = true
	}

	#line(session: Session, raw: string): void {
		const text = raw.trim()
		this.lines.push(text)
		for (const w of this.#waits.filter((x) => x.match(text))) w.done(text)
		this.#waits = this.#waits.filter((x) => !x.match(text))
		if (this.silent) return
		const m = /^@(\S+) (.*)$/u.exec(text)
		const id = m?.[1] ?? null
		const line = m?.[2] ?? text
		const words = line.split(' ')
		const verb = (words[0] ?? '').toUpperCase()
		const two = `${verb} ${(words[1] ?? '').toUpperCase()}`
		const ok = (payload?: string): void => session.send(`OK${id ? ` @${id}` : ''}${payload ? ` ${payload}` : ''}`)
		const err = (words: string): void => session.send(`ERR${id ? ` @${id}` : ''} ${words}`)
		if (verb === 'HELLO') {
			if (this.#helloFaulted < (this.options.helloFaults ?? 0)) {
				this.#helloFaulted++
				err('the node faulted while greeting — try again')
				return
			}
			session.name = line
				.slice(6)
				.replace(/ module=.*$/u, '')
				.trim()
			ok()
			if (session.paired) session.send('STATE ' + JSON.stringify(this.state))
			return
		}
		if (verb === 'AUTH-PROOF') {
			const right = proofIsRight(this.options.token, session.nonce, session.name, words[1])
			session.nonce = randomBytes(16).toString('hex')
			if (right) {
				session.paired = true
				ok('paired')
				session.send('STATE ' + JSON.stringify(this.state))
			} else {
				err("wrong token — the pairing token is in PlayMap's Network drawer")
				session.send(`NONCE ${session.nonce}`)
			}
			return
		}
		if (verb === 'PING') {
			const t = this.now()
			ok(`PONG ${words[1] ?? ''} ${t} ${t}`)
			return
		}
		if (!session.paired) {
			err('not paired — prove the pairing token first (AUTH-PROOF)')
			return
		}
		const custom = this.handlers.get(two) ?? this.handlers.get(verb)
		if (custom) {
			const reply = custom(line, words)
			if (reply === null) return
			if (reply !== undefined) {
				session.send(reply.replace(/^(OK|ERR)/u, (w) => (id ? `${w} @${id}` : w)))
				return
			}
		}
		switch (two) {
			case 'SUBSCRIBE INDEX':
				ok('subscribed index')
				session.send('INDEX ' + JSON.stringify(this.index))
				return
			case 'SUBSCRIBE EVENTS':
				ok('subscribed events')
				return
			case 'SUBSCRIBE THUMBS':
				ok('subscribed thumbs')
				session.send('THUMBS ' + JSON.stringify(Object.fromEntries(words.slice(2).map((r) => [r, `etag-${r}-1`]))))
				return
			case 'EVENTS SINCE':
				ok(JSON.stringify({ seq: 0, missed: 0, events: [] }))
				return
		}
		switch (verb) {
			case 'WHOAMI':
				ok(JSON.stringify(this.whoami))
				return
			case 'VERBS':
				ok(verbsJson.replace(/\s*\n\s*/g, ''))
				return
			case 'INDEX':
				ok(JSON.stringify(this.index))
				return
			case 'PRESENCE':
				ok()
				return
			case 'THUMB': {
				const ref = words[1] ?? ''
				ok(
					JSON.stringify({
						ref,
						etag: `etag-${ref}-1`,
						mime: 'image/png',
						w: 96,
						h: 96,
						data: Buffer.from(ref).toString('base64'),
					}),
				)
				return
			}
		}
		ok()
	}
}

/** The fixture's playlist ids and items, by name (the engine's renamed ids). */
export const SHOW = {
	playlist: 'pl00000001',
	loops: 'pl00000004',
	intro: 'it00000009',
	red: 'it00000002',
	blue: 'it00000003',
} as const

/** A copy of a fixture document to edit. */
export function copy<T>(doc: T): T {
	return JSON.parse(JSON.stringify(doc)) as T
}

/** Silences a promise's rejection a test does not wait for. */
export function ignore(p: Promise<unknown>): void {
	p.catch(() => undefined)
}

export type { Session, Handler }
export { readLine }
