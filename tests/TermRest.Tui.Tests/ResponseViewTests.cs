using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json.Nodes;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Testing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.History;
using TermRest.Core.Http;
using TermRest.Core.Scripting;
using TermRest.Tui.Views;
using Xunit;

namespace TermRest.Tui.Tests;

/// <summary>The response pane's Body tab with large bodies: cut for display, formatted in the background, copied whole.</summary>
[Collection("TUI")]
public sealed class ResponseViewTests : IDisposable
{
    private readonly IApplication _app;
    private readonly Window _window = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly ResponseView _view = new();
    private readonly List<string> _notices = [];
    private readonly SessionToken? _token;

    public ResponseViewTests()
    {
        _app = Application.Create();
        _app.Init(DriverRegistry.Names.ANSI);
        _app.Driver!.SetScreenSize(100, 30);
        _app.Driver.Clipboard = new FakeClipboard();
        _view.Notice += (_, notice) => _notices.Add(notice);
        _window.Add(_view);
        _token = _app.Begin(_window);
        _app.LayoutAndDraw(forceRedraw: true);
    }

    private static HttpExchange Exchange(byte[] body, string contentType = "application/json", bool truncated = false) =>
        new(new PreparedRequest { Method = "GET", Url = "https://api.example.com/items" })
        {
            Response = new ResponseSnapshot
            {
                StatusCode = 200,
                ReasonPhrase = "OK",
                ContentType = contentType,
                Body = body,
                BodyTruncated = truncated,
                Headers = [new("Content-Type", contentType)],
            },
        };

    /// <summary>A minified JSON array of about <paramref name="size"/> bytes, as APIs send it.</summary>
    private static byte[] Items(int size)
    {
        var json = new StringBuilder("[");
        for (var i = 0; json.Length < size; i++)
        {
            json.Append(i > 0 ? "," : "").Append("{\"id\":").Append(i).Append(",\"name\":\"Item ").Append(i).Append("\",\"tags\":[\"alpha\",\"beta\"]}");
        }

        return Encoding.UTF8.GetBytes(json.Append(']').ToString());
    }

    /// <summary>Runs the main loop's work until <paramref name="done"/>, for <paramref name="seconds"/> at most.</summary>
    private void RunUntil(Func<bool> done, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < deadline)
        {
            _app.TimedEvents!.RunTimers();
            Thread.Sleep(10);
        }
    }

    /// <summary>Waits a few seconds at most for background work to come back to the main loop, and runs it.</summary>
    private void RunBackgroundWork()
    {
        RunUntil(() => _app.TimedEvents!.Timeouts.Count > 0, seconds: 5);
        RunUntil(() => _app.TimedEvents!.Timeouts.Count == 0);
    }

    private void Click(string text)
    {
        _app.LayoutAndDraw(forceRedraw: true);
        var lines = (_app.Driver!.ToString() ?? "").Split('\n');
        var y = Array.FindIndex(lines, line => line.Contains(text, StringComparison.Ordinal));
        Assert.True(y >= 0, $"'{text}' is not on screen");
        _app.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(lines[y].IndexOf(text, StringComparison.Ordinal), y)));
    }

    [Fact]
    public void A_large_body_is_formatted_in_the_background_shown_cut_with_a_note_and_copied_whole()
    {
        var body = Items(3_000_000);
        var whole = BodyFormatter.Format(body, "application/json");

        var watch = Stopwatch.StartNew();
        _view.ShowExchange(Exchange(body));
        var returned = watch.Elapsed;
        var placeholder = _view.BodyText;
        RunUntil(() => _view.BodyText != placeholder);
        var shown = _view.BodyText;
        Click("Copy");
        RunUntil(() => _notices.Count > 1);

        Assert.True(returned < TimeSpan.FromSeconds(1), $"ShowExchange took {returned}"); // formatting 3 MB took 1 to 2 s
        Assert.Equal("Formatting…", placeholder);
        var note = shown.LastIndexOf("\n\n… showing the first 1.0 MB of ", StringComparison.Ordinal);
        Assert.True(note > ResponseView.MaxShownLength - 4096, $"cut at {note}");
        Assert.True(note <= ResponseView.MaxShownLength);
        Assert.StartsWith(shown[..note], whole, StringComparison.Ordinal);
        Assert.Equal('\n', whole[note]); // at the end of a line
        Assert.EndsWith(": Copy and Save as… give the whole body", shown, StringComparison.Ordinal);
        Assert.Equal(["Copying the response body…", "Response body copied to the clipboard."], _notices);
        Assert.Equal(whole, _app.Clipboard!.GetClipboardData());
    }

    [Fact]
    public void A_body_formatted_in_the_background_is_not_shown_once_another_body_replaced_it()
    {
        var large = Exchange(Items(2_000_000));

        _view.ShowExchange(large);
        _view.ShowExchange(Exchange("{\"ok\":true}"u8.ToArray()));
        RunBackgroundWork();
        var afterAnother = _view.BodyText;

        _view.ShowExchange(large);
        Click("Pretty: on");
        RunUntil(() => _view.BodyText != "Formatting…");
        RunBackgroundWork();
        var afterPrettyOff = _view.BodyText;

        Assert.Equal("{\n  \"ok\": true\n}", afterAnother);
        Assert.Equal(ResponseView.Shown(large.Response!, pretty: false), afterPrettyOff);
    }

    [Fact]
    public void A_long_body_is_cut_at_a_line_break_when_one_is_near_and_never_inside_a_character()
    {
        static string Shown(string text, bool truncated = false) =>
            ResponseView.Shown(Exchange(Encoding.UTF8.GetBytes(text), "text/plain", truncated).Response!, pretty: true);

        var lines = new string('a', ResponseView.MaxShownLength - 100) + "\r\n" + new string('b', 300);
        var line = new string('a', ResponseView.MaxShownLength - 1) + "😀 and more";
        var small = "short body";

        var cutAtLine = Shown(lines);
        var cutBeforePair = Shown(line, truncated: true);

        Assert.Equal(new string('a', ResponseView.MaxShownLength - 100) + "\n\n… showing the first 1.0 MB of 1.0 MB: Copy and Save as… give the whole body", cutAtLine);
        Assert.Equal(new string('a', ResponseView.MaxShownLength - 1) + "\n\n… showing the first 1.0 MB of 1.0 MB (truncated): Copy and Save as… give the whole body", cutBeforePair);
        Assert.Equal(small, Shown(small));
        Assert.Equal(small + "\n\n… (truncated)", Shown(small, truncated: true));
    }

    [Fact]
    public void What_scripts_wrote_is_shown_up_to_the_same_length_as_a_body()
    {
        // 2,000 lines of 4,000 characters (two phases' worth): about 700 MB in the editor, and a second or two to load.
        var scripts = new ScriptResults();
        scripts.Logs.AddRange(Enumerable.Range(0, 2000).Select(i => new ScriptLog("test", "request 'Items'", $"{i:D4} " + new string('x', 3995))));
        var exchange = Exchange("[]"u8.ToArray());
        exchange.Scripts = scripts;

        _view.ShowExchange(exchange);

        var shown = _view.ScriptsText;
        Assert.InRange(shown.Length, ResponseView.MaxShownLength - 4096, ResponseView.MaxShownLength + 100);
        Assert.EndsWith(" the scripts wrote", shown, StringComparison.Ordinal);
        Assert.Contains("0000 xxx", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("1999 xxx", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_JSON_body_that_cannot_be_pretty_printed_is_shown_as_received()
    {
        const string Json = "{\"a\":\"\\ud800 lone surrogate\"}";

        _view.ShowExchange(Exchange(Encoding.UTF8.GetBytes(Json)));

        Assert.Equal(Json, _view.BodyText);
    }

    [Fact]
    public void The_timeline_cuts_a_long_request_body_before_a_character_it_would_cut_in_two()
    {
        var request = new PreparedRequest
        {
            Method = "POST",
            Url = "https://api.example.com/items",
            Body = new PreparedBody { Kind = PreparedBodyKind.Text, Text = new string('a', 3999) + "😀 and more" },
        };
        var start = $"POST https://api.example.com/items\n\n{new string('a', 3999)}\n… (truncated)\n";

        _view.ShowSending(request);
        var sending = _view.TimelineText;
        _view.ShowExchange(new HttpExchange(request) { ErrorMessage = "Connection refused" });
        var sent = _view.TimelineText;
        Click("Timeline");
        _app.LayoutAndDraw(forceRedraw: true);

        Assert.StartsWith(start, sending, StringComparison.Ordinal);
        Assert.StartsWith(start, sent, StringComparison.Ordinal);
        Assert.Contains("POST https://api.example.com/items", _app.Driver!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_response_from_the_history_says_what_the_history_does_not_keep()
    {
        // An https request with tests and a client certificate, whose 3 MB response the history keeps the start of.
        var entry = new HistoryEntry
        {
            Id = "cut",
            Time = DateTimeOffset.Now,
            Method = "GET",
            Url = "https://api.example.com/items",
            Item = new JsonObject { ["name"] = "Items" },
            Status = 200,
            Reason = "OK",
            HttpVersion = "1.1",
            Size = 3 * 1024 * 1024,
            ContentType = "text/plain",
            BodySaved = true,
            BodyTruncated = true,
            ClientCertificate = "client.example.com (expires 2027-06-30, 0A1B2C3D…)",
            Timing = new TimingBreakdown { Tls = TimeSpan.FromMilliseconds(35), Total = TimeSpan.FromMilliseconds(120) },
        };
        var fromHistory = entry.ToExchange("the start"u8.ToArray());
        var live = new HttpExchange(new PreparedRequest { Method = "GET", Url = entry.Url, Uri = new Uri(entry.Url) })
        {
            Response = fromHistory.Response,
            Timing = entry.Timing,
        };

        _view.ShowExchange(fromHistory, fromHistory: true);
        var shown = (_view.StatusText, _view.BodyText, _view.TlsText, _view.ScriptsText, _view.TimelineText);
        _view.ShowExchange(fromHistory); // put back, as when the next request could not be sent
        var shownAgain = (_view.StatusText, _view.BodyText, _view.TlsText, _view.ScriptsText, _view.TimelineText);
        _view.ShowExchange(entry.ToExchange(null), fromHistory: true); // its copy of the body is gone
        var withoutBody = (_view.StatusText, _view.BodyText);
        _app.Clipboard!.SetClipboardData("the user's");
        Click("Copy"); // neither copies nor saves nothing in its place
        Click("Save as…");
        RunUntil(() => _notices.Count >= 2, seconds: 5);
        var withoutBodyNotices = _notices.ToList();
        var clipboard = _app.Clipboard.GetClipboardData();
        _view.ShowExchange(live);

        Assert.Equal("200 OK · 120 ms · 3.0 MB · HTTP/1.1", shown.StatusText);
        Assert.Equal("the start\n\n… (truncated)", shown.BodyText);
        Assert.Equal("TLS details are not kept in the history.", shown.TlsText);
        Assert.Equal("Script results are not kept in the history.", shown.ScriptsText);
        Assert.Contains("\nAuth: (not recorded)\nClient certificate: client.example.com (expires 2027-06-30, 0A1B2C3D…)\nSSL verification: (not recorded) · Redirects: (not recorded)\n",
            shown.TimelineText, StringComparison.Ordinal);
        Assert.Equal(shown, shownAgain);
        Assert.Equal(("200 OK · 120 ms · 3.0 MB · HTTP/1.1", "(response body not kept in the history)"), withoutBody);
        Assert.Equal(["The history kept no copy of this response body.", "The history kept no copy of this response body."], withoutBodyNotices);
        Assert.Equal("the user's", clipboard);

        // A response just received says what it has.
        Assert.Equal("200 OK · 120 ms · 9 B · HTTP/1.1", _view.StatusText);
        Assert.Equal("the start\n\n… (truncated)", _view.BodyText);
        Assert.Equal("No TLS session was established.", _view.TlsText);
        Assert.StartsWith("No scripts ran for this request.", _view.ScriptsText, StringComparison.Ordinal);
        Assert.Contains("\nAuth: No auth\nClient certificate: none\nSSL verification: on · Redirects: follow\n", _view.TimelineText, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_token is not null)
        {
            _app.End(_token);
        }

        _window.Dispose();
        _app.Dispose();
    }
}
