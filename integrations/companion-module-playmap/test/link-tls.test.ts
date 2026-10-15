// The line channel over TLS (wss): the node's certificate is its own, so the module trusts the fingerprint it saw on the first link and
// keeps it in the connection's config; a certificate that is not the pinned one halts the link before any line is sent.
import test from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { X509Certificate } from 'node:crypto'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { createServer, type Server } from 'node:https'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { WebSocketServer } from 'ws'
import { InstanceStatus } from '@companion-module/base'
import { boot } from './harness.ts'
import { fixture } from './fake-node.ts'
import { normalisePin } from '../src/link/channel.ts'
import { Words } from '../src/words.ts'

interface TlsNode {
	readonly port: number
	readonly fingerprint: string
	readonly lines: string[]
	stop(): Promise<void>
}

/** A node that asks no token, on wss with a certificate made for this test. */
async function tlsNode(dir: string): Promise<TlsNode> {
	const key = join(dir, 'key.pem')
	const cert = join(dir, 'cert.pem')
	execFileSync(
		'openssl',
		[
			'req',
			'-x509',
			'-newkey',
			'rsa:2048',
			'-nodes',
			'-keyout',
			key,
			'-out',
			cert,
			'-days',
			'1',
			'-subj',
			'/CN=node.test',
		],
		{
			stdio: 'ignore',
		},
	)
	const pem = readFileSync(cert)
	const server: Server = createServer({ key: readFileSync(key), cert: pem })
	const wss = new WebSocketServer({
		server,
		handleProtocols: (protocols) => (protocols.has('playmap.lines.v1') ? 'playmap.lines.v1' : false),
	})
	const lines: string[] = []
	wss.on('connection', (ws) => {
		ws.on('message', (data) => {
			const text = (data as Buffer).toString('utf8')
			lines.push(text)
			const id = /^@(\S+) /u.exec(text)?.[1]
			const verb = text.replace(/^@\S+ /u, '').split(' ')[0]
			ws.send(`OK${id ? ` @${id}` : ''}${verb === 'WHOAMI' ? ' {"proto":1,"may":"*"}' : ''}`)
			if (verb === 'HELLO') ws.send('STATE ' + JSON.stringify(fixture('state.json')))
		})
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	return {
		port: (server.address() as { port: number }).port,
		fingerprint: new X509Certificate(pem).fingerprint256,
		lines,
		async stop() {
			for (const c of wss.clients) c.terminate()
			await new Promise<void>((resolve) => wss.close(() => resolve()))
			await new Promise<void>((resolve) => server.close(() => resolve()))
		},
	}
}

void test('the first wss link keeps the certificate it saw; a later one is held to it', async () => {
	const dir = mkdtempSync(join(tmpdir(), 'playmap-tls-'))
	const node = await tlsNode(dir)
	try {
		const first = await boot({ host: '127.0.0.1', port: node.port, channel: 'wss', tls_pin: '' }, {})
		try {
			await first.host.until(() => first.host.status?.status === InstanceStatus.Ok, 'linked over wss')
			await first.host.until(() => first.host.saved.length > 0, 'the pin kept')
			const kept = first.host.saved.at(-1)?.config as { tls_pin?: string }
			assert.equal(normalisePin(kept.tls_pin ?? ''), normalisePin(node.fingerprint))
			await first.host.until(() => first.host.variables.get('node_name') === 'Test node', 'STATE over wss')
		} finally {
			await first.stop()
		}

		const pinned = await boot({ host: '127.0.0.1', port: node.port, channel: 'wss', tls_pin: node.fingerprint }, {})
		try {
			await pinned.host.until(() => pinned.host.status?.status === InstanceStatus.Ok, 'the pinned certificate is taken')
			assert.equal(pinned.host.saved.length, 0, 'a pinned link keeps its pin')
		} finally {
			await pinned.stop()
		}
	} finally {
		await node.stop()
		rmSync(dir, { recursive: true, force: true })
	}
})

void test('a certificate that is not the pinned one halts the link before any line is sent', async () => {
	const dir = mkdtempSync(join(tmpdir(), 'playmap-tls-'))
	const node = await tlsNode(dir)
	try {
		const wrong = 'AA:' + node.fingerprint.slice(3).replace(/[0-9A-F]/gu, (c) => (c === 'F' ? '0' : 'F'))
		const m = await boot({ host: '127.0.0.1', port: node.port, channel: 'wss', tls_pin: wrong }, {})
		try {
			await m.host.until(() => m.host.status?.status === InstanceStatus.AuthenticationFailure, 'refused')
			assert.equal(m.host.status?.message, Words.pinMismatch)
			assert.equal(m.instance.link(), 'halted')
			assert.deepEqual(node.lines, [], 'no line crosses before the certificate is checked')
		} finally {
			await m.stop()
		}
	} finally {
		await node.stop()
		rmSync(dir, { recursive: true, force: true })
	}
})
