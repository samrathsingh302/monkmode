---
name: navigating-bypass-surface
description: "Bypass, B1-B11, threat model, severity, residual, mitigation status, smoke evidence, kill drill, attack surface — reasons about monk-mode's bypass surface: what each bypass B1-B11 is, its current status, the live-verification evidence backing each claim, and the residuals to carry rather than fix. Use when assessing a bypass claim, planning a hardening slice, updating ARCHITECTURE.md's bypass table, auditing residuals, or answering 'can X defeat the block?'."
---

# Navigating the bypass surface (B1-B11)

Canonical source: the bypass table at vault/dev/monk-mode/specs/ARCHITECTURE.md:271-283. Never assess a bypass from memory — read the row first; every claim below cites it.

## Status board — DATED SNAPSHOT (10/07/2026)
This table is a convenience snapshot only; ARCHITECTURE.md:271-283 is the source of truth. Read the live row before acting on any entry here.
| # | Bypass | Severity | Evidence |
|---|---|---|---|
| B1 | Force-kill service | Medium | 47/47 elevated smoke, kill drills K1-K4, 13/06/2026 |
| B2 | Edit/blank hosts | Low | self-heal live-verified 12/06/2026; re-confirmed in later smokes |
| B3 | Safe Mode boot | Low | 52/52 registration drill; in-Safe-Mode run deliberately NOT reboot-tested |
| B4 | Clock roll forward | Low | clock drills LIVE 9/0 10/07/2026 — +30m jump past Until ×3 held; B1c backward −30m roll lifted at ~117s real of 120s |
| B5 | DNS / DoH / VPN | High | B5a DONE — 71/0 smoke 01/07/2026 (tasks.md:15); B5b firewall deferred; VPN/Tor → B10 |
| B6 | `sc delete MONKMODE` | Medium | 63/63 — delete refused, stripped ACE self-healed 9.9s |
| B7 | Forge the config | Medium | `b7-failclosed-test` 10/0 14/06/2026 — corrupt MAC held, no re-stamp |
| B8 | Delete folder/ini | Low | assessed fail-closed 02/07/2026 (doc-only, verifier-confirmed) + C1b backup |
| B9 | Other user / portable app | Medium | assessed 02/07/2026 — app-kill spans all sessions when both enforcers run |
| B10 | Offline / WinRE edit | Medium | accepted OUT OF SCOPE — nothing on the same unencrypted disk defends |
| B11 | Hardcoded identifiers | Low | ACCEPTED-AS-IS 02/07/2026 — enabler, not an independent bypass |

## Evidence discipline
- [ ] Every mitigation claim must carry its live-verification evidence inline (drill counts + date), exactly as the table rows do.
- [ ] Freshest dated evidence wins: repo CLAUDE.md:16 still says "63/63, 14/06/2026" — SUPERSEDED. Freshest whole-stack: 69/0 elevated smoke 09/07/2026 (handoff 2026-07-09-0409) + clock drills 9/0 10/07/2026 (handoff 2026-07-10-0205) + b7-failclosed 10/0 09/07/2026. Cite those.
- [ ] Distinguish live-verified (elevated smoke drill) from unit-pinned (test only) from assessed (read-only analysis, doc-only). B3's Safe-Mode reboot and B4's creep cap are NOT live-drilled — say so. (B1c backward-roll IS live-drilled: 9/0 watched sitting 10/07/2026, handoff 2026-07-10-0205.)
- [ ] Remaining gated drills: E3/H3 (E5 is a gated external review, not a smoke — fable5-slices.md:80). B1c was cleared from the gated batch 10/07/2026.

## Everything chains to B1
- [ ] If the service + guardian both stay dead, the B2 hosts, B3 SafeBoot, B5a DoH and B6 ACE self-heals all stop — each of those rows explicitly chains its residual to B1 (ARCHITECTURE.md:274, 275, 277, 278).
- [ ] So when assessing any residual, state the chain: "defeated only by first winning B1 (double-kill inside the ~1s/10s restart windows, recovery-disable, or guardian pre-pin) or B10".
- [ ] B1's own residuals (table row :273): scripted near-simultaneous double-kill, `sc failure ... reset= 0`, suspend-then-kill, elevated guardian pre-pin. True kill-immunity = PPL/kernel driver = B10-tier, out of scope.

## Accepted — do not "improve"
- [ ] B10 offline/admin attack: honestly out of scope by design (README + table row :282).
- [ ] B11 fixed identifiers (`MONKMODE`, hosts marker, mutex, ini name, `mm_textbox`): accepted 02/07/2026 — obfuscation buys nothing because every scripted teardown hits the same fail-closed gate a manual one hits (B1/B2/B4/B6/B7). Do NOT propose randomising/salting identifiers (:283). No secret leaks via logs (`DefineTrace=false` in all four projects).

## Known residuals — carry, don't fix in passing
- [ ] B7 anti-rollback nonce gap: a stale-but-MAC-valid config REPLAY could drive an early lift — pre-existing, noted by the C1b verifier (ARCHITECTURE.md B8 row :191), not worsened by the backup.
- [ ] c3: CLI↔service lost-update race in `schedule --clear` — FIXED 06/07/2026 and landed on `monkmode` (issue #2 fix session, verifier GO; handoff 2026-07-06-2126-issue2-schedule-clear-race.md). No longer a carried residual; the CV-smoke flag is historical.
- [ ] O1: OnStart `SetAttr` error→lift path — FIXED 06/07/2026 on branch `fix/1-onstart-fail-closed` (verifier GO; handoff 2026-07-06-2122); merge into `monkmode` in flight 06-07/07/2026 — check `git log` + the newest handoff for live state; closes GitHub issue #1 on merge.
- [ ] O2: `WriteDefaultBlock` blanks the ini pre-Save — cosmetic (handoff 2026-07-04-2206, item 4).
- [ ] B5b (system-DNS/non-browser DoH firewall) deferred 30/06 per "minimal collateral"; B9 all-user app-kill folded into D2 — deferred, not regressions.

## When updating the table
- [ ] Edit only the affected row; keep the strikethrough severity history (e.g. ~~Critical~~ → Low) and the inline evidence + date.
- [ ] A severity change needs new dated evidence, not re-reasoning over old evidence.
- [ ] Sweep restated copies afterwards (repo CLAUDE.md test line, README) or note the drift where you found it.

## Provenance & maintenance
Distilled 06/07/2026 pre-model-sunset from vault/dev/monk-mode/specs/ARCHITECTURE.md (bypass table :182-194), vault/dev/monk-mode/tasks.md, plans/fable5-slices.md and the vault handoffs (the newest dated handoff always wins over this snapshot). Re-verify against ARCHITECTURE.md's bypass table and the newest handoff in vault/dev/monk-mode/handoffs/ when they change — newest dated evidence wins.
