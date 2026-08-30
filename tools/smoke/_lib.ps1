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

# ============================================================================
# tools\smoke\_lib.ps1 - the SHARED TEARDOWN for every elevated smoke drill.
#
# WHY THIS FILE EXISTS. Ledger 319 (30/08/2026) removed `monkmode unblock
# --force`, the cooling-off exit and tools\smoke\cleanup.ps1. Five drills used
# `--force` as their inter-section teardown and stopped working. Ledger 320
# converts all five to the ONE exit that still exists: the one-time partner
# accountability code the arm prints. That teardown is four fiddly steps
# (capture the arm's stdout -> parse the code -> submit it -> poll the service
# to Stopped-or-gone -> sc delete), and five hand-rolled copies of it is five
# chances to write a teardown that silently no-ops. So it lives here once.
#
# THERE IS NO ESCAPE HATCH BEHIND THESE HELPERS, BY DESIGN. Every lift below
# is a code this run's own arm minted seconds earlier. If a drill aborts before
# its lift, the block it armed stands until its own --for timer runs out -
# nothing in this file, at any privilege level, can shorten that. Keep every
# drill's --for short enough that natural expiry is an acceptable backstop.
# (`--for 1` is always refused - Program.vb's >60s-in-the-future floor - so one
# minute is the floor, and 2 is the practical minimum.)
#
# USAGE - dot-source it from the drill, then MMInit once:
#
#     . (Join-Path $PSScriptRoot '_lib.ps1')
#     MMInit -Monk $monk -Hosts $hosts
#
# A dot-sourced function runs in the CALLING script's session state, so
# MMCheck below increments the drill's own $pass / $fail counters and its
# output interleaves with the drill's own [PASS]/[FAIL] lines. Every drill
# already declares `$pass = 0; $fail = 0` at script scope; that is the whole
# contract. MMInit stores the two paths the helpers need in $script:MM* so
# nothing here depends on a caller variable being named a particular thing.
#
# The drills keep their OWN Check/SvcState/HostsBlocked helpers (each has
# slightly different semantics - fx6's HostsBlocked deliberately errs to "still
# blocked" on an unreadable file). Nothing here overwrites them: every name in
# this file is MM-prefixed, except ParseCode, which cv-d-smoke.ps1 already
# owned and whose shape MonkMode\Program.vb:1228-1235 pins by unit test.
# ============================================================================

$script:MMMonk  = $null
$script:MMHosts = "$env:SystemRoot\System32\drivers\etc\hosts"
# Every code this run has minted, newest first: @{ Id = <int>; Code = 'XXXXX-XXXXX' }.
# MMArm appends; MMEmergencyLift walks it in the outer finally. This is NOT a
# rescue hatch - it holds only codes THIS run was legitimately shown.
$script:MMCodes = @()

function MMInit {
  param([Parameter(Mandatory)][string]$Monk, [string]$Hosts)
  $script:MMMonk = $Monk
  if ($Hosts) { $script:MMHosts = $Hosts }
  $script:MMCodes = @()
}

# Scored assertion, in the drills' own format and against their own counters.
function MMCheck($name, $cond) {
  if ($cond) { Write-Host "  [PASS] $name" -ForegroundColor Green; $script:pass++ }
  else       { Write-Host "  [FAIL] $name" -ForegroundColor Red;   $script:fail++ }
}

function MMSvcState {
  $s = Get-Service MONKMODE -ErrorAction SilentlyContinue
  if ($s) { "$($s.Status)" } else { 'gone' }
}

function MMWaitSvc([string]$want, [int]$sec) {
  $u = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $u) { if ((MMSvcState) -eq $want) { return $true }; Start-Sleep -Milliseconds 500 }
  return ((MMSvcState) -eq $want)
}

# RUNBOOK E9 - THE SINGLE MOST-GOT-WRONG FACT IN THIS HARNESS. A lift (natural
# expiry OR a partner code) leaves the service REGISTERED and STOPPED. It does
# NOT leave 'gone': only an explicit `sc.exe delete` removes the registration.
# A teardown that polls for 'gone' therefore always times out, reports a wedge
# that never happened, and sends the next session hunting a defect that isn't
# there. Poll Stopped-OR-gone, always. ('gone' is accepted so the helper still
# works after MMResetInstall has already deleted the registration.)
function MMWaitSvcLifted([int]$sec) {
  $u = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $u) { if ((MMSvcState) -in @('Stopped','gone')) { return $true }; Start-Sleep -Milliseconds 500 }
  return ((MMSvcState) -in @('Stopped','gone'))
}

function MMHostsRaw {
  try { Get-Content $script:MMHosts -Raw -ErrorAction Stop } catch { $null }
}

# True when hosts still carries MonkMode's marker block. An UNREADABLE hosts
# file reads as STILL BLOCKED, never as clean: a false "clean" walks away from
# a machine that is still enforcing, while a false "still blocked" fails loudly
# at the next teardown assertion. (fx6-drill.ps1's rule, generalised.)
function MMHostsHasMarker {
  $t = MMHostsRaw
  if ($null -eq $t) { Write-Host '  [WARN] hosts unreadable - treating as STILL MARKED' -ForegroundColor Yellow; return $true }
  return ($t -match '#### MonkMode Entries ####')
}

function MMHostsReadOnly {
  try { [bool]((Get-Item $script:MMHosts).Attributes -band [IO.FileAttributes]::ReadOnly) } catch { $false }
}

# ---------------------------------------------------------------------------
# The one-time accountability code
# ---------------------------------------------------------------------------
# The arm prints, on consecutive lines (Program.vb:558-566, header pinned by
# Program.FormatUnlockCodeHeader at :1228-1235):
#
#     Emergency unlock code for block 3 (give it to your accountability partner NOW - it will NOT be shown again):
#         K4M7Q-2XPWB
#
# The code is 10 chars of Crockford base32 (ConfigIntegrity.PartnerCodeAlphabet,
# "0123456789ABCDEFGHJKMNPQRSTVWXYZ" - no I/L/O/U) grouped XXXXX-XXXXX. We match
# that 5-5 SHAPE rather than "the next line", so the F74 partner-relay line
# printed BELOW the code cannot be mistaken for it, and a future line inserted
# between header and code fails loudly instead of returning prose.
$script:MMCodeRe = '\b([0-9A-Z]{5}-[0-9A-Z]{5})\b'

# The code from an arm's captured output, or $null.
function ParseCode($lines) {
  if ($null -eq $lines) { return $null }
  $text = ($lines | ForEach-Object { "$_" }) -join "`n"
  $idx = $text.IndexOf('Emergency unlock code')
  if ($idx -lt 0) { return $null }
  $m = [regex]::Match($text.Substring($idx), $script:MMCodeRe)
  if ($m.Success) { return $m.Groups[1].Value }
  return $null
}

# The block id from the same header ("... for block N ..."), or -1.
function ParseCodeId($lines) {
  if ($null -eq $lines) { return -1 }
  $text = ($lines | ForEach-Object { "$_" }) -join "`n"
  $m = [regex]::Match($text, 'Emergency unlock code for block\s+(\d+)')
  if ($m.Success) { return [int]$m.Groups[1].Value }
  return -1
}

# A shape-valid code that is NOT this one: flip the first character to a
# different symbol from the same alphabet. Stronger than a "WRONG-" prefix,
# which a length/shape check could reject before the KDF ever runs - this
# proves PartnerCodeMatches itself says no. NB the alphabet excludes I/L/O/U
# and NormalisePartnerCode folds I,L->1 and O->0, so a flip must avoid them or
# it could normalise back onto the real code.
function MMWrongCode([string]$code) {
  if (-not $code) { return 'ZZZZZ-ZZZZZ' }
  $first = $code.Substring(0,1)
  $flip = if ($first -ceq 'A') { 'B' } else { 'A' }
  return $flip + $code.Substring(1)
}

# ---------------------------------------------------------------------------
# Arming
# ---------------------------------------------------------------------------
# PIPE-WEDGE NOTE (why this capture is safe now): on a dist that predates the
# 10/07/2026 P2 fix, the CLI's RegisterAndLaunchNotifier spawned mm_notify.exe
# so it INHERITED the CLI's stdout, and any PowerShell pipeline reading that
# stdout blocked until the notifier exited at block EXPIRY - so every later
# check ran post-expiry (the void drills of 2026-07-10). Blocker.Register-
# AndLaunchNotifier fixed the root cause (UseShellExecute=True -> no handle
# inheritance), live-proven 14/07/2026 (piped arm returns in ~0.8s). Ledger 320
# makes the capture MANDATORY everywhere - the code is the only teardown left,
# and an arm whose stdout was thrown away cannot be torn down at all. So: run
# these drills against a CURRENT dist. A stale one wedges here.
#
# Returns @{ Out = <lines>; Code = 'XXXXX-XXXXX'; Id = <int> } and registers the
# code for MMEmergencyLift. Waits for the service to reach Running.
function MMArm([string]$argline, [int]$WaitSec = 60) {
  $out = & $script:MMMonk block @($argline -split ' ') 2>&1 | ForEach-Object { "$_" }
  [void](MMWaitSvc 'Running' $WaitSec)   # poll, not a fixed sleep: install+start races StartPending under load
  Start-Sleep -Seconds 2                 # small settle for the hosts write + config flush
  $code = ParseCode $out
  $id   = ParseCodeId $out
  if ($code) { $script:MMCodes = ,([pscustomobject]@{ Id = $id; Code = $code }) + $script:MMCodes }
  else {
    Write-Host "  [FAIL] arm did not print an unlock code - this block CANNOT be torn down early." -ForegroundColor Red
    Write-Host "         arm output follows:" -ForegroundColor Red
    $out | ForEach-Object { Write-Host "         $_" }
    $script:fail++
  }
  return [pscustomobject]@{ Out = $out; Code = $code; Id = $id }
}

# ---------------------------------------------------------------------------
# The exit surface, asserted against a LIVE block
# ---------------------------------------------------------------------------
# (a) `unblock --force` is an UNKNOWN OPTION now, not a withheld one: the CLI
#     warns "unrecognised option(s) --force - ignored", then gives the ordinary
#     refusal (exit 1), and the block goes on enforcing.
# (b) `unblock --id N` with no code is refused the same way (exit 1).
# Both are cheap (no waiting) - run them at the first live block of a drill.
function MMCheckRefusedExits([int]$Id) {
  $f = (& $script:MMMonk unblock --force 2>&1 | ForEach-Object { "$_" }) -join "`n"
  $fExit = $LASTEXITCODE
  MMCheck "319: 'unblock --force' refused as an unknown option (exit 1)" (($fExit -eq 1) -and ($f -match '(?i)unrecognised option') -and ($f -match '--force'))
  MMCheck "319: block still enforcing after 'unblock --force'" (((MMSvcState) -eq 'Running') -and (MMHostsHasMarker))
  $b = (& $script:MMMonk unblock --id $Id 2>&1 | ForEach-Object { "$_" }) -join "`n"
  $bExit = $LASTEXITCODE
  MMCheck "319: bare 'unblock --id $Id' refused, no code given (exit 1)" (($bExit -eq 1) -and ($b -match '(?i)partner code'))
  MMCheck "319: block still enforcing after the bare refusal" (((MMSvcState) -eq 'Running') -and (MMHostsHasMarker))
}

# (c) a WRONG code leaves the block standing. Costs ~16s (>1 service tick), so
#     it is a separate call the drill opts into.
function MMCheckWrongCode([string]$Code) {
  & $script:MMMonk unblock --code (MMWrongCode $Code) 2>&1 | Out-Null
  Start-Sleep -Seconds 16   # > one 10s tick: the service must have SEEN and rejected it
  MMCheck "319: a WRONG code did NOT lift the block (still enforcing)" (((MMSvcState) -eq 'Running') -and (MMHostsHasMarker))
}

# ---------------------------------------------------------------------------
# THE TEARDOWN: lift by code
# ---------------------------------------------------------------------------
# (d) the right code lifts within ~30s. The CLI only SUBMITS (it has zero lift
# authority); the SERVICE KDF-verifies against the slot's MAC-covered hash and
# lifts via its own stopMe() on a tick, so ~10s is typical and 30s is the
# scored bound. We poll to 60s before declaring a wedge, so a slow box reports
# "late" rather than stranding the drill mid-section.
#
# Returns $true when the service reached Stopped-or-gone. A no-op success when
# the block has ALREADY gone down (natural expiry beat us to it) - the drills
# with a natural-expiry section rely on that.
function MMLiftWithCode {
  param([string]$Code, [int]$Id = -1, [int]$ScoredSec = 30, [int]$HardSec = 60, [string]$Label = '')
  $what = if ($Label) { " ($Label)" } else { '' }
  if ((MMSvcState) -in @('Stopped','gone')) {
    Write-Host "  [ ok ] block already down before the code lift$what - nothing to lift." -ForegroundColor DarkGray
    return $true
  }
  if (-not $Code) {
    Write-Host "  [FAIL] no unlock code in scope$what - this block CANNOT be lifted early." -ForegroundColor Red
    Write-Host "         It stands until its own --for timer ends. There is no escape hatch (ledger 319)." -ForegroundColor Red
    $script:fail++
    return $false
  }
  $sw = [Diagnostics.Stopwatch]::StartNew()
  & $script:MMMonk unblock --code $Code 2>&1 | Out-Null
  $lifted = MMWaitSvcLifted $HardSec
  $sw.Stop()
  $secs = [int]$sw.Elapsed.TotalSeconds
  if (-not $lifted) {
    Write-Host "  [FAIL] the partner code did NOT lift the block$what within ${HardSec}s (service is '$(MMSvcState)')." -ForegroundColor Red
    Write-Host "         THE MACHINE IS STILL BLOCKED. There is no force teardown (ledger 319): the block" -ForegroundColor Red
    Write-Host "         ends at its own end time. Code was: $Code" -ForegroundColor Red
    $script:fail++
    return $false
  }
  MMCheck "319: the right code lifted the block$what in ${secs}s (<=${ScoredSec}s; Stopped-or-gone per RUNBOOK E9)" ($secs -le $ScoredSec)
  # A code lift runs the service's own stopMe(), so hosts must come back clean.
  # We ASSERT that - we never strip the marker ourselves. A marker left behind
  # after a lift is a DEFECT to report, not something for a smoke test to tidy
  # away (tidying it is exactly how a real regression goes unnoticed).
  MMCheck "319: hosts marker removed by the code lift$what" (-not (MMHostsHasMarker))
  return $true
}

# ---------------------------------------------------------------------------
# THE TEARDOWN: back to a clean 'gone' for the next section
# ---------------------------------------------------------------------------
# Every drill's precondition is "no MONKMODE service at all", and a lift leaves
# it Stopped-but-REGISTERED (E9 again). Only `sc.exe delete` closes that gap.
#
# REFUSES to run while the service is Running: deleting a registration out from
# under a live block is exactly the forced teardown ledger 319 removed, and a
# smoke test must not reimplement it. Lift by code first.
function MMResetInstall([int]$TimeoutSec = 30) {
  if (-not ((MMSvcState) -in @('Stopped','gone'))) {
    Write-Host "  [FAIL] refusing to reset: MONKMODE is '$(MMSvcState)', not Stopped-or-gone." -ForegroundColor Red
    Write-Host "         A live block is lifted by its partner code or by its own timer - never by sc delete." -ForegroundColor Red
    $script:fail++
    return $false
  }
  # sc delete can return "marked for deletion" while any handle (services.msc,
  # an open SCM snap-in, our own Get-Service cache) is still open, so poll.
  $u = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $u -and (MMSvcState) -ne 'gone') {
    sc.exe delete MONKMODE 2>&1 | Out-Null
    Start-Sleep -Milliseconds 750
  }
  $gone = ((MMSvcState) -eq 'gone')
  MMCheck "teardown: MONKMODE registration deleted (service 'gone')" $gone
  if (-not $gone) {
    Write-Host "         'sc delete' did not take. If the B6 deny-DELETE ACE is still on the service" -ForegroundColor Yellow
    Write-Host "         object, the lift did not run stopMe()'s RestoreDefaultServiceSd - report that," -ForegroundColor Yellow
    Write-Host "         do not strip the ACE to make this pass. Close services.msc and retry." -ForegroundColor Yellow
  }
  # The notifier autorun is the CLI's HKCU footprint. The CLI's own
  # ClearNotifierAutorun went with the escape hatch (ledger 319), so a leftover
  # value here is harmless housekeeping, not an assertion - just remove it.
  Remove-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name MonkMode_notify -ErrorAction SilentlyContinue
  # ASSERT, NEVER FIX (F78): hosts must already be marker-free and writable.
  # Stripping the marker here would hide a service that failed to restore it,
  # and clearing the read-only bit would hide the 313(b) regression (a natural
  # expiry leaving hosts locked, so every later hosts writer fails until a
  # manual `attrib -r`).
  MMCheck "teardown: hosts carries no MonkMode marker" (-not (MMHostsHasMarker))
  MMCheck "teardown: hosts is NOT read-only after the lift" (-not (MMHostsReadOnly))
  return $gone
}

# Submit a code and assert NOTHING. For the multi-block case: a code opens only
# the slot that minted it, so with two blocks armed the FIRST submission may
# match a slot that has already retired, and the service rightly stays Running
# for the other one. Asserting a lift there would fail on a healthy machine.
# Submit the older codes with this, then MMLiftWithCode/MMTearDown the last one.
function MMSubmitCode([string]$Code) {
  if (-not $Code) { return }
  & $script:MMMonk unblock --code $Code 2>&1 | Out-Null
}

# Lift by code, then reset to 'gone'. The ordinary inter-section teardown -
# what `ForceDown` used to be, spelled honestly.
function MMTearDown {
  param([string]$Code, [int]$Id = -1, [string]$Label = '')
  [void](MMLiftWithCode -Code $Code -Id $Id -Label $Label)
  return (MMResetInstall)
}

# Outer-finally backstop: try every code this run minted (each verifies against
# its own slot, so a stale one is simply rejected), then reset if that got the
# machine idle. NOT a rescue hatch - it can only use codes this run was shown,
# and it says so loudly when nothing is left to try.
function MMEmergencyLift {
  if ((MMSvcState) -in @('Stopped','gone')) { [void](MMResetInstall); return }
  Write-Host "`n  a block is still live at teardown - submitting this run's minted codes..." -ForegroundColor Yellow
  foreach ($c in $script:MMCodes) {
    & $script:MMMonk unblock --code $c.Code 2>&1 | Out-Null
    if (MMWaitSvcLifted 25) { break }
  }
  if ((MMSvcState) -in @('Stopped','gone')) { [void](MMResetInstall); return }
  Write-Host "  !! THE MACHINE IS STILL BLOCKED and none of this run's codes lifted it." -ForegroundColor Red
  Write-Host "     Ledger 319 removed every force teardown: the block ends at its own end time." -ForegroundColor Red
  Write-Host "     Wait it out ('$($script:MMMonk)' status shows the end), then 'sc.exe delete MONKMODE' while idle." -ForegroundColor Red
}
