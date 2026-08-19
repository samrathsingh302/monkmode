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
'      monkmode setup  [--partner "Alex (alex@example.com)"] [--cooloff 2h] [--default-sites a.com,b.com] [--default-preset social] [--default-apps a.exe,b.exe] [--default-app-preset games]  (required first-run onboarding)
'      monkmode block  [--sites a.com,b.com] [--preset social,video] [--apps chrome.exe,foo.exe] [--app-preset games,chat]
'                      (--for 2h30m | --until "2026-06-11 18:00") [--file list.txt] [--commit] [--cooloff 2h]
'                      [--urls "*/watch*"] [--start +90m]
'      monkmode status                     (a row per armed block, with each one's exit)
'      monkmode stats                      (read-only summary of your block history)
'      monkmode add    --sites c.com[,d.com] [--id N]
'      monkmode unblock [--id N]           (request cooling-off — lifts after ~1h active time)
'      monkmode unblock --cancel           (cancel a pending cooling-off; stay blocked)
'      monkmode unblock --code <CODE>      (submit the partner code — service verifies + lifts)
'      monkmode unblock --force            (escape hatch — tears down an active block)
'      monkmode help
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
        ' C6c: optional --cooloff sets the ACCOUNT-DEFAULT cooling-off duration that every later
        ' `block` inherits when it gives no --cooloff of its own (an explicit block --cooloff still
        ' overrides). Same grammar + 365d cap as block --cooloff; parsed BEFORE the write so a bad
        ' value fails fast with no partial state. 0 = not given (no account default stored).
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
        Console.WriteLine("  Cooling-off - without the code you can still leave, but not instantly: 'monkmode")
        Console.WriteLine("  unblock' starts a mandatory ~1 hour wait of active machine time; the block stays")
        Console.WriteLine("  fully enforced until it elapses, then lifts itself ('monkmode unblock --cancel' aborts it).")
        ' C6c: confirm the account-default cooling-off when set (blocks without their own --cooloff inherit it).
        If coolOffSeconds > 0 Then Console.WriteLine("  Your account-default cooling-off wait is " & Humanize(TimeSpan.FromSeconds(coolOffSeconds)) & ", inherited by any block without its own --cooloff (the ~1h minimum still applies).")
        ' D1b: confirm the account-default blocklist when set (a block naming no --sites/--preset/--file inherits it).
        If defaultSites <> "" Then Console.WriteLine("  Your account-default blocklist is: " & defaultSites.Replace(",", ", ") & " - inherited by any block you start without --sites/--preset/--file.")
        ' D2b: confirm the account-default app list when set (a block naming no --apps/--app-preset inherits it).
        If defaultApps <> "" Then Console.WriteLine("  Your account-default app list is: " & defaultApps.Replace(",", ", ") & " - inherited by any block you start without --apps/--app-preset.")
        Console.WriteLine("")
        Console.WriteLine("  Committed blocks - 'monkmode block --commit' disables even the cooling-off exit, so")
        Console.WriteLine("  the accountability code is the ONLY early way out. Use it when you mean it.")
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
        ' permanently with no cooling-off and no partner-code exit. Refuse the whole arm, name the
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

        ' C6b/C6c: optional --cooloff sets THIS block's cooling-off DURATION - how long the
        ' self-serve `unblock` exit takes to lift. Parsed up front (shared TryParseCoolOffArg:
        ' grammar + 365d cap) so a bad value fails BEFORE any hosts/service side effects. When
        ' --cooloff is ABSENT (seconds = 0), inherit the account default set at `setup --cooloff`
        ' (C6c); still 0 there (no default / setup incomplete / tampered) => the service uses its
        ' compile-time floor (~1h). SetupIsComplete was already required above, so the setup file
        ' is present here; SetupDefaultCoolOffSeconds fail-closes to 0 on any read/tamper anyway.
        ' A configured value below the floor is clamped up by the service, so --cooloff can only
        ' ever EXTEND the wait, never shorten it.
        Dim coolOffSeconds As Long
        If Not TryParseCoolOffArg(args, coolOffSeconds) Then Return 1
        If coolOffSeconds = 0 Then coolOffSeconds = Blocker.SetupDefaultCoolOffSeconds()

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

        ' C4: `--commit` arms a COMMITTED block (self-serve cooling-off disabled = the
        ' partner code + expiry are the only exits). The flag is MAC-covered from birth.
        Dim committed As Boolean = HasFlag(args, "--commit")
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
            Console.Error.WriteLine("Wait for the running block(s) to end, or use 'monkmode unblock --force' once they have.")
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
        If Not Blocker.TryWriteArmHostsBlock(domains, arm.FreshRewrite, hostsWarning) Then
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
            Console.Error.WriteLine("Warning: the block IS armed, but the MonkMode service could not be installed or started (" & ex.Message & ").")
            Console.Error.WriteLine("App-kill, self-repair and the countdown are paused until it starts; the blocked sites stay in your hosts file meanwhile.")
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
        ' C6b: confirm a custom cooling-off duration when set (the ~1h floor still applies if shorter).
        If coolOffSeconds > 0 Then Console.WriteLine("  Cooling-off: " & Humanize(TimeSpan.FromSeconds(coolOffSeconds)) & " (the self-serve 'unblock' wait; a ~1h minimum still applies if this is shorter).")
        Console.WriteLine("Close and reopen your browser to see the block. It cannot be removed until the timer ends.")

        ' C4: committed-block notice - a committed block surrenders the self-serve
        ' cooling-off wait, so the accountability code below is the ONLY early exit.
        If committed Then
            Console.WriteLine("")
            Console.WriteLine("This block is COMMITTED: self-serve cooling-off is DISABLED. The ONLY early exit is the accountability code below (or waiting for the timer to end).")
        End If

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
        Console.WriteLine("To end block " & arm.Id & " early, they run:  monkmode unblock --code <CODE>")
        Return 0
    End Function

    Private Function DoStatus() As Integer
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
            ' v1.1 S7b (P48): what the blocks have actually stopped today, from the
            ' display-only sidecars. "" (printed as nothing) on a quiet day or when the
            ' sidecar is absent/corrupt - `status` must never grow a row of zeros, and it
            ' must never fail because a counter file did.
            Dim todayLine As String = BlockedTodayStatusLine()
            If todayLine <> "" Then Console.WriteLine(todayLine)
            ' B7: never render a reassuring exit story over a config that failed its integrity
            ' check. ReadSlotViews already suppresses the committed / cooling-off fields in that
            ' case (so the Exit column reads code+wait, its most conservative value); say plainly
            ' why none of it can be acted on.
            If Not macValid Then
                Console.WriteLine("")
                Console.WriteLine("  NOTE: the stored configuration failed its integrity check, so MonkMode is FROZEN: nothing lifts, and the exit lines above cannot be acted on until the blocks end and you re-arm.")
            End If
            ' The table is read off the CONFIG, which stays true whether or not the service is
            ' up - so say which half is paused rather than implying either "all fine" or
            ' "nothing is blocked". The hosts block itself survives a stopped service; what
            ' stops is app-kill, self-repair and the countdown to the next exit.
            If Not Blocker.ServiceIsRunning() Then
                Console.WriteLine("")
                Console.WriteLine("  NOTE: the MonkMode service isn't running at the moment, so app-kill and self-repair are paused (the blocked sites stay in your hosts file). It starts itself again automatically.")
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
        Dim ends As DateTime = Blocker.ActiveBlockEnd()
        If Blocker.ServiceIsRunning() AndAlso ends > DateTime.Now Then
            Console.WriteLine("MonkMode: ACTIVE")
            Console.WriteLine("  Ends:  " & ends.ToString() & " (" & Humanize(ends.Subtract(DateTime.Now)) & " left)")
            Dim sites As String = Blocker.BlockedSites()
            Dim apps As String = Blocker.BlockedApps()
            If sites <> "" Then Console.WriteLine("  Sites: " & sites.Replace(";", " "))
            If apps <> "" Then Console.WriteLine("  Apps:  " & apps.Replace(";", " "))
            ' D5: the exit story - committed (code-only), cooling-off pending (monotonic remaining), or
            ' the self-serve wait + code. A read-only, MAC-gated, best-effort view (never mutates state).
            Console.WriteLine("  " & FormatCoolOffStatusLine(Blocker.BlockIsCommitted(), Blocker.CoolOffPendingRemaining()))
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
                Console.Error.WriteLine("Wait for the running block(s) to end, or use 'monkmode unblock --force' once they have.")
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

    ' C2b (R1): `unblock` is now a REQUEST, not a teardown. Bare `unblock` drops
    ' the presence-only cooling-off request trigger; the SERVICE (the sole timing
    ' authority) starts a floor-long cooling-off on its next tick - the block
    ' stays fully enforced while a MAC-covered monotonic deadline counts down -
    ' and then lifts via its own stopMe(). `--cancel` drops the cancel trigger
    ' (clear the pending cooling-off; stay blocked). Nothing here can shorten the
    ' wait: the trigger files carry no timing (R2) and the deadline is
    ' service-computed and floor-clamped.
    '
    ' C3b (R1): `--code <CODE>` is the FAST partner-relayed exit. It drops the ONE
    ' content-bearing trigger with the candidate; the SERVICE alone KDF-verifies it
    ' against the MAC-covered hash and, on a match, lifts via the same stopMe(). The
    ' CLI has ZERO lift authority - it only submits (an attacker running the CLI
    ' cannot forge a preimage, swap the MAC-covered verifier, or skip the
    ' service-side lift). A wrong/blank/tampered code leaves the block standing.
    '
    ' `--force` remains the UNCHANGED B6 escape hatch (D2: retained as
    ' brick-insurance until partner-code exists at C3/C4/H2 to take over that
    ' role - you cannot remove the only guaranteed exit before its replacement
    ' exists, or a DPAPI-dead freeze traps the machine). Once B1/B2/B3/B4/B7 are
    ' all fail-closed, a tampered or corrupted block never auto-lifts, and the
    ' service resists `sc delete`; this verb is the deliberate, documented way
    ' out (see vault\dev\monk-mode\specs\ARCHITECTURE.md B6 / the honest
    ' ceiling). It is UNCONDITIONAL by design but gated behind an explicit
    ' --force, so it can never be a casual one-word bypass. Every step is
    ' best-effort and ordered so nothing resurrects the service mid-teardown;
    ' failures are reported, not fatal. Mirrors the live-verified cleanup.ps1
    ' emergency teardown.
    Private Function DoUnblock(ByVal args As String()) As Integer
        Dim forced As Boolean = HasFlag(args, "--force")
        If Not forced Then
            ' v1.1 S3b: the exit surface is SLOT-ADDRESSED (P40), so this needs the ids.
            ' `--id <N>` (P35's flag name, shared with `add` and `schedule`) targets ONE block.
            ' Read BEFORE the liveness check so "nothing armed" is decided off the config, not
            ' off whether the service happens to be up.
            Dim armedIds As List(Of Integer) = Blocker.ArmedSlotIds()
            Dim targetArg As String = GetOption(args, "--id")
            Dim explicitId As Boolean = targetArg <> ""
            If explicitId Then
                Dim wanted As Integer
                If Not Integer.TryParse(targetArg.Trim(), wanted) OrElse Not armedIds.Contains(wanted) Then
                    Console.Error.WriteLine("No armed block #" & targetArg.Trim() & ". Run 'monkmode status' to see the armed blocks.")
                    Return 1
                End If
                armedIds = New List(Of Integer) From {wanted}
            End If
            ' The cooling-off surface (bare request / --cancel). Only meaningful
            ' against an active block - the service only polls while it runs.
            If armedIds.Count = 0 AndAlso Not Blocker.BlockIsActive() Then
                Console.Error.WriteLine("No active block to unblock.")
                Return 1
            End If
            ' v1.1 S3b: every exit trigger is now ADDRESSED at a slot, so with no armed slots
            ' there is nothing to address. Say so rather than dropping nothing and reporting
            ' success - the one shape that reaches here is a v9 schedule-only block, which by
            ' design holds at full strength through its window and has no early exit at all.
            If armedIds.Count = 0 Then
                Console.Error.WriteLine("No block slot to address. A scheduled block holds until its window closes and cannot be ended early.")
                Return 1
            End If
            ' C3b: partner-code attempt. Drop the ONE content-bearing trigger with
            ' the candidate; the SERVICE alone verifies it (KDF + constant-time
            ' compare against the MAC-covered hash) on its next tick and, on a match,
            ' lifts via the SAME stopMe() natural expiry and cooling-off use. The CLI
            ' has ZERO lift authority here - it only submits a candidate. Deliberately
            ' does NOT reveal correctness synchronously (the service adjudicates); a
            ' wrong/blank/tampered code just leaves the block standing.
            If HasFlag(args, "--code") Then
                Dim code As String = GetOption(args, "--code")
                If code = "" Then
                    Console.Error.WriteLine("Provide the code:  monkmode unblock --code <CODE>")
                    Return 1
                End If
                ' BROADCAST, deliberately - and NOT what P33 governs. The partner is handed a
                ' code, not a block number, so requiring --id here would make the documented
                ' exit unusable. It is safe because each slot KDF-verifies the candidate
                ' against its OWN MAC-covered PartnerSalt/PartnerHash: it can match at most
                ' one - the block that minted it - and every other block stays fully enforced.
                ' Contrast the bare cooling-off request below, where a broadcast would start a
                ' real countdown on blocks the user never named.
                For Each id As Integer In armedIds
                    Blocker.RequestPartnerCode(id, code)
                Next
                Console.WriteLine("Code submitted. If it's correct that block lifts within ~10s; if not, every block stays fully enforced.")
                Return 0
            End If
            If HasFlag(args, "--cancel") Then
                ' Also a broadcast, and also not P33's business: a cancel puts blocks BACK
                ' into full enforcement, so addressing more of them than the user meant is
                ' the over-blocking direction. Refusing it would be the fail-open one.
                For Each id As Integer In armedIds
                    Blocker.CancelCoolOff(id)
                Next
                Console.WriteLine("Cooling-off cancel requested. Any pending cooling-off is cleared within ~10s; the block continues to its normal end.")
                Return 0
            End If

            ' P33 - THE ONE PATH THAT MUST REFUSE. A bare `unblock` starts a REAL countdown
            ' that ENDS a block, so it may never be aimed at a block the user did not name.
            ' The failure this prevents is concrete: with a 1h focus block and a 30d
            ' commitment armed, habit-typing `unblock` meaning the short one would start the
            ' ~1h cooling-off on BOTH, and the 30d block would lift within the hour. Refusing
            ' to begin an exit is also the fail-closed direction, so this costs nothing.
            ' With exactly one armed slot, bare `unblock` still targets it - v1.0's feel.
            If BareUnblockIsAmbiguous(explicitId, armedIds.Count) Then
                Console.Error.WriteLine("More than one block is active. Name the one you mean:  monkmode unblock --id <N>")
                For Each line As String In Blocker.ArmedSlotLines()
                    Console.Error.WriteLine(line)
                Next
                Return 1
            End If

            ' C4: a committed block has NO self-serve cooling-off - refuse the request
            ' with an actionable message instead of dropping a trigger the service would
            ' just Ignore. The partner code (verified service-side) is the intended exit.
            ' v1.1 S3b: read the ADDRESSED slot's own flag, never the machine-wide v9 latch.
            ' The latch is "yes if ANY slot is", so gating one block's exit on it locks the
            ' survivor out of an exit it is entitled to for as long as it runs.
            If Blocker.SlotIsCommitted(armedIds(0)) Then
                Console.Error.WriteLine("This block is COMMITTED: self-serve cooling-off is disabled. The only early exit is the accountability code:  monkmode unblock --code <CODE>")
                Return 1
            End If
            For Each id As Integer In armedIds
                Blocker.RequestCoolOff(id)
            Next
            Console.WriteLine("Cooling-off requested. The block stays FULLY enforced while the service counts down ~1 hour of active machine time; it then lifts itself.")
            Console.WriteLine("Changed your mind? Run:  monkmode unblock --cancel")
            Return 0
        End If

        Console.WriteLine("Forcing MonkMode down (escape hatch). This removes the active block.")

        ' 1. Stop the SCM from auto-restarting the service the moment we kill it
        '    (B1 layer 1), so the kills in step 2 actually stick.
        Step_("Disabling service recovery policy", Sub() ServiceTools.ServiceInstaller.DisableRecovery(Blocker.ServiceName))

        ' 2. Kill the watchdog pair (guardian first, then service) so neither
        '    re-asserts the deny-DELETE ACE nor re-enforces hosts, plus the
        '    notifier. Retries until both stay down (recovery is already off).
        Step_("Stopping the watchdog pair and notifier", Sub() Blocker.KillWatchdogProcesses())

        ' 3+4. With nothing alive to re-deny, remove the deny-DELETE ACE so the
        '    service object can be opened for DELETE (the CLI runs as BA), then
        '    delete the service registration itself (the `sc delete` we
        '    normally refuse during a block). Audit #9: while the deny ACE is
        '    still on the SD the SCM is GUARANTEED to refuse the delete, so a
        '    hard-failed SD restore is retried once and a still-failed restore
        '    SKIPS the delete with an actionable message, instead of burying
        '    the real cause under a misleading AccessDenied "skipped" from
        '    step 4. Steps 5+ run either way (best-effort teardown continues).
        RunSdRestoreThenDelete(
            Function(attempt As Integer) Step_(
                If(attempt = 1, "Removing the service deny-DELETE protection", "Retrying the deny-DELETE removal"),
                Sub() ServiceTools.ServiceInstaller.RestoreDefaultServiceSd(Blocker.ServiceName)),
            Function() Step_("Deleting the MonkMode service", Sub() ServiceTools.ServiceInstaller.DeleteServiceByName(Blocker.ServiceName)),
            Sub(msg) Console.WriteLine(msg))

        ' 5. Unlock hosts and strip ONLY the MonkMode marker block (user content
        '    preserved byte-for-byte — the same data-loss-safe strip the service
        '    uses at expiry).
        Step_("Restoring the hosts file", Sub() Blocker.RestoreHostsFromStrip())

        ' 6-8. Remove the B2 snapshot, the B3 SafeBoot leaf keys, and the HKCU
        '    autorun, so a future install can't self-heal the old block back.
        Step_("Removing the hosts snapshot", Sub() Blocker.DeleteSnapshot())
        ' C1b: remove the config shadow backup so a future install can't restore the
        ' old config from it (mirrors the hosts-snapshot removal + stopMe's delete).
        Step_("Removing the config backup", Sub() Blocker.DeleteBackup())
        ' C2b/C3b: remove any cooling-off + partner-code trigger files (mirrors
        ' stopMe's deletes) so a stale request can't auto-start a cooling-off, and no
        ' stale candidate lingers, on the NEXT armed block. Cleanup only - the
        ' teardown above is unchanged (D2 keeps --force as-is through C3b).
        Step_("Removing cooling-off and partner-code triggers", Sub()
                                                   Try
                                                       File.Delete(Path.Combine(Blocker.AppDir(), Blocker.CoolOffRequestFileName))
                                                   Catch
                                                   End Try
                                                   Try
                                                       File.Delete(Path.Combine(Blocker.AppDir(), Blocker.CoolOffCancelFileName))
                                                   Catch
                                                   End Try
                                                   Try
                                                       File.Delete(Path.Combine(Blocker.AppDir(), Blocker.PartnerCodeFileName))
                                                   Catch
                                                   End Try
                                                   ' v1.1 S3b: and every SLOT-ADDRESSED trigger (P40) - the
                                                   ' three unsuffixed deletes above only clear a legacy file
                                                   ' an older build left. A surviving <prefix><id> would be
                                                   ' inherited by whatever block next takes that id.
                                                   Try
                                                       For Each pattern As String In New String() {Blocker.CoolOffRequestPrefix & "*", Blocker.CoolOffCancelPrefix & "*", Blocker.PartnerCodePrefix & "*", Blocker.AddRequestPrefix & "*"}
                                                           For Each stale As String In Directory.GetFiles(Blocker.AppDir(), pattern)
                                                               Try
                                                                   File.Delete(stale)
                                                               Catch
                                                               End Try
                                                           Next
                                                       Next
                                                   Catch
                                                   End Try
                                               End Sub)
        Step_("Removing the Safe Mode registration", Sub() Blocker.RemoveSafeBootKeys())
        ' B5a: restore the user's prior browser DoH policy (or remove our lingering
        ' "off") from the snapshot, then consume it - no data loss, so a reinstall
        ' can't re-restore a stale prior.
        Step_("Restoring browser DoH policy", Sub() Blocker.RemoveDohPolicy())
        Step_("Clearing the notifier autorun", Sub() Blocker.ClearNotifierAutorun())

        Console.WriteLine("Done. MonkMode has been removed. If your browser still shows a block, flush DNS / reopen it.")
        Return 0
    End Function

    ' ---------- helpers ----------

    ' P33 (PURE): must a bare `unblock` (no --id) refuse rather than start a cooling-off?
    ' Yes as soon as more than one block is armed. A cooling-off request starts a REAL
    ' countdown that ENDS a block, so it may never be aimed at a block the user did not name:
    ' with a 1h focus block and a 30d commitment armed, habit-typing `unblock` meaning the
    ' short one would otherwise start the ~1h clock on BOTH and the 30d block would lift
    ' within the hour. With exactly one armed slot it targets that one, so a single-block
    ' machine feels exactly like v1.0. Refusing to BEGIN an exit is the fail-closed direction,
    ' so the refusal costs nothing in safety. Pure + Friend so the rule is unit-pinned rather
    ' than only reachable through the console path.
    Friend Function BareUnblockIsAmbiguous(ByVal explicitId As Boolean, ByVal armedCount As Integer) As Boolean
        Return Not explicitId AndAlso armedCount > 1
    End Function

    ' C6a: the shared "run setup first" refusal for the arm paths (block/schedule). A
    ' distinct exit code (4) so a script can tell "not set up" apart from a usage error (1)
    ' or an already-active block (3).
    Private Function SetupRequired() As Integer
        Console.Error.WriteLine("MonkMode isn't set up yet. Run 'monkmode setup' once first - it takes a minute and explains how to end a block (the accountability code + cooling-off).")
        Return 4
    End Function

    ' Run one best-effort teardown step: print what it does, swallow + report any
    ' failure so the escape hatch always continues to the next step. Returns
    ' whether the step succeeded so a dependent step can be gated on it (audit
    ' #9); callers stay free to ignore the result.
    Private Function Step_(ByVal label As String, ByVal action As Action) As Boolean
        Console.Write("  " & label & " ... ")
        Try
            action()
            Console.WriteLine("ok")
            Return True
        Catch ex As Exception
            Console.WriteLine("skipped (" & ex.Message & ")")
            Return False
        End Try
    End Function

    ' Audit #9 teardown policy: the service delete (step 4) is refused by the
    ' SCM for as long as the deny-DELETE ACE is still on the service SD, so a
    ' hard-failed SD restore (step 3) makes the delete attempt pure noise - a
    ' misleading AccessDenied "skipped". Policy: retry the restore once (covers
    ' a transient SCM hiccup), attempt the delete ONLY after a restore attempt
    ' succeeded, otherwise report an actionable skip. Friend + delegate params
    ' so the unit tests drive the policy without touching the real SCM (hard
    ' fence); production wires the delegates through Step_, so they never
    ' throw. Returns whether the delete ran and succeeded.
    Friend Function RunSdRestoreThenDelete(ByVal tryRestoreSd As Func(Of Integer, Boolean),
                                           ByVal tryDeleteService As Func(Of Boolean),
                                           ByVal reportSkip As Action(Of String)) As Boolean
        Dim sdRestored As Boolean = tryRestoreSd(1)
        If Not sdRestored Then sdRestored = tryRestoreSd(2)
        If Not sdRestored Then
            reportSkip("  Deleting the MonkMode service ... skipped (the deny-DELETE removal failed twice, so the SCM would refuse the delete; re-run 'monkmode unblock --force' to retry)")
            Return False
        End If
        Return tryDeleteService()
    End Function

    ' True if a bare flag (e.g. --force) is present anywhere in args.
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
            Console.Error.WriteLine("--cooloff is too long (max ~365d). Cooling-off is a short wait before the self-serve exit, not a second timer.")
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

    ' D5 (rich status, pure): the exit/cooling-off line for an ACTIVE manual block. A committed block
    ' shows the code-only exit; a pending cooling-off shows the monotonic remaining + the cancel;
    ' otherwise the self-serve wait + the code are offered. Always returns a non-empty line (an
    ' active block always has SOME exit story). Friend so it is unit-tested by literal - the branch
    ' selection is display logic worth pinning. coolOffRemaining is Blocker.CoolOffPendingRemaining()
    ' (Nothing = none pending); a committed block ignores it (self-serve cooling-off is disabled).
    ' v1.1 S5 (P31): reused VERBATIM per slot, gaining only "--id <N>" inside the two command
    ' hints (slotId = "" keeps the pre-v1.1 single-block wording, which is what the v9
    ' fallback branch of `status` still prints). The three literal texts are load-bearing for
    ' tools\smoke\cv-d-smoke.ps1 - "committed block" (:141) and "cooling-off pending" (:165)
    ' are matched by the live smoke, so they must survive any edit here verbatim.
    Friend Function FormatCoolOffStatusLine(ByVal committed As Boolean, ByVal coolOffRemaining As TimeSpan?, Optional ByVal slotId As String = "") As String
        ' "?" is ArmedSlotLines'/ReadSlotViews' unreadable-id placeholder: never build a
        ' command hint the user cannot type.
        Dim target As String = If(slotId Is Nothing OrElse slotId = "" OrElse slotId = "?", "", " --id " & slotId)
        If committed Then
            Return "Exit:  committed block - the accountability code (shown at block start) is the only early exit, or wait for the timer."
        End If
        If coolOffRemaining IsNot Nothing Then
            Return "Exit:  cooling-off pending - lifts in about " & Humanize(coolOffRemaining.Value) & " of active time. Run 'monkmode unblock" & target & " --cancel' to stay blocked."
        End If
        Return "Exit:  run 'monkmode unblock" & target & "' to start a cooling-off wait, or the accountability code (shown at block start) lifts it now."
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
        Return FormatSlotRowCells(v.Id, v.State, FormatSlotWhenCell(v),
                                  v.Sites.ToString(CultureInfo.InvariantCulture),
                                  v.Apps.ToString(CultureInfo.InvariantCulture),
                                  v.Urls.ToString(CultureInfo.InvariantCulture),
                                  SlotExitToken(v))
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

    ' The one-word Exit column. Committed is checked BEFORE cooling-off, matching
    ' FormatCoolOffStatusLine: a committed block has no self-serve cooling-off at all, so a
    ' deadline stored against one could only be stale.
    Friend Function SlotExitToken(ByVal v As Blocker.SlotView) As String
        If v Is Nothing Then Return ""
        If v.State = Blocker.SlotStateSchedule Then Return "window"
        If v.State = Blocker.SlotStatePending Then Return "cancel"
        If v.Committed Then Return "committed"
        If v.CoolOffRemaining IsNot Nothing Then Return "cooling-off"
        Return "code+wait"
    End Function

    ' The full-sentence Exit line printed under each row.
    Friend Function FormatSlotExitLine(ByVal v As Blocker.SlotView) As String
        If v Is Nothing Then Return ""
        If v.State = Blocker.SlotStateSchedule Then Return "Exit:  an open window can't be ended early; it closes on its own."
        If v.State = Blocker.SlotStatePending Then Return "Exit:  not started yet - 'monkmode unblock --id " & v.Id & " --cancel' cancels it freely until it starts."
        Return FormatCoolOffStatusLine(v.Committed, v.CoolOffRemaining, v.Id)
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
    ' flag is added in exactly one place alongside its DoBlock handling.
    Friend Function BlockOptionNames() As String()
        Return New String() {"--sites", "--preset", "--apps", "--app-preset", "--for", "--until", "--file", "--commit", "--cooloff", "--all-session-kill", "--urls", "--start"}
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
        Console.WriteLine("  monkmode setup [--partner ""Alex (alex@example.com)""] [--cooloff 2h] [--default-sites a.com,b.com] [--default-preset social] [--default-apps chrome.exe] [--default-app-preset games]   (first-run onboarding; required before the first block)")
        Console.WriteLine("  monkmode block [--sites a.com,b.com] [--preset social,video] [--apps chrome.exe,foo.exe] [--app-preset games,chat] (--for 2h30m | --until ""2026-06-11 18:00"") [--file list.txt] [--commit] [--cooloff 2h] [--all-session-kill] [--urls ""*/watch*""] [--start +90m]")
        Console.WriteLine("  monkmode status  (one row per armed block - time left, what it covers, and how to exit each one)")
        Console.WriteLine("  monkmode stats   (read-only summary of your block history: counts, total focus time, longest block)")
        Console.WriteLine("  monkmode add --sites c.com [--id N]   (adds sites to ONE block; --id is required when more than one is running)")
        Console.WriteLine("  monkmode schedule --sites a.com,b.com [--apps chrome.exe] --windows ""Mon-Fri 09:00-17:00; Sat,Sun 10:00-14:00""")
        Console.WriteLine("  monkmode schedule --clear   (stop future windows; an open window still runs to its end)")
        Console.WriteLine("  monkmode schedule --show    (print the armed schedule; read-only)")
        Console.WriteLine("  monkmode schedule --validate --sites a.com --windows ""Mon-Fri 09:00-17:00""  (check a schedule without arming it)")
        Console.WriteLine("  monkmode unblock [--id N]  (request cooling-off: the block lifts after ~1h of active machine time; --id is required when more than one block is running)")
        Console.WriteLine("  monkmode unblock --cancel  (cancel a pending cooling-off; stay blocked)")
        Console.WriteLine("  monkmode unblock --code <CODE>  (submit the partner accountability code; the service verifies it and lifts within ~10s)")
        Console.WriteLine("  monkmode unblock --force   (escape hatch: tears down an active block + removes the service)")
        Console.WriteLine("  monkmode help")
        Console.WriteLine("")
        Console.WriteLine("Notes:")
        Console.WriteLine("  - Run 'monkmode setup' once before your first block; it explains the accountability code + cooling-off and is required to arm.")
        Console.WriteLine("  - Run as Administrator (needed to edit the hosts file and install the service).")
        Console.WriteLine("  - Once a block starts it cannot be shortened; 'unblock' starts a mandatory cooling-off wait.")
        Console.WriteLine("  - --commit arms a COMMITTED block: self-serve cooling-off is disabled, so the only early exit is the accountability code shown at block start (or the timer).")
        Console.WriteLine("  - --all-session-kill kills blocked apps in EVERY logged-in Windows session, not just the one you ran 'block' in (useful if you fast-user-switch to a second account to dodge the kill). No effect unless you block apps.")
        Console.WriteLine("  - schedule = recurring wall-clock windows (--windows uses days Mon-Sun + 24-hour HH:MM; an end BEFORE the start means overnight (e.g. ""Mon-Fri 22:30-04:00"" covers Tue-Sat 00:00-04:00)). An open window holds at manual strength until it closes; a schedule and a manual block can't both be armed at once.")
        Console.WriteLine("  - --for accepts forms like 45 (minutes), 90m, 2h, 1d12h.")
        Console.WriteLine("  - You can run up to " & MonkMode.ConfigIntegrity.MaxSlots & " blocks at once: 'monkmode block' starts a NEW one beside the others, and 'monkmode status' lists them with their ids. Use --id <N> to add to, or exit, a particular one.")
        Console.WriteLine("  - --start delays a block: '--start +90m' / '--start 2h' / '--start ""2026-08-10 07:00""'. --for then measures from the START (so '--start +90m --for 2h' blocks for 2h, beginning in 90 minutes), it can be at most " & MaxStartDelayDays & " days ahead, and until it starts you can cancel it freely with 'monkmode unblock --id <N> --cancel'.")
        Console.WriteLine("  - --urls attaches URL patterns to a block (e.g. --urls ""*/watch*,*reddit.com/r/*""), for pages rather than whole sites.")
        Console.WriteLine("  - --preset blocks a whole category of well-known sites at once (comma-separate several): " & String.Join(", ", Blocker.KnownPresetNames()) & ". Combine it with --sites to add your own.")
        Console.WriteLine("  - --app-preset kills a whole category of well-known apps at once (comma-separate several): " & String.Join(", ", Blocker.KnownAppPresetNames()) & ". Combine it with --apps to add your own.")
        Console.WriteLine("  - --cooloff sets THIS block's cooling-off wait (how long 'unblock' takes to lift), e.g. --cooloff 2h. A ~1h minimum applies, so a shorter value still waits that; a larger value makes leaving early harder. Same forms as --for.")
        Console.WriteLine("  - 'monkmode setup --cooloff 2h' sets an ACCOUNT DEFAULT cooling-off wait that every block without its own --cooloff inherits; a block's own --cooloff always overrides it. The ~1h minimum still applies.")
        Console.WriteLine("  - 'monkmode setup --default-sites a.com,b.com [--default-preset social]' sets an ACCOUNT DEFAULT blocklist that 'monkmode block' inherits when you give it no --sites/--preset/--file; naming any of those overrides the default. Each 'setup' run rewrites these defaults, so pass them again to keep them.")
        Console.WriteLine("  - 'monkmode setup --default-apps chrome.exe,foo.exe [--default-app-preset games]' sets an ACCOUNT DEFAULT app-kill list that 'monkmode block' inherits when you give it no --apps/--app-preset; naming either overrides the default. Each 'setup' run rewrites these defaults, so pass them again to keep them.")
    End Sub

End Module
