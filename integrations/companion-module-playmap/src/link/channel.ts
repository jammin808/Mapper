import { connect as tcpConnect, type Socket } from 'node:net'
import type { TLSSocket } from 'node:tls'
import type { IncomingMessage } from 'node:http'
import WebSocket from 'ws'
import { SUBPROTOCOL } from '../version.ts'

/** Where and how the link opens: the web port's line channel (ws or wss, `/ws` with the subprotocol) or the TCP line wire. */
export interface ChannelTarget {
	readonly kind: 'ws' | 'wss' | 'tcp'
	readonly host: string
	readonly port: number
	/** wss only: the certificate's SHA-256 fingerprint this connection trusts ("" = trust on first use). */
	readonly pin: string
}

/** Why a channel ended, as far as the module can tell the operator. */
export interface ChannelClosed {
	/** WebSocket close code (4401 = wrong proofs, 1001 = the node stopping), when there was one. */
	readonly code?: number
	readonly reason: string
	/** The HTTP status that refused the upgrade, and the node's own words from the body's "error". */
	readonly httpStatus?: number
	readonly httpWords?: string
	/** The node has no line channel on its web port (an older node): the TCP line wire is the way in. */
	readonly fallback?: boolean
	/** The certificate was not the pinned one. */
	readonly pinMismatch?: boolean
}

/** What a channel tells its session. Every callback is called at most once per channel except line. */
export interface ChannelEvents {
	open(info: { fingerprint?: string }): void
	line(text: string): void
	closed(info: ChannelClosed): void
}

/** One open link: send a line, close it. A line is sent without its terminator (the channel frames it). */
export interface LineChannel {
	readonly kind: ChannelTarget['kind']
	send(line: string): void
	close(): void
}

export type ChannelFactory = (target: ChannelTarget, events: ChannelEvents) => LineChannel

/** The largest line the node reads (WireGrammar.MaxLineBytes): a longer one is never sent, and the node closes 1009 on one. */
export const MAX_LINE_BYTES = 64 * 1024
/** The largest line the module takes from the node: its answers are not held to the 64 KiB a command is (VERBS is the whole
 * vocabulary, INDEX a whole show), but a line past this is not the node's and closes the link. */
export const MAX_INBOUND_BYTES = 16 * 1024 * 1024

/** "AB:CD:…" or "abcd…" as one comparable form. */
export function normalisePin(pin: string): string {
	return pin.replace(/[^0-9a-fA-F]/g, '').toLowerCase()
}

/** Opens the channel the target names. */
export const openChannel: ChannelFactory = (target, events) =>
	target.kind === 'tcp' ? openTcp(target, events) : openWebSocket(target, events)

function openWebSocket(target: ChannelTarget, events: ChannelEvents): LineChannel {
	const scheme = target.kind === 'wss' ? 'wss' : 'ws'
	const host = target.host.includes(':') && !target.host.startsWith('[') ? `[${target.host}]` : target.host
	// No Origin header: the node refuses a foreign Origin, and a controller is no web page.
	const ws = new WebSocket(`${scheme}://${host}:${target.port}/ws`, [SUBPROTOCOL], {
		handshakeTimeout: 5000,
		maxPayload: MAX_INBOUND_BYTES,
		perMessageDeflate: false,
		// The certificate is checked against the pin below, not against a CA: a node's certificate is its own.
		rejectUnauthorized: false,
	})
	let done = false
	let fingerprint: string | undefined
	const finish = (info: ChannelClosed): void => {
		if (done) return
		done = true
		events.closed(info)
	}
	ws.on('upgrade', (res: IncomingMessage) => {
		if (target.kind !== 'wss') return
		const cert = (res.socket as TLSSocket).getPeerCertificate?.()
		fingerprint = cert?.fingerprint256
		if (target.pin && normalisePin(fingerprint ?? '') !== normalisePin(target.pin)) {
			finish({ reason: 'the certificate is not the pinned one', pinMismatch: true })
			ws.terminate()
		}
	})
	ws.on('unexpected-response', (req, res) => {
		const status = res.statusCode ?? 0
		let body = ''
		res.setEncoding('utf8')
		res.on('data', (chunk: string) => {
			if (body.length < 4096) body += chunk
		})
		res.on('end', () => {
			let words: string | undefined
			try {
				const parsed: unknown = JSON.parse(body)
				if (parsed && typeof parsed === 'object' && typeof (parsed as { error?: unknown }).error === 'string')
					words = (parsed as { error: string }).error
			} catch {
				words = undefined // a body that is not the node's JSON says nothing the operator can use
			}
			finish({
				reason: `HTTP ${status}`,
				httpStatus: status,
				...(words !== undefined ? { httpWords: words } : {}),
				fallback: status === 400 || status === 404 || status === 426,
			})
			req.destroy()
		})
		res.on('error', () => finish({ reason: `HTTP ${status}`, httpStatus: status }))
	})
	ws.on('open', () => {
		if (done) return
		if (ws.protocol !== SUBPROTOCOL) {
			// A node whose /ws is only the screens' JSON socket: the line wire is on TCP.
			finish({ reason: 'the node has no line channel on its web port', fallback: true })
			ws.terminate()
			return
		}
		events.open(fingerprint !== undefined ? { fingerprint } : {})
	})
	ws.on('message', (data, isBinary) => {
		if (done || isBinary) return
		events.line(Buffer.isBuffer(data) ? data.toString('utf8') : Buffer.concat(data as Buffer[]).toString('utf8'))
	})
	ws.on('close', (code, reason) => finish({ code, reason: reason.toString('utf8') || `closed (${code})` }))
	ws.on('error', (err) => finish({ reason: err.message }))
	return {
		kind: target.kind,
		send(line: string): void {
			if (!done && ws.readyState === WebSocket.OPEN) ws.send(line)
		},
		close(): void {
			if (ws.readyState === WebSocket.OPEN) ws.close(1000, 'closed')
			else ws.terminate()
			finish({ code: 1000, reason: 'closed' })
		},
	}
}

function openTcp(target: ChannelTarget, events: ChannelEvents): LineChannel {
	const socket: Socket = tcpConnect({ host: target.host, port: target.port })
	socket.setNoDelay(true)
	socket.setEncoding('utf8')
	let done = false
	let buffer = ''
	const finish = (info: ChannelClosed): void => {
		if (done) return
		done = true
		events.closed(info)
	}
	socket.setTimeout(5000, () => {
		if (socket.connecting) socket.destroy(new Error('no answer within 5 s'))
	})
	socket.on('connect', () => {
		socket.setTimeout(0)
		events.open({})
	})
	socket.on('data', (chunk: string) => {
		buffer += chunk
		let nl = buffer.indexOf('\n')
		while (nl >= 0) {
			const line = buffer.slice(0, nl).replace(/\r$/u, '')
			buffer = buffer.slice(nl + 1)
			if (line.length > 0 && !done) events.line(line)
			nl = buffer.indexOf('\n')
		}
		if (buffer.length > MAX_INBOUND_BYTES) socket.destroy(new Error('a line past 16 MiB'))
	})
	socket.on('error', (err) => finish({ reason: err.message }))
	socket.on('close', () => finish({ reason: 'closed' }))
	return {
		kind: 'tcp',
		send(line: string): void {
			if (!done && !socket.destroyed) socket.write(line + '\n')
		},
		close(): void {
			socket.destroy()
			finish({ reason: 'closed' })
		},
	}
}
