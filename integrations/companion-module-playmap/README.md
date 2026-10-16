# companion-module-jammin808-playmap

The Bitfocus Companion 5.1 connection for a PlayMap node: the show's playlists, outputs and projectors and every package's keys,
live, on a Stream Deck or any surface Companion drives. `companion/HELP.md` is the operator's page; `docs/COMPANION.md` at the
repository's root says how the module and the node's side fit, and `docs/wire/companion.md` lists every line it says and reads.

## What it is

- A Companion connection (`jammin808-playmap`), TypeScript on `@companion-module/base` 2.2.1, for Node 22.
- One link per connection: the node's web port with the line channel (`/ws`, subprotocol `playmap.lines.v1`), over wss with the
  certificate's fingerprint pinned on the first link, or the TCP line wire for a node without the channel. It pairs by proof
  (`HELLO`, then `AUTH-PROOF` for the node's `NONCE`): the token is a Companion secret and never crosses the network.
- Its actions come from the node's `VERBS`, its variables and feedbacks from `STATE`, its presets from every section of `INDEX`, so
  a package the node adds reaches the deck without a new module. Every key's line passes one fence that reads it as the node does
  (no session word, pairing or secret, at any depth it rides); a risky line is held 0.8 s or pressed twice, never fired at a touch;
  a key's light is the node's STATE, never the press.

## Build and test

```
npm ci
npm test          # type check, lint, then every test, the end-to-end test against the real node among them
npm run check     # Companion's own module check
npm run package   # the package Companion imports
```

The end-to-end test (`test/e2e-engine.test.ts`) runs `tools/PlayMap.Node engine --headless`: build the solution first
(`dotnet build PlayMap.sln` for the Debug node), or name a built `PlayMap.Node.dll` in `PLAYMAP_ENGINE`. The rest run the module
through Companion's own host layer against a fake node.

## Held to the node

The node checks the module against itself (`CompanionContractTests`, `CompanionModuleFixtureTests`): `src/catalog/verbs.json` is the
node's `VERBS`; `test/fixtures/` holds a STATE and an INDEX the engine wrote; `test/lines.txt` is every line the module can send,
`test/section-lines.txt` every line a section key or Live slot sends for that INDEX, and `test/state-paths.txt` every STATE path the
module reads. None is edited by hand. After the node changes, run `TheModulesVerbsAreTheCatalogue` (PlayMap.Net.Tests) and
`CompanionModuleFixtureTests` (PlayMap.Engine.Tests) with `PLAYMAP_WRITE_FIXTURES=1`, then `npm run lines` here; a new INDEX section
that lists no rows on the fixture's node is given a sample row in `test/emitted.ts`.
