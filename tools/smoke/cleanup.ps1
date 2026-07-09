# Emergency teardown for MonkMode (RUN ELEVATED).
# Use if the smoke test hangs or leaves the machine blocked.
# The service is CanStop=False AND self-restarting since B1 (SCM recovery
# policy + the mm_guard guardian each resurrect a killed service, and the
# service respawns a killed guardian), so the order matters: disable the SCM
# recovery policy first, then kill guardian + service together (retrying until
# both stay down), then unlock + restore hosts, delete the service
# registration, and clear the notifier.
param([string]$Dist)

# $Dist = the built dist\ folder (holds the block snapshot to remove). Default:
# derive from this script's location (repo\tools\smoke\ -> repo\dist); fall back
# to the absolute path if unknown.
if (-not $Dist) {
  if ($PSScriptRoot) { $Dist = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'dist' }
  else               { $Dist = 'C:\Users\samra\repos\monk-mode\dist' }
}

$hosts  = "$env:SystemRoot\System32\drivers\etc\hosts"
$backup = 'C:\Users\samra\monkmode-smoketest\hosts.backup.txt'
$runKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$snap   = Join-Path $Dist 'monkmode_hosts.block'
$sbMin  = 'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\MONKMODE'
$sbNet  = 'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\MONKMODE'

Write-Host "Disarming B1 (SCM recovery off) + killing MonkMode processes..." -ForegroundColor Cyan
cmd /c 'sc failure MONKMODE reset= 0 actions= ""' | Out-Null
foreach ($i in 1..6) {
  Get-Process mm_guard     -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Get-Process MonkMode_srv -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  if (-not (Get-Process mm_guard -ErrorAction SilentlyContinue) -and
      -not (Get-Process MonkMode_srv -ErrorAction SilentlyContinue)) { break }
  Start-Sleep -Milliseconds 500
}
Get-Process mm_notify -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# B6: the service denies its own DELETE to Built-in Administrators while a block
# is active (ACE (D;;SD;;;BA) on its object DACL). 'sc delete' opens with DELETE
# and runs as BA, so it would be REFUSED and leave the service orphaned. Strip
# the ACE first — we always hold WRITE_DAC (B6 never denies it), and the service
# is killed above so it cannot re-assert between strip and delete. No-op if
# absent (a cleanly-expired block already removed it).
Write-Host "Removing B6 deny-DELETE ACE so the service is deletable..." -ForegroundColor Cyan
$denyAce = '(D;;SD;;;BA)'
$sd = ((sc.exe sdshow MONKMODE 2>$null) -join '').Trim()
if ($sd -and $sd.Contains($denyAce)) { sc.exe sdset MONKMODE ($sd.Replace($denyAce, '')) | Out-Null }

Write-Host "Deleting MONKMODE service..." -ForegroundColor Cyan
sc.exe stop MONKMODE   2>$null | Out-Null
sc.exe delete MONKMODE 2>$null | Out-Null

Write-Host "Unlocking + restoring hosts..." -ForegroundColor Cyan
if (Test-Path $hosts) {
  $h = Get-Item $hosts
  if ($h.Attributes -band [IO.FileAttributes]::ReadOnly) { $h.Attributes = $h.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) }
}
if (Test-Path $backup) {
  Copy-Item $backup $hosts -Force
  Write-Host "  Restored hosts from $backup"
} else {
  Write-Host "  No backup found; manually remove any '#### MonkMode Entries ####' block from $hosts" -ForegroundColor Yellow
}

Write-Host "Removing B2 hosts snapshot (else a reinstalled service self-heals old sites back in)..." -ForegroundColor Cyan
Remove-Item $snap -Force -ErrorAction SilentlyContinue

Write-Host "Removing B3 SafeBoot keys (else an orphaned Safe Mode registration lingers)..." -ForegroundColor Cyan
Remove-Item -LiteralPath $sbMin, $sbNet -Recurse -Force -ErrorAction SilentlyContinue

# B5a: restore the browser DoH-off policy the service forced. No data loss: if the
# CLI's snapshot (monkmode_doh.snapshot, next to the exes) recorded a prior value,
# put it back; otherwise remove ONLY our specific value (never the shared vendor
# key). Mirrors monkmode.Blocker.RemoveDohPolicy for the emergency path.
Write-Host "Restoring browser DoH policy (B5a)..." -ForegroundColor Cyan
$dohSnap = Join-Path (Split-Path $snap) 'monkmode_doh.snapshot'
$dohEntries = @(
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge';               Name = 'DnsOverHttpsMode'; Kind = 'String' },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Google\Chrome';                Name = 'DnsOverHttpsMode'; Kind = 'String' },
  @{ Path = 'HKLM:\SOFTWARE\Policies\BraveSoftware\Brave';          Name = 'DnsOverHttpsMode'; Kind = 'String' },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS'; Name = 'Enabled';          Kind = 'DWord'  },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS'; Name = 'Locked';           Kind = 'DWord'  }
)
$dohPrior = @{}   # "HKLM:\<subkey>|<value>" -> prior value string (only for P lines)
if (Test-Path $dohSnap) {
  foreach ($line in (Get-Content -LiteralPath $dohSnap)) {
    $f = $line -split "`t"
    if ($f.Count -ge 4 -and $f[2] -eq 'P') { $dohPrior["HKLM:\$($f[0])|$($f[1])"] = $f[3] }
  }
}
foreach ($e in $dohEntries) {
  $key = "$($e.Path)|$($e.Name)"
  if ($dohPrior.ContainsKey($key)) {
    New-Item -Path $e.Path -Force -ErrorAction SilentlyContinue | Out-Null
    $val = if ($e.Kind -eq 'DWord') { [int]$dohPrior[$key] } else { $dohPrior[$key] }
    Set-ItemProperty -LiteralPath $e.Path -Name $e.Name -Value $val -Type $e.Kind -ErrorAction SilentlyContinue
  } else {
    Remove-ItemProperty -LiteralPath $e.Path -Name $e.Name -Force -ErrorAction SilentlyContinue
  }
}
Remove-Item $dohSnap -Force -ErrorAction SilentlyContinue

Write-Host "Clearing notifier autorun..." -ForegroundColor Cyan
Remove-ItemProperty $runKey -Name MonkMode_notify -ErrorAction SilentlyContinue
ipconfig /flushdns | Out-Null

Write-Host "Done. Verify with: Get-Service MONKMODE  (should be gone)" -ForegroundColor Green
