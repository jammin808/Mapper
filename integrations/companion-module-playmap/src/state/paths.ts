/** Every STATE path this module reads, in one table: the code reads STATE only through these names (`at`, `field`), and
 * `npm run lines` writes them to test/state-paths.txt, which the node's contract test resolves against a STATE the node wrote. "[]"
 * steps into each element of an array; an element's field is read with `field(element, P.x)`. */
export const P = {
	seq: 'seq',
	id: 'id',
	name: 'name',
	version: 'version',
	at: 'at',
	light: 'light',
	headline: 'headline',

	blackout: 'air.blackout',
	frozen: 'air.frozen',
	pattern: 'air.pattern',
	patternKind: 'air.pattern.kind',
	identify: 'air.identify',
	masterLevel: 'air.master.level',
	masterTo: 'air.master.to',
	panic: 'air.panic',
	panicOutcome: 'air.panic.outcome',

	video: 'video',
	videoFile: 'video.file',
	videoTag: 'video.tag',
	videoPositionText: 'video.positionText',
	videoLengthText: 'video.lengthText',
	videoRemainingText: 'video.remainingText',
	videoChip: 'video.chip',
	videoText: 'video.text',
	videoPlaying: 'video.playing',

	playlists: 'playlists',
	plId: 'playlists.[].id',
	plName: 'playlists.[].name',
	plFocused: 'playlists.[].focused',
	plState: 'playlists.[].state',
	plIndex: 'playlists.[].index',
	plLoop: 'playlists.[].loop',
	plItemId: 'playlists.[].item.id',
	plItemLabel: 'playlists.[].item.label',
	plItemSource: 'playlists.[].item.source',
	plPositionMs: 'playlists.[].positionMs',
	plLengthMs: 'playlists.[].lengthMs',
	plRemainingMs: 'playlists.[].remainingMs',
	plRate: 'playlists.[].rate',
	plSeq: 'playlists.[].seq',
	plNextId: 'playlists.[].next.id',
	plNextLabel: 'playlists.[].next.label',

	outputs: 'outputs',
	opId: 'outputs.[].id',
	opName: 'outputs.[].name',
	opEnabled: 'outputs.[].enabled',
	opFps: 'outputs.[].fps',
	opDropped: 'outputs.[].dropped',
	opLight: 'outputs.[].light',

	projectors: 'stage.projectors',
	pjId: 'stage.projectors.[].id',
	pjName: 'stage.projectors.[].name',
	pjSolved: 'stage.projectors.[].solved',
	pjResidualPx: 'stage.projectors.[].residualPx',
	pjGrade: 'stage.projectors.[].grade',

	mediaReady: 'media.ready',
	mediaMissing: 'media.missing',
	mediaItems: 'media.items',
	transfers: 'media.transfers',
	transferProgress: 'media.transfers.[].progress',

	scan: 'scan',
	scanState: 'scan.state',
	scanPattern: 'scan.pattern',
	scanPatterns: 'scan.patterns',
	scanProgress: 'scan.progress',
	scanMessage: 'scan.message',

	devices: 'devices',
	dvId: 'devices.[].id',
	dvName: 'devices.[].name',
	dvProjector: 'devices.[].projector',
	dvLight: 'devices.[].light',
	dvHeadline: 'devices.[].headline',
	dvPower: 'devices.[].power',
	dvShutter: 'devices.[].shutter',

	syncRole: 'sync.role',
	syncLeader: 'sync.leader',
	syncOffsetMs: 'sync.offsetMs',
	syncRttMs: 'sync.rttMs',

	patternsLink: 'links.patterns.state',
	stagewatchLink: 'links.stagewatch.state',
	stagewatchText: 'links.stagewatch.timer.text',
	stagewatchPhase: 'links.stagewatch.timer.phase',

	peers: 'peers',
	peerId: 'peers.[].id',
	peerName: 'peers.[].name',
	peerLight: 'peers.[].light',
	peerHeadline: 'peers.[].headline',
	peerLive: 'peers.[].live',

	renderer: 'machine.renderer',
	projectName: 'project.name',
	projectDirty: 'project.dirty',
	projectRevision: 'project.revision',
	projectIndex: 'project.index',

	faults: 'faults',
	faultPart: 'faults.[].part',
	faultWords: 'faults.[].words',

	proposals: 'ai.proposals',

	showArmed: 'show.armed',
	showBy: 'show.by',
	showBlind: 'show.blind',
	showRules: 'show.rules',
	showLocks: 'show.locks',
	showMinGoMs: 'show.minGoMs',

	calMoved: 'calibration.moved',
	calTargets: 'calibration.targets',
	calTargetId: 'calibration.targets.[].id',
	calTargetScore: 'calibration.targets.[].score',
	calTargetGrade: 'calibration.targets.[].grade',
	calTargetLight: 'calibration.targets.[].light',
	calTargetWords: 'calibration.targets.[].words',

	bpm: 'audio.bpm',
	level: 'audio.level',

	clients: 'clients',
	eventsSeq: 'events.seq',
} as const

export type StatePath = (typeof P)[keyof typeof P]

type Json = unknown

/** The value at a top-level path ("air.blackout"), or undefined. A path with "[]" is read from an element with `field`. */
export function at(root: Json, path: StatePath): unknown {
	let cur: unknown = root
	for (const part of path.split('.')) {
		if (cur === null || typeof cur !== 'object' || Array.isArray(cur)) return undefined
		cur = (cur as Record<string, unknown>)[part]
	}
	return cur
}

/** An element's field, by the table's path for it ("playlists.[].item.id" read from one playlist). */
export function field(element: Json, path: StatePath): unknown {
	const cut = path.lastIndexOf('[].')
	const rest = cut < 0 ? path : path.slice(cut + 3)
	let cur: unknown = element
	for (const part of rest.split('.')) {
		if (cur === null || typeof cur !== 'object' || Array.isArray(cur)) return undefined
		cur = (cur as Record<string, unknown>)[part]
	}
	return cur
}

/** The array at a path, or an empty one. */
export function list(root: Json, path: StatePath): readonly unknown[] {
	const v = at(root, path)
	return Array.isArray(v) ? v : []
}

export const str = (v: unknown, fallback = ''): string => (typeof v === 'string' ? v : fallback)
export const num = (v: unknown, fallback = 0): number => (typeof v === 'number' && Number.isFinite(v) ? v : fallback)
export const bool = (v: unknown): boolean => v === true
