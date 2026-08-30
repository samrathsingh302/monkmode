'    Copyright (C) 2026 Samrath Singh
'
'    This file is part of MonkMode, a fork of Cold Turkey.
'    Source: https://github.com/samrathsingh302/monkmode
'
'    This program is free software: you can redistribute it and/or modify
'    it under the terms of the GNU General Public License as published by
'    the Free Software Foundation, either version 3 of the License, or
'    (at your option) any later version.
'
'    This program is distributed in the hope that it will be useful,
'    but WITHOUT ANY WARRANTY; without even the implied warranty of
'    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
'    GNU General Public License for more details.
'
'    You should have received a copy of the GNU General Public License
'    along with this program.  If not, see <https://www.gnu.org/licenses/>.

'    MonkMode - CLI entry point
'
'    Usage:
'      monkmode setup  [--partner "Alex (alex@example.com)"] [--default-sites a.com,b.com] [--default-preset social] [--default-apps a.exe,b.exe] [--default-app-preset games]  (required first-run onboarding)
'      monkmode block  [--sites a.com,b.com] [--preset social,video] [--apps chrome.exe,foo.exe] [--app-preset games,chat]
'                      (--for 2h30m | --until "2026-06-11 18:00") [--file list.txt]
'                      [--urls "youtube.com/shorts"] [--start +90m]   (substring match - NO wildcards)
'      monkmode status                     (a row per armed block, with each one's exit)
'      monkmode stats                      (read-only summary of your block history)
'      monkmode add    --sites c.com[,d.com] [--id N]
'      monkmode unblock --code <CODE>      (submit the partner code — service verifies + lifts)
'      monkmode help
'
'    Ledger 319 (30/08/2026): a running block has exactly TWO ends - its own end time,
'    or the partner code. The `--force` escape hatch and the self-serve cooling-off wait
'    were both REMOVED; there is no other exit, and no recovery for a lost code.
'
'    v1.1: `block` arms a NEW block beside the ones already running (up to 8), so
'    every verb that addresses one takes --id. A block, once started, cannot be
'    shortened (the service enforces this). 'add' only adds sites, to one block.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.Globalization
Imports System.IO

Module Program

    Function Main(ByVal args As String()) As Integer
        If args Is Nothing OrElse args.Length = 0 Then
            PrintUsage()
            Return 1
        End If

        Dim verb As String = args(0).ToLowerInvariant()
        Try
            ' C1b (R8, CLI side of the restore-on-corrupt path): if the primary
            ' config is corrupt/blanked/short and a MAC-valid backup exists, restore
            ' it before dispatching - so status/add see the real (self-healed) block
            ' instead of a fail-closed blank. Never writes a default and never
            ' overwrites a usable primary (a tampered-but-parseable config is left to
            ' freeze per B7); best-effort, never throws.
            Blocker.RestorePrimaryFromBackupIfCorrupt()
            Select Case verb
                Case "setup" : Return DoSetup(args)
                Case "block" : Return DoBlock(args)
                Case "status" : Return DoStatus()
                Case "stats" : Return DoStats()
                Case "add" : Return DoAdd(args)
                Case "schedule" : Return DoSchedule(args)
                Case "unblock" : Return DoUnblock(args)
                Case "help", "-h", "--help", "/?" : PrintUsage() : Return 0
                ' F75: which build am I on? The exe's file version is still the inherited
                ' Cold Turkey 0.7.0.0 stamp, so Windows' own properties dialog answers this
                ' WRONGLY. Cheap, read-only, and the first question anyone asks when
                ' something looks off.
                Case "version", "--version", "-v" : PrintVersion() : Return 0
                Case Else
                    Console.Error.WriteLine("Unknown command: " & verb)
                    PrintUsage()
                    Return 1
            End Select
        Catch ex As UnauthorizedAccessException
            Console.Error.WriteLine("Access denied. Run MonkMode as Administrator.")
            Return 2
        Catch ex As Exception
            Console.Error.WriteLine("Error: " & ex.Message)
            Return 2
        End Try
    End Function

    ' ---------- verbs ----------

    ' C6a: `monkmode setup` - required first-run onboarding. Records that setup has run (a
    ' MAC-covered [Setup] Done in the SEPARATE monkmode_setup.ini) plus the optional
    ' accountability-partner label, then explains the exit model. `block`/`schedule` refuse
    ' to arm until this has run (SetupIsComplete), so a first block always goes through this
    ' explanation and is never armed by a user who hasn't seen how to get out. Idempotent +
    ' safe to re-run any time (it never touches a live block). C6a does NOT mint an
    ' account-level code: each `block` already mints its OWN one-time code (C3b,
    ' rotate-on-use), so setup RELAYS that model rather than double-minting a second code
    ' (the lighter reconciliation, design gotcha #3). The configurable cooling-off duration
    ' + default blocklist/presets are deferred (C6b / D1).
    Private Function DoSetup(ByVal args As String()) As Integer
        Dim partner As String = GetOption(args, "--partner").Trim()
        ' C6c: `setup --cooloff` is still parsed and still stored on the SETUP canonical, so the
        ' s-schema is untouched and an old invocation keeps working - but ledger 319 made the
        ' value INERT: no block reads it any more, because no block has a cooling-off. Kept
        ' rather than removed purely to avoid a setup-schema bump for a dead field; undocumented
        ' in help. 0 = not given.
        Dim coolOffSeconds As Long
        If Not TryParseCoolOffArg(args, coolOffSeconds) Then Return 1
        ' D1b: optional --default-sites / --default-preset set the ACCOUNT-DEFAULT blocklist that every
        ' later `block` inherits when it names no site source of its own. Built (merged + preset-expanded,
        ' FAIL-CLOSED on an unknown preset) BEFORE the write so a bad preset fails fast with no partial
        ' state, exactly like the --cooloff parse above. "" = no default stored.
        Dim defaultSites As String = ""
        Dim defaultSitesErr As String = ""
        If Not Blocker.TryBuildDefaultSites(GetOption(args, "--default-sites"), GetOption(args, "--default-preset"), defaultSites, defaultSitesErr) Then
            Console.Error.WriteLine(defaultSitesErr)
            Return 1
        End If
        ' D2b: optional --default-apps / --default-app-preset set the ACCOUNT-DEFAULT app-kill list
        ' that every later `block` inherits when it names no app source of its own. Built (merged +
        ' app-preset-expanded, FAIL-CLOSED on an unknown app-preset) BEFORE the write so a bad preset
        ' fails fast with no partial state, exactly like --default-sites above. "" = no default stored.
        Dim defaultApps As String = ""
        Dim defaultAppsErr As String = ""
        If Not Blocker.TryBuildDefaultApps(GetOption(args, "--default-apps"), GetOption(args, "--default-app-preset"), defaultApps, defaultAppsErr) Then
            Console.Error.WriteLine(defaultAppsErr)
            Return 1
        End If
        If Not Blocker.WriteSetupConfig(partner, coolOffSeconds, defaultSites, defaultApps) Then
            Console.Error.WriteLine("Could not secure the setup file (Windows DPAPI is unavailable on this machine).")
            Console.Error.WriteLine("MonkMode can't protect its config here, so it won't arm blocks safely. Resolve DPAPI, then re-run 'monkmode setup'.")
            Return 2
        End If
        Console.WriteLine("MonkMode is set up. Here's how it works before you start your first block:")
        Console.WriteLine("")
        Console.WriteLine("  Accountability code - every block you start mints a ONE-TIME code, shown once at")
        Console.WriteLine("  the start. Relay it to your accountability partner" & If(partner <> "", " (" & partner & ")", "") & " straight away. To end")
        Console.WriteLine("  a block early, they run:  monkmode unblock --code <CODE>  (a fresh code each block).")
        Console.WriteLine("")
        Console.WriteLine("  There is NO other way out - ledger 319. Without the code a block runs to its end")
        Console.WriteLine("  time and not a second less. There is no self-serve wait, no escape hatch, no")
        Console.WriteLine("  override and no recovery: lose the code and you wait. Choose durations you mean.")
        ' D1b: confirm the account-default blocklist when set (a block naming no --sites/--preset/--file inherits it).
        If defaultSites <> "" Then Console.WriteLine("  Your account-default blocklist is: " & defaultSites.Replace(",", ", ") & " - inherited by any block you start without --sites/--preset/--file.")
        ' D2b: confirm the account-default app list when set (a block naming no --apps/--app-preset inherits it).
        If defaultApps <> "" Then Console.WriteLine("  Your account-default app list is: " & defaultApps.Replace(",", ", ") & " - inherited by any block you start without --apps/--app-preset.")
        Console.WriteLine("")
        Console.WriteLine("  Every block is COMMITTED - there is no uncommitted mode any more. The code, or the")
        Console.WriteLine("  timer. Use durations you mean.")
        Console.WriteLine("")
        Console.WriteLine("  Schedules - 'monkmode schedule' arms recurring wall-clock windows that open/close")
        Console.WriteLine("  automatically; a window can't be ended early once open.")
        Console.WriteLine("")
        Console.WriteLine("You're ready. Start a block with, e.g.:  monkmode block --sites reddit.com --for 2h")
        Return 0
    End Function

    Private Function DoBlock(ByVal args As String()) As Integer
        ' C6a: required first-run setup. Refuse to arm until `monkmode setup` has run, so a
        ' first block always goes through the accountability-model explanation (and can
        ' never be armed by someone who hasn't seen how to exit). Gates only NEW arms -
        ' status/unblock/add against an EXISTING block are never gated, so this can't trap
        ' an already-active block. Fail-closed: a missing/tampered setup file reads as not
        ' set up -> re-run `setup`.
        If Not Blocker.SetupIsComplete() Then Return SetupRequired()

        ' D5 (friendly validation): warn on unrecognised --flags (likely typos, e.g. --site for
        ' --sites) WITHOUT failing - an over-strict reject could refuse a valid command. The block
        ' proceeds with whatever valid flags were given.
        Dim unknownOpts As List(Of String) = UnknownOptions(args, BlockOptionNames())
        If unknownOpts.Count > 0 Then
            Console.Error.WriteLine("Note: ignoring unrecognised option(s): " & String.Join(", ", unknownOpts) & ". Run 'monkmode help' for the flags 'block' accepts.")
        End If

        ' D5 follow-up: a boolean flag given as "--flag=value" (e.g. --commit=yes) is a NO-OP under
        ' HasFlag's bare-flag match, so it would SILENTLY arm a non-committed / session-0-only block
        ' the user believed they had configured. UnknownOptions won't catch it (its head "--commit"
        ' is a known flag), so warn here specifically - still never failing (proceed with it OFF).
        Dim boolWithValue As List(Of String) = BooleanFlagsWithValue(args)
        If boolWithValue.Count > 0 Then
            Console.Error.WriteLine("Note: " & String.Join(", ", boolWithValue) & " is an on/off flag - pass it bare (e.g. '--commit'), not with '=value'. The '=value' form was ignored.")
        End If

        Dim domains As New List(Of String)
        domains.AddRange(SplitList(GetOption(args, "--sites")))

        ' D1a: expand --preset categories (social, video, news, shopping, adult) into the SAME
        ' site list. Pure input sugar - the expanded domains are enforced + MAC-covered exactly
        ' like --sites (combinable with --sites/--file, all merge into `domains`). Fail-closed:
        ' an unknown category aborts the block up front (before any hosts/service side effect)
        ' with a friendly error, never a silent under-block.
        Dim presetArg As String = GetOption(args, "--preset")
        If presetArg <> "" Then
            Dim presetDomains As New List(Of String), presetErr As String = ""
            If Not Blocker.TryExpandPresets(presetArg, presetDomains, presetErr) Then
                Console.Error.WriteLine(presetErr)
                Return 1
            End If
            domains.AddRange(presetDomains)
        End If

        Dim fileArg As String = GetOption(args, "--file")
        If fileArg <> "" AndAlso File.Exists(fileArg) Then
            For Each line As String In File.ReadAllLines(fileArg)
                Dim t As String = line.Trim()
                If t <> "" AndAlso Not t.StartsWith("#") Then domains.Add(t)
            Next
        End If

        Dim apps As New List(Of String)
        apps.AddRange(SplitList(GetOption(args, "--apps")))

        ' D2a: expand --app-preset categories (games, chat) into the SAME app-kill list. Pure input
        ' sugar - the expanded .exe names are enforced + MAC-covered exactly like --apps (combinable
        ' with --apps, both merge into `apps`). Fail-closed: an unknown category aborts the block up
        ' front (before any hosts/service side effect) with a friendly error, never a silent under-kill.
        Dim appPresetArg As String = GetOption(args, "--app-preset")
        If appPresetArg <> "" Then
            Dim presetApps As New List(Of String), appPresetErr As String = ""
            If Not Blocker.TryExpandAppPresets(appPresetArg, presetApps, appPresetErr) Then
                Console.Error.WriteLine(appPresetErr)
                Return 1
            End If
            apps.AddRange(presetApps)
        End If

        ' P55 (v1.1): optional --urls attaches per-slot URL patterns to THIS block. Parsed
        ' up front so a bad list fails before any side effect. The patterns are stored
        ' MAC-covered and the F2b browser watcher consumes them.
        '
        ' FX4 (F4): this parse MOVED UP, above the default inheritance and the emptiness gate.
        ' Both of those predate --urls and read only domains/apps, which broke the shipped
        ' URL-only wrappers (mm-shorts / `mm-lock shorts` compose `block --urls ... --for X`
        ' with no --sites at all): with no account defaults the gate below refused the command
        ' outright, and WITH defaults the two inherits below silently turned a "Shorts/Reels
        ' only" command into a full hosts block of the default site list plus every default app,
        ' for the whole duration, uncancellable. --urls has to be known before either decision.
        Dim urlPatterns As String = "", urlErr As String = ""
        If Not Blocker.TryBuildUrlPatterns(GetOption(args, "--urls"), urlPatterns, urlErr) Then
            Console.Error.WriteLine(urlErr)
            Return 1
        End If
        ' The glob footgun (26/08/2026): a "*" in a pattern matches nothing, silently, because
        ' the P57 matcher is ordinal substring. WARN and CARRY ON - never refuse (see
        ' Blocker.UrlGlobWarningLine for why). Printed here, before the first side effect, so
        ' it is not buried under the arm's own output.
        Dim globWarning As String = Blocker.UrlGlobWarningLine(GetOption(args, "--urls"))
        If globWarning <> "" Then Console.Error.WriteLine(globWarning)

        ' D1b/D2b: inherit the account-default blocklist / app list when this block names NO explicit
        ' source of its own (--sites/--preset/--file produced nothing; --apps/--app-preset produced
        ' nothing). An explicit source OVERRIDES the default (you get exactly what you asked for); the
        ' default only fills in when you named none - the direct analogue of the C6c cooling-off
        ' inheritance below. The two dimensions still inherit INDEPENDENTLY, so `block --sites x.com`
        ' with a default app list still picks up the default apps, and an --apps-only block still picks
        ' up the default sites. SetupIsComplete was required above and both readers fail-close to empty
        ' on any tamper, so this can only ADD to THIS new arm (never lift/shorten a live block). The
        ' inherited values ride WriteHostsBlock + [User] CustomSites / PackApps -> [Process] List,
        ' MAC-covered exactly like hand-typed ones.
        '
        ' FX4 (F4) - THE ONE EXCEPTION, ShouldInheritDefaults: a URL-ONLY invocation inherits NOTHING.
        ' "URL-only" is exact and narrow - --urls produced patterns AND no site source AND no app
        ' source produced anything - and in that one case both inherits are skipped. Anywhere else
        ' (including --sites x.com --urls ..., which is not URL-only) the pre-FX4 semantics stand
        ' untouched, and a bare `block --for 2h` with defaults configured behaves exactly as before.
        ' Rationale: with --urls the user HAS named what to block, so the "you named nothing, here are
        ' your defaults" premise is simply false; honouring it turns a page-level block into a
        ' machine-wide one the user never asked for and cannot cut short. This narrows only what a NEW
        ' arm inherits - it can never lift, shorten or unblock anything already running.
        If Blocker.ShouldInheritDefaults(domains.Count, apps.Count, urlPatterns <> "") Then
            If domains.Count = 0 Then domains.AddRange(Blocker.SetupDefaultSites())
            If apps.Count = 0 Then apps.AddRange(Blocker.SetupDefaultApps())
        End If

        ' FX4 (F4): --urls now counts as something to block, so a URL-only arm passes this gate.
        If Blocker.HasNothingToBlock(domains.Count, apps.Count, urlPatterns <> "") Then
            Console.Error.WriteLine(Blocker.NothingToBlockMessage)
            Return 1
        End If

        ' FX4 (F30): refuse control characters in ANY site/app value BEFORE the first side effect.
        ' Every site/app source has converged on these two lists by now - --sites, --preset, --file
        ' lines, --apps, --app-preset and the inherited setup defaults - so this is the CLI's single
        ' chokepoint for them (--urls was checked inside TryBuildUrlPatterns above, and ArmSlot
        ' re-checks all three as the writer backstop). One such character would be written verbatim
        ' into the ini, split the line on reload, and freeze this AND every other armed block
        ' permanently - with no partner-code exit either (ledger 319 removed the escape hatch
        ' that used to be the answer to this, so it is now unrecoverable). Refuse the whole arm, name the
        ' value, write nothing - never strip or truncate.
        Dim ctrlErr As String = ""
        If Not Blocker.TryRejectControlChars("site", domains, ctrlErr) OrElse
           Not Blocker.TryRejectControlChars("app", apps, ctrlErr) Then
            Console.Error.WriteLine(ctrlErr)
            Return 1
        End If

        ' P26/P27/P28 (v1.1): optional --start delays this block. A PENDING slot stores
        ' StartAt + a DURATION (never an absolute end - see P29), and the SERVICE computes
        ' its end at activation. Parsed BEFORE the end, because `--for` on a delayed block
        ' measures its duration from the START, not from now (2h starting in 90m = 2h of
        ' blocking; the other reading would silently shorten the block).
        Dim armNow As DateTime = DateTime.Now
        Dim startArg As String = GetOption(args, "--start")
        Dim startAt As DateTime? = Nothing
        If startArg <> "" Then
            Dim parsedStart As DateTime, startErr As String = ""
            If Not TryParseStart(startArg, armNow, parsedStart, startErr) Then
                Console.Error.WriteLine(startErr)
                Return 1
            End If
            ' P27: an unbounded PENDING slot would squat one of the 8 slots indefinitely.
            If StartIsTooFarAhead(parsedStart, armNow) Then
                Console.Error.WriteLine("--start can be at most " & MaxStartDelayDays & " days ahead.")
                Return 1
            End If
            ' P28 (consented taste default): a start already in the past is not an error -
            ' it means "now". The slot is written ACTIVE (Until set, StartAt empty), never
            ' PENDING, so nothing waits on a moment that has already gone.
            If parsedStart <= armNow Then
                Console.WriteLine("--start is in the past - starting now.")
            Else
                startAt = parsedStart
            End If
        End If

        ' The moment the enforcement window opens: the start for a PENDING block, now for
        ' an immediate one. Both --for and the 60s floor are anchored on it.
        Dim windowStart As DateTime = If(startAt.HasValue, startAt.Value, armNow)

        Dim untilDate As DateTime
        Dim untilArg As String = GetOption(args, "--until")
        Dim forArg As String = GetOption(args, "--for")
        If untilArg <> "" Then
            If Not DateTime.TryParse(untilArg, CultureInfo.CurrentCulture, DateTimeStyles.None, untilDate) _
               AndAlso Not DateTime.TryParse(untilArg, CultureInfo.InvariantCulture, DateTimeStyles.None, untilDate) Then
                Console.Error.WriteLine("Could not understand --until '" & untilArg & "'. Try ""2026-06-11 18:00"".")
                Return 1
            End If
        ElseIf forArg <> "" Then
            Dim span As TimeSpan
            If Not TryParseDuration(forArg, span) Then
                Console.Error.WriteLine("Could not understand --for '" & forArg & "'. Try 2h, 90m, 1d12h.")
                Return 1
            End If
            untilDate = windowStart.Add(span)
        Else
            Console.Error.WriteLine("Specify a duration with --for or --until.")
            Return 1
        End If

        ' P27: an --until at or before the start is a contradiction, not a zero-length block;
        ' and the 60s floor, re-anchored on the ENFORCEMENT window (identical to the pre-v1.1
        ' check for an immediate block, where windowStart is now). Both messages unchanged.
        Select Case ClassifyBlockWindow(startAt.HasValue, windowStart, untilDate)
            Case WindowRefusal.EndsBeforeStart
                Console.Error.WriteLine("The block must start before it ends.")
                Return 1
            Case WindowRefusal.TooShort
                Console.Error.WriteLine("The block must end at least a minute in the future.")
                Return 1
        End Select

        ' Ledger 319 (30/08/2026): there is no cooling-off exit any more, so a per-block
        ' cooling-off DURATION has nothing to configure. `--cooloff` is still ACCEPTED and
        ' still grammar-checked (an old script or muscle-memory invocation must not start
        ' failing, and it stays in BlockOptionNames so it draws no "unknown flag" warning),
        ' but the value is DISCARDED: the slot's MAC-covered CoolOffDuration is always
        ' written empty, and nothing anywhere reads it to decide a lift. Removed from help.
        Dim ignoredCoolOffSeconds As Long
        If Not TryParseCoolOffArg(args, ignoredCoolOffSeconds) Then Return 1
        Const coolOffSeconds As Long = 0

        Dim serviceExe As String = Path.Combine(Blocker.AppDir(), Blocker.ServiceExeName)
        If Not File.Exists(serviceExe) Then
            Console.Error.WriteLine("Cannot find " & Blocker.ServiceExeName & " next to monkmode.exe (" & Blocker.AppDir() & ").")
            Console.Error.WriteLine("Deploy the service and notifier into the same folder as the CLI.")
            Return 2
        End If

        ' v1.1 S2: the two "you already have a block" refusals that stood here are GONE -
        ' `block` now ARMS A NEW SLOT beside whatever is already running (the SD-c1
        ' schedule refusal and the BlockIsActive refusal both existed only because v1.0
        ' had a single machine-wide block a second arm would have overwritten). The only
        ' arm refusals left are the cap (P34) and a frozen config, both raised by ArmSlot
        ' BEFORE it touches anything. `add` and `schedule` keep their own refusals until
        ' schedules become slots (S3b).
        '
        ' FX3 (F3): ...EXCEPT the SCHEDULE half of SD-c1, restored here. S2's reasoning
        ' ("v1.1 blocks coexist") is true SLOT-vs-SLOT and FALSE slot-vs-SCHEDULE: a
        ' schedule is not a slot. It lives in the GLOBAL [Schedule] Spec/ActiveUntil with
        ' no [SlotN] section of its own, so arming a block beside one destroyed it two
        ' ways - with the service absent ShouldFreshRewrite scaffolds a BRAND-NEW config
        ' over it (the Spec is gone at arm time), and with the service present the Spec
        ' survives only until that slot retires, when the last-slot NeutraliseV9Residual
        ' blanked the pair and tore an OPEN scheduled window down mid-window. `schedule`
        ' has refused beside armed slots since S3b; this is the same refusal the other way
        ' round, raised BEFORE any side effect (nothing armed, nothing written).
        '
        ' Fail-safe by construction: ScheduleIsArmed is macValid-gated, so a tampered or
        ' unreadable config reads NOT armed here and falls through to ArmSlot's own frozen-
        ' config refusal instead (with the service installed that is unconditional). The
        ' one path that still scaffolds over an unverifiable [Schedule] Spec is service-
        ' ABSENT + MAC-INVALID, i.e. P18's deliberate recovery hatch: nothing is enforcing,
        ' the stored Spec cannot be trusted anyway, and refusing there would leave the user
        ' unable to arm anything ever again. ArmSlot backstops this refusal independently.
        If Blocker.ScheduleIsArmed() Then
            Console.Error.WriteLine("A schedule is armed, and a schedule and a manual block can't run together.")
            Console.Error.WriteLine("Clear it first with 'monkmode schedule --clear' (any open window still runs to its end), then start the block.")
            Return 3
        End If

        ' Ledger 319 (30/08/2026): EVERY block is committed. The partner code and the end time
        ' are the only exits there are, which is exactly what `--commit` used to opt into, so
        ' the flag has nothing left to switch. It stays ACCEPTED (and stays in BlockOptionNames
        ' + the boolean-flag list, so `--commit` and `--commit=yes` still behave as before)
        ' purely so existing scripts keep working; the field is written `yes` regardless.
        Const committed As Boolean = True
        ' D2c: `--all-session-kill` widens app-kill from the current session (+ session 0) to
        ' EVERY logged-in session - the LocalSystem service kills blocked apps in all sessions,
        ' not just session 0. MAC-covered from birth; a widen-only policy (fail-closed: it can
        ' only ADD kills). No-op if no apps are blocked (nothing to kill), noted below only then.
        Dim allSessionKill As Boolean = HasFlag(args, "--all-session-kill")

        ' M0 (F6): sample "is anything already armed?" HERE, BEFORE ArmSlot appends this
        ' block's own slot - afterwards AnySlotArmed() is unconditionally True and the two
        ' fresh-arm guards below could never fire. Both of them get this one reading:
        ' the B5a DoH snapshot, and D4d's leftover-notifier kill.
        Dim alreadyArmed As Boolean = Blocker.AnythingArmed()

        ' F74 (22/08/2026): read the accountability-partner label HERE, before the arm, and
        ' never between the arm and the code print below. SetupPartnerLabel is documented
        ' never-throws (its whole body is a Try/Catch returning ""), so this is belt AND
        ' braces - but F6 is the lesson that a throw between the arm and that print costs a
        ' committed block its ONLY early exit for the block's whole life, and the cheapest
        ' way to keep that promise is to put nothing new in that window at all.
        Dim partnerLabel As String = Blocker.SetupPartnerLabel()

        ' v1.1 S2: CONFIG FIRST, then the snapshot, then hosts. ArmSlot appends this block
        ' as a new slot (or refuses without side effects), mints its own partner code and
        ' returns it ONCE - only a salted, MAC-covered hash is ever persisted.
        Dim arm As Blocker.ArmResult = Blocker.ArmSlot(domains, apps, urlPatterns, startAt, untilDate,
                                                       Blocker.ServiceIsInstalled(),
                                                       committed, coolOffSeconds, allSessionKill)
        If arm.Outcome = Blocker.ArmOutcome.CapReached Then
            Console.Error.WriteLine("All " & MonkMode.ConfigIntegrity.MaxSlots & " block slots are in use. End or wait out one of these first:")
            For Each line As String In arm.SlotSummaries
                Console.Error.WriteLine(line)
            Next
            Return Blocker.ExitCapReached
        ElseIf arm.Outcome = Blocker.ArmOutcome.ScheduleArmed Then
            ' FX3 (F3): the writer's own SD-c1 refusal. Normally unreachable (the check above
            ' already refused), so reaching it means the schedule was armed in the window
            ' between the two reads - same refusal, same wording, nothing written.
            Console.Error.WriteLine("A schedule is armed, and a schedule and a manual block can't run together.")
            Console.Error.WriteLine("Clear it first with 'monkmode schedule --clear' (any open window still runs to its end), then start the block.")
            Return 3
        ElseIf arm.Outcome = Blocker.ArmOutcome.BadInput Then
            ' FX4 (F30): the writer's own control-character refusal. Normally unreachable (the
            ' check above already refused), so reaching it means a value slipped past the CLI
            ' chokepoint - same refusal, nothing written.
            Console.Error.WriteLine(arm.Message)
            Return 1
        ElseIf arm.Outcome = Blocker.ArmOutcome.Frozen Then
            ' B7: the stored config failed its integrity check. Re-stamping it would
            ' re-bless a tamper, so MonkMode refuses to change anything at all.
            Console.Error.WriteLine("The current MonkMode configuration failed its integrity check, so it is frozen and cannot be added to.")
            Console.Error.WriteLine("Nothing can be armed or added while it is frozen, and a frozen config never lifts by itself - see 'monkmode help'.")
            Return Blocker.ExitArmFailed
        ElseIf Not arm.Ok Then
            Console.Error.WriteLine("Could not arm the block right now (the service was writing). Try again.")
            Return Blocker.ExitArmFailed
        End If

        ' The hosts block is the UNION over the snapshot, CONFIG TRUTH and this arm's own
        ' entries (FX5/F5), so arming a second block can never unblock the first one's
        ' sites - not even with the snapshot file deleted. A fresh rewrite discards the
        ' stale snapshot.
        '
        ' FX5 (F6): from here to the partner-code print the slot is COMMITTED - ArmSlot has
        ' saved it, stamped it and refreshed the C1b backup - so NOTHING below may throw
        ' past the print. A code is minted once and stored only as a salted hash, so an
        ' exception here used to cost a committed block its only early exit. Every remaining
        ' step is therefore guarded and best-effort; each one that fails says so and the
        ' block continues, because the block IS armed and saying otherwise would be a lie.
        Dim hostsWarning As String = ""
        ' Kept as a local (FX5 leftover, 19/08/2026): the service-install warning below used to
        ' promise unconditionally that "the blocked sites stay in your hosts file meanwhile",
        ' which is a lie in the double-failure case - hosts write failed AND the service could
        ' not start means nothing is blocking right now. The answer differs, so the fact has to
        ' survive down to it.
        Dim hostsWritten As Boolean = Blocker.TryWriteArmHostsBlock(domains, arm.FreshRewrite, hostsWarning)
        If Not hostsWritten Then
            Console.Error.WriteLine(hostsWarning)
        End If
        Dim partnerCode As String = arm.PartnerCode
        ' B5a: snapshot the user's current browser DoH policy BEFORE the service
        ' starts and forces it off, so teardown restores the pre-block state (no
        ' data loss). Must precede InstallAndStart - the service sets the policy in
        ' its OnStart. Never aborts arming the block; if it fails, teardown will
        ' leave the DoH-off policy in place rather than risk deleting a user value.
        '
        ' M0 (F6): ONLY on a genuinely fresh arm. Re-snapshotting beside a live block
        ' captures MonkMode's OWN forced-off DoH policy as "the user's prior", and
        ' teardown then restores that and consumes the snapshot - the P0 the 13/08
        ' estate bug-hunt found live on this machine. Full argument at
        ' Blocker.ShouldSnapshotDohPolicy. On a non-fresh arm the EXISTING snapshot is
        ' the truth and is left untouched; nothing is warned about, because nothing
        ' was lost.
        If Blocker.ShouldSnapshotDohPolicy(alreadyArmed, Blocker.DohSnapshotExists()) Then
            If Not Blocker.WriteDohSnapshot() Then
                Console.Error.WriteLine("Warning: could not snapshot current browser DoH settings; MonkMode will leave 'Secure DNS off' in place at expiry rather than restore/remove it.")
            End If
        End If
        ' FX5 (F6): guarded for the same reason as the hosts write above - the SCM can throw
        ' (a wedged service, a denied SCM handle), and an unguarded throw here reached Main's
        ' catch and swallowed the partner-code print of an ALREADY-COMMITTED block. A failed
        ' install lifts nothing: the slot is armed and the hosts entries are written, and the
        ' service is registered AUTO_START, so it comes up at the next boot even if it cannot
        ' be started now. Say what is paused rather than dropping the block on the floor.
        Try
            ServiceTools.ServiceInstaller.InstallAndStart(Blocker.ServiceName, Blocker.ServiceDisplay, serviceExe)
        Catch ex As Exception
            For Each line As String In FormatServiceInstallFailureLines(ex.Message, hostsWritten)
                Console.Error.WriteLine(line)
            Next
        End Try
        ' D4d rider: a FRESH manual arm clears an orphaned notifier first, so this block's
        ' spawn wins D4c's single-instance claim instead of standing down behind a leftover
        ' pointed at the previous block. M0 rider (F6): when something is ALREADY armed the
        ' notifier it would kill is not an orphan but the live block's working one, so the
        ' arm leaves it alone - see Blocker.ManualArmKillPolicy.
        Blocker.RegisterAndLaunchNotifier(Blocker.ManualArmKillPolicy(alreadyArmed))

        ' D3b: record this arm to the separate, non-MAC stats history (best-effort - Stats.RecordBlockStart
        ' swallows every error and never throws, so a stats failure can't perturb the block just armed
        ' above). COUNTS only (no site/app names) land in the plaintext file; the block is already fully
        ' armed, so this has ZERO enforcement authority - it is pure telemetry for `monkmode stats`.
        ' The recorded start is the ENFORCEMENT window's start (now, or the --start moment),
        ' so a delayed block's planned time is the block, not the wait before it.
        Stats.RecordBlockStart(windowStart, untilDate, domains.Count, apps.Count, committed, coolOffSeconds)

        If startAt.HasValue Then
            Console.WriteLine("Block #" & arm.Id & " is scheduled to start " & startAt.Value.ToString() & " and run until " & untilDate.ToString() & " (" & Humanize(untilDate.Subtract(startAt.Value)) & ").")
        Else
            Console.WriteLine("Block #" & arm.Id & " is now active until " & untilDate.ToString() & " (" & Humanize(untilDate.Subtract(DateTime.Now)) & ").")
        End If
        If domains.Count > 0 Then Console.WriteLine("  Sites: " & String.Join(", ", domains))
        If apps.Count > 0 Then Console.WriteLine("  Apps:  " & String.Join(", ", apps))
        ' D2c: confirm the all-session widening, but only when there are apps to kill (the flag is a
        ' no-op without a blocklist - never claim an effect that won't happen).
        If allSessionKill AndAlso apps.Count > 0 Then Console.WriteLine("  App-kill: ALL sessions (blocked apps are killed in every logged-in session, not just this one).")
        Console.WriteLine("Close and reopen your browser to see the block. It cannot be removed until the timer ends.")

        ' Ledger 319: every block is committed, so this notice is unconditional. It is the last
        ' thing said before the code is printed, because the code is now the ONLY early exit
        ' that exists - there is no self-serve wait behind it and no escape hatch under it.
        Console.WriteLine("")
        Console.WriteLine("This block ends at its end time, or earlier ONLY with the accountability code below. There is no other way out - if you lose the code, you wait.")

        ' C3b: show the partner accountability code ONCE - this is the only time it
        ' is ever displayed (it is stored only as a salted one-way hash, never in
        ' plaintext, never logged). Relay it to your accountability partner now; to
        ' leave early, they authorise `monkmode unblock --code <CODE>` and the block
        ' lifts within ~10s. A fresh code is minted for every new block.
        Console.WriteLine("")
        ' P31: the header keeps the literal "Emergency unlock code" and the code stays on the
        ' IMMEDIATELY following line, indented - tools\smoke\cv-d-smoke.ps1's ParseCode (:113-118)
        ' reads exactly that shape. "for block <id>" is appended so a machine running several
        ' blocks tells the partner WHICH one this code opens.
        Console.WriteLine(FormatUnlockCodeHeader(arm.Id))
        Console.WriteLine("    " & partnerCode)
        ' F74: name WHO to send it to, at the one moment the code is on screen. The label is
        ' otherwise read by nothing at runtime - `setup` stored it, echoed the argument back,
        ' and no later command ever mentioned it again, so a partner who was never actually
        ' given a code looked identical to one who was. This line goes BELOW the code, never
        ' between the header and it: cv-d-smoke.ps1's ParseCode (:113-118) takes the line
        ' IMMEDIATELY after the header as the code, and that adjacency is the contract.
        ' Empty label (none set, or an incomplete/tampered setup file) prints nothing - the
        ' header already says to hand it over.
        Dim relay As String = FormatPartnerRelayLine(partnerLabel)
        If relay <> "" Then Console.WriteLine(relay)
        Console.WriteLine("To end block " & arm.Id & " early, they run:  monkmode unblock --code <CODE>")
        Return 0
    End Function

    Private Function DoStatus() As Integer
        ' Deploy-gap + two-installs visibility (backlog, 30/08/2026): say WHICH install is
        ' answering, and which build is in it, before saying anything about blocks. `dist\`
        ' and `C:\Program Files\MonkMode\` keep SEPARATE config and setup state, so the same
        ' question genuinely has two different true answers on this machine and "dist\ says
        ' it isn't set up while Program Files is" read as a bug rather than as two installs.
        ' First, unconditionally - including on the never-installed path below, which is the
        ' exact message that gets attributed to the wrong install.
        Console.WriteLine(ReadBuildIdentityLine())
        If Not Blocker.ServiceIsInstalled() Then
            Console.WriteLine("MonkMode: no block has ever been installed on this machine.")
            Return 0
        End If
        ' P32 (v1.1 S5): with slots armed, `status` is the SLOT TABLE - one fixed-width row per
        ' block plus its Exit sentence. Read-only: ReadSlotViews opens the config and writes
        ' nothing (no MAC re-stamp, no backup refresh, no trigger), so this is safe to run
        ' against a live block at any moment. It comes FIRST because a v10 config's [Time]
        ' Until / [Schedule] Spec are the over-blocking v9 MIRROR of the slots - reading the
        ' mirror here would collapse eight blocks into one misleading line.
        Dim macValid As Boolean = False
        Dim views As List(Of Blocker.SlotView) = Blocker.ReadSlotViews(macValid)
        If views.Count > 0 Then
            Console.WriteLine(FormatStatusHeading(views.Count))
            Console.WriteLine(FormatSlotTableHeader())
            For Each v As Blocker.SlotView In views
                Console.WriteLine(FormatSlotRow(v))
                Console.WriteLine(SlotExitIndent & FormatSlotExitLine(v))
            Next
            ' 313(a): one line, under the whole table, for the thing the "Ends" column cannot say
            ' by itself - those stamps advance on machine-ON time.
            If AnyActiveSlot(views) Then Console.WriteLine(FormatMonotonicNoteLine())
            ' v1.1 S7b (P48): what the blocks have actually stopped today, from the
            ' display-only sidecars. "" (printed as nothing) on a quiet day or when the
            ' sidecar is absent/corrupt - `status` must never grow a row of zeros, and it
            ' must never fail because a counter file did.
            Dim todayLine As String = BlockedTodayStatusLine()
            If todayLine <> "" Then Console.WriteLine(todayLine)
            ' B7: never render a reassuring exit story over a config that failed its integrity
            ' check. Ledger 319: the Exit column now reads "code" on every manual row regardless,
            ' and a frozen config cannot be lifted by the code either - so say plainly why none
            ' of it can be acted on.
            If Not macValid Then
                Console.WriteLine("")
                Console.WriteLine(ConfigFrozenNoteLine())
            End If
            ' The table is read off the CONFIG, which stays true whether or not the service is
            ' up - so say which half is paused rather than implying either "all fine" or
            ' "nothing is blocked". The hosts block itself survives a stopped service; what
            ' stops is app-kill, self-repair and the countdown to the next exit.
            If Not Blocker.ServiceIsRunning() Then
                Console.WriteLine("")
                Console.WriteLine(ServicePausedNoteLine())
            End If
            Return 0
        End If
        ' C5b (c4): a schedule-only block reads as BlockIsActive()=False (its [Time] Until is the past
        ' sentinel), so report an armed schedule HERE, before the manual-block/idle branches below - or
        ' status would misreport an armed schedule as "no active block (idle)". Read-only; writes nothing.
        If Blocker.ScheduleIsArmed() Then
            Dim windows As List(Of String) = Nothing, sites As List(Of String) = Nothing, apps As List(Of String) = Nothing
            Blocker.DescribeScheduleSpec(Blocker.ArmedScheduleSpec(), windows, sites, apps)
            Console.WriteLine("MonkMode: SCHEDULE ARMED")
            If windows.Count > 0 Then Console.WriteLine("  Windows: " & String.Join("; ", windows))
            If sites.Count > 0 Then Console.WriteLine("  Sites:   " & String.Join(", ", sites))
            If apps.Count > 0 Then Console.WriteLine("  Apps:    " & String.Join(", ", apps))
            ' D5: the LIVE window state (open now vs waiting), from the service-maintained ActiveUntil.
            If Blocker.ScheduleWindowIsOpen() Then
                Console.WriteLine("  Now:     a window is OPEN - sites/apps are blocked until it closes (it can't be ended early).")
            Else
                Console.WriteLine("  Now:     no window open right now - the next one opens automatically at its time.")
            End If
            Console.WriteLine("  Windows open automatically at their times; run 'monkmode schedule --show' for detail.")
            Return 0
        End If
        ' 313(a): the v9 fallback used to ask `ends > DateTime.Now`, which is NOT how expiry is
        ' decided. After a shutdown or a long sleep the wall clock runs past Until while the
        ' monotonic mark lags behind it, so this branch printed "no active block (service
        ' installed but idle)" over a block the service was still fully enforcing. Both the
        ' decision and the remaining now come off the mark (Blocker.LegacyBlockIsActive /
        ' FormatRemainingParenthetical), so the idle line is reached only when the block is
        ' genuinely over or none is configured. The old `ServiceIsRunning()` conjunct is gone from
        ' the DECISION for the same reason it is not in the slot table's: a stopped service does
        ' not mean nothing is blocked (the hosts entries survive it, and it restarts itself). It
        ' is reported by the same paused NOTE the table prints, below.
        Dim legacy As Blocker.LegacyView = Blocker.ReadLegacyView()
        If Blocker.LegacyBlockIsActive(legacy.MacValid, legacy.Ends, legacy.Mark) Then
            Console.WriteLine("MonkMode: ACTIVE")
            Console.WriteLine("  Ends:  " & legacy.Ends.ToString() & " " & FormatRemainingParenthetical(legacy.Ends, legacy.Mark))
            Dim sites As String = Blocker.BlockedSites()
            Dim apps As String = Blocker.BlockedApps()
            If sites <> "" Then Console.WriteLine("  Sites: " & sites.Replace(";", " "))
            If apps <> "" Then Console.WriteLine("  Apps:  " & apps.Replace(";", " "))
            ' D5: the exit story - ledger 319, one sentence, identical to the slot table's.
            Console.WriteLine("  " & FormatExitStatusLine())
            Console.WriteLine(FormatMonotonicNoteLine())
            ' B7, as in the table above: a frozen config is why the end stamp and the remaining
            ' cannot be trusted or acted on - it reads as ACTIVE precisely because nothing lifts.
            If Not legacy.MacValid Then
                Console.WriteLine("")
                Console.WriteLine(ConfigFrozenNoteLine())
            End If
            If Not Blocker.ServiceIsRunning() Then
                Console.WriteLine("")
                Console.WriteLine(ServicePausedNoteLine())
            End If
        Else
            Console.WriteLine("MonkMode: no active block (service installed but idle).")
        End If
        Return 0
    End Function

    ' D3b: `monkmode stats` - a read-only summary of block history from the separate non-MAC stats file
    ' (Stats.vb). Display-only: ZERO enforcement authority, never touches a block. A missing/corrupt file
    ' simply reads as no/less history (Stats.ReadRecords is tolerant), so stats can never error a user out.
    ' v1.1 S7b (P48): `stats` now shows PLANNED history (monkmode_stats, above) AND the
    ' ACTUALS the two sidecars recorded (%ProgramData%\MonkMode\, S7b). Both halves are
    ' independently tolerant, so either can be missing: a machine that has armed blocks but
    ' never had the sidecar (an older install) shows only the planned half, and a machine
    ' whose monkmode_stats was deleted still shows its streak. Only when BOTH are empty do
    ' we print the "no blocks yet" hint.
    Private Function DoStats() As Integer
        Dim s As Stats.StatsSummary = Stats.SummarizeAsOf(Stats.ReadRecords(), DateTime.Now)
        Dim actuals As List(Of String) = FormatStatsActuals(StatsSidecar.ReadMerged(),
                                                           StatsSidecar.DayKeyFor(DateTime.Now))
        If Not s.HasAny AndAlso actuals.Count = 0 Then
            Console.WriteLine("No blocks recorded yet. Start one with, e.g.:  monkmode block --sites reddit.com --for 2h")
            Return 0
        End If
        Console.WriteLine("MonkMode stats")
        If s.HasAny Then
            Console.WriteLine("  Blocks started:   " & s.TotalBlocks & "  (" & s.CompletedBlocks & " completed, " & s.ActiveOrUpcomingBlocks & " active/upcoming)")
            Console.WriteLine("  Committed blocks: " & s.CommittedBlocks)
            Console.WriteLine("  Total focus time: " & Humanize(s.TotalPlannedTime) & " (planned)")
            Console.WriteLine("  Longest block:    " & Humanize(s.LongestPlannedBlock))
            Console.WriteLine("  First block:      " & s.FirstStart.ToString("yyyy-MM-dd"))
            Console.WriteLine("  Latest block:     " & s.LastStart.ToString("yyyy-MM-dd"))
        End If
        For Each line As String In actuals
            Console.WriteLine(line)
        Next
        Return 0
    End Function

    ' v1.1 S7b (P47/P48), PURE + DISPLAY-ONLY: the "actuals" block of `stats`, built from
    ' the MERGED sidecars. An EMPTY list means "nothing recorded" - which is what lets
    ' DoStats decide whether it has anything to print at all - and that is exactly the
    ' answer a missing, corrupt, garbage or hostile sidecar produces, since StatsSidecar
    ' reads every failure as zeros. Emptiness is keyed on armed SECONDS: a file holding
    ' only kills with no held time is corrupt-ish, and the honest reading of it is "we have
    ' no measured focus time", so nothing is claimed.
    Friend Function FormatStatsActuals(ByVal d As StatsSidecar.StatsData, ByVal todayKey As String) As List(Of String)
        Dim lines As New List(Of String)
        If d Is Nothing OrElse d.Lifetime.ArmedSeconds <= 0 Then Return lines
        Dim today As StatsSidecar.Counts = StatsSidecar.TotalForDay(d, todayKey)
        lines.Add("  Time blocked:     " & Humanize(TimeSpan.FromSeconds(d.Lifetime.ArmedSeconds)) & " (actual)")
        lines.Add("  Apps closed:      " & d.Lifetime.Kills)
        lines.Add("  Browser nudges:   " & d.Lifetime.Redirects)
        lines.Add("  Focus days:       " & StatsSidecar.FocusDayCount(d) &
                  "  (streak " & StatsSidecar.CurrentStreak(d, todayKey) &
                  ", longest " & StatsSidecar.LongestStreak(d) & ")")
        lines.Add("  Today:            " & Humanize(TimeSpan.FromSeconds(today.ArmedSeconds)) &
                  " blocked, " & (today.Kills + today.Redirects) & " attempt(s) stopped")
        Return lines
    End Function

    ' The IO half of the `status` today-line: read the merged sidecars and format them.
    ' Best-effort - "" on any failure, so `status` can never be broken by a counter file.
    Private Function BlockedTodayStatusLine() As String
        Try
            Dim today As StatsSidecar.Counts =
                StatsSidecar.TotalForDay(StatsSidecar.ReadMerged(), StatsSidecar.DayKeyFor(DateTime.Now))
            Return FormatBlockedTodayLine(today.Kills, today.Redirects)
        Catch ex As Exception
            Return ""
        End Try
    End Function

    ' v1.1 S7b (P48), PURE + DISPLAY-ONLY: `status`'s one-line "what MonkMode has stopped
    ' today" note, or "" when it has stopped nothing (a quiet day says nothing rather than
    ' printing a row of zeros). Counts come from the merged sidecars and have no
    ' enforcement authority whatsoever.
    Friend Function FormatBlockedTodayLine(ByVal kills As Long, ByVal redirects As Long) As String
        Dim k As Long = If(kills > 0, kills, 0L)
        Dim r As Long = If(redirects > 0, redirects, 0L)
        If k + r <= 0 Then Return ""
        Return "  Today: " & (k + r) & " stopped (" & k & " app close(s), " & r & " browser nudge(s))"
    End Function

    Private Function DoAdd(ByVal args As String()) As Integer
        Dim domains As New List(Of String)
        domains.AddRange(SplitList(GetOption(args, "--sites")))
        If domains.Count = 0 Then
            Console.Error.WriteLine("Provide sites to add with --sites a.com,b.com")
            Return 1
        End If
        ' FX4 (F30): `add` grows a LIVE slot's MAC-covered Sites, so a control character here would
        ' brick the config on the service's next tick instead of at arm time - refused up front,
        ' before the request trigger is written. (The service's own MergeSiteList drops whitespace-
        ' bearing tokens, but that is a silent SUBSET, exactly the under-block this repo refuses.)
        Dim addCtrlErr As String = ""
        If Not Blocker.TryRejectControlChars("site", domains, addCtrlErr) Then
            Console.Error.WriteLine(addCtrlErr)
            Return 1
        End If
        ' P42 (v1.1 S5): `add` is SLOT-ADDRESSED and SERVICE-ADJUDICATED. The CLI validates the
        ' request and drops `monkmode_add.request.<id>`; the service grows THAT slot's
        ' MAC-covered Sites (growth-only) on its next tick and P37 then reconciles the hosts
        ' snapshot from config truth. S3a/S3b's honest refusal ("'add' can't extend a block yet")
        ' is retired with it: the old CLI-side path wrote the v9 [User] CustomSites plus the
        ' snapshot and NO slot, so the per-tick reconciliation stripped the added sites back out
        ' within ~10s.
        Dim armedIds As List(Of Integer) = Blocker.ArmedSlotIds()
        If armedIds.Count > 0 Then
            Dim targetArg As String = GetOption(args, "--id")
            Dim target As Integer
            If targetArg <> "" Then
                If Not Integer.TryParse(targetArg.Trim(), target) OrElse Not armedIds.Contains(target) Then
                    Console.Error.WriteLine("No armed block #" & targetArg.Trim() & ". Run 'monkmode status' to see the armed blocks.")
                    Return 1
                End If
            ElseIf armedIds.Count > 1 Then
                ' The P33 rule applied to `add`: with several blocks running, an unnamed `add`
                ' would silently widen whichever one happened to be first. Widening the wrong
                ' block is not a lift, but it is a block the user never asked for and cannot
                ' undo before the timer ends - so name it.
                Console.Error.WriteLine("More than one block is active. Name the one you mean:  monkmode add --id <N> --sites " & String.Join(",", domains))
                For Each line As String In Blocker.ArmedSlotLines()
                    Console.Error.WriteLine(line)
                Next
                Return 1
            Else
                target = armedIds(0)
            End If
            ' B7: the service never widens a FROZEN config (that would mean re-stamping bytes
            ' it did not verify), so an `add` against one would be accepted, binned and
            ' reported as applied. Refuse it here with the same message `block` gives.
            If Not Blocker.ConfigIsMacValid() Then
                Console.Error.WriteLine("The current MonkMode configuration failed its integrity check, so it is frozen and cannot be added to.")
                Console.Error.WriteLine("Nothing can be armed or added while it is frozen, and a frozen config never lifts by itself - see 'monkmode help'.")
                Return Blocker.ExitArmFailed
            End If
            ' P40: the service reads a content-bearing trigger only up to TriggerMaxBytes and
            ' deletes an oversize one WITHOUT changing state, so RequestAdd refuses rather than
            ' write a request that will be binned. It also UNIONS with any request still
            ' pending for this block, so a second `add` inside one ~10s tick cannot overwrite
            ' (and silently lose) sites the CLI has already reported as accepted - which means
            ' the cap is measured on the MERGED request, and a refusal here leaves the pending
            ' one intact and applying.
            If Not Blocker.RequestAdd(target, domains) Then
                Console.Error.WriteLine("That's too many sites for one 'add' (a pending request is capped at " & Blocker.TriggerMaxBytes & " bytes, and block " & target & " already has one waiting).")
                Console.Error.WriteLine("The sites already queued for block " & target & " still apply within ~10s - run this 'add' again afterwards, or split it up.")
                Return 1
            End If
            Console.WriteLine("Added to block " & target & "; MonkMode applies it within ~10s.")
            ' Honest about latency: the trigger is durable, so a stopped service applies it at
            ' its next start rather than losing it - but "~10s" would be a lie right now.
            If Not Blocker.ServiceIsRunning() Then
                Console.WriteLine("  (the MonkMode service isn't running at the moment - it applies this the moment it next starts.)")
            End If
            Return 0
        End If

        ' SD-c1: `add` targets a manual block. When a schedule is armed, edit the schedule instead
        ' (re-run `monkmode schedule` with the full site list) - the schedule's sites live in its
        ' MAC-covered Spec, not a slot `add` can address.
        If Blocker.ScheduleIsArmed() Then
            Console.Error.WriteLine("A schedule is armed. To change its sites, re-run 'monkmode schedule --sites ... --windows ...' with the full list.")
            Return 1
        End If
        ' No slot to address. The v9 CLI-side append (Blocker.AppendAddToHosts) is deliberately
        ' NOT used as a fallback: it writes sites that no slot owns, which the next tick's P37
        ' reconciliation removes again. Refusing is honest and costs one `block` command.
        Console.Error.WriteLine("No block slot to add to. Start one with:  monkmode block --sites " & String.Join(",", domains) & " --for <duration>")
        Return 1
    End Function

    ' C5b (c3): `schedule` arms/edits/clears a SCHEDULE-ONLY block - a recurring wall-clock rule
    ' ("Mon-Fri 09:00-17:00") the service opens/closes automatically at manual strength (SD1: an
    ' open window holds until it closes). Unlike `block` it does NOT open a block now and does NOT
    ' write the hosts snapshot (the service creates monkmode_hosts.block on window-open, c1); it
    ' writes the MAC-covered [Schedule] Spec + the past-Until sentinel, then installs/starts the
    ' service (+ notifier/guardian) so windows are evaluated. `--clear` blanks the Spec (future
    ' windows vanish; a currently-open window still runs to its monotonic close, C5a §7). SD-c1:
    ' refuses while a manual block is active (mutually exclusive with `block` in C5b).
    Private Function DoSchedule(ByVal args As String()) As Integer
        ' C5b (c4): read-only introspection FIRST - writes nothing, touches no service/hosts/registry
        ' and returns before any arm/clear path. `--show` prints the armed schedule in a human form;
        ' `--validate` dry-runs the builder and prints the canonical Spec or the exact grammar error.
        If HasFlag(args, "--show") Then Return DoScheduleShow()
        If HasFlag(args, "--validate") Then Return DoScheduleValidate(args)

        ' `--clear`: blank the Spec (only if a schedule is armed) -> the service tears down after any
        ' open window closes. Never installs/starts anything; a no-op message if nothing is armed.
        If HasFlag(args, "--clear") Then
            If Not Blocker.ScheduleIsArmed() Then
                Console.WriteLine("No schedule is armed. Nothing to clear.")
                Return 0
            End If
            Blocker.WriteScheduleConfig("")
            Console.WriteLine("Schedule cleared. No future windows will open.")
            Console.WriteLine("If a window is open now it runs to its end; MonkMode then tears down within ~10s.")
            Return 0
        End If

        ' C6a: required first-run setup gates a fresh ARM only - NOT --show/--validate
        ' (read-only) or --clear (a reduction), which returned above. Same gate as DoBlock,
        ' so a first schedule also goes through the accountability-model explanation.
        If Not Blocker.SetupIsComplete() Then Return SetupRequired()

        ' SD-c1: a manual `--for` block and a schedule are mutually exclusive.
        '
        ' v1.1 S3b - THE GUARD MADE SOUND. It still stands, because WriteScheduleConfig's
        ' fresh-scaffold branch builds a BRAND-NEW ini: with slots armed it would delete every
        ' [SlotN] section and stamp a fresh VALID MAC over the result, silently lifting every
        ' running block. But S2's guard was BlockIsActive() ALONE, and BlockIsActive short-
        ' circuits False the moment the service is not RUNNING - so on a registered-but-
        ' STOPPED machine with slots armed (exactly the state this machine sits in between
        ' blocks) `monkmode schedule` could still wipe them. AnySlotArmed() answers the real
        ' question - does the CONFIG carry slots - without consulting the SCM at all, and
        ' fail-SAFE (an unreadable config reads as ARMED). The two are OR'd, so the v9
        ' schedule-only shape (no slots, service running) is still caught by the old arm.
        ' WriteScheduleConfig refuses independently as the structural backstop.
        If Blocker.AnySlotArmed() OrElse Blocker.BlockIsActive() Then
            Console.Error.WriteLine("A block is armed. Finish or exit it before setting a schedule.")
            Return 3
        End If

        ' Gather + validate the schedule args, serialising to the compact v1 Spec (a malformed/empty
        ' window or an empty site list is rejected here - the CLI never stamps a garbage Spec).
        Dim sites As New List(Of String)
        sites.AddRange(SplitList(GetOption(args, "--sites")))
        Dim apps As New List(Of String)
        apps.AddRange(SplitList(GetOption(args, "--apps")))
        Dim windowsArg As String = GetOption(args, "--windows")

        Dim spec As String = "", err As String = ""
        If Not Blocker.TryBuildScheduleSpec(windowsArg, sites, apps, spec, err) Then
            Console.Error.WriteLine(err)
            Return 1
        End If

        Dim serviceExe As String = Path.Combine(Blocker.AppDir(), Blocker.ServiceExeName)
        If Not File.Exists(serviceExe) Then
            Console.Error.WriteLine("Cannot find " & Blocker.ServiceExeName & " next to monkmode.exe (" & Blocker.AppDir() & ").")
            Console.Error.WriteLine("Deploy the service and notifier into the same folder as the CLI.")
            Return 2
        End If

        ' A FRESH arm (nothing armed yet) captures the browser DoH snapshot BEFORE the service forces
        ' DoH off during windows (so teardown restores the user's prior policy - no data loss) and
        ' clears any stale hosts snapshot left by a prior block (so the service's window-open union
        ' starts clean). Neither runs on a re-arm: re-snapshotting DoH mid-open-window would capture
        ' our own forced-off state as the "prior", and a live schedule snapshot must not be dropped.
        '
        ' M0 (F6): "nothing armed yet" now means nothing at all - SLOTS as well as a schedule. This
        ' guard was written when a schedule was the only thing that could already be running; v1.1 S2
        ' lets a manual block coexist, so `schedule` beside a live manual block hit the SAME
        ' re-snapshot bug the manual path did. Both changes only ever do LESS: one fewer DoH
        ' overwrite, and one fewer hosts-snapshot deletion (deleting it while a slot is armed would
        ' strip that slot's sites out of the B2 repair source, which nothing would put back).
        Dim alreadyArmed As Boolean = Blocker.AnythingArmed()
        If Blocker.ShouldSnapshotDohPolicy(alreadyArmed, Blocker.DohSnapshotExists()) Then
            If Not Blocker.WriteDohSnapshot() Then
                Console.Error.WriteLine("Warning: could not snapshot current browser DoH settings; MonkMode will leave 'Secure DNS off' in place at teardown rather than restore/remove it.")
            End If
        End If
        If Not alreadyArmed Then
            Blocker.DeleteSnapshot()
        End If

        Blocker.WriteScheduleConfig(spec)
        ServiceTools.ServiceInstaller.InstallAndStart(Blocker.ServiceName, Blocker.ServiceDisplay, serviceExe)
        ' D4d rider: a schedule arm/re-arm can land during an OPEN window with a healthy
        ' notifier running - never kill it for tidiness (Blocker.ScheduleArmKillsLeftovers).
        Blocker.RegisterAndLaunchNotifier(Blocker.ScheduleArmKillsLeftovers)

        Console.WriteLine("Schedule armed. Windows open automatically at their times.")
        Console.WriteLine("  Windows: " & windowsArg.Trim())
        Console.WriteLine("  Sites:   " & String.Join(", ", sites))
        If apps.Count > 0 Then Console.WriteLine("  Apps:    " & String.Join(", ", apps))
        Console.WriteLine("During a window the block holds at full strength until the window closes; it cannot be ended early.")
        Console.WriteLine("Change it any time with 'monkmode schedule ...'; stop future windows with 'monkmode schedule --clear'.")
        Return 0
    End Function

    ' C5b (c4): `schedule --show` - READ-ONLY. Print the armed schedule's windows/sites/apps in a human
    ' form (a cosmetic reverse of the compact Spec; NO live window/remaining state - that folds into the
    ' richer `status`, D5). WRITES NOTHING. "No schedule is armed" when none - a tampered Spec reads as
    ' not-armed (frozen by the service) and is likewise not shown.
    Private Function DoScheduleShow() As Integer
        If Not Blocker.ScheduleIsArmed() Then
            Console.WriteLine("No schedule is armed.")
            Console.WriteLine("Arm one with:  monkmode schedule --sites a.com,b.com --windows ""Mon-Fri 09:00-17:00""")
            Return 0
        End If
        Dim windows As List(Of String) = Nothing, sites As List(Of String) = Nothing, apps As List(Of String) = Nothing
        Blocker.DescribeScheduleSpec(Blocker.ArmedScheduleSpec(), windows, sites, apps)
        Console.WriteLine("MonkMode schedule: ARMED")
        If windows.Count > 0 Then
            Console.WriteLine("  Windows:")
            For Each w As String In windows
                Console.WriteLine("    " & w)
            Next
        End If
        If sites.Count > 0 Then Console.WriteLine("  Sites: " & String.Join(", ", sites))
        If apps.Count > 0 Then Console.WriteLine("  Apps:  " & String.Join(", ", apps))
        Console.WriteLine("Windows open automatically at their times; during a window the block holds until it closes.")
        Console.WriteLine("Change it with 'monkmode schedule --sites ... --windows ...'; stop future windows with 'monkmode schedule --clear'.")
        Return 0
    End Function

    ' C5b (c4): `schedule --validate --sites ... --windows "..."` - READ-ONLY dry-run. Reuses the SAME
    ' Blocker.TryBuildScheduleSpec the arm path uses (no second parser), printing the canonical v1 Spec
    ' on success or the EXACT grammar error on failure. WRITES NOTHING and never installs/starts the
    ' service (so it needs no admin and can't touch a live block). Returns 0 valid / 1 invalid so it is
    ' scriptable. NB (design gotcha #4): the builder requires >=1 site, so --validate takes --sites too.
    Private Function DoScheduleValidate(ByVal args As String()) As Integer
        Dim sites As New List(Of String)
        sites.AddRange(SplitList(GetOption(args, "--sites")))
        Dim apps As New List(Of String)
        apps.AddRange(SplitList(GetOption(args, "--apps")))
        Dim windowsArg As String = GetOption(args, "--windows")

        Dim spec As String = "", err As String = ""
        If Not Blocker.TryBuildScheduleSpec(windowsArg, sites, apps, spec, err) Then
            Console.Error.WriteLine(err)
            Return 1
        End If
        Console.WriteLine("Valid. This would arm:")
        Dim windows As List(Of String) = Nothing, s2 As List(Of String) = Nothing, a2 As List(Of String) = Nothing
        Blocker.DescribeScheduleSpec(spec, windows, s2, a2)
        For Each w As String In windows
            Console.WriteLine("    " & w)
        Next
        If s2.Count > 0 Then Console.WriteLine("  Sites: " & String.Join(", ", s2))
        If a2.Count > 0 Then Console.WriteLine("  Apps:  " & String.Join(", ", a2))
        Console.WriteLine("  Spec:  " & spec)
        Console.WriteLine("(Dry run - nothing was armed. Drop --validate to arm this schedule.)")
        Return 0
    End Function

    ' LEDGER 319 (30/08/2026) - `unblock` HAS EXACTLY ONE JOB: SUBMIT THE PARTNER CODE.
    '
    ' Before this slice there were two ways out of a running block that needed no code:
    ' `unblock --force` (an unconditional teardown - service deleted, hosts stripped, config
    ' zeroed) and a bare `unblock` (the self-serve cooling-off wait, which lifted the block
    ' after ~1h of active machine time). Samrath's words on 30/08/2026: "i dont like how i
    ' can force unblock it regardless ... i should only be able to unblock with code." Both
    ' are GONE - the flag, the teardown, the request/cancel triggers and the service-side
    ' timing that honoured them. A running block now ends on exactly two events: its own end
    ' time, or a partner code the SERVICE verifies. Nothing else, at any privilege level the
    ' CLI has. The honest ceiling is unchanged and now unsoftened: B10 (boot elsewhere and
    ' edit the disk) still wins, and that is deliberately not a feature of this program.
    '
    ' THERE IS NO RECOVERY FOR A LOST CODE. That is the design, not an oversight: an escape
    ' hatch that exists is an escape hatch that gets used at 2am. The cost is real and was
    ' accepted with it - a config that fails its integrity check (B7 freeze) can be lifted by
    ' NOTHING, not even the code (ClassifyPartnerCodeSignal requires a valid MAC), so it holds
    ' until B10. `monkmode help` says so in those words.
    '
    ' C3b (R1), UNCHANGED and now the only exit here: `--code <CODE>` drops the ONE
    ' content-bearing trigger with the candidate; the SERVICE alone KDF-verifies it against
    ' the MAC-covered hash and, on a match, lifts via its own stopMe(). The CLI has ZERO lift
    ' authority - it only submits (an attacker running the CLI cannot forge a preimage, swap
    ' the MAC-covered verifier, or skip the service-side lift). A wrong/blank/tampered code
    ' leaves every block standing.
    Private Function DoUnblock(ByVal args As String()) As Integer
        ' D5 (friendly validation): warn on unrecognised --flags without failing. This is
        ' where a habit-typed `--force` now lands: an "unknown flag" note, then the ordinary
        ' refusal below. It is deliberately a WARNING and not a special-cased error, because
        ' `--force` should read as a flag that does not exist, not as one being withheld.
        Dim unknownOpts As List(Of String) = UnknownOptions(args, UnblockOptionNames())
        If unknownOpts.Count > 0 Then
            Console.Error.WriteLine("Note: unrecognised option(s) " & String.Join(", ", unknownOpts) & " - ignored.")
        End If

        ' v1.1 S3b: the exit surface is SLOT-ADDRESSED (P40), so this needs the ids.
        ' `--id <N>` (P35's flag name, shared with `add` and `schedule`) targets ONE block.
        ' Read BEFORE the liveness check so "nothing armed" is decided off the config, not
        ' off whether the service happens to be up.
        Dim armedIds As List(Of Integer) = Blocker.ArmedSlotIds()
        Dim targetArg As String = GetOption(args, "--id")
        If targetArg <> "" Then
            Dim wanted As Integer
            If Not Integer.TryParse(targetArg.Trim(), wanted) OrElse Not armedIds.Contains(wanted) Then
                Console.Error.WriteLine("No armed block #" & targetArg.Trim() & ". Run 'monkmode status' to see the armed blocks.")
                Return 1
            End If
            armedIds = New List(Of Integer) From {wanted}
        End If
        If armedIds.Count = 0 AndAlso Not Blocker.BlockIsActive() Then
            Console.Error.WriteLine("No active block to unblock.")
            Return 1
        End If
        ' v1.1 S3b: every exit trigger is ADDRESSED at a slot, so with no armed slots there is
        ' nothing to address. Say so rather than dropping nothing and reporting success - the
        ' one shape that reaches here is a v9 schedule-only block, which by design holds at
        ' full strength through its window and has no early exit at all.
        If armedIds.Count = 0 Then
            Console.Error.WriteLine("No block slot to address. A scheduled block holds until its window closes and cannot be ended early.")
            Return 1
        End If

        If HasFlag(args, "--code") Then
            Dim code As String = GetOption(args, "--code")
            If code = "" Then
                Console.Error.WriteLine("Provide the code:  monkmode unblock --code <CODE>")
                Return 1
            End If
            ' BROADCAST, deliberately. The partner is handed a code, not a block number, so
            ' requiring --id here would make the only exit unusable. It is safe because each
            ' slot KDF-verifies the candidate against its OWN MAC-covered PartnerSalt/
            ' PartnerHash: it can match at most one - the block that minted it - and every
            ' other block stays fully enforced.
            For Each id As Integer In armedIds
                Blocker.RequestPartnerCode(id, code)
            Next
            Console.WriteLine("Code submitted. If it's correct that block lifts within ~10s; if not, every block stays fully enforced.")
            Return 0
        End If

        ' THE REFUSAL. Reached by a bare `unblock`, by `unblock --id N`, and by every retired
        ' flag (`--force`, `--cancel`) now that they are merely unknown options. Exit code 1:
        ' this is a usage refusal, and no state was touched.
        Console.Error.WriteLine("A running block ends only at its end time or with the partner code. Run:  monkmode unblock --code <CODE>")
        Console.Error.WriteLine("If the code is lost, you wait. There is no cooling-off wait, no escape hatch and no recovery - that is the point.")
        Return 1
    End Function

    ' ---------- helpers ----------

    ' Ledger 319: P33's BareUnblockIsAmbiguous is GONE with the cooling-off it guarded. It
    ' existed because a bare `unblock` started a real countdown that ENDED a block, so it
    ' could not be aimed at blocks the user had not named. A bare `unblock` now starts
    ' nothing at all - it refuses - so there is no ambiguity left to arbitrate, and the
    ' broadcast `--code` is safe by construction (each slot verifies against its own hash).

    ' C6a: the shared "run setup first" refusal for the arm paths (block/schedule). A
    ' distinct exit code (4) so a script can tell "not set up" apart from a usage error (1)
    ' or an already-active block (3).
    Private Function SetupRequired() As Integer
        Console.Error.WriteLine("MonkMode isn't set up yet. Run 'monkmode setup' once first - it takes a minute and explains the accountability code, which is the only way to end a block early.")
        Return 4
    End Function

    ' Ledger 319: Step_ and RunSdRestoreThenDelete are GONE with the teardown they sequenced.
    ' Both existed only for `unblock --force` - Step_ printed and swallowed one best-effort
    ' teardown step, and RunSdRestoreThenDelete encoded audit #9's rule that the service
    ' delete must not be attempted while the deny-DELETE ACE is still on the SD. There is no
    ' CLI teardown left to sequence: the SERVICE tears itself down at a genuine exit, and the
    ' service object's deny-DELETE ACE is now never removed by anything the CLI can run.

    ' True if a bare flag (e.g. --code) is present anywhere in args.
    Private Function HasFlag(ByVal args As String(), ByVal name As String) As Boolean
        For Each a As String In args
            If String.Equals(a, name, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    Private Function GetOption(ByVal args As String(), ByVal name As String) As String
        For i As Integer = 0 To args.Length - 1
            If String.Equals(args(i), name, StringComparison.OrdinalIgnoreCase) Then
                If i + 1 < args.Length Then Return args(i + 1)
                Return ""
            End If
            If args(i).StartsWith(name & "=", StringComparison.OrdinalIgnoreCase) Then
                Return args(i).Substring(name.Length + 1)
            End If
        Next
        Return ""
    End Function

    Private Function SplitList(ByVal value As String) As String()
        If value Is Nothing OrElse value.Trim() = "" Then Return New String() {}
        Return value.Split(New Char() {","c, ";"c}, StringSplitOptions.RemoveEmptyEntries)
    End Function

    ' Parse "1d2h30m" / "90m" / "2h" / "45" (minutes) into a TimeSpan.
    Private Function TryParseDuration(ByVal s As String, ByRef span As TimeSpan) As Boolean
        s = s.Trim().ToLowerInvariant()
        If s = "" Then Return False
        Dim days As Integer = 0, hours As Integer = 0, mins As Integer = 0
        Dim matched As Boolean = False
        Dim m As System.Text.RegularExpressions.Match =
            System.Text.RegularExpressions.Regex.Match(s, "^(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?$")
        If m.Success AndAlso (m.Groups(1).Success OrElse m.Groups(2).Success OrElse m.Groups(3).Success) Then
            If m.Groups(1).Success Then days = Integer.Parse(m.Groups(1).Value)
            If m.Groups(2).Success Then hours = Integer.Parse(m.Groups(2).Value)
            If m.Groups(3).Success Then mins = Integer.Parse(m.Groups(3).Value)
            matched = True
        ElseIf Integer.TryParse(s, mins) Then
            matched = True   ' bare number = minutes
        End If
        If Not matched Then Return False
        span = New TimeSpan(days, hours, mins, 0)
        Return span.TotalSeconds > 0
    End Function

    ' P27 (v1.1): the furthest ahead a --start may be. An unbounded PENDING slot would squat
    ' one of the 8 slots indefinitely, so an absurd start is refused at arm time. Over-refusing
    ' at arm time is never fail-open - nothing is armed, and the user just retypes the command.
    Friend Const MaxStartDelayDays As Integer = 30

    ' P26 (v1.1, pure): parse --start into an absolute start time.
    ' A token that parses as a DURATION is a DELAY from `nowRef` - the identical grammar to
    ' --for ("90m" / "2h" / "1d12h" / a bare number of minutes), with a leading "+" accepted
    ' and stripped. Anything else is tried as an ABSOLUTE datetime with the identical
    ' two-culture parse --until uses. Failing both is an error, never a guess: a misread start
    ' would arm a block at the wrong time, so the CLI refuses and says what it accepts.
    ' `nowRef` is passed in (not read here) so the whole grammar is unit-testable.
    Friend Function TryParseStart(ByVal raw As String, ByVal nowRef As DateTime, ByRef startAt As DateTime, ByRef errorMsg As String) As Boolean
        startAt = DateTime.MinValue
        errorMsg = ""
        Dim tok As String = If(raw, "").Trim()
        If tok <> "" Then
            Dim durationTok As String = If(tok.StartsWith("+"), tok.Substring(1).Trim(), tok)
            Dim span As TimeSpan
            If TryParseDuration(durationTok, span) Then
                startAt = nowRef.Add(span)
                Return True
            End If
            Dim absolute As DateTime
            If DateTime.TryParse(tok, CultureInfo.CurrentCulture, DateTimeStyles.None, absolute) _
               OrElse DateTime.TryParse(tok, CultureInfo.InvariantCulture, DateTimeStyles.None, absolute) Then
                startAt = absolute
                Return True
            End If
        End If
        errorMsg = "Could not understand --start '" & tok & "'. Try ""+90m"", ""2h"", or ""2026-08-10 07:00""."
        Return False
    End Function

    ' P27 (pure): is this --start too far ahead to arm? An unbounded PENDING slot would squat
    ' one of the 8 slots indefinitely, so the delay is capped at MaxStartDelayDays. Over-
    ' refusing at arm time is never fail-open: nothing is armed, and the user retypes it.
    Friend Function StartIsTooFarAhead(ByVal parsedStart As DateTime, ByVal armNow As DateTime) As Boolean
        Return parsedStart > armNow.AddDays(MaxStartDelayDays)
    End Function

    ' P27 (pure): the two ENFORCEMENT-WINDOW refusals, both decided on `windowStart` (the
    ' --start moment for a delayed block, now for an immediate one) rather than on the wall
    ' clock - `--for` on a delayed block measures its duration from the START, so anchoring
    ' either check on "now" would silently accept a block with no enforcement time in it.
    Friend Enum WindowRefusal
        None = 0
        EndsBeforeStart = 1
        TooShort = 2
    End Enum

    Friend Function ClassifyBlockWindow(ByVal delayed As Boolean, ByVal windowStart As DateTime, ByVal endsAt As DateTime) As WindowRefusal
        ' An end at or before a DELAYED start is a contradiction, not a too-short block - say
        ' which, so the user fixes the right flag.
        If delayed AndAlso endsAt <= windowStart Then Return WindowRefusal.EndsBeforeStart
        If endsAt <= windowStart.AddSeconds(60) Then Return WindowRefusal.TooShort
        Return WindowRefusal.None
    End Function

    ' P31: the one-time accountability code's header line. The literal "Emergency unlock code"
    ' and the code on the IMMEDIATELY FOLLOWING indented line are what
    ' tools\smoke\cv-d-smoke.ps1's ParseCode (:113-118) reads, so the shape is pinned by test
    ' here rather than left to a Console.WriteLine no test can see.
    Friend Function FormatUnlockCodeHeader(ByVal slotId As Integer) As String
        Return "Emergency unlock code for block " & slotId.ToString(CultureInfo.InvariantCulture) &
               " (give it to your accountability partner NOW - it will NOT be shown again):"
    End Function

    ' F75: the release this build belongs to. A hand-maintained constant on purpose - the
    ' assembly's own FileVersion is the inherited Cold Turkey 0.7.0.0 and is not worth
    ' re-plumbing, and a constant is the one thing a `CHANGELOG.md` bump can keep in step.
    Friend Const AppVersion As String = "1.1.0"

    ' F75, extended for the DEPLOY GAP + TWO INSTALLS (backlog, 30/08/2026): the ONE line
    ' that says which install this is and which build is in it. Pure - the caller supplies
    ' the facts, because the point of this line is to tell the truth about a MACHINE, and a
    ' function that read the machine itself could not be tested.
    '
    ' `dist\` and `C:\Program Files\MonkMode\` are two separate live installs with SEPARATE
    ' setup/config state, and the release constant alone cannot tell two builds of 1.1.0
    ' apart - which is exactly how both sat on a stale build for two days after a fix
    ' shipped. So the line carries all three facts at once: release, commit, and where this
    ' exe actually lives.
    '
    '   MonkMode 1.1.0 (850f1ef, built 30/08/2026 16:31) at C:\Program Files\MonkMode
    '
    ' Unknowns are NAMED rather than dropped - a developer build reads "dev", an unstamped
    ' one with an unreadable exe reads "built unknown" - because a line with a hole in it is
    ' still more use than no line, and this is what someone runs when things look broken.
    Friend Function FormatVersionLine(ByVal installDir As String, ByVal revision As String, ByVal builtUtc As DateTime?) As String
        Dim rev As String = If(revision, "").Trim()
        If rev = "" Then rev = "dev"
        Dim built As String = "unknown"
        ' Rendered in LOCAL time: the reader is comparing it against "when did I last build",
        ' which they remember in their own clock, not UTC.
        If builtUtc.HasValue Then built = builtUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
        Dim dir As String = If(installDir, "").Trim()
        If dir = "" Then
            dir = "(unknown)"
        Else
            dir = dir.TrimEnd(CChar("\"))
        End If
        Return "MonkMode " & AppVersion & " (" & rev & ", built " & built & ") at " & dir
    End Function

    ' The build instant this exe was STAMPED with at compile time (Directory.Build.props ->
    ' MonkMode.vbproj's StampBuildInfo target), parsed. Nothing/"" on a developer build, and
    ' on anything unparseable - the caller then falls back to the exe's own timestamp. Pure.
    Friend Function ParseStampedBuildUtc(ByVal stamp As String) As DateTime?
        Dim s As String = If(stamp, "").Trim()
        If s = "" Then Return Nothing
        Dim parsed As DateTime
        If DateTime.TryParse(s, CultureInfo.InvariantCulture,
                             DateTimeStyles.AdjustToUniversal Or DateTimeStyles.AssumeUniversal, parsed) Then
            Return DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
        End If
        Return Nothing
    End Function

    ' The live reader behind that line, shared by `version` and the top of `status`.
    '
    ' Best-effort throughout: this must never be the thing that throws. The stamped build
    ' instant is preferred over the exe's timestamp because it is the instant the code was
    ' COMPILED (a file timestamp can be rewritten by any copy that does not preserve it);
    ' the timestamp remains the fallback for a developer build, where it is the only answer
    ' there is.
    Private Function ReadBuildIdentityLine() As String
        Dim dir As String = ""
        Dim built As DateTime? = ParseStampedBuildUtc(BuildStamp.BuiltUtc)
        Try
            dir = Blocker.AppDir()
            If Not built.HasValue Then
                Dim exe As String = Path.Combine(dir, "monkmode.exe")
                If File.Exists(exe) Then built = File.GetLastWriteTimeUtc(exe)
            End If
        Catch
        End Try
        Return FormatVersionLine(dir, BuildStamp.Revision, built)
    End Function

    Private Sub PrintVersion()
        Console.WriteLine(ReadBuildIdentityLine())
    End Sub

    ' F74: the "send it to X" line printed under the one-time code, or "" when there is no
    ' usable label. Pure and Friend so the wording is pinned by test rather than living in a
    ' Console.WriteLine nothing can see (the FormatUnlockCodeHeader discipline).
    ' Whitespace-only labels are treated as absent: SetupPartnerLabel already Trim()s, but a
    ' direct caller must not be able to print a bare "send it to:" with nothing after it.
    Friend Function FormatPartnerRelayLine(ByVal partnerLabel As String) As String
        Dim label As String = If(partnerLabel, "").Trim()
        If label = "" Then Return ""
        Return "Send it to your accountability partner NOW: " & label
    End Function

    ' C6c: parse an optional --cooloff argument (shared by `setup` and `block`) into seconds,
    ' applying the same duration grammar (TryParseDuration) and the shared 365d sanity cap
    ' (Blocker.MaxCoolOffSeconds). Returns True with seconds=0 when --cooloff is ABSENT (each
    ' caller supplies its own default: the account default for `block`, none for `setup`); True
    ' with seconds>0 for a valid value; False (after printing a friendly error) for an unparseable
    ' or too-long value. The cap refuses an absurd value up front (fail-fast) - it would otherwise
    ' risk a DateTime overflow when the service computes HighWater + duration each tick (the C6b
    ' verifier's Low finding). Friend so the override(>0)/absent(0)/reject(False) contract DoBlock's
    ' inherit relies on is unit-tested.
    Friend Function TryParseCoolOffArg(ByVal args As String(), ByRef seconds As Long) As Boolean
        seconds = 0
        Dim arg As String = GetOption(args, "--cooloff")
        If arg = "" Then Return True   ' absent => 0 (not given); the caller decides the default
        Dim span As TimeSpan
        If Not TryParseDuration(arg, span) Then
            Console.Error.WriteLine("Could not understand --cooloff '" & arg & "'. Try 2h, 90m, 1d.")
            Return False
        End If
        seconds = CLng(Math.Round(span.TotalSeconds))
        If seconds > Blocker.MaxCoolOffSeconds Then
            Console.Error.WriteLine("--cooloff is too long (max ~365d). Note that since ledger 319 --cooloff is accepted but has no effect at all: there is no cooling-off exit.")
            seconds = 0
            Return False
        End If
        Return True
    End Function

    Private Function Humanize(ByVal ts As TimeSpan) As String
        If ts.TotalSeconds <= 0 Then Return "0 minutes"
        Dim parts As New List(Of String)
        If ts.Days > 0 Then parts.Add(ts.Days & "d")
        If ts.Hours > 0 Then parts.Add(ts.Hours & "h")
        If ts.Minutes > 0 Then parts.Add(ts.Minutes & "m")
        If parts.Count = 0 Then parts.Add("<1m")
        Return String.Join(" ", parts)
    End Function

    ' Ledger 319: ONE sentence, no branches. Every armed block now has the same two exits and
    ' only those two, so the three-way branch this function used to carry (committed /
    ' cooling-off pending / self-serve) described a choice that no longer exists. Kept as a
    ' function (rather than inlined) because both `status` renderers print it - the v1.1 slot
    ' table and the v9 single-block fallback - and one literal is how they are kept identical.
    ' The parameters are gone with the branches; slotId survives only so the hint can name the
    ' block. Friend so the literal is unit-pinned. Renamed from FormatCoolOffStatusLine, and
    ' tools\smoke\cv-d-smoke.ps1's "committed block" / "cooling-off pending" matches were
    ' retired with it.
    Friend Function FormatExitStatusLine(Optional ByVal slotId As String = "") As String
        ' "?" is ArmedSlotLines'/ReadSlotViews' unreadable-id placeholder: never build a
        ' command hint the user cannot type.
        Dim target As String = If(slotId Is Nothing OrElse slotId = "" OrElse slotId = "?", "", " --id " & slotId)
        Return "Exit:  ends at its end time, or earlier with the partner code (shown once at block start): 'monkmode unblock" & target & " --code <CODE>'. There is no other way out."
    End Function

    ' ---- P32 (v1.1 S5): the `status` slot table - pure, fixed-width, pinned by literal ----
    '
    ' Column widths (chars): Id 3 right - 2sp - State 8 left - 2sp - Ends/Starts 24 left -
    ' 2sp - Sites 5 right - Apps 5 right - URLs 5 right - 2sp - Exit token. An over-long cell
    ' is never TRUNCATED (a clipped datetime or block id is worse than a ragged line): it
    ' simply pushes the columns after it right, which is what a PENDING row's
    ' "starts <stamp> (<dur>)" cell does.
    Private Const SlotColId As Integer = 3
    Private Const SlotColState As Integer = 8
    Private Const SlotColWhen As Integer = 24
    Private Const SlotColCount As Integer = 5
    Private Const SlotColGap As String = "  "

    ' The Exit sentence sits under its row, indented to the State column (P32's sample).
    Friend Const SlotExitIndent As String = "     "

    ' Timestamps in the table are rendered in a FIXED, culture-independent form: the column is
    ' fixed-width, and a user reading two rows must be able to compare them at a glance.
    Private Function FormatStamp(ByVal dt As DateTime) As String
        Return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
    End Function

    Friend Function FormatStatusHeading(ByVal blockCount As Integer) As String
        Return "MonkMode: " & blockCount.ToString(CultureInfo.InvariantCulture) & If(blockCount = 1, " block active", " blocks active")
    End Function

    Friend Function FormatSlotTableHeader() As String
        Return FormatSlotRowCells("Id", "State", "Ends / Starts", "Sites", "Apps", "URLs", "Exit")
    End Function

    ' The width contract itself, independent of any SlotView - so the layout is pinned by
    ' literal tests that never touch an ini.
    Friend Function FormatSlotRowCells(ByVal id As String, ByVal state As String, ByVal whenText As String,
                                       ByVal sites As String, ByVal apps As String, ByVal urls As String,
                                       ByVal exitToken As String) As String
        Return If(id, "").PadLeft(SlotColId) & SlotColGap &
               If(state, "").PadRight(SlotColState) & SlotColGap &
               If(whenText, "").PadRight(SlotColWhen) & SlotColGap &
               If(sites, "").PadLeft(SlotColCount) &
               If(apps, "").PadLeft(SlotColCount) &
               If(urls, "").PadLeft(SlotColCount) & SlotColGap &
               If(exitToken, "")
    End Function

    Friend Function FormatSlotRow(ByVal v As Blocker.SlotView) As String
        If v Is Nothing Then Return ""
        Dim row As String = FormatSlotRowCells(v.Id, v.State, FormatSlotWhenCell(v),
                                               v.Sites.ToString(CultureInfo.InvariantCulture),
                                               v.Apps.ToString(CultureInfo.InvariantCulture),
                                               v.Urls.ToString(CultureInfo.InvariantCulture),
                                               SlotExitToken(v))
        ' 313(a): the real remaining rides on the END of the row, after the Exit token, so the
        ' fixed-width columns above it keep lining up (a variable-length cell anywhere earlier
        ' would push the counts around on every ACTIVE row - the PENDING deviation, but for the
        ' common case). Empty for every row that has nothing to measure.
        Dim remaining As String = FormatSlotRemainingCell(v)
        If remaining = "" Then Return row
        Return row & SlotColGap & remaining
    End Function

    ' 313(a): the trailing cell - how much time an ACTIVE block ACTUALLY has left. The "Ends"
    ' column is a wall-clock moment, and since expiry is decided against the monotonic
    ' [Time] HighWater (which only advances while the service runs), every hour the machine
    ' spends off or asleep pushes the real end past that stamp. This is deadline - HighWater:
    ' the same subtraction the tray notifier already shows, and the one the service enforces.
    ' Only ACTIVE rows have it: a PENDING block has no end yet and a SCHEDULE window's cell
    ' already says what it is doing. "" when there is nothing to measure.
    Friend Function FormatSlotRemainingCell(ByVal v As Blocker.SlotView) As String
        If v Is Nothing Then Return ""
        If v.State <> Blocker.SlotStateActive Then Return ""
        If v.Ends = DateTime.MinValue Then Return ""
        Return FormatRemainingParenthetical(v.Ends, v.Mark)
    End Function

    ' The wording itself, shared by the slot table and the v9 fallback line so `status` says the
    ' remaining ONE way. mark is the MAC-gated [Time] HighWater (MinValue = unreadable or a
    ' frozen config): a display path must never invent a countdown out of values it could not
    ' read, so that degrades to a placeholder - never a wrong number and never an exception.
    ' A remaining that has already run out reads as due to lift rather than "0 minutes": the
    ' service tears the block down within a tick of that moment.
    Friend Function FormatRemainingParenthetical(ByVal ends As DateTime, ByVal mark As DateTime) As String
        If mark = DateTime.MinValue Then Return "(active time left unknown)"
        Dim remaining As TimeSpan = ends - mark
        If remaining.TotalSeconds <= 0 Then Return "(due to lift)"
        Return "(~" & Humanize(remaining) & " of active time left)"
    End Function

    ' 313(a): the ONE caveat under the table. Printed whenever something is ACTIVE, because the
    ' "Ends" stamp on those rows is the one number a user acts on and it is NOT a promise about
    ' the wall clock - a block armed for 2h that spends 8h shut down ends 8h later than it says
    ' UNTIL the service can corroborate the real time online and credit that downtime back
    ' (F77/v12; offline the old arithmetic still stands, which is why the note says both).
    Friend Function FormatMonotonicNoteLine() As String
        ' F77 (v12, deployed 30/08/2026) made the old wording only half true. The end stamp
        ' still advances on machine-ON time, but downtime is no longer LOST: on the next tick
        ' after the machine comes back, the service credits the gap against an EXTERNALLY
        ' corroborated clock (>= 2 agreeing HTTPS witnesses, MonkMode_srv\TrustedTime.vb).
        ' With no network - or fewer than two agreeing witnesses - there is no credit and the
        ' pre-F77 behaviour stands, so both halves have to be said.
        Return "  Note: the end time counts machine-ON time; time spent off or asleep is credited back once the service can confirm the real time online (otherwise it pushes the end later)."
    End Function

    ' FX5 leftover (19/08/2026): what a FAILED service install/start says, as lines, pinned by
    ' test rather than left inside a Catch nothing can see.
    '
    ' The block IS armed by this point (ArmSlot has committed and stamped the slot), and a
    ' failed install lifts NOTHING - the service is registered AUTO_START, so it comes up at
    ' the next boot. What differs is whether anything is blocking RIGHT NOW, and that is
    ' exactly the hosts write: with it the sites are already dead in the hosts file and only
    ' app-kill/self-repair/the countdown are paused; without it BOTH halves failed and nothing
    ' is blocking until the service starts and runs its own hosts self-heal. The old single
    ' literal promised the first case in both, which is the one thing this must not do.
    Friend Function FormatServiceInstallFailureLines(ByVal exMessage As String, ByVal hostsWritten As Boolean) As List(Of String)
        Dim lines As New List(Of String)
        lines.Add("Warning: the block IS armed, but the MonkMode service could not be installed or started (" & If(exMessage, "") & ").")
        If hostsWritten Then
            lines.Add("App-kill, self-repair and the countdown are paused until it starts; the blocked sites stay in your hosts file meanwhile.")
        Else
            lines.Add("The hosts write above ALSO failed, so nothing is being blocked at the moment: app-kill, self-repair and the countdown are paused, and the sites only go down when the service starts and repairs the hosts file itself (it is registered to start at the next boot).")
        End If
        Return lines
    End Function

    ' The frozen-config NOTE, one literal for both `status` paths. B7: never render a reassuring
    ' exit story over a config that failed its integrity check - the countdowns and exit lines
    ' above it are suppressed (MAC-gated), and this says plainly why none of it can be acted on.
    Friend Function ConfigFrozenNoteLine() As String
        Return "  NOTE: the stored configuration failed its integrity check, so MonkMode is FROZEN: nothing lifts it - not the end time, and not the partner code either - and the exit lines above cannot be acted on at all."
    End Function

    ' The stopped-service NOTE, one literal for both `status` paths (the slot table and the v9
    ' fallback): the table/line is read off the CONFIG, which stays true whether or not the
    ' service is up, so say which half is paused rather than implying either "all fine" or
    ' "nothing is blocked". The hosts block itself survives a stopped service; what stops is
    ' app-kill, self-repair and the countdown to the next exit. The wording is echoed by
    ' PrintTroubleshooting, so it is Friend + pinned rather than typed twice.
    Friend Function ServicePausedNoteLine() As String
        Return "  NOTE: the MonkMode service isn't running at the moment, so app-kill and self-repair are paused (the blocked sites stay in your hosts file). It starts itself again automatically."
    End Function

    ' Whether that note is owed: only an ACTIVE row has an end stamp it is about (a PENDING
    ' block has not started and a SCHEDULE window opens and closes on the wall clock). Pure +
    ' null-safe, so the condition is pinned without an ini.
    Friend Function AnyActiveSlot(ByVal views As List(Of Blocker.SlotView)) As Boolean
        If views Is Nothing Then Return False
        For Each v As Blocker.SlotView In views
            If v IsNot Nothing AndAlso v.State = Blocker.SlotStateActive Then Return True
        Next
        Return False
    End Function

    ' The "Ends / Starts" cell. ACTIVE shows its end; PENDING shows when it STARTS plus the
    ' planned length (its end does not exist yet - the service computes it at activation,
    ' P29); SCHEDULE shows the live window state.
    Friend Function FormatSlotWhenCell(ByVal v As Blocker.SlotView) As String
        If v Is Nothing Then Return "?"
        If v.State = Blocker.SlotStateSchedule Then
            If v.WindowOpen AndAlso v.WindowUntil > DateTime.MinValue Then
                Return "window OPEN until " & v.WindowUntil.ToString("HH:mm", CultureInfo.InvariantCulture)
            End If
            Return "no window open now"
        End If
        If v.State = Blocker.SlotStatePending Then
            Dim planned As String = If(v.DurationSeconds > 0, " (" & Humanize(TimeSpan.FromSeconds(v.DurationSeconds)) & ")", "")
            Return "starts " & If(v.StartAt > DateTime.MinValue, FormatStamp(v.StartAt), "?") & planned
        End If
        If v.Ends > DateTime.MinValue Then Return FormatStamp(v.Ends)
        Return "?"
    End Function

    ' The one-word Exit column. Ledger 319: a manual block - ACTIVE or PENDING - has exactly
    ' one early exit, so both read "code". The old "committed" / "cooling-off" / "code+wait"
    ' trichotomy went with the cooling-off, and PENDING's "cancel" went with `--cancel`.
    Friend Function SlotExitToken(ByVal v As Blocker.SlotView) As String
        If v Is Nothing Then Return ""
        If v.State = Blocker.SlotStateSchedule Then Return "window"
        Return "code"
    End Function

    ' The full-sentence Exit line printed under each row.
    Friend Function FormatSlotExitLine(ByVal v As Blocker.SlotView) As String
        If v Is Nothing Then Return ""
        If v.State = Blocker.SlotStateSchedule Then Return "Exit:  an open window can't be ended early; it closes on its own."
        ' Ledger 319: a PENDING block used to advertise `--cancel` as a free way out "until it
        ' starts". That was never true - `--cancel` only ever cleared a pending cooling-off
        ' deadline, and a PENDING slot has none, so the cancel did nothing and the block
        ' started anyway (docs\USER-GUIDE.md recorded the lie). The flag is gone; a pending
        ' block gets the same honest sentence as a running one, minus the tense.
        If v.State = Blocker.SlotStatePending Then Return "Exit:  not started yet, and it cannot be cancelled. Once it starts it ends at its end time, or earlier with the partner code."
        Return FormatExitStatusLine(v.Id)
    End Function

    ' D5 (friendly validation, pure): the "--flags" in args NOT in the known set (case-insensitive) -
    ' i.e. likely typos ("--site" for "--sites"). Only "--"-prefixed tokens are considered (a value
    ' like a domain never is); a "--flag=value" form is matched on its "--flag" head, so the reported
    ' token is the bare flag. Used to WARN + continue (never to fail: an over-strict reject could
    ' refuse a valid command). Null-safe; Friend so it is unit-tested.
    Friend Function UnknownOptions(ByVal args As String(), ByVal known As String()) As List(Of String)
        Dim result As New List(Of String)
        If args Is Nothing Then Return result
        For Each a As String In args
            If a Is Nothing OrElse Not a.StartsWith("--", StringComparison.Ordinal) Then Continue For
            Dim head As String = a
            Dim eq As Integer = head.IndexOf("="c)
            If eq >= 0 Then head = head.Substring(0, eq)
            Dim isKnown As Boolean = False
            If known IsNot Nothing Then
                For Each k As String In known
                    If String.Equals(head, k, StringComparison.OrdinalIgnoreCase) Then
                        isKnown = True
                        Exit For
                    End If
                Next
            End If
            If Not isKnown Then result.Add(head)
        Next
        Return result
    End Function

    ' D5: the flags `block` accepts (for the UnknownOptions typo warning). One list, so a new block
    ' flag is added in exactly one place alongside its DoBlock handling. Ledger 319 keeps
    ' `--commit` and `--cooloff` in this list although both are now inert: they are ACCEPTED
    ' (every block is committed and no block has a cooling-off), and an accepted flag must not
    ' also be reported as unrecognised.
    Friend Function BlockOptionNames() As String()
        Return New String() {"--sites", "--preset", "--apps", "--app-preset", "--for", "--until", "--file", "--commit", "--cooloff", "--all-session-kill", "--urls", "--start"}
    End Function

    ' Ledger 319: the flags `unblock` accepts - the whole exit surface, in one line. `--force`
    ' and `--cancel` are deliberately ABSENT rather than accepted-and-ignored: they used to DO
    ' something, so a silent no-op would be the dangerous shape (a user believing the block is
    ' coming down). Landing in UnknownOptions makes the CLI say the flag does not exist.
    Friend Function UnblockOptionNames() As String()
        Return New String() {"--id", "--code"}
    End Function

    ' D5 follow-up (pure): the BOOLEAN "--flag=value" tokens in args - an on/off flag (--commit,
    ' --all-session-kill) written with an "=value" (e.g. "--commit=yes"), which HasFlag's bare-flag
    ' match silently IGNORES, leaving the user believing they set it. Returned so the caller WARNS
    ' (never fails: the block proceeds with the flag OFF, HasFlag's default). Matches the "--flag"
    ' head against the known boolean set, case-insensitive. Null-safe; Friend so it is unit-tested.
    Friend Function BooleanFlagsWithValue(ByVal args As String()) As List(Of String)
        Dim result As New List(Of String)
        If args Is Nothing Then Return result
        Dim boolNames As String() = {"--commit", "--all-session-kill"}
        For Each a As String In args
            If a Is Nothing Then Continue For
            Dim eq As Integer = a.IndexOf("="c)
            If eq < 0 Then Continue For
            Dim head As String = a.Substring(0, eq)
            For Each k As String In boolNames
                If String.Equals(head, k, StringComparison.OrdinalIgnoreCase) Then
                    result.Add(head)
                    Exit For
                End If
            Next
        Next
        Return result
    End Function

    Private Sub PrintUsage()
        Console.WriteLine("MonkMode - tamper-resistant self-control blocker")
        Console.WriteLine("")
        Console.WriteLine("Usage:")
        Console.WriteLine("  monkmode setup [--partner ""Alex (alex@example.com)""] [--default-sites a.com,b.com] [--default-preset social] [--default-apps chrome.exe] [--default-app-preset games]   (first-run onboarding; required before the first block)")
        Console.WriteLine("  monkmode block [--sites a.com,b.com] [--preset social,video] [--apps chrome.exe,foo.exe] [--app-preset games,chat] (--for 2h30m | --until ""2026-06-11 18:00"") [--file list.txt] [--all-session-kill] [--urls ""youtube.com/shorts""] [--start +90m]")
        Console.WriteLine("  monkmode status  (one row per armed block - time left, what it covers, and how to exit each one)")
        Console.WriteLine("  monkmode stats   (read-only summary of your block history: counts, total focus time, longest block)")
        Console.WriteLine("  monkmode add --sites c.com [--id N]   (adds sites to ONE block; --id is required when more than one is running)")
        Console.WriteLine("  monkmode schedule --sites a.com,b.com [--apps chrome.exe] --windows ""Mon-Fri 09:00-17:00; Sat,Sun 10:00-14:00""")
        Console.WriteLine("  monkmode schedule --clear   (stop future windows; an open window still runs to its end)")
        Console.WriteLine("  monkmode schedule --show    (print the armed schedule; read-only)")
        Console.WriteLine("  monkmode schedule --validate --sites a.com --windows ""Mon-Fri 09:00-17:00""  (check a schedule without arming it)")
        Console.WriteLine("  monkmode unblock --code <CODE>  (the ONLY early exit: submit the partner accountability code; the service verifies it and lifts within ~10s)")
        Console.WriteLine("  monkmode version  (which release and which build this machine is running)")
        Console.WriteLine("  monkmode help")
        Console.WriteLine("")
        Console.WriteLine("Notes:")
        ' F75: the timer was the ONE exit the help never named, and since ledger 319 it is one
        ' of only two - so it still belongs first. It is the exit that needs no action.
        Console.WriteLine("  - A block ENDS BY ITSELF when its timer runs out. You do not have to do anything; the sites come back within about 10 seconds of the end time.")
        Console.WriteLine("  - A running block has exactly TWO ends: its end time, or the accountability code shown once at block start. There is no self-serve wait, no escape hatch and no override - if you lose the code, you wait.")
        Console.WriteLine("  - Run 'monkmode setup' once before your first block; it explains the accountability code and is required to arm.")
        Console.WriteLine("  - Run as Administrator (needed to edit the hosts file and install the service).")
        Console.WriteLine("  - Once a block starts it cannot be shortened, paused or cancelled.")
        Console.WriteLine("  - --all-session-kill kills blocked apps in EVERY logged-in Windows session, not just the one you ran 'block' in (useful if you fast-user-switch to a second account to dodge the kill). No effect unless you block apps.")
        Console.WriteLine("  - schedule = recurring wall-clock windows (--windows uses days Mon-Sun + 24-hour HH:MM; an end BEFORE the start means overnight (e.g. ""Mon-Fri 22:30-04:00"" covers Tue-Sat 00:00-04:00)). An open window holds at manual strength until it closes; a schedule and a manual block can't both be armed at once.")
        Console.WriteLine("  - --for accepts forms like 45 (minutes), 90m, 2h, 1d12h.")
        Console.WriteLine("  - You can run up to " & MonkMode.ConfigIntegrity.MaxSlots & " blocks at once: 'monkmode block' starts a NEW one beside the others, and 'monkmode status' lists them with their ids. Use --id <N> to add to, or exit, a particular one.")
        Console.WriteLine("  - --start delays a block: '--start +90m' / '--start 2h' / '--start ""2026-08-10 07:00""'. --for then measures from the START (so '--start +90m --for 2h' blocks for 2h, beginning in 90 minutes), and it can be at most " & MaxStartDelayDays & " days ahead. A delayed block CANNOT be cancelled while it waits - its sites are already blocked and it starts on schedule.")
        Console.WriteLine("  - --urls attaches URL patterns to a block (e.g. --urls ""youtube.com/shorts,reddit.com/r/all""), for pages rather than whole sites.")
        Console.WriteLine("    A pattern is PLAIN TEXT that must appear in the web address - there are NO wildcards. A '*' is matched literally, so a pattern containing one (""*/shorts*"") matches nothing at all; write ""youtube.com/shorts"" instead. A pattern ending in '/' means only that site's front page.")
        Console.WriteLine("  - --preset blocks a whole category of well-known sites at once (comma-separate several): " & String.Join(", ", Blocker.KnownPresetNames()) & ". Combine it with --sites to add your own.")
        Console.WriteLine("  - --app-preset kills a whole category of well-known apps at once (comma-separate several): " & String.Join(", ", Blocker.KnownAppPresetNames()) & ". Combine it with --apps to add your own.")
        Console.WriteLine("  - 'monkmode setup --default-sites a.com,b.com [--default-preset social]' sets an ACCOUNT DEFAULT blocklist that 'monkmode block' inherits when you give it no --sites/--preset/--file; naming any of those overrides the default. Each 'setup' run rewrites these defaults, so pass them again to keep them.")
        Console.WriteLine("  - 'monkmode setup --default-apps chrome.exe,foo.exe [--default-app-preset games]' sets an ACCOUNT DEFAULT app-kill list that 'monkmode block' inherits when you give it no --apps/--app-preset; naming either overrides the default. Each 'setup' run rewrites these defaults, so pass them again to keep them.")
        PrintTroubleshooting()
    End Sub

    ' F75: the section that was missing entirely. Every recovery path used to live only in
    ' docs\RUNBOOK.md - a developer file, in a source repo, which a user running the installed
    ' binary has no reason to have on disk at all. If this exe is the ONLY thing someone has,
    ' it has to be able to talk them out of a corner, so the four things that actually look
    ' like breakage (and are not) are named here, and so is the exit that always works.
    Private Sub PrintTroubleshooting()
        Console.WriteLine("")
        Console.WriteLine("If something looks wrong:")
        Console.WriteLine("  - NOTHING PRINTED and it returned instantly? You were not in an Administrator prompt.")
        Console.WriteLine("    MonkMode needs elevation, so Windows re-ran it in a new window that closed immediately -")
        Console.WriteLine("    your output went there. Open an elevated prompt and run it again. Nothing is broken.")
        Console.WriteLine("  - 'no active block (service installed but idle)' is NORMAL between blocks. The MonkMode")
        Console.WriteLine("    service stays registered after a block ends; that is not a leftover block and blocks nothing.")
        Console.WriteLine("  - 'the MonkMode service isn't running at the moment' is also normal: the blocked sites stay")
        Console.WriteLine("    in your hosts file regardless, and the service restarts itself.")
        Console.WriteLine("  - 'the stored configuration failed its integrity check' means MonkMode is FROZEN: it keeps")
        Console.WriteLine("    blocking and will NOT lift by itself. That is deliberate (it never fails open). See below.")
        Console.WriteLine("")
        Console.WriteLine("  THE WAY OUT, AND THERE IS ONLY ONE:  monkmode unblock --code <CODE>")
        Console.WriteLine("    The accountability code is shown ONCE, at block start. Relay it to your partner then.")
        Console.WriteLine("    A block you have no code for runs to its end time and not a second less. There is no")
        Console.WriteLine("    escape hatch, no override, no admin bypass and no recovery - the old 'unblock --force'")
        Console.WriteLine("    and the self-serve cooling-off wait were both REMOVED on purpose. Do not go looking.")
        Console.WriteLine("    One honest exception, stated so it is not a surprise: a FROZEN config (above) cannot be")
        Console.WriteLine("    lifted by the code either, because the code is checked against a config that failed its")
        Console.WriteLine("    integrity check - so a frozen block holds indefinitely, past its end time. Freezing needs")
        Console.WriteLine("    someone to have edited or corrupted the stored config; do not do that.")
        Console.WriteLine("")
        Console.WriteLine("  Everything MonkMode stores lives beside this program (run 'monkmode version' for the path),")
        Console.WriteLine("  plus block counters in %ProgramData%\MonkMode. It needs no internet, no account and no licence,")
        Console.WriteLine("  it sends nothing anywhere, and nothing expires. Full guide: docs\USER-GUIDE.md in the source")
        Console.WriteLine("  repo (section 7 is emergency recovery) - but you should not need it to get out of anything.")
    End Sub

End Module
