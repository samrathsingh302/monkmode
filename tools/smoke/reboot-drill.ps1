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
# If anything ever looks wrong:  monkmode unblock --force   (elevated). Always works.
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

$pass = 0; $fail = 0
function Check($n, $c) { if ($c) { Write-Host "  [PASS] $n" -ForegroundColor Green; $script:pass++ } else { Write-Host "  [FAIL] $n" -ForegroundColor Red; $script:fail++ } }
function SvcState     { $s = Get-Service MONKMODE -ErrorAction SilentlyContinue; if ($s) { $s.Status } else { 'gone' } }
function HostsBlocked { $(try { Get-Content $hosts -Raw } catch { '' }) -match '#### MonkMode Entries ####' }
function Notifiers    { @(Get-Process -Name mm_notify -ErrorAction SilentlyContinue) }

$me = [Security.Principal.WindowsPrincipal]([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Write-Host 'Run ELEVATED.' -ForegroundColor Red; exit 1 }
if (-not (Test-Path $monk)) { Write-Host "monkmode.exe not at $monk" -ForegroundColor Red; exit 1 }
if (-not ($Arm -or $Check -or $Watch)) { Write-Host 'Pass one of -Arm, -Check or -Watch. See the header for the order.' -ForegroundColor Yellow; exit 1 }

# ---------------- ARM ----------------
if ($Arm) {
    if (Get-Service MONKMODE -ErrorAction SilentlyContinue) { Write-Host 'MONKMODE already exists - tear down first.' -ForegroundColor Yellow; exit 1 }
    Write-Host "=== ARM: a real $Minutes-minute block ===" -ForegroundColor Cyan
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
    @{ armedAt = (Get-Date).ToString('o'); minutes = $Minutes; sites = $Sites; notifiersBefore = $n } |
        ConvertTo-Json | Set-Content -LiteralPath $state -Encoding utf8
    Write-Host ""
    Write-Host "NOW REBOOT NORMALLY. After you log back in, wait ~60s and run:  -Check" -ForegroundColor Yellow
    Write-Host "If you want out at any point:  monkmode unblock --force" -ForegroundColor Yellow
}

# ---------------- CHECK (after the reboot) ----------------
if ($Check) {
    Write-Host "=== CHECK: after the reboot ===" -ForegroundColor Cyan
    $s = if (Test-Path $state) { Get-Content $state -Raw | ConvertFrom-Json } else { $null }
    if (-not $s) { Write-Host 'No -Arm marker found; run -Arm first.' -ForegroundColor Yellow; exit 1 }
    $armedAt = [DateTime]::Parse($s.armedAt)
    $endsAt  = $armedAt.AddMinutes($s.minutes)
    $up      = (Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    Write-Host ("  armed {0}, ends {1}, uptime {2}" -f $armedAt.ToString('HH:mm:ss'), $endsAt.ToString('HH:mm:ss'), $up.ToString('hh\:mm\:ss'))
    Check "the machine really did reboot since the arm" ((Get-CimInstance Win32_OperatingSystem).LastBootUpTime -gt $armedAt)
    if ((Get-Date) -ge $endsAt) { Write-Host '  NOTE: the block has already passed its end time - reboot-survival is still readable, expiry is not.' -ForegroundColor Yellow }

    # R - reboot survival.
    Check "R: the MONKMODE service came back after the reboot" ((SvcState) -eq 'Running')
    Check "R: still enforcing (hosts marker survived the reboot)" (HostsBlocked)

    # 91 - exactly one notifier. Both the HKCU Run autorun and the guardian can launch it.
    $procs = Notifiers
    Write-Host ("  notifiers now: {0}{1}" -f $procs.Count, $(if ($procs.Count) { ' (pids ' + (($procs | ForEach-Object { $_.Id }) -join ', ') + ')' } else { '' }))
    Check "91: exactly ONE mm_notify after the reboot (D4c single-instance)" ($procs.Count -eq 1)
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
    $liftedEarly = $false
    while ((Get-Date) -lt $deadline -and (HostsBlocked)) {
        if ((Get-Date) -lt $endsAt.AddSeconds(-30) -and -not (HostsBlocked)) { $liftedEarly = $true; break }
        Start-Sleep -Seconds 15
    }
    $liftedAt = Get-Date
    Check "E: the block lifted by itself (hosts marker gone)"       (-not (HostsBlocked))
    Check "E: it did NOT lift early"                                (-not $liftedEarly -and $liftedAt -ge $endsAt.AddSeconds(-30))
    Check "E: the service stopped itself (Stopped or gone)"         ((SvcState) -in @('Stopped', 'gone'))
    Check "E: the notifier is gone"                                 ((Notifiers).Count -eq 0)
    Write-Host ("  lifted at {0} (nominal end {1}, {2}s late)" -f $liftedAt.ToString('HH:mm:ss'), $endsAt.ToString('HH:mm:ss'), [math]::Round(($liftedAt - $endsAt).TotalSeconds))
    Write-Host "  NOTE: the service staying REGISTERED but Stopped is normal - it is not a leftover block."
    & $monk status
    Remove-Item -LiteralPath $state -Force -ErrorAction SilentlyContinue
}

Write-Host "`n================ REBOOT DRILL: $pass passed, $fail failed ================" -ForegroundColor Cyan
"REBOOT_DRILL pass=$pass fail=$fail"
