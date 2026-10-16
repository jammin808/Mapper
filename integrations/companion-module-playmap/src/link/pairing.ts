import { createHmac } from 'node:crypto'

/** A pairing token as the node keeps it: dashes and whitespace removed, upper-cased ("k7qm-3xwd p9ra" → "K7QM3XWDP9RA"). */
export function normaliseToken(token: string | undefined | null): string {
	if (!token) return ''
	let out = ''
	for (const c of token) {
		if (c === '-' || /\s/u.test(c)) continue
		out += c.toUpperCase()
	}
	return out
}

/** The pairing proof: lowercase hex of HMAC-SHA256(key = the normalised token, message = nonce + the HELLO name). The token never
 * crosses the wire; byte-equal to Patterns' and the node's Pairing.Proof. */
export function authProof(token: string, nonce: string, name: string): string {
	return createHmac('sha256', Buffer.from(normaliseToken(token), 'utf8'))
		.update(Buffer.from(nonce + name, 'utf8'))
		.digest('hex')
}
