---
name: guarding-fail-closed-enforcement
description: "Fail-closed, fail-open, block lift, MAC gate, EffectiveBlockHasExpired, heartbeat Hold, self-heal gate, config corruption, error path, schema freeze — protects monk-mode's core invariant that no error path may ever lift a block. Use when writing or reviewing any enforcement, heartbeat, self-heal, config-integrity, recovery, or CLI input-path change in monk-mode, or when assessing whether an error/exception branch could under-block."
---

# Guarding fail-closed enforcement

The core invariant: **no error path may ever lift a block**. Errors freeze or over-block; they never under-block. Every check below is a place that invariant lives — verify a change against ALL of them before approving it.

## The single lift gate
- [ ] `EffectiveBlockHasExpired = macValid AndAlso BlockHasExpired(...)` — MonkMode_srv/MonkMode_srv/Service1.vb:1227-1229. Absent/forged/foreign-machine MAC ⇒ "not expired" ⇒ block stands.
- [ ] `BlockHasExpired` itself fails closed: an unparseable `Until` is NOT expired (Service1.vb:1209-1215).
- [ ] Any new expiry decision must route through this gate (or `BlockHeld`, below) — never compare `Until` to `DateTime.Now` directly.

## Heartbeat trichotomy — Lift / Restamp / Hold
- [ ] `HeartbeatAction` enum, Service1.vb:1232-1236: Lift (valid MAC + genuinely past end), Restamp (valid MAC, no exit due), **Hold (invalid MAC ⇒ freeze — neither lift nor re-stamp)**.
- [ ] The B7 fail-open fix comment (Service1.vb:1238-1254) is why: the old heartbeat re-stamped the MAC unconditionally in the "not expired" branch, re-blessing a tampered `Until` with a fresh valid MAC — a plain ini edit then lifted the block next tick. Never re-stamp over an unverified config.
- [ ] Regression pin: `ClassifyHeartbeat(macValid:=False, blockExpired:=True) = Hold`.

## Self-heal gates (B2 / B3 / B5a / B6)
- [ ] All four per-tick self-heals gate on `BlockHeld(...)` — B2 hosts repair Service1.vb:950, B3 SafeBoot :1018, B5a DoH-off :1034, B6 deny-DELETE ACE :1050.
- [ ] `BlockHeld` (Service1.vb:1688-1689) = `Not EffectiveBlockHasExpired(...) OrElse (macValid AndAlso ScheduleActive(...))` — for a manual block it reduces to the verbatim `Not EffectiveBlockHasExpired(...)` pattern (comment :1000); invalid MAC ⇒ held ⇒ keep enforcing.
- [ ] Read-only probe first, so intact state is a true no-op — no churn (pattern documented in vault/dev/monk-mode/specs/ARCHITECTURE.md:184-191).
- [ ] A new self-heal MUST copy this gate verbatim, take `asOf = newHwAsOf` (trusted high-water, never `DateTime.Now`), and sit in its own `Try` so its failure never disturbs sibling gates or crashes the tick.

## Config corruption and recovery (B8 + C1b)
- [ ] Corrupt / missing / blanked / short (<2-section) ini ⇒ deliberately UNSTAMPED default 7-day block ⇒ `macValid=False` ⇒ unliftable until re-armed from the CLI (ARCHITECTURE.md B8 row, :191).
- [ ] C1b shadow backup: `RecoverPrimaryConfig` (Service1.vb:854 call; body :362-386) first restores a MAC-valid `monkmode_settings.ini.bak` via `ConfigBackup.CopyIfSourceValid` (ConfigBackup.vb:63, both copies) so a restored block stays liftable at its REAL expiry; only with no trustworthy backup does it fall back to the unstamped default.
- [ ] Recovery fires ONLY on structural corruption (`PrimaryIsStructurallyUsable` / parse throw) — NEVER on MAC invalidity. A parseable-but-MAC-invalid (tampered) config FREEZES and is never "recovered". Preserve this distinction in any recovery change.
- [ ] `CopyIfSourceValid` copies only when the SOURCE is MAC-valid — corrupt never overwrites good, either direction; copy is atomic (temp+rename).

## The one historical error→lift path (O1)
- [ ] OnStart hosts `SetAttr` failure called `stopMe()` — full teardown off a transient FS error at boot (Service1.vb:273-277 at baseline `dc34f0b`), inconsistent with the tick, which swallows the same failure.
- [ ] FIXED 06/07/2026 on branch `fix/1-onstart-fail-closed` (commit `ace47d3`, fresh-eyes verifier GO; handoff 2026-07-06-2122-o1-onstart-fail-closed.md); the merge into `monkmode` was IN FLIGHT 06-07/07/2026. Check `git log` + the newest handoff for the live merge state before treating O1 as open — and either way, do NOT re-fix it in passing.

## New input paths must fail closed too
- [ ] Exemplar: `TryExpandPresets` (MonkMode/Blocker.vb:393-419) — any unknown preset token ⇒ emit NOTHING, return False with the full unknown list + valid names (:411-417). A typo can never silently under-block by expanding only the known tokens.
- [ ] Same stance everywhere: the schedule day-name parser rejects unknown days; D1b setup defaults validate presets once at setup time and abort BEFORE the write (Blocker.vb:421-429 comment).
- [ ] Test any new parser's unknown-token branch: assert it emits nothing, not a subset.

## Schema-version freeze (C1, v2→v3)
- [ ] The schema version is a compile-time constant (`ConfigIntegrity.CurrentSchemaVersion`, byte-identical across all four projects) and the FIRST MAC-covered canonical line — never read from the ini, so a doctored old config cannot self-declare its version (ARCHITECTURE.md B7 row, :190).
- [ ] Old-version config under new binaries fails the MAC ⇒ freeze (Hold — never auto-restamped into a fresh MAC), never auto-migrate.
- [ ] Operational rule: arm blocks AFTER upgrading binaries, never across an upgrade.
- [ ] Pinned by `ForwardMigration_OldSchemaMacUnderCurrentCode` + `AllFourCopies_ShareTheSameSchemaVersion`.

## Review checklist for any enforcement change
- [ ] Does any new `Catch`/error branch call `stopMe()`, strip hosts, remove an ACE/key/policy, or skip a self-heal? That is an error→lift path — reject.
- [ ] Does any write path re-stamp the MAC without first proving `macValid`? Reject (the B7 bug reborn).
- [ ] Does any new decision read `DateTime.Now` instead of the high-water mark? Reject (see advancing-monotonic-highwater).
- [ ] Fail-closed on ambiguity: unparseable, missing, unknown ⇒ block stands / emit nothing.

## Provenance & maintenance
Distilled 06/07/2026 pre-model-sunset from Service1.vb, Blocker.vb, ConfigBackup.vb, vault/dev/monk-mode/specs/ARCHITECTURE.md and the vault handoffs. Line numbers pinned to baseline commit `dc34f0b` (dirty D1b tree for Blocker.vb — cited as-is); the 06-07/07/2026 fix-branch merges shift Service1.vb lines — re-locate symbols by NAME, not line, after those land. Re-verify against ARCHITECTURE.md's bypass table and the newest handoff in vault/dev/monk-mode/handoffs/ when they change — newest dated evidence wins.
