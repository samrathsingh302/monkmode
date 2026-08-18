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

'    MonkMode - StatsSidecar (v1.1 S7b, pins P45-P49): counters, day-log, streaks.
'
'    WHAT THIS IS: two small counter files in %ProgramData%\MonkMode\ that record
'    what MonkMode actually DID - apps killed, browsers nudged, seconds spent with
'    a block held - so `monkmode stats` can show actuals and streaks next to the
'    PLANNED history Stats.vb already keeps (monkmode_stats, in AppDir).
'
'    THE LOAD-BEARING SAFETY PROPERTY (P49, and the reason this file is allowed to
'    exist at all): NOTHING IN ANY ENFORCEMENT PATH EVER READS THESE FILES. The
'    service's expiry decision, the hosts self-heal, the MAC, the guardian floor,
'    the app-kill matcher and the URL watcher are all completely independent of
'    them. Deleting, forging, truncating or hostile-editing a sidecar changes
'    NUMBERS ON A SCREEN and nothing else - it can never lift, shorten, extend or
'    perturb a block. Consequently the files are NOT MAC-covered (they make no
'    security claim, so there is nothing to forge) and every READ and every WRITE
'    in this module is wrapped in a Try that swallows: a stats failure must never
'    throw into the service's 10s tick or the notifier's 2s beat.
'
'    P45 - TWO SINGLE-WRITER FILES, never one shared file:
'      stats-service.ini  written ONLY by the service (per-slot app-kill counts +
'                         the armed-seconds day-log)
'      stats-notify.ini   written ONLY by the notifier (per-slot redirect counts)
'    The service runs as LocalSystem and the notifier non-elevated, on unrelated
'    timers; a shared file would need cross-process locking to avoid lost updates,
'    and a lock is a thing that can be held. Two writers, two files, no lock. The
'    display path (`monkmode stats` / `status`) MERGES them field-wise (P48).
'
'    P46 - FORMAT IS IniFile, deliberately: byte-identical in all four projects
'    already, atomic Save (temp + rename), tolerant Load (missing file = no-op,
'    junk lines skipped), verbatim value fidelity - zero new dependency and zero
'    new parser. Schema:
'
'      [Meta]
'      Version=st1
'      [Lifetime]
'      Kills=0
'      Redirects=0
'      ArmedSeconds=0
'      [Slot.<id>]
'      Kills=0
'      Redirects=0
'      [Days]
'      2026-08-18=<armedSeconds>|<kills>|<redirects>
'
'    Slot sections are keyed on the slot's stable Id, NEVER on its position: a
'    retire COMPACTS the config's [SlotN] sections, so a position is a different
'    block tomorrow while an Id is for ever.
'
'    P47 - the day-log is keyed yyyy-MM-dd on the WALL clock (a streak is a
'    calendar idea, and this is display-only, so the monotonic HighWater the
'    enforcement core uses is the wrong timeline here). A "focus day" is a day key
'    with armedSeconds > 0. Retention is MaxDayKeys = 730 keys, oldest pruned at
'    write, so the file is bounded (~25 KB) no matter how long MonkMode runs.
'
'    P48 - counters SURVIVE. A retire, a teardown, `unblock --force` and an
'    uninstall without -PurgeData all leave these files alone: a streak history is
'    USER DATA and the no-data-loss rule covers it. Nothing in this module deletes
'    anything; only the pruner drops day keys, and only beyond 730.
'
'    TOTALITY CONTRACT: every function here is total. Nothing returns Nothing, and
'    an absent / corrupt / garbage / hostile file reads as ZEROS rather than
'    throwing. That is not politeness - the ONLY reason a counter is allowed to be
'    incremented from inside the service tick is that no input can make this code
'    throw.
'
'    This file is byte-for-byte identical across the CLI, the service and the
'    notifier (the guardian has no use for it), like the ConfigIntegrity / IniFile
'    / Simple3Des copies - the unit tests pin that 3-copy parity. Only the
'    RootNamespace differs (set per project), so no Namespace is declared here.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Security.AccessControl
Imports System.Security.Principal

Friend Module StatsSidecar

    ' The [Meta] Version tag. A file carrying anything else is read as ZEROS (not
    ' as garbage to guess at) so a future format can never be half-understood by an
    ' old reader; and because the counters are display-only, "reads as zero" is a
    ' complete, safe answer.
    Friend Const SchemaVersion As String = "st1"

    ' P45: the two single-writer file names, and the directory they live in.
    Friend Const ServiceFileName As String = "stats-service.ini"
    Friend Const NotifyFileName As String = "stats-notify.ini"
    Friend Const DirName As String = "MonkMode"

    ' P47: day-key retention. 730 = two years; oldest pruned at write.
    Friend Const MaxDayKeys As Integer = 730

    ' The day-key format. Chosen because it sorts ORDINALLY in calendar order,
    ' which is what lets the pruner and the streak walk use plain string sorts.
    Friend Const DayKeyFormat As String = "yyyy-MM-dd"

    Private Const MetaSection As String = "Meta"
    Private Const LifetimeSection As String = "Lifetime"
    Private Const DaysSection As String = "Days"
    Private Const SlotSectionPrefix As String = "Slot."
    Private Const VersionKey As String = "Version"
    Private Const KillsKey As String = "Kills"
    Private Const RedirectsKey As String = "Redirects"
    Private Const ArmedSecondsKey As String = "ArmedSeconds"
    Private Const DayFieldSep As Char = "|"c

    ' ------------------------------------------------------------------
    ' the model
    ' ------------------------------------------------------------------

    ' One bucket of counters. A reference type with mutable fields so Merge can
    ' accumulate into a fresh instance without a pile of copying.
    Friend Class Counts
        Public Kills As Long
        Public Redirects As Long
        Public ArmedSeconds As Long

        Public Sub New()
        End Sub

        Public Sub New(ByVal kills As Long, ByVal redirects As Long, ByVal armedSeconds As Long)
            Me.Kills = kills
            Me.Redirects = redirects
            Me.ArmedSeconds = armedSeconds
        End Sub
    End Class

    ' One sidecar's worth of counters, or the merge of several. PerSlot is keyed on
    ' the slot Id (case-insensitively, since ini lookup is); Days on the yyyy-MM-dd
    ' key (ORDINALLY - a date key has no casing, and ordinal keeps sort and lookup
    ' agreeing).
    Friend Class StatsData
        Public ReadOnly Lifetime As New Counts()
        Public ReadOnly PerSlot As New Dictionary(Of String, Counts)(StringComparer.OrdinalIgnoreCase)
        Public ReadOnly Days As New Dictionary(Of String, Counts)(StringComparer.Ordinal)
    End Class

    ' ------------------------------------------------------------------
    ' locations (P45) - pure string work, no IO
    ' ------------------------------------------------------------------

    ' %ProgramData%\MonkMode. NOT beside the exes: Program Files is admin-write-only
    ' by deliberate ACL (tools\install.ps1's "WHY PROGRAM FILES"), and the notifier
    ' runs NON-elevated, so a sidecar next to the binaries could never be written by
    ' the party that counts redirects. "" if the folder cannot be resolved (the
    ' callers then do nothing).
    Friend Function StatsDir() As String
        Try
            Dim root As String = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            If root Is Nothing OrElse root.Length = 0 Then Return ""
            Return Path.Combine(root, DirName)
        Catch ex As Exception
            Return ""
        End Try
    End Function

    Friend Function ServiceStatsPath() As String
        Dim dir As String = StatsDir()
        If dir = "" Then Return ""
        Return Path.Combine(dir, ServiceFileName)
    End Function

    Friend Function NotifyStatsPath() As String
        Dim dir As String = StatsDir()
        If dir = "" Then Return ""
        Return Path.Combine(dir, NotifyFileName)
    End Function

    ' The [Days] key for an instant. WALL clock by design (see the header).
    Friend Function DayKeyFor(ByVal moment As DateTime) As String
        Return moment.ToString(DayKeyFormat, CultureInfo.InvariantCulture)
    End Function

    ' ------------------------------------------------------------------
    ' pure arithmetic
    ' ------------------------------------------------------------------

    ' A one-event delta, internally consistent: the same counts land in Lifetime,
    ' in the slot's bucket (skipped when slotId is blank - an unattributable event
    ' still counts towards the lifetime and day totals) and in the day's bucket
    ' (skipped when dayKey is blank). Merge-able, so a tick that killed three apps
    ' for two different slots is three of these folded together.
    Friend Function NewDelta(ByVal slotId As String,
                             ByVal kills As Long,
                             ByVal redirects As Long,
                             ByVal armedSeconds As Long,
                             ByVal dayKey As String) As StatsData
        Dim d As New StatsData()
        AddInto(d.Lifetime, kills, redirects, armedSeconds)
        Dim id As String = If(slotId, "").Trim()
        If id <> "" Then AddInto(BucketFor(d.PerSlot, id), kills, redirects, armedSeconds)
        Dim day As String = If(dayKey, "").Trim()
        If day <> "" Then AddInto(BucketFor(d.Days, day), kills, redirects, armedSeconds)
        Return d
    End Function

    ' Field-wise sum of two StatsData into a NEW one (neither input is mutated).
    ' Nothing reads as "no counters", so Merge(Nothing, Nothing) is a valid zero -
    ' which is what makes the display path's "one file is missing" case free.
    Friend Function Merge(ByVal a As StatsData, ByVal b As StatsData) As StatsData
        Dim outData As New StatsData()
        MergeInto(outData, a)
        MergeInto(outData, b)
        Return outData
    End Function

    ' Does this carry any counter at all? Used by the service to skip a write on a
    ' tick that recorded nothing (an idle machine must not rewrite the file every
    ' 10s for no reason).
    Friend Function IsEmpty(ByVal d As StatsData) As Boolean
        If d Is Nothing Then Return True
        If d.Lifetime.Kills <> 0 OrElse d.Lifetime.Redirects <> 0 OrElse d.Lifetime.ArmedSeconds <> 0 Then Return False
        Return d.PerSlot.Count = 0 AndAlso d.Days.Count = 0
    End Function

    ' P47: keep only the newest maxKeys day entries (ordinal sort = calendar order),
    ' dropping the oldest. Mutates in place; a non-positive maxKeys is ignored
    ' rather than obeyed - "prune everything" is never what a caller means, and
    ' silently binning the whole history would breach the no-data-loss rule.
    Friend Sub PruneDays(ByVal d As StatsData, ByVal maxKeys As Integer)
        If d Is Nothing OrElse maxKeys <= 0 Then Return
        If d.Days.Count <= maxKeys Then Return
        Dim keys As New List(Of String)(d.Days.Keys)
        keys.Sort(StringComparer.Ordinal)
        Dim drop As Integer = keys.Count - maxKeys
        For i As Integer = 0 To drop - 1
            d.Days.Remove(keys(i))
        Next
    End Sub

    ' The counters recorded for one day key ("" / absent => a zero bucket, never
    ' Nothing). Returns a COPY, so a display caller cannot scribble on the model.
    Friend Function TotalForDay(ByVal d As StatsData, ByVal dayKey As String) As Counts
        If d Is Nothing OrElse dayKey Is Nothing Then Return New Counts()
        Dim c As Counts = Nothing
        If Not d.Days.TryGetValue(dayKey, c) Then Return New Counts()
        Return New Counts(c.Kills, c.Redirects, c.ArmedSeconds)
    End Function

    ' P47: a FOCUS DAY is a day whose armedSeconds is > 0 - i.e. a day MonkMode
    ' actually held a block. Kills and redirects alone do not make one (they can
    ' only happen while a block is held anyway, and keying the streak off the one
    ' field the service always writes keeps the definition single-valued).
    Friend Function IsFocusDay(ByVal d As StatsData, ByVal dayKey As String) As Boolean
        Return TotalForDay(d, dayKey).ArmedSeconds > 0
    End Function

    ' How many focus days are on record.
    Friend Function FocusDayCount(ByVal d As StatsData) As Integer
        If d Is Nothing Then Return 0
        Dim n As Integer = 0
        For Each kv As KeyValuePair(Of String, Counts) In d.Days
            If kv.Value.ArmedSeconds > 0 Then n += 1
        Next
        Return n
    End Function

    ' The streak of consecutive focus days ending at todayKey.
    '
    ' LENIENT ANCHOR, deliberately: if today is not (yet) a focus day the walk
    ' starts at YESTERDAY, so a streak is not reported as broken at 00:01 simply
    ' because the day is young - it breaks only once a WHOLE day has passed with
    ' nothing blocked. The alternative (anchor strictly on today) would show every
    ' user a 0 every morning, which is both discouraging and untrue.
    '
    ' An unparseable todayKey => 0. Bounded: the walk stops at the first non-focus
    ' day, and the day-log holds at most MaxDayKeys entries.
    Friend Function CurrentStreak(ByVal d As StatsData, ByVal todayKey As String) As Integer
        If d Is Nothing Then Return 0
        Dim today As DateTime
        If Not DateTime.TryParseExact(If(todayKey, ""), DayKeyFormat, CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, today) Then Return 0
        Dim cursor As DateTime = today
        If Not IsFocusDay(d, DayKeyFor(cursor)) Then
            cursor = cursor.AddDays(-1)
            If Not IsFocusDay(d, DayKeyFor(cursor)) Then Return 0
        End If
        Dim n As Integer = 0
        While IsFocusDay(d, DayKeyFor(cursor))
            n += 1
            cursor = cursor.AddDays(-1)
        End While
        Return n
    End Function

    ' The longest run of consecutive focus days anywhere in the log. Walks the
    ' sorted focus-day keys and restarts the run whenever two adjacent keys are not
    ' one calendar day apart. An unparseable key ends the current run (it cannot be
    ' placed on the calendar, so it cannot extend anything).
    Friend Function LongestStreak(ByVal d As StatsData) As Integer
        If d Is Nothing Then Return 0
        Dim keys As New List(Of String)
        For Each kv As KeyValuePair(Of String, Counts) In d.Days
            If kv.Value.ArmedSeconds > 0 Then keys.Add(kv.Key)
        Next
        If keys.Count = 0 Then Return 0
        keys.Sort(StringComparer.Ordinal)
        Dim best As Integer = 0
        Dim run As Integer = 0
        Dim prev As DateTime = DateTime.MinValue
        Dim havePrev As Boolean = False
        For Each k As String In keys
            Dim cur As DateTime
            If Not DateTime.TryParseExact(k, DayKeyFormat, CultureInfo.InvariantCulture,
                                          DateTimeStyles.None, cur) Then
                run = 0
                havePrev = False
                Continue For
            End If
            If havePrev AndAlso cur = prev.AddDays(1) Then
                run += 1
            Else
                run = 1
            End If
            If run > best Then best = run
            prev = cur
            havePrev = True
        Next
        Return best
    End Function

    ' ------------------------------------------------------------------
    ' serialisation (P46) - pure, no IO
    ' ------------------------------------------------------------------

    ' Read a loaded ini into the model. TOLERANT: a wrong/absent [Meta] Version, a
    ' missing section, an unparseable integer or a malformed day value all read as
    ' ZEROS for that field rather than aborting the parse. Never throws.
    Friend Function FromIni(ByVal ini As IniFile) As StatsData
        Dim d As New StatsData()
        If ini Is Nothing Then Return d
        Try
            If ini.GetKeyValue(MetaSection, VersionKey) <> SchemaVersion Then Return New StatsData()
            AddInto(d.Lifetime,
                    ParseCount(ini.GetKeyValue(LifetimeSection, KillsKey)),
                    ParseCount(ini.GetKeyValue(LifetimeSection, RedirectsKey)),
                    ParseCount(ini.GetKeyValue(LifetimeSection, ArmedSecondsKey)))
            ' IniFile.Sections is the non-generic ICollection of the Friend
            ' IniSection type (it deliberately exposes nothing Public). This module
            ' is compiled INTO each project's own assembly, so the element type is
            ' directly in scope and no late binding or IniFile.vb edit is needed.
            For Each sec As IniSection In ini.Sections
                Dim name As String = If(sec.Name, "")
                If name.StartsWith(SlotSectionPrefix, StringComparison.OrdinalIgnoreCase) Then
                    Dim id As String = name.Substring(SlotSectionPrefix.Length).Trim()
                    If id <> "" Then
                        AddInto(BucketFor(d.PerSlot, id),
                                ParseCount(ini.GetKeyValue(name, KillsKey)),
                                ParseCount(ini.GetKeyValue(name, RedirectsKey)),
                                0)
                    End If
                ElseIf String.Equals(name, DaysSection, StringComparison.OrdinalIgnoreCase) Then
                    For Each key As String In New List(Of String)(sec.Keys)
                        Dim armed As Long, kills As Long, redirects As Long
                        If TryParseDayValue(ini.GetKeyValue(DaysSection, key), armed, kills, redirects) Then
                            AddInto(BucketFor(d.Days, key.Trim()), kills, redirects, armed)
                        End If
                    Next
                End If
            Next
        Catch ex As Exception
            ' Whatever parsed before the failure stands; the counters are cosmetic.
        End Try
        Return d
    End Function

    ' Write the model into a FRESH ini (the caller Saves it). Day keys are emitted
    ' in calendar order so the file reads like a log.
    Friend Function ToIni(ByVal d As StatsData) As IniFile
        Dim ini As New IniFile()
        ini.SetKeyValue(MetaSection, VersionKey, SchemaVersion)
        Dim src As StatsData = If(d, New StatsData())
        ini.SetKeyValue(LifetimeSection, KillsKey, Render(src.Lifetime.Kills))
        ini.SetKeyValue(LifetimeSection, RedirectsKey, Render(src.Lifetime.Redirects))
        ini.SetKeyValue(LifetimeSection, ArmedSecondsKey, Render(src.Lifetime.ArmedSeconds))
        Dim slotIds As New List(Of String)(src.PerSlot.Keys)
        slotIds.Sort(StringComparer.Ordinal)
        For Each id As String In slotIds
            Dim c As Counts = src.PerSlot(id)
            ini.SetKeyValue(SlotSectionPrefix & id, KillsKey, Render(c.Kills))
            ini.SetKeyValue(SlotSectionPrefix & id, RedirectsKey, Render(c.Redirects))
        Next
        Dim dayKeys As New List(Of String)(src.Days.Keys)
        dayKeys.Sort(StringComparer.Ordinal)
        ini.AddSection(DaysSection)
        For Each key As String In dayKeys
            Dim c As Counts = src.Days(key)
            ini.SetKeyValue(DaysSection, key,
                            Render(c.ArmedSeconds) & DayFieldSep & Render(c.Kills) & DayFieldSep & Render(c.Redirects))
        Next
        Return ini
    End Function

    ' ------------------------------------------------------------------
    ' IO - every entry point best-effort, never throws
    ' ------------------------------------------------------------------

    ' The largest sidecar this module will PARSE. A real one is bounded by design -
    ' MaxDayKeys day lines plus a [Slot.<id>] pair per slot is roughly 25 KB - so 1 MB
    ' is forty times any honest file and cannot be reached by MonkMode's own writes.
    '
    ' WHY A BOUND EXISTS AT ALL (S7b verifier P2). These files sit in
    ' %ProgramData%\MonkMode, where P49 deliberately grants BUILTIN\Users : Modify so
    ' the non-elevated notifier can record a redirect. A user therefore has WRITE
    ' access to a file that two enforcement-adjacent beats read: the notifier's 5s
    ' RefreshTray poll runs on the WinForms UI THREAD - the very thread the app-kill
    ' and URL-watch timers dispatch on - and the service reads its own file once per
    ' 10s tick. Planting a multi-hundred-megabyte stats-*.ini mid-block would make
    ' those reads take seconds, starving the layers the notifier exists to run. That
    ' is a denial-of-service against enforcement bought with an ordinary text file,
    ' and no counter is worth it.
    Friend Const MaxFileBytes As Long = 1000000L

    ' Read one sidecar. A missing / unreadable / corrupt / wrong-version / OVERSIZE
    ' file reads as an empty StatsData (all zeros). Never throws.
    '
    ' Oversize is treated as corrupt rather than as an error, and that choice
    ' SELF-HEALS: the next Apply merges zeros with its delta and rewrites a small,
    ' well-formed file, so one planted giant costs the user their counter history
    ' (display-only, and it was theirs to delete anyway) and nothing else. No block is
    ' touched either way.
    Friend Function ReadFrom(ByVal path As String) As StatsData
        Try
            If path Is Nothing OrElse path = "" OrElse Not File.Exists(path) Then Return New StatsData()
            ' Inside the same Try as the read, deliberately: a delete racing between
            ' the length probe and the Load throws, and a throw here already means
            ' zeros. The bound is a fast-path refusal, never a second failure mode.
            If New FileInfo(path).Length > MaxFileBytes Then Return New StatsData()
            Dim ini As New IniFile()
            ini.Load(path)
            Return FromIni(ini)
        Catch ex As Exception
            Return New StatsData()
        End Try
    End Function

    ' P48: the display view - both sidecars, summed field-wise. Either file being
    ' absent or corrupt simply contributes zeros. Never throws.
    Friend Function ReadMerged() As StatsData
        Return Merge(ReadFrom(ServiceStatsPath()), ReadFrom(NotifyStatsPath()))
    End Function

    ' Add `delta` to the sidecar at `path`: read what is there, merge, prune to
    ' MaxDayKeys, save atomically. BEST-EFFORT - returns True only when the file was
    ' written, and NEVER throws, because the two callers are the service's 10s tick
    ' and the notifier's watch pass and a counter may not be able to disturb either.
    '
    ' A read-modify-write is safe here precisely because of P45: each file has ONE
    ' writer, so there is no second party whose update could be lost.
    Friend Function Apply(ByVal path As String,
                          ByVal delta As StatsData,
                          ByVal grantUsersModify As Boolean) As Boolean
        Try
            If path Is Nothing OrElse path = "" OrElse IsEmpty(delta) Then Return False
            If Not EnsureDirFor(path, grantUsersModify) Then Return False
            Dim merged As StatsData = Merge(ReadFrom(path), delta)
            PruneDays(merged, MaxDayKeys)
            ToIni(merged).Save(path)
            Return True
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' P49: make sure the directory HOLDING `filePath` exists - in production always
    ' %ProgramData%\MonkMode, since that is where the two paths above point.
    '
    ' Derived from the file path rather than from StatsDir() on purpose: it makes
    ' Apply complete on any path, which is what lets the unit tests exercise the
    ' whole read-merge-prune-write cycle against a temp file in the test bin
    ' directory WITHOUT any test ever creating or touching %ProgramData%\MonkMode
    ' (the MonkMode.Tests fence).
    '
    ' grantUsersModify is the SERVICE's flag. LocalSystem creating the directory
    ' leaves it inheriting ProgramData's ACL, under which an ordinary user has read
    ' but not write - and the notifier, which runs NON-elevated, would then never be
    ' able to record a redirect. So the service adds an explicit
    ' BUILTIN\Users : Modify ACE (the same one tools\install.ps1 sets up front); the
    ' notifier passes False and merely creates the folder if nobody has yet.
    '
    ' The ACE is applied ONLY when we created the directory ourselves: re-asserting
    ' it on every write would be pointless churn, and silently re-widening a
    ' directory whose ACL the user tightened on purpose is not ours to do.
    ' Best-effort throughout; False simply means "no counters this time".
    Friend Function EnsureDirFor(ByVal filePath As String, ByVal grantUsersModify As Boolean) As Boolean
        Try
            If filePath Is Nothing OrElse filePath = "" Then Return False
            Dim dir As String = Path.GetDirectoryName(filePath)
            If dir Is Nothing OrElse dir = "" Then Return False
            If Directory.Exists(dir) Then Return True
            Directory.CreateDirectory(dir)
            If grantUsersModify Then AddUsersModifyAce(dir)
            Return Directory.Exists(dir)
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' Add BUILTIN\Users : Modify (inherited by children) to a directory. Uses the
    ' well-known SID rather than the name "Users" so it is correct on a
    ' non-English Windows. Best-effort; never throws.
    Private Function AddUsersModifyAce(ByVal dir As String) As Boolean
        Try
            Dim di As New DirectoryInfo(dir)
            Dim acl As DirectorySecurity = di.GetAccessControl()
            Dim users As New SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, Nothing)
            acl.AddAccessRule(New FileSystemAccessRule(users,
                                                       FileSystemRights.Modify,
                                                       InheritanceFlags.ObjectInherit Or InheritanceFlags.ContainerInherit,
                                                       PropagationFlags.None,
                                                       AccessControlType.Allow))
            di.SetAccessControl(acl)
            Return True
        Catch ex As Exception
            Return False
        End Try
    End Function

    ' ------------------------------------------------------------------
    ' private helpers
    ' ------------------------------------------------------------------

    Private Sub AddInto(ByVal c As Counts, ByVal kills As Long, ByVal redirects As Long, ByVal armedSeconds As Long)
        c.Kills += kills
        c.Redirects += redirects
        c.ArmedSeconds += armedSeconds
    End Sub

    ' The bucket for a key, created on first sight.
    Private Function BucketFor(ByVal map As Dictionary(Of String, Counts), ByVal key As String) As Counts
        Dim c As Counts = Nothing
        If map.TryGetValue(key, c) Then Return c
        c = New Counts()
        map(key) = c
        Return c
    End Function

    Private Sub MergeInto(ByVal dest As StatsData, ByVal src As StatsData)
        If src Is Nothing Then Return
        AddInto(dest.Lifetime, src.Lifetime.Kills, src.Lifetime.Redirects, src.Lifetime.ArmedSeconds)
        For Each kv As KeyValuePair(Of String, Counts) In src.PerSlot
            AddInto(BucketFor(dest.PerSlot, kv.Key), kv.Value.Kills, kv.Value.Redirects, kv.Value.ArmedSeconds)
        Next
        For Each kv As KeyValuePair(Of String, Counts) In src.Days
            AddInto(BucketFor(dest.Days, kv.Key), kv.Value.Kills, kv.Value.Redirects, kv.Value.ArmedSeconds)
        Next
    End Sub

    ' A stored counter: any non-integer, negative or overflowing text reads as 0.
    ' NEGATIVE IS REJECTED, not preserved: a hand-edited "-999999" would otherwise
    ' let a tampered file drive a displayed total below the truth, and a counter
    ' that can go backwards is a counter nobody can read.
    Private Function ParseCount(ByVal raw As String) As Long
        Dim v As Long
        If Not Long.TryParse(If(raw, "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, v) Then Return 0
        If v < 0 Then Return 0
        Return v
    End Function

    Private Function Render(ByVal v As Long) As String
        If v < 0 Then Return "0"
        Return v.ToString(CultureInfo.InvariantCulture)
    End Function

    ' "<armedSeconds>|<kills>|<redirects>". False (skip the day entirely) unless
    ' there are exactly three fields; individual unparseable fields read as 0.
    Private Function TryParseDayValue(ByVal raw As String,
                                      ByRef armedSeconds As Long,
                                      ByRef kills As Long,
                                      ByRef redirects As Long) As Boolean
        armedSeconds = 0
        kills = 0
        redirects = 0
        If raw Is Nothing Then Return False
        Dim parts() As String = raw.Split(DayFieldSep)
        If parts.Length <> 3 Then Return False
        armedSeconds = ParseCount(parts(0))
        kills = ParseCount(parts(1))
        redirects = ParseCount(parts(2))
        Return True
    End Function

End Module
