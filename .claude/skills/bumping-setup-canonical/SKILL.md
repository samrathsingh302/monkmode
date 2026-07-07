---
name: bumping-setup-canonical
description: "Setup canonical bump, s-schema bump, SetupSchemaVersion, monkmode_setup.ini, new [Setup] field, SetupCanonicalFromIni, WriteSetupConfig, setup default — executes a version bump on the CLI-only MAC'd setup ini (the D1b→D2b s2→s3→s4 pattern). Use when adding/removing a MAC-covered field on the SETUP file (account defaults like Partner, CoolOffSeconds, DefaultSites, DefaultApps), when SetupSchemaVersion must change, or when editing SetupCanonicalFromIni or any Setup* reader."
---

# Bumping the setup canonical (s-schema)

## The two-ini rule — check this FIRST
There are TWO different MAC'd ini files. Conflating them is the #1 documented risk:
- `monkmode_setup.ini` — SETUP canonical (s-series; CURRENT = s4 after D2b added DefaultApps; D1b was s2→s3), written and read by the CLI ONLY. The service NEVER reads it. THIS skill.
- `monkmode_settings.ini` — ENFORCEMENT canonical (currently v8), read by the service every tick. Use `bumping-enforcement-canonical` instead.

D1b's default sites live on the SETUP ini, never in the v8 canonical. Setup fields are account DEFAULTS that feed a NEW arm; anything the service must trust during a live block belongs on the enforcement ini instead.

## Ground truth (line numbers are approximate — they drifted after the D1b→D2b commits; verify symbols in code, never by line)
- Version const: `Public Const SetupSchemaVersion As String = "s4"` (CURRENT, since D2b landed 07/07/2026) — MonkMode/Blocker.vb (~:1197). Ladder in the comment just above: s1 = C6a (Done + Partner) · s2 = C6c (CoolOffSeconds) · s3 = D1b (DefaultSites) · s4 = D2b (DefaultApps). The next bump is s4→s5.
- Canonical builder: `SetupCanonicalFromIni` — MonkMode/Blocker.vb:1142. A SINGLE copy, CLI project only — the service never reads this file, so there are NO cross-assembly wrappers and no 4-copy parity (unlike the enforcement canonical). TRAP: older handoffs call it "BuildSetupCanonical" and speak of "all wrappers" — that symbol does not exist; verify symbols in the code before editing, never from handoff memory.
- Completeness gate: `SetupIsComplete` (Blocker.vb:1178) = MAC valid AND `[Setup] Done`="yes". Every reader shares the same fail-closed gate: `SetupPartnerLabel` (:1194), `SetupDefaultCoolOffSeconds` (:1222), `SetupDefaultSites` (:1248). Any tamper/missing file/DPAPI failure reads as NOT set up → arming is refused → user re-runs `setup`. Never a lift.
- Writer: `WriteSetupConfig(partnerLabel, coolOffSeconds, defaultSites, defaultApps)` — Blocker.vb (~:1413). Optional fields are written ONLY when set (absent = "" in the canonical, round-tripping identically at stamp and verify). A DPAPI failure in `StampFreshSetupMac` leaves the file unstamped and WriteSetupConfig returns False after its re-read check (:1300-1308, comment :1312-1313).
- Existing D1b tests to mirror: `SetupCanonical_S3_Format_IsExact_DefaultSitesAppendedLast` (MonkMode.Tests/SetupTests.cs:484) and `SchemaBump_S2MacUnderS3Code_FreezesSetup_ForcesReRun` (SetupTests.cs:613, the s2-under-s3 freeze).

## Bump checklist — copy the C6b→C6c / D1b mechanics verbatim
1. [ ] Confirm the field belongs on the setup ini (see two-ini rule). Confirm the canonical is still single-copy: grep the whole solution for `SetupCanonicalFromIni` before editing.
2. [ ] Add the const trio near Blocker.vb:1108-1129: the key const (like `SetupDefaultSitesKey`) + a comment stating storage form (PLAINTEXT + MAC-covered, like Partner/CoolOffSeconds) and write-only-when-set semantics.
3. [ ] Extend `SetupCanonicalFromIni`: read the new key with `If(ini.GetKeyValue(...), "")`, APPEND its `Key=Value` line LAST (the append-at-end rule every s-bump follows — see the comment at :1145-1148).
4. [ ] Bump `SetupSchemaVersion` (next is s4 → s5) and extend the ladder comment just above the const with the new step + owning slice.
5. [ ] Extend `WriteSetupConfig` with the new optional parameter, set BEFORE `StampFreshSetupMac`, written only when non-default.
6. [ ] Add the fail-closed reader on the shared gate pattern (load → `SetupMacIsValidForIni` → Done="yes" → parse defensively): copy `SetupDefaultSites` (:1248) or `SetupDefaultCoolOffSeconds` (:1222) shape exactly. Safe fallback on any failure (empty/0), never a shorter-or-looser value.
7. [ ] Wire the setter into the `setup` verb; validate the input at setup time so a stored default can never make a later `block` fail to arm (the `TryBuildDefaultSites` fail-fast pattern, Blocker.vb:432).
8. [ ] Tests, mirroring SetupTests.cs:
   - [ ] Byte-exact format pin (the parity analogue for a single-copy canonical): version tag, exact field order, key names, new field appended last — clone :484 for sN.
   - [ ] All-absent-optionals shape still emits every field line as "" (:504-509 pattern).
   - [ ] sN-under-sN+1 forward-migration FREEZE test: forge the honest old canonical, stamp its MAC with the real key, assert `SetupIsComplete()` is False and the new reader returns its empty default — clone :613.
   - [ ] Tamper/Done="no" reads as incomplete via the new reader too.
9. [ ] Build Release 0-err + full suite green (`C:/Users/samra/.dotnet/dotnet.exe build MonkMode.sln -c Release`, then `... test MonkMode.sln`). Unit tests only — never run the setup verb against a live install, never arm a block.
10. [ ] Proper fresh-eyes verifier over the diff — a setup-canonical bump is NOT "inputs-only", even though its failure mode is the benign freeze (setup re-run), not a lift.

## Why the freeze is safe
An old-schema file's byte-exact MAC cannot validate the new canonical (new version tag + an appended field line), so upgrading forces exactly one `setup` re-run — mirroring the enforcement "arm blocks after upgrading" rule. Tampering yields the same path: incomplete → arming refused. There is no code path where a bad setup file lifts or shortens a live block.

## Provenance & maintenance
Distilled 06/07/2026 pre-model-sunset from Blocker.vb / ConfigIntegrity.vb / the monk-mode handoffs (esp. 2026-07-04-2206-d1a-site-presets.md). Line numbers cite the dirty D1b working tree. Re-verify every citation when `SetupSchemaVersion` (Blocker.vb:1107), `CurrentSchemaVersion`, or the newest handoff in vault/dev/monk-mode/handoffs/ changes — the code consts are ground truth; handoff naming has drifted before (e.g. "BuildSetupCanonical").
