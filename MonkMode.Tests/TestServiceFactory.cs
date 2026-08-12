// Copyright (C) 2026 Samrath Singh
//
// This file is part of MonkMode, a fork of Cold Turkey.
// Source: https://github.com/samrathsingh302/monkmode
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

// MonkMode.Tests - the ONE way a test may construct a Service1.
//
// THE LANDMINE THIS DEFUSES. Service1.InitializeComponent sets `timer.Enabled = True`, so
// simply CONSTRUCTING a Service1 starts the live 10-second ENFORCEMENT TICK on a threadpool
// thread. In production that is exactly right - a Service1 is only ever constructed in order
// to be Run. In a unit-test process it is not:
//
//   * the tick reads and WRITES Application.StartupPath\monkmode_settings.ini, which under
//     `dotnet test` is the test-bin config every CLI-writer test arms and asserts over. A
//     missing or unreadable one sends it down RecoverPrimaryConfig -> WriteDefaultBlock,
//     which stamps a brand-new default block over it;
//   * it re-asserts the read-only attribute on the REAL hosts file every beat;
//   * had it ever read a held block it would run the self-heals - the guardian spawn, the
//     SafeBoot keys, the DoH policy and the deny-DELETE ACE.
//
// Every constructed instance ticks for the life of the test process, and they accumulate.
// This was caught the hard way: a stray heartbeat rewrote [CurrentTime] Now underneath
// SlotArmTests' "a second arm never re-seeds the monotonic frame" assertion, which failed
// deterministically once enough instances existed - a test reporting a config-frame bug that
// the product did not have.
//
// So: no test constructs a Service1 directly. This factory stops the timer the instant the
// instance exists, which is the narrowest fix that keeps production's arming behaviour
// completely untouched. (Whether PRODUCTION should arm its timer at the end of OnStart
// rather than at construction is a separate question, raised for the enforcement-core gate
// rather than settled here.)

namespace MonkMode.Tests;

internal static class TestSvc
{
    internal static monkmode.Service1 New()
    {
        // The ONLY direct construction in the whole test assembly.
        var svc = new monkmode.Service1();
        svc.StopTimerForTests();
        return svc;
    }
}
