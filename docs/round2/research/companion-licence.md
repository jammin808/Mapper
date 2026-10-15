# The Companion module's licence: what Companion and Bitfocus allow

The question: the most restrictive licence a Bitfocus Companion module may carry. Researched 15 Oct 2026 from the source of
Companion 5.1.0 and its module tooling, Bitfocus' published policy and the live module store, then checked adversarially.
**V** = opened and read; **U** = not opened.

## Answer, by the way a module reaches users

| Channel | Most restrictive allowed | Why |
|---|---|---|
| Companion's module store (the Developer Portal; since Companion 4.0 the only official route for a connection module) | Source (`package.json`) **MIT**, required; packaged module (`companion/manifest.json`) **GPL-3.0-only** | Bitfocus' licensing page: the `package.json` licence "must be `MIT`" — "a hard requirement for modules distributed with the official builds"; the manifest licence may be `MIT`, `GPL-2.0-only` or `GPL-3.0-only` [1] **V**. The portal builds from `bitfocus/companion-module-<name>` with `@companion-module/tools` ≥ 2.8.0 / 3.1.0 and never passes `--ignore-license-rules` [3][4] **V**. |
| The offline module bundle Bitfocus publishes | Permissive only | Companion ≥ 5.0.5 skips modules whose licence category is COPYLEFT or UNKNOWN [5] **V**. |
| Bitfocus Buttons (the commercial product, fed by the same portal) | MIT, as observed | Its listing (843 modules) leaves out every module whose licence is not MIT, ISC and GPL alike [6] **V**; the filter rule itself was inferred from the listing. |
| Side-loaded (a `.tgz` import, the developer folder, an extra module path) | Proprietary (all rights reserved; no SPDX id, e.g. `LicenseRef-…`) | Companion 5.1.0 never reads a module's licence; the manifest schema takes any string [2][7] **V**. Bitfocus documents this route for modules "internal to your company or organisation" and `--ignore-license-rules` for private modules [1][7] **V**. |

The tooling: `@companion-module/base`'s manifest schema declares `license` as a plain string [2] **V**;
`@companion-module/tools` 3.1.x fails `companion-module-build` and `companion-module-check` unless `package.json` is exactly `MIT`
and the manifest is exactly `MIT`, `GPL-2.0-only` or `GPL-3.0-only` (no dual licences, no AGPL, LGPL, MPL, Apache or
proprietary); dependency allow-lists follow the manifest's licence and AGPL is never allowed [3] **V**.

In practice the store holds 859 connection modules: 845 MIT, 8 ISC, 3 LGPL-3.0 (grandfathered), 2 GPL-3.0-only and 1
GPL-2.0-only, all with public source in Bitfocus' GitHub organisation; none is proprietary, closed or AGPL [6] **V**. A module
with a GPL-3.0-only manifest and only MIT dependencies has been accepted (a prerelease) [8] **V**; Bitfocus frames GPL as for
bundling a GPL library, so a voluntary GPL label may be questioned in review.

## Decision

The owner asked for the most restrictive licence Companion allows, for the Companion module only. The module carries
`"license": "MIT"` in `package.json` (Bitfocus requires it) and `"license": "GPL-3.0-only"` in `companion/manifest.json`, with
the MIT text in `LICENSE` ("Copyright (c) 2026 jammin808") and the GPL-3.0 text for the packaged module. The cost against MIT: no
offline bundle, no Buttons listing. PlayMap itself names no licence.

## Sources

1. Bitfocus, "Licensing", module development — https://companion.free/for-developers/module-development/module-lifecycle/licensing;
   `bitfocus/website` licensing.md lines 15–47, added `bae84df` (31 Aug 2026), at `b2ea4d2` (14 Oct 2026). **V**
2. `bitfocus/companion-module-base` `9360ad7` (9 Oct 2026): `packages/base/src/manifest-schema.ts` line 140,
   `packages/base/assets/manifest.schema.json` lines 30 and 73, `src/manifest.ts` lines 33–44. **V**
3. `bitfocus/companion-module-tools` v3.1.1 `35367ed` (1 Oct 2026): `src/license/policy.ts` (lines 24–130, 155–193, 329–373,
   474–494), `src/scripts/lib/license-warning-util.ts` lines 18–28, `README.md` "Licensing"; v3.1.0 `9061cba` and v2.8.0
   `edd8d69` (28 Aug 2026) fail by default. **V**
4. `bitfocus/companion-bundled-modules` `.github/workflows/portal-update-module.yml` at `5a1c6c4` (1 Oct 2026);
   `bitfocus/actions` `.github/workflows/module-checks.yaml` lines 31–35 and 240–292 at `1a093b7` (17 Sep 2026). **V**
5. `bitfocus/companion` v5.1.0 `257b325` (11 Oct 2026): `tools/prepare_offline_module_bundle.mts` lines 121–127;
   CHANGELOG "Companion v5.0.5". **V**
6. `https://developer.bitfocus.io/api/v1/companion/modules/connection` and `…/buttons/modules/connection`, read 15 Oct 2026.
   **V**
7. `bitfocus/companion` v5.1.0: `companion/lib/Instance/ModuleScanner.ts` line 173, `InstalledModulesManager.ts` lines 255–259,
   `docs/user-guide/3_config/modules.md` line 25. **V**
8. `bitfocus/companion-module-stageaudioworks-obs-multisite` v0.3.2 `0d4fc92` (25 Sep 2026). **V**
