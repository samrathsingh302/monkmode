---
name: bumping-enforcement-canonical
description: "Enforcement canonical bump, v-schema bump, CurrentSchemaVersion, MAC schema change, new MAC-covered field, BuildCanonical extension, BuildSlotCanonical, slot canonical, CanonicalFromIni wrapper, four-copy parity — executes a version bump on the MAC'd enforcement ini (monkmode_settings.ini) without breaking cross-assembly parity. Use when adding/removing a MAC-covered field on the ENFORCEMENT config, when CurrentSchemaVersion must change, or when any CanonicalFromIni wrapper or ConfigIntegrity.vb copy is edited."
---

# Bumping the enforcement canonical (v-schema)

## The two-ini rule — check this FIRST
There are TWO different MAC'd ini files. Conflating them is the #1 documented risk:
- `monkmode_settings.ini` — ENFORCEMENT canonical, currently **v10**, read by the service every 10s tick. THIS skill.
- `monkmode_setup.ini` — SETUP canonical (s-series), CLI-only, the service never reads it. Use `bumping-setup-canonical` instead.

If the new field is a user preference or default (e.g. default sites), it belongs on the SETUP ini — stop here. Only fields the service must trust while a block is live go in the v-canonical.

## Ground truth (v10, S1 of the v1.1 multi-block work)
The canonical is **TWO-LEVEL** since v10: a fixed global header, then one 16-line block per armed slot.

- Version const: `Friend Const CurrentSchemaVersion As String = "v10"` — `MonkMode/ConfigIntegrity.vb`. Compile-time, caller-supplied as a parameter, and the FIRST line of the canonical — deliberately never read from the ini, so a doctored config can't self-declare its own version.
- Slot ceiling: `Friend Const MaxSlots As Integer = 8` — same file. `ParseSlotCount` clamps to `[0, MaxSlots]`.
- The four members `ConfigIntegrity` owns for the canonical, ALL pure:
  - `BuildCanonical(schemaVersion, highWater, now, nextSlotId, slotCount As Integer, guardHoldUntil, guardArmedCount, slotBlock)` — header + the pre-built slot block.
  - `BuildSlotCanonical(position As Integer, id, startAt, durationSeconds, until, sites, apps, urlPatterns, allSession, scheduleSpec, scheduleActiveUntil, coolOffUntil, coolOffDuration, partnerSalt, partnerHash, partnerUnlockedAt, committed)` — the 16 `SlotN.Field=` lines for ONE slot.
  - `ParseSlotCount(raw) As Integer` — blank/garbage/negative ⇒ **0**; above `MaxSlots` ⇒ `MaxSlots`.
- Global header lines, in order: `HighWater`, `Now`, `NextSlotId`, `SlotCount`, `GuardHoldUntil`, `GuardArmedCount`. `slotCount` in the header is the **clamped** value (that is why the parameter is typed `Integer` — a raw ini string cannot reach it).
- Per-slot lines, in order (exactly 16, always emitted, `""` when unset): `Id · StartAt · DurationSeconds · Until · Sites · Apps · UrlPatterns · AllSession · ScheduleSpec · ScheduleActiveUntil · CoolOffUntil · CoolOffDuration · PartnerSalt · PartnerHash · PartnerUnlockedAt · Committed`.
- Encryption split (decrypted for the canonical): globals `HighWater`, `Now`, `Guard.HoldUntil`; per-slot `Until`, `StartAt`, `CoolOffUntil`, `ScheduleActiveUntil`. **Everything else is plaintext-as-stored — including `Sites`, `Apps`, `UrlPatterns`** (a blocklist is not a secret; the MAC is its protection).
- The `"null"` sentinel for "no apps" is **RETIRED** as of v10 (v9 stored `[Process] List = "null"` and special-cased the decrypt in all four wrappers). v10 stores an empty string; no wrapper special-cases anything.
- Slot storage is INI SECTIONS `[Slot1]…[Slot8]` keyed by POSITION, not id; ids live in the `Id` field. `[Slots] SlotCount` / `[Slots] NextSlotId` are plaintext ints. Stale `[SlotN]` sections beyond `SlotCount` are IGNORED and contribute nothing.
- `ConfigIntegrity.vb` and `IniFile.vb` each exist in FOUR byte-identical copies (separate assemblies can't reference each other):
  - `MonkMode/` · `MonkMode_srv/MonkMode_srv/` · `MM_notify/MM_notify/` · `MM_guard/MM_guard/`
- Four `CanonicalFromIni` wrappers, one per party, must derive byte-identical input. Each owns the same small `For pos = 1 To ParseSlotCount(...)` loop:
  - `MonkMode/Blocker.vb` (CLI) · `MonkMode_srv/MonkMode_srv/Service1.vb` (service) · `MM_guard/MM_guard/Program.vb` (guardian) · `MM_notify/MM_notify/Form1.vb` (notifier)
  - Every wrapper aliases its own Simple3Des instance to a local `crypt` on the FIRST line of the body (`Dim crypt As Simple3Des = enc`, or `= encryptionW` in the service) so everything below it is byte-identical text across the four copies. Keep it that way — extract the four bodies and diff them; below the alias line the diff must be empty.
- Pinned by `MonkMode.Tests/CanonicalParityTests.cs` (the four real wrappers on the same ini), `MonkMode.Tests/SlotCanonicalTests.cs` (the v10 byte-literal freeze + `ParseSlotCount` + stray-section + `RemoveSection`), and in `ConfigIntegrityTests.cs`: `AllFourCopies_ShareTheSameSchemaVersion`, `AllFourCopies_ProduceIdenticalCanonical/Mac`, and the `ForwardMigration_*` freeze tests.

## Bump checklist (every step is mandatory)
1. [ ] Confirm the field genuinely belongs in the enforcement canonical (see two-ini rule). Never put non-enforcement data here: stats went in a separate non-MAC file by design (D3); preset INPUTS are non-MAC (D1a); user defaults live on the setup ini (D1b).
2. [ ] Decide GLOBAL vs PER-SLOT. A per-slot field goes in `BuildSlotCanonical` (and becomes a 17th key — update the "exactly 16 keys" pin and the byte-literal test); a global goes in `BuildCanonical`'s header. Per-slot is the default for anything describing ONE block.
3. [ ] Verify all four `ConfigIntegrity.vb` copies (and all four `IniFile.vb` copies) are currently byte-identical before touching anything: `Get-FileHash` all four. If they already differ, stop and investigate.
4. [ ] Edit `BuildCanonical` / `BuildSlotCanonical` in ONE copy: append the new parameter at the END of that function's parameter list and its `Key=Value` line at the END of that function's line group (the append-at-end rule every bump has followed — see the history comment above `CurrentSchemaVersion`).
5. [ ] Bump `CurrentSchemaVersion` in the same copy, and extend the history comment with the new version + reason.
6. [ ] Copy the edited file byte-for-byte over the OTHER THREE copies — **never hand-edit four times, that is how divergence enters**. Re-hash all four to prove identity.
7. [ ] Extend ALL FOUR `CanonicalFromIni` wrappers identically: read the new key, decide decrypted-vs-as-stored deliberately (datetimes are decrypted; policy/list/plaintext fields pass as-stored), pass it through. Absent value must pass as `""`.
8. [ ] Extend the writer side (CLI arm path) so the new field is set BEFORE the MAC is stamped.
9. [ ] Update the byte-literal format test and the parity tests for the new field/version; `AllFourCopies_ShareTheSameSchemaVersion` must pass **unmodified**.
10. [ ] Add a byte-exact forward-migration freeze test on the `ForwardMigration_*` pattern: forge an honest OLD-version canonical as a raw string literal (never via the current `BuildCanonical` — the literal is what makes the test independent of today's format), stamp its MAC with the real key, assert it does NOT validate under the new code → the block FREEZES (fail-closed), never lifts.
11. [ ] Build Release 0-err + full test suite green: `C:/Users/samra/.dotnet/dotnet.exe build MonkMode.sln -c Release` then `... test MonkMode.sln`. Unit tests only — never arm a block or run the service.
12. [ ] Mandatory fresh-eyes verifier over the whole diff before landing — an enforcement-canonical bump is never "inputs-only".

## Failure modes this prevents
- One stale copy or wrapper → the parties stamp/verify different tags → every block freezes.
- Field appended mid-list instead of last → every existing config invalidates in a non-obvious way.
- Version read from the ini instead of the const → attacker downgrade path.
- A forged `[Slots] SlotCount` (e.g. `99`) → `ParseSlotCount` clamps → the canonical no reader can match → MAC-invalid → freeze. Garbage clamps to 0 slots, which also cannot match a real stamp. It NEVER yields "fewer slots to enforce". **One benign edge, stated so nobody "fixes" it:** on a genuine 8-slot config a forged `SlotCount=9` clamps to 8 and the MAC still VALIDATES — but the resulting canonical and the enforcement are byte-identical to the honest file, so no authority is gained. The rule that keeps it benign is that **nothing may ever consume the raw stored count** — always `ParseSlotCount` first.
- A PER-SLOT MAC would be a partial-lift vector — there is exactly ONE `[Integrity] Mac` over the whole file, and a test pins that.
- Operational rule: arm blocks AFTER upgrading binaries, not across an upgrade — an in-flight block armed under the old schema correctly freezes until re-armed.

## Provenance & maintenance
Distilled 06/07/2026; **rewritten 12/08/2026 for the v10 two-level (multi-block) canonical** during slice S1 of the v1.1 build — the previous revision documented v8 and a flat 15-parameter `BuildCanonical` that no longer exists. Deliberately cites SYMBOL NAMES, not line numbers: the v1.1 slices move `Service1.vb`/`Blocker.vb` lines constantly, and every prior line citation in this file had rotted. Re-verify against `ConfigIntegrity.vb` (the code const is ground truth) and the newest handoff in `OneDrive/dev/repos/monk-mode/handoffs/` whenever `CurrentSchemaVersion` changes.
