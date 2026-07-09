# MonkMode CV + Section-D live smoke (RUN ELEVATED).
#
# Covers the owed live checks that run-smoketest.ps1 does NOT (it proves the
# B1-B7 enforcement core). This proves the C-core accountability model and the
# Section-D usability surface, end-to-end against the real service:
#
#   CV1  partner-code verify + rotate  (each block mints a fresh one-time code;
#        the service KDF-verifies `unblock --code`, a wrong code stays blocked)
#   CV2  committed block = code-only exit  (bare `unblock` is refused; the code
#        still lifts it)                                                    [+D5]
#   CV3  cooling-off trigger flow  (bare `unblock` registers a service-computed
#        ~1h pending; `--cancel` clears it)                                 [+D5]
#   D1a  --preset expands to its domains (social -> reddit.com et al in hosts)
#   D2a  --app-preset expands to its apps (games -> steam.exe in the config)
#   D1b/D2b  account-default blocklist + app list inherit (both dimensions) when
#        a block names no source of its own
#   D3   `monkmode stats` renders the recorded history
#   D5   `status` renders the committed / cooling-off / schedule exit lines
#   #2   `schedule --clear` tears an armed schedule down cleanly (no orphan)
#   D2c  `--all-session-kill` arms AllSession=yes (MAC-covered) and the service
#        kills the blocked app; the pure cross-SESSION widening is unit-pinned
#        (ProcessInKillScope) and needs a 2nd interactive session to eyeball.
#
# Every block uses a SHORT --for (minutes) as a safety floor and is torn down
# immediately with `unblock --force` (unconditional, data-loss-safe hosts strip).
# A global finally runs cleanup.ps1 so an abort never leaves the box blocked.
# NO clock manipulation here (see clock-drill-test.ps1 for B1c/B4).
#
# Usage (ELEVATED):  powershell -ExecutionPolicy Bypass -File tools\smoke\cv-d-smoke.ps1
param([string]$Dist)

$ErrorActionPreference = 'Continue'
if (-not $Dist) {
  if ($PSScriptRoot) { $Dist = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'dist' }
  else               { $Dist = 'C:\Users\samra\repos\monk-mode\dist' }
}
$monk    = Join-Path $Dist 'monkmode.exe'
$ini     = Join-Path $Dist 'monkmode_settings.ini'
$hosts   = "$env:SystemRoot\System32\drivers\etc\hosts"
$cleanup = Join-Path $PSScriptRoot 'cleanup.ps1'
$testExe = Join-Path $env:TEMP 'mmsmoke_testapp.exe'
$FOR     = 5   # minutes: safety floor with headroom so a block never auto-expires mid-section
               # (--for 2 raced the multi-step assertions); every block is force-unblocked first
$pass = 0; $fail = 0
function Check($name, $cond) {
  if ($cond) { Write-Host "  [PASS] $name" -ForegroundColor Green; $script:pass++ }
  else       { Write-Host "  [FAIL] $name" -ForegroundColor Red;   $script:fail++ }
}
function SvcState { $s = Get-Service MONKMODE -ErrorAction SilentlyContinue; if ($s) { "$($s.Status)" } else { 'gone' } }
function WaitSvc([string]$want, [int]$sec) {
  $u = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $u) { if ((SvcState) -eq $want) { return $true }; Start-Sleep -Milliseconds 500 }
  return ((SvcState) -eq $want)
}
# Arm a block; return its stdout lines. Waits for the service to come up.
function Arm([string]$argline) {
  $out = & $monk block @($argline -split ' ') 2>&1 | ForEach-Object { "$_" }
  [void](WaitSvc 'Running' 60)   # poll, not a fixed sleep: install+start races StartPending under load
  Start-Sleep -Seconds 2         # small settle for the hosts write + config flush
  return $out
}
# The one-time accountability code is printed on the line after the "Emergency
# unlock code" header, indented. Return it trimmed, or $null.
function ParseCode($lines) {
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'Emergency unlock code') { if ($i + 1 -lt $lines.Count) { return $lines[$i + 1].Trim() } }
  }
  return $null
}
function ForceDown {
  & $monk unblock --force 2>&1 | Out-Null
  if (-not (WaitSvc 'gone' 25)) { & powershell -ExecutionPolicy Bypass -File $cleanup -Dist $Dist 2>&1 | Out-Null }
}
function HostsHas([string]$re) { $t = try { Get-Content $hosts -Raw } catch { '' }; return ($t -match $re) }
function IniHas([string]$re)   { $t = try { Get-Content $ini -Raw } catch { '' }; return ($t -match $re) }

# --- 0. preconditions --------------------------------------------------------
$me = [Security.Principal.WindowsPrincipal]([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Write-Host 'Run ELEVATED.' -ForegroundColor Red; exit 1 }
if (-not (Test-Path $monk)) { Write-Host "monkmode.exe not at $monk - build dist first." -ForegroundColor Red; exit 1 }
if (Get-Service MONKMODE -ErrorAction SilentlyContinue) { Write-Host 'MONKMODE already exists - run cleanup.ps1 first.' -ForegroundColor Yellow; exit 1 }
& $monk setup --partner 'Smoke Tester (smoke@test.local)' 2>&1 | Out-Null   # idempotent; ensures SetupIsComplete

try {
  # --- CV2 + CV1 + D5: committed block = code-only exit; code verify ----------
  Write-Host "`n=== CV2/CV1/D5: committed block, code-only exit, code verify ===" -ForegroundColor Cyan
  $outA = Arm "--sites example.com --commit --for $FOR"
  Check "committed block armed (service running)" ((SvcState) -eq 'Running')
  $codeA = ParseCode $outA
  Check "CV1 one-time accountability code minted" ($codeA -and $codeA.Length -ge 6)
  $st = & $monk status 2>&1 | ForEach-Object { "$_" }
  Check "D5 status shows committed code-only exit line" (($st -join "`n") -match 'committed block')
  # bare unblock must be REFUSED on a committed block (exit 1)
  & $monk unblock 2>&1 | Out-Null
  Check "CV2 bare 'unblock' refused on committed block (exit 1)" ($LASTEXITCODE -eq 1)
  Check "CV2 block still enforced after refused unblock" ((SvcState) -eq 'Running' -and (HostsHas 'example\.com'))
  # the correct code lifts it (service adjudicates on its tick)
  & $monk unblock --code $codeA 2>&1 | Out-Null
  Check "CV1 correct code lifted the block (service verified, <=40s)" (WaitSvc 'gone' 40)
  Check "CV1 hosts block removed after code lift" (-not (HostsHas '#### MonkMode Entries ####'))
  ForceDown

  # --- CV1 rotate + CV3 cooling-off + D5 --------------------------------------
  Write-Host "`n=== CV1(rotate)/CV3/D5: fresh code per block; cooling-off flow ===" -ForegroundColor Cyan
  $outB = Arm "--sites example.com --for $FOR"
  $codeB = ParseCode $outB
  Check "CV1 rotate: 2nd block minted a DIFFERENT code" ($codeB -and $codeB -ne $codeA)
  # wrong code must NOT lift
  & $monk unblock --code "WRONG-$codeB" 2>&1 | Out-Null
  Start-Sleep -Seconds 16
  Check "CV1 wrong code did NOT lift (still enforced)" ((SvcState) -eq 'Running')
  # bare unblock registers a service-computed ~1h cooling-off; status reflects it
  & $monk unblock 2>&1 | Out-Null
  Start-Sleep -Seconds 24        # >2 service ticks: the deadline is computed on a tick
  $st2 = (& $monk status 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Check "CV3/D5 status shows cooling-off pending (~active-time countdown)" ($st2 -match 'cooling-off pending')
  Check "CV3 block stays FULLY enforced during cooling-off" ((SvcState) -eq 'Running' -and (HostsHas 'example\.com'))
  & $monk unblock --cancel 2>&1 | Out-Null
  Start-Sleep -Seconds 24        # >2 ticks for the cancel to clear the pending
  $st3 = (& $monk status 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Check "CV3 cooling-off cancelled (pending cleared)" ($st3 -notmatch 'cooling-off pending')
  ForceDown

  # --- D1a: --preset expands to its domains -----------------------------------
  Write-Host "`n=== D1a: --preset social expands to its domains ===" -ForegroundColor Cyan
  Arm "--preset social --for $FOR" | Out-Null
  Check "D1a social preset armed (service running)" ((SvcState) -eq 'Running')
  Check "D1a preset expanded: reddit.com in hosts"    (HostsHas '127\.0\.0\.1\s+reddit\.com')
  Check "D1a preset expanded: facebook.com in hosts"  (HostsHas '127\.0\.0\.1\s+facebook\.com')
  ForceDown

  # --- D2a: --app-preset expands to its apps ----------------------------------
  Write-Host "`n=== D2a: --app-preset games expands to its apps ===" -ForegroundColor Cyan
  Arm "--app-preset games --for $FOR" | Out-Null
  Check "D2a games app-preset armed (service running)" ((SvcState) -eq 'Running')
  $stApps = (& $monk status 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Check "D2a app-preset expanded: steam.exe in status/config" ($stApps -match 'steam\.exe' -or (IniHas 'steam\.exe'))
  ForceDown

  # --- D1b/D2b: account defaults inherit when a block names no source ----------
  Write-Host "`n=== D1b/D2b: default site + app lists inherit (both dimensions) ===" -ForegroundColor Cyan
  & $monk setup --partner 'Smoke Tester' --default-sites inherit-test.example --default-apps mmsmoke_dfl.exe 2>&1 | Out-Null
  Arm "--for $FOR" | Out-Null    # NO --sites/--apps: must inherit both defaults
  Check "D1b default SITE inherited (inherit-test.example in hosts)" (HostsHas '127\.0\.0\.1\s+inherit-test\.example')
  $stDfl = (& $monk status 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Check "D2b default APP inherited (mmsmoke_dfl.exe in status/config)" ($stDfl -match 'mmsmoke_dfl\.exe' -or (IniHas 'mmsmoke_dfl\.exe'))
  ForceDown
  & $monk setup --partner 'Smoke Tester (smoke@test.local)' 2>&1 | Out-Null   # reset: clear the test defaults

  # --- D3: stats renders recorded history -------------------------------------
  Write-Host "`n=== D3: monkmode stats ===" -ForegroundColor Cyan
  $stats = (& $monk stats 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Write-Host $stats
  Check "D3 stats renders a block history (Blocks started: N)" ($stats -match 'Blocks started:\s+[1-9]')

  # --- #2 + D5: schedule arm -> status window state -> clear tears down --------
  Write-Host "`n=== #2/D5: schedule arm, status window state, --clear teardown ===" -ForegroundColor Cyan
  & $monk schedule --sites example.com --windows "Mon-Fri 09:00-17:00" 2>&1 | Out-Null
  [void](WaitSvc 'Running' 60)
  Check "schedule armed (service running)" ((SvcState) -eq 'Running')
  $stSch = (& $monk status 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Check "D5 status shows the armed schedule window state" ($stSch -match '(?i)window|schedule')
  & $monk schedule --clear 2>&1 | Out-Null
  Check "#2 schedule --clear tore the service down (no orphan; window closed at 03:xx)" (WaitSvc 'gone' 35)
  ForceDown

  # --- D2c: --all-session-kill arms AllSession=yes; the service kills the app --
  Write-Host "`n=== D2c: --all-session-kill arm + live app kill ===" -ForegroundColor Cyan
  Copy-Item 'C:\Windows\System32\PING.EXE' $testExe -Force
  Start-Process -FilePath $testExe -ArgumentList '-t','127.0.0.1' -WindowStyle Hidden | Out-Null
  Start-Sleep -Seconds 1
  Check "D2c test app (mmsmoke_testapp.exe) running pre-block" ($null -ne (Get-Process mmsmoke_testapp -ErrorAction SilentlyContinue))
  Arm "--apps mmsmoke_testapp.exe --all-session-kill --for $FOR" | Out-Null
  Check "D2c block armed (service running)" ((SvcState) -eq 'Running')
  Check "D2c v9 config carries AllSession=yes (MAC-covered)" (IniHas '(?im)^\s*AllSession\s*=\s*yes')
  $killed = $false
  $u = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $u) { if ($null -eq (Get-Process mmsmoke_testapp -ErrorAction SilentlyContinue)) { $killed = $true; break }; Start-Sleep -Milliseconds 500 }
  Check "D2c service KILLED the blocked app within 30s" $killed
  Write-Host "  NOTE: cross-SESSION widening (session 1+) needs a 2nd interactive login to eyeball;" -ForegroundColor Yellow
  Write-Host "        the ProcessInKillScope truth-table is unit-pinned. Flagged for a 2-min FUS check." -ForegroundColor Yellow
  ForceDown
}
finally {
  Write-Host "`n=== teardown (force-unblock + cleanup.ps1 + remove test app) ===" -ForegroundColor Cyan
  & $monk unblock --force 2>&1 | Out-Null
  & powershell -ExecutionPolicy Bypass -File $cleanup -Dist $Dist 2>&1 | Out-Null
  Get-Process mmsmoke_testapp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Remove-Item $testExe -Force -ErrorAction SilentlyContinue
}

Write-Host "`n================ CV/D RESULT: $pass passed, $fail failed ================" -ForegroundColor $(if($fail -eq 0){'Green'}else{'Red'})
"CVD_RESULT pass=$pass fail=$fail"
