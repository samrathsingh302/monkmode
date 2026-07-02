'    MonkMode - ConfigIntegrity (B7: tamper-evident config)
'
'    An HMAC-SHA256 "integrity" MAC over the decrypted config values, on top of
'    the (documented-weak, B7-owned) Simple3Des layer. The threat it closes:
'    an attacker who recovers the hardcoded 3DES key can re-encrypt [Time] Until
'    to "now" and write it into monkmode_settings.ini to end a block early. With
'    the MAC, that edit no longer verifies, and the readers (service/guardian/
'    notifier) treat a MAC-invalid config as STILL ACTIVE (fail CLOSED) - so the
'    block never auto-lifts until the config is legitimately re-stamped, exactly
'    the tamper-resistant direction every other gate takes.
'
'    The HMAC key is a per-block random 32 bytes, DPAPI-protected at machine
'    scope ([Integrity] Key) so it is not a second hardcoded secret an attacker
'    can lift from the binaries; the MAC itself is [Integrity] Mac. Both keys are
'    EXCLUDED from the canonical (you can't MAC the MAC).
'
'    This file is byte-for-byte identical across all four projects (CLI,
'    service, guardian, notifier), like the Simple3Des copies - the unit tests
'    pin that 4-copy parity. Only the RootNamespace differs (set per project).
'
'    Split of concerns:
'      - BuildCanonical / ComputeConfigMac / ConfigMacIsValid are PURE
'        (filesystem/DPAPI-free) and fully unit-tested; ConfigMacIsValid is the
'        fail-closed gate and NEVER throws (False on any bad/blank/non-Base64
'        stored MAC).
'      - NewRandomKey / ProtectKey / UnprotectKey are the DPAPI live seam (not
'        unit-tested - they hit the real DPAPI; smoke-tested later). A DPAPI
'        failure on a reader must read as MAC-INVALID -> fail closed, never as
'        "lift", so UnprotectKey returns Nothing on failure and the caller maps
'        a Nothing key to "MAC invalid".
'
'    This file is part of MonkMode (GPLv3).

Option Explicit On
Option Strict Off

Imports System.Security.Cryptography
Imports System.Text

Friend Module ConfigIntegrity

    ' The current config schema version - the FIRST line of the canonical below,
    ' so it is MAC-covered. Bumping it makes every config stamped under an OLDER
    ' schema fail verification on the new binaries; because the readers fail
    ' CLOSED on an invalid MAC, a pre-upgrade block then FREEZES rather than being
    ' silently accepted or auto-lifted (R9 forward-migration freeze). It is a
    ' COMPILE-TIME constant every party bakes in from its byte-identical copy,
    ' never read from the ini: a config-DECLARED version would let a doctored old
    ' config re-declare its own version and re-validate, so the version an evader
    ' is measured against must be the CODE's, not the file's. Operational rule
    ' this implies: arm blocks AFTER upgrading the binaries, not ACROSS an upgrade
    ' (an in-flight block armed by the previous schema correctly freezes until it
    ' is re-armed). History: v1 (pre-B4) -> v2 (B4: added HighWater) -> v3
    ' (Section C accountability core). The four copies MUST share this value or
    ' the parties would stamp/verify different tags and every block would freeze;
    ' AllFourCopies_ShareTheSameSchemaVersion pins that.
    Friend Const CurrentSchemaVersion As String = "v3"

    ' The canonical string the MAC is computed over: the schema version tag plus
    ' one Key=Value line per protected field, vbLf-separated, in a FIXED order.
    ' Every party (CLI writer, service/guardian/notifier readers) builds this from
    ' the DECRYPTED plaintext values and supplies CurrentSchemaVersion for the tag,
    ' so the input is byte-identical regardless of the ciphertext or who wrote it.
    ' "null"/"" pass through as-is - the point is a stable, reproducible input, not
    ' interpretation. [Integrity] Key and [Integrity] Mac are deliberately NOT part
    ' of this (you can't MAC the MAC).
    '
    ' B4 (clock-rollback hardening): the canonical includes HighWater - the
    ' monotonic high-water mark (an en-CA LOCAL datetime, same format as Until)
    ' that the service advances at most one tick at a time and never on a clock
    ' jump. It MUST be MAC-covered so an attacker who recovered the 3DES key can't
    ' forge a HighWater past Until to fake an expiry; covering it here is what
    ' couples the value to the MAC. HighWater sits right after Until (the two are
    ' paired: Until is the target, HighWater the trusted clock measured against it).
    '
    ' The version is a caller-supplied PARAMETER (not a hardcoded literal) so it
    ' rides the same wrapper-supplied contract as every other field: each
    ' CanonicalFromIni wrapper passes CurrentSchemaVersion exactly as it passes the
    ' decrypted Until/HighWater/ProcessList/Now - the uniform shape every Section-C
    ' field will slot into. A bump is then one edit to CurrentSchemaVersion, pinned
    ' loudly by the 4-copy parity + format tests.
    Friend Function BuildCanonical(ByVal schemaVersion As String, ByVal until As String, ByVal processList As String, ByVal customSites As String, ByVal now As String, ByVal highWater As String) As String
        Return schemaVersion & vbLf &
               "Until=" & until & vbLf &
               "HighWater=" & highWater & vbLf &
               "ProcessList=" & processList & vbLf &
               "CustomSites=" & customSites & vbLf &
               "Now=" & now & vbLf
    End Function

    ' HMAC-SHA256 of the canonical (Unicode bytes), Base64-encoded. The key is
    ' the raw 32-byte block key (post-DPAPI-unprotect on a reader).
    Friend Function ComputeConfigMac(ByVal canonical As String, ByVal key As Byte()) As String
        Using h As New HMACSHA256(key)
            Dim mac() As Byte = h.ComputeHash(Encoding.Unicode.GetBytes(canonical))
            Return Convert.ToBase64String(mac)
        End Using
    End Function

    ' The fail-closed verification gate. Recompute the MAC over canonical and
    ' compare it to storedMacB64 in constant time (CryptographicOperations.
    ' FixedTimeEquals over the raw bytes). Returns False - never throws - on a
    ' null/blank/non-Base64 stored MAC, so a tampered or absent [Integrity] Mac
    ' reads as "invalid" and the caller keeps the block standing.
    Friend Function ConfigMacIsValid(ByVal canonical As String, ByVal storedMacB64 As String, ByVal key As Byte()) As Boolean
        If String.IsNullOrWhiteSpace(storedMacB64) Then Return False
        Dim storedBytes() As Byte
        Try
            storedBytes = Convert.FromBase64String(storedMacB64)
        Catch ef As System.FormatException
            Return False
        End Try
        Try
            Using h As New HMACSHA256(key)
                Dim expected() As Byte = h.ComputeHash(Encoding.Unicode.GetBytes(canonical))
                Return CryptographicOperations.FixedTimeEquals(expected, storedBytes)
            End Using
        Catch ex As Exception
            ' A null key (or any other crypto hiccup) reads as MAC-invalid, not
            ' as a lift - fail closed.
            Return False
        End Try
    End Function

    ' ---- DPAPI key management (live seam - NOT unit-tested) ----

    ' A fresh per-block HMAC key. 32 bytes of CSPRNG output.
    Friend Function NewRandomKey() As Byte()
        Return RandomNumberGenerator.GetBytes(32)
    End Function

    ' DPAPI-protect the raw key at MACHINE scope and return it Base64 (the
    ' [Integrity] Key value). Machine scope (decision locked) so the LocalSystem
    ' service, the SYSTEM guardian and the user-session notifier/CLI can all
    ' unprotect it. Returns Nothing on failure so the caller can degrade
    ' gracefully (a DPAPI failure must NOT abort arming the block).
    Friend Function ProtectKey(ByVal key As Byte()) As String
        Try
            Dim blob() As Byte = ProtectedData.Protect(key, Nothing, DataProtectionScope.LocalMachine)
            Return Convert.ToBase64String(blob)
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    ' Reverse of ProtectKey: Base64 protected blob -> raw key bytes. Returns
    ' Nothing on any failure (blank/non-Base64 blob, DPAPI denial, blob written
    ' by a different machine). The caller MUST map a Nothing key to "MAC
    ' invalid" -> fail closed (block stands), never to "lift".
    Friend Function UnprotectKey(ByVal protectedB64 As String) As Byte()
        If String.IsNullOrWhiteSpace(protectedB64) Then Return Nothing
        Try
            Dim blob() As Byte = Convert.FromBase64String(protectedB64)
            Return ProtectedData.Unprotect(blob, Nothing, DataProtectionScope.LocalMachine)
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

End Module
