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
`git checkout --` and redone as 5 Edit calls. Verify endings with
`grep -vc $'\r' <file>` — it must be 0 for every repo source file.

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
