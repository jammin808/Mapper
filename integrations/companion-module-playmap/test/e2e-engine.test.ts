// The module against the real node: tools/PlayMap.Node built beside it, run `engine --headless` with a pairing token, the module
// through the host's InstanceWrapper. It pairs over ws:// by proof, makes a playlist with its actions, sees the playlist's template
// group come with the INDEX push, presses the item key's down action and sees its feedback and variable move with STATE, sees every
// key dimmed when the engine is killed, and pairs again when it is back. PLAYMAP_ENGINE names the node host's dll; otherwise the
// Debug build in the tree is used (build the solution first).
import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn, type ChildProcess } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { InstanceStatus } from '@companion-module/base'
import { boot, configFor } from './harness.ts'

const TOKEN = 'K7QM-3XWD-P9RA'

function engineDll(): string {
	const named = process.env.PLAYMAP_ENGINE
	if (named) return named
	const built = fileURLToPath(
		new URL('../../../tools/PlayMap.Node/bin/Debug/net10.0/PlayMap.Node.dll', import.meta.url),
	)
	if (!existsSync(built))
		throw new Error(`the node host is not built (${built}): dotnet build PlayMap.sln, or set PLAYMAP_ENGINE`)
	return built
}

/** The node's settings file: loopback only, a token, no address trusted (so the link must prove it), no announcements. */
function writeSettings(home: string, httpPort: number, wirePort: number): void {
	writeFileSync(
		join(home, 'playmap.node.json'),
		JSON.stringify({
			nodeId: 'e2e0c0de',
			name: 'E2E node',
			token: TOKEN,
			bind: '127.0.0.1',
			httpPort,
			wirePort,
			syncPort: 0,
			trustLoopback: false,
			trustedAddresses: [],
			requireHttps: false,
			announce: false,
			patternsHost: '',
			patternsToken: '',
			stagewatchHost: '',
			stagewatchKey: '',
			stagewatchEnabled: false,
			companion: { mdns: false },
		}),
	)
}

interface Engine {
	readonly process: ChildProcess
	readonly web: number
	readonly wire: number
}

/** Starts the node and resolves with its ports once its log says it is up. */
async function startEngine(home: string): Promise<Engine> {
	const child = spawn(
		process.env.DOTNET_HOST_PATH ?? 'dotnet',
		[engineDll(), 'engine', '--headless', '--home', home, '--no-recover'],
		{
			stdio: ['ignore', 'ignore', 'pipe'],
		},
	)
	let errors = ''
	child.stderr?.on('data', (d: Buffer) => (errors += d.toString()))
	const log = join(home, 'playmap.engine.log')
	for (;;) {
		if (child.exitCode !== null) throw new Error(`the engine exited (${child.exitCode}): ${errors}`)
		const text = existsSync(log) ? readFileSync(log, 'utf8') : ''
		const m = /is up: wire (\d+), web (\d+)/u.exec(text)
		if (m) return { process: child, wire: Number(m[1]), web: Number(m[2]) }
		await new Promise((resolve) => setTimeout(resolve, 100))
	}
}

async function kill(engine: Engine): Promise<void> {
	if (engine.process.exitCode !== null) return
	const exited = new Promise((resolve) => engine.process.once('exit', resolve))
	engine.process.kill('SIGKILL')
	await exited
}

void test(
	'the module pairs with the real engine, builds its keys from INDEX, follows STATE, dims when it dies and pairs again',
	{ timeout: 180_000 },
	async () => {
		const home = mkdtempSync(join(tmpdir(), 'playmap-e2e-'))
		writeSettings(home, 0, 0)
		let engine = await startEngine(home)
		const m = await boot(configFor(engine.web, engine.wire), { token: TOKEN })
		try {
			// Paired by proof over ws://.
			await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, 'paired')
			assert.equal(m.instance.link(), 'live')
			await m.host.until(() => m.host.variables.get('node_name') === 'E2E node', 'STATE from the engine')

			// Actions make a playlist.
			const made = (await m.press('v_playlist_new', { name: 'E2E show' })) as {
				ok: boolean
				stage: string
				reply: string
			}
			assert.equal(made.ok, true, JSON.stringify(made))
			const playlist = made.reply.trim()
			assert.match(playlist, /^pl[0-9a-f]{8}$/u)
			const added = (await m.press('v_playlist_add', { playlist, source: 'color:#FF0000', label: 'Red' })) as {
				ok: boolean
				reply: string
			}
			assert.equal(added.ok, true, JSON.stringify(added))
			const item = added.reply.trim()
			assert.match(item, /^it[0-9a-f]{8}$/u)

			// The playlist's template group comes with the INDEX push, its item the template value.
			const group = (): { id: string; templateValues?: { name?: string; value: unknown }[] } | undefined =>
				(
					m.host.structure as {
						id: string
						definitions: { id: string; templateValues?: { name?: string; value: unknown }[] }[]
					}[]
				)
					.find((s) => s.id === 'playlists')
					?.definitions.find((d) => d.id === `pl.${playlist}`)
			await m.host.until(
				() => (group()?.templateValues ?? []).some((v) => v.value === item),
				`template group pl.${playlist}`,
			)
			assert.deepEqual(group()?.templateValues, [{ name: 'Red', value: item }])

			// The item key: its feedbacks placed, its down action pressed; the light moves when STATE does, not on the press.
			await m.place('onair', 'item_on_air', { item })
			await m.place('stale', 'stale', {})
			await m.host.until(() => m.host.feedbackValues.get('onair') === false, 'not on air yet')
			const preset = (m.host.presets.item_key as { variants: { steps: { down: { actionId: string }[] }[] }[] })
				.variants[0]
			assert.equal(preset?.steps[0]?.down[0]?.actionId, 'v_playlist_go')
			const go = (await m.press('v_playlist_go', { playlist, item }, 'item-key')) as { ok: boolean; stage: string }
			assert.equal(go.ok, true, JSON.stringify(go))
			await m.host.until(() => m.host.feedbackValues.get('onair') === true, 'the item on air from STATE')
			await m.host.until(() => m.host.variables.get(`pl.${playlist}.state`) === 'playing', 'the playlist playing')
			assert.equal(m.host.feedbackValues.get('stale'), false)

			// Killed: the link drops, the state is emptied, every key is checked again and dims.
			await kill(engine)
			await m.host.until(() => m.host.feedbackValues.get('onair') === false, 'the on-air key off once the node is gone')
			await m.host.until(() => m.host.feedbackValues.get('stale') === true, 'keys dimmed')
			assert.notEqual(m.instance.link(), 'live')
			assert.equal(m.host.variables.get(`pl.${playlist}.state`), '')

			// Back on the same ports: paired again by proof, and green.
			writeSettings(home, engine.web, engine.wire)
			engine = await startEngine(home)
			await m.host.until(
				() => m.instance.link() === 'live' && m.host.status?.status === InstanceStatus.Ok,
				'paired again',
			)
			await m.host.until(() => m.host.feedbackValues.get('stale') === false, 'keys lit again')
			await m.host.until(() => m.host.variables.get('node_name') === 'E2E node', 'STATE again')
		} finally {
			await m.stop()
			await kill(engine)
			rmSync(home, { recursive: true, force: true })
		}
	},
)
