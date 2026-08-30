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

# MonkMode end-to-end smoke test (RUN ELEVATED).
# Installs the MONKMODE service, blocks example.com for 5 minutes, verifies the
# block is live, TAMPERS with hosts twice and verifies the B2 self-heal restores
# it, runs the B1 watchdog KILL DRILLS (K1 force-kill service -> SCM restarts it;
# K2 force-kill mm_guard -> service respawns it; K3 force-kill mm_notify ->
# guardian relaunches it into the user session; K4 disable SCM recovery + kill
# service -> guardian alone restarts it, policy restored after), drills B6
# (sc-delete resistance: deny-DELETE ACE present + 'sc delete' refused mid-block
# + self-heal after the ACE is stripped + ACE removed at expiry), waits for the
# service to auto-lift — asserting it STAYS stopped (no stopMe <-> recovery
# restart loop) with no stray mm_guard/mm_notify — verifies cleanup, then tears
# everything down (strips the B6 ACE, sc delete, restores the hosts backup).
#
# Also drills B3 (Safe Mode): the service registers itself under the SafeBoot
# Minimal + Network keys so it runs in Safe Mode, re-asserts them every tick if
# they are deleted, and removes them at expiry. NOTE: that the service actually
# RUNS in Safe Mode is the one thing this (normal-mode) test CANNOT prove
# unattended — confirm it once by hand (start a block, reboot into Safe Mode,
# check 'sc query MONKMODE' = RUNNING and hosts still blocks, then reboot back).
#
# B4 (clock rollback) and B7 (tamper-evident config) get WIRING-PRESENT checks
# here ([Time] HighWater seeded; [Integrity] Mac + Key written). Their deep
# behavioural proofs are intentionally NOT in the main flow:
#   - B4 (does a clock jump past Until fail to lift?) is an OPTIONAL in-flow
#     drill, OFF by default — pass -IncludeClockTest to enable it. It moves the
#     SYSTEM CLOCK, so it is gated + wrapped in a restore. Leave it off unless
#     you are watching the run.
#   - B7 (does an invalid MAC keep the block past Until?) CANNOT share a run with
#     the auto-lift test: corrupting the MAC is one-way (we can't recompute it
#     without the DPAPI key), and fail-closed means the block then never lifts,
#     which would hang this script. Prove it in a DEDICATED run: block, corrupt
#     [Integrity] Mac, set --for to ~1 min, confirm it does NOT lift after the
#     minute, then 'monkmode unblock --force'. The 310-line ConfigIntegrity unit
#     tests are the authoritative coverage.
#
# Expected result: 69 passed, 0 failed (15 original + 12 B2 + 20 B1 + 5 B3 +
# 7 B5a + 9 B4/B6/B7 + 1 early-lift guard). With -IncludeClockTest: 71 (the B4 clock drill adds 2).
# NOTE (B5a, 01/07/2026): the DoH beats are BENCH-UNTESTED (added without an
# elevated run) - validate them in the batched G1 run. The $dist/$snap paths were
# repointed Cold-Turkey-Serious -> monk-mode this session.
#
# Usage (from an ELEVATED PowerShell):
#   powershell -ExecutionPolicy Bypass -File C:\Users\samra\monkmode-smoketest\run-smoketest.ps1
#   powershell -ExecutionPolicy Bypass -File ...\run-smoketest.ps1 -IncludeClockTest
# or just run it line-by-line in an admin terminal.

param([switch]$IncludeClockTest, [string]$Dist)

# $Dist = the built dist\ folder (fresh output of tools\build-dist.ps1). Default:
# derive from this script's location (repo\tools\smoke\ -> repo\dist). Falls back
# to the canonical absolute path when $PSScriptRoot is empty (line-by-line paste).
if (-not $Dist) {
  if ($PSScriptRoot) { $Dist = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'dist' }
  else               { $Dist = 'C:\Users\samra\repos\monk-mode\dist' }
}

$ErrorActionPreference = 'Continue'

$log = 'C:\Users\samra\monkmode-smoketest\smoketest.log'
try { Stop-Transcript | Out-Null } catch {}
Start-Transcript -Path $log -Force | Out-Null

$dist     = $Dist
$monk     = Join-Path $dist 'monkmode.exe'
$ini      = Join-Path $dist 'monkmode_settings.ini'
$setupIni = Join-Path $dist 'monkmode_setup.ini'   # CLI setup file; absent on a fresh dist -> arms refuse exit 4
$snap     = Join-Path $dist 'monkmode_hosts.block'
$hosts    = "$env:SystemRoot\System32\drivers\etc\hosts"
$backup   = 'C:\Users\samra\monkmode-smoketest\hosts.backup.txt'
$runKey   = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$marker   = '#### MonkMode Entries ####'
$sentinel = '# MM-SMOKETEST-SENTINEL (user content - must survive repair)'
$sbMin    = 'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\MONKMODE'
$sbNet    = 'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\MONKMODE'
$dohSnap  = Join-Path $dist 'monkmode_doh.snapshot'   # B5a: prior DoH policy snapshot
$pass = 0; $fail = 0
function Check($name, $cond) {
  if ($cond) { Write-Host "  [PASS] $name" -ForegroundColor Green; $script:pass++ }
  else       { Write-Host "  [FAIL] $name" -ForegroundColor Red;   $script:fail++ }
}

# Clear hosts' read-only attribute and overwrite it with $text. Retries because
# the service re-asserts read-only every 10s and can race our write.
function Write-HostsTamper($text) {
  for ($i = 0; $i -lt 5; $i++) {
    try {
      $h = Get-Item $hosts
      if ($h.Attributes -band [IO.FileAttributes]::ReadOnly) {
        $h.Attributes = $h.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)
      }
      [IO.File]::WriteAllText($hosts, $text)
      return $true
    } catch { Start-Sleep -Milliseconds 400 }
  }
  return $false
}

# Poll hosts until the MonkMode block reappears (marker + example.com entry).
# Returns the repaired text, or $null on timeout. Service tick is 10s, so 35s
# covers a tick plus slack.
function Wait-HostsRepair([int]$timeoutSec) {
  $until = (Get-Date).AddSeconds($timeoutSec)
  while ((Get-Date) -lt $until) {
    Start-Sleep -Seconds 2
    $t = try { [IO.File]::ReadAllText($hosts) } catch { '' }
    if ($t -match '#### MonkMode Entries ####' -and $t -match '127\.0\.0\.1\s+example\.com') { return $t }
  }
  return $null
}

function Resolve-Example {
  try { [System.Net.Dns]::GetHostAddresses('example.com') | ForEach-Object { $_.ToString() } } catch { @() }
}

# Read the (Default) value of a SafeBoot subkey, or $null if the key is absent.
# B3 registers MONKMODE under SafeBoot so it starts in Safe Mode; the (Default)
# tag is the conventional "Service".
function SafeBootTag($key) {
  # SilentlyContinue (not -EA Stop + try/catch): an absent key is the EXPECTED
  # state during the 2d delete drill and the post-expiry check, and -EA Stop
  # spams the transcript with a TerminatingError each probe. Return $null if absent.
  $k = Get-Item -LiteralPath $key -ErrorAction SilentlyContinue
  if ($k) { return $k.GetValue('') } else { return $null }
}

# --- B5a: browser DoH-off policy. The service forces each browser "Secure DNS"
# policy value OFF at OnStart + every 10s tick, and restores the user's prior at a
# genuine expiry (no data loss). This list MIRRORS monkmode.DohPolicy.Entries;
# DohPolicyParityTests keeps the CLI + service copies in lockstep, and this smoke
# copy must be re-checked against them if the vendor keys ever change. ---
$dohEntries = @(
  @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge';               Name = 'DnsOverHttpsMode'; Blocked = 'off'; Kind = 'String' },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Google\Chrome';                Name = 'DnsOverHttpsMode'; Blocked = 'off'; Kind = 'String' },
  @{ Path = 'HKLM:\SOFTWARE\Policies\BraveSoftware\Brave';          Name = 'DnsOverHttpsMode'; Blocked = 'off'; Kind = 'String' },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS'; Name = 'Enabled';          Blocked = 0;     Kind = 'DWord'  },
  @{ Path = 'HKLM:\SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS'; Name = 'Locked';           Blocked = 1;     Kind = 'DWord'  }
)
# Read one policy value, or $null if the value/key is absent (SilentlyContinue: an
# absent value is the EXPECTED state during the delete drill + post-expiry check).
function DohVal($path, $name) {
  $p = Get-ItemProperty -LiteralPath $path -Name $name -ErrorAction SilentlyContinue
  if ($p) { return $p.$name } else { return $null }
}
# True if EVERY DoH policy value is at its blocked setting (case-sensitive, like
# the service's ordinal ValueIsBlocked). Absent => not blocked.
function DohAllBlocked {
  foreach ($e in $dohEntries) {
    $v = DohVal $e.Path $e.Name
    if ($null -eq $v -or "$v" -cne "$($e.Blocked)") { return $false }
  }
  return $true
}
# True if ANY of our policy values is still present (used post-expiry).
function DohAnyPresent {
  foreach ($e in $dohEntries) { if ($null -ne (DohVal $e.Path $e.Name)) { return $true } }
  return $false
}

# Poll a condition until it is true or the timeout passes. Returns the elapsed
# seconds on success (so the log shows HOW fast a watchdog layer reacted), or
# -1 on timeout. Used by the B1 kill drills.
function Wait-Condition([scriptblock]$cond, [int]$timeoutSec, [int]$pollMs = 500) {
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
    if (& $cond) { return [Math]::Round($sw.Elapsed.TotalSeconds, 1) }
    Start-Sleep -Milliseconds $pollMs
  }
  return -1
}

# B6: the exact deny ACE the service puts on its own object DACL (Deny, right SD
# = DELETE, principal BA = Built-in Administrators). Mirrors
# ServiceSecurity.DenyDeleteAce — a drift here means the test is checking the
# wrong token.
$denyAce = '(D;;SD;;;BA)'

# Read the MONKMODE service object's security descriptor as one SDDL string
# (sc.exe sdshow prints it, sometimes across blank lines), or '' if unavailable.
function Get-ServiceSddl {
  return ((sc.exe sdshow MONKMODE 2>$null) -join '').Trim()
}

# Strip the B6 deny-DELETE ACE from the live service SD so 'sc delete' (which
# opens with DELETE — denied to BA by that ACE) can proceed. We always hold
# WRITE_DAC (B6 NEVER denies it), so sdset always succeeds. No-op if absent.
# Used by the teardown rescue path (and the same logic lives in cleanup.ps1).
function Remove-ServiceDenyDelete {
  $sd = Get-ServiceSddl
  if ($sd -and $sd.Contains($denyAce)) {
    $stripped = $sd.Replace($denyAce, '')
    sc.exe sdset MONKMODE $stripped | Out-Null
  }
}

# 0. preconditions ----------------------------------------------------------
$me = [System.Security.Principal.WindowsPrincipal]([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Host "This script must be run ELEVATED (as Administrator). Aborting." -ForegroundColor Red
  exit 1
}
if (-not (Test-Path $monk)) { Write-Host "monkmode.exe not found at $monk. Build dist first (tools\build-dist.ps1)." -ForegroundColor Red; exit 1 }

# build-dist.ps1 wipes dist\ INCLUDING monkmode_setup.ini, so a fresh dist has no
# setup file and every `block` arm fail-closes with exit 4. Self-setup once here
# (defaults: no partner/cooloff/default-lists needed for the smoke) so the run is
# not silently aborted (cost a 30-min re-run 14/07/2026). Idempotent: skipped when
# the file already exists.
if (-not (Test-Path $setupIni)) {
  Write-Host "No monkmode_setup.ini in dist -> running one-time 'monkmode setup' (fresh-dist precondition)." -ForegroundColor Yellow
  & $monk setup | Out-Null
  if (-not (Test-Path $setupIni)) { Write-Host "'monkmode setup' did not produce $setupIni (exit $LASTEXITCODE). Aborting." -ForegroundColor Red; exit 1 }
}

if (-not (Test-Path $backup)) { Copy-Item $hosts $backup -Force }

if (Get-Service MONKMODE -ErrorAction SilentlyContinue) {
  Write-Host "MONKMODE service already exists. Run cleanup.ps1 first." -ForegroundColor Yellow
  exit 1
}

# Plant a user-content sentinel line in hosts BEFORE the block so the tamper
# tests can prove the repair preserves the user's own content. The teardown's
# backup restore removes it again.
$h0 = Get-Item $hosts
if ($h0.Attributes -band [IO.FileAttributes]::ReadOnly) { $h0.Attributes = $h0.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) }
[IO.File]::AppendAllText($hosts, "`r`n$sentinel`r`n")

ipconfig /flushdns | Out-Null   # clear any stale cache BEFORE the block (no handle held yet)

# B5a: capture the box's PRE-BLOCK DoH policy so the post-expiry check can prove
# every value is RESTORED to EXACTLY its prior state (no data loss), whatever the
# starting state (present or absent). Keyed "path|name".
$dohPre = @{}
foreach ($e in $dohEntries) { $dohPre["$($e.Path)|$($e.Name)"] = DohVal $e.Path $e.Name }

# 5 minutes (was 3): the B2 tamper drills (~100s) PLUS the B1 kill drills
# (~60-90s) must all finish comfortably before expiry — killing the service
# right as the end time lands would tangle the drills with the lift.
Write-Host "`n=== 1. Starting a 5-minute block on example.com ===" -ForegroundColor Cyan
$blockStart = Get-Date
& $monk block --sites example.com --for 5
Write-Host "(monkmode.exe exit code: $LASTEXITCODE)"

Start-Sleep -Seconds 3   # let the service finish starting

Write-Host "`n=== 2. Verifying the block is LIVE ===" -ForegroundColor Cyan
$svc = Get-Service MONKMODE -ErrorAction SilentlyContinue
Check "MONKMODE service installed"            ($null -ne $svc)
Check "MONKMODE service running"              ($svc -and $svc.Status -eq 'Running')
$hostsText = Get-Content $hosts -Raw
Check "hosts has MonkMode marker"             ($hostsText -match '#### MonkMode Entries ####')
Check "hosts blocks example.com -> 127.0.0.1" ($hostsText -match '127\.0\.0\.1\s+example\.com')
Check "hosts is read-only (locked)"           ((Get-Item $hosts).Attributes -band [IO.FileAttributes]::ReadOnly)
Check "config ini written"                    (Test-Path $ini)
Check "notifier registered in HKCU Run"       ($null -ne (Get-ItemProperty $runKey -Name MonkMode_notify -ErrorAction SilentlyContinue))
Check "mm_notify.exe running"                 ($null -ne (Get-Process mm_notify -ErrorAction SilentlyContinue))
# B1 layer 1: the CLI must have stamped the SCM FailureActions at install
# (ServiceTools.SetRecoveryOptions: 3x RESTART, 1000ms delay, reset INFINITE).
$qf = (sc.exe qfailure MONKMODE) -join "`n"
Check "B1 recovery policy: 3x RESTART actions"        (([regex]::Matches($qf, 'RESTART')).Count -eq 3)
Check "B1 recovery policy: 1000 ms delay on each"     (([regex]::Matches($qf, 'Delay\s*=\s*1000')).Count -eq 3)
Check "B1 recovery policy: reset period INFINITE"     ($qf -match 'RESET_PERIOD[^\r\n]*(INFINITE|4294967295)')
# B1 layer 2: the service's first 10s timer tick spawns the SYSTEM guardian.
$tGuard = Wait-Condition { $null -ne (Get-Process mm_guard -ErrorAction SilentlyContinue) } 25
Write-Host "    mm_guard appeared after ${tGuard}s (service tick is 10s)"
Check "B1 mm_guard spawned by the service (<=25s)"    ($tGuard -ge 0)
Check "B1 mm_guard runs as SYSTEM (session 0)"        ((Get-Process mm_guard -ErrorAction SilentlyContinue | Select-Object -First 1).SessionId -eq 0)
# B3: the service registers itself under both SafeBoot keys at OnStart (and
# re-asserts them every tick) so it starts in Safe Mode / Safe Mode w/ Network.
Check "B3 SafeBoot Minimal key registered (tag=Service)"  ((SafeBootTag $sbMin) -eq 'Service')
Check "B3 SafeBoot Network key registered (tag=Service)"  ((SafeBootTag $sbNet) -eq 'Service')
# B5a: the service forces every browser Secure-DNS (DoH) policy value OFF at
# OnStart, so DoH can't tunnel around the hosts block; the CLI snapshotted the
# user's prior values next to the exes for the no-data-loss restore at expiry.
Check "B5a all browser DoH policy values forced off at start" (DohAllBlocked)
Check "B5a DoH prior-state snapshot written (monkmode_doh.snapshot)" (Test-Path $dohSnap)
# B2: the CLI must have persisted the exact block it wrote, next to the exes.
Check "hosts snapshot written (monkmode_hosts.block)" (Test-Path $snap)
$snapText = try { [IO.File]::ReadAllText($snap) } catch { '' }
Check "snapshot content present in hosts verbatim"    ($snapText -ne '' -and $hostsText.Contains($snapText))
# B7 (tamper-evident config) + B4 (clock high-water) WIRING checks: the CLI must
# have written the HMAC + DPAPI key and seeded the monotonic high-water mark.
# Field NAMES are plaintext in the ini (the VALUES are encrypted/Base64); the
# presence of the keys proves the writers ran. (Behavioural proofs: see header.)
$iniText = try { Get-Content $ini -Raw } catch { '' }
Check "B7 [Integrity] Mac present in ini"     ($iniText -match '(?m)^\s*Mac\s*=\s*\S')
Check "B7 [Integrity] Key present in ini"     ($iniText -match '(?m)^\s*Key\s*=\s*\S')
Check "B4 [Time] HighWater seeded in ini"     ($iniText -match '(?m)^\s*HighWater\s*=\s*\S')
# B6 (sc-delete resistance): the service must have stamped the deny-DELETE ACE on
# its own object DACL at OnStart (re-asserted every tick). Probe the live SD.
$sddl0 = Get-ServiceSddl
Write-Host "    service SDDL: $sddl0"
Check "B6 deny-DELETE ACE present on service object" ($sddl0.Contains($denyAce))
# Do NOT flush here: flushing while the service holds the hosts file open makes
# dnscache fail to reload hosts. Normal apps just resolve (hosts already loaded
# on the file-change from the block write), so resolve directly like a browser.
$addrs = Resolve-Example
Write-Host "    example.com resolves to: $($addrs -join ', ')"
Check "example.com resolves to 127.0.0.1"     ($addrs -contains '127.0.0.1')
Check "example.com NOT reaching real IPs"     (-not ($addrs | Where-Object { $_ -notmatch '^127\.0\.0\.1$|^::1$' }))

# Robustness: flushing DNS during a block must NOT bypass it (the old persistent
# write-handle made this break; the fix must survive a flush).
ipconfig /flushdns | Out-Null
Start-Sleep -Milliseconds 500
$addrsF = Resolve-Example
Write-Host "    after flushdns during block, example.com -> $($addrsF -join ', ')"
Check "block survives ipconfig /flushdns"     ($addrsF -contains '127.0.0.1' -and -not ($addrsF | Where-Object { $_ -notmatch '^127\.0\.0\.1$|^::1$' }))

Write-Host "`n=== 2b. B2 TAMPER-REPAIR: the service must self-heal hosts ===" -ForegroundColor Cyan

# --- T1: admin clears read-only and deletes the MonkMode block (keeps own content)
Write-Host "  T1: clearing read-only + deleting the MonkMode block from hosts..."
$raw = [IO.File]::ReadAllText($hosts)
$idx = $raw.IndexOf($marker, [StringComparison]::Ordinal)
$t1Applied = $false
if ($idx -ge 0) { $t1Applied = Write-HostsTamper ($raw.Substring(0, $idx)) }
Check "T1 tamper applied (block deleted, user content kept)" $t1Applied
$t1Restored = Wait-HostsRepair 35
Check "T1 service restored marker + entries within 35s"      ($null -ne $t1Restored)
Check "T1 user content (sentinel) preserved through repair"  ($t1Restored -and $t1Restored.Contains($sentinel))
# the repair's Finally re-asserts read-only in the same tick; give it a moment
$roBack = $false
foreach ($i in 1..6) {
  if ((Get-Item $hosts).Attributes -band [IO.FileAttributes]::ReadOnly) { $roBack = $true; break }
  Start-Sleep -Seconds 1
}
Check "T1 read-only re-asserted after repair"                $roBack
ipconfig /flushdns | Out-Null
Start-Sleep -Milliseconds 500
$addrsT1 = Resolve-Example
Write-Host "    after T1 repair, example.com -> $($addrsT1 -join ', ')"
Check "T1 example.com blocked again after repair"            ($addrsT1 -contains '127.0.0.1')
# convergence: an intact block must NOT be rewritten every tick (no churn)
Start-Sleep -Seconds 12
$t1Later = [IO.File]::ReadAllText($hosts)
Check "T1 repair converges (hosts unchanged one tick later)" ($t1Restored -eq $t1Later)

# --- T2: admin blanks the whole hosts file
Write-Host "  T2: blanking the entire hosts file..."
$t2Applied = Write-HostsTamper "# blanked by MonkMode smoketest T2`r`n"
Check "T2 tamper applied (hosts blanked)"                    $t2Applied
$t2Restored = Wait-HostsRepair 35
Check "T2 service restored marker + entries within 35s"      ($null -ne $t2Restored)
ipconfig /flushdns | Out-Null
Start-Sleep -Milliseconds 500
$addrsT2 = Resolve-Example
Write-Host "    after T2 repair, example.com -> $($addrsT2 -join ', ')"
Check "T2 example.com blocked again after repair"            ($addrsT2 -contains '127.0.0.1')
# (T2 destroys the sentinel by design — the snapshot only owns OUR block; user
# content destroyed by the attacker stays destroyed. Teardown restores backup.)

& $monk status

Write-Host "`n=== 2c. B1 KILL DRILLS: both watchdog layers must hold ===" -ForegroundColor Cyan
$mySession = (Get-Process -Id $PID).SessionId

# --- K1: force-kill the service -> the SCM restarts it (layer 1, policy delay
#     1s, so well inside 5s). The guardian is NOT killed by this and the
#     restarted service's spawn gate must not duplicate it.
Write-Host "  K1: taskkill /F the service process..."
taskkill /F /IM MonkMode_srv.exe | Out-Null
Check "K1 service force-killed"                              ($LASTEXITCODE -eq 0)
# observe it actually DOWN first, else a stale 'Running' status read in the
# instant after the kill would fake-pass the restart check
Wait-Condition { $s = Get-Service MONKMODE -ErrorAction SilentlyContinue; -not ($s -and $s.Status -eq 'Running') } 5 100 | Out-Null
$t = Wait-Condition { $s = Get-Service MONKMODE -ErrorAction SilentlyContinue; $s -and $s.Status -eq 'Running' } 5 250
Write-Host "    service Running again after ${t}s (policy delay is 1s)"
Check "K1 SCM restarted the service within 5s (layer 1)"     ($t -ge 0)
$t = Wait-Condition { @(Get-Process mm_guard -ErrorAction SilentlyContinue).Count -eq 1 } 10
Check "K1 exactly one mm_guard alive after the restart"      ($t -ge 0)

# --- K2: force-kill the guardian -> the service's next tick respawns it
#     (ShouldRestartPeer). Nominal <=10s; the 15s bound allows a worst-case
#     tick phase (kill lands just after a tick) plus process-start slack.
Write-Host "  K2: taskkill /F mm_guard..."
$guardPidOld = (Get-Process mm_guard -ErrorAction SilentlyContinue | Select-Object -First 1).Id
taskkill /F /IM mm_guard.exe | Out-Null
Check "K2 mm_guard force-killed"                             ($LASTEXITCODE -eq 0)
$t = Wait-Condition { $g = @(Get-Process mm_guard -ErrorAction SilentlyContinue); $g.Count -eq 1 -and $g[0].Id -ne $guardPidOld } 15
Write-Host "    mm_guard respawned after ${t}s (service tick is 10s)"
Check "K2 service respawned mm_guard within 15s (layer 2)"   ($t -ge 0)

# --- K3: force-kill the notifier -> the guardian's next tick relaunches it
#     INTO THE INTERACTIVE USER SESSION (WTSQueryUserToken + CreateProcessAsUser)
#     - a SYSTEM Process.Start would land invisibly in session 0, so the
#     session check is the real assertion here.
Write-Host "  K3: taskkill /F mm_notify..."
$notifyPidOld = (Get-Process mm_notify -ErrorAction SilentlyContinue | Select-Object -First 1).Id
taskkill /F /IM mm_notify.exe | Out-Null
Check "K3 mm_notify force-killed"                            ($LASTEXITCODE -eq 0)
$t = Wait-Condition { $n = @(Get-Process mm_notify -ErrorAction SilentlyContinue); $n.Count -ge 1 -and $n[0].Id -ne $notifyPidOld } 15
Write-Host "    mm_notify relaunched after ${t}s (guardian tick is 10s)"
Check "K3 guardian relaunched mm_notify within 15s"          ($t -ge 0)
Check "K3 relaunched mm_notify is in the user session"       ((Get-Process mm_notify -ErrorAction SilentlyContinue | Select-Object -First 1).SessionId -eq $mySession)

# --- K4: disable SCM recovery entirely (the 'sc failure ... reset= 0' attack),
#     kill the service -> ONLY the guardian can bring it back. Restore the
#     policy afterwards. (The non-crash failure flag is a separate setting that
#     'sc failure' does not touch, so restoring the actions restores the policy.)
Write-Host "  K4: disabling SCM recovery, then killing the service..."
cmd /c 'sc failure MONKMODE reset= 0 actions= ""' | Out-Null
$qfOff = (sc.exe qfailure MONKMODE) -join "`n"
Check "K4 SCM recovery disabled (no RESTART actions)"        ($qfOff -notmatch 'RESTART')
taskkill /F /IM MonkMode_srv.exe | Out-Null
Wait-Condition { $s = Get-Service MONKMODE -ErrorAction SilentlyContinue; -not ($s -and $s.Status -eq 'Running') } 5 100 | Out-Null
$t = Wait-Condition { $s = Get-Service MONKMODE -ErrorAction SilentlyContinue; $s -and $s.Status -eq 'Running' } 15
Write-Host "    service Running again after ${t}s (guardian tick is 10s)"
Check "K4 guardian restarted the service within 15s (layer 2 only)" ($t -ge 0)
sc.exe failure MONKMODE reset= INFINITE actions= restart/1000/restart/1000/restart/1000 | Out-Null
$qfBack = (sc.exe qfailure MONKMODE) -join "`n"
Check "K4 SCM recovery policy restored (3x RESTART)"         (([regex]::Matches($qfBack, 'RESTART')).Count -eq 3)

# After all that violence the block itself must still be enforced.
ipconfig /flushdns | Out-Null
Start-Sleep -Milliseconds 500
$hostsK = Get-Content $hosts -Raw
$addrsK = Resolve-Example
Write-Host "    after kill drills, example.com -> $($addrsK -join ', ')"
Check "block still enforced after the kill drills"           (($hostsK -match '#### MonkMode Entries ####') -and ($addrsK -contains '127.0.0.1'))

Write-Host "`n=== 2d. B3 SAFEBOOT: the Safe Mode registration must self-heal ===" -ForegroundColor Cyan
# S1: an admin deletes both SafeBoot keys mid-block -> the service's next tick
#     must re-assert them (mirrors the hosts read-only self-heal). The 15s bound
#     allows one full 10s tick + slack, like the B1 respawn drills.
Write-Host "  S1: deleting both SafeBoot keys..."
Remove-Item -LiteralPath $sbMin -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $sbNet -Recurse -Force -ErrorAction SilentlyContinue
$sbGone = ((SafeBootTag $sbMin) -ne 'Service') -and ((SafeBootTag $sbNet) -ne 'Service')
Check "S1 SafeBoot keys deleted (tamper applied)"            $sbGone
$t = Wait-Condition { (SafeBootTag $sbMin) -eq 'Service' -and (SafeBootTag $sbNet) -eq 'Service' } 15
Write-Host "    SafeBoot keys re-asserted after ${t}s (service tick is 10s)"
Check "S1 service re-asserted both SafeBoot keys within 15s" ($t -ge 0)

Write-Host "`n=== 2e. B6 SC-DELETE RESISTANCE: the service must refuse deletion ===" -ForegroundColor Cyan
# D1: `sc delete MONKMODE` while the block is active must be REFUSED. The CLI/sc
#     run as BA, which the deny-DELETE ACE blocks; the service must still exist
#     afterwards. (sc.exe prints 'Access is denied' / FAILED 5 and returns a
#     non-zero exit; either signal is fine — the ground truth is the service
#     surviving the attempt.)
Write-Host "  D1: attempting 'sc delete MONKMODE' during the active block..."
$delOut = (sc.exe delete MONKMODE 2>&1) -join ' '
$delExit = $LASTEXITCODE
Write-Host "    sc delete -> exit $delExit : $delOut"
$svcAfterDel = Get-Service MONKMODE -ErrorAction SilentlyContinue
Check "D1 sc delete refused during active block"          ($delExit -ne 0 -or $delOut -match 'denied|FAILED')
Check "D1 MONKMODE service still present after refusal"   ($null -ne $svcAfterDel)

# D2: deny-DELETE self-heal. An admin holding WRITE_DAC can strip the ACE (a
#     casual `sc sdset`/Process-Explorer re-ACL); the service's next tick must
#     re-assert it (mirrors the hosts read-only + SafeBoot self-heal). 15s bound
#     = one full 10s tick + slack, like the B1/B3 drills.
Write-Host "  D2: stripping the deny-DELETE ACE (sc sdset)..."
Remove-ServiceDenyDelete
$strippedNow = -not (Get-ServiceSddl).Contains($denyAce)
Check "D2 deny-DELETE ACE stripped (tamper applied)"      $strippedNow
$t = Wait-Condition { (Get-ServiceSddl).Contains($denyAce) } 15
Write-Host "    deny-DELETE ACE re-asserted after ${t}s (service tick is 10s)"
Check "D2 service re-asserted deny-DELETE within 15s"     ($t -ge 0)

if ($IncludeClockTest) {
  Write-Host "`n=== 2f. B4 CLOCK ROLLBACK (optional): a jump past Until must NOT lift ===" -ForegroundColor Cyan
  # C1: roll the SYSTEM CLOCK forward past the block's Until. B4 decides expiry
  #     off a monotonic high-water mark that only advances <=120s/tick and NEVER
  #     on a jump, so the block must stay enforced. The clock is RESTORED in the
  #     finally (set back to t0 + real elapsed) so the rest of the run — and the
  #     genuine 5-min expiry — proceed normally. RISK: if this script is killed
  #     mid-drill the clock is left ~+12min until the finally/an external resync.
  $t0 = Get-Date
  $drillSw = [System.Diagnostics.Stopwatch]::StartNew()   # monotonic, immune to the clock jump below
  $clockDrill = $false; $stillBlocked = $false
  try {
    Set-Date -Date $t0.AddMinutes(10) -ErrorAction Stop | Out-Null
    $clockDrill = $true
    Start-Sleep -Seconds 12   # let >=1 tick observe (and refuse) the jump
    $hostsC = Get-Content $hosts -Raw
    $svcC = Get-Service MONKMODE -ErrorAction SilentlyContinue
    $stillBlocked = ($hostsC -match '#### MonkMode Entries ####') -and ($svcC -and $svcC.Status -eq 'Running')
  } finally {
    # restore to t0 + REAL elapsed. Measure with a monotonic Stopwatch, NOT
    # (Get-Date)-$t0: the wall clock is +10m in here, so that subtraction baked the
    # +10m straight back in and left the system clock ~10 min fast (root cause of the
    # 53/10 run on 14/06/2026). Stopwatch elapsed is clock-immune.
    $drillSw.Stop()
    try { Set-Date -Date $t0.AddSeconds([math]::Round($drillSw.Elapsed.TotalSeconds)) -ErrorAction Stop | Out-Null } catch {}
    try { w32tm /resync /force 2>$null | Out-Null } catch {}
  }
  Check "B4 clock drill applied (clock moved +10m)"        $clockDrill
  Check "B4 block survived a forward clock jump past Until" $stillBlocked
  Write-Host "    clock restored (now $(Get-Date -Format 'HH:mm:ss'))"
}

Write-Host "`n=== 2g. B5a DoH: the browser Secure-DNS-off policy must self-heal ===" -ForegroundColor Cyan
# An admin flips a browser's DoH back on mid-block (here: delete the Edge policy
# value) -> the service's next tick must re-force it OFF. 15s = one full 10s tick
# + slack, exactly like the B3 SafeBoot / B6 deny-DELETE self-heal drills.
$edge = $dohEntries[0]
Write-Host "  deleting the Edge DnsOverHttpsMode policy value (DoH tamper)..."
Remove-ItemProperty -LiteralPath $edge.Path -Name $edge.Name -Force -ErrorAction SilentlyContinue
Check "B5a Edge DoH value cleared (tamper applied)"          ((DohVal $edge.Path $edge.Name) -cne 'off')
$t = Wait-Condition { (DohVal $edge.Path $edge.Name) -ceq 'off' } 15
Write-Host "    Edge DoH policy re-forced off after ${t}s (service tick is 10s)"
Check "B5a service re-forced Edge DoH off within 15s"        ($t -ge 0)
# FUNCTIONAL PROOF (needs a real browser; do this by hand during the run, it is
# NOT a scored check): with the block live, open Edge/Chrome/Brave -> Settings ->
# Privacy and confirm "Use secure DNS" is OFF and greyed/managed; try a site that
# ONLY resolves via a DoH provider and confirm it fails / falls back to the
# hosts-filtered system resolver. That is the real bypass this closes.

Write-Host "`n=== 3. Waiting for the block to auto-lift (5-min block)... ===" -ForegroundColor Cyan
Write-Host "    Watch for a tray balloon/toast when it ends." -ForegroundColor Yellow
$deadline = $blockStart.AddSeconds(420)   # 5-min block + 2 min slack
$lifted = $false
$liftAt = $null
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 10
  $s = Get-Service MONKMODE -ErrorAction SilentlyContinue
  $h = Get-Content $hosts -Raw
  $stillRunning = ($s -and $s.Status -eq 'Running')
  $markerGone   = ($h -notmatch '#### MonkMode Entries ####')
  Write-Host ("    [{0:HH:mm:ss}] service={1} markerPresent={2}" -f (Get-Date), $(if($s){$s.Status}else{'gone'}), (-not $markerGone))
  if (-not $stillRunning -and $markerGone) { $lifted = $true; $liftAt = Get-Date; break }
}

Write-Host "`n=== 4. Verifying the block LIFTED ===" -ForegroundColor Cyan
$hostsText2 = Get-Content $hosts -Raw
$svc2 = Get-Service MONKMODE -ErrorAction SilentlyContinue
Check "block auto-lifted within timeout"      $lifted
# EARLY-LIFT GUARD (added 14/06/2026): the block is --for 5 (300s), so a genuine
# lift is ALWAYS at >= ~Until. HighWater only LAGS real time; it never lifts the
# block early. A lift well before +300s means something SHORTENED the block (the
# notifier clock-comp writing a past Until - the bug the -IncludeClockTest drill
# exposed, which still reported 63/0 because section 4 only checked THAT it lifted,
# not WHEN). 285s = 300s Until - 5s grace - 10s slack; the bug lifted at ~+120s.
$liftElapsed = if ($liftAt) { [int]($liftAt - $blockStart).TotalSeconds } else { -1 }
Write-Host "    block lifted at +${liftElapsed}s after start (Until ~+300s; a legit lift is >= ~+300s)"
Check "block did NOT lift EARLY (>= ~Until; no clock-comp shortening)" ($lifted -and ($null -ne $liftAt) -and ($liftAt -ge $blockStart.AddSeconds(285)))
Check "MonkMode marker removed from hosts"    ($hostsText2 -notmatch '#### MonkMode Entries ####')
# 313(b): a NATURAL expiry must leave hosts an ordinary WRITABLE file. The read-only
# attribute is enforcement (the DNS-client lock) and there is nothing left to enforce
# once our block is gone; leaving it set made every later hosts writer (Tailscale, a
# DNS tool) fail until a manual `attrib -r`. Unit tests pin the decision
# (Service1.StripHostsBlockAtExpiry) against a temp file - this is the LIVE proof.
# Polled, not read once: the strip and the attribute clear are two statements, and the
# lift loop above can observe the marker gone microseconds before the second one runs.
$roCleared = $false
foreach ($i in 1..10) {
  if (-not ((Get-Item $hosts).Attributes -band [IO.FileAttributes]::ReadOnly)) { $roCleared = $true; break }
  Start-Sleep -Milliseconds 500
}
Check "hosts NOT read-only after expiry (no manual attrib -r owed)" $roCleared
Check "service no longer running"             (-not ($svc2 -and $svc2.Status -eq 'Running'))
Check "snapshot deleted after lift"           (-not (Test-Path $snap))
# B3: stopMe() must remove both SafeBoot keys at a genuine expiry — a lifted
# block leaves nothing that would start the (now stopped) service in Safe Mode.
Check "B3 SafeBoot keys removed after expiry" ((-not (Test-Path $sbMin)) -and (-not (Test-Path $sbNet)))
# B5a: stopMe() -> RemoveDohPolicy must RESTORE every browser DoH policy value to
# its EXACT pre-block state (no data loss) and CONSUME the snapshot. On a clean box
# with no prior policy that means every value is gone; if the box had priors they
# must be back verbatim - this compares against $dohPre either way.
$dohRestored = $true
foreach ($e in $dohEntries) {
  if ("$(DohVal $e.Path $e.Name)" -cne "$($dohPre["$($e.Path)|$($e.Name)"])") { $dohRestored = $false }
}
Check "B5a DoH policy restored to pre-block state after expiry" $dohRestored
Check "B5a DoH snapshot consumed after expiry"                  (-not (Test-Path $dohSnap))
# B6: stopMe()'s RestoreDefaultServiceSd must remove the deny-DELETE ACE at a
# genuine expiry, so a lifted block leaves a fully REMOVABLE service. (Probed
# before teardown deletes the service. If the service object is already gone the
# SDDL read is empty — also 'no deny ACE'.)
Check "B6 deny-DELETE ACE removed after expiry (service removable)" (-not (Get-ServiceSddl).Contains($denyAce))
ipconfig /flushdns | Out-Null
$addrs2 = Resolve-Example
Write-Host "    example.com now resolves to: $($addrs2 -join ', ')"
Check "example.com no longer sinkholed"       (-not ($addrs2 -contains '127.0.0.1') -and -not ($addrs2 -contains '0.0.0.0'))

# B1: the expiry teardown must be FINAL. stopMe() Ends the process while the
# SCM recovery policy (3x RESTART, INFINITE reset) is still armed and the
# guardian may still be mid-tick — the no-restart-loop interaction was only
# ever reasoned about + verifier-walked, never run live. Tight 500ms poll so
# even a fast kill/respawn cycle (~1.5s+) can't slip between samples.
Write-Host "    watching 30s for a restart loop / stray processes..."
$loopSeen = $false; $guardSeen = $false
$watch = [System.Diagnostics.Stopwatch]::StartNew()
while ($watch.Elapsed.TotalSeconds -lt 30) {
  $s = Get-Service MONKMODE -ErrorAction SilentlyContinue
  if ($s -and ($s.Status -eq 'Running' -or $s.Status -eq 'StartPending')) { $loopSeen = $true }
  if (Get-Process MonkMode_srv -ErrorAction SilentlyContinue) { $loopSeen = $true }
  if (Get-Process mm_guard -ErrorAction SilentlyContinue) { $guardSeen = $true }
  Start-Sleep -Milliseconds 500
}
Check "service STAYS stopped after expiry (no restart loop)" (-not $loopSeen)
Check "no stray mm_guard after expiry"                       (-not $guardSeen)
# the notifier shows the toast, then self-exits (~11s after Done=yes); by now
# that window has long passed, but allow a short grace.
$tBye = Wait-Condition { $null -eq (Get-Process mm_notify -ErrorAction SilentlyContinue) } 15
Check "mm_notify exited after the toast (no stray)"          ($tBye -ge 0)

Write-Host "`n=== 4b. B5a NO-DATA-LOSS: a user's own DoH-off must survive a no-snapshot teardown ===" -ForegroundColor Cyan
# Regression for the double-teardown clobber (verifier P2, fixed 01/07/2026). The
# block has lifted and CONSUMED its DoH snapshot. Simulate a security-conscious user
# who now sets DoH off THEMSELVES, then run the escape hatch: with NO snapshot,
# RemoveDohPolicy must DO NOTHING (never delete a value it can't prove it created),
# so the user's own value SURVIVES. Before the fix this deleted it (un-hardening the
# box). Section 5 below restores the true pre-block state ($dohPre).
$edge0 = $dohEntries[0]
New-Item -Path $edge0.Path -Force -ErrorAction SilentlyContinue | Out-Null
Set-ItemProperty -LiteralPath $edge0.Path -Name $edge0.Name -Value 'off' -Type String -ErrorAction SilentlyContinue
& $monk unblock --force | Out-Null
Check "B5a user's own DoH-off survived the no-snapshot teardown" ((DohVal $edge0.Path $edge0.Name) -ceq 'off')

# 5. teardown ---------------------------------------------------------------
Write-Host "`n=== 5. Teardown (sc delete + restore hosts + clear notifier) ===" -ForegroundColor Cyan
# B1 made the pair self-restarting: disable SCM recovery and kill the guardian
# BEFORE (well, alongside) the service so nothing resurrects anything
# mid-teardown. After a clean expiry these are all already gone — this is the
# failure-path rescue.
cmd /c 'sc failure MONKMODE reset= 0 actions= ""' | Out-Null
foreach ($i in 1..6) {
  Get-Process mm_guard     -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Get-Process MonkMode_srv -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  if (-not (Get-Process mm_guard -ErrorAction SilentlyContinue) -and
      -not (Get-Process MonkMode_srv -ErrorAction SilentlyContinue)) { break }
  Start-Sleep -Milliseconds 500
}
Get-Process mm_notify -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-ItemProperty $runKey -Name MonkMode_notify -ErrorAction SilentlyContinue
# B6: strip the deny-DELETE ACE BEFORE 'sc delete' — otherwise the ACE we just
# tested refuses our own delete (sc runs as BA) and the service would be left
# orphaned + undeletable. Safe now: the service is killed above, so it cannot
# re-assert the ACE between this strip and the delete; we hold WRITE_DAC.
Remove-ServiceDenyDelete
sc.exe delete MONKMODE | Out-Null
Remove-Item $snap -Force -ErrorAction SilentlyContinue   # never leave a stale snapshot to resurrect old sites
Remove-Item -LiteralPath $sbMin, $sbNet -Recurse -Force -ErrorAction SilentlyContinue  # B3: never leave orphaned SafeBoot keys
# B5a: restore each browser DoH policy value to its PRE-BLOCK state (remove ours
# where there was no prior), so the smoke leaves the box's DoH settings untouched.
foreach ($e in $dohEntries) {
  $prior = $dohPre["$($e.Path)|$($e.Name)"]
  if ($null -eq $prior) {
    Remove-ItemProperty -LiteralPath $e.Path -Name $e.Name -Force -ErrorAction SilentlyContinue
  } else {
    New-Item -Path $e.Path -Force -ErrorAction SilentlyContinue | Out-Null
    Set-ItemProperty -LiteralPath $e.Path -Name $e.Name -Value $prior -Type $e.Kind -ErrorAction SilentlyContinue
  }
}
Remove-Item $dohSnap -Force -ErrorAction SilentlyContinue   # B5a: never leave a stale DoH snapshot
$h = Get-Item $hosts
if ($h.Attributes -band [IO.FileAttributes]::ReadOnly) { $h.Attributes = $h.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) }
Copy-Item $backup $hosts -Force
ipconfig /flushdns | Out-Null
Write-Host "    Stripped B6 deny-DELETE ACE, restored hosts from backup, deleted MONKMODE service, killed guardian/notifier, cleared autorun, removed snapshot + SafeBoot keys."

Write-Host "`n================ RESULT: $pass passed, $fail failed ================" -ForegroundColor $(if($fail -eq 0){'Green'}else{'Red'})
if ($fail -ne 0) {
  Write-Host "If something is stuck, run cleanup.ps1 (elevated)." -ForegroundColor Yellow
}
"SMOKETEST_RESULT pass=$pass fail=$fail"
try { Stop-Transcript | Out-Null }
catch {}
