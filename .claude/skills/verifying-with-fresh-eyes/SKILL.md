---
name: verifying-with-fresh-eyes
description: "Fresh-eyes verifier, verification pass, GO/NO-GO gate, refute the diff, P0/P1 findings for the monk-mode repo — how to run the mandatory pre-commit verifier the way this repo expects. Use before committing any monk-mode slice that needs a verifier, when judging GO vs NO-GO, or when classifying findings as regression vs pre-existing."
---

# Verifying with fresh eyes (monk-mode)

The verifier is a fresh-context subagent over the diff. Its job is adversarial: try to
REFUTE the safety claim, don't confirm the builder's story.

## When mandatory
- [ ] Any slice touching `Service1.vb` enforcement paths, `ConfigIntegrity`, a canonical
  bump, or teardown — vault/dev/repos/monk-mode/plans/fable5-slices.md:121 lists the slice ids
  (A2, C1, C1b, C2b, C3b, C4, C5b, B1b, H2).
- [ ] Inputs-only slices get a LIGHT verifier pass instead (precedent: D1a, handoff
  2026-07-04-2206-d1a-site-presets.md).

## What the verifier does
- [ ] Fresh context — no builder assumptions carried in; it reads the diff and the live code.
- [ ] Attacks the safety claim from multiple angles: can this change cause an
  **early-lift** (block torn down before genuine expiry) or an **under-block** (sites/apps
  that should be blocked, aren't)?
- [ ] Independently reproduces the evidence: re-runs
  `C:/Users/samra/.dotnet/dotnet.exe build MonkMode.sln -c Release` (0-err) and
  `C:/Users/samra/.dotnet/dotnet.exe test MonkMode.sln`, and confirms the counts itself —
  never accepts the builder's numbers.
- [ ] Traces EVERY contract point of the slice to concrete evidence (a test, a code read
  with file:line, or an explicit "verified by reading only" note).
- [ ] Checks tests are non-tautological — a test must be able to fail if the behaviour
  regresses, not merely restate the implementation.

## Verdict criteria
Severity ladder (as used in the monk-mode handoffs): P0 = enforcement-breaking or data-loss
(early-lift/under-block possible); P1 = correctness bug in the slice's contract; P2 = nit/polish.
- [ ] **GO (enforcement-core / mandatory tier):** no P0/P1 findings.
- [ ] **GO (light tier):** no P0/P1/P2 findings.
- [ ] P2 nits: either applied in the same commit or explicitly WAIVED with written
  reasoning in the handoff — never silently dropped.
- [ ] Anything worse = NO-GO: fix and re-verify before commit.

## Findings discipline
- [ ] Distinguish **new regression** (introduced by this diff) from **pre-existing,
  documented** behaviour. Pre-existing safe behaviours are NON-findings: recorded in the
  handoff, not fixed in this slice (precedent: the two D1a non-findings).
- [ ] Documented-weak surfaces (e.g. the B7 crypto) are owned elsewhere — record, don't
  re-flag (repo CLAUDE.md:23).
- [ ] Every finding lands in the handoff with its classification and disposition
  (fixed / waived / deferred-to-slice-X), so the next session inherits it.

## Verifier fences
- [ ] Read-only + build/test only — the verifier never arms a block, never touches live
  hosts/registry/SCM, never pushes, never rewrites history.

## Provenance & maintenance
Distilled 06/07/2026 pre-model-sunset from repo CLAUDE.md + vault/dev/repos/monk-mode/ handoffs/specs.
Re-verify when repo CLAUDE.md or the newest handoff contradicts it (newest handoff wins).
