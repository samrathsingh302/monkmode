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

'    MonkMode - Guardian decision gates (B1 watchdog, layer 2)
'
'    The pure half of the SYSTEM-session guardian: every decision the live loop
'    in Program.vb takes goes through one of these functions, so the
'    tamper-critical logic is unit-testable without ever touching the real
'    SCM, process list or file system (the live wiring is verified by the
'    manual elevated smoke test, exactly like the service's B2 repair wiring).
'
'    Fail-safe direction, same spirit as the service's gates:
'      - BlockHasExpired is a copy of Service1.BlockHasExpired: an unparseable
'        Until is NOT expired (fail CLOSED), so a corrupted config keeps the
'        guardian guarding rather than letting it stand down early;
'      - ShouldRestartService / ShouldRelaunchNotifier only act while the block
'        is active, so the guardian can never resurrect anything after the
'        block has genuinely ended;
'      - ShouldRelaunchNotifier mirrors Service1.ShouldRestartPeer: no
'        duplicate-spawn while an instance is running, nothing started when the
'        exe is missing.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.Globalization

Friend Module Guardian

    ' Decides whether a persisted block end time has expired. Byte-for-byte the
    ' same semantics as Service1.BlockHasExpired (the service and the guardian
    ' must agree on "expired", or one side could stand down while the other
    ' still enforces): untilText is the decrypted [Time] Until value (an en-CA
    ' datetime string); expired means no more than graceSeconds remain at asOf;
    ' an unparseable value is NOT expired (fail CLOSED).
    Friend Function BlockHasExpired(ByVal untilText As String, ByVal asOf As DateTime, ByVal graceSeconds As Long) As Boolean
        Dim untilDate As DateTime
        If Not DateTime.TryParse(untilText, New CultureInfo("en-CA"), DateTimeStyles.None, untilDate) Then
            Return False
        End If
        Return DateDiff(DateInterval.Second, asOf, untilDate) <= graceSeconds
    End Function

    ' B7: MAC-aware expiry, byte-for-byte the same semantics as
    ' Service1.EffectiveBlockHasExpired - the guardian and the service MUST agree
    ' on "expired" or one side could stand down while the other still enforces.
    ' The block is expired ONLY when the time has genuinely passed AND the config
    ' MAC is valid; a tampered/invalid MAC (or an unparseable Until) reads as NOT
    ' expired, so the guardian keeps guarding (fail CLOSED) until a legitimate
    ' stamp exists. The live MAC evaluation that yields macValid lives in
    ' Program.vb (the DPAPI seam); this stays pure and unit-testable.
    Friend Function EffectiveBlockHasExpired(ByVal untilText As String, ByVal asOf As DateTime, ByVal graceSeconds As Long, ByVal macValid As Boolean) As Boolean
        Return macValid AndAlso BlockHasExpired(untilText, asOf, graceSeconds)
    End Function

    ' ---- Ledger 319 (30/08/2026): the cooling-off arm of the guardian's exit decision is
    ' PERMANENTLY DISARMED, exactly as in the service ----
    '
    ' This was the guardian's parity copy of Service1.CoolOffElapsedTime, folded into its
    ' EffectiveExit so a guardian tick in the stopMe() gap at the end of a cooling-off would
    ' not read "Until not passed + macValid => still active" and SCM-resurrect the service.
    ' There is no cooling-off any more - nothing writes a CoolOffUntil, and there is no gap to
    ' cover - so the honest answer is always NO, returned WITHOUT LOOKING at either argument.
    ' Both copies must stay byte-identical in BEHAVIOUR (parity-pinned like BlockHasExpired):
    ' if the guardian still lifted on an elapsed deadline while the service ignored it, the
    ' guardian would stand down under a block the service is still enforcing. Returning False
    ' is also the over-blocking direction here - it can only keep the guardian guarding.
    Friend Function CoolOffElapsedTime(ByVal coolOffUntilText As String, ByVal highWaterText As String) As Boolean
        Return False
    End Function

    ' C3b: is the block partner-code-unlocked? Byte-for-byte the same as
    ' Service1.PartnerUnlocked (parity-pinned): a non-empty [Partner] UnlockedAt
    ' (under the caller's macValid gate) = unlocked. LOAD-BEARING for the guardian:
    ' its stand-down MUST fold this in, or a guardian tick in the stopMe() gap at
    ' the end of a code-unlock would read "Until not passed + macValid => still
    ' active" and SCM-resurrect the just-code-unlocked block.
    Friend Function PartnerUnlocked(ByVal unlockedAtText As String) As Boolean
        Return Not String.IsNullOrWhiteSpace(unlockedAtText)
    End Function

    ' The ONE exit decision (the guardian's copy of Service1.EffectiveExit): the
    ' block may end ONLY when the MAC is valid AND (it genuinely expired OR a
    ' pending cooling-off deadline has been reached OR a partner code has unlocked
    ' it), the time-based arms measured against the monotonic HighWater. The
    ' guardian's stand-down gates on this, so it agrees with the service's lift
    ' within one tick - and never resurrects a cooled-off OR code-unlocked block
    ' (worst case, one bounce: a restarted service's OnStart immediately re-lifts,
    ' and the guardian's next read stands it down). C5b (SD1): also folds ScheduleActive
    ' in as a HARD HOLD - while a window is open the guardian keeps guarding and never
    ' stands down, so it can't stand down at a window's start nor resurrect one at its
    ' close (the service is the sole writer of [Schedule] ActiveUntil; the guardian only
    ' reads it). C5b (c2): also folds scheduleArmed - the BETWEEN-windows hold - so the
    ' guardian stays alive between windows of a recurring schedule (and can restart a killed
    ' service for tomorrow's window), standing down (terminal teardown) only once the Spec is
    ' cleared. This BODY is byte-for-byte Service1.EffectiveExit; the exact-vs-over-approx
    ' derivation of scheduleArmed differs only in the CALLER (Program.vb uses the cheap
    ' Spec-non-empty over-approximation - no 4th ParseSchedule copy - the fail-SAFE direction:
    ' a garbage non-empty Spec only keeps the guardian watching a little longer, never an early
    ' stand-down). Param order parity-pinned with Service1.EffectiveExit (until, coolOffUntil,
    ' unlockedAt, scheduleActiveUntil, highWater, grace, macValid, scheduleArmed).
    Friend Function EffectiveExit(ByVal untilText As String, ByVal coolOffUntilText As String, ByVal unlockedAtText As String, ByVal scheduleActiveUntilText As String, ByVal highWaterText As String, ByVal graceSeconds As Long, ByVal macValid As Boolean, ByVal scheduleArmed As Boolean) As Boolean
        If Not macValid Then Return False
        ' C5b (SD1): an open scheduled window is a HARD HOLD - nothing lifts while it is open.
        If ScheduleActive(scheduleActiveUntilText, highWaterText) Then Return False
        ' C5b (c2): an armed schedule keeps the service+guardian ALIVE between windows.
        If scheduleArmed Then Return False
        Dim asOf As DateTime = DateTime.MinValue
        Dim parsedHw As DateTime
        If DateTime.TryParse(highWaterText, New CultureInfo("en-CA"), DateTimeStyles.None, parsedHw) Then asOf = parsedHw
        Return BlockHasExpired(untilText, asOf, graceSeconds) OrElse CoolOffElapsedTime(coolOffUntilText, highWaterText) OrElse PartnerUnlocked(unlockedAtText)
    End Function

    ' ---- C5b: schedules (the guardian's half of the hold decision) ----
    '
    ' Byte-for-byte the same semantics as Service1.ScheduleElapsed / ScheduleActive
    ' (parity-pinned, like CoolOffElapsedTime). LOAD-BEARING for the guardian in C5b
    ' sub-slice (b): its stand-down gate must fold ScheduleActive in, or at a window's
    ' start it could stand down (Until passed + no cooling-off/code => "exited") and
    ' NOT restart a killed service mid-window, and at a window's close it could
    ' resurrect one. The guardian only READS ScheduleActiveUntil (the service is its
    ' sole writer, like HighWater/CoolOffUntil) - no write race. NOTE (sub-slice a):
    ' these gates exist + are parity-pinned here; the guardian's EffectiveExit fold +
    ' the [Schedule] ActiveUntil read are sub-slice (b) - this slice changes no
    ' guardian behaviour.

    ' Has the open scheduled window reached its monotonic close? "" (no window) and any
    ' unparseable input read as NOT elapsed (fail closed - a corrupted deadline keeps
    ' the guardian guarding). The caller folds macValid, exactly as expiry does.
    Friend Function ScheduleElapsed(ByVal scheduleActiveUntilText As String, ByVal highWaterText As String) As Boolean
        If scheduleActiveUntilText = "" Then Return False
        Dim ca As New CultureInfo("en-CA")
        Dim activeUntil As DateTime, highWater As DateTime
        If Not DateTime.TryParse(scheduleActiveUntilText, ca, DateTimeStyles.None, activeUntil) Then Return False
        If Not DateTime.TryParse(highWaterText, ca, DateTimeStyles.None, highWater) Then Return False
        Return activeUntil <= highWater
    End Function

    ' Is a scheduled window currently open (set AND not yet elapsed)? SD1: an open
    ' window is a HARD HOLD. Empty => not active; a non-empty-but-unparseable deadline
    ' => active (fail-closed: hold). Byte-for-byte the same as the service copy.
    Friend Function ScheduleActive(ByVal scheduleActiveUntilText As String, ByVal highWaterText As String) As Boolean
        Return scheduleActiveUntilText <> "" AndAlso Not ScheduleElapsed(scheduleActiveUntilText, highWaterText)
    End Function

    ' C5b (c2): the guardian's DERIVED scheduleArmed signal (design §4C) - the cheap OVER-
    ' APPROXIMATION (the Spec is non-empty), NOT a 4th ParseSchedule copy. It over-approximates
    ' the service's exact ScheduleArmed (ParseSchedule().Windows.Count>0) in the fail-SAFE
    ' direction: a garbage non-empty Spec keeps the guardian guarding a little longer, never an
    ' early stand-down (a parsed window implies a non-empty Spec, so guardian-armed superset
    ' service-armed). Null-safe via IsNullOrWhiteSpace (so a MAC-valid absent/whitespace Spec
    ' reads not-armed rather than throwing). macValid AndAlso first (a frozen config is never
    ' armed). Folded into EffectiveExit so the guardian stays alive between windows and can
    ' restart a killed service for the next one; stands down (terminal) only once the Spec is
    ' cleared. Pure so the derivation is directly unit-tested. INERT until the CLI writes a Spec.
    '
    ' v1.1 S4: NO LONGER WIRED. The live loop stopped reading the global [Schedule] Spec (P44
    ' replaces that legacy floor with the raw per-slot scan + the MAC'd [Guard] scalars), so
    ' Program.vb now passes scheduleArmed:=False into EffectiveExit and gets the schedule hold
    ' from AnyBlockHeld instead. Kept - not deleted - because it is still the parity twin of
    ' Service1.ScheduleArmed (pinned equal by the cross-assembly [Theory]s) and S5 makes a
    ' schedule a SLOT, at which point the guardian reads a slot's own ScheduleSpec through it.
    Friend Function ScheduleArmed(ByVal macValid As Boolean, ByVal specText As String) As Boolean
        Return macValid AndAlso Not String.IsNullOrWhiteSpace(specText)
    End Function

    ' ---- B4: monotonic high-water mark (clock-rollback hardening) ----
    '
    ' Byte-for-byte the same gates as Service1's B4 block (the parity tests pin
    ' that the two copies agree). The service is the SOLE writer of [Time]
    ' HighWater; the guardian only READS the last-persisted value and uses it as
    ' the asOf for its own EffectiveBlockHasExpired stand-down decision, so the
    ' guardian never stands down on a rolled clock either. These classifiers are
    ' here so the guardian could reason about an advance identically if needed
    ' and so the two halves can be parity-pinned exactly like BlockHasExpired.
    '
    ' DELIBERATE SEMANTIC: because HighWater only advances while the service is
    ' running, genuine machine-OFF downtime is NOT credited toward expiry - the
    ' block extends by the downtime. That is the fail-closed cost of defeating
    ' clock-forward (the block measures real ON-machine elapsed time). At OnStart
    ' the boot gap is a big delta => ForwardJump => not advanced => not credited.
    ' Intended. A spring-DST forward shift is likewise a >ceiling jump => refused
    ' => the only cost is a rare ~1h block-extension, the safe direction (LOCAL
    ' time on purpose, so a DST/timezone forward shift never lifts a block early).

    ' The classifier results for ClassifyTimeAdvance. Plain Integer (not an Enum)
    ' so the service-copy and guardian-copy results compare directly in the
    ' parity tests across the two assemblies. Untrusted folds into ForwardJump:
    ' both mean "do not credit this advance", so the caller needs only the one
    ' "don't advance" branch.
    Friend Const TimeAdvanceBackward As Integer = -1     ' delta < 0 (clock rolled back)
    Friend Const TimeAdvanceTrusted As Integer = 0       ' 0 <= delta <= ceiling (a real tick)
    Friend Const TimeAdvanceForwardJump As Integer = 1   ' delta > ceiling, OR unparseable stored (fail closed)

    ' The largest forward advance, in seconds, that still counts as a TRUSTED
    ' tick. Must be >> the 10s TimerIntervalMs so an ordinary (possibly slightly
    ' late) tick is always Trusted, while a deliberate clock-forward of minutes/
    ' hours is a ForwardJump. Pinned by a unit test (like the recovery-policy and
    ' SafeBoot consts) - a retune that dropped it near the tick interval would
    ' start refusing legitimate ticks (block never advances), and one that raised
    ' it huge would let a clock-forward of up to that many seconds through.
    Friend Const HighWaterJumpCeilingSeconds As Long = 120

    ' Classify how 'now' compares to the stored high-water mark. storedHwText and
    ' nowText are en-CA LOCAL datetime strings (same format/parse as [Time]
    ' Until, via DateDiff over the en-CA parse). Returns Backward (delta < 0),
    ' Trusted (0 <= delta <= ceilingSeconds) or ForwardJump (delta > ceiling).
    ' Fail-closed: an unparseable storedHw or nowText is ForwardJump (never
    ' credit an advance we can't measure). Pure and Shared so it is unit tested.
    Friend Function ClassifyTimeAdvance(ByVal storedHwText As String, ByVal nowText As String, ByVal ceilingSeconds As Long) As Integer
        Dim storedHw As DateTime, nowDt As DateTime
        If Not DateTime.TryParse(storedHwText, New CultureInfo("en-CA"), DateTimeStyles.None, storedHw) Then
            Return TimeAdvanceForwardJump
        End If
        If Not DateTime.TryParse(nowText, New CultureInfo("en-CA"), DateTimeStyles.None, nowDt) Then
            Return TimeAdvanceForwardJump
        End If
        Dim delta As Long = DateDiff(DateInterval.Second, storedHw, nowDt)
        If delta < 0 Then Return TimeAdvanceBackward
        If delta > ceilingSeconds Then Return TimeAdvanceForwardJump
        Return TimeAdvanceTrusted
    End Function

    ' The next [Time] HighWater string to persist. MONOTONIC: it advances to the
    ' 'now' string ONLY when the advance is Trusted (a real tick); a backward
    ' roll or a forward jump leaves it UNCHANGED (returns storedHwText), so a
    ' clock jump can never move it past Until. On an unparseable storedHw it is
    ' ForwardJump (not Trusted), so storedHwText is returned unchanged - keeping
    ' the value and the MAC coupled (a tampered HighWater already fails the MAC,
    ' so the block stands regardless; we deliberately do NOT re-seed to now here).
    ' Pure and Shared so it is unit tested.
    Friend Function NextHighWater(ByVal storedHwText As String, ByVal nowText As String, ByVal ceilingSeconds As Long) As String
        If ClassifyTimeAdvance(storedHwText, nowText, ceilingSeconds) = TimeAdvanceTrusted Then
            Return nowText
        End If
        Return storedHwText
    End Function

    ' Decides whether this tick should ask the SCM to start the MONKMODE
    ' service. Only while the block is active (blockActive is the caller's
    ' Not BlockHasExpired(...), so fail-closed carries through) and only when
    ' the service is not already running/starting - the guardian must never
    ' fight the SCM over an already-starting service, and never resurrect the
    ' service after the block has truly ended (it tore itself down for a
    ' reason: stopMe() strips hosts and stops the service at expiry).
    Friend Function ShouldRestartService(ByVal blockActive As Boolean, ByVal serviceRunning As Boolean) As Boolean
        If Not blockActive Then Return False
        Return Not serviceRunning
    End Function

    ' Decides whether this tick should relaunch the user-session notifier.
    ' Mirrors Service1.ShouldRestartPeer exactly (the same fail-safe shape, on
    ' the guardian's side of the pair): only while the block is active, only
    ' when the exe exists, only when no instance is already running so a slow
    ' start can never spawn a storm of duplicates.
    Friend Function ShouldRelaunchNotifier(ByVal notifierInstanceCount As Integer, ByVal blockActive As Boolean, ByVal notifierExeExists As Boolean) As Boolean
        If Not blockActive Then Return False
        If Not notifierExeExists Then Return False
        Return notifierInstanceCount <= 0
    End Function

    '    M1 (F6, 14/08/2026): the guardian's own single-instance name and process
    '    name. Kept here beside the other gates rather than in Program.vb, so the
    '    stand-down decision and the two names it needs sit in one unit-tested place
    '    (the SingleInstance.vb shape on the notifier's side).
    Friend Const GuardianMutexName As String = "Global\MonkModeGuardian"
    Friend Const GuardianProcessName As String = "mm_guard"

    '    M1: the stand-down decision for a guardian that LOST the single-instance
    '    claim - the exact port of MM_notify's SingleInstance.ShouldStandDown, which
    '    was written against this same attack and which this half never received.
    '
    '    Losing the mutex alone is NOT enough to exit. The old code exited on that
    '    signal alone, justified by a comment claiming a Global\ object needs
    '    SeCreateGlobalPrivilege - which is FALSE for mutexes. SeCreateGlobalPrivilege
    '    gates the creation of section (file-mapping) objects in the global namespace;
    '    mutexes, events and semaphores are not covered, and the name carries a default
    '    DACL. So ANY non-elevated same-machine process can create
    '    "Global\MonkModeGuardian" first, hold it forever, and every guardian the
    '    service ever spawns exits within milliseconds - the SYSTEM watchdog switched
    '    off permanently by three lines, and the service's ShouldRestartPeer (which
    '    counts processes, sees zero, and spawns again) turned into a 10 s spawn loop.
    '    That is strictly worse than having no mutex at all, which is exactly the
    '    argument SingleInstance.ShouldStandDown makes on the notifier's side.
    '
    '    So the loser also requires a SECOND real mm_guard process to exist. The count
    '    includes self, so > 1 means another copy is genuinely live. In the real race
    '    the mutex was meant to close (two service ticks both deciding to spawn) both
    '    processes exist, the loser sees >= 2 and exits; a bare squatter leaves the
    '    count at 1 and the guardian keeps guarding, unclaimed - the pre-mutex posture,
    '    which is safe because a duplicate guardian only ever OVER-enforces (its two
    '    actions are SCM-start-if-stopped and relaunch-notifier-if-none, both already
    '    idempotent gates) and the service's own spawn gate stops it multiplying.
    '
    '    Fail-closed direction, unlike the notifier's fail-SOFT one: this process's
    '    entire job is to keep enforcement alive, so every ambiguity resolves to "keep
    '    guarding". The live wrapper in Program.vb therefore also keeps guarding when
    '    the claim attempt itself THROWS - which is not hypothetical, since a squatter
    '    can create a different KIND of kernel object under the same name and make the
    '    Mutex constructor throw, a second three-line permanent kill.
    '
    '    Accepted residual, unchanged from the notifier's: a squatter who also runs a
    '    decoy process named mm_guard.exe makes every real guardian count >= 2 and
    '    stand down. That decoy ALREADY defeats the service's ShouldRestartPeer
    '    (peerInstanceCount <= 0) today with no mutex involved, so this adds no new
    '    capability.
    Friend Function ShouldStandDown(ByVal claimLost As Boolean, ByVal guardianProcessCount As Integer) As Boolean
        Return claimLost AndAlso guardianProcessCount > 1
    End Function

    ' ==== v1.1 S4 (P43/P44): the guardian sees SLOTS, without growing a parser ====
    '
    ' Until S4 the guardian's stand-down read the v9 SINGLE-BLOCK mirror only - [Time] Until,
    ' [Time] CoolOffUntil, [Partner] UnlockedAt, [Schedule] ActiveUntil, and the legacy
    ' [Schedule] Spec floor. v10 keeps every armed block in [Slot1]..[Slot8], so a machine can
    ' hold N blocks the mirror never mentions; a guardian that cannot see them would stand
    ' down - and stop SCM-restarting a killed service - while those blocks are still
    ' enforcing. That is the fail-OPEN direction, so the fold below is a pure WIDENING: the
    ' loop's blockActive becomes "the v9 residual still holds OR AnyBlockHeld", and nothing
    ' here can make the guardian exit earlier than it did before.
    '
    ' TWO INDEPENDENT SIGNALS, either of which alone holds:
    '
    '   1. THE MAC'd GUARD SCALARS (P43). [Guard] HoldUntil is the EXTEND-ONLY horizon - the
    '      latest moment any slot could still hold - written by the CLI at every arm and
    '      cleared ONLY by the whole-machine teardown; [Guard] ArmedCount counts the slots
    '      that keep the machinery up WITHOUT an open block of their own (schedule + pending).
    '      Both are INSIDE the v10 canonical, so editing either is tamper-evident (macValid
    '      False => held regardless). Extend-only means a stale scalar can only OVER-guard.
    '
    '   2. THE RAW PER-POSITION FLOOR (P44), scanned in Program.RawSlotFloorHeld: a non-empty
    '      Slot<pos>.ScheduleSpec / StartAt / Until at ANY pos 1..MaxSlots. NO parser, NO
    '      decrypt, NO MAC gate - the guardian must never grow a fifth copy of the slot
    '      readers, and a garbage non-empty value can only over-guard. It is the backstop for
    '      what the scalars cannot cover: a config whose [Guard] section was never written.
    '
    ' Both go quiet only once the slot SECTIONS ARE GONE, which is exactly what RetireSlotAt's
    ' compaction and P39's TeardownAll do (RemoveSection on every freed position, ArmedCount=0
    ' and HoldUntil="" written). Slots are removed, never flagged, so "the file still names a
    ' slot" is a sound floor and the guardian genuinely does stand down after a teardown.

    ' Is the extend-only [Guard] HoldUntil horizon still ahead of the service-written monotonic
    ' mark? DEFINED AS ScheduleActive rather than re-derived: the question has the identical
    ' shape (a stored deadline vs HighWater; "" = nothing recorded = not held; a non-empty but
    ' unparseable deadline = HELD, fail-closed) and two hand-written copies of one policy
    ' drift. B4: measured against HighWater, never DateTime.Now, so a rolled clock cannot
    ' shorten the guard horizon.
    Friend Function GuardHoldActive(ByVal guardHoldUntilText As String, ByVal highWaterText As String) As Boolean
        Return ScheduleActive(guardHoldUntilText, highWaterText)
    End Function

    ' Does [Guard] ArmedCount hold the guardian up? Blank/absent = nothing recorded = NO hold:
    ' a teardown writes an explicit "0", and for a config that never wrote the key at all the
    ' RAW FLOOR is the backstop - reading "absent" as held would leave a guardian that can
    ' never exit on any config missing the section, which is a wedge, not a defence. A parsed
    ' count > 0 holds. A NON-EMPTY value that will not parse HOLDS (fail-closed): the only
    ' thing "I cannot read the count" can honestly mean is "keep guarding".
    Friend Function GuardArmedCountHolds(ByVal guardArmedCountText As String) As Boolean
        If String.IsNullOrWhiteSpace(guardArmedCountText) Then Return False
        Dim n As Integer
        If Not Integer.TryParse(guardArmedCountText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, n) Then Return True
        Return n > 0
    End Function

    ' THE S4 HOLD. Pure, so the whole truth table is unit-tested; the live reads it consumes
    ' (decrypting [Guard] HoldUntil, the raw slot scan, the DPAPI MAC evaluation) stay in
    ' Program.vb behind this one call, exactly like EffectiveExit's macValid.
    '
    ' Not macValid => True BEFORE anything else. A tampered or unreadable config must never
    ' stand the watchdog down, and with zero readable slots a plain "does anything hold?" fold
    ' answers False - the empty-list fail-open that Service1.AnyBlockHeld guards against the
    ' same way, by answering the MAC first.
    Friend Function AnyBlockHeld(ByVal guardHoldUntilText As String, ByVal guardArmedCountText As String, ByVal rawFloorHeld As Boolean, ByVal highWaterText As String, ByVal macValid As Boolean) As Boolean
        If Not macValid Then Return True
        If rawFloorHeld Then Return True
        If GuardHoldActive(guardHoldUntilText, highWaterText) Then Return True
        Return GuardArmedCountHolds(guardArmedCountText)
    End Function

End Module
