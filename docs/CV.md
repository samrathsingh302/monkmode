# MonkMode — CV & interview pack

Everything you need to put this project on your CV and talk about it under
pressure. Numbers and claims below are true as of 14/06/2026 — if the project
moves on, update this file in the same session.

**Honesty rules (read first, they protect you):**
- It is a **fork**. Say "forked and modernised an open-source GPLv3 blocker" —
  never imply you designed the original enforcement model. The migration,
  CLI, tests, smoke-test bug hunt, fail-closed redesign, self-healing
  hardening and the watchdog guardian (the fourth process) ARE yours; the
  three-process enforcement core and hosts-sinkhole idea are inherited.
  Interviewers respect the distinction; blurring it is the fastest way to lose
  them.
- The crypto is **deliberately weak by design inheritance** (TripleDES,
  hardcoded key, no integrity check). If asked, own it: "known-weak, catalogued
  as bypass B7, fix planned (HMAC/signed config) — not silently ignored."
- Don't claim "unbreakable". The honest ceiling — an admin with physical disk
  access always wins offline (B10) — is your *strongest* talking point, not a
  weakness. It shows threat-model maturity most candidates don't have.

---

## 1. The one-liner (CV project title)

> **MonkMode** — tamper-resistant website/app blocker for Windows
> (VB.NET / .NET 8, Windows services, xunit, GitHub Actions)

## 2. CV bullets — pick a variant

**Short (2 lines, space-tight CV):**
- Forked and modernised an unbuildable 2011 GPLv3 VB.NET codebase to .NET 8;
  replaced the GUI with a CLI and authored a Win32 P/Invoke service-control
  layer the original was missing.
- Threat-modelled the product (11 ranked bypasses), then hardened it:
  fail-closed expiry, self-healing hosts-file enforcement, culture-safe
  persistence — backed by an xunit suite running in CI.

**Medium (4 bullets — recommended):**
- Forked an open-source website blocker whose public source had never compiled;
  migrated VB.NET 2010/.NET 2.0 → .NET 8, converted the WinForms GUI to a CLI,
  and wrote the missing Windows service install/start layer via advapi32
  P/Invoke.
- Produced a ranked bypass-surface analysis (B1–B11) of the four-process
  enforcement design (elevated CLI, LocalSystem service, user-session notifier,
  SYSTEM watchdog guardian) with an honestly documented threat ceiling.
- Ran an elevated end-to-end smoke test (15/15) that exposed three bugs static
  checks missed — including Windows' DNS resolver silently ignoring `0.0.0.0`
  hosts entries and a file-handle bug that let `ipconfig /flushdns` defeat the
  block — and fixed all three.
- Hardened failure modes to fail closed (corrupted state keeps the block
  standing) and made enforcement self-healing (tampered hosts entries restored
  within 10 s), with an xunit suite (C#, locale-matrix and tamper edge cases)
  gating every push via GitHub Actions.

**Long (for a portfolio page / cover letter paragraph):**
> MonkMode is my tamper-resistant self-control blocker for Windows, forked from
> the GPLv3 Cold Turkey codebase. The inherited source was 2011-era VB.NET that
> had never actually built — it referenced a helper library that was never
> published — so the first job was real software archaeology: migrating five
> Visual Studio 2010 solutions to SDK-style .NET 8, writing the missing service
> control layer against the raw Win32 API, and replacing the GUI with a clean
> CLI. The interesting part is the security posture: the product's adversary is
> *its own administrator*, so I catalogued every realistic bypass (eleven,
> ranked), documented the honest ceiling (an offline admin always wins), and
> hardened what software can defend: expiry logic that fails closed on
> corrupted state, a service that restores tampered hosts entries within ten
> seconds, and clock-change compensation. A live elevated smoke test caught
> three bugs the compiler never would — the best being that Windows' resolver
> silently ignores `0.0.0.0` sinkholes — and an xunit suite now gates every
> push in CI.

## 3. The 30-second elevator pitch (spoken)

> "I forked an open-source website blocker and turned it into something I
> actually trust to keep me off Reddit during exams. The codebase was 2011
> VB.NET that didn't even compile, so I migrated it to .NET 8, replaced the GUI
> with a CLI, and wrote the Windows service plumbing it was missing. The fun
> part is that the threat model is *me* — the adversary has admin rights — so I
> catalogued every way to break it, hardened the ones software can defend, like
> restoring the hosts file within ten seconds of tampering and failing closed
> on corrupted state, and documented honestly which ones it can't. Testing it
> live found bugs I'd never have caught statically — Windows literally ignores
> `0.0.0.0` in the hosts file."

## 4. STAR stories (your five best — rehearse these)

### 4.1 The DNS bug hunt (use for: debugging, persistence, "hardest bug")
- **S:** After migrating the blocker to .NET 8 it compiled cleanly, but I don't
  trust "it compiles", so I built an elevated end-to-end smoke test: start a
  real 2-minute block, verify enforcement, wait for auto-expiry, verify clean
  teardown.
- **T:** The block "worked" — then any `ipconfig /flushdns` made blocked sites
  resolve again. Intermittently. Worst kind of bug.
- **A:** Wrote DNS diagnostic probes and isolated **two stacked causes**:
  (1) the inherited code wrote `0.0.0.0` sinkhole entries, which the Windows
  resolver silently ignores — it falls through to real DNS; `127.0.0.1` is
  honoured and suppresses both A and AAAA lookups. (2) The service held a
  persistent write handle on the hosts file (`FileShare.Read`), which blocked
  the DNS Client service from *re-reading* it — so the block only appeared to
  work because of a cache race. I switched entries to `127.0.0.1` and removed
  the persistent handle entirely, enforcing the lock via the read-only
  attribute re-asserted on the service's 10-second timer.
- **R:** Block now survives `flushdns` (verified in the re-run smoke test,
  15/15). Lesson I quote: *a compile-only verification is a hypothesis, not
  evidence* — the three worst bugs were all invisible to the compiler.

### 4.2 Fail-open → fail-closed (use for: security mindset, attention to detail)
- **S:** The block's end time is stored encrypted in an ini file. Both the
  service and the notifier parsed it with `DateTime.TryParse` — and **ignored
  the return value**.
- **T:** An unparseable end time (corrupted file, or a legacy machine-locale
  value) became `DateTime.MinValue`, which read as "expired" — the service
  lifted the block. For a tamper-resistance product, that's failing *open*:
  corrupting one file ends the block early.
- **A:** Inverted the failure direction: extracted the expiry decision into a
  pure, testable function where an unparseable value means *not expired* — the
  block stands until the state is fixed. Same fix in the notifier's
  clock-change compensation.
- **R:** Corruption now favours the block, not the bypass. Talking point: in
  security software, *the failure direction of every error path is a design
  decision* — most code fails open by accident.

### 4.3 Self-healing enforcement (use for: ownership, designing under constraints)
- **S:** The service kept the hosts file read-only, but any admin can clear an
  attribute, delete the block entries, and re-set it. The service re-asserted
  the attribute every 10 s but never noticed missing entries — catalogued as
  bypass B2 in my threat model.
- **T:** Close the gap without touching the parts of the smoke-tested service I
  couldn't re-verify live (no elevation available that session).
- **A:** At block time the CLI snapshots the exact marker block it wrote; the
  service's timer compares hosts against the snapshot each tick and, if the
  block is active, restores tampered or deleted entries — implemented as a
  pure, unit-testable repair function that strips any partial block and
  re-appends the canonical one, preserving the user's own hosts content
  byte-for-byte.
- **R:** Manual hosts edits are now undone within 10 seconds. The snapshot
  itself is deletable by an admin — that residual is documented, not hidden.

### 4.4 The namespace collision (use for: pragmatism, knowing your tools)
- **S:** Adding the first-ever test project, I hit a VB.NET landmine: VB
  namespaces are case-insensitive, so the `MonkMode` (CLI) and `monkmode`
  (service) namespaces **merge** inside a VB test project, making the
  deliberately duplicated `Simple3Des`/`IniFile` types ambiguous — it cannot
  reference both.
- **A:** Wrote the test project in **C#** (case-sensitive), which can reference
  both assemblies and even assert that the duplicated crypto implementations
  produce identical ciphertext — a contract test that the duplication can't
  silently drift.
- **R:** Full suite green, including locale-matrix tests (de-DE, fr-FR, en-US,
  en-GB) proving datetime persistence survives any machine locale. Shows you
  pick tools per problem, not per habit.

### 4.5 Software archaeology (use for: legacy code, working with what exists)
- **S:** The upstream source — five VS2010 solutions — had **never built from
  public source**: it referenced a third-party `ServiceTools` library the
  author never shipped.
- **A:** Re-implemented the missing layer myself against the raw Win32 API
  (`OpenSCManager`/`CreateService`/`StartService` via advapi32 P/Invoke),
  migrated everything to SDK-style .NET 8 projects, deleted two of the five
  programs as dead weight, and converted the GUI to a CLI.
- **R:** From "doesn't compile" to: builds clean, full test suite in CI, and a
  live-verified install/enforce/expire cycle. Most candidates have never made
  an old codebase *work*; this is direct evidence you can.

## 5. Interview Q&A (the questions you'll actually get)

**"Why VB.NET?!"**
> Inherited, not chosen — the fork is GPLv3 VB.NET, and a rewrite would have
> thrown away working, subtle enforcement logic before I understood it. The
> engineering judgement was *minimising rewrite risk*: migrate the platform,
> keep the proven core, write new code (CLI, P/Invoke layer) cleanly, and put
> the tests in C# where VB's case-insensitivity made it the wrong tool. I'd
> rather defend that trade-off than claim I'd never touch an unfashionable
> language.

**"Isn't a hosts-file blocker trivial to bypass?"**
> Against a determined admin with a USB stick — yes, eventually, and my
> ARCHITECTURE.md says so in writing: that's bypass B10 and the documented
> ceiling. The realistic bar is the commercial one — Cold Turkey Pro, Freedom —
> defeating casual-to-determined bypasses. Eleven bypasses are catalogued and
> ranked; each hardening phase closes specific ones, and the ones software
> can't close get non-software mitigations: a non-admin daily account,
> BitLocker, a BIOS boot lock. The honest threat model is the feature.

**"What's the architecture?"**
> Four processes with different privilege levels: an elevated CLI that
> configures everything, a LocalSystem service with `CanStop=False` as the
> enforcement core on a 10-second loop, a per-user notifier for app-kills,
> clock-change compensation and the expiry toast, and a SYSTEM-session watchdog
> guardian that SCM-restarts the service if it is force-killed and relaunches
> the notifier (the B1 layer-2 mitigation). They share state through an
> encrypted ini and a hosts-file marker convention — the config contract is the
> sacred interface; all four binaries must agree on it byte-for-byte.

**"What would you do next?"**
> Phase 3 has already closed the casual-to-determined kills, all live-verified:
> a watchdog pair so force-killing the service gets it restarted (B1), Safe Mode
> registration (B3), and an HMAC over the config so editing the end time is
> rejected rather than trusted (B7). The biggest item still open is moving from
> hosts-file to WFP/firewall-layer enforcement so DoH and VPNs can't sidestep
> DNS (B5) — the one remaining Critical. Each maps to a named bypass — hardening
> without a threat model is just decoration.

**"What did you learn?"**
> Three things I'll reuse anywhere: (1) runtime verification beats static
> confidence — my three worst bugs compiled perfectly; (2) every error path has
> a failure *direction*, and you choose it deliberately or it defaults to open;
> (3) honest limits make a security story stronger, not weaker.

**"Why does this project matter to you?"** *(the differentiator — it's real)*
> I built it for myself and I run it on my own machine — final-year exams, gym
> discipline, monk mode. The adversary in the threat model is literally me at
> 1am. Dogfooding a security tool against yourself is the fastest feedback loop
> there is: the smoke test wasn't a checkbox, it was "does this actually keep
> me off Reddit".

## 6. Numbers to have cold

| Number | Fact |
|---|---|
| 4 | cooperating processes (elevated CLI / LocalSystem service / user notifier / SYSTEM watchdog guardian) |
| .NET 2.0 → 8 | platform migration (VS2010 VB.NET → SDK-style, 5 solutions → 1) |
| 11 | catalogued bypasses (B1–B11), ranked, with honest ceiling (B10) |
| 15/15 | live elevated smoke-test checks passed (block → enforce → expire → teardown) |
| 3 | real bugs the smoke test found that compilation couldn't |
| 10 s | enforcement loop: read-only re-assert + tamper repair + process kills |
| 81 | xunit tests (C#), locale-matrix + tamper edge cases, green in CI |
| 4 | locales the datetime persistence is proven under (de-DE, fr-FR, en-US, en-GB) |

## 7. Skills this project evidences (for tailoring per application)

- **Windows internals:** services & SCM, LocalSystem vs user sessions, P/Invoke
  (advapi32), registry autorun, hosts file + DNS Client behaviour, file
  attributes/locking, UAC manifests.
- **Security engineering:** threat modelling, ranked bypass analysis,
  fail-closed design, tamper detection/response, honest residual-risk
  documentation.
- **Legacy modernisation:** .NET Framework → .NET 8 migration, dead-code
  removal, replacing unshipped dependencies, preserving a working core under a
  no-regression constraint.
- **Testing & verification:** xunit, locale-matrix testing, contract tests
  across duplicated implementations, pure-function extraction for
  testability, end-to-end smoke testing with teardown, CI (GitHub Actions).
- **Judgement:** fork-vs-rewrite trade-off, C# tests in a VB codebase, scoping
  what software can and cannot defend.

**Role-tailoring hints:** security-flavoured role → lead with §4.2/§4.3 and the
threat model; platform/backend role → lead with §4.5 (migration) and §4.1
(debugging); generalist/placement → medium bullets as-is, pitch from §3.

## 8. Logistics

- Repo is **private** (`github.com/samrathsingh302/monkmode`). On the CV, list
  the project without a link or with "(private repo — walkthrough on
  request)"; offer a screen-share demo of `monkmode status` + the test suite.
  Making it public is possible (GPLv3 fork with attribution is compliant) but
  is your call — flag it to Claude when decided and the README is already
  presentation-ready.
- The interesting commits to walk through in a live interview: the smoke-test
  fix wave, the fail-closed expiry commit (`640db62`), and the B2 self-healing
  hosts change.
