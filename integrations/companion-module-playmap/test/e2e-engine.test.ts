// The module against the real node: tools/PlayMap.Node built beside it, run `engine --headless` with a pairing token, the module
// through the host's InstanceWrapper. It pairs over ws:// by proof, makes a playlist with its actions, sees the playlist's template
// group come with the INDEX push, presses the item key's down action and sees its feedback and variable move with STATE, sees every
// key dimmed when the engine is killed, and pairs again when it is back. PLAYMAP_ENGINE names the node host's dll; otherwise the
// Debug build in the tree is used (build the solution first). The engine started is killed on every path, and a start reads only
// the log written since it spawned: one that exits, says a port is closed or is not up by its deadline is killed and fails with the
// engine's stderr (held here against a script standing in for the engine).
import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn, type ChildProcess } from 'node:child_process'
import { appendFileSync, existsSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { InstanceStatus } from '@companion-module/base'
import { boot, configFor, type Booted } from './harness.ts'
import { openChannel } from '../src/link/channel.ts'
import { MODULE_VERSION } from '../src/version.ts'

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

/** How the node host is run: the dotnet host and the built dll. */
const engineCommand = (): string[] => [process.env.DOTNET_HOST_PATH ?? 'dotnet', engineDll()]

/** The node's log, appended to by every run in one home. */
const logOf = (home: string): string => join(home, 'playmap.engine.log')

/** Kills the engine, resolving once it has exited (at once when it already has). */
async function stopEngine(child: ChildProcess): Promise<void> {
	if (child.exitCode !== null || child.signalCode !== null) return
	const exited = new Promise((resolve) => child.once('exit', resolve))
	child.kill('SIGKILL')
	await exited
}

/** A start that failed: why, with the engine's stderr, and the pid of the engine it killed. */
class EngineStartError extends Error {
	readonly pid: number | undefined

	constructor(why: string, stderr: string, pid: number | undefined) {
		super(`${why}: ${stderr}`)
		this.pid = pid
	}
}

/** Starts the node and resolves with its ports once the log written since it spawned says it is up. An engine that exits, says a
 * port is closed, or is not up within `deadlineMs` is killed and the start throws with its stderr. */
async function startEngine(home: string, command = engineCommand(), deadlineMs = 60_000): Promise<Engine> {
	const log = logOf(home)
	// An earlier run's lines are in the same file: only what is written after this size is this run's.
	const from = existsSync(log) ? statSync(log).size : 0
	const [file = '', ...args] = command
	const child = spawn(file, [...args, 'engine', '--headless', '--home', home, '--no-recover'], {
		stdio: ['ignore', 'ignore', 'pipe'],
	})
	let errors = ''
	child.stderr?.on('data', (d: Buffer) => (errors += d.toString()))
	// A host that cannot be run is an engine that exited (its exitCode the errno), saying why.
	child.once('error', (e) => (errors += `${e.message}\n`))
	// Its stderr is whole once its streams close, which may be after it exits.
	const closed = new Promise((resolve) => child.once('close', resolve))
	const deadline = performance.now() + deadlineMs
	let why: string
	for (;;) {
		if (child.exitCode !== null || child.signalCode !== null) {
			why = `the engine exited (${child.exitCode ?? child.signalCode})`
			break
		}
		const bytes = existsSync(log) ? readFileSync(log) : Buffer.alloc(0)
		// A log rolled since (smaller than it was) is all this run's.
		const text = bytes.subarray(bytes.length >= from ? from : 0).toString('utf8')
		const m = /is up: wire (\d+|closed), web (\d+|closed)/u.exec(text)
		if (m && m[1] !== 'closed' && m[2] !== 'closed') return { process: child, wire: Number(m[1]), web: Number(m[2]) }
		if (m) {
			why = `the engine is up with a port closed (${m[0]})`
			break
		}
		if (performance.now() >= deadline) {
			why = `the engine did not say it is up within ${deadlineMs} ms`
			break
		}
		await new Promise((resolve) => setTimeout(resolve, 100))
	}
	await stopEngine(child)
	await closed
	throw new EngineStartError(why, errors, child.pid)
}

/** A script standing in for the engine: `up` logs that it is up on wire 50001 and web 50002, `closed` that it is up with its web port
 * closed, `exit` says why on stderr and exits 3, `silent` logs nothing; each says its mode on stderr first, and all but `exit` run
 * until killed. */
function fakeEngine(dir: string, mode: 'up' | 'closed' | 'exit' | 'silent'): string[] {
	const script = join(dir, 'fake-engine.mjs')
	writeFileSync(
		script,
		[
			"import { appendFileSync } from 'node:fs'",
			"import { join } from 'node:path'",
			"const home = process.argv[process.argv.indexOf('--home') + 1]",
			'const mode = process.argv[2]',
			"const log = (line) => appendFileSync(join(home, 'playmap.engine.log'), line + '\\n')",
			"process.stderr.write('fake engine: ' + mode + '\\n')",
			"if (mode === 'exit') {",
			"\tprocess.stderr.write('the web port is taken\\n')",
			'\tprocess.exitCode = 3',
			'} else {',
			"\tif (mode === 'up') log('info: E2E node (e2e0c0de) is up: wire 50001, web 50002, renderer none.')",
			"\tif (mode === 'closed') log('info: E2E node (e2e0c0de) is up: wire 50001, web closed, renderer none.')",
			'\tsetInterval(() => undefined, 1 << 30)',
			'}',
		].join('\n'),
	)
	return [process.execPath, script, mode]
}

/** Whether a process is still there. */
function running(pid: number | undefined): boolean {
	assert.ok(pid !== undefined, 'the engine was spawned')
	try {
		process.kill(pid, 0)
		return true
	} catch (e) {
		if ((e as NodeJS.ErrnoException).code === 'ESRCH') return false
		throw e
	}
}

/** A home holding an earlier run's log, as a restart finds it. */
function homeWithLog(): string {
	const home = mkdtempSync(join(tmpdir(), 'playmap-e2e-'))
	appendFileSync(logOf(home), 'info: E2E node (e2e0c0de) is up: wire 41234, web 41235, renderer none.\n')
	return home
}

/** Starts a fake engine expecting the start to fail; answers its error. */
async function failedStart(home: string, command: string[], deadlineMs?: number): Promise<EngineStartError> {
	const e = await startEngine(home, command, deadlineMs).then(
		async (engine) => {
			await stopEngine(engine.process)
			return null
		},
		(err: unknown) => err,
	)
	assert.ok(e instanceof EngineStartError, `the start failed: ${String(e)}`)
	return e
}

void test(
	'a start reads only the log written since it spawned: the new run’s ports, not the earlier run’s',
	{ timeout: 20_000 },
	async () => {
		const home = homeWithLog()
		try {
			const engine = await startEngine(home, fakeEngine(home, 'up'))
			try {
				assert.deepEqual({ wire: engine.wire, web: engine.web }, { wire: 50001, web: 50002 })
				assert.equal(running(engine.process.pid), true)
			} finally {
				await stopEngine(engine.process)
			}
			assert.equal(running(engine.process.pid), false)
		} finally {
			rmSync(home, { recursive: true, force: true })
		}
	},
)

void test(
	'a restart whose engine exits fails with its code and stderr, though the log says an earlier run was up',
	{ timeout: 20_000 },
	async () => {
		const home = homeWithLog()
		try {
			const e = await failedStart(home, fakeEngine(home, 'exit'))
			assert.equal(e.message, 'the engine exited (3): fake engine: exit\nthe web port is taken\n')
			assert.equal(running(e.pid), false)
		} finally {
			rmSync(home, { recursive: true, force: true })
		}
	},
)

void test(
	'an engine up with a port closed is killed and its start fails at once, with its stderr',
	{ timeout: 20_000 },
	async () => {
		const home = mkdtempSync(join(tmpdir(), 'playmap-e2e-'))
		try {
			const e = await failedStart(home, fakeEngine(home, 'closed'))
			assert.equal(
				e.message,
				'the engine is up with a port closed (is up: wire 50001, web closed): fake engine: closed\n',
			)
			assert.equal(running(e.pid), false)
		} finally {
			rmSync(home, { recursive: true, force: true })
		}
	},
)

void test(
	'an engine that never says it is up is killed at its start’s deadline, with what it said',
	{ timeout: 20_000 },
	async () => {
		const home = mkdtempSync(join(tmpdir(), 'playmap-e2e-'))
		try {
			const e = await failedStart(home, fakeEngine(home, 'silent'), 500)
			// Killed at the deadline, it may not have said anything yet.
			assert.match(e.message, /^the engine did not say it is up within 500 ms: (fake engine: silent\n)?$/u)
			assert.equal(running(e.pid), false)
		} finally {
			rmSync(home, { recursive: true, force: true })
		}
	},
)

void test(
	'the module pairs with the real engine, builds its keys from INDEX, follows STATE, dims when it dies and pairs again',
	{ timeout: 180_000 },
	async () => {
		const home = mkdtempSync(join(tmpdir(), 'playmap-e2e-'))
		// Every path lets go of what it started: the module, the engine and its home.
		let engine: Engine | null = null
		let booted: Booted | null = null
		try {
			writeSettings(home, 0, 0)
			engine = await startEngine(home)
			// Every line the module says, as it leaves (the engine's own channel underneath).
			const said: string[] = []
			const m = await boot(
				configFor(engine.web, engine.wire),
				{ token: TOKEN },
				{
					factory: (target, events) => {
						const channel = openChannel(target, events)
						return {
							kind: channel.kind,
							send: (line: string): void => {
								said.push(line)
								channel.send(line)
							},
							close: (): void => channel.close(),
						}
					},
				},
			)
			booted = m

			// Paired by proof over ws:// — the engine's own WHOAMI says the session is on its web port's line channel, not the
			// TCP wire the module would fall back to.
			await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, 'paired')
			assert.equal(m.instance.link(), 'live')
			const who = JSON.parse((await m.instance.ask('WHOAMI')).payload) as { channel?: string; paired?: boolean }
			assert.equal(who.channel, 'ws')
			await m.host.until(() => m.host.variables.get('node_name') === 'E2E node', 'STATE from the engine')

			// PRESENCE as the module writes it is taken by the engine's own reader: STATE.clients shows this deck's version and
			// what its Live page shows.
			type Client = { kind?: string; paired?: boolean; module?: string | null; where?: string | null }
			const deck = (): Client | undefined =>
				((m.instance.model.state?.clients ?? []) as Client[]).find((c) => c.kind === 'companion')
			await m.host.until(() => deck()?.module === MODULE_VERSION, 'STATE.clients shows the module PRESENCE named')
			assert.equal(deck()?.paired, true)
			assert.match(deck()?.where ?? '', /^Live · focused /u)
			assert.ok(
				said.some((l) => /^@c\d+ PRESENCE \{"kind":"companion","module":"/u.test(l)),
				'PRESENCE said once paired',
			)

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
			// It came from the node's SUBSCRIBE INDEX push: the module never had to ask INDEX itself.
			assert.ok(
				said.some((l) => /^@c\d+ SUBSCRIBE INDEX$/u.test(l)),
				'subscribed to INDEX',
			)
			assert.deepEqual(
				said.filter((l) => /^@c\d+ INDEX$/u.test(l)),
				[],
				'no INDEX asked: the push brought it',
			)

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

			// Killed: the link drops, the state is emptied, every key is checked again and dims — within 4.5 s (design M12), the
			// status saying in words that the node is not there and when the link tries again.
			const killed = performance.now()
			await stopEngine(engine.process)
			await m.host.until(
				() => m.host.feedbackValues.get('onair') === false && m.host.feedbackValues.get('stale') === true,
				'the on-air key off and every key dimmed once the node is gone',
			)
			assert.ok(
				performance.now() - killed <= 4500,
				`keys off ${Math.round(performance.now() - killed)} ms after the kill`,
			)
			assert.notEqual(m.instance.link(), 'live')
			assert.equal(m.host.status?.status, InstanceStatus.ConnectionFailure)
			assert.match(m.host.status?.message ?? '', /^No link to the node at .* — trying again in \d+ s$/u)
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
			try {
				await booted?.stop()
			} finally {
				if (engine) await stopEngine(engine.process)
				rmSync(home, { recursive: true, force: true })
			}
		}
	},
)
