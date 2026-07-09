# MonkMode clock drills (RUN ELEVATED) - B4 forward-jump + B1c backward-roll.
#
# Proves the two clock-attack defences live, against the real service:
#   B4  (forward jump past Until must NOT lift): HighWater is a monotonic mark
#       that only advances on REAL elapsed (<=120s/tick) and never on a wall
#       jump, so pushing the clock past the block's Until keeps it enforced.
#   B1c (backward roll must NOT over-extend): AdvanceHighWater credits
#       min(real-monotonic-elapsed, 120s) even on a backward roll, so the block
#       still ends at its REAL duration - a backward clock cannot freeze it open.
#
# SAFETY - guaranteed restore, no gaps:  every manipulation is
#   anchor(wall) + Stopwatch(monotonic)  ->  Set-Date jump  ->  assert  ->
#   FINALLY Set-Date (anchor + real elapsed)  ->  verify restored.
# The Stopwatch is immune to the wall-clock jump, so the restore is always the
# TRUE current time. NO 'w32tm /resync' is used (it can block/hang; run-smoketest
# 2f died there). w32time still keeps truth as a backstop. This script NEVER
# leaves the clock jumped: the finally runs even on assertion failure or Ctrl-C.
#
# Run this FOREGROUND, UNBUFFERED and WATCHED (each drill < 2 min). The try/finally
# restores on every code path - but only an in-process exit runs the finally. An
# EXTERNAL hard-kill (a bash/CI timeout wrapping the run, a taskkill) mid-drill
# skips the finally and leaves the clock jumped. So NEVER run this piped under an
# external timeout or detached; run it directly in a watched console and confirm
# the post-drill NTP line yourself. (Learned the hard way 2026-07-09.)
#
# Usage (ELEVATED):  powershell -ExecutionPolicy Bypass -File tools\smoke\clock-drill-test.ps1
param([string]$Dist, [switch]$SkipBackward)

$ErrorActionPreference = 'Continue'
if (-not $Dist) {
  if ($PSScriptRoot) { $Dist = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'dist' }
  else               { $Dist = 'C:\Users\samra\repos\monk-mode\dist' }
}
$monk  = Join-Path $Dist 'monkmode.exe'
$hosts = "$env:SystemRoot\System32\drivers\etc\hosts"
$cleanup = Join-Path $PSScriptRoot 'cleanup.ps1'
$pass = 0; $fail = 0
function Check($n,$c){ if($c){Write-Host "  [PASS] $n" -ForegroundColor Green;$script:pass++}else{Write-Host "  [FAIL] $n" -ForegroundColor Red;$script:fail++} }
function SvcState { $s=Get-Service MONKMODE -ErrorAction SilentlyContinue; if($s){$s.Status}else{'gone'} }
function HostsBlocked { (try{Get-Content $hosts -Raw}catch{''}) -match '#### MonkMode Entries ####' }
function ForceDown { & $monk unblock --force 2>&1|Out-Null; $u=(Get-Date).AddSeconds(25); while((Get-Date)-lt $u -and (SvcState)-ne 'gone'){Start-Sleep -Milliseconds 500}; if((SvcState)-ne 'gone'){& powershell -ExecutionPolicy Bypass -File $cleanup -Dist $Dist 2>&1|Out-Null} }
# True clock offset (seconds) vs an external HTTP Date header, WITHOUT setting
# the clock. $null if unreachable. NB: uses an 8s-bounded HTTP HEAD, NOT
# 'w32tm /stripchart' or '/resync' - those can BLOCK indefinitely on a slow NTP
# source and hung this script (2026-07-09), so it ran invisibly into a drill and
# an external timeout killed it mid-jump, leaving the clock +30m. Never block here.
function NtpOffset {
  try {
    $r = Invoke-WebRequest -Uri 'https://www.cloudflare.com' -Method Head -UseBasicParsing -TimeoutSec 8
    $u = [DateTime]::Parse($r.Headers['Date'], [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
    return ((Get-Date).ToUniversalTime() - $u).TotalSeconds
  } catch { return $null }
}

# --- preconditions -----------------------------------------------------------
$me=[Security.Principal.WindowsPrincipal]([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){Write-Host 'Run ELEVATED.' -ForegroundColor Red;exit 1}
if(-not (Test-Path $monk)){Write-Host "monkmode.exe not at $monk." -ForegroundColor Red;exit 1}
if(Get-Service MONKMODE -ErrorAction SilentlyContinue){Write-Host 'MONKMODE exists - cleanup first.' -ForegroundColor Yellow;exit 1}
& $monk setup --partner 'Smoke Tester (smoke@test.local)' 2>&1|Out-Null
$off0 = NtpOffset
Write-Host ("Pre-drill NTP offset: {0}s" -f $off0)
Check "clock within 5s of NTP before drills" ($null -ne $off0 -and [math]::Abs($off0) -lt 5)

# --- FEASIBILITY probe: will w32time let a manual Set-Date persist ~8s? -------
# If w32time immediately yanks a manual jump back, the service can't observe it
# and the drill is moot (and safe). We probe with a tiny +90s jump.
Write-Host "`n=== feasibility probe (does a manual clock jump persist a tick?) ===" -ForegroundColor Cyan
$anchor=Get-Date; $sw=[Diagnostics.Stopwatch]::StartNew(); $held=$false
try { Set-Date -Date $anchor.AddSeconds(90) -ErrorAction Stop|Out-Null; Start-Sleep -Seconds 8; $held=((Get-Date)-$anchor).TotalSeconds -gt 60 }
finally { $sw.Stop(); Set-Date -Date $anchor.AddSeconds([math]::Round($sw.Elapsed.TotalSeconds)) -ErrorAction SilentlyContinue|Out-Null }
Check "clock restored after probe (within 5s of NTP)" ([math]::Abs(([double](NtpOffset))) -lt 5)
if (-not $held) {
  Write-Host "w32time is yanking manual jumps back within 8s - the drills cannot be observed by the service." -ForegroundColor Yellow
  Write-Host "DEFERRING the live clock drills (moot under active time-sync). B4/B1c remain unit-pinned." -ForegroundColor Yellow
  Write-Host "`n================ CLOCK RESULT: $pass passed, $fail failed (drills DEFERRED) ================"
  "CLOCK_RESULT pass=$pass fail=$fail deferred=1"
  return
}
Write-Host "  probe held - proceeding with the drills." -ForegroundColor Green

try {
  # --- B4: forward jump past Until must NOT lift ------------------------------
  Write-Host "`n=== B4: forward clock jump past Until must NOT lift ===" -ForegroundColor Cyan
  & $monk block --sites example.com --for 3 2>&1|Out-Null; Start-Sleep -Seconds 3
  Check "B4 block armed" ((SvcState) -eq 'Running')
  Start-Sleep -Seconds 12   # let HighWater seed on real ticks
  $anchor=Get-Date; $sw=[Diagnostics.Stopwatch]::StartNew(); $survived=$false
  try {
    Set-Date -Date $anchor.AddMinutes(30) -ErrorAction Stop|Out-Null   # jump +30m, well past the +3m Until
    Start-Sleep -Seconds 14   # >=1 tick observes (and must refuse) the jump
    $survived = ((SvcState) -eq 'Running') -and (HostsBlocked)
  } finally {
    $sw.Stop(); Set-Date -Date $anchor.AddSeconds([math]::Round($sw.Elapsed.TotalSeconds)) -ErrorAction SilentlyContinue|Out-Null
  }
  Check "B4 block SURVIVED a +30m forward jump past Until" $survived
  Check "B4 clock restored after drill (within 5s of NTP)" ([math]::Abs(([double](NtpOffset))) -lt 5)
  ForceDown

  # --- B1c: backward roll must NOT over-extend -------------------------------
  if (-not $SkipBackward) {
    Write-Host "`n=== B1c: backward clock roll must NOT over-extend the block ===" -ForegroundColor Cyan
    & $monk block --sites example.com --for 1 2>&1|Out-Null; Start-Sleep -Seconds 3
    Check "B1c block armed (--for 1)" ((SvcState) -eq 'Running')
    $anchor=Get-Date; $sw=[Diagnostics.Stopwatch]::StartNew(); $liftedAt=-1
    try {
      Start-Sleep -Seconds 8                                   # a little real elapsed first
      Set-Date -Date $anchor.AddMinutes(-30) -ErrorAction Stop|Out-Null   # roll BACK 30m mid-block
      # wait on REAL monotonic time (immune to the wall roll) up to ~95s; the
      # block's real duration is 60s, so a correct fix lifts by ~60-75s real.
      while ($sw.Elapsed.TotalSeconds -lt 95) {
        if ((SvcState) -eq 'gone') { $liftedAt = [int]$sw.Elapsed.TotalSeconds; break }
        Start-Sleep -Milliseconds 1000
      }
    } finally {
      $sw.Stop(); Set-Date -Date $anchor.AddSeconds([math]::Round($sw.Elapsed.TotalSeconds)) -ErrorAction SilentlyContinue|Out-Null
    }
    Write-Host ("    block lifted at ~{0}s real (real duration 60s; over-extend bug would exceed 90s)" -f $liftedAt)
    Check "B1c block lifted at its REAL ~60s duration despite the -30m roll (not frozen open)" ($liftedAt -ge 55 -and $liftedAt -le 90)
    Check "B1c clock restored after drill (within 5s of NTP)" ([math]::Abs(([double](NtpOffset))) -lt 5)
    ForceDown
  }
}
finally {
  ForceDown
  $offZ = NtpOffset
  Write-Host ("Post-drill NTP offset: {0}s" -f $offZ)
  Check "clock within 5s of NTP after ALL drills" ($null -ne $offZ -and [math]::Abs($offZ) -lt 5)
}

Write-Host "`n================ CLOCK RESULT: $pass passed, $fail failed ================" -ForegroundColor $(if($fail -eq 0){'Green'}else{'Red'})
"CLOCK_RESULT pass=$pass fail=$fail"
