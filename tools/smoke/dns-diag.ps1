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
