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

# MonkMode F2 (URL watcher) probe - NON-ELEVATED, ARMS NOTHING, ~20 seconds.
#
# The F2b watcher reads the foreground browser's address bar through UI Automation and, on a
# hit, redirects it. Two things about that are machine facts rather than code facts, and this
# script is how they are checked WITHOUT arming a block:
#
#   1. does the Chromium omnibox actually expose itself the way the watcher expects
#      (ControlType=Edit, AutomationId "view_1012", ClassName "OmniboxViewViews"), in each of
#      Chrome, Edge and Brave; and
#   2. does ValuePattern.Value actually hand back the ACTIVE tab's URL - and in what shape
#      (Chromium hides "https://" and "www." in the DISPLAYED text when the box is unfocused,
#      and it was an open question whether the UIA value is elided too).
#
# WHAT IT WILL NOT DO. It never starts a browser, never starts mm_notify.exe or any other
# MonkMode executable, never arms or ends a block, and never touches the hosts file, the
# registry or the SCM. Against the browsers it is STRICTLY READ-ONLY: it walks the UIA tree and
# reads properties. It never calls SetValue, never sends a key, never navigates or closes a
# window. Safe to run against Samrath's own live browsing session (it will PRINT whatever URL
# the address bar currently shows, which is the only privacy note worth making).
#
# It reads each browser's MAIN WINDOW rather than the foreground window, deliberately: while
# this script runs, the foreground window is the terminal it is running in, so a
# foreground-only probe could only ever see one browser per run. The watcher itself is
# foreground-only (background windows and background tabs are its documented residual); the
# element it finds is the same element either way.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\smoke\f2-url-smoke.ps1
#   powershell -ExecutionPolicy Bypass -File tools\smoke\f2-url-smoke.ps1 -Browser chrome
#   powershell -ExecutionPolicy Bypass -File tools\smoke\f2-url-smoke.ps1 -SkipTests

param(
    # Which browsers to probe. A browser that is not running is reported, not failed - the
    # probe cannot start one.
    [ValidateSet('all', 'chrome', 'edge', 'brave')]
    [string]$Browser = 'all',

    # The built output folder. Default: <repo>\dist, derived from this script's location.
    [string]$Dist,

    # Skip step (c), the URL unit tests. For a probe-only re-run.
    [switch]$SkipTests
)

$ErrorActionPreference = 'Continue'

if (-not $Dist) {
    if ($PSScriptRoot) { $Dist = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'dist' }
    else               { $Dist = 'C:\Users\samra\repos\monk-mode\dist' }
}
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# P54's process names, and the display names for the report. Keep in step with
# UrlWatch.BrowserProcessNames (MM_notify\MM_notify\UrlWatch.vb).
$browsers = @(
    @{ Key = 'chrome'; Proc = 'chrome';  Name = 'Chrome' },
    @{ Key = 'edge';   Proc = 'msedge';  Name = 'Edge'   },
    @{ Key = 'brave';  Proc = 'brave';   Name = 'Brave'  }
)
if ($Browser -ne 'all') { $browsers = $browsers | Where-Object { $_.Key -eq $Browser } }

# The two identifiers the watcher looks the omnibox up by (UrlWatch.FindOmnibox). Measured
# 18/08/2026 on daddykins: Brave reports AutomationId view_1012 + ClassName
# BraveOmniboxViewViews, Edge reports view_1021 + OmniboxViewViews. Neither identifier alone
# covers both, which is why the watcher tries the id first and then a ClassName SUFFIX match.
$omniboxAutomationId   = 'view_1012'
$omniboxClassNameSuffix = 'OmniboxViewViews'

$results = @()   # one row per browser, for the summary
$failures = @()

function Write-Head([string]$text) {
    Write-Host ""
    Write-Host $text -ForegroundColor Cyan
    Write-Host ("-" * $text.Length) -ForegroundColor Cyan
}

# ============================================================================
# (a) the built notifier is present
# ============================================================================

Write-Head "(a) dist payload"
$notify = Join-Path $Dist 'mm_notify.exe'
if (Test-Path -LiteralPath $notify) {
    Write-Host "  OK   $notify"
    # Not executed - only stamped, so the report says WHICH build the probe accompanied.
    $stamp = (Get-Item -LiteralPath $notify).LastWriteTime
    Write-Host "       built $stamp"
} else {
    Write-Host "  FAIL $notify is missing - run tools\build-dist.ps1 first" -ForegroundColor Red
    $failures += 'dist\mm_notify.exe missing'
}

# ============================================================================
# (b) the UIA probe
# ============================================================================

Write-Head "(b) omnibox probe"

$uiaLoaded = $true
try {
    Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
    Add-Type -AssemblyName UIAutomationTypes  -ErrorAction Stop
} catch {
    $uiaLoaded = $false
    Write-Host "  FAIL could not load UIAutomationClient/UIAutomationTypes: $($_.Exception.Message)" -ForegroundColor Red
    $failures += 'UIAutomation assemblies would not load'
}

if (-not ([System.Management.Automation.PSTypeName]'MonkModeProbeNative').Type) {
    Add-Type -Namespace '' -Name 'MonkModeProbeNative' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr GetForegroundWindow();
'@
}

# The address-bar element inside one window handle, by the same two ways in that
# UrlWatch.FindOmnibox uses, in the same order - PLUS a third, DIAGNOSTIC-ONLY fallback the
# watcher deliberately does not have (see FindOmnibox: typing into an unidentified Edit control
# would be worse than not nudging). A 'first Edit' result here therefore means the watcher
# would find NOTHING on that browser, and the summary counts it as a failure.
# Returns a hashtable describing which way hit.
function Find-Omnibox([System.IntPtr]$hwnd) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    if ($null -eq $root) { return $null }
    $isEdit = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)

    # 1. by AutomationId.
    try {
        $byId = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.AndCondition($isEdit,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $omniboxAutomationId)))))
        if ($null -ne $byId) { return @{ Element = $byId; How = 'AutomationId'; Positive = $true } }
    } catch {
        # Same swallow the watcher does: try the next way in.
    }

    # 2. by ClassName SUFFIX (no PropertyCondition can express that, so enumerate), and
    # 3. failing that, the first Edit control, for diagnosis only.
    $edits = $null
    try { $edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $isEdit) } catch { return $null }
    if ($null -eq $edits -or $edits.Count -eq 0) { return $null }
    foreach ($el in $edits) {
        try {
            if ($el.Current.ClassName -and $el.Current.ClassName.EndsWith($omniboxClassNameSuffix, 'OrdinalIgnoreCase')) {
                return @{ Element = $el; How = 'ClassName suffix'; Positive = $true }
            }
        } catch { }
    }
    return @{ Element = $edits[0]; How = 'first Edit (DIAGNOSTIC ONLY - the watcher would give up here)'; Positive = $false }
}

if ($uiaLoaded) {
    $fg = [MonkModeProbeNative]::GetForegroundWindow()
    foreach ($b in $browsers) {
        $row = @{ Name = $b.Name; State = 'not running'; How = ''; AutomationId = ''; ClassName = '';
                  ControlType = ''; ElementName = ''; Value = ''; Elided = '' }
        $procs = @(Get-Process -Name $b.Proc -ErrorAction SilentlyContinue |
                   Where-Object { $_.MainWindowHandle -ne 0 })
        if ($procs.Count -eq 0) {
            Write-Host ("  {0,-6} not running (or no top-level window) - nothing to probe" -f $b.Name) -ForegroundColor Yellow
        } else {
            $p = $procs[0]
            $row.State = 'no omnibox found'
            $hit = $null
            try { $hit = Find-Omnibox $p.MainWindowHandle } catch {
                Write-Host ("  {0,-6} UIA walk threw: {1}" -f $b.Name, $_.Exception.Message) -ForegroundColor Red
                $row.State = 'UIA walk threw'
            }
            if ($null -ne $hit) {
                $el = $hit.Element
                $row.State        = 'found'
                $row.How          = $hit.How
                $row.ElementName  = $el.Current.Name
                $row.AutomationId = $el.Current.AutomationId
                $row.ClassName    = $el.Current.ClassName
                $row.ControlType  = $el.Current.ControlType.ProgrammaticName
                try {
                    $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                    $row.Value = $vp.Current.Value
                } catch {
                    $row.Value = "<ValuePattern unavailable: $($_.Exception.Message)>"
                }
                if ($row.Value -match '^(?i)https?://') { $row.Elided = 'no (scheme present)' }
                elseif ([string]::IsNullOrWhiteSpace($row.Value)) { $row.Elided = 'n/a (empty)' }
                else { $row.Elided = 'YES (scheme stripped)' }

                if ($hit.Positive) {
                    Write-Host ("  {0,-6} FOUND via {1}" -f $b.Name, $row.How) -ForegroundColor Green
                } else {
                    $row.State = 'not identified'
                    Write-Host ("  {0,-6} NOT IDENTIFIED - {1}" -f $b.Name, $row.How) -ForegroundColor Red
                    $failures += "$($b.Name): omnibox matched neither the AutomationId nor the ClassName suffix"
                }
                Write-Host ("         Name         : {0}" -f $row.ElementName)
                Write-Host ("         AutomationId : {0}" -f $row.AutomationId)
                Write-Host ("         ClassName    : {0}" -f $row.ClassName)
                Write-Host ("         ControlType  : {0}" -f $row.ControlType)
                Write-Host ("         Value        : {0}" -f $row.Value)
                Write-Host ("         scheme/www elided in the UIA value: {0}" -f $row.Elided)
                if ($p.MainWindowHandle -eq $fg) { Write-Host "         (this window is the FOREGROUND window)" }
            } elseif ($row.State -eq 'no omnibox found') {
                Write-Host ("  {0,-6} running, but NO address-bar element matched any of the three ways in" -f $b.Name) -ForegroundColor Red
                $failures += "$($b.Name): omnibox not found"
            }
        }
        $results += New-Object psobject -Property $row
    }
}

# ============================================================================
# (c) the URL unit tests
# ============================================================================

Write-Head "(c) URL unit tests"
if ($SkipTests) {
    Write-Host "  skipped (-SkipTests)" -ForegroundColor Yellow
} else {
    # The same dotnet-with-an-SDK search build-dist.ps1 does (the SDK here is user-scoped and
    # not on PATH). The filter covers BOTH URL suites: UrlMatchTests (the pure layer, S6) and
    # UrlWatchSeamTests (the seam, S7).
    $candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source,
                    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) |
        Where-Object { $_ -and (Test-Path $_) }
    $dotnet = $candidates | Where-Object { (& $_ --list-sdks) -match '^\d+\.' } | Select-Object -First 1
    if (-not $dotnet) {
        Write-Host "  FAIL no dotnet with an SDK found" -ForegroundColor Red
        $failures += 'no dotnet SDK for the unit tests'
    } else {
        $sln = Join-Path $repo 'MonkMode.sln'
        $out = & $dotnet test $sln -c Release --nologo --filter 'FullyQualifiedName~Url'
        $line = $out | Where-Object { $_ -match '^\s*(Passed!|Failed!)' } | Select-Object -Last 1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  OK   $line"
        } else {
            Write-Host "  FAIL $line" -ForegroundColor Red
            $failures += 'URL unit tests failed'
        }
    }
}

# ============================================================================
# (d) summary
# ============================================================================

Write-Head "(d) summary"
foreach ($r in $results) {
    Write-Host ("  {0,-6} {1,-16} {2}" -f $r.Name, $r.State, $r.Value)
}
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "PASS - nothing was armed, nothing was started, and no browser was written to." -ForegroundColor Green
    exit 0
} else {
    Write-Host "FAIL:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    Write-Host "(still: nothing was armed, nothing was started, and no browser was written to.)"
    exit 1
}
