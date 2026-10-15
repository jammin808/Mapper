import { quote } from './link/grammar.ts'
import { MODULE_VERSION } from './version.ts'

/** Every line the module writes itself — its smart actions', its presets' and its link's — built here, so test/lines.txt (written
 * from these builders) is what the module sends. */
export const Lines = {
	go: (playlist: string, expect: string | null): string =>
		`PLAYLIST GO ${quote(playlist)}${expect ? ` EXPECT ${quote(expect)}` : ''}`,
	next: (playlist: string, expect: string | null): string =>
		`PLAYLIST NEXT ${quote(playlist)}${expect ? ` EXPECT ${quote(expect)}` : ''}`,
	prev: (playlist: string): string => `PLAYLIST PREV ${quote(playlist)}`,
	goItem: (playlist: string, item: string): string => `PLAYLIST GO ${quote(playlist)} ${quote(item)}`,
	panic: (outcome: string): string => (outcome ? `PANIC ${outcome}` : 'PANIC'),
	master: (level: number): string => `MASTER ${level}`,
	rate: (playlist: string, rate: number): string => `VIDEO RATE ${rate} ON ${quote(playlist)}`,
	seek: (playlist: string, seconds: number): string => `VIDEO SEEK ${seconds} ON ${quote(playlist)}`,
	generatorBy: (generator: string, param: string, by: number): string =>
		`GENERATOR SET ${quote(generator)} ${quote(param)} BY ${by}`,
	lensBy: (device: string, motor: string, by: number): string => `DEVICE LENS ${quote(device)} ${motor} BY ${by}`,
	nudge: (id: string, path: string, by: number): string =>
		`NUDGE ${quote(id)} ${quote(path)} ${by >= 0 ? '+' : ''}${by}`,
	thumb: (ref: string, size: number, etag?: string): string =>
		`THUMB ${quote(ref)} ${size}x${size}` + (etag ? ` IF ${quote(etag)}` : ''),
	hello: (name: string): string => `HELLO ${name} module=${MODULE_VERSION}`,
	presence: (json: string): string => `PRESENCE ${json}`,
	watchThumbs: (refs: readonly string[]): string => ['SUBSCRIBE THUMBS', ...refs.map(quote)].join(' '),
	eventsSince: (seq: number): string => `EVENTS SINCE ${seq}`,
	proof: (hex: string): string => `AUTH-PROOF ${hex}`,
	ping: (t1: number): string => `PING ${t1}`,
	subscribe: (topic: 'INDEX' | 'EVENTS'): string => `SUBSCRIBE ${topic}`,
	blackoutToggle: 'BLACKOUT TOGGLE',
	freezeToggle: 'FREEZE TOGGLE',
	identifyOutput: (output: string): string => `OUTPUT IDENTIFY ${quote(output)}`,
	whoami: 'WHOAMI',
	verbs: 'VERBS',
	index: 'INDEX',
} as const
