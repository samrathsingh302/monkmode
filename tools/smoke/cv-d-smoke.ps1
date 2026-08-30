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
# TEARDOWN AFTER F79 (ledger 320). `unblock --force` and cleanup.ps1 are gone
# (ledger 319), so this smoke tears each section down the only way that still
# exists: the one-time partner code its own arm printed. Every arm therefore
# CAPTURES its stdout (MMArm), and every section ends with
# MMTearDown = `unblock --code <CODE>` -> Stopped-or-gone (RUNBOOK E9: a lift
# leaves the service registered but stopped, never 'gone') -> `sc.exe delete
# MONKMODE` for the next section's precondition. All of it lives in _lib.ps1.
#
# THERE IS NO RESCUE IF THIS ABORTS. The outer finally submits the codes this
# run minted (MMEmergencyLift), and if none of them lifts it, the armed block
# stands until its own --for timer runs out. That is the design. Keep $FOR
# short. CV3 (the cooling-off flow) was deleted outright by ledger 319 rather
# than reworked: there is no cooling-off exit left to smoke.
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
$testExe = Join-Path $env:TEMP 'mmsmoke_testapp.exe'
$FOR     = 5   # minutes: safety floor with headroom so a block never auto-expires mid-section
               # (--for 2 raced the multi-step assertions). It is also the worst case if this
               # run aborts: an abandoned block stands for at most $FOR minutes (ledger 320).
$pass = 0; $fail = 0
function Check($name, $cond) {
  if ($cond) { Write-Host "  [PASS] $name" -ForegroundColor Green; $script:pass++ }
  else       { Write-Host "  [FAIL] $name" -ForegroundColor Red;   $script:fail++ }
}
# Ledger 320: SvcState/WaitSvc/WaitSvcLifted/Arm/ParseCode and the old ForceDown all
# moved into _lib.ps1, so the five drills share ONE teardown instead of five copies of
# a four-step sequence that is easy to get silently wrong. The wrappers below keep this
# script's own vocabulary; MMArm replaces BOTH Arm and ArmQuiet, because every arm now
# has to capture its stdout - the code it prints is the only teardown that exists.
$mmLib = if ($PSScriptRoot) { $PSScriptRoot } else { 'C:\Users\samra\repos\monk-mode\tools\smoke' }
. (Join-Path $mmLib '_lib.ps1')
MMInit -Monk $monk -Hosts $hosts
function SvcState { MMSvcState }
function WaitSvc([string]$want, [int]$sec) { MMWaitSvc $want $sec }
function Arm([string]$argline) { MMArm $argline }
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
  $armA  = Arm "--sites example.com --commit --for $FOR"
  $codeA = $armA.Code
  Check "committed block armed (service running)" ((SvcState) -eq 'Running')
  Check "CV1 one-time accountability code minted (XXXXX-XXXXX)" ($codeA -match '^[0-9A-Z]{5}-[0-9A-Z]{5}$')
  Check "CV1 the code names its block ('for block N')" ($armA.Id -ge 1)
  $st = & $monk status 2>&1 | ForEach-Object { "$_" }
  # LEDGER 319: the matched substring was 'committed block'. Every block is committed
  # now, so `status` prints one exit sentence for all of them and the phrase is gone.
  Check "D5 status shows the code-only exit line" (($st -join "`n") -match '--code <CODE>')
  Check "D5 status offers no other way out" (($st -join "`n") -match 'There is no other way out')
  # bare unblock must be REFUSED (exit 1)
  & $monk unblock 2>&1 | Out-Null
  Check "CV2 bare 'unblock' refused (exit 1)" ($LASTEXITCODE -eq 1)
  Check "CV2 block still enforced after refused unblock" ((SvcState) -eq 'Running' -and (HostsHas 'example\.com'))
  # 320 (a)+(b): the two RETIRED shapes, asserted against a live block - `--force` is now
  # merely an unknown option, and `--id N` without a code is the same refusal as a bare one.
  MMCheckRefusedExits $armA.Id
  # (d) the right code lifts it, within ~30s, and takes the hosts marker with it.
  MMTearDown -Code $codeA -Id $armA.Id -Label 'CV1 committed block'

  # --- CV1 rotate + D5 --------------------------------------------------------
  # LEDGER 319: CV3 (the cooling-off trigger flow - bare `unblock` registering a
  # service-computed ~1h pending, `--cancel` clearing it) was DELETED from here rather
  # than reworked: there is no cooling-off exit to smoke, and bare `unblock` now refuses
  # on every block rather than only on a committed one. What survives is CV1's rotate
  # half plus the wrong-code check, and the bare-unblock refusal now covers ALL blocks.
  Write-Host "`n=== CV1(rotate)/D5: fresh code per block; a wrong code never lifts ===" -ForegroundColor Cyan
  $armB  = Arm "--sites example.com --for $FOR"
  $codeB = $armB.Code
  Check "CV1 rotate: 2nd block minted a DIFFERENT code" ($codeB -and $codeB -ne $codeA)
  # (c) a wrong code must NOT lift. 320: the wrong code is now the REAL code with its
  # first character flipped - shape-valid, so the refusal proves PartnerCodeMatches said
  # no, not that a malformed string was rejected before the KDF ever ran.
  MMCheckWrongCode $codeB
  # bare unblock is refused on EVERY block now (ledger 319), not just a committed one
  & $monk unblock 2>&1 | Out-Null
  Check "319 bare 'unblock' refused on an ordinary block too (exit 1)" ($LASTEXITCODE -eq 1)
  Check "319 block still enforced after the refusal" ((SvcState) -eq 'Running' -and (HostsHas 'example\.com'))
  # CV1's other half: block A's code is dead, and cannot open block B either.
  & $monk unblock --code $codeA 2>&1 | Out-Null
  Start-Sleep -Seconds 16
  Check "CV1 rotate: block A's spent code does NOT open block B" ((SvcState) -eq 'Running' -and (HostsHas 'example\.com'))
  MMTearDown -Code $codeB -Id $armB.Id -Label 'CV1 rotate block'

  # --- D1a: --preset expands to its domains -----------------------------------
  Write-Host "`n=== D1a: --preset social expands to its domains ===" -ForegroundColor Cyan
  # 320: this was ArmQuiet (output deliberately thrown away). It cannot be any more -
  # the code on that stdout is the only thing that can tear the section down.
  $armD1a = Arm "--preset social --for $FOR"
  Check "D1a social preset armed (service running)" ((SvcState) -eq 'Running')
  Check "D1a preset expanded: reddit.com in hosts"    (HostsHas '127\.0\.0\.1\s+reddit\.com')
  Check "D1a preset expanded: facebook.com in hosts"  (HostsHas '127\.0\.0\.1\s+facebook\.com')
  MMTearDown -Code $armD1a.Code -Id $armD1a.Id -Label 'D1a'

  # --- D2a: --app-preset expands to its apps ----------------------------------
  Write-Host "`n=== D2a: --app-preset games expands to its apps ===" -ForegroundColor Cyan
  $armD2a = Arm "--app-preset games --for $FOR"
  Check "D2a games app-preset armed (service running)" ((SvcState) -eq 'Running')
  $stApps = (& $monk status 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Check "D2a app-preset expanded: steam.exe in status/config" ($stApps -match 'steam\.exe' -or (IniHas 'steam\.exe'))
  MMTearDown -Code $armD2a.Code -Id $armD2a.Id -Label 'D2a'

  # --- D1b/D2b: account defaults inherit when a block names no source ----------
  Write-Host "`n=== D1b/D2b: default site + app lists inherit (both dimensions) ===" -ForegroundColor Cyan
  & $monk setup --partner 'Smoke Tester' --default-sites inherit-test.example --default-apps mmsmoke_dfl.exe 2>&1 | Out-Null
  $armDfl = Arm "--for $FOR"    # NO --sites/--apps: must inherit both defaults
  Check "D1b default SITE inherited (inherit-test.example in hosts)" (HostsHas '127\.0\.0\.1\s+inherit-test\.example')
  $stDfl = (& $monk status 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Check "D2b default APP inherited (mmsmoke_dfl.exe in status/config)" ($stDfl -match 'mmsmoke_dfl\.exe' -or (IniHas 'mmsmoke_dfl\.exe'))
  MMTearDown -Code $armDfl.Code -Id $armDfl.Id -Label 'D1b/D2b'
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
  # No code teardown here and none needed: `schedule --clear` is a legitimate CLI verb that
  # blanks the Spec (it is not an early exit from a running block - a currently-OPEN window
  # still runs to its monotonic close, C5a section 7), and this schedule's window is shut.
  Check "#2 schedule --clear tore enforcement down (service Stopped-or-gone <=35s; RUNBOOK E9)" (MMWaitSvcLifted 35)
  Check "#2 schedule --clear left no hosts orphan (marker block gone)" (-not (HostsHas '#### MonkMode Entries ####'))
  [void](MMResetInstall)

  # --- D2c: --all-session-kill arms AllSession=yes; the service kills the app --
  Write-Host "`n=== D2c: --all-session-kill arm + live app kill ===" -ForegroundColor Cyan
  Copy-Item 'C:\Windows\System32\PING.EXE' $testExe -Force
  Start-Process -FilePath $testExe -ArgumentList '-t','127.0.0.1' -WindowStyle Hidden | Out-Null
  Start-Sleep -Seconds 1
  Check "D2c test app (mmsmoke_testapp.exe) running pre-block" ($null -ne (Get-Process mmsmoke_testapp -ErrorAction SilentlyContinue))
  $armD2c = Arm "--apps mmsmoke_testapp.exe --all-session-kill --for $FOR"
  Check "D2c block armed (service running)" ((SvcState) -eq 'Running')
  Check "D2c v9 config carries AllSession=yes (MAC-covered)" (IniHas '(?im)^\s*AllSession\s*=\s*yes')
  $killed = $false
  $u = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $u) { if ($null -eq (Get-Process mmsmoke_testapp -ErrorAction SilentlyContinue)) { $killed = $true; break }; Start-Sleep -Milliseconds 500 }
  Check "D2c service KILLED the blocked app within 30s" $killed
  Write-Host "  NOTE: cross-SESSION widening (session 1+) needs a 2nd interactive login to eyeball;" -ForegroundColor Yellow
  Write-Host "        the ProcessInKillScope truth-table is unit-pinned. Flagged for a 2-min FUS check." -ForegroundColor Yellow
  MMTearDown -Code $armD2c.Code -Id $armD2c.Id -Label 'D2c'
}
finally {
  # 320: the backstop is the codes THIS RUN minted, nothing more. If an abort happened
  # between an arm and its teardown, MMEmergencyLift submits them (each verifies against
  # its own slot) and resets. If none lifts, the block stands until its own --for timer
  # ends - there is no escape hatch and no cleanup script, by design.
  Write-Host "`n=== teardown (this run's minted codes, then the test app) ===" -ForegroundColor Cyan
  MMEmergencyLift
  Get-Process mmsmoke_testapp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Remove-Item $testExe -Force -ErrorAction SilentlyContinue
}

Write-Host "`n================ CV/D RESULT: $pass passed, $fail failed ================" -ForegroundColor $(if($fail -eq 0){'Green'}else{'Red'})
"CVD_RESULT pass=$pass fail=$fail"
