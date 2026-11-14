using System.Globalization;
using System.Text;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Http;
using TermRest.Tui.Dialogs;
using TermRest.Tui.Theming;

namespace TermRest.Tui.Views;

/// <summary>Response pane: status line plus Body / Headers / Timeline / TLS tabs.</summary>
internal sealed class ResponseView : View
{
    /// <summary>
    /// The body tab shows this many characters at most (the editor takes about 90 bytes of memory for each); Copy and
    /// Save as… give the whole body.
    /// </summary>
    internal const int MaxShownLength = 1_048_576;

    /// <summary>Bodies of this many bytes or more are formatted (decoded, pretty printed) in the background.</summary>
    private const int BackgroundSize = 1_048_576;

    /// <summary>The Timeline tab shows this many characters of a request's body at most.</summary>
    private const int TimelineBodyLength = 4000;

    /// <summary>How far back from where a long body is cut a line break is looked for, to cut there instead.</summary>
    private const int LineBreakSearch = 4096;

    /// <summary>The Headers tab's Value column is never narrower than this, however narrow the pane.</summary>
    private const int MinValueColumns = 10;

    private readonly Label _status;
    private readonly TabPanel _tabs;
    private readonly View _bodyTab;
    private readonly TextArea _body;
    private readonly Button _pretty;
    private readonly Button _wrap;
    private readonly TableView _headers;
    private List<KeyValuePair<string, string>> _shownHeaders = [];
    private int _nameColumns; // the widest header name shown
    private readonly TextArea _timeline;
    private readonly TextArea _tls;
    private readonly TextArea _scripts;
    private HttpExchange? _exchange;
    private bool _fromHistory; // the exchange shown came from the history: its body as the history keeps it
    private bool _prettyOn = true;
    private int _bodyVersion; // a body formatted in the background is shown only if no other body replaced it meanwhile
    private bool _disposed;

    public ResponseView()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _status = new Label { HotKeySpecifier = Ui.NoHotKey, X = 0, Y = 0, Width = Dim.Fill(), Text = "No response yet. Press F5 (or Enter in the URL bar) to send." };

        _tabs = new TabPanel { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };

        _bodyTab = new View { Title = "Body", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _pretty = new Button { Text = "Pretty: on", X = 0, Y = 0, NoDecorations = true, ShadowStyle = null };
        _pretty.Accepted += (_, _) =>
        {
            _prettyOn = !_prettyOn;
            _pretty.Text = _prettyOn ? "Pretty: on" : "Pretty: off";
            RenderBody();
        };
        _wrap = new Button { Text = "Wrap: off", X = Pos.Right(_pretty) + 2, Y = 0, NoDecorations = true, ShadowStyle = null };
        _wrap.Accepted += (_, _) =>
        {
            _body!.WordWrap = !_body.WordWrap;
            _wrap.Text = _body.WordWrap ? "Wrap: on" : "Wrap: off";
        };
        var copy = new Button { Text = "Copy", X = Pos.Right(_wrap) + 2, Y = 0, NoDecorations = true, ShadowStyle = null };
        copy.Accepted += (_, _) => CopyBody();
        var save = new Button { Text = "Save as…", X = Pos.Right(copy) + 2, Y = 0, NoDecorations = true, ShadowStyle = null };
        save.Accepted += (_, _) => SaveBody();
        _body = new TextArea { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true };
        _bodyTab.Add(_pretty, _wrap, copy, save, _body);

        var headersTab = new View { Title = "Headers", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _headers = new TableView { Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
        _headers.Style.ExpandLastColumn = true;

        // A value cut at the pane's edge ends with "…" (a column style's mark); Enter or a double-click shows the whole header.
        _headers.Style.GetOrCreateColumnStyle(1);
        _headers.FrameChanged += (_, _) => FitValueColumn();
        _headers.Accepted += (_, _) => ShowSelectedHeader();

        // TableView's Ctrl+N (down) would hide the window's New request; Ctrl+P (up) goes with it.
        _headers.KeyBindings.Remove(Key.N.WithCtrl);
        _headers.KeyBindings.Remove(Key.P.WithCtrl);
        headersTab.Add(_headers);

        var timelineTab = new View { Title = "Timeline", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _timeline = new TextArea { ReadOnly = true };
        timelineTab.Add(_timeline);

        var tlsTab = new View { Title = "TLS", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _tls = new TextArea { ReadOnly = true };
        tlsTab.Add(_tls);

        var scriptsTab = new View { Title = "Scripts", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _scripts = new TextArea { ReadOnly = true };
        scriptsTab.Add(_scripts);

        _tabs.AddTabs(_bodyTab, headersTab, timelineTab, tlsTab, scriptsTab);
        Add(_status, _tabs);
        SetHeaders([]);
        _scripts.SetText(NoScripts);
    }

    private const string NoScripts =
        "No scripts ran for this request.\n\n"
        + "Scripts run before a request is sent and after its response arrives: set variables, sign requests, check\n"
        + "responses with tests. Write them in Python, JavaScript (Postman's pm.* scripts), C# or PowerShell in the\n"
        + "request's Scripts tab, or for a whole collection or folder (right-click it › Scripts…). Choose the runtimes that\n"
        + "run them (Python, Node.js or Bun, the .NET SDK, PowerShell) in Tools › Scripts…; Help › Scripting shows the tr\n"
        + "and pm objects.";

    /// <summary>Raised with short user-facing notices (copied, saved, …).</summary>
    public event EventHandler<string>? Notice;

    public HttpExchange? Exchange => _exchange;

    public string StatusText => _status.Text;

    /// <summary>The Scripts tab's text (for tests).</summary>
    internal string ScriptsText => _scripts.GetText();

    /// <summary>The Body tab's text (for tests).</summary>
    internal string BodyText => _body.GetText();

    /// <summary>The Timeline tab's text (for tests).</summary>
    internal string TimelineText => _timeline.GetText();

    /// <summary>The TLS tab's text (for tests).</summary>
    internal string TlsText => _tls.GetText();

    public void Clear()
    {
        _exchange = null;
        _fromHistory = false;
        _status.Text = "No response yet. Press F5 (or Enter in the URL bar) to send.";
        _status.SchemeName = null;
        ShowBody("");
        _timeline.SetText("");
        _tls.SetText("");
        _scripts.SetText(NoScripts);
        SetHeaders([]);
    }

    /// <summary>Pre-request scripts are running: the request is not prepared yet.</summary>
    public void ShowRunningScripts()
    {
        _status.Text = "Running pre-request scripts …  (Esc or Send to cancel)";
        _status.SchemeName = null;
    }

    public void ShowSending(PreparedRequest request)
    {
        _status.Text = $"Sending {request.Method} {request.Url} …  (Esc or Send to cancel)";
        _status.SchemeName = null;
        _timeline.SetText(DescribeRequest(request, null));
    }

    /// <param name="fromHistory">
    /// It comes from the history (see <see cref="Core.History.HistoryEntry.ToExchange"/>), whose copy of a body may be cut
    /// short and has secrets masked, and which keeps no TLS details, script results, or SSL and redirect settings.
    /// </param>
    public void ShowExchange(HttpExchange exchange, bool fromHistory = false)
    {
        // A response shown again (a request that could not be sent puts back the one shown before it) keeps where it came from.
        _fromHistory = fromHistory || (_fromHistory && ReferenceEquals(exchange, _exchange));
        _exchange = exchange;
        var response = exchange.Response;
        var scripts = exchange.Scripts;
        if (response is null)
        {
            _status.Text = exchange.Cancelled ? "Request cancelled." : $"Error: {exchange.ErrorMessage}";
            _status.SchemeName = exchange.Cancelled ? null : "Error";

            // When a script stopped the request, its traceback is what the user needs to see.
            ShowBody(exchange.Cancelled ? "" : scripts?.Error is not null ? ShownScripts($"{exchange.ErrorMessage}\n\n{scripts.Describe()}") : exchange.ErrorMessage ?? "");
            SetHeaders([]);
        }
        else
        {
            var http = $"HTTP/{response.Version.Major}{(response.Version.Major < 2 ? "." + response.Version.Minor : "")}";
            var warnings = exchange.Request.Warnings.Count > 0 ? $" · ⚠ {exchange.Request.Warnings.Count} warning(s), see Timeline" : "";
            var tests = scripts?.Summary is { Length: > 0 } summary ? $" · {summary}" : "";

            // The history may keep only the start of the body: its entry has the size received.
            var size = (_fromHistory ? exchange.HistoryEntry?.Size : null) ?? response.Body.LongLength;
            _status.Text = $"{response.StatusCode} {response.ReasonPhrase} · {HttpExchange.Format(exchange.Timing.Total)} · {HttpExchange.FormatSize(size)} · {http}{tests}{warnings}";
            _status.SchemeName = response.StatusCode switch
            {
                >= 500 => "Error",
                >= 400 => ThemeCatalog.WarningScheme,
                _ when scripts is { Succeeded: false } or { NotRun.Count: > 0 } => ThemeCatalog.WarningScheme,
                _ => ThemeCatalog.SuccessScheme,
            };
            SetHeaders(response.Headers);
            RenderBody();
        }

        _scripts.SetText(_fromHistory ? "Script results are not kept in the history." : scripts is null ? NoScripts : ShownScripts(scripts.Describe()));

        _timeline.SetText(DescribeRequest(exchange.Request, exchange, _fromHistory));
        _tls.SetText(_fromHistory
            ? "TLS details are not kept in the history."
            : exchange.Tls.Count == 0
                ? exchange.Request.Uri?.Scheme == "https" ? "No TLS session was established." : "Plain HTTP (no TLS)."
                : string.Join("\n\n", exchange.Tls.Select(t => t.Describe())));
        SetNeedsDraw();
    }

    /// <summary>
    /// Shows the response body, pretty or not, formatted from the response each time (as Copy is): the editor's text has
    /// its own line breaks (a lone CR shows as one) and only the start of a long body, and keeping the whole text as well
    /// would take memory. A large body is formatted in the background, and shown if nothing replaced it meanwhile.
    /// </summary>
    private void RenderBody()
    {
        if (_exchange?.Response is not { } response)
        {
            return;
        }

        // The history may have no copy of a body that was not empty (it keeps none, or the copy's file is gone).
        if (BodyNotKept)
        {
            ShowBody("(response body not kept in the history)");
            return;
        }

        var pretty = _prettyOn;
        if (App is not { } app || response.Body.Length < BackgroundSize)
        {
            ShowBody(Shown(response, pretty));
            return;
        }

        var version = ShowBody("Formatting…");
        _ = Task.Run(() => Shown(response, pretty)).ContinueWith(
            task => app.AddTimeout(TimeSpan.Zero, () =>
            {
                if (version == _bodyVersion && !_disposed)
                {
                    _body.SetText(task.IsCompletedSuccessfully ? task.Result : $"✗ {task.Exception?.GetBaseException().Message}");
                }

                return false;
            }),
            TaskScheduler.Default);
    }

    /// <summary>The response comes from the history, which has no copy of its body, though the body was not empty.</summary>
    private bool BodyNotKept => _fromHistory && _exchange?.Response?.Body.Length == 0 && _exchange.HistoryEntry?.Size > 0;

    /// <summary>Shows <paramref name="text"/> in the body tab, in place of a body still being formatted.</summary>
    private int ShowBody(string text)
    {
        _body.SetText(text);
        return ++_bodyVersion;
    }

    /// <summary>Copies the whole body, as formatted (a large one is formatted in the background).</summary>
    private void CopyBody()
    {
        if (_exchange?.Response is not { } response)
        {
            return;
        }

        if (BodyNotKept)
        {
            Notify("The history kept no copy of this response body.");
            return;
        }

        var pretty = _prettyOn;
        if (App is not { } app || response.Body.Length < BackgroundSize)
        {
            Copied(Whole(response, pretty));
            return;
        }

        Notify("Copying the response body…");
        _ = Task.Run(() => Whole(response, pretty)).ContinueWith(
            task => app.AddTimeout(TimeSpan.Zero, () =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    Copied(task.Result);
                }
                else if (!_disposed)
                {
                    Notify($"✗ {task.Exception?.GetBaseException().Message}");
                }

                return false;
            }),
            TaskScheduler.Default);
    }

    private void Copied(string text)
    {
        if (!_disposed)
        {
            Notify(Ui.CopyToClipboard(App, text) ? "Response body copied to the clipboard." : "Clipboard is not available in this terminal.");
        }
    }

    /// <summary>The whole body as formatted, with a note when the size limit cut it.</summary>
    private static string Whole(ResponseSnapshot response, bool pretty)
    {
        var text = BodyFormatter.Format(response.Body, response.ContentType, pretty);
        return response.BodyTruncated ? text + "\n\n… (truncated)" : text;
    }

    /// <summary>
    /// The body as the body tab shows it: formatted, and when longer than <see cref="MaxShownLength"/>, its start (up to a
    /// line break when one is near) and a note saying so.
    /// </summary>
    internal static string Shown(ResponseSnapshot response, bool pretty)
    {
        var text = BodyFormatter.Format(response.Body, response.ContentType, pretty);
        if (text.Length <= MaxShownLength)
        {
            return response.BodyTruncated ? text + "\n\n… (truncated)" : text;
        }

        var truncated = response.BodyTruncated ? " (truncated)" : "";
        return string.Concat(
            Start(text),
            $"\n\n… showing the first {HttpExchange.FormatSize(MaxShownLength)} of {HttpExchange.FormatSize(text.Length)}{truncated}: Copy and Save as… give the whole body");
    }

    /// <summary>
    /// What scripts printed (with their tests and errors), as the Scripts tab, or the Body tab of a request a script stopped,
    /// shows it: like a body, at most <see cref="MaxShownLength"/> characters. The command line prints it all.
    /// </summary>
    internal static string ShownScripts(string text) => text.Length <= MaxShownLength
        ? text
        : string.Concat(Start(text), $"\n\n… showing the first {HttpExchange.FormatSize(MaxShownLength)} of {HttpExchange.FormatSize(text.Length)} the scripts wrote");

    /// <summary>
    /// The start of a text longer than <see cref="MaxShownLength"/>: up to a line break when one is near, and never inside a
    /// character (a surrogate pair).
    /// </summary>
    private static ReadOnlySpan<char> Start(string text)
    {
        var cut = text.LastIndexOf('\n', MaxShownLength - 1, LineBreakSearch);
        if (cut < 0)
        {
            cut = char.IsHighSurrogate(text[MaxShownLength - 1]) ? MaxShownLength - 1 : MaxShownLength;
        }
        else if (cut > 0 && text[cut - 1] == '\r')
        {
            cut--;
        }

        return text.AsSpan(0, cut);
    }

    private void SetHeaders(List<KeyValuePair<string, string>> headers)
    {
        _shownHeaders = headers;
        _nameColumns = headers.Count == 0 ? 0 : headers.Max(h => h.Key.GetColumns());
        _headers.Table = new EnumerableTableSource<KeyValuePair<string, string>>(headers, new Dictionary<string, Func<KeyValuePair<string, string>, object>>
        {
            ["Header"] = h => h.Key,
            ["Value"] = h => h.Value,
        });
        FitValueColumn();
        _headers.Update();
    }

    /// <summary>
    /// Keeps the Value column within the pane, so that a value cut there ends with "…" in sight: Terminal.Gui widens the
    /// last column to the pane, but never narrows it, and a longer value would run past the pane's edge, cut unmarked.
    /// </summary>
    private void FitValueColumn()
    {
        // The widest name (Terminal.Gui measures the rows in view, never wider), and the lines before, between and after the columns.
        var names = Math.Min(_headers.MaxCellWidth, Math.Max("Header".Length, _nameColumns));
        var width = Math.Max(MinValueColumns, _headers.Viewport.Width - names - 3);
        var style = _headers.Style.GetOrCreateColumnStyle(1);
        if (style.MaxWidth != width)
        {
            style.MaxWidth = width;
            _headers.Update();
        }
    }

    /// <summary>The selected response header in full (Enter or a double-click): its value wrapped, to read, select or copy.</summary>
    private void ShowSelectedHeader()
    {
        if (App is null || _headers.Value is not { } selection || selection.SelectedCell.Y < 0 || selection.SelectedCell.Y >= _shownHeaders.Count)
        {
            return;
        }

        var (name, value) = _shownHeaders[selection.SelectedCell.Y];
        using var dialog = new Dialog { HotKeySpecifier = Ui.NoHotKey, Title = name, Width = Dim.Percent(80), Height = Dim.Percent(50) };
        var area = new TextArea { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
        area.SetText(value);
        dialog.Add(area);
        dialog.AddButton(new Button { Text = "Close" });
        dialog.AddButton(new Button { Text = "Copy" });
        App.Run(dialog);
        if (dialog.Result == 1)
        {
            Notify(Ui.CopyToClipboard(App, value) ? $"The value of {name} copied to the clipboard." : "Clipboard is not available in this terminal.");
        }
    }

    private void SaveBody()
    {
        if (_exchange?.Response is not { } response || App is null)
        {
            return;
        }

        if (BodyNotKept)
        {
            Notify("The history kept no copy of this response body.");
            return;
        }

        // Terminal.Gui's save dialog takes an existing file (a collection, when it opens on the workspace folder) without
        // asking; Cancel is the default.
        if (Ui.PickSaveFile(App, "Save response body") is { } path
            && (!File.Exists(path) || MessageBox.Query(App, "Save response body", $"{path} exists. Replace it?", "Replace", "Cancel") == 0))
        {
            try
            {
                File.WriteAllBytes(path, response.Body);
                var saved = $"Saved {HttpExchange.FormatSize(response.Body.LongLength)} to {path}";
                Notify((_fromHistory, response.BodyTruncated) switch
                {
                    (true, true) => $"⚠ {saved}: only the start of the body, as the history keeps it (secrets masked as ***)",
                    (true, false) => $"{saved}: the body as the history keeps it (secrets masked as ***)",
                    (false, true) => $"⚠ {saved}: only the start of the body, cut at the maximum response size (Tools > Settings)",
                    _ => saved,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Ui.Error(App, "Save response body", $"The response body could not be saved to {path}:\n{ex.Message}");
            }
        }
    }

    private void Notify(string message) => Notice?.Invoke(this, message);

    protected override void Dispose(bool disposing)
    {
        _disposed = true; // formatting that finishes later must not touch the disposed views
        base.Dispose(disposing);
    }

    /// <param name="fromHistory">The exchange comes from the history, which has the client certificate but not the request's settings.</param>
    private static string DescribeRequest(PreparedRequest request, HttpExchange? exchange, bool fromHistory = false)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{request.Method} {request.Url}\n");
        var headers = exchange?.SentHeaders.Count > 0 ? exchange.SentHeaders : request.Headers;
        foreach (var (key, value) in headers)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{key}: {value}\n");
        }

        if (request.Body.Kind != PreparedBodyKind.None)
        {
            var body = request.Body.Describe();
            builder.Append('\n');
            if (body.Length > TimelineBodyLength)
            {
                // Not inside a character: half of one can't be shown.
                var cut = char.IsHighSurrogate(body[TimelineBodyLength - 1]) ? TimelineBodyLength - 1 : TimelineBodyLength;
                builder.Append(body.AsSpan(0, cut)).Append("\n… (truncated)");
            }
            else
            {
                builder.Append(body);
            }

            builder.Append('\n');
        }

        builder.Append(CultureInfo.InvariantCulture, $"\nAuth: {request.AuthDescription}\n");
        if (fromHistory)
        {
            builder.Append(CultureInfo.InvariantCulture, $"Client certificate: {exchange?.HistoryEntry?.ClientCertificate ?? "none"}\n");
            builder.Append("SSL verification: (not recorded) · Redirects: (not recorded)\n");
        }
        else
        {
            builder.Append(CultureInfo.InvariantCulture, $"Client certificate: {(request.ClientCertificate is { } c ? $"{c.Entry.Name} — {c.Summary} ({c.Origin})" : "none")}\n");
            builder.Append(CultureInfo.InvariantCulture, $"SSL verification: {(request.StrictSsl ? "on" : "off")} · Redirects: {(request.FollowRedirects ? "follow" : "don't follow")}\n");
        }

        if (exchange is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\nStarted {exchange.StartedAt:yyyy-MM-dd HH:mm:ss}\n");
            builder.Append(exchange.Timing).Append('\n');
            if (exchange.Response?.FinalUri is { } finalUri && finalUri.ToString() != request.Uri?.ToString())
            {
                builder.Append(CultureInfo.InvariantCulture, $"Redirected to   {finalUri}\n");
            }
        }

        if (request.Warnings.Count > 0)
        {
            builder.Append("\nWarnings:\n");
            foreach (var warning in request.Warnings)
            {
                builder.Append(CultureInfo.InvariantCulture, $"  ⚠ {warning}\n");
            }
        }

        return builder.ToString();
    }
}
