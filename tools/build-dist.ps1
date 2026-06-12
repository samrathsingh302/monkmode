# Builds MonkMode and assembles all components into one folder (dist\),
# which is the layout MonkMode expects at runtime: monkmode.exe (CLI),
# MonkMode_srv.exe (service), mm_notify.exe (notifier) and mm_guard.exe
# (watchdog guardian) all live together, alongside monkmode_settings.ini
# once a block is started.
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\build-dist.ps1
# Then (from an elevated prompt):  dist\monkmode.exe block --sites reddit.com --for 2h

$ErrorActionPreference = 'Stop'

# Use a dotnet that actually has an SDK: try PATH first, then the user-scoped
# install (a runtime-only dotnet on PATH, e.g. C:\Program Files\dotnet, can't build).
$candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source,
                (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) |
    Where-Object { $_ -and (Test-Path $_) }
$dotnet = $candidates | Where-Object { (& $_ --list-sdks) -match '^\d+\.' } |
    Select-Object -First 1
if (-not $dotnet) { throw "No dotnet with an SDK installed was found. Install the .NET 8 SDK." }

$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

$projects = @(
    'MonkMode\MonkMode.vbproj',
    'MonkMode_srv\MonkMode_srv\MonkMode_srv.vbproj',
    'MM_notify\MM_notify\MM_notify.vbproj',
    'MM_guard\MM_guard\MM_guard.vbproj'
)

foreach ($p in $projects) {
    & $dotnet publish (Join-Path $root $p) -c Release -o $dist --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $p" }
}

Write-Host ""
Write-Host "Deployed to: $dist"
Get-ChildItem $dist -Filter *.exe | Select-Object -ExpandProperty Name
