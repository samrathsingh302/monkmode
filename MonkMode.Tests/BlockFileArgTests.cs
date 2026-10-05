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
}
