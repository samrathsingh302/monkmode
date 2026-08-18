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

'    MonkMode - guardian entry point (B1 watchdog, layer 2)
'
'    mm_guard.exe is the SYSTEM-session guardian half of the mutual
'    service <-> guardian restart pair (vault\dev\monk-mode\specs\ARCHITECTURE.md B1, decision (A) locked
'    13/06/2026). The service's timer spawns it (gate: Service1.ShouldRestartPeer)
'    and re-spawns it if it is killed; reciprocally, every tick this loop:
'
'      1. reads monkmode_settings.ini next to the exes and EXITS once EVERY
'         block has genuinely ended - the v9 residual ([Time] Until and friends)
'         AND, since v1.1 S4, the v10 slots ([Guard] HoldUntil / ArmedCount plus
'         the raw [Slot1..8] floor, folded by Guardian.AnyBlockHeld). That is the
'         only way the guardian ever stands down; every unreadable or unparseable
'         value fails CLOSED and it keeps guarding;
'      2. restarts the MONKMODE service via the SCM if it is not running
'         (it has SCM rights: spawned by the LocalSystem service, it IS SYSTEM);
'      3. relaunches the user-session notifier (mm_notify.exe) if it has been
'         killed - the proper version of the old MM_notify2 twin's job. A
'         SYSTEM process can't just Process.Start into the interactive session,
'         so this uses WTSQueryUserToken + CreateProcessAsUser (no user logged
'         on -> skipped, retried next tick).
'
'    All decisions go through the pure gates in Guardian.vb (unit-tested); all
'    actions are best-effort Try/Catch and retried on the next tick, so a
'    transient failure can never kill the guardian loop itself.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.IO
Imports System.Runtime.InteropServices
Imports System.ServiceProcess
Imports System.Text
Imports System.Threading

Module Program

    Friend Const ServiceName As String = "MONKMODE"
    Friend Const NotifierExeName As String = "mm_notify.exe"
    Friend Const NotifierProcessName As String = "mm_notify"
    Friend Const IniName As String = "monkmode_settings.ini"

    ' B7 tamper-evident config: same [Integrity] section the CLI stamps. The
    ' guardian reads (never writes) it - a MAC failure keeps it guarding.
    Friend Const IntegritySection As String = "Integrity"
    Friend Const IntegrityKeyName As String = "Key"
    Friend Const IntegrityMacName As String = "Mac"

    ' Same cadence and expiry grace as the service's timer (10s tick, 5s grace)
    ' so both halves of the pair agree on "expired" within one tick of each
    ' other. Pinned by the unit tests - keep in sync with Service1.
    Friend Const TickIntervalMs As Integer = 10000
    Friend Const ExpiryGraceSeconds As Long = 5

    Private ReadOnly enc As New Simple3Des("mm_textbox")

    Sub Main()
        ' Single instance, machine-wide ("Global\" so the SYSTEM-session copy
        ' also excludes any copy started in a user session, and vice versa).
        ' The service's spawn gate already counts processes, but the mutex
        ' closes the race of two ticks both deciding to spawn.
        '
        ' M1 (F6, 14/08/2026) - CORRECTION. This block used to exit on the bare
        ' "already exists" signal, and to exit on a constructor throw, justified
        ' by a comment claiming a Global\ object needs SeCreateGlobalPrivilege so
        ' only a stray non-elevated launch could fail here. That premise is
        ' WRONG: SeCreateGlobalPrivilege gates SECTION (file-mapping) objects in
        ' the global namespace, not mutexes/events/semaphores, and the name
        ' carries a default DACL. Any non-elevated same-machine process could
        ' therefore create "Global\MonkModeGuardian" first and permanently
        ' disable the SYSTEM watchdog in three lines - while the service kept
        ' respawning it every 10 s, because ShouldRestartPeer counts processes
        ' and always found zero. MM_notify's SingleInstance.ShouldStandDown was
        ' written against exactly this attack; this half never got it. Ported now.
        '
        ' Both failure directions therefore KEEP GUARDING:
        '   - claim lost, but no second real mm_guard process exists => a squatter,
        '     not a genuine race => carry on unclaimed (the pre-mutex posture);
        '   - the constructor THROWS (e.g. a squatter created a different KIND of
        '     kernel object under the same name, which is a second three-line
        '     kill) => carry on unclaimed rather than exit.
        ' Running unclaimed is safe: a duplicate guardian only ever OVER-enforces
        ' (both its actions are already idempotent gates), and the service's own
        ' spawn gate stops duplicates multiplying.
        Dim createdNew As Boolean = False
        Dim mtx As Mutex = Nothing
        Try
            mtx = New Mutex(True, Guardian.GuardianMutexName, createdNew)
            If Not createdNew Then
                ' We never took ownership (initiallyOwned is ignored when the
                ' object already exists), so closing our handle releases nothing
                ' of theirs - it just drops our reference.
                mtx.Dispose()
                mtx = Nothing
                Dim liveCount As Integer =
                    Process.GetProcessesByName(Guardian.GuardianProcessName).Length
                If Guardian.ShouldStandDown(True, liveCount) Then Return
            End If
        Catch ex As Exception
            ' Claim attempt or process count failed. Cannot prove a genuine second
            ' guardian exists, so guard on without a claim - never exit.
            mtx = Nothing
        End Try

        ' AppDomain.UnhandledException backstop (fail-closed on crash) - see
        ' OnUnhandledException. Registered only once we are going to guard (a
        ' genuine duplicate returned above); an unclaimed guardian guards for
        ' real, so it needs the backstop just as much as a claimed one.
        AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf OnUnhandledException

        Try
            Do
                ' Sleep first: the service that just spawned us has already
                ' verified the world this tick - act on the NEXT tick.
                Thread.Sleep(TickIntervalMs)

                ' Read the v9 residual ([Time] Until/HighWater/CoolOffUntil, the C3b
                ' [Partner] UnlockedAt, [Schedule] ActiveUntil), the S4 slot signals
                ' ([Guard] HoldUntil/ArmedCount + the raw [SlotN] floor) and the B7 MAC
                ' validity in one ini load.
                Dim until As String = ""
                Dim highWater As String = ""
                Dim coolOffUntil As String = ""
                Dim unlockedAt As String = ""
                Dim scheduleActiveUntil As String = ""
                Dim guardHoldUntil As String = ""
                Dim guardArmedCount As String = ""
                Dim rawFloorHeld As Boolean = False
                Dim macValid As Boolean = False
                ReadBlockState(until, highWater, coolOffUntil, unlockedAt, scheduleActiveUntil,
                               guardHoldUntil, guardArmedCount, rawFloorHeld, macValid)

                ' Fail CLOSED on every axis: an unparseable Until OR an invalid/
                ' absent B7 MAC (a tampered config) reads as NOT ended, so the
                ' guardian keeps guarding. Only a valid MAC AND (a parsed, past
                ' end time OR an elapsed cooling-off deadline) stands it down -
                ' exactly Service1's exit semantics (EffectiveExit parity), so the
                ' pair never disagree. B4: both are measured against the service-
                ' written HighWater mark (parsed inside EffectiveExit; unparseable
                ' => MinValue => NOT ended), never raw DateTime.Now, so a rolled
                ' clock can't stand the guardian down. C2b: folding cooling-off in
                ' here is LOAD-BEARING - without it the guardian would SCM-restart
                ' the service the moment a completed cooling-off tears it down,
                ' resurrecting the cooled-off block. C3b: folding the partner-code
                ' UnlockedAt in is LOAD-BEARING for the identical reason - the
                ' guardian must not resurrect a just-code-unlocked block.
                '
                ' v1.1 S4: the stand-down is now the AND of two independent exits, i.e.
                ' blockActive is their OR - the v9 residual must have exited AND no slot may
                ' hold. AnyBlockHeld can only ever ADD a hold, so that disjunct is a pure
                ' widening.
                '
                ' P44: scheduleArmed is passed FALSE and the global [Schedule] Spec is no
                ' longer read at all - the raw slot floor replaces that legacy floor, and v10
                ' moves the rule to each slot's own ScheduleSpec, which the floor sees. An OPEN
                ' window still holds HARD through scheduleActiveUntil, and every slot-borne
                ' schedule holds through the floor and [Guard] ArmedCount.
                '
                ' THE ONE NARROWING THIS COSTS, argued rather than hidden: a v9-shaped
                ' schedule-only config - which today's `monkmode schedule` still writes, until
                ' S5 makes a schedule a slot - has NO slots, so BETWEEN windows nothing holds
                ' here any more and the guardian exits at a window's close instead of surviving
                ' to the next one. It is bounded: the SERVICE's own peer-spawn gate is
                ' enforcementHeld = BlockHeld OrElse slotsHeld, and for that config BlockHeld is
                ' already False between windows - so the service would not respawn a guardian
                ' killed in that gap either, and after a reboot in the gap none exists at all.
                ' S4 therefore ALIGNS the guardian's stand-down with the service's own notion of
                ' "enforcement held" rather than leaving the two halves disagreeing. Inside a
                ' window, and for every slot-borne block, nothing changes.
                Dim blockActive As Boolean =
                    (Not Guardian.EffectiveExit(until, coolOffUntil, unlockedAt, scheduleActiveUntil, highWater, ExpiryGraceSeconds, macValid, False)) OrElse
                    Guardian.AnyBlockHeld(guardHoldUntil, guardArmedCount, rawFloorHeld, highWater, macValid)
                If Not blockActive Then
                    ' Genuinely expired (parsed, past end time, valid MAC): stand
                    ' down for good. The service's stopMe() also kills us at
                    ' expiry; this is the fallback if we outlive that.
                    Exit Do
                End If

                TryRestartService(blockActive)
                TryRelaunchNotifier(blockActive)
            Loop
        Finally
            ' Nothing when we are guarding unclaimed (squatter, or a claim attempt
            ' that threw); the claim is otherwise a handle the kernel frees with the
            ' process anyway, so a missed Dispose could never strand it.
            If mtx IsNot Nothing Then mtx.Dispose()
        End Try
    End Sub

    ' AppDomain.UnhandledException backstop (fail-closed on crash). The guardian
    ' holds no hosts/registry/config state of its own - it only READS the ini and
    ' drives the SCM/notifier - so its own crash leaves nothing fail-OPEN. But its
    ' whole purpose is to keep the enforcement core alive, so if an exception is
    ' about to kill it, make ONE best-effort pass to (re)start the MONKMODE service
    ' while the block is still active before dying - so the guardian's own death
    ' never leaves the service down. Reuses the EXACT fail-closed gates the loop
    ' uses (ReadBlockState fails CLOSED on any read/MAC error; the HighWater asOf;
    ' Guardian.EffectiveBlockHasExpired; Guardian.ShouldRestartService inside
    ' TryRestartService). The service reciprocally re-spawns the guardian
    ' (ShouldRestartPeer), so this is defence-in-depth. Never throws.
    Private Sub OnUnhandledException(ByVal sender As Object, ByVal e As UnhandledExceptionEventArgs)
        Try
            Dim until As String = ""
            Dim highWater As String = ""
            Dim coolOffUntil As String = ""
            Dim unlockedAt As String = ""
            Dim scheduleActiveUntil As String = ""
            Dim guardHoldUntil As String = ""
            Dim guardArmedCount As String = ""
            Dim rawFloorHeld As Boolean = False
            Dim macValid As Boolean = False
            ReadBlockState(until, highWater, coolOffUntil, unlockedAt, scheduleActiveUntil,
                           guardHoldUntil, guardArmedCount, rawFloorHeld, macValid)
            ' C2b/C3b/C5b: same EffectiveExit gate as the loop - the dying guardian must
            ' not restart the service into a block that just cooled off, was code-unlocked
            ' OR whose scheduled window has closed (nor stand down mid-window: an open
            ' window holds via ScheduleActive). v1.1 S4: and the SAME AnyBlockHeld fold, so
            ' the dying guardian's last act also covers a machine whose only live blocks are
            ' SLOTS the v9 residual never mentions - byte-for-byte the loop's expression.
            Dim blockActive As Boolean =
                (Not Guardian.EffectiveExit(until, coolOffUntil, unlockedAt, scheduleActiveUntil, highWater, ExpiryGraceSeconds, macValid, False)) OrElse
                Guardian.AnyBlockHeld(guardHoldUntil, guardArmedCount, rawFloorHeld, highWater, macValid)
            TryRestartService(blockActive)
        Catch ex As Exception
        End Try
    End Sub

    ' Reads the block state from a single ini load: the decrypted [Time] Until
    ' (untilOut), the decrypted [Time] HighWater (highWaterOut, B4), the
    ' decrypted [Time] CoolOffUntil (coolOffUntilOut, C2b) and the B7 MAC
    ' validity (macValidOut). All fail CLOSED on any error - untilOut "" is
    ' unparseable (block reads active), highWaterOut "" parses to MinValue
    ' (reads active), coolOffUntilOut "" means no cooling-off pending (never an
    ' early stand-down), macValidOut False means a tampered/unreadable config
    ' also reads active - so a deleted or corrupted config keeps the guardian
    ' guarding, never stands it down. One load (not four) so Until, HighWater,
    ' CoolOffUntil, ScheduleActiveUntil, the S4 slot signals and the MAC are all evaluated
    ' against the same bytes. C5b: scheduleActiveOut is the decrypted [Schedule] ActiveUntil
    ' ("" = no window open, the fail-closed default); the guardian only READS it (the
    ' service is its sole writer, like HighWater/CoolOffUntil), and folding
    ' ScheduleActive into the stand-down (via EffectiveExit) is LOAD-BEARING - without
    ' it the guardian could stand down at a window's start (not restart a killed
    ' service mid-window) or resurrect the block at its close.
    '
    ' v1.1 S4 (P43/P44) - the three slot signals, and one deletion:
    '   * guardHoldOut       = the DECRYPTED [Guard] HoldUntil horizon ("" = none recorded);
    '   * guardArmedCountOut = [Guard] ArmedCount, plaintext-as-stored (an int; the MAC is
    '                          its protection, so it is never decrypted);
    '   * rawFloorHeldOut    = the P44 raw per-position floor (see RawSlotFloorHeld);
    '   * the global [Schedule] Spec read is GONE. It was the legacy floor P44 replaces, and
    '     v10 moves the rule to each slot's own ScheduleSpec - which the raw floor sees.
    ' All three keep the same fail-closed defaults as the rest: a failed load leaves
    ' macValidOut False, which alone makes Guardian.AnyBlockHeld answer HELD.
    Private Sub ReadBlockState(ByRef untilOut As String, ByRef highWaterOut As String, ByRef coolOffUntilOut As String, ByRef unlockedOut As String, ByRef scheduleActiveOut As String, ByRef guardHoldOut As String, ByRef guardArmedCountOut As String, ByRef rawFloorHeldOut As Boolean, ByRef macValidOut As Boolean)
        untilOut = ""
        highWaterOut = ""
        coolOffUntilOut = ""
        unlockedOut = ""
        scheduleActiveOut = ""
        guardHoldOut = ""
        guardArmedCountOut = ""
        rawFloorHeldOut = False
        macValidOut = False
        Try
            Dim ini As New IniFile
            ini.Load(Path.Combine(AppContext.BaseDirectory, IniName))
            untilOut = enc.DecryptData(ini.GetKeyValue("Time", "Until"))
            highWaterOut = enc.DecryptData(ini.GetKeyValue("Time", "HighWater"))
            Dim coolOffEnc As String = ini.GetKeyValue("Time", "CoolOffUntil")
            coolOffUntilOut = If(coolOffEnc = "", "", enc.DecryptData(coolOffEnc))
            ' C3b: [Partner] UnlockedAt is plaintext-as-stored (MAC-covered), not
            ' decrypted; absent/"" = not code-unlocked (never an early stand-down).
            unlockedOut = ini.GetKeyValue("Partner", "UnlockedAt")
            ' C5b: [Schedule] ActiveUntil is an encrypted datetime like CoolOffUntil
            ' ("" = no window open). Absent/unreadable => "" (fail-closed: no phantom
            ' window; a genuine window's deadline is covered by the same MAC).
            Dim scheduleEnc As String = ini.GetKeyValue("Schedule", "ActiveUntil")
            scheduleActiveOut = If(scheduleEnc = "", "", enc.DecryptData(scheduleEnc))
            ' S4/P43: [Guard] HoldUntil is an ENCRYPTED datetime like ActiveUntil ("" = none
            ' recorded); [Guard] ArmedCount is a plaintext int. Both are MAC-covered, so an
            ' edit to either is tamper-evident and lands on macValid=False => held.
            Dim guardHoldEnc As String = ini.GetKeyValue("Guard", "HoldUntil")
            guardHoldOut = If(guardHoldEnc = "", "", enc.DecryptData(guardHoldEnc))
            guardArmedCountOut = ini.GetKeyValue("Guard", "ArmedCount")
            ' S4/P44: the raw floor, read BEFORE the MAC evaluation deliberately - if anything
            ' below were to throw, the Catch leaves macValidOut False (=> held) rather than a
            ' quiet floor under a True MAC.
            rawFloorHeldOut = RawSlotFloorHeld(ini)
            macValidOut = ConfigMacIsValidForIni(ini)
        Catch ex As Exception
        End Try
    End Sub

    ' P44 - THE GUARDIAN FLOOR: does the config still NAME any block? A raw key scan with NO
    ' parser, NO decrypt and NO MAC gate. Held when EITHER
    '   (a) the clamped [Slots] SlotCount is > 0, or
    '   (b) any position 1..MaxSlots carries a non-empty ScheduleSpec / StartAt / Until.
    '
    ' (a) is what makes the floor a provable SUPERSET of the service's own slot hold rather
    ' than a shape-by-shape approximation of it: Service1.LoadSlots only ever produces a slot
    ' for pos = 1 To SlotCount, and Service1.SlotHeld holds a slot with NO recorded end ("no
    ' recorded end" can never mean "over"). So a config declaring a slot whose section is
    ' empty or absent holds the SERVICE - and without (a) the guardian would stand down under
    ' it, which is the one direction that must never happen. Pinned by
    ' GuardianHold_IsASupersetOfTheServiceSlotHold. Clamped, so a forged count reads 0 rather
    ' than 99 - the fail-open direction on its own, which is exactly why (b) does not consult
    ' the count at all and why a forged count breaks the MAC anyway (=> held).
    '
    ' Why raw. The guardian must not grow a fifth copy of the slot readers (that is how the
    ' four canonical copies stay honest - see CanonicalFromIni's warning), and it needs an
    ' answer even for a config it cannot decrypt or verify. Every axis over-approximates in
    ' the SAFE direction: a garbage non-empty value, a stale section beyond SlotCount, a
    ' ciphertext it never decrypts - each one only ever keeps the guardian watching longer.
    ' The scan bound is MaxSlots, NOT the stored SlotCount, so forging SlotCount=0 cannot
    ' silence it.
    '
    ' Why it still goes quiet. Slots are REMOVED, never flagged: RetireSlotAt compacts and
    ' RemoveSection's the freed trailing position, and P39's TeardownAll removes all
    ' MaxSlots sections before the hosts strip. So after a genuine teardown no position
    ' carries any of the three keys and the floor answers False - which is what lets the
    ' guardian ever exit at all.
    '
    ' Fail-closed on its own account: any throw answers True (keep guarding). Friend so the
    ' unit tests drive it against a test-owned ini, the CanonicalFromIni pattern.
    Friend Function RawSlotFloorHeld(ByVal ini As IniFile) As Boolean
        Try
            If ini Is Nothing Then Return True
            If ConfigIntegrity.ParseSlotCount(ini.GetKeyValue("Slots", "SlotCount")) > 0 Then Return True
            For pos As Integer = 1 To ConfigIntegrity.MaxSlots
                Dim sec As String = "Slot" & pos.ToString(System.Globalization.CultureInfo.InvariantCulture)
                If Not String.IsNullOrWhiteSpace(ini.GetKeyValue(sec, "ScheduleSpec")) Then Return True
                If Not String.IsNullOrWhiteSpace(ini.GetKeyValue(sec, "StartAt")) Then Return True
                If Not String.IsNullOrWhiteSpace(ini.GetKeyValue(sec, "Until")) Then Return True
            Next
            Return False
        Catch ex As Exception
            Return True
        End Try
    End Function

    ' B7/B4: builds the v10 two-level canonical the MAC is computed over, from a
    ' loaded ini - the global header, then one 16-line block per slot for
    ' pos = 1 To the CLAMPED [Slots] SlotCount. Every party (this writer, plus the
    ' service/guardian/notifier readers) must derive a byte-identical string or the
    ' MAC never validates and every block freezes, so BELOW the `crypt` alias line
    ' this body is byte-identical text in all four copies - diff them on any edit.
    ' [Integrity] Key/Mac are excluded (you can't MAC the MAC); missing values pass
    ' through as "". Stale [SlotN] sections beyond SlotCount are never read.
    Friend Function CanonicalFromIni(ByVal ini As IniFile) As String
        Dim crypt As Simple3Des = enc      ' the ONLY line that differs across the four copies
        ' Globals: HighWater/Now/[Guard] HoldUntil are ENCRYPTED datetimes (decrypted
        ' here, like every datetime); [Slots] NextSlotId/SlotCount and [Guard]
        ' ArmedCount are plaintext ints (the MAC is their protection).
        Dim highWaterEnc As String = ini.GetKeyValue("Time", "HighWater")
        Dim nowEnc As String = ini.GetKeyValue("CurrentTime", "Now")
        Dim guardHoldEnc As String = ini.GetKeyValue("Guard", "HoldUntil")
        Dim highWaterPlain As String = If(highWaterEnc = "", "", crypt.DecryptData(highWaterEnc))
        Dim nowPlain As String = If(nowEnc = "", "", crypt.DecryptData(nowEnc))
        Dim guardHoldPlain As String = If(guardHoldEnc = "", "", crypt.DecryptData(guardHoldEnc))

        ' The CLAMPED count is BOTH the header value and the loop bound, so a forged
        ' SlotCount can only ever build a canonical nothing can match -> freeze.
        Dim slotCount As Integer = ConfigIntegrity.ParseSlotCount(ini.GetKeyValue("Slots", "SlotCount"))
        Dim slots As New System.Text.StringBuilder()
        For pos As Integer = 1 To slotCount
            Dim sec As String = "Slot" & pos.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ' Per slot, the ENCRYPTED datetimes are Until/StartAt/CoolOffUntil/
            ' ScheduleActiveUntil; everything else - INCLUDING Sites/Apps/UrlPatterns -
            ' is plaintext-as-stored. No "null" sentinel any more (v10): "no apps" is "".
            Dim untilEnc As String = ini.GetKeyValue(sec, "Until")
            Dim startAtEnc As String = ini.GetKeyValue(sec, "StartAt")
            Dim coolOffEnc As String = ini.GetKeyValue(sec, "CoolOffUntil")
            Dim scheduleActiveEnc As String = ini.GetKeyValue(sec, "ScheduleActiveUntil")
            slots.Append(ConfigIntegrity.BuildSlotCanonical(pos,
                ini.GetKeyValue(sec, "Id"),
                If(startAtEnc = "", "", crypt.DecryptData(startAtEnc)),
                ini.GetKeyValue(sec, "DurationSeconds"),
                If(untilEnc = "", "", crypt.DecryptData(untilEnc)),
                ini.GetKeyValue(sec, "Sites"),
                ini.GetKeyValue(sec, "Apps"),
                ini.GetKeyValue(sec, "UrlPatterns"),
                ini.GetKeyValue(sec, "AllSession"),
                ini.GetKeyValue(sec, "ScheduleSpec"),
                If(scheduleActiveEnc = "", "", crypt.DecryptData(scheduleActiveEnc)),
                If(coolOffEnc = "", "", crypt.DecryptData(coolOffEnc)),
                ini.GetKeyValue(sec, "CoolOffDuration"),
                ini.GetKeyValue(sec, "PartnerSalt"),
                ini.GetKeyValue(sec, "PartnerHash"),
                ini.GetKeyValue(sec, "PartnerUnlockedAt"),
                ini.GetKeyValue(sec, "Committed")))
        Next

        Return ConfigIntegrity.BuildCanonical(ConfigIntegrity.CurrentSchemaVersion,
                                             highWaterPlain,
                                             nowPlain,
                                             ini.GetKeyValue("Slots", "NextSlotId"),
                                             slotCount,
                                             guardHoldPlain,
                                             ini.GetKeyValue("Guard", "ArmedCount"),
                                             slots.ToString())
    End Function

    ' B7 live MAC gate (DPAPI seam - smoke-tested). DPAPI-unprotect [Integrity]
    ' Key and validate [Integrity] Mac over the canonical. False on ANY failure
    ' (missing/blank/non-Base64 key or MAC, DPAPI denial, foreign-machine blob,
    ' crypto error) so a tamper reads as "keep guarding", never "stand down".
    Private Function ConfigMacIsValidForIni(ByVal ini As IniFile) As Boolean
        Try
            Dim key() As Byte = ConfigIntegrity.UnprotectKey(ini.GetKeyValue(IntegritySection, IntegrityKeyName))
            If key Is Nothing Then Return False
            Return ConfigIntegrity.ConfigMacIsValid(CanonicalFromIni(ini), ini.GetKeyValue(IntegritySection, IntegrityMacName), key)
        Catch ex As Exception
            Return False
        End Try
    End Function

    Private Sub TryRestartService(ByVal blockActive As Boolean)
        Try
            Using sc As New ServiceController(ServiceName)
                Dim running As Boolean =
                    (sc.Status = ServiceControllerStatus.Running OrElse
                     sc.Status = ServiceControllerStatus.StartPending)
                If Guardian.ShouldRestartService(blockActive, running) Then
                    sc.Start()
                End If
            End Using
        Catch ex As Exception
            ' Service deleted/SCM denied/start race - retry next tick.
        End Try
    End Sub

    Private Sub TryRelaunchNotifier(ByVal blockActive As Boolean)
        Try
            Dim notifierExe As String = Path.Combine(AppContext.BaseDirectory, NotifierExeName)
            Dim count As Integer = Process.GetProcessesByName(NotifierProcessName).Length
            If Guardian.ShouldRelaunchNotifier(count, blockActive, File.Exists(notifierExe)) Then
                LaunchInActiveUserSession(notifierExe)
            End If
        Catch ex As Exception
            ' Best effort - retry next tick.
        End Try
    End Sub

#Region "Launch into the interactive user session (SYSTEM -> user)"

    ' A SYSTEM-session process can't Process.Start a program into the
    ' interactive desktop - it would land invisibly in session 0. The supported
    ' route is: active console session id -> that user's primary token
    ' (WTSQueryUserToken, needs SE_TCB which SYSTEM has) -> CreateProcessAsUser
    ' on winsta0\default with the user's environment block. Every failure path
    ' just returns; the caller retries on a later tick (e.g. once someone is
    ' actually logged on).

    Private Const CREATE_UNICODE_ENVIRONMENT As Integer = &H400
    Private Const INVALID_SESSION_ID As UInteger = &HFFFFFFFFUI

    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
    Private Structure STARTUPINFO
        Public cb As Integer
        Public lpReserved As String
        Public lpDesktop As String
        Public lpTitle As String
        Public dwX As Integer
        Public dwY As Integer
        Public dwXSize As Integer
        Public dwYSize As Integer
        Public dwXCountChars As Integer
        Public dwYCountChars As Integer
        Public dwFillAttribute As Integer
        Public dwFlags As Integer
        Public wShowWindow As Short
        Public cbReserved2 As Short
        Public lpReserved2 As IntPtr
        Public hStdInput As IntPtr
        Public hStdOutput As IntPtr
        Public hStdError As IntPtr
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure PROCESS_INFORMATION
        Public hProcess As IntPtr
        Public hThread As IntPtr
        Public dwProcessId As Integer
        Public dwThreadId As Integer
    End Structure

    <DllImport("kernel32.dll")>
    Private Function WTSGetActiveConsoleSessionId() As UInteger
    End Function

    <DllImport("wtsapi32.dll", SetLastError:=True)>
    Private Function WTSQueryUserToken(ByVal sessionId As UInteger, ByRef phToken As IntPtr) As Boolean
    End Function

    <DllImport("userenv.dll", SetLastError:=True)>
    Private Function CreateEnvironmentBlock(ByRef lpEnvironment As IntPtr, ByVal hToken As IntPtr, ByVal bInherit As Boolean) As Boolean
    End Function

    <DllImport("userenv.dll", SetLastError:=True)>
    Private Function DestroyEnvironmentBlock(ByVal lpEnvironment As IntPtr) As Boolean
    End Function

    <DllImport("advapi32.dll", EntryPoint:="CreateProcessAsUserW", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function CreateProcessAsUser(ByVal hToken As IntPtr, ByVal lpApplicationName As String,
        ByVal lpCommandLine As StringBuilder, ByVal lpProcessAttributes As IntPtr,
        ByVal lpThreadAttributes As IntPtr, ByVal bInheritHandles As Boolean,
        ByVal dwCreationFlags As Integer, ByVal lpEnvironment As IntPtr,
        ByVal lpCurrentDirectory As String, ByRef lpStartupInfo As STARTUPINFO,
        ByRef lpProcessInformation As PROCESS_INFORMATION) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function CloseHandle(ByVal hObject As IntPtr) As Boolean
    End Function

    Private Sub LaunchInActiveUserSession(ByVal exePath As String)
        Dim sessionId As UInteger = WTSGetActiveConsoleSessionId()
        If sessionId = INVALID_SESSION_ID Then Return ' no console session attached

        Dim userToken As IntPtr = IntPtr.Zero
        If Not WTSQueryUserToken(sessionId, userToken) Then Return ' nobody logged on (or not SYSTEM)

        Dim envBlock As IntPtr = IntPtr.Zero
        Try
            If Not CreateEnvironmentBlock(envBlock, userToken, False) Then
                envBlock = IntPtr.Zero ' fall back to inheriting ours
            End If

            Dim si As New STARTUPINFO
            si.cb = Marshal.SizeOf(GetType(STARTUPINFO))
            si.lpDesktop = "winsta0\default"
            Dim pi As New PROCESS_INFORMATION

            ' CreateProcessW may scribble on the command-line buffer, so it must
            ' be mutable (StringBuilder). Quote the path against spaces.
            Dim cmd As New StringBuilder("""" & exePath & """")
            If CreateProcessAsUser(userToken, exePath, cmd, IntPtr.Zero, IntPtr.Zero, False,
                                   CREATE_UNICODE_ENVIRONMENT, envBlock,
                                   Path.GetDirectoryName(exePath), si, pi) Then
                If pi.hProcess <> IntPtr.Zero Then CloseHandle(pi.hProcess)
                If pi.hThread <> IntPtr.Zero Then CloseHandle(pi.hThread)
            End If
        Finally
            If envBlock <> IntPtr.Zero Then DestroyEnvironmentBlock(envBlock)
            CloseHandle(userToken)
        End Try
    End Sub

#End Region

End Module
