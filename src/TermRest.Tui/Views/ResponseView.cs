using System.Globalization;
using System.Text;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Tui.Dialogs;
using TermRest.Tui.Theming;

namespace TermRest.Tui.Views;

/// <summary>
/// Response pane: status line plus Body / Headers / Timeline / TLS / Scripts tabs. The body is colored as its type says,
/// can be filtered with JSONPath, and Find (Ctrl+F) looks for text in the tab shown.
/// </summary>
internal sealed class ResponseView : View
{
    /// <summary>
    /// The body tab shows this many characters at most (the editor takes about 90 bytes of memory for each); Copy and
    /// Save as… give the whole body.
    /// </summary>
    internal const int MaxShownLength = 1_048_576;

    /// <summary>Bodies of this many bytes or more are formatted (decoded, pretty printed) in the background.</summary>
    private const int BackgroundSize = 1_048_576;

    /// <summary>A filter's values are written up to this many bytes (of UTF-8): the body tab shows their start, Copy all of them.</summary>
    internal const long MaxFilteredBytes = 16 * 1024 * 1024;

    /// <summary>The Timeline tab shows this many characters of a request's body at most.</summary>
    private const int TimelineBodyLength = 4000;

    /// <summary>How far back from where a long body is cut a line break is looked for, to cut there instead.</summary>
    private const int LineBreakSearch = 4096;

    /// <summary>The Headers tab's Value column is never narrower than this, however narrow the pane.</summary>
    private const int MinValueColumns = 10;

    private readonly Label _status;
    private readonly Button _saveExample;
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
    private readonly View _filterRow;
    private readonly TextField _filterField;
    private readonly Label _filterStatus;
    private readonly View _findBar;
    private readonly TextField _findField;
    private readonly CheckBox _matchCase;
    private readonly Label _findCount;
    private HttpExchange? _exchange;
    private bool _fromHistory; // the exchange shown came from the history: its body as the history keeps it
    private bool _prettyOn = true;
    private int _bodyVersion; // a body formatted in the background is shown only if no other body replaced it meanwhile
    private CancellationTokenSource? _rendering; // the body formatted or filtered in the background, stopped when replaced
    private JsonPath? _filter; // the query the body is filtered with, kept for the responses that follow
    private bool _filterInvalid; // the query typed is not valid JSONPath: the filter row says why
    private TextArea? _findTarget; // the text of the tab that Find looks in
    private bool _disposed;

    public ResponseView()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _status = new Label { HotKeySpecifier = Ui.NoHotKey, X = 0, Y = 0, Width = Dim.Fill(17), Text = "No response yet. Press F5 (or Enter in the URL bar) to send." };

        // Saves the response shown as an example of its request (Postman's Save Response › Save as example).
        _saveExample = new Button { Text = "Save as example", X = Pos.AnchorEnd(15), Y = 0, NoDecorations = true, ShadowStyle = null, Visible = false };
        _saveExample.Accepted += (_, _) => SaveAsExampleRequested?.Invoke(this, EventArgs.Empty);

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
        var filter = new Button { Text = "Filter", X = Pos.Right(save) + 2, Y = 0, NoDecorations = true, ShadowStyle = null };
        filter.Accepted += (_, _) => ToggleFilter();
        _body = new TextArea { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true };

        // JSONPath: the body shows the values the query selects, as a JSON array.
        _filterRow = new View { X = 0, Y = 1, Width = Dim.Fill(), Height = 1, CanFocus = true, Visible = false };
        var filterLabel = new Label { Text = "JSONPath:", X = 0, Y = 0 };
        _filterField = new TextField { X = Pos.Right(filterLabel) + 1, Y = 0, Width = Dim.Percent(45) };
        _filterField.TextChanged += (_, _) => FilterChanged();
        _filterStatus = new Label { HotKeySpecifier = Ui.NoHotKey, X = Pos.Right(_filterField) + 1, Y = 0, Width = Dim.Fill(3), Text = "" };
        var closeFilter = new Button { Text = "✕", X = Pos.AnchorEnd(2), Y = 0, NoDecorations = true, ShadowStyle = null };
        closeFilter.Accepted += (_, _) => CloseFilter();
        _filterRow.Add(filterLabel, _filterField, _filterStatus, closeFilter);
        _bodyTab.Add(_pretty, _wrap, copy, save, filter, _filterRow, _body);

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

        // Find (Ctrl+F): in the text of the tab shown.
        _findBar = new View { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = true, Visible = false };
        var findLabel = new Label { Text = "Find:", X = 0, Y = 0 };
        _findField = new TextField { X = Pos.Right(findLabel) + 1, Y = 0, Width = Dim.Fill(38) };
        _findField.TextChanged += (_, _) => FindChanged();
        var previous = new Button { Text = "▲", X = Pos.Right(_findField) + 1, Y = 0, NoDecorations = true, ShadowStyle = null };
        previous.Accepted += (_, _) => FindNext(backwards: true);
        var next = new Button { Text = "▼", X = Pos.Right(previous) + 1, Y = 0, NoDecorations = true, ShadowStyle = null };
        next.Accepted += (_, _) => FindNext();
        _matchCase = new CheckBox { Text = "Match case", X = Pos.Right(next) + 2, Y = 0 };
        _matchCase.ValueChanged += (_, _) => FindChanged();
        _findCount = new Label { HotKeySpecifier = Ui.NoHotKey, X = Pos.Right(_matchCase) + 2, Y = 0, Width = Dim.Fill(3), Text = "" };
        var closeFind = new Button { Text = "✕", X = Pos.AnchorEnd(2), Y = 0, NoDecorations = true, ShadowStyle = null };
        closeFind.Accepted += (_, _) => CloseFind();
        _findBar.Add(findLabel, _findField, previous, next, _matchCase, _findCount, closeFind);

        // Enter and Shift+Enter go to the next and previous match (Esc closes the bar: see OnKeyDown).
        _findField.KeyDown += (_, key) =>
        {
            if (key == Key.Enter || key == Key.Enter.WithShift)
            {
                FindNext(backwards: key.IsShift);
                key.Handled = true;
            }
        };
        _tabs.ValueChanged += (_, _) =>
        {
            if (_findBar.Visible)
            {
                FindIn(SearchedArea());
            }
        };
        foreach (var area in new[] { _body, _timeline, _tls, _scripts })
        {
            area.MatchesChanged += (sender, _) =>
            {
                if (sender == _findTarget)
                {
                    ShowFindCount();
                }
            };
        }

        Add(_status, _saveExample, _tabs, _findBar);
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

    /// <summary>Save as example was pressed (shown while a response is).</summary>
    public event EventHandler? SaveAsExampleRequested;

    /// <summary>Whether Save as example is offered (for tests).</summary>
    internal bool OffersSaveAsExample => _saveExample.Visible;

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

    /// <summary>The language the Body tab colors its text in (for tests).</summary>
    internal string? BodySyntax => _body.SyntaxLanguage;

    /// <summary>What the filter row says: how many values the query selects, or why it can't (for tests).</summary>
    internal string FilterStatus => _filterStatus.Text;

    /// <summary>What the find bar says: which match is selected, of how many (for tests).</summary>
    internal string FindStatus => _findCount.Text;

    public void Clear()
    {
        _exchange = null;
        _fromHistory = false;
        _saveExample.Visible = false;
        _status.Text = "No response yet. Press F5 (or Enter in the URL bar) to send.";
        _status.SchemeName = null;
        ShowBody("");
        SetFilterStatus("", error: false);
        _timeline.SetText("");
        _tls.SetText("");
        _scripts.SetText(NoScripts);
        SetHeaders([]);
    }

    /// <summary>Pre-request scripts are running: the request is not prepared yet.</summary>
    public void ShowRunningScripts() => ShowProgress("Running pre-request scripts …  (Esc or Send to cancel)");

    /// <summary>Says what the request waits for before it goes (scripts, an OAuth 2.0 token).</summary>
    public void ShowProgress(string text)
    {
        _status.Text = text;
        _status.SchemeName = null;
        _saveExample.Visible = false;
    }

    public void ShowSending(PreparedRequest request)
    {
        _status.Text = $"Sending {request.Method} {request.Url} …  (Esc or Send to cancel)";
        _status.SchemeName = null;
        _saveExample.Visible = false;
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
        _saveExample.Visible = response is not null;
        if (response is null)
        {
            _status.Text = exchange.Cancelled ? "Request cancelled." : $"Error: {exchange.ErrorMessage}";
            _status.SchemeName = exchange.Cancelled ? null : "Error";

            // When a script stopped the request, its traceback is what the user needs to see.
            ShowBody(exchange.Cancelled ? "" : scripts?.Error is not null ? ShownScripts($"{exchange.ErrorMessage}\n\n{scripts.Describe()}") : exchange.ErrorMessage ?? "");
            SetFilterStatus(_filter is null ? "" : "No response to filter.", error: false);
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
    /// Shows the response body, pretty or not (or the values the filter selects in it), formatted from the response each time
    /// (as Copy is): the editor's text has its own line breaks (a lone CR shows as one) and only the start of a long body,
    /// and keeping the whole text as well would take memory. A large body is formatted in the background, and shown if
    /// nothing replaced it meanwhile.
    /// </summary>
    private void RenderBody()
    {
        _rendering?.Cancel();
        _rendering = null;
        if (_exchange?.Response is not { } response)
        {
            return;
        }

        // The history may have no copy of a body that was not empty (it keeps none, or the copy's file is gone).
        if (BodyNotKept)
        {
            ShowBody("(response body not kept in the history)");
            SetFilterStatus(_filter is null ? "" : "✗ The history kept no copy of this response body.", error: true);
            return;
        }

        var (pretty, filter) = (_prettyOn, _filter);
        if (App is not { } app || response.Body.Length < BackgroundSize)
        {
            Show(Render(response, pretty, filter, CancellationToken.None));
            return;
        }

        var rendering = _rendering = new CancellationTokenSource();
        var version = ShowBody(filter is null ? "Formatting…" : "Filtering…");
        _ = Task.Run(() => Render(response, pretty, filter, rendering.Token), rendering.Token).ContinueWith(
            task => app.AddTimeout(TimeSpan.Zero, () =>
            {
                if (_rendering == rendering)
                {
                    _rendering = null;
                }

                rendering.Dispose();
                if (version == _bodyVersion && !_disposed)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        Show(task.Result);
                    }
                    else if (!task.IsCanceled)
                    {
                        ShowBody($"✗ {task.Exception?.GetBaseException().Message}");
                    }
                }

                return false;
            }),
            TaskScheduler.Default);
    }

    /// <summary>What the body tab shows of a response, the language it is colored in, and what the filter row says.</summary>
    internal readonly record struct RenderedBody(string Text, string? Language, string FilterStatus = "", bool FilterFailed = false);

    /// <summary>
    /// The body as the body tab shows it (see <see cref="Shown"/>), or, with a filter, the values it selects (as much of them
    /// as <see cref="MaxShownLength"/> allows). A body the filter can't apply to is shown as it is, and the filter row says why.
    /// </summary>
    internal static RenderedBody Render(ResponseSnapshot response, bool pretty, JsonPath? filter, CancellationToken cancellationToken)
    {
        if (filter is not null)
        {
            try
            {
                var filtered = BodyFilter.Apply(filter, response.Body, response.ContentType, pretty, MaxFilteredBytes, cancellationToken);
                var text = filtered.Text.Length <= MaxShownLength
                    ? filtered.Text
                    : string.Concat(Start(filtered.Text), $"\n\n… showing the first {HttpExchange.FormatSize(MaxShownLength)} of {HttpExchange.FormatSize(filtered.Text.Length)}: Copy gives them all");
                return new RenderedBody(text, "json", Describe(filtered));
            }
            catch (JsonPathException ex)
            {
                var shown = Shown(response, pretty);
                var cut = response.BodyTruncated ? " The body was cut at the maximum response size (Tools > Settings)." : "";
                return new RenderedBody(shown, SyntaxOf(response, shown), $"✗ {ex.Message}{cut}", FilterFailed: true);
            }
        }

        var body = Shown(response, pretty);
        return new RenderedBody(body, SyntaxOf(response, body));
    }

    /// <summary>How many values a filter selected, and how many of them it wrote.</summary>
    private static string Describe(FilteredBody filtered)
    {
        var count = filtered.Count.ToString("N0", CultureInfo.InvariantCulture);
        return filtered.Count == 0 ? "No values"
            : filtered.Shown < filtered.Count ? $"{count} values: the first {filtered.Shown.ToString("N0", CultureInfo.InvariantCulture)}, {HttpExchange.FormatSize(MaxFilteredBytes)}, are shown"
            : filtered.Count == 1 ? "1 value"
            : $"{count} values";
    }

    /// <summary>
    /// The language the body tab colors a response body in: the one its Content-Type names, or JSON, HTML or other markup
    /// (as XML) that a body of another type (text/plain, say) or none holds. Null for other text and for a hex dump.
    /// </summary>
    internal static string? SyntaxOf(ResponseSnapshot response, string shown)
    {
        var language = BodyFormatter.Language(response.Body, response.ContentType);
        if (language != "text")
        {
            return language == "binary" ? null : language;
        }

        // Pretty printing drops an XML declaration: the text shown starts with the root element then.
        var start = shown.AsSpan().TrimStart();
        return start.StartsWith('{') || start.StartsWith('[') ? "json"
            : start.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ? "html"
            : start.StartsWith('<') ? "xml"
            : null;
    }

    private void Show(RenderedBody rendered)
    {
        ShowBody(rendered.Text, rendered.Language);
        if (rendered.FilterStatus.Length > 0)
        {
            SetFilterStatus(rendered.FilterStatus, rendered.FilterFailed);
        }
    }

    /// <summary>The response comes from the history, which has no copy of its body, though the body was not empty.</summary>
    private bool BodyNotKept => _fromHistory && _exchange?.Response?.Body.Length == 0 && _exchange.HistoryEntry?.Size > 0;

    /// <summary>Shows <paramref name="text"/> in the body tab, colored in <paramref name="language"/>, in place of a body still being formatted.</summary>
    private int ShowBody(string text, string? language = null)
    {
        _body.SyntaxLanguage = language;
        _body.SetText(text);
        return ++_bodyVersion;
    }

    /// <summary>Copies the whole body, as formatted, or the values the filter selects (a large body is done in the background).</summary>
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

        var (pretty, filter) = (_prettyOn, _filter);
        if (App is not { } app || response.Body.Length < BackgroundSize)
        {
            Copied(Whole(response, pretty, filter));
            return;
        }

        Notify(filter is null ? "Copying the response body…" : "Copying the values the filter selects…");
        _ = Task.Run(() => Whole(response, pretty, filter)).ContinueWith(
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

    private void Copied((string Text, bool Filtered) copied)
    {
        if (!_disposed)
        {
            Notify(!Ui.CopyToClipboard(App, copied.Text) ? "Clipboard is not available in this terminal."
                : copied.Filtered ? "The values the filter selects copied to the clipboard."
                : "Response body copied to the clipboard.");
        }
    }

    /// <summary>
    /// What Copy copies: the values the filter selects (up to <see cref="MaxFilteredBytes"/>), or the whole body as formatted,
    /// with a note when the size limit cut it.
    /// </summary>
    private static (string Text, bool Filtered) Whole(ResponseSnapshot response, bool pretty, JsonPath? filter)
    {
        if (filter is not null)
        {
            try
            {
                return (BodyFilter.Apply(filter, response.Body, response.ContentType, pretty, MaxFilteredBytes).Text, true);
            }
            catch (JsonPathException)
            {
                // Not JSON: the body tab shows the body, which is copied.
            }
        }

        var text = BodyFormatter.Format(response.Body, response.ContentType, pretty);
        return (response.BodyTruncated ? text + "\n\n… (truncated)" : text, false);
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

    // ----- filter ---------------------------------------------------------------------------

    /// <summary>The Filter button: shows the JSONPath row (or closes it, which clears the filter).</summary>
    internal void ToggleFilter()
    {
        if (_filterRow.Visible)
        {
            CloseFilter();
            return;
        }

        _filterRow.Visible = true;
        _body.Y = 2;
        _tabs.Select(_bodyTab, focusPage: false);
        _filterField.SetFocus();
        FilterChanged();
    }

    private void CloseFilter()
    {
        _filterRow.Visible = false;
        _body.Y = 1;
        _filterField.Text = ""; // the filter goes with it
        FilterChanged();
        _body.SetFocus();
    }

    /// <summary>The query in the filter row, as typed; the body shows what it selects in this response and the ones after it.</summary>
    internal void SetFilter(string query)
    {
        if (!_filterRow.Visible)
        {
            ToggleFilter();
        }

        _filterField.Text = query;
    }

    /// <summary>
    /// The query was edited: the body shows what it selects. While it is not valid JSONPath (being typed), the row says why
    /// and the body shows what the last valid one selected.
    /// </summary>
    private void FilterChanged()
    {
        var text = _filterField.Text.TrimEnd();
        _filterInvalid = false;
        if (text.Length == 0)
        {
            SetFilterStatus("Type a JSONPath query, such as $.items[*].id", error: false);
            if (_filter is not null)
            {
                _filter = null;
                RenderBody();
            }

            return;
        }

        try
        {
            _filter = JsonPath.Parse(text);
        }
        catch (JsonPathException ex)
        {
            SetFilterStatus($"✗ {ex.Message}", error: true);
            _filterInvalid = true;
            return;
        }

        if (_exchange?.Response is null)
        {
            SetFilterStatus("No response to filter yet.", error: false);
            return;
        }

        RenderBody();
    }

    private void SetFilterStatus(string text, bool error)
    {
        if (_filterInvalid)
        {
            return; // the row says why the query typed is not valid
        }

        _filterStatus.Text = text;
        _filterStatus.SchemeName = error ? "Error" : null;
    }

    // ----- find -----------------------------------------------------------------------------

    /// <summary>Ctrl+F: shows the find bar, its text selected to type over, looking in the text of the tab shown.</summary>
    internal void OpenFind()
    {
        if (!_findBar.Visible)
        {
            _findBar.Visible = true;
            _tabs.Height = Dim.Fill(1);
            FindIn(SearchedArea());
        }

        _findField.SetFocus();
        _findField.SelectAll();
    }

    /// <summary>
    /// F3 or Enter in the find bar: selects the next match (Shift: the previous one), going round. Opens the find bar when it
    /// is closed or has no text.
    /// </summary>
    internal void FindNext(bool backwards = false)
    {
        if (!_findBar.Visible || _findField.Text.Length == 0)
        {
            OpenFind();
            return;
        }

        _findTarget?.SelectNextMatch(backwards);
        ShowFindCount();
    }

    /// <summary>The text the find bar looks for (for tests).</summary>
    internal void Find(string text, bool matchCase = false)
    {
        OpenFind();
        _matchCase.Value = matchCase ? CheckState.Checked : CheckState.UnChecked;
        _findField.Text = text;
    }

    private void CloseFind()
    {
        var area = _findTarget;
        FindIn(null);
        _findBar.Visible = false;
        _tabs.Height = Dim.Fill();
        if (area is not null)
        {
            area.SetFocus();
        }
        else
        {
            _tabs.FocusSelectedPage();
        }
    }

    private void FindChanged()
    {
        _findTarget?.Find(_findField.Text, _matchCase.Value == CheckState.Checked);
        ShowFindCount();
    }

    /// <summary>Looks for the find bar's text in <paramref name="area"/> (the text of the tab shown), and no longer in another.</summary>
    private void FindIn(TextArea? area)
    {
        if (_findTarget is { } previous && previous != area)
        {
            previous.Find(null, matchCase: false);
        }

        _findTarget = area;
        area?.Find(_findField.Text, _matchCase.Value == CheckState.Checked);
        ShowFindCount();
    }

    /// <summary>The text of the tab shown; none for Headers, a table.</summary>
    private TextArea? SearchedArea() => _tabs.Value?.SubViews.OfType<TextArea>().FirstOrDefault();

    private void ShowFindCount()
    {
        static string Count(TextArea area) => area.MatchCount.ToString("N0", CultureInfo.InvariantCulture) + (area.MoreMatches ? "+" : "");
        _findCount.Text = _findTarget is not { } target ? "Not in this tab"
            : _findField.Text.Length == 0 ? ""
            : target.MatchCount == 0 ? "No matches"
            : target.CurrentMatch >= 0 ? $"{target.CurrentMatch + 1} of {Count(target)}"
            : $"{Count(target)} matches";
    }

    /// <inheritdoc/>
    /// <remarks>Esc in the find bar or the filter row closes it (elsewhere in the main window, it cancels a request).</remarks>
    protected override bool OnKeyDown(Key key)
    {
        if (key == Key.Esc && _findBar.HasFocus)
        {
            CloseFind();
            return true;
        }

        if (key == Key.Esc && _filterRow.HasFocus)
        {
            CloseFilter();
            return true;
        }

        return base.OnKeyDown(key);
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true; // formatting that finishes later must not touch the disposed views
        _rendering?.Cancel();
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
