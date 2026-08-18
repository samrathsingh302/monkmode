# Third-party licences

MonkMode itself is GPLv3 (see COPYING and NOTICE). Built/published output ships
the following MIT-licensed NuGet assemblies alongside the MonkMode executables
(no third-party SOURCE is in the tree — see NOTICE):

- **Microsoft.Toolkit.Uwp.Notifications 7.1.3** — Copyright (c) .NET Foundation
  and Contributors
- **Microsoft.Windows.SDK.NET / WinRT.Runtime** (C#/WinRT projection) —
  Copyright (c) Microsoft Corporation

A **self-contained** build (`tools\build-dist.ps1 -SelfContained`, the payload
`tools\install.ps1` deploys) additionally bundles the .NET desktop runtime. Since
18/08/2026 that bundle includes the WPF / UI-Automation half of it, because the
notifier's URL watcher reads the browser address bar through managed UI
Automation (`System.Windows.Automation`, in `UIAutomationClient` /
`UIAutomationTypes`) — see NOTICE for the exact file delta:

- **Microsoft.WindowsDesktop.App** (.NET 10 desktop runtime: `UIAutomation*`,
  `PresentationCore`, `PresentationFramework*`, `System.Xaml`, …) — Copyright (c)
  .NET Foundation and Contributors

A framework-dependent build ships none of these — they come from the .NET 10
desktop runtime installed on the machine.

All of the above are distributed under the MIT License:

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The MIT licence is GPLv3-compatible; the combined distributed work remains
GPLv3.
