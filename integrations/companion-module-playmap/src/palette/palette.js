// The colour language: one hue per kind of thing on a key, one treatment per state it can be in,
// so a look key, a cue key and a screen key are told apart at a glance and "on air", "in the
// preview", "changed since", "held", "in trouble" read the same on every key that has them.
// The same table lives in Patterns' Core (CompanionPalette.cs) and a test on the desk's side reads
// this file and checks the numbers agree, so the deck and the desk never drift into two languages.
//
// Numbers are plain [r, g, b] triplets, not Companion colour numbers, so the file is data — the
// desk's test parses it, and rgb() turns a triplet into what Companion wants.

/** Named colours, by what they mean. */
export const COLOURS = {
	white: [255, 255, 255],
	ink: [14, 15, 19], // dark text on a light key
	dark: [20, 22, 28], // an idle key
	dim: [12, 13, 16], // a bank key with nothing behind it
	dimText: [80, 84, 94],
	green: [30, 158, 90], // on air, armed, running
	brightGreen: [46, 230, 138], // a mark: the standby cue, review on
	amber: [255, 194, 77], // preview, held, changed since, waiting
	orange: [255, 138, 0], // late, a screen gone its own way
	red: [224, 52, 46], // a lower third on screen, the stream live, a failed cue, black on its own
	blackout: [200, 0, 0],
	outputsOn: [0, 100, 50],
	off: [90, 30, 30], // an OFF key
	steel: [0, 90, 130], // the presenter's things: a web page, a deck, a clip, EDIT SAFE, an advert
	blue: [0, 100, 160], // audio and VOGs
	music: [20, 120, 90],
	stingerBrown: [190, 120, 0],
	screenOn: [0, 120, 60],
	lock: [160, 110, 0],
	sky: [53, 170, 255], // the overlays: clock, message, logo, PiP, weather
	cyan: [53, 224, 208], // frozen
	// The nodes around the desk, one hue per kind, and the twin.
	desk: [0, 140, 200],
	caller: [130, 80, 220],
	arcade: [255, 70, 150],
	timer: [0, 170, 120],
	gone: [110, 40, 40], // a node not heard for a while
}

/** A triplet as Companion's colour number (0xRRGGBB). */
export const rgb = ([r, g, b]) => ((r & 0xff) << 16) | ((g & 0xff) << 8) | (b & 0xff)

/** A style with a dark ground and white text: an idle key. */
export const idle = () => ({ color: rgb(COLOURS.white), bgcolor: rgb(COLOURS.dark) })

/** A style on a named colour, with the text colour that reads on it. */
export function on(name) {
	const bg = COLOURS[name]
	if (!bg) throw new Error(`No colour named ${name}`)
	return { bgcolor: rgb(bg), color: rgb(lightGround(name) ? COLOURS.ink : COLOURS.white) }
}

/** The light grounds, which take dark text. */
function lightGround(name) {
	return name === 'amber' || name === 'orange' || name === 'brightGreen' || name === 'sky' || name === 'cyan'
}

/** The bank key with nothing behind it. */
export const empty = () => ({ bgcolor: rgb(COLOURS.dim), color: rgb(COLOURS.dimText) })

/**
 * What each kind of thing wears in each state — the table the feedbacks' default styles and the
 * presets are built from. A state a kind does not have is not in its row.
 */
export const STATES = {
	look: { air: 'green', preview: 'amber', edited: 'amber', screensOff: 'orange' },
	cue: { armed: 'green', hold: 'amber', confirm: 'amber', failed: 'red', standby: 'brightGreen', late: 'orange' },
	transport: { blackout: 'blackout', outputsOn: 'outputsOn', off: 'off', frozen: 'cyan', review: 'brightGreen', editSafe: 'steel', black: 'red' },
	screen: { enabled: 'screenOn', locked: 'lock', armed: 'green', own: 'steel', black: 'red', offLook: 'orange', pattern: 'green', fault: 'red', group: 'sky', sound: 'blue' },
	stinger: { playing: 'stingerBrown', hold: 'amber' },
	vog: { playing: 'blue' },
	lowerThird: { on: 'red', preview: 'amber', edited: 'amber', person: 'red', timed: 'amber' },
	audio: { playing: 'blue', follow: 'sky' },
	music: { playing: 'music' },
	overlay: { on: 'sky' },
	countdown: { running: 'green', over: 'red' },
	presenter: { on: 'steel', ended: 'amber', out: 'red' },
	install: { schedule: 'green', announcement: 'amber', advert: 'steel' },
	signal: { match: 'green', mismatch: 'red', partial: 'amber' },
	rig: { same: 'green', drift: 'amber', commissioned: 'green' },
	eye: { red: 'red', amber: 'amber', green: 'green' },
	take: { next: 'amber', sting: 'stingerBrown', landing: 'stingerBrown' },
	library: { selected: 'amber' },
	midi: { learning: 'amber' },
	nav: { on: 'green', live: 'red', preview: 'amber', stack: 'blue', go: 'steel', ask: 'music', warn: 'orange', tile: 'lock', plain: 'dark', drawer: 'sky', level: 'steel', run: 'red', follow: 'green', desk: 'green', settings: 'amber', recording: 'red' },
	stream: { active: 'red', trouble: 'amber' },
	device: { open: 'green', fault: 'red' },
	tone: { on: 'amber' },
	duck: { on: 'amber' },
	weather: { on: 'sky' },
	node: { desk: 'desk', caller: 'caller', arcade: 'arcade', timer: 'timer', gone: 'gone' },
	twin: { inStep: 'green', silent: 'red', tookOver: 'amber', standby: 'steel' },
	stage: { running: 'green', amber: 'amber', red: 'red', pending: 'amber', paused: 'sky' },
}

/** The style for a kind in a state: style('look', 'air'). */
export function style(kind, state) {
	const row = STATES[kind]
	if (!row) throw new Error(`No kind ${kind} in the palette`)
	const name = row[state]
	if (!name) throw new Error(`${kind} has no state ${state}`)
	return on(name)
}
