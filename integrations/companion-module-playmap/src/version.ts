/** The module's version: equal in package.json, companion/manifest.json and here (the node's contract test reads all three). It
 * moves on every build, because Companion refuses a version it already has. */
export const MODULE_VERSION = '1.0.0'

/** The controllers' line protocol this module speaks: WHOAMI's and the announcement's `proto`. A node speaking a higher one is
 * still driven, with a warning to update the module. */
export const KNOWN_LINE_PROTOCOL = 1

/** The WebSocket subprotocol of the node's line channel on its web port. */
export const SUBPROTOCOL = 'playmap.lines.v1'
