---
name: powershell-setcontent-corrupts-vb-sources
description: Never script monk-mode .vb/.cs edits through PowerShell Get-Content/Set-Content (BOM + mangled non-ASCII), a Python read/write, or sed -i (both flatten CRLF to LF); use the Edit tool or Copy-Item
metadata:
  type: feedback
---

Do not round-trip monk-mode source files through PowerShell `Get-Content` → `Set-Content`
(or `Out-File`) to script a multi-file edit. Use the Edit tool per file, or `Copy-Item`
when the whole file is being duplicated (that is byte-exact and safe).

**Why:** Windows PowerShell 5.1 reads a BOM-less file as ANSI and writes UTF-8 **with** a
BOM. On 12/08/2026 (slice S1) a scripted splice of the four `CanonicalFromIni` wrappers
added `efbbbf` to three files and turned `§4C` into `Â§4C` inside unrelated comments —
invisible in the intended hunk, obvious only in `git diff --stat` (the line counts came out
far larger than the replacement) and in a `head -c3 | xxd` BOM check. Recovered with
`git checkout --` on the three files.

The same trap has a **Python** shape: a `io.open(p).read()` / `write()` round-trip in text
mode flattens the file's CRLF endings to LF (universal newlines on read, `newline=''` on
write emits what it was given). On 19/08/2026 (FX7) a scripted 5-replacement pass over
`AppDomainBackstopTests.cs` converted the whole file to LF while landing only 2 of the
replacements; git hid it (`core.autocrlf=true` normalises the diff), so the tell was the
"LF will be replaced by CRLF" warning plus a suspiciously small `--stat`. Recovered with
`git checkout --` and redone as 5 Edit calls. Verify endings by BYTES, not with
`grep -vc $'\r'` (under Git Bash that reported 0 on a file that was entirely LF, 30/08/2026):
`python -c "b=open(F,'rb').read(); print(b.count(b'\n')-b.count(b'\r\n'))"`.

The Python shape has a **second, independent trap that `newline=''` does not cover**: the
codec. On 30/08/2026 (ledger 319 follow-up) a two-site replace on `MM_guard/Program.vb` used
`newline=''` correctly — endings survived — but `encoding='utf-8-sig'` on the **write**, which
ADDS `efbbbf` to a file that had no BOM. It showed as a phantom first-line change in
`git diff` (`-'    Copyright` / `+﻿'    Copyright`). `utf-8-sig` is only safe to *read*
with; write back with plain `utf-8`. Simpler rule: do not script source edits at all — that
pass should have been two Edit calls.

**CORRECTION (30/08/2026): LF endings are NOT by themselves evidence of corruption in this
repo.** Its working tree is genuinely MIXED — 64 CRLF vs 27 LF source files with nothing
touched — and `core.autocrlf=true` normalises every blob to LF, so a whole-file ending flip is
invisible to git and harmless to MSBuild. The "LF will be replaced by CRLF" warning is
therefore a *hint* to look, not a finding: the real tell of a scripted-edit accident is a
`git diff --stat` line count that does not match the hunks you intended, or a BOM. Do not run a
CRLF normalisation pass to "fix" the warning (ledger 313 did, on four files that were already
LF — zero-diff, but it rewrote bytes outside the slice for no gain). Files created with the
Write tool come out LF-only; that is fine, and matches 27 files already in the tree.

A third shape: **`sed -i` in the Bash tool**. On 20/08/2026 (FX9) a one-line `sed -i`
mutation-test edit on `MM_notify/BlockPage.vb` flattened the *whole file* CRLF→LF (GNU sed
rewrites the file from its own LF-normalised buffer). Same tells: the "LF will be replaced
by CRLF" warning and `file <path>` losing "with CRLF line terminators". Recovered with
`git checkout --` and the addition re-applied with one Edit call. **Mutation tests are not
an exception** — do the mutation with the Edit tool too, and revert it the same way.

**How to apply:** whenever tempted to script an identical edit across the four
project copies. `Copy-Item` for whole-file duplication (ConfigIntegrity.vb / IniFile.vb
four-copy parity) is fine and is the documented method. For a *region* edit in four files,
do four Edit calls with identical new text, then PROVE identity by extracting the function
bodies and `diff`-ing them — that catches the divergence the script was meant to prevent,
without the encoding risk. Sanity check after any bulk edit: `git diff --stat` line counts
should match what you intended, and `head -c3 <file> | xxd` should match `git show HEAD:<file>`.

Related: [[dotnet-sdk-install-build-server-lock]]
