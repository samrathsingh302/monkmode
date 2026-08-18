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

// MonkMode.Tests - the loopback BLOCK PAGE (v1.1 S7d, pins P50 + P51).
//
// PORT 80 IS NEVER BOUND BY THIS FILE. BlockPageServer.TryStart takes the port as an argument
// precisely so that every test here passes 0 (an OS-assigned EPHEMERAL port) and reads the real
// port back off BoundPort. The live port 80 appears in exactly one place below: a constant
// assertion that BlockPage.LoopbackPort still IS 80. Every server started here also asserts its
// bound port is not 80 before it is used, so a regression that ignored the argument would fail
// loudly rather than quietly bind the live port on Samrath's machine.
//
// A loopback socket is not the blocker's enforcement surface: nothing in this file touches the
// hosts file, the registry, the SCM or the config, and every server started is stopped in a
// finally.
//
// THE PROPERTIES THESE TESTS EXIST FOR.
//   1. The page is a PURE function of the slots and the monotonic mark - so it can never print a
//      deadline it cannot justify, and a PENDING or schedule slot is never listed as running.
//   2. A failed bind is a quiet False, never an exception - the notifier is also the user-session
//      app-kill loop and the URL watcher, so a listener that could throw would be a bypass
//      manufactured out of a cosmetic feature.
//   3. The request is IGNORED. Malformed, empty, oversized or hostile input all get the same
//      bytes, and nothing in the request path writes any state.

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MonkMode.Tests;

public class BlockPagePureTests
{
    // Config datetimes are en-CA everywhere in MonkMode; the page's mark and ends are no
    // exception, so the fixtures below are built through that culture.
    private static readonly System.Globalization.CultureInfo CA = new("en-CA");

    private static string Stamp(DateTime dt) => dt.ToString(CA);

    private static mm_notify.BlockPage.SlotLine Slot(string id, int sites, string untilText)
    {
        var s = new mm_notify.BlockPage.SlotLine();
        s.Id = id;
        s.SiteCount = sites;
        s.UntilText = untilText;
        return s;
    }

    // The fixed frame every page carries, so the per-slot expectations below stay readable.
    private const string Head =
        "<!DOCTYPE html>\n" +
        "<html lang=\"en\">\n" +
        "<head><meta charset=\"utf-8\"><title>MonkMode - locked in</title></head>\n" +
        "<body>\n" +
        "<h1>Locked in.</h1>\n";

    private const string Tail =
        "<p>MonkMode only serves this page on plain HTTP; HTTPS sites just fail to load.</p>\n" +
        "</body>\n" +
        "</html>\n";

    [Fact]
    public void ZeroSlots_IsStillAValidLockedInPage()
    {
        string html = mm_notify.BlockPage.BuildBlockPageHtml(new List<mm_notify.BlockPage.SlotLine>(), "");
        Assert.Equal(Head + Tail, html);
    }

    [Fact]
    public void NullSlots_ReadAsZeroSlots()
    {
        Assert.Equal(Head + Tail, mm_notify.BlockPage.BuildBlockPageHtml(null, ""));
    }

    [Fact]
    public void OneSlot_ListsIdSiteCountAndRemaining()
    {
        var mark = new DateTime(2026, 8, 19, 9, 0, 0);
        var slots = new List<mm_notify.BlockPage.SlotLine>
        {
            Slot("3", 2, Stamp(mark.AddMinutes(45))),
        };

        string html = mm_notify.BlockPage.BuildBlockPageHtml(slots, Stamp(mark));

        Assert.Equal(Head + "<p>Block 3: 2 sites blocked - 45m</p>\n" + Tail, html);
    }

    [Fact]
    public void ThreeSlots_AreListedInOrderWithTheirOwnRemainingSpans()
    {
        var mark = new DateTime(2026, 8, 19, 9, 0, 0);
        var slots = new List<mm_notify.BlockPage.SlotLine>
        {
            Slot("1", 12, Stamp(mark.AddMinutes(45))),
            Slot("2", 1, Stamp(mark.AddHours(2).AddMinutes(30))),
            Slot("7", 40, Stamp(mark.AddDays(1).AddHours(3))),
        };

        string html = mm_notify.BlockPage.BuildBlockPageHtml(slots, Stamp(mark));

        Assert.Equal(
            Head +
            "<p>Block 1: 12 sites blocked - 45m</p>\n" +
            "<p>Block 2: 1 sites blocked - 2h 30m</p>\n" +
            "<p>Block 7: 40 sites blocked - 1d 3h</p>\n" +
            Tail,
            html);
    }

    // P50: PENDING and schedule slots are NOT listed. Both are recognised by the same thing -
    // they have no end of their own yet - so one rule drops both.
    [Fact]
    public void SlotWithNoUntil_IsNotListed()
    {
        var mark = new DateTime(2026, 8, 19, 9, 0, 0);
        var slots = new List<mm_notify.BlockPage.SlotLine>
        {
            Slot("1", 12, Stamp(mark.AddMinutes(45))),
            Slot("2", 5, ""),      // PENDING / schedule
        };

        string html = mm_notify.BlockPage.BuildBlockPageHtml(slots, Stamp(mark));

        Assert.Equal(Head + "<p>Block 1: 12 sites blocked - 45m</p>\n" + Tail, html);
        Assert.DoesNotContain("Block 2", html);
    }

    [Theory]
    [InlineData("")]              // no end
    [InlineData("not a date")]    // unreadable end
    public void UnreadableUntil_IsDroppedRatherThanGuessed(string untilText)
    {
        var mark = new DateTime(2026, 8, 19, 9, 0, 0);
        string html = mm_notify.BlockPage.BuildBlockPageHtml(
            new List<mm_notify.BlockPage.SlotLine> { Slot("4", 3, untilText) }, Stamp(mark));

        Assert.Equal(Head + Tail, html);
    }

    [Fact]
    public void UnreadableMark_ListsNothing_RatherThanPrintABogusSpan()
    {
        var mark = new DateTime(2026, 8, 19, 9, 0, 0);
        string html = mm_notify.BlockPage.BuildBlockPageHtml(
            new List<mm_notify.BlockPage.SlotLine> { Slot("4", 3, Stamp(mark.AddHours(1))) }, "");

        Assert.Equal(Head + Tail, html);
    }

    [Theory]
    [InlineData(0)]        // exactly at the mark
    [InlineData(-30)]      // already behind it
    public void ElapsedSlot_IsNotListed(int minutesFromMark)
    {
        var mark = new DateTime(2026, 8, 19, 9, 0, 0);
        string html = mm_notify.BlockPage.BuildBlockPageHtml(
            new List<mm_notify.BlockPage.SlotLine> { Slot("4", 3, Stamp(mark.AddMinutes(minutesFromMark))) },
            Stamp(mark));

        Assert.Equal(Head + Tail, html);
    }

    // The mark is the MONOTONIC [Time] HighWater, not the wall clock: the page's remaining span is
    // a function of the two stamps it is given and of nothing else, so rolling the system clock
    // cannot change what it says.
    [Fact]
    public void RemainingIsMeasuredAgainstTheGivenMark_NotTheWallClock()
    {
        var slots = new List<mm_notify.BlockPage.SlotLine> { Slot("1", 1, Stamp(new DateTime(1999, 1, 1, 1, 0, 0))) };
        string html = mm_notify.BlockPage.BuildBlockPageHtml(slots, Stamp(new DateTime(1999, 1, 1, 0, 0, 0)));

        Assert.Contains("<p>Block 1: 1 sites blocked - 1h</p>", html);
    }

    [Fact]
    public void SubMinuteRemaining_UsesTheSharedShortVocabulary()
    {
        var mark = new DateTime(2026, 8, 19, 9, 0, 0);
        string html = mm_notify.BlockPage.BuildBlockPageHtml(
            new List<mm_notify.BlockPage.SlotLine> { Slot("1", 1, Stamp(mark.AddSeconds(20))) }, Stamp(mark));

        Assert.Contains("<p>Block 1: 1 sites blocked - &lt;1m</p>", html);
    }

    [Fact]
    public void NullSlotEntry_IsSkipped_NotThrown()
    {
        var mark = new DateTime(2026, 8, 19, 9, 0, 0);
        var slots = new List<mm_notify.BlockPage.SlotLine> { null!, Slot("1", 1, Stamp(mark.AddHours(1))) };

        string html = mm_notify.BlockPage.BuildBlockPageHtml(slots, Stamp(mark));

        Assert.Equal(Head + "<p>Block 1: 1 sites blocked - 1h</p>\n" + Tail, html);
    }

    // The notifier reads the ini WITHOUT a MAC gate (the widest reading, as every other notifier
    // scan does), and this page is rendered by a BROWSER - so a forged slot Id must land as text,
    // never as markup.
    [Fact]
    public void HostileSlotId_IsEscaped_NotRendered()
    {
        var mark = new DateTime(2026, 8, 19, 9, 0, 0);
        string html = mm_notify.BlockPage.BuildBlockPageHtml(
            new List<mm_notify.BlockPage.SlotLine> { Slot("<script>alert(1)</script>", 1, Stamp(mark.AddHours(1))) },
            Stamp(mark));

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("Block &lt;script&gt;alert(1)&lt;/script&gt;:", html);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("a&b", "a&amp;b")]
    [InlineData("<b>", "&lt;b&gt;")]
    [InlineData("\"q\"", "&quot;q&quot;")]
    [InlineData("&lt;", "&amp;lt;")]     // '&' first, so nothing is double-escaped
    public void HtmlEscape_CoversTheFourMarkupCharacters(string? raw, string expected)
    {
        Assert.Equal(expected, mm_notify.BlockPage.HtmlEscape(raw));
    }
}

public class BlockPageResponseTests
{
    private const string Crlf = "\r\n";

    [Fact]
    public void Response_IsTheP50StatusLineHeadersAndBody()
    {
        const string body = "<html>hi</html>";

        byte[] bytes = mm_notify.BlockPage.BuildResponseBytes(body);

        Assert.Equal(
            "HTTP/1.1 200 OK" + Crlf +
            "Content-Type: text/html; charset=utf-8" + Crlf +
            "Content-Length: 15" + Crlf +
            "Cache-Control: no-store" + Crlf +
            "Connection: close" + Crlf +
            Crlf +
            body,
            Encoding.UTF8.GetString(bytes));
    }

    // Content-Length is a BYTE count, not a character count: a slot Id is user data and could be
    // non-ASCII, and a wrong length truncates or hangs the browser.
    [Fact]
    public void ContentLength_CountsUtf8Bytes_NotCharacters()
    {
        const string body = "éé";   // two chars, four UTF-8 bytes

        byte[] bytes = mm_notify.BlockPage.BuildResponseBytes(body);
        string text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Content-Length: 4" + Crlf, text);
        int headEnd = text.IndexOf(Crlf + Crlf, StringComparison.Ordinal) + 4;
        Assert.Equal(4, bytes.Length - Encoding.UTF8.GetByteCount(text.Substring(0, headEnd)));
    }

    [Fact]
    public void EmptyAndNullBodies_StillProduceAWellFormedResponse()
    {
        foreach (string? body in new string?[] { "", null })
        {
            string text = Encoding.UTF8.GetString(mm_notify.BlockPage.BuildResponseBytes(body));
            Assert.StartsWith("HTTP/1.1 200 OK" + Crlf, text);
            Assert.Contains("Content-Length: 0" + Crlf, text);
            Assert.EndsWith(Crlf + Crlf, text);
        }
    }

    [Fact]
    public void RealPage_LengthHeaderMatchesTheRealBody()
    {
        string html = mm_notify.BlockPage.BuildBlockPageHtml(new List<mm_notify.BlockPage.SlotLine>(), "");
        byte[] bytes = mm_notify.BlockPage.BuildResponseBytes(html);
        string text = Encoding.UTF8.GetString(bytes);

        int headEnd = text.IndexOf(Crlf + Crlf, StringComparison.Ordinal) + 4;
        Assert.Contains("Content-Length: " + Encoding.UTF8.GetByteCount(html) + Crlf, text);
        Assert.Equal(html, text.Substring(headEnd));
    }

    // P50's re-bind gate: attempt-anchored, once a minute, and never negative-triggered.
    [Theory]
    [InlineData(0L, 0L, false)]
    [InlineData(0L, 59_999L, false)]
    [InlineData(0L, 60_000L, true)]
    [InlineData(0L, 600_000L, true)]
    [InlineData(1_000_000L, 999_999L, false)]   // a backwards delta is "not yet", never a storm
    public void ShouldRetryBind_IsAOncePerMinuteGate(long lastTick, long nowTick, bool expected)
    {
        Assert.Equal(expected, mm_notify.BlockPage.ShouldRetryBind(lastTick, nowTick, 60_000L));
    }

    [Fact]
    public void PinnedConstants()
    {
        Assert.Equal(80, mm_notify.BlockPage.LoopbackPort);
        Assert.Equal(60_000L, mm_notify.BlockPage.RebindRetryIntervalMs);
        Assert.Equal(2_000, mm_notify.BlockPage.ConnectionTimeoutMs);
        Assert.Equal(8_192, mm_notify.BlockPage.MaxRequestBytes);
    }
}

// The listener seam. Every server here is started on an EPHEMERAL port (0) and stopped in a
// finally; nothing binds 80.
public class BlockPageListenerTests
{
    private static string SamplePage() =>
        mm_notify.BlockPage.BuildBlockPageHtml(new List<mm_notify.BlockPage.SlotLine>(), "");

    private static mm_notify.BlockPageServer StartEphemeral(string html)
    {
        var server = new mm_notify.BlockPageServer();
        server.SetBody(html);
        Assert.True(server.TryStart(0), "an ephemeral bind must succeed");
        Assert.True(server.IsListening);
        Assert.True(server.BoundPort > 0);
        Assert.NotEqual(mm_notify.BlockPage.LoopbackPort, server.BoundPort);   // never 80
        return server;
    }

    // One raw-socket exchange. request may be null (send nothing); shutdownSend closes our half so
    // the server's read returns immediately instead of waiting out its 2s timeout.
    private static byte[] Exchange(int port, byte[]? request, bool shutdownSend)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        client.ReceiveTimeout = 15_000;
        client.SendTimeout = 15_000;
        using var ns = client.GetStream();
        if (request is { Length: > 0 })
        {
            ns.Write(request, 0, request.Length);
            ns.Flush();
        }
        if (shutdownSend) client.Client.Shutdown(SocketShutdown.Send);

        using var ms = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = ns.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    private static byte[] Get(int port) =>
        Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: reddit.com\r\nUser-Agent: test\r\n\r\n");

    [Fact]
    public void AnOrdinaryRequest_GetsTheExactResponseBytes()
    {
        string html = SamplePage();
        var server = StartEphemeral(html);
        try
        {
            byte[] got = Exchange(server.BoundPort, Get(server.BoundPort), shutdownSend: false);

            Assert.Equal(mm_notify.BlockPage.BuildResponseBytes(html), got);
            string text = Encoding.UTF8.GetString(got);
            Assert.StartsWith("HTTP/1.1 200 OK\r\n", text);
            Assert.Contains("Content-Type: text/html; charset=utf-8\r\n", text);
            Assert.Contains("Content-Length: " + Encoding.UTF8.GetByteCount(html) + "\r\n", text);
            Assert.Contains("Cache-Control: no-store\r\n", text);
            Assert.Contains("Connection: close\r\n", text);
            Assert.EndsWith(html, text);
        }
        finally
        {
            server.StopServing();
        }
    }

    // P50: the request is ignored ENTIRELY, so there is no parse to confuse. Garbage, a bare
    // newline, an oversized flood and a completely empty request all get the identical bytes and
    // the socket closes cleanly (the read loop above ends at EOF, which is that assertion).
    [Fact]
    public void MalformedEmptyAndOversizedRequests_AllGetTheSameResponse()
    {
        string html = SamplePage();
        byte[] expected = mm_notify.BlockPage.BuildResponseBytes(html);
        var server = StartEphemeral(html);
        try
        {
            var cases = new byte[]?[]
            {
                null,                                                   // nothing at all
                Encoding.ASCII.GetBytes("\n"),                          // a bare newline
                new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x7F },            // binary junk
                Encoding.ASCII.GetBytes("GET " + new string('A', 40_000) + " HTTP/1.1\r\n\r\n"),
            };

            foreach (byte[]? request in cases)
            {
                Assert.Equal(expected, Exchange(server.BoundPort, request, shutdownSend: true));
            }

            Assert.True(server.IsListening, "a malformed request must not stand the listener down");
        }
        finally
        {
            server.StopServing();
        }
    }

    // A bind failure is a quiet False. The notifier is NOT elevated and does not own the machine's
    // port 80, so this is the EXPECTED outcome on some machines - and it must disable the page and
    // nothing else.
    [Fact]
    public void BindFailure_ReturnsFalseAndDisables_WithoutThrowing()
    {
        string html = SamplePage();
        var holder = StartEphemeral(html);
        try
        {
            int taken = holder.BoundPort;
            var loser = new mm_notify.BlockPageServer();

            Assert.False(loser.TryStart(taken));
            Assert.False(loser.IsListening);
            Assert.Equal(0, loser.BoundPort);

            loser.StopServing();          // idempotent on a server that never bound
            loser.SetBody("<html></html>");
            Assert.False(loser.IsListening);

            // the incumbent is untouched
            Assert.Equal(mm_notify.BlockPage.BuildResponseBytes(html),
                         Exchange(taken, Get(taken), shutdownSend: false));
        }
        finally
        {
            holder.StopServing();
        }
    }

    [Fact]
    public void StopServing_ReleasesThePortAndIsIdempotent()
    {
        var server = StartEphemeral(SamplePage());
        int port = server.BoundPort;

        server.StopServing();
        server.StopServing();

        Assert.False(server.IsListening);
        Assert.Equal(0, server.BoundPort);
        Assert.Throws<SocketException>(() =>
        {
            using var c = new TcpClient();
            c.Connect(IPAddress.Loopback, port);
        });
    }

    [Fact]
    public void TryStartTwice_DoesNotRebind()
    {
        var server = StartEphemeral(SamplePage());
        try
        {
            int first = server.BoundPort;
            Assert.True(server.TryStart(0));
            Assert.Equal(first, server.BoundPort);
        }
        finally
        {
            server.StopServing();
        }
    }

    // The page is rendered on the UI thread's poll and handed over as finished bytes; a serve in
    // flight therefore always writes ONE self-consistent page, and the next request sees the new
    // one. This is also the reason the request path reads no file: there is nothing to read.
    [Fact]
    public void SetBodyWhileListening_ChangesTheNextResponse()
    {
        var server = StartEphemeral(SamplePage());
        try
        {
            var mark = new DateTime(2026, 8, 19, 9, 0, 0);
            var slot = new mm_notify.BlockPage.SlotLine();
            slot.Id = "2";
            slot.SiteCount = 9;
            slot.UntilText = mark.AddMinutes(90).ToString(new System.Globalization.CultureInfo("en-CA"));
            string updated = mm_notify.BlockPage.BuildBlockPageHtml(
                new List<mm_notify.BlockPage.SlotLine> { slot },
                mark.ToString(new System.Globalization.CultureInfo("en-CA")));

            server.SetBody(updated);

            string text = Encoding.UTF8.GetString(Exchange(server.BoundPort, Get(server.BoundPort), false));
            Assert.EndsWith(updated, text);
            Assert.Contains("<p>Block 2: 9 sites blocked - 1h 30m</p>", text);
        }
        finally
        {
            server.StopServing();
        }
    }

    // A burst of connections is answered one after another on the listener's OWN background
    // thread; none of them throws, none of them gets a partial page, and the listener is still up
    // afterwards. (The UI thread is not in this picture at all - it only ever calls SetBody /
    // TryStart / StopServing, none of which do socket I/O.)
    [Fact]
    public void AConnectionBurst_IsAllAnsweredAndTheListenerSurvives()
    {
        string html = SamplePage();
        byte[] expected = mm_notify.BlockPage.BuildResponseBytes(html);
        var server = StartEphemeral(html);
        try
        {
            for (int i = 0; i < 25; i++)
            {
                Assert.Equal(expected, Exchange(server.BoundPort, Get(server.BoundPort), shutdownSend: true));
            }
            Assert.True(server.IsListening);
        }
        finally
        {
            server.StopServing();
        }
    }

    // Slowloris: a peer that connects and then says nothing costs the 2s read timeout and NOT the
    // listener. The next request is still served, which is the whole point of bounding the read.
    [Fact]
    public void ASilentClient_CannotWedgeTheListener()
    {
        string html = SamplePage();
        var server = StartEphemeral(html);
        TcpClient? silent = null;
        try
        {
            silent = new TcpClient();
            silent.Connect(IPAddress.Loopback, server.BoundPort);   // connect, then say nothing

            var sw = System.Diagnostics.Stopwatch.StartNew();
            byte[] got = Exchange(server.BoundPort, Get(server.BoundPort), shutdownSend: true);
            sw.Stop();

            Assert.Equal(mm_notify.BlockPage.BuildResponseBytes(html), got);
            // Bounded by the silent connection's 2s read timeout, not by its lifetime.
            Assert.True(sw.Elapsed.TotalSeconds < 10, $"served in {sw.Elapsed.TotalSeconds:0.0}s");
            Assert.True(server.IsListening);
        }
        finally
        {
            try { silent?.Close(); } catch { }
            server.StopServing();
        }
    }

    // A brand-new server that was never given a body still answers completely: the constructor
    // seeds the no-listable-slots page, so no connection can ever get an empty or truncated
    // response.
    [Fact]
    public void AServerWithNoBodySet_StillServesTheZeroSlotPage()
    {
        var server = new mm_notify.BlockPageServer();
        Assert.True(server.TryStart(0));
        Assert.NotEqual(mm_notify.BlockPage.LoopbackPort, server.BoundPort);
        try
        {
            Assert.Equal(mm_notify.BlockPage.BuildResponseBytes(SamplePage()),
                         Exchange(server.BoundPort, Get(server.BoundPort), shutdownSend: true));
        }
        finally
        {
            server.StopServing();
        }
    }
}
