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

# MonkMode B7 fail-closed test (RUN ELEVATED) - standalone, ~1 minute.
#
# Proves the B7 fail-open FIX (2026-06-13): the service must NOT re-stamp the
# tamper-evident MAC over a config whose MAC is currently INVALID. The bug was
# that the active-block heartbeat re-stamped [Integrity] Mac unconditionally, so
# a tampered config was re-blessed with a fresh valid MAC within one tick and the
# block lifted next tick - defeating B7 with no HMAC forge.
#
# WHY THIS TEST CORRUPTS THE MAC, NOT [Time] Until:
#   The faithful attack edits [Time] Until (re-encrypted with the known 3DES key)
#   to a past time. We deliberately DON'T do that here: a malformed encrypted
#   Until hits the service's DecryptData, which calls End() and CRASHES the
#   service (a documented landmine). [Integrity] Mac is NEVER decrypted - it is
#   only HMAC-compared (ConfigMacIsValid returns False safely on any bad value) -
#   so corrupting IT is safe AND is a clean discriminator:
#     - FIXED service: macValid=False each tick => HOLD => the MAC we wrote stays
#       byte-for-byte untouched, and the block keeps enforcing (fail-closed).
#     - BUGGY service: re-stamps a fresh VALID MAC within ~1 tick => the value
#       changes. (Under the bug, if Until were also past, it would then lift.)
#   So "the MAC value is unchanged after 2 ticks" == the fix is live.
#
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
#
# (It used to exit via 'monkmode unblock --force', which also exercised the B6
# escape hatch, falling back to cleanup.ps1 if anything was left behind. B6 is no
# longer a feature: the CLI has no teardown at all.)
#
# Usage (ELEVATED):
#   powershell -ExecutionPolicy Bypass -File C:\Users\samra\monkmode-smoketest\b7-failclosed-test.ps1

param([string]$Dist)

$ErrorActionPreference = 'Continue'

# $Dist = the built dist\ folder. Default: derive from this script's location
# (repo\tools\smoke\ -> repo\dist); fall back to the absolute path if unknown.
if (-not $Dist) {
  if ($PSScriptRoot) { $Dist = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'dist' }
  else               { $Dist = 'C:\Users\samra\repos\monk-mode\dist' }
}
$dist   = $Dist
$monk   = Join-Path $dist 'monkmode.exe'
$ini    = Join-Path $dist 'monkmode_settings.ini'
$setupIni = Join-Path $dist 'monkmode_setup.ini'   # absent on a fresh dist -> arms refuse exit 4
$hosts  = "$env:SystemRoot\System32\drivers\etc\hosts"
$backup = 'C:\Users\samra\monkmode-smoketest\hosts.backup.txt'
# BROKEN BY 319: cleanup.ps1 was deleted with the escape hatch it wrapped.
$pass = 0; $fail = 0
function Check($name, $cond) {
  if ($cond) { Write-Host "  [PASS] $name" -ForegroundColor Green; $script:pass++ }
  else       { Write-Host "  [FAIL] $name" -ForegroundColor Red;   $script:fail++ }
}
function Resolve-Example { try { [System.Net.Dns]::GetHostAddresses('example.com') | ForEach-Object { $_.ToString() } } catch { @() } }

# Read the [Integrity] Mac value from the ini (raw line scan; the value is Base64,
# the field name is plaintext). $null if absent.
function Get-IniMac {
  $t = try { Get-Content $ini -Raw } catch { '' }
  if ($t -match '(?m)^\s*Mac\s*=\s*(\S+)\s*$') { return $Matches[1] } else { return $null }
}
# Write a replacement [Integrity] Mac value back into the ini.
function Set-IniMac($value) {
  $t = Get-Content $ini -Raw
  $t = [regex]::Replace($t, '(?m)^(\s*Mac\s*=\s*)\S+\s*$', ('${1}' + $value))
  [IO.File]::WriteAllText($ini, $t)
}

# 0. preconditions
$me = [System.Security.Principal.WindowsPrincipal]([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Host "This script must be run ELEVATED (as Administrator). Aborting." -ForegroundColor Red; exit 1
}
if (-not (Test-Path $monk)) { Write-Host "monkmode.exe not found at $monk. Build dist first (tools\build-dist.ps1)." -ForegroundColor Red; exit 1 }
# build-dist.ps1 wipes monkmode_setup.ini, so a fresh dist arms fail-closed (exit
# 4). Self-setup once (defaults suffice for this test) so the arm below isn't
# silently refused. Idempotent: skipped when the file already exists.
if (-not (Test-Path $setupIni)) {
  Write-Host "No monkmode_setup.ini in dist -> running one-time 'monkmode setup' (fresh-dist precondition)." -ForegroundColor Yellow
  & $monk setup | Out-Null
  if (-not (Test-Path $setupIni)) { Write-Host "'monkmode setup' did not produce $setupIni (exit $LASTEXITCODE). Aborting." -ForegroundColor Red; exit 1 }
}
if (Get-Service MONKMODE -ErrorAction SilentlyContinue) { Write-Host "MONKMODE already exists. Let any block end, then 'sc.exe delete MONKMODE' while idle." -ForegroundColor Yellow; exit 1 }
if (-not (Test-Path $backup)) { Copy-Item $hosts $backup -Force }

try {
  # 1. Arm a block long enough that it cannot auto-expire during the test (we
  #    exit by code, not by waiting - see the BROKEN BY 319 header).
  Write-Host "`n=== 1. Arming a 15-minute block on example.com ===" -ForegroundColor Cyan
  & $monk block --sites example.com --for 15
  Start-Sleep -Seconds 3

  Write-Host "`n=== 2. Verifying the block is live + B7 wiring present ===" -ForegroundColor Cyan
  $svc = Get-Service MONKMODE -ErrorAction SilentlyContinue
  Check "MONKMODE service running" ($svc -and $svc.Status -eq 'Running')
  $addrs = Resolve-Example
  Check "example.com resolves to 127.0.0.1" ($addrs -contains '127.0.0.1')
  $macOrig = Get-IniMac
  Write-Host "    [Integrity] Mac (original) = $macOrig"
  Check "B7 [Integrity] Mac present" ($null -ne $macOrig -and $macOrig.Length -gt 0)

  # 3. Corrupt the MAC (flip the first Base64 char to a different valid one - stays
  #    valid Base64, wrong bytes => ConfigMacIsValid=False, no DecryptData/End risk).
  Write-Host "`n=== 3. Corrupting [Integrity] Mac (tamper -> macValid=False) ===" -ForegroundColor Cyan
  $first = $macOrig.Substring(0,1)
  $flip = 'A'
  if ($first -ceq 'A') { $flip = 'B' }
  $macCorrupt = $flip + $macOrig.Substring(1)
  Set-IniMac $macCorrupt
  Start-Sleep -Milliseconds 300
  $macNow = Get-IniMac
  Check "MAC corruption written to ini" ($macNow -eq $macCorrupt -and $macNow -ne $macOrig)

  # 4. Wait ~2 service ticks. The FIX => the service HOLDs (no re-stamp): the MAC
  #    we wrote is still there. The BUG => the service re-stamped a fresh valid MAC
  #    (value changed).
  Write-Host "`n=== 4. Waiting ~2 ticks (25s) to see if the service re-stamps ===" -ForegroundColor Cyan
  Start-Sleep -Seconds 25
  $macAfter = Get-IniMac
  Write-Host "    [Integrity] Mac (after 25s)  = $macAfter"
  Check "FIX: service did NOT re-stamp over the invalid MAC (Hold)" ($macAfter -eq $macCorrupt)
  Check "FIX: MAC was NOT healed back to a valid value"            ($macAfter -ne $macOrig)

  # 5. Fail-closed enforcement: despite the invalid MAC, the block must still hold
  #    (the service keeps enforcing; it just won't auto-lift until re-armed).
  ipconfig /flushdns | Out-Null; Start-Sleep -Milliseconds 500
  $addrs2 = Resolve-Example
  $hostsText = Get-Content $hosts -Raw
  Check "block still enforced under invalid MAC (fail-closed)" (($hostsText -match '#### MonkMode Entries ####') -and ($addrs2 -contains '127.0.0.1'))

  # 4b. The 'add' verb must NOT re-bless a tampered config. With macValid=False,
  #     'monkmode add' (which only needs BlockIsActive, not a valid MAC) must NOT
  #     re-stamp [Integrity] Mac. FIXED => the MAC stays exactly the corrupted
  #     value we wrote. BUGGY => add would mint a fresh valid MAC (value changes)
  #     and the block would lift next tick.
  Write-Host "`n=== 4b. 'monkmode add' must NOT re-stamp a tampered config ===" -ForegroundColor Cyan
  & $monk add --sites added-by-b7-test.com
  Start-Sleep -Seconds 2
  $macAfterAdd = Get-IniMac
  Write-Host "    [Integrity] Mac (after add) = $macAfterAdd"
  Check "FIX: 'add' did NOT re-stamp over the invalid MAC" ($macAfterAdd -eq $macCorrupt)
}
finally {
  # 6. Exit via the B6 escape hatch (the documented clean way out of a frozen
  #    block), then verify.
  # BROKEN BY 319: `unblock --force` no longer exists, and neither does cleanup.ps1.
  # This step must become a partner-code lift + `sc.exe delete MONKMODE` (header).
  Write-Host "`n=== 5. Tearing down ===" -ForegroundColor Cyan
  throw 'BROKEN BY 319: this teardown step needs rewriting as a partner-code lift (see the header).'
  Start-Sleep -Seconds 2
  $gone = -not (Get-Service MONKMODE -ErrorAction SilentlyContinue)
  $hostsClean = ((Get-Content $hosts -Raw) -notmatch '#### MonkMode Entries ####')
  Check "the teardown removed the service" $gone
  Check "the teardown restored hosts"      $hostsClean
  if (-not $gone -or -not $hostsClean) {
    Write-Host "  the teardown did not fully clean up - and ledger 319 removed the rescue." -ForegroundColor Yellow
    Write-Host "  Wait for the block's timer, or lift it with its partner code." -ForegroundColor Yellow
  }
}

Write-Host "`n================ B7 RESULT: $pass passed, $fail failed ================" -ForegroundColor $(if($fail -eq 0){'Green'}else{'Red'})
"B7_FAILCLOSED_RESULT pass=$pass fail=$fail"
