import type { JsonObject, SomeCompanionConfigField } from '@companion-module/base'
import type { NodeTarget } from './link/session.ts'
import { RULES } from './live.ts'

/** The preset groups a connection can tick, so a deck lists only what it uses. */
export const GROUPS = [
	['show', 'Show: GO, panic, blackout, master'],
	['playlists', 'Playlists: an item key per item, by id'],
	['outputs', 'Outputs'],
	['projectors', 'Projectors'],
	['surfaces', 'Surfaces'],
	['live', 'The Live page (32 slots)'],
	['encoders', 'Encoders (Stream Deck +, Studio)'],
	['sections', "Every package's section (effects, calibration, tempo …)"],
	['fleet', 'Fleet: the machines this node hears'],
] as const

export type GroupId = (typeof GROUPS)[number][0]

/** The connection's config as Companion keeps it: node, host, port, channel, wire_port, tls_pin, the g_<group> ticks, bank_size,
 * thumb_size, min_go_ms, interpolate, live_rule. Read through typed guards (a hand-edited config is not trusted). */
export type PlayMapConfig = JsonObject

/** The connection's secrets: the pairing token. */
export type PlayMapSecrets = JsonObject

export const DEFAULT_PORT = 9940
export const DEFAULT_WIRE_PORT = 9941

export function configFields(): SomeCompanionConfigField[] {
	return [
		{
			type: 'static-text',
			id: 'info',
			width: 12,
			label: 'PlayMap',
			value:
				'Pick the node from the list (it announces itself as _playmap._tcp), or type its address. The pairing token is in the Network drawer on the PlayMap desk.',
		},
		{ type: 'bonjour-device', id: 'node', label: 'Node on the network', width: 6 },
		{
			type: 'textinput',
			id: 'host',
			label: 'Address',
			width: 6,
			default: '',
			isVisibleExpression: '!$(options:node)',
		},
		{
			type: 'number',
			id: 'port',
			label: 'Web port (the line channel)',
			width: 3,
			default: DEFAULT_PORT,
			min: 1,
			max: 65535,
			isVisibleExpression: '!$(options:node)',
		},
		{
			type: 'dropdown',
			id: 'channel',
			label: 'Link',
			width: 3,
			default: 'ws',
			choices: [
				{ id: 'ws', label: 'WebSocket (ws)' },
				{ id: 'wss', label: 'WebSocket over TLS (wss)' },
				{ id: 'tcp', label: 'TCP line wire' },
			],
		},
		{
			type: 'number',
			id: 'wire_port',
			label: 'TCP line wire port',
			width: 3,
			default: DEFAULT_WIRE_PORT,
			min: 1,
			max: 65535,
		},
		{
			type: 'textinput',
			id: 'tls_pin',
			label: "The node's certificate fingerprint (kept on first link; clear it to trust a new one)",
			width: 9,
			default: '',
			isVisibleExpression: "$(options:channel) == 'wss'",
		},
		{
			type: 'secret-text',
			id: 'token',
			label: 'Pairing token',
			width: 6,
			default: '',
			tooltip: 'In the Network drawer on the PlayMap desk; it never crosses the wire (the link proves it)',
		},
		...GROUPS.map(([id, label]): SomeCompanionConfigField => ({
			type: 'checkbox',
			id: `g_${id}`,
			label,
			width: 4,
			default: true,
		})),
		{ type: 'number', id: 'bank_size', label: 'Bank size', width: 3, default: 8, min: 1, max: 64 },
		{ type: 'number', id: 'thumb_size', label: 'Picture size (px)', width: 3, default: 96, min: 72, max: 144 },
		{
			type: 'number',
			id: 'min_go_ms',
			label: 'Double-GO guard (ms)',
			width: 3,
			default: 300,
			min: 0,
			max: 5000,
		},
		{ type: 'checkbox', id: 'interpolate', label: 'Clocks run between pushes (10 Hz)', width: 3, default: true },
		{
			type: 'dropdown',
			id: 'live_rule',
			label: "The Live page's source",
			width: 4,
			default: 'focused',
			choices: RULES.map((r) => ({ id: r, label: r })),
			allowCustom: true,
		},
	]
}

/** Where the link goes: the picked node (Companion hands "address:port") or the typed address. Null when neither is set. */
export function connectionTarget(config: PlayMapConfig): NodeTarget | null {
	const channel: NodeTarget['channel'] = config.channel === 'wss' ? 'wss' : config.channel === 'tcp' ? 'tcp' : 'ws'
	const wirePort = num(config.wire_port, DEFAULT_WIRE_PORT)
	const pin = typeof config.tls_pin === 'string' ? config.tls_pin.trim() : ''
	const picked = typeof config.node === 'string' ? config.node.trim() : ''
	if (picked) {
		const m = /^(.*):(\d+)$/u.exec(picked)
		const host = (m?.[1] ?? picked).replace(/^\[|\]$/g, '')
		return { host, port: m ? Number(m[2]) : DEFAULT_PORT, wirePort, channel, pin }
	}
	const host = typeof config.host === 'string' ? config.host.trim() : ''
	if (!host) return null
	return { host, port: num(config.port, DEFAULT_PORT), wirePort, channel, pin }
}

/** A group's tick (on unless unticked). */
export function groupOn(config: PlayMapConfig, id: GroupId): boolean {
	return config[`g_${id}`] !== false
}

export function num(v: unknown, fallback: number): number {
	const n = typeof v === 'number' ? v : typeof v === 'string' && v.trim() !== '' ? Number(v) : NaN
	return Number.isFinite(n) ? n : fallback
}
