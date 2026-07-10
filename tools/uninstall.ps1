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

# MonkMode file uninstaller (slice H2). The sibling of tools\install.ps1: it removes
# the four executables from C:\Program Files\MonkMode\, the machine PATH entry, and the
# current user's notifier autorun. It is deliberately WEAKER than the R1 exits, NOT an
# alternative to them.
#
# THE ONE RULE: this uninstaller is NOT an escape hatch. If a block is enforcing it
# REFUSES and routes you to the intended exits (wait for the timer / cooling-off /
# the partner code) - see docs\USER-GUIDE.md section 6. It NEVER calls
# 'monkmode unblock --force', NEVER stops or deletes a RUNNING service, and NEVER
# edits the hosts file. To remove MonkMode DURING an active block, use the documented
# escape hatch 'monkmode unblock --force' (docs\RUNBOOK.md section 3.2) FIRST, then
# run this uninstaller on the resulting idle machine.
#
# DETECTION IS FAIL-CLOSED. The script proceeds ONLY when it can positively establish
# that nothing is enforcing. Any ambiguity refuses with diagnosis pointers. It reads
# three independent signals, grounded in docs\RUNBOOK.md section 2:
#   1. Service state (Get-Service MONKMODE). RUNNING = a block is active, a schedule
#      window is open, or the service heartbeat is alive => REFUSE. STOPPED-but-present
#      is the genuinely-expired idle leftover (RUNBOOK 2.1: at a real expiry the service
#      strips hosts and stops itself but its registration stays installed) => removable.
#      Absent => nothing is enforced by the service.
#   2. Hosts marker block '#### MonkMode Entries ####' (RUNBOOK 2.3). Present => sites are
#      being sinkholed (or a stuck/orphaned block outlived a dead service) => REFUSE. At a
#      genuine expiry this marker is already gone, so the idle leftover passes this signal.
#   3. Schedule spec in monkmode_settings.ini ([Schedule] Spec, stored plaintext -
#      MonkMode\Blocker.vb:740). A configured recurring schedule normally keeps the service
#      RUNNING (Blocker.vb:77), so signal 1 usually catches it; this is the belt-and-braces
#      for a lingering spec whose service is down. REFUSE by default so a scheduled re-arm
#      is never silently orphaned; pass -IgnoreSchedule to remove anyway (the recurring
#      schedule is then abandoned - clear it first with 'monkmode schedule --clear').
#
# WHAT IT REMOVES when clear to proceed (idle machine):
#   - The MONKMODE service registration IF it is present and STOPPED ('sc delete' on a
#     stopped service only - the idle-leftover case, RUNBOOK 3.1). A running service is
#     never touched (signal 1 would have refused).
#   - The install dir (C:\Program Files\MonkMode by default, or -InstallDir).
#   - The MonkMode entry on the MACHINE PATH (idempotent, same normaliser as install.ps1).
#   - The CURRENT user's notifier autorun (HKCU Run value 'MonkMode_notify',
#     MonkMode\Blocker.vb:84). PER-USER LIMITATION: this only clears the account that runs
#     the uninstaller; if the block was armed under a different Windows account, that
#     account keeps its own stale Run value (the same B9 per-user boundary the notifier has).
#
# DATA, KEPT BY DEFAULT (honest no-data-loss default). By default your account data
# SURVIVES so a reinstall keeps it - mirroring what 'unblock --force' verifiably leaves
# behind (docs\RUNBOOK.md 3.3): monkmode_setup.ini (partner + defaults), monkmode_stats
# (block history), and monkmode_settings.ini(.bak) (stale enforcement config, overwritten
# by the next block). These are preserved in the install dir and the dir is kept. Pass
# -PurgeData to delete them too and remove the dir entirely - a truly clean slate.
#
# SELF-PROTECTION. Refuses to delete a dir that looks like a source/working tree rather
# than an installed copy: refuses if -InstallDir contains a .git folder or MonkMode.sln,
# or if it resolves inside this repo (so pointing it at the repo or its dist\ is refused).
#
# Usage (from an ELEVATED PowerShell prompt):
#   powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1
#   powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1 -PurgeData
#   powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1 -InstallDir "D:\Apps\MonkMode"
#   powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1 -IgnoreSchedule

param(
    # The install dir to remove. Defaults to the same Program Files folder install.ps1 uses.
    [string]$InstallDir = 'C:\Program Files\MonkMode',

    # Also delete the account/history data (monkmode_setup.ini, monkmode_stats,
    # monkmode_settings.ini[.bak]) and remove the dir entirely. Default = keep data.
    [switch]$PurgeData,

    # Proceed even when a recurring schedule spec lingers in the config (it will be
    # orphaned). Without this, a lingering schedule spec refuses. Never overrides an
    # ACTIVE block or an open window - those are gated by the service-state signal.
    [switch]$IgnoreSchedule
)

$ErrorActionPreference = 'Stop'

$serviceName = 'MONKMODE'
$hostsPath   = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$hostsMarker = '#### MonkMode Entries ####'
$runValue    = 'MonkMode_notify'
$runKey      = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run'

# The account/history data files (all in the install dir, next to the exes - RUNBOOK 2.7).
# Kept by default; removed only with -PurgeData.
$dataFiles = @(
    'monkmode_settings.ini',
    'monkmode_settings.ini.bak',
    'monkmode_setup.ini',
    'monkmode_stats'
)

# ============================================================================
# Pure helpers (no machine state) - unit-testable in isolation. Kept above the
# live logic so the imperative body reads top-to-bottom.
# ============================================================================

# Remove the install dir from a PATH string, idempotently. Case- and
# trailing-backslash-insensitive, same normalisation as install.ps1. Returns the new
# PATH string (never mutates the machine). Pure function of its inputs.
function Remove-PathEntry {
    param([string]$PathValue, [string]$Target)
    if ($null -eq $PathValue) { $PathValue = '' }
    $normalise = {
        param($x)
        if ($null -eq $x) { return '' }
        return $x.Trim().TrimEnd('\').ToLowerInvariant()
    }
    $targetNorm = & $normalise $Target
    $kept = @()
    foreach ($entry in ($PathValue -split ';')) {
        if ($entry.Trim() -eq '') { continue }          # drop empties (also collapses ';;')
        if ((& $normalise $entry) -eq $targetNorm) { continue }  # drop the install dir
        $kept += $entry
    }
    return ($kept -join ';')
}

# Is the target dir actually an entry on this PATH string? Same normalisation as
# Remove-PathEntry. Pure. Used to gate the machine-PATH write so we never rewrite PATH
# (which would collapse unrelated empty entries) when MonkMode was never on it.
function Test-PathHasEntry {
    param([string]$PathValue, [string]$Target)
    if ($null -eq $PathValue) { return $false }
    $normalise = {
        param($x)
        if ($null -eq $x) { return '' }
        return $x.Trim().TrimEnd('\').ToLowerInvariant()
    }
    $targetNorm = & $normalise $Target
    foreach ($entry in ($PathValue -split ';')) {
        if ($entry.Trim() -eq '') { continue }
        if ((& $normalise $entry) -eq $targetNorm) { return $true }
    }
    return $false
}

# Does the hosts text carry the MonkMode marker block? (RUNBOOK 2.3.) Pure.
function Test-HostsMarker {
    param([string]$HostsText)
    if ($null -eq $HostsText) { return $false }
    return $HostsText.Contains($hostsMarker)
}

# Does the config text carry a non-empty [Schedule] Spec? (Blocker.vb:740 - stored
# plaintext.) A conservative textual read: we cannot check the MAC from PowerShell, so a
# lingering spec is treated as "schedule configured" and refuses (fail-closed - a tampered
# config that froze fail-closed keeps the service RUNNING and is caught by signal 1
# regardless). Pure function of the ini text.
function Test-ScheduleArmed {
    param([string]$IniText)
    if ([string]::IsNullOrEmpty($IniText)) { return $false }
    $inSchedule = $false
    foreach ($raw in ($IniText -split "`n")) {
        $line = $raw.Trim()
        if ($line -eq '') { continue }
        if ($line -like '`[*`]') {                       # a section header
            $inSchedule = ($line -ieq '[Schedule]')
            continue
        }
        if ($inSchedule -and ($line -imatch '^Spec\s*=\s*(.+)$')) {
            if ($matches[1].Trim() -ne '') { return $true }
        }
    }
    return $false
}

# The fail-closed decision. Given the three signals, return a decision object with a
# boolean .Proceed and a .Reason string. Pure: no side effects, no live queries.
#   $serviceRunning - MONKMODE service exists AND is Running.
#   $hostsMarker    - the marker block is present in hosts.
#   $scheduleArmed  - a non-empty [Schedule] Spec is present in the config.
#   $ignoreSchedule - the -IgnoreSchedule switch was passed.
function Get-UninstallDecision {
    param(
        [bool]$ServiceRunning,
        [bool]$HostsMarker,
        [bool]$ScheduleArmed,
        [bool]$IgnoreSchedule
    )
    if ($ServiceRunning) {
        return [pscustomobject]@{ Proceed = $false; Reason =
            "The $serviceName service is RUNNING - a block is active, a schedule window is open, or the service heartbeat is alive. This uninstaller is NOT an escape hatch and will not stop it. End the block through an R1 exit first (wait for the timer / 'monkmode unblock' cooling-off / 'monkmode unblock --code <CODE>' - see docs\USER-GUIDE.md section 6), or use the documented escape hatch 'monkmode unblock --force' (docs\RUNBOOK.md section 3.2). Then re-run this uninstaller." }
    }
    if ($HostsMarker) {
        return [pscustomobject]@{ Proceed = $false; Reason =
            "The hosts marker '$hostsMarker' is still present but the service is not running - an orphaned or stuck block (docs\RUNBOOK.md 2.3 / 3.2). Refusing rather than editing hosts. Run 'monkmode unblock --force' to tear it down cleanly, then re-run this uninstaller." }
    }
    if ($ScheduleArmed -and -not $IgnoreSchedule) {
        return [pscustomobject]@{ Proceed = $false; Reason =
            "A recurring schedule is still configured ([Schedule] Spec in monkmode_settings.ini). Removing MonkMode now would orphan it. Clear it first with 'monkmode schedule --clear', or re-run with -IgnoreSchedule to remove anyway (the schedule is then abandoned)." }
    }
    return [pscustomobject]@{ Proceed = $true; Reason = 'No block is enforcing (service not running, no hosts marker, no live schedule).' }
}

# ============================================================================
# Live logic
# ============================================================================

# ---- 1. Elevation self-check (mirrors install.ps1) ---------------------------
$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw "uninstall.ps1 must be run from an ELEVATED (Administrator) prompt: it deletes the service (if idle), removes '$InstallDir', and edits the machine PATH."
}

# ---- 2. Self-protection: never delete a source / working tree ----------------
$repoRoot = Split-Path $PSScriptRoot -Parent
$resolvedInstall = $null
if (Test-Path $InstallDir) {
    $resolvedInstall = (Resolve-Path $InstallDir).Path
    $resolvedRepo = (Resolve-Path $repoRoot).Path
    $ri = $resolvedInstall.TrimEnd('\').ToLowerInvariant()
    $rr = $resolvedRepo.TrimEnd('\').ToLowerInvariant()
    # Equal-or-child, with a separator boundary so 'C:\repos\monk-mode-other' does NOT
    # match repo 'C:\repos\monk-mode'.
    if (($ri -eq $rr) -or $ri.StartsWith($rr + '\')) {
        throw "Refusing to uninstall from '$resolvedInstall': it is inside this repo ($resolvedRepo). This uninstaller targets an INSTALLED copy (Program Files), not a source or dist\ working tree."
    }
    if ((Test-Path (Join-Path $resolvedInstall '.git')) -or (Test-Path (Join-Path $resolvedInstall 'MonkMode.sln'))) {
        throw "Refusing to uninstall from '$resolvedInstall': it looks like a source/working tree (contains .git or MonkMode.sln), not an installed copy."
    }
}

# ---- 3. Read the three fail-closed signals (all read-only) -------------------
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$serviceRunning = ($svc -ne $null) -and ($svc.Status -eq 'Running')

$hostsText = ''
# Fail-closed: read the hosts file WITHOUT swallowing errors. A present-but-unreadable
# hosts is exactly the ambiguity we must refuse on, so a read failure throws (via
# $ErrorActionPreference='Stop') rather than silently reading as "no marker".
if (Test-Path $hostsPath) { $hostsText = Get-Content -Path $hostsPath -Raw }
$hostsMarkerPresent = Test-HostsMarker $hostsText

$iniPath = Join-Path $InstallDir 'monkmode_settings.ini'
$iniText = ''
if (Test-Path $iniPath) { $iniText = Get-Content -Path $iniPath -Raw }
$scheduleArmed = Test-ScheduleArmed $iniText

Write-Host "MonkMode uninstaller - state read (read-only):"
Write-Host "  Service:  $(if ($null -eq $svc) { 'absent' } else { $svc.Status })"
Write-Host "  Hosts:    $(if ($hostsMarkerPresent) { 'MonkMode marker PRESENT' } else { 'no MonkMode marker' })"
Write-Host "  Schedule: $(if ($scheduleArmed) { 'spec present in config' } else { 'none' })"
Write-Host ""

# ---- 4. Fail-closed decision -------------------------------------------------
$decision = Get-UninstallDecision -ServiceRunning $serviceRunning -HostsMarker $hostsMarkerPresent `
                                  -ScheduleArmed $scheduleArmed -IgnoreSchedule:$IgnoreSchedule.IsPresent
if (-not $decision.Proceed) {
    throw "REFUSING to uninstall. $($decision.Reason)"
}
Write-Host "$($decision.Reason) Proceeding." -ForegroundColor Green
Write-Host ""

# ---- 5. Delete the service registration IF present and STOPPED ----------------
# 'sc delete' on a STOPPED service only - the idle-leftover case (RUNBOOK 3.1). We never
# reach here with a Running service (step 4 would have refused). If the service is absent
# there is nothing to delete (a files-only install that never armed).
if ($null -ne $svc) {
    if ($svc.Status -eq 'Stopped') {
        Write-Host "Deleting the idle $serviceName service registration ..."
        & sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "'sc delete $serviceName' failed (exit $LASTEXITCODE). The service may hold a deny-DELETE ACE (an active block - B6); if so it is not idle. See docs\RUNBOOK.md section 3."
        }
        Write-Host "Service registration removed."
    } else {
        # Belt-and-braces: any non-Stopped, non-Running state (StartPending, etc.) is ambiguous.
        throw "The $serviceName service is in state '$($svc.Status)', not Stopped - refusing to delete it. Re-check with 'monkmode status' / 'sc query $serviceName'."
    }
} else {
    Write-Host "No $serviceName service registration to remove."
}
Write-Host ""

# ---- 6. Remove the notifier autorun for the CURRENT user (HKCU Run) -----------
# Per-user only: clears the account running this uninstaller. A block armed under a
# different Windows account keeps its own stale Run value (the B9 per-user boundary).
$rk = Get-Item -Path ("HKCU:\" + $runKey) -ErrorAction SilentlyContinue
if ($rk -and ($null -ne $rk.GetValue($runValue, $null))) {
    Remove-ItemProperty -Path ("HKCU:\" + $runKey) -Name $runValue -Force
    Write-Host "Removed the notifier autorun (HKCU Run '$runValue') for the current user."
    Write-Host "  NOTE: if MonkMode was armed under a DIFFERENT Windows account, that account keeps its own '$runValue' entry - remove it while logged in as that user."
} else {
    Write-Host "No notifier autorun ('$runValue') under the current user's HKCU Run key."
}
Write-Host ""

# ---- 7. Remove the MonkMode entry from the MACHINE PATH -----------------------
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if ($null -eq $machinePath) { $machinePath = '' }
if (Test-PathHasEntry $machinePath $InstallDir) {
    $newPath = Remove-PathEntry $machinePath $InstallDir
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'Machine')
    Write-Host "Removed $InstallDir from the machine PATH."
    Write-Host "  NOTE: open a NEW prompt for the updated PATH to take effect."
} else {
    Write-Host "$InstallDir was not on the machine PATH - leaving PATH unchanged."
}
Write-Host ""

# ---- 8. Remove the install dir (keeping data unless -PurgeData) ---------------
if ($null -eq $resolvedInstall) {
    Write-Host "Install dir '$InstallDir' does not exist - nothing to remove there."
} elseif ($PurgeData) {
    # -PurgeData: delete everything, data files included, and remove the dir entirely.
    Remove-Item -Path $resolvedInstall -Recurse -Force
    Write-Host "Removed the install dir and ALL data ('$resolvedInstall') - clean slate (-PurgeData)."
} else {
    # Default: remove the payload (binaries + runtime + transient snapshots/triggers) but
    # PRESERVE the account/history data files. Delete everything in the dir EXCEPT the
    # known data files; if any data remain, keep the dir and say where the data is.
    $preserved = @()
    Get-ChildItem -Path $resolvedInstall -Force | ForEach-Object {
        if ($_.PSIsContainer) {
            Remove-Item -Path $_.FullName -Recurse -Force
        } elseif ($dataFiles -contains $_.Name) {
            $preserved += $_.Name
        } else {
            Remove-Item -Path $_.FullName -Force
        }
    }
    if ($preserved.Count -gt 0) {
        Write-Host "Removed the MonkMode binaries from '$resolvedInstall'."
        Write-Host "  KEPT your data ($($preserved -join ', ')) so a reinstall keeps your setup and history."
        Write-Host "  Re-run with -PurgeData to delete these too and remove the folder."
    } else {
        Remove-Item -Path $resolvedInstall -Recurse -Force
        Write-Host "Removed the install dir '$resolvedInstall' (no data files were present to keep)."
    }
}

# ---- Done --------------------------------------------------------------------
Write-Host ""
Write-Host "MonkMode uninstalled." -ForegroundColor Green
Write-Host "Verify: 'sc query $serviceName' should report the service does not exist, and the hosts file should have no '$hostsMarker' marker."
