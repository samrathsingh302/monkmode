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

// MonkMode.Tests - v1.1 S7c: the PRESET LAYER (pins P63-P66b).
//
// WHAT THIS LAYER IS. `presets\` is INPUT SUGAR and nothing else: some .txt lists, a defaults.ini
// and a dot-sourced PowerShell file whose only power is to compose the SAME `monkmode block`
// command a user could type by hand. It stores nothing, it is not MAC-covered, it has zero
// enforcement authority. A mistake here can only build the wrong command - which is exactly why
// the grammar refuses rather than guesses.
//
// HOW IT IS TESTED WITHOUT RUNNING IT. mm-presets.ps1 is NEVER executed - not here, not by the
// build, not by the slice that wrote it (the hard fence: a wrapper's whole job is to arm a
// CanStop=False block against the live machine). Two things stand in for running it:
//   * a C# TWIN of every pure function in the script (LockGrammar below), mirrored line for line
//     and brute-forced against the vocabulary matrix. The twin is the thing under test; drift
//     between it and the script is caught by reading the script as TEXT (bottom section) and by
//     the AST parse the slice runs by hand.
//   * the REAL .txt / .ini files, read off disk and pushed through the CLI's own parsers
//     (Blocker.TryBuildUrlPatterns, Blocker.TryExpandAppPresets) so a shipped list that the CLI
//     would reject cannot survive a green suite.
//
// FAIL DIRECTION (R1). At arm time nothing is armed yet, so REFUSING is the safe end: a typo that
// refuses costs a retype, a typo that guesses could arm the wrong block for hours with no way to
// cut it short. Where a reading is genuinely open the LARGER block set wins - pinned as a
// superset property, not asserted in prose.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MonkMode.Tests;

// ---------------------------------------------------------------------------------------------
// The twin: a line-for-line C# mirror of the pure half of presets\mm-presets.ps1.
// Every function here names the script function it mirrors. Nothing here reads the clock.
// ---------------------------------------------------------------------------------------------
internal static class LockGrammar
{
    // MM-BundleNames - canonical order; `everything` is exactly this list.
    internal static readonly string[] BundleNames = { "doomscroll", "shorts", "social", "games" };

    // MM-Sites: non-comment, non-blank lines, trimmed, comma-joined.
    internal static string Sites(string path)
    {
        var kept = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            if (raw.Trim().Length == 0) continue;              // -match '\S'
            if (raw.Trim().StartsWith("#", StringComparison.Ordinal)) continue;   // -notmatch '^#'
            kept.Add(raw.Trim());
        }
        return string.Join(",", kept);
    }

    // MM-Default: the first `key = value` line of defaults.ini, or "2h" when absent.
    internal static string Default(string iniPath, string key)
    {
        foreach (var line in File.ReadAllLines(iniPath))
        {
            if (Regex.IsMatch(line, "^\\s*" + Regex.Escape(key) + "\\s*=")) return line.Split(new[] { '=' }, 2)[1].Trim();
        }
        return "2h";
    }

    // MM-Dedupe: case-insensitive, first occurrence wins, order preserved, blanks dropped.
    internal static List<string> Dedupe(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<string>();
        foreach (var x in items)
        {
            var k = (x ?? "").Trim();
            if (k.Length == 0) continue;
            if (!seen.Add(k)) continue;
            outp.Add(k);
        }
        return outp;
    }

    // MM-IsDuration - the CLI's TryParseDuration grammar (Program.vb:1174-1192) plus the
    // >9-digit guard the script documents.
    internal static bool IsDuration(string token)
    {
        var t = (token ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        long d = 0, h = 0, m = 0;
        var rx = Regex.Match(t, "^(?:(\\d+)d)?(?:(\\d+)h)?(?:(\\d+)m)?$");
        if (rx.Success && (rx.Groups[1].Success || rx.Groups[2].Success || rx.Groups[3].Success))
        {
            for (var g = 1; g <= 3; g++)
            {
                if (rx.Groups[g].Success && rx.Groups[g].Value.Length > 9) return false;
            }
            if (rx.Groups[1].Success) d = long.Parse(rx.Groups[1].Value, CultureInfo.InvariantCulture);
            if (rx.Groups[2].Success) h = long.Parse(rx.Groups[2].Value, CultureInfo.InvariantCulture);
            if (rx.Groups[3].Success) m = long.Parse(rx.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        else if (Regex.IsMatch(t, "^[0-9]{1,9}$"))
        {
            m = long.Parse(t, CultureInfo.InvariantCulture);
        }
        else
        {
            return false;
        }
        return (d * 1440) + (h * 60) + m > 0;
    }

    // MM-UntilTime - HH:MM as an absolute stamp, rolled to tomorrow when today's has gone or is
    // within the lead. "" for anything that is not a 24-hour clock time.
    internal static string UntilTime(DateTime now, string hhmm, int leadSeconds = 90)
    {
        var m = Regex.Match((hhmm ?? "").Trim(), "^([01]?[0-9]|2[0-3]):([0-5][0-9])$");
        if (!m.Success) return "";
        var target = now.Date.AddHours(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                             .AddMinutes(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        if (target <= now.AddSeconds(leadSeconds)) target = target.AddDays(1);
        return target.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    internal sealed class Parsed
    {
        public bool Ok = true;
        public string Error = "";
        public List<string> Bundles = new();
        public string For = "";
        public string Until = "";
        public bool Rolled;
        public bool Committed;
    }

    // MM-LockVocabulary - the words the refusal lists.
    internal static readonly string[] VocabularyWords =
    {
        "doomscroll", "shorts", "social", "games", "everything", "for", "until", "tonight",
        "midnight", "committed",
    };

    // MM-ParseLockArgs.
    internal static Parsed Parse(string[] words, DateTime now, string defaultFor = "2h", int leadSeconds = 90)
    {
        var r = new Parsed();

        Parsed Refuse(string why)
        {
            r.Ok = false;
            r.Error = why + " Nothing has been armed." + Environment.NewLine + "vocabulary";
            return r;
        }

        var toks = new List<string>();
        foreach (var w in words ?? Array.Empty<string>())
        {
            if (w != null && w.Trim().Length > 0) toks.Add(w.Trim().ToLowerInvariant());
        }

        var picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var timeWord = "";
        var i = 0;
        while (i < toks.Count)
        {
            var w = toks[i];
            var step = 1;

            if (w == "everything")
            {
                foreach (var b in BundleNames) picked.Add(b);
            }
            else if (BundleNames.Contains(w))
            {
                picked.Add(w);
            }
            else if (w == "committed")
            {
                r.Committed = true;
            }
            else if (w == "for" || w == "until")
            {
                if (timeWord.Length > 0) return Refuse($"'{w}' is a second length for the same block, after '{timeWord}'.");
                if (i + 1 >= toks.Count) return Refuse($"'{w}' needs a value after it.");
                var val = toks[i + 1];
                step = 2;
                if (w == "for")
                {
                    if (val == "midnight") r.For = "midnight";
                    else if (IsDuration(val)) r.For = val;
                    else return Refuse($"'for {val}' is not a length I understand.");
                }
                else
                {
                    var until = UntilTime(now, val, leadSeconds);
                    if (until.Length == 0) return Refuse($"'until {val}' is not a 24-hour clock time (use e.g. 'until 22:00').");
                    r.Until = until;
                }
                timeWord = w;
            }
            else if (w == "tonight")
            {
                if (timeWord.Length > 0) return Refuse($"'tonight' is a second length for the same block, after '{timeWord}'.");
                r.Until = UntilTime(now, "04:00", leadSeconds);
                timeWord = w;
            }
            else if (w == "midnight")
            {
                if (timeWord.Length > 0) return Refuse($"'midnight' is a second length for the same block, after '{timeWord}'.");
                r.For = "midnight";
                timeWord = w;
            }
            else if (IsDuration(w))
            {
                if (timeWord.Length > 0) return Refuse($"'{w}' is a second length for the same block, after '{timeWord}'.");
                r.For = w;
                timeWord = w;
            }
            else
            {
                return Refuse($"'{w}' is not a word mm-lock knows.");
            }
            i += step;
        }

        r.Bundles = BundleNames.Where(b => picked.Contains(b)).ToList();
        if (r.Bundles.Count == 0) r.Bundles = new List<string> { "doomscroll" };
        if (timeWord.Length == 0) r.For = defaultFor;
        if (r.Until.Length > 0) r.Rolled = !r.Until.StartsWith(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return r;
    }

    internal sealed class Sources
    {
        public List<string> SiteFiles = new();
        public List<string> UrlFiles = new();
        public List<string> AppFiles = new();
        public List<string> Presets = new();
        public List<string> AppPresets = new();
        public List<string> Apps = new();
    }

    // MM-BundleFlags.
    internal static Sources BundleFlags(IEnumerable<string> bundles)
    {
        var siteFiles = new List<string>();
        var urlFiles = new List<string>();
        var appFiles = new List<string>();
        var presets = new List<string>();
        var appPresets = new List<string>();
        var apps = new List<string>();
        foreach (var raw in bundles)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "doomscroll": siteFiles.Add("doomscroll.txt"); urlFiles.Add("shortform-urls.txt"); break;
                case "shorts": urlFiles.Add("shortform-urls.txt"); break;
                case "social": siteFiles.Add("social.txt"); presets.Add("social"); apps.Add("WhatsApp.exe"); break;
                case "games": appFiles.Add("games-extra.txt"); appPresets.Add("games"); break;
            }
        }
        return new Sources
        {
            SiteFiles = Dedupe(siteFiles),
            UrlFiles = Dedupe(urlFiles),
            AppFiles = Dedupe(appFiles),
            Presets = Dedupe(presets),
            AppPresets = Dedupe(appPresets),
            Apps = Dedupe(apps),
        };
    }

    // MM-BuildBlockArgs.
    internal static List<string> BuildBlockArgs(string sites, string presets, string urls, string apps,
                                                string appPresets, bool committed, string[] extra,
                                                string forValue, string until)
    {
        var a = new List<string> { "block" };
        if (!string.IsNullOrEmpty(sites)) { a.Add("--sites"); a.Add(sites); }
        if (!string.IsNullOrEmpty(presets)) { a.Add("--preset"); a.Add(presets); }
        if (!string.IsNullOrEmpty(urls)) { a.Add("--urls"); a.Add(urls); }
        if (!string.IsNullOrEmpty(apps)) { a.Add("--apps"); a.Add(apps); }
        if (!string.IsNullOrEmpty(appPresets)) { a.Add("--app-preset"); a.Add(appPresets); }
        if (committed) a.Add("--commit");
        foreach (var e in extra ?? Array.Empty<string>()) { if (!string.IsNullOrEmpty(e)) a.Add(e); }
        if (!string.IsNullOrEmpty(until)) { a.Add("--until"); a.Add(until); }
        else { a.Add("--for"); a.Add(forValue); }
        return a;
    }

    // MM-CountList.
    internal static int CountList(string csv)
    {
        var n = 0;
        foreach (var t in (csv ?? "").Split(',')) { if (t.Trim().Length > 0) n++; }
        return n;
    }

    // MM-CountMatches - an unknown count (-1) or an unreadable cell matches anything.
    internal static bool CountMatches(string cell, int expected)
    {
        if (expected < 0) return true;
        var c = (cell ?? "").Trim();
        if (!Regex.IsMatch(c, "^[0-9]+$")) return true;
        return int.Parse(c, CultureInfo.InvariantCulture) == expected;
    }

    // MM-DuplicateActiveWarning - reads the fixed-width slot table `monkmode status` prints.
    internal static string DuplicateActiveWarning(string statusText, int sites, int apps, int urls)
    {
        foreach (var line in Regex.Split(statusText ?? "", "\r?\n"))
        {
            if (line.Length < 56) continue;
            if (!Regex.IsMatch(line.Substring(0, 3).Trim(), "^[0-9]+$")) continue;
            if (line.Substring(5, 8).Trim() != "ACTIVE") continue;
            if (!CountMatches(line.Substring(41, 5), sites)) continue;
            if (!CountMatches(line.Substring(46, 5), apps)) continue;
            if (!CountMatches(line.Substring(51, 5), urls)) continue;
            var id = line.Substring(0, 3).Trim();
            return $"A block that looks identical (id {id} - the same number of sites, apps and URLs) is already active. Arming again creates a SECOND slot - press Ctrl+C now if that isn't what you meant.";
        }
        return "";
    }

    // MM-ArmBundles' composition step (the file-reading half), so a whole command can be built
    // from the REAL lists. Returns the argument vector `& monkmode` would receive.
    internal static List<string> ComposeCommand(string presetsDir, Parsed p)
    {
        var f = BundleFlags(p.Bundles);
        var sites = new List<string>();
        var urls = new List<string>();
        var apps = new List<string>(f.Apps);
        foreach (var file in f.SiteFiles) sites.Add(Sites(Path.Combine(presetsDir, file)));
        foreach (var file in f.UrlFiles) urls.Add(Sites(Path.Combine(presetsDir, file)));
        foreach (var file in f.AppFiles) apps.Add(Sites(Path.Combine(presetsDir, file)));
        return BuildBlockArgs(
            string.Join(",", Dedupe(string.Join(",", sites).Split(','))),
            string.Join(",", f.Presets),
            string.Join(",", Dedupe(string.Join(",", urls).Split(','))),
            string.Join(",", Dedupe(string.Join(",", apps).Split(','))),
            string.Join(",", f.AppPresets),
            p.Committed, Array.Empty<string>(), p.For, p.Until);
    }
}

// Where the shipped preset files live. Walks up from the test binary to the directory holding
// MonkMode.sln - the tests read the REAL lists, never a copy, so a stale copy cannot go green.
internal static class PresetsDir
{
    internal static readonly string Path = Find();

    private static string Find()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "MonkMode.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return System.IO.Path.Combine(d!.FullName, "presets");
    }

    internal static string File(string name) => System.IO.Path.Combine(Path, name);
    internal static string Script => File("mm-presets.ps1");
    internal static string ScriptText => System.IO.File.ReadAllText(Script);
}

// ---- 1. P63/P64: the shipped bundle files -----------------------------------------------------

public class PresetBundleFileTests
{
    // The four patterns the URL layer's own tests call "the shipped shortform preset"
    // (MonkMode.Tests\UrlMatchTests.cs:49-53). The .txt and that array are the same contract
    // read from two ends; if this fails, one of them moved without the other.
    private static readonly string[] ShippedShortform =
    {
        "youtube.com/shorts", "instagram.com/reels", "facebook.com/reel", "youtube.com/",
    };

    private static string[] Tokens(string file) =>
        LockGrammar.Sites(PresetsDir.File(file)).Split(',').Where(t => t.Length > 0).ToArray();

    [Fact]
    public void ShortformUrls_IsExactlyTheFourShippedPatterns()
    {
        Assert.Equal(ShippedShortform, Tokens("shortform-urls.txt"));
    }

    [Fact]
    public void Doomscroll_IsTheP64Set()
    {
        // P64: decision 11's whole-domain TikTok and X, plus the existing video.txt set.
        // Whole-domain because a path pattern cannot express "all of TikTok".
        Assert.Equal(
            new[] { "tiktok.com", "vm.tiktok.com", "snapchat.com", "x.com", "twitter.com" },
            Tokens("doomscroll.txt"));
    }

    [Fact]
    public void GamesExtra_IsTheP63Set()
    {
        Assert.Equal(
            new[] { "steamwebhelper.exe", "ubisoftconnect.exe", "eadesktop.exe", "goggalaxy.exe", "minecraftlauncher.exe" },
            Tokens("games-extra.txt"));
    }

    [Fact]
    public void GamesExtra_AddsOnlyLaunchersTheBuiltInPresetDoesNotAlreadyCover()
    {
        // The bundle passes --app-preset games AND this list, so a name in both is dead weight.
        List<string> builtIn = new();
        var err = "";
        Assert.True(MonkMode.Blocker.TryExpandAppPresets("games", ref builtIn, ref err), err);
        foreach (var extra in Tokens("games-extra.txt"))
        {
            Assert.DoesNotContain(extra, builtIn, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("shortform-urls.txt")]
    [InlineData("doomscroll.txt")]
    [InlineData("games-extra.txt")]
    [InlineData("video.txt")]
    [InlineData("social.txt")]
    [InlineData("reddit.txt")]
    public void NoTokenCarriesASeparatorTheCliWouldChokeOn(string file)
    {
        // "|" packs url patterns in the config and ";" packs every other list, so a token
        // containing either would be silently reinterpreted as two entries downstream. The CLI
        // refuses them outright for --urls (Blocker.vb:1055-1058); nothing shipped may contain one.
        foreach (var t in Tokens(file))
        {
            Assert.DoesNotContain("|", t, StringComparison.Ordinal);
            Assert.DoesNotContain(";", t, StringComparison.Ordinal);
            Assert.DoesNotContain(",", t, StringComparison.Ordinal);   // "," separates them
        }
    }

    [Theory]
    [InlineData("shortform-urls.txt")]
    [InlineData("doomscroll.txt")]
    [InlineData("games-extra.txt")]
    public void EveryTokenIsTrimmedLowercaseAndFreeOfAScheme(string file)
    {
        foreach (var t in Tokens(file))
        {
            Assert.Equal(t.Trim(), t);
            Assert.Equal(t.ToLowerInvariant(), t);
            Assert.DoesNotContain("://", t, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", t, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryBundleFileIsNonEmptyAsShipped()
    {
        // An empty list is legal at runtime (the wrapper warns and arms what is left), but a
        // SHIPPED empty list would mean a bundle that silently blocks nothing.
        Assert.NotEmpty(Tokens("shortform-urls.txt"));
        Assert.NotEmpty(Tokens("doomscroll.txt"));
        Assert.NotEmpty(Tokens("games-extra.txt"));
    }

    [Fact]
    public void TheShortformPatternsAreAcceptedByTheCliUrlParserVerbatim()
    {
        // The list is handed to `--urls` as one comma-joined string. Push the real thing through
        // the CLI's own parser: if it would be rejected, the bundle would fail to arm.
        var joined = LockGrammar.Sites(PresetsDir.File("shortform-urls.txt"));
        var packed = "";
        var err = "";
        Assert.True(MonkMode.Blocker.TryBuildUrlPatterns(joined, ref packed, ref err), err);
        Assert.Equal(string.Join("|", ShippedShortform), packed);
    }

    [Fact]
    public void TheHomeTokenCarriesTheFixedPointWarningNextToIt()
    {
        // S6's finding: with a bare `youtube.com` the redirect TARGET also matches, so the page
        // redirects itself every cooldown. The trailing slash is what keeps /feed/subscriptions
        // reachable, and the file has to say so or someone will "simplify" it back.
        var text = File.ReadAllText(PresetsDir.File("shortform-urls.txt"));
        Assert.Contains("trailing slash", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/feed/subscriptions", text, StringComparison.Ordinal);
        Assert.Contains("redirect", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- MM-Sites' comment/blank handling, pinned on a fixture rather than a shipped file ----

    [Fact]
    public void CommentsBlanksAndIndentationAreHandledExactlyAsMmSitesDoes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"presetlist_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, new[]
        {
            "# a leading comment",
            "",
            "   ",
            "\t",
            "  indented.com  ",
            "   # an INDENTED comment - trimmed before the # test, so still a comment",
            "kept.com",
            "trailing.com   ",
            "#nospace.com",
        });
        try
        {
            Assert.Equal("indented.com,kept.com,trailing.com", LockGrammar.Sites(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AFileOfNothingButCommentsAndBlanksReadsAsEmpty()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"presetempty_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, new[] { "# only", "", "  ", "# comments" });
        try
        {
            // This is the case the wrapper must WARN about rather than arm silently - the
            // warning wording is pinned in PresetGrammarTests.AnEmptyListIsSaidOutLoud.
            Assert.Equal("", LockGrammar.Sites(path));
        }
        finally { File.Delete(path); }
    }
}

// ---- 2. P65: defaults.ini has a key per wrapper ------------------------------------------------

public class PresetDefaultsTests
{
    private static string Ini => PresetsDir.File("defaults.ini");

    [Theory]
    [InlineData("lock")]
    [InlineData("shorts")]
    [InlineData("games")]
    [InlineData("video")]
    [InlineData("insta")]
    [InlineData("reddit")]
    public void EveryWrapperHasItsOwnDefaultLength(string key)
    {
        var v = LockGrammar.Default(Ini, key);
        Assert.NotEqual("", v);
        // Every shipped default must be something the CLI would accept, or the wrapper that
        // falls back to it fails to arm at the moment the user asked for the simplest thing.
        Assert.True(LockGrammar.IsDuration(v) || v == "midnight", $"{key}={v}");
    }

    [Fact]
    public void TheThreeNewKeysShipAsTwoHours()
    {
        Assert.Equal("2h", LockGrammar.Default(Ini, "lock"));
        Assert.Equal("2h", LockGrammar.Default(Ini, "shorts"));
        Assert.Equal("2h", LockGrammar.Default(Ini, "games"));
    }

    [Fact]
    public void AMissingKeyFallsBackToTwoHoursRatherThanNothing()
    {
        // An empty --for would be a CLI error, i.e. a failure to arm.
        Assert.Equal("2h", LockGrammar.Default(Ini, "no-such-wrapper"));
    }
}

// ---- 3. P66b: the grammar ----------------------------------------------------------------------

public class PresetGrammarTests
{
    // A fixed evening, so `tonight` and `until` have one right answer.
    private static readonly DateTime Evening = new(2026, 8, 19, 21, 30, 0);

    private static LockGrammar.Parsed Parse(string words, DateTime? now = null, string defaultFor = "2h") =>
        LockGrammar.Parse(words.Length == 0 ? Array.Empty<string>() : words.Split(' '), now ?? Evening, defaultFor);

    private static List<string> Command(string words, DateTime? now = null, string defaultFor = "2h") =>
        LockGrammar.ComposeCommand(PresetsDir.Path, Parse(words, now, defaultFor));

    private static string Doom => LockGrammar.Sites(PresetsDir.File("doomscroll.txt"));
    private static string Short => LockGrammar.Sites(PresetsDir.File("shortform-urls.txt"));
    private static string Social => LockGrammar.Sites(PresetsDir.File("social.txt"));
    private static string GamesExtra => LockGrammar.Sites(PresetsDir.File("games-extra.txt"));

    // ---- the four examples P66b says MUST work, each to its exact argument vector ----

    [Fact]
    public void Example1_DoomscrollFor2h()
    {
        Assert.Equal(
            new[] { "block", "--sites", Doom, "--urls", Short, "--for", "2h" },
            Command("doomscroll for 2h"));
    }

    [Fact]
    public void Example2_ShortsUntil2200()
    {
        // 22:00 is still ahead of 21:30, so it is TODAY's - no rollover.
        Assert.Equal(
            new[] { "block", "--urls", Short, "--until", "2026-08-19 22:00" },
            Command("shorts until 22:00"));
        Assert.False(Parse("shorts until 22:00").Rolled);
    }

    [Fact]
    public void Example3_EverythingTonight()
    {
        Assert.Equal(
            new[]
            {
                "block",
                "--sites", Doom + "," + Social,
                "--preset", "social",
                "--urls", Short,
                "--apps", "WhatsApp.exe," + GamesExtra,
                "--app-preset", "games",
                "--until", "2026-08-20 04:00",
            },
            Command("everything tonight"));
    }

    [Fact]
    public void Example4_SocialGamesFor3hCommitted()
    {
        Assert.Equal(
            new[]
            {
                "block",
                "--sites", Social,
                "--preset", "social",
                "--apps", "WhatsApp.exe," + GamesExtra,
                "--app-preset", "games",
                "--commit",
                "--for", "3h",
            },
            Command("social games for 3h committed"));
    }

    // ---- defaults ----

    [Fact]
    public void NoWordsAtAllIsTheDoomscrollBundleForTheDefaultsIniLength()
    {
        var p = Parse("", defaultFor: "90m");
        Assert.True(p.Ok);
        Assert.Equal(new[] { "doomscroll" }, p.Bundles);
        Assert.Equal("90m", p.For);
        Assert.Equal("", p.Until);
        Assert.Equal(new[] { "block", "--sites", Doom, "--urls", Short, "--for", "90m" }, Command("", defaultFor: "90m"));
    }

    [Fact]
    public void ABundleWithNoTimeStillTakesTheDefaultLength()
    {
        Assert.Equal("2h", Parse("games").For);
    }

    [Fact]
    public void ATimeWithNoBundleStillTakesTheDefaultBundle()
    {
        Assert.Equal(new[] { "doomscroll" }, Parse("for 45m").Bundles);
        Assert.Equal("45m", Parse("for 45m").For);
    }

    // ---- vocabulary ----

    [Theory]
    [InlineData("doomscrol")]          // one letter short
    [InlineData("doomscrolling")]
    [InlineData("shortz")]
    [InlineData("youtube")]
    [InlineData("please")]
    [InlineData("2hours")]
    [InlineData("--commit")]           // a CLI flag is not grammar
    [InlineData("commited")]           // the plausible typo for `committed`
    [InlineData("everthing")]
    public void AnUnknownWordRefusesAndArmsNothing(string word)
    {
        var p = Parse(word);
        Assert.False(p.Ok);
        Assert.Contains(word, p.Error, StringComparison.Ordinal);      // names the offending word
        Assert.Contains("Nothing has been armed", p.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownWordRefusesTheWholeCommandEvenWhenTheRestIsValid()
    {
        // The dangerous shape: `mm-lock everything tonite` must not fall back to a doomscroll
        // block for 2h - the user asked for something else entirely.
        var p = Parse("everything tonite");
        Assert.False(p.Ok);
        Assert.Contains("tonite", p.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusalHappensBeforeAnyTimeOrBundleIsSettled()
    {
        var p = Parse("doomscroll for 2h banana");
        Assert.False(p.Ok);
    }

    [Theory]
    [InlineData("for")]
    [InlineData("until")]
    public void AValuedWordWithNothingAfterItRefuses(string word)
    {
        var p = Parse(word);
        Assert.False(p.Ok);
        Assert.Contains("needs a value", p.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("until 25:00")]
    [InlineData("until 22:60")]
    [InlineData("until 2200")]
    [InlineData("until tea-time")]
    [InlineData("until 10pm")]
    public void AClockTimeThatIsNotAClockTimeRefuses(string words)
    {
        Assert.False(Parse(words).Ok);
    }

    [Theory]
    [InlineData("for banana")]
    [InlineData("for -2h")]
    [InlineData("for 0")]
    [InlineData("for 0h0m")]
    [InlineData("for 2 h")]
    public void ALengthThatIsNotALengthRefuses(string words)
    {
        Assert.False(Parse(words).Ok);
    }

    [Theory]
    [InlineData("for 2h until 22:00")]
    [InlineData("tonight for 2h")]
    [InlineData("until 22:00 tonight")]
    [InlineData("2h midnight")]
    [InlineData("tonight midnight")]
    public void TwoTimeClausesRefuseRatherThanPickOne(string words)
    {
        // One of the two is the longer block. Guessing either way is a wrong block that cannot
        // be cut short; refusing costs a retype.
        var p = Parse(words);
        Assert.False(p.Ok);
        Assert.Contains("second length", p.Error, StringComparison.Ordinal);
    }

    // ---- bundles ----

    [Fact]
    public void EverythingIsEveryBundleName()
    {
        Assert.Equal(LockGrammar.BundleNames, Parse("everything").Bundles.ToArray());
    }

    [Fact]
    public void BundlesComeOutInCanonicalOrderWhateverOrderTheyAreTypedIn()
    {
        Assert.Equal(Parse("games social").Bundles, Parse("social games").Bundles);
        Assert.Equal(new[] { "social", "games" }, Parse("games social").Bundles);
    }

    [Fact]
    public void ARepeatedBundleIsStillOneBundle()
    {
        Assert.Equal(new[] { "shorts" }, Parse("shorts shorts shorts").Bundles);
        Assert.Equal(Command("shorts"), Command("shorts shorts"));
    }

    [Fact]
    public void EverythingAbsorbsAnyBundleNamedBesideIt()
    {
        Assert.Equal(Command("everything"), Command("social everything games"));
    }

    [Fact]
    public void DoomscrollAndShortsShareTheUrlListWithoutDoublingIt()
    {
        var urls = Command("doomscroll shorts");
        var at = urls.IndexOf("--urls");
        Assert.True(at >= 0);
        Assert.Equal(Short, urls[at + 1]);                       // not Short + "," + Short
        Assert.Equal(1, urls.Count(a => a == "--urls"));
    }

    [Fact]
    public void EverythingIsASupersetOfEverySingleBundle()
    {
        // The fail-direction property, proved rather than asserted: whatever a single bundle
        // would block, `everything` blocks too. A later bundle added to MM-BundleNames is in
        // `everything` automatically, so this cannot rot into a narrowing.
        var all = Command("everything");
        var allSites = FieldSet(all, "--sites");
        var allUrls = FieldSet(all, "--urls");
        var allApps = FieldSet(all, "--apps");
        var allPresets = FieldSet(all, "--preset");
        var allAppPresets = FieldSet(all, "--app-preset");
        foreach (var b in LockGrammar.BundleNames)
        {
            var one = Command(b);
            Assert.Subset(allSites, FieldSet(one, "--sites"));
            Assert.Subset(allUrls, FieldSet(one, "--urls"));
            Assert.Subset(allApps, FieldSet(one, "--apps"));
            Assert.Subset(allPresets, FieldSet(one, "--preset"));
            Assert.Subset(allAppPresets, FieldSet(one, "--app-preset"));
        }
    }

    private static HashSet<string> FieldSet(List<string> args, string flag)
    {
        var at = args.IndexOf(flag);
        if (at < 0 || at + 1 >= args.Count) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(args[at + 1].Split(','), StringComparer.OrdinalIgnoreCase);
    }

    // ---- time clauses ----

    [Fact]
    public void UntilRollsToTomorrowWhenTodaysTimeHasAlreadyGone()
    {
        // The CLI refuses a block ending less than a minute out (Program.vb:277), so an
        // already-passed target would silently fail to arm - nothing blocked, at 21:30 at night.
        var p = Parse("shorts until 09:00");
        Assert.Equal("2026-08-20 09:00", p.Until);
        Assert.True(p.Rolled);
    }

    [Fact]
    public void UntilRollsWhenTheTargetIsInsideTheLeadWindow()
    {
        // 21:31 is 60s away - inside the CLI's own floor once monkmode.exe re-reads the clock.
        var p = LockGrammar.Parse(new[] { "shorts", "until", "21:31" }, Evening);
        Assert.Equal("2026-08-20 21:31", p.Until);
        Assert.True(p.Rolled);
    }

    [Fact]
    public void UntilKeepsTodayWhenTheTargetIsComfortablyAhead()
    {
        var p = LockGrammar.Parse(new[] { "shorts", "until", "21:33" }, Evening);
        Assert.Equal("2026-08-19 21:33", p.Until);
        Assert.False(p.Rolled);
    }

    [Fact]
    public void TonightIsTheNextFourAm_TomorrowsFromTheEvening()
    {
        Assert.Equal("2026-08-20 04:00", Parse("everything tonight").Until);
    }

    [Fact]
    public void TonightIsTodaysFourAmWhenItIsAlreadyTheSmallHours()
    {
        // At 01:00, "tonight" is three hours away, not twenty-seven. A block cannot be cut
        // short, so the reading that would trap the user until tomorrow morning is the wrong one.
        var p = LockGrammar.Parse(new[] { "everything", "tonight" }, new DateTime(2026, 8, 20, 1, 0, 0));
        Assert.Equal("2026-08-20 04:00", p.Until);
        Assert.False(p.Rolled);
    }

    [Fact]
    public void MidnightIsHandedOnAsIsForMmMidnightUntilToResolveAtArmTime()
    {
        var p = Parse("doomscroll midnight");
        Assert.Equal("midnight", p.For);
        Assert.Equal("", p.Until);
        Assert.Equal(new[] { "block", "--sites", Doom, "--urls", Short, "--for", "midnight" }, Command("doomscroll midnight"));
    }

    [Fact]
    public void ForMidnightMeansTheSameAsBareMidnight()
    {
        Assert.Equal(Parse("midnight").For, Parse("for midnight").For);
    }

    [Fact]
    public void ABareLengthStillWorksLikeTheV10Wrappers()
    {
        // `mm-lock 2h` is the shape spec decision 15 named and the shape MM-Arm's callers use.
        Assert.Equal("2h", Parse("2h").For);
        Assert.Equal("90m", Parse("90m").For);
        Assert.Equal("45", Parse("45").For);
        Assert.Equal("1d2h30m", Parse("1d2h30m").For);
        Assert.Equal(Command("for 2h"), Command("2h"));
    }

    [Fact]
    public void CommittedIsAModifierNotATimeClause()
    {
        var p = Parse("committed");
        Assert.True(p.Ok);
        Assert.True(p.Committed);
        Assert.Equal("2h", p.For);                 // the default length still applies
        Assert.Equal(new[] { "doomscroll" }, p.Bundles);
        Assert.Contains("--commit", Command("committed"));
    }

    [Fact]
    public void CommittedCanBeSaidAnywhereInTheSentence()
    {
        Assert.Equal(Command("social games for 3h committed"), Command("committed social for 3h games"));
    }

    [Fact]
    public void TheWholeSentenceIsCaseAndWhitespaceInsensitive()
    {
        var loud = LockGrammar.Parse(new[] { " DOOMSCROLL ", "For", "2H", "" }, Evening);
        Assert.True(loud.Ok);
        Assert.Equal(new[] { "doomscroll" }, loud.Bundles);
        Assert.Equal("2h", loud.For);
    }

    [Fact]
    public void NullAndBlankWordsAreIgnoredRatherThanRefused()
    {
        var p = LockGrammar.Parse(new[] { "shorts", "", null!, "  ", "for", "2h" }, Evening);
        Assert.True(p.Ok);
        Assert.Equal(new[] { "shorts" }, p.Bundles);
    }

    // ---- the duration grammar, against the CLI's own ----

    [Theory]
    [InlineData("2h", true)]
    [InlineData("90m", true)]
    [InlineData("45", true)]
    [InlineData("1d", true)]
    [InlineData("1d2h30m", true)]
    [InlineData("2H", true)]
    [InlineData("0", false)]
    [InlineData("0m", false)]
    [InlineData("0d0h0m", false)]
    [InlineData("", false)]
    [InlineData("h", false)]
    [InlineData("2h30", false)]      // trailing bare number is not the CLI's grammar
    [InlineData("30m2h", false)]     // order is fixed d,h,m
    [InlineData("-5", false)]
    [InlineData("2.5h", false)]
    [InlineData("midnight", false)]  // handled as its own word, not as a duration
    [InlineData("99999999999h", false)]   // the CLI's Integer.Parse would throw; refuse instead
    public void TheDurationGrammarMatchesTheCli(string token, bool ok)
    {
        Assert.Equal(ok, LockGrammar.IsDuration(token));
    }

    // ---- a bundle whose list has been emptied ----

    // The real bundle files copied into a temp folder, with `blanked` replaced by a file of
    // comments only - i.e. what the user gets after editing a list down to nothing.
    private static string PresetsWithBlankedList(string blanked)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, $"presetsblank_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(PresetsDir.Path, "*.txt"))
        {
            var dest = Path.Combine(dir, Path.GetFileName(f));
            if (string.Equals(Path.GetFileName(f), blanked, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllLines(dest, new[] { "# everything commented out" });
            }
            else
            {
                File.Copy(f, dest);
            }
        }
        return dir;
    }

    [Fact]
    public void AnEmptiedListDropsItsOwnHalfAndStillArmsTheRest()
    {
        // The fail-direction that matters: mm-lock with an empty doomscroll.txt must still block
        // the shortform URLs. Arming LESS is bad; arming NOTHING while looking like it worked is
        // the failure this pins against.
        var dir = PresetsWithBlankedList("doomscroll.txt");
        try
        {
            var cmd = LockGrammar.ComposeCommand(dir, Parse("doomscroll for 2h"));
            Assert.DoesNotContain("--sites", cmd);
            Assert.Contains("--urls", cmd);
            Assert.Equal(Short, cmd[cmd.IndexOf("--urls") + 1]);
            Assert.Equal(new[] { "block", "--urls", Short, "--for", "2h" }, cmd);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AllListsEmptyLeavesNoInputFlagAtAllForTheWrapperToRefuseOn()
    {
        var dir = PresetsWithBlankedList("shortform-urls.txt");
        try
        {
            var cmd = LockGrammar.ComposeCommand(dir, Parse("shorts for 2h"));
            // Nothing but the length - which is the shape MM-ArmResolved refuses to send.
            Assert.Equal(new[] { "block", "--for", "2h" }, cmd);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnEmptiedListNeverSilentlyBecomesAnEmptyFlag()
    {
        // "--sites ''" would be a CLI error at best and an empty block at worst; the flag is
        // omitted entirely rather than sent blank.
        var dir = PresetsWithBlankedList("social.txt");
        try
        {
            var cmd = LockGrammar.ComposeCommand(dir, Parse("social for 2h"));
            Assert.DoesNotContain("", cmd);
            Assert.DoesNotContain("--sites", cmd);
            Assert.Contains("--preset", cmd);      // the CLI-side social preset still stands
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnEmptyListIsSaidOutLoud()
    {
        // The wording the wrapper prints when a bundle's list has no entries. Pinned because the
        // failure it guards against is SILENT: a bundle that contributes nothing looks exactly
        // like a bundle that worked.
        var text = PresetsDir.ScriptText;
        Assert.Contains("is empty - that part of the block is missing", text, StringComparison.Ordinal);
        Assert.Contains("Nothing to block - every list for this command is empty.", text, StringComparison.Ordinal);
    }
}

// ---- 4. P66: the duplicate-arm guard ------------------------------------------------------------

public class DuplicateArmGuardTests
{
    // Rows built by the CLI's OWN formatter, so the column arithmetic in the guard is pinned
    // against the real table rather than a hand-typed imitation of it.
    private static string ActiveRow(string id, int sites, int apps, int urls) =>
        MonkMode.Program.FormatSlotRow(new MonkMode.Blocker.SlotView
        {
            Id = id,
            State = MonkMode.Blocker.SlotStateActive,
            Ends = new DateTime(2026, 8, 19, 23, 30, 0),
            Sites = sites,
            Apps = apps,
            Urls = urls,
        });

    private static string Table(params string[] rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MonkMode.Program.FormatStatusHeading(rows.Length));
        sb.AppendLine(MonkMode.Program.FormatSlotTableHeader());
        foreach (var r in rows) sb.AppendLine(r);
        return sb.ToString();
    }

    [Fact]
    public void AnActiveRowOfTheSameShapeIsReportedWithItsId()
    {
        var w = LockGrammar.DuplicateActiveWarning(Table(ActiveRow("1", 5, 0, 4)), 5, 0, 4);
        Assert.Contains("id 1", w, StringComparison.Ordinal);
        Assert.Contains("SECOND slot", w, StringComparison.Ordinal);
        Assert.Contains("Ctrl+C", w, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWarningClaimsOnlyWhatStatusCanShow()
    {
        // `status` prints counts, never site names (Program.vb:1355-1365), so the sentence says
        // "looks identical ... the same number of", not "these exact sites".
        var w = LockGrammar.DuplicateActiveWarning(Table(ActiveRow("2", 5, 0, 4)), 5, 0, 4);
        Assert.Contains("looks identical", w, StringComparison.Ordinal);
        Assert.Contains("the same number of sites, apps and URLs", w, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentShapeIsNotADuplicate()
    {
        var table = Table(ActiveRow("1", 5, 0, 4));
        Assert.Equal("", LockGrammar.DuplicateActiveWarning(table, 6, 0, 4));
        Assert.Equal("", LockGrammar.DuplicateActiveWarning(table, 5, 1, 4));
        Assert.Equal("", LockGrammar.DuplicateActiveWarning(table, 5, 0, 3));
    }

    [Fact]
    public void AnUncountableAxisIsSkippedRatherThanGuessed()
    {
        // -1 = "a --preset expands inside the CLI, so this side cannot be counted here". Skipping
        // WIDENS the match, and the match only ever produces a warning.
        var table = Table(ActiveRow("3", 16, 12, 0));
        Assert.NotEqual("", LockGrammar.DuplicateActiveWarning(table, -1, -1, 0));
        Assert.Equal("", LockGrammar.DuplicateActiveWarning(table, -1, -1, 4));
    }

    [Fact]
    public void OnlyActiveRowsCount()
    {
        var pending = MonkMode.Program.FormatSlotRow(new MonkMode.Blocker.SlotView
        {
            Id = "4",
            State = MonkMode.Blocker.SlotStatePending,
            StartAt = new DateTime(2026, 8, 19, 23, 0, 0),
            DurationSeconds = 7200,
            Sites = 5,
            Apps = 0,
            Urls = 4,
        });
        var schedule = MonkMode.Program.FormatSlotRow(new MonkMode.Blocker.SlotView
        {
            Id = "5",
            State = MonkMode.Blocker.SlotStateSchedule,
            WindowOpen = true,
            WindowUntil = new DateTime(2026, 8, 20, 4, 0, 0),
            Sites = 5,
            Apps = 0,
            Urls = 4,
        });
        Assert.Equal("", LockGrammar.DuplicateActiveWarning(Table(pending, schedule), 5, 0, 4));
    }

    [Fact]
    public void TheHeaderRowIsNeverMistakenForABlock()
    {
        Assert.Equal("", LockGrammar.DuplicateActiveWarning(Table(), 0, 0, 0));
        Assert.Equal("", LockGrammar.DuplicateActiveWarning(MonkMode.Program.FormatSlotTableHeader(), 0, 0, 0));
    }

    [Fact]
    public void TheFirstMatchingRowIsTheOneNamed()
    {
        var w = LockGrammar.DuplicateActiveWarning(Table(ActiveRow("2", 1, 1, 1), ActiveRow("7", 1, 1, 1)), 1, 1, 1);
        Assert.Contains("id 2", w, StringComparison.Ordinal);
        Assert.DoesNotContain("id 7", w, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryIdFromOneToEightIsReadCorrectly()
    {
        for (var id = 1; id <= 8; id++)
        {
            var w = LockGrammar.DuplicateActiveWarning(Table(ActiveRow(id.ToString(CultureInfo.InvariantCulture), 2, 2, 2)), 2, 2, 2);
            Assert.Contains($"id {id} ", w, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LargeCountsStillLineUpWithTheColumns()
    {
        Assert.NotEqual("", LockGrammar.DuplicateActiveWarning(Table(ActiveRow("1", 999, 999, 999)), 999, 999, 999));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("MonkMode: no block has ever been installed on this machine.")]
    [InlineData("MonkMode: no active block (service installed but idle).")]
    [InlineData("\r\n\r\n")]
    [InlineData("garbage garbage garbage garbage garbage garbage garbage garbage garbage")]
    [InlineData("1  ACTIVE")]
    [InlineData("  1  ACTIVE  short")]
    public void NoStatusOutputCanMakeTheGuardThrowOrInvent(string? statusText)
    {
        // The guard runs before every bundle arm. If it threw, the arm would never happen - a
        // failure to block caused by a cosmetic helper, which is the one thing it may not do.
        Assert.Equal("", LockGrammar.DuplicateActiveWarning(statusText!, 5, 0, 4));
    }

    [Fact]
    public void TheGuardWarnsAndProceedsAndNeverRefuses()
    {
        // Read from the script itself: after printing the warning it sleeps, and there is no
        // `return` between the guard and the arm. A second identical block is legitimate - a
        // longer one stacked on a short one - so refusing here would be a new way to NOT block.
        var text = PresetsDir.ScriptText;
        var guard = text.IndexOf("$warning = MM-DuplicateActiveWarning", StringComparison.Ordinal);
        var arm = text.IndexOf("& monkmode @blockArgs", StringComparison.Ordinal);
        Assert.True(guard > 0 && arm > guard);
        var between = text.Substring(guard, arm - guard);
        Assert.Contains("Start-Sleep -Seconds 5", between, StringComparison.Ordinal);
        Assert.DoesNotContain("return", between, StringComparison.Ordinal);
    }
}

// ---- 5. The script as text: the properties the twin cannot see ---------------------------------

public class PresetScriptTextTests
{
    private static string Text => PresetsDir.ScriptText;

    // The ONE line in the script allowed to capture a monkmode invocation, matched WHOLE and
    // exactly. A substring exemption would be a hole: `& monkmode status; & monkmode block | tee`
    // contains "& monkmode status" and would have been waved through.
    private const string TheOneAllowedCapture =
        "try { $statusText = (& monkmode status | Out-String) } catch { $statusText = '' }";

    [Fact]
    public void NoArmingCommandIsEverPipedOrCaptured()
    {
        // The file header's own rule, and a live incident: the one-time partner code prints once,
        // and a captured arm has wedged shells. `monkmode status` is the ONE capture allowed - it
        // is read-only and prints no code - and it is the duplicate guard's input.
        foreach (var raw in Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            var at = line.IndexOf("& monkmode", StringComparison.Ordinal);
            if (at < 0) continue;
            if (line == TheOneAllowedCapture) continue;
            // Drop a trailing comment first - prose after a command legitimately contains ";"
            // and "|" (mm-schedule-off's does), and only the CODE is under test here. No
            // monkmode line in this file carries a "#" inside a quoted string.
            var cmd = line.Substring(at);
            var comment = cmd.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0) cmd = cmd.Substring(0, comment);
            Assert.DoesNotContain("|", cmd);
            Assert.DoesNotContain(">", cmd);
            Assert.DoesNotContain(";", cmd);                           // no second command hiding behind it
            Assert.False(line.Substring(0, at).Contains('='), line);   // not assigned either
        }
    }

    [Fact]
    public void TheOnlyCapturedCommandIsTheReadOnlyStatusReadVerbatim()
    {
        var captured = Text.Split('\n').Select(l => l.Trim())
                           .Where(l => l.Contains("& monkmode", StringComparison.Ordinal)
                                    && l.Contains('|', StringComparison.Ordinal)
                                    && !l.StartsWith("#", StringComparison.Ordinal)).ToList();
        Assert.Single(captured);
        Assert.Equal(TheOneAllowedCapture, captured[0]);
    }

    [Fact]
    public void TheAllowedCaptureIsStillTheLineTheScriptActuallyCarries()
    {
        // Guards the exemption itself: if the guard's read is reworded, this fails rather than
        // the exemption quietly matching nothing and the whole pin going vacuous.
        var hits = Text.Split('\n').Count(l => l.Trim() == TheOneAllowedCapture);
        Assert.Equal(1, hits);
    }

    [Fact]
    public void ARolledTimePausesBeforeArmingJustAsTheDuplicateGuardDoes()
    {
        // `mm-lock everything tonight committed` typed at 03:59 rolls to nearly a full day, and
        // committed means uncancellable. A yellow line with no pause behind it is not a warning -
        // the arm is already gone by the time it is read.
        var at = Text.IndexOf("if ($parsed.Rolled) {", StringComparison.Ordinal);
        Assert.True(at > 0);
        var arm = Text.IndexOf("MM-ArmBundles $parsed.Bundles", StringComparison.Ordinal);
        Assert.True(arm > at);
        var between = Text.Substring(at, arm - at);
        Assert.Contains("Start-Sleep -Seconds 5", between, StringComparison.Ordinal);
        Assert.Contains("Ctrl+C", between, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStaleMutuallyExclusiveClaimIsGone()
    {
        // v1.0 refused a manual block while the schedule was armed; v1.1 runs up to eight blocks
        // side by side, so the old comment now describes behaviour that no longer exists.
        var lines = Text.Split('\n').Where(l => l.Contains("mutually exclusive", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.All(lines, l => Assert.Contains("NO LONGER", l, StringComparison.Ordinal));
    }

    [Fact]
    public void TheRefusalListsEveryWordTheGrammarAccepts()
    {
        // A refusal that hides half the vocabulary teaches the user to guess again.
        var at = Text.IndexOf("function MM-LockVocabulary", StringComparison.Ordinal);
        Assert.True(at > 0);
        var vocab = Text.Substring(at, Text.IndexOf("function MM-Dedupe", StringComparison.Ordinal) - at);
        foreach (var w in LockGrammar.VocabularyWords)
        {
            Assert.Contains(w, vocab, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryBundleNameTheTwinKnowsIsTheListTheScriptDeclares()
    {
        // The one place the twin could silently drift from the script.
        var at = Text.IndexOf("function MM-BundleNames", StringComparison.Ordinal);
        Assert.True(at > 0);
        var line = Text.Substring(at, Text.IndexOf('\n', at) - at);
        foreach (var b in LockGrammar.BundleNames) Assert.Contains($"'{b}'", line, StringComparison.Ordinal);
        Assert.Equal(LockGrammar.BundleNames.Length, line.Count(c => c == '\'') / 2);
    }

    [Fact]
    public void EveryBundleFileNamedByTheScriptExistsOnDisk()
    {
        foreach (var f in new[] { "doomscroll.txt", "shortform-urls.txt", "social.txt", "games-extra.txt", "video.txt", "reddit.txt" })
        {
            Assert.True(Text.Contains($"'{f}'", StringComparison.Ordinal), f);
            Assert.True(System.IO.File.Exists(PresetsDir.File(f)), f);
        }
    }

    [Fact]
    public void TheThreeNewCommandsExist()
    {
        Assert.Contains("function mm-lock", Text, StringComparison.Ordinal);
        Assert.Contains("function mm-shorts", Text, StringComparison.Ordinal);
        Assert.Contains("function mm-games", Text, StringComparison.Ordinal);
        Assert.Contains("function MM-ArmSlot", Text, StringComparison.Ordinal);
        Assert.Contains("function MM-ParseLockArgs", Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCommandStillChecksForElevationBeforeDoingAnything()
    {
        // monkmode refuses without it; without the check the user gets a bare CLI error instead
        // of the sentence telling them how to fix it.
        foreach (var fn in new[] { "function mm-lock", "function mm-games", "function MM-ArmSlot" })
        {
            var at = Text.IndexOf(fn, StringComparison.Ordinal);
            Assert.True(at > 0, fn);
            Assert.Contains("MM-IsAdmin", Text.Substring(at, 400), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheScriptIsPlainAsciiSoAnEditorCannotMangleIt()
    {
        // A smart quote or an en dash pasted into a .ps1 has broken this file's kind of script
        // before; the whole preset layer is dot-sourced into a live elevated shell.
        var bytes = File.ReadAllBytes(PresetsDir.Script);
        Assert.All(bytes, b => Assert.True(b < 0x80, $"non-ASCII byte 0x{b:X2}"));
    }
}
