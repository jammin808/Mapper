// The module's side of the contracts it shares with Patterns, the node and Companion: the palette is Patterns' byte for byte and
// PlayMap only adds to it (no colour or kind of Patterns' is recoloured); the version is one number in package.json, the manifest
// and MODULE_VERSION, and the one HELLO and PRESENCE announce; the line protocol is the one the node speaks; the manifest is one
// Companion accepts, naming the installed API, the build's entrypoint and the node's mDNS service; the licences are the owner's
// (MIT source, GPL-3.0-only package, both texts in the tree); config ids are unique and the token is a secret; every action and
// feedback id, the bundled vocabulary's or a node's own, is one Companion keeps for good.
import test from 'node:test'
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, join } from 'node:path'
import { InstanceStatus, type SomeCompanionConfigField } from '@companion-module/base'
import { validateManifest } from '@companion-module/base/manifest'
import { InstanceWrapper } from '@companion-module/host'
import { FakeNode } from './fake-node.ts'
import { RecordingHost, boot, configFor } from './harness.ts'
import PlayMapInstance, { UpgradeScripts } from '../src/main.ts'
import { configFields } from '../src/config.ts'
import { COLOURS, STATES, rgb } from '../src/palette/palette.js'
import { COLOURS_PLAYMAP, STATES_PLAYMAP, colourOf, css, rgbOf } from '../src/palette/playmap.ts'
import { KNOWN_LINE_PROTOCOL, MODULE_VERSION } from '../src/version.ts'
import { Words } from '../src/words.ts'

/** The module's own folder, and its companion folder (where the manifest's paths start). */
const MODULE_URL = new URL('../', import.meta.url)
const COMPANION_URL = new URL('../companion/', import.meta.url)
const bytes = (path: string): Buffer => readFileSync(new URL(`../${path}`, import.meta.url))
const textOf = (path: string): string => bytes(path).toString('utf8')
const jsonOf = (path: string): Record<string, unknown> => JSON.parse(textOf(path)) as Record<string, unknown>
const sha256 = (data: Buffer): string => createHash('sha256').update(data).digest('hex')
const requireHere = createRequire(import.meta.url)

/** Patterns' palette.js, pinned: the same bytes the desk's test and Patterns' module read. */
const PATTERNS_PALETTE_SHA256 = 'b0a8a8ce9c56b16ce8eee24bd59f34750d8089e4b0fe9a5e774d3df0c8ffb647'
/** The FSF's GPL-3.0 text (gpl-3.0.txt, "Version 3, 29 June 2007"), byte for byte. */
const GPL3_SHA256 = '3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986'
/** The id Companion keeps an action or feedback by; a shipped one never changes. */
const STABLE_ID = /^[a-z0-9_]+$/u

const PATTERNS_COLOURS = COLOURS as Record<string, number[]>
const PATTERNS_STATES = STATES as Record<string, Record<string, string>>
const hexOf = (c: readonly number[]): string => '#' + c.map((v) => v.toString(16).padStart(2, '0')).join('')

void test("the palette is Patterns' byte for byte, and no formatter or linter may rewrite it", async () => {
	assert.equal(sha256(bytes('src/palette/palette.js')), PATTERNS_PALETTE_SHA256)
	const prettierIgnored = textOf('.prettierignore')
		.split('\n')
		.map((l) => l.trim())
	assert.ok(prettierIgnored.includes('src/palette/palette.js'), '`npm run format` must leave the vendored bytes alone')
	assert.ok(
		textOf('eslint.config.mjs').includes("'src/palette/palette.js'"),
		'the linter leaves the vendored file alone',
	)
})

void test("PlayMap's palette only adds: no colour or kind of Patterns' is recoloured, and every PlayMap state names a colour", async () => {
	const patternsColours = Object.keys(PATTERNS_COLOURS)
	const patternsKinds = Object.keys(PATTERNS_STATES)
	assert.deepEqual(
		Object.keys(COLOURS_PLAYMAP).filter((c) => patternsColours.includes(c)),
		[],
		'a PlayMap colour named like a Patterns colour would recolour it',
	)
	assert.deepEqual({ ...COLOURS_PLAYMAP }, { playmap: [255, 120, 40] }, 'one new colour: a PlayMap node')
	assert.deepEqual(
		Object.keys(STATES_PLAYMAP).filter((k) => patternsKinds.includes(k)),
		[],
		'a PlayMap kind named like a Patterns kind would recolour its states',
	)
	for (const [kind, row] of Object.entries(STATES_PLAYMAP))
		for (const [state, colour] of Object.entries(row)) {
			assert.ok(
				colour in PATTERNS_COLOURS || colour in COLOURS_PLAYMAP,
				`${kind}.${state} names ${colour}, which neither palette has`,
			)
			assert.equal(colourOf(kind, state), colour)
			assert.match(css(colour), /^#[0-9a-f]{6}$/u)
		}
	// What a key wears for a Patterns kind or colour is Patterns' own, through every helper the module draws with.
	for (const [kind, row] of Object.entries(PATTERNS_STATES))
		for (const [state, colour] of Object.entries(row)) assert.equal(colourOf(kind, state), colour, `${kind}.${state}`)
	for (const [name, triplet] of Object.entries(PATTERNS_COLOURS)) {
		assert.equal(css(name), hexOf(triplet), name)
		assert.equal(rgbOf(name), rgb(triplet as [number, number, number]), name)
	}
})

void test('the version is one number: package.json, companion/manifest.json and MODULE_VERSION', async () => {
	const pkg = jsonOf('package.json')
	const manifest = jsonOf('companion/manifest.json')
	assert.match(MODULE_VERSION, /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u)
	assert.equal(pkg.version, MODULE_VERSION)
	assert.equal(manifest.version, MODULE_VERSION)
})

void test('the line protocol is the fixture node\'s: WHOAMI proto 1, no "update the module" warning; HELLO and PRESENCE carry the version', async () => {
	assert.equal((new FakeNode().whoami as { proto: number }).proto, 1)
	assert.equal(KNOWN_LINE_PROTOCOL, 1)
	const node = await new FakeNode().start()
	const m = await boot(configFor(node.webPort))
	try {
		await m.host.until(() => m.host.variables.get('node_protocol') === 1, 'node_protocol from WHOAMI')
		assert.equal(m.host.variables.get('module_behind'), false)
		await m.host.until(() => m.host.status?.status === InstanceStatus.Ok, 'status ok')
		assert.ok(
			!m.host.statuses.some((s) => s.message === Words.newerProtocol(1, KNOWN_LINE_PROTOCOL)),
			'a node on the known protocol is never told to update',
		)
		const hello = await node.heard(/ HELLO /u)
		assert.ok(hello.endsWith(` module=${MODULE_VERSION}`), hello)
		const presence = await node.heard(/ PRESENCE \{/u)
		const announced = JSON.parse(presence.slice(presence.indexOf('{'))) as { kind: string; module: string }
		assert.equal(announced.kind, 'companion')
		assert.equal(announced.module, MODULE_VERSION)
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test('a node a protocol ahead is still driven, with the warning to update the module and module_behind true', async () => {
	const ahead = KNOWN_LINE_PROTOCOL + 1
	const node = await new FakeNode({ whoami: { ...(new FakeNode().whoami as object), proto: ahead } }).start()
	const m = await boot(configFor(node.webPort))
	try {
		await m.host.until(
			() =>
				m.host.status?.status === InstanceStatus.UnknownWarning &&
				m.host.status.message === Words.newerProtocol(ahead, KNOWN_LINE_PROTOCOL),
			'the newer-protocol warning',
		)
		await m.host.until(() => m.host.variables.get('module_behind') === true, 'module_behind')
		assert.equal(m.host.variables.get('node_protocol'), ahead)
		assert.equal(m.instance.linked(), true, 'the deck still drives the node')
	} finally {
		await m.stop()
		await node.stop()
	}
})

void test("the manifest is one Companion accepts: strict validation, the installed API, the build's entrypoint, the node's mDNS service", async () => {
	const manifest = jsonOf('companion/manifest.json')
	const pkg = jsonOf('package.json')
	const base = requireHere('@companion-module/base/package.json') as { version: string }
	assert.doesNotThrow(() => validateManifest(manifest, false))
	assert.equal(manifest.type, 'connection')
	assert.equal(manifest.id, 'jammin808-playmap')
	assert.equal(manifest.name, 'jammin808-playmap')
	assert.equal(manifest.shortname, 'playmap')
	assert.deepEqual(manifest.legacyIds, [])
	const runtime = manifest.runtime as { type: string; api: string; apiVersion: string; entrypoint: string }
	assert.equal(runtime.type, 'node22')
	assert.equal(runtime.api, 'nodejs-ipc')
	assert.equal(runtime.apiVersion, base.version, 'the API the module was built against is the one installed')
	assert.equal(
		new URL(runtime.entrypoint, COMPANION_URL).href,
		new URL(String(pkg.main), MODULE_URL).href,
		"the manifest's entrypoint is the file package.json's main names (what the build compiles)",
	)
	assert.deepEqual(manifest.bonjourQueries, { node: { type: 'playmap', protocol: 'tcp' } })
	// Companion feeds a bonjour-device field from the manifest query of the same id.
	const bonjour = manifest.bonjourQueries as Record<string, unknown>
	const queries = Object.keys(bonjour)
	const pickers = configFields().filter((f) => f.type === 'bonjour-device')
	assert.ok(pickers.length > 0, 'the node can be picked from the network')
	for (const f of pickers) assert.ok(queries.includes(f.id), `bonjour-device field ${f.id} has no query`)
})

void test("the licences are the owner's: MIT source (LICENSE), GPL-3.0-only package (COPYING), each text in full", async () => {
	assert.equal(jsonOf('package.json').license, 'MIT')
	assert.equal(jsonOf('companion/manifest.json').license, 'GPL-3.0-only')

	const licence = textOf('LICENSE')
	const lines = licence
		.split('\n')
		.map((l) => l.trim())
		.filter((l) => l !== '')
	assert.match(lines[0] ?? '', /^(?:The )?MIT License$/u)
	assert.ok(lines.includes('Copyright (c) 2026 jammin808'), 'the copyright line names the owner')
	// The permission and warranty text is MIT's, word for word (compared with the base's own MIT LICENSE, line wrapping aside).
	const mitBody = (text: string): string => {
		const from = text.indexOf('Permission is hereby granted')
		assert.ok(from >= 0, 'the MIT permission text')
		return text.slice(from).replace(/\s+/gu, ' ').trim()
	}
	const baseLicence = readFileSync(
		join(dirname(requireHere.resolve('@companion-module/base/package.json')), 'LICENSE'),
		'utf8',
	)
	assert.equal(mitBody(licence), mitBody(baseLicence))

	const copying = bytes('COPYING')
	const copyingLines = copying
		.toString('utf8')
		.split('\n')
		.map((l) => l.trim())
		.filter((l) => l !== '')
	assert.equal(copyingLines[0], 'GNU GENERAL PUBLIC LICENSE')
	assert.equal(copyingLines[1], 'Version 3, 29 June 2007')
	assert.equal(sha256(copying), GPL3_SHA256, "COPYING is the FSF's GPL-3.0 text, unedited")
})

void test('every config field id is unique, and the pairing token is a secret: Companion keeps it in secrets, never config', async () => {
	const host = new RecordingHost()
	PlayMapInstance.testHooks = {}
	const wrapper = new InstanceWrapper('contract-first-init', host, PlayMapInstance, UpgradeScripts, '2.2.1')
	await wrapper.init({ label: 'FOH deck', isFirstInit: true, config: {}, secrets: {}, lastUpgradeIndex: -1 })
	try {
		const fields: SomeCompanionConfigField[] = await wrapper.getConfigFields()
		const ids = fields.map((f) => f.id)
		assert.deepEqual(
			ids.filter((id, i) => ids.indexOf(id) !== i),
			[],
			'a duplicated id would make two fields share one value',
		)
		const token = fields.filter((f) => f.id === 'token')
		assert.equal(token.length, 1)
		assert.equal(token[0]?.type, 'secret-text')
		// A first init writes every default: the token's into secrets, nothing of it into config.
		const first = host.saved[0] as { config: Record<string, unknown>; secrets: Record<string, unknown> } | undefined
		assert.ok(first, 'a first init saves the defaults')
		assert.ok('token' in first.secrets, 'the token lives in the secrets store')
		assert.ok(!('token' in first.config), 'the token is never in the config store')
		for (const f of fields) if (f.type !== 'secret-text') assert.ok(!(f.id in first.secrets), `${f.id} is not a secret`)
		assert.equal(host.status?.status, InstanceStatus.BadConfig, 'no node picked yet')
	} finally {
		await wrapper.destroy()
	}
})

void test('every action and feedback id is one Companion keeps (^[a-z0-9_]+$), from the bundled vocabulary and from a node that adds verbs', async () => {
	const offline = await boot({})
	try {
		assert.ok(offline.host.actions.size > 0 && offline.host.feedbacks.size > 0, 'definitions exist before any link')
		for (const id of offline.host.actions.keys()) assert.match(id, STABLE_ID, `action ${id}`)
		for (const id of offline.host.feedbacks.keys()) assert.match(id, STABLE_ID, `feedback ${id}`)
	} finally {
		await offline.stop()
	}

	// A node whose vocabulary has a package's verb in its own spelling (a dot, a hyphen, a digit).
	const rows = JSON.parse(textOf('src/catalog/verbs.json')) as Record<string, unknown>[]
	const param = { name: 'mix', kind: 'number', min: 0, max: 1 }
	rows.push({
		name: 'EFFECT.X3 SET-MIX',
		syntax: 'EFFECT.X3 SET-MIX <mix>',
		words: "Sets the effect's mix",
		category: 'effects',
		risk: 'low',
		mutates: true,
		show: 'live',
		available: true,
		pairedOnly: false,
		forms: ['EFFECT.X3 SET-MIX <mix>'],
		params: [param],
		formParams: [[param]],
		press: 'instant',
	})
	const node = await new FakeNode().start()
	node.handlers.set('VERBS', () => 'OK ' + JSON.stringify(rows))
	const m = await boot(configFor(node.webPort))
	try {
		await m.host.until(() => m.host.actions.has('v_effect_x3_set_mix'), "the node's own verb as an action")
		await m.host.until(() => m.host.variables.get('node_name') === 'Test node', 'STATE in')
		for (const id of m.host.actions.keys()) assert.match(id, STABLE_ID, `action ${id}`)
		for (const id of m.host.feedbacks.keys()) assert.match(id, STABLE_ID, `feedback ${id}`)
	} finally {
		await m.stop()
		await node.stop()
	}
})
