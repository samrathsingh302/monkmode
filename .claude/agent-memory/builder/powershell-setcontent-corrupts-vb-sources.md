---
name: powershell-setcontent-corrupts-vb-sources
description: Never splice monk-mode .vb/.cs files with PowerShell Get-Content/Set-Content — it adds a UTF-8 BOM and mangles non-ASCII (§ became Â§); use the Edit tool or Copy-Item instead
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

**How to apply:** whenever tempted to script an identical edit across the four
project copies. `Copy-Item` for whole-file duplication (ConfigIntegrity.vb / IniFile.vb
four-copy parity) is fine and is the documented method. For a *region* edit in four files,
do four Edit calls with identical new text, then PROVE identity by extracting the function
bodies and `diff`-ing them — that catches the divergence the script was meant to prevent,
without the encoding risk. Sanity check after any bulk edit: `git diff --stat` line counts
should match what you intended, and `head -c3 <file> | xxd` should match `git show HEAD:<file>`.

Related: [[dotnet-sdk-install-build-server-lock]]
