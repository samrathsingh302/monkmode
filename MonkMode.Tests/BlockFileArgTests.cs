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

// MonkMode.Tests - T5 (A6, 05/10/2026): `block --file <path>` refuses a file it cannot read.
//
// DoBlock read the file only `If fileArg <> "" AndAlso File.Exists(fileArg)`, with no Else, so a
// typo'd or missing path contributed nothing and said nothing - and the block then inherited the
// account-default list (or armed only --sites) for the whole duration, uncancellable. The read
// now lives in the pure TryReadSiteFile, and DoBlock refuses on its False before any side effect.
// DoBlock itself arms, so it is never driven here.
//
// Fences honoured: a GUID temp directory only (deleted in finally). Nothing is armed.

namespace MonkMode.Tests;

public class BlockFileArgTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mm-blockfile-" + Guid.NewGuid().ToString("N"));

    public BlockFileArgTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // TryReadSiteFile's ByRef parameters are C# `ref`, not `out`.
    private static bool Read(string path, out List<string> domains, out string err)
    {
        List<string>? d = null; string e = "";
        var ok = MonkMode.Program.TryReadSiteFile(path, ref d, ref e);
        domains = d!; err = e;
        return ok;
    }

    [Fact]
    public void PresentFile_ReturnsItsSites()
    {
        var path = Path.Combine(_dir, "sites.txt");
        File.WriteAllLines(path, new[] { "reddit.com", "  youtube.com  " });

        Assert.True(Read(path, out var domains, out var err));
        Assert.Equal(new[] { "reddit.com", "youtube.com" }, domains);
        Assert.Equal("", err);
    }

    [Fact]
    public void CommentAndBlankLines_AreSkipped()
    {
        var path = Path.Combine(_dir, "sites.txt");
        File.WriteAllLines(path, new[] { "# exam season", "", "   ", "reddit.com", "  # indented comment", "x.com" });

        Assert.True(Read(path, out var domains, out _));
        Assert.Equal(new[] { "reddit.com", "x.com" }, domains);
    }

    [Fact]
    public void AnExistingButEmptyFile_IsReadAsEmpty_NotRefused()
    {
        // Today's behaviour for a readable file is unchanged: only a file that cannot be read refuses.
        var path = Path.Combine(_dir, "empty.txt");
        File.WriteAllText(path, "");

        Assert.True(Read(path, out var domains, out _));
        Assert.Empty(domains);
    }

    [Fact]
    public void AbsentFile_IsRefused_NamingThePath()
    {
        // The audit's repro: `block --file exam-seasn.txt` must not quietly arm the defaults.
        var path = Path.Combine(_dir, "exam-seasn.txt");

        Assert.False(Read(path, out var domains, out var err));
        Assert.Equal("Could not read --file '" + path + "'.", err);
        Assert.Empty(domains);
    }

    [Fact]
    public void ADirectoryPath_IsRefused()
    {
        Assert.False(Read(_dir, out var domains, out var err));
        Assert.Equal("Could not read --file '" + _dir + "'.", err);
        Assert.Empty(domains);
    }

    [Fact]
    public void AnUnreadableFile_IsRefused()
    {
        // Held open with no sharing, so the read fails with an IOException - the "exists but
        // cannot be read" case, distinct from "absent".
        var path = Path.Combine(_dir, "locked.txt");
        File.WriteAllText(path, "reddit.com");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(Read(path, out var domains, out var err));
            Assert.Equal("Could not read --file '" + path + "'.", err);
            Assert.Empty(domains);
        }
    }

    // T5 round 3: Main refuses an unreadable --file BEFORE the config restore it runs ahead of
    // every dispatch (a write), through the pure PreDispatchFileRefusal.
    private static bool PreDispatch(string[] args, out List<string>? domains, out string err)
    {
        List<string>? d = null; string e = "";
        var refused = MonkMode.Program.PreDispatchFileRefusal(args, ref d, ref e);
        domains = d; err = e;
        return refused;
    }

    [Fact]
    public void PreDispatch_BlockWithAnAbsentFile_IsRefused()
    {
        var path = Path.Combine(_dir, "exam-seasn.txt");

        Assert.True(PreDispatch(new[] { "block", "--file", path, "--for", "2h" }, out var domains, out var err));
        Assert.Equal("Could not read --file '" + path + "'.", err);
        Assert.Null(domains);
        Assert.True(PreDispatch(new[] { "BLOCK", "--for", "2h", "--file=" + path }, out _, out err));
        Assert.Equal("Could not read --file '" + path + "'.", err);
    }

    [Fact]
    public void PreDispatch_BlockWithAPresentFile_PassesAndCarriesItsSites()
    {
        var path = Path.Combine(_dir, "sites.txt");
        File.WriteAllLines(path, new[] { "# exam season", "reddit.com", "x.com" });

        Assert.False(PreDispatch(new[] { "block", "--file", path, "--for", "2h" }, out var domains, out var err));
        Assert.Equal(new[] { "reddit.com", "x.com" }, domains);
        Assert.Equal("", err);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("add", "--sites", "c.com")]
    [InlineData("block", "--sites", "reddit.com", "--for", "2h")]
    [InlineData("block", "--file", "", "--for", "2h")]
    [InlineData("add", "--file", "C:\\no-such-dir-mm\\absent.txt")]
    [InlineData("unblock", "--code", "X")]
    public void PreDispatch_EveryOtherCall_PassesThroughUntouched(params string[] args)
    {
        Assert.False(PreDispatch(args, out var domains, out var err));
        Assert.Null(domains);
        Assert.Equal("", err);
    }

    // ---- T4 (13/10/2026): a named --file that read NO site still overrides the defaults ----
    //
    // ShouldInheritDefaults saw only counts, so an existing but empty or comment-only --file
    // looked like "no site source named" and the arm inherited every default site and app -
    // against DoBlock's own rule that an explicit source overrides the default. DoBlock passes
    // `namedSiteSource = fileDomains IsNot Nothing`; the composition below is DoBlock's
    // (inherit gate -> site dimension -> emptiness gate), driven on the pure functions.

    [Fact]
    public void CommentOnlyFile_ReadsAsANamedSourceWithNoSites()
    {
        var path = Path.Combine(_dir, "comments.txt");
        File.WriteAllLines(path, new[] { "# nothing yet", "", "   ", "  # still nothing" });

        Assert.False(PreDispatch(new[] { "block", "--file", path, "--for", "1h" }, out var domains, out var err));
        Assert.NotNull(domains);           // named: DoBlock's namedSiteSource is True
        Assert.Empty(domains);
        Assert.Equal("", err);
    }

    [Fact]
    public void EmptyFileAlone_InheritsNothing_AndIsRefusedAsNothingToBlock()
    {
        // `block --file empty.txt --for 1h` with default sites and apps configured.
        Assert.False(MonkMode.Blocker.ShouldInheritDefaults(0, 0, false, true));
        Assert.True(MonkMode.Blocker.HasNothingToBlock(0, 0, false));
        Assert.StartsWith("Nothing to block.", MonkMode.Blocker.NothingToBlockMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyFileWithApps_ArmsTheAppsAndNoDefaultSite()
    {
        // `block --file empty.txt --apps x --for 1h`: the inherit gate is open (an app source
        // produced something), but the site dimension stays closed - the file named it.
        Assert.True(MonkMode.Blocker.ShouldInheritDefaults(0, 1, false, true));
        Assert.False(MonkMode.Blocker.ShouldInheritDefaultSites(0, true));
        Assert.False(MonkMode.Blocker.HasNothingToBlock(0, 1, false));
    }

    [Fact]
    public void NoSiteSourceNamed_StillInheritsExactlyAsBefore()
    {
        // A bare `block --for 2h` and an --apps-only block: the pre-T4 answers, by both the
        // four-argument form and the three-argument one existing callers use.
        Assert.True(MonkMode.Blocker.ShouldInheritDefaults(0, 0, false, false));
        Assert.True(MonkMode.Blocker.ShouldInheritDefaults(0, 0, false));
        Assert.True(MonkMode.Blocker.ShouldInheritDefaultSites(0, false));
        Assert.True(MonkMode.Blocker.ShouldInheritDefaults(0, 1, false, false));
        // URL-only (FX4) is unchanged with or without a named empty file.
        Assert.False(MonkMode.Blocker.ShouldInheritDefaults(0, 0, true, false));
        Assert.False(MonkMode.Blocker.ShouldInheritDefaults(0, 0, true, true));
        // A file that read sites never takes the default sites.
        Assert.False(MonkMode.Blocker.ShouldInheritDefaultSites(2, true));
    }
}
