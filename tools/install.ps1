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

# MonkMode file installer (slice H1). Elevated install of the four executables to
# C:\Program Files\MonkMode\, added to the machine PATH so `monkmode` is on the
# command line from any elevated prompt.
#
# WHAT THIS SCRIPT DOES:
#   1. Refuses unless run elevated (Administrator).
#   2. Refuses if the MONKMODE service already exists (installed OR running) - see
#      "Never upgrade across a block" below.
#   3. Publishes a self-contained win-x64 payload (via build-dist.ps1 -SelfContained),
#      OR copies a pre-built payload you pass with -PayloadDir.
#   4. Copies that payload to C:\Program Files\MonkMode\ (creating/upgrading in place).
#   5. Adds C:\Program Files\MonkMode\ to the MACHINE PATH, idempotently (no duplicate
#      entries on re-run).
#
# WHAT THIS SCRIPT DELIBERATELY DOES *NOT* DO:
#   - It does NOT install, arm, or start the MONKMODE service. That is by design: the
#     first `monkmode block` (or `monkmode schedule`) registers and starts the service
#     itself (MonkMode\Program.vb DoBlock -> ServiceInstaller.InstallAndStart). Installing
#     files is inert until you arm your first block.
#   - It does NOT create Start-menu shortcuts (there is no GUI - MonkMode is a CLI).
#   - It does NOT uninstall anything. Removal is the sibling tools\uninstall.ps1 (slice
#     H2), which is fail-closed and refuses while a block is enforcing (never an escape
#     hatch). Or remove by hand per docs\RUNBOOK.md section 3 (Full-uninstall how-to):
#     remove the service first (`sc delete MONKMODE` while idle, or `monkmode unblock
#     --force`), THEN delete C:\Program Files\MonkMode\.
#
# WHY PROGRAM FILES:
#   C:\Program Files is protected by admin-only write ACLs, so a standard (non-elevated)
#   user cannot swap out, delete, or edit the four executables. That raises the tamper
#   bar over a user-writable folder (e.g. a Desktop dist\), where anyone could replace
#   MonkMode_srv.exe with a no-op before the next block arms. It does NOT make MonkMode
#   admin-proof - the honest ceiling (an admin who wants out can always take it) is
#   unchanged; see docs\USER-GUIDE.md and the ARCHITECTURE.md bypass table.
#
# WHY REFUSE WHILE THE SERVICE EXISTS (R9 forward-migration freeze):
#   The enforcement config (monkmode_settings.ini) carries a compile-time schema version
#   as its first MAC-covered line. A block armed under the OLD binaries fails the MAC
#   under NEW ones and freezes fail-closed (keeps enforcing, won't auto-lift). So we never
#   overwrite the binaries while a MONKMODE service registration exists - that registration
#   may be an active block, or an idle-but-installed leftover of one. Let any live block end,
#   remove the service (RUNBOOK section 3), THEN upgrade. A clean re-run over a machine with
#   no MONKMODE service upgrades in place safely.
#
# INSTALL DIR AND THE SERVICE BINPATH:
#   The service binPath is derived from the CLI's own folder at arm time:
#   Path.Combine(Blocker.AppDir(), Blocker.ServiceExeName) (MonkMode\Program.vb:295), where
#   AppDir() = AppContext.BaseDirectory (MonkMode\Blocker.vb:109-111). CreateService is given
#   that path QUOTED (MonkMode\ServiceTools.vb:218), so a Program Files path with a space is
#   handled correctly. Installing to any fixed folder therefore works - the service, guardian
#   and notifier all resolve each other relative to that same folder - so we pick the
#   admin-ACL'd Program Files location.
#
# Usage (from an ELEVATED PowerShell prompt):
#   powershell -ExecutionPolicy Bypass -File tools\install.ps1
#   powershell -ExecutionPolicy Bypass -File tools\install.ps1 -PayloadDir C:\path\to\prebuilt
#   powershell -ExecutionPolicy Bypass -File tools\install.ps1 -InstallDir "D:\Apps\MonkMode"

param(
    # Where to install. Defaults to the admin-ACL'd Program Files folder.
    [string]$InstallDir = 'C:\Program Files\MonkMode',

    # A pre-built payload folder holding the four exes. When omitted, the script
    # publishes a fresh self-contained payload via build-dist.ps1 -SelfContained.
    [string]$PayloadDir = ''
)

$ErrorActionPreference = 'Stop'

# The four executables that MUST all be present in one folder (the runtime contract).
$requiredExes = @('monkmode.exe', 'MonkMode_srv.exe', 'mm_notify.exe', 'mm_guard.exe')

$serviceName = 'MONKMODE'

# ---- 1. Elevation self-check -------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw "install.ps1 must be run from an ELEVATED (Administrator) prompt: it writes to '$InstallDir' and edits the machine PATH."
}

# ---- 2. Refuse while the MONKMODE service exists (R9 freeze) ------------------
# Never upgrade the binaries across an armed block. An existing service registration
# may be a live block OR the idle-but-installed leftover of one; either way, overwriting
# the exes could freeze a MAC'd config fail-closed. Get-Service throws when the service
# is absent, which is the case we WANT (install proceeds).
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "The $serviceName service is present (status: $($existing.Status))." -ForegroundColor Yellow
    throw ("Refusing to install over an existing $serviceName service (never upgrade across a block - R9 forward-migration freeze). " +
           "Let any live block end, then remove the service (docs\RUNBOOK.md section 3: 'sc delete $serviceName' while idle, or 'monkmode unblock --force'), then re-run this installer.")
}

# ---- 3. Resolve the payload (publish, or use a pre-built folder) -------------
$repoRoot = Split-Path $PSScriptRoot -Parent

if ($PayloadDir -eq '') {
    Write-Host "No -PayloadDir given; publishing a self-contained win-x64 payload ..."
    & (Join-Path $PSScriptRoot 'build-dist.ps1') -SelfContained
    if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) { throw "build-dist.ps1 -SelfContained failed." }
    $PayloadDir = Join-Path $repoRoot 'dist'
}

if (-not (Test-Path $PayloadDir)) {
    throw "Payload folder not found: $PayloadDir"
}

# Validate the payload holds all four exes before we touch Program Files - a partial
# payload (e.g. missing mm_guard.exe) is the classic cause of a half-broken block.
foreach ($exe in $requiredExes) {
    $p = Join-Path $PayloadDir $exe
    if (-not (Test-Path $p)) {
        throw "Payload is incomplete: '$exe' is missing from $PayloadDir. All four executables must be present."
    }
}

# ---- 4. Copy the payload to the install dir (create / upgrade in place) -------
# Safe re-run: the service-exists gate above guarantees no live/idle block here, so
# overwriting the files in place is an ordinary upgrade. -Force overwrites; existing
# runtime files not in the payload (there should be none) are left as-is.
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Write-Host "Created $InstallDir"
} else {
    Write-Host "Upgrading in place: $InstallDir already exists."
}

Write-Host "Copying payload from $PayloadDir ..."
Copy-Item -Path (Join-Path $PayloadDir '*') -Destination $InstallDir -Recurse -Force

# Re-verify the four exes actually landed.
foreach ($exe in $requiredExes) {
    $p = Join-Path $InstallDir $exe
    if (-not (Test-Path $p)) {
        throw "Copy incomplete: '$exe' did not land in $InstallDir."
    }
}

# ---- 4b. Create the stats-sidecar directory with a Users:Modify ACE (P49) ----
# v1.1 S7b. MonkMode keeps its counters (apps closed, browser nudges, armed-seconds
# day-log, streaks) in %ProgramData%\MonkMode\ - NOT beside the executables, because
# Program Files is admin-write-only by the deliberate ACL described under "WHY PROGRAM
# FILES" above and the notifier that records browser nudges runs NON-elevated.
#
# A directory created under ProgramData inherits an ACL under which ordinary users can
# read but not write, so an explicit BUILTIN\Users : Modify ACE is what makes the
# non-elevated notifier able to write its own file. Granted here, once, by the elevated
# installer; the LocalSystem service applies the identical ACE at runtime if the folder
# is ever absent (StatsSidecar.EnsureDirFor).
#
# THIS GRANTS NOTHING THAT MATTERS TO ENFORCEMENT. No enforcement path ever reads these
# files: they are numbers on a screen. A user who deletes, forges or rewrites them
# changes what `monkmode stats` prints and nothing else - no block lifts, shortens or
# moves. The four executables and the MAC'd config stay in admin-only Program Files.
#
# Idempotent and non-fatal: an existing directory is left in place (its history is USER
# DATA - no-data-loss), and a failure to set the ACE is a warning, not a throw, since a
# missing counter must never abort an install.
$statsDir = Join-Path $env:ProgramData 'MonkMode'
if (-not (Test-Path $statsDir)) {
    New-Item -ItemType Directory -Path $statsDir -Force | Out-Null
    Write-Host "Created $statsDir (MonkMode stats)."
} else {
    Write-Host "$statsDir already exists - leaving it and its counter history alone."
}
try {
    # The well-known SID, not the name "Users": the name is localised on a non-English
    # Windows and would fail to resolve there.
    $usersSid = New-Object Security.Principal.SecurityIdentifier(
        [Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)
    $acl = Get-Acl -Path $statsDir
    $rule = New-Object Security.AccessControl.FileSystemAccessRule(
        $usersSid, 'Modify', 'ObjectInherit,ContainerInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -Path $statsDir -AclObject $acl
    Write-Host "Granted BUILTIN\Users : Modify on $statsDir (the notifier is not elevated)."
} catch {
    Write-Host "WARNING: could not set the Users:Modify ACE on $statsDir - MonkMode will still block; only the counters may not record. ($_)" -ForegroundColor Yellow
}

# ---- 5. Add the install dir to the MACHINE PATH, idempotently ----------------
# Read the current machine PATH, split into entries, and only append if the install dir
# is not already there (case-insensitive, trailing-backslash-insensitive) - so re-running
# the installer never grows the PATH with duplicates.
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if ($null -eq $machinePath) { $machinePath = '' }

$normalise = {
    param($x)
    if ($null -eq $x) { return '' }
    return $x.Trim().TrimEnd('\').ToLowerInvariant()
}
$targetNorm = & $normalise $InstallDir

$already = $false
foreach ($entry in ($machinePath -split ';')) {
    if ($entry.Trim() -eq '') { continue }
    if ((& $normalise $entry) -eq $targetNorm) { $already = $true; break }
}

if ($already) {
    Write-Host "PATH already contains $InstallDir - leaving it unchanged."
} else {
    if ($machinePath -eq '') {
        $newPath = $InstallDir
    } elseif ($machinePath.EndsWith(';')) {
        $newPath = "$machinePath$InstallDir"
    } else {
        $newPath = "$machinePath;$InstallDir"
    }
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'Machine')
    Write-Host "Added $InstallDir to the machine PATH."
    Write-Host "NOTE: open a NEW elevated prompt for the updated PATH to take effect."
}

# ---- Done --------------------------------------------------------------------
Write-Host ""
Write-Host "MonkMode installed to $InstallDir." -ForegroundColor Green
Write-Host "The MONKMODE service is NOT installed yet - your first 'monkmode block' (or 'monkmode schedule') registers and starts it."
Write-Host ""
Write-Host "Next steps (from an elevated prompt; open a new one so PATH is picked up):"
Write-Host "  monkmode setup --partner ""Alex (alex@example.com)"""
Write-Host "  monkmode block --sites reddit.com --for 2h"
Write-Host ""
Write-Host "To remove MonkMode later, run tools\uninstall.ps1 (elevated) once no block is active - it is"
Write-Host "fail-closed and refuses while a block is enforcing. Or remove by hand per docs\RUNBOOK.md section 3."
