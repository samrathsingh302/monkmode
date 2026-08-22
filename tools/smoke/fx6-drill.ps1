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

# FX6 drill (RUN ELEVATED, FOREGROUND, WATCHED) - the config-writer / race family.
#
# WHAT IT PROVES LIVE. Service1.TimeChangeHoldActive is already PURE and unit-pinned
# (Service1.vb:2402-2414). What no unit test can show is whether the RUNNING service
# actually wires that gate in, and whether a raised-then-orphaned [Time] TimeChanging
# really self-expires at the 300s bound (TimeChangeHoldMaxSeconds, Service1.vb:2400)
# instead of wedging the block forever. Three sub-drills:
#
#   1. ARM-vs-RETIRE RACE (no clock manipulation - always runnable, runs FIRST).
#      Arm a second slot in the seconds around another slot's retirement and confirm the
#      confirmed arm is not clobbered and no duplicate slot appears.
#   2. ORPHAN RECOVERY end to end. Raise TimeChanging via a real clock change, kill the
#      notifier so nothing lowers it, and confirm the service treats it as orphaned past
#      the 300s bound and resumes - the block ends LATE by at most that bound, NEVER early.
#   3. GATE-SITE WIRING. Observe across that one raised-then-orphaned flag that the block
#      stayed enforced throughout (hosts marker never disappeared while the flag was up).
#
# *** READ THIS BEFORE RUNNING ***
# Sub-drills 2 and 3 CHANGE THE SYSTEM CLOCK. Every jump is anchor(wall) + Stopwatch
# (monotonic) -> Set-Date -> assert -> FINALLY Set-Date(anchor + real elapsed). The
# Stopwatch is immune to the jump, so the restore is always the TRUE time. BUT only an
# IN-PROCESS exit runs a finally: an EXTERNAL hard-kill (a CI/agent timeout wrapping the
# run, a taskkill, closing the window) mid-jump SKIPS IT and leaves your clock wrong.
# So: run this DIRECTLY in a watched elevated PowerShell window. Do NOT run it through an
# agent tool with a timeout, do NOT pipe it, do NOT walk away. (Learned 2026-07-09.)
# Pass -SkipClock to run only sub-drill 1, which is completely safe and unattended.
#
# It arms REAL short blocks against the LIVE hosts file and the real SCM, then tears them
# down. It drills dist\, so dist\monkmode_setup.ini is overwritten with a smoke partner -
# your real setup in C:\Program Files\MonkMode is a different file and is NOT touched.
#
# Usage (ELEVATED):
#   powershell -ExecutionPolicy Bypass -File tools\smoke\fx6-drill.ps1
#   powershell -ExecutionPolicy Bypass -File tools\smoke\fx6-drill.ps1 -SkipClock

param([string]$Dist, [switch]$SkipClock)

$ErrorActionPreference = 'Continue'
if (-not $Dist) {
    if ($PSScriptRoot) { $Dist = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'dist' }
    else               { $Dist = 'C:\Users\samra\repos\monk-mode\dist' }
}
$monk    = Join-Path $Dist 'monkmode.exe'
$cfg     = Join-Path $Dist 'monkmode_settings.ini'
$hosts   = "$env:SystemRoot\System32\drivers\etc\hosts"
$cleanup = Join-Path $PSScriptRoot 'cleanup.ps1'

$pass = 0; $fail = 0; $void = 0
function Check($n, $c)  { if ($c) { Write-Host "  [PASS] $n" -ForegroundColor Green; $script:pass++ } else { Write-Host "  [FAIL] $n" -ForegroundColor Red; $script:fail++ } }
# VOID, never FAIL, when the HARNESS could not create the condition under test. A drill that
# reports FAIL for its own limitation sends the next session hunting a defect that isn't there.
function Void($n, $why) { Write-Host "  [VOID] $n - $why" -ForegroundColor Yellow; $script:void++ }

function SvcState     { $s = Get-Service MONKMODE -ErrorAction SilentlyContinue; if ($s) { $s.Status } else { 'gone' } }
function HostsBlocked { $(try { Get-Content $hosts -Raw } catch { '' }) -match '#### MonkMode Entries ####' }
function TimeChanging { $(try { (Get-Content $cfg -Raw) } catch { '' }) -split "`r?`n" | Where-Object { $_ -like 'TimeChanging=*' } | Select-Object -First 1 }
function SlotCount    { $l = $(try { (Get-Content $cfg -Raw) } catch { '' }) -split "`r?`n" | Where-Object { $_ -like 'SlotCount=*' } | Select-Object -First 1
                        if ($l) { [int]($l -replace 'SlotCount=', '') } else { 0 } }
function Notifiers    { @(Get-Process -Name mm_notify -ErrorAction SilentlyContinue).Count }

# Teardown that always works, in the documented order (the service is CanStop=False and
# self-restarting, so --force first, cleanup.ps1 as the backstop).
function ForceDown {
    & $monk unblock --force 2>&1 | Out-Null
    $u = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $u -and (SvcState) -ne 'gone') { Start-Sleep -Milliseconds 500 }
    if ((SvcState) -ne 'gone') { & powershell -ExecutionPolicy Bypass -File $cleanup -Dist $Dist 2>&1 | Out-Null }
}

# True clock offset vs an external HTTP Date header, WITHOUT setting the clock. 8s-bounded
# HTTP HEAD, never w32tm /resync - that can block indefinitely and has hung a drill before.
function NtpOffset {
    try {
        $r = Invoke-WebRequest -Uri 'https://www.cloudflare.com' -Method Head -UseBasicParsing -TimeoutSec 8
        $u = [DateTime]::Parse($r.Headers['Date'], [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
        return ((Get-Date).ToUniversalTime() - $u).TotalSeconds
    } catch { return $null }
}

# --- preconditions -----------------------------------------------------------
$me = [Security.Principal.WindowsPrincipal]([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Write-Host 'Run ELEVATED.' -ForegroundColor Red; exit 1 }
if (-not (Test-Path $monk)) { Write-Host "monkmode.exe not at $monk - build dist first." -ForegroundColor Red; exit 1 }
if (Get-Service MONKMODE -ErrorAction SilentlyContinue) { Write-Host 'MONKMODE service exists - tear it down first (unblock --force).' -ForegroundColor Yellow; exit 1 }
if (HostsBlocked) { Write-Host 'hosts already carries a MonkMode marker - clean up first.' -ForegroundColor Yellow; exit 1 }

Write-Host ("FX6 drill - dist build {0}" -f (Get-Item $monk).LastWriteTime.ToString('dd/MM HH:mm:ss')) -ForegroundColor Cyan
& $monk setup --partner 'Smoke Tester (smoke@test.local)' 2>&1 | Out-Null

try {
    # ================= 1. ARM-vs-RETIRE RACE (no clock manipulation) =================
    Write-Host "`n=== 1. arm-vs-retire race ===" -ForegroundColor Cyan
    # NEVER pipe or capture an arm: the CLI spawns mm_notify.exe, which used to inherit
    # stdout, so a PS pipeline blocked until block expiry and every later check ran
    # post-expiry (the 2026-07-10 void drills). Fixed 14/07, but the habit stays.
    & $monk block --sites example.com --for 2
    Start-Sleep -Seconds 4
    Check "slot A armed (service Running)" ((SvcState) -eq 'Running')
    Check "slot A: hosts marker present"   (HostsBlocked)
    $countA = SlotCount
    Check "slot A: SlotCount = 1" ($countA -eq 1)

    # Slot A retires at ~120s. Arm B inside the retirement window so the two writers collide.
    Write-Host "  waiting for slot A's retirement window (~110s) ..."
    Start-Sleep -Seconds 110
    & $monk block --sites example.net --for 4
    Start-Sleep -Seconds 20    # let A's retire tick and B's arm both land

    $countB = SlotCount
    Write-Host ("  SlotCount after the collision: {0}" -f $countB)
    # B must survive. A may or may not have retired yet - either 1 (A gone, B live) or
    # 2 (both still listed) is legitimate; 0 means B was clobbered, >2 means duplicates.
    Check "race: the confirmed arm was NOT clobbered (SlotCount >= 1)" ($countB -ge 1)
    Check "race: no duplicate slot appeared (SlotCount <= 2)"          ($countB -le 2)
    Check "race: still enforcing (hosts marker present)"               (HostsBlocked)
    Check "race: service still Running"                               ((SvcState) -eq 'Running')
    & $monk status
    ForceDown
    Check "race: torn down cleanly (service gone, hosts clean)" (((SvcState) -eq 'gone') -and -not (HostsBlocked))

    if ($SkipClock) {
        Write-Host "`n-SkipClock given: sub-drills 2 and 3 not run." -ForegroundColor Yellow
        $void += 2
    } else {
        # ================= feasibility probe: will a manual jump persist? =================
        # If w32time yanks a manual jump back within seconds, the service can never observe
        # it and the orphan drill is moot (and harmless). Probe with a tiny +90s jump.
        Write-Host "`n=== feasibility probe (does a manual clock jump persist a tick?) ===" -ForegroundColor Cyan
        $off0 = NtpOffset
        Write-Host ("  pre-drill NTP offset: {0}s" -f $off0)
        $anchor = Get-Date; $sw = [Diagnostics.Stopwatch]::StartNew(); $held = $false
        try {
            Set-Date -Date $anchor.AddSeconds(90) -ErrorAction Stop | Out-Null
            Start-Sleep -Seconds 8
            $held = ((Get-Date) - $anchor).TotalSeconds -gt 60
        } finally {
            $sw.Stop()
            Set-Date -Date $anchor.AddSeconds([math]::Round($sw.Elapsed.TotalSeconds)) -ErrorAction SilentlyContinue | Out-Null
        }
        Check "clock restored after the probe (within 5s of NTP)" ([math]::Abs([double](NtpOffset)) -lt 5)

        if (-not $held) {
            Void "orphan recovery"  "w32time yanks manual jumps back within 8s - the service cannot observe the change"
            Void "gate-site wiring" "same reason; both remain unit-pinned (TimeChangeHoldActive, Service1.vb:2402)"
        } else {
            # ================= 2 + 3. ORPHAN RECOVERY and GATE-SITE WIRING =================
            # One armed block carries both: the flag is raised, orphaned, and the block is
            # watched across the whole hold to prove it never stopped enforcing.
            Write-Host "`n=== 2+3. orphan recovery + gate-site wiring ===" -ForegroundColor Cyan
            $nominal = 7   # minutes; must exceed the 300s bound so the release is observable
            & $monk block --sites example.com --for $nominal
            $armed = Get-Date
            Start-Sleep -Seconds 15   # let HighWater seed on real ticks
            Check "orphan: block armed and enforcing" (((SvcState) -eq 'Running') -and (HostsBlocked))

            # Raise TimeChanging by making a real clock change, then orphan it by killing the
            # notifier. The GUARDIAN relaunches a killed notifier by design, so a respawn is
            # expected - what matters is that nothing LOWERS the flag before the bound.
            $anchor = Get-Date; $sw = [Diagnostics.Stopwatch]::StartNew()
            try {
                Set-Date -Date $anchor.AddSeconds(120) -ErrorAction Stop | Out-Null
                Start-Sleep -Seconds 6
            } finally {
                $sw.Stop()
                Set-Date -Date $anchor.AddSeconds([math]::Round($sw.Elapsed.TotalSeconds)) -ErrorAction SilentlyContinue | Out-Null
            }
            Check "clock restored after the raise (within 5s of NTP)" ([math]::Abs([double](NtpOffset)) -lt 5)

            Get-Process -Name mm_notify -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 3
            $flag = TimeChanging
            Write-Host ("  [Time] {0}   notifiers alive: {1}" -f $flag, (Notifiers))
            if ($flag -notlike 'TimeChanging=yes*') {
                Void "orphan recovery" "the clock change did not leave TimeChanging raised (read '$flag') - nothing to orphan"
                Void "gate-site wiring" "no raised flag to observe the gates across"
            } else {
                # Watch the whole hold. The block must NEVER stop enforcing while the flag is
                # up (gate-site wiring), and the flag must self-expire past the 300s bound.
                Write-Host "  flag is up. Watching the 300s bound (this takes ~6 minutes) ..."
                $enforcedThroughout = $true
                $released = $false
                $deadline = (Get-Date).AddSeconds(400)
                while ((Get-Date) -lt $deadline) {
                    Start-Sleep -Seconds 15
                    if (-not (HostsBlocked)) { $enforcedThroughout = $false }
                    if ((TimeChanging) -like 'TimeChanging=no*') { $released = $true; break }
                }
                $heldFor = ((Get-Date) - $armed).TotalSeconds
                Check "gate wiring: enforced continuously while the flag was raised" $enforcedThroughout
                Check "orphan: the flag self-expired past the 300s bound"            $released
                Write-Host ("  flag released after ~{0}s from arm" -f [math]::Round($heldFor))

                # The whole point: releasing the gate can NEVER lift early (HighWater is not
                # persisted while the flag holds, so the block over-runs by the wedged span).
                Write-Host "  waiting for the block's own end (it must end LATE, never early) ..."
                $endBy = $armed.AddSeconds($nominal * 60 + 400)
                while ((Get-Date) -lt $endBy -and (HostsBlocked)) { Start-Sleep -Seconds 15 }
                $lifted = ((Get-Date) - $armed).TotalSeconds
                Check "orphan: block did NOT lift early (>= its nominal $nominal min)" ($lifted -ge ($nominal * 60))
                Write-Host ("  lifted at ~{0}s vs nominal {1}s (over-run {2}s, bound 300s)" -f [math]::Round($lifted), ($nominal * 60), [math]::Round($lifted - $nominal * 60))
            }
            ForceDown
        }
    }
} finally {
    # Belt and braces: never leave this machine armed, whatever happened above.
    ForceDown
    Write-Host "`n--- final state ---"
    Write-Host ("  service: {0}   hosts marker: {1}   notifiers: {2}" -f (SvcState), (HostsBlocked), (Notifiers))
    $o = NtpOffset
    Write-Host ("  clock offset vs NTP: {0}s  (must be within a few seconds)" -f $o)
}

Write-Host "`n================ FX6 RESULT: $pass passed, $fail failed, $void void ================" -ForegroundColor Cyan
"FX6_RESULT pass=$pass fail=$fail void=$void"
