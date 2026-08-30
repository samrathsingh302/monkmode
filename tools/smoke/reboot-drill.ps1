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

# REBOOT DRILL (RUN ELEVATED) - three open questions, one block, no supervision needed.
#
# A reboot kills any session driving it, so this script is deliberately split into modes
# you run yourself, minutes or hours apart. It is designed so that ONE armed block answers
# everything that was still unproven about running MonkMode unattended:
#
#   91  DOUBLE-NOTIFIER. After a reboot, exactly ONE mm_notify must be alive. Both the HKCU
#       Run autorun and the SYSTEM guardian can launch it, so the single-instance claim
#       (D4c) is what stops two. Never live-tested; deferred 20/08 because a reboot would
#       have killed six live sessions.
#   R   REBOOT SURVIVAL. Never watched end to end on ANY build. The service is AUTO_START
#       and hosts is a static file, so enforcement SHOULD resume - but "should" is not
#       evidence, and the failure direction matters: if the service did not come back, hosts
#       would stay blocked with nothing alive to ever lift it at expiry.
#   E   NATURAL EXPIRY ON THE CURRENT BUILD. The only rigorous full-cycle expiry proof is a
#       5-minute block from 09/07/2026 on PRE-v1.1, pre-.NET-10 binaries. This re-proves it
#       on what is actually installed, at a duration long enough to be meaningful.
#
# HOW TO RUN IT - three commands, elevated, in this order:
#
#   1)  ...\reboot-drill.ps1 -Arm -Minutes 45
#       Arms a real block and writes a marker file so the later modes know what to expect.
#       Note the one-time code it prints if you want the option of leaving early.
#
#   2)  reboot the machine normally, log back in, wait ~60s, then:
#       ...\reboot-drill.ps1 -Check
#       Counts notifiers, confirms the service came back and the block is still enforcing.
#
#   3)  ...\reboot-drill.ps1 -Watch
#       Sits until the block's own end time and confirms it lifts ITSELF - hosts marker
#       gone, service stopped. Safe to run in a window you leave open; it only reads.
#
# LEDGER 319 (30/08/2026): there is NO escape hatch any more. If anything looks
# wrong, the block still ends at its own end time, or earlier with the one-time
# partner code this drill's arm printed:  monkmode unblock --code <CODE>  (elevated).
# Keep that code where you can find it before starting a reboot drill.
#
# It uses the INSTALLED binary (Program Files, on PATH) by default, because the point is to
# prove the thing you actually run. Pass -Dist to drill a build folder instead.

param(
    [switch]$Arm,
    [switch]$Check,
    [switch]$Watch,
    [int]$Minutes = 45,
    [string]$Dist,
    [string]$Sites = 'example.com'
)

$ErrorActionPreference = 'Continue'
$monk  = if ($Dist) { Join-Path $Dist 'monkmode.exe' } else { 'C:\Program Files\MonkMode\monkmode.exe' }
$hosts = "$env:SystemRoot\System32\drivers\etc\hosts"
$state = Join-Path $env:ProgramData 'MonkMode\reboot-drill.json'

$pass = 0; $fail = 0; $void = 0
function Check($n, $c) { if ($c) { Write-Host "  [PASS] $n" -ForegroundColor Green; $script:pass++ } else { Write-Host "  [FAIL] $n" -ForegroundColor Red; $script:fail++ } }
# VOID, never PASS, when the PRECONDITION for an assertion was never created. Run 1 (25/08)
# printed [PASS] on "the service came back after the reboot" and "exactly ONE notifier after
# the reboot" on a machine that had not rebooted for seven days: both were vacuously true and
# read as evidence. An assertion whose premise is absent proves nothing and must say so.
function Void($n, $why) { Write-Host "  [VOID] $n - $why" -ForegroundColor Yellow; $script:void++ }
function SvcState     { $s = Get-Service MONKMODE -ErrorAction SilentlyContinue; if ($s) { $s.Status } else { 'gone' } }
# An unreadable hosts file used to read as '' = "not blocked", which every LIFT assertion
# counts as a pass. Err the OTHER way (still blocked) and say so: a false "still enforcing"
# fails loudly, while a false "lifted" walks away leaving the machine blocked.
function HostsBlocked { try { (Get-Content $hosts -Raw -ErrorAction Stop) -match '#### MonkMode Entries ####' } catch { Write-Host '  [WARN] hosts unreadable - treating as STILL BLOCKED' -ForegroundColor Yellow; $true } }
function Notifiers    { @(Get-Process -Name mm_notify -ErrorAction SilentlyContinue) }

$me = [Security.Principal.WindowsPrincipal]([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Write-Host 'Run ELEVATED.' -ForegroundColor Red; exit 1 }
if (-not (Test-Path $monk)) { Write-Host "monkmode.exe not at $monk" -ForegroundColor Red; exit 1 }
if (-not ($Arm -or $Check -or $Watch)) { Write-Host 'Pass one of -Arm, -Check or -Watch. See the header for the order.' -ForegroundColor Yellow; exit 1 }

# ---------------- ARM ----------------
if ($Arm) {
    if (Get-Service MONKMODE -ErrorAction SilentlyContinue) { Write-Host 'MONKMODE already exists - tear down first.' -ForegroundColor Yellow; exit 1 }
    Write-Host "=== ARM: a real $Minutes-minute block ===" -ForegroundColor Cyan
    # Stamp the arm BEFORE the command, not after the settle sleeps below. Run 1 (25/08)
    # stamped it 43s late, so the computed end sat 43s beyond MonkMode's real end and the
    # "did not lift early" assertion carried 43s of slack in the WRONG direction - it would
    # have passed an early lift of up to 43s. MonkMode's own end is armedAt + Minutes to
    # within a second or two, and `monkmode status` is the cross-check.
    $armedAt = Get-Date
    # NEVER pipe or capture an arm - the notifier used to inherit stdout and wedge the
    # calling shell until expiry. Let it print straight to the console.
    & $monk block --sites $Sites --for $Minutes
    Start-Sleep -Seconds 8
    Check "armed: service Running"      ((SvcState) -eq 'Running')
    Check "armed: hosts marker present" (HostsBlocked)
    # Give the service a tick or two to spawn the guardian and the notifier before counting.
    Start-Sleep -Seconds 35
    $n = (Notifiers).Count
    Write-Host ("  notifiers before the reboot: {0}" -f $n)
    Check "armed: exactly one notifier BEFORE the reboot (the baseline 91 compares against)" ($n -eq 1)
    @{ armedAt = $armedAt.ToString('o'); minutes = $Minutes; sites = $Sites; notifiersBefore = $n
       bootAtArm = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $state -Encoding utf8
    Write-Host ""
    Write-Host ("  ends at {0} (cross-check it against 'Ends' in the table above)" -f $armedAt.AddMinutes($Minutes).ToString('HH:mm:ss'))
    Write-Host ""
    Write-Host "NOW REBOOT NORMALLY - the drill is worthless without it. Then log back in, wait ~60s, and run:  -Check" -ForegroundColor Yellow
    Write-Host "If you want out at any point:  monkmode unblock --code <CODE>  (the code printed above - there is no other way out)" -ForegroundColor Yellow
}

# ---------------- CHECK (after the reboot) ----------------
if ($Check) {
    Write-Host "=== CHECK: after the reboot ===" -ForegroundColor Cyan
    $s = if (Test-Path $state) { Get-Content $state -Raw | ConvertFrom-Json } else { $null }
    if (-not $s) { Write-Host 'No -Arm marker found; run -Arm first.' -ForegroundColor Yellow; exit 1 }
    $armedAt = [DateTime]::Parse($s.armedAt)
    $endsAt  = $armedAt.AddMinutes($s.minutes)
    $boot    = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    $up      = (Get-Date) - $boot
    # Days, explicitly. 'hh\:mm\:ss' TRUNCATES the day component, so run 1 printed a 6-day
    # uptime as "17:49:51" and it read like the machine had just come back.
    Write-Host ("  armed {0}, ends {1}" -f $armedAt.ToString('HH:mm:ss'), $endsAt.ToString('HH:mm:ss'))
    Write-Host ("  last boot {0}, uptime {1}d {2}" -f $boot.ToString('dd/MM HH:mm:ss'), $up.Days, $up.ToString('hh\:mm\:ss'))
    $rebooted = $boot -gt $armedAt
    Check "the machine really did reboot since the arm" $rebooted
    if ((Get-Date) -ge $endsAt) { Write-Host '  NOTE: the block has already passed its end time - reboot-survival is still readable, expiry is not.' -ForegroundColor Yellow }

    $procs = Notifiers
    Write-Host ("  notifiers now: {0}{1}" -f $procs.Count, $(if ($procs.Count) { ' (pids ' + (($procs | ForEach-Object { $_.Id }) -join ', ') + ')' } else { '' }))

    if (-not $rebooted) {
        # Everything below this line is ABOUT the reboot. With no reboot they are vacuously
        # true and would read as evidence they are not. Say VOID and say why.
        Void "R: the MONKMODE service came back after the reboot"          "no reboot happened - this only shows the service is running, which it never stopped being"
        Void "R: still enforcing (hosts marker survived the reboot)"       "no reboot happened - nothing was survived"
        Void "91: exactly ONE mm_notify after the reboot (D4c)"            "no reboot happened - one notifier during an ordinary block is the baseline, not the drill"
        Write-Host "  => REBOOT THE MACHINE while this block is still running, then re-run -Check." -ForegroundColor Yellow
    } else {
        # R - reboot survival.
        Check "R: the MONKMODE service came back after the reboot" ((SvcState) -eq 'Running')
        Check "R: still enforcing (hosts marker survived the reboot)" (HostsBlocked)
        # 91 - exactly one notifier. Both the HKCU Run autorun and the guardian can launch it.
        Check "91: exactly ONE mm_notify after the reboot (D4c single-instance)" ($procs.Count -eq 1)
    }
    & $monk status
    Write-Host ""
    Write-Host "Leave it running. When you are near its end time, run:  -Watch" -ForegroundColor Yellow
}

# ---------------- WATCH (natural expiry) ----------------
if ($Watch) {
    Write-Host "=== WATCH: does it lift ITSELF? ===" -ForegroundColor Cyan
    $s = if (Test-Path $state) { Get-Content $state -Raw | ConvertFrom-Json } else { $null }
    if (-not $s) { Write-Host 'No -Arm marker found; run -Arm first.' -ForegroundColor Yellow; exit 1 }
    $armedAt = [DateTime]::Parse($s.armedAt)
    $endsAt  = $armedAt.AddMinutes($s.minutes)
    # Generous grace: a clock-change hold can legitimately delay the end by up to 300s
    # (TimeChangeHoldMaxSeconds), and the tick is 10s. Ending LATE is correct; early is not.
    $deadline = $endsAt.AddSeconds(600)
    Write-Host ("  ends {0}; watching until {1} (late is fine, early is a bug)" -f $endsAt.ToString('HH:mm:ss'), $deadline.ToString('HH:mm:ss'))
    $watchFrom = Get-Date
    # "Exited" is only meaningful for a process that was seen ALIVE. A notifier that never
    # spawned also counts 0, and run 1 (25/08) showed how convincing a vacuous pass looks -
    # so witness them at watch start, and VOID the exit assertions if nothing was there.
    $notifiersAtWatch = (Notifiers).Count
    $guardiansAtWatch = @(Get-Process -Name mm_guard -ErrorAction SilentlyContinue).Count
    # Record WHEN the marker disappeared. The old in-loop early-lift detector re-read
    # HostsBlocked immediately after the loop condition had just proven it TRUE, so it
    # could never fire - dead code posing as a witness.
    $liftedAt = $null
    while ((Get-Date) -lt $deadline) {
        if (-not (HostsBlocked)) { $liftedAt = Get-Date; break }
        Start-Sleep -Seconds 15
    }
    $stillBlocked = HostsBlocked
    if (-not $stillBlocked -and -not $liftedAt) { $liftedAt = Get-Date }
    Check "E: the block lifted by itself (hosts marker gone)"       (-not $stillBlocked)
    # "Did not lift early" is only EVIDENCE if this watch was looking during the early
    # window. Launched at/after endsAt-30s it would pass unwitnessed - VOID instead.
    if ($watchFrom -ge $endsAt.AddSeconds(-30)) {
        Void "E: it did NOT lift early" "watch started at/after the early window (end-30s) - an early lift could not have been witnessed"
    } else {
        Check "E: it did NOT lift early" (-not ($liftedAt -and $liftedAt -lt $endsAt.AddSeconds(-30)))
    }
    Check "E: the service stopped itself (Stopped or gone)"         ((SvcState) -in @('Stopped', 'gone'))
    # SETTLE before asserting a process is GONE. Run 1 (25/08) failed this the instant the
    # marker vanished; mm_notify was still showing its expiry toast and had exited moments
    # later. Same trap as asserting a spawned process exists with no settle window, inverted.
    $q = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $q -and (Notifiers).Count -gt 0) { Start-Sleep -Seconds 5 }
    if ($notifiersAtWatch -gt 0) { Check "E: the notifier exited (within a 90s settle window)" ((Notifiers).Count -eq 0) }
    else { Void "E: the notifier exited" "no notifier was alive when the watch began - never spawned, or already gone; 'exited' would be vacuous" }
    if ($guardiansAtWatch -gt 0) { Check "E: the guardian exited" (@(Get-Process -Name mm_guard -ErrorAction SilentlyContinue).Count -eq 0) }
    else { Void "E: the guardian exited" "no guardian was alive when the watch began - never spawned, or already gone; 'exited' would be vacuous" }
    if ($stillBlocked) {
        # The watch's OWN deadline expiring is not a product event. Run 1 printed it as
        # "lifted at ..." and claimed a lift that never happened.
        Write-Host ("  TIMED OUT at {0}: still enforcing (nominal end {1}, watch deadline {2}) - nothing lifted" -f (Get-Date).ToString('HH:mm:ss'), $endsAt.ToString('HH:mm:ss'), $deadline.ToString('HH:mm:ss')) -ForegroundColor Yellow
    } else {
        Write-Host ("  lifted at {0} (nominal end {1}, {2}s late)" -f $liftedAt.ToString('HH:mm:ss'), $endsAt.ToString('HH:mm:ss'), [math]::Round(($liftedAt - $endsAt).TotalSeconds))
    }
    Write-Host "  NOTE: the service staying REGISTERED but Stopped is normal - it is not a leftover block."
    & $monk status
    # Keep the arm marker on a timeout so a later -Watch (or -Check) can still read it;
    # only a witnessed lift retires it.
    if (-not $stillBlocked) { Remove-Item -LiteralPath $state -Force -ErrorAction SilentlyContinue }
}

Write-Host "`n================ REBOOT DRILL: $pass passed, $fail failed, $void void ================" -ForegroundColor Cyan
if ($void -gt 0) { Write-Host "VOID means the precondition was never created - those assertions prove NOTHING. Re-run them properly." -ForegroundColor Yellow }
"REBOOT_DRILL pass=$pass fail=$fail void=$void"
