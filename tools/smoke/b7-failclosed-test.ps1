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

# MonkMode B7 fail-closed test (RUN ELEVATED) - standalone, ~3 minutes.
# (~1 min before ledger 320; the restore + resume + code-lift teardown adds ~2.)
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
# # TEARDOWN AFTER F79 (ledger 320) - READ THIS BEFORE RUNNING.
# #
# # This drill deliberately drives the config into the FROZEN state, and ledger
# # 319 made that state absolute: with an invalid MAC, NOTHING lifts the block.
# # Not the timer (EffectiveBlockHasExpired holds), and not the partner code
# # either (ClassifyPartnerCodeSignal requires a valid MAC). The old exit was
# # `unblock --force`, which no longer exists. So the order below is not
# # optional and not a convenience - it is the only way out of this drill:
# #
# #   corrupt the MAC -> assert fail-closed -> RESTORE THE INI BYTES SAVED
# #   BEFORE CORRUPTING -> assert the service resumes -> lift with the code.
# #
# # The restore is a WHOLE-FILE byte restore, never a "put the old Mac value
# # back". [Time] HighWater and Now are MAC-COVERED (ConfigIntegrity.Build-
# # Canonical:261-273) and the heartbeat rewrites them every tick, so the MAC
# # string read at step 2 goes stale the instant a tick lands: writing it back
# # over newer content yields a config that is STILL invalid, and the machine
# # would be frozen with no exit at all. Restoring the file whole keeps content
# # and MAC mutually consistent by construction. The restored HighWater is a
# # few seconds behind, which the service re-credits at the honest <=10s/tick
# # rate (AdvanceHighWater) - i.e. the block ends slightly LATE. Fail-closed,
# # which is the only direction this repo accepts.
# #
# # If this run aborts between the corruption and the restore, the machine is
# # frozen until B10 (boot elsewhere and edit dist\monkmode_settings.ini back
# # from $iniBackup, written next to the hosts backup). That is why the arm is
# # 15 minutes and why you run this WATCHED. There is no rescue script.
# ############################################################################
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
# 320: the pre-corruption snapshot of the whole enforcement ini. Written beside the
# hosts backup so it survives this process - it is the ONLY way back out of the frozen
# state this drill creates (see the header).
$iniBackup = 'C:\Users\samra\monkmode-smoketest\monkmode_settings.pre-b7.ini'
$pass = 0; $fail = 0
function Check($name, $cond) {
  if ($cond) { Write-Host "  [PASS] $name" -ForegroundColor Green; $script:pass++ }
  else       { Write-Host "  [FAIL] $name" -ForegroundColor Red;   $script:fail++ }
}
$mmLib = if ($PSScriptRoot) { $PSScriptRoot } else { 'C:\Users\samra\repos\monk-mode\tools\smoke' }
. (Join-Path $mmLib '_lib.ps1')
MMInit -Monk $monk -Hosts $hosts
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
# 320: the whole-file snapshot/restore pair that is this drill's only way out of the
# frozen state (header). Byte-for-byte, never field-by-field: content and MAC must stay
# mutually consistent, and the heartbeat rewrites MAC-covered [Time] fields every tick.
# Both retry - the service has the file open on its own 10s cadence and a sharing
# violation here is transient, not fatal.
function Save-IniBytes([string]$to) {
  for ($i = 0; $i -lt 8; $i++) {
    try { [IO.File]::WriteAllBytes($to, [IO.File]::ReadAllBytes($ini)); return $true } catch { Start-Sleep -Milliseconds 400 }
  }
  return $false
}
function Restore-IniBytes([string]$from) {
  if (-not (Test-Path $from)) { return $false }
  for ($i = 0; $i -lt 12; $i++) {
    try { [IO.File]::WriteAllBytes($ini, [IO.File]::ReadAllBytes($from)); return $true } catch { Start-Sleep -Milliseconds 400 }
  }
  return $false
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

$arm = $null; $corrupted = $false; $restored = $false
try {
  # 1. Arm a block long enough that it cannot auto-expire during the test. 320: the arm
  #    is CAPTURED (MMArm) because the one-time code on its stdout is the only teardown
  #    that exists - and this drill must not reach its restore without one in hand.
  Write-Host "`n=== 1. Arming a 15-minute block on example.com ===" -ForegroundColor Cyan
  $arm = MMArm "--sites example.com --for 15"
  Write-Host "    block id $($arm.Id); unlock code captured: $([bool]$arm.Code)"

  Write-Host "`n=== 2. Verifying the block is live + B7 wiring present ===" -ForegroundColor Cyan
  $svc = Get-Service MONKMODE -ErrorAction SilentlyContinue
  Check "MONKMODE service running" ($svc -and $svc.Status -eq 'Running')
  $addrs = Resolve-Example
  Check "example.com resolves to 127.0.0.1" ($addrs -contains '127.0.0.1')
  $macOrig = Get-IniMac
  Write-Host "    [Integrity] Mac (original) = $macOrig"
  Check "B7 [Integrity] Mac present" ($null -ne $macOrig -and $macOrig.Length -gt 0)

  # 2b. 320: the retired exits, asserted here on a HEALTHY block - before the corruption,
  #     because once the MAC is invalid every exit refuses for the wrong reason.
  Write-Host "`n=== 2b. 319/F79 exit surface (on a healthy block) ===" -ForegroundColor Cyan
  MMCheckRefusedExits $arm.Id

  # 2c. THE SNAPSHOT. Everything below is one-way until this file is written back.
  Write-Host "`n=== 2c. Snapshotting the ini BEFORE corrupting it (the only way back) ===" -ForegroundColor Cyan
  Check "pre-corruption ini snapshot written" (Save-IniBytes $iniBackup)
  Write-Host "    -> $iniBackup"

  # 3. Corrupt the MAC (flip the first Base64 char to a different valid one - stays
  #    valid Base64, wrong bytes => ConfigMacIsValid=False, no DecryptData/End risk).
  Write-Host "`n=== 3. Corrupting [Integrity] Mac (tamper -> macValid=False) ===" -ForegroundColor Cyan
  $first = $macOrig.Substring(0,1)
  $flip = 'A'
  if ($first -ceq 'A') { $flip = 'B' }
  $macCorrupt = $flip + $macOrig.Substring(1)
  Set-IniMac $macCorrupt
  $corrupted = $true
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
  # NB deliberately NOT drilled here: submitting the partner code while the config is
  # frozen. It would not lift (ClassifyPartnerCodeSignal requires a valid MAC - that is
  # the documented cost of B7 and `monkmode help` says so), but it leaves a
  # monkmode_partner.code.<id> trigger on disk that the restored service could consume
  # on its very next tick, lifting the block before step 5b could observe the resume.
  # The frozen-config-never-lifts property is unit-pinned; this drill's job is to prove
  # the RESUME, and to leave the machine in a state a code can still open.

  # 5. RESTORE. The one-way door closes here (header). Whole file, byte for byte.
  Write-Host "`n=== 5. Restoring the pre-corruption ini (leaving the frozen state) ===" -ForegroundColor Cyan
  $restored = Restore-IniBytes $iniBackup
  Check "pre-corruption ini restored byte-for-byte" $restored

  # 5b. The service must RESUME normal enforcement: with a valid MAC again, the
  #     heartbeat re-stamps [Integrity] Mac each tick (it covers [Time] Now/HighWater,
  #     which every tick rewrites - ConfigIntegrity.BuildCanonical:261-273). So the MAC
  #     MOVING OFF the restored value within ~2 ticks is the proof that the Hold has
  #     lifted and the writer is live again. The block must still be enforcing while it
  #     does: resuming is not lifting.
  Write-Host "`n=== 5b. The service must resume normal enforcement (~2 ticks) ===" -ForegroundColor Cyan
  $macRestored = Get-IniMac
  $resumed = $false
  $u = (Get-Date).AddSeconds(35)
  while ((Get-Date) -lt $u) {
    Start-Sleep -Seconds 2
    if ((Get-IniMac) -ne $macRestored) { $resumed = $true; break }
  }
  Check "RESUME: the service re-stamped the MAC once the config was valid again" $resumed
  Check "RESUME: the block is still enforcing (a resume is not a lift)" (((MMSvcState) -eq 'Running') -and (MMHostsHasMarker))

  # 6. And only now can the code lift it - which is the point of restoring first: a
  #    frozen config is opened by NOTHING, so a drill that freezes one must unfreeze it.
  Write-Host "`n=== 6. Partner-code lift + teardown ===" -ForegroundColor Cyan
  MMTearDown -Code $arm.Code -Id $arm.Id -Label 'B7'
}
finally {
  # LAST-DITCH RESTORE. If anything above threw between the corruption and step 5, the
  # machine is frozen and NOTHING lifts it - not the timer, not the code. Put the bytes
  # back here before doing anything else; without this the only way out is B10.
  if ($corrupted -and -not $restored) {
    Write-Host "`n  !! aborted while the config was CORRUPT - restoring the ini snapshot now" -ForegroundColor Yellow
    if (Restore-IniBytes $iniBackup) {
      Write-Host "     restored from $iniBackup - the block is enforcing normally again and its code will open it." -ForegroundColor Yellow
      Start-Sleep -Seconds 12   # let one tick re-stamp before anything submits a code
    } else {
      Write-Host "     RESTORE FAILED. The config is frozen: no timer and no code can lift this block." -ForegroundColor Red
      Write-Host "     Copy $iniBackup back over $ini by hand (B10 territory)." -ForegroundColor Red
      $fail++
    }
  }
  Write-Host "`n=== teardown (this run's minted code) ===" -ForegroundColor Cyan
  MMEmergencyLift
}

Write-Host "`n================ B7 RESULT: $pass passed, $fail failed ================" -ForegroundColor $(if($fail -eq 0){'Green'}else{'Red'})
"B7_FAILCLOSED_RESULT pass=$pass fail=$fail"
