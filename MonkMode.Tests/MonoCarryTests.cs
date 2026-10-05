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

// MonkMode.Tests - A5 (05/10 audit): the sub-second monotonic remainder is carried.
//
// The tick used to credit (nowMono - lastMonoMs) \ 1000 and then set lastMonoMs =
// nowMono, throwing the sub-second remainder away every tick: ticks alternating
// 9 984 / 10 016 ms credited 9 + 10 per 20 s, so an hour of real time advanced
// HighWater by only 3 420 s and the block over-ran. Service1.TakeWholeMonoSeconds
// advances the anchor by exactly result x 1000 ms, so the fraction carries. B4 is
// the bound pinned last: the summed credit never exceeds floor(real elapsed / 1000).
// Pure - no timers, no sleeps, no files.

using System;
using Xunit;

public class MonoCarryTests
{
    private const long Seed = 1_000_000L;

    private static long Take(ref long anchor, long now) =>
        monkmode.Service1.TakeWholeMonoSeconds(ref anchor, now);

    [Fact]
    public void FirstTick_SeedsAnchorAndCreditsZero()
    {
        long anchor = 0;
        Assert.Equal(0L, Take(ref anchor, Seed));
        Assert.Equal(Seed, anchor);
    }

    [Fact]
    public void NegativeAnchor_IsTreatedAsUnseeded()
    {
        long anchor = -5;
        Assert.Equal(0L, Take(ref anchor, Seed));
        Assert.Equal(Seed, anchor);
    }

    [Fact]
    public void AlternatingTicks_OverAnHour_CreditTheWholeHour()
    {
        long anchor = 0;
        long now = Seed;
        Take(ref anchor, now);
        long credited = 0;
        for (int i = 0; i < 360; i++)
        {
            now += (i % 2 == 0) ? 9_984L : 10_016L;
            credited += Take(ref anchor, now);
        }
        // 360 ticks = exactly 3 600 000 ms; the discarding arithmetic gave 3 420.
        Assert.True(credited >= 3_599L, $"credited {credited} s over 3600 s of real time");
        Assert.True(credited <= 3_600L, $"credited {credited} s - more than real time");
    }

    [Fact]
    public void Tick9999_CreditsNineAndCarries999_ThenOneMsTickCreditsOne()
    {
        long anchor = Seed;
        Assert.Equal(9L, Take(ref anchor, Seed + 9_999L));
        Assert.Equal(Seed + 9_000L, anchor);   // 999 ms carried
        Assert.Equal(1L, Take(ref anchor, Seed + 10_000L));
        Assert.Equal(Seed + 10_000L, anchor);  // carry 0
    }

    [Fact]
    public void Tick25000_CreditsTwentyFiveAndCarriesZero()
    {
        long anchor = Seed;
        Assert.Equal(25L, Take(ref anchor, Seed + 25_000L));
        Assert.Equal(Seed + 25_000L, anchor);
    }

    [Fact]
    public void BackwardsNow_CreditsZero_AnchorUnchanged()
    {
        long anchor = Seed;
        Assert.Equal(0L, Take(ref anchor, Seed - 500L));
        Assert.Equal(Seed, anchor);
    }

    [Fact]
    public void ZeroDelta_CreditsZero_AnchorUnchanged()
    {
        long anchor = Seed;
        Assert.Equal(0L, Take(ref anchor, Seed));
        Assert.Equal(Seed, anchor);
    }

    [Fact]
    public void CumulativeCredit_NeverExceedsRealElapsed_NeverBelowItByMoreThanOne()
    {
        var rng = new Random(20261005);
        for (int run = 0; run < 200; run++)
        {
            long anchor = Seed;
            long now = Seed;
            long credited = 0;
            int ticks = rng.Next(1, 500);
            for (int i = 0; i < ticks; i++)
            {
                now += rng.Next(1, 30_000);
                credited += Take(ref anchor, now);
                long floorSeconds = (now - Seed) / 1000L;
                Assert.True(credited <= floorSeconds, $"run {run} tick {i}: credited {credited} > floor {floorSeconds}");
                Assert.True(credited >= floorSeconds - 1L, $"run {run} tick {i}: credited {credited} < floor {floorSeconds} - 1");
            }
        }
    }
}
