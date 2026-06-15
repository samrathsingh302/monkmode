// MonkMode.Tests - IniFile atomic Save (audit P2 #3).
//
// IniFile.Save now serializes to a unique temp file then renames it over the
// target (File.Move overwrite == MoveFileEx REPLACE_EXISTING, atomic on NTFS),
// instead of truncate-rewriting in place. The old in-place rewrite left a window
// where a concurrent reader (the notifier clock-comp read, or a fresh service
// tick) could see a half-written/empty ini. These tests pin:
//   (a) Save -> Load round-trips the data;
//   (b) Save over an existing file replaces it WHOLESALE (last-writer-wins, never
//       a torn merge of old + new keys);
//   (c) no .tmp sibling is left behind;
//   (d) the bytes on disk are a complete, parseable file;
//   (e) all four per-project copies behave identically (the file is duplicated
//       per project and must stay in parity, like Simple3Des / ConfigIntegrity).
//
// HARD FENCE: only files inside the test bin directory are touched - never the
// real monkmode_settings.ini, hosts, registry or service.

using System.IO;

namespace MonkMode.Tests;

public class IniFileSaveTests
{
    private static string TempIniPath() =>
        Path.Combine(AppContext.BaseDirectory, $"inisave_{Guid.NewGuid():N}.ini");

    [Fact]
    public void Save_ThenLoad_RoundTripsSectionsAndKeys()
    {
        var path = TempIniPath();
        try
        {
            var w = new monkmode.IniFile();
            w.SetKeyValue("Time", "Until", "2026-06-25 5:00:00 p.m.");
            w.SetKeyValue("Time", "TimeChanging", "no");
            w.SetKeyValue("Process", "List", "null");
            w.Save(path);

            var r = new monkmode.IniFile();
            r.Load(path);
            Assert.Equal("2026-06-25 5:00:00 p.m.", r.GetKeyValue("Time", "Until"));
            Assert.Equal("no", r.GetKeyValue("Time", "TimeChanging"));
            Assert.Equal("null", r.GetKeyValue("Process", "List"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_OverExistingFile_ReplacesWholesale()
    {
        var path = TempIniPath();
        try
        {
            var first = new monkmode.IniFile();
            first.SetKeyValue("Time", "Until", "FIRST");
            first.SetKeyValue("Time", "Stale", "shouldDisappear");
            first.Save(path);

            var second = new monkmode.IniFile();
            second.SetKeyValue("Time", "Until", "SECOND");
            second.Save(path);

            var r = new monkmode.IniFile();
            r.Load(path);
            Assert.Equal("SECOND", r.GetKeyValue("Time", "Until"));
            // The first write's extra key must be gone: the rename replaces the
            // whole file, it does not merge old content into the new one.
            Assert.Equal(string.Empty, r.GetKeyValue("Time", "Stale"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        var path = TempIniPath();
        try
        {
            var w = new monkmode.IniFile();
            w.SetKeyValue("Time", "Until", "x");
            w.Save(path);

            // The atomic write uses "<name>.<guid>.tmp" siblings; none must survive
            // a successful Save (the rename consumes the temp).
            var leftovers = Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".*.tmp");
            Assert.Empty(leftovers);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_ProducesCompleteParseableFile()
    {
        // The atomic rename means the target is never empty/torn after Save.
        var path = TempIniPath();
        try
        {
            var w = new monkmode.IniFile();
            w.SetKeyValue("User", "CustomSites", "a.com;b.com;");
            w.Save(path);
            var text = File.ReadAllText(path);
            Assert.Contains("[User]", text);
            Assert.Contains("CustomSites=a.com;b.com;", text);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_AllFourProjectCopies_RoundTripOverwriteAndLeaveNoTemp()
    {
        // Each project carries its own copy of IniFile in its own namespace; they
        // are distinct types with no shared interface, so exercise them late-bound.
        // All four must show the atomic-Save behaviour (overwrite wholesale, no
        // temp leftover) - this pins the fix was applied to every copy.
        var copies = new object[]
        {
            new monkmode.IniFile(),   // service
            new MonkMode.IniFile(),   // CLI
            new mm_notify.IniFile(),  // notifier
            new mm_guard.IniFile(),   // guardian
        };

        foreach (var sample in copies)
        {
            var type = sample.GetType();
            var path = TempIniPath();
            try
            {
                dynamic w1 = Activator.CreateInstance(type)!;
                w1.SetKeyValue("Time", "Until", "FIRST");
                w1.SetKeyValue("Time", "Stale", "shouldDisappear");
                w1.Save(path);

                dynamic w2 = Activator.CreateInstance(type)!;
                w2.SetKeyValue("Time", "Until", "SECOND");
                w2.Save(path);

                dynamic r = Activator.CreateInstance(type)!;
                r.Load(path, false);
                Assert.Equal("SECOND", (string)r.GetKeyValue("Time", "Until"));
                Assert.Equal(string.Empty, (string)r.GetKeyValue("Time", "Stale"));

                var leftovers = Directory.GetFiles(
                    Path.GetDirectoryName(path)!,
                    Path.GetFileName(path) + ".*.tmp");
                Assert.True(leftovers.Length == 0, $"{type.FullName} left a temp file behind");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
