/** The words a connection's status says, in Patterns' voice: what is wrong and what to do about it, never a code. */
export const Words = {
	noTarget: 'Pick the PlayMap node on the network, or type its address',
	tokenNeeded:
		'This node asks for its pairing token — it is in the Network drawer on the PlayMap desk; type it in this connection',
	wrongToken: (node: string): string =>
		`The pairing token is not this node's — ${node}. Type the token from the Network drawer on the PlayMap desk`,
	lockedOut: (node: string): string =>
		`The node closed the link after wrong tokens — ${node}. Check the token, then wait a minute`,
	pinMismatch:
		"The node's certificate is not the pinned one — check the fingerprint in its Network drawer, then clear the pin in this connection",
	newerProtocol: (n: number, known: number): string =>
		`Node speaks line protocol ${n}, this module knows ${known} — update the module`,
	stale: 'No answer from the node for 4 s — keys dimmed',
	unreachable: (where: string, seconds: number): string =>
		`No link to the node at ${where} — trying again in ${seconds} s`,
	refusedUpgrade: (status: number): string => `The node refused the link (HTTP ${status})`,
} as const

/** A node's refusal that means the token is wrong, not that the line was. */
export function isWrongToken(words: string): boolean {
	return words.startsWith('wrong token')
}

/** A node's refusal that means this address spent its wrong tokens. */
export function isLockedOut(words: string): boolean {
	return words.startsWith('too many wrong tokens')
}
