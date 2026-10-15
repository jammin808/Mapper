// Writes test/lines.txt (every line the module can send, which the node's contract test parses with VerbCatalog.Match and fits to
// its row) and test/state-paths.txt (every STATE path the module reads, which the node's contract test finds in a STATE it wrote).
// The module's own test checks both files are what this writes now.
import { writeFileSync } from 'node:fs'
import { allLines, statePaths } from '../test/emitted.ts'

const root = new URL('../test/', import.meta.url)
writeFileSync(new URL('lines.txt', root), allLines().join('\n') + '\n')
writeFileSync(new URL('state-paths.txt', root), statePaths().join('\n') + '\n')
