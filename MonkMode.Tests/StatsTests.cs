// MonkMode.Tests - D3: block history / `monkmode stats` (Stats.vb).
//
// Stats is a DELIBERATELY-SEPARATE, NON-MAC, CLI-ONLY history file (monkmode_stats). One best-effort
// line per block arm (start, planned-until, site/app COUNTS, committed, cooloff). Load-bearing safety
// (R5): nothing in the enforcement path reads it, recording never throws (can't break arming), and
// reading is tolerant (a corrupt/unknown line is skipped, a missing file = no history). Privacy: only
// COUNTS are stored, never the domains/exe names. These tests pin the round-trip, the read-tolerance,
// and the deterministic SummarizeAsOf (asOf passed IN, not DateTime.Now) - all on the test-bin file.
// DateTimes are Kind-Unspecified so the "o" round-trip is exact regardless of the machine's timezone.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonkMode.Tests;

public class StatsTests
{
    private static void WipeStats()
    {
        try { if (File.Exists(MonkMode.Stats.StatsPath())) File.Delete(MonkMode.Stats.StatsPath()); }
        catch { /* best-effort */ }
    }

    private static MonkMode.Stats.BlockStat Rec(DateTime start, DateTime until, int sites, int apps, bool committed, long cool)
        => new MonkMode.Stats.BlockStat
        {
            Start = start, UntilTime = until, SiteCount = sites, AppCount = apps, Committed = committed, CoolOffSeconds = cool
        };

    [Fact]
    public void RecordThenRead_RoundTripsAllFields()
    {
        WipeStats();
        try
        {
            var start = new DateTime(2026, 7, 1, 9, 0, 0);
            var until = new DateTime(2026, 7, 1, 11, 30, 0);
            Assert.True(MonkMode.Stats.RecordBlockStart(start, until, 3, 2, true, 7200));

            var recs = MonkMode.Stats.ReadRecords();
            Assert.Single(recs);
            Assert.Equal(start, recs[0].Start);
            Assert.Equal(until, recs[0].UntilTime);
            Assert.Equal(3, recs[0].SiteCount);
            Assert.Equal(2, recs[0].AppCount);
            Assert.True(recs[0].Committed);
            Assert.Equal(7200L, recs[0].CoolOffSeconds);
            Assert.Equal(TimeSpan.FromMinutes(150), recs[0].PlannedDuration);
        }
        finally { WipeStats(); }
    }

    [Fact]
    public void MultipleRecords_AppendInFileOrder()
    {
        WipeStats();
        try
        {
            MonkMode.Stats.RecordBlockStart(new DateTime(2026, 7, 1, 9, 0, 0), new DateTime(2026, 7, 1, 10, 0, 0), 1, 0, false, 0);
            MonkMode.Stats.RecordBlockStart(new DateTime(2026, 7, 2, 9, 0, 0), new DateTime(2026, 7, 2, 12, 0, 0), 5, 1, true, 3600);

            var recs = MonkMode.Stats.ReadRecords();
            Assert.Equal(2, recs.Count);
            Assert.Equal(1, recs[0].SiteCount);   // oldest first (append order)
            Assert.Equal(5, recs[1].SiteCount);
        }
        finally { WipeStats(); }
    }

    [Fact]
    public void MissingFile_ReadsEmpty_NeverThrows()
    {
        WipeStats();   // ensure absent
        Assert.Empty(MonkMode.Stats.ReadRecords());
    }

    [Fact]
    public void CorruptAndUnknownLines_AreSkipped_GoodLinesSurvive()
    {
        WipeStats();
        try
        {
            // Hand-write a mixed file: a good record, a blank line, garbage, a wrong-field-count line, an
            // unknown-version line, an unparseable-fields line, then another good record. Only the two
            // well-formed mmstat1 lines survive; nothing throws. This is the "corrupt stats never breaks
            // anything" property, isolated to the read path.
            var good1 = "mmstat1|" + new DateTime(2026, 7, 1, 9, 0, 0).ToString("o") + "|" + new DateTime(2026, 7, 1, 10, 0, 0).ToString("o") + "|2|1|0|0";
            var good2 = "mmstat1|" + new DateTime(2026, 7, 3, 9, 0, 0).ToString("o") + "|" + new DateTime(2026, 7, 3, 11, 0, 0).ToString("o") + "|4|0|1|3600";
            var lines = new[]
            {
                good1,
                "",
                "total garbage with no separators",
                "mmstat1|only|three|fields",                                                              // wrong field count
                "mmstat2|" + new DateTime(2026, 7, 2).ToString("o") + "|" + new DateTime(2026, 7, 2).ToString("o") + "|1|1|0|0",  // unknown version
                "mmstat1|not-a-date|also-bad|x|y|9|z",                                                    // unparseable fields
                good2,
            };
            File.WriteAllLines(MonkMode.Stats.StatsPath(), lines);

            var recs = MonkMode.Stats.ReadRecords();
            Assert.Equal(2, recs.Count);
            Assert.Equal(2, recs[0].SiteCount);
            Assert.Equal(4, recs[1].SiteCount);
            Assert.True(recs[1].Committed);
        }
        finally { WipeStats(); }
    }

    [Fact]
    public void BadCommittedFlag_IsRejected()
    {
        WipeStats();
        try
        {
            // The committed field must be exactly "0" or "1"; anything else fails the record (defensive).
            File.WriteAllLines(MonkMode.Stats.StatsPath(), new[]
            {
                "mmstat1|" + new DateTime(2026, 7, 1, 9, 0, 0).ToString("o") + "|" + new DateTime(2026, 7, 1, 10, 0, 0).ToString("o") + "|1|0|2|0",
            });
            Assert.Empty(MonkMode.Stats.ReadRecords());
        }
        finally { WipeStats(); }
    }

    [Fact]
    public void PlannedDuration_ClampsNegativeToZero()
    {
        // A record whose until precedes its start (clock weirdness / manual edit) => zero, never negative.
        var r = Rec(new DateTime(2026, 7, 1, 12, 0, 0), new DateTime(2026, 7, 1, 11, 0, 0), 1, 0, false, 0);
        Assert.Equal(TimeSpan.Zero, r.PlannedDuration);
    }

    [Fact]
    public void Summarize_Empty_HasAnyFalse_AllZero()
    {
        var s = MonkMode.Stats.SummarizeAsOf(new List<MonkMode.Stats.BlockStat>(), new DateTime(2026, 7, 7));
        Assert.False(s.HasAny);
        Assert.Equal(0, s.TotalBlocks);
        Assert.Equal(TimeSpan.Zero, s.TotalPlannedTime);
    }

    [Fact]
    public void Summarize_Null_HasAnyFalse()
    {
        var s = MonkMode.Stats.SummarizeAsOf(null, new DateTime(2026, 7, 7));
        Assert.False(s.HasAny);
        Assert.Equal(0, s.TotalBlocks);
    }

    [Fact]
    public void Summarize_TotalsCommittedLongestFirstLast()
    {
        var recs = new List<MonkMode.Stats.BlockStat>
        {
            Rec(new DateTime(2026, 6, 1, 9, 0, 0), new DateTime(2026, 6, 1, 10, 0, 0), 2, 0, false, 0),    // 1h
            Rec(new DateTime(2026, 6, 5, 9, 0, 0), new DateTime(2026, 6, 5, 13, 0, 0), 1, 1, true, 3600),  // 4h, committed
            Rec(new DateTime(2026, 6, 3, 9, 0, 0), new DateTime(2026, 6, 3, 11, 30, 0), 3, 2, false, 0),   // 2h30
        };
        var s = MonkMode.Stats.SummarizeAsOf(recs, new DateTime(2026, 7, 7));
        Assert.True(s.HasAny);
        Assert.Equal(3, s.TotalBlocks);
        Assert.Equal(1, s.CommittedBlocks);
        Assert.Equal(TimeSpan.FromHours(7.5), s.TotalPlannedTime);       // 1 + 4 + 2.5
        Assert.Equal(TimeSpan.FromHours(4), s.LongestPlannedBlock);
        Assert.Equal(new DateTime(2026, 6, 1, 9, 0, 0), s.FirstStart);   // earliest start (not file order)
        Assert.Equal(new DateTime(2026, 6, 5, 9, 0, 0), s.LastStart);    // latest start
    }

    [Fact]
    public void Summarize_CompletedVsActive_ByAsOf()
    {
        var recs = new List<MonkMode.Stats.BlockStat>
        {
            Rec(new DateTime(2026, 6, 1, 9, 0, 0), new DateTime(2026, 6, 1, 10, 0, 0), 1, 0, false, 0),   // ended before asOf
            Rec(new DateTime(2026, 7, 7, 8, 0, 0), new DateTime(2026, 7, 7, 23, 0, 0), 1, 0, false, 0),   // ends after asOf
        };
        var asOf = new DateTime(2026, 7, 7, 12, 0, 0);
        var s = MonkMode.Stats.SummarizeAsOf(recs, asOf);
        Assert.Equal(1, s.CompletedBlocks);
        Assert.Equal(1, s.ActiveOrUpcomingBlocks);
    }

    [Fact]
    public void RecordBlockStart_ReturnsTrue_OnNormalWrite_CreatesFile()
    {
        WipeStats();
        try
        {
            Assert.True(MonkMode.Stats.RecordBlockStart(new DateTime(2026, 7, 7, 1, 0, 0), new DateTime(2026, 7, 7, 2, 0, 0), 1, 0, false, 0));
            Assert.True(File.Exists(MonkMode.Stats.StatsPath()));
        }
        finally { WipeStats(); }
    }
}
