---
name: dotnet-sdk-install-build-server-lock
description: Installing a new .NET SDK into C:\Users\samra\.dotnet fails while MSBuild/Roslyn build-server nodes hold dotnet.exe - shut them down first
metadata:
  type: project
---

`dotnet-install.ps1 -InstallDir C:\Users\samra\.dotnet` fails at the extract step with
"The process cannot access the file 'C:\Users\samra\.dotnet\dotnet.exe' because it is being
used by another process" if any MSBuild / VB-C# compiler server nodes from an earlier build
are still alive (they persist for ~15 min after a build, as `dotnet.exe` processes).

**Why:** node reuse keeps build-server processes resident after `dotnet build`/`test` returns,
and they hold a lock on the very `dotnet.exe` the installer overwrites. Hit during the S0b
.NET 10 retarget (12/08/2026) - five leftover nodes from the S0 build.

**How to apply:** run `C:\Users\samra\.dotnet\dotnet.exe build-server shutdown` and confirm no
user-scoped `dotnet` processes remain before re-running the installer. These nodes are build
daemons, never MonkMode binaries, so stopping them touches nothing the never-run fence protects.

Related: PATH `dotnet` is `C:\Program Files\dotnet\dotnet.exe` and has NO SDK at all, which is
why `tools\build-dist.ps1` discovery correctly falls through to the user-scoped install.
