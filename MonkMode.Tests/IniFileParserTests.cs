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

// MonkMode.Tests - IniFile parser semantics (clean-room replacement, 10/07/2026).
//
// IniFileSaveTests pins the atomic-Save + round-trip contract; CanonicalParityTests
// pins that all four project copies build an identical MAC canonical from a saved
// ini. THIS file pins the parser's behavioural contract directly - the parts the
// enforcement config's round-trip fidelity depends on but no other test isolates:
//   - a value is preserved VERBATIM (spaces, '=', quotes, unicode, Base64 padding),
//     split from its key on the FIRST '=' only;
//   - a value's fidelity survives Save/Load (the MAC is computed over parsed values,
//     so a shifted value would flip the MAC and freeze the block);
//   - section/key lookup is case-INSENSITIVE (the Windows .ini convention);
//   - an absent section/key reads as "" (never null);
//   - Load of a missing file is a no-op (no throw);
//   - comment (';'/'#'), blank, and malformed (no '=') lines are tolerated;
//   - CRLF and LF line endings both parse, with no stray '\r' left in a value;
//   - duplicate keys are last-wins;
//   - Sections.Count reflects the distinct [Section] blocks (the B7/C1b structural
//     recovery gate reads it).
//
// HARD FENCE: only unique temp files inside the test bin directory are touched -
// never the real monkmode_settings.ini, hosts, registry or service. Uses the CLI's
// MonkMode.IniFile as the representative type; all four copies are byte-identical
// (md5-verified) and IniFileSaveTests already pins their 4-copy Save parity, so one
// late-bound cross-copy parse-parity smoke here is enough to guard the rest.

using System.IO;
using System.Text;

namespace MonkMode.Tests;

public class IniFileParserTests
{
    private static string TempIniPath() =>
        Path.Combine(AppContext.BaseDirectory, $"iniparse_{Guid.NewGuid():N}.ini");

    // ---- verbatim value fidelity across a real Save/Load ----

    [Theory]
    [InlineData("a plain value")]
    [InlineData("value with = an equals sign")]                 // splits on FIRST '='
    [InlineData("v1;12345:0900-1700;sites=reddit.com;apps=chrome.exe")] // real [Schedule] Spec shape
    [InlineData("he said \"hello\" then left")]                  // quotes are NOT stripped
    [InlineData("aGVsbG8gd29ybGQh/pad+chars==")]                 // Base64-ish: '/', '+', '=' padding
    [InlineData("café-éü☃-\U0001F600 unicode")]   // accented + snowman + emoji
    [InlineData("2026-06-25 5:00:00 p.m.")]                      // en-CA datetime as the app stores it
    [InlineData("trailing spaces   ")]                            // internal/trailing spaces kept verbatim
    [InlineData("")]                                              // present-but-empty round-trips as ""
    public void SaveThenLoad_PreservesValueVerbatim(string value)
    {
        var path = TempIniPath();
        try
        {
            var w = new MonkMode.IniFile();
            w.SetKeyValue("Section", "Key", value);
            w.Save(path);

            var r = new MonkMode.IniFile();
            r.Load(path);
            Assert.Equal(value, r.GetKeyValue("Section", "Key"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---- absent section / key => "" (never null) ----

    [Fact]
    public void GetKeyValue_AbsentSectionOrKey_ReturnsEmptyString_NotNull()
    {
        var ini = new MonkMode.IniFile();
        // absent section
        Assert.Equal(string.Empty, ini.GetKeyValue("NoSuchSection", "NoSuchKey"));
        // present section, absent key
        ini.SetKeyValue("Time", "Until", "x");
        Assert.Equal(string.Empty, ini.GetKeyValue("Time", "Missing"));
        // null args are tolerated (return "", no throw)
        Assert.Equal(string.Empty, ini.GetKeyValue(null!, "k"));
        Assert.Equal(string.Empty, ini.GetKeyValue("Time", null!));
    }

    // ---- Load of a missing file is a no-op, no throw ----

    [Fact]
    public void Load_MissingFile_NoThrow_LeavesModelEmpty()
    {
        var ini = new MonkMode.IniFile();
        var missing = Path.Combine(AppContext.BaseDirectory, $"does_not_exist_{Guid.NewGuid():N}.ini");

        var ex = Record.Exception(() => ini.Load(missing));
        Assert.Null(ex);
        Assert.Equal(string.Empty, ini.GetKeyValue("Time", "Until"));
        Assert.Equal(0, ini.Sections.Count);
    }

    [Fact]
    public void Load_NonMerge_ClearsPreviousContentFirst()
    {
        var path = TempIniPath();
        try
        {
            var first = new MonkMode.IniFile();
            first.SetKeyValue("Time", "Until", "ON_DISK");
            first.Save(path);

            var ini = new MonkMode.IniFile();
            ini.SetKeyValue("Stale", "Key", "should_be_cleared");
            ini.Load(path);   // default merge=false -> clears the pre-set section

            Assert.Equal("ON_DISK", ini.GetKeyValue("Time", "Until"));
            Assert.Equal(string.Empty, ini.GetKeyValue("Stale", "Key"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---- case-insensitive lookup, in memory and after a Save/Load ----

    [Fact]
    public void Lookup_IsCaseInsensitive_ForSectionAndKey()
    {
        var path = TempIniPath();
        try
        {
            var w = new MonkMode.IniFile();
            w.SetKeyValue("Time", "Until", "value-1");
            Assert.Equal("value-1", w.GetKeyValue("TIME", "until"));   // in memory
            w.Save(path);

            var r = new MonkMode.IniFile();
            r.Load(path);
            Assert.Equal("value-1", r.GetKeyValue("time", "UNTIL"));   // after round-trip
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SetKeyValue_SameKeyDifferentCase_OverwritesNotDuplicates()
    {
        var ini = new MonkMode.IniFile();
        ini.SetKeyValue("Time", "Until", "first");
        ini.SetKeyValue("Time", "UNTIL", "second");
        Assert.Equal("second", ini.GetKeyValue("Time", "Until"));
    }

    // ---- the parser accepts what the writer emits + tolerates hand edits ----

    [Fact]
    public void Load_SplitsValueOnFirstEqualsOnly()
    {
        var path = TempIniPath();
        try
        {
            File.WriteAllText(path, "[Schedule]\r\nSpec=v1;a=b;c=d\r\n");
            var ini = new MonkMode.IniFile();
            ini.Load(path);
            Assert.Equal("v1;a=b;c=d", ini.GetKeyValue("Schedule", "Spec"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_ToleratesCommentBlankAndMalformedLines()
    {
        var path = TempIniPath();
        try
        {
            var sb = new StringBuilder();
            sb.Append("; a semicolon comment\r\n");
            sb.Append("# a hash comment\r\n");
            sb.Append("\r\n");                          // blank line
            sb.Append("   \r\n");                       // whitespace-only line
            sb.Append("[Time]\r\n");
            sb.Append("this line has no equals sign\r\n"); // malformed -> skipped, no throw
            sb.Append("Until=keep-me\r\n");
            sb.Append("  # indented comment inside a section\r\n");
            File.WriteAllText(path, sb.ToString());

            var ini = new MonkMode.IniFile();
            var ex = Record.Exception(() => ini.Load(path));
            Assert.Null(ex);
            Assert.Equal("keep-me", ini.GetKeyValue("Time", "Until"));
            Assert.Equal(1, ini.Sections.Count);   // only [Time]; comments/junk added nothing
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("\r\n")]   // Windows CRLF (what the writer emits)
    [InlineData("\n")]     // bare LF (a hand-edited / cross-tool file)
    public void Load_HandlesBothLineEndings_NoStrayCarriageReturnInValue(string nl)
    {
        var path = TempIniPath();
        try
        {
            File.WriteAllText(path, $"[Time]{nl}Until=2026-01-02 3:04:05{nl}TimeChanging=no{nl}");
            var ini = new MonkMode.IniFile();
            ini.Load(path);
            // If '\r' leaked into the value the equality (and downstream MAC) would break.
            Assert.Equal("2026-01-02 3:04:05", ini.GetKeyValue("Time", "Until"));
            Assert.Equal("no", ini.GetKeyValue("Time", "TimeChanging"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_DuplicateKey_LastWins()
    {
        var path = TempIniPath();
        try
        {
            File.WriteAllText(path, "[Time]\r\nUntil=first\r\nUntil=second\r\n");
            var ini = new MonkMode.IniFile();
            ini.Load(path);
            Assert.Equal("second", ini.GetKeyValue("Time", "Until"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_DuplicateSectionHeaders_MergeIntoOneSection()
    {
        var path = TempIniPath();
        try
        {
            File.WriteAllText(path, "[Time]\r\nUntil=u\r\n[Time]\r\nHighWater=hw\r\n");
            var ini = new MonkMode.IniFile();
            ini.Load(path);
            Assert.Equal("u", ini.GetKeyValue("Time", "Until"));
            Assert.Equal("hw", ini.GetKeyValue("Time", "HighWater"));
            Assert.Equal(1, ini.Sections.Count);   // one distinct [Time] section
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---- Sections.Count (the structural-usability recovery gate) ----

    [Fact]
    public void Sections_Count_ReflectsDistinctSections()
    {
        var ini = new MonkMode.IniFile();
        Assert.Equal(0, ini.Sections.Count);
        ini.SetKeyValue("Process", "List", "null");
        ini.SetKeyValue("User", "CustomSites", "null");
        ini.SetKeyValue("Time", "Until", "x");
        ini.AddSection("Process");   // idempotent: no new section
        Assert.Equal(3, ini.Sections.Count);
    }

    // ---- 4-copy parse parity smoke (byte-identical copies must parse identically) ----

    [Fact]
    public void AllFourCopies_ParseAValueContainingEquals_Identically()
    {
        var path = TempIniPath();
        try
        {
            // Write with the CLI copy, read back with every project's own copy.
            var w = new MonkMode.IniFile();
            w.SetKeyValue("Schedule", "Spec", "v1;12345:0900-1700;sites=reddit.com;apps=chrome.exe");
            w.SetKeyValue("Integrity", "Mac", "bWFjLXdpdGgtcGFkZGluZz09");   // Base64 '==' tail
            w.Save(path);

            var readers = new object[]
            {
                new monkmode.IniFile(),   // service
                new MonkMode.IniFile(),   // CLI
                new mm_notify.IniFile(),  // notifier
                new mm_guard.IniFile(),   // guardian
            };

            foreach (var sample in readers)
            {
                dynamic r = Activator.CreateInstance(sample.GetType())!;
                r.Load(path);
                Assert.Equal("v1;12345:0900-1700;sites=reddit.com;apps=chrome.exe",
                    (string)r.GetKeyValue("Schedule", "Spec"));
                Assert.Equal("bWFjLXdpdGgtcGFkZGluZz09",
                    (string)r.GetKeyValue("Integrity", "Mac"));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
