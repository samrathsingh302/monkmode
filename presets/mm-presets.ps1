# MonkMode preset commands - dot-sourced from the PowerShell profile.
# To change WHAT is blocked or for HOW LONG, edit the .txt lists and defaults.ini
# in this folder - you should not need to touch this file.
# Every command needs an ELEVATED terminal: Win+X -> Terminal (Admin), then type
# `powershell` (the mm-* commands live in the Windows PowerShell profile, not pwsh).
# NEVER pipe or capture the output of an arming command (mm-video | tee, > file):
# the one-time partner code prints once, and captured output has wedged shells before.

# Versioned home since 03/08/2026: this folder (repos\monk-mode\presets) is the live layer,
# tracked in git. C:\Users\samra\monkmode-lists\ is the retired pre-versioning copy.
$global:MonkModeLists = 'C:\Users\samra\repos\monk-mode\presets'

function MM-IsAdmin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function MM-Sites([string]$file) {
    (Get-Content (Join-Path $global:MonkModeLists $file) |
        Where-Object { $_ -match '\S' -and $_.Trim() -notmatch '^#' } |
        ForEach-Object { $_.Trim() }) -join ','
}

function MM-Default([string]$key) {
    $line = Get-Content (Join-Path $global:MonkModeLists 'defaults.ini') |
        Where-Object { $_ -match "^\s*$key\s*=" } | Select-Object -First 1
    if ($line) { ($line -split '=', 2)[1].Trim() } else { '2h' }
}

function MM-Arm([string]$listFile, [string]$key, [string]$For, [string[]]$extraArgs) {
    if (-not (MM-IsAdmin)) {
        Write-Host 'Run this from an ELEVATED terminal (Win+X -> Terminal (Admin), then type powershell) - MonkMode refuses without it.' -ForegroundColor Yellow
        return
    }
    if (-not $For) { $For = MM-Default $key }
    $sites = MM-Sites $listFile
    if (-not $sites) { Write-Host "List $listFile is empty - nothing to block." -ForegroundColor Yellow; return }
    if ($For -eq 'midnight') {
        $until = (Get-Date).ToString('yyyy-MM-dd') + ' 23:59'
        & monkmode block --sites $sites @extraArgs --until $until
    } else {
        & monkmode block --sites $sites @extraArgs --for $For
    }
}

# --- The three presets -------------------------------------------------------
# Duration is optional; without it the defaults.ini value is used.
#   mm-video            mm-video 3h          mm-video midnight
function mm-video  { param([string]$For) MM-Arm 'video.txt'  'video'  $For @() }
function mm-insta  { param([string]$For) MM-Arm 'social.txt' 'insta'  $For @('--apps', 'WhatsApp.exe') }
function mm-reddit { param([string]$For) MM-Arm 'reddit.txt' 'reddit' $For @() }

# --- Always-on schedule for the video set (mutually exclusive with manual blocks!) ---
function mm-video-schedule {
    if (-not (MM-IsAdmin)) { Write-Host 'Needs an ELEVATED terminal.' -ForegroundColor Yellow; return }
    & monkmode schedule --sites (MM-Sites 'video.txt') --windows 'Mon-Sun 00:00-23:59'
}
function mm-schedule-show { & monkmode schedule --show }
function mm-schedule-off  {
    if (-not (MM-IsAdmin)) { Write-Host 'Needs an ELEVATED terminal.' -ForegroundColor Yellow; return }
    & monkmode schedule --clear   # stops FUTURE windows; a currently-open window still runs to its end
}

# --- Info / management -------------------------------------------------------
function mm-status {
    if (-not (MM-IsAdmin)) { Write-Host 'Needs an ELEVATED terminal (Win+X -> Terminal (Admin), then type powershell).' -ForegroundColor Yellow; return }
    & monkmode status
}
function mm-stats  {
    if (-not (MM-IsAdmin)) { Write-Host 'Needs an ELEVATED terminal (Win+X -> Terminal (Admin), then type powershell).' -ForegroundColor Yellow; return }
    & monkmode stats
}
function mm-edit   { Start-Process explorer $global:MonkModeLists }   # open the lists folder
