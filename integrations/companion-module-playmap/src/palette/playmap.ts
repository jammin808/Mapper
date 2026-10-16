import { COLOURS, STATES } from './palette.js'

/** PlayMap's additions to Patterns' colour language: one new colour and new kinds only — a Patterns colour or kind is never
 * recoloured here (a test holds it), so a PlayMap key and a Patterns key mean the same thing by the same colour. */
export const COLOURS_PLAYMAP: Record<string, readonly [number, number, number]> = {
	playmap: [255, 120, 40], // a PlayMap node in Patterns' Eye
}

export const STATES_PLAYMAP: Record<string, Record<string, string>> = {
	playlist: { playing: 'green', paused: 'amber', cued: 'amber', ended: 'steel', idle: 'dark' },
	item: { air: 'green', next: 'amber', cued: 'amber', missing: 'red', removed: 'gone' },
	output: { green: 'green', amber: 'amber', red: 'red', off: 'off', pattern: 'sky' },
	projector: { on: 'green', warming: 'amber', standby: 'dark', shutter: 'off', fault: 'red', moved: 'orange' },
	surface: { shown: 'screenOn', hidden: 'dark', locked: 'lock' },
	scan: { running: 'amber', done: 'green', failed: 'red' },
	calibration: { good: 'green', fair: 'amber', poor: 'red', moved: 'orange', fallback: 'steel' },
	show: { armed: 'red', rehearsal: 'dark', blind: 'steel', locked: 'lock', panic: 'blackout' },
	ai: { proposal: 'music', high: 'red' },
	receipt: { pending: 'amber', failed: 'red' },
	fleet: { leader: 'desk', follower: 'timer', backup: 'steel', gone: 'gone', playmap: 'playmap' },
	light: { green: 'green', amber: 'amber', red: 'red', grey: 'dark' },
}

const ALL_COLOURS: Record<string, readonly number[]> = { ...(COLOURS as Record<string, number[]>), ...COLOURS_PLAYMAP }

/** A named colour as CSS "#rrggbb" (API 2.2 takes CSS colours in elements and styles). */
export function css(name: string): string {
	const c = ALL_COLOURS[name]
	if (!c) throw new Error(`No colour named ${name}`)
	return '#' + c.map((v) => v.toString(16).padStart(2, '0')).join('')
}

/** A named colour as Companion's colour number (0xRRGGBB). */
export function rgbOf(name: string): number {
	const c = ALL_COLOURS[name]
	if (!c) throw new Error(`No colour named ${name}`)
	return ((c[0] ?? 0) << 16) | ((c[1] ?? 0) << 8) | (c[2] ?? 0)
}

/** The colour a kind wears in a state, from Patterns' table or PlayMap's. */
export function colourOf(kind: string, state: string): string {
	const row = (STATES as Record<string, Record<string, string>>)[kind] ?? STATES_PLAYMAP[kind]
	const name = row?.[state]
	if (!name) throw new Error(`${kind} has no state ${state}`)
	return name
}

/** Text that reads on a named ground: dark on the light grounds, white elsewhere (Patterns' rule). */
export function inkOn(name: string): string {
	return ['amber', 'orange', 'brightGreen', 'sky', 'cyan'].includes(name) ? css('ink') : css('white')
}

/** An expression mapping a light word ("green", "amber", "red", "grey") to its colour: a value feedback's style override. */
export function lightExpression(value = '$(this:value)'): string {
	return `${value} == 'green' ? '${css('green')}' : ${value} == 'amber' ? '${css('amber')}' : ${value} == 'red' ? '${css('red')}' : '${css('dark')}'`
}
