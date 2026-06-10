# Builds MonkMode and assembles all components into one folder (dist\),
# which is the layout MonkMode expects at runtime: monkmode.exe (CLI),
# MonkMode_srv.exe (service) and mm_notify.exe (notifier) all live together,
# alongside monkmode_settings.ini once a block is started.
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\build-dist.ps1
# Then (from an elevated prompt):  dist\monkmode.exe block --sites reddit.com --for 2h

$ErrorActionPreference = 'Stop'

# Prefer dotnet on PATH; fall back to the user-scoped SDK install.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "dotnet not found. Install the .NET 8 SDK." }

$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

$projects = @(
    'MonkMode\MonkMode.vbproj',
    'MonkMode_srv\MonkMode_srv\MonkMode_srv.vbproj',
    'MM_notify\MM_notify\MM_notify.vbproj'
)

foreach ($p in $projects) {
    & $dotnet publish (Join-Path $root $p) -c Release -o $dist --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $p" }
}

Write-Host ""
Write-Host "Deployed to: $dist"
Get-ChildItem $dist -Filter *.exe | Select-Object -ExpandProperty Name
