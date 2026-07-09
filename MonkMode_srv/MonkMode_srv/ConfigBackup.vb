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

'    MonkMode - ConfigBackup (C1b / R8: MAC-covered config shadow copy)
'
'    A tamper-evident BACKUP of monkmode_settings.ini, kept next to the exes/ini.
'    The problem it closes: the service's OnStart + tick corrupt/short-ini paths
'    used to rewrite an UNSTAMPED default block (WriteDefaultBlock), which the
'    readers treat as MAC-invalid and therefore FREEZE - it holds until a manual
'    CLI re-arm. Once R1 removed the unconditional escape, a corrupt config with
'    no escape = trapped, so a corrupt/blanked/short primary must be RESTORED to a
'    real, MAC-valid config instead of frozen into the panic default. This module
'    is that restore's file layer: a byte-copy of the last-known-good config the
'    CLI/service can put back when the primary is found unreadable.
'
'    THE SAFETY CONTRACT (no data loss, both directions) lives in CopyIfSourceValid:
'    a copy only ever happens when the SOURCE is known MAC-valid. So
'      - refresh (primary -> backup) never overwrites a good backup with a corrupt
'        primary, and
'      - restore (backup -> primary) never overwrites the primary with a CORRUPT
'        (or tampered/unstamped) backup.
'    The MAC-validity that produces sourceIsValid is the caller's live DPAPI seam
'    (Blocker/Service1.ConfigMacIsValidForIni); this module is boolean-injected
'    w.r.t. it, so the copy gate is fully unit-testable against temp files with NO
'    DPAPI (exactly like ConfigIntegrity's pure functions take a raw injected key).
'
'    The backup is only consulted on GENUINE corruption (< 2 sections, or a parse/
'    decrypt throw) - NOT on a parseable-but-MAC-invalid (tampered) config, which
'    still freezes per B7. That distinction is deliberate: a tamper must not be
'    silently "recovered" away; only an unreadable primary is restored.
'
'    This file is byte-for-byte identical to the MonkMode_srv copy (only the
'    RootNamespace differs), like the AtomicHosts / Simple3Des / ConfigIntegrity /
'    IniFile copies - the unit tests pin that parity.
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict On

Imports System.IO

Module ConfigBackup

    ' The shadow-copy filename, kept next to monkmode_settings.ini (and the exes).
    ' Single source of truth within each copy; a parity test pins the CLI and
    ' service copies equal (and equal to the ini name + ".bak"), like the SafeBoot
    ' key consts - a drift here would silently point the two halves at different
    ' files and defeat the restore.
    Friend Const BackupFileName As String = "monkmode_settings.ini.bak"

    ' Atomically copy sourcePath's bytes over destPath, but ONLY when the source is
    ' known-good (sourceIsValid). Returns True iff the copy happened.
    '
    ' sourceIsValid is the no-data-loss gate in BOTH directions (see the header):
    ' the caller passes ConfigMacIsValidForIni(<the source, loaded>). A byte copy
    ' (read + atomic write) preserves the source's [Integrity] Key/Mac exactly, so
    ' the copy round-trips the canonical/MAC unchanged - a restored primary is as
    ' MAC-valid as the backup was. AtomicHosts.WriteAtomic owns the crash-safe
    ' temp+rename of the destination (a torn/blanked dest would itself be a
    ' data-loss / fail-open window), so this never leaves a half-written config.
    '
    ' Not a no-op-swallower: a missing source returns False, but a locked source or
    ' a persistently-unrenameable dest propagates out of AtomicHosts - every caller
    ' wraps this in Try (best-effort), exactly like the other hosts/ini writers.
    Friend Function CopyIfSourceValid(ByVal sourcePath As String, ByVal destPath As String, ByVal sourceIsValid As Boolean) As Boolean
        If Not sourceIsValid Then Return False
        If Not File.Exists(sourcePath) Then Return False
        AtomicHosts.WriteAtomic(destPath, File.ReadAllText(sourcePath))
        Return True
    End Function

    ' The STRUCTURAL-corruption predicate the restore-on-corrupt routing keys off:
    ' a loaded primary is "usable" (NO recovery) iff it has >= 2 sections. This is
    ' the pure boundary that ENFORCES invariant B (the B7 tamper-freeze): recovery
    ' fires ONLY on structural corruption (< 2 sections), NOT on MAC validity, so a
    ' full (>= 2-section) config that PARSES but whose MAC is invalid - a classic
    ' tamper (re-encrypted [Time] Until) - is classified USABLE here, takes the
    ' normal path and FREEZES per B7; it is never routed to restore/default. (A
    ' load/parse/decrypt THROW is the caller's SEPARATE corrupt trigger, handled by
    ' its Try/Catch; this predicate is only the section-count dimension.) Pure +
    ' Friend so a unit test pins both the threshold and that load-bearing "a
    ' >= 2-section config is never treated as needs-recovery" contract - the
    ' regression guard the copy-layer tests alone can't give.
    Friend Function PrimaryIsStructurallyUsable(ByVal sectionCount As Integer) As Boolean
        Return sectionCount >= 2
    End Function

End Module
