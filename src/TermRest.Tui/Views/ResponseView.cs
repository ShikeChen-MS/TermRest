using System.Globalization;
using System.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Http;
using TermRest.Tui.Dialogs;

namespace TermRest.Tui.Views;

/// <summary>Response pane: status line plus Body / Headers / Timeline / TLS tabs.</summary>
internal sealed class ResponseView : View
{
    private readonly Label _status;
    private readonly Tabs _tabs;
    private readonly View _bodyTab;
    private readonly TextArea _body;
    private readonly Button _pretty;
    private readonly Button _wrap;
    private readonly TableView _headers;
    private readonly TextArea _timeline;
    private readonly TextArea _tls;
    private HttpExchange? _exchange;
    private bool _prettyOn = true;

    public ResponseView()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _status = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = "No response yet. Press F5 (or Enter in the URL bar) to send." };

        _tabs = new Tabs { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };

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
        copy.Accepted += (_, _) =>
        {
            if (_exchange?.Response is not null)
            {
                Notify(Ui.CopyToClipboard(App, _body!.GetText()) ? "Response body copied to the clipboard." : "Clipboard is not available in this terminal.");
            }
        };
        var save = new Button { Text = "Save as…", X = Pos.Right(copy) + 2, Y = 0, NoDecorations = true, ShadowStyle = null };
        save.Accepted += (_, _) => SaveBody();
        _body = new TextArea { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true };
        _bodyTab.Add(_pretty, _wrap, copy, save, _body);

        var headersTab = new View { Title = "Headers", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _headers = new TableView { Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
        _headers.Style.ExpandLastColumn = true;
        headersTab.Add(_headers);

        var timelineTab = new View { Title = "Timeline", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _timeline = new TextArea { ReadOnly = true };
        timelineTab.Add(_timeline);

        var tlsTab = new View { Title = "TLS", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _tls = new TextArea { ReadOnly = true };
        tlsTab.Add(_tls);

        _tabs.Add(_bodyTab, headersTab, timelineTab, tlsTab);
        _tabs.Value = _bodyTab;
        Add(_status, _tabs);
        SetHeaders([]);
    }

    /// <summary>Raised with short user-facing notices (copied, saved, …).</summary>
    public event EventHandler<string>? Notice;

    public HttpExchange? Exchange => _exchange;

    public string StatusText => _status.Text;

    public void Clear()
    {
        _exchange = null;
        _status.Text = "No response yet. Press F5 (or Enter in the URL bar) to send.";
        _status.SchemeName = null;
        _body.SetText("");
        _timeline.SetText("");
        _tls.SetText("");
        SetHeaders([]);
    }

    public void ShowSending(PreparedRequest request)
    {
        _status.Text = $"Sending {request.Method} {request.Url} …  (Esc or Send to cancel)";
        _status.SchemeName = null;
        _timeline.SetText(DescribeRequest(request, null));
    }

    public void ShowExchange(HttpExchange exchange)
    {
        _exchange = exchange;
        var response = exchange.Response;
        if (response is null)
        {
            _status.Text = exchange.Cancelled ? "Request cancelled." : $"Error: {exchange.ErrorMessage}";
            _status.SchemeName = exchange.Cancelled ? null : "Error";
            _body.SetText(exchange.Cancelled ? "" : exchange.ErrorMessage ?? "");
            SetHeaders([]);
        }
        else
        {
            var http = $"HTTP/{response.Version.Major}{(response.Version.Major < 2 ? "." + response.Version.Minor : "")}";
            var warnings = exchange.Request.Warnings.Count > 0 ? $" · ⚠ {exchange.Request.Warnings.Count} warning(s), see Timeline" : "";
            _status.Text = $"{response.StatusCode} {response.ReasonPhrase} · {HttpExchange.Format(exchange.Timing.Total)} · {HttpExchange.FormatSize(response.Body.LongLength)} · {http}{warnings}";
            _status.SchemeName = response.StatusCode >= 400 ? "Error" : "Accent";
            SetHeaders(response.Headers);
            RenderBody();
        }

        _timeline.SetText(DescribeRequest(exchange.Request, exchange));
        _tls.SetText(exchange.Tls.Count == 0
            ? exchange.Request.Uri?.Scheme == "https" ? "No TLS session was established." : "Plain HTTP (no TLS)."
            : string.Join("\n\n", exchange.Tls.Select(t => t.Describe())));
        SetNeedsDraw();
    }

    private void RenderBody()
    {
        if (_exchange?.Response is not { } response)
        {
            return;
        }

        var text = BodyFormatter.Format(response.Body, response.ContentType, _prettyOn);
        if (response.BodyTruncated)
        {
            text += "\n\n… (truncated)";
        }

        _body.SetText(text);
    }

    private void SetHeaders(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        _headers.Table = new EnumerableTableSource<KeyValuePair<string, string>>(headers, new Dictionary<string, Func<KeyValuePair<string, string>, object>>
        {
            ["Header"] = h => h.Key,
            ["Value"] = h => h.Value,
        });
        _headers.Update();
    }

    private void SaveBody()
    {
        if (_exchange?.Response is not { } response || App is null)
        {
            return;
        }

        if (Ui.PickSaveFile(App, "Save response body") is { } path)
        {
            File.WriteAllBytes(path, response.Body);
            Notify($"Saved {HttpExchange.FormatSize(response.Body.LongLength)} to {path}");
        }
    }

    private void Notify(string message) => Notice?.Invoke(this, message);

    private static string DescribeRequest(PreparedRequest request, HttpExchange? exchange)
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
            builder.Append('\n').Append(body.Length > 4000 ? body[..4000] + "\n… (truncated)" : body).Append('\n');
        }

        builder.Append(CultureInfo.InvariantCulture, $"\nAuth: {request.AuthDescription}\n");
        builder.Append(CultureInfo.InvariantCulture, $"Client certificate: {(request.ClientCertificate is { } c ? $"{c.Entry.Name} — {c.Certificate.Summary} ({c.Origin})" : "none")}\n");
        builder.Append(CultureInfo.InvariantCulture, $"SSL verification: {(request.StrictSsl ? "on" : "off")} · Redirects: {(request.FollowRedirects ? "follow" : "don't follow")}\n");

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
