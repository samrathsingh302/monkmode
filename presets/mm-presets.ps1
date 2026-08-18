# MonkMode preset commands - dot-sourced from the PowerShell profile.
# To change WHAT is blocked or for HOW LONG, edit the .txt lists and defaults.ini
# in this folder - you should not need to touch this file.
# Every command needs an ELEVATED terminal: Win+X -> Terminal (Admin), then type
# `powershell` (the mm-* commands live in the Windows PowerShell profile, not pwsh).
# NEVER pipe or capture the output of an arming command (mm-video | tee, > file):
# the one-time partner code prints once, and captured output has wedged shells before.

# Versioned home since 03/08/2026: this folder (repos\monk-mode\presets) is the live layer,
# tracked in git. C:\Users\samra\monkmode-lists\ is the retired pre-versioning copy.
$global:MonkModeLists = 'C:\Users\samra\repos\monk-mode\presets'

function MM-IsAdmin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function MM-Sites([string]$file) {
    (Get-Content (Join-Path $global:MonkModeLists $file) |
        Where-Object { $_ -match '\S' -and $_.Trim() -notmatch '^#' } |
        ForEach-Object { $_.Trim() }) -join ','
}

function MM-Default([string]$key) {
    $line = Get-Content (Join-Path $global:MonkModeLists 'defaults.ini') |
        Where-Object { $_ -match "^\s*$key\s*=" } | Select-Object -First 1
    if ($line) { ($line -split '=', 2)[1].Trim() } else { '2h' }
}

# The '23:59 today' target for `midnight`, rolled to TOMORROW when today's has gone (or is about
# to). The CLI refuses any block that does not end MORE than 60 seconds out - MonkMode\Program.vb:277,
# `If untilDate <= DateTime.Now.AddSeconds(60)` -> "The block must end at least a minute in the
# future." (exit 1). So the old unconditional "today 23:59" silently FAILED TO ARM when run at or
# near 23:59: nothing was blocked, precisely at the late-night hour the block is most wanted.
# $LeadSeconds is deliberately wider than the CLI's own 60s floor: the CLI re-reads DateTime.Now
# after this string is built, so a target computed at exactly the floor could still be refused by
# the time monkmode.exe starts. 90s keeps that race off the table.
# Pure - takes 'now' as a parameter and only returns a string, so the rollover is inspectable and
# testable without waiting for 23:59 or arming anything.
function MM-MidnightUntil([datetime]$Now = (Get-Date), [int]$LeadSeconds = 90) {
    $target = $Now.Date.AddHours(23).AddMinutes(59)
    if ($target -le $Now.AddSeconds($LeadSeconds)) { $target = $target.AddDays(1) }
    $target.ToString('yyyy-MM-dd HH:mm')
}

function MM-Arm([string]$listFile, [string]$key, [string]$For, [string[]]$extraArgs) {
    if (-not (MM-IsAdmin)) {
        Write-Host 'Run this from an ELEVATED terminal (Win+X -> Terminal (Admin), then type powershell) - MonkMode refuses without it.' -ForegroundColor Yellow
        return
    }
    if (-not $For) { $For = MM-Default $key }
    $sites = MM-Sites $listFile
    if (-not $sites) { Write-Host "List $listFile is empty - nothing to block." -ForegroundColor Yellow; return }
    if ($For -eq 'midnight') {
        $now = Get-Date
        $until = MM-MidnightUntil $now
        if ($until -notlike ($now.ToString('yyyy-MM-dd') + '*')) {
            # Rolled past today's 23:59, so this is a ~24h block, not a short one. Say so BEFORE
            # arming - a block can never be cut short once it starts.
            Write-Host "Today's 23:59 has passed (or is too close to arm) - blocking until $until instead." -ForegroundColor Yellow
        }
        & monkmode block --sites $sites @extraArgs --until $until
    } else {
        & monkmode block --sites $sites @extraArgs --for $For
    }
}

# --- The three presets -------------------------------------------------------
# Duration is optional; without it the defaults.ini value is used.
#   mm-video            mm-video 3h          mm-video midnight
function mm-video  { param([string]$For) MM-Arm 'video.txt'  'video'  $For @() }
function mm-insta  { param([string]$For) MM-Arm 'social.txt' 'insta'  $For @('--apps', 'WhatsApp.exe') }
function mm-reddit { param([string]$For) MM-Arm 'reddit.txt' 'reddit' $For @() }

# --- Always-on schedule for the video set ---
# v1.1: NO LONGER mutually exclusive with manual blocks. Up to 8 blocks (the schedule occupies one
# of them) run side by side, so arming a manual block on top of the schedule is legal and both
# hold - it is not the refusal it was in v1.0. That is also why the bundle commands below warn
# about a duplicate instead of bouncing off one.
function mm-video-schedule {
    if (-not (MM-IsAdmin)) { Write-Host 'Needs an ELEVATED terminal.' -ForegroundColor Yellow; return }
    & monkmode schedule --sites (MM-Sites 'video.txt') --windows 'Mon-Sun 00:00-23:59'
}
function mm-schedule-show { & monkmode schedule --show }
function mm-schedule-off  {
    if (-not (MM-IsAdmin)) { Write-Host 'Needs an ELEVATED terminal.' -ForegroundColor Yellow; return }
    & monkmode schedule --clear   # stops FUTURE windows; a currently-open window still runs to its end
}

# --- Info / management -------------------------------------------------------
function mm-status {
    if (-not (MM-IsAdmin)) { Write-Host 'Needs an ELEVATED terminal (Win+X -> Terminal (Admin), then type powershell).' -ForegroundColor Yellow; return }
    & monkmode status
}
function mm-stats  {
    if (-not (MM-IsAdmin)) { Write-Host 'Needs an ELEVATED terminal (Win+X -> Terminal (Admin), then type powershell).' -ForegroundColor Yellow; return }
    & monkmode stats
}
function mm-edit   { Start-Process explorer $global:MonkModeLists }   # open the lists folder

# =============================================================================================
# v1.1 - preset BUNDLES and the English lock grammar (spec decision 18)
#
#   mm-lock                                the default bundle (doomscroll), defaults.ini `lock`
#   mm-lock doomscroll for 2h              plain words compose the CLI flags
#   mm-lock shorts until 22:00             a clock time; already gone today => tomorrow
#   mm-lock everything tonight             until the next 04:00
#   mm-lock social games for 3h committed
#   mm-shorts / mm-games                   the two single-purpose wrappers
#
# Everything except the three mm-* commands is PURE: words in, strings out, no clock of its own
# (the time arrives as a parameter) and no side effects. That is what lets the whole grammar be
# pinned by a C# twin in MonkMode.Tests\PresetBundleTests.cs without this file ever being run.
#
# A typo REFUSES. Nothing is armed at that point, so refusing costs a retype - whereas guessing
# could arm the wrong block for hours, and no block can be cut short once it starts.
# =============================================================================================

# The bundle vocabulary, in the canonical order the composed argument list follows. `everything`
# is every name here; whatever order the words are typed in, the command that runs is the same.
function MM-BundleNames { @('doomscroll', 'shorts', 'social', 'games') }

# The vocabulary as the refusal prints it - one place, so the help can never drift from the parser.
function MM-LockVocabulary {
    @(
        '  Bundles:  doomscroll  shorts  social  games  everything      (none given = doomscroll)',
        '  Time:     for <2h|90m|1d2h30m>   until <HH:MM>   tonight   midnight   (or a bare length: 2h)',
        '  Modifier: committed',
        '  Example:  mm-lock social games for 3h committed'
    ) -join [Environment]::NewLine
}

# Case-insensitive dedupe, first occurrence wins, order preserved - the CLI's own rule for
# --sites / --preset / --apps, so a bundle union looks the same on both sides of the call.
function MM-Dedupe([string[]]$Items) {
    $seen = @{}
    $out = @()
    foreach ($x in $Items) {
        $k = ([string]$x).Trim()
        if ($k -eq '') { continue }
        if ($seen.ContainsKey($k.ToLowerInvariant())) { continue }
        $seen[$k.ToLowerInvariant()] = $true
        $out += $k
    }
    , $out
}

# Does this token parse as a --for duration? Mirrors the CLI's TryParseDuration
# (MonkMode\Program.vb:1174-1192): "1d2h30m" with any part omitted, or a bare number of minutes,
# and it must come to more than zero. The one deliberate divergence is the >9-digit guard - the
# CLI's Integer.Parse would THROW on "99999999999h", and refusing here is the safer end of that
# (nothing armed, the user retypes).
function MM-IsDuration([string]$Token) {
    $t = ([string]$Token).Trim().ToLowerInvariant()
    if ($t -eq '') { return $false }
    $d = 0; $h = 0; $m = 0
    $rx = [regex]::Match($t, '^(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?$')
    if ($rx.Success -and ($rx.Groups[1].Success -or $rx.Groups[2].Success -or $rx.Groups[3].Success)) {
        foreach ($g in 1, 2, 3) {
            if ($rx.Groups[$g].Success -and $rx.Groups[$g].Value.Length -gt 9) { return $false }
        }
        if ($rx.Groups[1].Success) { $d = [int]$rx.Groups[1].Value }
        if ($rx.Groups[2].Success) { $h = [int]$rx.Groups[2].Value }
        if ($rx.Groups[3].Success) { $m = [int]$rx.Groups[3].Value }
    } elseif ($t -match '^[0-9]{1,9}$') {
        $m = [int]$t          # a bare number is minutes, exactly like the CLI
    } else {
        return $false
    }
    return ((($d * 1440) + ($h * 60) + $m) -gt 0)
}

# The `until HH:MM` target as the absolute datetime string the CLI takes, rolled to TOMORROW when
# today's has gone or is about to. This is MM-MidnightUntil's rule generalised off 23:59, and it
# exists for the same reason: the CLI refuses any block ending less than 60s out
# (MonkMode\Program.vb:277), so an already-passed target would silently FAIL TO ARM - nothing
# blocked at exactly the hour the block is most wanted. '' for anything that is not a 24h HH:MM.
function MM-UntilTime([datetime]$Now, [string]$HHMM, [int]$LeadSeconds = 90) {
    $m = [regex]::Match(([string]$HHMM).Trim(), '^([01]?[0-9]|2[0-3]):([0-5][0-9])$')
    if (-not $m.Success) { return '' }
    $target = $Now.Date.AddHours([int]$m.Groups[1].Value).AddMinutes([int]$m.Groups[2].Value)
    if ($target -le $Now.AddSeconds($LeadSeconds)) { $target = $target.AddDays(1) }
    $target.ToString('yyyy-MM-dd HH:mm')
}

# PURE. The whole grammar: words in, a description of what to arm out. No file read (the
# defaults.ini value arrives as -DefaultFor), no clock (-Now), no arming - so every example can be
# proved without a block in sight. Keys: Ok / Error / Bundles / For / Until / Rolled / Committed.
# Exactly one of For and Until is ever set.
function MM-ParseLockArgs([string[]]$Words, [datetime]$Now = (Get-Date), [string]$DefaultFor = '2h', [int]$LeadSeconds = 90) {
    $r = @{ Ok = $true; Error = ''; Bundles = @(); For = ''; Until = ''; Rolled = $false; Committed = $false }
    $refuse = {
        param($why)
        $r.Ok = $false
        $r.Error = $why + ' Nothing has been armed.' + [Environment]::NewLine + (MM-LockVocabulary)
        $r
    }

    $toks = @()
    foreach ($w in $Words) {
        if ($null -ne $w -and ([string]$w).Trim() -ne '') { $toks += ([string]$w).Trim().ToLowerInvariant() }
    }

    $picked = @{}
    $timeWord = ''
    $i = 0
    while ($i -lt $toks.Count) {
        $w = $toks[$i]
        $step = 1

        if ($w -eq 'everything') {
            foreach ($b in (MM-BundleNames)) { $picked[$b] = $true }
        } elseif ((MM-BundleNames) -contains $w) {
            $picked[$w] = $true
        } elseif ($w -eq 'committed') {
            $r.Committed = $true
        } elseif ($w -eq 'for' -or $w -eq 'until') {
            # Two time clauses is not something to resolve by guessing - "for 2h until 22:00" could
            # mean either, and one of them is the longer block. Refuse; the user says which.
            if ($timeWord -ne '') { return (& $refuse "'$w' is a second length for the same block, after '$timeWord'.") }
            if ($i + 1 -ge $toks.Count) { return (& $refuse "'$w' needs a value after it.") }
            $val = $toks[$i + 1]
            $step = 2
            if ($w -eq 'for') {
                if ($val -eq 'midnight') { $r.For = 'midnight' }
                elseif (MM-IsDuration $val) { $r.For = $val }
                else { return (& $refuse "'for $val' is not a length I understand.") }
            } else {
                $until = MM-UntilTime $Now $val $LeadSeconds
                if ($until -eq '') { return (& $refuse "'until $val' is not a 24-hour clock time (use e.g. 'until 22:00').") }
                $r.Until = $until
            }
            $timeWord = $w
        } elseif ($w -eq 'tonight') {
            if ($timeWord -ne '') { return (& $refuse "'tonight' is a second length for the same block, after '$timeWord'.") }
            # The next 04:00 - tomorrow's from any evening hour, today's when it is already the
            # small hours. Rolling rather than always taking tomorrow's keeps a 01:00 `tonight` a
            # three-hour block instead of a twenty-seven-hour one that could not be cut short.
            $r.Until = MM-UntilTime $Now '04:00' $LeadSeconds
            $timeWord = $w
        } elseif ($w -eq 'midnight') {
            if ($timeWord -ne '') { return (& $refuse "'midnight' is a second length for the same block, after '$timeWord'.") }
            $r.For = 'midnight'      # handed to MM-MidnightUntil at arm time, reused unchanged
            $timeWord = $w
        } elseif (MM-IsDuration $w) {
            if ($timeWord -ne '') { return (& $refuse "'$w' is a second length for the same block, after '$timeWord'.") }
            $r.For = $w              # `mm-lock 2h` - the bare length the v1.0 wrappers took
            $timeWord = $w
        } else {
            return (& $refuse "'$w' is not a word mm-lock knows.")
        }
        $i += $step
    }

    # Canonical order, not typing order, so the same request always builds the same command.
    $r.Bundles = @(MM-BundleNames | Where-Object { $picked.ContainsKey($_) })
    if ($r.Bundles.Count -eq 0) { $r.Bundles = @('doomscroll') }
    if ($timeWord -eq '') { $r.For = $DefaultFor }
    # A rolled `until` is a MUCH longer block than the words suggest - the caller says so out loud.
    if ($r.Until -ne '') { $r.Rolled = -not $r.Until.StartsWith($Now.ToString('yyyy-MM-dd')) }
    $r
}

# PURE. Bundle names -> the input SOURCES they name; no file is read here, the .txt names are
# returned and the caller reads them. Deduped, order preserved (doomscroll and shorts share a url
# list). `social` is the existing social preset widened, not narrowed: the site table's `social`
# category AND social.txt AND the WhatsApp app-kill mm-insta already carries.
function MM-BundleFlags([string[]]$Bundles) {
    $siteFiles = @(); $urlFiles = @(); $appFiles = @(); $presets = @(); $appPresets = @(); $apps = @()
    foreach ($b in $Bundles) {
        switch (([string]$b).Trim().ToLowerInvariant()) {
            'doomscroll' { $siteFiles += 'doomscroll.txt'; $urlFiles += 'shortform-urls.txt' }
            'shorts'     { $urlFiles += 'shortform-urls.txt' }
            'social'     { $siteFiles += 'social.txt'; $presets += 'social'; $apps += 'WhatsApp.exe' }
            'games'      { $appFiles += 'games-extra.txt'; $appPresets += 'games' }
        }
    }
    @{
        SiteFiles  = @(MM-Dedupe $siteFiles)
        UrlFiles   = @(MM-Dedupe $urlFiles)
        AppFiles   = @(MM-Dedupe $appFiles)
        Presets    = @(MM-Dedupe $presets)
        AppPresets = @(MM-Dedupe $appPresets)
        Apps       = @(MM-Dedupe $apps)
    }
}

# PURE. The exact `monkmode block ...` argument vector, first element 'block'. One fixed order, so
# a command is reproducible and a test can pin the whole vector rather than a description of it.
# --until wins over --for when both arrive; the parser never sets both.
function MM-BuildBlockArgs([string]$Sites, [string]$Presets, [string]$Urls, [string]$Apps, [string]$AppPresets, [bool]$Committed, [string[]]$Extra, [string]$For, [string]$Until) {
    $a = @('block')
    if ($Sites)      { $a += @('--sites', $Sites) }
    if ($Presets)    { $a += @('--preset', $Presets) }
    if ($Urls)       { $a += @('--urls', $Urls) }
    if ($Apps)       { $a += @('--apps', $Apps) }
    if ($AppPresets) { $a += @('--app-preset', $AppPresets) }
    if ($Committed)  { $a += '--commit' }
    foreach ($e in $Extra) { if ($null -ne $e -and ([string]$e) -ne '') { $a += ([string]$e) } }
    if ($Until)      { $a += @('--until', $Until) } else { $a += @('--for', $For) }
    , $a
}

# Entries in a comma-joined list (what MM-Sites builds) - the count the duplicate check compares.
function MM-CountList([string]$Csv) {
    $n = 0
    foreach ($t in (([string]$Csv) -split ',')) { if ($t.Trim() -ne '') { $n++ } }
    $n
}

# The one sentence for an empty list file. Said out loud rather than passed over: a bundle whose
# list is empty contributes NOTHING, and "armed something smaller than you asked for" must never
# be silent.
function MM-EmptyListWarning([string]$File) {
    "List $File is empty - that part of the block is missing."
}

# P66 - the duplicate-arm guard, PURE (the caller supplies `monkmode status` output as text).
#
# Multi-slot arming is legal in v1.1, so re-running a wrapper no longer bounces off the "block
# already active" refusal v1.0 gave - it quietly arms a SECOND slot. This says so first.
#
# `status` prints COUNTS, never the site names (the Sites/Apps/URLs columns of the slot table are
# integers - MonkMode\Program.vb:1349,1355-1365), so the only comparable thing here is the SHAPE
# of an ACTIVE row, and the wording claims no more than that. -1 means "this axis cannot be
# counted from here" (a --preset expands inside the CLI) and is skipped, which widens the match.
# Deliberate: this NEVER refuses - a second identical block is a legitimate thing to want, e.g.
# a longer one on top of a short one - so over-warning costs a 5s pause while under-warning costs
# a surprise slot that cannot be cut short.
function MM-DuplicateActiveWarning([string]$StatusText, [int]$Sites, [int]$Apps, [int]$Urls) {
    # Column offsets of the fixed-width slot row (Program.vb:1330-1334: id 3, gap 2, state 8, gap
    # 2, when 24, gap 2, then three 5-wide counts). An ACTIVE row's `when` cell is a 16-character
    # stamp, so it never overflows its column and these offsets hold.
    foreach ($line in (([string]$StatusText) -split "`r?`n")) {
        if ($line.Length -lt 56) { continue }
        $id = $line.Substring(0, 3).Trim()
        if ($id -notmatch '^[0-9]+$') { continue }
        if ($line.Substring(5, 8).Trim() -ne 'ACTIVE') { continue }
        $rowSites = $line.Substring(41, 5)
        $rowApps = $line.Substring(46, 5)
        $rowUrls = $line.Substring(51, 5)
        if (-not (MM-CountMatches $rowSites $Sites)) { continue }
        if (-not (MM-CountMatches $rowApps $Apps)) { continue }
        if (-not (MM-CountMatches $rowUrls $Urls)) { continue }
        return "A block that looks identical (id $id - the same number of sites, apps and URLs) is already active. Arming again creates a SECOND slot - press Ctrl+C now if that isn't what you meant."
    }
    ''
}

# One axis of that comparison. An unknown count (-1) or an unreadable cell matches anything.
function MM-CountMatches([string]$Cell, [int]$Expected) {
    if ($Expected -lt 0) { return $true }
    $c = ([string]$Cell).Trim()
    if ($c -notmatch '^[0-9]+$') { return $true }
    ([int]$c -eq $Expected)
}

# The one place a bundle command arms. Everything arrives resolved, so nothing below here reads a
# file or parses a word. $Plan keys: Sites / Presets / Urls / Apps / AppPresets / Committed / Extra.
#
# `monkmode status` IS captured here - it is read-only, prints no one-time code, and its output is
# what the duplicate guard reads. The ARM itself is never piped or captured (see the file header):
# the partner code prints once, and a captured arm has wedged shells before.
function MM-ArmResolved([hashtable]$Plan, [string]$For, [string]$Until) {
    $extra = @()
    if ($Plan.Extra) { $extra = @($Plan.Extra) }    # @($null) would count as ONE argument
    if (-not $Plan.Sites -and -not $Plan.Urls -and -not $Plan.Apps -and
        -not $Plan.Presets -and -not $Plan.AppPresets -and $extra.Count -eq 0) {
        Write-Host 'Nothing to block - every list for this command is empty.' -ForegroundColor Yellow
        return
    }

    $statusText = ''
    try { $statusText = (& monkmode status | Out-String) } catch { $statusText = '' }
    # A --preset / --app-preset expands INSIDE the CLI, so that axis cannot be counted from here.
    $siteCount = if ($Plan.Presets)    { -1 } else { MM-CountList $Plan.Sites }
    $appCount  = if ($Plan.AppPresets) { -1 } else { MM-CountList $Plan.Apps }
    $warning = MM-DuplicateActiveWarning $statusText $siteCount $appCount (MM-CountList $Plan.Urls)
    if ($warning) {
        Write-Host $warning -ForegroundColor Yellow
        Start-Sleep -Seconds 5
    }

    $target = $Until
    if (-not $target -and $For -eq 'midnight') {
        $now = Get-Date
        $target = MM-MidnightUntil $now
        if ($target -notlike ($now.ToString('yyyy-MM-dd') + '*')) {
            Write-Host "Today's 23:59 has passed (or is too close to arm) - blocking until $target instead." -ForegroundColor Yellow
        }
    }
    $blockArgs = MM-BuildBlockArgs $Plan.Sites $Plan.Presets $Plan.Urls $Plan.Apps $Plan.AppPresets ([bool]$Plan.Committed) $extra $For $target
    & monkmode @blockArgs
}

# P65 - MM-Arm's sibling for a site list PLUS a url list. Same shape, same defaults.ini lookup,
# and $extraArgs is passed through verbatim exactly as MM-Arm passes it. Either file name may be
# '' when that half does not apply.
function MM-ArmSlot([string]$listFile, [string]$urlFile, [string]$key, [string]$For, [string[]]$extraArgs) {
    if (-not (MM-IsAdmin)) {
        Write-Host 'Run this from an ELEVATED terminal (Win+X -> Terminal (Admin), then type powershell) - MonkMode refuses without it.' -ForegroundColor Yellow
        return
    }
    if (-not $For) { $For = MM-Default $key }
    $sites = ''
    $urls = ''
    if ($listFile) {
        $sites = MM-Sites $listFile
        if (-not $sites) { Write-Host (MM-EmptyListWarning $listFile) -ForegroundColor Yellow }
    }
    if ($urlFile) {
        $urls = MM-Sites $urlFile
        if (-not $urls) { Write-Host (MM-EmptyListWarning $urlFile) -ForegroundColor Yellow }
    }
    MM-ArmResolved @{ Sites = $sites; Presets = ''; Urls = $urls; Apps = ''; AppPresets = ''; Committed = $false; Extra = $extraArgs } $For ''
}

# Arm a set of BUNDLES: read each named list, warn about any that is empty, arm what remains.
function MM-ArmBundles([string[]]$Bundles, [string]$For, [string]$Until, [bool]$Committed) {
    $flags = MM-BundleFlags $Bundles
    $sites = @(); $urls = @(); $apps = @($flags.Apps)
    foreach ($f in $flags.SiteFiles) {
        $v = MM-Sites $f
        if ($v) { $sites += $v } else { Write-Host (MM-EmptyListWarning $f) -ForegroundColor Yellow }
    }
    foreach ($f in $flags.UrlFiles) {
        $v = MM-Sites $f
        if ($v) { $urls += $v } else { Write-Host (MM-EmptyListWarning $f) -ForegroundColor Yellow }
    }
    foreach ($f in $flags.AppFiles) {
        $v = MM-Sites $f
        if ($v) { $apps += $v } else { Write-Host (MM-EmptyListWarning $f) -ForegroundColor Yellow }
    }
    MM-ArmResolved @{
        Sites      = ((MM-Dedupe (($sites -join ',') -split ',')) -join ',')
        Presets    = ($flags.Presets -join ',')
        Urls       = ((MM-Dedupe (($urls -join ',') -split ',')) -join ',')
        Apps       = ((MM-Dedupe (($apps -join ',') -split ',')) -join ',')
        AppPresets = ($flags.AppPresets -join ',')
        Committed  = $Committed
        Extra      = @()
    } $For $Until
}

# --- The v1.1 commands -------------------------------------------------------
# mm-shorts / mm-games: one bundle each, optional length (defaults.ini otherwise).
#   mm-shorts           mm-shorts 90m        mm-games 3h
function mm-shorts { param([string]$For) MM-ArmSlot '' 'shortform-urls.txt' 'shorts' $For @() }
function mm-games  {
    param([string]$For)
    if (-not (MM-IsAdmin)) {
        Write-Host 'Run this from an ELEVATED terminal (Win+X -> Terminal (Admin), then type powershell) - MonkMode refuses without it.' -ForegroundColor Yellow
        return
    }
    if (-not $For) { $For = MM-Default 'games' }
    MM-ArmBundles @('games') $For '' $false
}

# mm-lock: the full grammar. Words in any order; an unknown word refuses without arming.
function mm-lock {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Words)
    if (-not (MM-IsAdmin)) {
        Write-Host 'Run this from an ELEVATED terminal (Win+X -> Terminal (Admin), then type powershell) - MonkMode refuses without it.' -ForegroundColor Yellow
        return
    }
    $parsed = MM-ParseLockArgs $Words (Get-Date) (MM-Default 'lock')
    if (-not $parsed.Ok) { Write-Host $parsed.Error -ForegroundColor Yellow; return }
    if ($parsed.Rolled) {
        # A rolled target is a MUCH longer block than the words asked for - `tonight` typed at
        # 03:59 is nearly a full day, and `committed` would make it uncancellable. Pause like the
        # duplicate guard does: a yellow line nobody has time to read is not a warning.
        Write-Host "That time has already gone today - blocking until $($parsed.Until) instead." -ForegroundColor Yellow
        Write-Host "Press Ctrl+C now if that isn't what you meant." -ForegroundColor Yellow
        Start-Sleep -Seconds 5
    }
    MM-ArmBundles $parsed.Bundles $parsed.For $parsed.Until ([bool]$parsed.Committed)
}
