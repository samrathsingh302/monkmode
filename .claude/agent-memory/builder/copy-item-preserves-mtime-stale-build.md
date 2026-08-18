---
name: copy-item-preserves-mtime-stale-build
description: PowerShell Copy-Item preserves LastWriteTime, so MSBuild skips rebuilding a parity copy and tests silently run against a STALE assembly - touch the file after copying
metadata:
  type: feedback
---

After creating a byte-identical parity copy with `Copy-Item` (the ConfigIntegrity /
IniFile / Simple3Des / StatsSidecar pattern in monk-mode), **set the destination's
`LastWriteTime` to now** before building:

```powershell
Copy-Item $src $dst -Force
(Get-Item $dst).LastWriteTime = Get-Date
```

**Why:** `Copy-Item` copies the SOURCE's timestamps to the destination. If the source
file's mtime is older than the consuming project's last successful build, MSBuild's
incremental check declares that project up to date and does **not** recompile it. The
build log still prints `MonkMode_srv -> ...MonkMode_srv.dll` (it copied, it did not
compile), so nothing looks wrong. Cross-assembly parity tests then pass against an
assembly built from the PREVIOUS version of the copy — the exact drift those tests
exist to catch.

Caught on 18/08/2026 during v1.1 S7b: a source-level byte-identity test passed and the
behavioural parity test passed, yet `monkmode.StatsSidecar.Apply` was still executing a
superseded `EnsureDir()` that created `%ProgramData%\MonkMode` — a breach of the
MonkMode.Tests fence (no test may touch `%ProgramData%\MonkMode`). Only a
`dotnet test` compile error against a method the stale DLL lacked exposed it.

**How to apply:** any monk-mode slice that adds or edits a multi-project parity file.
Touch after copying, and when the change matters, confirm with
`dotnet build MonkMode.sln --no-incremental` (both Debug and Release — the tests run
Debug) before trusting a parity result. A source-hash parity test alone is NOT proof
that the assemblies agree.
