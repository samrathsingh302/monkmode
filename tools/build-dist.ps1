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

# Builds MonkMode and assembles all components into one folder (dist\),
# which is the layout MonkMode expects at runtime: monkmode.exe (CLI),
# MonkMode_srv.exe (service), mm_notify.exe (notifier) and mm_guard.exe
# (watchdog guardian) all live together, alongside monkmode_settings.ini
# once a block is started.
#
# The four exes find each other by directory (AppContext.BaseDirectory -
# MonkMode\Blocker.vb:109-111, MM_guard\...\Program.vb:218/317), so the single-
# folder layout is a hard runtime contract, not just tidiness.
#
# M2 (F6, 14/08/2026) - THIS SCRIPT USED TO DESTROY LIVE DATA. It wiped the whole
# output folder unconditionally, and dist\ is not a build artefact: it is a live
# install. It holds the enforcement config and its shadow backup, the account setup
# file, the block history, and the hosts/DoH snapshots that are the only record able
# to restore the user's real browser DNS policy at teardown. It is also the binary
# path of a REGISTERED AUTO_START LocalSystem service. This already bit us - the
# 12/08 smoke pre-flight rebuilt dist\, lost monkmode_setup.ini, and every arm
# afterwards refused exit 4. Two guards now stand here:
#
#   1. RUNTIME FILES SURVIVE A REBUILD. They are stashed before the wipe and put
#      back afterwards, in a finally, so a failed publish cannot strand them either.
#      Partner-code trigger files are deliberately NOT preserved: they are one-shot
#      unlock requests consumed within seconds, and dropping one only costs a re-run,
#      while carrying one across a rebuild would replay it.
#
#   2. IT REFUSES TO BUILD OVER A LIVE BLOCK. The service running, a MonkMode marker
#      in the hosts file, an armed slot or an armed schedule in the config: any of
#      them, and the script stops without touching anything. Fail-closed on ambiguity
#      too - a config that is present but unreadable refuses, because the one thing
#      that must never happen is replacing the binaries of a service that is at this
#      moment enforcing. A REGISTERED-but-STOPPED service is fine to build over (that
#      is the normal state between blocks) but says so, because its binary is being
#      replaced underneath it.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\build-dist.ps1
#     Framework-dependent build (needs the .NET 10 desktop runtime on the target
#     machine). Smaller output; the default.
#
#   powershell -ExecutionPolicy Bypass -File tools\build-dist.ps1 -SelfContained
#     Self-contained win-x64 build (bundles the .NET 10 runtime - runs on a machine
#     with NO .NET installed). Larger output; this is the payload tools\install.ps1
#     copies to C:\Program Files\MonkMode\ (slice H1).
#
#   powershell -ExecutionPolicy Bypass -File tools\build-dist.ps1 -DistDir D:\scratch\dist
#     Assemble somewhere else. Same guards apply to whatever folder is named.
#
# Then (from an elevated prompt):  dist\monkmode.exe block --sites reddit.com --for 2h

param(
    # Bundle the .NET 10 runtime into dist\ (self-contained win-x64) so the target
    # machine needs no .NET installed. Off = framework-dependent (the original
    # behaviour), which needs the .NET 10 desktop runtime present.
    [switch]$SelfContained,

    # Where to assemble. Defaults to <repo>\dist - the folder the registered
    # MONKMODE service's binary path points at. Parameterised (M2) so the guards
    # and the preserve-list can be exercised against a scratch folder without
    # going anywhere near the live install.
    [string]$DistDir
)

$ErrorActionPreference = 'Stop'

$serviceName = 'MONKMODE'
$hostsMarker = '#### MonkMode Entries ####'

# The files in the output folder that are RUNTIME STATE, not build output. Same
# set tools\uninstall.ps1 preserves (plus the two snapshots, which it now also
# keeps) and tools\install.ps1 refuses to copy OUT of a payload dir (F72 - because
# THIS folder is the default payload, and these files are exactly what must not
# travel with it): losing any of them loses user data that nothing can reconstruct.
$runtimeFiles = @(
    'monkmode_settings.ini',        # the enforcement config (MAC-covered)
    'monkmode_settings.ini.bak',    # C1b shadow backup the service recovers from
    'monkmode_setup.ini',           # account setup - without it every arm refuses exit 4
    'monkmode_stats',               # block history
    'monkmode_doh.snapshot',        # B5a: the user's REAL browser DoH policy
    'monkmode_hosts.block'          # B2: the hosts self-heal repair source
)

# ============================================================================
# Pure helpers (no machine state) - so the parsing is testable in isolation and
# reads the same way the VB code does.
# ============================================================================

# Read one key out of ini TEXT. Section- and key-insensitive, same shape as
# IniFile's reader. Returns $null when the section or key is absent. Pure.
function Get-IniValue {
    param([string]$Text, [string]$Section, [string]$Key)
    if ([string]::IsNullOrEmpty($Text)) { return $null }
    $inSection = $false
    foreach ($raw in ($Text -split "`r?`n")) {
        $line = $raw.Trim()
        if ($line -eq '' -or $line.StartsWith(';')) { continue }
        if ($line.StartsWith('[') -and $line.EndsWith(']')) {
            $inSection = ($line.Substring(1, $line.Length - 2).Trim() -ieq $Section)
            continue
        }
        if (-not $inSection) { continue }
        $eq = $line.IndexOf('=')
        if ($eq -lt 1) { continue }
        if ($line.Substring(0, $eq).Trim() -ieq $Key) { return $line.Substring($eq + 1).Trim() }
    }
    return $null
}

# The reasons this folder must not be rebuilt over. Returns an array of strings;
# empty means safe. The DECISION is a pure function of the facts the caller reads
# live, so the policy is separable from the machine reads (the ShouldFreshRewrite
# shape); only the message text borrows the two script constants.
#
# Note on coverage: a pre-v1.1 config with no [Slots] section carries its block in
# the ENCRYPTED [Time] Until, which no PowerShell can read. That case is covered by
# the other two signals instead - a live v9 block means the service is running and
# the hosts marker is present - and both of those refuse.
function Get-BuildRefusals {
    param(
        [bool]$ServiceRunning,
        [bool]$HostsHasMarker,
        # $null = no config file at all (a clean folder); '' = present but unreadable.
        [AllowNull()][string]$ConfigText,
        [bool]$ConfigPresent
    )
    $reasons = @()
    if ($ServiceRunning) {
        $reasons += "the $serviceName service is RUNNING - a block is being enforced right now"
    }
    if ($HostsHasMarker) {
        $reasons += "the hosts file still carries the '$hostsMarker' marker - a block's entries are live"
    }
    if ($ConfigPresent) {
        if ([string]::IsNullOrEmpty($ConfigText)) {
            # Fail closed: present but unreadable. We cannot prove nothing is armed.
            $reasons += 'monkmode_settings.ini is present but could not be read - cannot prove nothing is armed'
        } else {
            $slotCount = Get-IniValue $ConfigText 'Slots' 'SlotCount'
            $parsed = 0
            if ($null -ne $slotCount -and [int]::TryParse($slotCount.Trim(), [ref]$parsed) -and $parsed -gt 0) {
                $reasons += "the config has $parsed armed block slot(s)"
            } elseif ($null -ne $slotCount -and -not [int]::TryParse($slotCount.Trim(), [ref]$parsed)) {
                # An unparseable SlotCount is exactly what Blocker.AnySlotArmed's
                # catch treats as ARMED. Same answer here.
                $reasons += "the config's [Slots] SlotCount ('$slotCount') is unreadable - cannot prove nothing is armed"
            }
            $spec = Get-IniValue $ConfigText 'Schedule' 'Spec'
            if (-not [string]::IsNullOrWhiteSpace($spec)) {
                $reasons += 'the config has an armed recurring schedule ([Schedule] Spec is set)'
            }
        }
    }
    return $reasons
}

# ============================================================================
# Live reads + the build
# ============================================================================

$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($DistDir)) { $DistDir = Join-Path $root 'dist' }
$dist = $DistDir

# --- Guard: never build over a live block --------------------------------------
$svc = $null
try { $svc = Get-Service -Name $serviceName -ErrorAction Stop } catch { $svc = $null }
$serviceRunning = ($null -ne $svc) -and
                  ($svc.Status -eq 'Running' -or $svc.Status -eq 'StartPending')

$hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$hostsHasMarker = $false
if (Test-Path -LiteralPath $hostsPath) {
    try {
        # [IO.File]::ReadAllText, not Get-Content: PS 5.1's default encoding
        # mangles UTF-8 and would be a silent false negative here.
        $hostsHasMarker = [System.IO.File]::ReadAllText($hostsPath).Contains($hostsMarker)
    } catch {
        # Cannot read hosts => cannot prove no block is live => refuse.
        $hostsHasMarker = $true
    }
}

$configPath = Join-Path $dist 'monkmode_settings.ini'
$configPresent = Test-Path -LiteralPath $configPath
$configText = $null
if ($configPresent) {
    try { $configText = [System.IO.File]::ReadAllText($configPath) } catch { $configText = '' }
}

$refusals = Get-BuildRefusals -ServiceRunning $serviceRunning `
                              -HostsHasMarker $hostsHasMarker `
                              -ConfigText $configText `
                              -ConfigPresent $configPresent
if ($refusals.Count -gt 0) {
    Write-Host ""
    Write-Host "REFUSING to rebuild '$dist' - a block looks LIVE:" -ForegroundColor Red
    foreach ($r in $refusals) { Write-Host "  - $r" }
    Write-Host ""
    Write-Host "Rebuilding would replace the enforcing service's binaries and destroy the config,"
    Write-Host "snapshots and history the running block depends on. Wait for the block to end (or"
    Write-Host "end it properly with 'monkmode unblock'), then run this again."
    throw "refusing to build over a live block in '$dist'"
}

# Registered but stopped is the normal state between blocks - allowed, but say so.
if ($null -ne $svc) {
    Write-Host "NOTE: the $serviceName service is REGISTERED (status: $($svc.Status)) and its binary in '$dist' is being replaced by this build." -ForegroundColor Yellow
}

# --- The build -----------------------------------------------------------------
# Use a dotnet that actually has an SDK: try PATH first, then the user-scoped
# install (a runtime-only dotnet on PATH, e.g. C:\Program Files\dotnet, can't build).
$candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source,
                (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) |
    Where-Object { $_ -and (Test-Path $_) }
$dotnet = $candidates | Where-Object { (& $_ --list-sdks) -match '^\d+\.' } |
    Select-Object -First 1
if (-not $dotnet) { throw "No dotnet with an SDK installed was found. Install the .NET 10 SDK." }

# Stash the runtime state before the wipe. Restored in the finally below, so a
# publish that throws half way cannot strand it.
$stash = $null
if (Test-Path -LiteralPath $dist) {
    $present = @(Get-ChildItem -LiteralPath $dist -File -Force |
                 Where-Object { $runtimeFiles -contains $_.Name })
    if ($present.Count -gt 0) {
        $stash = Join-Path ([System.IO.Path]::GetTempPath()) ("monkmode-dist-stash-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stash | Out-Null
        foreach ($f in $present) {
            Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $stash $f.Name) -Force
        }
        Write-Host "Preserving runtime state across the rebuild: $(($present | ForEach-Object { $_.Name }) -join ', ')"
    }
}

$projects = @(
    'MonkMode\MonkMode.vbproj',
    'MonkMode_srv\MonkMode_srv\MonkMode_srv.vbproj',
    'MM_notify\MM_notify\MM_notify.vbproj',
    'MM_guard\MM_guard\MM_guard.vbproj'
)

# Common publish args for all four projects. Self-contained adds the win-x64 RID
# and bundles the runtime; all four still land in the SAME $dist folder (they share
# one copy of the runtime DLLs - identical files, so overwriting across the four
# publishes is harmless, and the four exes keep finding each other by directory).
$publishArgs = @('-c', 'Release', '-o', $dist, '--nologo')
if ($SelfContained) {
    $publishArgs += @('-r', 'win-x64', '--self-contained', 'true')
}

# The wipe is INSIDE the guarded region, so every step between stashing and
# restoring is covered: a wipe that fails half way (a locked file) and a publish
# that throws both still put the runtime state back.
try {
    if (Test-Path -LiteralPath $dist) {
        Remove-Item -LiteralPath $dist -Recurse -Force
    }
    foreach ($p in $projects) {
        & $dotnet publish (Join-Path $root $p) @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $p" }
    }
} finally {
    if ($stash) {
        if (-not (Test-Path -LiteralPath $dist)) {
            New-Item -ItemType Directory -Path $dist -Force | Out-Null
        }
        $restored = @()
        foreach ($f in (Get-ChildItem -LiteralPath $stash -File -Force)) {
            Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $dist $f.Name) -Force
            $restored += $f.Name
        }
        Remove-Item -LiteralPath $stash -Recurse -Force
        Write-Host "Restored runtime state: $($restored -join ', ')"
    }
}

Write-Host ""
if ($SelfContained) {
    Write-Host "Deployed (self-contained win-x64) to: $dist"
} else {
    Write-Host "Deployed (framework-dependent) to: $dist"
}
Get-ChildItem $dist -Filter *.exe | Select-Object -ExpandProperty Name
