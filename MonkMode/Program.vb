'    MonkMode - CLI entry point
'
'    Usage:
'      monkmode block  --sites a.com,b.com [--apps chrome.exe,foo.exe]
'                      (--for 2h30m | --until "2026-06-11 18:00") [--file list.txt] [--commit]
'      monkmode status
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
                Case "block" : Return DoBlock(args)
                Case "status" : Return DoStatus()
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

    Private Function DoBlock(ByVal args As String()) As Integer
        Dim domains As New List(Of String)
        domains.AddRange(SplitList(GetOption(args, "--sites")))

        Dim fileArg As String = GetOption(args, "--file")
        If fileArg <> "" AndAlso File.Exists(fileArg) Then
            For Each line As String In File.ReadAllLines(fileArg)
                Dim t As String = line.Trim()
                If t <> "" AndAlso Not t.StartsWith("#") Then domains.Add(t)
            Next
        End If

        Dim apps As New List(Of String)
        apps.AddRange(SplitList(GetOption(args, "--apps")))

        If domains.Count = 0 AndAlso apps.Count = 0 Then
            Console.Error.WriteLine("Nothing to block. Provide --sites and/or --apps.")
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
        ' C3b: WriteConfig mints a fresh partner code and returns the plaintext ONCE
        ' (stored only as a salted, MAC-covered hash). Shown once below; never logged.
        Dim partnerCode As String = Blocker.WriteConfig(domains, apps, untilDate, committed)
        ' B5a: snapshot the user's current browser DoH policy BEFORE the service
        ' starts and forces it off, so teardown restores the pre-block state (no
        ' data loss). Must precede InstallAndStart - the service sets the policy in
        ' its OnStart. Never aborts arming the block; if it fails, teardown will
        ' leave the DoH-off policy in place rather than risk deleting a user value.
        If Not Blocker.WriteDohSnapshot() Then
            Console.Error.WriteLine("Warning: could not snapshot current browser DoH settings; MonkMode will leave 'Secure DNS off' in place at expiry rather than restore/remove it.")
        End If
        ServiceTools.ServiceInstaller.InstallAndStart(Blocker.ServiceName, Blocker.ServiceDisplay, serviceExe)
        Blocker.RegisterAndLaunchNotifier()

        Console.WriteLine("MonkMode is now active until " & untilDate.ToString() & " (" & Humanize(untilDate.Subtract(DateTime.Now)) & ").")
        If domains.Count > 0 Then Console.WriteLine("  Sites: " & String.Join(", ", domains))
        If apps.Count > 0 Then Console.WriteLine("  Apps:  " & String.Join(", ", apps))
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
        Dim ends As DateTime = Blocker.ActiveBlockEnd()
        If Blocker.ServiceIsRunning() AndAlso ends > DateTime.Now Then
            Console.WriteLine("MonkMode: ACTIVE")
            Console.WriteLine("  Ends:  " & ends.ToString() & " (" & Humanize(ends.Subtract(DateTime.Now)) & " left)")
            Dim sites As String = Blocker.BlockedSites()
            Dim apps As String = Blocker.BlockedApps()
            If sites <> "" Then Console.WriteLine("  Sites: " & sites.Replace(";", " "))
            If apps <> "" Then Console.WriteLine("  Apps:  " & apps.Replace(";", " "))
        Else
            Console.WriteLine("MonkMode: no active block (service installed but idle).")
        End If
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
        Blocker.RegisterAndLaunchNotifier()

        Console.WriteLine("Schedule armed. Windows open automatically at their times.")
        Console.WriteLine("  Windows: " & windowsArg.Trim())
        Console.WriteLine("  Sites:   " & String.Join(", ", sites))
        If apps.Count > 0 Then Console.WriteLine("  Apps:    " & String.Join(", ", apps))
        Console.WriteLine("During a window the block holds at full strength until the window closes; it cannot be ended early.")
        Console.WriteLine("Change it any time with 'monkmode schedule ...'; stop future windows with 'monkmode schedule --clear'.")
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

    Private Function Humanize(ByVal ts As TimeSpan) As String
        If ts.TotalSeconds <= 0 Then Return "0 minutes"
        Dim parts As New List(Of String)
        If ts.Days > 0 Then parts.Add(ts.Days & "d")
        If ts.Hours > 0 Then parts.Add(ts.Hours & "h")
        If ts.Minutes > 0 Then parts.Add(ts.Minutes & "m")
        If parts.Count = 0 Then parts.Add("<1m")
        Return String.Join(" ", parts)
    End Function

    Private Sub PrintUsage()
        Console.WriteLine("MonkMode - tamper-resistant self-control blocker")
        Console.WriteLine("")
        Console.WriteLine("Usage:")
        Console.WriteLine("  monkmode block --sites a.com,b.com [--apps chrome.exe,foo.exe] (--for 2h30m | --until ""2026-06-11 18:00"") [--file list.txt] [--commit]")
        Console.WriteLine("  monkmode status")
        Console.WriteLine("  monkmode add --sites c.com")
        Console.WriteLine("  monkmode schedule --sites a.com,b.com [--apps chrome.exe] --windows ""Mon-Fri 09:00-17:00; Sat,Sun 10:00-14:00""")
        Console.WriteLine("  monkmode schedule --clear   (stop future windows; an open window still runs to its end)")
        Console.WriteLine("  monkmode unblock           (request cooling-off: the block lifts after ~1h of active machine time)")
        Console.WriteLine("  monkmode unblock --cancel  (cancel a pending cooling-off; stay blocked)")
        Console.WriteLine("  monkmode unblock --code <CODE>  (submit the partner accountability code; the service verifies it and lifts within ~10s)")
        Console.WriteLine("  monkmode unblock --force   (escape hatch: tears down an active block + removes the service)")
        Console.WriteLine("  monkmode help")
        Console.WriteLine("")
        Console.WriteLine("Notes:")
        Console.WriteLine("  - Run as Administrator (needed to edit the hosts file and install the service).")
        Console.WriteLine("  - Once a block starts it cannot be shortened; 'unblock' starts a mandatory cooling-off wait.")
        Console.WriteLine("  - --commit arms a COMMITTED block: self-serve cooling-off is disabled, so the only early exit is the accountability code shown at block start (or the timer).")
        Console.WriteLine("  - schedule = recurring wall-clock windows (--windows uses days Mon-Sun + 24-hour HH:MM, same-day only). An open window holds at manual strength until it closes; a schedule and a manual block can't both be armed at once.")
        Console.WriteLine("  - --for accepts forms like 45 (minutes), 90m, 2h, 1d12h.")
    End Sub

End Module
