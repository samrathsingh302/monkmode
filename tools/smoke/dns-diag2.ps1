# Isolate WHY MonkMode's hosts write isn't honored (RUN ELEVATED). No service.
# Replicates the product's write path with .NET File APIs and the service's
# open-handle + read-only behavior, probing resolution after each step.
$ErrorActionPreference = 'Continue'
$log = 'C:\Users\samra\monkmode-smoketest\dns-diag2.log'
try { Stop-Transcript | Out-Null } catch {}
Start-Transcript -Path $log -Force | Out-Null

$hosts  = "$env:SystemRoot\System32\drivers\etc\hosts"
$backup = 'C:\Users\samra\monkmode-smoketest\hosts.diag2.backup.txt'
$name   = 'example.org'
Copy-Item $hosts $backup -Force
function Res($label) {
  $a = try { [System.Net.Dns]::GetHostAddresses($name) | % { $_.ToString() } } catch { @("ERR") }
  Write-Host ("  {0,-40} -> {1}" -f $label, ($a -join ','))
}
function Unlock { $h = Get-Item $hosts; if ($h.Attributes -band [IO.FileAttributes]::ReadOnly) { $h.Attributes = $h.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) } }

ipconfig /flushdns | Out-Null; Res "baseline"

# --- Variant A: whole-file rewrite via .NET File.WriteAllText (UTF-8 no BOM), like the product ---
Unlock
$existing = [System.IO.File]::ReadAllText($hosts)
$newText = $existing.TrimEnd() + "`r`n#### MonkMode Entries ####`r`n127.0.0.1 example.org`r`n127.0.0.1 www.example.org`r`n"
[System.IO.File]::WriteAllText($hosts, $newText)
Res "A: after File.WriteAllText (no flush)"
"  bytes start: " + (([System.IO.File]::ReadAllBytes($hosts))[0..3] -join ',')
"  encoding tail line present: " + ([System.IO.File]::ReadAllText($hosts).Contains("127.0.0.1 example.org"))

# --- Variant B: now set read-only (attr only) ---
$h = Get-Item $hosts; $h.Attributes = $h.Attributes -bor [IO.FileAttributes]::ReadOnly
Res "B: after SetReadOnly"

# --- Variant C: now hold an open write handle like the service (Append/Write/Share=Read) ---
Unlock
$fs = [System.IO.FileStream]::new($hosts, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
$h = Get-Item $hosts; $h.Attributes = $h.Attributes -bor [IO.FileAttributes]::ReadOnly
Res "C: handle open + readonly (no flush)"
ipconfig /flushdns | Out-Null; Res "C: handle open + readonly (after flush)"
$fs.Close(); $fs.Dispose()
Res "C: after closing handle"

# cleanup
Unlock
Copy-Item $backup $hosts -Force
ipconfig /flushdns | Out-Null
Write-Host "restored hosts."
try { Stop-Transcript | Out-Null } catch {}
