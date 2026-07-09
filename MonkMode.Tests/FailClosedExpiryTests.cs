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

// MonkMode.Tests - fail-CLOSED handling of unparseable persisted end times.
//
// Both expiry deciders used to call DateTime.TryParse without checking the
// return value, leaving DateTime.MinValue on failure:
//   - the service treated MinValue as "expired" and lifted the block
//     (stopMe()) - a legacy machine-locale ini written pre-culture-fix, or a
//     corrupted-but-decryptable value, silently UNBLOCKED everything;
//   - the notifier's clock-change compensation rewrote [Time] Until with a
//     time derived from the garbage (roughly "now"), ending the block early.
// For a tamper-resistant blocker a bad value must keep the block STANDING.
//
// Everything here is in-memory - no files, registry or service. The crypto
// layer is bypassed on purpose (the helpers take the decrypted plaintext);
// all four DecryptData copies now return "" on bad Base64 (the service's inline
// copy no longer calls End on the process - that availability bypass is fixed).

using System.Globalization;

namespace MonkMode.Tests;

public class ServiceBlockHasExpiredTests
{
    private static readonly CultureInfo EnCa = new("en-CA");

    [Theory]
    [InlineData("25.06.2026 17:04:33")] // legacy de-DE-formatted ini, pre-culture-fix
    [InlineData("25/06/2026 17:04:33")] // legacy en-GB-formatted ini
    [InlineData("not a date")]
    [InlineData("")]                    // e.g. a decrypt that yielded nothing useful
    public void UnparseableUntil_IsNotExpired_BlockStaysStanding(string stored)
    {
        // asOf is far past any plausible end time: if the parse failure leaked
        // through as MinValue, both gates would report "expired" here.
        var asOf = new DateTime(2030, 1, 1, 12, 0, 0);
        Assert.False(monkmode.Service1.BlockHasExpired(stored, asOf, 0), "OnStart gate failed open");
        Assert.False(monkmode.Service1.BlockHasExpired(stored, asOf, 5), "timer gate failed open");
    }

    [Fact]
    public void PastUntil_IsExpired()
    {
        var until = new DateTime(2026, 6, 25, 17, 0, 0);
        var asOf = until.AddMinutes(1);
        Assert.True(monkmode.Service1.BlockHasExpired(until.ToString(EnCa), asOf, 0));
        Assert.True(monkmode.Service1.BlockHasExpired(until.ToString(EnCa), asOf, 5));
    }

    [Fact]
    public void FutureUntil_IsNotExpired()
    {
        var until = new DateTime(2026, 6, 25, 17, 0, 0);
        var asOf = until.AddMinutes(-30);
        Assert.False(monkmode.Service1.BlockHasExpired(until.ToString(EnCa), asOf, 0));
        Assert.False(monkmode.Service1.BlockHasExpired(until.ToString(EnCa), asOf, 5));
    }

    [Fact]
    public void GraceWindow_TimerLiftsWithinFiveSeconds_OnStartDoesNot()
    {
        // Pins the two call sites' thresholds: 4s remaining is inside the
        // timer's 5s grace but not yet expired for OnStart's 0s.
        var until = new DateTime(2026, 6, 25, 17, 0, 0);
        var asOf = until.AddSeconds(-4);
        Assert.True(monkmode.Service1.BlockHasExpired(until.ToString(EnCa), asOf, 5));
        Assert.False(monkmode.Service1.BlockHasExpired(until.ToString(EnCa), asOf, 0));
    }
}

public class NotifierClockCompensationTests
{
    private static readonly CultureInfo EnCa = new("en-CA");

    [Theory]
    [InlineData("25.06.2026 17:04:33", "garbage")] // Until unparseable
    [InlineData("garbage", "2026-06-25 5:04:33 p.m.")] // Now unparseable
    [InlineData("", "")]
    public void EitherValueUnparseable_ReturnsNull_SoStoredUntilIsNotClobbered(string storedNow, string storedUntil)
    {
        // Null tells the caller to skip the [Time] Until rewrite entirely; the
        // old code wrote DateTime.Now + garbage-derived seconds instead.
        Assert.Null(mm_notify.Form1.ComputeCompensatedUntil(storedNow, storedUntil, new DateTime(2026, 6, 25, 12, 0, 0)));
    }

    [Fact]
    public void ParseableValues_PreserveRemainingTimeAcrossTheClockChange()
    {
        // 90 minutes were left when the clock changed; the compensated end
        // time must be 90 minutes after the (new) current time.
        var oldNow = new DateTime(2026, 6, 25, 12, 0, 0);
        var until = oldNow.AddMinutes(90);
        var newClock = new DateTime(2026, 6, 25, 23, 30, 0); // clock rolled forward
        var result = mm_notify.Form1.ComputeCompensatedUntil(oldNow.ToString(EnCa), until.ToString(EnCa), newClock);
        Assert.Equal(newClock.AddMinutes(90), result);
    }

    [Fact]
    public void BackwardClockChange_DoesNotShortenTheBlock()
    {
        // Clock rolled BACKWARD: the naive formula moves Until earlier (shortening
        // the block). Clock-comp must never shorten - clamp to oldUntil.
        var oldNow = new DateTime(2026, 6, 25, 12, 0, 0);
        var until = oldNow.AddMinutes(90);                  // 13:30
        var newClock = new DateTime(2026, 6, 25, 11, 0, 0); // rolled back 1h
        var result = mm_notify.Form1.ComputeCompensatedUntil(oldNow.ToString(EnCa), until.ToString(EnCa), newClock);
        Assert.Equal(until, result); // held at oldUntil, NOT 12:30
    }

    [Fact]
    public void PoisonedNow_PastStoredUntil_DoesNotProducePastUntil_NoEarlyLift()
    {
        // THE 14/06/2026 smoke-test regression. During the -IncludeClockTest drill
        // the service wrote [CurrentTime] Now = the +10min jumped clock, overshooting
        // Until (Now > Until => negative remaining). On the clock RESTORE the notifier
        // computed newUntil = restoredNow + (Until - Now) = a time in the PAST, which
        // the service then read as expired (HighWater >= past-Until) and lifted EARLY.
        // Without the clamp this assertion fails (result is ~17:51:19); with it, held.
        var storedUntil = new DateTime(2026, 6, 25, 18, 1, 19);
        var poisonedNow = storedUntil.AddMinutes(7);   // service advanced Now past Until (18:08:19)
        var restoredNow = storedUntil.AddMinutes(-3);  // clock restored below Until (17:58:19)
        var result = mm_notify.Form1.ComputeCompensatedUntil(
            poisonedNow.ToString(EnCa), storedUntil.ToString(EnCa), restoredNow);
        Assert.Equal(storedUntil, result);
        Assert.True(result >= storedUntil, "clock-comp must never move Until earlier (no early lift)");
    }

    [Fact]
    public void ForwardClockChange_StillExtends_AfterTheClamp()
    {
        // The clamp must not break the legit forward case (block preserves remaining).
        var oldNow = new DateTime(2026, 6, 25, 12, 0, 0);
        var until = oldNow.AddMinutes(90);            // 13:30
        var newClock = oldNow.AddMinutes(30);         // 12:30 (forward 30min)
        var result = mm_notify.Form1.ComputeCompensatedUntil(oldNow.ToString(EnCa), until.ToString(EnCa), newClock);
        Assert.Equal(newClock.AddMinutes(90), result); // 14:00 (extended, > oldUntil)
    }
}
