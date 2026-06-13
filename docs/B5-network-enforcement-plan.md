# B5 — Network-layer enforcement (DNS / DoH / VPN bypass) — design plan

**Status:** PLAN for review (authored 2026-06-14, not started). No code yet.
**Author:** carry-on planning while live verification of B1–B7 is blocked.
**Decision needed from Samrath before building** — see §7 Open questions.

---

## 1. The bypass (why B5 matters)

MonkMode blocks by writing `127.0.0.1 <host>` into the hosts file (B2 keeps it
locked + self-healing). Hosts only intercepts the **OS stub resolver**. A
*determined* user defeats the entire block without touching MonkMode at all:

| Bypass | Effort | Defeats hosts? |
|---|---|---|
| Browser **DoH** (Firefox/Chrome/Edge "Secure DNS" → Cloudflare/Google) | one settings toggle | **Yes** — DNS goes out over HTTPS:443, never hits the stub resolver |
| Change system **DNS** to 8.8.8.8 / a DoH proxy | one settings change | Partial (hosts still applies to the stub resolver, but apps doing their own DNS bypass it) |
| **VPN / proxy / Tor** | install an app | **Yes** — traffic tunnels past the local resolver entirely |
| `nslookup` / app-embedded resolver | trivial | **Yes** for that app |

So hosts-blocking is a *casual* control. B5's goal: push enforcement down to the
**network layer** so DNS-over-HTTPS and a changed system resolver can't trivially
bypass it. **VPN/Tor to an arbitrary exit is explicitly NOT fully closable** while
the user keeps admin (that's the B10 ceiling) — B5 targets the casual→determined
DoH/DNS bypass, not a nation-state tunnel.

This is the **highest-impact remaining bypass**: every other mitigation (B1/B2/
B3/B4/B6/B7) is moot if the user just flips on Secure DNS.

---

## 2. Approach options

### (A) Windows Firewall rules via the `INetFwPolicy2` COM API / `netsh advfirewall`
Block outbound to known DoH endpoints + force DNS through the (hosts-filtered)
stub resolver.
- **Block outbound UDP/TCP 53 except to the local resolver** → apps can't do their
  own plaintext DNS.
- **Block outbound 443 to a curated list of public DoH provider IPs** (Cloudflare
  1.1.1.1, Google 8.8.8.8, Quad9, NextDNS, etc.) → kills the common browser DoH
  toggles (which use those well-known endpoints).
- **Block the canary/bootstrap** so browsers fall back to the system resolver.
- Pro: high-level, no kernel driver, scriptable, removable=re-assertable (fits the
  B2/B3 self-heal pattern). Con: IP-list maintenance; a DoH provider on a shared
  CDN IP is hard to block without collateral; doesn't stop a VPN.

### (B) Windows Filtering Platform (WFP) user-mode filters (`fwpuclnt`/`Fwpm*`)
Add WFP filters at `FWPM_LAYER_ALE_AUTH_CONNECT_V4/V6` that block connects to
blocked IPs / DoH endpoints, weighted, in a MonkMode sublayer.
- Pro: more robust than firewall rules, same "add filter / re-assert / remove"
  lifecycle as B2/B3, can be tied to the service's lifetime (filters auto-removed
  if the engine handle closes — a nice fail-safe). Con: heavier P/Invoke surface
  (`FwpmEngineOpen`, `FwpmFilterAdd0`, `FWPM_FILTER0`/`FWPM_FILTER_CONDITION0`
  marshalling); still IP/endpoint-based unless paired with name resolution.
- A WFP **callout driver** (kernel) could do true per-hostname SNI/DNS inspection
  but that's a signed kernel driver — **out of scope** (huge effort, signing, BSOD
  risk). Not B5.

### (C) Force-and-pin the system DNS + disable DoH via policy
Set the system resolver to a controlled value, and set the registry/Group-Policy
that **disables browser DoH** (e.g. Edge/Chrome `DnsOverHttpsMode=off`, Firefox
`network.trr.mode=5`), re-asserted each tick like B3.
- Pro: directly kills the #1 bypass (browser Secure DNS) with documented policy
  keys; cheap; pure registry (testable like B3/SafeBoot). Con: per-browser policy
  coverage; a portable/dev browser build can ignore policy; doesn't stop VPN.

### Recommended: **(C) + (A)** layered, in that order of value
1. **(C) browser-DoH-off policy** — cheapest, closes the most common real bypass
   (the browser toggle), pure-registry self-heal (mirrors B3 exactly — we already
   have that pattern + tests).
2. **(A) firewall block of port-53-except-local + known DoH IPs** — closes
   app-level DNS and standalone DoH.
3. **(B) WFP** only if (A) proves insufficient — it's the same shape with a
   heavier API; defer unless needed.

This stages value-first and reuses the **B3 self-heal pattern** (assert at OnStart,
re-assert each tick gated on `Not EffectiveBlockHasExpired`, remove at genuine
expiry, no-data-loss = only touch MonkMode's own rules/keys). It also inherits the
B4/B7 fail-closed posture for free (the gate is the same).

---

## 3. Integration with the existing service

- **New module(s)** mirroring the B3/B6 split: a PURE, unit-testable layer (which
  policy keys / which firewall-rule specs to assert; idempotent add/remove; the
  curated DoH-endpoint list as a pinned const, like the B1 recovery policy and B3
  SafeBoot consts — drift = silent disarm) + a thin live-I/O seam (the actual
  registry / `INetFwPolicy2` calls) covered by the smoke test.
- **Lifecycle** = the B3 hosts/SafeBoot pattern, byte-for-byte:
  - `OnStart` (active path only): assert the DoH-off policy + firewall rules.
  - Each 10s tick while `Not EffectiveBlockHasExpired` (MAC + HighWater gated):
    read-only probe → re-assert only if drifted (no churn).
  - `stopMe()` at genuine expiry: remove ONLY MonkMode's own rules/keys.
  - `unblock --force` (B6 escape hatch): add the rule/key removal to `DoUnblock`'s
    teardown sequence + `cleanup.ps1`.
- **No-data-loss fence**: name every firewall rule with a `MonkMode-` prefix and
  only ever add/remove those; never touch the user's own firewall rules or the
  global DoH policy if the user had set one (snapshot + restore, like B2's hosts
  snapshot — TBD, see open questions).
- **Curated DoH IP list**: a pinned const list + a doc note that it's
  best-effort/maintained (new providers appear). Honest residual.

---

## 4. The honest ceiling (must be documented, like B10)

B5 does NOT make blocking unbreakable:
- An **admin** can delete the firewall rules / flip the policy back (≤10s
  re-assert window, same residual as B2/B3; chained to B1 keeping the service
  alive).
- A **VPN/Tor** to an arbitrary exit tunnels past everything — **not closable**
  without endpoint control (B10). B5 should block *known* VPN bootstrap where
  cheap but must not claim to stop VPNs.
- A **portable browser** ignoring policy, or a hard-coded-IP DoH client, evades
  (C); (A)'s port-53 + IP block catches most but not a DoH server on a shared
  CDN IP.
- **DoH on 443 to a CDN IP shared with legitimate sites** can't be IP-blocked
  without collateral — a known hard limit.

Realistic claim: "B5 defeats the casual→determined DNS/DoH bypass (browser Secure
DNS, changed system DNS, standalone DoH, app-level DNS). VPN/Tor and a determined
admin remain the documented ceiling (B10)."

---

## 5. Testing strategy

- **Unit (pure, no elevation):** the policy-key set + firewall-rule specs +
  DoH-endpoint list are pinned consts (drift test, like B1/B3); idempotent
  add/remove/probe predicates; snapshot/restore logic if we add it. This is where
  the real logic lives and is fully testable offline.
- **Live (elevated smoke test, Samrath):** extend `run-smoketest.ps1` — after
  arming a block: assert the DoH-off policy keys present + the `MonkMode-*`
  firewall rules present; **functional check**: with the block live, a DoH query
  to a blocked provider fails / falls back; delete a rule → re-asserted ≤15s
  (self-heal drill, like B3 §2d); post-expiry all MonkMode rules/keys removed,
  user's own untouched. Same structure as the B2/B3 drills.
- **The functional "is example.com actually unreachable via DoH" check** is the
  one that needs a real machine — design it into the smoke test, not unit tests.

---

## 6. Rough size / sequencing

- **Slice B5a (MVP, ~1 session):** (C) browser-DoH-off policy — pure layer + B3-style
  self-heal + tests + smoke-test extension. Closes the #1 bypass cheaply.
- **Slice B5b (~1–2 sessions):** (A) firewall rules (port-53-except-local + DoH IP
  list) via `INetFwPolicy2` — pure rule-spec layer + live seam + snapshot/restore
  + tests + smoke drills.
- **Slice B5c (only if needed):** (B) WFP filters — defer.

One slice per session (house rule). B5a first — best value/effort.

---

## 7. Open questions for Samrath (decide before building)

1. **Scope:** start with B5a (browser-DoH-off policy) only, or commit to B5a+B5b
   (policy + firewall) as the B5 definition?
2. **Collateral tolerance:** blocking outbound port 53 except to the local
   resolver, and blocking DoH-provider IPs, can break *legitimate* tools (a
   corporate VPN's DNS, a dev using 1.1.1.1). On your personal machine that's
   probably fine — confirm you accept the collateral, or we scope to browser
   policy only.
3. **User-owned firewall/DoH settings:** do you already use custom firewall rules
   or a DoH policy we must snapshot + restore (B2-style), or is the machine's
   firewall "stock" so we can assert/remove our own rules freely?
4. **VPN stance:** confirm VPN/Tor stays explicitly out of scope (B10 ceiling) —
   B5 won't try to stop a VPN, only DNS/DoH.
5. **Priority vs. live-verifying B1–B7 first:** B1–B7 are code-complete + audited
   but NOT live-verified (blocked on an elevated run). Do you want to live-verify
   the existing stack before adding B5 surface, or proceed with B5a in parallel?

---

*This plan reuses the proven B3 self-heal + B6 escape-hatch + B4/B7 fail-closed
patterns rather than inventing new machinery — lowest-risk path to closing the
biggest remaining bypass.*
