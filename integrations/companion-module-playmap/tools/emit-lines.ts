// Writes test/lines.txt (every line the module can send, which the node's contract test parses with VerbCatalog.Match and fits to
// its row), test/section-lines.txt (every line a section key or a Live slot sends for the INDEX fixture's sections, filled as the
// module fills them, which the node's contract test fits the same way) and test/state-paths.txt (every STATE path the module reads,
// which the node's contract test finds in a STATE it wrote). The module's own test checks all three are what this writes now.
import { writeFileSync } from 'node:fs'
import { allLines, sectionLines, statePaths } from '../test/emitted.ts'

const root = new URL('../test/', import.meta.url)
writeFileSync(new URL('lines.txt', root), allLines().join('\n') + '\n')
writeFileSync(new URL('section-lines.txt', root), sectionLines().join('\n') + '\n')
writeFileSync(new URL('state-paths.txt', root), statePaths().join('\n') + '\n')
