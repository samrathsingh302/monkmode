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

// MonkMode.Tests - AtomicHosts crash-safe hosts write (audit C1).
//
// The four hosts writers - the CLI's WriteHostsBlock + RestoreHostsFromStrip and
// the service's stopMe (expiry strip) + timer self-heal (B2 repair) - now route
// through AtomicHosts.WriteAtomic, which serializes to a unique temp file in the
// same directory then renames it over the target (File.Move overwrite ==
// MoveFileEx REPLACE_EXISTING, atomic on NTFS), instead of truncate-rewriting
// hosts in place. The old in-place rewrite left a window where a crash blanked or
// half-wrote hosts and destroyed the user's own custom entries (and the B2
// self-heal fired it every 10s). These tests mirror IniFileSaveTests and pin:
//   (a) WriteAtomic writes the exact contents (read back byte-for-byte);
//   (b) WriteAtomic over an existing file replaces it WHOLESALE (no torn merge of
//       old + new bytes - this is what protects the user's entries on a re-strip);
//   (c) no .tmp sibling is left behind after a successful write;
//   (d) the bytes on disk are exactly what was written (never empty/torn);
//   (e) both per-project copies (CLI + service) behave identically - the file is
//       byte-for-byte duplicated per project and must stay in parity, like
//       Simple3Des / ConfigIntegrity / IniFile.
//
// HARD FENCE: only files inside the test bin directory are touched - never the
// real hosts file, the registry or the service.

using System.IO;

namespace MonkMode.Tests;

public class AtomicHostsTests
{
    private const string Marker = "#### MonkMode Entries ####";
    private const string Block = Marker + "\r\n127.0.0.1 reddit.com\r\n127.0.0.1 www.reddit.com\r\n";

    private static string TempHostsPath() =>
        Path.Combine(AppContext.BaseDirectory, $"atomichosts_{Guid.NewGuid():N}.hosts");

    [Fact]
    public void WriteAtomic_WritesExactContents()
    {
        var path = TempHostsPath();
        try
        {
            var contents = "# my hosts\r\n127.0.0.1 my-dev-box\r\n" + Block;
            MonkMode.AtomicHosts.WriteAtomic(path, contents);
            Assert.Equal(contents, File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WriteAtomic_OverExistingFile_ReplacesWholesale()
    {
        // The strip/repair paths overwrite hosts; the rename must replace the
        // whole file, never leave stale bytes from the longer previous content
        // appended after the new (shorter) content - that would be a torn write
        // that re-introduces a partial MonkMode block or duplicate user lines.
        var path = TempHostsPath();
        try
        {
            var withBlock = "# user line\r\n127.0.0.1 keep-me\r\n" + Block;
            MonkMode.AtomicHosts.WriteAtomic(path, withBlock);

            var stripped = "# user line\r\n127.0.0.1 keep-me\r\n";
            MonkMode.AtomicHosts.WriteAtomic(path, stripped);

            // Exactly the stripped content - no trailing remnant of the longer
            // first write, and the marker is fully gone.
            Assert.Equal(stripped, File.ReadAllText(path));
            Assert.DoesNotContain(Marker, File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WriteAtomic_LeavesNoTempFileBehind()
    {
        var path = TempHostsPath();
        try
        {
            MonkMode.AtomicHosts.WriteAtomic(path, Block);

            // The atomic write uses "<name>.<guid>.tmp" siblings; none must survive
            // a successful write (the rename consumes the temp).
            var leftovers = Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".*.tmp");
            Assert.Empty(leftovers);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WriteAtomic_NewFile_ProducesCompleteFile()
    {
        // Works whether or not the target already exists (the arm path writes a
        // hosts that may not pre-exist in a clean image).
        var path = TempHostsPath();
        try
        {
            Assert.False(File.Exists(path));
            MonkMode.AtomicHosts.WriteAtomic(path, Block);
            Assert.Equal(Block, File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WriteAtomic_BothProjectCopies_WriteOverwriteAndLeaveNoTemp()
    {
        // The CLI (MonkMode) and the service (monkmode) each carry their own
        // byte-identical copy of AtomicHosts. Both writers must show the atomic
        // behaviour (exact write, wholesale overwrite, no temp leftover) - this
        // pins the C1 fix was applied to BOTH copies and they stay in parity.
        // Late-bind the two distinct VB Module types via reflection.
        var copyTypes = new[]
        {
            typeof(MonkMode.AtomicHosts).GetMethod("WriteAtomic"),  // CLI
            typeof(monkmode.AtomicHosts).GetMethod("WriteAtomic"),  // service
        };

        foreach (var writeAtomic in copyTypes)
        {
            Assert.NotNull(writeAtomic);
            var path = TempHostsPath();
            try
            {
                var first = "# keep\r\n127.0.0.1 keep-me\r\n" + Block;
                writeAtomic!.Invoke(null, new object[] { path, first });
                Assert.Equal(first, File.ReadAllText(path));

                var second = "# keep\r\n127.0.0.1 keep-me\r\n";
                writeAtomic.Invoke(null, new object[] { path, second });
                Assert.Equal(second, File.ReadAllText(path));

                var leftovers = Directory.GetFiles(
                    Path.GetDirectoryName(path)!,
                    Path.GetFileName(path) + ".*.tmp");
                Assert.True(leftovers.Length == 0,
                    $"{writeAtomic.DeclaringType?.FullName} left a temp file behind");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
