---
name: bumping-enforcement-canonical
description: "Enforcement canonical bump, v-schema bump, CurrentSchemaVersion, MAC schema change, new MAC-covered field, BuildCanonical extension, CanonicalFromIni wrapper, four-copy parity — executes a version bump on the MAC'd enforcement ini (monkmode_settings.ini) without breaking cross-assembly parity. Use when adding/removing a MAC-covered field on the ENFORCEMENT config, when CurrentSchemaVersion must change, or when any CanonicalFromIni wrapper or ConfigIntegrity.vb copy is edited."
---

# Bumping the enforcement canonical (v-schema)

## The two-ini rule — check this FIRST
There are TWO different MAC'd ini files. Conflating them is the #1 documented risk:
- `monkmode_settings.ini` — ENFORCEMENT canonical, currently **v8**, read by the service every 10s tick. THIS skill.
- `monkmode_setup.ini` — SETUP canonical (s-series, s2→s3 in the in-flight D1b work), CLI-only, the service never reads it. Use `bumping-setup-canonical` instead.

If the new field is a user preference or default (e.g. D1b's default sites), it belongs on the SETUP ini — stop here. Only fields the service must trust while a block is live go in the v-canonical.

## Ground truth
- Version const: `Friend Const CurrentSchemaVersion As String = "v8"` — MonkMode/ConfigIntegrity.vb:62. Compile-time, caller-supplied as a parameter, and the FIRST line of the canonical — deliberately never read from the ini, so a doctored config can't self-declare its own version (ConfigIntegrity.vb:103-108).
- Canonical builder: `BuildCanonical(schemaVersion, until, processList, customSites, now, highWater, coolOffUntil, partnerSalt, partnerHash, partnerUnlockedAt, committed, scheduleSpec, scheduleActiveUntil, coolOffDuration)` — MonkMode/ConfigIntegrity.vb:149.
- ConfigIntegrity.vb exists in FOUR byte-identical copies (separate assemblies can't reference each other):
  - MonkMode/ConfigIntegrity.vb
  - MonkMode_srv/MonkMode_srv/ConfigIntegrity.vb
  - MM_notify/MM_notify/ConfigIntegrity.vb
  - MM_guard/MM_guard/ConfigIntegrity.vb
- Four `CanonicalFromIni` wrappers, one per party, must derive byte-identical input:
  - MonkMode/Blocker.vb:573 (CLI) · MonkMode_srv/MonkMode_srv/Service1.vb:2511 (service) · MM_guard/MM_guard/Program.vb:225 (guardian) · MM_notify/MM_notify/Form1.vb:211 (notifier)
- Pinned by MonkMode.Tests/CanonicalParityTests.cs plus `AllFourCopies_ShareTheSameSchemaVersion` (ConfigIntegrityTests.cs:305) and `ForwardMigration_OldSchemaMacUnderCurrentCode_FailsClosed_FreezesBlock` (ConfigIntegrityTests.cs:319).

## Bump checklist (every step is mandatory)
1. [ ] Confirm the field genuinely belongs in the enforcement canonical (see two-ini rule). Never put non-enforcement data here: stats went in a separate non-MAC file by design (D3); preset INPUTS are non-MAC (D1a); user defaults live on the setup ini (D1b).
2. [ ] Verify all four ConfigIntegrity.vb copies are currently byte-identical before touching anything: `Glob **/ConfigIntegrity.vb` then hash all four. If they already differ, stop and investigate.
3. [ ] Edit `BuildCanonical` in ONE copy: append the new parameter at the END of the parameter list and its `Key=Value` line at the END of the canonical (the append-at-end rule every prior bump followed — see the v1→v8 history comment at ConfigIntegrity.vb:53-58).
4. [ ] Bump `CurrentSchemaVersion` (v8 → v9) in the same copy, and extend the history comment with the new version + reason.
5. [ ] Copy the edited file byte-for-byte over the OTHER THREE copies. Re-hash all four to prove identity.
6. [ ] Extend ALL FOUR `CanonicalFromIni` wrappers identically (the four locations above): read the new key, decide decrypted-vs-as-stored deliberately (datetimes are decrypted like Until/HighWater; policy/plaintext fields pass as-stored like CustomSites/[Partner]), pass it through. Absent value must pass as "".
7. [ ] Extend the writer side (CLI stamp path) so the new field is set BEFORE the MAC is stamped.
8. [ ] Update CanonicalParityTests.cs and the BuildCanonical format test for the new field/version; `AllFourCopies_ShareTheSameSchemaVersion` must pass unmodified.
9. [ ] Add a byte-exact forward-migration freeze test on the `ForwardMigration_OldSchemaMacUnderCurrentCode` pattern (ConfigIntegrityTests.cs:319): forge an honest old-version canonical, stamp its MAC with the real key, assert it does NOT validate under the new code → the block FREEZES (fail-closed), never lifts.
10. [ ] Build Release 0-err + full test suite green: `C:/Users/samra/.dotnet/dotnet.exe build MonkMode.sln -c Release` then `... test MonkMode.sln`. Unit tests only — never arm a block or run the service.
11. [ ] Mandatory fresh-eyes verifier over the whole diff before landing — an enforcement-canonical bump is never "inputs-only".

## Failure modes this prevents
- One stale copy or wrapper → the parties stamp/verify different tags → every block freezes.
- Field appended mid-list instead of last → every existing config invalidates in a non-obvious way.
- Version read from the ini instead of the const → attacker downgrade path.
- Operational rule: arm blocks AFTER upgrading binaries, not across an upgrade — an in-flight block armed under the old schema correctly freezes until re-armed (ConfigIntegrity.vb:50-53).

## Provenance & maintenance
Distilled 06/07/2026 pre-model-sunset from Blocker.vb / ConfigIntegrity.vb / the monk-mode handoffs (esp. 2026-07-04-2206-d1a-site-presets.md). Line numbers pinned to baseline commit `dc34f0b`; the 06-07/07/2026 fix-branch merges shift Service1.vb lines — re-locate symbols by NAME. Re-verify every citation when `CurrentSchemaVersion` (ConfigIntegrity.vb:62), `SetupSchemaVersion`, or the newest handoff in vault/dev/repos/monk-mode/handoffs/ changes — the code consts are ground truth; handoff naming has drifted before.
