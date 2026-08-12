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
'      monkmode status
'      monkmode stats                      (read-only summary of your block history)
'      monkmode add    --sites c.com[,d.com]
'      monkmode unblock                    (request cooling-off — lifts after ~1h active time)
'      monkmode unblock --cancel           (cancel a pending cooling-off; stay blocked)
'      monkmode unblock --code <CODE>      (submit the partner code — service verifies + lifts)
'      monkmode unblock --force            (escape hatch — tears down an active block)
'      monkmode help
'
'    A block, once started, cannot be shortened or started anew until the
'    current one expires (the service enforces this). 'add' only adds sites.
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

        ' D1b: inherit the account-default blocklist when this block names NO explicit site source
        ' (--sites/--preset/--file all produced nothing). An explicit source OVERRIDES the default
        ' (you get exactly the sites you asked for); the default only fills in when you named none -
        ' the direct analogue of the C6c cooling-off inheritance below. SetupIsComplete was already
        ' required above, and SetupDefaultSites fail-closes to empty on any tamper, so this can only
        ' ADD sites to THIS new arm (never lift/shorten a live block). A block naming only --apps (no
        ' site source) likewise picks up the standing default sites - that is the point of a default
        ' blocklist. The expanded defaults then ride WriteHostsBlock + [User] CustomSites, MAC-covered
        ' exactly like a hand-typed --sites domain.
        If domains.Count = 0 Then
            domains.AddRange(Blocker.SetupDefaultSites())
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

        ' D2b: inherit the account-default app list when this block names NO explicit app source
        ' (--apps/--app-preset both produced nothing). Symmetric with the D1b default-sites inherit
        ' above: an explicit --apps/--app-preset OVERRIDES the default; the default only fills in when
        ' you named none. SetupIsComplete was required above and SetupDefaultApps fail-closes to empty
        ' on any tamper, so this can only ADD apps to THIS new arm (never lift/shorten a live block).
        ' The inherited names ride PackApps -> [Process] List, MAC-covered exactly like a hand-typed
        ' --apps name. Site and app defaults inherit INDEPENDENTLY per dimension: `block --sites x.com`
        ' with a default app list still picks up the default apps, mirroring how the D1b default sites
        ' fill in for an --apps-only block. Over-block-safe; the armed apps are printed at Sites/Apps.
        If apps.Count = 0 Then
            apps.AddRange(Blocker.SetupDefaultApps())
        End If

        If domains.Count = 0 AndAlso apps.Count = 0 Then
            Console.Error.WriteLine("Nothing to block. Provide --sites, --preset, --apps, and/or --app-preset.")
            Return 1
        End If

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
            untilDate = DateTime.Now.Add(span)
        Else
            Console.Error.WriteLine("Specify a duration with --for or --until.")
            Return 1
        End If

        If untilDate <= DateTime.Now.AddSeconds(60) Then
            Console.Error.WriteLine("The block must end at least a minute in the future.")
            Return 1
        End If

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

        ' SD-c1: schedules and manual `--for` blocks are mutually exclusive in C5b (so the armed
        ' config is always manual-only OR schedule-only and the past-Until sentinel + scheduleArmed
        ' lifecycle stay unambiguous). Refuse a manual block while a schedule is armed - note a
        ' schedule-only block reads as `BlockIsActive()`=False (its Until is the past sentinel), so
        ' THIS guard, not the BlockIsActive check below, is what protects an armed schedule from
        ' being overwritten by `block`.
        If Blocker.ScheduleIsArmed() Then
            Console.Error.WriteLine("A schedule is armed. Clear it first with 'monkmode schedule --clear', then start a manual block.")
            Return 3
        End If

        If Blocker.BlockIsActive() Then
            Dim ends As DateTime = Blocker.ActiveBlockEnd()
            Console.WriteLine("A block is already active (" & Humanize(ends.Subtract(DateTime.Now)) & " left, until " & ends.ToString() & ").")
            Console.WriteLine("You can't start a new block or shorten this one. Use 'monkmode add --sites ...' to add sites.")
            Return 3
        End If

        If domains.Count > 0 Then
            Blocker.WriteHostsBlock(domains)
        Else
            ' Apps-only block: remove any stale snapshot from an earlier block,
            ' otherwise the service's B2 repair would resurrect the OLD sites
            ' into hosts for the lifetime of this block.
            Try
                File.Delete(Blocker.SnapshotPath())
            Catch
            End Try
        End If
        ' C4: `--commit` arms a COMMITTED block (self-serve cooling-off disabled = the
        ' partner code + expiry are the only exits). The flag is MAC-covered from birth.
        Dim committed As Boolean = HasFlag(args, "--commit")
        ' D2c: `--all-session-kill` widens app-kill from the current session (+ session 0) to
        ' EVERY logged-in session - the LocalSystem service kills blocked apps in all sessions,
        ' not just session 0. MAC-covered from birth; a widen-only policy (fail-closed: it can
        ' only ADD kills). No-op if no apps are blocked (nothing to kill), noted below only then.
        Dim allSessionKill As Boolean = HasFlag(args, "--all-session-kill")
        ' C3b: WriteConfig mints a fresh partner code and returns the plaintext ONCE
        ' (stored only as a salted, MAC-covered hash). Shown once below; never logged.
        ' C6b: coolOffSeconds (0 = default floor) is written MAC-covered from birth.
        Dim partnerCode As String = Blocker.WriteConfig(domains, apps, untilDate, committed, coolOffSeconds, allSessionKill)
        ' B5a: snapshot the user's current browser DoH policy BEFORE the service
        ' starts and forces it off, so teardown restores the pre-block state (no
        ' data loss). Must precede InstallAndStart - the service sets the policy in
        ' its OnStart. Never aborts arming the block; if it fails, teardown will
        ' leave the DoH-off policy in place rather than risk deleting a user value.
        If Not Blocker.WriteDohSnapshot() Then
            Console.Error.WriteLine("Warning: could not snapshot current browser DoH settings; MonkMode will leave 'Secure DNS off' in place at expiry rather than restore/remove it.")
        End If
        ServiceTools.ServiceInstaller.InstallAndStart(Blocker.ServiceName, Blocker.ServiceDisplay, serviceExe)
        ' D4d rider: a MANUAL arm clears an orphaned notifier first, so this block's
        ' fresh spawn wins D4c's single-instance claim instead of standing down behind
        ' a leftover pointed at the previous block (Blocker.ManualArmKillsLeftovers).
        Blocker.RegisterAndLaunchNotifier(Blocker.ManualArmKillsLeftovers)

        ' D3b: record this arm to the separate, non-MAC stats history (best-effort - Stats.RecordBlockStart
        ' swallows every error and never throws, so a stats failure can't perturb the block just armed
        ' above). COUNTS only (no site/app names) land in the plaintext file; the block is already fully
        ' armed, so this has ZERO enforcement authority - it is pure telemetry for `monkmode stats`.
        Stats.RecordBlockStart(DateTime.Now, untilDate, domains.Count, apps.Count, committed, coolOffSeconds)

        Console.WriteLine("MonkMode is now active until " & untilDate.ToString() & " (" & Humanize(untilDate.Subtract(DateTime.Now)) & ").")
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
        Console.WriteLine("Emergency unlock code (give it to your accountability partner NOW - it will NOT be shown again):")
        Console.WriteLine("    " & partnerCode)
        Console.WriteLine("To end the block early, they run:  monkmode unblock --code <CODE>")
        Return 0
    End Function

    Private Function DoStatus() As Integer
        If Not Blocker.ServiceIsInstalled() Then
            Console.WriteLine("MonkMode: no block has ever been installed on this machine.")
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
    Private Function DoStats() As Integer
        Dim s As Stats.StatsSummary = Stats.SummarizeAsOf(Stats.ReadRecords(), DateTime.Now)
        If Not s.HasAny Then
            Console.WriteLine("No blocks recorded yet. Start one with, e.g.:  monkmode block --sites reddit.com --for 2h")
            Return 0
        End If
        Console.WriteLine("MonkMode stats")
        Console.WriteLine("  Blocks started:   " & s.TotalBlocks & "  (" & s.CompletedBlocks & " completed, " & s.ActiveOrUpcomingBlocks & " active/upcoming)")
        Console.WriteLine("  Committed blocks: " & s.CommittedBlocks)
        Console.WriteLine("  Total focus time: " & Humanize(s.TotalPlannedTime) & " (planned)")
        Console.WriteLine("  Longest block:    " & Humanize(s.LongestPlannedBlock))
        Console.WriteLine("  First block:      " & s.FirstStart.ToString("yyyy-MM-dd"))
        Console.WriteLine("  Latest block:     " & s.LastStart.ToString("yyyy-MM-dd"))
        Return 0
    End Function

    Private Function DoAdd(ByVal args As String()) As Integer
        Dim domains As New List(Of String)
        domains.AddRange(SplitList(GetOption(args, "--sites")))
        If domains.Count = 0 Then
            Console.Error.WriteLine("Provide sites to add with --sites a.com,b.com")
            Return 1
        End If
        ' SD-c1: `add` targets a manual block. When a schedule is armed, edit the schedule instead
        ' (re-run `monkmode schedule` with the full site list) - the schedule's sites live in its
        ' MAC-covered Spec, not the manual snapshot `add` appends to.
        If Blocker.ScheduleIsArmed() Then
            Console.Error.WriteLine("A schedule is armed. To change its sites, re-run 'monkmode schedule --sites ... --windows ...' with the full list.")
            Return 1
        End If
        If Not Blocker.BlockIsActive() Then
            Console.Error.WriteLine("No active block to add to. Start one with 'monkmode block'.")
            Return 1
        End If
        Blocker.AppendAddToHosts(domains)
        Console.WriteLine("Added to the active block: " & String.Join(", ", domains))
        Return 0
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

        ' SD-c1: a manual `--for` block and a schedule are mutually exclusive in C5b.
        If Blocker.BlockIsActive() Then
            Console.Error.WriteLine("A manual block is active. Finish or exit it before setting a schedule.")
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
        Dim freshArm As Boolean = Not Blocker.ScheduleIsArmed()
        If freshArm Then
            If Not Blocker.WriteDohSnapshot() Then
                Console.Error.WriteLine("Warning: could not snapshot current browser DoH settings; MonkMode will leave 'Secure DNS off' in place at teardown rather than restore/remove it.")
            End If
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
            ' The cooling-off surface (bare request / --cancel). Only meaningful
            ' against an active block - the service only polls while it runs.
            If Not Blocker.BlockIsActive() Then
                Console.Error.WriteLine("No active block to unblock.")
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
                Blocker.RequestPartnerCode(code)
                Console.WriteLine("Code submitted. If it's correct the block lifts within ~10s; if not, the block stays fully enforced.")
                Return 0
            End If
            If HasFlag(args, "--cancel") Then
                Blocker.CancelCoolOff()
                Console.WriteLine("Cooling-off cancel requested. Any pending cooling-off is cleared within ~10s; the block continues to its normal end.")
                Return 0
            End If
            ' C4: a committed block has NO self-serve cooling-off - refuse the request
            ' with an actionable message instead of dropping a trigger the service would
            ' just Ignore. The partner code (verified service-side) is the intended exit.
            If Blocker.BlockIsCommitted() Then
                Console.Error.WriteLine("This block is COMMITTED: self-serve cooling-off is disabled. The only early exit is the accountability code:  monkmode unblock --code <CODE>")
                Return 1
            End If
            Blocker.RequestCoolOff()
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
    Friend Function FormatCoolOffStatusLine(ByVal committed As Boolean, ByVal coolOffRemaining As TimeSpan?) As String
        If committed Then
            Return "Exit:  committed block - the accountability code (shown at block start) is the only early exit, or wait for the timer."
        End If
        If coolOffRemaining IsNot Nothing Then
            Return "Exit:  cooling-off pending - lifts in about " & Humanize(coolOffRemaining.Value) & " of active time. Run 'monkmode unblock --cancel' to stay blocked."
        End If
        Return "Exit:  run 'monkmode unblock' to start a cooling-off wait, or the accountability code (shown at block start) lifts it now."
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
        Return New String() {"--sites", "--preset", "--apps", "--app-preset", "--for", "--until", "--file", "--commit", "--cooloff", "--all-session-kill"}
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
        Console.WriteLine("  monkmode block [--sites a.com,b.com] [--preset social,video] [--apps chrome.exe,foo.exe] [--app-preset games,chat] (--for 2h30m | --until ""2026-06-11 18:00"") [--file list.txt] [--commit] [--cooloff 2h] [--all-session-kill]")
        Console.WriteLine("  monkmode status  (an active block + time left + how to exit / a pending cooling-off, or the armed schedule's live window state)")
        Console.WriteLine("  monkmode stats   (read-only summary of your block history: counts, total focus time, longest block)")
        Console.WriteLine("  monkmode add --sites c.com")
        Console.WriteLine("  monkmode schedule --sites a.com,b.com [--apps chrome.exe] --windows ""Mon-Fri 09:00-17:00; Sat,Sun 10:00-14:00""")
        Console.WriteLine("  monkmode schedule --clear   (stop future windows; an open window still runs to its end)")
        Console.WriteLine("  monkmode schedule --show    (print the armed schedule; read-only)")
        Console.WriteLine("  monkmode schedule --validate --sites a.com --windows ""Mon-Fri 09:00-17:00""  (check a schedule without arming it)")
        Console.WriteLine("  monkmode unblock           (request cooling-off: the block lifts after ~1h of active machine time)")
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
        Console.WriteLine("  - schedule = recurring wall-clock windows (--windows uses days Mon-Sun + 24-hour HH:MM, same-day only). An open window holds at manual strength until it closes; a schedule and a manual block can't both be armed at once.")
        Console.WriteLine("  - --for accepts forms like 45 (minutes), 90m, 2h, 1d12h.")
        Console.WriteLine("  - --preset blocks a whole category of well-known sites at once (comma-separate several): " & String.Join(", ", Blocker.KnownPresetNames()) & ". Combine it with --sites to add your own.")
        Console.WriteLine("  - --app-preset kills a whole category of well-known apps at once (comma-separate several): " & String.Join(", ", Blocker.KnownAppPresetNames()) & ". Combine it with --apps to add your own.")
        Console.WriteLine("  - --cooloff sets THIS block's cooling-off wait (how long 'unblock' takes to lift), e.g. --cooloff 2h. A ~1h minimum applies, so a shorter value still waits that; a larger value makes leaving early harder. Same forms as --for.")
        Console.WriteLine("  - 'monkmode setup --cooloff 2h' sets an ACCOUNT DEFAULT cooling-off wait that every block without its own --cooloff inherits; a block's own --cooloff always overrides it. The ~1h minimum still applies.")
        Console.WriteLine("  - 'monkmode setup --default-sites a.com,b.com [--default-preset social]' sets an ACCOUNT DEFAULT blocklist that 'monkmode block' inherits when you give it no --sites/--preset/--file; naming any of those overrides the default. Each 'setup' run rewrites these defaults, so pass them again to keep them.")
        Console.WriteLine("  - 'monkmode setup --default-apps chrome.exe,foo.exe [--default-app-preset games]' sets an ACCOUNT DEFAULT app-kill list that 'monkmode block' inherits when you give it no --apps/--app-preset; naming either overrides the default. Each 'setup' run rewrites these defaults, so pass them again to keep them.")
    End Sub

End Module
