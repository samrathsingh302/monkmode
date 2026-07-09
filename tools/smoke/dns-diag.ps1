# Isolated hosts-reload timing diagnostic (RUN ELEVATED). No MonkMode involved.
# Adds 127.0.0.1 example.org to hosts and measures how long / what action is
# needed before getaddrinfo honors it. Restores hosts at the end.
$ErrorActionPreference = 'Continue'
$log = 'C:\Users\samra\monkmode-smoketest\dns-diag.log'
try { Stop-Transcript | Out-Null } catch {}
Start-Transcript -Path $log -Force | Out-Null

$hosts  = "$env:SystemRoot\System32\drivers\etc\hosts"
$backup = 'C:\Users\samra\monkmode-smoketest\hosts.diag.backup.txt'
$name   = 'example.org'
function Res($label) {
  $a = try { [System.Net.Dns]::GetHostAddresses($name) | % { $_.ToString() } } catch { @("ERR:$_") }
  $blocked = ($a -contains '127.0.0.1') -and -not ($a | ? { $_ -notmatch '^127\.0\.0\.1$|^::1$' })
  Write-Host ("  {0,-34} -> {1}   blocked={2}" -f $label, ($a -join ','), $blocked)
}

Copy-Item $hosts $backup -Force
Write-Host "0. baseline (no block):"
ipconfig /flushdns | Out-Null; Res "after flush, before edit"

# append entry, clearing read-only first
$h = Get-Item $hosts
if ($h.Attributes -band [IO.FileAttributes]::ReadOnly) { $h.Attributes = $h.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) }
Add-Content -Path $hosts -Value "`r`n#### DNSTEST ####`r`n127.0.0.1 example.org`r`n" -Encoding ASCII

Write-Host "1. entry written. probing:"
Res "t+0, no flush"
ipconfig /flushdns | Out-Null;            Res "after flushdns"
Start-Sleep -Seconds 2; ipconfig /flushdns | Out-Null; Res "after sleep2 + flushdns"
Start-Sleep -Seconds 5; ipconfig /flushdns | Out-Null; Res "after sleep5 + flushdns"
try { Restart-Service Dnscache -Force -ErrorAction Stop; Write-Host "  (Dnscache restarted)" } catch { Write-Host "  (Dnscache restart failed: $_)" }
Start-Sleep -Seconds 2; Res "after Dnscache restart"

# cleanup
Copy-Item $backup $hosts -Force
ipconfig /flushdns | Out-Null
Write-Host "restored hosts."
try { Stop-Transcript | Out-Null } catch {}
