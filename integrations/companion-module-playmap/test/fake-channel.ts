// An in-memory line channel for tests on fake timers: the test is the node, answering or keeping silent line by line.
import type { ChannelEvents, ChannelFactory, ChannelTarget, LineChannel } from '../src/link/channel.ts'

export class FakeChannel implements LineChannel {
	readonly kind: ChannelTarget['kind']
	readonly target: ChannelTarget
	readonly events: ChannelEvents
	readonly sent: string[] = []
	closed = false

	constructor(target: ChannelTarget, events: ChannelEvents) {
		this.kind = target.kind
		this.target = target
		this.events = events
	}

	send(line: string): void {
		if (!this.closed) this.sent.push(line)
	}

	close(): void {
		if (this.closed) return
		this.closed = true
		this.events.closed({ code: 1000, reason: 'closed' })
	}

	/** The node says a line. */
	say(line: string): void {
		this.events.line(line)
	}

	/** Answers the last line sent that starts with `verb` (after its id) with OK and a payload. */
	ok(verb: string, payload = ''): void {
		const line = [...this.sent].reverse().find((l) => l.replace(/^@\S+ /u, '').startsWith(verb))
		if (!line) throw new Error(`nothing sent starts with ${verb}: ${this.sent.join(' | ')}`)
		const id = line.split(' ')[0] ?? ''
		this.say(`OK ${id}${payload ? ' ' + payload : ''}`)
	}

	/** Answers every PING sent and not answered yet. */
	pong(nodeNow: number): void {
		for (const l of this.sent.splice(0))
			if (/^@\S+ PING /u.test(l)) this.say(`OK ${l.split(' ')[0]} PONG ${l.split(' ')[2]} ${nodeNow} ${nodeNow}`)
	}
}

/** A factory recording every channel it opened. */
export function fakeChannels(): { factory: ChannelFactory; opened: FakeChannel[] } {
	const opened: FakeChannel[] = []
	return {
		opened,
		factory: (target, events) => {
			const c = new FakeChannel(target, events)
			opened.push(c)
			return c
		},
	}
}

/** The fake clock a test drives (its own context's, so nothing carries over to the next test). */
export interface FakeTimers {
	tick(ms: number): void
}

/** Moves fake time on in steps of `step` ms, letting promise callbacks run between (an interval fires once per step it is due). */
export async function advance(timers: FakeTimers, ms: number, step = 50): Promise<void> {
	for (let t = 0; t < ms; t += step) {
		timers.tick(Math.min(step, ms - t))
		await settle()
	}
}

/** Lets promise callbacks and the host's immediate work run (setImmediate is never faked here). */
export async function settle(): Promise<void> {
	for (let i = 0; i < 5; i++) await new Promise((resolve) => setImmediate(resolve))
}
