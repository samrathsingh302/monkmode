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

'    MonkMode - Stats.vb (D3): block history + `monkmode stats`.
'
'    A DELIBERATELY-SEPARATE, NON-MAC, CLI-ONLY history file (monkmode_stats in AppDir()). It records
'    one best-effort line per block ARM (start, planned-until, site/app COUNTS, committed, cooling-off)
'    so `monkmode stats` can summarise the user's focus history.
'
'    Load-bearing SAFETY property (R5): NOTHING in the enforcement path ever reads this file. The
'    service, the hosts self-heal, the MAC, and DoBlock's arming logic are all independent of it, so a
'    corrupt / deleted / forged / truncated stats file can NEVER lift, shorten, extend, or otherwise
'    perturb a block. Recording is BEST-EFFORT: RecordBlockStart swallows every error and never throws,
'    so a stats-write failure cannot break arming (the caller ignores the result). Reading is TOLERANT:
'    a blank / malformed / unknown-version / unparseable line is skipped, a missing file reads as no
'    history, and ReadRecords never throws. It is NOT MAC-covered - it makes no security claim, so there
'    is nothing to forge; the worst a tampered file does is show wrong numbers in `stats`.
'
'    PRIVACY: only COUNTS of sites/apps are stored, never the domains / exe names themselves - this is a
'    plaintext, unprotected file, so it must never become a place a blocklist (e.g. adult sites) leaks.
'    Durations are PLANNED (start -> planned until); an early code/cooling-off exit is not reflected
'    (recording actual ends would need a service-side write, which would touch the enforcement core -
'    deferred as a later slice).

Imports System.Globalization
Imports System.IO

Module Stats

    Public Const StatsFileName As String = "monkmode_stats"
    ' Per-line record version tag. A line not starting with this exact tag (a future format, a blank
    ' line, or corruption) is skipped on read, so the format can evolve without breaking old readers.
    Private Const RecordTag As String = "mmstat1"
    Private Const FieldSep As Char = "|"c

    Public Function StatsPath() As String
        Return Path.Combine(Blocker.AppDir(), StatsFileName)
    End Function

    ' One recorded block-arm: COUNTS (not the site/app lists) + planned timing only.
    Public Structure BlockStat
        Public Start As DateTime
        Public UntilTime As DateTime
        Public SiteCount As Integer
        Public AppCount As Integer
        Public Committed As Boolean
        Public CoolOffSeconds As Long
        ' Planned duration (never negative). Actual end may be earlier via a code/cooling-off exit,
        ' which is not recorded (see file header).
        Public ReadOnly Property PlannedDuration As TimeSpan
            Get
                If UntilTime <= Start Then Return TimeSpan.Zero
                Return UntilTime - Start
            End Get
        End Property
    End Structure

    ' Append one block-arm record. BEST-EFFORT: returns True if written, but NEVER throws - a failure
    ' here must not break arming (the caller ignores the result). Format (pipe-delimited, one line):
    '   mmstat1|<startO>|<untilO>|<siteCount>|<appCount>|<committed 0/1>|<coolOffSeconds>
    ' DateTimes use the round-trippable "o" format in the invariant culture; no field can contain "|".
    Public Function RecordBlockStart(ByVal start As DateTime, ByVal untilTime As DateTime,
                                     ByVal siteCount As Integer, ByVal appCount As Integer,
                                     ByVal committed As Boolean, ByVal coolOffSeconds As Long) As Boolean
        Try
            Dim fields() As String = {
                RecordTag,
                start.ToString("o", CultureInfo.InvariantCulture),
                untilTime.ToString("o", CultureInfo.InvariantCulture),
                siteCount.ToString(CultureInfo.InvariantCulture),
                appCount.ToString(CultureInfo.InvariantCulture),
                If(committed, "1", "0"),
                coolOffSeconds.ToString(CultureInfo.InvariantCulture)}
            File.AppendAllText(StatsPath(), String.Join(FieldSep.ToString(), fields) & Environment.NewLine)
            Return True
        Catch
            Return False   ' best-effort: never break arming on a stats failure
        End Try
    End Function

    ' Read every well-formed record, in file (oldest-first) order. Tolerant: a missing file => empty
    ' list; a blank, malformed, wrong-version, or unparseable line is SKIPPED (never throws). Only the
    ' display path (`monkmode stats`) calls this - it has no enforcement authority.
    Public Function ReadRecords() As List(Of BlockStat)
        Dim records As New List(Of BlockStat)
        Try
            Dim path As String = StatsPath()
            If Not File.Exists(path) Then Return records
            For Each raw As String In File.ReadAllLines(path)
                Dim rec As BlockStat
                If TryParseRecord(raw, rec) Then records.Add(rec)
            Next
        Catch
            ' A read failure yields whatever parsed so far (or empty) - stats is display-only.
        End Try
        Return records
    End Function

    Private Function TryParseRecord(ByVal raw As String, ByRef rec As BlockStat) As Boolean
        rec = Nothing
        If raw Is Nothing Then Return False
        Dim parts() As String = raw.Split(FieldSep)
        If parts.Length <> 7 Then Return False
        If parts(0) <> RecordTag Then Return False
        Dim start As DateTime, untilTime As DateTime
        Dim siteCount As Integer, appCount As Integer
        Dim coolOff As Long
        If Not DateTime.TryParse(parts(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, start) Then Return False
        If Not DateTime.TryParse(parts(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, untilTime) Then Return False
        If Not Integer.TryParse(parts(3), NumberStyles.Integer, CultureInfo.InvariantCulture, siteCount) Then Return False
        If Not Integer.TryParse(parts(4), NumberStyles.Integer, CultureInfo.InvariantCulture, appCount) Then Return False
        If parts(5) <> "0" AndAlso parts(5) <> "1" Then Return False
        If Not Long.TryParse(parts(6), NumberStyles.Integer, CultureInfo.InvariantCulture, coolOff) Then Return False
        rec.Start = start
        rec.UntilTime = untilTime
        rec.SiteCount = siteCount
        rec.AppCount = appCount
        rec.Committed = (parts(5) = "1")
        rec.CoolOffSeconds = coolOff
        Return True
    End Function

    ' A computed summary over a set of records, evaluated AS OF a caller-supplied instant (passed in,
    ' NOT DateTime.Now, so the summary is deterministic + unit-testable). "Completed" = planned end at
    ' or before asOf; the rest are active/upcoming. TotalPlannedTime/LongestPlannedBlock use the clamped
    ' (never-negative) planned durations.
    Public Structure StatsSummary
        Public HasAny As Boolean
        Public TotalBlocks As Integer
        Public CompletedBlocks As Integer
        Public ActiveOrUpcomingBlocks As Integer
        Public CommittedBlocks As Integer
        Public TotalPlannedTime As TimeSpan
        Public LongestPlannedBlock As TimeSpan
        Public FirstStart As DateTime
        Public LastStart As DateTime
    End Structure

    Public Function SummarizeAsOf(ByVal records As List(Of BlockStat), ByVal asOf As DateTime) As StatsSummary
        Dim s As New StatsSummary
        If records Is Nothing OrElse records.Count = 0 Then Return s
        s.HasAny = True
        s.TotalBlocks = records.Count
        s.TotalPlannedTime = TimeSpan.Zero
        s.LongestPlannedBlock = TimeSpan.Zero
        s.FirstStart = records(0).Start
        s.LastStart = records(0).Start
        For Each r As BlockStat In records
            Dim d As TimeSpan = r.PlannedDuration
            s.TotalPlannedTime += d
            If d > s.LongestPlannedBlock Then s.LongestPlannedBlock = d
            If r.Committed Then s.CommittedBlocks += 1
            If r.UntilTime <= asOf Then
                s.CompletedBlocks += 1
            Else
                s.ActiveOrUpcomingBlocks += 1
            End If
            If r.Start < s.FirstStart Then s.FirstStart = r.Start
            If r.Start > s.LastStart Then s.LastStart = r.Start
        Next
        Return s
    End Function

End Module
