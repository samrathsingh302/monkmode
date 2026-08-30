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
#   4. Copies that payload to C:\Program Files\MonkMode\ (creating/upgrading in place) -
#      BINARIES ONLY. The six runtime data files (setup, enforcement config + shadow, block
#      history, DoH/hosts snapshots) are never copied out of the payload, so an upgrade keeps
#      the install dir's own and a fresh install starts clean (F72).
#   4a. If -InstallDir pointed somewhere OUTSIDE Program Files, stamps the admin-only write
#      ACL on it by hand (F32) - see "WHY PROGRAM FILES" below for what that ACL is for.
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
#     remove the service first (`sc delete MONKMODE` while idle - ledger 319 removed
#     `monkmode unblock --force`, so an idle machine is the only time it can go),
#     THEN delete C:\Program Files\MonkMode\.
#
# WHY PROGRAM FILES:
#   C:\Program Files is protected by admin-only write ACLs, so a standard (non-elevated)
#   user cannot swap out, delete, or edit the four executables. That raises the tamper
#   bar over a user-writable folder (e.g. a Desktop dist\), where anyone could replace
#   MonkMode_srv.exe with a no-op before the next block arms. It does NOT make MonkMode
#   admin-proof - the honest ceiling (an admin who wants out can always take it) is
#   unchanged; see docs\USER-GUIDE.md and the ARCHITECTURE.md bypass table.
#
#   That ACL is a property of the FOLDER, not of this script, so -InstallDir used to be able
#   to void the whole argument silently (F32): a folder on a data drive inherits
#   BUILTIN\Users : Modify. Step 4a now applies the same admin-only shape by hand to any
#   install dir outside Program Files, and REFUSES the install if it cannot.
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

# F72. The files in an install dir that are RUNTIME STATE, not build output. This is the
# SAME SET tools\build-dist.ps1 preserves across a rebuild ($runtimeFiles, build-dist.ps1:89-96)
# and tools\uninstall.ps1 keeps without -PurgeData ($dataFiles, uninstall.ps1:120-127). Three
# copies of one list is two too many, but the alternative is dot-sourcing one script from
# another across an elevation boundary; keep them in step by hand and cross-reference, which
# is what the other two already do.
$dataFiles = @(
    'monkmode_settings.ini',        # the enforcement config (MAC-covered)
    'monkmode_settings.ini.bak',    # C1b shadow backup the service recovers from
    'monkmode_setup.ini',           # account setup - without it every arm refuses exit 4
    'monkmode_stats',               # block history
    'monkmode_doh.snapshot',        # B5a: the user's REAL browser DoH policy
    'monkmode_hosts.block'          # B2: the hosts self-heal repair source
)

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
           "Let any live block end - at its own end time, or with its partner code - then remove the service ('sc delete $serviceName' while idle; docs\RUNBOOK.md section 3), then re-run this installer. Ledger 319 removed 'monkmode unblock --force': there is no forced teardown.")
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
# overwriting the files in place is an ordinary upgrade. -Force overwrites the BINARIES;
# the install dir's own data files are never touched (F72, below).
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Write-Host "Created $InstallDir"
} else {
    Write-Host "Upgrading in place: $InstallDir already exists."
}

# F72 (22/08/2026) - THIS COPY USED TO DESTROY THE INSTALL'S OWN DATA. It was a flat
# `Copy-Item -Path (Join-Path $PayloadDir '*') -Recurse -Force`, which copies EVERYTHING in
# the payload dir - and the default payload dir is <repo>\dist, which is not a build artefact
# but a live install: build-dist.ps1 deliberately PRESERVES the six data files there across a
# rebuild. So a payload assembled in dist\ carries whatever setup/config/history that folder
# accumulated, and the copy silently restored it over the install dir's own. Measured on the
# 22/08 v1.1 deploy: C:\Program Files\MonkMode\monkmode_setup.ini was replaced by dist\'s
# 20/08 smoke-session copy, so the real account setup was destroyed and `monkmode setup` had
# to be re-run. monkmode_settings.ini survived only because dist\ happened to have none - and
# THAT is the bad case, because restoring a STALE enforcement config over a newer one puts
# the wrong block truth in front of the service. The script prints "Upgrading in place" and
# docs\RUNBOOK.md promises the data is preserved, so this contradicted a stated guarantee.
#
# The installer copies BUILD OUTPUT. Data belongs to the install dir and never travels in a
# payload - in EITHER direction: on an upgrade, skipping protects the install's live data;
# on a fresh install, skipping is what stops a stale smoke-session setup being planted in a
# brand-new folder as if it were the user's own.
#
# Refusing outright when the payload carries data files was considered (it does mean the
# payload was assembled over an install dir) and rejected: the DEFAULT payload dir IS dist\,
# which legitimately carries them by build-dist.ps1's own design, so refusing would break the
# ordinary no-arguments install on any machine that has ever armed a block from dist\.
# Skipping is the same guarantee without the false alarm; the skip is printed, not silent.
#
# Directories still copy with -Recurse (a self-contained payload has satellite resource dirs
# cs\, de\, ja\ ...); only top-level FILES are filtered, which is the only level data ever
# lives at.
Write-Host "Copying payload from $PayloadDir ..."
$skippedData = @()
foreach ($item in (Get-ChildItem -LiteralPath $PayloadDir -Force)) {
    if ((-not $item.PSIsContainer) -and ($dataFiles -contains $item.Name)) {
        $skippedData += $item.Name
        continue
    }
    Copy-Item -LiteralPath $item.FullName -Destination $InstallDir -Recurse -Force
}
if ($skippedData.Count -gt 0) {
    Write-Host ("  SKIPPED data files carried by the payload ($($skippedData -join ', ')) - " +
                "'$InstallDir' keeps its own (F72).") -ForegroundColor Yellow
}

# Re-verify the four exes actually landed.
foreach ($exe in $requiredExes) {
    $p = Join-Path $InstallDir $exe
    if (-not (Test-Path $p)) {
        throw "Copy incomplete: '$exe' did not land in $InstallDir."
    }
}

# ---- 4a. Harden a NON-Program-Files install dir with the admin-only ACL (F32) --
# FX9. "WHY PROGRAM FILES" above is the whole tamper argument: an admin-only write ACL is
# what stops a standard (non-elevated) user swapping MonkMode_srv.exe for a no-op before
# the next block arms. That argument is a property of the FOLDER, not of the script - and
# -InstallDir lets you point the install at, say, D:\Apps\MonkMode, where a freshly created
# folder inherits the data drive's root ACL, which grants BUILTIN\Users : Modify. That put
# the four executables, the MAC'd config, the C1b shadow backup, the hosts snapshot and the
# whole trigger zone inside a non-elevated user's reach while the script still printed the
# ordinary success banner. (The MAC itself is DPAPI-keyed, so the config could never be
# FORGED - but the binaries that read it could simply be replaced.)
#
# So: whenever the effective install dir is NOT already under Program Files, stamp the same
# shape Program Files has - inheritance BROKEN, SYSTEM and Administrators full control,
# Users read+execute and nothing more - and say so out loud.
#
# The Program Files default is untouched by design: it already inherits exactly this and
# re-stamping it would churn an ACL that is right, for no gain.
#
# AFTER the copy, not before, and that is deliberate: Set-Acl writes the directory DACL
# through SetNamedSecurityInfo, which PROPAGATES the new inheritable ACEs down to children
# that were inheriting - so the four exes copied in at step 4 are re-stamped with it, and so
# are the runtime files a previous install left behind. Hardening first would miss those,
# because Copy-Item -Force overwrites a file's contents while leaving its ACL alone.
# (Measured, not assumed: a file inheriting Users:Modify came back Users:ReadAndExecute.)
#
# A FAILURE HERE IS FATAL, unlike the stats ACE below. The stats folder is cosmetic; this
# one IS the tamper model. Silently continuing would hand back precisely the machine F32
# describes - installed, on PATH, and writable by the user it is meant to constrain - so
# the installer stops before the PATH edit and says what to do instead.
$installFull = (Resolve-Path -LiteralPath $InstallDir).ProviderPath

# (Step 5's $normalise below is the same three lines for the PATH comparison. Kept as its
# own local rather than hoisting step 5's, so this fix does not disturb the PATH block.)
$normalisePath = {
    param($x)
    if ($null -eq $x -or $x -eq '') { return '' }
    return $x.Trim().TrimEnd('\').ToLowerInvariant()
}
$installNorm = & $normalisePath $installFull

# All THREE Program Files roots: on a 32-bit PowerShell host $env:ProgramFiles is the (x86)
# folder and ProgramW6432 is the 64-bit one, so checking only the first would "harden" a
# genuine Program Files path (or miss one). Every one of them is admin-write-only.
$underProgramFiles = $false
foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432)) {
    $pfNorm = & $normalisePath $pf
    if ($pfNorm -eq '') { continue }
    if ($installNorm -eq $pfNorm -or $installNorm.StartsWith($pfNorm + '\')) {
        $underProgramFiles = $true
        break
    }
}

if (-not $underProgramFiles) {
    try {
        # Well-known SIDs, not names: "Users"/"Administrators" are localised on a non-English
        # Windows and would fail to resolve there (same reason as the stats ACE below).
        $systemSid = New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
        $adminsSid = New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
        $usersSid = New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)

        # Built from EMPTY rather than from Get-Acl: the inherited Users:Modify ACE is the
        # bug, so the fix has to replace the DACL, not add to it. SetAccessRuleProtection
        # ($true, $false) = stop inheriting AND do not copy what was inherited in.
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $systemSid, 'FullControl', 'ObjectInherit,ContainerInherit', 'None', 'Allow')))
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $adminsSid, 'FullControl', 'ObjectInherit,ContainerInherit', 'None', 'Allow')))
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $usersSid, 'ReadAndExecute', 'ObjectInherit,ContainerInherit', 'None', 'Allow')))
        Set-Acl -Path $installFull -AclObject $acl

        Write-Host "$installFull is outside Program Files, so it inherited a user-writable ACL." -ForegroundColor Yellow
        Write-Host "Hardened it: inheritance broken; SYSTEM + Administrators full control, BUILTIN\Users read/execute only."
        Write-Host "That admin-only write ACL is what stops a non-elevated user replacing MonkMode_srv.exe before the next block arms."
    } catch {
        throw ("Could not apply the admin-only ACL to '$installFull' ($_). " +
               "The payload has been copied but the folder may still be USER-WRITABLE, which voids MonkMode's tamper model " +
               "(a standard user could swap MonkMode_srv.exe for a no-op). The installer has stopped BEFORE the PATH edit. " +
               "Either re-run without -InstallDir to install to the default '$env:ProgramFiles\MonkMode', or set the ACL by hand " +
               "(icacls ""$installFull"" /inheritance:r /grant ""*S-1-5-18:(OI)(CI)F"" ""*S-1-5-32-544:(OI)(CI)F"" ""*S-1-5-32-545:(OI)(CI)RX"") and re-run.")
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
Write-Host "ELEVATED means elevated (F73). monkmode.exe is manifested requireAdministrator, so from a" -ForegroundColor Yellow
Write-Host "NON-elevated prompt Windows shows a UAC prompt and then runs it in a NEW console window that" -ForegroundColor Yellow
Write-Host "closes the instant the command returns. Your prompt gets NO output and exit code 0 - even" -ForegroundColor Yellow
Write-Host "'monkmode status' looks like it did nothing. It is not broken; you just cannot see it. Run" -ForegroundColor Yellow
Write-Host "every monkmode command from a prompt that is ALREADY elevated and the output appears normally." -ForegroundColor Yellow
Write-Host ""
Write-Host "To remove MonkMode later, run tools\uninstall.ps1 (elevated) once no block is active - it is"
Write-Host "fail-closed and refuses while a block is enforcing. Or remove by hand per docs\RUNBOOK.md section 3."
