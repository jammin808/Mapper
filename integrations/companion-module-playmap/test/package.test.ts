// The module ships as Companion installs it: the compiled module passes companion-module-check with the licence rules enforced, and
// companion-module-build makes pkg.tgz; that archive opens as Companion opens one (gzip, one root folder, a manifest that validates
// strictly, an entrypoint that lands on a bundle inside the package, nothing installed beside it), carries the packaged licence
// (package.json GPL-3.0-only, the tools' LICENSE inventory, COPYING's GPL-3.0 text) and the help page, and its bundle imports under
// Node on its own. The outputs this file makes (dist/, pkg/, pkg.tgz) are git-ignored and removed afterwards.
import test, { after } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { dirname, join, posix, sep } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { gunzipSync } from 'node:zlib'
import { validateManifest } from '@companion-module/base/manifest'
import { MODULE_VERSION } from '../src/version.ts'

const MODULE_DIR = fileURLToPath(new URL('../', import.meta.url))
const requireHere = createRequire(import.meta.url)
const CHECK = requireHere.resolve('@companion-module/tools/scripts/check-connection.js')
const BUILD = requireHere.resolve('@companion-module/tools/scripts/build-connection.js')
const versionOf = (name: string): string => (requireHere(`${name}/package.json`) as { version: string }).version
const BASE_VERSION = versionOf('@companion-module/base')
/** The FSF's GPL-3.0 text (gpl-3.0.txt), byte for byte: the packaged module's licence. */
const GPL3_SHA256 = '3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986'

const sha256 = (data: Buffer): string => createHash('sha256').update(data).digest('hex')
const here = (path: string): string => join(MODULE_DIR, path)
const sourceJson = (path: string): Record<string, unknown> =>
	JSON.parse(readFileSync(here(path), 'utf8')) as Record<string, unknown>

// What was there before this file ran stays; what it made goes.
const OUTPUTS = ['dist', 'pkg', 'pkg.tgz'] as const
const before = new Map(OUTPUTS.map((o) => [o, existsSync(here(o))]))
const scratch: string[] = []
after(() => {
	for (const o of OUTPUTS) if (before.get(o) === false) rmSync(here(o), { recursive: true, force: true })
	for (const dir of scratch) rmSync(dir, { recursive: true, force: true })
})

/** Runs a command in the module's folder and answers everything it said; a non-zero exit fails the test with that. */
function run(file: string, args: readonly string[], what: string): string {
	const r = spawnSync(file, args, {
		cwd: MODULE_DIR,
		encoding: 'utf8',
		stdio: ['ignore', 'pipe', 'pipe'],
		env: { ...process.env, FORCE_COLOR: '0', NO_COLOR: '1' },
		shell: file === 'npm' && process.platform === 'win32',
		maxBuffer: 64 * 1024 * 1024,
	})
	const said = `${r.stdout}\n${r.stderr}`
	assert.equal(r.error, undefined, `${what} did not start: ${String(r.error)}`)
	assert.equal(r.status, 0, `${what} failed (exit ${String(r.status)}, signal ${String(r.signal)}):\n${said}`)
	return said
}

let compiled = false
/** `npm run build`: the TypeScript the bundle is made from (package.json's main), once per file. */
function compile(): void {
	if (compiled) return
	run('npm', ['run', 'build'], 'npm run build')
	assert.ok(
		existsSync(here(String(sourceJson('package.json').main))),
		'the build writes the file package.json names as main',
	)
	compiled = true
}

interface TarEntry {
	readonly path: string
	readonly type: 'file' | 'dir' | 'link' | 'other'
	readonly data: Buffer
}

let archive: { readonly tgz: Buffer; readonly entries: readonly TarEntry[]; readonly log: string } | null = null
/** `companion-module-build --output pkg`, once per file: pkg.tgz and its entries. */
function packaged(): { readonly tgz: Buffer; readonly entries: readonly TarEntry[]; readonly log: string } {
	if (archive) return archive
	compile()
	const log = run(process.execPath, [BUILD, '--output', 'pkg'], 'companion-module-build --output pkg')
	assert.ok(existsSync(here('pkg.tgz')), 'the build writes pkg.tgz')
	const tgz = readFileSync(here('pkg.tgz'))
	archive = { tgz, entries: readTar(gunzipSync(tgz)), log }
	return archive
}

/** A NUL-terminated header field. */
function field(block: Buffer, from: number, length: number): string {
	const raw = block.subarray(from, from + length)
	const end = raw.indexOf(0)
	return raw.subarray(0, end < 0 ? raw.length : end).toString('utf8')
}

/** A numeric header field: octal text, or base-256 when the high bit of its first byte is set. */
function numeric(block: Buffer, from: number, length: number): number {
	const raw = block.subarray(from, from + length)
	if (((raw[0] ?? 0) & 0x80) !== 0) {
		let n = (raw[0] ?? 0) & 0x7f
		for (const b of raw.subarray(1)) n = n * 256 + b
		return n
	}
	const text = raw
		.toString('latin1')
		.replace(/[\0 ]+$/u, '')
		.trim()
	return text === '' ? 0 : parseInt(text, 8)
}

/** pax records: "<length> <key>=<value>\n", the length in bytes and counting itself. */
function paxRecords(data: Buffer): Record<string, string> {
	const out: Record<string, string> = {}
	let at = 0
	while (at < data.length) {
		const space = data.indexOf(0x20, at)
		if (space < 0) break
		const length = Number(data.subarray(at, space).toString('latin1'))
		assert.ok(Number.isInteger(length) && length > 0, 'a pax record has a length')
		const record = data.subarray(space + 1, at + length - 1).toString('utf8')
		const eq = record.indexOf('=')
		out[record.slice(0, eq)] = record.slice(eq + 1)
		at += length
	}
	return out
}

/** Reads a tar stream (ustar, pax extended and global headers, GNU long names and links); every header's checksum is checked. */
function readTar(tar: Buffer): TarEntry[] {
	const entries: TarEntry[] = []
	let global: Record<string, string> = {}
	let pax: Record<string, string> = {}
	let longName: string | null = null
	let at = 0
	while (at + 512 <= tar.length) {
		const block = tar.subarray(at, at + 512)
		if (block.every((b) => b === 0)) break
		let sum = 0
		for (let i = 0; i < 512; i++) sum += i >= 148 && i < 156 ? 0x20 : (block[i] ?? 0)
		assert.equal(sum, numeric(block, 148, 8), `the header checksum at byte ${at}`)
		const flag = block[156] === 0 ? '0' : String.fromCharCode(block[156] ?? 0)
		const meta = flag === 'x' || flag === 'g' || flag === 'L' || flag === 'K'
		const extended = { ...global, ...pax }
		const size = !meta && extended.size !== undefined ? Number(extended.size) : numeric(block, 124, 12)
		const data = tar.subarray(at + 512, at + 512 + size)
		assert.equal(data.length, size, 'the archive holds every byte its headers promise')
		at += 512 + Math.ceil(size / 512) * 512
		if (flag === 'x') pax = paxRecords(data)
		else if (flag === 'g') global = { ...global, ...paxRecords(data) }
		else if (flag === 'L') longName = field(data, 0, data.length)
		else if (flag !== 'K') {
			let name = field(block, 0, 100)
			// POSIX ustar ("ustar\0") splits a long name into prefix/name; GNU tar ("ustar  ") uses those bytes otherwise.
			if (block.subarray(257, 263).toString('latin1') === 'ustar\0') {
				const prefix = field(block, 345, 155)
				if (prefix) name = `${prefix}/${name}`
			}
			const path = extended.path ?? longName ?? name
			const type =
				flag === '0' || flag === '7' ? 'file' : flag === '5' ? 'dir' : flag === '1' || flag === '2' ? 'link' : 'other'
			entries.push({ path: path.replace(/^\.\//u, '').replace(/\/$/u, ''), type, data })
			pax = {}
			longName = null
		}
	}
	return entries
}

/** The archive's single root folder and its files by their path under it (what Companion keeps after stripping the root). */
function rooted(entries: readonly TarEntry[]): { root: string; files: Map<string, Buffer> } {
	const roots = new Set(entries.map((e) => e.path.split('/')[0]))
	assert.equal(roots.size, 1, `one root folder, not ${[...roots].join(', ')}`)
	const root = [...roots][0] ?? ''
	const files = new Map<string, Buffer>()
	for (const e of entries) if (e.type === 'file') files.set(e.path.slice(root.length + 1), e.data)
	return { root, files }
}

const jsonIn = (files: Map<string, Buffer>, path: string): Record<string, unknown> => {
	const data = files.get(path)
	assert.ok(data, `${path} is in the package`)
	return JSON.parse(data.toString('utf8')) as Record<string, unknown>
}

void test('companion-module-check passes the compiled module with the licence rules enforced', async () => {
	const scripts = sourceJson('package.json').scripts as Record<string, string>
	assert.match(scripts.check ?? '', /companion-module-check/u, '`npm run check` is the check this test runs')
	assert.doesNotMatch(scripts.check ?? '', /--ignore-license-rules/u, 'the licence rules are never waved through')
	compile()
	run(process.execPath, [CHECK], 'companion-module-check')
})

void test('companion-module-build makes pkg.tgz, a gzip archive, with the licence rules enforced', async () => {
	const scripts = sourceJson('package.json').scripts as Record<string, string>
	assert.match(
		scripts.package ?? '',
		/companion-module-build --output pkg\b/u,
		'`npm run package` is the build this test runs',
	)
	assert.doesNotMatch(scripts.package ?? '', /--ignore-license-rules/u, 'the licence rules are never waved through')
	const { tgz, entries, log } = packaged()
	assert.doesNotMatch(log, /^License inventory:/mu, 'every bundled package has its licence on record')
	assert.equal(tgz[0], 0x1f, 'gzip magic')
	assert.equal(tgz[1], 0x8b, 'gzip magic')
	assert.ok(entries.length > 0, 'the archive holds the package')
	assert.ok(statSync(here('pkg.tgz')).size === tgz.length)
	const ignored = readFileSync(here('.gitignore'), 'utf8')
		.split('\n')
		.map((l) => l.trim())
	for (const o of ['dist/', 'pkg/', '*.tgz']) assert.ok(ignored.includes(o), `${o} is git-ignored`)
})

void test('pkg.tgz opens as Companion opens it: one root folder, a strictly valid manifest, ../main.js a bundle inside, no node_modules', async () => {
	const { entries } = packaged()
	const { root, files } = rooted(entries)
	assert.equal(root, 'jammin808-playmap', "the root folder is the manifest's id")
	for (const e of entries) {
		assert.ok(!e.path.startsWith('/') && !e.path.split('/').includes('..'), `${e.path} stays inside the package`)
		assert.ok(!e.path.split('/').includes('node_modules'), `${e.path}: nothing is installed beside the bundle`)
		assert.ok(e.type === 'file' || e.type === 'dir', `${e.path} is a plain file or folder`)
	}
	const manifest = jsonIn(files, 'companion/manifest.json')
	assert.doesNotThrow(() => validateManifest(manifest, false))
	assert.equal(manifest.id, 'jammin808-playmap')
	assert.equal(manifest.version, MODULE_VERSION)
	assert.equal(manifest.license, 'GPL-3.0-only')
	assert.equal(manifest.isPrerelease, false)
	const runtime = manifest.runtime as { type: string; api: string; apiVersion: string; entrypoint: string }
	assert.equal(runtime.type, 'node22')
	assert.equal(runtime.api, 'nodejs-ipc')
	assert.equal(runtime.apiVersion, BASE_VERSION)
	assert.equal(runtime.entrypoint, '../main.js')
	const entry = posix.normalize(posix.join('companion', runtime.entrypoint))
	assert.ok(!entry.startsWith('..'), 'the entrypoint lands inside the package')
	const bundle = files.get(entry)
	assert.ok(bundle && bundle.length > 0, `the entrypoint ${entry} is a file in the package`)
	assert.deepEqual(manifest.bonjourQueries, { node: { type: 'playmap', protocol: 'tcp' } })
})

void test('the package carries its licence and help: package.json GPL-3.0-only, the LICENSE inventory, COPYING, HELP.md', async () => {
	const { files } = rooted(packaged().entries)
	const pkg = jsonIn(files, 'package.json')
	assert.equal(pkg.type, 'module')
	assert.equal(pkg.license, 'GPL-3.0-only')
	assert.equal(pkg.version, MODULE_VERSION)
	assert.equal(pkg.version, sourceJson('package.json').version)
	assert.deepEqual(pkg.dependencies, {}, 'everything is bundled; nothing is installed at load')
	const licence = files.get('LICENSE')
	assert.ok(licence && licence.toString('utf8').trim().length > 0, "the tools' legal inventory is in the package")
	// The inventory names every package in the bundle: the module under its packaged licence, and what it bundles.
	const named = licence
		.toString('utf8')
		.split('\n')
		.filter((l) => l.startsWith('Packages: '))
		.flatMap((l) => l.slice('Packages: '.length).split(', '))
		.map((p) => p.replace(/ \(.*\)$/u, ''))
	assert.ok(
		named.includes(`${String(sourceJson('package.json').name)}@${MODULE_VERSION} — GPL-3.0-only`),
		named.join(' | '),
	)
	assert.ok(named.includes(`@companion-module/base@${BASE_VERSION} — MIT`), named.join(' | '))
	assert.ok(named.includes(`ws@${versionOf('ws')} — MIT`), named.join(' | '))
	const copying = files.get('COPYING')
	assert.ok(copying, 'COPYING travels with the package')
	const lines = copying
		.toString('utf8')
		.split('\n')
		.map((l) => l.trim())
		.filter((l) => l !== '')
	assert.equal(lines[0], 'GNU GENERAL PUBLIC LICENSE')
	assert.equal(lines[1], 'Version 3, 29 June 2007')
	assert.equal(sha256(copying), GPL3_SHA256)
	assert.ok(
		files.get('companion/HELP.md')?.equals(readFileSync(here('companion/HELP.md'))),
		'HELP.md travels unchanged',
	)
})

void test('the bundle imports under Node on its own: the module class as the default export, its upgrade scripts and version', async () => {
	const { root, files } = rooted(packaged().entries)
	const manifest = jsonIn(files, 'companion/manifest.json')
	const dir = mkdtempSync(join(tmpdir(), 'playmap-pkg-'))
	scratch.push(dir)
	for (const [path, data] of files) {
		const out = join(dir, ...path.split('/'))
		assert.ok(out.startsWith(dir + sep), `${root}/${path} stays inside the folder`)
		mkdirSync(dirname(out), { recursive: true })
		writeFileSync(out, data)
	}
	const entry = join(dir, 'companion', (manifest.runtime as { entrypoint: string }).entrypoint)
	const loaded = (await import(pathToFileURL(entry).href)) as {
		default?: unknown
		UpgradeScripts?: unknown
		MODULE_VERSION?: unknown
	}
	assert.equal(typeof loaded.default, 'function', 'the module class is the default export')
	const proto = (loaded.default as { prototype?: Record<string, unknown> }).prototype
	assert.equal(typeof proto?.init, 'function')
	assert.equal(typeof proto?.getConfigFields, 'function')
	assert.ok(Array.isArray(loaded.UpgradeScripts), 'the upgrade scripts are exported')
	assert.equal(loaded.MODULE_VERSION, MODULE_VERSION)
})
