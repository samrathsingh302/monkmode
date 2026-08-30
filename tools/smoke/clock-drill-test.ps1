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

# MonkMode clock drills (RUN ELEVATED) - B4 forward-jump + B1c backward-roll.
#
# Proves the two clock-attack defences live, against the real service:
#   B4  (forward jump past Until must NOT lift): HighWater is a monotonic mark
#       that only advances on REAL elapsed (<=120s/tick) and never on a wall
#       jump, so pushing the clock past the block's Until keeps it enforced.
#   B1c (backward roll must NOT over-extend): AdvanceHighWater credits
#       min(real-monotonic-elapsed, 120s) even on a backward roll, so the block
#       still ends at its REAL duration - a backward clock cannot freeze it open.
#
# SAFETY - guaranteed restore, no gaps:  every manipulation is
#   anchor(wall) + Stopwatch(monotonic)  ->  Set-Date jump  ->  assert  ->
#   FINALLY Set-Date (anchor + real elapsed)  ->  verify restored.
# The Stopwatch is immune to the wall-clock jump, so the restore is always the
# TRUE current time. NO 'w32tm /resync' is used (it can block/hang; run-smoketest
# 2f died there). w32time still keeps truth as a backstop. This script NEVER
# leaves the clock jumped: the finally runs even on assertion failure or Ctrl-C.
#
# Run this FOREGROUND, UNBUFFERED and WATCHED (each drill < 2 min). The try/finally
# restores on every code path - but only an in-process exit runs the finally. An
# EXTERNAL hard-kill (a bash/CI timeout wrapping the run, a taskkill) mid-drill
# skips the finally and leaves the clock jumped. So NEVER run this piped under an
# external timeout or detached; run it directly in a watched console and confirm
# the post-drill NTP line yourself. (Learned the hard way 2026-07-09.)
#
# Usage (ELEVATED):  powershell -ExecutionPolicy Bypass -File tools\smoke\clock-drill-test.ps1
param([string]$Dist, [switch]$SkipBackward)

$ErrorActionPreference = 'Continue'
if (-not $Dist) {
  if ($PSScriptRoot) { $Dist = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'dist' }
  else               { $Dist = 'C:\Users\samra\repos\monk-mode\dist' }
}
$monk  = Join-Path $Dist 'monkmode.exe'
$hosts = "$env:SystemRoot\System32\drivers\etc\hosts"
# BROKEN BY 319: cleanup.ps1 was deleted with the escape hatch it wrapped.
$pass = 0; $fail = 0
function Check($n,$c){ if($c){Write-Host "  [PASS] $n" -ForegroundColor Green;$script:pass++}else{Write-Host "  [FAIL] $n" -ForegroundColor Red;$script:fail++} }
function SvcState { $s=Get-Service MONKMODE -ErrorAction SilentlyContinue; if($s){$s.Status}else{'gone'} }
function HostsBlocked { $(try{Get-Content $hosts -Raw}catch{''}) -match '#### MonkMode Entries ####' }
# ############################################################################
# # BROKEN BY 319:  THIS DRILL'S TEARDOWN NO LONGER EXISTS. DO NOT RUN IT AS-IS.
# #
# # Ledger 319 (30/08/2026) removed `monkmode unblock --force` and deleted
# # tools\smoke\cleanup.ps1. A running block now ends on exactly two events - its
# # own end time, or a service-verified partner code - so there is no forced
# # teardown to fall back on and no rescue script behind it.
# #
# # WHAT IT NEEDS (a live, elevated sitting; it cannot be verified from a bench):
# #   * capture each arm's output and parse the one-time code off the line after
# #     "Emergency unlock code" (cv-d-smoke.ps1's ParseCode is the pattern), then
# #     tear down with `unblock --code <CODE>`, wait for Stopped-or-gone
# #     (RUNBOOK E9 - never poll for 'gone' after a lift), and `sc.exe delete
# #     MONKMODE` to reach 'gone' for the next section's precondition;
# #   * or, where a code cannot be threaded through, use a --for short enough that
# #     natural expiry IS the teardown and say so in the header. `--for 1` is
# #     always refused, so one minute is the floor.
# #   * an aborted run now leaves the armed block standing until its timer runs
# #     out. That is the design, not a bug - say it in the header rather than
# #     implying a rescue exists.
# ############################################################################
function ForceDown { throw 'BROKEN BY 319: ForceDown needs rewriting as a partner-code lift (see the header).' }
# True clock offset (seconds) vs an external HTTP Date header, WITHOUT setting
# the clock. $null if unreachable. NB: uses an 8s-bounded HTTP HEAD, NOT
# 'w32tm /stripchart' or '/resync' - those can BLOCK indefinitely on a slow NTP
# source and hung this script (2026-07-09), so it ran invisibly into a drill and
# an external timeout killed it mid-jump, leaving the clock +30m. Never block here.
function NtpOffset {
  try {
    $r = Invoke-WebRequest -Uri 'https://www.cloudflare.com' -Method Head -UseBasicParsing -TimeoutSec 8
    $u = [DateTime]::Parse($r.Headers['Date'], [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
    return ((Get-Date).ToUniversalTime() - $u).TotalSeconds
  } catch { return $null }
}

# --- preconditions -----------------------------------------------------------
$me=[Security.Principal.WindowsPrincipal]([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){Write-Host 'Run ELEVATED.' -ForegroundColor Red;exit 1}
if(-not (Test-Path $monk)){Write-Host "monkmode.exe not at $monk." -ForegroundColor Red;exit 1}
if(Get-Service MONKMODE -ErrorAction SilentlyContinue){Write-Host 'MONKMODE exists - let any block end, then sc.exe delete MONKMODE while idle.' -ForegroundColor Yellow;exit 1}
& $monk setup --partner 'Smoke Tester (smoke@test.local)' 2>&1|Out-Null
$off0 = NtpOffset
Write-Host ("Pre-drill NTP offset: {0}s" -f $off0)
Check "clock within 5s of NTP before drills" ($null -ne $off0 -and [math]::Abs($off0) -lt 5)

# --- FEASIBILITY probe: will w32time let a manual Set-Date persist ~8s? -------
# If w32time immediately yanks a manual jump back, the service can't observe it
# and the drill is moot (and safe). We probe with a tiny +90s jump.
Write-Host "`n=== feasibility probe (does a manual clock jump persist a tick?) ===" -ForegroundColor Cyan
$anchor=Get-Date; $sw=[Diagnostics.Stopwatch]::StartNew(); $held=$false
try { Set-Date -Date $anchor.AddSeconds(90) -ErrorAction Stop|Out-Null; Start-Sleep -Seconds 8; $held=((Get-Date)-$anchor).TotalSeconds -gt 60 }
finally { $sw.Stop(); Set-Date -Date $anchor.AddSeconds([math]::Round($sw.Elapsed.TotalSeconds)) -ErrorAction SilentlyContinue|Out-Null }
Check "clock restored after probe (within 5s of NTP)" ([math]::Abs(([double](NtpOffset))) -lt 5)
if (-not $held) {
  Write-Host "w32time is yanking manual jumps back within 8s - the drills cannot be observed by the service." -ForegroundColor Yellow
  Write-Host "DEFERRING the live clock drills (moot under active time-sync). B4/B1c remain unit-pinned." -ForegroundColor Yellow
  Write-Host "`n================ CLOCK RESULT: $pass passed, $fail failed (drills DEFERRED) ================"
  "CLOCK_RESULT pass=$pass fail=$fail deferred=1"
  return
}
Write-Host "  probe held - proceeding with the drills." -ForegroundColor Green

try {
  # --- B4: forward jump past Until must NOT lift ------------------------------
  Write-Host "`n=== B4: forward clock jump past Until must NOT lift ===" -ForegroundColor Cyan
  # NEVER pipe/suppress an arm: the CLI's RegisterAndLaunchNotifier spawns mm_notify.exe,
  # which INHERITS stdout - a PS pipeline (|Out-Null, capture) then blocks until the
  # notifier exits at block EXPIRY, so every check runs post-expiry (2026-07-10 void drills).
  & $monk block --sites example.com --for 3; Start-Sleep -Seconds 3
  Check "B4 block armed" ((SvcState) -eq 'Running')
  Start-Sleep -Seconds 12   # let HighWater seed on real ticks
  $anchor=Get-Date; $sw=[Diagnostics.Stopwatch]::StartNew(); $survived=$false
  try {
    Set-Date -Date $anchor.AddMinutes(30) -ErrorAction Stop|Out-Null   # jump +30m, well past the +3m Until
    Start-Sleep -Seconds 14   # >=1 tick observes (and must refuse) the jump
    $survived = ((SvcState) -eq 'Running') -and (HostsBlocked)
  } finally {
    $sw.Stop(); Set-Date -Date $anchor.AddSeconds([math]::Round($sw.Elapsed.TotalSeconds)) -ErrorAction SilentlyContinue|Out-Null
  }
  Check "B4 block SURVIVED a +30m forward jump past Until" $survived
  Check "B4 clock restored after drill (within 5s of NTP)" ([math]::Abs(([double](NtpOffset))) -lt 5)
  ForceDown

  # --- B1c: backward roll must NOT over-extend -------------------------------
  if (-not $SkipBackward) {
    Write-Host "`n=== B1c: backward clock roll must NOT over-extend the block ===" -ForegroundColor Cyan
    # --for 2, not 1: the CLI enforces a strict >60s-in-the-future floor (Program.vb
    # 'must end at least a minute in the future'), so a 1-minute block always refuses.
    & $monk block --sites example.com --for 2; Start-Sleep -Seconds 3   # unpiped - see B4 note
    Check "B1c block armed (--for 2)" ((SvcState) -eq 'Running')
    $anchor=Get-Date; $sw=[Diagnostics.Stopwatch]::StartNew(); $liftedAt=-1
    try {
      Start-Sleep -Seconds 8                                   # a little real elapsed first
      Set-Date -Date $anchor.AddMinutes(-30) -ErrorAction Stop|Out-Null   # roll BACK 30m mid-block
      # wait on REAL monotonic time (immune to the wall roll) up to ~160s; the
      # block's real duration is 120s, so a correct fix lifts by ~120-140s real.
      # Lift signal: on GENUINE expiry the service goes Stopped but stays INSTALLED
      # (it is only deleted by the service's own genuine-expiry teardown - observed live 2026-07-10),
      # and the hosts marker block is removed. 'gone' would never fire here.
      while ($sw.Elapsed.TotalSeconds -lt 160) {
        if ((SvcState) -ne 'Running' -or -not (HostsBlocked)) { $liftedAt = [int]$sw.Elapsed.TotalSeconds; break }
        Start-Sleep -Milliseconds 1000
      }
    } finally {
      $sw.Stop(); Set-Date -Date $anchor.AddSeconds([math]::Round($sw.Elapsed.TotalSeconds)) -ErrorAction SilentlyContinue|Out-Null
    }
    Write-Host ("    block lifted at ~{0}s real (real duration 120s; over-extend bug would exceed 150s)" -f $liftedAt)
    Check "B1c block lifted at its REAL ~120s duration despite the -30m roll (not frozen open)" ($liftedAt -ge 115 -and $liftedAt -le 150)
    Check "B1c clock restored after drill (within 5s of NTP)" ([math]::Abs(([double](NtpOffset))) -lt 5)
    ForceDown
  }
}
finally {
  ForceDown
  $offZ = NtpOffset
  Write-Host ("Post-drill NTP offset: {0}s" -f $offZ)
  Check "clock within 5s of NTP after ALL drills" ($null -ne $offZ -and [math]::Abs($offZ) -lt 5)
}

Write-Host "`n================ CLOCK RESULT: $pass passed, $fail failed ================" -ForegroundColor $(if($fail -eq 0){'Green'}else{'Red'})
"CLOCK_RESULT pass=$pass fail=$fail"
