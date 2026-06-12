// MonkMode.Tests - block end-time persistence.
//
// The config contract stores datetimes as en-CA strings; the service parses
// them with en-CA. If any writer formats with the machine locale instead, the
// service's parse fails and it silently falls back to a default 7-day block
// (the P1 audit finding). These tests prove the explicit-culture round-trip
// survives a hostile CurrentCulture (e.g. de-DE).
//
// The only file touched is monkmode_settings.ini inside the test bin
// directory (AppContext.BaseDirectory under the test host) - never the real
// deployed config, hosts file, registry or service.

using System.Globalization;

namespace MonkMode.Tests;

public class ExpiryRoundTripTests
{
    private static readonly CultureInfo EnCa = new("en-CA");

    private static void WithCulture(string name, Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    public void EnCaFormat_ParseRoundTrip_SurvivesAnyCurrentCulture(string locale)
    {
        // 25th of the month on purpose: a day > 12 makes any silent
        // day/month swap visible instead of round-tripping by luck.
        var until = new DateTime(2026, 6, 25, 17, 4, 33);
        WithCulture(locale, () =>
        {
            var stored = until.ToString(EnCa);
            Assert.True(DateTime.TryParse(stored, EnCa, DateTimeStyles.None, out var parsed),
                $"en-CA string '{stored}' failed to parse under {locale}");
            Assert.Equal(until, parsed);
        });
    }

    [Fact]
    public void ServiceFallbackWriteFormat_ReadBackByService_UnderGermanLocale()
    {
        // Mirrors the service's own write/read pair after the P1 fix:
        // write EncryptData(dt.ToString(en-CA)), read TryParse(DecryptData, en-CA).
        var enc = new monkmode.Simple3Des("mm_textbox");
        var until = new DateTime(2026, 6, 25, 9, 30, 0);
        WithCulture("de-DE", () =>
        {
            var stored = enc.EncryptData(until.ToString(EnCa));
            Assert.True(DateTime.TryParse(enc.DecryptData(stored), EnCa, DateTimeStyles.None, out var parsed));
            Assert.Equal(until, parsed);
        });
    }

    [Fact]
    public void CliWriteConfig_ActiveBlockEnd_RoundTrips_UnderGermanLocale()
    {
        // Full persisted round-trip through the real CLI code paths
        // (WriteConfig encrypt+format -> ini file -> ActiveBlockEnd
        // decrypt+parse). Under the test host the ini lands in the test bin
        // directory, so no real MonkMode state is involved.
        var until = new DateTime(2026, 6, 25, 22, 15, 45); // second precision: sub-seconds aren't stored
        var iniPath = MonkMode.Blocker.IniPath();
        try
        {
            WithCulture("de-DE", () =>
            {
                MonkMode.Blocker.WriteConfig(Array.Empty<string>(), Array.Empty<string>(), until);
                Assert.Equal(until, MonkMode.Blocker.ActiveBlockEnd());
            });
        }
        finally
        {
            if (File.Exists(iniPath)) File.Delete(iniPath);
        }
    }

    [Fact]
    public void NotifierClockCompensationFormat_IsReadableByTheService()
    {
        // The notifier rewrites [Time] Until after a clock change; the service
        // must be able to parse what it wrote (both en-CA after the P1 fix),
        // whatever the machine locale is.
        var notifierEnc = new mm_notify.Simple3Des("mm_textbox");
        var serviceEnc = new monkmode.Simple3Des("mm_textbox");
        var newUntil = new DateTime(2026, 12, 31, 23, 59, 59);
        WithCulture("de-DE", () =>
        {
            var stored = notifierEnc.EncryptData(newUntil.ToString(EnCa));
            Assert.True(DateTime.TryParse(serviceEnc.DecryptData(stored), EnCa, DateTimeStyles.None, out var parsed));
            Assert.Equal(newUntil, parsed);
        });
    }
}
