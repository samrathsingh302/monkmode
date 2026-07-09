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

# Confirm the fix direction (RUN ELEVATED). Tests whether dropping the service's
# persistent write-handle (vs. holding it) makes the hosts block survive a flush.
$ErrorActionPreference = 'Continue'
$log = 'C:\Users\samra\monkmode-smoketest\dns-diag3.log'
try { Stop-Transcript | Out-Null } catch {}
Start-Transcript -Path $log -Force | Out-Null

$hosts  = "$env:SystemRoot\System32\drivers\etc\hosts"
$backup = 'C:\Users\samra\monkmode-smoketest\hosts.diag3.backup.txt'
$name   = 'example.org'
Copy-Item $hosts $backup -Force
function Unlock { $h = Get-Item $hosts; if ($h.Attributes -band [IO.FileAttributes]::ReadOnly) { $h.Attributes = $h.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) } }
function WriteBlock { Unlock; $e=[System.IO.File]::ReadAllText($hosts); $e=($e -replace '(?s)#### MonkMode Entries ####.*$','').TrimEnd(); [System.IO.File]::WriteAllText($hosts, $e + "`r`n#### MonkMode Entries ####`r`n127.0.0.1 example.org`r`n") }
function Res($label) { Start-Sleep -Milliseconds 300; $a = try { [System.Net.Dns]::GetHostAddresses($name) | % { $_.ToString() } } catch { @("ERR") }; Write-Host ("  {0,-46} -> {1}" -f $label, ($a -join ',')) }

Write-Host "X. no persistent handle (write + readonly attr only):"
ipconfig /flushdns | Out-Null
WriteBlock
$h = Get-Item $hosts; $h.Attributes = $h.Attributes -bor [IO.FileAttributes]::ReadOnly
ipconfig /flushdns | Out-Null; Res "X: flush AFTER write, then resolve"

Unlock; Copy-Item $backup $hosts -Force; ipconfig /flushdns | Out-Null

Write-Host "Y. service-style persistent write handle (Append/Write/Share=Read):"
ipconfig /flushdns | Out-Null
WriteBlock
$fsY = [System.IO.FileStream]::new($hosts, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
$h = Get-Item $hosts; $h.Attributes = $h.Attributes -bor [IO.FileAttributes]::ReadOnly
ipconfig /flushdns | Out-Null; Res "Y: flush with handle held"
$fsY.Close(); $fsY.Dispose()

Unlock; Copy-Item $backup $hosts -Force; ipconfig /flushdns | Out-Null

Write-Host "Z. persistent handle but Read access + ReadWrite share:"
ipconfig /flushdns | Out-Null
WriteBlock
$h = Get-Item $hosts; $h.Attributes = $h.Attributes -bor [IO.FileAttributes]::ReadOnly
$fsZ = [System.IO.FileStream]::new($hosts, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, ([System.IO.FileShare]::Read -bor [System.IO.FileShare]::Write))
ipconfig /flushdns | Out-Null; Res "Z: flush with read-share handle held"
$fsZ.Close(); $fsZ.Dispose()

Unlock; Copy-Item $backup $hosts -Force; ipconfig /flushdns | Out-Null
Write-Host "restored hosts."
try { Stop-Transcript | Out-Null } catch {}
