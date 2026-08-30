# Copyright (C) 2026 Samrath Singh
#
# This file is part of MonkMode, a fork of Cold Turkey.
# Source: https://github.com/samrathsingh302/monkmode
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with this program.  If not, see <https://www.gnu.org/licenses/>.

# MonkMode CV + Section-D live smoke (RUN ELEVATED).
#
# Covers the owed live checks that run-smoketest.ps1 does NOT (it proves the
# B1-B7 enforcement core). This proves the C-core accountability model and the
# Section-D usability surface, end-to-end against the real service:
#
#   CV1  partner-code verify + rotate  (each block mints a fresh one-time code;
#        the service KDF-verifies `unblock --code`, a wrong code stays blocked)
#   CV2  code-only exit  (bare `unblock` is refused; the code still lifts it) [+D5]
#   D1a  --preset expands to its domains (social -> reddit.com et al in hosts)
#   D2a  --app-preset expands to its apps (games -> steam.exe in the config)
#   D1b/D2b  account-default blocklist + app list inherit (both dimensions) when
#        a block names no source of its own
#   D3   `monkmode stats` renders the recorded history
#   D5   `status` renders the code-only / schedule exit lines
#   #2   `schedule --clear` tears an armed schedule down cleanly (no orphan)
#   D2c  `--all-session-kill` arms AllSession=yes (MAC-covered) and the service
#        kills the blocked app; the pure cross-SESSION widening is unit-pinned
#        (ProcessInKillScope) and needs a 2nd interactive session to eyeball.
#
# ############################################################################
# # BROKEN BY 319:  THIS SCRIPT'S TEARDOWN NO LONGER EXISTS. DO NOT RUN IT AS-IS.
# #
# # Ledger 319 (30/08/2026) removed `monkmode unblock --force` and deleted
# # tools\smoke\cleanup.ps1. Both were this smoke's teardown: every section armed a
# # 5-minute block and called ForceDown between phases, and the global finally ran
# # cleanup.ps1 so an abort could never leave the box blocked. Neither exists now.
# #
# # WHAT IT NEEDS (a live, elevated sitting - it cannot be verified from a bench):
# #   1. ForceDown must become a PARTNER-CODE lift: `unblock --code <CODE>`, then
# #      WaitSvcLifted (Stopped-or-gone, RUNBOOK E9), then `sc.exe delete MONKMODE`
# #      to get back to 'gone' for the next section's precondition.
# #   2. That needs the code in scope at every teardown, so the ArmQuiet sections
# #      (D1a/D1b/D2a/D2b/D2c) must capture the arm output like Arm does and parse
# #      the code with ParseCode. Read the PIPE-WEDGE note below first - piping the
# #      arm was live-proven fixed on 14/07/2026, but old dists still wedge.
# #   3. The global finally has no rescue any more. The honest replacement is to
# #      keep every block short enough that natural expiry IS the backstop, and to
# #      say plainly that an aborted run leaves a block standing until its timer
# #      runs out. There is no escape hatch to fall back on, by design.
# #   4. CV3 was deleted outright rather than reworked: cooling-off is gone.
# ############################################################################
#
# Every block uses a SHORT --for (minutes) as a safety floor.
# NO clock manipulation here (see clock-drill-test.ps1 for B1c/B4).
#
# Usage (ELEVATED):  powershell -ExecutionPolicy Bypass -File tools\smoke\cv-d-smoke.ps1
param([string]$Dist)

$ErrorActionPreference = 'Continue'
if (-not $Dist) {
  if ($PSScriptRoot) { $Dist = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'dist' }
  else               { $Dist = 'C:\Users\samra\repos\monk-mode\dist' }
}
$monk    = Join-Path $Dist 'monkmode.exe'
$ini     = Join-Path $Dist 'monkmode_settings.ini'
$hosts   = "$env:SystemRoot\System32\drivers\etc\hosts"
# BROKEN BY 319: cleanup.ps1 was deleted with the escape hatch it wrapped.
$testExe = Join-Path $env:TEMP 'mmsmoke_testapp.exe'
$FOR     = 5   # minutes: safety floor with headroom so a block never auto-expires mid-section
               # (--for 2 raced the multi-step assertions); every block is force-unblocked first
$pass = 0; $fail = 0
function Check($name, $cond) {
  if ($cond) { Write-Host "  [PASS] $name" -ForegroundColor Green; $script:pass++ }
  else       { Write-Host "  [FAIL] $name" -ForegroundColor Red;   $script:fail++ }
}
function SvcState { $s = Get-Service MONKMODE -ErrorAction SilentlyContinue; if ($s) { "$($s.Status)" } else { 'gone' } }
function WaitSvc([string]$want, [int]$sec) {
  $u = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $u) { if ((SvcState) -eq $want) { return $true }; Start-Sleep -Milliseconds 500 }
  return ((SvcState) -eq $want)
}
# A LIFT ends Stopped-but-present (RUNBOOK [E9]: the service strips hosts and stops itself;
# its registration stays installed - only an explicit `sc.exe delete` removes it). 'gone'
# is accepted too so the helper keeps working if a teardown ever races a force-clean.
function WaitSvcLifted([int]$sec) {
  $u = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $u) { if ((SvcState) -in @('Stopped','gone')) { return $true }; Start-Sleep -Milliseconds 500 }
  return ((SvcState) -in @('Stopped','gone'))
}
# PIPE-WEDGE WARNING (belt-and-braces): NEVER pipe/capture a block-arm's stdout on an
# OLD dist that predates the P2 notifier handle-inheritance fix. There, the CLI's
# RegisterAndLaunchNotifier spawns mm_notify.exe so it INHERITS the CLI's stdout, so any
# PowerShell pipeline reading that stdout (| ForEach-Object, | Out-Null on a capture, a
# `$x = & monk block` assignment) blocks until the notifier exits at block EXPIRY - every
# later check then runs post-expiry (this voided drills on 2026-07-10 + retro-explains the
# 09/07 cv-d spurious FAILs). Blocker.RegisterAndLaunchNotifier fixed the ROOT cause in
# source (10/07/2026: UseShellExecute=True -> no handle inheritance -> pipe closes on CLI
# exit), so a CURRENT dist doesn't wedge at all; these helpers stay pipe-free anyway so a
# stale smoke build can never re-wedge the script. Mirrors clock-drill-test.ps1 (8db2fcb).

# Arm a block WITHOUT reading its stdout (bare, exactly like clock-drill-test.ps1): the
# arm's output flows to the console, never through a PS pipeline, so it cannot wedge on any
# build. Use for arms whose output we don't parse. Waits for the service to come up.
function ArmQuiet([string]$argline) {
  & $monk block @($argline -split ' ')   # bare: stdout -> console, no pipe (no wedge)
  [void](WaitSvc 'Running' 60)   # poll, not a fixed sleep: install+start races StartPending under load
  Start-Sleep -Seconds 2         # small settle for the hosts write + config flush
}
# Arm a block AND return its stdout lines - ONLY for CV1/CV2, which must parse the one-time
# accountability code minted on stdout (ParseCode). This one unavoidably captures stdout, so
# it relies on the P2 source fix (current dist) to not wedge; on a pre-fix dist these two
# arms would still stall until expiry, so run CV1/CV2 against a CURRENT build. Waits for the
# service to come up.
function Arm([string]$argline) {
  $out = & $monk block @($argline -split ' ') 2>&1 | ForEach-Object { "$_" }
  [void](WaitSvc 'Running' 60)
  Start-Sleep -Seconds 2
  return $out
}
# The one-time accountability code is printed on the line after the "Emergency
# unlock code" header, indented. Return it trimmed, or $null.
function ParseCode($lines) {
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'Emergency unlock code') { if ($i + 1 -lt $lines.Count) { return $lines[$i + 1].Trim() } }
  }
  return $null
}
# BROKEN BY 319: there is no forced teardown any more. See the header - this must
# become a partner-code lift plus `sc.exe delete MONKMODE`, with the code threaded
# through from each section's arm. Left THROWING rather than silently no-opping, so a
# run cannot appear to pass while every section leaks a live block into the next.
function ForceDown {
  throw 'BROKEN BY 319: ForceDown needs rewriting as a partner-code lift (see the header).'
}
function HostsHas([string]$re) { $t = try { Get-Content $hosts -Raw } catch { '' }; return ($t -match $re) }
function IniHas([string]$re)   { $t = try { Get-Content $ini -Raw } catch { '' }; return ($t -match $re) }

# --- 0. preconditions --------------------------------------------------------
$me = [Security.Principal.WindowsPrincipal]([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Write-Host 'Run ELEVATED.' -ForegroundColor Red; exit 1 }
if (-not (Test-Path $monk)) { Write-Host "monkmode.exe not at $monk - build dist first." -ForegroundColor Red; exit 1 }
if (Get-Service MONKMODE -ErrorAction SilentlyContinue) { Write-Host 'MONKMODE already exists - let any block end, then `sc.exe delete MONKMODE` while idle.' -ForegroundColor Yellow; exit 1 }
& $monk setup --partner 'Smoke Tester (smoke@test.local)' 2>&1 | Out-Null   # idempotent; ensures SetupIsComplete

try {
  # --- CV2 + CV1 + D5: committed block = code-only exit; code verify ----------
  Write-Host "`n=== CV2/CV1/D5: committed block, code-only exit, code verify ===" -ForegroundColor Cyan
  $outA = Arm "--sites example.com --commit --for $FOR"
  Check "committed block armed (service running)" ((SvcState) -eq 'Running')
  $codeA = ParseCode $outA
  Check "CV1 one-time accountability code minted" ($codeA -and $codeA.Length -ge 6)
  $st = & $monk status 2>&1 | ForEach-Object { "$_" }
  # LEDGER 319: the matched substring was 'committed block'. Every block is committed
  # now, so `status` prints one exit sentence for all of them and the phrase is gone.
  Check "D5 status shows the code-only exit line" (($st -join "`n") -match '--code <CODE>')
  Check "D5 status offers no other way out" (($st -join "`n") -match 'There is no other way out')
  # bare unblock must be REFUSED (exit 1)
  & $monk unblock 2>&1 | Out-Null
  Check "CV2 bare 'unblock' refused (exit 1)" ($LASTEXITCODE -eq 1)
  Check "CV2 block still enforced after refused unblock" ((SvcState) -eq 'Running' -and (HostsHas 'example\.com'))
  # the correct code lifts it (service adjudicates on its tick)
  & $monk unblock --code $codeA 2>&1 | Out-Null
  Check "CV1 correct code lifted the block (service Stopped-or-gone, <=40s; RUNBOOK E9)" (WaitSvcLifted 40)
  Check "CV1 hosts block removed after code lift" (-not (HostsHas '#### MonkMode Entries ####'))
  ForceDown

  # --- CV1 rotate + D5 --------------------------------------------------------
  # LEDGER 319: CV3 (the cooling-off trigger flow - bare `unblock` registering a
  # service-computed ~1h pending, `--cancel` clearing it) was DELETED from here rather
  # than reworked: there is no cooling-off exit to smoke, and bare `unblock` now refuses
  # on every block rather than only on a committed one. What survives is CV1's rotate
  # half plus the wrong-code check, and the bare-unblock refusal now covers ALL blocks.
  Write-Host "`n=== CV1(rotate)/D5: fresh code per block; a wrong code never lifts ===" -ForegroundColor Cyan
  $outB = Arm "--sites example.com --for $FOR"
  $codeB = ParseCode $outB
  Check "CV1 rotate: 2nd block minted a DIFFERENT code" ($codeB -and $codeB -ne $codeA)
  # wrong code must NOT lift
  & $monk unblock --code "WRONG-$codeB" 2>&1 | Out-Null
  Start-Sleep -Seconds 16
  Check "CV1 wrong code did NOT lift (still enforced)" ((SvcState) -eq 'Running')
  # bare unblock is refused on EVERY block now (ledger 319), not just a committed one
  & $monk unblock 2>&1 | Out-Null
  Check "319 bare 'unblock' refused on an ordinary block too (exit 1)" ($LASTEXITCODE -eq 1)
  Check "319 block still enforced after the refusal" ((SvcState) -eq 'Running' -and (HostsHas 'example\.com'))
  ForceDown

  # --- D1a: --preset expands to its domains -----------------------------------
  Write-Host "`n=== D1a: --preset social expands to its domains ===" -ForegroundColor Cyan
  ArmQuiet "--preset social --for $FOR"   # pipe-free: output unused (see PIPE-WEDGE WARNING)
  Check "D1a social preset armed (service running)" ((SvcState) -eq 'Running')
  Check "D1a preset expanded: reddit.com in hosts"    (HostsHas '127\.0\.0\.1\s+reddit\.com')
  Check "D1a preset expanded: facebook.com in hosts"  (HostsHas '127\.0\.0\.1\s+facebook\.com')
  ForceDown

  # --- D2a: --app-preset expands to its apps ----------------------------------
  Write-Host "`n=== D2a: --app-preset games expands to its apps ===" -ForegroundColor Cyan
  ArmQuiet "--app-preset games --for $FOR"   # pipe-free: output unused (see PIPE-WEDGE WARNING)
  Check "D2a games app-preset armed (service running)" ((SvcState) -eq 'Running')
  $stApps = (& $monk status 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Check "D2a app-preset expanded: steam.exe in status/config" ($stApps -match 'steam\.exe' -or (IniHas 'steam\.exe'))
  ForceDown

  # --- D1b/D2b: account defaults inherit when a block names no source ----------
  Write-Host "`n=== D1b/D2b: default site + app lists inherit (both dimensions) ===" -ForegroundColor Cyan
  & $monk setup --partner 'Smoke Tester' --default-sites inherit-test.example --default-apps mmsmoke_dfl.exe 2>&1 | Out-Null
  ArmQuiet "--for $FOR"    # NO --sites/--apps: must inherit both defaults; pipe-free (output unused)
  Check "D1b default SITE inherited (inherit-test.example in hosts)" (HostsHas '127\.0\.0\.1\s+inherit-test\.example')
  $stDfl = (& $monk status 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Check "D2b default APP inherited (mmsmoke_dfl.exe in status/config)" ($stDfl -match 'mmsmoke_dfl\.exe' -or (IniHas 'mmsmoke_dfl\.exe'))
  ForceDown
  & $monk setup --partner 'Smoke Tester (smoke@test.local)' 2>&1 | Out-Null   # reset: clear the test defaults

  # --- D3: stats renders recorded history -------------------------------------
  Write-Host "`n=== D3: monkmode stats ===" -ForegroundColor Cyan
  $stats = (& $monk stats 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Write-Host $stats
  Check "D3 stats renders a block history (Blocks started: N)" ($stats -match 'Blocks started:\s+[1-9]')

  # --- #2 + D5: schedule arm -> status window state -> clear tears down --------
  Write-Host "`n=== #2/D5: schedule arm, status window state, --clear teardown ===" -ForegroundColor Cyan
  & $monk schedule --sites example.com --windows "Mon-Fri 09:00-17:00" 2>&1 | Out-Null
  [void](WaitSvc 'Running' 60)
  Check "schedule armed (service running)" ((SvcState) -eq 'Running')
  $stSch = (& $monk status 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Check "D5 status shows the armed schedule window state" ($stSch -match '(?i)window|schedule')
  & $monk schedule --clear 2>&1 | Out-Null
  Check "#2 schedule --clear tore enforcement down (service Stopped-or-gone <=35s; RUNBOOK E9)" (WaitSvcLifted 35)
  Check "#2 schedule --clear left no hosts orphan (marker block gone)" (-not (HostsHas '#### MonkMode Entries ####'))
  ForceDown

  # --- D2c: --all-session-kill arms AllSession=yes; the service kills the app --
  Write-Host "`n=== D2c: --all-session-kill arm + live app kill ===" -ForegroundColor Cyan
  Copy-Item 'C:\Windows\System32\PING.EXE' $testExe -Force
  Start-Process -FilePath $testExe -ArgumentList '-t','127.0.0.1' -WindowStyle Hidden | Out-Null
  Start-Sleep -Seconds 1
  Check "D2c test app (mmsmoke_testapp.exe) running pre-block" ($null -ne (Get-Process mmsmoke_testapp -ErrorAction SilentlyContinue))
  ArmQuiet "--apps mmsmoke_testapp.exe --all-session-kill --for $FOR"   # pipe-free: output unused (see PIPE-WEDGE WARNING)
  Check "D2c block armed (service running)" ((SvcState) -eq 'Running')
  Check "D2c v9 config carries AllSession=yes (MAC-covered)" (IniHas '(?im)^\s*AllSession\s*=\s*yes')
  $killed = $false
  $u = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $u) { if ($null -eq (Get-Process mmsmoke_testapp -ErrorAction SilentlyContinue)) { $killed = $true; break }; Start-Sleep -Milliseconds 500 }
  Check "D2c service KILLED the blocked app within 30s" $killed
  Write-Host "  NOTE: cross-SESSION widening (session 1+) needs a 2nd interactive login to eyeball;" -ForegroundColor Yellow
  Write-Host "        the ProcessInKillScope truth-table is unit-pinned. Flagged for a 2-min FUS check." -ForegroundColor Yellow
  ForceDown
}
finally {
  # BROKEN BY 319: the rescue teardown is gone. An aborted run now leaves whatever
  # block was armed standing until its own --for timer runs out - there is no escape
  # hatch and no cleanup script to fall back on. See the header.
  Write-Host "`n=== teardown (remove test app only - NO block rescue exists) ===" -ForegroundColor Cyan
  Get-Process mmsmoke_testapp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Remove-Item $testExe -Force -ErrorAction SilentlyContinue
}

Write-Host "`n================ CV/D RESULT: $pass passed, $fail failed ================" -ForegroundColor $(if($fail -eq 0){'Green'}else{'Red'})
"CVD_RESULT pass=$pass fail=$fail"
