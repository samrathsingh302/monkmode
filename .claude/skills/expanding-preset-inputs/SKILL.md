---
name: expanding-preset-inputs
description: "Site presets, preset category, PresetTable, TryExpandPresets, --preset, input sugar, default site list, blocklist shorthand — adds or edits input-side sugar (preset categories, domain expansion, defaults plumbing) without touching the enforcement surface. Use when adding a preset category or domain, changing preset expansion/error behaviour, or wiring a new input source that feeds the domains list of a NEW arm."
---

# Expanding preset inputs (D1a pattern — input sugar only)

## Scope rule
Presets are PURE INPUT: expanded domains flow into the SAME `domains` list as a hand-typed `--sites` and ride the existing MAC-covered `[User] CustomSites` path of the enforcement canonical. Inputs-only changes need NO new canonical field and NO version bump. The moment a change must STORE something (an editable default, a user table), it stops being inputs-only — that is the setup-canonical pattern (`bumping-setup-canonical`; D1b's default sites live on `monkmode_setup.ini`, never in the enforcement canonical).

## Ground truth (line numbers reflect the dirty working tree, 06/07/2026)
- `PresetTable` — MonkMode/Blocker.vb:367. Fixed compile-time dictionary (case-insensitive), 5 categories: social / video / news / shopping / adult. Not stored config, so nothing extra to MAC.
- Every table domain is deliberately a bare SINGLE-DOT registrable domain: `BuildHostsEntries` (Blocker.vb `BuildHostsEntries`) auto-adds the `www./m./web./mobile.` mirror variants only when `Not d.StartsWith("www.") AndAlso d.IndexOf("."c) = d.LastIndexOf("."c)` — a multi-dot host (bbc.co.uk) gets no variant lines. Keep new table entries single-dot, or accept the missing-mirror gap knowingly. (D1c, 14/07/2026: widened from www.-only to www./m./web./mobile. so web.snapchat.com etc. can't casually bypass.)
- `KnownPresetNames()` — Blocker.vb:378. Public, sorted snapshot of the table keys; feeds both the usage/help text and the unknown-preset error hint, so names can never drift from the live table.
- `TryExpandPresets(presetArg, domains, errorMsg)` — Blocker.vb:393. Comma/semicolon split, trimmed, union deduped case-insensitively with order preserved (category order, then domain order). FAIL-CLOSED: ANY unknown token → emit NOTHING, return False, error names every unknown token plus all valid names (:411-417). Empty/Nothing arg → True with an empty list. A typo must never quietly UNDER-block.
- CLI wiring: `DoBlock` (MonkMode/Program.vb:124) expands `--preset` at Program.vb:141-148 — BEFORE every side effect; an unknown category returns 1 before any hosts/service touch.
- Setup-side merge (D1b): `TryBuildDefaultSites` (Blocker.vb:432) reuses the SAME `TryExpandPresets`, fail-fast at setup time so a stored default can never make a later `block` fail to arm.

## Checklist — adding/editing a category or domain
1. [ ] Edit `PresetTable` only (Blocker.vb:367). Bare single-dot domains, lower-case, no scheme/path (NormalizeDomain would strip them anyway, but keep the table clean).
2. [ ] Do NOT touch `KnownPresetNames`, help text lists, or error text — they derive from the table.
3. [ ] Do NOT touch the enforcement or setup canonicals, `CurrentSchemaVersion`, or `SetupSchemaVersion` — no bump for pure input sugar.
4. [ ] Update/extend MonkMode.Tests/SitePresetTests.cs: expansion of the new/changed category, dedupe across categories, fail-closed unknown-token behaviour, `KnownPresetNames` ordering.
5. [ ] Build Release 0-err + full suite green (`C:/Users/samra/.dotnet/dotnet.exe build MonkMode.sln -c Release`, then `... test MonkMode.sln`).
6. [ ] Light fresh-eyes verifier is sufficient for inputs-only changes (the D1a precedent) — escalate to a proper verifier the moment the diff strays beyond input plumbing.

## Checklist — wiring a NEW input source into `block`
1. [ ] Expand/validate BEFORE any side effect, in `DoBlock` next to the existing `--preset` handling (Program.vb:141-148); on invalid input print the error and `Return 1` before any hosts/service touch.
2. [ ] Feed results into the existing `domains` list only — they then flow through WriteHostsBlock + `[User] CustomSites` and are enforcement-MAC-covered downstream with no new surface.
3. [ ] Fail closed on anything unrecognised: emit nothing, name every bad token, list the valid alternatives (the `TryExpandPresets` contract).
4. [ ] Keep the pure logic in Blocker.vb as a Friend function so it is unit-testable without arming a block.

## Known quirks — do not fix
- `--preset --for 2h` (value omitted): `GetOption` (Program.vb:690) grabs the next arg, so `--for` becomes the preset value → "Unknown preset: --for" + safe abort. Identical behaviour to every valued option; harmless.
- `DoBlock` wiring is verify-by-READING only: it does console/service I/O and cannot be unit-run without arming a block — live verification is the elevated manual smoke's job, never a dev-session run.
- The single-dot-only mirror rule is behaviour shared with `--sites`; the mirror set (`www./m./web./mobile.`) was widened from www.-only in slice D1c (14/07/2026) by touching `BuildHostsEntries` (both parity copies), not the preset table.

## Provenance & maintenance
Distilled 06/07/2026 pre-model-sunset from Blocker.vb / ConfigIntegrity.vb / the monk-mode handoffs (esp. 2026-07-04-2206-d1a-site-presets.md). Line numbers cite the dirty D1b working tree. Re-verify every citation when the schema version consts (ConfigIntegrity.vb `CurrentSchemaVersion`, Blocker.vb `SetupSchemaVersion`) or the newest handoff in vault/dev/repos/monk-mode/handoffs/ change — the code consts are ground truth; handoff naming has drifted before.
